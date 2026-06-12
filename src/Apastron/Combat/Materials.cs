using System.Collections.Generic;

namespace Apastron.Combat;

/// <summary>
/// Ballistic material properties. <see cref="Resistance"/> (R_t) and <see cref="Strength"/>
/// (Y_p) drive the Tate long-rod penetration model; <see cref="AblationEnergy"/> is the
/// specific energy to heat and vaporize the material (used by the laser model).
/// </summary>
public readonly struct Material
{
    public readonly double Density;         // kg/m^3
    public readonly double Resistance;      // Pa, target resistance R_t
    public readonly double Strength;        // Pa, penetrator strength Y_p
    public readonly double AblationEnergy;  // J/kg

    public Material(double density, double resistance, double strength, double ablation)
    {
        Density = density;
        Resistance = resistance;
        Strength = strength;
        AblationEnergy = ablation;
    }
}

/// <summary>Small table of materials shared by penetrators, armor and laser targets.</summary>
public static class Materials
{
    private static readonly Dictionary<string, Material> Table = new()
    {
        ["Tungsten"]   = new Material(17600.0, 5.0e9,  2.0e9,  5.0e6),
        ["Steel(RHA)"] = new Material(7850.0,  4.0e9,  1.2e9,  7.5e6),
        ["Aluminium"]  = new Material(2700.0,  1.2e9,  0.4e9,  1.2e7),
        ["Ice"]        = new Material(920.0,   0.05e9, 0.02e9, 3.0e6),
        ["Carbon"]     = new Material(2200.0,  2.0e9,  1.0e9,  3.0e7),
    };

    public static readonly string[] Names = { "Tungsten", "Steel(RHA)", "Aluminium", "Ice", "Carbon" };

    public static Material Get(string name) =>
        Table.TryGetValue(name, out Material m) ? m : Table["Steel(RHA)"];
}
