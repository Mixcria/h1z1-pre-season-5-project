using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class StringHashValueTests
{
    [Fact]
    public void ClientSettingsAllowFullSpeedDismount()
    {
        var setting = Assert.Single(StringHashValues.Entries, e => e.Name == "Vehicle.DefaultMaxDismountSpeed");
        Assert.Equal(0x2503A5E0u, setting.Hash);
        Assert.Equal("10000.0", setting.Value);
    }

    // Hashes the binary bakes in, each resolved against the client's own StringHashToValue.txt:
    // FUN_14220be70 (model file name), FUN_140aee2a0 case 0x18, FUN_140f034a0 (two doubles).
    [Theory]
    [InlineData("Model.DescriptorReplaceString", 0x8F15DDC2u)]
    [InlineData("Network.KeepAlive", 0xC0DC29E7u)]
    [InlineData("Audio.MinGrenadeBounceVelocity", 0x310129C3u)]
    [InlineData("Audio.GrenadeBounceEffectId", 0x294FC64Cu)]
    public void HashMatchesTheClientsBakedInConstants(string name, uint expected) =>
        Assert.Equal(expected, StringHashValue.HashName(name));

    [Fact]
    public void GeneratedTableHashesAgreeWithTheRuntimeHash()
    {
        Assert.True(StringHashValues.Entries.Count > 500);
        Assert.All(StringHashValues.Entries, entry => Assert.Equal(StringHashValue.HashName(entry.Name), entry.Hash));
        Assert.Equal(StringHashValues.Entries.Count, StringHashValues.Entries.Select(e => e.Hash).Distinct().Count());
        Assert.Contains(StringHashValues.Entries, e => e.Name == "Model.DescriptorReplaceString" && e.Value == "<faction>");
    }

    [Fact]
    public void RecordAndListHaveTheParserShape()
    {
        // FUN_140a4dc30: i32 count; per record i32 hash; str value; u8 flag; str name.
        using var writer = new PacketWriter();
        StringHashValue.WriteList(writer, [new StringHashValue("Network.KeepAlive", "1")]);
        Assert.Equal(
            Convert.FromHexString(
                "01000000" +
                "E729DCC0" +
                "01000000" + "31" +
                "00" +
                "11000000" + "4E6574776F726B2E4B656570416C697665"),
            writer.Written.ToArray());
    }

    [Fact]
    public void EmptyListIsFourZeroBytes()
    {
        using var writer = new PacketWriter();
        StringHashValue.WriteList(writer, null);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, writer.Written.ToArray());
    }

    [Fact]
    public void ZoneDetailsCarriesTheTableAfterTheFrozenHead()
    {
        using var bare = new PacketWriter();
        new SendZoneDetails("LoginZone").WriteTo(bare);

        using var full = new PacketWriter();
        new SendZoneDetails("LoginZone", Values: StringHashValues.Entries).WriteTo(full);

        using var list = new PacketWriter();
        StringHashValue.WriteList(list, StringHashValues.Entries);

        // Everything before the trailing list is unchanged (the 190-byte head of docs/07 vector C).
        Assert.Equal(bare.Written.Slice(0, bare.Position - 4).ToArray(), full.Written.Slice(0, bare.Position - 4).ToArray());
        Assert.Equal(list.Written.ToArray(), full.Written.Slice(bare.Position - 4).ToArray());
    }

    [Fact]
    public void StringHashToValueManagerPacketIsOpcodeThenList()
    {
        using var writer = new PacketWriter();
        new StringHashToValueManager([new StringHashValue("Network.KeepAlive", "1")]).WriteTo(writer);
        Assert.Equal(0xFB, writer.Written[0]);
        Assert.Equal(Convert.FromHexString("01000000E729DCC0"), writer.Written.Slice(1, 8).ToArray());
    }
}
