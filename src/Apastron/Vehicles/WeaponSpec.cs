namespace Apastron.Vehicles;

/// <summary>The three weapon families modelled by the ballistics library.</summary>
public enum WeaponKind { Kinetic, Missile, Laser }

/// <summary>
/// Physical parameters for a weapon part. Only the fields relevant to <see cref="Kind"/> are
/// used; the rest stay zero. Material names key into the ballistics material table.
/// </summary>
public sealed class WeaponSpec
{
    public WeaponKind Kind { get; set; }

    /// <summary>Maximum effective engagement range (m). Fire control will not loose a round (or, for a
    /// point-defense mount, attempt an intercept) beyond this. Roughly: fusion torpedoes &gt; 1000 km,
    /// plasma railguns/KKVs to ~1000 km, mass drivers/naval guns to ~100 km, point-defense (40 mm / laser
    /// CIWS) a few tens of km or less.</summary>
    public double Range { get; set; } = 400_000.0;

    // --- kinetic (railgun / coilgun) ---
    public double ProjectileMass { get; set; }      // kg
    public double ProjectileLength { get; set; }    // m (long-rod penetrator length)
    public string ProjectileMaterial { get; set; } = "Tungsten";
    public double MuzzleVelocity { get; set; }      // m/s
    public double RateOfFire { get; set; }          // rounds/s
    public bool PointDefense { get; set; }           // fast short-range gun used against missiles

    /// <summary>Hull damage (% of max) dealt by a single perforating hit from this weapon. Lets a capital
    /// railgun be devastating while a PDC slug barely scratches a warship. 0 falls back to the generic
    /// per-kind value in the combat model.</summary>
    public double HullDamage { get; set; }

    /// <summary>Long-range KKV launcher: looses kinetic-kill vehicles at incoming enemy missiles/torpedoes
    /// (and ballistic threats) far outside point-defense range, rather than firing at ships.</summary>
    public bool Interceptor { get; set; }

    // --- laser ---
    public double BeamPower { get; set; }           // W delivered to the beam
    public double Aperture { get; set; }            // m, mirror/lens diameter
    public double Wavelength { get; set; }          // m
    public double BeamQuality { get; set; } = 1.5;  // M^2 (1 = diffraction limited)

    // --- missile ---
    public double MissileDryMass { get; set; }      // kg
    public double MissilePropellant { get; set; }   // kg
    public double MissileIsp { get; set; }          // s
    public double MissileThrust { get; set; }       // N

    /// <summary>Magazine-fed ammunition: rounds in a full magazine before an automated reload kicks in. 0 means
    /// the weapon is not magazine-limited (railguns/lasers fire as fast as their cooldown allows). Used for the
    /// PDC ammo belt and the torpedo/KKV magazines.</summary>
    public int MagazineSize { get; set; }

    /// <summary>Seconds to reload an emptied magazine from onboard stores. Only meaningful when
    /// <see cref="MagazineSize"/> &gt; 0; the mount cannot fire while reloading.</summary>
    public double ReloadTime { get; set; }

    public WeaponSpec Clone() => (WeaponSpec)MemberwiseClone();
}

/// <summary>Armor described by areal density (kg/m^2) over a material — what the ballistics
/// library needs to turn a penetration depth into a perforation verdict.</summary>
public sealed class ArmorSpec
{
    public double ArealDensity { get; set; }        // kg/m^2
    public string Material { get; set; } = "Steel(RHA)";

    public ArmorSpec Clone() => (ArmorSpec)MemberwiseClone();
}
