using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Apastron.Combat;
using Apastron.Physics;
using Apastron.Simulation;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Fleet command panel: shows the current selection, picks the formation the group moves in, halts it, and
/// exposes the nine control groups. Move orders themselves are issued in the 3D view (right-click for a planar
/// move, Ctrl+right-drag for altitude, Shift to queue). Pure ImGui - no GL or sim state of its own.
/// </summary>
public static class FleetPanel
{
    private static readonly (string Label, FleetFormation Kind)[] Forms =
    {
        ("Loose",  FleetFormation.None),
        ("Sphere", FleetFormation.Sphere),
        ("Wall",   FleetFormation.Wall),
        ("Delta",  FleetFormation.Delta),
        ("Claw",   FleetFormation.Claw),
    };

    public static void Body(GameContext ctx)
    {
        int n = ctx.Selection.Count;
        string sel = n == 0 ? "No ships selected" : n == 1 ? "1 ship selected" : $"{n} ships selected";
        Ui.TextColored(n > 0 ? Theme.Accent : Theme.TextDim, sel);

        // --- formation selector (3 per row) ---
        ImGui.Spacing();
        Ui.TextDisabled("Formation");
        for (int i = 0; i < Forms.Length; i++)
        {
            bool on = ctx.Formation == Forms[i].Kind;
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, Theme.Accent);
            if (ImGui.Button(Forms[i].Label, new Vector2(70f, 0f))) ctx.Formation = Forms[i].Kind;
            if (on) ImGui.PopStyleColor();
            if (i % 3 != 2 && i < Forms.Length - 1) ImGui.SameLine();
        }
        Ui.TextDisabled("Applies on the next move order.");

        // --- halt ---
        ImGui.Spacing();
        if (ImGui.Button("Stop (S)", new Vector2(110f, 0f)))
            foreach (RigidBody v in ctx.Selection) ctx.Fleet.Stop(v);

        // --- control groups (5 per row) ---
        ImGui.Spacing();
        Ui.TextDisabled("Groups   Ctrl+# set / # recall");
        for (int g = 1; g <= 9; g++)
        {
            int c = FleetGroups.Count(ctx, g);
            bool has = c > 0;
            if (has) ImGui.PushStyleColor(ImGuiCol.Button, Theme.Accent);
            if (ImGui.Button($"{g}", new Vector2(30f, 0f)) && has) FleetGroups.Recall(ctx, g);
            if (has) ImGui.PopStyleColor();
            if (has && ImGui.IsItemHovered()) ImGui.SetTooltip(c == 1 ? "1 ship" : $"{c} ships");
            if (g % 5 != 0 && g < 9) ImGui.SameLine();
        }

        // --- roster ---
        if (n > 0)
        {
            ImGui.Spacing();
            Ui.TextDisabled("Selected");
            foreach (RigidBody v in ctx.Selection)
            {
                bool moving = ctx.Fleet.HasOrder(v);
                Ui.TextColored(moving ? Theme.AccentHi : Theme.Text, (moving ? "-> " : "   ") + v.Name);
            }
        }

        // --- reinforcements: build a wing of a chosen hull class ---
        ImGui.Spacing();
        ImGui.Separator();
        Ui.TextDisabled("Reinforcements");

        if (_spec == null || _specClass != ctx.SpawnClass)
        { _specClass = ctx.SpawnClass; _spec = WarshipClasses.Build(ctx.SpawnClass, "preview"); }

        int ci = Array.IndexOf(WarshipClasses.All, ctx.SpawnClass); if (ci < 0) ci = 0;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Class", ref ci, ClassNames, ClassNames.Length)) ctx.SpawnClass = WarshipClasses.All[ci];

        int di = Array.IndexOf(SpawnDoctrines, ctx.SpawnDoctrine); if (di < 0) di = 0;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Doctrine", ref di, DoctrineNames, DoctrineNames.Length)) ctx.SpawnDoctrine = SpawnDoctrines[di];

        double mass = _spec.TotalMass, accel = _spec.AccelerationAt(1.0), armor = 0.0;
        foreach (Part p in _spec.Parts) if (p.Armor != null) armor += p.Armor!.ArealDensity;
        Ui.TextColored(Theme.Text, $"{mass / 1000.0:N0} t   {accel:0.#} m/s2   armour {armor:N0} kg/m2");
        ImGui.PushTextWrapPos(0f);
        Ui.TextDisabled("Guns: " + WeaponSummary(_spec));
        Ui.TextDisabled(WarshipClasses.Role(ctx.SpawnClass));
        ImGui.PopTextWrapPos();

        // --- wing size: a +/- stepper that sets how many ships the next spawn adds ---
        ImGui.Spacing();
        Ui.TextDisabled("Wing size");
        if (ImGui.Button("-", new Vector2(34f, 0f))) _spawnCount = Math.Max(MinWing, _spawnCount - 1);
        ImGui.SameLine();
        Ui.TextColored(Theme.Text, $"  {_spawnCount}  ");
        ImGui.SameLine();
        if (ImGui.Button("+", new Vector2(34f, 0f))) _spawnCount = Math.Min(MaxWing, _spawnCount + 1);

        if (ImGui.Button($"Spawn wing (x{_spawnCount})", new Vector2(150f, 0f)))
            FleetSpawn.Squadron(ctx, ctx.SpawnClass, _spawnCount, ctx.SpawnDoctrine);
        if (ctx.Combat == null || !ctx.Combat.Active)
            Ui.TextDisabled("(no battle active - they spawn but won't fire)");
    }

    private static HullClass _specClass = (HullClass)(-1);
    private static Spacecraft? _spec;
    private static int _spawnCount = 4;          // how many ships the next "Spawn wing" adds (player-set via +/-)
    private const int MinWing = 1;
    private const int MaxWing = 12;

    private static readonly string[] ClassNames = WarshipClasses.All.Select(WarshipClasses.DisplayName).ToArray();
    private static readonly CombatDoctrine[] SpawnDoctrines =
        { CombatDoctrine.Aggressive, CombatDoctrine.Standoff, CombatDoctrine.Evasive };
    private static readonly string[] DoctrineNames = { "Aggressive", "Standoff", "Evasive" };

    // Grouped, counted list of weapon mounts (e.g. "Spinal Railgun x2, Missile Rack, Point-Defense Gun").
    private static string WeaponSummary(Spacecraft s)
    {
        var counts = new System.Collections.Generic.Dictionary<string, int>();
        var order = new System.Collections.Generic.List<string>();
        foreach (Part p in s.Parts)
            if (p.Category == PartCategory.Weapon)
            {
                if (!counts.ContainsKey(p.Name)) { counts[p.Name] = 0; order.Add(p.Name); }
                counts[p.Name]++;
            }
        if (order.Count == 0) return "unarmed";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < order.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(order[i]);
            if (counts[order[i]] > 1) sb.Append(" x").Append(counts[order[i]]);
        }
        return sb.ToString();
    }
}
