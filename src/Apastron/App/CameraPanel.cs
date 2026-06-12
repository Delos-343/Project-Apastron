using ImGuiNET;
using Apastron.Render;

namespace Apastron.App;

/// <summary>Controls for the 3D camera and what it draws.</summary>
public static class CameraPanel
{
    public static void Body(GameContext ctx)
    {
        ViewSettings view = ctx.View;

        Ui.TextDisabled("Focus");
        if (ImGui.RadioButton("Body", view.Focus == CameraFocus.Body)) view.Focus = CameraFocus.Body;
        ImGui.SameLine();
        if (ImGui.RadioButton("Vessel", view.Focus == CameraFocus.Vessel)) view.Focus = CameraFocus.Vessel;
        ImGui.SameLine();
        if (ImGui.RadioButton("Target", view.Focus == CameraFocus.Target)) view.Focus = CameraFocus.Target;

        ImGui.Spacing();
        ImGui.Checkbox("Show orbit path", ref view.ShowOrbitPath);
        ImGui.Checkbox("Show vessel marker", ref view.ShowVesselMarker);

        ImGui.Spacing();
        float fov = view.FovDegrees;
        if (ImGui.SliderFloat("FOV", ref fov, 20.0f, 90.0f, "%.0f deg")) view.FovDegrees = fov;

        ImGui.Spacing();
        // Convert camera distance (render units) to km using the world's render scale, so the readout
        // is correct across scenarios (orbital scale and the wide deep-space combat arena alike).
        double scale = ctx.World.RenderScaleHint > 0.0 ? ctx.World.RenderScaleHint : 1.0e-6;
        double km = view.Distance / scale / 1000.0;   // units -> metres -> km
        Ui.Text($"Distance: {view.Distance:F1} u  (~{km:N0} km)");
        Ui.TextDisabled("Drag: rotate    Scroll: zoom");

        ImGui.Spacing();
        if (ImGui.Button("Reset view")) view.ResetRequested = true;
    }
}
