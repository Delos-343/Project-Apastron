using System;
using ImGuiNET;
using Apastron.Config;
using Apastron.Physics.Integrators;

namespace Apastron.App;

/// <summary>Graphics and simulation settings, persisted on save.</summary>
public static class SettingsWindow
{
    private static readonly string[] TierNames = { "Low", "Medium", "High", "Ultra" };
    private static readonly int[]     MsaaOpts  = { 1, 2, 4, 8 };
    private static readonly string[]  MsaaNames = { "Off", "2x", "4x", "8x" };
    private static readonly string[]  IntegratorNames =
        { "Velocity Verlet", "Semi-implicit Euler", "Runge-Kutta 4" };
    private static readonly string[]  PaletteNames = { "Cool slate", "Warm tan", "Dusk mauve" };

    public static void Draw(GameContext ctx)
    {
        if (!ctx.ShowSettings) return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(420, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Settings", ref ctx.ShowSettings))
        {
            ImGui.End();
            return;
        }

        var g = ctx.Config.Graphics;
        var s = ctx.Config.Simulation;

        if (ctx.Device != null)
        {
            Section("Detected device");
            Ui.TextWrapped($"Vendor:   {ctx.Device.Vendor}");
            Ui.TextWrapped($"Renderer: {ctx.Device.Renderer}");
            Ui.TextWrapped($"OpenGL:   {ctx.Device.GlVersion}");
            Ui.TextDisabled($"Recommended tier: {ctx.Device.RecommendedTier}");
        }

        Section("Graphics");
        bool autoDetect = g.AutoDetect;
        if (ImGui.Checkbox("Auto-detect from GPU", ref autoDetect)) g.AutoDetect = autoDetect;

        bool disabled = g.AutoDetect;
        if (disabled) ImGui.BeginDisabled();

        int tex = (int)g.TextureQuality;
        if (ImGui.Combo("Texture quality", ref tex, TierNames, TierNames.Length))
            g.TextureQuality = (QualityTier)tex;

        int sh = (int)g.ShadowQuality;
        if (ImGui.Combo("Shadow quality", ref sh, TierNames, TierNames.Length))
            g.ShadowQuality = (QualityTier)sh;

        int msaaIdx = Math.Max(0, Array.IndexOf(MsaaOpts, g.MsaaSamples));
        if (ImGui.Combo("MSAA", ref msaaIdx, MsaaNames, MsaaNames.Length))
            g.MsaaSamples = MsaaOpts[msaaIdx];

        float scale = (float)g.RenderScale;
        if (ImGui.SliderFloat("Render scale", ref scale, 0.5f, 2.0f, "%.2fx"))
            g.RenderScale = scale;

        float distMm = (float)(g.MaxRenderDistance / 1.0e6);
        if (ImGui.SliderFloat("Draw distance (Mm)", ref distMm, 100f, 20000f, "%.0f Mm"))
            g.MaxRenderDistance = distMm * 1.0e6;

        if (disabled) ImGui.EndDisabled();

        Ui.TextDisabled("Render scale, MSAA, draw distance & LOD apply live.");
        Ui.TextDisabled("Texture/shadow quality currently drive sphere LOD; textures land in a later chunk.");

        ImGui.Spacing();
        bool vsync = g.VSync;
        if (ImGui.Checkbox("VSync", ref vsync)) g.VSync = vsync;
        int fps = g.TargetFps;
        if (ImGui.SliderInt("Target FPS", ref fps, 30, 360)) g.TargetFps = fps;

        Section("Appearance");
        bool grade = g.FilmGrade;
        if (ImGui.Checkbox("Film grade (soft-focus, cross-process & grain)", ref grade)) g.FilmGrade = grade;
        bool plumes = g.AdditivePlumes;
        if (ImGui.Checkbox("Additive engine plumes (glow shader)", ref plumes)) g.AdditivePlumes = plumes;
        int pal = (int)g.Palette;
        if (ImGui.Combo("UI palette", ref pal, PaletteNames, PaletteNames.Length))
        {
            g.Palette = (UiPalette)pal;
            Theme.Apply(g.Palette);
        }
        Ui.TextDisabled("Gen X Soft Club theme. Film grade & palette apply live.");

        if (g.DriverSafeMode)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.95f, 0.75f, 0.35f, 1.0f));
            Ui.TextWrapped("Driver safe mode is active: a previous session fast-failed inside the GPU " +
                           "driver on the first burning frame, so the additive plume shader and the film " +
                           "grade are bypassed. Update the GPU driver, then re-enable.");
            ImGui.PopStyleColor();
            if (ImGui.Button("Re-enable full effects (clear driver safe mode)"))
            {
                g.DriverSafeMode = false;
                ctx.Config.Save();
            }
        }

        Section("Simulation");
        int integ = Math.Max(0, Array.IndexOf(IntegratorNames, s.Integrator));
        if (ImGui.Combo("Integrator", ref integ, IntegratorNames, IntegratorNames.Length))
        {
            s.Integrator = IntegratorNames[integ];
            ctx.World.Integrator = MakeIntegrator(s.Integrator);
        }

        float step = (float)s.FixedStep;
        if (ImGui.SliderFloat("Fixed step (s)", ref step, 0.05f, 10f, "%.2f s"))
            s.FixedStep = step;

        int maxSteps = s.MaxStepsPerFrame;
        if (ImGui.SliderInt("Max steps / frame", ref maxSteps, 1000, 100000))
            s.MaxStepsPerFrame = maxSteps;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Save")) ctx.Config.Save();
        ImGui.SameLine();
        if (ImGui.Button("Save & Close")) { ctx.Config.Save(); ctx.ShowSettings = false; }

        ImGui.Spacing();
        Ui.TextDisabled("Saved to:");
        Ui.TextWrapped(AppConfig.ConfigPath);

        ImGui.End();
    }

    /// <summary>Resolve an integrator display name to a fresh instance.</summary>
    public static IIntegrator MakeIntegrator(string name) => name switch
    {
        "Runge-Kutta 4"       => new Rk4Integrator(),
        "Semi-implicit Euler" => new SemiImplicitEulerIntegrator(),
        _                     => new VelocityVerletIntegrator(),
    };

    private static void Section(string label)
    {
        ImGui.Spacing();
        Ui.TextDisabled(label);
        ImGui.Separator();
    }
}
