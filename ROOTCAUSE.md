# Apastron 0xC0000409 — Root-cause analysis (verified)

## The crash
- Exit code -1073740791 = 0xC0000409 = STATUS_STACK_BUFFER_OVERRUN (__fastfail).
- Silent: no managed exception (OnUpdate/OnRender try/catch never fire), no CLR "Fatal error"
  banner. ⇒ the kill is in NATIVE code, and it is a *stack buffer overrun* specifically.
- Deterministic: first burning frame (press Prograde), with engine plume ON or OFF.
- Trace bracket: dies between Program.cs:441 ("backdrop done") and :472 ("panels done") —
  i.e. inside the Sandbox panel-draw switch. Update bracket completes; igRender never reached.

## Binary evidence (ImGui.NET 1.90.8.1, the version Silk.NET 2.22 pins)
cimgui.dll (deps/cimgui/win-x64, the exact native lib on the user's machine):
- Imports ONLY IMM32 / KERNEL32 / USER32 ⇒ statically-linked MSVC CRT (vendored vsnprintf).
- Contains format strings that themselves use %n (ImGui .ini settings serializer):
    "ID=0x%08X%n", " Pos=%i,%i%n", " Size=%i,%i%n", " Split=%c%n", ...
  ⇒ the bundled CRT has %n ENABLED (not the _set_printf_count_output=0 hardening). A stray
  %n-shaped conversion in user content would be honored as a write-back to a garbage pointer,
  which is exactly a STATUS_STACK_BUFFER_OVERRUN generator.
- Only *recoverable* assert strings present ("Recovered from missing End()/PopID()"); hard
  IM_ASSERTs route to C assert(), compiled out in this release build. So the fault is NOT an
  assertion — it is the /GS stack-cookie guard firing on a corrupted stack.

## The defect (root cause)
Dear ImGui's Text / TextColored / TextDisabled / TextWrapped take the string as a printf
FORMAT. Confirmed at the binding level:
  - igText/igTextColored/igTextDisabled/igTextWrapped receive `fmt` and call ...V(fmt, args)
    → ImFormatStringV → vendored vsnprintf, with NO variadic args supplied.
  - ONLY igTextUnformatted(begin,end) takes a byte range and never invokes the formatter.

Every '%' in dynamic UI content is therefore parsed as a conversion specifier reading a
nonexistent va_list. Most malformed specifiers read benign stack garbage (so they survive);
the wrong one corrupts the stack cookie → __fastfail → 0xC0000409.

## Why frame 4 (first burn), not frame 1
Percent-bearing strings render every frame pre-burn and SURVIVE (proving the formatter
tolerates e.g. "% o" in "37% of rejection", "%)" in "(57%)", trailing "% "). What's new on
the first burning frame is the content that newly enters a format slot:
  - ShipAiPanel.cs:36  Ui.TextColored(Theme.Accent, ai.Activity)
      Activity flips "Coasting..." → "Burning Prograde at 100 % throttle"   (':P0' under
      InvariantGlobalization renders "100 %", a space then '%') ⇒ specifier "% t".
  - ShipAiPanel.cs:163 the AI log gains governor/reserve entries containing "100 % (heat-...)"
      ⇒ specifier "% (".
These specifier shapes do not occur pre-burn; one of them reads a stack slot that, interpreted
as a conversion argument, overruns the guarded buffer. (The AI log also grew that frame,
shifting stack layout — consistent with a latent format bug becoming lethal at this point.)

The exact specifier that pulls the trigger is not the point: ANY '%' reaching these slots is
unsafe, and the correct fix removes the formatter from the path entirely.

## The fix
New App/Ui.cs — Text/TextColored/TextDisabled/TextWrapped wrappers that route through
ImGui.TextUnformatted (no format parsing). All 239 Text-family call sites across App/ and
Program.cs swept to Ui.*. Slider/Drag *display formats* ("%.0f", "%.2f AU", ...) are left as
real printf usage (correct and required there). cimgui itself is unchanged.

After this, no dynamic string can reach a printf slot, so the 0xC0000409 cannot recur from
this cause regardless of locale, throttle value, or log contents.

## Defense in depth (driver-fault hypothesis, also addressed)
A parallel theory in earlier sessions was an AMD-driver lazy-shader-compile fast-fail on the
first plume draw. The toggle-OFF repro (no novel GL state) makes that an unlikely primary
cause, but it is cheaply and safely mitigated:
  - SceneRenderer.WarmUp(): first-draws EVERY pipeline (incl. additive plume + film grade)
    at load, breadcrumbed, then glFinish — moving any deferred driver work to a synchronous,
    recoverable point.
  - CrashLog.PreviousSessionDiedDuringFirstBurn(): on next launch, detects the burn-frame /
    warm-up fast-fail signature in crash.log and engages GraphicsSettings.DriverSafeMode
    (additive plumes + film grade bypassed), so the game self-heals into a playable state.
  - Settings ▸ Appearance exposes the additive-plume toggle and a "clear safe mode" button.
  - --gldebug installs a GC-rooted KHR_debug callback (synchronous) for future driver triage.

These are diagnostics/robustness; the format fix is the actual root-cause repair.


---

## Addendum - UI overflow fixes and projectile-collision rewrite

### UI overflows
- **Mission HUD banner** (`MissionHud.DrawBanner`): the objective separator was an em-dash that rendered
  as "?" in the ASCII-only default font - replaced with ASCII; the "YOU"/"ENEMY" labels used an absolute
  `SameLine(48)` that cramped the health bar (switched to natural spacing); hull fractions are clamped to
  [0,1] before bar and label, fixing the "-0%" readout.
- **Mission HUD command bar** (`MissionHud.DrawCommandBar`): footer middots replaced with ASCII and the
  hint split onto its own line; window widened 520 -> 580 and the telemetry lines wrap, so "inbound
  missiles N" and the PD line no longer clip.
- **Victory/Defeat modal**: subtitle wraps at a fixed width so auto-resize cannot balloon.
- **Left sidebar - Ship AI** (`ShipAiPanel`): a wrapping button-flow helper lays the order/torch/dock
  buttons left-to-right and wraps them, so the long "Dock: <station>" names stop clipping; the altitude
  slider and "Change orbit" are full-width rows; the activity log wraps long lines.
- **Combat panel**: the armor/hull bars size to available width instead of a fixed 200 px.

### Projectile collision (`CombatManager.StepMunitions`, rewritten)
Hit radii were dimensionless points (slug 20 m, missile 75 m) against a ~67 m hull, so rounds passing
through a ship counted as misses; and the combat step received the full un-substepped frame dt (unlike the
world integrator), so at time warp a 3 km/s slug jumped thousands of metres per step and tunnelled through.
Now: munition flight is sub-stepped (<=50 ms, capped) so fast rounds cannot tunnel; collision is a swept
closest-approach test with both round and target advanced linearly; the hull radius is derived from ship
length (~0.75x, 45-150 m); and a round can strike any non-owner combatant. Unguided kinetics still
realistically miss a hard-maneuvering target at long range - which is why guided torpedoes and (formerly)
beam weapons are the dependable reach weapons.

---

## Addendum - Expanse weapon ranges, point-defense interception, and ship-selectable camera

Three feature requests: match The Expanse weapon-range hierarchy, implement and display missile
interception by point defense, and allow Homeworld-style selection/focus of any individual ship.

### Per-weapon Expanse ranges (`WeaponSpec.Range`, `PartCatalog`, `CombatManager.Fire`)
Weapons previously shared one global range per *kind*, so a railgun and a mass driver - both kinetic -
could not differ. Added a `Range` field to `WeaponSpec` and drive fire control from each mount's own
value. Loadout now follows the Expanse envelope:
- Missile Rack (fusion torpedoes): 2000 km (the long-range standoff weapon).
- Railgun (plasma-augmented): 1000 km.
- Mass Driver (naval gun): 100 km.
- Laser Battery: reclassified to a **point-defense laser**, 25 km (see below).
- Point-Defense Gun (40 mm CIWS): 5 km.

The Expanse has no main-battery lasers, so the laser is no longer a ship-killer: it is now a
point-defense mount (`PointDefense = true`) that burns down incoming missiles at longer reach than the
40 mm gun, giving a layered defense (laser at 25 km, 40 mm at 5 km). The old beam-ablation path
(`ApplyBeam`, `LaserHullRate`, `LaserRange`) was removed; offensive damage now comes from railgun and
torpedoes. Duel doctrine ranges were retuned to the new envelope (close 80 km / standoff 800 km /
evade 1500 km); scripted missions keep their tighter default profile.

### Point-defense missile interception, now displayed (`CombatManager.PointDefense`, `SceneRenderer`)
Point defense (40 mm gun and PD laser) engages the nearest incoming missile within the mount's range.
On each intercept it now spawns a short-lived `PdTracer` from the firing mount to the killed missile -
a pale-cyan beam for the laser, a hot yellow-white streak for the 40 mm gun - which fades over ~0.1 s
(rendered as a brighter line that dims toward the background). The existing "PD intercepts" counters in
the combat panel and mission HUD continue to tally them.

### Homeworld-style camera selection (`ViewSettings.FocusVesselIndex`, `SceneRenderer`, `CombatPanel`)
Added `FocusVesselIndex` to the view: when set, the floating-origin camera locks onto that specific
vessel - player, NPC, or enemy - overriding the Focus mode. Three ways to select, matching Homeworld:
- **Click** a ship in the 3D view (a left-click that is not a camera drag). The renderer caches its
  last view-projection and projects every vessel to screen to pick the one under the cursor.
- **Tab** cycles the lock: free-orbit -> first ship -> next -> ... -> free-orbit.
- A **focus list** in the combat panel (a selectable row per ship, plus "Free orbit (auto)").
The lock is cleared whenever a new scenario loads.

All changes remain statically verified only (no SDK in the build container); a `dotnet build` is still
required and any issue will be mechanical.

---

## Addendum - mission time-scale (shaky/fast combat) and a pause/play CTA

**Reported:** in-mission combat is extremely shaky and runs too fast, "as if sped up".

**Root cause:** `TimeScale` is a single global setting. The sandbox lets you time-warp (e.g. x50,
x100) so an engagement that would otherwise crawl at 1x becomes watchable. But `StartMission` reset
`Paused` and `ShowPauseMenu` and never reset `TimeScale`, so whatever warp was active in the sandbox
*carried straight into the mission*. At high warp each frame advances `dt * TimeScale` seconds of sim
in one go; with the camera locked to the player (floating origin), the player stays centred while the
enemy, missiles and tracers jump a large step across the screen every frame - which reads exactly as
"shaky and sped up". The per-frame combat AI (one thrust update per frame) over a large step also puts
visible kinks in each ship's path. (The attitude slew is rate-limited to 18 deg/s and was not the
cause; `Advance` treats the 1 s fixed step as a max, so 1x itself is smooth.)

**Fix:**
- `StartMission` now sets `TimeScale = 1.0`, so missions always begin at smooth real-time regardless of
  any sandbox warp. At 1x (and the in-mission presets below) the per-frame advance is small and the
  motion is stable.
- Missions previously had no time control at all (the warp menu lives only in the sandbox menu bar), so
  the command bar now carries its own controls:
  - a **Pause / Play** button that toggles the simulation **without** opening the pause menu (the menu,
    on Esc, is unchanged), plus a **P** key shortcut;
  - **speed presets** (1x / 2x / 4x / 8x) that keep combat in a smooth range, highlighting the current
    one. The player sets the pacing instead of inheriting whatever carried in.

(High sandbox warp will still look choppy for fast combat - that is the nature of large time steps and
is the player's explicit choice there; missions are now insulated from it.)

Static verification only; a `dotnet build` is still required.

---

## Addendum - Halo "VISR" HUD overhaul (angular, frosted, animated chrome)

**Requested:** in-game control panels (sandbox and engagement mode) should have no rounded corners; every
HUD component should read as one cohesive sci-fi-helmet surface - sides curved gently inward, translucent
but frosted, and smoothly/procedurally animated, in the spirit of Halo: Infinite's VISR display.

**What changed**

*Theme (`App/Theme.cs`) - global ImGui style:*
- Every corner radius is now `0` (WindowRounding / ChildRounding / FrameRounding / GrabRounding /
  PopupRounding / ScrollbarRounding / TabRounding). Hard angular corners everywhere.
- Thin tech-frame borders: `FrameBorderSize = 1`, and the border colour is now accent-tinted rather than
  neutral grey, so inputs and buttons get a faint cyan outline.
- All three palettes were re-tuned darker, more translucent and frosted. The default "Cool" palette now
  leads with a Halo VISR cyan (accent ~0.38,0.78,0.96); Warm (gold) and Dusk (mauve) keep their hue but
  inherit the same dark frosted structure. Spacing tightened slightly for a more utilitarian feel.

*New `App/HudChrome.cs` - per-panel animated frame (pure ImGui draw-list work, same class of code as
`AmbientBackdrop`, so no GL state and nothing that can destabilise the renderer):*
- A frosted translucent fill (a dark blue-glass vertical gradient) plus very faint horizontal scan lines
  for a "frosted display" texture. Appropriate over mostly-black space - a true backdrop blur would add
  little against the void and is deliberately approximated rather than implemented.
- An angular beveled outline whose **left and right rails bow gently inward** (a shallow sine curve), so
  panels read as the requested "curved inward" sci-fi plates and feel like one consistent frame.
- Bright chamfer accents at the four cut corners (Halo targeting-tick feel).
- A slow **animated scan sweep** - a soft bright band travelling top-to-bottom - and a gently breathing
  border glow. Both are driven by `ImGui.GetTime()`, so the HUD keeps animating even while paused.

*Applied to the control panels:*
- Sandbox sidebars (`App/Sidebar.cs`): both drawers and their collapsed reopen tabs now draw with a
  transparent background and no default window border, with `HudChrome` providing the frosted angular
  surface behind the header and scrolling sections.
- Mission HUD (`App/MissionHud.cs`): the banner, the command bar, the pause card and the result modal all
  use the same chrome. The result modal tints its frame green on victory / red on defeat.

**Scope note:** this pass covers the primary control panels (sidebars + mission HUD). The settings window
and the top menu bar already lost their rounding via the theme; they can be given the same custom chrome
later if wanted. Static verification only - a `dotnet build` is still required; any issue will be mechanical.

---

## Addendum - HUD layout fixes (perf overlay orientation/overlap, command-bar overflow)

**Reported:** (1) the sandbox perf readout (FPS / resolution / MSAA / draw-cull) is stacked vertically and
its box slightly clips into the top of the "View & Data" sidebar; (2) the engagement command bar's content
overflows past the bottom/side of the panel.

**Sandbox - perf overlay**
- *Orientation:* `Hud.DrawPerf` now lays the four metrics out as a single horizontal row
  (`165 FPS | 2560x1334 (1.00x) | MSAA 4x | draw 2 cull 1`) separated by thin rules, instead of four
  stacked lines.
- *Overlap (root cause):* the sidebars reserve a top band computed as `MenuBar(const 30) + overlayHeight +
  gap`. The real main-menu bar is taller than 30 px at the current 1.35x font, so the reserve undershot the
  overlay's actual bottom by a few pixels and the perf box clipped into the sidebar - regardless of the
  overlay's height. Fixed by having both top overlays (flight strip and perf readout) report their measured
  **bottom edge** (`GetWindowPos().Y + GetWindowHeight()`) and reserving from that plus a gap, so the band is
  correct at any font scale or menu-bar height. The flight strip's right-side width cap was also widened so
  the now-wider horizontal perf row can never meet it on a narrow window.

**Engagement - command bar**
- The bar was widened (580 -> 660 px) so the readout rows and the controls hint sit comfortably inside it.
- The controls hint is now wrapped at the panel edge (`PushTextWrapPos`) instead of a single clipped line, so
  it can never run off the right side regardless of width or wording.

Static verification only; a `dotnet build` is still required. Any issue will be mechanical.
