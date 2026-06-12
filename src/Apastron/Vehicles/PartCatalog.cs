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
        new() { Name = "Reaction Mass Tank", Category = PartCategory.FuelTank,  DryMass = 3000,  Length = 8.0,  PropellantCapacity = 200000, Propellant = 200000 },
        new() { Name = "High-Temp Radiator", Category = PartCategory.Radiator,  DryMass = 4000,  Length = 6.0,  HeatRejection = 1_500_000_000 },
        new() { Name = "Droplet Radiator",   Category = PartCategory.Radiator,  DryMass = 7000,  Length = 5.0,  HeatRejection = 4_000_000_000 },

        // --- weapons (transient firing heat is out of the steady-state thermal model) ---
        // Ranges follow the Expanse weapon hierarchy: fusion torpedoes reach past 1000 km, plasma
        // railguns to ~1000 km, mass drivers to ~100 km, and point-defense (40 mm gun / laser CIWS) close in.
        new() { Name = "Railgun", Category = PartCategory.Weapon, DryMass = 8000, Length = 9.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 1.0, ProjectileLength = 0.30,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 3000.0, RateOfFire = 1.0,
                                          Range = 1000.0e3 } },
        new() { Name = "Mass Driver", Category = PartCategory.Weapon, DryMass = 6000, Length = 8.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 5.0, ProjectileLength = 0.50,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 2000.0, RateOfFire = 0.3,
                                          Range = 100.0e3 } },
        new() { Name = "Missile Rack", Category = PartCategory.Weapon, DryMass = 3000, Length = 5.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Missile, MissileDryMass = 200.0, MissilePropellant = 300.0,
                                          MissileIsp = 300.0, MissileThrust = 50000.0, Range = 2000.0e3 } },
        // Point-defense laser (CIWS): burns down incoming missiles at short range; not a ship-killer.
        new() { Name = "Laser Battery", Category = PartCategory.Weapon, DryMass = 10000, Length = 6.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Laser, BeamPower = 5_000_000.0, Aperture = 4.0,
                                          Wavelength = 1.06e-6, BeamQuality = 1.5, RateOfFire = 2.0,
                                          PointDefense = true, Range = 25.0e3 } },
        new() { Name = "Point-Defense Gun", Category = PartCategory.Weapon, DryMass = 2000, Length = 3.0,
                Weapon = new WeaponSpec { Kind = WeaponKind.Kinetic, ProjectileMass = 0.05, ProjectileLength = 0.08,
                                          ProjectileMaterial = "Tungsten", MuzzleVelocity = 3500.0, RateOfFire = 20.0,
                                          PointDefense = true, Range = 5.0e3 } },

        // --- armor (described by areal density over a material) ---
        new() { Name = "Whipple Shield", Category = PartCategory.Armor, DryMass = 1500, Length = 1.0,
                Armor = new ArmorSpec { ArealDensity = 100.0, Material = "Aluminium" } },
        new() { Name = "Steel Belt", Category = PartCategory.Armor, DryMass = 8000, Length = 1.0,
                Armor = new ArmorSpec { ArealDensity = 800.0, Material = "Steel(RHA)" } },
        new() { Name = "Tungsten Plate", Category = PartCategory.Armor, DryMass = 20000, Length = 1.0,
                Armor = new ArmorSpec { ArealDensity = 2000.0, Material = "Tungsten" } },
    };
}
