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
    // warhead-detonation blooms (expanding light), each fading over a short life.
    public readonly List<Explosion> Explosions = new();

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
    private const int    SalvoPerLauncher = 5;     // in-flight torpedoes allowed per working launcher
    private const int    KkvInFlightCap   = 6;     // KKV interceptors in flight per launcher
    private const double KkvMaxAge        = 120.0; // s before an interceptor that lost its target expires
    private const double KkvKillRadius    = 120.0; // m proximity at which a KKV kills the torpedo it chases
    private const double LanceRefPower    = 60_000_000.0; // W, nominal solar-lance beam power (scales ablation)
    private const double LanceArmorAblate = 25.0;  // kg/m^2 of armour melted per second at reference power
    private const double LanceHullDps      = 3.0;  // hull % per second once the armour belt is stripped
    private const double LaserKnifeRange   = 12.0e3;  // m - pulse-laser anti-ship "knife" envelope
    private const double LaserKnifeArmor   = 140.0;   // kg/m^2 of armour melted per second at knife range
    private const double LaserKnifeHull    = 12.0;    // hull % per second once the belt is stripped (devastating)
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
            c.Body.MaxThrust = c.Ship.TotalThrustVac;   // keep fleet/autopilot steering's accel cap current
            c.Body.Controllable = c.IsPlayer;           // only the player's ships are RTS-selectable
            c.Body.HullLength = c.Ship.TotalLength;     // class-scaled hull rendering
            StepAI(world, c, i, dt);
            Survivability(c);
        }
        for (int i = 0; i < Combatants.Count; i++)
        {
            Combatant c = Combatants[i];
            if (!c.Alive) continue;
            Fire(world, c, i, dt);
            PointDefense(c, i, dt);
            Interceptors(c, i, dt);
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
            for (int i = Explosions.Count - 1; i >= 0; i--)
            {
                Explosion x = Explosions[i];
                x.Ttl -= dt;
                if (x.Ttl <= 0.0) Explosions.RemoveAt(i);
                else Explosions[i] = x;
            }
        }
    }

    // ---- AI maneuver: set the body's thrust and recompute signature/detection ----
    private void StepAI(PhysicsWorld world, Combatant c, int ci, double dt)
    {
        int ei = BestTarget(ci);
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
            // press the kill: an aggressor closes hard on a wounded target to finish it fast
            if (c.Doctrine == CombatDoctrine.Aggressive && e.HullMax > 0.0 && e.Hull < 0.4 * e.HullMax)
                desired *= 0.45;

            int ti = NearestThreatMissile(ci, ThreatReactRange);
            if (ti >= 0 && c.Doctrine != CombatDoctrine.HoldFire)
            {
                // an inbound torpedo is the priority - break perpendicular to it, but keep a bias toward the
                // engagement band so we don't drift out of the fight while dodging
                Vec3 tref = Munitions[ti].Position - c.Body.Position;
                Vec3 dodge = Perp(tref.Length > 1e-6 ? tref.Normalized() : losHat, c.Body.Velocity);
                Vec3 bias = range > desired * 1.1 ? losHat : (range < desired * 0.9 ? losHat * -1.0 : Vec3.Zero);
                want = dodge * 0.75 + bias * 0.5;
            }
            else if (c.Doctrine == CombatDoctrine.Evasive)
            {
                want = range < desired ? losHat * -1.0 : Perp(losHat, c.Body.Velocity);
            }
            else
            {
                // hold the preferred band, but always weave laterally so we are never a static gun target
                Vec3 radial = range > desired * 1.08 ? losHat : (range < desired * 0.92 ? losHat * -1.0 : Vec3.Zero);
                Vec3 strafe = Perp(losHat, c.Body.Velocity) * 0.55;
                want = radial + strafe;
            }
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

    // Picks the most valuable hostile to focus: a blend of how wounded it is (finish the kill) and how close
    // it is (shots that actually connect). Concentrating fire this way is what ends engagements decisively.
    private int BestTarget(int ci)
    {
        Combatant self = Combatants[ci];
        int best = -1; double bestScore = double.MinValue;
        for (int i = 0; i < Combatants.Count; i++)
        {
            Combatant o = Combatants[i];
            if (i == ci || !o.Alive || o.IsPlayer == self.IsPlayer) continue;
            double range = (o.Body.Position - self.Body.Position).Length;
            double wounded = o.HullMax > 0.0 ? 1.0 - o.Hull / o.HullMax : 0.0;   // 0 healthy .. 1 nearly dead
            double prox = 1.0 / (1.0 + range / 200.0e3);                          // 1 close .. ->0 far
            double score = wounded * 1.5 + prox;
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    // ---- ammunition / magazines ----
    // A magazine-fed mount (PDC belt, torpedo/KKV magazine) must have a loaded round to fire; once emptied it
    // is unavailable for ReloadTime seconds while it reloads from onboard stores, then refills. Non-magazine
    // weapons (MagazineSize 0: railguns, lasers) are always ready. Call WeaponReady every frame for the mount
    // (it advances any reload in progress); call ConsumeRound only when a round is actually loosed.
    private bool WeaponReady(Combatant c, Part p, WeaponSpec w, double dt)
    {
        if (w.MagazineSize <= 0) return true;   // not magazine-limited

        if (c.Reload.TryGetValue(p, out double rt) && rt > 0.0)
        {
            rt -= dt;
            if (rt <= 0.0) { c.Reload[p] = 0.0; c.Ammo[p] = w.MagazineSize; }   // reload complete - magazine refilled
            else { c.Reload[p] = rt; return false; }                            // still reloading
        }

        int ammo = c.Ammo.TryGetValue(p, out int a) ? a : w.MagazineSize;
        if (ammo <= 0) { c.Reload[p] = w.ReloadTime; return false; }            // empty - begin reload
        return true;
    }

    private void ConsumeRound(Combatant c, Part p, WeaponSpec w)
    {
        if (w.MagazineSize <= 0) return;
        int ammo = (c.Ammo.TryGetValue(p, out int a) ? a : w.MagazineSize) - 1;
        c.Ammo[p] = ammo;
        if (ammo <= 0) c.Reload[p] = w.ReloadTime;   // just emptied - start the reload clock now
    }

    // ---- main weapons ----
    private void Fire(PhysicsWorld world, Combatant c, int ci, double dt)
    {
        int ei = BestTarget(ci);
        Combatant? e = ei >= 0 ? Combatants[ei] : null;
        double range = e != null ? (e.Body.Position - c.Body.Position).Length : double.MaxValue;
        bool detected = e != null && range < e.DetectionRange;

        // Torpedoes are fired in waves: the in-flight cap scales with how many (working) launchers the ship
        // carries, so a battleship sustains a far bigger salvo than a drone. (Floored at the scenario cap.)
        int launchers = 0;
        foreach (Part pp in c.Ship.Parts)
            if (pp.Category == PartCategory.Weapon && pp.Weapon != null &&
                pp.Weapon.Kind == WeaponKind.Missile && pp.Health > 0.05) launchers++;
        int salvoCap = Math.Max(MissileSalvoCap, launchers * SalvoPerLauncher);

        foreach (Part p in c.Ship.Parts)
        {
            if (p.Category != PartCategory.Weapon || p.Weapon == null || p.Health <= 0.05) continue;
            WeaponSpec w = p.Weapon;
            if (w.PointDefense) continue;   // handled in PointDefense
            if (w.Interceptor) continue;    // KKV launchers handled in Interceptors
            if (!WeaponReady(c, p, w, dt)) continue;   // magazine-fed mounts (torpedoes) must be loaded

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
                                Material = w.ProjectileMaterial, MaxAge = SlugMaxAge, HullDamage = w.HullDamage,
                            });
                            SlugsFired++;
                            Sound?.Invoke(GameSound.SlugFire);
                            cd = w.RateOfFire > 0.0 ? 1.0 / w.RateOfFire : 1.0;
                        }
                        break;

                    case WeaponKind.Missile:
                        int inFlight = Munitions.Count(m => m.Owner == ci && m.Kind == MunitionKind.Missile);
                        if (cd <= 0.0 && range < w.Range && inFlight < salvoCap)
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
                                Length = 0.6, Material = "Tungsten", MaxAge = MissileMaxAge, HullDamage = w.HullDamage,
                            });
                            ConsumeRound(c, p, w);
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

                    case WeaponKind.Laser:
                        // Anti-ship directed energy (solar lance): a sustained beam that slowly ablates the
                        // target's armour belt at medium range, biting hull once the belt is gone. Continuous
                        // while the nearest enemy is in reach (no cooldown), drawn as a violet-white beam.
                        if (range < w.Range)
                        {
                            double dose = (w.BeamPower / LanceRefPower) * dt;
                            if (e.Armor > 0.0)
                            {
                                e.Armor -= LanceArmorAblate * dose;
                                if (e.Armor < 0.0) e.Armor = 0.0;
                            }
                            else e.Hull -= LanceHullDps * dose;
                            Beams.Add((c.Body.Position, e.Body.Position, 1.00f, 0.55f, 0.95f));
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
        // nearest hostile, for the pulse-laser knife-range bite (computed once, reused across mounts)
        int ei = NearestEnemy(ci);
        Combatant? foe = ei >= 0 ? Combatants[ei] : null;
        double foeRange = foe != null ? (foe.Body.Position - c.Body.Position).Length : double.MaxValue;
        bool canBurn = foe != null && c.Doctrine != CombatDoctrine.HoldFire && !c.WeaponsHold;

        foreach (Part p in c.Ship.Parts)
        {
            WeaponSpec? w = p.Weapon;
            if (p.Category != PartCategory.Weapon || w == null || !w.PointDefense || p.Health <= 0.05) continue;

            // pulse-laser clusters also melt ships at knife range - a continuous burn, independent of the
            // intercept duty cycle below. Devastating once an enemy closes inside the knife envelope.
            if (w.Kind == WeaponKind.Laser && canBurn && foeRange < LaserKnifeRange)
            {
                if (foe!.Armor > 0.0) { foe.Armor -= LaserKnifeArmor * dt; if (foe.Armor < 0.0) foe.Armor = 0.0; }
                else foe.Hull -= LaserKnifeHull * dt;
                Beams.Add((c.Body.Position, foe.Body.Position, 0.70f, 0.90f, 1.00f));
            }

            if (!WeaponReady(c, p, w, dt)) continue;   // PDC ammo belt: nothing to fire while empty/reloading

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
                ConsumeRound(c, p, w);
                Intercepts++;
                Sound?.Invoke(GameSound.Intercept);
                cd = w.RateOfFire > 0.0 ? 1.0 / w.RateOfFire : 0.2;
            }
            c.Cooldown[p] = cd;
        }
    }

    // ---- long-range KKV defense: loose a kinetic-kill vehicle at the nearest inbound enemy torpedo,
    //      far outside the point-defense envelope. Each launcher self-limits by KKVs already in flight. ----
    private void Interceptors(Combatant c, int ci, double dt)
    {
        foreach (Part p in c.Ship.Parts)
        {
            WeaponSpec? w = p.Weapon;
            if (p.Category != PartCategory.Weapon || w == null || !w.Interceptor || p.Health <= 0.05) continue;
            if (!WeaponReady(c, p, w, dt)) continue;   // KKV magazine must be loaded
            double cd = c.Cooldown.TryGetValue(p, out double v) ? v : 0.0;
            if (cd > 0.0) { cd -= dt; c.Cooldown[p] = cd; continue; }

            int inFlight = 0;
            foreach (Munition mm in Munitions)
                if (mm.Alive && mm.Kind == MunitionKind.KKV && mm.Owner == ci) inFlight++;
            if (inFlight >= KkvInFlightCap) { c.Cooldown[p] = cd; continue; }

            // nearest inbound enemy torpedo in range that no friendly KKV is already chasing
            int best = -1; double bestD = w.Range;
            for (int mi = 0; mi < Munitions.Count; mi++)
            {
                Munition t = Munitions[mi];
                if (!t.Alive || t.Kind != MunitionKind.Missile) continue;
                if (t.Owner < 0 || t.Owner >= Combatants.Count) continue;
                if (Combatants[t.Owner].IsPlayer == c.IsPlayer) continue;   // only enemy torpedoes
                if (AlreadyChased(t)) continue;
                double d = (t.Position - c.Body.Position).Length;
                if (d < bestD) { bestD = d; best = mi; }
            }
            if (best < 0) { c.Cooldown[p] = cd; continue; }

            Munition target = Munitions[best];
            Vec3 los = target.Position - c.Body.Position;
            Vec3 lh = los.Length > 1e-6 ? los.Normalized() : Vec3.UnitX;
            Munitions.Add(new Munition
            {
                Kind = MunitionKind.KKV, Owner = ci, Target = -1, Chase = target,
                Position = c.Body.Position,
                Velocity = c.Body.Velocity + lh * 200.0,
                Mass = w.MissileDryMass + w.MissilePropellant,
                Propellant = w.MissilePropellant, Isp = w.MissileIsp, Thrust = w.MissileThrust,
                Length = 0.4, Material = "Tungsten", MaxAge = KkvMaxAge,
            });
            ConsumeRound(c, p, w);
            Sound?.Invoke(GameSound.MissileLaunch);
            cd = w.RateOfFire > 0.0 ? 1.0 / w.RateOfFire : 0.5;
            c.Cooldown[p] = cd;
        }
    }

    private bool AlreadyChased(Munition target)
    {
        foreach (Munition m in Munitions)
            if (m.Alive && m.Kind == MunitionKind.KKV && ReferenceEquals(m.Chase, target)) return true;
        return false;
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

    // Queue a detonation bloom (purely visual; aged/expired in Update, drawn additively by the renderer).
    private void AddExplosion(Vec3 at, double size, double life)
        => Explosions.Add(new Explosion { Position = at, Ttl = life, Life = life, Size = size });

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
                else if (m.Kind == MunitionKind.KKV && m.Propellant > 0.0 && m.Chase != null && m.Chase.Alive && m.Mass > 0.0)
                {
                    double aMax = m.Thrust / m.Mass;
                    Vec3 dir = FireControl.GuidanceDir(m.Position, m.Velocity, m.Chase.Position, m.Chase.Velocity, aMax, N);
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

                if (m.Kind == MunitionKind.KKV)
                {
                    // KKV kills the torpedo it chases on proximity (both vanish in a small flash); it never
                    // strikes ships. If its quarry is already gone it just coasts to expiry.
                    if (m.Chase != null && m.Chase.Alive &&
                        (m.Position - m.Chase.Position).Length < KkvKillRadius)
                    {
                        m.Chase.Alive = false;
                        m.Alive = false;
                        AddExplosion(m.Position, 0.5, 0.5);
                        Intercepts++;
                        Sound?.Invoke(GameSound.Intercept);
                        break;
                    }
                }
                else
                {
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
        // A torpedo warhead detonates on proximity whether or not it perforates - bloom either way.
        if (m.Kind == MunitionKind.Missile) AddExplosion(m.Position, 1.0, 0.75);

        Material pen = Materials.Get(m.Material);
        Material armorMat = Materials.Get(tgt.ArmorMaterial);
        double vImpact = (m.Velocity - tgt.Body.Velocity).Length;
        double penetration = Ballistics.TatePenetration(m.Length, pen, armorMat, vImpact);
        double thickness = armorMat.Density > 0.0 ? tgt.Armor / armorMat.Density : 0.0;

        if (penetration > thickness)
        {
            tgt.Armor = Math.Max(0.0, tgt.Armor - tgt.ArmorMax * ArmorErosionPerf);
            double dmg = m.HullDamage > 0.0 ? m.HullDamage : (m.Kind == MunitionKind.Missile ? MissileHull : KineticHull);
            tgt.Hull -= dmg;
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
            Combatant o = Combatants[i];
            if (i == ci || !o.Alive || o.IsPlayer == self.IsPlayer) continue;   // opposing side only
            double r = (o.Body.Position - self.Body.Position).Length;
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
