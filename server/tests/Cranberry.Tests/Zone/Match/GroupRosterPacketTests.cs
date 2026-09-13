using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchEndgame;

public sealed class GroupRosterPacketTests
{
    [Fact]
    public void PlayerKillsUsesTheNativeMetricPacketWithNoGroupExecutionByte()
    {
        using var writer = new PacketWriter();
        new PlayerKillCountUpdate(0x0102030405060708, 7).WriteTo(writer);
        // 140a342a0 consumes u8 base, u16 sub, metric u32, value u32, character u64.
        Assert.Equal(Convert.FromHexString("114A0001000000070000000807060504030201"), writer.Written.ToArray());
    }

    [Fact]
    public void FullRosterMatchesAugustHeaderIdentityArrayAndMemberBoundaries()
    {
        using var writer = new PacketWriter();
        new GroupRoster(0x40000001, 42, [new(42, "Sam", new(10, 20, 30)), new(43, "Max", Vector3.Zero, 1)])
            .WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();
        Assert.Equal(347, bytes.Length);
        Assert.Equal(new byte[] { 0x13, 0x12, 2 }, bytes[..3]);
        Assert.Equal(0x40000001u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(3)));
        Assert.Equal(42ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(7)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(25)));
        Assert.Equal(42ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(29))); // map key
        Assert.Equal(42ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(37))); // member guid
        Assert.Equal("Sam", System.Text.Encoding.UTF8.GetString(bytes, 61, 3));
        Assert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(138)));
        Assert.Equal(20f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(142)));
        Assert.Equal(30f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(146)));
        Assert.Equal(43ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(186))); // next member
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(343))); // trailing field
    }

    [Fact]
    public void RemovingAGroupCarriesAnErrorFieldBeforeTheGroupId()
    {
        using var writer = new PacketWriter();
        new RemoveGroup(123).WriteTo(writer);
        Assert.Equal(new byte[] { 0x13, 0x16, 2, 0, 0, 0, 0, 123, 0, 0, 0 }, writer.Written.ToArray());
    }
}
