using ImGuiNET;

namespace Apastron.App;

/// <summary>Top application menu bar.</summary>
public static class MainMenuBar
{
    private static readonly (string Label, double Scale)[] WarpRates =
    {
        ("x1",       1.0),
        ("x10",      10.0),
        ("x100",     100.0),
        ("x1000",    1000.0),
        ("x10000",   10000.0),
        ("x100000",  100000.0),
    };

    public static void Draw(GameContext ctx)
    {
        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Save Scenario")) ctx.SaveScenario?.Invoke();
            if (ImGui.MenuItem("Load Scenario")) ctx.LoadScenario?.Invoke();
            ImGui.Separator();
            if (ImGui.MenuItem("New: Low Earth Orbit")) ctx.ResetScenario?.Invoke();
            if (ImGui.MenuItem("New: Interplanetary"))   ctx.NewInterplanetary?.Invoke();
            if (ImGui.MenuItem("New: Duel"))             ctx.NewDuel?.Invoke();
            ImGui.Separator();
            if (ImGui.MenuItem("Main Menu")) ctx.ExitToTitle?.Invoke();
            if (ImGui.MenuItem("Exit")) ctx.RequestExit = true;
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Simulation"))
        {
            bool paused = ctx.Paused;
            if (ImGui.MenuItem("Pause", "Space", ref paused)) ctx.Paused = paused;

            ImGui.Separator();
            Ui.TextDisabled("Time warp");
            foreach (var (label, scale) in WarpRates)
            {
                bool selected = ctx.TimeScale == scale;
                if (ImGui.MenuItem(label, "", selected)) ctx.TimeScale = scale;
            }
            ImGui.Separator();
            ImGui.MenuItem("God mode (unlimited)", "", ref ctx.GodMode);
            if (ctx.GodMode)
                Ui.TextDisabled("  fuel, heat & range limits off");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem("Telemetry",          "", ref ctx.ShowTelemetry);
            ImGui.MenuItem("Flight Controls",    "", ref ctx.ShowDemoControls);
            ImGui.MenuItem("Fleet Command",      "", ref ctx.ShowFleet);
            ImGui.MenuItem("Ship AI",            "", ref ctx.ShowShipAi);
            ImGui.MenuItem("Spacecraft Builder", "", ref ctx.ShowBuilder);
            ImGui.MenuItem("Maneuver Planner",   "", ref ctx.ShowManeuver);
            ImGui.MenuItem("Rendezvous",         "", ref ctx.ShowRendezvous);
            ImGui.MenuItem("Torch Transfer",     "", ref ctx.ShowTorch);
            ImGui.MenuItem("Weapons",            "", ref ctx.ShowWeapons);
            ImGui.MenuItem("Combat",             "", ref ctx.ShowCombat);
            ImGui.MenuItem("3D Camera",          "", ref ctx.ShowCamera);
            ImGui.MenuItem("HUD overlay",        "", ref ctx.ShowHud);
            ImGui.Separator();
            ImGui.MenuItem("Left drawer (flight & ops)",  "", ref ctx.LeftSidebarOpen);
            ImGui.MenuItem("Right drawer (view & data)",  "", ref ctx.RightSidebarOpen);
            ImGui.Separator();
            ImGui.MenuItem("Engine plume",       "", ref ctx.View.ShowEnginePlume);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Settings"))
        {
            ImGui.MenuItem("Graphics & Simulation", "", ref ctx.ShowSettings);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Help"))
        {
            ImGui.MenuItem("About", "", ref ctx.ShowAbout);
            ImGui.EndMenu();
        }

        // Right-aligned status: transient message + paused / warp factor.
        string status = ctx.Paused ? "PAUSED" : $"running  x{ctx.TimeScale:0}";
        if (ctx.StatusTtl > 0.0 && ctx.StatusMessage.Length > 0)
            status = ctx.StatusMessage + "   |   " + status;
        float w = ImGui.CalcTextSize(status).X;
        ImGui.SameLine(ImGui.GetWindowWidth() - w - 16f);
        Ui.TextDisabled(status);

        ImGui.EndMainMenuBar();
    }
}
