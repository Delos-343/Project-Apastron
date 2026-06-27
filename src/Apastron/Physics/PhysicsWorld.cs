using System.Collections.Generic;
using Apastron.Core;
using Apastron.Physics.Integrators;

namespace Apastron.Physics;

/// <summary>
/// Owns the simulation state: the gravitating bodies, the vessels, the active
/// integrator and the elapsed simulation clock. Bodies are fixed in this build;
/// only vessels are propagated.
/// </summary>
public sealed class PhysicsWorld
{
    public List<CelestialBody> Bodies  { get; } = new();
    public List<RigidBody>     Vessels { get; } = new();

    /// <summary>Active numerical integrator. Defaults to the symplectic Velocity Verlet.</summary>
    public IIntegrator Integrator { get; set; } = new VelocityVerletIntegrator();

    /// <summary>Elapsed simulation time (s) since the scenario start.</summary>
    public double SimTime { get; private set; }

    /// <summary>Overwrite the simulation clock (used when loading a saved scenario).</summary>
    public void SetSimTime(double seconds) => SimTime = seconds;

    /// <summary>Render units per metre, chosen per scenario (LEO ~1e-6, interplanetary ~5e-9).</summary>
    public double RenderScaleHint { get; set; } = 1.0e-6;

    /// <summary>Suggested camera standoff (render units) when this scenario loads.</summary>
    public double CameraDistanceHint { get; set; } = 28.0;

    /// <summary>Convenience accessor for the vessel the UI focuses on.</summary>
    public RigidBody? PrimaryVessel => Vessels.Count > 0 ? Vessels[0] : null;

    /// <summary>The rendezvous target, if one has been spawned (the second vessel).</summary>
    public RigidBody? TargetVessel => Vessels.Count > 1 ? Vessels[1] : null;

    /// <summary>Install or replace the rendezvous target at vessel slot 1 (never displacing a station).</summary>
    public void SetTarget(RigidBody target)
    {
        if (Vessels.Count > 1 && !Vessels[1].IsStation) Vessels[1] = target;
        else if (Vessels.Count > 1) Vessels.Insert(1, target);
        else Vessels.Add(target);
    }

    /// <summary>Remove the rendezvous target, if any (stations are scenario furniture and stay).</summary>
    public void ClearTarget()
    {
        if (Vessels.Count > 1 && !Vessels[1].IsStation) Vessels.RemoveAt(1);
    }

    /// <summary>Advance every vessel by exactly one fixed step of <paramref name="dt"/> seconds.</summary>
    public void Step(double dt)
    {
        for (int i = 0; i < Vessels.Count; i++)
            Integrator.Step(Vessels[i], Bodies, dt);
        SimTime += dt;
    }

    /// <summary>
    /// Advance the world by <paramref name="seconds"/> of simulation time using
    /// fixed sub-steps of <paramref name="fixedStep"/>. The step count is clamped to
    /// <paramref name="maxSteps"/> to keep a single frame bounded under heavy time-warp;
    /// any leftover is applied as one partial step so the clock stays exact.
    /// </summary>
    public void Advance(double seconds, double fixedStep, int maxSteps)
    {
        if (seconds <= 0.0 || fixedStep <= 0.0) return;

        int steps = (int)(seconds / fixedStep);
        if (steps > maxSteps) steps = maxSteps;

        for (int i = 0; i < steps; i++)
            Step(fixedStep);

        double remainder = seconds - steps * fixedStep;
        if (remainder > 1e-9)
            Step(remainder);
    }

    /// <summary>The body exerting the strongest gravity at <paramref name="position"/> (max mu / d^2).</summary>
    public CelestialBody? DominantBody(Vec3 position)
    {
        CelestialBody? best = null;
        double bestPull = double.NegativeInfinity;
        for (int i = 0; i < Bodies.Count; i++)
        {
            CelestialBody b = Bodies[i];
            double d2 = (b.Position - position).LengthSquared;
            if (d2 < 1.0) d2 = 1.0;
            double pull = b.Mu / d2;
            if (pull > bestPull) { bestPull = pull; best = b; }
        }
        return best;
    }
}
