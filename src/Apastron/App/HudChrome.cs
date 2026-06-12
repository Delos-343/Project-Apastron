using System;
using System.Numerics;
using ImGuiNET;

namespace Apastron.App;

/// <summary>
/// Halo "VISR"-style chrome for HUD windows: a frosted translucent fill, an angular beveled outline whose
/// left and right rails bow gently inward, bright chamfer accents at the corners, faint frosted scan lines,
/// and a slow animated scan sweep. It is pure ImGui draw-list work (the same kind <see cref="AmbientBackdrop"/>
/// already does) - no GL state, nothing that can destabilise the renderer.
///
/// Usage: set the window's background transparent (<c>SetNextWindowBgAlpha(0)</c> + no window border), then
/// call <see cref="Window"/> (or <see cref="Panel"/>) once right after <c>ImGui.Begin</c>, before drawing any
/// content, so the frame sits behind everything. Animation is driven by <c>ImGui.GetTime()</c>, so the HUD
/// keeps breathing even while the simulation is paused.
/// </summary>
public static class HudChrome
{
    /// <summary>Draw the frame for the current window (call just after Begin, before content).</summary>
    public static void Window(Vector4 accent) =>
        Panel(ImGui.GetWindowDrawList(), ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize(), accent);

    /// <summary>Draw the frame for a panel spanning [min,max] in screen space, tinted by <paramref name="accent"/>.</summary>
    public static void Panel(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 accent)
    {
        float w = max.X - min.X, h = max.Y - min.Y;
        if (w < 16f || h < 16f) return;

        float t = (float)ImGui.GetTime();
        float c = Math.Clamp(MathF.Min(w, h) * 0.07f, 9f, 22f);      // corner chamfer
        float bow = Math.Clamp(h * 0.03f, 6f, 16f);                  // inward bow of the side rails
        Vector4 lite = Lite(accent);

        dl.PushClipRectFullScreen();   // draw to the window's true extent, not the inset content region

        // 1) frosted translucent fill - a dark blue glass with a faint vertical sheen
        uint fillTop = Col(0.05f, 0.08f, 0.12f, 0.52f);
        uint fillBot = Col(0.07f, 0.12f, 0.17f, 0.38f);
        dl.AddRectFilledMultiColor(min, max, fillTop, fillTop, fillBot, fillBot);

        // faint scan lines for a frosted-display texture
        uint scan = Col(accent.X, accent.Y, accent.Z, 0.020f);
        for (float y = min.Y + 3f; y < max.Y - 1f; y += 4f)
            dl.AddLine(new Vector2(min.X + 2f, y), new Vector2(max.X - 2f, y), scan, 1f);

        // 2) animated scan sweep - a soft bright band travelling top -> bottom
        float sweep = (t * 0.16f) % 1f;
        float sy = min.Y + sweep * h;
        const float band = 26f;
        uint clear = Col(accent.X, accent.Y, accent.Z, 0f);
        uint glow  = Col(lite.X, lite.Y, lite.Z, 0.10f);
        dl.AddRectFilledMultiColor(new Vector2(min.X, MathF.Max(sy - band, min.Y)), new Vector2(max.X, sy),
                                   clear, clear, glow, glow);
        dl.AddRectFilledMultiColor(new Vector2(min.X, sy), new Vector2(max.X, MathF.Min(sy + band, max.Y)),
                                   glow, glow, clear, clear);
        dl.AddLine(new Vector2(min.X + 2f, sy), new Vector2(max.X - 2f, sy),
                   Col(accent.X, accent.Y, accent.Z, 0.16f), 1f);

        // 3) angular beveled outline with inward-bowed sides; the border alpha breathes slowly
        float pulse = 0.40f + 0.14f * MathF.Sin(t * 1.7f);
        uint bcol = Col(accent.X, accent.Y, accent.Z, pulse);
        Vector2 a  = new(min.X + c, min.Y), b  = new(min.X, min.Y + c);   // top-left chamfer ends
        Vector2 cc = new(max.X - c, min.Y), d  = new(max.X, min.Y + c);   // top-right
        Vector2 e  = new(max.X, max.Y - c), fp = new(max.X - c, max.Y);   // bottom-right
        Vector2 g  = new(min.X + c, max.Y), hh = new(min.X, max.Y - c);   // bottom-left
        dl.AddLine(a, cc, bcol, 1.6f);            // top edge
        dl.AddLine(cc, d, bcol, 1.6f);            // TR chamfer
        BowedRail(dl, d, e, -1f, bow, bcol);      // right rail (bows toward centre)
        dl.AddLine(e, fp, bcol, 1.6f);            // BR chamfer
        dl.AddLine(fp, g, bcol, 1.6f);            // bottom edge
        dl.AddLine(g, hh, bcol, 1.6f);            // BL chamfer
        BowedRail(dl, hh, b, +1f, bow, bcol);     // left rail (bows toward centre)
        dl.AddLine(b, a, bcol, 1.6f);             // TL chamfer

        // 4) bright chamfer accents - the cut corners pop like Halo targeting ticks
        uint brk = Col(lite.X, lite.Y, lite.Z, 0.92f);
        dl.AddLine(cc, d, brk, 2.4f);             // TR
        dl.AddLine(e, fp, brk, 2.4f);             // BR
        dl.AddLine(g, hh, brk, 2.4f);             // BL
        dl.AddLine(b, a, brk, 2.4f);              // TL

        dl.PopClipRect();
    }

    // A rail from p0 to p1 (sharing an x) whose middle is pulled toward the panel centre by `bow`.
    private static void BowedRail(ImDrawListPtr dl, Vector2 p0, Vector2 p1, float inwardSign, float bow, uint col)
    {
        const int seg = 18;
        Vector2 prev = p0;
        for (int i = 1; i <= seg; i++)
        {
            float u = i / (float)seg;
            float bx = MathF.Sin(u * MathF.PI) * bow * inwardSign;
            Vector2 p = new(Lerp(p0.X, p1.X, u) + bx, Lerp(p0.Y, p1.Y, u));
            dl.AddLine(prev, p, col, 1.6f);
            prev = p;
        }
    }

    private static float Lerp(float a, float b, float u) => a + (b - a) * u;

    private static Vector4 Lite(Vector4 a) => new(
        MathF.Min(a.X * 1.25f + 0.10f, 1f),
        MathF.Min(a.Y * 1.25f + 0.10f, 1f),
        MathF.Min(a.Z * 1.25f + 0.10f, 1f), 1f);

    private static uint Col(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
