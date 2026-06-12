using System;
using Apastron.Core;

namespace Apastron.Simulation;

/// <summary>A two-burn Hohmann transfer between coplanar circular orbits.</summary>
public readonly struct HohmannTransfer
{
    public double DeltaV1 { get; init; }       // m/s, burn at r1 (prograde)
    public double DeltaV2 { get; init; }       // m/s, burn at r2 (prograde)
    public double TotalDeltaV { get; init; }   // m/s
    public double TransferTime { get; init; }  // s (half the transfer ellipse period)
    public double PhaseAngleDeg { get; init; } // required lead angle of the target at departure

    public static HohmannTransfer Compute(double mu, double r1, double r2)
    {
        if (mu <= 0.0 || r1 <= 0.0 || r2 <= 0.0) return default;

        double v1 = Math.Sqrt(mu / r1);
        double v2 = Math.Sqrt(mu / r2);
        double at = (r1 + r2) / 2.0;

        double vPeri = Math.Sqrt(mu * (2.0 / r1 - 1.0 / at));   // transfer speed at r1
        double vApo  = Math.Sqrt(mu * (2.0 / r2 - 1.0 / at));   // transfer speed at r2

        double dv1 = vPeri - v1;
        double dv2 = v2 - vApo;
        double tt = Math.PI * Math.Sqrt(at * at * at / mu);
        double phase = Math.PI * (1.0 - Math.Pow((r1 + r2) / (2.0 * r2), 1.5));

        return new HohmannTransfer
        {
            DeltaV1 = dv1,
            DeltaV2 = dv2,
            TotalDeltaV = Math.Abs(dv1) + Math.Abs(dv2),
            TransferTime = tt,
            PhaseAngleDeg = phase * MathConstants.RadToDeg,
        };
    }
}
