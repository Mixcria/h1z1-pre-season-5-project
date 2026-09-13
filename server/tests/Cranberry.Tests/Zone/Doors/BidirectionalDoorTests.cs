using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

public sealed class BidirectionalDoorTests
{
    private static readonly Lazy<Z2Doors> Data = new(Z2Doors.LoadDefault);
    private sealed class Sink : IPeerSink { public bool IsOpen => true; public void Send(byte[] packet) { } }
    private static byte[] Packet(DoorInstance door)
    {
        using var writer = new PacketWriter(); door.StateUpdate().WriteTo(writer); return writer.Written.ToArray();
    }
    private static (SharedMatchDoors Shared, MatchDoors A, MatchDoors B, DoorInstance DoorA, DoorInstance DoorB) Pair()
    {
        var shared = new SharedMatchDoors();
        var a = new MatchDoors(Data.Value, shared: shared); var b = new MatchDoors(Data.Value, shared: shared);
        shared.Join(a, new Sink()); shared.Join(b, new Sink());
        int index = Enumerable.Range(0, Data.Value.Count).First(i => Data.Value.KindOf(Data.Value[i]).Name == "CommercialGlass");
        return (shared, a, b, a.Register(index), b.Register(index));
    }
    private static Vector3 Side(DoorInstance door, float distance) => door.Position
        + new Vector3(MathF.Sin(door.Yaw) * distance, 0, MathF.Cos(door.Yaw) * distance);

    [Theory]
    [InlineData(0f, 0f, 2f, -1)]
    [InlineData(0f, 0f, -2f, 1)]
    [InlineData(1.5707963268f, 2f, 0f, -1)]
    [InlineData(1.5707963268f, -2f, 0f, 1)]
    [InlineData(3.1415926536f, 0f, 2f, 1)]
    public void OrdinaryLeavesOpenAwayFromBothSides(float yaw, float x, float z, int expected)
        => Assert.Equal(expected, DoorSwing.AwayFrom(Vector3.Zero, yaw, "CommercialGlass", new(x, 3, z)));

    [Theory]
    [InlineData(0f, 2f, 0f, -1)]
    [InlineData(0f, -2f, 0f, 1)]
    [InlineData(1.5707963268f, 0f, -2f, -1)]
    [InlineData(1.5707963268f, 0f, 2f, 1)]
    public void CamperUsesItsDifferentMeshAxis(float yaw, float x, float z, int expected)
        => Assert.Equal(expected, DoorSwing.AwayFrom(Vector3.Zero, yaw, "Camper", new(x, 0, z)));

    [Fact]
    public void SecondPlayerClosesFirstPlayersDoorAndReopeningCanUseTheOtherSide()
    {
        var (shared, a, b, da, db) = Pair();
        Assert.Equal(DoorToggleOutcome.Toggled, a.TryToggle(da.WorldGuid, 1000, out _, Side(da, 2)));
        Assert.True(db.IsOpen); Assert.Equal(-1, db.SwingDirection);
        byte[] open = Packet(db);
        Assert.Equal(DoorStateDelta.Length, open.Length); Assert.Equal(0x3f, open[1]);
        Assert.Equal(DoorSwing.NegativeSource, BinaryPrimitives.ReadUInt64LittleEndian(open.AsSpan(10)));
        Assert.Equal(DoorStateBits.Open, BinaryPrimitives.ReadUInt64LittleEndian(open.AsSpan(18)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(open.AsSpan(26)));
        Assert.Equal(DoorToggleOutcome.Toggled, b.TryToggle(db.WorldGuid, 2000, out _, Side(db, -2)));
        Assert.False(da.IsOpen); Assert.False(shared.IsOpen(da.InstanceId)); Assert.Equal(-1, da.SwingDirection);
        byte[] closed = Packet(da);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(closed.AsSpan(10)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(closed.AsSpan(18)));
        Assert.Equal(DoorStateBits.Open, BinaryPrimitives.ReadUInt64LittleEndian(closed.AsSpan(26)));
        Assert.Equal(DoorToggleOutcome.Toggled, a.TryToggle(da.WorldGuid, 3000, out _, Side(da, -2)));
        Assert.True(db.IsOpen); Assert.Equal(1, db.SwingDirection);
        Assert.Equal(DoorSwing.PositiveSource, BinaryPrimitives.ReadUInt64LittleEndian(Packet(db).AsSpan(10)));
    }

    [Fact]
    public void AnotherPlayerAndAStreamedBackViewerCannotReverseAnUnfinishedSwing()
    {
        var (shared, a, b, da, db) = Pair();
        a.TryToggle(da.WorldGuid, 1000, out _, Side(da, 2));
        Assert.Equal(DoorToggleOutcome.Absorbed, b.TryToggle(db.WorldGuid, 1002, out _, Side(db, -2)));
        Assert.Equal(DoorToggleOutcome.Absorbed, a.TryToggle(da.WorldGuid, 1003, out _, Side(da, -2)));
        b.Unregister(db.WorldGuid); db = b.Register(db.DoorIndex);
        Assert.True(db.IsOpen); Assert.Equal(-1, db.SwingDirection); Assert.Equal(1000, db.LastToggleMs);
        Assert.Equal(DoorToggleOutcome.Absorbed, b.TryToggle(db.WorldGuid, 1799, out _, Side(db, -2)));
        Assert.Equal(DoorToggleOutcome.Toggled, b.TryToggle(db.WorldGuid, 1800, out _, Side(db, -2)));
        Assert.False(da.IsOpen); Assert.Equal(0, shared.OpenCount);
    }

    [Fact]
    public void AJoiningViewerGetsTheSameDirectionAndItsMatchResetForgetsIt()
    {
        var (shared, a, b, da, _) = Pair();
        a.TryToggle(da.WorldGuid, 1000, out _, Side(da, 2));
        var c = new MatchDoors(Data.Value, shared: shared); shared.Join(c, new Sink());
        DoorInstance dc = c.Register(da.DoorIndex);
        Assert.True(dc.IsOpen); Assert.Equal(-1, dc.SwingDirection); Assert.Equal(Packet(da), Packet(dc));
        Assert.Equal(DoorToggleOutcome.Absorbed, c.TryToggle(dc.WorldGuid, 1100, out _, Side(dc, -2)));
        shared.Leave(a); shared.Leave(b); shared.Leave(c);
        var next = new MatchDoors(Data.Value, shared: shared); shared.Join(next, new Sink());
        DoorInstance fresh = next.Register(da.DoorIndex);
        Assert.False(fresh.IsOpen); Assert.Equal(0, fresh.SwingDirection); Assert.Equal(long.MinValue, fresh.LastToggleMs);
    }

    [Fact]
    public void LocalStreamingRetainsDirectionAndDifferentMatchesStayIndependent()
    {
        var first = new MatchDoors(Data.Value); var second = new MatchDoors(Data.Value);
        DoorInstance a = first.Register(0), b = second.Register(0);
        first.TryToggle(a.WorldGuid, 1000, out _, Side(a, 2));
        int direction = a.SwingDirection;
        first.Unregister(a.WorldGuid); a = first.Register(0);
        Assert.True(a.IsOpen); Assert.Equal(direction, a.SwingDirection); Assert.Equal(1000, a.LastToggleMs);
        Assert.False(b.IsOpen); Assert.Equal(long.MinValue, b.LastToggleMs);
    }

    [Fact]
    public void RealGlassDoubleDoorsOpenBothLeavesOntoTheSameSide()
    {
        var glass = Enumerable.Range(0, Data.Value.Count).Where(i => Data.Value.KindOf(Data.Value[i]).Name == "CommercialGlass").ToArray();
        int pairs = 0;
        for (int i = 0; i < glass.Length; i++) for (int j = i + 1; j < glass.Length; j++)
        {
            DoorPlacement a = Data.Value[glass[i]], b = Data.Value[glass[j]];
            float distance = Vector3.Distance(a.Position, b.Position);
            if (distance is < 2.3f or > 2.9f || MathF.Abs(a.Y - b.Y) > .3f || MathF.Cos(a.Yaw - b.Yaw) > -.999f) continue;
            pairs++;
            Vector3 middle = (a.Position + b.Position) / 2;
            Vector3 normal = new(MathF.Sin(a.Yaw), 0, MathF.Cos(a.Yaw));
            foreach (int side in new[] { -1, 1 })
            {
                Vector3 player = middle + normal * side * 2;
                int sa = DoorSwing.AwayFrom(a.Position, a.Yaw, "CommercialGlass", player);
                int sb = DoorSwing.AwayFrom(b.Position, b.Yaw, "CommercialGlass", player);
                Assert.Equal(-sa, sb);
                foreach (var door in new[] { a, b })
                {
                    int sign = DoorSwing.AwayFrom(door.Position, door.Yaw, "CommercialGlass", player);
                    Vector3 initialVelocity = new(MathF.Sin(door.Yaw) * sign, 0, MathF.Cos(door.Yaw) * sign);
                    Assert.True(Vector3.Dot(initialVelocity, player - middle) < -1.9f);
                }
            }
        }
        Assert.Equal(24, pairs);
    }

    [Fact]
    public void InvalidCoordinatesAndAnExactlyOnPlanePlayerHaveADeterministicFiniteFallback()
    {
        Assert.Equal(1, DoorSwing.AwayFrom(Vector3.Zero, 0, "CommercialGlass", new(0, 0, float.NaN)));
        Assert.Equal(1, DoorSwing.AwayFrom(Vector3.Zero, 0, "CommercialGlass", Vector3.Zero));
    }
}
