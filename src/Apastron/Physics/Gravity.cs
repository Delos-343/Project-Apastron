using System.Collections.Generic;
using Apastron.Core;

namespace Apastron.Physics;

/// <summary>Newtonian gravitational field sampling.</summary>
public static class Gravity
{
    /// <summary>
    /// Gravitational acceleration (m/s^2) at <paramref name="position"/> produced by all
    /// <paramref name="bodies"/>:  a = sum_i mu_i * (r_i - p) / |r_i - p|^3.
    /// </summary>
    public static Vec3 Acceleration(Vec3 position, IReadOnlyList<CelestialBody> bodies)
    {
        Vec3 a = Vec3.Zero;
        for (int i = 0; i < bodies.Count; i++)
        {
            CelestialBody b = bodies[i];
            Vec3 d = b.Position - position;
            double r2 = d.LengthSquared;
            if (r2 < 1.0) continue;                 // avoid the singularity at the centre
            double invR  = 1.0 / System.Math.Sqrt(r2);
            double invR3 = invR / r2;
            a += d * (b.Mu * invR3);
        }
        return a;
    }
}
