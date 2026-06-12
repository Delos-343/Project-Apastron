using System;
using System.Numerics;
using ImGuiNET;
using Apastron.Physics;
using Apastron.Simulation;

namespace Apastron.App;

/// <summary>
/// The ship's crew/flight-computer readout: live activity, acceleration in g, heat and
/// propellant state, the autonomy controls (master switch + propellant reserve), and the
/// AI's activity log - each warning paired with the action the AI took in response.
/// ASCII-only strings (the default ImGui font has no glyphs above Latin-1).
/// </summary>
public static class ShipAiPanel
{
    private static readonly Vector4 Green = new(0.55f, 0.85f, 0.55f, 1.0f);
    private static readonly Vector4 Amber = new(0.95f, 0.75f, 0.35f, 1.0f);
    private static readonly Vector4 Red   = new(0.95f, 0.45f, 0.40f, 1.0f);

    private static float _altKm = 1000.0f;   // "Change orbit" target altitude (km)

    // Wrapping button-flow: lays buttons left-to-right and wraps to a new line when the next one would
    // overflow the content region, so the order buttons never run off the edge of the narrow sidebar.
    private static float _flowX, _flowW;
    private static void FlowReset() { _flowX = 0f; _flowW = ImGui.GetContentRegionAvail().X; }
    private static bool FlowButton(string label)
    {
        float w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
        float sp = ImGui.GetStyle().ItemSpacing.X;
        if (_flowX > 0f && _flowX + sp + w <= _flowW) { ImGui.SameLine(); _flowX += sp + w; }
        else { _flowX = w; }   // first button on a line, or wrap onto a new one
        return ImGui.Button(label);
    }

    public static void Body(GameContext ctx)
    {
        ShipAI ai = ctx.ShipAI;

        Ui.TextColored(Theme.Accent, ai.Activity);
        ImGui.Spacing();

        // --- live readouts ---
        Vector4 gCol = ai.GLoad > 3.0 ? Amber : Theme.Text;
        Vector4 heatCol = ai.HeatFrac > 0.999 ? Red : ai.HeatFrac > 0.85 ? Amber : Green;
        Vector4 propCol = ai.PropFrac <= ai.PropellantReserveFrac + 0.001 ? Red
                        : ai.PropFrac <= 0.15 ? Amber : Green;

        Ui.TextColored(gCol, $"Accel       {ai.GLoad:F2} g");
        Ui.TextColored(heatCol, $"Heat load   {ai.HeatFrac * 100.0:F0}% of rejection");
        Ui.TextColored(propCol, $"Propellant  {ai.PropFrac * 100.0:F0}%  (reserve {ai.PropellantReserveFrac:P0})");
        Ui.Text($"Delta-v     {ai.DeltaV:N0} m/s");

        ImGui.Spacing();
        ImGui.Separator();
        Ui.TextDisabled("Orders");

        ShipOrders orders = ctx.Orders;
        if (orders.Current != null)
        {
            Ui.TextColored(Theme.Accent, $"Tasked: {orders.Current.Label}");
            ImGui.PushTextWrapPos();
            Ui.TextDisabled(orders.Status);
            ImGui.PopTextWrapPos();
            if (ImGui.Button("Cancel order")) orders.CancelRequested = true;
            if (orders.Queue.Count > 0)
            {
                ImGui.SameLine();
                if (ImGui.Button($"Clear queue ({orders.Queue.Count})")) orders.ClearQueue();
            }
        }
        else
        {
            Ui.TextDisabled("No active order. Clicks below start a task (or queue one).");
        }
        ImGui.Spacing();

        FlowReset();
        if (FlowButton("Hold orbit"))
            orders.Issue(new ShipOrder { Kind = OrderKind.HoldOrbit, Label = "Hold orbit" });
        if (FlowButton("Circularize"))
            orders.Issue(new ShipOrder { Kind = OrderKind.Circularize, Label = "Circularize orbit" });

        ImGui.SetNextItemWidth(-1.0f);
        ImGui.SliderFloat("##orderalt", ref _altKm, 200.0f, 40000.0f, "%.0f km", ImGuiSliderFlags.Logarithmic);
        if (ImGui.Button("Change orbit", new Vector2(-1.0f, 0.0f)))
            orders.Issue(new ShipOrder
            {
                Kind = OrderKind.ChangeOrbit,
                Label = $"Change orbit to {_altKm:N0} km",
                TargetAltitude = _altKm * 1000.0,
            });

        if (ctx.World.TargetVessel != null)
        {
            if (ImGui.Button("Rendezvous with target"))
                orders.Issue(new ShipOrder { Kind = OrderKind.RendezvousTarget, Label = "Rendezvous with target" });
        }
        else
        {
            Ui.TextDisabled("Rendezvous: no target (spawn one in the Rendezvous panel).");
        }

        RigidBody? vessel = ctx.World.PrimaryVessel;
        CelestialBody? near = vessel != null ? ctx.World.DominantBody(vessel.Position) : null;
        bool anyDest = false;
        FlowReset();
        foreach (CelestialBody b in ctx.World.Bodies)
        {
            if (ReferenceEquals(b, near)) continue;
            anyDest = true;
            if (FlowButton($"Torch to {b.Name}"))
                orders.Issue(new ShipOrder
                {
                    Kind = OrderKind.TorchTransfer,
                    Label = $"Torch transfer to {b.Name}",
                    BodyName = b.Name,
                });
        }
        if (!anyDest)
            Ui.TextDisabled("Torch transfers: no interplanetary destinations here.");

        bool anyStation = false;
        FlowReset();
        foreach (RigidBody rb in ctx.World.Vessels)
        {
            if (!rb.IsStation) continue;
            anyStation = true;
            if (FlowButton($"Dock: {rb.Name}"))
                orders.Issue(new ShipOrder
                {
                    Kind = OrderKind.DockAt,
                    Label = $"Dock & replenish at {rb.Name}",
                    StationName = rb.Name,
                });
        }
        if (!anyStation)
            Ui.TextDisabled("Docking: no stations in this scenario.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // --- autonomy controls ---
        ImGui.Checkbox("Crew autonomy (safeguards)", ref ai.Enabled);
        float reserve = (float)(ai.PropellantReserveFrac * 100.0);
        ImGui.SetNextItemWidth(200.0f);
        if (ImGui.SliderFloat("Propellant reserve", ref reserve, 0.0f, 20.0f, "%.0f %%"))
            ai.PropellantReserveFrac = reserve / 100.0;
        if (!ai.Enabled)
            Ui.TextColored(Amber, "Safeguards stood down - manual responsibility.");

        ImGui.Spacing();
        ImGui.Separator();
        Ui.TextDisabled("Activity log");

        // Fixed-height scrolling log so the section sits as a tidy paragraph within the sidebar
        // (a zero-size BeginChild would try to fill the whole sidebar and fight the outer scroll).
        ImGui.BeginChild("##ailog", new Vector2(0f, 168f), ImGuiChildFlags.Border);
        ImGui.PushTextWrapPos(0.0f);   // wrap long log lines at the child's right edge instead of clipping
        foreach (AiEvent ev in ai.Log)
        {
            Vector4 col = ev.Severity switch
            {
                AiSeverity.Warning => Red,
                AiSeverity.Caution => Amber,
                _ => Theme.Text,
            };
            Ui.TextColored(col, $"[{FormatTime(ev.Time)}] {ev.Text}");
            if (ev.Response != null)
                Ui.TextDisabled($"    AI> {ev.Response}");
        }
        if (ai.Log.Count == 0)
            Ui.TextDisabled("No events - all systems nominal.");
        ImGui.PopTextWrapPos();
        ImGui.EndChild();
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(seconds, 0.0));
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
