using System.Collections.Generic;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.Combat;

public enum MunitionKind { Slug, Missile, KKV }

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

/// <summary>A short-lived expanding light bloom from a warhead detonating on/near a target. Purely visual;
/// the renderer draws it as an additive sphere that grows and fades over its life.</summary>
public struct Explosion
{
    public Vec3 Position;
    public double Ttl;      // seconds of life remaining
    public double Life;     // initial lifetime (drives the expand + fade ramp)
    public double Size;     // relative peak-radius hint (1 = a torpedo warhead)
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
    public double HullDamage = 6.0;   // hull % this round removes on a perforating/warhead hit

    // missile propulsion
    public double Propellant;     // kg
    public double Isp;            // s
    public double Thrust;         // N

    /// <summary>For a KKV interceptor: the incoming missile it is chasing. When that missile dies, the KKV
    /// loses guidance and coasts to expiry; on proximity the KKV kills it (and itself).</summary>
    public Munition? Chase;
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

    // magazine-fed ammunition, keyed by the weapon part: rounds currently loaded, and reload timer
    // (s remaining; 0 = loaded/idle). Only populated for weapons whose WeaponSpec.MagazineSize &gt; 0.
    public readonly Dictionary<Part, int> Ammo = new();
    public readonly Dictionary<Part, double> Reload = new();

    /// <summary>Build a combatant from a ship, taking its armour pool and material from the ship's armour
    /// parts (fallback 1 kg/m^2 / steel). Hull starts intact.</summary>
    public static Combatant Create(string name, RigidBody body, Spacecraft ship, CombatDoctrine doctrine)
    {
        double armor = 0.0; string mat = "Steel(RHA)"; bool gotMat = false;
        foreach (Part p in ship.Parts)
        {
            var arm = p.Armor;
            if (arm == null) continue;
            armor += arm.ArealDensity;
            if (!gotMat) { mat = arm.Material; gotMat = true; }
        }
        if (armor <= 0.0) armor = 1.0;
        var c = new Combatant
        {
            Name = name, Body = body, Ship = ship, Doctrine = doctrine,
            Armor = armor, ArmorMax = armor, ArmorMaterial = mat,
            Hull = 100.0, HullMax = 100.0,
        };
        // start every magazine-fed mount with a full magazine
        foreach (Part p in ship.Parts)
        {
            var w = p.Weapon;
            if (w != null && w.MagazineSize > 0) c.Ammo[p] = w.MagazineSize;
        }
        return c;
    }
}
