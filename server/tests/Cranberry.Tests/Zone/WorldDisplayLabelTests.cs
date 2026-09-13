using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class WorldDisplayLabelTests
{
    [Theory]
    [InlineData("Solo EU World 1")]
    [InlineData("Duos EU World 2")]
    [InlineData("Fives EU World 2")]
    [InlineData("Hosted Games")]
    [InlineData("")]
    public void LabelUpdatePreservesEveryGameplayDefaultAndWritesExactName(string name)
    {
        IReadOnlyList<StringHashValue> values = WorldDisplayLabel.Values(name);
        Assert.Equal(StringHashValues.Entries.Count + 2, values.Count);
        Assert.Equal(StringHashValues.Entries, values.Take(values.Count - 2));
        Assert.Equal(new StringHashValue("Cranberry.Healing", "0"), values[^2]);
        Assert.Equal(values.Count, values.Select(value => value.Hash).Distinct().Count());
        Assert.Equal(new StringHashValue(WorldDisplayLabel.Key, name), values[^1]);

        using var writer = new PacketWriter();
        new StringHashToValueManager(values).WriteTo(writer);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(ZoneOpcodes.StringHashToValueManager, reader.ReadByte());
        Assert.Equal(values.Count, reader.ReadInt32());
        foreach (StringHashValue value in values)
        {
            Assert.Equal(value.Hash, reader.ReadUInt32());
            Assert.Equal(value.Value, reader.ReadString());
            Assert.False(reader.ReadBool());
            Assert.Equal(value.Name, reader.ReadString());
        }
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void CreatingAnotherWorldOrMenuLabelDoesNotMutatePriorPacket()
    {
        IReadOnlyList<StringHashValue> first = WorldDisplayLabel.Values("Fives EU World 2");
        IReadOnlyList<StringHashValue> hosted = WorldDisplayLabel.Values("Hosted Games");
        IReadOnlyList<StringHashValue> menu = WorldDisplayLabel.Values("");
        Assert.Equal("Fives EU World 2", first[^1].Value);
        Assert.Equal("Hosted Games", hosted[^1].Value);
        Assert.Equal("", menu[^1].Value);
        Assert.DoesNotContain(StringHashValues.Entries, entry => entry.Name == WorldDisplayLabel.Key);
    }
}
