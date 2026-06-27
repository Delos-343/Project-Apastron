using System;
using System.Numerics;
using ImGuiNET;

namespace Apastron.App;

/// <summary>
/// Title-screen ambience drawn on the ImGui background draw list (behind all windows). On the title it
/// renders a "Gen X/Z Soft Club" NASA-punk screensaver: an opaque cool gradient (so no gameplay shows
/// through), a few glassy frosted discs, a faint planet limb, slow concentric orbital rings with
/// travelling nodes, an inclined elliptical orbit, and a minimal technical graticule — all in soft white
/// line-work, animated smoothly. Elsewhere it is just a faint vignette so the simulation stays legible.
/// Pure draw-list work (no shaders), so it cannot destabilise the build.
/// </summary>
public static class AmbientBackdrop
{
    // Glassy frosted discs (white): slow drift frequencies + base position + radius (fraction of diagonal).
    private static readonly (float fx, float fy, float px, float py, float rad)[] Discs =
    {
        (0.018f, 0.013f, 0.30f, 0.34f, 0.34f),
        (0.012f, 0.020f, 0.74f, 0.40f, 0.40f),
        (0.015f, 0.011f, 0.60f, 0.72f, 0.36f),
    };

    public static void Draw(Vector2 size, bool full)
    {
        if (size.X < 1f || size.Y < 1f) return;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        float t = (float)ImGui.GetTime();
        const float TAU = 6.2831853f;

        if (full)
        {
            // opaque cool gradient (slightly lighter at top) — the screensaver fully replaces the scene
            uint top = Col(0.11f, 0.13f, 0.17f, 1f);
            uint bot = Col(0.06f, 0.07f, 0.10f, 1f);
            dl.AddRectFilledMultiColor(Vector2.Zero, size, top, top, bot, bot);

            float diag = MathF.Sqrt(size.X * size.X + size.Y * size.Y);
            Vector2 ctr = new(size.X * 0.5f, size.Y * 0.44f);

            // glassy frosted discs, drifting slowly
            foreach (var d in Discs)
            {
                Vector2 c = new(
                    size.X * (d.px + 0.05f * MathF.Sin(t * d.fx * TAU)),
                    size.Y * (d.py + 0.05f * MathF.Cos(t * d.fy * TAU)));
                GlassDisc(dl, c, diag * d.rad, 0.020f);
            }

            // faint planet limb, lower-left, with a soft inner glow band
            Vector2 planet = new(size.X * 0.20f, size.Y * 0.64f);
            float pr = diag * 0.30f;
            dl.AddCircle(planet, pr, White(0.07f), 128, 1.5f);
            dl.AddCircle(planet, pr * 0.978f, White(0.045f), 128, 7f);

            // concentric orbital rings: slow breathing radius, a travelling node, and a bright arc segment
            for (int i = 0; i < 3; i++)
            {
                float r = diag * (0.085f + i * 0.072f) + 4f * MathF.Sin(t * 0.18f + i * 1.3f);
                dl.AddCircle(ctr, r, White(0.10f - i * 0.022f), 160, 1.1f);

                float ang = t * (0.16f - i * 0.028f) + i * 2.2f;
                Vector2 node = new(ctr.X + r * MathF.Cos(ang), ctr.Y + r * MathF.Sin(ang));
                dl.AddCircleFilled(node, 3.0f, White(0.75f), 18);
                dl.AddCircle(node, 7.5f, White(0.22f), 18, 1.0f);

                Arc(dl, ctr, r, ang + 1.1f, ang + 2.0f, White(0.16f), 2.0f);
            }

            // an inclined elliptical orbit with a single travelling satellite
            float ea = diag * 0.30f, eb = diag * 0.115f, tilt = 0.42f;
            float ct = MathF.Cos(tilt), st = MathF.Sin(tilt);
            Ellipse(dl, ctr, ea, eb, ct, st, White(0.08f), 1.1f);
            {
                float ang = t * 0.11f;
                Vector2 p = EllipsePt(ctr, ea, eb, ct, st, ang);
                dl.AddCircleFilled(p, 3.4f, White(0.8f), 18);
                dl.AddCircle(p, 8f, White(0.2f), 18, 1.0f);
            }

            // minimal technical graticule: a gapped crosshair through the centre with end ticks
            Crosshair(dl, ctr, diag * 0.36f, White(0.06f));

            Vignette(dl, size, 0.34f);
            return;
        }

        Vignette(dl, size, 0.18f);
    }

    // Fake a soft frosted-glass disc by stacking translucent white circles, faint outside to denser inside.
    private static void GlassDisc(ImDrawListPtr dl, Vector2 c, float radius, float peak)
    {
        const int rings = 7;
        for (int i = 0; i < rings; i++)
        {
            float f = 1f - i / (float)rings;
            float r = radius * f;
            float a = peak * (1f - f) + 0.006f;
            dl.AddCircleFilled(c, r, White(a), 64);
        }
    }

    private static Vector2 EllipsePt(Vector2 c, float a, float b, float ct, float st, float th)
    {
        float x = a * MathF.Cos(th), y = b * MathF.Sin(th);
        return new Vector2(c.X + x * ct - y * st, c.Y + x * st + y * ct);
    }

    private static void Ellipse(ImDrawListPtr dl, Vector2 c, float a, float b, float ct, float st, uint col, float th)
    {
        const int seg = 96;
        Vector2 prev = EllipsePt(c, a, b, ct, st, 0f);
        for (int i = 1; i <= seg; i++)
        {
            Vector2 p = EllipsePt(c, a, b, ct, st, i / (float)seg * 6.2831853f);
            dl.AddLine(prev, p, col, th);
            prev = p;
        }
    }

    private static void Arc(ImDrawListPtr dl, Vector2 c, float r, float a0, float a1, uint col, float th)
    {
        const int seg = 24;
        Vector2 prev = new(c.X + r * MathF.Cos(a0), c.Y + r * MathF.Sin(a0));
        for (int i = 1; i <= seg; i++)
        {
            float a = a0 + (a1 - a0) * (i / (float)seg);
            Vector2 p = new(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a));
            dl.AddLine(prev, p, col, th);
            prev = p;
        }
    }

    private static void Crosshair(ImDrawListPtr dl, Vector2 c, float reach, uint col)
    {
        float gap = reach * 0.28f, tk = 6f;
        dl.AddLine(new Vector2(c.X - reach, c.Y), new Vector2(c.X - gap, c.Y), col, 1f);
        dl.AddLine(new Vector2(c.X + gap, c.Y), new Vector2(c.X + reach, c.Y), col, 1f);
        dl.AddLine(new Vector2(c.X, c.Y - reach), new Vector2(c.X, c.Y - gap), col, 1f);
        dl.AddLine(new Vector2(c.X, c.Y + gap), new Vector2(c.X, c.Y + reach), col, 1f);
        dl.AddLine(new Vector2(c.X - reach, c.Y - tk), new Vector2(c.X - reach, c.Y + tk), col, 1f);
        dl.AddLine(new Vector2(c.X + reach, c.Y - tk), new Vector2(c.X + reach, c.Y + tk), col, 1f);
        dl.AddLine(new Vector2(c.X - tk, c.Y - reach), new Vector2(c.X + tk, c.Y - reach), col, 1f);
        dl.AddLine(new Vector2(c.X - tk, c.Y + reach), new Vector2(c.X + tk, c.Y + reach), col, 1f);
    }

    // Edge darkening via four transparent-to-dark bands.
    private static void Vignette(ImDrawListPtr dl, Vector2 size, float a)
    {
        uint dark = Col(0.04f, 0.05f, 0.07f, a);
        uint clear = Col(0.04f, 0.05f, 0.07f, 0f);
        float bx = size.X * 0.18f, by = size.Y * 0.22f;
        dl.AddRectFilledMultiColor(Vector2.Zero, new Vector2(size.X, by), dark, dark, clear, clear);
        dl.AddRectFilledMultiColor(new Vector2(0f, size.Y - by), size, clear, clear, dark, dark);
        dl.AddRectFilledMultiColor(Vector2.Zero, new Vector2(bx, size.Y), dark, clear, clear, dark);
        dl.AddRectFilledMultiColor(new Vector2(size.X - bx, 0f), size, clear, dark, dark, clear);
    }

    private static uint White(float a) => Col(1f, 1f, 1f, a);

    private static uint Col(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
