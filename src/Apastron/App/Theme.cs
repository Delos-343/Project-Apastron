using System.Numerics;
using ImGuiNET;
using Apastron.Config;

namespace Apastron.App;

/// <summary>
/// Halo "VISR"-style HUD theme in three palettes - cool cyan (the default, closest to Halo: Infinite),
/// warm gold, and dusk mauve - all dark, frosted and translucent, with hard angular corners (no rounding)
/// and thin accent-tinted tech borders. The active palette's key colours are exposed as live properties so
/// the title and HUD recolour instantly when the palette changes. Panels additionally get an animated
/// frosted frame from <see cref="HudChrome"/>; this class sets the global ImGui style underneath it.
/// </summary>
public static class Theme
{
    public static Vector4 Accent   { get; private set; } = new(0.55f, 0.74f, 0.69f, 1.00f);
    public static Vector4 AccentHi { get; private set; } = new(0.69f, 0.86f, 0.81f, 1.00f);
    public static Vector4 Chrome   { get; private set; } = new(0.82f, 0.86f, 0.88f, 1.00f);
    public static Vector4 Sand     { get; private set; } = new(0.81f, 0.74f, 0.62f, 1.00f);
    public static Vector4 Text     { get; private set; } = new(0.95f, 0.96f, 0.93f, 1.00f);
    public static Vector4 TextDim  { get; private set; } = new(0.58f, 0.60f, 0.60f, 1.00f);

    public static void Apply() => Apply(UiPalette.Cool);

    public static void Apply(UiPalette palette)
    {
        Vector4 accent, accentHi, bg, panel, panelHi, text, sand;
        switch (palette)
        {
            case UiPalette.Warm:
                accent   = new(0.86f, 0.71f, 0.42f, 1.00f); accentHi = new(0.97f, 0.85f, 0.58f, 1.00f);
                bg       = new(0.10f, 0.09f, 0.07f, 0.82f); panel    = new(0.17f, 0.14f, 0.11f, 0.46f);
                panelHi  = new(0.27f, 0.22f, 0.16f, 0.78f); text     = new(0.96f, 0.93f, 0.86f, 1.00f);
                sand     = new(0.88f, 0.78f, 0.60f, 1.00f);
                break;
            case UiPalette.Dusk:
                accent   = new(0.67f, 0.57f, 0.88f, 1.00f); accentHi = new(0.81f, 0.73f, 0.97f, 1.00f);
                bg       = new(0.09f, 0.08f, 0.12f, 0.82f); panel    = new(0.16f, 0.14f, 0.21f, 0.46f);
                panelHi  = new(0.25f, 0.22f, 0.33f, 0.78f); text     = new(0.95f, 0.94f, 0.98f, 1.00f);
                sand     = new(0.80f, 0.72f, 0.66f, 1.00f);
                break;
            default: // Cool - Halo VISR cyan
                accent   = new(0.38f, 0.78f, 0.96f, 1.00f); accentHi = new(0.64f, 0.92f, 1.00f, 1.00f);
                bg       = new(0.05f, 0.08f, 0.12f, 0.82f); panel    = new(0.09f, 0.16f, 0.21f, 0.44f);
                panelHi  = new(0.15f, 0.27f, 0.35f, 0.80f); text     = new(0.90f, 0.96f, 0.99f, 1.00f);
                sand     = new(0.85f, 0.78f, 0.58f, 1.00f);
                break;
        }

        Accent = accent; AccentHi = accentHi; Text = text; Sand = sand;
        TextDim = new Vector4(text.X * 0.72f, text.Y * 0.74f, text.Z * 0.76f, 1.00f);
        Vector4 hover = new(accent.X, accent.Y, accent.Z, 0.80f);
        Vector4 border = new(accent.X, accent.Y, accent.Z, 0.45f);   // accent-tinted angular tech borders

        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        // All metrics below are pre-scaled ~1.35x to match the larger global font (set in Program).
        // They are explicit constants rather than style.ScaleAllSizes(1.35f) because Apply() runs
        // again on every live palette switch, and ScaleAllSizes multiplies the *current* values,
        // compounding 1.35x per switch. Explicit values keep repeated Apply() idempotent.
        // Halo-style "VISR" HUD: hard angular corners (no rounding anywhere), thin tech-frame borders,
        // and tight utilitarian spacing. Pre-scaled ~1.35x for the larger global font; explicit constants
        // (not ScaleAllSizes) so repeated Apply() on palette switches stays idempotent.
        style.WindowRounding    = 0f;
        style.ChildRounding     = 0f;
        style.FrameRounding     = 0f;
        style.GrabRounding      = 0f;
        style.PopupRounding     = 0f;
        style.ScrollbarRounding = 0f;
        style.TabRounding       = 0f;
        style.WindowPadding     = new Vector2(20f, 16f);
        style.FramePadding      = new Vector2(15f, 8f);
        style.ItemSpacing       = new Vector2(12f, 10f);
        style.ItemInnerSpacing  = new Vector2(10f, 7f);
        style.IndentSpacing     = 26f;
        style.CellPadding       = new Vector2(5f, 3f);
        style.WindowBorderSize  = 1f;
        style.FrameBorderSize   = 1f;
        style.PopupBorderSize   = 1f;
        style.ScrollbarSize     = 16f;
        style.GrabMinSize       = 14f;

        Set(ImGuiCol.Text,                 text);
        Set(ImGuiCol.TextDisabled,         TextDim);
        Set(ImGuiCol.WindowBg,             bg);
        Set(ImGuiCol.ChildBg,              new Vector4(0f, 0f, 0f, 0f));
        Set(ImGuiCol.PopupBg,              new Vector4(bg.X, bg.Y, bg.Z, 0.97f));
        Set(ImGuiCol.Border,               border);
        Set(ImGuiCol.FrameBg,              panel);
        Set(ImGuiCol.FrameBgHovered,       panelHi);
        Set(ImGuiCol.FrameBgActive,        panelHi);
        Set(ImGuiCol.TitleBg,              new Vector4(bg.X * 0.85f, bg.Y * 0.85f, bg.Z * 0.85f, 0.95f));
        Set(ImGuiCol.TitleBgActive,        panelHi);
        Set(ImGuiCol.TitleBgCollapsed,     new Vector4(bg.X * 0.85f, bg.Y * 0.85f, bg.Z * 0.85f, 0.70f));
        Set(ImGuiCol.MenuBarBg,            new Vector4(panel.X, panel.Y, panel.Z, 0.88f));
        Set(ImGuiCol.ScrollbarBg,          new Vector4(0.10f, 0.12f, 0.14f, 0.40f));
        Set(ImGuiCol.ScrollbarGrab,        panelHi);
        Set(ImGuiCol.ScrollbarGrabHovered, accent);
        Set(ImGuiCol.ScrollbarGrabActive,  accentHi);
        Set(ImGuiCol.CheckMark,            accentHi);
        Set(ImGuiCol.SliderGrab,           accent);
        Set(ImGuiCol.SliderGrabActive,     Chrome);
        Set(ImGuiCol.Button,               panelHi);
        Set(ImGuiCol.ButtonHovered,        hover);
        Set(ImGuiCol.ButtonActive,         accent);
        Set(ImGuiCol.Header,               panelHi);
        Set(ImGuiCol.HeaderHovered,        hover);
        Set(ImGuiCol.HeaderActive,         accent);
        Set(ImGuiCol.Separator,            border);
        Set(ImGuiCol.SeparatorHovered,     accent);
        Set(ImGuiCol.SeparatorActive,      accentHi);
        Set(ImGuiCol.ResizeGrip,           new Vector4(0.64f, 0.68f, 0.66f, 0.30f));
        Set(ImGuiCol.ResizeGripHovered,    accent);
        Set(ImGuiCol.ResizeGripActive,     accentHi);
        Set(ImGuiCol.Tab,                  panel);
        Set(ImGuiCol.TabHovered,           accent);
        Set(ImGuiCol.PlotLines,            accent);
        Set(ImGuiCol.PlotLinesHovered,     accentHi);
        Set(ImGuiCol.PlotHistogram,        accent);
        Set(ImGuiCol.PlotHistogramHovered, accentHi);
        Set(ImGuiCol.TextSelectedBg,       new Vector4(accent.X, accent.Y, accent.Z, 0.30f));
    }

    private static void Set(ImGuiCol idx, Vector4 color)
    {
        var style = ImGui.GetStyle();
        style.Colors[(int)idx] = color;
    }
}
