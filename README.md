# Apastron — Hard-Science Spaceflight & Combat

Apastron is a hard-science orbital-mechanics game and sandbox built on real Newtonian physics, in the spirit of *Children of a Dead Earth* and *Kerbal Space Program*. World state is double-precision, gravity is summed from measured standard gravitational parameters, orbits are propagated analytically, and spacecraft, weapons and armor behave according to validated engineering models rather than scripted approximations.

It is a complete, playable game wrapped around a complete simulator. The game flow is **title → mission → win/lose → score → retry**: four player-commanded combat missions against an AI that fights back, with letter-rated scoring and persistent best scores. Behind the title, the full **free-flight sandbox** is one click away — design a vessel, place it in orbit, plan maneuvers and transfers, fly the SCAS DUAT (the default vessel: a 175 m fusion-torch destroyer) to Mars with an in-world flip-and-burn, and run a doctrine-driven AI-vs-AI duel. It is built in C# on .NET 8 with a hand-written OpenGL 3.3 renderer (Silk.NET) and a Dear ImGui interface, and ships as a self-contained single-file executable.

---

## Contents

- [Overview](#overview)
- [Feature matrix](#feature-matrix)
- [Tech stack](#tech-stack)
- [Requirements](#requirements)
- [Installation — build & run](#installation--build--run)
- [Controls](#controls)
- [Usage](#usage)
- [Architecture](#architecture)
- [Project layout](#project-layout)
- [Physics & numerical model](#physics--numerical-model)
- [Validation](#validation)
- [Configuration & data files](#configuration--data-files)
- [Audio](#audio)
- [Scope & limitations](#scope--limitations)
- [Roadmap](#roadmap)
- [Troubleshooting](#troubleshooting)
- [Conventions](#conventions)
- [License & acknowledgements](#license--acknowledgements)

---

## Overview

The guiding constraint of the project is **numerical honesty**. Orbital simulation lives at scales of 10⁶–10⁹ m, where 32-bit float silently destroys accuracy, so all world state is 64-bit and the conversion to float happens only at the rendering boundary. Gravity is summed from real μ = GM values; integrators are chosen for their conservation properties; orbits are propagated with exact analytic Kepler methods. Every physical model underneath — orbital elements, maneuver geometry, the rocket equation, brachistochrone transfers, terminal ballistics, combat guidance, mission scoring — is validated numerically against an independent reference (see [Validation](#validation)).

Combat is **captain-style command**, not a twitch flight sim: you set your cruiser's posture and weapons release while the AI flies the maneuvering details. This is a deliberate fit to capital-ship orbital combat, where engagements are decided by doctrine, range and heat management rather than reflexes — and where, faithfully, unguided kinetics cannot reliably hit a maneuvering target at hundreds of kilometres, so the duel turns on lasers, missiles and point-defense.

The interface wears a **"Gen X Soft Club"** theme — a washed-out, cool late-'90s lounge look with heavy rounding, airy spacing and gently translucent panels — over a 3D scene that can be finished with a film-grade post-process (soft focus, cross-process color grade, grain, bloom, vignette, starfield).

## Feature matrix

| Area | Implemented | Notes |
|---|---|---|
| Gravity | N-body field sampling from fixed bodies | Moving bodies / full N-body is a future direction |
| Integrators | Velocity Verlet, Semi-implicit Euler, RK4 (switchable live) | Verlet is the symplectic default |
| Propagation | Analytic Kepler (Lagrange f/g) for nodes, previews, rendezvous prediction | Exact, drift-free |
| State precision | Double precision + floating-origin render transform | — |
| Telemetry | Live Keplerian elements + state vectors, plus HUD overlays | — |
| Flight | Throttle + prograde/retrograde/radial burns, propellant burn | — |
| Maneuvers | Nodes (prograde/normal/radial), live post-burn orbit, finite-burn autopilot, Hohmann helper | — |
| Rendezvous | Live target, RTN relative state, phase angle, transfer window, closest approach | — |
| Time warp | x1 … x100000 | — |
| God mode | Sandbox toggle: unlimited propellant, heat cap lifted, transfer gating bypassed | — |
| Ship autonomy | Crew AI safeguards (thermal governor, propellant reserve cut-off, graded warnings with the AI's responses) plus a task-level orders layer: hold orbit, circularize, change orbit, rendezvous with target, torch transfer, dock & replenish at a station - planned and flown by the ship - and combat survivability: per-module damage that derates the ship, with a crew that breaks off when crippled | Multi-ship fleet autonomy is future |
| Player vessel | SCAS DUAT: ~175 m destroyer; railgun, 2 missile racks, 4 PD cannons, triangular 3-torch drive array, tokamak power | — |
| Spacecraft | Parts builder: mass, CoM, delta-v (Tsiolkovsky), TWR, thermal balance | — |
| Propulsion tiers | Near-future (chemical / NTR / ion) + fusion-torch (Fusion / Epstein-type) | Staging is a future direction |
| Torch transfers | Brachistochrone planner (transit time, peak v, delta-v, mass ratio, reaction mass) + in-world flip-and-burn flight | — |
| Heat | Radiator-limited sustainable throttle (engine waste heat vs rejection) | Transient core-temperature model is future |
| Weapons | Railgun / mass driver (kinetic), missile rack, laser battery, point-defense gun — mountable parts with physical specs | — |
| Terminal ballistics | Tate long-rod penetration, diffraction-limited laser ablation, missile intercept kinematics | Spaced-armor geometry, oblique impact are future |
| Armor | Areal-density plate over a material (Whipple / steel belt / tungsten); penetration-vs-perforation verdict | Multi-layer / reactive is future |
| Live combat | Slugs and guided missiles flown as entities, lead-fire and ZEM/proportional-navigation guidance, point-defense allocation, evasion, Tate-resolved damage, and **per-module damage** that knocks out engines, radiators and weapons (derating thrust, cooling and firepower) | Deterministic PD |
| Combat AI | Selectable doctrine (aggressive / standoff / evasive / hold-fire): engagement range, salvo cadence, continuous jink | Multi-ship fleets are future |
| Signature | IR signature from reactor + engine waste heat; detection range gates fire (cold ships hide) | Aspect / sensor model is future |
| Scenarios | Low Earth Orbit, interplanetary Sun + Earth + Mars (fixed planets), deep-space duel — switchable from the File menu, and furnished with dockable **spin stations** (Kestrel Yard, Highport Meridian, Lowell Station) | — |
| 3D view | Orbit camera (body/vessel/target focus), procedurally-shaded planets, oriented modular ship hulls, spin-habitat stations, predicted/post-burn/target orbits | Asset-free procedural geometry |
| Attitude | Kinematic nose-tracking (slews toward thrust); a burn reversal animates a 180° flip | 6-DOF RCS dynamics is future |
| Spin habitats | Counter-rotating rings, artificial gravity a = ω²r, rim-speed + comfort warning | — |
| Culling | CPU frustum culling of bodies and vessels; live draw/cull counts on the HUD | Occlusion / LOD is future |
| Quality settings | Render scale, MSAA, draw distance, mesh LOD, VSync/FPS — all live | — |
| Look & feel | "Gen X Soft Club" theme (3 live palettes) + optional GLSL film-grade post-process | — |
| Game flow | Title → mission select → play → victory/defeat → retry; the full sandbox is a menu choice | — |
| Missions | Four hand-tuned engagements (Ambush / Patrol Skirmish / Outgunned / Last Stand) with Destroy or Survive objectives | Branching campaign is future |
| Scoring | Win/lose detection, score from hull / speed / intercepts, S–D letter rating, persistent best per mission | Online leaderboards are future |
| Audio | Sound hooks fire on every shot / hit / intercept / outcome; silent default backend (drop-in to enable) | Bundled sound backend is future |
| Persistence | JSON config + full scenario save/load + best scores in `%APPDATA%/Apastron` | — |
| Packaging | Self-contained single-file publish (win / linux / osx) via scripts | `dotnet run` for dev |

## Tech stack

| Component | Choice | Why |
|---|---|---|
| Runtime | .NET 8 / C# | Fast, cross-toolable, ships as a self-contained exe |
| Windowing / input / GL | [Silk.NET](https://github.com/dotnet/Silk.NET) 2.22 | Thin, modern bindings; OpenGL 3.3 core |
| UI | [Dear ImGui](https://github.com/ocornut/imgui) via ImGui.NET | Immediate-mode, ideal for dense tool panels |
| Physics | Custom double-precision core | `System.Numerics.Vector3` is 32-bit and unusable at orbital scale |
| Config | `System.Text.Json` | First-party, no extra dependency |

## Requirements

- Windows 10/11 (x64) is the primary target; Linux and macOS builds are produced by the publish scripts.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build, or just the .NET 8 runtime to run a framework-dependent build (a self-contained build needs neither installed on the target).
- A GPU/driver exposing **OpenGL 3.3 core** or newer. Software rasterisers run but are detected and dropped to the low quality tier.
- Internet access on the first build, to restore the Silk.NET NuGet packages.

The project targets `x64` explicitly because the native OpenGL/ImGui binaries Silk.NET pulls in are architecture-specific.

## Installation — build & run

Clone or unzip the project, then from the repository root:

```bash
# restore + build + run
dotnet run --project src/Apastron/Apastron.csproj
```

Or open `Apastron.sln` in **Visual Studio 2022 (17.x)** and press **F5**.

Run the headless physics self-test (no window, no GPU needed — useful for CI):

```bash
dotnet run --project src/Apastron/Apastron.csproj -- --selftest
```

### Packaging (self-contained, single file)

Produce a standalone executable that needs no .NET install on the target. The helper scripts pass `-p:SelfContained=true`, which switches on single-file bundling in the project:

```bash
./publish.sh win-x64            # or linux-x64, osx-x64, osx-arm64
# PowerShell:  ./publish.ps1 -Rid win-x64
# output under publish/<rid>/   (Apastron.exe on Windows, Apastron elsewhere)
```

Equivalent one-liner:

```bash
dotnet publish src/Apastron/Apastron.csproj -c Release -r win-x64 --self-contained true -p:SelfContained=true -o publish/win-x64
```

Native Silk.NET binaries are bundled into the single file and extracted on first run. Trimming is intentionally left off because the GL bindings use reflection.

## Controls

| Input | Action |
|---|---|
| Left-drag | Orbit the camera (rotate yaw/pitch) |
| Scroll wheel | Zoom in / out (8–8000 render units) |
| `Space` | Pause / resume (in a mission, opens the pause menu) |
| `Esc` | Sandbox: toggle Settings. Mission: pause menu |
| `1` / `2` / `3` | Mission only: set posture Close / Hold / Evade |
| `F` | Mission only: toggle Weapons Free / Hold Fire |
| Simulation → Time warp | x1 / x10 / x100 / x1000 / x10000 / x100000 |
| Simulation → God mode | Sandbox: unlimited propellant, heat cap lifted, transfer gating bypassed |
| File → Save / Load Scenario | Persist or restore the full scenario |
| File → New: LEO / Interplanetary / Duel | Load a fresh scenario |
| View menu | Show/hide Telemetry, Flight Controls, Ship AI, Spacecraft Builder, Maneuver Planner, Rendezvous, Torch Transfer, Weapons, Combat, 3D Camera, HUD overlay; toggle the engine plume |

Mouse input is ignored by the camera whenever the pointer is over an ImGui panel, so dragging a window never spins the view.

## Usage

### The game: title screen and missions

Apastron opens on a **title screen** over a slow Earth backdrop:

- **New Engagement** opens **mission select**. Four missions span the difficulty curve, each showing your persistent best score:
  - **Ambush** *(Easy)* — an enemy cruiser drifts unaware (it holds fire for the first 18 s and is lightly armoured); open fire and finish it while it is cold.
  - **Patrol Skirmish** *(Normal)* — a near-matched cruiser holding at range; win the laser exchange and screen the incoming missiles.
  - **Outgunned** *(Hard)* — a heavily-armoured aggressor closes fast; use evasion to spoil its kinetics and wear it down with beams and missiles.
  - **Last Stand** *(Survive)* — you cannot kill it; weave and screen missiles to survive 90 seconds.
- **Free-Flight Sandbox** — the full orbital-mechanics toolset (below). Nothing from the simulator was removed; it is simply behind a menu.
- **Settings / About / Quit**.

In a mission you **command your cruiser** while the AI flies the opposing one:

- A top **objective banner** shows the goal, a survival timer where relevant, and both ships' hull bars (yours green, the enemy's red).
- A bottom **command bar** is your orders: **Close / Hold / Evade** posture (`1` / `2` / `3`) and a **Weapons Free / Hold Fire** toggle (`F`), with a live readout of range, closing rate and inbound missiles. Point-defense always runs. Evading spoils the enemy's unguided slugs; closing strengthens your beams; holding fire lets you reposition before committing.
- The 3D view is the same engine as the sandbox — orbit/zoom freely while munitions fly. Your cruiser is tinted **cyan** and the enemy **red**, and each hull turns to face its thrust.
- **Esc** (or `Space`) pauses with a menu (Resume / Restart / Settings / Main Menu).
- On resolution you get a **Victory / Defeat** overlay with your score, an **S–D rating**, end-of-fight stats, and a **NEW BEST!** flag when you beat your record — then **Retry**, **Mission Select** or **Main Menu**.

Difficulty is set by the enemy's armour and opening posture (and your reaction to it), so good command decisions, not reflexes, win the fight.

### The sandbox

Choosing **Free-Flight Sandbox** drops you into the full simulator: a dark window with a 3D viewport behind a menu bar, a heads-up overlay, and dockable tool panels.

- **3D viewport** — Earth as a procedurally-shaded sphere (Lambert lighting, latitude/longitude graticule, subtle banding so the day/night terminator reads clearly), the vessel tracing its orbit, and any rendezvous target in green. The **predicted orbit** is a cyan conic computed analytically from the live state vector and recomputed every frame, so firing a burn reshapes the ellipse in real time. The primary vessel renders as an **oriented modular hull** (smooth-tapered bow, reactor, segmented glowing radiator fins, gimballed engine cluster, weapon hardpoints, an emissive plume while burning) whose nose slews to track thrust — reversing the burn animates a real 180° flip.
- **HUD overlay** — glanceable, click-through readouts: run/pause, time-warp and mission clock with altitude, speed, apo/periapsis and eccentricity top-left; framerate, render resolution, MSAA and **draw/cull counts** top-right. Toggle under **View → HUD overlay**.
- **Telemetry** — sim clock, active integrator, and the vessel's live orbital elements (altitude, speed, apo/periapsis, semi-major axis, eccentricity, inclination, period, specific energy) plus raw state vectors.
- **Flight Controls** — set a **throttle** and a persistent thrust direction (**Prograde / Retrograde / Radial Out / Radial In**), or **Cut Thrust**. The burn runs continuously, consuming propellant; out of propellant, thrust cuts automatically.
- **Spacecraft Builder** — assemble the vessel from a parts catalogue: near-future parts (command pod, habitat, tanks, chemical/nuclear/ion engines, fission reactor, radiators, structure) plus a **fusion-torch tier** (Tokamak Reactor, Fusion Torch, Epstein-Type Torch, large reaction-mass tanks, high-temp and droplet radiators; every stock vessel ships on the torch + tokamak stack), and weapons/armor. A schematic shows the stack with a centre-of-mass tick; the readout reports dry/total mass, CoM, thrust, effective Isp, exhaust velocity, **delta-v (Tsiolkovsky)**, burn time, **TWR**, and the **thermal balance** including the radiator-limited sustainable throttle for torch drives. Edits apply live.
- **Maneuver Planner** — **Create node** drops a maneuver point ahead on the orbit; set its **lead time** (`+30 s` / `+1 orbit` / `Now`) and dial **prograde / normal / radial** delta-v. The resulting orbit draws live in yellow with a node marker; the panel reports total delta-v, time-to-node, whether you have the delta-v, and the resulting apo/periapsis, period and eccentricity. **Execute burn** arms a finite-thrust autopilot that holds the burn vector inertially until the planned delta-v is delivered, then cuts. A **Hohmann** section sizes a two-burn transfer to a target circular orbit and can seed the departure node.
- **Rendezvous** — **Spawn target** places a station in a circular co-planar orbit. The panel then reports the relative state in the target's **RTN frame** (range, closing rate, radial/along-track/cross-track offsets, relative speed), the signed **phase angle**, the **phasing** block (both periods, synodic period, required Hohmann phase, time to the transfer window), and a **closest-approach** prediction (minimum range, time-to-closest-approach, match delta-v) over the coming orbits.
- **Ship AI** — the vessel's crew layer: the live activity line, g-load / heat / propellant / delta-v readouts, the autonomy safeguards (thermal governor, propellant reserve), and the **orders** section. Issue a task - hold orbit, circularize, change orbit to an altitude, rendezvous with the target, torch to another body - and the AI plans the burns as maneuver nodes and flies them, reporting each step to its log. While it waits for a node or a transfer window the status says so; **time warp stays under your command**, so warp ahead to the marked time. One order runs at a time and further clicks queue; Cancel tears the current one down safely.
- **Torch Transfer** — a **brachistochrone (flip-and-burn) planner**. Pick a distance (AU) and acceleration (g) and read transit time, peak velocity at the midpoint flip, delta-v, **mass ratio** and **reaction mass** — colour-coded for whether the ship has the propellant and whether the radiators can sustain that acceleration. **Fly to Mars** (in the interplanetary scenario) commands the in-world autopilot: it holds the commanded acceleration, accelerates, visibly **flips at the midpoint**, decelerates, and cuts at closest approach.
- **Ship AI** — the crew of your vessel. The top half is the **orders** section: issue a task (hold orbit, circularize, change orbit to an altitude, rendezvous with the target, torch transfer to a body) and the ship plans and flies every step itself — pre-circularizing if needed, waiting for transfer windows, executing departure and arrival burns through the node autopilot, and matching velocity at closest approach. The yellow node marker previews the AI's planned burns; waits ask you to **time-warp**. Clicks while tasked queue. The bottom half shows the live activity line, **acceleration in g**, heat and propellant state, the safeguard controls, and the crew log pairing every warning with the AI's response. Safeguards outrank orders: a propellant-reserve cut stands the task down. **Dock & replenish** is the headline order: pick a station and the crew flies the whole path — torch hops across the system if it orbits another body (with course-correction hops), a feedback orbit insertion, the rendezvous chain, a proportional final approach, and a sub-600 m dock — then transfers propellant until the tanks are full (about 45 s docked). A wide pass triggers a 7% phasing orbit and a retry. Stations are scenario furniture: always rendered, never displaced by spawning a practice target.
- **Weapons / fire control** — with a weapon mounted in the Builder, **View → Weapons** resolves the terminal result for that weapon family against a chosen target armor (material + areal density): for a **railgun**, impact velocity, kinetic energy, long-rod penetration depth and perforation verdict; for a **laser**, the diffraction-limited spot size at range, on-target intensity and burn-through dwell (intensity falls as 1/range²); for a **missile**, its delta-v, acceleration, intercept time and whether it out-accelerates a chosen evasion. This is fire-control *analysis*; to see weapons flown live, use the duel.
- **Live duel (combat AI)** — **File → New: Duel** drops two armed warships ~150 km apart and arms them: **Vanguard** (aggressive) vs **Corsair** (standoff). **View → Combat** drives it. Each ship has a **doctrine** combo (aggressive / standoff / evasive / hold-fire) you can change at any time, plus **Engage / Cease fire** and **Reset duel**. Watch pale **slug streaks** (lead solution), orange **missile streaks** (proportional navigation), **point-defense** intercepts, and instantly-tracking **laser beams**; the panel tallies time, rounds fired/in-flight, perforations and PD intercepts, and each ship's armor / hull / IR signature / detection range, then the outcome. A through-armor hit now strikes a **module** — a wrecked drive or radiator visibly slows the ship and saturates its heat, a wrecked weapon falls silent — and a **damage-control feed** logs each module loss; when a ship is crippled its crew breaks off to evasive on its own (the player's crew recommends disengaging, and forces evasive when the hull is failing). Identical ships fight to a near-mutual result decided by the lasers — change a doctrine to break the symmetry.
- **Adaptive render scale** — each scenario carries a render scale and camera framing, so the LEO view (Earth a few units across) and the interplanetary view (1 AU ≈ 748 units) both frame without manual zooming.
- **Camera** — focus on the **Body**, **Vessel** or **Target**, toggle the orbit path / vessel marker, set field of view, read the current distance, and reset the view.

### Look & feel

Tool panels dock to the window's bottom corners with consistent margins and stacking (a fixed sci-fi-HUD layout that re-flows live as the window resizes), and typography is scaled 1.35x with high-contrast text for high-DPI laptop screens. The UI wears a **"Gen X Soft Club"** theme — a washed-out, cool late-'90s lounge look with heavy rounding, airy spacing and gently translucent panels. Three palettes ship and switch live from **Settings → Appearance**: **Cool slate** (sage-teal, default), **Warm tan**, and **Dusk mauve**; the title and HUD accents follow the choice.

Two layers of ambience sit behind the UI. A **draw-list backdrop** runs on the title screen — a hazy gradient with slow drifting colour blobs over the orbiting Earth, plus a vignette (a faint vignette persists in the sandbox/missions for legibility). A real **film-grade post-process** (a fullscreen GLSL pass over the rendered scene) applies soft-focus blur, a cross-process color grade (desaturated, cool shadows / warm highlights, lifted blacks), bloom on hot surfaces (so the radiators and engine plumes bleed light), animated film grain, a starfield in deep space, and a vignette. It is toggleable in **Settings → Appearance** ("Film grade"), and if the post shader ever fails to compile on a given GPU it disables itself automatically rather than disturbing the render.

### Settings & quality scaling

The Settings panel reads the GL vendor/renderer/version strings at startup and proposes a quality tier (Low/Medium/High/Ultra). With **Auto-detect** on, texture/shadow/MSAA/render-scale follow that tier; turn it off to set them by hand. All graphics options take effect **live**:

- **Render scale (0.5–2.0×)** — the 3D scene renders into an offscreen framebuffer at `window × scale`, then is blit-scaled to the window. Below 1.0 is a performance mode; above 1.0 is supersampling.
- **MSAA (off / 2× / 4× / 8×)** — the offscreen target is multisampled and resolved before the final scale blit, clamped to the GPU's `GL_MAX_SAMPLES`.
- **Draw distance** — sets the camera far clip (kept at least twice the camera distance so nothing clips when zoomed out).
- **Texture/shadow quality** — currently drive sphere tessellation (geometric LOD, 16×24 up to 48×72).
- **VSync / Target FPS** — applied immediately; the frame cap engages only when VSync is off.

Simulation settings are live: **Integrator** (switches the propagator at runtime), **Fixed step (s)** (physics sub-step size), and **Max steps/frame** (caps work per frame so extreme time-warp can't stall the loop; leftover time is applied as one partial step so the clock stays exact).

### Saving & loading scenarios

**File → Save Scenario** writes the entire live state — every body and vessel (including the rendezvous target), the spacecraft design and its propellant, the camera view, the chosen integrator and the simulation clock — to a flat, human-readable JSON snapshot. **File → Load Scenario** rebuilds it. Mission best scores live alongside it.

## Architecture

The code is layered so the physics core has **zero dependency on rendering or UI** and can be exercised headlessly (the self-test does exactly that):

```
Core         Vec3 (double), constants            — no dependencies
  ↑
Physics      CelestialBody, RigidBody, Gravity,
             Integrators, PhysicsWorld            — depends on Core
  ↑
Simulation   OrbitalElements, Kepler, transfers,
             maneuvers, scenarios                 — depends on Physics
  ↑
Vehicles /   parts, propulsion, attitude,
Combat       ballistics, AI, missions             — depends on the above
  ↑
App / Config / ImGui panels, theme, settings,
Render       renderer                             + Silk.NET + ImGui
```

## Project layout

```
Apastron/
├─ Apastron.sln
├─ README.md
├─ publish.sh                     self-contained single-file publish (bash)
├─ publish.ps1                    self-contained single-file publish (PowerShell)
└─ src/Apastron/
   ├─ Apastron.csproj
   ├─ Program.cs                  window + GL + ImGui + main loop; --selftest entry
   ├─ Core/
   │  ├─ Vec3.cs                  double-precision 3-vector
   │  └─ MathConstants.cs         G, μ and radii for Sun/Earth/Moon/Mars/Jupiter
   ├─ Physics/
   │  ├─ CelestialBody.cs         gravitating body (fixed)
   │  ├─ RigidBody.cs             point-mass vessel + thrust
   │  ├─ Gravity.cs               a = Σ μ·(rᵢ−p)/|rᵢ−p|³
   │  ├─ PhysicsWorld.cs          state, clock, sub-stepped Advance()
   │  └─ Integrators/
   │     ├─ IIntegrator.cs
   │     ├─ VelocityVerletIntegrator.cs    symplectic, default
   │     ├─ SemiImplicitEulerIntegrator.cs symplectic, 1st-order
   │     └─ Rk4Integrator.cs               4th-order, non-symplectic
   ├─ Simulation/
   │  ├─ OrbitalElements.cs       state vector → Keplerian elements
   │  ├─ OrbitPath.cs             analytic conic sampler (predicted orbit)
   │  ├─ Kepler.cs                Lagrange f/g time propagation (node anchoring)
   │  ├─ Transfers.cs             Hohmann transfer (Δv1/Δv2, time, phase angle)
   │  ├─ Maneuver.cs              nodes, post-burn preview, finite-burn autopilot
   │  ├─ Rendezvous.cs            relative state (RTN), phase, closest approach
   │  ├─ Brachistochrone.cs       flip-and-burn transfer planner
   │  ├─ FlipBurn.cs              in-world flip-and-burn autopilot (bang-bang)
   │  ├─ ShipAI.cs                crew autonomy: safeguards, warnings, activity readout
   │  ├─ ShipOrders.cs            task-level orders: multi-burn sequences planned + flown by the AI
   │  ├─ ShipOrders.cs            task-level orders: planned + flown via nodes, phasing, flip-burn
   │  ├─ ScenarioIO.cs            JSON save/load of the full scenario
   │  └─ Scenarios.cs             LowEarthOrbit() + Interplanetary() + Duel() + HighOrbit()
   ├─ Vehicles/
   │  ├─ Part.cs                  a single component (mass, thrust, thermal, weapon/armor)
   │  ├─ PartCatalog.cs           catalogue of part templates
   │  ├─ Spacecraft.cs            parts → mass/CoM/Δv/TWR/thermal; CreateDuat / CreateTorchship / CreateWarship
   │  ├─ Attitude.cs              kinematic nose-tracking + flip slew
   │  ├─ WeaponSpec.cs            weapon / armor part specifications
   │  └─ Propulsion.cs            applies thrust + propellant burn to the RigidBody
   ├─ Combat/
   │  ├─ Materials.cs             ballistic material table (density, strength, ablation)
   │  ├─ Ballistics.cs            Tate penetration, laser ablation, missile intercept
   │  ├─ FireControl.cs           lead/intercept solver + ZEM/proportional-navigation guidance
   │  ├─ CombatTypes.cs           Munition / Combatant / doctrine + munition kinds
   │  ├─ CombatManager.cs         live engagement: AI, guidance, point-defense, damage, signature
   │  ├─ Mission.cs               mission definitions + catalogue, objectives, win/lose, scoring
   │  └─ ScoreBoard.cs            per-mission best scores persisted to %APPDATA%/Apastron
   ├─ Audio/
   │  ├─ IAudio.cs                sound-hook interface + GameSound events
   │  └─ SilentAudio.cs           default no-op backend (real audio is a drop-in)
   ├─ Render/
   │  ├─ Mat4.cs                  column-major GL matrices (perspective/lookAt/model)
   │  ├─ Shader.cs                GL program + cached uniforms (with safe fallbacks)
   │  ├─ Mesh.cs                  indexed pos+normal mesh
   │  ├─ SphereMesh.cs            UV-sphere factory (tessellation = LOD)
   │  ├─ Primitives.cs            procedural cylinder / cone / frustum / box (hull parts)
   │  ├─ LineBatch.cs             dynamic line-strip buffer (orbit path)
   │  ├─ Camera.cs                orbit camera (yaw/pitch/distance)
   │  ├─ ViewSettings.cs          focus / FOV / toggles
   │  ├─ RenderTarget.cs          offscreen FBO (render-scale + MSAA resolve)
   │  ├─ PostTarget.cs            texture FBO for the film-grade post pass
   │  └─ SceneRenderer.cs         floating-origin transform + offscreen pipeline + draw + post
   ├─ Diagnostics/
   │  ├─ CrashLog.cs              records unhandled / per-frame faults to %APPDATA%/Apastron/crash.log
   │  └─ PhysicsSelfTest.cs       headless accuracy check (--selftest)
   ├─ Config/
   │  ├─ GraphicsSettings.cs      quality tiers, MSAA, render scale, draw distance, palette, film grade
   │  ├─ AppConfig.cs             load/save JSON to %APPDATA%/Apastron (incl. simulation settings)
   │  └─ DeviceProfiler.cs        GL-string → quality heuristic
   └─ App/
      ├─ Theme.cs                 "Gen X Soft Club" ImGui theme + palettes (1.35x type)
      ├─ HudDock.cs               corner-docking HUD layout (margins, stacking, clamping)
      ├─ AmbientBackdrop.cs       title-screen gradient/blob/vignette draw-list backdrop
      ├─ GameContext.cs           shared per-frame state + AppScreen (Title/Sandbox/Mission)
      ├─ MainMenuBar.cs           File / Simulation / View / Settings / Help
      ├─ TitleScreen.cs           front-end: title + main menu + mission select
      ├─ MissionHud.cs            in-mission objective banner, command bar, result / pause overlays
      ├─ TelemetryWindow.cs       live orbital read-out
      ├─ SettingsWindow.cs        graphics + sim + appearance settings
      ├─ FlightControls.cs        throttle + thrust-mode flight
      ├─ ShipAiPanel.cs           ship AI command panel: orders + readouts, warnings + responses, log
      ├─ ShipBuilder.cs           parts builder UI + performance readout
      ├─ ManeuverPanel.cs         maneuver node + Hohmann planner UI
      ├─ RendezvousPanel.cs       target spawn + relative state + closest approach
      ├─ BrachistochronePanel.cs  fusion-torch flip-and-burn planner
      ├─ WeaponsPanel.cs          fire-control / terminal-ballistics analysis
      ├─ CombatPanel.cs           duel setup + live engagement monitor
      ├─ CameraPanel.cs           3D camera / view controls
      ├─ Hud.cs                   heads-up overlays (flight + perf)
      └─ AboutWindow.cs
```

## Physics & numerical model

- **Units:** SI throughout — metres, seconds, kilograms, radians internally (degrees only for display).
- **Double precision.** Near a 400 km orbit the radius from Earth's centre is ~6.77 × 10⁶ m. A 32-bit float has ~7 significant digits, so it resolves that magnitude only to roughly the metre — corrupting velocities and visibly drifting orbits within minutes. All simulation state uses the project's own `Vec3` (three `double`s).
- **Floating origin.** The renderer applies a camera-relative transform: world positions are shifted so the focus body (or vessel) sits at the origin, then scaled (e.g. 1 render unit = 1000 km in LEO) and cast to float, keeping rendered magnitudes near zero regardless of absolute world position.
- **Gravity.** `a(p) = Σᵢ μᵢ (rᵢ − p) / |rᵢ − p|³`, with μ = GM from measured values; the singularity at a body centre is guarded.
- **Fixed bodies.** Celestial bodies are stationary, which makes the two-body orbital-element computation exact and the simulation fully deterministic. The data model (`CelestialBody.Velocity`) already anticipates moving bodies.
- **Orbital elements.** Computed from the state vector via specific energy (vis-viva), the eccentricity vector and specific angular momentum — giving a, e, i, periapsis/apoapsis and period.

### Integrator trade-offs

| Integrator | Order | Symplectic | Use it for |
|---|---|---|---|
| Velocity Verlet | 2 | Yes | Default free orbital flight; energy stays bounded |
| Semi-implicit Euler | 1 | Yes | Very high time-warp where step count dominates |
| Runge-Kutta 4 | 4 | No | Short, precise propagation / maneuvers; slowly loses energy over many orbits |

## Validation

Every physical model is mirrored in an independent reference implementation and checked numerically; several real bugs were caught this way. Highlights:

**Orbital propagation.** Over one full period of the default scenario (circular LEO, 400 km, dt = 1 s, 5545 steps), Velocity Verlet and RK4 hold altitude to a few metres or less while first-order Semi-implicit Euler oscillates ~3.8 km in radius (correct for its order), and all three conserve specific energy to well under 0.0001%. The shared ~1.1 km "closure" figure is not integrator error — the true period (5544.86 s) is not an integer number of 1 s steps, so the vessel is sampled ~0.14 s of arc past its start (0.14 s × 7672 m/s ≈ 1074 m), which matches all three. A 10 s prograde burn at periapsis raises apoapsis, leaves periapsis fixed and increases eccentricity — textbook behaviour confirming thrust direction and sign.

**Orbit-path geometry.** The conic sampler that draws the predicted orbit was validated independently of the renderer: for a circular LEO it returns a closed loop of constant radius lying exactly in the orbital plane and passing through the vessel's position to ~1e-9 m; for an inclined eccentric orbit the sampled periapsis/apoapsis match the analytic a(1∓e) to the metre. The ellipse you see is the true predicted trajectory, not a fit.

**Rocket equation & propellant.** For the starter vessel (m₀ = 3050 kg, dry = 1550 kg, F = 220 kN, Isp = 340 s) the closed-form Tsiolkovsky delta-v is 2256.9 m/s; numerically integrating the actual in-flight propellant-drain model yields 2257.5 m/s — a 0.02% match. So the delta-v the builder reports is the delta-v you get.

**Kepler propagation.** The Lagrange f/g propagator that anchors a node ahead in time returns a full-period state to its start with machine-precision error; a half-period from periapsis lands exactly at apoapsis; energy and angular momentum are conserved to ~1e-8 across arbitrary intervals.

**Maneuvers & transfers.** A prograde node at a circular LEO produces a transfer ellipse whose apoapsis reaches the intended target radius to 0.000 m. The Hohmann helper for LEO (400 km) → GEO returns Δv₁ ≈ 2399 m/s, Δv₂ ≈ 1457 m/s, total ≈ 3857 m/s, a 5.29 h transfer and a 100.4° departure phase angle — all matching standard references.

**Rendezvous.** The target's RTN decomposition is orthonormal (√(R²+T²+N²) = range; zero cross-track for a co-planar target), the signed phase angle returns the true offset with correct sign, and the closest-approach search recovers minimum separation, time-to-closest-approach and the match delta-v (for two co-planar circular orbits separated by Δθ the relative speed at approach is 2·v·sin(Δθ/2), reproduced to the m/s).

**Interplanetary flip-and-burn.** Integrating the full flight (real thrust, mass loss, solar gravity) for the reference torchship to Mars (1.08 AU) at 0.30 g: it flips at the geometric midpoint (peak velocity 691 km/s ≈ √(a·d)), decelerates, and arrives ~314,000 km from Mars at ~80 km/s relative in 5.3 days, using 975 t of 1000 t of reaction mass. An earlier constant-*throttle* design was caught overshooting (rising acceleration from mass loss defeats the flip criterion); holding a constant *commanded acceleration* via throttle modulation is what matches the planner.

**Terminal ballistics.** The kinetic model integrates the Tate / Alekseevskii eroding long-rod equations: a tungsten rod into RHA steel climbs from a penetration-to-length ratio of ~0.53 at 1 km/s through ~1.0 at 1.5 km/s to the hydrodynamic limit √(ρ_p/ρ_t) = 1.50 at hypervelocity — matching the textbook curve. The laser model is diffraction-limited (spot diameter 2.44·M²·λ·R/D) feeding thermal ablation: a 5 MW, 4 m laser burns a 200 kg/m² steel plate in ~4 s at 100 km but needs ~7 minutes at 1000 km (1/range² falloff). The missile model integrates a two-phase burn-then-coast intercept and reports reach and the acceleration advantage needed to defeat evasion.

**Live combat.** The kinetic lead solution (solving (|u|²−s²)t² + 2(r·u)t + |r|² = 0) intercepts to floating-point precision and correctly reports *no solution* for a target receding at muzzle speed. The missile guidance uses zero-effort-miss / proportional navigation, intercepts a non-evading target at 80 km in ~19 s and is progressively defeated by ≥5 g evasion — exposing the real lesson that a missile must keep thrust through the terminal phase. A full duel between two identical 478 t warships 150 km apart is faithfully hard-SF: across ~74 s, 136 unguided slugs scored **zero** perforations against a 0.53 g jink while point-defense made 9 intercepts and the instantly-tracking lasers decided it.

**Scoring.** Rating thresholds were checked across the full range of outcomes for a sensible spread: a flawless fast Destroy win lands around A, a solid win around B, a pyrrhic win around C, losses at D, and an S (≥4000) requires a near-perfect clear. Difficulty comes from readable asymmetry (enemy armour multipliers, a timed passive opening on the Ambush), not from handicapping the physics, which stays identical on both sides.

## Configuration & data files

All persistence lives in `%APPDATA%/Apastron/` (Windows) or the platform-equivalent application-data directory:

| File | Contents |
|---|---|
| `config.json` | Graphics + simulation settings and appearance (palette, film grade). Written on **Save** and on exit; delete to reset to defaults. |
| `scenario.json` | A flat snapshot of the full scenario (bodies, both vessels, spacecraft design + propellant, view, integrator, clock). Written by **File → Save Scenario**. |
| `scores.json` | Per-mission best scores and ratings. |

## Audio

Audio is wired as **hooks, not a hard dependency**. The game calls `IAudio.Play(GameSound)` at every meaningful moment — slug fire, missile launch, hit, point-defense intercept, victory, defeat, UI click — and ships with `SilentAudio`, a no-op backend assigned in `Program`. So the project builds and runs with zero audio dependencies and the triggers are already in the right places.

To add sound, implement `IAudio` over your audio library of choice (e.g. Silk.NET.OpenAL) and assign it once (`_ctx.Audio = new YourAudio();` in `Program.OnLoad`); every hook then produces sound with no other code changes.

## Scope & limitations

These are deliberate boundaries of the current simulation, not bugs:

- **Fixed bodies.** Celestial bodies are stationary point masses. This makes two-body elements exact, but a multi-day interplanetary transit assumes the planets don't move — a flight test-stand, not a full ephemeris. Interplanetary arrival is in the destination's vicinity at low (tens of km/s) relative speed, not a lateral-guided orbit insertion.
- **Captain-style combat.** You command posture and weapons release; the AI flies the maneuvering. This suits capital-ship orbital combat but is not a 6-DOF flight sim. Thrust reversal in the physics is instantaneous while the hull's flip is a cosmetic slew, so under heavy time-warp the two visibly decouple.
- **Terminal ballistics are engineering approximations** — good to tens of percent, not hydrocode. Armor is a single areal-density plate over one material: no spaced/Whipple geometry, no obliquity vector, no fragmentation or behind-armor spall. Impact velocity uses a scalar muzzle-plus-closing approximation along the bore. A weapon part contributes mass but its transient firing heat is outside the steady-state thermal model.
- **Combat simplifications.** Point-defense is a deterministic burst-kill of the nearest incoming missile within range (no PD-projectile flight); missiles coast ballistically after burnout; combatant maneuvering does not consume propellant (only missiles burn their own fuel); laser absorptivity is fixed at 0.3. Because the duel's two ships are identical, the winner is decided by a tiny margin unless you break the symmetry with a doctrine.
- **Rendering is asset-free.** All geometry is procedural (spheres, cylinders, cones, frustums, boxes); there are no model or texture files. The visual ceiling is a clean brutalist hard-SF look, not photoreal.
- **Audio is silent-by-default hooks** (see [Audio](#audio)).

## Roadmap

Apastron is a complete, playable game with the full hard-science simulator reachable as a sandbox mode. Natural directions beyond the current build:

- Moving / full-N-body celestial bodies and a real ephemeris.
- Lateral terminal guidance for true orbit insertion.
- 6-DOF RCS attitude dynamics.
- Multi-ship fleets and fleet command.
- Spaced / multi-layer / reactive armor and a richer damage model.
- A real audio backend on the existing hooks.
- Staging in the spacecraft builder.
- A branching campaign and online leaderboards.

## Troubleshooting

- **NuGet restore fails:** ensure internet access on the first build; run `dotnet restore` separately to see detailed errors.
- **Window fails to create / GL errors:** update GPU drivers; confirm OpenGL ≥ 3.3 (Settings shows the reported version). On headless/CI machines use `--selftest`, which needs no GPU.
- **Silent exit with code -1073740791 (0xC0000409) when firing the engine:** fixed. This was a stack-buffer-overrun fast-fail raised inside Dear ImGui's native text formatter: `ImGui.Text`/`TextColored`/`TextDisabled`/`TextWrapped` treat their string as a printf **format**, so a literal `%` in dynamic UI content (e.g. the crew-AI line "Burning Prograde at 100 % throttle", or "Heat load 37% of rejection") was parsed as a conversion specifier reading a non-existent argument. All dynamic text now routes through `ImGui.TextUnformatted` via the `App/Ui.cs` wrappers, which perform no format parsing. (Slider/Drag display formats such as `"%.0f"` are genuine printf and are unchanged.)
- **"Driver safe mode active" message at startup:** the previous session fast-failed mid-frame on the first burn (the native-driver-fault signature is detected in `crash.log` on launch), so the additive engine-plume shader and the film-grade pass are bypassed to keep the game playable. Update your GPU driver, then re-enable from **Settings ▸ Appearance ▸ Re-enable full effects**. You can also toggle **Additive engine plumes** there independently.
- **Diagnosing a GPU-driver fault:** run with `--gldebug` to request a debug GL context and stream synchronous `KHR_debug` messages into `crash.log`; the load-time shader warm-up (which first-draws every pipeline before flight) records a per-program breadcrumb so a deferred-compile fault names the offending program.
- **Film grade disabled at startup:** the post shader failed to compile on your GPU; the game continues without it. The render is otherwise unaffected.
- **Native library load errors:** confirm you're building for `x64` (the default) so Silk.NET's native binaries match.
- **Config seems ignored:** it's written on Save/exit to `%APPDATA%/Apastron/config.json`; delete that file to reset to defaults.

## Conventions

- One inertial world frame; SI units; doubles for all state.
- The physics core stays free of UI/GL references and is exercised headlessly by the self-test.
- ImGui window-visibility flags are fields (not properties) so they can be passed by `ref`.
- **All dynamic on-screen text goes through `App/Ui.cs` (`Ui.Text`/`TextColored`/`TextDisabled`/`TextWrapped`), never `ImGui.Text*` directly** — the ImGui calls are printf format functions and a stray `%` in interpolated content is a process-killer. `ImGui.SliderFloat`/`DragFloat` display-format arguments remain real printf and stay as-is.
- Shader sources are kept ASCII-only (some GL drivers' lexers choke on non-ASCII bytes even inside comments).

## License & acknowledgements

MIT — add a `LICENSE` file as you prefer.

Built on [Silk.NET](https://github.com/dotnet/Silk.NET) and [Dear ImGui](https://github.com/ocornut/imgui) (via ImGui.NET). Inspired by *Children of a Dead Earth* and *Kerbal Space Program*.
