using Cranberry.Zone;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// Wave 10 (docs/92). The owner reports doors that spawn looking open and swing into the building.
/// The server's own burst line says every door was spawned CLOSED, so what is wrong is the POSE the
/// client draws — and these tests pin the two things that decide it, plus the log fragments his
/// next report depends on.
/// </summary>
public sealed class Wave10DoorPoseTests
{
    private static readonly Lazy<Z2Doors> Dataset = new(Z2Doors.LoadDefault);

    /// <summary>
    /// The yaw SIGN is the live sweep, and both packings must stay reachable: they differ only in
    /// the sign of the quaternion's y, which is a door hinged on the other jamb — exactly the
    /// owner's "swings the wrong way".
    /// </summary>
    [Fact]
    public void TheTwoYawSignsAreDistinctAndBothReachable()
    {
        const float yaw = MathF.PI / 2f;

        System.Numerics.Vector4 up = DoorRotationPacking.Pack(yaw, DoorRotation.QuaternionYUp);
        System.Numerics.Vector4 negated = DoorRotationPacking.Pack(yaw, DoorRotation.QuaternionYUpNegated);

        Assert.Equal(MathF.Sin(yaw / 2f), up.Y, 5);
        Assert.Equal(-MathF.Sin(yaw / 2f), negated.Y, 5);
        Assert.Equal(up.W, negated.W, 5);

        // The 180-degree consequence, which is why only SOME doors look wrong: a yaw of 0 or +/-pi
        // is unchanged by the sign, a yaw of +/-pi/2 is a half turn out.
        Assert.Equal(
            DoorRotationPacking.Pack(0f, DoorRotation.QuaternionYUp).Y,
            DoorRotationPacking.Pack(0f, DoorRotation.QuaternionYUpNegated).Y,
            5);
    }

    /// <summary>Every door is registered closed. A door that looks open was drawn open, not held open.</summary>
    [Fact]
    public void EveryDoorIsRegisteredClosed()
    {
        Z2Doors doors = Dataset.Value;
        var match = new MatchDoors(doors);

        for (int i = 0; i < 64 && i < doors.Count; i++)
        {
            Assert.False(match.Register(i).IsOpen);
        }

        Assert.Equal(0, match.OpenCount);
    }

    /// <summary>
    /// The burst line names the families present, so the owner's next report can say WHICH family
    /// spawned open — Camper points at the mesh axis, anything else points at the yaw sign.
    /// </summary>
    [Fact]
    public void TheBurstLineNamesTheFamiliesPresent()
    {
        Z2Doors doors = Dataset.Value;
        var match = new MatchDoors(doors);

        Assert.Equal(string.Empty, DoorBurstReport.DescribeFamilies(match));

        DoorInstance first = match.Register(0);
        string line = DoorBurstReport.DescribeFamilies(match);

        Assert.StartsWith(" [", line, StringComparison.Ordinal);
        Assert.EndsWith("]", line, StringComparison.Ordinal);
        Assert.Contains(first.KindName, line, StringComparison.Ordinal);
        Assert.Contains(" 1", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A door whose spawn record carries the collidable bit is never labelled "no-collision", even
    /// when its model is the visual mesh. That label is what made the last two reports hard to read.
    /// </summary>
    [Fact]
    public void ACollidableFlaggedVisualDoorIsNotLabelledNoCollision()
    {
        Z2Doors doors = Dataset.Value;
        var match = new MatchDoors(
            doors,
            new MatchDoorOptions
            {
                CollisionMode = DoorCollisionMode.VisibleMesh,
                SpawnFlags1 = LightweightEntityBody.DoorSpawnFlagsDefault,
            });

        DoorInstance door = match.Register(0);

        Assert.False(door.IsCollidable);                  // the MODEL is not a kinematic twin
        Assert.Equal("flag", door.CollisionSourceLabel);  // but the wire still claims collision
        Assert.DoesNotContain("no-collision", door.ToString(), StringComparison.Ordinal);
    }
}
