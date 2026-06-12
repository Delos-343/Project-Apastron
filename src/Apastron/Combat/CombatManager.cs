using System;
using System.Collections.Generic;
using System.Linq;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Audio;
using Apastron.Vehicles;

namespace Apastron.Combat;

/// <summary>
/// Drives a live AI-vs-AI engagement: each combatant maneuvers per its doctrine and fires its
/// weapons; slugs and missiles are flown as entities (missiles guided by <see cref="FireControl"/>),
/// point-defense allocates against incoming missiles, hits are resolved with the Tate penetration
/// model against areal-density armor, and IR signature gates detection. Combatant bodies live in
/// the world (the integrator moves them under the thrust the AI sets); munitions are flown here.
/// </summary>
public sealed class CombatManager
{
    public readonly List<Combatant> Combatants = new();
    public readonly List<Munition> Munitions = new();
    // transient laser beams for this frame: (from, to, r,g,b)
    public readonly List<(Vec3 From, Vec3 To, float R, float G, float B)> Beams = new();
    // point-defense intercept visuals (gun tracers and PD-laser beams), each fading over a short life.
    public readonly List<PdTracer> Tracers = new();

    public bool Active;
    public double Time;
    public int SlugsFired, MissilesFired, Hits, Intercepts;
    public System.Action<GameSound>? Sound;   // optional sonification hook

    // --- engagement profile -------------------------------------------------------------------
    // The doctrine stand-off ranges, the incoming-missile reaction range, and the per-ship missile
    // salvo cap. Defaults suit the scripted missions (close, sharp duels); the sandbox deep-space
    // duel widens them for beyond-visual-range combat (see Program.NewDuel). Made per-engagement so
    // changing the duel's character cannot disturb the hand-tuned mission scenarios.
    public double AggressiveRange  = 60.0e3;    // preferred range for an aggressive ship
    public double StandoffRange    = 300.0e3;   // preferred range for a standoff ship
    public double EvasiveRange     = 1200.0e3;  // preferred range for an evasive / fleeing ship
    public double ThreatReactRange = 200.0e3;   // incoming-missile distance that triggers a jink
    public int    MissileSalvoCap  = 6;         // max missiles in flight per ship

    // Rolling combat event feed (module damage, crew survivability calls), newest first.
    public readonly List<string> Log = new();
    private const int MaxLog = 30;
    private readonly System.Random _rng = new();
    private void LogEvent(string s)
    {
        Log.Insert(0, s);
        if (Log.Count > MaxLog) Log.RemoveAt(Log.Count - 1);
    }

    // --- tuning ---
    private const double N = 4.0;                  // PN navigation constant
    private const double MissileProximity = 60.0;  // m proximity-fuze radius, added to the hull radius
    private const double MaxSubStep = 0.05;        // s; munition flight is sub-stepped to at most this
    private const int MaxSubSteps = 64;            // cap on munition sub-steps per frame (cost guard)
    private const double SlugMaxAge = 180.0;
    private const double MissileMaxAge = 600.0;
    private const double LaunchKick = 500.0;       // m/s missile launch velocity
    private const double DetectK = 500.0;          // signature term: + K*sqrt(signature_W)
    // Space offers no concealment: a baseline sensor range that always detects a target within the
    // engagement envelope, regardless of how cold it runs. A ship's heat signature only extends
    // detection further (a torch burn is a beacon), it can never hide a ship inside this bubble.
    private const double BaseSensorRange = 3000.0e3;
    private const double KineticHull = 6.0;        // hull % per perforating slug
    private const double MissileHull = 35.0;       // hull % per perforating missile
    private const double ArmorErosionPerf = 0.05;  // armor pool fraction lost per perforation

    public void Update(PhysicsWorld world, double dt)
    {
        if (!Active) return;
        Beams.Clear();
        if (dt > 0.0) Time += dt;

        for (int i = 0; i < Combatants.Count; i++)
        {
            Combatant c = Combatants[i];
            if (!c.Alive) { c.Body.ThrustWorld = Vec3.Zero; continue; }
            StepAI(world, c, i, dt);
            Survivability(c);
        }
        for (int i = 0; i < Combatants.Count; i++)
        {
            Combatant c = Combatants[i];
            if (!c.Alive) continue;
            Fire(world, c, i, dt);
            PointDefense(c, i, dt);
        }

        StepMunitions(world, dt);
        Munitions.RemoveAll(m => !m.Alive);

        // age the point-defense intercept visuals (they fade out over a fraction of a second)
        if (dt > 0.0)
        {
            for (int i = Tracers.Count - 1; i >= 0; i--)
            {
                PdTracer t = Tracers[i];
                t.Ttl -= dt;
                if (t.Ttl <= 0.0) Tracers.RemoveAt(i);
                else Tracers[i] = t;
            }
        }
    }

    // ---- AI maneuver: set the body's thrust and recompute signature/detection ----
    private void StepAI(PhysicsWorld world, Combatant c, int ci, double dt)
    {
        int ei = NearestEnemy(ci);
        Vec3 want = Vec3.Zero;
        if (ei >= 0 && c.Doctrine != CombatDoctrine.HoldFire)
        {
            Combatant e = Combatants[ei];
            Vec3 los = e.Body.Position - c.Body.Position;
            double range = los.Length;
            Vec3 losHat = range > 1e-6 ? los / range : Vec3.UnitX;

            double desired = c.Doctrine switch
            {
                CombatDoctrine.Aggressive => AggressiveRange,
                CombatDoctrine.Standoff   => StandoffRange,
                CombatDoctrine.Evasive    => EvasiveRange,
                _                         => range,
            };

            int ti = NearestThreatMissile(ci, ThreatReactRange);
            bool jink = c.Doctrine == CombatDoctrine.Evasive || (ti >= 0 && c.Doctrine == CombatDoctrine.Aggressive);
            if (jink)
            {
                Vec3 tref = ti >= 0 ? (Munitions[ti].Position - c.Body.Position) : los;
                want = Perp(tref.Length > 1e-6 ? tref.Normalized() : losHat, c.Body.Velocity);
            }
            else if (range > desired * 1.1) want = losHat;
            else if (range < desired * 0.9) want = losHat * -1.0;
            else want = Vec3.Zero;
        }

        double thrustForce = 0.0;
        if (want.Length > 1e-6 && c.Ship.TotalThrustVac > 0.0)
        {
            thrustForce = c.Ship.TotalThrustVac * Math.Clamp(c.Ship.SustainableThrottle, 0.0, 1.0);
            c.Body.ThrustWorld = want.Normalized() * thrustForce;
        }
        else c.Body.ThrustWorld = Vec3.Zero;

        double throttle = c.Ship.TotalThrustVac > 0.0 ? thrustForce / c.Ship.TotalThrustVac : 0.0;
        c.Signature = c.Ship.ReactorHeat + throttle * c.Ship.EngineHeatFull;
        c.DetectionRange = BaseSensorRange + DetectK * Math.Sqrt(Math.Max(c.Signature, 1.0));
    }

    // ---- main weapons ----
    private void Fire(PhysicsWorld world, Combatant c, int ci, double dt)
    {
        int ei = NearestEnemy(ci);
        Combatant? e = ei >= 0 ? Combatants[ei] : null;
        double range = e != null ? (e.Body.Position - c.Body.Position).Length : double.MaxValue;
        bool detected = e != null && range < e.DetectionRange;

        foreach (Part p in c.Ship.Parts)
        {
            if (p.Category != PartCategory.Weapon || p.Weapon == null || p.Health <= 0.05) continue;
            WeaponSpec w = p.Weapon;
            if (w.PointDefense) continue;   // handled in PointDefense

            double cd = c.Cooldown.TryGetValue(p, out double v) ? v : 0.0;
            if (cd > 0.0) cd -= dt;

            if (e != null && detected && c.Doctrine != CombatDoctrine.HoldFire && !c.WeaponsHold)
            {
                switch (w.Kind)
                {
                    case WeaponKind.Kinetic:
                        if (cd <= 0.0 && range < w.Range &&
                            FireControl.LeadSolution(c.Body.Position, c.Body.Velocity,
                                e.Body.Position, e.Body.Velocity, w.MuzzleVelocity, out Vec3 dir, out _))
                        {
                            Munitions.Add(new Munition
                            {
                                Kind = MunitionKind.Slug, Owner = ci, Target = ei,
                                Position = c.Body.Position,
                                Velocity = c.Body.Velocity + dir * w.MuzzleVelocity,
                                Mass = w.ProjectileMass, Length = w.ProjectileLength,
                                Material = w.ProjectileMaterial, MaxAge = SlugMaxAge,
                            });
                            SlugsFired++;
                            Sound?.Invoke(GameSound.SlugFire);
                            cd = w.RateOfFire > 0.0 ? 1.0 / w.RateOfFire : 1.0;
                        }
                        break;

                    case WeaponKind.Missile:
                        int inFlight = Munitions.Count(m => m.Owner == ci && m.Kind == MunitionKind.Missile);
                        if (cd <= 0.0 && range < w.Range && inFlight < MissileSalvoCap)
                        {
                            Vec3 los = (e.Body.Position - c.Body.Position);
                            Vec3 lh = los.Length > 1e-6 ? los.Normalized() : Vec3.UnitX;
                            Munitions.Add(new Munition
                            {
                                Kind = MunitionKind.Missile, Owner = ci, Target = ei,
                                Position = c.Body.Position,
                                Velocity = c.Body.Velocity + lh * LaunchKick,
                                Mass = w.MissileDryMass + w.MissilePropellant,
                                Propellant = w.MissilePropellant, Isp = w.MissileIsp, Thrust = w.MissileThrust,
                                Length = 0.6, Material = "Tungsten", MaxAge = MissileMaxAge,
                            });
                            MissilesFired++;
                            Sound?.Invoke(GameSound.MissileLaunch);
                            cd = c.Doctrine switch
                            {
                                CombatDoctrine.Aggressive => 4.0,
                                CombatDoctrine.Standoff   => 6.0,
                                _                         => 10.0,
                            };
                        }
                        break;
                }
            }
            c.Cooldown[p] = cd;
        }
    }

    // ---- point defense: gun/laser CIWS that kills incoming missiles within the mount's range ----
    private void PointDefense(Combatant c, int ci, double dt)
    {
        foreach (Part p in c.Ship.Parts)
        {
            WeaponSpec? w = p.Weapon;
            if (p.Category != PartCategory.Weapon || w == null || !w.PointDefense || p.Health <= 0.05) continue;
            double cd = c.Cooldown.TryGetValue(p, out double v) ? v : 0.0;
            if (cd > 0.0) { cd -= dt; c.Cooldown[p] = cd; continue; }

            int best = -1; double bestR = w.Range;
            for (int m = 0; m < Munitions.Count; m++)
            {
                Munition mu = Munitions[m];
                if (!mu.Alive || mu.Kind != MunitionKind.Missile || mu.Target != ci) continue;
                double r = (mu.Position - c.Body.Position).Length;
                if (r < bestR) { bestR = r; best = m; }
            }
            if (best >= 0)
            {
                AddTracer(c.Body.Position, Munitions[best].Position, w.Kind == WeaponKind.Laser);
                Munitions[best].Alive = false;
                Intercepts++;
                Sound?.Invoke(GameSound.Intercept);
                cd = w.RateOfFire > 0.0 ? 1.0 / w.RateOfFire : 0.2;
            }
            c.Cooldown[p] = cd;
        }
    }

    // A brief point-defense intercept visual from a mount to the missile it just killed: a pale cyan
    // PD-laser beam, or a hot yellow-white 40 mm tracer streak.
    private void AddTracer(Vec3 from, Vec3 to, bool laser)
    {
        double life = laser ? 0.09 : 0.14;
        Tracers.Add(new PdTracer
        {
            From = from, To = to,
            R = laser ? 0.55f : 1.00f, G = 0.85f, B = laser ? 1.00f : 0.45f,
            Ttl = life, Life = life, Laser = laser,
        });
    }

    // ---- fly all munitions, detect and resolve hits ----
    // Fly every munition and resolve hits. Flight is sub-stepped (each sub-step at most MaxSubStep) so
    // a fast slug cannot tunnel through a ship in a single, possibly time-warped, frame, and so missile
    // guidance integrates smoothly. Collision is a swept test: the round and each candidate target are
    // both advanced linearly across the sub-step and a hit registers when their closest approach falls
    // within the target's hull radius (plus a proximity fuze for missiles). A round can strike any
    // combatant other than its owner, not only its assigned target.
    private void StepMunitions(PhysicsWorld world, double dt)
    {
        if (dt <= 0.0) return;
        int nsub = (int)Math.Clamp(Math.Ceiling(dt / MaxSubStep), 1, MaxSubSteps);
        double h = dt / nsub;

        foreach (Munition m in Munitions)
        {
            if (!m.Alive) continue;
            Combatant? tgt = m.Target >= 0 && m.Target < Combatants.Count ? Combatants[m.Target] : null;

            for (int k = 0; k < nsub; k++)
            {
                double remStart = dt - k * h;          // time from this sub-step's start to the frame end
                double remEnd   = dt - (k + 1) * h;    // time from this sub-step's end to the frame end
                Vec3 pOld = m.Position;

                Vec3 acc = GravAccel(world, m.Position);
                if (m.Kind == MunitionKind.Missile && m.Propellant > 0.0 && tgt != null && tgt.Alive && m.Mass > 0.0)
                {
                    double aMax = m.Thrust / m.Mass;
                    Vec3 dir = FireControl.GuidanceDir(m.Position, m.Velocity, tgt.Body.Position, tgt.Body.Velocity, aMax, N);
                    acc += dir * aMax;
                    double ve = m.Isp * MathConstants.StandardGravity;
                    double burn = ve > 0.0 ? m.Thrust / ve * h : 0.0;
                    m.Propellant -= burn; m.Mass -= burn;
                    if (m.Propellant < 0.0) m.Propellant = 0.0;
                }

                m.Velocity += acc * h;
                Vec3 pNew = pOld + m.Velocity * h;
                m.Position = pNew;
                m.Age += h;
                if (m.Age > m.MaxAge) { m.Alive = false; break; }

                // swept hit test vs every other combatant (the target moves linearly across the sub-step too)
                int hitIdx = -1; double hitMiss = double.MaxValue;
                for (int ci = 0; ci < Combatants.Count; ci++)
                {
                    if (ci == m.Owner) continue;
                    Combatant e = Combatants[ci];
                    if (!e.Alive) continue;
                    Vec3 qOld = e.Body.Position - e.Body.Velocity * remStart;
                    Vec3 qNew = e.Body.Position - e.Body.Velocity * remEnd;
                    double miss = SegPointDistance(pOld - qOld, pNew - qNew, Vec3.Zero);
                    double radius = HitRadius(e) + (m.Kind == MunitionKind.Missile ? MissileProximity : 0.0);
                    if (miss < radius && miss < hitMiss) { hitMiss = miss; hitIdx = ci; }
                }
                if (hitIdx >= 0) { ResolveHit(m, Combatants[hitIdx]); m.Alive = false; break; }
            }
        }
    }

    // A combatant's hull collision radius, derived from the ship's overall length: a sphere that
    // approximates the visible hull so a round passing through the ship registers a hit, rather than
    // slipping past a dimensionless point as it did with the old fixed 20 m radius. Sized toward the
    // generous end (a warship presents radiators and structure, not a bare cylinder) but still far
    // smaller than the kilometre-scale deflections a hard-maneuvering target accrues over a long shot.
    private static double HitRadius(Combatant c)
    {
        double len = c.Ship.TotalLength;
        return Math.Clamp(len * 0.75, 45.0, 150.0);
    }

    private void ResolveHit(Munition m, Combatant tgt)
    {
        Material pen = Materials.Get(m.Material);
        Material armorMat = Materials.Get(tgt.ArmorMaterial);
        double vImpact = (m.Velocity - tgt.Body.Velocity).Length;
        double penetration = Ballistics.TatePenetration(m.Length, pen, armorMat, vImpact);
        double thickness = armorMat.Density > 0.0 ? tgt.Armor / armorMat.Density : 0.0;

        if (penetration > thickness)
        {
            tgt.Armor = Math.Max(0.0, tgt.Armor - tgt.ArmorMax * ArmorErosionPerf);
            tgt.Hull -= m.Kind == MunitionKind.Missile ? MissileHull : KineticHull;
            DamageModule(tgt, m.Kind == MunitionKind.Missile);   // a through-armor hit strikes a module inside
            Hits++;
            Sound?.Invoke(GameSound.Hit);
        }
        else if (thickness > 0.0)
        {
            tgt.Armor = Math.Max(0.0, tgt.Armor - tgt.ArmorMax * 0.03 * (penetration / thickness));
        }
        if (tgt.Hull < 0.0) tgt.Hull = 0.0;
    }

    // Crew survivability layer. Once a ship is crippled - hull low, or its drive or reactor wrecked -
    // an AI ship breaks off to evasive on its own initiative; the player's crew forces evasive only
    // when the hull is about to fail, otherwise it just recommends disengaging. Latched so it fires
    // once per ship rather than every frame.
    private void Survivability(Combatant c)
    {
        if (c.DamageEvasion) return;
        double drive = c.ModuleHealth(PartCategory.Engine);
        double reactor = c.ModuleHealth(PartCategory.Reactor);
        bool crippled = c.Hull < 30.0 || drive < 0.3 || reactor < 0.3;
        if (!crippled) return;

        c.DamageEvasion = true;
        string cause = c.Hull < 30.0 ? "hull failing" : drive < 0.3 ? "drive crippled" : "reactor failing";
        if (!c.IsPlayer)
        {
            c.Doctrine = CombatDoctrine.Evasive;
            LogEvent($"{c.Name}: {cause} - breaking off (evasive)");
        }
        else if (c.Hull < 15.0)
        {
            c.Doctrine = CombatDoctrine.Evasive;
            LogEvent($"{c.Name}: HULL CRITICAL - crew forcing evasive");
        }
        else
        {
            LogEvent($"{c.Name}: {cause} - crew recommends disengaging");
        }
    }

    private static bool IsDamageable(Part p) =>
        p.Category is PartCategory.Engine or PartCategory.Reactor or PartCategory.Radiator
                   or PartCategory.Weapon or PartCategory.Command or PartCategory.FuelTank;

    // Picks a surviving internal module, weighted by length (a proxy for presented cross-section,
    // so larger modules are likelier to be struck).
    private Part? PickModule(Spacecraft ship)
    {
        double total = 0.0;
        foreach (Part p in ship.Parts)
            if (IsDamageable(p) && p.Health > 0.05) total += Math.Max(p.Length, 0.1);
        if (total <= 0.0) return null;

        double r = _rng.NextDouble() * total;
        foreach (Part p in ship.Parts)
        {
            if (!IsDamageable(p) || p.Health <= 0.05) continue;
            r -= Math.Max(p.Length, 0.1);
            if (r <= 0.0) return p;
        }
        return null;
    }

    // Degrades one struck module. A destroyed engine/radiator derates the ship's thrust/cooling
    // (see Spacecraft aggregates); a destroyed weapon stops firing (see the fire-control gates).
    private void DamageModule(Combatant tgt, bool missile)
    {
        Part? p = PickModule(tgt.Ship);
        if (p == null) return;
        double dmg = (missile ? 0.55 : 0.30) + _rng.NextDouble() * (missile ? 0.35 : 0.20);
        p.Health = Math.Max(0.0, p.Health - dmg);
        string state = p.Health <= 0.05 ? "DESTROYED" : $"{p.Health * 100.0:F0}% integrity";
        LogEvent($"{tgt.Name}: {p.Name} {state}");
    }

    // ---- helpers ----
    private int NearestEnemy(int ci)
    {
        Combatant self = Combatants[ci];
        int best = -1; double bestR = double.MaxValue;
        for (int i = 0; i < Combatants.Count; i++)
        {
            if (i == ci || !Combatants[i].Alive) continue;
            double r = (Combatants[i].Body.Position - self.Body.Position).Length;
            if (r < bestR) { bestR = r; best = i; }
        }
        return best;
    }

    private int NearestThreatMissile(int ci, double maxRange)
    {
        Combatant self = Combatants[ci];
        int best = -1; double bestR = maxRange;
        for (int m = 0; m < Munitions.Count; m++)
        {
            Munition mu = Munitions[m];
            if (!mu.Alive || mu.Kind != MunitionKind.Missile || mu.Target != ci) continue;
            double r = (mu.Position - self.Body.Position).Length;
            if (r < bestR) { bestR = r; best = m; }
        }
        return best;
    }

    private static Vec3 GravAccel(PhysicsWorld world, Vec3 pos)
    {
        CelestialBody? b = world.DominantBody(pos);
        if (b == null) return Vec3.Zero;
        Vec3 d = b.Position - pos;
        double r = d.Length;
        if (r < 1.0) return Vec3.Zero;
        return d * (b.Mu / (r * r * r));
    }

    private static double SegPointDistance(Vec3 a, Vec3 b, Vec3 p)
    {
        Vec3 ab = b - a;
        double denom = Vec3.Dot(ab, ab);
        double t = denom > 1e-12 ? Vec3.Dot(p - a, ab) / denom : 0.0;
        if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
        return (a + ab * t - p).Length;
    }

    private static Vec3 Perp(Vec3 dir, Vec3 vel)
    {
        // a unit vector perpendicular to dir, biased to the current velocity side
        Vec3 c = Vec3.Cross(dir, new Vec3(0, 0, 1));
        if (c.Length < 1e-6) c = Vec3.Cross(dir, new Vec3(0, 1, 0));
        c = c.Normalized();
        if (Vec3.Dot(c, vel) < 0.0) c = c * -1.0;
        return c;
    }
}
