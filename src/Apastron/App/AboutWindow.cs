using ImGuiNET;

namespace Apastron.App;

/// <summary>Modeless "about" panel describing the project and stack (ASCII-only UI strings:
/// ImGui's default font has no glyphs above Latin-1, so em-dashes etc. would render as '?').</summary>
public static class AboutWindow
{
    public static void Draw(GameContext ctx)
    {
        if (!ctx.ShowAbout) return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 430), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("About Apastron", ref ctx.ShowAbout))
        {
            ImGui.End();
            return;
        }

        Ui.Text("Apastron");
        Ui.TextDisabled("Hard-science orbital spaceflight & combat");
        ImGui.Spacing();
        Ui.TextWrapped(
            "Double-precision Newtonian gravity, symplectic integration, analytic Kepler " +
            "propagation, and live orbital telemetry. Build spacecraft, plan maneuvers and " +
            "torch transfers, fly the SCAS DUAT across the system, and fight captain-style " +
            "engagements resolved by real terminal ballistics.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Ui.Text("Stack");
        ImGui.BulletText(".NET 8 / C#");
        ImGui.BulletText("Silk.NET - windowing, input, OpenGL 3.3 core");
        ImGui.BulletText("Dear ImGui (ImGui.NET) - UI");
        ImGui.BulletText("Custom double-precision physics core");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Close")) ctx.ShowAbout = false;

        ImGui.End();
    }
}
