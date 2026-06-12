using System.Collections.Generic;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.Combat;

public enum MunitionKind { Slug, Missile }

/// <summary>A short-lived point-defense intercept visual: a gun tracer or a PD-laser beam, drawn from
/// the firing mount to the missile it killed and fading over a fraction of a second.</summary>
public struct PdTracer
{
    public Vec3 From;
    public Vec3 To;
    public float R, G, B;
    public double Ttl;      // seconds of life remaining
    public double Life;     // initial lifetime (for the fade ramp)
    public bool Laser;      // laser beam (thin, steady) vs gun tracer (brief streak)
}

/// <summary>Doctrine selected per side; shapes engagement range, maneuver and salvo behaviour.</summary>
public enum CombatDoctrine { Aggressive, Standoff, Evasive, HoldFire }

/// <summary>A round in flight: an unguided kinetic slug or a self-guided missile.</summary>
public sealed class Munition
{
    public MunitionKind Kind;
    public Vec3 Position;
    public Vec3 Velocity;
    public int Owner;             // index into combatant list
    public int Target;            // index into combatant list
    public bool Alive = true;
    public double Age;
    public double MaxAge;

    // kinetic
    public double Mass;           // kg (slug, or missile current mass)
    public double Length;         // m (penetrator length)
    public string Material = "Tungsten";

    // missile propulsion
    public double Propellant;     // kg
    public double Isp;            // s
    public double Thrust;         // N
}

/// <summary>A combatant: a vessel body in the world plus its loadout, armour, hull and AI state.</summary>
public sealed class Combatant
{
    public required string Name;
    public required RigidBody Body;     // lives in world.Vessels (motion + render)
    public required Spacecraft Ship;    // weapons are its Weapon parts
    public CombatDoctrine Doctrine = CombatDoctrine.Aggressive;
    public bool IsPlayer;               // commanded by the player (mission mode)
    public bool WeaponsHold;            // player order: hold fire (point-defense still active)

    public double Armor;                // current areal density (kg/m^2)
    public double ArmorMax;
    public string ArmorMaterial = "Steel(RHA)";
    public double Hull = 100.0;         // structural integrity (%)
    public double HullMax = 100.0;

    /// <summary>Latched once the crew has reacted to crippling damage (so it fires once, not every frame).</summary>
    public bool DamageEvasion;

    /// <summary>Worst integrity among parts of a category (1 = intact or category absent, 0 = destroyed).</summary>
    public double ModuleHealth(PartCategory cat)
    {
        double worst = 1.0;
        foreach (Part p in Ship.Parts)
            if (p.Category == cat && p.Health < worst) worst = p.Health;
        return worst;
    }

    public double Signature;            // W (IR), for detection
    public double DetectionRange;       // m, how far THIS ship can be seen

    public bool Alive => Hull > 0.0;

    // per-weapon firing cooldowns (s remaining), keyed by the weapon part
    public readonly Dictionary<Part, double> Cooldown = new();
}
