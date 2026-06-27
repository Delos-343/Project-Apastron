using System;
using System.Numerics;
using ImGuiNET;
using Apastron.Physics;
using Apastron.Render;
using Apastron.Simulation;

namespace Apastron.App;

/// <summary>
/// Rendezvous planning against a live target vessel: spawn a station, read the relative state
/// in the target's RTN frame, the phase angle and transfer-window timing, and the predicted
/// closest approach. A button seeds a Hohmann departure node for the Maneuver Planner.
/// </summary>
public static class RendezvousPanel
{
    private static float _spawnAltKm = 600.0f;
    private static float _spawnPhaseDeg = 40.0f;
    private static float _spinRadiusM = 100.0f;
    private static float _spinRpm = 1.64f;     // ~0.3 g at 100 m

    public static void Body(GameContext ctx)
    {
        RigidBody? vessel = ctx.World.PrimaryVessel;
        CelestialBody? primary = vessel != null ? ctx.World.DominantBody(vessel.Position) : null;
        if (vessel == null || primary == null)
        {
            Ui.TextDisabled("No vessel or dominant body.");
            return;
        }

        RigidBody? target = ctx.World.TargetVessel;

        Section("Target");
        if (target == null)
        {
            ImGui.SetNextItemWidth(180.0f);
            ImGui.SliderFloat("Spawn altitude", ref _spawnAltKm, 150.0f, 2000.0f, "%.0f km");
            ImGui.SetNextItemWidth(180.0f);
            ImGui.SliderFloat("Phase lead", ref _spawnPhaseDeg, -180.0f, 180.0f, "%.0f deg");
            ImGui.SetNextItemWidth(180.0f);
            ImGui.SliderFloat("Ring radius", ref _spinRadiusM, 20.0f, 400.0f, "%.0f m");
            ImGui.SetNextItemWidth(180.0f);
            ImGui.SliderFloat("Spin rate", ref _spinRpm, 0.0f, 6.0f, "%.2f rpm");
            Ui.TextDisabled($"artificial gravity {SpinGravity(_spinRadiusM, _spinRpm) / 9.80665:F2} g");
            if (ImGui.Button("Spawn spin station"))
            {
                RigidBody t = Scenarios.CreateTarget(primary, _spawnAltKm * 1000.0, _spawnPhaseDeg);
                t.SpinRadius = _spinRadiusM;
                t.SpinRpm = _spinRpm;
                ctx.World.SetTarget(t);
            }
            Ui.TextDisabled("Circular, co-planar orbit; counter-rotating ring nulls net spin.");
            return;
        }

        Ui.Text($"Target: {target.Name}");
        if (ImGui.Button("Remove target"))
        {
            ctx.World.ClearTarget();
            if (ctx.View.Focus == CameraFocus.Target) ctx.View.Focus = CameraFocus.Vessel;
            return;
        }
        ImGui.SameLine();
        if (ImGui.Button("Focus camera")) ctx.View.Focus = CameraFocus.Target;

        if (target.SpinRadius > 0.0)
        {
            Section("Spin habitat");
            float r = (float)target.SpinRadius, rpm = (float)target.SpinRpm;
            ImGui.SetNextItemWidth(180.0f);
            if (ImGui.SliderFloat("Ring radius##live", ref r, 20.0f, 400.0f, "%.0f m")) target.SpinRadius = r;
            ImGui.SetNextItemWidth(180.0f);
            if (ImGui.SliderFloat("Spin rate##live", ref rpm, 0.0f, 6.0f, "%.2f rpm")) target.SpinRpm = rpm;
            double g = SpinGravity(target.SpinRadius, target.SpinRpm);
            double rim = target.SpinRpm * Math.PI / 30.0 * target.SpinRadius;
            Ui.Text($"Artificial gravity {g:F2} m/s\u00b2  ({g / 9.80665:F2} g)");
            Ui.Text($"Rim speed          {rim:F1} m/s");
            if (target.SpinRpm > 2.0)
                Ui.TextColored(new Vector4(0.95f, 0.85f, 0.4f, 1.0f), "Above ~2 rpm crew may feel motion sickness.");
        }

        RelativeState rel = Rendezvous.Relative(vessel, target, primary);

        Section("Relative state (target RTN)");
        Ui.Text($"Range        {Dist(rel.Range)}");
        Ui.Text($"Range rate   {Signed(rel.RangeRate, 1)} m/s ({(rel.RangeRate < 0 ? "closing" : "opening")})");
        Ui.Text($"Rel speed    {rel.RelSpeed:N1} m/s");
        Ui.Text($"  radial     {Signed(rel.Radial, 0)} m");
        Ui.Text($"  along-trk  {Signed(rel.AlongTrack, 0)} m");
        Ui.Text($"  cross-trk  {Signed(rel.CrossTrack, 0)} m");
        Ui.Text($"Phase angle  {Signed(rel.PhaseDeg, 1)} deg");

        OrbitalElements oeA = OrbitalElements.Compute(vessel.Position, vessel.Velocity, primary.Position, primary.Mu, primary.Radius);
        OrbitalElements oeT = OrbitalElements.Compute(target.Position, target.Velocity, primary.Position, primary.Mu, primary.Radius);
        double ta = oeA.IsBound ? oeA.Period : 0.0;
        double tt = oeT.IsBound ? oeT.Period : 0.0;
        HohmannTransfer hoh = HohmannTransfer.Compute(primary.Mu, oeA.RadialDistance, oeT.RadialDistance);

        Section("Phasing");
        Ui.Text($"Vessel period {Dur(ta)}");
        Ui.Text($"Target period {Dur(tt)}");
        Ui.Text($"Synodic       {Dur(Rendezvous.SynodicPeriod(ta, tt))}");
        Ui.Text($"Hohmann phase {hoh.PhaseAngleDeg:F1} deg (target lead)");
        Ui.Text($"Time to window {Dur(Rendezvous.TimeToTransferWindow(rel.PhaseDeg, hoh.PhaseAngleDeg, ta, tt))}");

        Section("Closest approach (coasting)");
        double horizon = ta > 0.0 ? ta * 3.0 : 6.0 * 3600.0;
        ClosestApproach ca = Rendezvous.FindClosestApproach(vessel, target, primary, horizon);
        if (ca.Found)
        {
            Ui.Text($"Min range     {Dist(ca.MinRange)}");
            Ui.Text($"Time to CA    {Dur(ca.TimeToCA)}");
            Ui.TextColored(new Vector4(0.55f, 0.85f, 1.0f, 1.0f), $"Match delta-v {ca.RelSpeedAtCA:N1} m/s");
            Ui.TextDisabled($"over next {Dur(horizon)} (assumes no thrust)");
        }
        else
        {
            Ui.TextDisabled("Unbounded orbit - no closest approach.");
        }

        Section("Plan");
        if (ImGui.Button("Plan Hohmann to target"))
        {
            double lead = ta > 0.0 ? ta * 0.1 : 120.0;
            ctx.Node = new ManeuverNode { NodeTime = ctx.World.SimTime + lead, Prograde = hoh.DeltaV1 };
        }
        Ui.TextDisabled("Seeds a departure-burn node; refine in the Maneuver Planner.");
    }

    private static string Signed(double v, int decimals)
    {
        string f = "N" + decimals;
        return (v >= 0.0 ? "+" : "") + v.ToString(f);
    }

    /// <summary>Artificial gravity a = w^2 r for a ring of radius (m) spinning at rpm.</summary>
    private static double SpinGravity(double radiusM, double rpm)
    {
        double w = rpm * Math.PI / 30.0;   // rpm -> rad/s
        return w * w * radiusM;
    }

    private static string Dist(double m)
    {
        if (double.IsNaN(m) || double.IsInfinity(m)) return "--";
        return Math.Abs(m) >= 1000.0 ? $"{m / 1000.0:N1} km" : $"{m:N0} m";
    }

    private static string Dur(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "--";
        string sign = seconds < 0 ? "-" : "";
        long s = (long)Math.Abs(seconds);
        long d = s / 86400; s %= 86400;
        long h = s / 3600; s %= 3600;
        long m = s / 60; s %= 60;
        if (d > 0) return $"{sign}{d}d {h:00}:{m:00}:{s:00}";
        return $"{sign}{h:00}:{m:00}:{s:00}";
    }

    private static void Section(string label)
    {
        ImGui.Spacing();
        Ui.TextDisabled(label);
        ImGui.Separator();
    }
}
