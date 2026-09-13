using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Emotes;

namespace Cranberry.Tests.Zone.Emotes;

public sealed class EmotePacketTests
{
    [Fact]
    public void ManagerRowsIncludeTheOuterSlotKeyAndDefaultInstanceZero()
    {
        using var writer = new PacketWriter();
        EmotePackets.WriteItems(writer, AugustEmotes.DefaultSlots.Take(2).ToArray());

        Assert.Equal("02000000"
            + "01000000010000000000000000000000CC0C0000"
            + "02000000020000000000000000000000D70C0000",
            Convert.ToHexString(writer.Written));
        Assert.Equal(44, writer.Written.Length);
    }

    [Fact]
    public void SkinManagerPublishesDefaultsInCurrentAndSavedCollectionMaps()
    {
        var manager = new SetSkinItemManager(IncludeCatalog: true, Emotes: AugustEmotes.DefaultSlots);
        using var writer = new PacketWriter();
        manager.WriteTo(writer);
        var reader = new PacketReader(writer.Written);

        Assert.Equal(0xac, reader.ReadByte());
        Assert.Equal(0x23, reader.ReadByte());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(0, reader.ReadInt32()); // worn skins
        ReadDefaultItems(ref reader);
        Assert.Equal(2, reader.ReadInt32());
        foreach (uint collectionId in new[] { 1u, 2u })
        {
            Assert.Equal(collectionId, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(string.Empty, reader.ReadString());
            Assert.Equal(0, reader.ReadInt32()); // selected skins
            ReadDefaultItems(ref reader);
        }

        Assert.True(reader.AtEnd);
        Assert.Equal(manager.Length, writer.Written.Length);
    }

    [Fact]
    public void CollectionSelectionPreservesEveryDefaultFunctionKey()
    {
        var collection = new SetCurrentSkinItemCollection(Emotes: AugustEmotes.DefaultSlots);
        using var writer = new PacketWriter();
        collection.WriteTo(writer);
        var reader = new PacketReader(writer.Written);

        Assert.Equal(0xac, reader.ReadByte());
        Assert.Equal(0x28, reader.ReadByte());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(0, reader.ReadInt32());
        ReadDefaultItems(ref reader);
        Assert.True(reader.AtEnd);
        Assert.Equal(collection.WireLength, writer.Written.Length);
    }

    private static void ReadDefaultItems(ref PacketReader reader)
    {
        Assert.Equal(12, reader.ReadInt32());
        foreach (AugustEmote emote in AugustEmotes.DefaultSlots)
        {
            Assert.Equal(emote.SlotId, reader.ReadUInt32());
            Assert.Equal(emote.SlotId, reader.ReadUInt32());
            Assert.Equal(0ul, reader.ReadUInt64());
            Assert.Equal(emote.ItemDefinitionId, reader.ReadUInt32());
        }
    }

    [Theory]
    [InlineData("F70102000000", true, 2u)]
    [InlineData("F70103000000", true, 3u)]
    [InlineData("F70203000000", false, 3u)]
    public void RequestsCarryAnAnimationIdWithoutACharacterGuid(string hex, bool isStart, uint id)
    {
        Assert.True(EmotePackets.TryParseRequest(Convert.FromHexString(hex), out EmoteRequest request));
        Assert.Equal(new EmoteRequest(isStart, id), request);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F7")]
    [InlineData("F701020000")]
    [InlineData("F7010200000000")]
    [InlineData("F60102000000")]
    [InlineData("F70002000000")]
    [InlineData("F70302000000")]
    [InlineData("F70402000000")]
    [InlineData("F70100000000")]
    [InlineData("F702FFFFFFFF")]
    [InlineData("F703887766554433221103000000")]
    public void MalformedOrServerPlaybackPayloadsAreNotClientRequests(string hex)
    {
        Assert.False(EmotePackets.TryParseRequest(Convert.FromHexString(hex), out EmoteRequest request));
        Assert.Equal(default, request);
    }

    [Fact]
    public void StructurallyValidAccountEmoteStillRequiresCatalogAuthorization()
    {
        Assert.True(EmotePackets.TryParseRequest(Convert.FromHexString("F7011A000000"), out EmoteRequest request));
        Assert.False(AugustEmotes.TryGetAnimation(request.AnimationId, out _));
    }

    // Independent transcription of the native readers: f7, sub, u64 GUID, u32 definition ID.
    // F2's definition ID is 3, whereas the corresponding EmoteType variable is 12.
    [Fact]
    public void WaveGoodbyeCarriesTheDefinitionIdAndFullCharacterGuid()
    {
        byte[] payload = EmotePackets.Start(0x1122334455667788, 3);

        Assert.Equal("F703887766554433221103000000", Convert.ToHexString(payload));
        Assert.Equal(14, payload.Length);
    }

    [Fact]
    public void StopUsesTheNativeStopSubtypeWithTheSameDefinition()
    {
        Assert.Equal("F704887766554433221103000000",
            Convert.ToHexString(EmotePackets.Stop(0x1122334455667788, 3)));
    }

    [Theory]
    [InlineData(1u, 3276u, 2u, "WaveHello")]
    [InlineData(2u, 3287u, 3u, "WaveBye")]
    [InlineData(6u, 3280u, 18u, "TeaBag")]
    [InlineData(11u, 3877u, 4u, "DoubleBird")]
    [InlineData(12u, 3878u, 14u, "No")]
    public void FunctionKeysUseTheAugustDefaultItems(
        uint slotId, uint itemId, uint animationId, string name)
    {
        Assert.True(AugustEmotes.TryGetSlot(slotId, out AugustEmote emote));
        Assert.Equal(new AugustEmote(slotId, itemId, animationId, name), emote);
        Assert.True(AugustEmotes.TryGetAnimation(animationId, out AugustEmote byAnimation));
        Assert.Equal(emote, byAnimation);
    }

    [Fact]
    public void DefaultsCoverTwelveDistinctSlotsItemsAndAnimations()
    {
        Assert.Equal(Enumerable.Range(1, 12).Select(id => (uint)id),
            AugustEmotes.DefaultSlots.Select(emote => emote.SlotId));
        Assert.Equal(12, AugustEmotes.DefaultSlots.Select(emote => emote.ItemDefinitionId).Distinct().Count());
        Assert.Equal(12, AugustEmotes.DefaultSlots.Select(emote => emote.AnimationId).Distinct().Count());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(13u)]
    [InlineData(uint.MaxValue)]
    public void MissingSlotsAreRejected(uint slotId) =>
        Assert.False(AugustEmotes.TryGetSlot(slotId, out _));

    [Theory]
    [InlineData(0u)]
    [InlineData(16u)] // HandsUp is not a default F-key slot.
    [InlineData(26u)] // SarcasmDance has an account requirement.
    [InlineData(uint.MaxValue)]
    public void AnimationsOutsideTheDefaultSetAreRejected(uint animationId) =>
        Assert.False(AugustEmotes.TryGetAnimation(animationId, out _));
}
