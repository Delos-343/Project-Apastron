using Apastron.Core;

namespace Apastron.Physics;

/// <summary>
/// A gravitating body (planet, moon, star). In this build celestial bodies are fixed
/// in space, which keeps the demo deterministic; n-body propagation of the bodies
/// themselves is on the roadmap.
/// </summary>
public sealed class CelestialBody
{
    public string Name { get; init; } = "Body";

    /// <summary>Standard gravitational parameter mu = G*M (m^3/s^2).</summary>
    public double Mu { get; init; }

    /// <summary>Mean radius (m) — used for altitude and collision checks.</summary>
    public double Radius { get; init; }

    /// <summary>World position (m).</summary>
    public Vec3 Position { get; set; } = Vec3.Zero;

    /// <summary>World velocity (m/s). Reserved for future n-body propagation.</summary>
    public Vec3 Velocity { get; set; } = Vec3.Zero;

    /// <summary>Derived mass (kg).</summary>
    public double Mass => Mu / MathConstants.G;

    /// <summary>Colour hint for rendering (RGB 0..1), consumed in a later chunk.</summary>
    public (float R, float G, float B) Color { get; init; } = (0.6f, 0.6f, 0.65f);
}
