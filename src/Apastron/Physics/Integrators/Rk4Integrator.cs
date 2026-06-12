using System.Collections.Generic;
using Apastron.Core;

namespace Apastron.Physics.Integrators;

/// <summary>
/// Classic 4th-order Runge-Kutta. Highest short-term accuracy of the bundled
/// integrators, but NOT symplectic: total energy drifts slowly over many orbits, so
/// closed orbits gradually decay/expand. Best for maneuvers and short, precise
/// propagation.
/// </summary>
public sealed class Rk4Integrator : IIntegrator
{
    public string Name => "Runge-Kutta 4";

    public void Step(RigidBody body, IReadOnlyList<CelestialBody> bodies, double dt)
    {
        Vec3 aThrust = body.ThrustAcceleration;
        Vec3 x0 = body.Position;
        Vec3 v0 = body.Velocity;

        Vec3 Accel(Vec3 x) => Gravity.Acceleration(x, bodies) + aThrust;

        Vec3 k1x = v0;
        Vec3 k1v = Accel(x0);

        Vec3 k2x = v0 + 0.5 * dt * k1v;
        Vec3 k2v = Accel(x0 + 0.5 * dt * k1x);

        Vec3 k3x = v0 + 0.5 * dt * k2v;
        Vec3 k3v = Accel(x0 + 0.5 * dt * k2x);

        Vec3 k4x = v0 + dt * k3v;
        Vec3 k4v = Accel(x0 + dt * k3x);

        body.Position = x0 + (dt / 6.0) * (k1x + 2.0 * k2x + 2.0 * k3x + k4x);
        body.Velocity = v0 + (dt / 6.0) * (k1v + 2.0 * k2v + 2.0 * k3v + k4v);
    }
}
