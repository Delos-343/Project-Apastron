using System.Collections.Generic;

namespace Apastron.Vehicles;

/// <summary>
/// A small catalogue of part templates with deliberately contrasting engine archetypes:
/// chemical (high thrust, low Isp), nuclear-thermal (mid), and ion (tiny thrust, huge Isp).
/// Reactors produce heat that radiators must reject. Numbers are plausible, not exact.
/// </summary>
public static class PartCatalog
{
    public static IReadOnlyList<Part> Templates { get; } = new List<Part>
    {
        new() { Name = "Command Pod",       Category = PartCategory.Command,   DryMass = 800,  Length = 1.2 },
        new() { Name = "Habitat Module",    Category = PartCategory.Habitat,   DryMass = 2500, Length = 3.0 },
        new() { Name = "Fuel Tank (small)", Category = PartCategory.FuelTank,  DryMass = 150,  Length = 1.5, PropellantCapacity = 1500,  Propellant = 1500 },
        new() { Name = "Fuel Tank (large)", Category = PartCategory.FuelTank,  DryMass = 500,  Length = 4.0, PropellantCapacity = 8000,  Propellant = 8000 },
        new() { Name = "Chemical Engine",   Category = PartCategory.Engine,    DryMass = 600,  Length = 2.0, ThrustVac = 220000, IspVac = 340,  HeatOutput = 50000 },
        new() { Name = "Nuclear Thermal",   Category = PartCategory.Engine,    DryMass = 2300, Length = 3.0, ThrustVac = 65000,  IspVac = 900,  HeatOutput = 500000 },
        new() { Name = "Ion Thruster",      Category = PartCategory.Engine,    DryMass = 100,  Length = 1.0, ThrustVac = 250,    IspVac = 4200, HeatOutput = 5000 },
        new() { Name = "Fission Reactor",   Category = PartCategory.Reactor,   DryMass = 1500, Length = 2.0, HeatOutput = 800000 },
        new() { Name = "Radiator Panel",    Category = PartCategory.Radiator,  DryMass = 200,  Length = 1.0, HeatRejection = 300000 },
        new() { Name = "Structural Truss",  Category = PartCategory.Structure, DryMass = 300,  Length = 2.0 },

        // --- fusion-torch tier (mid-future): high thrust AND high Isp, enormous waste heat ---
        new() { Name = "Tokamak Reactor",    Category = PartCategory.Reactor,  DryMass = 9000,  Length = 4.0,  HeatOutput = 60_000_000 },
        new() { Name = "Fusion Torch",       Category = PartCategory.Engine,   DryMass = 25000, Length = 12.0, ThrustVac = 2_500_000, IspVac = 12000, HeatOutput = 7_350_000_000 },
        new() { Name = "Epstein-Type Torch", Category = PartCategory.Engine,   DryMass = 30000, Length = 14.0, ThrustVac = 4_000_000, IspVac = 60000, HeatOutput = 12_000_000_000 },
        new() { Name = "Compact Fusion Drive", Category = PartCategory.Engine,  DryMass = 6000,  Length = 8.0,  ThrustVac = 900_000,   IspVac = 9000,  HeatOutput = 1_200_000_000 },
        new() { Name = "Reaction Mass Tank", Category = PartCategory.FuelTank,  DryMass = 3000,  Length = 8.0,  PropellantCapacity = 200000, Propellant = 200000 },
        new() { Name = "Reaction Tank (compact)", Category = PartCategory.FuelTank, DryMass = 1500, Length = 5.0, PropellantCapacity = 40000, Propellant = 40000 },
        new() { Name = "High-Temp Radiator", Category = PartCategory.Radiator,  DryMass = 4000,  Length = 6.0,  HeatRejection = 1_500_000_000 },
        new() { Name = "Droplet Radiator",   Category = PartCategory.Radiator,  DryMass = 7000,  Length = 5.0,  HeatRejection = 4_000_000_000 },

        // --- weapons ----------------------------------------------------------------------------
        // Doctrine (transient firing heat is out of the steady-state thermal model):
        //   * Fusion torpedoes are the PRIMARY armament of every class, fired in waves (precise, guided,
        //     very long reach). Point-defense is what stops them.
        //   * Direct-fire main batteries climb the ladder: a single spinal railgun on destroyers and light
        //     cruisers; a PAIR of turreted high-caliber railguns on the largest ships. Smaller hulls carry
        //     no main gun and fight purely on torpedoes + PD.
        //   * Point-defense is universal 40 mm PDCs (lethal auto-tracking), with pulse-laser clusters added
        //     on larger/advanced ships for very close anti-missile work.

        // Primary armament (all classes): a fusion-driven guided torpedo. High specific impulse gives it a
        // long powered flyout and the agility to run down a maneuvering target; launched in salvos / waves.
        new() { Name = "Fusion Torpedo Launcher", Category = PartCategory.Weapon, DryMass = 3000, Length = 5.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Missile, MissileDryMass = 250.0, MissilePropellant = 350.0,
                                          MissileIsp = 6000.0, MissileThrust = 120000.0, Range = 3000.0e3,
                                          HullDamage = 35.0, MagazineSize = 8, ReloadTime = 25.0 } },

        // Main battery (destroyer + light cruiser): one keel-mounted spinal railgun - a heavy long-rod
        // penetrator at extreme velocity and the longest kinetic reach in the fleet, but slow-firing and massive.
        new() { Name = "Spinal Railgun", Category = PartCategory.Weapon, DryMass = 18000, Length = 16.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 6.0, ProjectileLength = 0.55,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 4000.0, RateOfFire = 0.2,
                                          Range = 1500.0e3, HullDamage = 30.0 } },

        // Main battery (capitals, mounted in pairs): a turreted high-caliber, high-yield railgun. All-aspect
        // and a heavier slug than the spinal, trading a little reach for sustained throughput from two turrets.
        new() { Name = "Heavy Railgun Turret", Category = PartCategory.Weapon, DryMass = 20000, Length = 11.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 12.0, ProjectileLength = 0.60,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 3500.0, RateOfFire = 0.3,
                                          Range = 1200.0e3, HullDamage = 38.0 } },

        // Universal point-defense: a 40 mm point-defense cannon (PDC) with lethal auto-tracking - a fast
        // CIWS that swats incoming torpedoes (and bites at knife range). Carried by every class.
        new() { Name = "40mm PDC", Category = PartCategory.Weapon, DryMass = 2000, Length = 3.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 0.9, ProjectileLength = 0.16,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 1300.0, RateOfFire = 16.0,
                                          PointDefense = true, Range = 6.0e3, HullDamage = 3.0,
                                          MagazineSize = 150, ReloadTime = 6.0 } },

        // Advanced point-defense (larger ships): a pulse-laser cluster for very close anti-missile and naval
        // defense - fast-cycling beams that burn down torpedoes inside a few tens of km. Not a ship-killer.
        new() { Name = "Pulse Laser Cluster", Category = PartCategory.Weapon, DryMass = 10000, Length = 6.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Laser, BeamPower = 5_000_000.0, Aperture = 4.0,
                                          Wavelength = 1.06e-6, BeamQuality = 1.5, RateOfFire = 4.0,
                                          PointDefense = true, Range = 30.0e3 } },

        // Long-range area defense: a KKV interceptor launcher. Looses kinetic-kill vehicles at inbound enemy
        // torpedoes (and ballistic threats) hundreds of km out - the outer layer ahead of the PD envelope.
        new() { Name = "KKV Interceptor", Category = PartCategory.Weapon, DryMass = 4000, Length = 5.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Missile, Interceptor = true, RateOfFire = 1.0,
                                          MissileDryMass = 80.0, MissilePropellant = 120.0,
                                          MissileIsp = 4000.0, MissileThrust = 60000.0, Range = 700.0e3,
                                          MagazineSize = 6, ReloadTime = 20.0 } },

        // The only anti-ship directed-energy weapon: a Solar Lance. A heavy spinal/drone-mounted beam that
        // slowly melts through an enemy's armour belt at medium range (then bites hull), rather than the quick
        // anti-missile zap of the pulse cluster. Not point-defense - it engages ships, continuously, while in reach.
        new() { Name = "Solar Lance", Category = PartCategory.Weapon, DryMass = 14000, Length = 14.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Laser, BeamPower = 60_000_000.0, Aperture = 6.0,
                                          Wavelength = 1.0e-6, BeamQuality = 1.2, Range = 180.0e3 } },

        // --- legacy / custom-builder kinetics (not in the standard class fits, but available in the
        //     shipyard): a medium railgun, a short-range mass driver, and a light rapid coilgun. ---
        new() { Name = "Railgun", Category = PartCategory.Weapon, DryMass = 8000, Length = 9.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 1.0, ProjectileLength = 0.30,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 3000.0, RateOfFire = 0.5,
                                          Range = 1000.0e3, HullDamage = 12.0 } },
        new() { Name = "Mass Driver", Category = PartCategory.Weapon, DryMass = 6000, Length = 8.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 5.0, ProjectileLength = 0.50,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 2000.0, RateOfFire = 0.3,
                                          Range = 100.0e3, HullDamage = 18.0 } },
        new() { Name = "Light Coilgun", Category = PartCategory.Weapon, DryMass = 3000, Length = 5.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 0.4, ProjectileLength = 0.20,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 2800.0, RateOfFire = 4.0,
                                          Range = 300.0e3, HullDamage = 5.0 } },

        // --- armor (described by areal density over a material) ---
        new() { Name = "Whipple Shield", Category = PartCategory.Armor, DryMass = 1500, Length = 1.0,
                Armor = new ArmorSpec { ArealDensity = 100.0, Material = "Aluminium" } },
        new() { Name = "Steel Belt", Category = PartCategory.Armor, DryMass = 8000, Length = 1.0,
                Armor = new ArmorSpec { ArealDensity = 800.0, Material = "Steel(RHA)" } },
        new() { Name = "Tungsten Plate", Category = PartCategory.Armor, DryMass = 20000, Length = 1.0,
                Armor = new ArmorSpec { ArealDensity = 2000.0, Material = "Tungsten" } },
    };
}
