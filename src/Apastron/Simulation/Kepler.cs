using System;
using Apastron.Core;

namespace Apastron.Simulation;

/// <summary>
/// Analytic two-body propagation. <see cref="TryPropagate"/> advances a state vector by a
/// time interval using Lagrange f/g coefficients with an eccentric-anomaly solve. It is used
/// to anchor a maneuver node ahead in time and to evaluate the orbit at that point — exact
/// (to machine precision) and immune to the step-size drift of numerical integration.
/// Elliptical orbits only; hyperbolic/parabolic states return false (caller falls back).
/// </summary>
public static class Kepler
{
    public static bool TryPropagate(Vec3 r0, Vec3 v0, double mu, double dt, out Vec3 r, out Vec3 v)
    {
        r = r0; v = v0;

        double r0m = r0.Length;
        if (r0m < 1.0 || mu <= 0.0) return false;

        double v0m = v0.Length;
        double energy = 0.5 * v0m * v0m - mu / r0m;
        if (energy >= -1e-12) return false;            // not bound -> not handled here

        double a = -mu / (2.0 * energy);
        double sqrtMuA = Math.Sqrt(mu * a);
        double period = MathConstants.TwoPi * Math.Sqrt(a * a * a / mu);

        // Keplerian motion is periodic: reduce dt to keep the eccentric-anomaly solve well-posed.
        double dtr = dt % period;
        if (dtr < 0.0) dtr += period;

        double ecosE0 = 1.0 - r0m / a;
        double esinE0 = Vec3.Dot(r0, v0) / sqrtMuA;
        double e0 = Math.Atan2(esinE0, ecosE0);
        double e = Math.Sqrt(ecosE0 * ecosE0 + esinE0 * esinE0);
        double m0 = e0 - esinE0;
        double n = Math.Sqrt(mu / (a * a * a));
        double m = m0 + n * dtr;                        // not wrapped: keeps the revolution

        // Newton solve of M = E - e sin E
        double bigE = m;
        for (int i = 0; i < 80; i++)
        {
            double d = (bigE - e * Math.Sin(bigE) - m) / (1.0 - e * Math.Cos(bigE));
            bigE -= d;
            if (Math.Abs(d) < 1e-13) break;
        }

        double dE = bigE - e0;
        double f = 1.0 - (a / r0m) * (1.0 - Math.Cos(dE));
        double g = dtr - Math.Sqrt(a * a * a / mu) * (dE - Math.Sin(dE));
        r = r0 * f + v0 * g;

        double rm = r.Length;
        double fdot = -(sqrtMuA / (rm * r0m)) * Math.Sin(dE);
        double gdot = 1.0 - (a / rm) * (1.0 - Math.Cos(dE));
        v = r0 * fdot + v0 * gdot;
        return true;
    }
}
