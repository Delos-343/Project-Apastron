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
    private static readonly string[] ClassNames = WarshipClasses.All.Select(WarshipClasses.DisplayName).ToArray();

    // Player picks their own hull; the opponent is a pirate swarm (normal) or a near-peer (hard).
    private static void DuelSetup(GameContext ctx)
    {
        Ui.Section("Duel setup");

        int a = System.Array.IndexOf(WarshipClasses.All, ctx.DuelClassA); if (a < 0) a = 0;
        if (Ui.LabeledCombo("Your ship##cls", ref a, ClassNames, 150.0f)) ctx.DuelClassA = WarshipClasses.All[a];
        ImGui.SameLine(); Ui.TextDisabled(ClassSpec(ctx.DuelClassA));

        bool hard = ctx.DuelHard;
        if (ImGui.Checkbox("Hard difficulty", ref hard)) ctx.DuelHard = hard;
        Ui.TextDisabled(ctx.DuelHard
            ? "Hard: one randomized near-peer warship (destroyer .. battleship)."
            : "Normal: 1-3 lightly-armed pirate raiders.");

        Ui.TextDisabled("Arm/Reset duel to apply.");
    }

    // One-line mass / acceleration summary for a class, built from its parts.
    private static string ClassSpec(HullClass c)
    {
        var s = WarshipClasses.Build(c, "x");
        return $"{s.TotalMass / 1000.0:N0} t, {s.AccelerationAt(1.0):0.#} m/s2";
    }

    public static void Body(GameContext ctx)
    {
        CombatManager? cm = ctx.Combat;
        if (cm == null || cm.Combatants.Count == 0)
        {
            Ui.TextWrapped("No engagement active. Pick a hull class for each side and arm a duel " +
                           "(or open File -> New: Duel).");
            DuelSetup(ctx);
            if (ImGui.Button("Arm duel")) ctx.NewDuel?.Invoke();
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

        DuelSetup(ctx);

        // --- outcome / global tally ---
        Ui.Section("Engagement");
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
        Ui.Section("Camera focus");
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
            Ui.Section(c.Name + (c.Alive ? "" : "  [DESTROYED]"));

            int di = (int)c.Doctrine;
            if (Ui.LabeledCombo($"Doctrine##{i}", ref di, DoctrineNames, 160.0f))
                c.Doctrine = (CombatDoctrine)di;

            float armorFrac = c.ArmorMax > 0.0 ? (float)(c.Armor / c.ArmorMax) : 0.0f;
            float hullFrac = c.HullMax > 0.0 ? (float)(c.Hull / c.HullMax) : 0.0f;
            Ui.StatBar("Armor", armorFrac, $"{c.Armor:N0} kg/m2");
            Ui.StatBar("Hull", hullFrac, $"{hullFrac * 100.0f:N0}%");

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
            string ord = Ordnance(c);
            if (ord.Length > 0) Ui.TextDisabled(ord);
        }

        // --- damage-control feed (module hits + crew survivability calls) ---
        Ui.Section("Damage control feed");
        if (cm.Log.Count == 0) Ui.TextDisabled("(no events)");
        else for (int i = 0; i < cm.Log.Count && i < 8; i++) Ui.TextWrapped(cm.Log[i]);
    }

    private static readonly Vector4 Green = new(0.5f, 0.85f, 0.55f, 1.0f);
    private static readonly Vector4 Amber = new(0.95f, 0.85f, 0.4f, 1.0f);
    private static readonly Vector4 Red = new(1.0f, 0.45f, 0.35f, 1.0f);

    // Rolls up magazine-fed ordnance (torpedoes / PDC belts / KKVs) into one line, summing rounds across
    // mounts of each type and flagging any that are mid-reload. Empty for ships with no magazine weapons.
    private static string Ordnance(Combatant c)
    {
        int torpA = 0, pdcA = 0, kkvA = 0; bool torp = false, pdc = false, kkv = false;
        int torpR = 0, pdcR = 0, kkvR = 0;
        foreach (Part p in c.Ship.Parts)
        {
            WeaponSpec? w = p.Weapon;
            if (w == null || w.MagazineSize <= 0) continue;
            int a = c.Ammo.TryGetValue(p, out int av) ? av : w.MagazineSize;
            bool reloading = c.Reload.TryGetValue(p, out double rv) && rv > 0.0;
            if (w.Interceptor) { kkv = true; kkvA += a; if (reloading) kkvR++; }
            else if (w.Kind == WeaponKind.Missile) { torp = true; torpA += a; if (reloading) torpR++; }
            else if (w.PointDefense) { pdc = true; pdcA += a; if (reloading) pdcR++; }
        }
        string s = "";
        if (torp) s += $"torpedoes {torpA}{(torpR > 0 ? " (reloading)" : "")}";
        if (pdc) s += (s.Length > 0 ? "   " : "") + $"PDC {pdcA}{(pdcR > 0 ? " (reloading)" : "")}";
        if (kkv) s += (s.Length > 0 ? "   " : "") + $"KKV {kkvA}{(kkvR > 0 ? " (reloading)" : "")}";
        return s;
    }

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
}
