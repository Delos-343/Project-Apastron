using System;
using System.Numerics;
using ImGuiNET;
using Apastron.Physics;
using Apastron.Simulation;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Plan and execute a maneuver: anchor a node ahead in time, dial prograde/normal/radial
/// delta-v, and watch the resulting orbit (yellow) update live. A Hohmann helper sizes a
/// two-burn transfer to a target circular orbit and can seed the departure node.
/// </summary>
public static class ManeuverPanel
{
    private static float _targetKm = 35786.0f;   // GEO altitude by default

    public static void Body(GameContext ctx)
    {
        RigidBody? vessel = ctx.World.PrimaryVessel;
        CelestialBody? primary = vessel != null ? ctx.World.DominantBody(vessel.Position) : null;
        if (vessel == null || primary == null)
        {
            Ui.TextDisabled("No vessel or dominant body.");
            return;
        }

        OrbitalElements oe = OrbitalElements.Compute(
            vessel.Position, vessel.Velocity, primary.Position, primary.Mu, primary.Radius);
        double period = oe.IsBound ? oe.Period : 0.0;

        DrawNodeSection(ctx, primary, period);
        DrawHohmannSection(ctx, primary, oe, period);
    }

    private static void DrawNodeSection(GameContext ctx, CelestialBody primary, double period)
    {
        Section("Maneuver node");

        if (ctx.Node == null || ctx.Node.Completed)
        {
            if (ctx.Node is { Completed: true })
            {
                Ui.TextDisabled("Burn complete.");
                if (ImGui.Button("Clear")) ctx.Node = null;
                ImGui.SameLine();
            }
            if (ImGui.Button("Create node"))
            {
                double seedLead = period > 0.0 ? period * 0.25 : 300.0;
                ctx.Node = new ManeuverNode { NodeTime = ctx.World.SimTime + seedLead };
            }
            return;
        }

        ManeuverNode node = ctx.Node;

        float lead = (float)Math.Max(node.NodeTime - ctx.World.SimTime, 0.0);
        float leadMax = (float)(period > 0.0 ? period : 6000.0);
        if (ImGui.SliderFloat("Lead time", ref lead, 0.0f, leadMax, "%.0f s"))
            node.NodeTime = ctx.World.SimTime + lead;

        if (ImGui.Button("+30 s")) node.NodeTime += 30.0;
        ImGui.SameLine();
        if (ImGui.Button("+1 orbit") && period > 0.0) node.NodeTime += period;
        ImGui.SameLine();
        if (ImGui.Button("Now")) node.NodeTime = ctx.World.SimTime;

        ImGui.Spacing();
        float pg = (float)node.Prograde, nm = (float)node.Normal, rd = (float)node.Radial;
        if (ImGui.SliderFloat("Prograde", ref pg, -2000.0f, 2000.0f, "%.0f m/s")) node.Prograde = pg;
        if (ImGui.SliderFloat("Normal",   ref nm, -2000.0f, 2000.0f, "%.0f m/s")) node.Normal = nm;
        if (ImGui.SliderFloat("Radial",   ref rd, -2000.0f, 2000.0f, "%.0f m/s")) node.Radial = rd;
        if (ImGui.Button("Zero delta-v")) { node.Prograde = node.Normal = node.Radial = 0.0; }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Ui.Text($"Total delta-v   {node.Magnitude:N0} m/s");
        Ui.Text($"Time to node    {Duration(node.NodeTime - ctx.World.SimTime)}");

        double shipDv = ctx.Ship.DeltaV;
        if (node.Magnitude > shipDv)
            Ui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), $"Exceeds vessel delta-v ({shipDv:N0} m/s)");
        else
            Ui.TextDisabled($"Vessel delta-v {shipDv:N0} m/s");

        ManeuverPreview prev = Maneuver.BuildPreview(ctx.World, node);
        if (prev.HasPost)
        {
            ImGui.Spacing();
            Ui.TextDisabled("Resulting orbit");
            Ui.Text($"  Apoapsis   {Altitude(prev.Post.Apoapsis, primary)}");
            Ui.Text($"  Periapsis  {Altitude(prev.Post.Periapsis, primary)}");
            Ui.Text($"  Period     {Duration(prev.Post.Period)}");
            Ui.Text($"  Ecc        {prev.Post.Eccentricity:0.0000}");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!node.Armed)
        {
            if (ImGui.Button("Execute burn")) node.Armed = true;
        }
        else
        {
            if (node.Burning)
                Ui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), "BURNING");
            else
                Ui.TextColored(new Vector4(0.95f, 0.85f, 0.4f, 1.0f), "Armed - warp to the node");
            Ui.Text($"Delivered {node.DeliveredDv:N0} / {node.Magnitude:N0} m/s");
            if (ImGui.Button("Abort"))
            {
                node.Armed = false; node.Burning = false; ctx.Ship.Mode = ThrustMode.None;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete node")) ctx.Node = null;
    }

    private static void DrawHohmannSection(GameContext ctx, CelestialBody primary, OrbitalElements oe, double period)
    {
        Section("Hohmann transfer");

        ImGui.SetNextItemWidth(180.0f);
        ImGui.SliderFloat("Target altitude", ref _targetKm, 100.0f, 400000.0f, "%.0f km");

        double r1 = oe.RadialDistance;
        double r2 = primary.Radius + _targetKm * 1000.0;
        HohmannTransfer h = HohmannTransfer.Compute(primary.Mu, r1, r2);

        Ui.Text($"Depart (dv1)  {h.DeltaV1:N1} m/s");
        Ui.Text($"Arrive (dv2)  {h.DeltaV2:N1} m/s");
        Ui.Text($"Total         {h.TotalDeltaV:N1} m/s");
        Ui.Text($"Transfer time {Duration(h.TransferTime)}");
        Ui.Text($"Phase angle   {h.PhaseAngleDeg:F1} deg");

        if (ImGui.Button("Plan departure burn"))
        {
            double lead = period > 0.0 ? period * 0.1 : 120.0;
            ctx.Node = new ManeuverNode { NodeTime = ctx.World.SimTime + lead, Prograde = h.DeltaV1 };
        }
    }

    private static string Altitude(double radius, CelestialBody b)
    {
        if (double.IsNaN(radius) || double.IsInfinity(radius)) return "--";
        double alt = radius - b.Radius;
        return alt >= 1.0e6 ? $"{alt / 1000.0:N0} km" : $"{alt / 1000.0:0.0} km";
    }

    private static string Duration(double seconds)
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
