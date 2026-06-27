using System;
using System.Numerics;
using ImGuiNET;
using Apastron.Audio;
using Apastron.Combat;

namespace Apastron.App;

/// <summary>The front-end: a title with a main menu and a mission-select list. Shown when Screen == Title.</summary>
public static class TitleScreen
{
    public static bool ShowMissionSelect;   // also set by the mission HUD's "Mission Select" button

    public const string Version = "v2.14";   // bump each release; shown on the title so the running build is unambiguous

    private static Vector4 Accent => Theme.AccentHi;            // pale accent (follows palette)
    private static Vector4 Dim    => Theme.TextDim;

    private static Vector4 DiffColor(string d) => d switch
    {
        "Easy"    => new Vector4(0.55f, 0.78f, 0.60f, 1.0f),   // soft sage
        "Normal"  => Theme.Sand,                               // warm tan
        "Hard"    => new Vector4(0.85f, 0.55f, 0.48f, 1.0f),   // dusty clay
        _         => new Vector4(0.66f, 0.62f, 0.78f, 1.0f),   // faded lilac (Survive)
    };

    public static void Draw(GameContext ctx)
    {
        if (ctx.Screen != AppScreen.Title) return;

        var io = ImGui.GetIO();
        // Responsive: half the window width, clamped to a large, readable band.
        float w = Math.Clamp(io.DisplaySize.X * 0.5f, 560.0f, 940.0f);
        ImGui.SetNextWindowPos(io.DisplaySize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(w, 0.0f), ImGuiCond.Always);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                       ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.AlwaysAutoResize;

        // Chunky framing so the larger fonts get matching breathing room.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(34.0f, 30.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(18.0f, 13.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12.0f, 12.0f));

        if (ImGui.Begin("##title", flags))
        {
            ImGui.SetWindowFontScale(1.5f);   // base: the whole modal renders larger
            float pulse = 0.82f + 0.18f * MathF.Sin((float)ImGui.GetTime() * 1.2f);
            ImGui.SetWindowFontScale(3.8f);   // hero title
            Ui.TextColored(new Vector4(Accent.X * pulse, Accent.Y * pulse, Accent.Z * pulse, 1f), "APASTRON");
            ImGui.SetWindowFontScale(1.5f);
            Ui.TextColored(Dim, "Hard-science orbital spaceflight & combat");
            ImGui.SetWindowFontScale(1.0f);
            Ui.TextColored(Dim, $"build {Version}");
            ImGui.SetWindowFontScale(1.5f);
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

            if (!ShowMissionSelect) DrawMainMenu(ctx);
            else DrawMissionSelect(ctx);

            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.End();
        ImGui.PopStyleVar(3);

        if (ctx.ShowSettings) SettingsWindow.Draw(ctx);
        if (ctx.ShowAbout) AboutWindow.Draw(ctx);
    }

    private static void DrawMainMenu(GameContext ctx)
    {
        if (ImGui.Button("New Engagement", new Vector2(-1, 0)))
        {
            ctx.Audio.Play(GameSound.UiClick);
            ShowMissionSelect = true;
        }
        ImGui.Spacing();
        if (ImGui.Button("Free-Flight Sandbox", new Vector2(-1, 0)))
        {
            ctx.Audio.Play(GameSound.UiClick);
            ctx.ResetScenario?.Invoke();      // installs a fresh Low Earth Orbit
            ctx.Screen = AppScreen.Sandbox;
        }
        ImGui.Spacing();
        if (ImGui.Button("Settings", new Vector2(-1, 0))) ctx.ShowSettings = true;
        ImGui.Spacing();
        if (ImGui.Button("About", new Vector2(-1, 0))) ctx.ShowAbout = true;
        ImGui.Spacing();
        if (ImGui.Button("Quit", new Vector2(-1, 0))) ctx.RequestExit = true;
    }

    private static void DrawMissionSelect(GameContext ctx)
    {
        Ui.Text("Choose your engagement:");
        ImGui.Spacing();

        Ui.TextColored(Accent, "Skirmish");
        ImGui.SameLine(); Ui.TextColored(DiffColor("Normal"), "[Normal]");
        Ui.TextWrapped("Run down a pack of 1-3 lightly-armed pirate raiders - skeletal hulls, weak guns. Dangerous in numbers.");
        if (ImGui.Button("Launch Skirmish", new Vector2(-1, 0)))
        {
            ctx.Audio.Play(GameSound.UiClick);
            ctx.DuelHard = false;
            ShowMissionSelect = false;
            ctx.NewDuel?.Invoke();              // pirate engagement (shares the live combat path)
            ctx.Screen = AppScreen.Sandbox;     // the screen that renders the engagement + combat panel
        }
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        Ui.TextColored(Accent, "Assault");
        ImGui.SameLine(); Ui.TextColored(DiffColor("Hard"), "[Hard]");
        Ui.TextWrapped("Face one randomized near-peer warship - destroyer, cruiser, battlecruiser or battleship. A serious fight.");
        if (ImGui.Button("Launch Assault", new Vector2(-1, 0)))
        {
            ctx.Audio.Play(GameSound.UiClick);
            ctx.DuelHard = true;
            ShowMissionSelect = false;
            ctx.NewDuel?.Invoke();
            ctx.Screen = AppScreen.Sandbox;
        }
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        Ui.TextDisabled("Pick your own hull and re-arm from the Combat panel once you're in.");
        if (ImGui.Button("Back", new Vector2(-1, 0)))
        {
            ctx.Audio.Play(GameSound.UiClick);
            ShowMissionSelect = false;
        }
    }
}
