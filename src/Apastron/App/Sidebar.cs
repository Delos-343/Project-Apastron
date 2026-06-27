using System;
using System.Numerics;
using ImGuiNET;

namespace Apastron.App;

/// <summary>
/// The two merged control drawers. Every former free-floating tool panel is now a padded,
/// collapsible <i>section</i> inside one of two sidebars docked to the left and right edges:
/// the left drawer carries flight &amp; ops (Flight Controls, Ship AI, Maneuver, Torch Transfer,
/// Weapons, Combat); the right carries view, data &amp; build (Camera, Telemetry, Rendezvous,
/// Spacecraft Builder). Each drawer is a single scrollable window, so its sections scroll together
/// as one menu instead of overlapping as separate windows.
///
/// Section visibility is still driven by the same <c>ctx.Show*</c> flags the View menu toggles, and
/// each section header carries a close (x) button that clears its flag — so the View menu and the
/// drawer stay in sync. A drawer can be collapsed to a thin edge tab (the "hidden / pop-out"
/// behaviour) via the chevron in its header; the tab reopens it. Geometry is recomputed every frame
/// from the live window size (width scales with the window and is capped so both drawers plus a
/// centre gap always fit), and each drawer spans from below its side's top overlay (reported by
/// <see cref="Hud"/> through <see cref="HudDock"/>) down to the bottom margin. Pure ImGui layout —
/// no GL state — so it cannot destabilise the renderer.
/// </summary>
public static class Sidebar
{
    private const float Pad     = 14f;   // inner window padding
    private const float Indent  = 8f;    // per-section content indent
    private const float TabW    = 26f;   // collapsed edge-tab width

    /// <summary>Draw the left drawer (flight &amp; ops) and, when collapsed, its reopen tab.</summary>
    public static void DrawLeft(GameContext ctx)
    {
        if (!ctx.LeftSidebarOpen) { DrawReopenTab(ctx, left: true); return; }

        float width = DrawerWidth();
        float x = HudDock.EdgeMargin;
        float y = HudDock.LeftReserve;
        float h = MathF.Max(HudDock.WinH - y - HudDock.EdgeMargin, 80f);

        if (BeginDrawer("##leftdrawer", "FLIGHT & OPS", left: true, ref ctx.LeftSidebarOpen, x, y, width, h))
        {
            Section(ctx, "Flight Controls", ref ctx.ShowDemoControls, () => FlightControls.Body(ctx));
            Section(ctx, "Fleet Command",   ref ctx.ShowFleet,        () => FleetPanel.Body(ctx));
            Section(ctx, "Ship AI",         ref ctx.ShowShipAi,       () => ShipAiPanel.Body(ctx));
            Section(ctx, "Maneuver Planner", ref ctx.ShowManeuver,    () => ManeuverPanel.Body(ctx));
            Section(ctx, "Torch Transfer",  ref ctx.ShowTorch,        () => BrachistochronePanel.Body(ctx));
            Section(ctx, "Weapons / Fire Control", ref ctx.ShowWeapons, () => WeaponsPanel.Body(ctx));
            Section(ctx, "Combat",          ref ctx.ShowCombat,       () => CombatPanel.Body(ctx));
            EmptyHint(ctx.ShowDemoControls || ctx.ShowFleet || ctx.ShowShipAi || ctx.ShowManeuver ||
                      ctx.ShowTorch || ctx.ShowWeapons || ctx.ShowCombat);
            EndDrawer();
        }
    }

    /// <summary>Draw the right drawer (view, data &amp; build) and, when collapsed, its reopen tab.</summary>
    public static void DrawRight(GameContext ctx)
    {
        if (!ctx.RightSidebarOpen) { DrawReopenTab(ctx, left: false); return; }

        float width = DrawerWidth();
        float x = HudDock.WinW - HudDock.EdgeMargin - width;
        float y = HudDock.RightReserve;
        float h = MathF.Max(HudDock.WinH - y - HudDock.EdgeMargin, 80f);

        if (BeginDrawer("##rightdrawer", "VIEW & DATA", left: false, ref ctx.RightSidebarOpen, x, y, width, h))
        {
            Section(ctx, "3D Camera",   ref ctx.ShowCamera,     () => CameraPanel.Body(ctx));
            Section(ctx, "Telemetry",   ref ctx.ShowTelemetry,  () => TelemetryWindow.Body(ctx));
            Section(ctx, "Rendezvous",  ref ctx.ShowRendezvous, () => RendezvousPanel.Body(ctx));
            Section(ctx, "Spacecraft Builder", ref ctx.ShowBuilder, () => ShipBuilder.Body(ctx));
            EmptyHint(ctx.ShowCamera || ctx.ShowTelemetry || ctx.ShowRendezvous || ctx.ShowBuilder);
            EndDrawer();
        }
    }

    // Responsive drawer width: a fraction of the window, clamped to a comfortable band, and never so
    // wide that the two drawers plus a centre gap can't coexist on a narrow window.
    private static float DrawerWidth()
    {
        float w = Math.Clamp(HudDock.WinW * 0.27f, 290f, 440f);
        return MathF.Min(w, HudDock.WinW * 0.44f);
    }

    // Opens the drawer window with consistent chrome and a header (title + collapse chevron). Returns
    // true and leaves a scrolling child region open for sections; the caller must call EndDrawer().
    private static bool BeginDrawer(string id, string title, bool left, ref bool open,
                                    float x, float y, float w, float h)
    {
        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(w, h), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);   // HudChrome draws the (frosted) surface; no default window fill
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Pad, Pad));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);   // HudChrome draws the border
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (!ImGui.Begin(id, flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            return false;
        }

        HudChrome.Window(Theme.Accent);   // frosted angular frame behind the header + sections

        // Header: title on the left, collapse chevron pinned to the right edge.
        Ui.TextColored(Theme.Accent, title);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - TabW - Pad);
        if (ImGui.SmallButton(left ? "<<" : ">>")) open = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hide this panel");
        ImGui.Separator();

        // Everything below scrolls as one; sections are clipped to this child, not the whole screen.
        ImGui.BeginChild("##scroll", new Vector2(0f, 0f), ImGuiChildFlags.None);
        return true;
    }

    private static void EndDrawer()
    {
        ImGui.EndChild();
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    // One collapsible, padded section. The header carries a close (x) button bound to the section's
    // visibility flag, so closing it here unticks it in the View menu and vice-versa.
    private static void Section(GameContext ctx, string label, ref bool show, Action body)
    {
        if (!show) return;
        ImGui.PushID(label);
        bool open = ImGui.CollapsingHeader(label, ref show, ImGuiTreeNodeFlags.DefaultOpen);
        if (open && show)
        {
            ImGui.Indent(Indent);
            body();
            ImGui.Unindent(Indent);
            ImGui.Spacing();
            ImGui.Spacing();
        }
        ImGui.PopID();
    }

    // Shown when a drawer has no active sections, so an empty drawer reads as intentional.
    private static void EmptyHint(bool anyVisible)
    {
        if (anyVisible) return;
        ImGui.Spacing();
        Ui.TextDisabled("No panels open on this side.");
        Ui.TextWrapped("Enable panels from the View menu.");
    }

    // The thin edge tab shown while a drawer is collapsed; clicking it reopens the drawer.
    private static void DrawReopenTab(GameContext ctx, bool left)
    {
        float h = 132f;
        float y = (left ? HudDock.LeftReserve : HudDock.RightReserve);
        float x = left ? 0f : HudDock.WinW - TabW;

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(TabW, h), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        string id = left ? "##lefttab" : "##righttab";
        if (ImGui.Begin(id, flags))
        {
            HudChrome.Window(Theme.Accent);
            if (ImGui.Button(left ? ">>" : "<<", new Vector2(TabW - 6f, h - 12f)))
            {
                if (left) ctx.LeftSidebarOpen = true; else ctx.RightSidebarOpen = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(left ? "Show flight & ops panel" : "Show view & data panel");
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }
}
