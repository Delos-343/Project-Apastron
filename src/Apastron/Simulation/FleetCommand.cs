using System;
using System.Collections.Generic;
using Apastron.Core;
using Apastron.Physics;

namespace Apastron.Simulation;

/// <summary>
/// Homeworld-style fleet movement. Holds a per-vessel queue of 3D move-to-point orders and, each sim
/// step, drives every ordered vessel toward its current waypoint with an "arrive" controller: it picks the
/// fastest speed from which the ship can still brake to a stop within the remaining distance (capped at a
/// cruise speed), chases that velocity with a proportional law, feeds gravity forward so a ship can hold a
/// point, and clamps the result to the vessel's real engine thrust. Thrust is written straight onto the
/// <see cref="RigidBody"/> (the same channel the combat AI and propulsion bridge use), so a move order
/// transparently overrides whatever else was steering that ship until the order completes or is cleared.
///
/// Pure translational control - it only sets <c>ThrustWorld</c>, so it cannot destabilise the integrator or
/// the renderer. It is deliberately decoupled from <c>Spacecraft</c>: it reads <see cref="RigidBody.MaxThrust"/>
/// (kept in sync by the propulsion/combat updates) and does not itself burn rocket propellant - fleet
/// maneuvering is treated as station-keeping/RCS thrust in this phase.
/// </summary>
public sealed class FleetManager
{
    private const double CruiseSpeed = 18000.0;  // m/s: transit speed cap
    private const double DefaultAccel = 6.0;     // m/s^2: fallback cap when MaxThrust is unknown
    private const double ArriveRadius = 1200.0;  // m: within this (and slow) the waypoint counts as reached
    private const double ArriveSpeed  = 25.0;    // m/s: "stopped enough" to settle / advance a waypoint
    private const double VelGain      = 1.6;     // 1/s: how hard the ship chases its desired velocity
    private const double StopSafety   = 0.85;    // derate braking so ships settle instead of overshooting

    /// <summary>The live order state for one vessel: a waypoint queue plus arrival/hold flags.</summary>
    public sealed class Slot
    {
        /// <summary>Pending move points in order; index 0 is the active goal. Empty = idle.</summary>
        public readonly List<Vec3> Waypoints = new();
        /// <summary>Keep station-keeping at the final point after arrival (Homeworld "stop here").</summary>
        public bool Hold = true;
        /// <summary>True once the final waypoint has been reached.</summary>
        public bool Arrived;
    }

    private readonly Dictionary<RigidBody, Slot> _slots = new();

    /// <summary>True if the vessel currently has at least one pending move point.</summary>
    public bool HasOrder(RigidBody b) => _slots.TryGetValue(b, out Slot? s) && s.Waypoints.Count > 0;

    /// <summary>The vessel's order state, or null if it has never been ordered.</summary>
    public Slot? Get(RigidBody b) => _slots.TryGetValue(b, out Slot? s) ? s : null;

    /// <summary>All current order state (for rendering move lines / waypoints).</summary>
    public IReadOnlyDictionary<RigidBody, Slot> Slots => _slots;

    /// <summary>Issue a move-to-point order. When <paramref name="queue"/> is true the point is appended as a
    /// waypoint; otherwise it replaces the current order.</summary>
    public void OrderMove(RigidBody b, Vec3 target, bool queue)
    {
        if (!_slots.TryGetValue(b, out Slot? s)) { s = new Slot(); _slots[b] = s; }
        if (!queue) s.Waypoints.Clear();
        s.Waypoints.Add(target);
        s.Arrived = false;
    }

    /// <summary>Cancel a vessel's order and let it coast.</summary>
    public void Stop(RigidBody b)
    {
        if (_slots.TryGetValue(b, out Slot? s)) { s.Waypoints.Clear(); s.Arrived = false; }
        b.ThrustWorld = Vec3.Zero;
    }

    /// <summary>Cancel every order (coasting handled next frame).</summary>
    public void StopAll() { foreach (KeyValuePair<RigidBody, Slot> kv in _slots) kv.Value.Waypoints.Clear(); }

    /// <summary>Drive every ordered vessel one frame, overriding its thrust. Call after the combat AI so an
    /// explicit move order wins over autonomous steering.</summary>
    public void Update(PhysicsWorld world, double dt)
    {
        if (_slots.Count == 0) return;

        foreach (KeyValuePair<RigidBody, Slot> kv in _slots)
        {
            RigidBody body = kv.Key;
            Slot slot = kv.Value;
            if (slot.Waypoints.Count == 0) continue;

            Vec3 goal = slot.Waypoints[0];
            Vec3 toGoal = goal - body.Position;
            double dist = toGoal.Length;

            bool last = slot.Waypoints.Count == 1;
            if (dist < ArriveRadius && body.Velocity.Length < ArriveSpeed)
            {
                if (!last) { slot.Waypoints.RemoveAt(0); continue; }   // next leg
                slot.Arrived = true;
                if (!slot.Hold) { slot.Waypoints.Clear(); body.ThrustWorld = Vec3.Zero; continue; }
            }

            double aMax = body.MaxThrust > 0.0 && body.Mass > 0.0 ? body.MaxThrust / body.Mass : DefaultAccel;
            if (aMax < 1e-6) aMax = DefaultAccel;

            // Arrive: fastest speed from which we can still brake to rest within `dist`, capped at cruise.
            double stopSpeed = Math.Sqrt(2.0 * aMax * Math.Max(dist, 0.0) * StopSafety);
            double desiredSpeed = Math.Min(CruiseSpeed, stopSpeed);
            Vec3 dir = dist > 1e-6 ? toGoal / dist : Vec3.Zero;
            Vec3 desiredVel = dir * desiredSpeed;

            // Chase the desired velocity; feed gravity forward so the ship can actually hold a fixed point.
            Vec3 grav = Gravity.Acceleration(body.Position, world.Bodies);
            Vec3 accel = (desiredVel - body.Velocity) * VelGain - grav;

            double am = accel.Length;
            if (am > aMax) accel = accel * (aMax / am);

            if (!IsFinite(accel)) { body.ThrustWorld = Vec3.Zero; continue; }
            body.ThrustWorld = accel * body.Mass;
        }
    }

    /// <summary>Forget orders for vessels that have left the world (e.g. destroyed).</summary>
    public void Prune(PhysicsWorld world)
    {
        if (_slots.Count == 0) return;
        List<RigidBody>? dead = null;
        foreach (RigidBody b in _slots.Keys)
            if (!world.Vessels.Contains(b)) (dead ??= new List<RigidBody>()).Add(b);
        if (dead != null) foreach (RigidBody b in dead) _slots.Remove(b);
    }

    private static bool IsFinite(Vec3 v) =>
        double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
}

/// <summary>The shape a group adopts around a move target. <see cref="None"/> is a loose ring on the
/// movement plane; the rest are oriented to the fleet's heading (the direction from the group centroid to the
/// target): a wall faces it, a delta points along it, a claw curves around it, a sphere englobes it.</summary>
public enum FleetFormation { None, Sphere, Wall, Delta, Claw }

/// <summary>
/// Computes per-ship slot offsets (relative to the move target) for a formation, given an orthonormal frame
/// oriented to the fleet's heading: <paramref name="fwd"/> points along the move, <paramref name="right"/> is
/// lateral, <paramref name="up"/> is vertical. Pure geometry - no engine or world state.
/// </summary>
public static class Formations
{
    public static void Layout(FleetFormation kind, int n, Vec3 fwd, Vec3 right, Vec3 up,
                              double spacing, List<Vec3> offsets)
    {
        offsets.Clear();
        if (n <= 0) return;
        if (n == 1) { offsets.Add(Vec3.Zero); return; }

        switch (kind)
        {
            case FleetFormation.Sphere:
            {
                double radius = spacing * System.Math.Cbrt(n) * 0.85;
                const double golden = System.Math.PI * (3.0 - 2.2360679775);   // golden angle
                for (int i = 0; i < n; i++)
                {
                    double y = 1.0 - 2.0 * (i + 0.5) / n;          // -1..1
                    double rr = System.Math.Sqrt(System.Math.Max(0.0, 1.0 - y * y));
                    double th = golden * i;
                    offsets.Add((right * (System.Math.Cos(th) * rr) + up * y + fwd * (System.Math.Sin(th) * rr)) * radius);
                }
                break;
            }
            case FleetFormation.Wall:
            {
                int cols = (int)System.Math.Ceiling(System.Math.Sqrt(n));
                int rows = (int)System.Math.Ceiling((double)n / cols);
                for (int i = 0; i < n; i++)
                {
                    int c = i % cols, r = i / cols;
                    offsets.Add(right * ((c - (cols - 1) / 2.0) * spacing) + up * ((r - (rows - 1) / 2.0) * spacing));
                }
                break;
            }
            case FleetFormation.Delta:
            {
                offsets.Add(Vec3.Zero);                            // apex leads
                for (int i = 1; i < n; i++)
                {
                    int side = (i % 2 == 0) ? 1 : -1;
                    int rank = (i + 1) / 2;
                    offsets.Add(right * (side * rank * spacing) - fwd * (rank * spacing));
                }
                break;
            }
            case FleetFormation.Claw:
            {
                double radius = spacing * n / 2.0;
                const double spread = 1.2;                          // half-arc (rad)
                for (int i = 0; i < n; i++)
                {
                    double a = -spread + 2.0 * spread * i / (n - 1);
                    offsets.Add(right * (System.Math.Sin(a) * radius) + fwd * ((System.Math.Cos(a) - 1.0) * radius));
                }
                break;
            }
            default:   // None: loose ring on the movement plane (right/fwd span the horizontal plane)
            {
                double radius = spacing * n / (2.0 * System.Math.PI);
                for (int i = 0; i < n; i++)
                {
                    double a = 2.0 * System.Math.PI * i / n;
                    offsets.Add(right * (System.Math.Cos(a) * radius) + fwd * (System.Math.Sin(a) * radius));
                }
                break;
            }
        }
    }
}
