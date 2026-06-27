using System;
using System.Globalization;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Physics.Integrators;
using Apastron.Simulation;

namespace Apastron.Diagnostics;

/// <summary>
/// Headless numerical-accuracy check. Runs each integrator for exactly one orbital
/// period of the default LEO scenario (1 s steps) and reports how well the orbit
/// closes and how well specific energy is conserved. Run with: <c>Apastron --selftest</c>.
/// No OpenGL context is created.
/// </summary>
public static class PhysicsSelfTest
{
    public static void Run()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        Console.WriteLine("Apastron physics self-test");
        Console.WriteLine("==========================");
        Console.WriteLine();

        // Reference orbit (analytic) from a throwaway world.
        var refWorld  = Scenarios.LowEarthOrbit();
        var earth     = refWorld.Bodies[0];
        var v0        = refWorld.Vessels[0];
        var refElems  = OrbitalElements.Compute(v0.Position, v0.Velocity, earth.Position, earth.Mu, earth.Radius);

        double period = refElems.Period;
        Console.WriteLine($"Scenario        : circular LEO, altitude {refElems.Altitude / 1000.0:F1} km");
        Console.WriteLine($"Radius (centre) : {refElems.RadialDistance:F1} m");
        Console.WriteLine($"Circular speed  : {refElems.Speed:F3} m/s");
        Console.WriteLine($"Eccentricity    : {refElems.Eccentricity:E3}");
        Console.WriteLine($"Analytic period : {period:F2} s  ({period / 60.0:F2} min)");
        Console.WriteLine();

        IIntegrator[] integrators =
        {
            new VelocityVerletIntegrator(),
            new SemiImplicitEulerIntegrator(),
            new Rk4Integrator(),
        };

        Console.WriteLine($"{"Integrator",-22} {"Close err (m)",15} {"Alt dev (m)",14} {"Energy drift",14}");
        Console.WriteLine(new string('-', 68));

        const double dt = 1.0;
        int steps = (int)Math.Round(period / dt);

        foreach (var integ in integrators)
        {
            var world  = Scenarios.LowEarthOrbit();
            world.Integrator = integ;
            var body   = world.Bodies[0];
            var vessel = world.Vessels[0];

            Vec3 startPos = vessel.Position;
            double e0 = SpecificEnergy(vessel, body);
            double targetAlt = startPos.Length - body.Radius;
            double maxAltDev = 0.0;

            for (int i = 0; i < steps; i++)
            {
                world.Step(dt);
                double alt = (vessel.Position - body.Position).Length - body.Radius;
                double dev = Math.Abs(alt - targetAlt);
                if (dev > maxAltDev) maxAltDev = dev;
            }

            double closeErr = (vessel.Position - startPos).Length;
            double e1 = SpecificEnergy(vessel, body);
            double drift = Math.Abs(e0) > 0 ? (e1 - e0) / Math.Abs(e0) : 0.0;

            Console.WriteLine(
                $"{integ.Name,-22} {closeErr,15:F1} {maxAltDev,14:F1} {drift,13:P4}");
        }

        Console.WriteLine();
        Console.WriteLine("Symplectic integrators (Verlet, Semi-implicit Euler) should show bounded");
        Console.WriteLine("energy drift; RK4 is more accurate per step but slowly loses energy.");
    }

    /// <summary>Specific orbital energy eps = v^2/2 - mu/r (J/kg).</summary>
    private static double SpecificEnergy(RigidBody vessel, CelestialBody body)
    {
        double r = (vessel.Position - body.Position).Length;
        double v = vessel.Velocity.Length;
        return 0.5 * v * v - body.Mu / r;
    }
}
