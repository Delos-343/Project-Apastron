using System.Collections.Generic;
using Apastron.Core;

namespace Apastron.Physics.Integrators;

/// <summary>
/// Semi-implicit (symplectic) Euler. Cheapest option, first-order, but symplectic so it
/// does not bleed energy the way explicit Euler does. Useful at very high time-warp
/// where the step count dominates cost.
/// </summary>
public sealed class SemiImplicitEulerIntegrator : IIntegrator
{
    public string Name => "Semi-implicit Euler";

    public void Step(RigidBody body, IReadOnlyList<CelestialBody> bodies, double dt)
    {
        Vec3 a = Gravity.Acceleration(body.Position, bodies) + body.ThrustAcceleration;
        body.Velocity += a * dt;             // update velocity FIRST (this is what makes it symplectic)
        body.Position += body.Velocity * dt;
    }
}
