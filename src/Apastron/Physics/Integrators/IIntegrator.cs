using System.Collections.Generic;

namespace Apastron.Physics.Integrators;

/// <summary>A numerical ODE integrator advancing one vessel by one timestep.</summary>
public interface IIntegrator
{
    string Name { get; }
    void Step(RigidBody body, IReadOnlyList<CelestialBody> bodies, double dt);
}
