using System;
using System.Collections.Generic;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.Simulation;

/// <summary>
/// A planned burn anchored at an absolute simulation time, with delta-v expressed in the
/// orbital frame (prograde / normal / radial) at the node. Also carries the live execution
/// state used by the autopilot.
/// </summary>
public sealed class ManeuverNode
{
    public double NodeTime;     // absolute sim time of the node (s)
    public double Prograde;     // delta-v components (m/s)
    public double Normal;
    public double Radial;

    // execution state
    public bool Armed;
    public bool Burning;
    public bool Completed;
    public double DeliveredDv;
    public Vec3 BurnDir;        // world direction, frozen when the burn starts

    public double Magnitude => Math.Sqrt(Prograde * Prograde + Normal * Normal + Radial * Radial);
}

/// <summary>Render-facing summary of a node: its world position and the resulting orbit.</summary>
public sealed class ManeuverPreview
{
    public bool Active;
    public Vec3 NodeWorld;
    public List<Vec3> Path = new();
    public OrbitalElements Post;
    public bool HasPost;
}

/// <summary>
/// Maneuver-node mechanics: where the node sits, what orbit results from its delta-v, and the
/// finite-burn autopilot that flies it. The node delta-v is applied in the orbital frame
/// {prograde = v̂, normal = ĥ, radial = prograde × normal}; the autopilot holds the resulting
/// world vector inertially and throttles until the planned delta-v has been delivered.
/// </summary>
public static class Maneuver
{
    private static (Vec3 p, Vec3 n, Vec3 w) Frame(Vec3 pos, Vec3 vel, Vec3 bodyPos)
    {
        Vec3 p = vel.Normalized();
        Vec3 h = Vec3.Cross(pos - bodyPos, vel);
        Vec3 n = h.Length > 1e-9 ? h.Normalized() : Vec3.UnitZ;
        Vec3 w = Vec3.Cross(p, n);
        return (p, n, w);
    }

    private static Vec3 DvWorld(ManeuverNode node, (Vec3 p, Vec3 n, Vec3 w) f)
        => f.p * node.Prograde + f.n * node.Normal + f.w * node.Radial;

    /// <summary>State (world position/velocity) at the node, by propagating the vessel forward.</summary>
    public static bool NodeState(PhysicsWorld world, ManeuverNode node,
                                 out Vec3 pos, out Vec3 vel, out CelestialBody body)
    {
        pos = Vec3.Zero; vel = Vec3.Zero; body = null!;
        RigidBody? v = world.PrimaryVessel;
        if (v == null) return false;
        CelestialBody? b = world.DominantBody(v.Position);
        if (b == null) return false;
        body = b;

        double lead = node.NodeTime - world.SimTime;
        if (lead <= 0.0)
        {
            pos = v.Position; vel = v.Velocity; return true;
        }
        if (Kepler.TryPropagate(v.Position - b.Position, v.Velocity, b.Mu, lead, out Vec3 rp, out Vec3 vp))
        {
            pos = b.Position + rp; vel = vp; return true;
        }
        pos = v.Position; vel = v.Velocity; return true;   // non-elliptical fallback
    }

    public static ManeuverPreview BuildPreview(PhysicsWorld world, ManeuverNode? node)
    {
        var mp = new ManeuverPreview();
        if (node == null || node.Completed) return mp;
        if (!NodeState(world, node, out Vec3 pos, out Vec3 vel, out CelestialBody body)) return mp;

        mp.Active = true;
        mp.NodeWorld = pos;

        if (node.Magnitude > 1e-6)
        {
            Vec3 dv = DvWorld(node, Frame(pos, vel, body.Position));
            Vec3 vPost = vel + dv;
            mp.Path = OrbitPath.Compute(pos, vPost, body.Position, body.Mu);
            mp.Post = OrbitalElements.Compute(pos, vPost, body.Position, body.Mu, body.Radius);
            mp.HasPost = true;
        }
        return mp;
    }

    /// <summary>Estimated burn duration for a delta-v at full throttle (rocket equation).</summary>
    public static double EstimateBurnTime(Spacecraft ship, double dvMag)
    {
        if (dvMag <= 0.0 || ship.EffectiveIsp <= 0.0 || ship.MassFlowFullThrust <= 0.0) return 0.0;
        double ve = ship.EffectiveIsp * MathConstants.StandardGravity;
        double m0 = ship.TotalMass;
        double mf = m0 * Math.Exp(-dvMag / ve);
        return (m0 - mf) / ship.MassFlowFullThrust;
    }

    /// <summary>Autopilot: arm-aware finite burn. Call each step before Propulsion.Apply.</summary>
    public static void UpdateExecution(PhysicsWorld world, Spacecraft ship, ManeuverNode? node, double simSeconds)
    {
        if (node == null || !node.Armed) return;

        if (node.Magnitude <= 1e-6) { node.Armed = false; node.Completed = true; return; }

        if (node.DeliveredDv >= node.Magnitude || ship.PropellantMass <= 0.0)
        {
            ship.Mode = ThrustMode.None;
            node.Burning = false;
            node.Armed = false;
            node.Completed = true;
            return;
        }

        double ttn = node.NodeTime - world.SimTime;
        double tb = EstimateBurnTime(ship, node.Magnitude);
        if (ttn > tb * 0.5) return;                      // coast until the burn window

        if (!node.Burning)
        {
            if (!NodeState(world, node, out Vec3 pos, out Vec3 vel, out CelestialBody body))
            {
                node.Armed = false; return;
            }
            Vec3 dv = DvWorld(node, Frame(pos, vel, body.Position));
            node.BurnDir = dv.Length > 1e-9 ? dv.Normalized() : Vec3.Zero;
            node.Burning = true;
        }

        if (node.BurnDir.Length < 1e-9) { node.Armed = false; return; }

        ship.Mode = ThrustMode.Inertial;
        ship.BurnDirectionWorld = node.BurnDir;
        ship.Throttle = 1.0;

        if (ship.TotalMass > 0.0)
            node.DeliveredDv += (ship.TotalThrustVac / ship.TotalMass) * simSeconds;
    }
}
