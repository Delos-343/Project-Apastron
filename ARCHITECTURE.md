# Apastron — Architecture

This document records two decisions: how the project is structured, and how "atomic design" fits in.
The short version: **Apastron already has a clean layered architecture — we formalize and keep it, we do
not rewrite to ECS, and we apply atomic design to the UI layer (only), where it actually belongs.**

---

## 1. "Atomic design" — what it is, and where it fits

Atomic Design (Brad Frost) is a methodology for **UI design systems**: build small reusable widgets and
compose them upward — *atoms* → *molecules* → *organisms* → *templates* → *pages*. It is a UI idea. It does
**not** describe how to structure a physics engine, a combat AI, or a renderer, and forcing it onto those
systems would be a category error.

It does, however, map cleanly onto Apastron's immediate-mode (Dear ImGui) panel layer, so that is exactly
where we apply it:

| Atomic tier   | In Apastron                                | Files |
|---------------|--------------------------------------------|-------|
| **Atoms**     | format-safe text primitives                | `App/Ui.cs` — `Text`, `TextColored`, `TextDisabled`, `TextWrapped` |
| **Molecules** | composite rows distilled from duplication  | `App/Ui.cs` — `Section`, `StatBar`, `LabeledCombo` |
| **Organisms** | the panels                                 | `CombatPanel`, `FleetPanel`, `WeaponsPanel`, `ShipAiPanel`, … |
| **Templates** | screen layout / docking                    | `Sidebar`, `Hud`/`HudDock`, `MissionHud` |
| **Pages**     | the actual screens                         | Title / Sandbox / Mission, assembled in `Program` |

Why this is worth doing here: `Section()` — a header caption plus a separator — was hand-rolled
**identically in nine different panels**. Promoting it to one molecule (`Ui.Section`) means a styling change
is a single edit instead of nine. `CombatPanel` is the worked example: it now composes entirely from the kit
(no raw `ImGui.Combo`/`ProgressBar` left, ~14 lines shorter). The remaining panels are a mechanical follow-up.

A safety note that constrains every UI atom: ImGui treats display strings as printf **format** strings, so a
stray `%` in dynamic text crashes the process. Every atom routes dynamic text through `TextUnformatted`
(see the `Ui` class comment). New atoms must do the same.

---

## 2. Project structure — layered / clean architecture

Apastron is organized in concentric layers. **Dependencies only ever point inward** (outer may use inner;
inner never references outer). This was verified, not assumed: the domain namespaces import **zero**
presentation code.

```
                +--------------------------------------------------+
                |  Composition root            Program.cs          |   wires everything; owns the game loop
                +--------------------------------------------------+
                |  Presentation / Application                      |
                |    App/      UI panels, GameContext (state), input
                |    Render/   OpenGL scene + HUD draw
                |    Audio/  Config/  Diagnostics/                 |
                +--------------------------------------------------+
                |  Domain  (the simulation - no UI, no I/O, no GL) |
                |    Physics/      Simulation/                     |
                |    Combat/       Vehicles/                       |
                +--------------------------------------------------+
                |  Core   math + primitives (Vec3, MathConstants)  |
                +--------------------------------------------------+
```

**The rule:** a file may depend only on layers at or below its own. Concretely, nothing in `Core`, `Physics`,
`Simulation`, `Combat`, or `Vehicles` may `using Apastron.Render` or `using Apastron.App`. (Checked: it
doesn't.) This is what makes the simulation testable and reusable independently of the renderer, and it is the
single most important invariant to preserve as the project grows.

State lives in `App/GameContext` (the mutable game state the presentation reads/writes); the domain objects
(`PhysicsWorld`, `CombatManager`, `Spacecraft`, `FleetManager`) are plain C# the root composes together.

---

## 3. Why NOT Entity-Component-System (ECS)

ECS is the modern default for data-oriented engines (Unity DOTS, Bevy) and it is genuinely excellent — at what
it is for: very large entity counts where cache-friendly struct-of-arrays iteration dominates. For Apastron it
is the wrong move *right now*, for three concrete reasons:

1. **It would throw away working code.** Apastron is ~12k lines of mature, debugged OOP with an already-clean
   domain. A from-scratch ECS migration rewrites all of it — the opposite of "lighter and more efficient
   *without sacrificing anything*."
2. **It can't be verified here.** With no compiler in the authoring environment, a global paradigm rewrite is
   exactly the change most likely to break in a hundred small ways and least possible to confirm.
3. **The entity counts don't demand it.** A handful of ships and (at most) a few hundred munitions run fine in
   OOP. The performance ECS chases, we are getting far more cheaply by removing per-frame allocations
   (hot-path `Spacecraft` aggregates and line-segment uploads — see `ROOTCAUSE` Phase 03).

**If** counts ever explode (thousands of simultaneous munitions), the migration path is *local*: turn the hot
list (`CombatManager.Munitions`) into struct-of-arrays inside `CombatManager`, leaving the rest of the
architecture untouched. That is an incremental optimization, not a rewrite — and it's the right time to do it,
not before.

---

## 4. Follow-up work (all incremental, all build-verifiable)

- Point the other eight panels' local `Section()` at `Ui.Section` and delete the copies.
- Promote more molecules as duplication shows up (metric rows, button bars, a labelled slider).
- Optionally split `Render/SceneRenderer.cs` (~1.6k lines) into partial-class files by concern
  (hulls / combat FX / post) — purely mechanical, but wide, so do it as its own build-checked chunk.
