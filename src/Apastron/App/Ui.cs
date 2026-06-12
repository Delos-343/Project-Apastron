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
}
