using System;
using System.Numerics;
using ImGuiNET;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Simulation;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Flip-and-burn transfer planner for the fusion-torch tier. Pick a distance and an
/// acceleration and see the transit time, peak velocity, delta-v, mass ratio and reaction
/// mass — and whether the ship has the propellant and the radiators to sustain it. Makes the
/// reaction-mass / transit-time / heat tradeoff explicit.
/// </summary>
public static class BrachistochronePanel
{
    private const double AU = 1.495978707e11;
    private static readonly double G0 = MathConstants.StandardGravity;

    private static float _distanceAu = 1.5f;   // ~Earth-Mars
    private static float _accelG = 0.1f;

    public static void Body(GameContext ctx)
    {
        Spacecraft ship = ctx.Ship;

        if (ship.TotalThrustVac <= 0.0)
        {
            Ui.TextWrapped("No engine installed. Fit a Fusion Torch or Epstein-Type Torch in the Spacecraft Builder.");
            return;
        }

        double aSus = Brachistochrone.SustainableAccel(ship);

        Section("Vessel");
        Ui.Text($"Total mass     {ship.TotalMass / 1000.0:N1} t");
        Ui.Text($"Thrust (vac)   {ship.TotalThrustVac / 1.0e6:N2} MN");
        Ui.Text($"Exhaust vel    {ship.ExhaustVelocity / 1000.0:N1} km/s  (Isp {ship.EffectiveIsp:N0} s)");
        Ui.Text($"Reaction mass  {ship.PropellantMass / 1000.0:N1} t");
        double stt = ship.SustainableThrottle;
        Vector4 stCol = stt >= 0.999 ? Green : stt > 0.0 ? Amber : Red;
        Ui.TextColored(stCol, $"Sustainable throttle {stt * 100.0:F0}%  ->  {aSus / G0:F3} g");
        Ui.TextDisabled($"drive heat {Power(ship.EngineHeatFull)} full  |  rejection {Power(ship.HeatRejectionCapacity)}");

        Section("Transfer");
        ImGui.SetNextItemWidth(200.0f);
        ImGui.SliderFloat("Distance", ref _distanceAu, 0.05f, 5.0f, "%.2f AU");
        ImGui.SameLine();
        Ui.TextDisabled($"{_distanceAu * AU / 1.0e9:N0} Gm");

        ImGui.SetNextItemWidth(200.0f);
        ImGui.SliderFloat("Acceleration", ref _accelG, 0.005f, 1.0f, "%.3f g");
        ImGui.SameLine();
        if (ImGui.SmallButton("Max sustainable")) _accelG = (float)Math.Max(aSus / G0, 0.005);

        double distance = _distanceAu * AU;
        double accel = _accelG * G0;
        BrachistochroneResult r = Brachistochrone.Plan(ship, distance, accel);

        Section("Profile (accelerate - flip - decelerate)");
        Ui.Text($"Transit time   {Duration(r.TransitTime)}");
        Ui.Text($"Peak velocity  {r.PeakVelocity / 1000.0:N1} km/s  (at flip)");
        Ui.Text($"Delta-v        {r.DeltaV / 1000.0:N1} km/s");

        Vector4 mrCol = r.MassRatio > 20.0 ? Red : r.MassRatio > 6.0 ? Amber : Green;
        Ui.TextColored(mrCol, $"Mass ratio     {r.MassRatio:N2}");
        Vector4 rmCol = r.PropellantOK ? Green : Red;
        Ui.TextColored(rmCol, $"Reaction mass  {r.ReactionMass / 1000.0:N1} t  /  {ship.PropellantMass / 1000.0:N1} t aboard");

        ImGui.Spacing();
        if (!r.PropellantOK)
            Ui.TextColored(Red, "Not enough reaction mass - lower the acceleration or add tanks.");
        if (!r.ThermalOK)
            Ui.TextColored(Amber, $"Exceeds radiator-sustainable accel ({aSus / G0:F3} g) - the torch would overheat.");
        if (r.PropellantOK && r.ThermalOK)
            Ui.TextColored(Green, "Feasible: propellant and radiators both support this burn.");

        Section("Flight (in-world flip-and-burn)");
        RigidBody? vessel = ctx.World.PrimaryVessel;
        if (vessel == null)
        {
            Ui.TextDisabled("No vessel.");
        }
        else if (ctx.FlipPlan is { Active: true } fp)
        {
            Ui.Text($"Phase          {fp.Phase}");
            Ui.Text($"Distance to go {fp.DistanceRemaining / AU:N3} AU");
            Ui.Text($"Closing speed  {fp.ClosingSpeed / 1000.0:N1} km/s");
            if (ImGui.Button("Abort burn"))
            {
                ctx.FlipPlan = null;
                ship.Throttle = 0.0;
                ship.Mode = ThrustMode.None;
            }
        }
        else
        {
            CelestialBody? near = ctx.World.DominantBody(vessel.Position);
            double shipDv = ship.DeltaV;
            bool any = false;
            foreach (CelestialBody b in ctx.World.Bodies)
            {
                if (near != null && ReferenceEquals(b, near)) continue;   // don't fly into what you orbit
                any = true;
                double d0 = (b.Position - vessel.Position).Length;
                double dvNeed = 2.0 * Math.Sqrt(accel * d0);
                bool feasible = ctx.GodMode
                    ? d0 > 0.0
                    : accel <= aSus * 1.001 && dvNeed <= shipDv && d0 > 0.0;
                if (!feasible) ImGui.BeginDisabled();
                if (ImGui.Button($"Fly to {b.Name}"))
                    ctx.FlipPlan = FlipBurn.ToPoint(vessel, b.Position, accel);
                if (!feasible) ImGui.EndDisabled();
                ImGui.SameLine();
                Ui.TextDisabled($"{d0 / AU:N2} AU   need {dvNeed / 1000.0:N0} km/s");
            }
            if (!any) Ui.TextDisabled("No destination bodies (single-body scenario).");
            Ui.TextDisabled($"Flies at the acceleration above; ship delta-v {shipDv / 1000.0:N0} km/s.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        Ui.TextDisabled("Higher acceleration cuts transit time but the delta-v (a*t) and");
        Ui.TextDisabled("mass ratio climb fast. The hull flips at the changeover; arrival is");
        Ui.TextDisabled("in the destination's vicinity at low relative speed.");
    }

    private static readonly Vector4 Green = new(0.5f, 0.85f, 0.55f, 1.0f);
    private static readonly Vector4 Amber = new(0.95f, 0.85f, 0.4f, 1.0f);
    private static readonly Vector4 Red = new(1.0f, 0.45f, 0.35f, 1.0f);

    private static string Power(double watts)
    {
        if (watts >= 1.0e9) return $"{watts / 1.0e9:N1} GW";
        if (watts >= 1.0e6) return $"{watts / 1.0e6:N1} MW";
        if (watts >= 1.0e3) return $"{watts / 1.0e3:N0} kW";
        return $"{watts:N0} W";
    }

    private static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0) return "--";
        long s = (long)seconds;
        long d = s / 86400; s %= 86400;
        long h = s / 3600; s %= 3600;
        long m = s / 60;
        if (d > 0) return $"{d}d {h:00}h {m:00}m";
        if (h > 0) return $"{h}h {m:00}m";
        return $"{m}m";
    }

    private static void Section(string label)
    {
        ImGui.Spacing();
        Ui.TextDisabled(label);
        ImGui.Separator();
    }
}
