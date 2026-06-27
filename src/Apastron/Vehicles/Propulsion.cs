using Apastron.Core;
using Apastron.Diagnostics;
using Apastron.Physics;

namespace Apastron.Vehicles;

/// <summary>
/// Bridges the parts-based <see cref="Spacecraft"/> to the point-mass <see cref="RigidBody"/>
/// the integrator sees. Each step it syncs the vessel mass, points thrust along the selected
/// mode, and — crucially — burns propellant at m-dot = F / (Isp * g0), so the vessel loses
/// mass and its delta-v depletes exactly as the rocket equation predicts.
/// </summary>
public static class Propulsion
{
    public static void Apply(Spacecraft ship, PhysicsWorld world, double simSeconds)
    {
        RigidBody? body = world.PrimaryVessel;
        if (body == null) return;

        if (ship.TotalMass > 0.0) body.Mass = ship.TotalMass;
        body.MaxThrust = ship.TotalThrustVac;   // keep fleet/autopilot steering's accel cap current

        bool firing = ship.Mode != ThrustMode.None
                      && ship.Throttle > 0.0
                      && ship.PropellantMass > 0.0
                      && ship.TotalThrustVac > 0.0;

        if (!firing) { body.ThrustWorld = Vec3.Zero; return; }

        Vec3 dir = Direction(ship, world, body);
        if (dir.Length < 1e-9) { body.ThrustWorld = Vec3.Zero; return; }

        double throttle = ship.Throttle > 1.0 ? 1.0 : ship.Throttle;
        double thrust = throttle * ship.TotalThrustVac;

        // Defensive: if the state vector has gone non-finite (e.g. a degenerate step), do not
        // propagate NaN/Inf into the world thrust - that would poison the integrator and render.
        if (!double.IsFinite(thrust) ||
            !double.IsFinite(dir.X) || !double.IsFinite(dir.Y) || !double.IsFinite(dir.Z))
        {
            body.ThrustWorld = Vec3.Zero;
            return;
        }

        double drain = ship.MassFlowFullThrust * throttle * simSeconds;
        if (drain > 0.0)
        {
            ship.Consume(drain);
            if (ship.TotalMass > 0.0) body.Mass = ship.TotalMass;
        }

        body.ThrustWorld = dir * thrust;
        CrashLog.Phase("first thrust applied to vessel (sim)");
        BurnTrace.Arm();
    }

    private static Vec3 Direction(Spacecraft ship, PhysicsWorld world, RigidBody body)
    {
        switch (ship.Mode)
        {
            case ThrustMode.Prograde:   return body.Velocity.Normalized();
            case ThrustMode.Retrograde: return body.Velocity.Normalized() * -1.0;
            case ThrustMode.Inertial:   return ship.BurnDirectionWorld.Length > 1e-9
                                               ? ship.BurnDirectionWorld.Normalized() : Vec3.Zero;
            case ThrustMode.RadialOut:
            case ThrustMode.RadialIn:
            {
                CelestialBody? c = world.DominantBody(body.Position);
                if (c == null) return Vec3.Zero;
                Vec3 r = (body.Position - c.Position).Normalized();
                return ship.Mode == ThrustMode.RadialOut ? r : r * -1.0;
            }
            default: return Vec3.Zero;
        }
    }
}
