using Apastron.Physics;

namespace Apastron.App;

/// <summary>
/// Homeworld-style control groups. <see cref="Assign"/> snapshots the current selection into group 1-9;
/// <see cref="Recall"/> makes that group the selection and focuses its first member. Dead ships are filtered
/// on recall, so a group quietly shrinks as its members are lost.
/// </summary>
public static class FleetGroups
{
    public static void Assign(GameContext ctx, int g)
    {
        if (g < 1 || g > 9) return;
        var grp = ctx.Groups[g];
        grp.Clear();
        foreach (RigidBody v in ctx.Selection) grp.Add(v);
    }

    public static void Recall(GameContext ctx, int g)
    {
        if (g < 1 || g > 9) return;
        ctx.Selection.Clear();
        foreach (RigidBody v in ctx.Groups[g])
            if (ctx.World.Vessels.Contains(v)) ctx.Selection.Add(v);
        foreach (RigidBody v in ctx.Selection)   // focus the first surviving member
        {
            ctx.View.FocusVesselIndex = ctx.World.Vessels.IndexOf(v);
            break;
        }
    }

    public static int Count(GameContext ctx, int g) => g is >= 1 and <= 9 ? ctx.Groups[g].Count : 0;
}
