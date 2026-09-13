using System.Buffers.Binary;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

public sealed class PeerMovementSnapshotTests
{
    private static readonly byte[] Falling = Convert.FromHexString("FF01C5F5B21C0084009B180000000000000000EB04EA04000000");
    private static readonly byte[] Precise = Convert.FromHexString("FF1F99F5B21C00850100000000000000000000000000000000000000000000000000000000");

    [Fact]
    public void ARecordedPoseAndSparseChangesProduceTheSameStateWithoutRequantizingItsFields()
    {
        var snapshot = new PeerMovementSnapshot();
        Assert.True(snapshot.Update(Falling));
        Assert.Equal(Falling, snapshot.Record.ToArray());
        byte[] heading = [0x20, 0, 10, 20, 30, 40, 3, .. BitConverter.GetBytes(1.25f)];
        var before = EntityMovementState.Apply(null, ClientMovementUpdate.Parse(Falling));
        var expected = EntityMovementState.Apply(before, ClientMovementUpdate.Parse(heading));
        Assert.True(snapshot.Update(heading));
        var result = EntityMovementState.Apply(null, ClientMovementUpdate.Parse(snapshot.Record));
        Assert.Equal(expected with { LastUpdate = result.LastUpdate }, result);
        Assert.Equal(1.25f, result.Orientation);
        Assert.Equal(before.Position, result.Position);
        Assert.Equal(before.VerticalSpeed, result.VerticalSpeed);
    }

    [Fact]
    public void PreciseRecordsAreBarriersAndDoNotLeakEarlierOrdinaryFieldsIntoLaterSnapshots()
    {
        var snapshot = new PeerMovementSnapshot();
        Assert.True(snapshot.Update(Falling));
        Assert.False(snapshot.Update(Precise));
        Assert.True(snapshot.Record.IsEmpty);
        byte[] heading = [0x20, 0, 10, 20, 30, 40, 3, .. BitConverter.GetBytes(1.25f)];
        Assert.True(snapshot.Update(heading));
        Assert.Equal(heading, snapshot.Record.ToArray());
    }

    [Fact]
    public void EveryOrdinaryFieldInTheCaptureSurvivesASubsequentHeaderOnlyUpdate()
    {
        byte[] ordinary = Precise[..^7]; // Seven one-byte zero values form the precise tail.
        BinaryPrimitives.WriteUInt16LittleEndian(ordinary, 0x0fff);
        var snapshot = new PeerMovementSnapshot();
        Assert.True(snapshot.Update(ordinary));
        Assert.Equal(ordinary, snapshot.Record.ToArray());
        byte[] header = [0, 0, 1, 2, 3, 4, 5];
        Assert.True(snapshot.Update(header));
        Assert.Equal(ordinary[7..], snapshot.Record[7..].ToArray());
        Assert.Equal(header[2..], snapshot.Record[2..7].ToArray());
        Assert.Equal(0x0fff, (int)ClientMovementUpdate.Parse(snapshot.Record).Fields);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(63u)]
    [InlineData(64u)]
    [InlineData(16383u)]
    [InlineData(16384u)]
    [InlineData(4194304u)]
    [InlineData(1073741823u)]
    public void SpanPoseWriterMatchesTheEstablishedWireWriter(uint id)
    {
        byte[] expected = PeerSpawnWriter.PlayerUpdatePosition(id, Falling);
        Span<byte> actual = stackalloc byte[256];
        int length = PeerSpawnWriter.WritePlayerUpdatePosition(actual, id, Falling);
        Assert.Equal(expected, actual[..length].ToArray());
    }
}
