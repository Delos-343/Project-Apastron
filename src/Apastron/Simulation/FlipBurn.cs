using System;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.Simulation;

/// <summary>
/// State for an in-world continuous-burn "flip-and-burn" transfer to a fixed point. The ship
/// holds a constant commanded acceleration (throttle modulated as mass drops), accelerates
/// toward the destination, flips at the changeover and decelerates — the brachistochrone the
/// torch-transfer planner describes, flown for real against gravity.
/// </summary>
public sealed class FlipBurnPlan
{
    public Vec3 Destination;
    public bool Active;
    public bool Arrived;

    public double Accel;          // commanded acceleration (m/s^2), held constant
    public double TransitTime;    // planned 2*sqrt(D0/a) (s)
    public double Elapsed;        // burn time so far (s)
    public bool DecelStarted;

    public string Phase = "Accelerate";
    public double DistanceRemaining;
    public double ClosingSpeed;
}

/// <summary>Bang-bang time-optimal controller that flies a <see cref="FlipBurnPlan"/>.</summary>
public static class FlipBurn
{
    // Capture thresholds (interplanetary scale): arrive in the destination's vicinity.
    private const double ArriveDist  = 5.0e8;   // 500,000 km
    private const double ArriveSpeed = 2.0e4;   // 20 km/s

    /// <summary>Begin a flip-and-burn to a fixed point at a chosen commanded acceleration.</summary>
    public static FlipBurnPlan ToPoint(RigidBody vessel, Vec3 destination, double accel)
    {
        double d0 = (destination - vessel.Position).Length;
        double a = accel > 1e-6 ? accel : 1e-6;
        return new FlipBurnPlan
        {
            Destination = destination,
            Active = true,
            Accel = a,
            TransitTime = 2.0 * Math.Sqrt(d0 / a),
            Phase = "Accelerate",
        };
    }

    public static void Update(PhysicsWorld world, Spacecraft ship, FlipBurnPlan? plan, double simSeconds, bool godMode = false)
    {
        if (plan == null || !plan.Active) return;
        RigidBody? vessel = world.PrimaryVessel;
        if (vessel == null) return;

        Vec3 r = plan.Destination - vessel.Position;
        double dist = r.Length;
        Vec3 los = dist > 1e-6 ? r / dist : vessel.Forward;
        Vec3 v = vessel.Velocity;
        double speed = v.Length;
        double vlos = Vec3.Dot(v, los);   // closing speed (+ = approaching)

        plan.Elapsed += simSeconds;
        double a = plan.Accel;

        // arrival: passed closest approach during decel, tight capture, or transit-time safety cap
        bool arrived = (plan.DecelStarted && vlos < 0.0)
                       || (dist < ArriveDist && speed < ArriveSpeed)
                       || (plan.Elapsed > 2.0 * plan.TransitTime);
        if (arrived)
        {
            plan.Active = false;
            plan.Arrived = true;
            plan.Phase = "Arrived";
            plan.DistanceRemaining = dist;
            plan.ClosingSpeed = vlos;
            ship.Throttle = 0.0;
            ship.Mode = ThrustMode.None;
            return;
        }

        // bang-bang: accelerate while the stopping distance is shorter than what remains
        double stop = vlos > 0.0 ? (vlos * vlos) / (2.0 * a) : 0.0;
        Vec3 dir;
        if (dist > stop)
        {
            dir = los;
            plan.Phase = "Accelerate";
        }
        else
        {
            plan.DecelStarted = true;
            dir = speed > 1e-6 ? v * (-1.0 / speed) : los * -1.0;
            plan.Phase = "Decelerate";
        }

        // throttle to hold the commanded acceleration, capped by the radiator-limited maximum
        // (God mode lifts that heat cap, so the full commanded acceleration is flown)
        double throttle = 0.0;
        if (ship.TotalThrustVac > 0.0 && ship.TotalMass > 0.0)
            throttle = ship.TotalMass * a / ship.TotalThrustVac;
        if (throttle < 0.0) throttle = 0.0;
        if (!godMode && throttle > ship.SustainableThrottle) throttle = ship.SustainableThrottle;

        ship.Mode = ThrustMode.Inertial;
        ship.BurnDirectionWorld = dir;
        ship.Throttle = throttle;

        plan.DistanceRemaining = dist;
        plan.ClosingSpeed = vlos;
    }
}
