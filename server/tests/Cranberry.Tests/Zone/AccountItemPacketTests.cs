using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class AccountItemPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void EmptyAccountManagerHasThreeEmptyCollections() =>
        Assert.Equal(
            Convert.FromHexString("AC11000000000000000000000000"),
            Bytes(writer => new SetAccountItemManager().WriteTo(writer)));

    [Fact]
    public void TestingCatalogueAccountRowsArePresentWithOwnedCount()
    {
        byte[] packet = Bytes(writer =>
            new SetAccountItemManager(IncludeCatalogRecords: true).WriteTo(writer));
        var reader = new PacketReader(packet);

        Assert.Equal(SetAccountItemManager.Opcode, reader.ReadByte());
        Assert.Equal(SetAccountItemManager.SubOpcode, reader.ReadByte());
        Assert.Equal(698, reader.ReadInt32());
        Assert.Equal(698, SetAccountItemManager.CatalogAccountItemIds.Count);

        foreach (uint accountItemId in SetAccountItemManager.CatalogAccountItemIds)
        {
            ulong expectedInstance = SetSkinItemManager.CatalogInstanceBase + accountItemId;
            Assert.Equal(expectedInstance, reader.ReadUInt64());
            Assert.Equal(expectedInstance, reader.ReadUInt64());
            Assert.Equal(accountItemId, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
        }

        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.True(reader.AtEnd);
        Assert.Equal(SetAccountItemManager.FullCatalogLength, packet.Length);
    }

    [Fact]
    public void AccountItemRecordMatchesTheAugustParserShape() =>
        Assert.Equal(
            Convert.FromHexString("AC12" + "3007000000425243" + "40070000" + "02000000" + "03000000" + "01"),
            Bytes(writer => new AddAccountItem(
                ItemInstanceId: 0x4352_4200_0000_0730,
                ItemId: 1856,
                ItemType: 2,
                StackCount: 3,
                IsNew: true).WriteTo(writer)));

    [Fact]
    public void ReadyStateSetsAllFourAccountFlags() =>
        Assert.Equal(
            Convert.FromHexString("AC1901000000010000000100000001000000"),
            Bytes(writer => new AccountItemManagerStateChanged().WriteTo(writer)));

    [Fact]
    public void EmptySkinManagerHasIdsNameAndThreeEmptyCollections()
    {
        byte[] packet = Bytes(writer => new SetSkinItemManager().WriteTo(writer));

        Assert.Equal(
            Convert.FromHexString("AC23" + ("00000000" + "00000000" + "00000000" + "00000000" + "00000000" + "00000000")),
            packet);
        Assert.Equal(SetSkinItemManager.EmptyLength, packet.Length);
    }

    [Fact]
    public void OwnershipDoesNotSelectEverySkinInTheOutfitCollections()
    {
        byte[] packet = Bytes(writer =>
            new SetSkinItemManager(IncludeCatalog: true).WriteTo(writer));
        var reader = new PacketReader(packet);

        Assert.Equal(SetSkinItemManager.Opcode, reader.ReadByte());
        Assert.Equal(SetSkinItemManager.SubOpcode, reader.ReadByte());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(0, reader.ReadInt32()); // worn items
        Assert.Equal(0, reader.ReadInt32()); // emotes
        Assert.Equal(2, reader.ReadInt32());

        ReadCollection(ref reader, SetSkinItemManager.ApparelCollectionId, []);
        ReadCollection(ref reader, SetSkinItemManager.WeaponCollectionId, []);

        Assert.True(reader.AtEnd);
        Assert.Equal(SetSkinItemManager.FullCatalogLength, packet.Length);
        Assert.Equal(609, AugustSkinCatalog.Apparel.Count);
        Assert.Equal(69, AugustSkinCatalog.Weapons.Count);
        Assert.Equal(
            AugustSkinCatalog.Apparel.Count,
            AugustSkinCatalog.Apparel.Select(entry => entry.RewardItemId).Distinct().Count());
        Assert.Contains(AugustSkinCatalog.Apparel,
            entry => entry is { CategoryPrototypeId: 2158, RewardItemId: 2158, AccountItemId: 1856 });
        Assert.Contains(AugustSkinCatalog.Weapons,
            entry => entry is { CategoryPrototypeId: 2229, RewardItemId: 2229, AccountItemId: 3698 });
    }

    [Fact]
    public void SelectedSkinRowMatchesTheAugustClickReplyShape() =>
        Assert.Equal(
            Convert.FromHexString(
                "AC24" + "01000000" + "6E080000" + "0110000000000000" + "B5090000" + "01"),
            Bytes(writer => new SetSkinItem(
                CharacterId: 0x1001,
                CategoryPrototypeId: 2158,
                AccountItemId: 2485).WriteTo(writer)));

    [Fact]
    public void ClientSkinSelectionRequestParsesItsFiveDwordBody()
    {
        SkinItemSelectionRequest request = SkinItemSelectionRequest.Parse(Convert.FromHexString(
            "AC32" + "01000000" + "00000000" + "01000000" + "6E080000" + "B5090000"));

        Assert.Equal(SkinItemSelectionRequest.RequestSetSkinItemByItemId, request.SubOpcode);
        Assert.Equal(1u, request.Field1);
        Assert.Equal(0u, request.Field2);
        Assert.Equal(1u, request.SlotType);
        Assert.Equal(2158u, request.CategoryPrototypeId);
        Assert.Equal(2485u, request.ClickedId);
        Assert.Throws<PacketFormatException>(() => SkinItemSelectionRequest.Parse(Convert.FromHexString(
            "AC32" + "01000000" + "00000000" + "01000000" + "6E080000" + "B5090000" + "00")));
    }

    [Fact]
    public void SkinManagerWornRowNamesTheSelectedRewardWithinItsCategory()
    {
        AugustSkinCatalogEntry selected = AugustSkinCatalog.Apparel.Single(entry =>
            entry.CategoryPrototypeId == 2158 && entry.AccountItemId == 2485);
        byte[] packet = Bytes(writer => new SetSkinItemManager(
            IncludeCatalog: false,
            WornItems: [selected]).WriteTo(writer));
        var reader = new PacketReader(packet);

        Assert.Equal(SetSkinItemManager.Opcode, reader.ReadByte());
        Assert.Equal(SetSkinItemManager.SubOpcode, reader.ReadByte());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadString();
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(selected.CategoryPrototypeId, reader.ReadUInt32());
        Assert.Equal(selected.RewardItemId, reader.ReadUInt32());
        Assert.Equal(SetSkinItemManager.CatalogInstanceBase + selected.AccountItemId, reader.ReadUInt64());
        Assert.Equal(selected.AccountItemId, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void CurrentCollectionContainsOnlyTheExplicitSelection()
    {
        AugustSkinCatalogEntry[] choices = [AugustSkinCatalog.Apparel[0]];
        var collection = new SetCurrentSkinItemCollection(SelectedItems: choices);
        byte[] packet = Bytes(collection.WriteTo);
        var reader = new PacketReader(packet);

        Assert.Equal(SetCurrentSkinItemCollection.Opcode, reader.ReadByte());
        Assert.Equal(SetCurrentSkinItemCollection.SubOpcode, reader.ReadByte());
        Assert.Equal(SetSkinItemManager.ApparelCollectionId, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadString());

        int count = reader.ReadInt32();
        Assert.Equal(1, count);
        foreach (AugustSkinCatalogEntry expected in choices)
        {
            Assert.Equal(expected.CategoryPrototypeId, reader.ReadUInt32());
            Assert.Equal(expected.CategoryPrototypeId, reader.ReadUInt32());
            Assert.Equal(SetSkinItemManager.CatalogInstanceBase + expected.AccountItemId, reader.ReadUInt64());
            Assert.Equal(expected.AccountItemId, reader.ReadUInt32());
            Assert.Equal(SetSkinItemManager.PreviewOnlyOverride, reader.ReadByte());
        }

        Assert.Equal(0, reader.ReadInt32());
        Assert.True(reader.AtEnd);
        Assert.Equal(collection.WireLength, packet.Length);
    }

    private static void ReadCollection(
        ref PacketReader reader,
        uint expectedCollectionId,
        IReadOnlyList<AugustSkinCatalogEntry> expected)
    {
        Assert.Equal(expectedCollectionId, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(expected.Count, reader.ReadInt32());
        foreach (AugustSkinCatalogEntry expectedEntry in expected)
        {
            uint outerKey = reader.ReadUInt32();
            uint category = reader.ReadUInt32();
            ulong instance = reader.ReadUInt64();
            uint accountItem = reader.ReadUInt32();
            byte flags = reader.ReadByte();

            Assert.Equal(expectedEntry.CategoryPrototypeId, outerKey);
            Assert.Equal(expectedEntry.CategoryPrototypeId, category);
            Assert.NotEqual(0ul, instance);
            Assert.Equal(expectedEntry.AccountItemId, accountItem);
            Assert.Equal(SetSkinItemManager.PreviewOnlyOverride, flags);
        }

        Assert.Equal(0, reader.ReadInt32()); // collection emotes
    }
}
