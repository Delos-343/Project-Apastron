using System;
using Apastron.Core;
using Apastron.Physics;

namespace Apastron.Vehicles;

/// <summary>
/// Kinematic attitude for the display hull. A torchship points its nose along its thrust
/// while burning (so the engine fires backward) and prograde while coasting; reversing thrust
/// for a deceleration burn therefore reads as a 180-degree flip. The nose slews toward the
/// commanded direction at a fixed angular rate, which animates that flip rather than snapping.
/// </summary>
public static class Attitude
{
    private const double SlewRateDegPerSec = 18.0;

    public static void Update(RigidBody? vessel, double dt)
    {
        if (vessel == null || dt <= 0.0) return;

        Vec3 commanded =
            vessel.ThrustWorld.Length > 1e-6 ? vessel.ThrustWorld.Normalized() :
            vessel.Velocity.Length > 1e-6 ? vessel.Velocity.Normalized() :
            vessel.Forward;

        vessel.Forward = Slew(vessel.Forward, commanded, SlewRateDegPerSec * MathConstants.DegToRad * dt);
    }

    /// <summary>Rotate <paramref name="from"/> toward <paramref name="to"/> by at most maxRad.</summary>
    public static Vec3 Slew(Vec3 from, Vec3 to, double maxRad)
    {
        Vec3 f = from.Length > 1e-9 ? from.Normalized() : Vec3.UnitX;
        Vec3 t = to.Length > 1e-9 ? to.Normalized() : f;

        double d = Vec3.Dot(f, t);
        if (d > 1.0) d = 1.0; else if (d < -1.0) d = -1.0;
        double angle = Math.Acos(d);
        if (angle <= maxRad || angle < 1e-9) return t;

        Vec3 axis;
        if (d < -0.9999)   // ~antiparallel (a flip): no unique axis, pick any perpendicular
        {
            axis = Vec3.Cross(f, Vec3.UnitZ);
            if (axis.Length < 1e-6) axis = Vec3.Cross(f, Vec3.UnitX);
        }
        else
        {
            axis = Vec3.Cross(f, t);
        }
        axis = axis.Normalized();

        // Rodrigues rotation of f about axis by maxRad
        double c = Math.Cos(maxRad), s = Math.Sin(maxRad);
        Vec3 rotated = f * c + Vec3.Cross(axis, f) * s + axis * (Vec3.Dot(axis, f) * (1.0 - c));
        return rotated.Normalized();
    }
}
