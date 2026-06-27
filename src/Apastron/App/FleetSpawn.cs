using Apastron.Combat;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Spawns a wing of controllable ships of a chosen <see cref="HullClass"/> abreast of the player flagship,
/// sharing its velocity so they hold the same orbit. Each ship is built from the class layout (so it carries
/// that class's mass, thrust and weapons) and, if an engagement is live, is enlisted player-side in the active
/// <see cref="CombatManager"/> with the requested doctrine so it fights autonomously. With no battle running
/// they still spawn as commandable bodies (their guns simply have nothing to shoot yet). Either way they are
/// selectable and fleet-steerable, and a move order overrides combat steering to reposition them.
/// </summary>
public static class FleetSpawn
{
    private static int _counter;

    public static void Squadron(GameContext ctx, HullClass cls, int count, CombatDoctrine doctrine)
    {
        RigidBody? lead = ctx.World.PrimaryVessel;
        if (lead == null || count <= 0) return;

        Vec3 fwd = lead.Velocity.Length > 1.0 ? lead.Velocity.Normalized() : Vec3.UnitX;
        Vec3 rAxis = Vec3.Cross(fwd, Vec3.UnitY);
        Vec3 right = rAxis.Length > 1e-6 ? rAxis.Normalized() : Vec3.UnitX;
        double spacing = 3000.0 + WarshipClasses.Build(cls, "x").TotalLength * 50.0;   // roomier for big hulls

        bool combat = ctx.Combat != null && ctx.Combat.Active;

        for (int i = 0; i < count; i++)
        {
            double lateral = (i - (count - 1) / 2.0) * spacing;
            Vec3 pos = lead.Position + right * lateral + fwd * 4000.0;   // line abreast, slightly ahead
            string name = $"{WarshipClasses.DisplayName(cls)} {++_counter}";

            var ship = WarshipClasses.Build(cls, name);
            var body = new RigidBody
            {
                Name = name,
                Position = pos,
                Velocity = lead.Velocity,         // matched orbit - they ride alongside the flagship
                Forward = fwd,
                Mass = ship.TotalMass,
                MaxThrust = ship.TotalThrustVac,
                HullLength = ship.TotalLength,
                Controllable = true,
            };
            ctx.World.Vessels.Add(body);

            if (combat)
            {
                var cbt = Combatant.Create(name, body, ship, doctrine);
                cbt.IsPlayer = true;
                ctx.Combat!.Combatants.Add(cbt);
            }
        }
    }
}
