using System.Collections.Generic;
using Apastron.Core;

namespace Apastron.Physics.Integrators;

/// <summary>
/// Velocity Verlet - a symplectic, time-reversible integrator. Energy stays bounded
/// over long runs (orbits remain closed), making it the default for free orbital
/// flight. Second-order accurate.
/// </summary>
public sealed class VelocityVerletIntegrator : IIntegrator
{
    public string Name => "Velocity Verlet";

    public void Step(RigidBody body, IReadOnlyList<CelestialBody> bodies, double dt)
    {
        Vec3 aThrust = body.ThrustAcceleration;

        Vec3 a0     = Gravity.Acceleration(body.Position, bodies) + aThrust;
        Vec3 newPos = body.Position + body.Velocity * dt + 0.5 * a0 * (dt * dt);
        Vec3 a1     = Gravity.Acceleration(newPos, bodies) + aThrust;

        body.Position = newPos;
        body.Velocity += 0.5 * (a0 + a1) * dt;
    }
}
