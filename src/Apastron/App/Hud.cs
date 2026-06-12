using System;
using System.Numerics;
using ImGuiNET;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Simulation;

namespace Apastron.App;

/// <summary>
/// Glanceable heads-up overlays drawn on top of the 3D view: flight state (top-left) and
/// a small performance readout (top-right). These are click-through (NoInputs) translucent
/// windows, distinct from the full Telemetry panel. ASCII-only so the default font renders.
/// </summary>
public static class Hud
{
    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize;

    public static void Draw(GameContext ctx)
    {
        if (!ctx.ShowHud) return;

        ImGuiViewportPtr vp = ImGui.GetMainViewport();
        Vector2 work = vp.WorkPos;
        Vector2 size = vp.WorkSize;
        const float pad = 12.0f;

        DrawFlightStrip(ctx, work, size, pad);
        DrawPerf(ctx, new Vector2(work.X + size.X - pad, work.Y + pad));
    }

    // A wide, single-row flight readout pinned across the top of the view, just under the menu bar.
    // Laid out as labelled cells separated by vertical rules, it stays short so the left and right
    // sidebars get the full height of the window. The strip is sized to its content and left-anchored
    // (so it never collides with the top-right perf readout); on a narrow window ImGui wraps naturally.
    private static void DrawFlightStrip(GameContext ctx, Vector2 work, Vector2 size, float pad)
    {
        ImGui.SetNextWindowPos(new Vector2(work.X + pad, work.Y + pad), ImGuiCond.Always);
        // Cap the strip width so it can never run under the perf readout (now a wider horizontal row).
        float maxW = MathF.Max(size.X - 2f * pad - 500f, 320f);
        ImGui.SetNextWindowSizeConstraints(new Vector2(0f, 0f), new Vector2(maxW, 1000f));
        ImGui.SetNextWindowBgAlpha(0.35f);
        if (!ImGui.Begin("##hud_flight", Flags)) { ImGui.End(); return; }

        RigidBody? vessel = ctx.World.PrimaryVessel;
        CelestialBody? primary = vessel != null ? ctx.World.DominantBody(vessel.Position) : null;

        // Cell 1 - run state / warp.
        if (ctx.Paused) Cell(null, "[ PAUSED ]", new Vector4(0.88f, 0.58f, 0.32f, 1.0f));
        else            Cell(null, $"RUN x{ctx.TimeScale:0.###}", Theme.Accent);

        Sep();
        Cell("T+", FormatTime(ctx.World.SimTime), Theme.Text);

        if (vessel != null && primary != null)
        {
            var oe = OrbitalElements.Compute(vessel.Position, vessel.Velocity,
                                             primary.Position, primary.Mu, primary.Radius);
            double accG = vessel.Mass > 0.0
                ? vessel.ThrustWorld.Length / vessel.Mass / MathConstants.StandardGravity : 0.0;
            double apoAlt = oe.IsBound ? oe.Apoapsis - primary.Radius : double.NaN;
            double periAlt = oe.Periapsis - primary.Radius;

            Sep(); Cell(null, $"orbiting {primary.Name}", Theme.Accent);
            Sep(); Cell("ALT", FormatDistance(oe.Altitude), Theme.Text);
            Sep(); Cell("VEL", FormatSpeed(oe.Speed), Theme.Text);
            Sep(); Cell("ACC", $"{accG:0.00} g", accG > 0.001 ? new Vector4(1f, 0.6f, 0.2f, 1f) : Theme.Text);
            Sep(); Cell("AP", FormatAltitudeOrDash(apoAlt), Theme.Text);
            Sep(); Cell("PE", FormatAltitudeOrDash(periAlt), Theme.Text);
            Sep(); Cell("ECC", $"{oe.Eccentricity:0.000}{(oe.IsBound ? "" : " esc")}", Theme.Text);
        }
        else
        {
            Sep(); Cell(null, "no vessel", Theme.TextDim);
        }

        Sep(); Cell("focus", ctx.View.Focus.ToString(), Theme.TextDim);

        // Publish this strip's bottom edge so the docked sidebars start just below it.
        HudDock.ReportTopOverlay(left: true, ImGui.GetWindowPos().Y + ImGui.GetWindowHeight());

        ImGui.End();
    }

    // One labelled cell on the horizontal strip: a dimmed caption then the value, kept on one line.
    private static void Cell(string? label, string value, Vector4 valueColor)
    {
        if (label != null)
        {
            Ui.TextDisabled(label);
            ImGui.SameLine(0f, 6f);
        }
        Ui.TextColored(valueColor, value);
        ImGui.SameLine(0f, 10f);
    }

    // A vertical rule between cells (drawn as a thin separator-coloured bar via a disabled glyph).
    private static void Sep()
    {
        Ui.TextDisabled("|");
        ImGui.SameLine(0f, 10f);
    }

    private static void DrawPerf(GameContext ctx, Vector2 posRight)
    {
        ImGui.SetNextWindowPos(posRight, ImGuiCond.Always, new Vector2(1.0f, 0.0f));
        ImGui.SetNextWindowBgAlpha(0.30f);
        if (!ImGui.Begin("##hud_perf", Flags)) { ImGui.End(); return; }

        float fps = ImGui.GetIO().Framerate;
        Vector2 display = ImGui.GetIO().DisplaySize;
        var g = ctx.Config.Graphics;

        int rw = Math.Max((int)Math.Round(display.X * g.RenderScale), 1);
        int rh = Math.Max((int)Math.Round(display.Y * g.RenderScale), 1);

        // One horizontal row: FPS | render resolution | MSAA | draw/cull counts, separated by thin rules.
        Ui.Text($"{fps,4:F0} FPS");
        PerfSep();
        Ui.TextDisabled($"{rw}x{rh} ({g.RenderScale:0.00}x)");
        PerfSep();
        Ui.TextDisabled(g.MsaaSamples > 1 ? $"MSAA {g.MsaaSamples}x" : "MSAA off");
        PerfSep();
        Ui.TextDisabled($"draw {ctx.DrawnObjects}  cull {ctx.CulledObjects}");

        // Publish this overlay's bottom edge so docked tool panels on the right stack clear of it.
        HudDock.ReportTopOverlay(left: false, ImGui.GetWindowPos().Y + ImGui.GetWindowHeight());

        ImGui.End();
    }

    // A thin separator between perf cells, kept on the same row.
    private static void PerfSep()
    {
        ImGui.SameLine(0f, 10f);
        Ui.TextDisabled("|");
        ImGui.SameLine(0f, 10f);
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "--";
        long s = (long)seconds;
        long days = s / 86400; s %= 86400;
        long h = s / 3600; s %= 3600;
        long m = s / 60; s %= 60;
        if (days > 0) return $"{days}d {h:00}:{m:00}:{s:00}";
        return $"{h:00}:{m:00}:{s:00}";
    }

    private static string FormatDistance(double metres)
    {
        if (Math.Abs(metres) >= 1.0e6) return $"{metres / 1.0e3:N0} km";
        return $"{metres / 1.0e3:0.0} km";
    }

    private static string FormatAltitudeOrDash(double metres)
    {
        if (double.IsNaN(metres) || double.IsInfinity(metres)) return "--";
        return FormatDistance(metres);
    }

    private static string FormatSpeed(double mps)
    {
        if (mps >= 1000.0) return $"{mps / 1000.0:0.000} km/s";
        return $"{mps:0.0} m/s";
    }
}
