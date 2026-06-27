using System;
using Apastron.Audio;
using Apastron.Combat;
using Apastron.Config;
using Apastron.Physics;
using Apastron.Render;
using Apastron.Simulation;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Shared mutable state passed to every UI panel each frame. Window-visibility flags
/// are public FIELDS (not properties) so they can be passed to ImGui by <c>ref</c>.
/// </summary>
public enum AppScreen { Title, Sandbox, Mission }

public sealed class GameContext
{
    /// <summary>Top-level screen: title menu, free-flight sandbox, or a combat mission.</summary>
    public AppScreen Screen = AppScreen.Title;

    public required PhysicsWorld World  { get; set; }
    public required AppConfig    Config { get; init; }

    /// <summary>Detected GPU profile, if available.</summary>
    public DeviceProfile? Device { get; set; }

    /// <summary>Invoked when the user asks for a fresh scenario (wired up in Program).</summary>
    public Action? ResetScenario;

    /// <summary>Save / load the current scenario to disk (wired up in Program).</summary>
    public Action? SaveScenario;
    public Action? LoadScenario;

    /// <summary>Swap to the interplanetary scenario (Sun + planets + a torchship).</summary>
    public Action? NewInterplanetary;

    /// <summary>Swap to the deep-space duel scenario and arm both ships (wired up in Program).</summary>
    public Action? NewDuel;

    /// <summary>Active live engagement, if any (set when entering the duel scenario).</summary>
    public CombatManager? Combat;

    /// <summary>Homeworld-style fleet move orders (selection-driven RTS movement).</summary>
    public FleetManager Fleet { get; } = new();

    /// <summary>Currently selected vessels (RTS selection set). Drives move orders and selection brackets.</summary>
    public readonly System.Collections.Generic.HashSet<RigidBody> Selection = new();

    /// <summary>Formation the selection adopts around a move target.</summary>
    public FleetFormation Formation = FleetFormation.None;

    /// <summary>Hull class and doctrine used by the Fleet panel's reinforcement spawner.</summary>
    public Apastron.Vehicles.HullClass SpawnClass = Apastron.Vehicles.HullClass.Corvette;
    public Apastron.Combat.CombatDoctrine SpawnDoctrine = Apastron.Combat.CombatDoctrine.Aggressive;

    /// <summary>Hull classes the sandbox duel arms each side with (applied on the next Reset/Arm duel).</summary>
    public Apastron.Vehicles.HullClass DuelClassA = Apastron.Vehicles.HullClass.Destroyer;
    public Apastron.Vehicles.HullClass DuelClassB = Apastron.Vehicles.HullClass.Destroyer;
    public bool DuelHard = false;   // false: 1-3 pirate raiders; true: a single randomized near-peer warship

    /// <summary>Control groups 1-9 (index 0 unused): Ctrl+digit assigns the selection, digit recalls it.</summary>
    public readonly System.Collections.Generic.HashSet<RigidBody>[] Groups = MakeGroups();

    private static System.Collections.Generic.HashSet<RigidBody>[] MakeGroups()
    {
        var g = new System.Collections.Generic.HashSet<RigidBody>[10];
        for (int i = 0; i < g.Length; i++) g[i] = new System.Collections.Generic.HashSet<RigidBody>();
        return g;
    }

    /// <summary>Active mission state (objective, win/lose, score) when Screen == Mission.</summary>
    public MissionState? Mission;

    /// <summary>Audio backend (silent by default; see README to enable real sound).</summary>
    public IAudio Audio = new SilentAudio();

    /// <summary>Start a mission with the given definition (wired in Program).</summary>
    public Action<MissionDef>? StartMission;
    /// <summary>Restart the current mission from the briefing state (wired in Program).</summary>
    public Action? RestartMission;
    /// <summary>Return to the title screen, tearing down any mission/combat (wired in Program).</summary>
    public Action? ExitToTitle;

    /// <summary>Mission-mode pause overlay.</summary>
    public bool ShowPauseMenu;


    /// <summary>Active in-world flip-and-burn transfer, if any.</summary>
    public FlipBurnPlan? FlipPlan;

    /// <summary>Transient status line shown in the menu bar; counts down via StatusTtl.</summary>
    public string StatusMessage = "";
    public double StatusTtl;

    /// <summary>Render diagnostics from the last frame (objects drawn vs frustum-culled).</summary>
    public int DrawnObjects;
    public int CulledObjects;

    // --- window toggles (fields, for ImGui ref) ---
    public bool ShowTelemetry    = true;
    public bool ShowSettings     = false;
    public bool ShowAbout        = false;
    public bool ShowDemoControls = true;
    public bool ShowShipAi       = true;
    public bool ShowCamera       = true;
    public bool ShowHud          = true;
    public bool ShowBuilder      = false;
    public bool ShowManeuver     = false;
    public bool ShowRendezvous   = false;
    public bool ShowTorch        = false;
    public bool ShowWeapons      = false;
    public bool ShowCombat       = false;
    public bool ShowFleet        = true;
    public bool RequestExit      = false;

    // --- sidebar drawer state (the merged left/right control menus) ---
    /// <summary>Whether the left control drawer (flight &amp; ops) is expanded. When false it
    /// collapses to a thin edge tab that reopens it.</summary>
    public bool LeftSidebarOpen  = true;
    /// <summary>Whether the right control drawer (view, data &amp; build) is expanded.</summary>
    public bool RightSidebarOpen = true;

    /// <summary>Camera / 3D-view preferences, shared with the renderer.</summary>
    public ViewSettings View = new();

    /// <summary>The active parts-based spacecraft; its mass drives the physics vessel.</summary>
    // Sandbox "God mode": eliminates spacecraft limitations (propellant runs unlimited, the
    // radiator heat cap on autopilot burns is lifted, and transfer feasibility gating is bypassed).
    public bool GodMode = false;

    public Spacecraft Ship = Spacecraft.CreateDuat();

    /// <summary>The primary vessel's crew/flight-computer autonomy layer (Ship AI panel).</summary>
    public ShipAI ShipAI = new();

    /// <summary>The primary vessel's task-level order queue, executed by the ship AI.</summary>
    public ShipOrders Orders = new();

    /// <summary>The planned maneuver node, if any.</summary>
    public ManeuverNode? Node;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }
    private bool _paused;

    /// <summary>Time-warp multiplier, mirrored into the persisted config.</summary>
    public double TimeScale
    {
        get => Config.Simulation.TimeScale;
        set => Config.Simulation.TimeScale = value;
    }
}
