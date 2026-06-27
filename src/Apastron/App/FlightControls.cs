using System.Numerics;
using ImGuiNET;
using Apastron.Diagnostics;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Manual flight: pick a throttle and a persistent thrust direction (prograde / retrograde
/// / radial). Thrust and propellant burn are applied by the Propulsion bridge each step, so
/// firing visibly depletes delta-v and reshapes the orbit. Maneuver nodes and burn planning
/// arrive in the gameplay chunk.
/// </summary>
public static class FlightControls
{
    /// <summary>Emits the flight-controls content into the current window (the left sidebar). The
    /// caller owns the window chrome and visibility; this draws widgets only.</summary>
    public static void Body(GameContext ctx)
    {
        RigidBody? vessel = ctx.World.PrimaryVessel;
        Spacecraft ship = ctx.Ship;

        if (vessel == null)
        {
            Ui.TextDisabled("No active vessel.");
            return;
        }

        if (ship.TotalThrustVac <= 0.0)
        {
            Ui.TextWrapped("The vessel has no engine. Add one in the Spacecraft Builder.");
            return;
        }

        // throttle
        float throttle = (float)(ship.Throttle * 100.0);
        if (ImGui.SliderFloat("Throttle", ref throttle, 0.0f, 100.0f, "%.0f %%"))
            ship.Throttle = throttle / 100.0;

        // direction (sets a persistent mode; firing with zero throttle does nothing,
        // so nudge to full if the user hits a burn button at idle)
        ImGui.Spacing();
        if (ImGui.Button("Prograde"))   SetMode(ship, ThrustMode.Prograde);
        ImGui.SameLine();
        if (ImGui.Button("Retrograde")) SetMode(ship, ThrustMode.Retrograde);

        if (ImGui.Button("Radial Out")) SetMode(ship, ThrustMode.RadialOut);
        ImGui.SameLine();
        if (ImGui.Button("Radial In"))  SetMode(ship, ThrustMode.RadialIn);

        ImGui.Spacing();
        if (ImGui.Button("Cut Thrust")) ship.Mode = ThrustMode.None;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        string mode = ship.Mode == ThrustMode.None ? "coasting" : ship.Mode.ToString();
        bool burning = ship.Mode != ThrustMode.None && ship.Throttle > 0.0 && ship.PropellantMass > 0.0;
        Ui.Text("Mode      ");
        ImGui.SameLine();
        if (burning) Ui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), $"BURN {mode}");
        else         Ui.TextDisabled(mode);

        Ui.Text($"Thrust    {vessel.ThrustWorld.Length / 1000.0:F1} kN");
        Ui.Text($"Accel     {vessel.ThrustAcceleration.Length:F4} m/s\u00b2");
        Ui.Text($"Mass      {vessel.Mass:N0} kg");

        double pct = ship.PropellantCapacity > 0.0 ? 100.0 * ship.PropellantMass / ship.PropellantCapacity : 0.0;
        Ui.Text($"Propellant {ship.PropellantMass:N0} kg ({pct:F0}%)");
        if (ship.PropellantMass <= 0.0)
            Ui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), "Out of propellant - Refuel in Builder");

        Ui.Text($"Delta-v    {ship.DeltaV:N0} m/s remaining");
    }

    private static void SetMode(Spacecraft ship, ThrustMode mode)
    {
        CrashLog.Phase($"thrust button pressed ({mode})");
        ship.Mode = mode;
        if (ship.Throttle <= 0.0) ship.Throttle = 1.0;
    }
}
