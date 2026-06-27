using System;
using ImGuiNET;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Simulation;

namespace Apastron.App;

/// <summary>Live read-out of the primary vessel's orbital state.</summary>
public static class TelemetryWindow
{
    public static void Body(GameContext ctx)
    {
        PhysicsWorld world = ctx.World;
        RigidBody? vessel = world.PrimaryVessel;
        if (vessel == null)
        {
            Ui.TextDisabled("No active vessel.");
            return;
        }

        CelestialBody? body = world.DominantBody(vessel.Position);

        Row("Sim time", FormatTime(world.SimTime));
        Row("Time warp", $"x{ctx.TimeScale:0}");
        Row("Integrator", world.Integrator.Name);
        Row("Vessel", vessel.Name);
        Row("Mass", $"{vessel.Mass:N0} kg");

        ImGui.Spacing();
        Section("Primary body");
        if (body == null)
        {
            Ui.TextDisabled("None in range.");
            return;
        }
        Row("Body", body.Name);
        Row("mu", $"{body.Mu:E4} m\u00b3/s\u00b2");

        var oe = OrbitalElements.Compute(vessel.Position, vessel.Velocity, body.Position, body.Mu, body.Radius);

        ImGui.Spacing();
        Section("Orbit");
        Row("Altitude", $"{oe.Altitude / 1000.0:N3} km");
        Row("Speed", $"{oe.Speed:N3} m/s");
        Row("Apoapsis", oe.IsBound ? $"{(oe.Apoapsis - body.Radius) / 1000.0:N3} km" : "unbound");
        Row("Periapsis", $"{(oe.Periapsis - body.Radius) / 1000.0:N3} km");
        Row("Semi-major", $"{oe.SemiMajorAxis / 1000.0:N3} km");
        Row("Eccentricity", $"{oe.Eccentricity:F5}");
        Row("Inclination", $"{oe.Inclination * MathConstants.RadToDeg:F3}\u00b0");
        Row("Period", oe.IsBound ? FormatTime(oe.Period) : "unbound");
        Row("Spec. energy", $"{oe.SpecificEnergy / 1000.0:N3} kJ/kg");
        Row("Orbit type", oe.IsBound ? "closed (elliptic)" : "escape (hyperbolic)");

        ImGui.Spacing();
        Section("State vectors (world)");
        Row("Position", $"{vessel.Position}");
        Row("Velocity", $"{vessel.Velocity}");
        Row("Thrust", vessel.ThrustWorld.LengthSquared > 0 ? $"{vessel.ThrustWorld.Length:N1} N" : "off");
    }

    private static void Section(string label)
    {
        ImGui.Spacing();
        Ui.TextDisabled(label);
        ImGui.Separator();
    }

    private static void Row(string label, string value)
    {
        Ui.TextDisabled(label);
        ImGui.SameLine(150f);
        ImGui.TextUnformatted(value);
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsInfinity(seconds)) return "\u221e";
        long s = (long)Math.Round(seconds);
        long h = s / 3600; s %= 3600;
        long m = s / 60;   s %= 60;
        if (h > 0) return $"{h:D2}:{m:D2}:{s:D2}";
        return $"{m:D2}:{s:D2}";
    }
}
