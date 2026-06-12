using System;
using System.Collections.Generic;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.Simulation;

public enum OrderKind { HoldOrbit, Circularize, ChangeOrbit, RendezvousTarget, TorchTransfer, DockAt }

/// <summary>A task-level order issued by the player; the ship AI plans and flies the steps.</summary>
public sealed class ShipOrder
{
    public OrderKind Kind;
    public string Label = "";
    public double TargetAltitude;    // ChangeOrbit (m above the primary's surface)
    public string? BodyName;         // TorchTransfer destination
    public string? StationName;      // DockAt destination

    // executor state
    public int Phase;
    public ClosestApproach Ca;       // rendezvous-chain terminal cache
    public int CaCooldown;
    public int Hops;                 // DockAt: torch course-correction hops used
    public int Retries;              // DockAt: phasing retries after a wide pass
}

/// <summary>
/// The task-level command layer: the player issues an order ("circularize", "change orbit to X",
/// "rendezvous with the target", "torch to Mars"), and the ship's AI plans and executes the steps
/// itself through the existing machinery - maneuver nodes flown by the finite-burn autopilot,
/// Hohmann transfers, transfer-window phasing, closest-approach matching, and the flip-and-burn
/// torch autopilot. One order runs at a time; further orders queue. The Ship AI safeguards
/// (Chunk A) still stand above this layer: a propellant-reserve cut stands the order down.
/// All math here is either reused from the validated planners or mirror-validated (time-to-apsis).
/// </summary>
public sealed class ShipOrders
{
    public ShipOrder? Current { get; private set; }
    public List<ShipOrder> Queue { get; } = new();
    public string Status { get; private set; } = "No orders";

    /// <summary>The status line shown as the ship's activity while an order runs.</summary>
    public string? ActiveStatus => Current != null ? Status : null;

    /// <summary>Set by the UI; the executor tears the current order down safely next update.</summary>
    public bool CancelRequested;

    private double _dt;   // sim seconds this frame (for warp-safe direct-burn controllers)

    public void Issue(ShipOrder order)
    {
        if (Current == null) { Current = order; order.Phase = 0; }
        else Queue.Add(order);
    }

    public void ClearQueue() => Queue.Clear();

    public void Update(Spacecraft ship, PhysicsWorld world, ref ManeuverNode? node,
                       ref FlipBurnPlan? flip, ShipAI ai, bool combatActive, double dt)
    {
        double now = world.SimTime;
        _dt = dt;

        if (CancelRequested)
        {
            CancelRequested = false;
            if (Current != null)
            {
                node = null;
                if (flip is { Active: true }) { flip.Active = false; flip.Phase = "Aborted (order cancelled)"; }
                ship.Mode = ThrustMode.None;
                ai.Report(now, AiSeverity.Info, $"Order cancelled: {Current.Label}.");
                Current = null;
            }
        }

        if (combatActive)
        {
            Status = "Orders stood down - fleet engagement in progress";
            return;
        }

        if (Current == null)
        {
            if (Queue.Count > 0) { Current = Queue[0]; Queue.RemoveAt(0); Current.Phase = 0; }
            else { Status = "No orders - ship under manual command"; return; }
        }

        RigidBody? v = world.PrimaryVessel;
        CelestialBody? primary = v != null ? world.DominantBody(v.Position) : null;
        if (v == null) { Fail(ai, now, "no vessel"); return; }

        ShipOrder o = Current;
        switch (o.Kind)
        {
            case OrderKind.HoldOrbit:
                ship.Mode = ThrustMode.None;
                node = null;
                if (flip is { Active: true }) { flip.Active = false; flip.Phase = "Aborted (hold orbit)"; }
                Complete(ai, now, "Holding orbit; drives secured.");
                break;

            case OrderKind.Circularize:
                if (primary == null) { Fail(ai, now, "no dominant body"); break; }
                UpdateCircularize(ship, world, primary, v, ref node, ai, now, o);
                break;

            case OrderKind.ChangeOrbit:
                if (primary == null) { Fail(ai, now, "no dominant body"); break; }
                UpdateChangeOrbit(ship, world, primary, v, ref node, ai, now, o);
                break;

            case OrderKind.RendezvousTarget:
                if (primary == null) { Fail(ai, now, "no dominant body"); break; }
                if (world.TargetVessel == null) { Fail(ai, now, "no target vessel"); break; }
                UpdateRendezvous(ship, world, primary, v, world.TargetVessel, ref node, ai, now, o);
                break;

            case OrderKind.TorchTransfer:
                UpdateTorch(ship, world, v, ref flip, ai, now, o);
                break;

            case OrderKind.DockAt:
                UpdateDockAt(ship, world, v, ref node, ref flip, ai, now, o);
                break;
        }
    }

    // ----- order phase machines ------------------------------------------------------------

    private void UpdateCircularize(Spacecraft ship, PhysicsWorld world, CelestialBody primary,
                                   RigidBody v, ref ManeuverNode? node, ShipAI ai, double now, ShipOrder o)
    {
        if (o.Phase == 0)
        {
            Vec3 r = v.Position - primary.Position, vel = v.Velocity - primary.Velocity;
            if (!Elements(r, vel, primary.Mu, out double a, out double e)) { Fail(ai, now, "orbit not bound"); return; }
            if (e < 0.005) { Complete(ai, now, "Orbit already circular."); return; }
            if (!PlanCircularizeNode(r, vel, primary.Mu, now, out ManeuverNode n, out double rAp))
            { Fail(ai, now, "could not plan burn"); return; }
            node = n;
            ai.Report(now, AiSeverity.Info, $"Order: circularize at apoapsis ({(rAp - primary.Radius) / 1000.0:N0} km).",
                      $"Burn {n.Prograde:N0} m/s planned in T-{Fmt(n.NodeTime - now)}.");
            o.Phase = 1;
        }
        else if (WaitNode(ref node, ai, now, "Circularize"))
        {
            Complete(ai, now, "Orbit circularized.");
        }
    }

    private void UpdateChangeOrbit(Spacecraft ship, PhysicsWorld world, CelestialBody primary,
                                   RigidBody v, ref ManeuverNode? node, ShipAI ai, double now, ShipOrder o)
    {
        Vec3 r = v.Position - primary.Position, vel = v.Velocity - primary.Velocity;

        switch (o.Phase)
        {
            case 0:
            {
                if (!Elements(r, vel, primary.Mu, out _, out double e)) { Fail(ai, now, "orbit not bound"); return; }
                if (e >= 0.02)
                {
                    if (!PlanCircularizeNode(r, vel, primary.Mu, now, out ManeuverNode n, out _))
                    { Fail(ai, now, "could not plan pre-circularization"); return; }
                    node = n;
                    ai.Report(now, AiSeverity.Info, "Order: change orbit - circularizing first.",
                              $"Burn {n.Prograde:N0} m/s in T-{Fmt(n.NodeTime - now)}.");
                    o.Phase = 1;
                }
                else o.Phase = 2;
                break;
            }
            case 1:
                if (WaitNode(ref node, ai, now, "Pre-circularize")) o.Phase = 2;
                break;
            case 2:
            {
                double r1 = r.Length;
                double r2 = primary.Radius + o.TargetAltitude;
                if (Math.Abs(r2 - r1) < 0.005 * r1)
                { Complete(ai, now, "Already at the ordered altitude."); return; }
                var h = HohmannTransfer.Compute(primary.Mu, r1, r2);
                node = new ManeuverNode { NodeTime = now + 30.0, Prograde = h.DeltaV1, Armed = true };
                ai.Report(now, AiSeverity.Info,
                          $"Order: transfer to {o.TargetAltitude / 1000.0:N0} km circular orbit.",
                          $"Departure {h.DeltaV1:N0} m/s now; arrival {h.DeltaV2:N0} m/s in {Fmt(h.TransferTime)}.");
                o.Phase = 3;
                break;
            }
            case 3:
                if (WaitNode(ref node, ai, now, "Transfer departure")) o.Phase = 4;
                break;
            case 4:
            {
                double r2 = primary.Radius + o.TargetAltitude;
                if (!PlanApsisBurnTo(r, vel, primary.Mu, r2, now, out ManeuverNode n))
                { Fail(ai, now, "could not plan arrival burn"); return; }
                node = n;
                o.Phase = 5;
                break;
            }
            case 5:
                if (WaitNode(ref node, ai, now, "Arrival circularization"))
                    Complete(ai, now, $"New circular orbit at {o.TargetAltitude / 1000.0:N0} km.");
                break;
        }
    }

    private void UpdateRendezvous(Spacecraft ship, PhysicsWorld world, CelestialBody primary, RigidBody v,
                                  RigidBody target, ref ManeuverNode? node, ShipAI ai, double now, ShipOrder o)
    {
        ChainState st = RendezvousChain(ship, primary, v, target, ref node, ai, now, o, 0);
        if (Current == null) return;                          // the chain failed and tore the order down
        if (st == ChainState.Matched)
        {
            ship.Mode = ThrustMode.None;
            var rel = Rendezvous.Relative(v, target, primary);
            Complete(ai, now, $"Rendezvous complete - holding {rel.Range / 1000.0:N1} km from target.");
        }
        else if (st == ChainState.WidePass)
        {
            ai.Report(now, AiSeverity.Caution,
                      $"Co-orbital; closest approach {o.Ca.MinRange / 1000.0:N0} km exceeds 100 km.",
                      "Re-issue the rendezvous order at the next window to refine phasing.");
            Complete(ai, now, "Rendezvous phase complete (wide pass).");
        }
    }

    private enum ChainState { Running, Matched, WidePass }

    /// <summary>
    /// The shared rendezvous phase chain: pre-circularize if eccentric, phase for the transfer
    /// window, fly the Hohmann departure, circularize at the target radius, predict the closest
    /// approach, and finish with a velocity-match burn. Phases occupy o.Phase = basePhase ..
    /// basePhase+7 so both the Rendezvous order (base 0) and DockAt (base 20) can run it.
    /// Returns Matched at station-keeping, WidePass when the closest approach is too wide to
    /// match (caller decides: complete with a caution, or fly a phasing orbit and retry).
    /// </summary>
    private ChainState RendezvousChain(Spacecraft ship, CelestialBody primary, RigidBody v,
                                       RigidBody target, ref ManeuverNode? node, ShipAI ai,
                                       double now, ShipOrder o, int basePhase)
    {
        Vec3 r = v.Position - primary.Position, vel = v.Velocity - primary.Velocity;
        double r1 = r.Length;
        double r2 = (target.Position - primary.Position).Length;

        switch (o.Phase - basePhase)
        {
            case 0:
            {
                if (!Elements(r, vel, primary.Mu, out _, out double e)) { Fail(ai, now, "orbit not bound"); return ChainState.Running; }
                if (e >= 0.02)
                {
                    if (!PlanCircularizeNode(r, vel, primary.Mu, now, out ManeuverNode n, out _))
                    { Fail(ai, now, "could not plan pre-circularization"); return ChainState.Running; }
                    node = n;
                    ai.Report(now, AiSeverity.Info, "Rendezvous chain - circularizing first.",
                              $"Burn {n.Prograde:N0} m/s in T-{Fmt(n.NodeTime - now)}.");
                    o.Phase = basePhase + 1;
                }
                else o.Phase = basePhase + 2;
                break;
            }
            case 1:
                if (WaitNode(ref node, ai, now, "Pre-circularize")) o.Phase = basePhase + 2;
                break;
            case 2:
            {
                if (Math.Abs(r1 - r2) < 0.01 * r2) { o.Phase = basePhase + 6; break; }    // already co-orbital
                var rel = Rendezvous.Relative(v, target, primary);
                var h = HohmannTransfer.Compute(primary.Mu, r1, r2);
                double pA = MathConstants.TwoPi * Math.Sqrt(r1 * r1 * r1 / primary.Mu);
                double pT = MathConstants.TwoPi * Math.Sqrt(r2 * r2 * r2 / primary.Mu);
                double wait = Rendezvous.TimeToTransferWindow(rel.PhaseDeg, h.PhaseAngleDeg, pA, pT);
                if (double.IsInfinity(wait)) { Fail(ai, now, "no transfer window (matched periods)"); return ChainState.Running; }
                if (wait > 60.0)
                {
                    Status = $"Phasing - transfer window in T-{Fmt(wait)} (warp ahead)";
                    return ChainState.Running;
                }
                node = new ManeuverNode { NodeTime = now + wait, Prograde = h.DeltaV1, Armed = true };
                ai.Report(now, AiSeverity.Info, "Transfer window reached.",
                          $"Departure {h.DeltaV1:N0} m/s at T-{Fmt(wait)}; arrival in {Fmt(h.TransferTime)}.");
                o.Phase = basePhase + 3;
                break;
            }
            case 3:
                if (WaitNode(ref node, ai, now, "Transfer departure")) o.Phase = basePhase + 4;
                break;
            case 4:
            {
                if (!PlanApsisBurnTo(r, vel, primary.Mu, r2, now, out ManeuverNode n))
                { Fail(ai, now, "could not plan arrival burn"); return ChainState.Running; }
                node = n;
                o.Phase = basePhase + 5;
                break;
            }
            case 5:
                if (WaitNode(ref node, ai, now, "Arrival circularization")) o.Phase = basePhase + 6;
                break;
            case 6:
            {
                var relNow = Rendezvous.Relative(v, target, primary);
                if (relNow.Range < 30_000.0) { o.Phase = basePhase + 7; break; }
                if (--o.CaCooldown <= 0)
                {
                    double period = MathConstants.TwoPi * Math.Sqrt(r1 * r1 * r1 / primary.Mu);
                    o.Ca = Rendezvous.FindClosestApproach(v, target, primary, 2.0 * period);
                    o.CaCooldown = 30;
                }
                if (!o.Ca.Found) { Status = "Terminal: predicting closest approach..."; return ChainState.Running; }
                if (o.Ca.MinRange > 100_000.0) return ChainState.WidePass;
                if (o.Ca.TimeToCA <= 30.0) { o.Phase = basePhase + 7; break; }
                Status = $"Terminal: closest approach {o.Ca.MinRange / 1000.0:N0} km in T-{Fmt(o.Ca.TimeToCA)} (warp ahead)";
                break;
            }
            case 7:
            {
                if (BurnAuthority(ship, ai) is string why)
                { ship.Mode = ThrustMode.None; Fail(ai, now, why); return ChainState.Running; }
                Vec3 relVel = v.Velocity - target.Velocity;
                double rs = relVel.Length;
                if (rs < 3.0)
                {
                    ship.Mode = ThrustMode.None;
                    return ChainState.Matched;
                }
                CommandBurn(ship, -relVel, _dt);
                Status = $"Terminal: matching velocity - {rs:N1} m/s relative";
                break;
            }
        }
        return ChainState.Running;
    }

    /// <summary>
    /// Dock-and-replenish: the full autonomous path to a station. If the station orbits another
    /// body, fly torch hops to a standoff point (leading the body's motion; up to 3 course
    /// corrections), feedback-circularize into orbit, then run the rendezvous chain, a
    /// proportional terminal approach, and finally dock and transfer propellant. A wide pass
    /// triggers a 7% phasing orbit and a retry instead of giving up.
    /// </summary>
    private void UpdateDockAt(Spacecraft ship, PhysicsWorld world, RigidBody v,
                              ref ManeuverNode? node, ref FlipBurnPlan? flip, ShipAI ai, double now, ShipOrder o)
    {
        RigidBody? station = FindStation(world, o.StationName);
        if (station == null) { Fail(ai, now, $"station '{o.StationName}' not found"); return; }
        CelestialBody? sBody = world.DominantBody(station.Position);
        if (sBody == null) { Fail(ai, now, "station has no dominant body"); return; }
        double rs = (station.Position - sBody.Position).Length;

        switch (o.Phase)
        {
            case 0:
            {
                CelestialBody? vBody = world.DominantBody(v.Position);
                o.Phase = ReferenceEquals(vBody, sBody) ? 20 : 10;
                break;
            }
            case 10:   // interplanetary leg: torch hop to a standoff point near the station's body
            {
                double accel = Brachistochrone.SustainableAccel(ship);
                if (accel <= 1e-4) { Fail(ai, now, "radiators cannot sustain the drive"); return; }
                double dist = (sBody.Position - v.Position).Length;
                double tEst = 2.0 * Math.Sqrt(Math.Max(dist, 1.0) / accel);
                Vec3 bodyAtArrival = sBody.Position + sBody.Velocity * tEst;   // lead term (zero for static bodies)
                Vec3 away = v.Position - bodyAtArrival;
                Vec3 awayDir = away.Length > 1.0 ? away.Normalized() : Vec3.UnitX;
                Vec3 dest = bodyAtArrival + awayDir * Math.Max(3.0 * rs, 2.0 * sBody.Radius + 1.0e7);
                flip = FlipBurn.ToPoint(v, dest, accel);
                ai.Report(now, AiSeverity.Info,
                          o.Hops == 0 ? $"Order: dock at {station.Name} - torch leg to {sBody.Name}."
                                      : $"Course correction hop {o.Hops} toward {sBody.Name}.",
                          $"Flip-and-burn at {accel / MathConstants.StandardGravity:F2} g (heat-sustainable).");
                o.Phase = 11;
                break;
            }
            case 11:
            {
                if (flip == null) { Fail(ai, now, "torch autopilot stood down"); return; }
                if (flip.Arrived)
                {
                    flip = null;
                    double miss = (v.Position - sBody.Position).Length;
                    double captureRange = Math.Max(12.0 * rs, 1.5e8);
                    if (miss > captureRange)
                    {
                        if (++o.Hops >= 4) { Fail(ai, now, $"could not close on {sBody.Name} after {o.Hops} hops"); return; }
                        o.Phase = 10;
                    }
                    else o.Phase = 12;
                    break;
                }
                if (!flip.Active) { Fail(ai, now, $"torch autopilot aborted ({flip.Phase})"); return; }
                Status = $"Dock at {station.Name}: torch leg - {flip.Phase}";
                break;
            }
            case 12:   // orbit insertion: feedback-circularize at the current radius
            {
                if (BurnAuthority(ship, ai) is string why)
                { ship.Mode = ThrustMode.None; Fail(ai, now, why); return; }
                Vec3 rRel = v.Position - sBody.Position;
                Vec3 vRel = v.Velocity - sBody.Velocity;
                double rm = rRel.Length;
                Vec3 rhat = rRel / rm;
                Vec3 vh = vRel - rhat * Vec3.Dot(vRel, rhat);
                Vec3 hdir = vh.Length > 1e-3 ? vh.Normalized() : PerpTo(rhat);
                Vec3 vDes = hdir * Math.Sqrt(sBody.Mu / rm);
                Vec3 dvec = vDes - vRel;
                double res = dvec.Length;
                if (res < 5.0)
                {
                    ship.Mode = ThrustMode.None;
                    ai.Report(now, AiSeverity.Info,
                              $"Orbit established at {sBody.Name} ({(rm - sBody.Radius) / 1000.0:N0} km).",
                              "Proceeding to rendezvous with the station.");
                    o.Phase = 20;
                    break;
                }
                CommandBurn(ship, dvec, _dt);
                Status = $"Orbit insertion at {sBody.Name}: residual {res:N0} m/s";
                break;
            }
            case >= 20 and <= 27:   // in-system rendezvous chain to the station
            {
                ChainState st = RendezvousChain(ship, sBody, v, station, ref node, ai, now, o, 20);
                if (Current == null) return;
                if (st == ChainState.Matched) { ship.Mode = ThrustMode.None; o.Phase = 50; }
                else if (st == ChainState.WidePass)
                {
                    if (++o.Retries > 2) { Fail(ai, now, "could not close after phasing retries"); return; }
                    ai.Report(now, AiSeverity.Caution,
                              $"Wide pass ({o.Ca.MinRange / 1000.0:N0} km) - executing a phasing orbit.",
                              "Dropping 7% below the station's altitude to drift into a window.");
                    o.Phase = 40;
                }
                break;
            }
            case 40:   // phasing step-aside: Hohmann down to 93% of the station radius
            {
                var h = HohmannTransfer.Compute(sBody.Mu, (v.Position - sBody.Position).Length, 0.93 * rs);
                node = new ManeuverNode { NodeTime = now + 30.0, Prograde = h.DeltaV1, Armed = true };
                o.Phase = 41;
                break;
            }
            case 41:
                if (WaitNode(ref node, ai, now, "Phasing descent")) o.Phase = 42;
                break;
            case 42:
            {
                Vec3 rNow = v.Position - sBody.Position, vNow = v.Velocity - sBody.Velocity;
                if (!PlanApsisBurnTo(rNow, vNow, sBody.Mu, 0.93 * rs, now, out ManeuverNode n))
                { Fail(ai, now, "could not plan phasing circularization"); return; }
                node = n;
                o.Phase = 43;
                break;
            }
            case 43:
                if (WaitNode(ref node, ai, now, "Phasing circularization")) { o.Phase = 20; o.CaCooldown = 0; }
                break;
            case 50:   // terminal approach: proportional closure onto the station
            {
                if (BurnAuthority(ship, ai) is string why)
                { ship.Mode = ThrustMode.None; Fail(ai, now, why); return; }
                Vec3 toStation = station.Position - v.Position;
                double range = toStation.Length;
                Vec3 relVel = v.Velocity - station.Velocity;
                if (range < 600.0 && relVel.Length < 2.0)
                {
                    ship.Mode = ThrustMode.None;
                    ai.Report(now, AiSeverity.Info, $"Docked at {station.Name}.", "Beginning propellant transfer.");
                    o.Phase = 51;
                    break;
                }
                double tau = Math.Max(120.0, 6.0 * _dt);
                double vClose = Math.Min(range / tau, 200.0);
                Vec3 vDes = station.Velocity + (range > 1.0 ? toStation / range : Vec3.Zero) * vClose;
                Vec3 dvec = vDes - v.Velocity;
                if (dvec.Length < 0.5) ship.Mode = ThrustMode.None;   // on profile - coast
                else CommandBurn(ship, dvec, _dt);
                Status = $"Final approach to {station.Name}: {range / 1000.0:N1} km, closing {vClose:N1} m/s";
                break;
            }
            case 51:   // docked: gradual propellant transfer
            {
                ship.Mode = ThrustMode.None;
                double cap = ship.PropellantCapacity;
                if (cap <= 0.0 || ship.PropellantMass >= cap - 1.0)
                {
                    Complete(ai, now, $"Replenished at {station.Name} - tanks full, holding station.");
                    return;
                }
                ship.AddPropellant(cap / 45.0 * Math.Max(_dt, 0.0));
                Status = $"Docked at {station.Name} - transferring propellant ({ship.PropellantMass / cap:P0})";
                break;
            }
            default:
                Fail(ai, now, $"unknown dock phase {o.Phase}");
                break;
        }
    }

    /// <summary>Find a dockable station by name anywhere in the vessel list.</summary>
    private static RigidBody? FindStation(PhysicsWorld world, string? name)
    {
        foreach (RigidBody rb in world.Vessels)
            if (rb.IsStation && rb.Name == name) return rb;
        return null;
    }

    /// <summary>Why the AI may not command a direct burn right now (null = clear to burn).</summary>
    private static string? BurnAuthority(Spacecraft ship, ShipAI ai)
    {
        if (ship.PropellantMass <= 1e-6) return "propellant exhausted";
        double cap = ship.PropellantCapacity;
        if (ai.Enabled && cap > 0.0 && ship.PropellantMass <= cap * ai.PropellantReserveFrac)
            return "propellant at reserve - safeguards hold";
        return null;
    }

    /// <summary>Command an inertial burn that nulls <paramref name="dvec"/>; gains scale with the
    /// frame's sim step so the controller stays stable under heavy time warp.</summary>
    private static void CommandBurn(Spacecraft ship, Vec3 dvec, double dt)
    {
        double needed = dvec.Length / Math.Max(4.0, 2.0 * dt);
        double th = ship.TotalThrustVac > 0.0
            ? Math.Clamp(ship.TotalMass * needed / ship.TotalThrustVac, 0.0, 1.0) : 0.0;
        if (th < 1e-4) { ship.Mode = ThrustMode.None; return; }   // a throttle floor here
        ship.Mode = ThrustMode.Inertial;                          // limit-cycles under warp:
        ship.BurnDirectionWorld = dvec.Normalized();              // min dv/frame would exceed
        ship.Throttle = th;                                       // the convergence gates
    }

    /// <summary>Any unit vector perpendicular to <paramref name="d"/>.</summary>
    private static Vec3 PerpTo(Vec3 d)
    {
        Vec3 p = Vec3.Cross(d, Vec3.UnitZ);
        return p.Length > 1e-6 ? p.Normalized() : Vec3.Cross(d, Vec3.UnitX).Normalized();
    }

    private void UpdateTorch(Spacecraft ship, PhysicsWorld world, RigidBody v,
                             ref FlipBurnPlan? flip, ShipAI ai, double now, ShipOrder o)
    {
        if (o.Phase == 0)
        {
            CelestialBody? body = null;
            foreach (CelestialBody b in world.Bodies)
                if (b.Name == o.BodyName) { body = b; break; }
            if (body == null) { Fail(ai, now, $"destination '{o.BodyName}' not in this scenario"); return; }
            double accel = Brachistochrone.SustainableAccel(ship);
            if (accel <= 1e-4) { Fail(ai, now, "radiators cannot sustain the drive"); return; }
            flip = FlipBurn.ToPoint(v, body.Position, accel);
            ai.Report(now, AiSeverity.Info, $"Order: torch transfer to {body.Name}.",
                      $"Flip-and-burn at {accel / MathConstants.StandardGravity:F2} g (heat-sustainable).");
            o.Phase = 1;
        }
        else
        {
            if (flip == null) { Fail(ai, now, "torch autopilot stood down"); return; }
            if (flip.Arrived)
            {
                flip = null;
                Complete(ai, now, $"Arrived in the vicinity of {o.BodyName}.");
                return;
            }
            if (!flip.Active) { Fail(ai, now, $"torch autopilot aborted ({flip.Phase})"); return; }
            Status = $"Torch transfer to {o.BodyName}: {flip.Phase}";
        }
    }

    // ----- shared helpers ------------------------------------------------------------------

    /// <summary>Wait for the current node to complete; updates Status; false while waiting.</summary>
    private bool WaitNode(ref ManeuverNode? node, ShipAI ai, double now, string what)
    {
        if (node == null) { Fail(ai, now, $"{what}: maneuver node lost"); return false; }
        if (node.Completed) { node = null; return true; }
        if (!node.Armed && !node.Burning)
        { Fail(ai, now, $"{what}: autopilot stood down (safeguards or manual abort)"); return false; }
        Status = node.Burning
            ? $"{what}: burning - {node.DeliveredDv:N0} / {node.Magnitude:N0} m/s"
            : $"{what}: burn {node.Magnitude:N0} m/s in T-{Fmt(node.NodeTime - now)} (warp to node)";
        return false;
    }

    private void Complete(ShipAI ai, double now, string text)
    {
        ai.Report(now, AiSeverity.Info, $"Order complete: {text}");
        Status = text;
        Current = null;
    }

    private void Fail(ShipAI ai, double now, string reason)
    {
        ai.Report(now, AiSeverity.Caution, $"Order aborted: {Current?.Label} - {reason}.");
        Status = $"Order aborted ({reason})";
        Current = null;
    }

    /// <summary>Semi-major axis and eccentricity of the relative orbit; false if not bound.</summary>
    private static bool Elements(Vec3 r, Vec3 v, double mu, out double a, out double e)
    {
        a = 0; e = 0;
        double rm = r.Length;
        double energy = v.LengthSquared / 2.0 - mu / rm;
        if (energy >= 0.0 || rm <= 0.0) return false;
        a = -mu / (2.0 * energy);
        Vec3 h = Vec3.Cross(r, v);
        Vec3 evec = Vec3.Cross(v, h) / mu - r / rm;
        e = evec.Length;
        return true;
    }

    /// <summary>
    /// Time until the next apoapsis (or periapsis) passage, via true -&gt; eccentric -&gt; mean anomaly.
    /// Mirror-validated against numeric Kepler propagation.
    /// </summary>
    private static bool TimeToApsis(Vec3 r, Vec3 v, double mu, bool apoapsis, out double t, out double rApsis)
    {
        t = 0; rApsis = 0;
        double rm = r.Length;
        double energy = v.LengthSquared / 2.0 - mu / rm;
        if (energy >= 0.0) return false;
        double a = -mu / (2.0 * energy);
        Vec3 h = Vec3.Cross(r, v);
        Vec3 evec = Vec3.Cross(v, h) / mu - r / rm;
        double e = evec.Length;
        rApsis = apoapsis ? a * (1.0 + e) : a * (1.0 - e);
        if (e < 1e-6) { t = 0.0; return true; }              // circular: any time
        double n = Math.Sqrt(mu / (a * a * a));
        double cosNu = Math.Clamp(Vec3.Dot(evec, r) / (e * rm), -1.0, 1.0);
        double nu = Math.Acos(cosNu);
        if (Vec3.Dot(r, v) < 0.0) nu = MathConstants.TwoPi - nu;
        double E = 2.0 * Math.Atan2(Math.Sqrt(1.0 - e) * Math.Sin(nu / 2.0),
                                    Math.Sqrt(1.0 + e) * Math.Cos(nu / 2.0));
        double M = E - e * Math.Sin(E);
        if (M < 0.0) M += MathConstants.TwoPi;
        double mTarget = apoapsis ? Math.PI : MathConstants.TwoPi;
        double dM = mTarget - M;
        while (dM <= 1e-9) dM += MathConstants.TwoPi;
        t = dM / n;
        return true;
    }

    /// <summary>Plan a prograde circularization burn at the next apoapsis.</summary>
    private static bool PlanCircularizeNode(Vec3 r, Vec3 v, double mu, double now,
                                            out ManeuverNode node, out double rApsis)
    {
        node = null!;
        if (!TimeToApsis(r, v, mu, apoapsis: true, out double t, out rApsis)) return false;
        double energy = v.LengthSquared / 2.0 - mu / r.Length;
        double a = -mu / (2.0 * energy);
        double vAt = Math.Sqrt(mu * (2.0 / rApsis - 1.0 / a));
        double dv = Math.Sqrt(mu / rApsis) - vAt;
        node = new ManeuverNode { NodeTime = now + t, Prograde = dv, Armed = true };
        return true;
    }

    /// <summary>Plan the circularization burn at whichever apsis of the current orbit matches r2.</summary>
    private static bool PlanApsisBurnTo(Vec3 r, Vec3 v, double mu, double r2, double now, out ManeuverNode node)
    {
        node = null!;
        if (!Elements(r, v, mu, out double a, out double e)) return false;
        double rApo = a * (1.0 + e), rPeri = a * (1.0 - e);
        bool atApo = Math.Abs(rApo - r2) <= Math.Abs(rPeri - r2);
        if (!TimeToApsis(r, v, mu, atApo, out double t, out double rAt)) return false;
        double vAt = Math.Sqrt(mu * (2.0 / rAt - 1.0 / a));
        double dv = Math.Sqrt(mu / rAt) - vAt;     // negative at periapsis (slow down), positive at apoapsis
        node = new ManeuverNode { NodeTime = now + t, Prograde = dv, Armed = true };
        return true;
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0.0) seconds = 0.0;
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1.0) return $"{(int)ts.TotalDays}d {ts.Hours:00}:{ts.Minutes:00}";
        if (ts.TotalHours >= 1.0) return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        return $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
