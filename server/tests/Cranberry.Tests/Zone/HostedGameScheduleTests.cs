using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class HostedGameScheduleTests
{
    [Fact]
    public void HostedEntryMatchesTheAugustParserAndSuppliesNativeJoinFields()
    {
        const ulong now = 1788696000;
        HostedGameScheduleEntry[] games = [new(GameWorldCatalog.HostedWorldId) { UnlockTime = now, StartTime = now }];
        using var writer = new PacketWriter();
        new MatchScheduleReply(games).WriteTo(writer);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(0x67, reader.ReadByte());
        Assert.Equal(0x12, reader.ReadByte());
        Assert.Equal(1, reader.ReadInt32());
        uint worldId = reader.ReadUInt32();
        Assert.Equal(GameWorldCatalog.HostedWorldId, worldId);
        Assert.True(GameWorldCatalog.TryGet(worldId, out var advertised));
        Assert.True(advertised.IsHosted);
        Assert.Equal(1u, reader.ReadUInt32()); // MatchGameModeName.1
        Assert.Equal(13u, reader.ReadUInt32());
        Assert.Equal(15997u, reader.ReadUInt32());
        Assert.Equal(15998u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32()); // image
        Assert.Equal(now, reader.ReadUInt64()); // unlock
        Assert.Equal(now, reader.ReadUInt64()); // start
        Assert.Equal(0u, reader.ReadUInt32()); // no entry fee
        Assert.Equal("", reader.ReadString());
        Assert.Equal(0u, reader.ReadUInt32()); // no event tickets
        Assert.Equal(0u, reader.ReadUInt32()); // no prize item
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.False(reader.ReadBool()); // unlocked
        Assert.True(reader.ReadBool()); // can enter
        Assert.Equal("", reader.ReadString()); // no watch URL
        Assert.True(reader.ReadBool()); // IsHostedGame=1 selects hosted tab
        Assert.Equal(1u, reader.ReadUInt32()); // normal player role
        Assert.False(reader.ReadBool()); // watch disabled
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadInt32()); // empty trailing u64 list
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void NoScheduledGamesStillHasTheExistingValidEmptyShape()
    {
        using var writer = new PacketWriter();
        new MatchScheduleReply().WriteTo(writer);
        Assert.Equal(Convert.FromHexString("671200000000"), writer.Written.ToArray());
    }
}
