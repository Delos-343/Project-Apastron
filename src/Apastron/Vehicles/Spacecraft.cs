using System;
using System.Collections.Generic;
using System.Linq;
using Apastron.Core;

namespace Apastron.Vehicles;

/// <summary>Persistent thrust direction selected by the flight controls or maneuver autopilot.</summary>
public enum ThrustMode { None, Prograde, Retrograde, RadialOut, RadialIn, Inertial }

/// <summary>
/// A parts-based spacecraft. Aggregate properties (mass, centre of mass, thrust, effective
/// Isp, delta-v, TWR, thermal balance) are derived from the parts so the design's
/// consequences are explicit. Delta-v is the single-stage Tsiolkovsky value; staging
/// arrives in a later chunk. Parts are stacked along one axis for centre-of-mass.
/// </summary>
public sealed class Spacecraft
{
    public string Name { get; set; } = "Custom Vessel";

    /// <summary>The hull class this ship was built as, when it came from <see cref="WarshipClasses"/>. Null for
    /// hand-built/custom ships. Used by the renderer to pick a distinct model per ship type.</summary>
    public HullClass? Class { get; set; }
    public List<Part> Parts { get; } = new();

    // --- flight state (applied by Propulsion each step) ---
    public ThrustMode Mode { get; set; } = ThrustMode.None;
    public double Throttle { get; set; } = 1.0;   // 0..1

    /// <summary>World-frame burn direction used when <see cref="Mode"/> is Inertial (node autopilot).</summary>
    public Vec3 BurnDirectionWorld { get; set; } = Vec3.Zero;

    // --- mass ---
    // These aggregates are read every frame for every combatant, so they use plain loops rather than LINQ:
    // Parts.Sum/Where each allocate an enumerator per call, which adds up to real GC pressure in a busy
    // fleet engagement. The results are identical; only the allocations are gone.
    public double DryMass            { get { double s = 0.0; foreach (Part p in Parts) s += p.DryMass; return s; } }
    public double PropellantMass     { get { double s = 0.0; foreach (Part p in Parts) s += p.Propellant; return s; } }
    public double PropellantCapacity { get { double s = 0.0; foreach (Part p in Parts) s += p.PropellantCapacity; return s; } }
    public double TotalMass          => DryMass + PropellantMass;
    public double TotalLength        { get { double s = 0.0; foreach (Part p in Parts) s += p.Length; return s; } }

    // --- propulsion ---
    public double TotalThrustVac
    {
        get { double s = 0.0; foreach (Part p in Parts) if (p.IsEngine) s += p.ThrustVac * p.Health; return s; }
    }

    /// <summary>Thrust-weighted vacuum Isp of all engines: F_total / Sum(F_i / Isp_i).</summary>
    public double EffectiveIsp
    {
        get
        {
            double f = TotalThrustVac;
            if (f <= 0.0) return 0.0;
            double denom = 0.0;
            foreach (Part p in Parts)
                if (p.IsEngine && p.IspVac > 0.0) denom += p.ThrustVac * p.Health / p.IspVac;
            return denom > 0.0 ? f / denom : 0.0;
        }
    }

    /// <summary>Propellant mass flow at full throttle (kg/s): Sum(F_i / (Isp_i * g0)).</summary>
    public double MassFlowFullThrust
    {
        get
        {
            double s = 0.0;
            foreach (Part p in Parts)
                if (p.IsEngine && p.IspVac > 0.0) s += p.ThrustVac * p.Health / (p.IspVac * MathConstants.StandardGravity);
            return s;
        }
    }

    /// <summary>Single-stage delta-v (m/s) via the rocket equation: Isp * g0 * ln(m0 / mf).</summary>
    public double DeltaV
    {
        get
        {
            double isp = EffectiveIsp;
            double m0 = TotalMass, mf = DryMass;
            if (isp <= 0.0 || mf <= 0.0 || m0 <= mf) return 0.0;
            return isp * MathConstants.StandardGravity * Math.Log(m0 / mf);
        }
    }

    public double BurnTimeFullThrust => MassFlowFullThrust > 0.0 ? PropellantMass / MassFlowFullThrust : 0.0;

    /// <summary>Thrust-to-weight ratio against a reference surface gravity g (m/s^2).</summary>
    public double TwrAt(double g) => (TotalMass > 0.0 && g > 0.0) ? TotalThrustVac / (TotalMass * g) : 0.0;

    // --- thermal (simplified: all heat output must be matched by radiator capacity) ---
    public double HeatLoad              { get { double s = 0.0; foreach (Part p in Parts) s += p.HeatOutput; return s; } }
    public double HeatRejectionCapacity { get { double s = 0.0; foreach (Part p in Parts) s += p.HeatRejection * p.Health; return s; } }
    public double HeatMargin            => HeatRejectionCapacity - HeatLoad;

    /// <summary>Constant heat from non-engine sources (reactors); independent of throttle.</summary>
    public double ReactorHeat    { get { double s = 0.0; foreach (Part p in Parts) if (!p.IsEngine) s += p.HeatOutput; return s; } }

    /// <summary>Engine waste heat at full throttle (scales linearly with throttle in flight).</summary>
    public double EngineHeatFull { get { double s = 0.0; foreach (Part p in Parts) if (p.IsEngine) s += p.HeatOutput; return s; } }

    /// <summary>Highest throttle the radiators can sustain: (rejection - reactor heat) / engine heat.</summary>
    public double SustainableThrottle
    {
        get
        {
            double ehf = EngineHeatFull;
            if (ehf <= 0.0) return 1.0;
            double t = (HeatRejectionCapacity - ReactorHeat) / ehf;
            return t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
        }
    }

    /// <summary>Effective exhaust velocity (m/s): Isp * g0.</summary>
    public double ExhaustVelocity => EffectiveIsp * MathConstants.StandardGravity;

    /// <summary>Acceleration (m/s^2) at a throttle setting and the current total mass.</summary>
    public double AccelerationAt(double throttle) => TotalMass > 0.0 ? throttle * TotalThrustVac / TotalMass : 0.0;

    /// <summary>Centre of mass measured from the base of the stack (m).</summary>
    public double CenterOfMass
    {
        get
        {
            double m = TotalMass;
            if (m <= 0.0) return 0.0;
            double weighted = 0.0, x = 0.0;
            foreach (Part p in Parts)
            {
                double centroid = x + p.Length * 0.5;
                weighted += centroid * p.WetMass;
                x += p.Length;
            }
            return weighted / m;
        }
    }

    /// <summary>Drain a propellant mass, taken proportionally from every tank that has fuel.</summary>
    public void Consume(double kg)
    {
        if (kg <= 0.0) return;
        double avail = PropellantMass;
        if (avail <= 0.0) return;
        double frac = Math.Min(kg, avail) / avail;
        foreach (Part p in Parts)
            if (p.Propellant > 0.0)
                p.Propellant -= p.Propellant * frac;
    }

    public void Refill() { foreach (Part p in Parts) if (p.PropellantCapacity > 0.0) p.Propellant = p.PropellantCapacity; }

    /// <summary>Add up to <paramref name="kg"/> of propellant, filling tanks with spare capacity
    /// in order (station replenishment). Returns the mass actually taken on.</summary>
    public double AddPropellant(double kg)
    {
        double left = Math.Max(kg, 0.0);
        foreach (Part p in Parts)
        {
            if (left <= 0.0) break;
            double room = p.PropellantCapacity - p.Propellant;
            if (room <= 0.0) continue;
            double take = Math.Min(room, left);
            p.Propellant += take;
            left -= take;
        }
        return Math.Max(kg, 0.0) - left;
    }
    public void Add(Part p) => Parts.Add(p);
    public void RemoveAt(int i) { if (i >= 0 && i < Parts.Count) Parts.RemoveAt(i); }
    public void Clear() { Parts.Clear(); Mode = ThrustMode.None; }

    /// <summary>A compact default vessel on the standard Sol-fleet power stack: a fusion torch
    /// fed by a tokamak reactor, with radiators sized to run both flat-out.</summary>
    public static Spacecraft CreateStarter()
    {
        var s = new Spacecraft { Name = "Pathfinder" };
        Part T(string n) => PartCatalog.Templates.First(t => t.Name == n).Clone();
        s.Add(T("Command Pod"));
        s.Add(T("Tokamak Reactor"));
        s.Add(T("Fusion Torch"));
        s.Add(T("Reaction Mass Tank"));
        s.Add(T("Reaction Mass Tank"));
        s.Add(T("Droplet Radiator"));
        s.Add(T("Droplet Radiator"));
        return s;
    }

    /// <summary>
    /// A combat vessel for the duel scenario: fusion drive for maneuvering, tokamak reactor + radiators,
    /// a steel belt, and a mixed battery (railgun, missile rack, laser, point-defense gun).
    /// </summary>
    public static Spacecraft CreateWarship(string name = "Warship")
    {
        var s = new Spacecraft { Name = name };
        Part Template(string n) => PartCatalog.Templates.First(t => t.Name == n).Clone();
        s.Add(Template("Command Pod"));
        s.Add(Template("Tokamak Reactor"));
        s.Add(Template("Fusion Torch"));
        s.Add(Template("Reaction Mass Tank"));
        s.Add(Template("Reaction Mass Tank"));
        s.Add(Template("Droplet Radiator"));
        s.Add(Template("Droplet Radiator"));
        s.Add(Template("Steel Belt"));
        s.Add(Template("Railgun"));
        s.Add(Template("Fusion Torpedo Launcher"));
        s.Add(Template("Pulse Laser Cluster"));
        s.Add(Template("40mm PDC"));
        return s;
    }

    /// <summary>
    /// The reference fusion torchship for the interplanetary scenario: an Epstein-type torch with
    /// five reaction-mass tanks and three droplet radiators, plus a command pod and habitat.
    /// </summary>
    public static Spacecraft CreateTorchship()
    {
        var s = new Spacecraft { Name = "Torchship-1" };
        Part Template(string name) => PartCatalog.Templates.First(t => t.Name == name).Clone();
        s.Add(Template("Command Pod"));
        s.Add(Template("Habitat Module"));
        s.Add(Template("Tokamak Reactor"));
        for (int i = 0; i < 5; i++) s.Add(Template("Reaction Mass Tank"));
        s.Add(Template("Epstein-Type Torch"));
        for (int i = 0; i < 3; i++) s.Add(Template("Droplet Radiator"));
        return s;
    }

    /// <summary>
    /// SCAS DUAT - "Sol Colonial Administration Ship: Duat" - a general-purpose ship-of-the-line of the
    /// destroyer type (~175 m). A fusion-torch array of three high-efficiency Epstein-type drives for cruise
    /// and combat maneuvering, a tokamak reactor for the ship load, five reaction-mass tanks for endurance,
    /// and nine droplet radiators sized so the array sustains full burn. Tungsten + steel armour. Armament is
    /// a plasma-augmented railgun, two quad-missile racks, and four 40 mm point-defense cannons. Crewed.
    /// (The rendered hull is the shared stylized warship model; it does not depict this exact fit.)
    /// </summary>
    public static Spacecraft CreateDuat()
    {
        var s = new Spacecraft { Name = "SCAS DUAT" };
        Part Template(string name) => PartCatalog.Templates.First(t => t.Name == name).Clone();

        // SCAS Duat - a kinetics-based fast-attack destroyer. Built around speed and guns, not staying power:
        // a single high-thrust Epstein drive on a light Whipple belt makes her one of the most agile hulls in
        // her weight, and she fights with a spinal railgun and rapid coilguns up close behind a dense PDC screen.
        s.Add(Template("Command Pod"));
        s.Add(Template("Habitat Module"));

        // power & propulsion: one Epstein torch run light (a single compact tank) for a high thrust-to-mass
        s.Add(Template("Tokamak Reactor"));
        s.Add(Template("Epstein-Type Torch"));
        s.Add(Template("Reaction Tank (compact)"));
        for (int i = 0; i < 3; i++) s.Add(Template("Droplet Radiator"));   // sized to sustain the Epstein burn

        // protection: a light Whipple belt only - she trades armour for acceleration
        s.Add(Template("Whipple Shield"));

        // armament: a keel spinal railgun and twin rapid coilguns (the kinetic punch), a dense 40 mm PDC
        // screen, and a single torpedo tube for opportunistic long-range shots
        s.Add(Template("Spinal Railgun"));
        s.Add(Template("Light Coilgun"));
        s.Add(Template("Light Coilgun"));
        s.Add(Template("40mm PDC"));
        s.Add(Template("40mm PDC"));
        s.Add(Template("40mm PDC"));
        s.Add(Template("Fusion Torpedo Launcher"));

        // structural spine
        s.Add(Template("Structural Truss"));
        return s;
    }
}
