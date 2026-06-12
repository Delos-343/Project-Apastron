using Apastron.Core;

namespace Apastron.Physics;

/// <summary>
/// A point-mass vessel integrated under gravity plus an optional thrust force.
/// Attitude/rotational dynamics and variable mass (fuel burn) arrive with the
/// spacecraft-builder chunk; for now this is a faithful translational integrator.
/// </summary>
public sealed class RigidBody
{
    public string Name { get; set; } = "Vessel";

    /// <summary>Total mass (kg).</summary>
    public double Mass { get; set; } = 1000.0;

    /// <summary>World position (m).</summary>
    public Vec3 Position { get; set; }

    /// <summary>World velocity (m/s).</summary>
    public Vec3 Velocity { get; set; }

    /// <summary>Commanded thrust in world frame (N). Zero = coasting.</summary>
    public Vec3 ThrustWorld { get; set; } = Vec3.Zero;

    /// <summary>Acceleration from thrust alone (m/s^2).</summary>
    public Vec3 ThrustAcceleration => Mass > 0.0 ? ThrustWorld / Mass : Vec3.Zero;

    // --- kinematic display state (not part of the point-mass dynamics) ---

    /// <summary>Current nose direction (world). Slewed toward thrust/velocity for the flip animation.</summary>
    public Vec3 Forward { get; set; } = Vec3.UnitX;

    /// <summary>If &gt; 0, this vessel is a spin habitat: ring radius (m) and spin rate (rpm).</summary>
    public double SpinRadius { get; set; }
    public double SpinRpm { get; set; }

    /// <summary>True for dockable trading ports / spin-stations (Ship AI dock-and-replenish orders).</summary>
    public bool IsStation { get; set; }
}
