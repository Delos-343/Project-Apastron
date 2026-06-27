using System;
using Apastron.Core;
using Apastron.Physics;

namespace Apastron.Simulation;

/// <summary>Relative state of the active vessel with respect to a target, in the target's RTN frame.</summary>
public readonly struct RelativeState
{
    public bool Valid { get; init; }
    public double Range { get; init; }       // m
    public double RangeRate { get; init; }   // m/s (negative = closing)
    public double RelSpeed { get; init; }    // m/s, |v_rel|
    public double Radial { get; init; }      // m, along target radial (out = +)
    public double AlongTrack { get; init; }  // m, along target velocity (+)
    public double CrossTrack { get; init; }  // m, along target orbit normal (+)
    public double PhaseDeg { get; init; }    // signed phase angle, target relative to active (+ = ahead)
}

/// <summary>Predicted closest approach between two coasting vessels.</summary>
public readonly struct ClosestApproach
{
    public bool Found { get; init; }
    public double TimeToCA { get; init; }      // s
    public double MinRange { get; init; }      // m
    public double RelSpeedAtCA { get; init; }  // m/s (the delta-v to match velocity there)
}

/// <summary>
/// Rendezvous mechanics: relative position/velocity in the target's RTN frame, the signed
/// phase angle, the closest approach over a horizon (via analytic Kepler propagation of both
/// vessels), and the time until the phasing is right to depart on a Hohmann transfer.
/// </summary>
public static class Rendezvous
{
    public static RelativeState Relative(RigidBody active, RigidBody target, CelestialBody body)
    {
        Vec3 ra = active.Position - body.Position;
        Vec3 rt = target.Position - body.Position;
        Vec3 va = active.Velocity - body.Velocity;
        Vec3 vt = target.Velocity - body.Velocity;

        Vec3 rUnit = rt.Normalized();
        Vec3 h = Vec3.Cross(rt, vt);
        Vec3 nUnit = h.Length > 1e-9 ? h.Normalized() : Vec3.UnitZ;
        Vec3 tUnit = Vec3.Cross(nUnit, rUnit);

        Vec3 rel = active.Position - target.Position;
        Vec3 vrel = va - vt;
        double range = rel.Length;
        double rate = range > 1e-9 ? Vec3.Dot(rel, vrel) / range : 0.0;

        Vec3 raUnit = ra.Normalized();
        Vec3 rtUnit = rt.Normalized();
        Vec3 na = Vec3.Cross(ra, va);
        na = na.Length > 1e-9 ? na.Normalized() : Vec3.UnitZ;
        double cosv = Vec3.Dot(raUnit, rtUnit);
        Vec3 crp = Vec3.Cross(raUnit, rtUnit);
        double phase = Math.Atan2(Vec3.Dot(crp, na), cosv) * MathConstants.RadToDeg;

        return new RelativeState
        {
            Valid = true,
            Range = range,
            RangeRate = rate,
            RelSpeed = vrel.Length,
            Radial = Vec3.Dot(rel, rUnit),
            AlongTrack = Vec3.Dot(rel, tUnit),
            CrossTrack = Vec3.Dot(rel, nUnit),
            PhaseDeg = phase,
        };
    }

    public static ClosestApproach FindClosestApproach(RigidBody active, RigidBody target,
                                                      CelestialBody body, double horizon, int coarse = 720)
    {
        if (horizon <= 0.0) return default;

        Vec3 ra0 = active.Position - body.Position, va0 = active.Velocity - body.Velocity;
        Vec3 rt0 = target.Position - body.Position, vt0 = target.Velocity - body.Velocity;

        int n = coarse < 2 ? 2 : coarse;
        double bestD = double.MaxValue, bestT = 0.0;

        for (int i = 0; i <= n; i++)
        {
            double t = horizon * i / n;
            if (!Kepler.TryPropagate(ra0, va0, body.Mu, t, out Vec3 pa, out _)) return default;
            if (!Kepler.TryPropagate(rt0, vt0, body.Mu, t, out Vec3 pt, out _)) return default;
            double d = (pa - pt).Length;
            if (d < bestD) { bestD = d; bestT = t; }
        }

        // refine around the coarse minimum
        double span = horizon / n;
        double t0 = Math.Max(bestT - span, 0.0), t1 = bestT + span;
        const int m = 200;
        for (int i = 0; i <= m; i++)
        {
            double t = t0 + (t1 - t0) * i / m;
            Kepler.TryPropagate(ra0, va0, body.Mu, t, out Vec3 pa, out _);
            Kepler.TryPropagate(rt0, vt0, body.Mu, t, out Vec3 pt, out _);
            double d = (pa - pt).Length;
            if (d < bestD) { bestD = d; bestT = t; }
        }

        Kepler.TryPropagate(ra0, va0, body.Mu, bestT, out _, out Vec3 vaC);
        Kepler.TryPropagate(rt0, vt0, body.Mu, bestT, out _, out Vec3 vtC);

        return new ClosestApproach
        {
            Found = true,
            TimeToCA = bestT,
            MinRange = bestD,
            RelSpeedAtCA = (vaC - vtC).Length,
        };
    }

    public static double SynodicPeriod(double periodA, double periodB)
    {
        if (periodA <= 0.0 || periodB <= 0.0) return double.PositiveInfinity;
        double diff = Math.Abs(1.0 / periodA - 1.0 / periodB);
        return diff > 1e-15 ? 1.0 / diff : double.PositiveInfinity;
    }

    /// <summary>Time until the phase angle reaches <paramref name="requiredPhaseDeg"/> (Hohmann departure).</summary>
    public static double TimeToTransferWindow(double currentPhaseDeg, double requiredPhaseDeg,
                                              double periodActive, double periodTarget)
    {
        if (periodActive <= 0.0 || periodTarget <= 0.0) return double.PositiveInfinity;
        double w = (MathConstants.TwoPi / periodTarget - MathConstants.TwoPi / periodActive) * MathConstants.RadToDeg;
        if (Math.Abs(w) < 1e-12) return double.PositiveInfinity;

        double diff = (requiredPhaseDeg - currentPhaseDeg) % 360.0;
        if (diff < 0.0) diff += 360.0;       // [0, 360)
        if (w < 0.0) diff -= 360.0;          // keep diff / w >= 0
        return diff / w;
    }
}
