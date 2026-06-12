using System;
using System.IO;
using System.Linq;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using ImGuiNET;
using Apastron.App;
using Apastron.Combat;
using Apastron.Config;
using Apastron.Physics;
using Apastron.Render;
using Apastron.Vehicles;
using Apastron.Simulation;
using Apastron.Diagnostics;

namespace Apastron;

/// <summary>
/// Application entry point. Creates the window + OpenGL 3.3 core context, wires Dear
/// ImGui, builds the default scenario, and runs the update/render loop. Pass
/// <c>--selftest</c> (or <c>-t</c>) to run the headless physics accuracy check instead.
/// </summary>
public static class Program
{
    private static IWindow         _window  = null!;
    private static GL              _gl      = null!;
    private static IInputContext   _input   = null!;
    private static ImGuiController _imgui   = null!;
    private static AppConfig       _config  = null!;
    private static GameContext     _ctx     = null!;
    private static SceneRenderer   _scene   = null!;
    private static IMouse?         _mouse;

    /// <summary>--gldebug: request a debug GL context and stream KHR_debug messages to crash.log.
    /// Synchronous debug output also forces the driver onto its single-threaded, validated path,
    /// which both instruments and (often) sidesteps multithreaded-driver faults.</summary>
    private static bool _glDebug;

    // Rooted in a static field so the native->managed thunk can never be garbage-collected while
    // the driver still holds the pointer; an un-rooted GL callback is itself a process-killer.
    private static DebugProc? _glDebugProc;

    private static void EnableGlDebugOutput()
    {
        try
        {
            _glDebugProc = OnGlDebugMessage;
            _gl.Enable(EnableCap.DebugOutput);
            _gl.Enable(EnableCap.DebugOutputSynchronous);
            unsafe { _gl.DebugMessageCallback(_glDebugProc, null); }
            CrashLog.Note("gldebug", "KHR_debug output enabled (synchronous)");
        }
        catch (Exception ex)
        {
            CrashLog.Report("gldebug", ex);   // missing extension etc.: diagnostics never block startup
        }
    }

    private static void OnGlDebugMessage(GLEnum source, GLEnum type, int id, GLEnum severity,
                                         int length, nint message, nint userParam)
    {
        string text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(message, length) ?? "";
        CrashLog.Note($"GLDBG:{severity}", $"{type} from {source} #{id}: {text}");
    }

    public static void Main(string[] args)
    {
        foreach (var a in args)
        {
            if (a is "--selftest" or "-t")
            {
                PhysicsSelfTest.Run();
                return;
            }
            if (a is "--gldebug") _glDebug = true;
        }

        // Read the PREVIOUS session's tail before Install() appends this session's header: a run
        // that fast-failed on the first burning frame (or inside shader warm-up) carries the
        // native-driver-fault signature, and engages the driver-safe fallback so the game is
        // playable again on the very next launch.
        bool diedMidBurn = CrashLog.PreviousSessionDiedDuringFirstBurn();

        CrashLog.Install();
        _config = AppConfig.Load();

        if (diedMidBurn && !_config.Graphics.DriverSafeMode)
        {
            _config.Graphics.DriverSafeMode = true;
            _config.Save();
            CrashLog.Note("driver-safe-mode",
                "Previous session fast-failed mid-frame on the first burn (native driver fault signature). " +
                "Additive plume shader and film grade are disabled until cleared from Settings > Appearance " +
                "(recommended after updating the GPU driver).");
        }

        var options = WindowOptions.Default;
        options.Size  = new Vector2D<int>(1280, 720);
        options.Title = "Apastron";
        options.VSync = _config.Graphics.VSync;
        options.API   = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            _glDebug ? ContextFlags.ForwardCompatible | ContextFlags.Debug
                     : ContextFlags.ForwardCompatible,
            new APIVersion(3, 3));

        _window = Window.Create(options);
        _window.Load              += OnLoad;
        _window.Update            += OnUpdate;
        _window.Render            += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing           += OnClose;

        try
        {
            _window.Run();
        }
        catch (Exception ex)
        {
            CrashLog.Report("Window.Run (fatal)", ex);
        }
        finally
        {
            _window.Dispose();
        }
    }

    private static void OnLoad()
    {
        _gl    = GL.GetApi(_window);
        if (_glDebug) EnableGlDebugOutput();
        _input = _window.CreateInput();
        _imgui = new ImGuiController(_gl, _window, _input);
        unsafe { ImGuiNET.ImGui.GetIO().NativePtr->IniFilename = null; }  // don't persist window layout; use our defaults
        ImGui.GetIO().FontGlobalScale = 1.35f;   // larger type for the high-DPI 15.6" 1440p laptop screen

        Theme.Apply(_config.Graphics.Palette);

        string vendor   = SafeGet(StringName.Vendor);
        string renderer = SafeGet(StringName.Renderer);
        string version  = SafeGet(StringName.Version);
        var device = DeviceProfile.FromStrings(vendor, renderer, version);

        var world = Scenarios.LowEarthOrbit();
        world.Integrator = SettingsWindow.MakeIntegrator(_config.Simulation.Integrator);

        _ctx = new GameContext { World = world, Config = _config, Device = device };

        // The physics vessel's mass is driven by the parts-based spacecraft design.
        if (world.PrimaryVessel != null)
        {
            world.PrimaryVessel.Mass = _ctx.Ship.TotalMass;
            world.PrimaryVessel.Forward = world.PrimaryVessel.Velocity.Normalized();
        }

        if (_config.Graphics.AutoDetect)
            device.ApplyTo(_config.Graphics);

        // Shared installer for switching scenarios (camera framing applied once _scene exists).
        void InstallWorld(PhysicsWorld fresh)
        {
            fresh.Integrator = SettingsWindow.MakeIntegrator(_config.Simulation.Integrator);
            if (fresh.PrimaryVessel != null)
            {
                fresh.PrimaryVessel.Mass = _ctx.Ship.TotalMass;
                if (fresh.PrimaryVessel.Velocity.Length > 1e-6)
                    fresh.PrimaryVessel.Forward = fresh.PrimaryVessel.Velocity.Normalized();
            }
            _ctx.World = fresh;
            _ctx.Node = null;
            _ctx.FlipPlan = null;
            _ctx.Combat = null;
            _ctx.View.FocusVesselIndex = -1;   // drop any per-vessel camera lock from the previous scenario
            _scene.FrameFor(fresh.CameraDistanceHint);
        }

        _ctx.ResetScenario = () =>
        {
            _ctx.Ship.Refill();
            _ctx.Ship.Mode = ThrustMode.None;
            InstallWorld(Scenarios.HighOrbit());
            _ctx.View.Focus = CameraFocus.Vessel;
        };

        _ctx.NewInterplanetary = () =>
        {
            _ctx.Ship = Spacecraft.CreateDuat();
            _ctx.Ship.Mode = ThrustMode.None;
            InstallWorld(Scenarios.Interplanetary());
        };

        _ctx.NewDuel = () =>
        {
            InstallWorld(Scenarios.Duel());   // also clears _ctx.Combat
            var world = _ctx.World;
            var shipA = Spacecraft.CreateWarship("Vanguard");
            var shipB = Spacecraft.CreateWarship("Corsair");
            world.Vessels[0].Mass = shipA.TotalMass;
            world.Vessels[1].Mass = shipB.TotalMass;

            var cm = new CombatManager { Active = true };
            cm.Sound = s => _ctx.Audio.Play(s);
            // Beyond-visual-range profile for the open-space duel, tuned to the Expanse weapon envelope:
            // open with torpedoes (>1000 km), trade railgun fire closing through ~1000 km, and only the
            // aggressor presses into mass-driver / point-defense knife range. (Missions keep the tighter
            // default profile.)
            cm.AggressiveRange  = 80.0e3;     // inside mass-driver (100 km) and railgun reach
            cm.StandoffRange    = 800.0e3;    // railgun + torpedo sniper, outside mass-driver reach
            cm.EvasiveRange     = 1500.0e3;   // run out past effective railgun range
            cm.ThreatReactRange = 300.0e3;
            cm.MissileSalvoCap  = 8;
            cm.Combatants.Add(MakeCombatant("Vanguard", world.Vessels[0], shipA, CombatDoctrine.Aggressive));
            cm.Combatants.Add(MakeCombatant("Corsair", world.Vessels[1], shipB, CombatDoctrine.Standoff));
            _ctx.Combat = cm;
            _ctx.ShowCombat = true;
            SetStatus("Duel armed: Vanguard (aggressive) vs Corsair (standoff)");
        };

        _ctx.StartMission = (MissionDef m) =>
        {
            InstallWorld(Scenarios.Duel());   // deep-space two-ship arena (clears _ctx.Combat)
            var world = _ctx.World;
            world.Vessels[1].Position = new Apastron.Core.Vec3(m.StartRange, 0.0, 0.0);
            // frame the camera to the opening range (with margin) rather than the generic duel standoff
            _scene.FrameFor(System.Math.Max(m.StartRange * world.RenderScaleHint * 2.5, 60.0));

            var shipA = Spacecraft.CreateWarship("Vanguard");
            var shipB = Spacecraft.CreateWarship("Corsair");
            world.Vessels[0].Mass = shipA.TotalMass;
            world.Vessels[1].Mass = shipB.TotalMass;

            var cm = new CombatManager { Active = true };
            cm.Sound = s => _ctx.Audio.Play(s);
            var player = MakeCombatant("Vanguard", world.Vessels[0], shipA, CombatDoctrine.Aggressive);
            var enemy = MakeCombatant("Corsair", world.Vessels[1], shipB, CombatDoctrine.HoldFire);
            player.IsPlayer = true;
            player.WeaponsHold = false;
            player.ArmorMax *= m.PlayerArmorMult; player.Armor = player.ArmorMax;
            enemy.ArmorMax *= m.EnemyArmorMult;   enemy.Armor = enemy.ArmorMax;
            enemy.WeaponsHold = true;             // passive until MissionState releases it
            cm.Combatants.Add(player);
            cm.Combatants.Add(enemy);
            _ctx.Combat = cm;

            _ctx.Mission = new MissionState { Def = m, Player = player, Enemy = enemy };
            _ctx.View.Focus = CameraFocus.Vessel;
            _ctx.Paused = false;
            _ctx.ShowPauseMenu = false;
            _ctx.TimeScale = 1.0;   // missions start at real-time; a sandbox time-warp must not carry in
            _ctx.ShowCombat = false;
            _ctx.Screen = AppScreen.Mission;
            _lastMission = m;
        };

        _ctx.RestartMission = () =>
        {
            if (_lastMission != null) _ctx.StartMission?.Invoke(_lastMission);
        };

        _ctx.ExitToTitle = () =>
        {
            _ctx.Combat = null;
            _ctx.Mission = null;
            _ctx.ShowPauseMenu = false;
            _ctx.Paused = false;
            _ctx.ResetScenario?.Invoke();   // Low Earth Orbit backdrop (also clears combat)
            _ctx.Screen = AppScreen.Title;
        };

        _ctx.SaveScenario = () =>
        {
            bool ok = ScenarioIO.Save(ScenarioPath, _ctx.World, _ctx.Ship,
                (int)_ctx.View.Focus, _ctx.View.FovDegrees, _ctx.View.ShowOrbitPath, _ctx.View.ShowVesselMarker);
            SetStatus(ok ? "Scenario saved" : "Save failed");
        };

        _ctx.LoadScenario = () =>
        {
            if (ScenarioIO.TryLoad(ScenarioPath, out var loaded))
            {
                loaded.World.Integrator = SettingsWindow.MakeIntegrator(loaded.Integrator);
                _config.Simulation.Integrator = loaded.Integrator;
                _ctx.World = loaded.World;
                if (_ctx.World.PrimaryVessel != null && _ctx.World.PrimaryVessel.Velocity.Length > 1e-6)
                    _ctx.World.PrimaryVessel.Forward = _ctx.World.PrimaryVessel.Velocity.Normalized();
                _ctx.Ship = loaded.Ship;
                _ctx.Node = null;
                _ctx.FlipPlan = null;
                _scene.FrameFor(_ctx.World.CameraDistanceHint);
                _ctx.View.Focus = (CameraFocus)loaded.View.Focus;
                _ctx.View.FovDegrees = loaded.View.Fov;
                _ctx.View.ShowOrbitPath = loaded.View.ShowOrbitPath;
                _ctx.View.ShowVesselMarker = loaded.View.ShowVesselMarker;
                SetStatus("Scenario loaded");
            }
            else SetStatus("No scenario to load");
        };

        foreach (var keyboard in _input.Keyboards)
            keyboard.KeyDown += OnKeyDown;

        _scene = new SceneRenderer(_gl);
        _scene.FrameFor(_ctx.World.CameraDistanceHint);

        // First-draw every render pipeline now (see SceneRenderer.WarmUp): the AMD driver this
        // project chases finalises shaders lazily at first draw and has fast-failed mid-flight
        // doing so; running it here is synchronous, breadcrumbed, and recoverable via safe mode.
        _scene.WarmUp(_config.Graphics);

        if (_config.Graphics.DriverSafeMode)
        {
            _ctx.StatusMessage = "Driver safe mode active: additive plumes & film grade off (Settings > Appearance to restore)";
            _ctx.StatusTtl = 12.0;
        }

        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;
        if (_mouse != null)
            _mouse.Scroll += OnScroll;

        Console.WriteLine($"[gl] {vendor} | {renderer} | {version}");
        CrashLog.Phase("load complete (GL context + scene built)");
    }

    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (_ctx != null && !ImGui.GetIO().WantCaptureMouse)
            _scene.Zoom(wheel.Y);
    }

    private static MissionDef? _lastMission;

    private static void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (_ctx == null) return;

        if (_ctx.Screen == AppScreen.Mission)
        {
            bool live = _ctx.Mission != null && _ctx.Mission.Outcome == MissionOutcome.InProgress;
            switch (key)
            {
                case Key.Space:
                case Key.Escape:
                    _ctx.ShowPauseMenu = !_ctx.ShowPauseMenu;
                    _ctx.Paused = _ctx.ShowPauseMenu;
                    break;
                case Key.Number1: if (live) _ctx.Mission!.Player.Doctrine = CombatDoctrine.Aggressive; break;
                case Key.Number2: if (live) _ctx.Mission!.Player.Doctrine = CombatDoctrine.Standoff; break;
                case Key.Number3: if (live) _ctx.Mission!.Player.Doctrine = CombatDoctrine.Evasive; break;
                case Key.F:       if (live) _ctx.Mission!.Player.WeaponsHold = !_ctx.Mission!.Player.WeaponsHold; break;
                case Key.P:       _ctx.Paused = !_ctx.Paused; break;   // pause/resume without the pause MENU
                case Key.Tab:     CycleFocus(); break;
            }
            return;
        }

        switch (key)
        {
            case Key.Space:  _ctx.Paused = !_ctx.Paused; break;
            case Key.Escape: _ctx.ShowSettings = !_ctx.ShowSettings; break;
            case Key.Tab:    CycleFocus(); break;
        }
    }

    // Cycle the camera's per-vessel lock: free-orbit -> vessel 0 -> vessel 1 -> ... -> free-orbit.
    private static void CycleFocus()
    {
        if (_ctx == null) return;
        int n = _ctx.World.Vessels.Count;
        if (n == 0) { _ctx.View.FocusVesselIndex = -1; return; }
        int next = _ctx.View.FocusVesselIndex + 1;
        _ctx.View.FocusVesselIndex = next >= n ? -1 : next;
    }

    private static void OnUpdate(double dt)
    {
        CrashLog.Phase("first update tick");
        if (_ctx.RequestExit)
        {
            CrashLog.Phase("exit requested (clean)");
            _window.Close();
            return;
        }

        SyncWindowSettings();

        try
        {
        if (_ctx.StatusTtl > 0.0) _ctx.StatusTtl -= dt;

        double simSeconds = _ctx.Paused ? 0.0 : dt * _ctx.TimeScale;

        if (_ctx.Screen == AppScreen.Title)
        {
            // front-end: a slow idle orbit over the backdrop; no simulation
            _scene.OrbitCamera((float)(dt * 0.04f), 0.0f);
            return;
        }

        if (_ctx.Screen == AppScreen.Mission)
        {
            UpdateAttitudes(dt);
            bool run = !_ctx.Paused && _ctx.Mission != null && _ctx.Mission.Outcome == MissionOutcome.InProgress;
            if (run)
            {
                _ctx.World.Advance(simSeconds, _config.Simulation.FixedStep, _config.Simulation.MaxStepsPerFrame);
                _ctx.Combat?.Update(_ctx.World, simSeconds);
            }
            _ctx.Mission?.Update(_ctx.Combat!, run ? simSeconds : 0.0);
            return;
        }

        // --- Sandbox ---
        // In a live engagement the combat AI drives every vessel's thrust, so the player thrust
        // pipeline is bypassed; otherwise the maneuver/flip-burn autopilots and propulsion bridge
        // run as usual. Either way the world then steps, and the engagement (if any) is advanced.
        bool combat = _ctx.Combat != null && _ctx.Combat.Active;
        if (!combat)
        {
            if (_ctx.GodMode) _ctx.Ship.Refill();   // God mode: unlimited propellant
            _ctx.Orders.Update(_ctx.Ship, _ctx.World, ref _ctx.Node, ref _ctx.FlipPlan, _ctx.ShipAI, combat, simSeconds);
            Maneuver.UpdateExecution(_ctx.World, _ctx.Ship, _ctx.Node, simSeconds);
            FlipBurn.Update(_ctx.World, _ctx.Ship, _ctx.FlipPlan, simSeconds, _ctx.GodMode);
            _ctx.ShipAI.Update(_ctx.Ship, _ctx.World, _ctx.FlipPlan, _ctx.Node, combat, _ctx.GodMode, _ctx.Orders.ActiveStatus);
            Propulsion.Apply(_ctx.Ship, _ctx.World, simSeconds);
            if (_ctx.Node is { Completed: true }) _ctx.Node = null;
        }
        BurnTrace.Mark("update: autopilots+propulsion done");
        UpdateAttitudes(dt);
        BurnTrace.Mark("update: attitudes done");

        if (!_ctx.Paused)
        {
            _ctx.World.Advance(
                simSeconds,
                _config.Simulation.FixedStep,
                _config.Simulation.MaxStepsPerFrame);
        }
        BurnTrace.Mark("update: integrate done");

        _ctx.Combat?.Update(_ctx.World, simSeconds);
        BurnTrace.Mark("update: end");
        }
        catch (Exception ex) { CrashLog.Report("OnUpdate", ex); }
    }

    private static void UpdateAttitudes(double dt)
    {
        CombatManager? cm = _ctx.Combat;
        if (cm != null && cm.Combatants.Count > 0)
            foreach (Combatant cb in cm.Combatants) Attitude.Update(cb.Body, dt);
        else
            Attitude.Update(_ctx.World.PrimaryVessel, dt);
    }

    private static bool _windowSyncInit;
    private static bool _lastVSync;
    private static int  _lastTargetFps;

    private static string ScenarioPath => Path.Combine(AppConfig.ConfigDirectory, "scenario.json");

    private static void SetStatus(string message)
    {
        _ctx.StatusMessage = message;
        _ctx.StatusTtl = 4.0;
    }

    /// <summary>Build a combatant, taking its armour pool from the ship's armour parts.</summary>
    private static Combatant MakeCombatant(string name, Apastron.Physics.RigidBody body,
                                           Spacecraft ship, CombatDoctrine doctrine)
    {
        double armor = ship.Parts.Where(p => p.Armor != null).Sum(p => p.Armor!.ArealDensity);
        string mat = ship.Parts.FirstOrDefault(p => p.Armor != null)?.Armor!.Material ?? "Steel(RHA)";
        if (armor <= 0.0) armor = 1.0;
        return new Combatant
        {
            Name = name, Body = body, Ship = ship, Doctrine = doctrine,
            Armor = armor, ArmorMax = armor, ArmorMaterial = mat,
            Hull = 100.0, HullMax = 100.0,
        };
    }

    /// <summary>Apply VSync / frame-cap changes from Settings to the live window.</summary>
    private static void SyncWindowSettings()
    {
        var g = _config.Graphics;
        if (_windowSyncInit && g.VSync == _lastVSync && g.TargetFps == _lastTargetFps)
            return;

        _window.VSync = g.VSync;
        _window.FramesPerSecond = g.VSync ? 0.0 : g.TargetFps;
        _lastVSync = g.VSync;
        _lastTargetFps = g.TargetFps;
        _windowSyncInit = true;
    }

    private static void OnRender(double dt)
    {
        CrashLog.Phase("first render frame: imgui update");
        _imgui.Update((float)dt);

        try
        {
        // Camera input first (skipped while the UI wants the mouse), then the 3D scene,
        // which also clears the framebuffer. ImGui is rendered last so panels sit on top.
        bool uiWantsMouse = ImGui.GetIO().WantCaptureMouse;
        if (_mouse != null)
            _scene.HandleDrag(_mouse, !uiWantsMouse);
        // Homeworld-style: a left-click (not a drag) on a ship locks the camera onto it.
        if (_mouse != null && !uiWantsMouse && _scene.TryConsumeClick(out var clickPos))
        {
            int picked = _scene.PickVessel(clickPos, _ctx.World);
            if (picked >= 0) _ctx.View.FocusVesselIndex = picked;
        }

        BurnTrace.Mark("render: begin");
        ManeuverPreview maneuver = Maneuver.BuildPreview(_ctx.World, _ctx.Node);
        BurnTrace.Mark("render: preview built");
        CrashLog.Phase("first scene render: entering");
        _scene.Render(_ctx.World, _ctx.View, _config.Graphics, _window.FramebufferSize, maneuver, _ctx.Combat);
        CrashLog.Phase("first scene render: complete");
        CheckGlErrors("scene");
        BurnTrace.Mark("render: scene done");
        _ctx.DrawnObjects = _scene.LastDrawn;
        _ctx.CulledObjects = _scene.LastCulled;

        // Soft "Gen X Soft Club" ambience behind the UI: full on the title (gradient + drifting haze),
        // a faint vignette elsewhere so the simulation stays legible.
        AmbientBackdrop.Draw(ImGui.GetIO().DisplaySize, full: _ctx.Screen == AppScreen.Title);
        BurnTrace.Mark("render: backdrop done");

        switch (_ctx.Screen)
        {
            case AppScreen.Title:
                TitleScreen.Draw(_ctx);
                break;

            case AppScreen.Mission:
                MissionHud.Draw(_ctx);
                break;

            default: // Sandbox
                MainMenuBar.Draw(_ctx);
                Hud.Draw(_ctx);
                HudDock.BeginFrame();        // consumes the overlay heights Hud just reported
                Sidebar.DrawLeft(_ctx);      // flight & ops (merged left drawer)
                Sidebar.DrawRight(_ctx);     // view, data & build (merged right drawer)
                SettingsWindow.Draw(_ctx);   // modal, centred
                AboutWindow.Draw(_ctx);      // modal, centred
                break;
        }

        BurnTrace.Mark("render: panels done");
        }
        catch (Exception ex) { CrashLog.Report("OnRender", ex); }

        DrawCrashBanner();
        CrashLog.Phase("first imgui render: entering");
        _imgui.Render();
        CrashLog.Phase("first imgui render: complete");
        BurnTrace.Mark("render: imgui done (frame complete)");
        BurnTrace.EndFrame();
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
    }

    // Drains the GL error queue and logs the FIRST non-zero code once, so a native pipeline
    // fault (e.g. a bad draw on this GPU) is recorded in crash.log instead of silently
    // corrupting the context. Logged once to avoid per-frame spam.
    private static bool _glErrorLogged;
    private static void CheckGlErrors(string phase)
    {
        if (_glErrorLogged) return;
        GLEnum err = _gl.GetError();
        if (err != GLEnum.NoError)
        {
            CrashLog.Note("GL:" + phase, "glGetError = " + err);
            _glErrorLogged = true;
        }
    }

    // A top-LEFT overlay shown after the game recovers from a per-frame exception. It animates in
    // (slide from the left + fade) when an error appears and animates out when dismissed/cleared, so
    // it draws attention without snapping. The last error text is cached so the out-animation still
    // has content to render after CrashLog has been cleared. The full stack stays in crash.log.
    private static float _bannerAnim;       // eased visibility 0..1
    private static string? _bannerText;     // cached so fade-out has text after LastError is cleared
    private static void DrawCrashBanner()
    {
        var io = ImGui.GetIO();
        bool active = CrashLog.LastError != null;
        if (active) _bannerText = CrashLog.LastError;

        // Smooth in/out toward the target visibility.
        float target = active ? 1f : 0f;
        _bannerAnim += (target - _bannerAnim) * MathF.Min(1f, io.DeltaTime * 8f);
        if (_bannerAnim <= 0.004f && !active) { _bannerAnim = 0f; return; }
        if (_bannerText == null) return;
        string text = _bannerText;

        float ease = _bannerAnim * _bannerAnim * (3f - 2f * _bannerAnim);   // smoothstep
        const float margin = 16f;
        float slideX = margin - 28f * (1f - ease);                          // slide in from the left
        float top = ImGui.GetFrameHeight() + 8f;                            // clear the menu bar

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(slideX, top), ImGuiCond.Always,
            new System.Numerics.Vector2(0f, 0f));
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(0f, 0f),
            new System.Numerics.Vector2(MathF.Max(io.DisplaySize.X * 0.5f, 360f), io.DisplaySize.Y));
        ImGui.SetNextWindowBgAlpha(0.93f * ease);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ease);                      // fade text + chrome together

        if (ImGui.Begin("##crashbanner",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            Ui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.4f, 1f),
                "Recovered from an error (logged to crash.log):");
            Ui.TextWrapped(text, MathF.Max(io.DisplaySize.X * 0.5f - 40f, 320f));
            Ui.TextDisabled($"Occurrences this session: {CrashLog.Count}");
            if (ImGui.Button("Dismiss")) CrashLog.Clear();
            ImGui.SameLine();
            if (ImGui.Button("Copy log path")) ImGui.SetClipboardText(CrashLog.Path);
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private static void OnClose()
    {
        CrashLog.Phase("clean shutdown (window closing)");
        _config.Save();
        _scene?.Dispose();
        _imgui?.Dispose();
        _input?.Dispose();
        _gl?.Dispose();
    }

    private static string SafeGet(StringName name)
    {
        try { return _gl.GetStringS(name) ?? "Unknown"; }
        catch { return "Unknown"; }
    }
}
