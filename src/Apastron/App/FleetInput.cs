using System;
using System.Collections.Generic;
using System.Numerics;
using Apastron.Audio;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Render;
using Apastron.Simulation;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Turns raw 3D-view mouse input (surfaced by <see cref="SceneRenderer"/>) into RTS selection and, in a later
/// chunk, fleet move orders. Left-click selects the ship under the cursor (Shift adds/removes); a left-drag
/// band-selects every ship inside the marquee (Shift adds to the current selection). Selection is stored on
/// <see cref="GameContext.Selection"/> as live <see cref="RigidBody"/> references and pruned when a ship
/// leaves the world.
/// </summary>
public static class FleetInput
{
    private static readonly List<int> _hits = new();      // scratch reused across band-selects
    private static readonly List<Vec3> _offsets = new();   // scratch reused across formation layouts

    public static void Apply(GameContext ctx, SceneRenderer scene, bool shiftHeld)
    {
        // Drop any selected ships that have been destroyed/removed since last frame.
        if (ctx.Selection.Count > 0)
            ctx.Selection.RemoveWhere(v => !ctx.World.Vessels.Contains(v));

        // ----- clean left-click: select one ship (Shift toggles) -----
        if (scene.TryConsumeSelectClick(out Vector2 click))
        {
            int picked = scene.PickVessel(click, ctx.World);
            if (picked >= 0 && !Controllable(ctx, ctx.World.Vessels[picked]))
            {
                // Clicked a non-friendly ship: leave the current selection untouched.
            }
            else
            {
                if (!shiftHeld) ctx.Selection.Clear();
                if (picked >= 0)
                {
                    RigidBody v = ctx.World.Vessels[picked];
                    if (shiftHeld && ctx.Selection.Contains(v)) ctx.Selection.Remove(v);
                    else ctx.Selection.Add(v);
                    ctx.View.FocusVesselIndex = picked;   // keep the click-to-focus camera convenience
                }
            }
        }

        // ----- band-select: every controllable ship inside the marquee (Shift adds) -----
        if (scene.TryConsumeBand(out Vector2 bmin, out Vector2 bmax))
        {
            scene.PickVesselsInRect(bmin, bmax, ctx.World, _hits);
            if (!shiftHeld) ctx.Selection.Clear();
            foreach (int i in _hits)
                if (i >= 0 && i < ctx.World.Vessels.Count && Controllable(ctx, ctx.World.Vessels[i]))
                    ctx.Selection.Add(ctx.World.Vessels[i]);
        }

        // ----- right-click / altitude gizmo: move the whole selection to a world point -----
        if (scene.TryConsumeMoveTarget(out Vec3 target))
        {
            if (ctx.Selection.Count > 0)
            {
                IssueMove(ctx, target, shiftHeld);   // Shift queues a waypoint instead of replacing
                ctx.Audio.Play(GameSound.UiClick);
            }
        }
    }

    // The flagship is always commandable; other ships must be flagged (escorts, player combatants).
    private static bool Controllable(GameContext ctx, RigidBody v) =>
        ReferenceEquals(v, ctx.World.PrimaryVessel) || v.Controllable;

    // Spread the selection into the active formation around the target and hand each ship its own waypoint.
    // The formation is oriented to the fleet's heading (group centroid -> target).
    private static void IssueMove(GameContext ctx, Vec3 target, bool queue)
    {
        int n = ctx.Selection.Count;
        const double spacing = 2500.0;   // m between neighbouring ships

        Vec3 centroid = Vec3.Zero;
        foreach (RigidBody v in ctx.Selection) centroid += v.Position;
        centroid = centroid / n;

        Vec3 fwd = target - centroid;
        fwd = fwd.Length > 1.0 ? fwd.Normalized() : Vec3.UnitX;
        Vec3 rAxis = Vec3.Cross(fwd, Vec3.UnitY);
        Vec3 right = rAxis.Length > 1e-6 ? rAxis.Normalized() : Vec3.UnitX;
        Vec3 up = Vec3.Cross(right, fwd).Normalized();

        Formations.Layout(ctx.Formation, n, fwd, right, up, spacing, _offsets);

        int i = 0;
        foreach (RigidBody v in ctx.Selection)
        {
            Vec3 slot = i < _offsets.Count ? target + _offsets[i] : target;
            ctx.Fleet.OrderMove(v, slot, queue);
            // Stop the flagship's manual burn so the fleet controller isn't fighting the propulsion bridge.
            if (ReferenceEquals(v, ctx.World.PrimaryVessel)) ctx.Ship.Mode = ThrustMode.None;
            i++;
        }
    }
}
