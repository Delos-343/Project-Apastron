using System;
using Apastron.Vehicles;

namespace Apastron.Combat;

public struct KineticResult
{
    public double ImpactVelocity;   // m/s
    public double KineticEnergy;    // J
    public double Penetration;      // m into the armor material
    public double ArmorThickness;   // m (from areal density)
    public double Residual;         // m of penetration beyond the armor
    public bool Perforates;
}

public struct LaserResult
{
    public double SpotDiameter;     // m at the target
    public double Intensity;        // W/m^2 at the target
    public double DwellTime;        // s to burn through the armor
}

public struct MissileResult
{
    public double DeltaV;           // m/s
    public double AccelInitial;     // m/s^2 at ignition
    public double AccelBurnout;     // m/s^2 at burnout
    public double BurnTime;         // s
    public double InterceptTime;    // s (PositiveInfinity if it never closes)
    public bool Intercepts;
    public bool OutAccelerates;     // out-accelerates the target's evasion
}

/// <summary>
/// Hard-science terminal ballistics: long-rod kinetic penetration (Tate), diffraction-limited
/// laser ablation, and missile intercept kinematics. Engineering approximations — good to tens
/// of percent — not a substitute for hydrocode, but they capture the right physics and scaling.
/// </summary>
public static class Ballistics
{
    public const double G0 = 9.80665;

    /// <summary>
    /// Tate / Alekseevskii eroding long-rod penetration depth (m), integrated numerically.
    /// Reduces to the hydrodynamic limit L*sqrt(rho_p/rho_t) at high velocity and falls to zero
    /// at the strength-determined critical velocity.
    /// </summary>
    public static double TatePenetration(double length, Material pen, Material tgt, double v0)
    {
        double l = length, v = v0, p = 0.0, t = 0.0;
        const double dt = 2.0e-7, tMax = 0.5;
        double rp = pen.Density, yp = pen.Strength, rt = tgt.Density, rTgt = tgt.Resistance;

        while (t < tMax && l > 1.0e-5 && v > 1.0)
        {
            double a = 0.5 * (rp - rt);
            double b = -rp * v;
            double c = 0.5 * rp * v * v + yp - rTgt;
            if (c <= 0.0) break;   // rod can no longer overcome target resistance

            double u;
            if (Math.Abs(a) < 1.0e-6)
            {
                u = c / -b;
            }
            else
            {
                double disc = b * b - 4.0 * a * c;
                if (disc < 0.0) break;
                double s = Math.Sqrt(disc);
                double r1 = (-b + s) / (2.0 * a);
                double r2 = (-b - s) / (2.0 * a);
                u = double.NaN;
                if (r1 > 0.0 && r1 < v) u = r1;
                if (r2 > 0.0 && r2 < v) u = double.IsNaN(u) ? r2 : Math.Min(u, r2);
                if (double.IsNaN(u)) break;
            }

            v += -(yp / (rp * l)) * dt;   // rigid rod decelerates under its own strength
            l += -(v - u) * dt;           // erosion at the interface
            p += u * dt;                  // penetration advances at the interface velocity
            t += dt;
        }
        return p;
    }

    public static KineticResult Kinetic(WeaponSpec w, double closingSpeed, ArmorSpec armor)
    {
        Material pen = Materials.Get(w.ProjectileMaterial);
        Material tgt = Materials.Get(armor.Material);

        double vi = w.MuzzleVelocity + closingSpeed;
        if (vi < 0.0) vi = 0.0;

        double pen_d = TatePenetration(w.ProjectileLength, pen, tgt, vi);
        double thick = tgt.Density > 0.0 ? armor.ArealDensity / tgt.Density : 0.0;

        return new KineticResult
        {
            ImpactVelocity = vi,
            KineticEnergy = 0.5 * w.ProjectileMass * vi * vi,
            Penetration = pen_d,
            ArmorThickness = thick,
            Perforates = pen_d > thick,
            Residual = Math.Max(pen_d - thick, 0.0),
        };
    }

    public static LaserResult Laser(WeaponSpec w, double range, double absorptivity, ArmorSpec armor)
    {
        Material tgt = Materials.Get(armor.Material);
        double d = w.Aperture > 0.0 ? 2.44 * w.Wavelength * range * w.BeamQuality / w.Aperture : 0.0;
        double area = Math.PI * (d * 0.5) * (d * 0.5);
        double intensity = area > 0.0 ? w.BeamPower / area : 0.0;
        double ePerArea = armor.ArealDensity * tgt.AblationEnergy;
        double dwell = (intensity > 0.0 && absorptivity > 0.0)
            ? ePerArea / (absorptivity * intensity) : double.PositiveInfinity;

        return new LaserResult { SpotDiameter = d, Intensity = intensity, DwellTime = dwell };
    }

    public static MissileResult Missile(WeaponSpec w, double range, double closingSpeed, double evasionAccel)
    {
        double ve = w.MissileIsp * G0;
        double m0 = w.MissileDryMass + w.MissilePropellant;
        double dv = (w.MissileDryMass > 0.0 && m0 > w.MissileDryMass) ? ve * Math.Log(m0 / w.MissileDryMass) : 0.0;
        double mdot = ve > 0.0 ? w.MissileThrust / ve : 0.0;
        double burn = mdot > 0.0 ? w.MissilePropellant / mdot : 0.0;
        double a0 = m0 > 0.0 ? w.MissileThrust / m0 : 0.0;
        double abo = w.MissileDryMass > 0.0 ? w.MissileThrust / w.MissileDryMass : 0.0;

        // burn phase (acceleration rises as fuel is spent), then coast at burnout velocity
        double dt = 0.01, t = 0.0, r = range, vrel = closingSpeed;
        bool closed = false;
        while (t < burn)
        {
            double m = m0 - mdot * t;
            if (m < w.MissileDryMass) m = w.MissileDryMass;
            double a = m > 0.0 ? w.MissileThrust / m : 0.0;
            vrel += a * dt;
            r -= vrel * dt;
            t += dt;
            if (r <= 0.0) { closed = true; break; }
        }
        double tInt;
        if (closed) tInt = t;
        else if (vrel > 0.0) tInt = t + r / vrel;
        else tInt = double.PositiveInfinity;

        return new MissileResult
        {
            DeltaV = dv,
            AccelInitial = a0,
            AccelBurnout = abo,
            BurnTime = burn,
            InterceptTime = tInt,
            Intercepts = double.IsFinite(tInt) && abo > evasionAccel,
            OutAccelerates = abo > evasionAccel,
        };
    }
}
