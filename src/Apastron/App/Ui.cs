using System;
using System.Numerics;
using ImGuiNET;

namespace Apastron.App;

/// <summary>
/// Format-safe ImGui text wrappers. Dear ImGui's Text/TextColored/TextDisabled/TextWrapped
/// treat the string as a printf <b>format</b>: any '%' inside dynamic content (e.g.
/// "Heat load 37% of rejection", or a percent produced by a ":P0"/"{x:F0}%" interpolation)
/// is parsed as a conversion specifier by the native CRT formatter inside cimgui. With no
/// varargs supplied, valid specifiers print garbage read from the stack, and the disabled
/// "%n" specifier fast-fails the whole process (exit 0xC0000409) with no managed exception.
/// These wrappers route everything through TextUnformatted, which performs no format
/// parsing, so arbitrary strings render verbatim and can never destabilise the process.
/// All dynamic or interpolated strings must use these; printf-style format strings remain
/// correct (and required) for Slider/Drag display formats, which are unaffected.
/// </summary>
public static class Ui
{
    /// <summary>Plain text, rendered verbatim (no printf parsing).</summary>
    public static void Text(string s) => ImGui.TextUnformatted(s ?? string.Empty);

    /// <summary>Coloured text, rendered verbatim (no printf parsing).</summary>
    public static void TextColored(Vector4 col, string s)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, col);
        ImGui.TextUnformatted(s ?? string.Empty);
        ImGui.PopStyleColor();
    }

    /// <summary>Disabled-styled text, rendered verbatim (no printf parsing).</summary>
    public static void TextDisabled(string s)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted(s ?? string.Empty);
        ImGui.PopStyleColor();
    }

    /// <summary>Word-wrapped text, rendered verbatim. <paramref name="wrapPos"/> follows
    /// PushTextWrapPos semantics: 0 wraps at the window edge; &gt;0 wraps at that local x.</summary>
    public static void TextWrapped(string s, float wrapPos = 0.0f)
    {
        ImGui.PushTextWrapPos(wrapPos);
        ImGui.TextUnformatted(s ?? string.Empty);
        ImGui.PopTextWrapPos();
    }

    // --- composite widgets (molecules) -------------------------------------------------------------
    // Reusable panel building-blocks distilled from the per-panel duplication. A panel ("organism") is
    // assembled from these plus the text atoms above; the building blocks live here so every panel renders
    // a consistent section header, stat bar and labelled dropdown.

    /// <summary>A section header: a little vertical breathing room, a dimmed caption, then a rule. This was
    /// hand-rolled identically in nine panels; this is the single canonical version.</summary>
    public static void Section(string title)
    {
        ImGui.Spacing();
        TextDisabled(title);
        ImGui.Separator();
    }

    /// <summary>A labelled progress bar row ("Armor [####    ] 800 kg/m2"). The bar auto-sizes to the
    /// remaining content width; <paramref name="overlay"/> is drawn verbatim (ImGui's bar overlay is not
    /// printf-parsed, so a literal '%' is safe here).</summary>
    public static void StatBar(string label, float frac, string overlay)
    {
        float barW = MathF.Max(ImGui.GetContentRegionAvail().X - 76.0f, 90.0f);
        Text(label);
        ImGui.SameLine(70.0f);
        ImGui.ProgressBar(Math.Clamp(frac, 0.0f, 1.0f), new Vector2(barW, 0.0f), overlay);
    }

    /// <summary>A fixed-width dropdown over a name list. Returns true on the frame the selection changes.
    /// Wraps the SetNextItemWidth + Combo pair that recurred across the combat, fleet and weapons panels.</summary>
    public static bool LabeledCombo(string label, ref int index, string[] items, float width)
    {
        ImGui.SetNextItemWidth(width);
        return ImGui.Combo(label, ref index, items, items.Length);
    }
}
