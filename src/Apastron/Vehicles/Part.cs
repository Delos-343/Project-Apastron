namespace Apastron.Vehicles;

/// <summary>Functional class of a spacecraft part; drives mass, thrust, and thermal roles.</summary>
public enum PartCategory { Command, Habitat, FuelTank, Engine, Reactor, Radiator, Structure, Weapon, Armor }

/// <summary>
/// A single spacecraft component. A flat field set (rather than a subclass per category)
/// keeps the builder UI and any future serialization simple; irrelevant fields stay zero.
/// Parts are stacked along one axis (<see cref="Length"/>) for centre-of-mass and the
/// schematic.
/// </summary>
public sealed class Part
{
    public string Name { get; set; } = "Part";
    public PartCategory Category { get; set; }

    /// <summary>Structural / empty mass (kg).</summary>
    public double DryMass { get; set; }

    /// <summary>Axial extent (m) — used for stacking, centre-of-mass, and the schematic.</summary>
    public double Length { get; set; } = 1.0;

    // --- tank ---
    public double PropellantCapacity { get; set; }   // kg
    public double Propellant { get; set; }           // kg, current fill (<= capacity)

    // --- engine ---
    public double ThrustVac { get; set; }            // N, vacuum thrust
    public double IspVac { get; set; }               // s, vacuum specific impulse

    // --- thermal ---
    public double HeatOutput { get; set; }           // W produced (reactor / engine waste heat)
    public double HeatRejection { get; set; }        // W rejected (radiator capacity)

    // --- combat (null unless this is a weapon / armor part) ---
    public WeaponSpec? Weapon { get; set; }
    public ArmorSpec? Armor { get; set; }

    /// <summary>Module integrity 0..1. 1 = undamaged (default, so non-combat ships are unaffected);
    /// combat perforation degrades it, derating the part's thrust / heat-rejection / weapon output.</summary>
    public double Health { get; set; } = 1.0;

    public double WetMass => DryMass + Propellant;
    public bool IsEngine => Category == PartCategory.Engine && ThrustVac > 0.0;
    public bool IsTank   => PropellantCapacity > 0.0;

    public Part Clone() => new()
    {
        Name = Name,
        Category = Category,
        DryMass = DryMass,
        Length = Length,
        PropellantCapacity = PropellantCapacity,
        Propellant = Propellant,
        ThrustVac = ThrustVac,
        IspVac = IspVac,
        HeatOutput = HeatOutput,
        HeatRejection = HeatRejection,
        Weapon = Weapon?.Clone(),
        Armor = Armor?.Clone(),
        Health = Health,
    };
}
