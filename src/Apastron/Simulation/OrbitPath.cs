using System;
using System.Collections.Generic;
using Apastron.Core;

namespace Apastron.Simulation;

/// <summary>
/// Samples the analytic conic (the *predicted* orbit) from a state vector, in world
/// coordinates. Bound orbits return a closed ellipse; unbound orbits return the visible
/// arc of the hyperbola. This is recomputed each frame so burns reshape it live.
/// </summary>
public static class OrbitPath
{
    public static List<Vec3> Compute(Vec3 position, Vec3 velocity, Vec3 bodyPosition, double mu, int segments = 256)
    {
        var pts = new List<Vec3>();

        Vec3 r = position - bodyPosition;
        Vec3 v = velocity;
        double rMag = r.Length;
        if (rMag < 1.0) return pts;

        double vMag = v.Length;
        double energy = 0.5 * vMag * vMag - mu / rMag;

        Vec3 h = Vec3.Cross(r, v);
        double hMag = h.Length;
        if (hMag < 1e-6) return pts;            // radial / degenerate trajectory

        Vec3 eVec = ((vMag * vMag - mu / rMag) * r - Vec3.Dot(r, v) * v) / mu;
        double e = eVec.Length;
        double a = (Math.Abs(energy) > 1e-12) ? -mu / (2.0 * energy) : double.PositiveInfinity;

        Vec3 wHat = h / hMag;                   // orbital-plane normal

        // In-plane basis: pHat toward periapsis. Circular orbits have no periapsis, so
        // pick any axis perpendicular to the plane normal.
        Vec3 pHat;
        if (e > 1e-6)
            pHat = eVec / e;
        else
        {
            Vec3 t = Math.Abs(wHat.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
            pHat = Vec3.Cross(t, wHat).Normalized();
        }
        Vec3 qHat = Vec3.Cross(wHat, pHat);     // 90 deg ahead, in-plane

        double p = a * (1.0 - e * e);           // semi-latus rectum (>0 for ellipse and hyperbola)
        if (double.IsInfinity(a) || p <= 0.0) return pts;

        if (e < 1.0)
        {
            for (int k = 0; k <= segments; k++)
            {
                double nu = MathConstants.TwoPi * k / segments;
                double rr = p / (1.0 + e * Math.Cos(nu));
                pts.Add(bodyPosition + rr * (Math.Cos(nu) * pHat + Math.Sin(nu) * qHat));
            }
        }
        else
        {
            double nuMax = (e > 1.0) ? Math.Acos(-1.0 / e) - 0.01 : Math.PI / 2.0 - 0.01;
            for (int k = 0; k <= segments; k++)
            {
                double nu = -nuMax + 2.0 * nuMax * k / segments;
                double rr = p / (1.0 + e * Math.Cos(nu));
                if (rr > 0.0)
                    pts.Add(bodyPosition + rr * (Math.Cos(nu) * pHat + Math.Sin(nu) * qHat));
            }
        }

        return pts;
    }
}
