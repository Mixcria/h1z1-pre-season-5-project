using System.Numerics;

namespace Cranberry.Zone.World.Doors;

/// <summary>One authoritative motion state, retained across viewers and streaming.</summary>
internal sealed class DoorMotionState
{
    public bool IsOpen;
    public long LastToggleMs = long.MinValue;
    public int SwingDirection;
}

public static class DoorSwing
{
    // Cranberry extension carried in the source-guid field of an open-only 0f 3f.
    // The native helper recognizes only these exact markers on a closed door.
    public const ulong PositiveSource = 0x4352_444F_4F52_3100;
    public const ulong NegativeSource = PositiveSource + 1;

    /// <summary>Choose the quarter turn whose initial leaf motion is away from the player.</summary>
    public static int AwayFrom(Vector3 hinge, float closedYaw, string kindName, Vector3 player)
    {
        if (!float.IsFinite(player.X) || !float.IsFinite(player.Z)
            || !float.IsFinite(hinge.X) || !float.IsFinite(hinge.Z) || !float.IsFinite(closedYaw))
            return 1;
        float dx = player.X - hinge.X, dz = player.Z - hinge.Z;
        // The shipped door meshes extend along local -X; the camper extends along +Z.
        // A positive native yaw moves those leaves toward local +Z and +X respectively.
        float side = kindName == "Camper"
            ? dx * MathF.Cos(closedYaw) - dz * MathF.Sin(closedYaw)
            : dx * MathF.Sin(closedYaw) + dz * MathF.Cos(closedYaw);
        return side > 0f ? -1 : 1;
    }
}
