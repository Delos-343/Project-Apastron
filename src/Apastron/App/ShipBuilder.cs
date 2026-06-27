using System;
using System.Numerics;
using ImGuiNET;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// The spacecraft builder: add/remove parts from a catalogue, set tank fill, and read the
/// derived performance (mass, centre of mass, thrust, Isp, delta-v, TWR, thermal balance).
/// Edits apply live — the active vessel's mass tracks the design via the Propulsion bridge.
/// </summary>
public static class ShipBuilder
{
    public static void Body(GameContext ctx)
    {
        Spacecraft ship = ctx.Ship;

        // ---- catalogue ----
        Section("Add parts");
        var templates = PartCatalog.Templates;
        float full = ImGui.GetContentRegionAvail().X;
        float colW = (full - ImGui.GetStyle().ItemSpacing.X) / 2.0f;
        for (int i = 0; i < templates.Count; i++)
        {
            if (ImGui.Button($"+ {templates[i].Name}", new Vector2(colW, 0)))
                ship.Add(templates[i].Clone());
            if (i % 2 == 0 && i < templates.Count - 1) ImGui.SameLine();
        }

        // ---- parts list ----
        Section($"Parts ({ship.Parts.Count})");
        if (ship.Parts.Count == 0)
        {
            Ui.TextDisabled("No parts. Add from the catalogue above.");
        }
        else
        {
            int removeIdx = -1;
            for (int i = 0; i < ship.Parts.Count; i++)
            {
                Part p = ship.Parts[i];
                ImGui.PushID(i);

                (string tag, Vector4 col) = Tag(p.Category);
                Ui.TextColored(col, tag);
                ImGui.SameLine();
                Ui.Text(p.Name);
                ImGui.SameLine();
                Ui.TextDisabled($"{p.WetMass:N0} kg");

                if (p.IsTank)
                {
                    float fill = (float)p.Propellant;
                    ImGui.SetNextItemWidth(full - 90.0f);
                    if (ImGui.SliderFloat("Fuel", ref fill, 0.0f, (float)p.PropellantCapacity, "%.0f kg"))
                        p.Propellant = fill;
                    ImGui.SameLine();
                }
                if (ImGui.SmallButton("Remove")) removeIdx = i;

                ImGui.PopID();
            }
            if (removeIdx >= 0) ship.RemoveAt(removeIdx);
        }

        // ---- schematic ----
        Section("Layout (base -> tip, white tick = CoM)");
        DrawSchematic(ship);

        // ---- performance ----
        Section("Performance");
        DrawStats(ctx, ship);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Refuel")) ship.Refill();
        ImGui.SameLine();
        if (ImGui.Button("Clear all")) ship.Clear();
    }

    private static void DrawSchematic(Spacecraft ship)
    {
        float availW = ImGui.GetContentRegionAvail().X;
        const float barH = 34.0f;
        Vector2 o = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(availW, barH + 10.0f));

        double total = ship.TotalLength;
        if (total <= 0.0) return;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float x = o.X;
        foreach (Part p in ship.Parts)
        {
            float segW = (float)(availW * (p.Length / total));
            uint col = ImGui.GetColorU32(Tag(p.Category).Item2);
            dl.AddRectFilled(new Vector2(x, o.Y), new Vector2(x + MathF.Max(segW - 1.0f, 1.0f), o.Y + barH), col);
            x += segW;
        }

        float comX = o.X + (float)(availW * (ship.CenterOfMass / total));
        uint tick = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.95f));
        dl.AddLine(new Vector2(comX, o.Y - 3.0f), new Vector2(comX, o.Y + barH + 3.0f), tick, 2.0f);
    }

    private static void DrawStats(GameContext ctx, Spacecraft ship)
    {
        Ui.Text($"Dry mass     {ship.DryMass:N0} kg");
        Ui.Text($"Propellant   {ship.PropellantMass:N0} / {ship.PropellantCapacity:N0} kg");
        Ui.Text($"Total mass   {ship.TotalMass:N0} kg");
        Ui.Text($"Centre of mass  {ship.CenterOfMass:0.00} m  ({Fraction(ship):0.0}% of length)");

        ImGui.Spacing();
        if (ship.TotalThrustVac > 0.0)
        {
            Ui.Text($"Thrust (vac) {ship.TotalThrustVac / 1000.0:N0} kN");
            Ui.Text($"Isp (vac)    {ship.EffectiveIsp:F0} s");
            Ui.Text($"Exhaust vel  {ship.ExhaustVelocity / 1000.0:N1} km/s");
            Ui.Text($"Mass flow    {ship.MassFlowFullThrust:F2} kg/s (full)");
            Ui.TextColored(new Vector4(0.55f, 0.85f, 1.0f, 1.0f), $"Delta-v      {ship.DeltaV:N0} m/s");
            Ui.Text($"Burn time    {ship.BurnTimeFullThrust:F0} s (full throttle)");

            double twrEarth = ship.TwrAt(9.80665);
            Ui.Text($"TWR @ Earth  {twrEarth:F2}");

            double gLocal = LocalGravity(ctx);
            if (gLocal > 0.0)
                Ui.Text($"TWR @ local  {ship.TwrAt(gLocal):F2}  (g={gLocal:F3} m/s\u00b2)");
        }
        else
        {
            Ui.TextDisabled("No engine installed.");
        }

        ImGui.Spacing();
        Ui.Text($"Heat (full)  {Power(ship.HeatLoad)}");
        Ui.Text($"Rejection    {Power(ship.HeatRejectionCapacity)}");
        if (ship.EngineHeatFull > 0.0)
        {
            double st = ship.SustainableThrottle;
            Vector4 col = st >= 0.999f ? new Vector4(0.5f, 0.85f, 0.55f, 1.0f)
                        : st > 0.0f ? new Vector4(0.95f, 0.85f, 0.4f, 1.0f)
                        : new Vector4(1.0f, 0.45f, 0.35f, 1.0f);
            Ui.TextColored(col, $"Sustainable throttle {st * 100.0:F0}% (radiator-limited)");
        }
        else if (ship.HeatLoad > ship.HeatRejectionCapacity)
            Ui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f),
                $"Overheating by {Power(ship.HeatLoad - ship.HeatRejectionCapacity)} - add radiators");
    }

    private static string Power(double watts)
    {
        if (watts >= 1.0e9) return $"{watts / 1.0e9:N1} GW";
        if (watts >= 1.0e6) return $"{watts / 1.0e6:N1} MW";
        if (watts >= 1.0e3) return $"{watts / 1.0e3:N0} kW";
        return $"{watts:N0} W";
    }

    private static float Fraction(Spacecraft ship)
        => ship.TotalLength > 0.0 ? (float)(100.0 * ship.CenterOfMass / ship.TotalLength) : 0.0f;

    /// <summary>Local gravitational acceleration at the vessel from its dominant body (m/s^2).</summary>
    private static double LocalGravity(GameContext ctx)
    {
        RigidBody? v = ctx.World.PrimaryVessel;
        if (v == null) return 0.0;
        CelestialBody? b = ctx.World.DominantBody(v.Position);
        if (b == null) return 0.0;
        double r = (v.Position - b.Position).Length;
        return r > 0.0 ? b.Mu / (r * r) : 0.0;
    }

    private static (string, Vector4) Tag(PartCategory c) => c switch
    {
        PartCategory.Command  => ("CMD", new Vector4(0.40f, 0.85f, 0.95f, 1.0f)),
        PartCategory.Habitat  => ("HAB", new Vector4(0.45f, 0.85f, 0.55f, 1.0f)),
        PartCategory.FuelTank => ("TNK", new Vector4(0.35f, 0.55f, 0.95f, 1.0f)),
        PartCategory.Engine   => ("ENG", new Vector4(1.00f, 0.55f, 0.20f, 1.0f)),
        PartCategory.Reactor  => ("RCT", new Vector4(0.95f, 0.35f, 0.45f, 1.0f)),
        PartCategory.Radiator => ("RAD", new Vector4(0.70f, 0.75f, 0.85f, 1.0f)),
        PartCategory.Weapon   => ("WPN", new Vector4(0.95f, 0.75f, 0.30f, 1.0f)),
        PartCategory.Armor    => ("ARM", new Vector4(0.60f, 0.60f, 0.68f, 1.0f)),
        _                     => ("STR", new Vector4(0.65f, 0.65f, 0.65f, 1.0f)),
    };

    private static void Section(string label)
    {
        ImGui.Spacing();
        Ui.TextDisabled(label);
        ImGui.Separator();
    }
}
