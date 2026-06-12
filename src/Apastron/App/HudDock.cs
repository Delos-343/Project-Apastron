using System.Numerics;
using ImGuiNET;

namespace Apastron.App;

/// <summary>Which window corner a HUD panel docks into.</summary>
public enum HudCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Anchors HUD tool windows to the application window's corners with consistent edge margins and
/// vertical stacking, recomputed every frame so the layout stays glued to the edges as the window is
/// resized — a fixed, sci-fi-HUD layout rather than free-floating panels. Panels call
/// <see cref="Place"/> immediately before <c>ImGui.Begin</c> in place of setting their own position and
/// size. Pure ImGui layout work (no GL state), so it cannot destabilise the build.
///
/// The top edge of each side is reserved independently: the always-on flight readout occupies the
/// top-left and the performance readout the top-right (both drawn by <see cref="Hud"/>), so docked
/// tool panels must start below whichever overlay is on their side. <see cref="Hud"/> reports the
/// live bottom edge of each overlay via <see cref="ReportTopOverlay"/> every frame, before the panels are
/// placed, so the reserve tracks the real overlay extent (which changes with content and font scale)
/// instead of a fixed guess. A panel taller than the band it docks into is shrunk to fit so it scrolls
/// internally rather than overrunning the opposite edge or sliding under the menu bar.
/// </summary>
public static class HudDock
{
    private const float Margin     = 18f;   // spacing from the window edge
    private const float Gap        = 10f;   // spacing between stacked panels in the same corner
    private const float MenuBar    = 30f;   // height kept clear for the main menu bar along the top
    private const float OverlayGap = 12f;   // breathing room between a top overlay and the panels below it

    private static float _w, _h;
    private static float _tlTop, _trTop;       // top-corner cursors grow downward
    private static float _blBottom, _brBottom; // bottom-corner cursors grow upward
    private static float _leftReserve, _rightReserve;   // top space taken by the flight / perf overlays

    // Bottom edge (absolute Y) of each top overlay as measured by Hud this frame (left = flight readout,
    // right = perf readout). Carried across frames so a one-frame settle after a resize doesn't momentarily
    // collapse the reserve to zero. Using the measured bottom (rather than menu-bar-const + height) keeps the
    // reserve correct even when the real menu bar is taller than the constant guess at large font scales.
    private static float _leftOverlayBottom, _rightOverlayBottom;

    /// <summary>
    /// Called by <see cref="Hud"/> for each top overlay it draws, reporting that overlay's measured bottom
    /// edge (absolute Y) so docked panels on the same side can be kept clear of it. <paramref name="left"/>
    /// selects the top-left (flight) band when true, the top-right (perf) band when false.
    /// </summary>
    public static void ReportTopOverlay(bool left, float bottomY)
    {
        if (bottomY <= 0f) return;
        if (left) _leftOverlayBottom = bottomY; else _rightOverlayBottom = bottomY;
    }

    /// <summary>Edge margin used for all docked HUD elements (window-edge inset).</summary>
    public static float EdgeMargin => Margin;
    /// <summary>Live window width in pixels (valid after <see cref="BeginFrame"/>).</summary>
    public static float WinW => _w;
    /// <summary>Live window height in pixels (valid after <see cref="BeginFrame"/>).</summary>
    public static float WinH => _h;
    /// <summary>Top space reserved on the left (menu bar + flight strip), in pixels.</summary>
    public static float LeftReserve => _leftReserve;
    /// <summary>Top space reserved on the right (menu bar + perf readout), in pixels.</summary>
    public static float RightReserve => _rightReserve;

    /// <summary>Reset the per-corner stacking cursors for a new frame. Call once before drawing panels
    /// (but after <see cref="Hud"/> has reported its overlay heights for the frame).</summary>
    public static void BeginFrame()
    {
        Vector2 d = ImGui.GetIO().DisplaySize;
        _w = d.X;
        _h = d.Y;

        // Reserve down to the measured bottom of whichever overlay sits on each side, plus a small gap.
        // The first frame (before Hud has reported) falls back to the menu bar alone; it self-corrects next frame.
        float topBase = MenuBar + Margin;
        _leftReserve  = _leftOverlayBottom  > 0f ? _leftOverlayBottom  + OverlayGap : topBase;
        _rightReserve = _rightOverlayBottom > 0f ? _rightOverlayBottom + OverlayGap : topBase;

        _tlTop    = _leftReserve;
        _trTop    = _rightReserve;
        _blBottom = _h - Margin;
        _brBottom = _h - Margin;
    }

    /// <summary>
    /// Size and position the next window so it docks into <paramref name="corner"/>, stacked clear of any
    /// earlier panels placed in that corner this frame and clear of the top overlay on its side. Sizes are
    /// fixed (locked) for a clean HUD feel, but are clamped to the live window so panels stay fully usable
    /// (scrolling internally) on small windows or when several tall panels share a side.
    /// </summary>
    public static void Place(HudCorner corner, float w, float h)
    {
        bool left = corner is HudCorner.TopLeft or HudCorner.BottomLeft;
        float reserve = left ? _leftReserve : _rightReserve;

        // Horizontal clamp: never wider than the window's usable width.
        float maxW = _w - 2f * Margin;
        if (maxW > 50f && w > maxW) w = maxW;

        float x = left ? Margin : _w - Margin - w;
        float y;

        switch (corner)
        {
            case HudCorner.TopLeft:
            case HudCorner.TopRight:
            {
                // Top stacks grow downward from the reserve; clamp height to the space remaining above
                // the bottom margin so a tall panel scrolls internally instead of running off-screen.
                float cursor = corner == HudCorner.TopLeft ? _tlTop : _trTop;
                float avail = _h - Margin - cursor;
                if (avail > 50f && h > avail) h = avail;
                y = cursor;
                if (corner == HudCorner.TopLeft) _tlTop += h + Gap; else _trTop += h + Gap;
                break;
            }
            default: // BottomLeft / BottomRight
            {
                // Bottom stacks grow upward from the bottom margin; clamp height to the space remaining
                // above the previous panel in this corner (down to the top reserve), so panels sharing a
                // side never overlap and the tallest simply scrolls within its slot.
                bool bl = corner == HudCorner.BottomLeft;
                float bottom = bl ? _blBottom : _brBottom;
                float avail = bottom - reserve;
                if (avail > 50f && h > avail) h = avail;
                y = bottom - h;
                if (y < reserve) y = reserve;
                if (bl) _blBottom = y - Gap; else _brBottom = y - Gap;
                break;
            }
        }

        ImGui.SetNextWindowSize(new Vector2(w, h), ImGuiCond.Always);
        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
    }
}
