using System;
using Apastron.Core;

namespace Apastron.Combat;

/// <summary>Fire-control solvers: kinetic lead/intercept and missile (ZEM/proportional-nav) steering.</summary>
public static class FireControl
{
    /// <summary>
    /// Lead solution for an unguided slug. Solves for the firing direction and time so a round
    /// leaving at <paramref name="muzzle"/> (added to the shooter's velocity) intercepts a target
    /// moving at constant velocity. Gravity is neglected over the (short) slug flight.
    /// </summary>
    public static bool LeadSolution(Vec3 ps, Vec3 vs, Vec3 pt, Vec3 vt, double muzzle, out Vec3 dir, out double tInt)
    {
        dir = Vec3.Zero; tInt = 0.0;
        Vec3 r = ps - pt, u = vs - vt;
        double a = Vec3.Dot(u, u) - muzzle * muzzle;
        double b = 2.0 * Vec3.Dot(r, u);
        double c = Vec3.Dot(r, r);

        double t;
        if (Math.Abs(a) < 1.0e-6)
        {
            if (Math.Abs(b) < 1.0e-9) return false;
            t = -c / b;
            if (t <= 1.0e-6) return false;
        }
        else
        {
            double disc = b * b - 4.0 * a * c;
            if (disc < 0.0) return false;
            double s = Math.Sqrt(disc);
            double t1 = (-b + s) / (2.0 * a), t2 = (-b - s) / (2.0 * a);
            t = double.MaxValue;
            if (t1 > 1.0e-6) t = t1;
            if (t2 > 1.0e-6 && t2 < t) t = t2;
            if (t == double.MaxValue) return false;
        }

        Vec3 aim = (pt + vt * t) - (ps + vs * t);
        if (aim.Length < 1.0e-6) return false;
        dir = aim.Normalized();
        tInt = t;
        return true;
    }

    /// <summary>
    /// Thrust direction for a guided missile using zero-effort-miss / proportional navigation.
    /// The single engine's full thrust is split: exactly enough lateral acceleration to null the
    /// predicted perpendicular miss, with the remainder along the line of sight to close.
    /// </summary>
    public static Vec3 GuidanceDir(Vec3 pm, Vec3 vm, Vec3 pt, Vec3 vt, double aMax, double n)
    {
        Vec3 r = pt - pm;
        double rng = r.Length;
        if (rng < 1.0e-6)
        {
            Vec3 rel = vt - vm;
            return rel.Length > 1.0e-6 ? rel.Normalized() : Vec3.UnitX;
        }
        Vec3 rhat = r / rng;
        Vec3 vrel = vt - vm;
        double vc = -Vec3.Dot(vrel, rhat);
        double tgo = rng / Math.Max(vc, 1.0);

        Vec3 zem = r + vrel * tgo;
        Vec3 zemPerp = zem - rhat * Vec3.Dot(zem, rhat);
        Vec3 aSteer = zemPerp * (n / (tgo * tgo));
        double amag = aSteer.Length;

        if (aMax <= 1.0e-9) return rhat;
        if (amag >= aMax) return aSteer.Normalized();

        double lat = amag / aMax;
        double fwd = Math.Sqrt(Math.Max(1.0 - lat * lat, 0.0));
        Vec3 dir = (amag > 1.0e-9 ? aSteer.Normalized() * lat : Vec3.Zero) + rhat * fwd;
        return dir.Length > 1.0e-9 ? dir.Normalized() : rhat;
    }
}
