using System.Linq;
using System.Numerics;
using ImGuiNET;
using Apastron.Audio;
using Apastron.Combat;
using Apastron.Core;

namespace Apastron.App;

/// <summary>In-mission UI: objective banner, the player's command bar, and the result / pause overlays.</summary>
public static class MissionHud
{
    private static Vector4 Accent => Theme.AccentHi;
    private static readonly Vector4 Green  = new(0.55f, 0.78f, 0.62f, 1.0f);  // you (muted sage)
    private static Vector4 Amber => Theme.Sand;                               // warm tan (follows palette)
    private static readonly Vector4 Red    = new(0.85f, 0.52f, 0.48f, 1.0f);  // enemy (dusty clay)
    private static readonly Vector4 Active = new(0.34f, 0.52f, 0.50f, 1.0f);  // posture highlight
    private static readonly int[] TimeSpeeds = { 1, 2, 4, 8 };                // mission time-warp presets

    public static void Draw(GameContext ctx)
    {
        if (ctx.Screen != AppScreen.Mission || ctx.Mission == null || ctx.Combat == null) return;
        MissionState ms = ctx.Mission;

        DrawBanner(ctx, ms);
        if (ms.Outcome == MissionOutcome.InProgress && !ctx.ShowPauseMenu) DrawCommandBar(ctx, ms);
        if (ms.Outcome != MissionOutcome.InProgress) DrawResult(ctx, ms);
        else if (ctx.ShowPauseMenu) DrawPause(ctx);
    }

    private static void DrawBanner(GameContext ctx, MissionState ms)
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X * 0.5f, 8.0f), ImGuiCond.Always, new Vector2(0.5f, 0.0f));
        const ImGuiWindowFlags f = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                   ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
                                   ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;
        ImGui.SetNextWindowBgAlpha(0f);
        if (ImGui.Begin("##banner", f))
        {
            HudChrome.Window(Accent);
            Ui.TextColored(Accent, ms.Def.Name);
            ImGui.SameLine();
            if (ms.Def.Objective == MissionObjective.Survive)
                Ui.TextColored(Amber, $"-  Survive  {ms.TimeLeft:N0} s");
            else
                Ui.TextColored(Dimmed, "-  Destroy the enemy cruiser");

            float you = ms.Player.HullMax > 0 ? (float)(ms.Player.Hull / ms.Player.HullMax) : 0f;
            float foe = ms.Enemy.HullMax > 0 ? (float)(ms.Enemy.Hull / ms.Enemy.HullMax) : 0f;
            you = System.Math.Clamp(you, 0f, 1f);
            foe = System.Math.Clamp(foe, 0f, 1f);
            Ui.Text("YOU"); ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Green);
            ImGui.ProgressBar(you, new Vector2(140, 0), $"{you * 100f:N0}%");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            Ui.Text("ENEMY"); ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Red);
            ImGui.ProgressBar(foe, new Vector2(140, 0), $"{foe * 100f:N0}%");
            ImGui.PopStyleColor();
        }
        ImGui.End();
    }

    private static readonly Vector4 Dimmed = new(0.80f, 0.80f, 0.76f, 1.0f);

    private static void DrawCommandBar(GameContext ctx, MissionState ms)
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y - 8.0f), ImGuiCond.Always,
                               new Vector2(0.5f, 1.0f));
        ImGui.SetNextWindowSize(new Vector2(660.0f, 0.0f), ImGuiCond.Always);
        const ImGuiWindowFlags f = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                   ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav;
        ImGui.SetNextWindowBgAlpha(0f);
        if (ImGui.Begin("##orders", f))
        {
            HudChrome.Window(Accent);
            // Time control: pause/play (without opening the pause MENU) and a speed selector, so the
            // pacing of the engagement is in the player's hands rather than whatever warp carried in.
            bool paused = ctx.Paused;
            if (paused) ImGui.PushStyleColor(ImGuiCol.Button, Active);
            if (ImGui.Button(paused ? "Play" : "Pause", new Vector2(78, 0))) ctx.Paused = !ctx.Paused;
            if (paused) ImGui.PopStyleColor();
            ImGui.SameLine(); Ui.TextDisabled("speed");
            foreach (int sp in TimeSpeeds)
            {
                ImGui.SameLine();
                bool on = !ctx.Paused && (int)System.Math.Round(ctx.TimeScale) == sp;
                if (on) ImGui.PushStyleColor(ImGuiCol.Button, Active);
                if (ImGui.Button($"{sp}x", new Vector2(40, 0))) { ctx.TimeScale = sp; ctx.Paused = false; }
                if (on) ImGui.PopStyleColor();
            }
            ImGui.Separator();

            Ui.TextColored(Accent, "ORDERS");
            ImGui.SameLine(90); PostureButton(ctx, ms, "Close", CombatDoctrine.Aggressive);
            ImGui.SameLine();   PostureButton(ctx, ms, "Hold", CombatDoctrine.Standoff);
            ImGui.SameLine();   PostureButton(ctx, ms, "Evade", CombatDoctrine.Evasive);
            ImGui.SameLine(360);
            bool hold = ms.Player.WeaponsHold;
            if (hold) ImGui.PushStyleColor(ImGuiCol.Button, Red);
            if (ImGui.Button(hold ? "Hold Fire" : "Weapons Free", new Vector2(130, 0)))
            {
                ms.Player.WeaponsHold = !ms.Player.WeaponsHold;
                ctx.Audio.Play(GameSound.UiClick);
            }
            if (hold) ImGui.PopStyleColor();

            int pIdx = ctx.Combat!.Combatants.IndexOf(ms.Player);
            int inbound = ctx.Combat.Munitions.Count(m => m.Kind == MunitionKind.Missile && m.Alive && m.Target == pIdx);

            Vec3 los = ms.Enemy.Body.Position - ms.Player.Body.Position;
            double range = los.Length;
            Vec3 relV = ms.Enemy.Body.Velocity - ms.Player.Body.Velocity;
            double closing = range > 1e-6 ? -Vec3.Dot(relV, los / range) : 0.0;   // +approaching, -opening

            Ui.TextWrapped($"range {range / 1000.0:N0} km    closing {closing:+0;-0} m/s    inbound missiles {inbound}");
            Ui.TextWrapped($"posture: {Posture(ms.Player.Doctrine)}    weapons: {(ms.Player.WeaponsHold ? "HOLD" : "free")}" +
                           $"    PD intercepts {ctx.Combat.Intercepts}");
            ImGui.PushTextWrapPos(0f);   // wrap at the panel edge so the hint can never clip off the side
            Ui.TextDisabled("(1/2/3 set posture  /  F toggle weapons  /  P pause  /  Esc menu)");
            ImGui.PopTextWrapPos();
        }
        ImGui.End();
    }

    private static void PostureButton(GameContext ctx, MissionState ms, string label, CombatDoctrine d)
    {
        bool on = ms.Player.Doctrine == d;
        if (on) ImGui.PushStyleColor(ImGuiCol.Button, Active);
        if (ImGui.Button(label, new Vector2(78, 0)))
        {
            ms.Player.Doctrine = d;
            ctx.Audio.Play(GameSound.UiClick);
        }
        if (on) ImGui.PopStyleColor();
    }

    private static string Posture(CombatDoctrine d) => d switch
    {
        CombatDoctrine.Aggressive => "closing",
        CombatDoctrine.Standoff   => "holding",
        CombatDoctrine.Evasive    => "evading",
        _                         => "holding fire",
    };

    private static void DrawResult(GameContext ctx, MissionState ms)
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(io.DisplaySize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(440.0f, 0.0f), ImGuiCond.Always);
        const ImGuiWindowFlags f = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                   ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize;
        bool win = ms.Outcome == MissionOutcome.Victory;
        ImGui.SetNextWindowBgAlpha(0f);
        if (ImGui.Begin("##result", f))
        {
            HudChrome.Window(win ? Green : Red);
            ImGui.SetWindowFontScale(2.2f);
            Ui.TextColored(win ? Green : Red, win ? "VICTORY" : "DEFEAT");
            ImGui.SetWindowFontScale(1.0f);
            Ui.TextWrapped(win
                ? (ms.Def.Objective == MissionObjective.Survive ? "You held out." : "Enemy cruiser destroyed.")
                : (ms.Def.Objective == MissionObjective.Survive ? "Your ship was lost before time ran out."
                                                                : "Your ship was lost."), 430f);
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

            ImGui.SetWindowFontScale(1.6f);
            Ui.TextColored(Accent, $"Score {ms.Score}");
            ImGui.SameLine();
            Ui.TextColored(Amber, $"  Rating {ms.Rating}");
            ImGui.SetWindowFontScale(1.0f);
            if (ms.IsNewBest) Ui.TextColored(Green, "NEW BEST!");
            else if (ms.Best > 0) Ui.TextDisabled($"Best: {ms.Best} [{ScoreBoard.RatingFor(ms.Best)}]");
            ImGui.Spacing();

            Ui.Text($"Time            {ms.Elapsed:N0} s");
            Ui.Text($"Your hull       {ms.PlayerHullPct:N0}%");
            Ui.Text($"Enemy hull      {ms.EnemyHullPct:N0}%");
            Ui.Text($"Slugs fired     {ctx.Combat!.SlugsFired}");
            Ui.Text($"Missiles fired  {ctx.Combat.MissilesFired}");
            Ui.Text($"PD intercepts   {ctx.Combat.Intercepts}");
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

            if (ImGui.Button("Retry", new Vector2(130, 32))) ctx.RestartMission?.Invoke();
            ImGui.SameLine();
            if (ImGui.Button("Mission Select", new Vector2(150, 32)))
            {
                TitleScreen.ShowMissionSelect = true;
                ctx.ExitToTitle?.Invoke();
            }
            ImGui.SameLine();
            if (ImGui.Button("Main Menu", new Vector2(120, 32)))
            {
                TitleScreen.ShowMissionSelect = false;
                ctx.ExitToTitle?.Invoke();
            }
        }
        ImGui.End();
    }

    private static void DrawPause(GameContext ctx)
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(io.DisplaySize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(320.0f, 0.0f), ImGuiCond.Always);
        const ImGuiWindowFlags f = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                   ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize;
        ImGui.SetNextWindowBgAlpha(0f);
        if (ImGui.Begin("##pause", f))
        {
            HudChrome.Window(Accent);
            ImGui.SetWindowFontScale(1.8f);
            Ui.TextColored(Accent, "PAUSED");
            ImGui.SetWindowFontScale(1.0f);
            ImGui.Spacing();
            if (ImGui.Button("Resume", new Vector2(-1, 32)))
            {
                ctx.ShowPauseMenu = false;
                ctx.Paused = false;
            }
            if (ImGui.Button("Restart", new Vector2(-1, 30))) ctx.RestartMission?.Invoke();
            if (ImGui.Button("Settings", new Vector2(-1, 30))) ctx.ShowSettings = true;
            if (ImGui.Button("Main Menu", new Vector2(-1, 30)))
            {
                TitleScreen.ShowMissionSelect = false;
                ctx.ExitToTitle?.Invoke();
            }
        }
        ImGui.End();
        if (ctx.ShowSettings) SettingsWindow.Draw(ctx);
    }
}
