using System;
using System.Collections.Generic;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.Simulation;

public enum AiSeverity { Info, Caution, Warning }

/// <summary>One entry in the ship AI's activity log: a message, and what the AI did about it.</summary>
public sealed class AiEvent
{
    public double Time;
    public AiSeverity Severity;
    public string Text = "";
    public string? Response;
}

/// <summary>
/// The vessel's own crew / flight-computer layer. It watches the ship every frame, keeps a live
/// activity readout and g-load, raises graded warnings, and takes protective action on its own -
/// the ship's continued survival comes first, so the player can command at the task level and
/// leave the housekeeping to the crew. v1 safeguards:
///   - thermal governor: throttle is clamped to what the radiators can actually reject;
///   - propellant reserve: the main drive (and any running autopilot) is secured before the tanks
///     run dry, holding a configurable reserve for attitude and contingencies;
///   - graded low-state warnings (propellant, delta-v) and a sustained-g caution.
/// God mode stands the safeguards down (readouts stay live). Pure state machine - no UI, no GL.
/// </summary>
public sealed class ShipAI
{
    /// <summary>Master switch for the autonomous safeguards (readouts always run).</summary>
    public bool Enabled = true;

    /// <summary>Fraction of propellant capacity the crew refuses to burn below (default 5%).</summary>
    public double PropellantReserveFrac = 0.05;

    public string Activity { get; private set; } = "Idle";
    public double GLoad { get; private set; }      // current acceleration, in g
    public double HeatFrac { get; private set; }   // heat load / rejection capacity
    public double PropFrac { get; private set; }   // propellant / capacity
    public double DeltaV { get; private set; }     // remaining delta-v (m/s)

    public IReadOnlyList<AiEvent> Log => _log;
    private readonly List<AiEvent> _log = new();
    private const int MaxLog = 40;

    private bool _governing, _reserveCut, _warnedProp, _warnedDv, _warnedG;

    private void Add(double t, AiSeverity sev, string text, string? response = null)
    {
        _log.Insert(0, new AiEvent { Time = t, Severity = sev, Text = text, Response = response });
        if (_log.Count > MaxLog) _log.RemoveAt(_log.Count - 1);
    }

    /// <summary>External report channel into the same crew log (used by the orders layer).</summary>
    public void Report(double t, AiSeverity sev, string text, string? response = null)
        => Add(t, sev, text, response);

    public void Update(Spacecraft ship, PhysicsWorld world, FlipBurnPlan? flip, ManeuverNode? node,
                       bool combatActive, bool godMode, string? orderStatus = null)
    {
        RigidBody? v = world.PrimaryVessel;
        double t = world.SimTime;
        double g0 = MathConstants.StandardGravity;

        // --- live state ---
        double cap = ship.PropellantCapacity;
        PropFrac = cap > 0.0 ? ship.PropellantMass / cap : 0.0;
        DeltaV = ship.DeltaV;
        bool wantsBurn = ship.Mode != ThrustMode.None && ship.Throttle > 0.0 && ship.PropellantMass > 0.0;

        bool safeguards = Enabled && !godMode;

        // --- propellant reserve: secure the drive (and stand autopilots down) before running dry ---
        if (safeguards && cap > 0.0 && PropFrac <= PropellantReserveFrac && wantsBurn)
        {
            if (!_reserveCut)
            {
                _reserveCut = true;
                Add(t, AiSeverity.Warning,
                    $"Propellant at reserve ({PropellantReserveFrac:P0}).",
                    "Main drive secured; autopilots stood down; reserve held for attitude.");
            }
            ship.Mode = ThrustMode.None;
            if (node != null) { node.Armed = false; node.Burning = false; }
            if (flip is { Active: true })
            {
                flip.Active = false;
                flip.Phase = "Aborted (propellant reserve)";
            }
            wantsBurn = false;
        }
        if (_reserveCut && PropFrac > PropellantReserveFrac + 0.02)
        {
            _reserveCut = false;
            Add(t, AiSeverity.Info, "Propellant replenished above reserve.", "Main drive released to command.");
        }

        // --- graded low-state warnings ---
        if (safeguards && cap > 0.0 && PropFrac <= 0.15 && PropFrac > PropellantReserveFrac && !_warnedProp)
        {
            _warnedProp = true;
            Add(t, AiSeverity.Caution, $"Propellant 15% - reserve cut-off at {PropellantReserveFrac:P0}.");
        }
        if (PropFrac > 0.18) _warnedProp = false;

        if (safeguards && DeltaV > 0.0 && DeltaV < 500.0 && !_warnedDv)
        {
            _warnedDv = true;
            Add(t, AiSeverity.Caution, $"Remaining delta-v {DeltaV:N0} m/s - plan maneuvers accordingly.");
        }
        if (DeltaV > 800.0) _warnedDv = false;

        // --- thermal governor: never command more throttle than the radiators can reject ---
        double sustainable = ship.SustainableThrottle;
        if (safeguards && wantsBurn && ship.Throttle > sustainable + 1e-6)
        {
            ship.Throttle = sustainable;
            if (!_governing)
            {
                _governing = true;
                Add(t, AiSeverity.Warning, "Radiators saturated - drive heat exceeds rejection.",
                    $"Throttle governed to {sustainable:P0} (heat-sustainable).");
            }
            if (sustainable <= 0.0)
            {
                ship.Mode = ThrustMode.None;
                wantsBurn = false;
            }
        }
        else if (_governing && (!wantsBurn || ship.Throttle <= sustainable + 1e-6))
        {
            _governing = false;
            Add(t, AiSeverity.Info, "Thermal margin restored.", "Throttle authority returned.");
        }

        // --- final readouts (post-safeguards) ---
        double thrust = wantsBurn ? ship.Throttle * ship.TotalThrustVac : 0.0;
        double mass = v?.Mass ?? ship.TotalMass;
        GLoad = mass > 0.0 ? thrust / mass / g0 : 0.0;
        double rej = ship.HeatRejectionCapacity;
        HeatFrac = rej > 0.0
            ? (ship.ReactorHeat + (wantsBurn ? ship.EngineHeatFull * ship.Throttle : 0.0)) / rej
            : 0.0;

        if (safeguards && GLoad > 3.0 && !_warnedG)
        {
            _warnedG = true;
            Add(t, AiSeverity.Caution, $"Sustained {GLoad:F1} g acceleration.", "Crew secured on couches.");
        }
        if (GLoad < 2.5) _warnedG = false;

        // --- activity readout (an active order's status takes priority; safeguard tags still append) ---
        string act;
        if (combatActive) act = "Fleet engagement in progress (combat AI in command)";
        else if (orderStatus != null) act = orderStatus;
        else if (flip is { Active: true }) act = $"Flip-and-burn autopilot: {flip.Phase}";
        else if (node is { Burning: true }) act = $"Maneuver burn: {node.DeliveredDv:N0} / {node.Magnitude:N0} m/s";
        else if (node is { Armed: true }) act = "Maneuver armed - awaiting node";
        else if (wantsBurn) act = $"Burning {ship.Mode} at {ship.Throttle:P0} throttle";
        else act = "Coasting - systems nominal";
        if (_governing) act += "  [heat-governed]";
        if (_reserveCut) act += "  [reserve hold]";
        Activity = act;
    }
}
