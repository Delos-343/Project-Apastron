using System;
using Apastron.Vehicles;

namespace Apastron.Simulation;

/// <summary>Result of planning a constant-acceleration (flip-and-burn) transfer.</summary>
public readonly struct BrachistochroneResult
{
    public bool Valid { get; init; }
    public double Accel { get; init; }         // m/s^2
    public double TransitTime { get; init; }   // s (accelerate to midpoint, flip, decelerate)
    public double PeakVelocity { get; init; }  // m/s at the midpoint flip
    public double DeltaV { get; init; }        // m/s (= Accel * TransitTime)
    public double ReactionMass { get; init; }  // kg of propellant expended
    public double MassRatio { get; init; }     // m0 / m_final
    public bool PropellantOK { get; init; }    // reaction mass <= what the ship carries
    public bool ThermalOK { get; init; }       // accel <= radiator-sustainable acceleration
}

/// <summary>
/// Brachistochrone (continuous-thrust "flip and burn") transfer math. The ship accelerates at
/// constant thrust to the midpoint, flips, and decelerates to arrive at rest. Transit time
/// scales as 2*sqrt(d/a), but the delta-v is a*t — punishingly large at high acceleration, so
/// only very high exhaust velocities keep the mass ratio sane. Heat caps the usable acceleration.
/// </summary>
public static class Brachistochrone
{
    /// <summary>Bare kinematic profile for a distance at an acceleration: (time, peak velocity, delta-v).</summary>
    public static (double Time, double PeakVelocity, double DeltaV) Profile(double distance, double accel)
    {
        if (distance <= 0.0 || accel <= 0.0) return (0.0, 0.0, 0.0);
        double tHalf = Math.Sqrt(distance / accel);
        double t = 2.0 * tHalf;
        return (t, accel * tHalf, accel * t);
    }

    /// <summary>The acceleration the ship's radiators can sustain at its current mass (heat-limited).</summary>
    public static double SustainableAccel(Spacecraft ship) => ship.AccelerationAt(ship.SustainableThrottle);

    /// <summary>Plan a flip-and-burn for a ship over a distance at a chosen acceleration.</summary>
    public static BrachistochroneResult Plan(Spacecraft ship, double distance, double accel)
    {
        (double t, double vMid, double dv) = Profile(distance, accel);
        double ve = ship.ExhaustVelocity;
        double m0 = ship.TotalMass;

        bool ok = distance > 0.0 && accel > 0.0 && ve > 0.0 && m0 > 0.0;
        double massRatio = ok ? Math.Exp(dv / ve) : double.PositiveInfinity;
        double reactionMass = ok ? m0 * (1.0 - Math.Exp(-dv / ve)) : double.PositiveInfinity;

        return new BrachistochroneResult
        {
            Valid = ok,
            Accel = accel,
            TransitTime = t,
            PeakVelocity = vMid,
            DeltaV = dv,
            ReactionMass = reactionMass,
            MassRatio = massRatio,
            PropellantOK = ok && reactionMass <= ship.PropellantMass,
            ThermalOK = accel <= SustainableAccel(ship) + 1e-9,
        };
    }
}
