using System;
using System.Numerics;
using ImGuiNET;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Render;

namespace Apastron.App;

/// <summary>
/// Draws the RTS selection layer over the 3D view: corner brackets around each selected ship and the
/// band-select marquee while dragging. Rendered on the background draw list, so it sits above the 3D scene
/// but beneath the HUD panels. Move-order lines and the move gizmo are layered on in a later chunk. Pure
/// ImGui draw-list work - no GL state - so it cannot destabilise the renderer.
/// </summary>
public static class FleetOverlay
{
    public static void Draw(GameContext ctx, SceneRenderer scene)
    {
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        Vector4 acc = Theme.Accent;
        uint col   = ImGui.ColorConvertFloat4ToU32(acc);
        uint colHi = ImGui.ColorConvertFloat4ToU32(Theme.AccentHi);

        // Corner brackets around each selected ship.
        foreach (RigidBody v in ctx.Selection)
            if (scene.WorldToScreen(v.Position, out Vector2 s))
                Bracket(dl, s, 18.0f, colHi);

        // Move-order paths for selected ships: ship -> waypoint chain with a destination marker.
        uint moveCol = ImGui.ColorConvertFloat4ToU32(new Vector4(acc.X, acc.Y, acc.Z, 0.85f));
        uint wpCol   = ImGui.ColorConvertFloat4ToU32(new Vector4(acc.X, acc.Y, acc.Z, 0.40f));
        foreach (RigidBody v in ctx.Selection)
        {
            var slot = ctx.Fleet.Get(v);
            if (slot == null || slot.Waypoints.Count == 0) continue;

            bool have = scene.WorldToScreen(v.Position, out Vector2 prev);
            int last = slot.Waypoints.Count - 1;
            for (int k = 0; k <= last; k++)
            {
                if (!scene.WorldToScreen(slot.Waypoints[k], out Vector2 wp)) { have = false; continue; }
                if (have) dl.AddLine(prev, wp, k == 0 ? moveCol : wpCol, k == 0 ? 1.6f : 1.2f);
                if (k < last) dl.AddCircleFilled(wp, 3.0f, wpCol);
                prev = wp; have = true;
            }
            if (scene.WorldToScreen(slot.Waypoints[last], out Vector2 dest)) MoveMarker(dl, dest, colHi);
        }

        // Band-select marquee (translucent fill + bright outline).
        if (scene.BandActive)
        {
            Vector2 a = scene.BandRectMin, b = scene.BandRectMax;
            uint fill = ImGui.ColorConvertFloat4ToU32(new Vector4(acc.X, acc.Y, acc.Z, 0.10f));
            dl.AddRectFilled(a, b, fill);
            dl.AddRect(a, b, col, 0.0f, ImDrawFlags.None, 1.4f);
        }

        // Live altitude move-disk (Ctrl + right-drag): a ring on the movement plane, a vertical line up to
        // the dragged target, and a destination marker at the target.
        if (scene.MoveGizmoActive)
        {
            Vec3 baseW = scene.MoveGizmoBase;
            double r = scene.MoveGizmoDiskRadius;
            Vector2 prev = default; bool prevOk = false;
            for (int s = 0; s <= 24; s++)
            {
                double ang = 2.0 * Math.PI * s / 24.0;
                var p = new Vec3(baseW.X + Math.Cos(ang) * r, baseW.Y, baseW.Z + Math.Sin(ang) * r);
                bool ok = scene.WorldToScreen(p, out Vector2 sp);
                if (ok && prevOk) dl.AddLine(prev, sp, wpCol, 1.3f);
                prev = sp; prevOk = ok;
            }
            bool hb = scene.WorldToScreen(baseW, out Vector2 baseS);
            bool ht = scene.WorldToScreen(scene.MoveGizmoTarget, out Vector2 tgtS);
            if (hb) dl.AddCircleFilled(baseS, 2.5f, colHi);
            if (hb && ht) dl.AddLine(baseS, tgtS, colHi, 1.6f);
            if (ht) MoveMarker(dl, tgtS, colHi);
        }
    }

    // A destination marker: a ring with a small inner diamond.
    private static void MoveMarker(ImDrawListPtr dl, Vector2 p, uint col)
    {
        dl.AddCircle(p, 9.0f, col, 16, 1.6f);
        const float d = 4.0f;
        dl.AddLine(new Vector2(p.X, p.Y - d), new Vector2(p.X + d, p.Y), col, 1.4f);
        dl.AddLine(new Vector2(p.X + d, p.Y), new Vector2(p.X, p.Y + d), col, 1.4f);
        dl.AddLine(new Vector2(p.X, p.Y + d), new Vector2(p.X - d, p.Y), col, 1.4f);
        dl.AddLine(new Vector2(p.X - d, p.Y), new Vector2(p.X, p.Y - d), col, 1.4f);
    }

    // Four L-shaped corner ticks forming a bracket box centred on p.
    private static void Bracket(ImDrawListPtr dl, Vector2 p, float r, uint col)
    {
        const float t = 6.0f;     // tick length
        const float th = 1.6f;    // line thickness
        Vector2 tl = new(p.X - r, p.Y - r), tr = new(p.X + r, p.Y - r);
        Vector2 bl = new(p.X - r, p.Y + r), br = new(p.X + r, p.Y + r);
        dl.AddLine(tl, new Vector2(tl.X + t, tl.Y), col, th); dl.AddLine(tl, new Vector2(tl.X, tl.Y + t), col, th);
        dl.AddLine(tr, new Vector2(tr.X - t, tr.Y), col, th); dl.AddLine(tr, new Vector2(tr.X, tr.Y + t), col, th);
        dl.AddLine(bl, new Vector2(bl.X + t, bl.Y), col, th); dl.AddLine(bl, new Vector2(bl.X, bl.Y - t), col, th);
        dl.AddLine(br, new Vector2(br.X - t, br.Y), col, th); dl.AddLine(br, new Vector2(br.X, br.Y - t), col, th);
    }
}
