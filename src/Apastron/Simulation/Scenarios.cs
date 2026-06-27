using System;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Physics.Integrators;

namespace Apastron.Simulation;

/// <summary>Factory methods that build ready-to-run worlds.</summary>
public static class Scenarios
{
    /// <summary>
    /// A single vessel on a circular equatorial orbit around the Earth at the given
    /// altitude (default 400 km, roughly the ISS). The circular speed is set from
    /// v = sqrt(mu / r) so eccentricity starts at ~0.
    /// </summary>
    public static PhysicsWorld LowEarthOrbit(double altitude = 400_000.0)
    {
        var world = new PhysicsWorld { Integrator = new VelocityVerletIntegrator() };

        var earth = new CelestialBody
        {
            Name     = "Earth",
            Mu       = MathConstants.Mu.Earth,
            Radius   = MathConstants.Radius.Earth,
            Position = Vec3.Zero,
            Color    = (0.20f, 0.45f, 0.85f),
        };
        world.Bodies.Add(earth);

        double r     = earth.Radius + altitude;
        double vCirc = Math.Sqrt(earth.Mu / r);

        var vessel = new RigidBody
        {
            Name     = "SCAS DUAT",
            Mass     = 1200.0,
            Position = new Vec3(r, 0.0, 0.0),
            Velocity = new Vec3(0.0, vCirc, 0.0),
        };
        world.Vessels.Add(vessel);

        var yard = CreateTarget(earth, 550_000.0, 25.0, "Kestrel Yard");
        yard.IsStation = true; yard.Mass = 1_800_000.0;
        yard.SpinRadius = 120.0; yard.SpinRpm = 3.0;
        world.Vessels.Add(yard);

        world.RenderScaleHint = 1.0e-6;     // 1 unit = 1000 km
        world.CameraDistanceHint = 28.0;
        return world;
    }

    /// <summary>
    /// The default "showcase" scenario: a single vessel on a circular equatorial orbit high above
    /// the Earth (default 8,000 km), where the planet reads as a distant backdrop rather than filling
    /// the view. Paired with the stylized close-up camera, this frames the ship as the subject — the
    /// scale is deliberately non-physical (the rendered vessel is enormous) for a readable, cinematic view.
    /// </summary>
    public static PhysicsWorld HighOrbit(double altitude = 8_000_000.0)
    {
        var world = new PhysicsWorld { Integrator = new VelocityVerletIntegrator() };

        var earth = new CelestialBody
        {
            Name     = "Earth",
            Mu       = MathConstants.Mu.Earth,
            Radius   = MathConstants.Radius.Earth,
            Position = Vec3.Zero,
            Color    = (0.20f, 0.45f, 0.85f),
        };
        world.Bodies.Add(earth);

        double r     = earth.Radius + altitude;
        double vCirc = Math.Sqrt(earth.Mu / r);

        world.Vessels.Add(new RigidBody
        {
            Name     = "SCAS DUAT",
            Mass     = 1200.0,
            Position = new Vec3(r, 0.0, 0.0),
            Velocity = new Vec3(0.0, vCirc, 0.0),
        });

        var port = CreateTarget(earth, 6_000_000.0, 35.0, "Highport Meridian");
        port.IsStation = true; port.Mass = 2_500_000.0;
        port.SpinRadius = 220.0; port.SpinRpm = 2.0;
        world.Vessels.Add(port);

        world.RenderScaleHint = 1.0e-6;     // 1 unit = 1000 km
        world.CameraDistanceHint = 12.0;    // close, for the stylized ship-subject view
        return world;
    }

    /// <summary>
    /// A heliocentric test scenario: the Sun at the origin with Earth and Mars as fixed point
    /// masses, and a torchship at rest near Earth. Planets are stationary (a deliberate
    /// simplification for multi-day torch transits; full ephemeris motion is future work),
    /// which makes a flip-and-burn to a fixed destination clean to fly. Pair with
    /// <see cref="Apastron.Vehicles.Spacecraft.CreateTorchship"/>.
    /// </summary>
    public static PhysicsWorld Interplanetary()
    {
        const double au = 1.495978707e11;
        var world = new PhysicsWorld { Integrator = new VelocityVerletIntegrator() };

        world.Bodies.Add(new CelestialBody
        {
            Name = "Sun", Mu = MathConstants.Mu.Sun, Radius = MathConstants.Radius.Sun,
            Position = Vec3.Zero, Color = (1.0f, 0.85f, 0.30f),
        });
        var earth = new CelestialBody
        {
            Name = "Earth", Mu = MathConstants.Mu.Earth, Radius = MathConstants.Radius.Earth,
            Position = new Vec3(au, 0.0, 0.0), Color = (0.20f, 0.45f, 0.85f),
        };
        world.Bodies.Add(earth);

        double th = 45.0 * MathConstants.DegToRad;
        var mars = new CelestialBody
        {
            Name = "Mars", Mu = MathConstants.Mu.Mars, Radius = MathConstants.Radius.Mars,
            Position = new Vec3(1.524 * au * Math.Cos(th), 1.524 * au * Math.Sin(th), 0.0),
            Color = (0.80f, 0.35f, 0.25f),
        };
        world.Bodies.Add(mars);

        // Torchship at rest (heliocentric) ~500,000 km from Earth, ready to burn for Mars.
        world.Vessels.Add(new RigidBody
        {
            Name = "SCAS DUAT",
            Mass = 1_069_300.0,
            Position = earth.Position + new Vec3(0.0, -5.0e8, 0.0),
            Velocity = Vec3.Zero,
        });

        var meridian = CreateTarget(earth, 8_000_000.0, 0.0, "Highport Meridian");
        meridian.IsStation = true; meridian.Mass = 2_500_000.0;
        meridian.SpinRadius = 220.0; meridian.SpinRpm = 2.0;
        world.Vessels.Add(meridian);

        var lowell = CreateTarget(mars, 5_000_000.0, 0.0, "Lowell Station");
        lowell.IsStation = true; lowell.Mass = 2_000_000.0;
        lowell.SpinRadius = 180.0; lowell.SpinRpm = 2.5;
        world.Vessels.Add(lowell);

        world.RenderScaleHint = 5.0e-9;       // 1 AU ~ 748 units
        world.CameraDistanceHint = 1200.0;    // ~1.6 AU standoff
        return world;
    }

    /// <summary>
    /// A deep-space duel: two warships ~2,500 km apart with no central body, so munitions fly on
    /// straight lines (gravity is negligible at combat ranges anyway) and the view is clean. The
    /// opening range sits just outside missile reach, so an engagement begins with sensor contact and
    /// a closing phase rather than a knife fight — beyond-visual-range combat in an empty void where
    /// there is no cover or concealment, only sensors, missiles, lasers and maneuver. The second ship
    /// crosses laterally so kinetic lead solutions are non-trivial once ranges close. Pair with
    /// <see cref="Apastron.Vehicles.Spacecraft.CreateWarship"/> and a CombatManager.
    /// </summary>
    public static PhysicsWorld Duel()
    {
        var world = new PhysicsWorld { Integrator = new VelocityVerletIntegrator() };

        world.Vessels.Add(new RigidBody
        {
            Name = "Vanguard", Mass = 478_000.0,
            Position = Vec3.Zero, Velocity = Vec3.Zero,
        });
        world.Vessels.Add(new RigidBody
        {
            Name = "Corsair", Mass = 478_000.0,
            // ~2,500 km downrange (beyond-visual-range opening) with a lateral crossing velocity.
            Position = new Vec3(2_500.0e3, 0.0, 0.0), Velocity = new Vec3(0.0, 800.0, 0.0),
        });

        world.RenderScaleHint = 3.0e-4;       // 1 unit ~ 3.3 km (unchanged; missions rely on this)
        world.CameraDistanceHint = 3000.0;    // frames the ~2,500 km BVR engagement (was ~150 km)
        return world;
    }

    /// phase-shifted by <paramref name="phaseLeadDeg"/> along the orbit. Same plane as
    /// <see cref="LowEarthOrbit"/> (equatorial, prograde in +Z), so it is a valid rendezvous target.
    /// </summary>
    public static RigidBody CreateTarget(CelestialBody body, double altitude, double phaseLeadDeg,
                                         string name = "Station")
    {
        double r = body.Radius + altitude;
        double v = Math.Sqrt(body.Mu / r);
        double th = phaseLeadDeg * MathConstants.DegToRad;

        Vec3 pos = body.Position + new Vec3(r * Math.Cos(th), r * Math.Sin(th), 0.0);
        Vec3 vel = new Vec3(-Math.Sin(th), Math.Cos(th), 0.0) * v;

        return new RigidBody { Name = name, Mass = 4000.0, Position = pos, Velocity = vel };
    }
}
