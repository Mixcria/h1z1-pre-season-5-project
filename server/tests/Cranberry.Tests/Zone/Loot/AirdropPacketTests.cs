using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed class AirdropPacketTests
{
    private static AirdropDisplaySegment Plane => new(
        AirdropPackets.PlaneModelId, 0x12345678, 60f, 0.5f, 0, Vector4.Zero, 0,
        [new(0, new(-1000, 850, 30, 0)), new(1, new(1000, 850, 30, 0))]);

    [Fact]
    public void AugustReaderFieldOrderAndLengthsArePreserved()
    {
        // The 1148 parser consumes u8 + u16, not a two-byte command base. Segment fields
        // follow 14126e510: +0,+4,+8,+c,+138,+140..14c,+150,count,progress,xyzw.
        byte[] packet = AirdropPackets.DeliveryDisplayInfo(7, [Plane]);
        Assert.Equal(95, packet.Length);
        Assert.Equal(Convert.FromHexString(
            "0950000700000001000000FF23000078563412000070420000003F00000000"
            + "000000000000000000000000000000000000000002000000"
            + "0000000000007AC4008054440000F04100000000"
            + "0000803F00007A44008054440000F04100000000"), packet);
    }

    [Fact]
    public void CrateAndBombSegmentsShareOneDeliveryWithoutEntityGuids()
    {
        var landing = new Vector4(10, 42, 20, 0);
        AirdropDisplayWaypoint[] descent = [new(0, new(10, 850, 20, 0)), new(1, landing)];
        AirdropDisplaySegment crate = new(AirdropPackets.ParachuteCrateModelId, 12345, 20, 0,
            AirdropPackets.LandingEffectId, landing, 0, descent);
        AirdropDisplaySegment bomb = crate with
        {
            ModelId = AirdropPackets.BombModelId,
            EndEffectId = AirdropPackets.BombExplosionEffectId,
            DurationSeconds = 6,
        };
        byte[] packet = AirdropPackets.DeliveryDisplayInfo(8, [Plane, crate, bomb]);
        Assert.Equal(11 + 3 * 84, packet.Length);
        var reader = new PacketReader(packet.AsSpan(7));
        Assert.Equal(3u, reader.ReadUInt32());
        var crateReader = new PacketReader(packet.AsSpan(11 + 84));
        Assert.Equal(9219u, crateReader.ReadUInt32());
        Assert.Equal(12345u, crateReader.ReadUInt32());
        Assert.Equal(20f, crateReader.ReadSingle());
        Assert.Equal(0f, crateReader.ReadSingle());
        Assert.Equal(5038u, crateReader.ReadUInt32());
        var bombReader = new PacketReader(packet.AsSpan(11 + 168));
        Assert.Equal(9372u, bombReader.ReadUInt32());
        Assert.Equal(12345u, bombReader.ReadUInt32());
        Assert.Equal(6f, bombReader.ReadSingle());
        Assert.Equal(0f, bombReader.ReadSingle());
        Assert.Equal(5328u, bombReader.ReadUInt32());
        Assert.Equal("PFX_Impact_Explosion_AirdropBomb_Default_10m",
            AugustEffectCatalog.ById(AirdropPackets.BombExplosionEffectId)!.Value.Name);
    }

    [Fact]
    public void InvalidRailsCannotReachTheClientsUnguardedInterpolator()
    {
        Assert.Throws<ArgumentException>(() => AirdropPackets.DeliveryDisplayInfo(1, [Plane with { DurationSeconds = 0 }]));
        Assert.Throws<ArgumentException>(() => AirdropPackets.DeliveryDisplayInfo(1, [Plane with { DurationSeconds = float.NaN }]));
        Assert.Throws<ArgumentException>(() => AirdropPackets.DeliveryDisplayInfo(1,
            [Plane with { Waypoints = [new(0, Vector4.Zero), new(0, Vector4.Zero), new(1, Vector4.One)] }]));
        Assert.Throws<ArgumentException>(() => AirdropPackets.DeliveryDisplayInfo(1,
            [Plane with { Waypoints = [new(0.5f, Vector4.Zero), new(1, Vector4.One)] }]));
        Assert.Throws<ArgumentException>(() => AirdropPackets.DeliveryDisplayInfo(1,
            [Plane with { Waypoints = [new(0, Vector4.Zero), new(1, new(float.NaN, 0, 0, 0))] }]));
    }
}
