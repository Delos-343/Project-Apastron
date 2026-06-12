using System.Linq;
using System.Numerics;
using ImGuiNET;
using Apastron.Combat;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Sets up and monitors a live AI-vs-AI engagement: pick each ship's doctrine, engage or cease
/// fire, reset the duel, and watch armour, hull, IR signature, rounds in flight and the outcome.
/// </summary>
public static class CombatPanel
{
    private static readonly string[] DoctrineNames = { "Aggressive", "Standoff", "Evasive", "Hold Fire" };

    public static void Body(GameContext ctx)
    {
        CombatManager? cm = ctx.Combat;
        if (cm == null || cm.Combatants.Count == 0)
        {
            Ui.TextWrapped("No engagement active. Open File -> New: Duel to set up two warships, then engage.");
            return;
        }

        // --- controls ---
        if (cm.Active)
        {
            if (ImGui.Button("Cease fire")) cm.Active = false;
        }
        else
        {
            if (ImGui.Button("Engage")) cm.Active = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset duel")) ctx.NewDuel?.Invoke();

        // --- outcome / global tally ---
        Section("Engagement");
        int aliveCount = cm.Combatants.Count(c => c.Alive);
        if (aliveCount <= 1 && cm.Combatants.Count >= 2)
        {
            var survivor = cm.Combatants.FirstOrDefault(c => c.Alive);
            if (survivor != null) Ui.TextColored(Green, $"{survivor.Name} wins.");
            else Ui.TextColored(Red, "Mutual destruction.");
        }
        else Ui.TextColored(Amber, cm.Active ? "Engaging." : "Standing by.");

        // Inter-ship separation, so the beyond-visual-range character of the engagement is legible.
        if (cm.Combatants.Count >= 2)
        {
            double sep = (cm.Combatants[1].Body.Position - cm.Combatants[0].Body.Position).Length;
            string sepStr = sep >= 1000.0 ? $"{sep / 1000.0:N1} km" : $"{sep:N0} m";
            Ui.Text($"Range       {sepStr}  ({cm.Combatants[0].Name} -> {cm.Combatants[1].Name})");
        }

        Ui.Text($"Elapsed     {cm.Time:N0} s");
        Ui.Text($"Slugs fired {cm.SlugsFired}");
        Ui.Text($"Missiles    {cm.MissilesFired}  (in flight {cm.Munitions.Count(m => m.Kind == MunitionKind.Missile)})");
        Ui.Text($"Perforations {cm.Hits}");
        Ui.Text($"PD intercepts {cm.Intercepts}");

        // --- camera focus (Homeworld-style: lock the view onto any ship) ---
        Section("Camera focus");
        FocusRow(ctx, "Free orbit (auto)", -1);
        for (int i = 0; i < cm.Combatants.Count; i++)
        {
            Combatant c = cm.Combatants[i];
            int vi = ctx.World.Vessels.IndexOf(c.Body);
            if (vi < 0) continue;
            FocusRow(ctx, c.Name + (c.Alive ? "" : "  [destroyed]"), vi);
        }
        Ui.TextDisabled("Click a ship in the view or press Tab to follow it.");

        // --- per-ship status ---
        for (int i = 0; i < cm.Combatants.Count; i++)
        {
            Combatant c = cm.Combatants[i];
            Section(c.Name + (c.Alive ? "" : "  [DESTROYED]"));

            int di = (int)c.Doctrine;
            ImGui.SetNextItemWidth(160.0f);
            if (ImGui.Combo($"Doctrine##{i}", ref di, DoctrineNames, DoctrineNames.Length))
                c.Doctrine = (CombatDoctrine)di;

            float armorFrac = c.ArmorMax > 0.0 ? (float)(c.Armor / c.ArmorMax) : 0.0f;
            float hullFrac = c.HullMax > 0.0 ? (float)(c.Hull / c.HullMax) : 0.0f;
            float barW = System.Math.Max(ImGui.GetContentRegionAvail().X - 76.0f, 90.0f);
            Ui.Text("Armor"); ImGui.SameLine(70);
            ImGui.ProgressBar(System.Math.Clamp(armorFrac, 0f, 1f), new Vector2(barW, 0), $"{c.Armor:N0} kg/m2");
            Ui.Text("Hull"); ImGui.SameLine(70);
            ImGui.ProgressBar(System.Math.Clamp(hullFrac, 0f, 1f), new Vector2(barW, 0), $"{hullFrac * 100.0f:N0}%");

            // module integrity (Chunk D): list any internal module below full health
            var hurt = c.Ship.Parts.Where(p => p.Health < 0.99 &&
                (p.Category is PartCategory.Engine or PartCategory.Reactor or PartCategory.Radiator
                            or PartCategory.Weapon or PartCategory.Command or PartCategory.FuelTank)).ToList();
            if (hurt.Count == 0)
                Ui.TextDisabled("modules nominal");
            else
                foreach (Part p in hurt)
                {
                    bool dead = p.Health <= 0.05;
                    Ui.TextColored(dead ? Red : Amber,
                        $"  {p.Name}: {(dead ? "DESTROYED" : $"{p.Health * 100.0:N0}%")}");
                }

            int inbound = cm.Munitions.Count(m => m.Kind == MunitionKind.Missile && m.Target == i && m.Alive);
            Ui.TextDisabled($"signature {Power(c.Signature)}  detect {c.DetectionRange / 1000.0:N0} km  inbound {inbound}");
        }

        // --- damage-control feed (module hits + crew survivability calls) ---
        Section("Damage control feed");
        if (cm.Log.Count == 0) Ui.TextDisabled("(no events)");
        else for (int i = 0; i < cm.Log.Count && i < 8; i++) Ui.TextWrapped(cm.Log[i]);
    }

    private static readonly Vector4 Green = new(0.5f, 0.85f, 0.55f, 1.0f);
    private static readonly Vector4 Amber = new(0.95f, 0.85f, 0.4f, 1.0f);
    private static readonly Vector4 Red = new(1.0f, 0.45f, 0.35f, 1.0f);

    private static void FocusRow(GameContext ctx, string label, int vesselIndex)
    {
        bool on = ctx.View.FocusVesselIndex == vesselIndex;
        if (ImGui.Selectable(label, on)) ctx.View.FocusVesselIndex = vesselIndex;
    }

    private static string Power(double w)
    {
        if (w >= 1.0e9) return $"{w / 1.0e9:N1} GW";
        if (w >= 1.0e6) return $"{w / 1.0e6:N1} MW";
        if (w >= 1.0e3) return $"{w / 1.0e3:N0} kW";
        return $"{w:N0} W";
    }

    private static void Section(string label)
    {
        ImGui.Spacing();
        Ui.TextDisabled(label);
        ImGui.Separator();
    }
}
