using Cranberry.Protocol;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

// Items.RequestUseItem (0xac / 0x2c) - docs/63 §1.
//
// Every byte string in this file is a REAL PACKET THE AUGUST CLIENT SENT at Cranberry, copied out of
// this project's own captures. That makes these the only inventory tests in the suite whose input is
// client-originated (D29): they do not pin what the server believes, they pin what the client said.
public sealed class ItemUseRequestTests
{
    // captures\wire-20260830-090725.txt 09:11:15.205 (host-20260830-090725.log logged it as
    // "zone Items sub=0x2c 47 bytes: AC2C... (unanswered)"). The leading 06 of the capture line is
    // the gateway tunnel byte and is not part of the zone payload.
    private const string DropSimple =
        "AC2C0100000000000000" + "04000000"
        + "0310000000000000" + "0310000000000000" + "0310000000000000"
        + "0600000000000031" + "01";

    // captures\wire-20260830-090725.txt 09:11:15.979 - the same session, the 7.62mm stack.
    private const string DropQuantity =
        "AC2C0100000000000000" + "04000000"
        + "0310000000000000" + "0310000000000000" + "0310000000000000"
        + "0900000000000031" + "00"
        + "01000000" + "01000000" + "78000000"
        + "00000000000000000000000000000000";

    // captures\wire-20260829-201025.txt 20:14:34.882 - ITEM_USE_OPTION_ID 99, ConsumeItem.
    private const string Consume =
        "AC2C0100000000000000" + "63000000"
        + "0310000000000000" + "0310000000000000" + "0310000000000000"
        + "0700000000000031" + "01";

    // captures\wire-20260829-220829.txt 22:33:57.668 - ITEM_USE_OPTION_ID 63, SalvageItem.
    private const string Salvage =
        "AC2C0100000000000000" + "3F000000"
        + "0310000000000000" + "0310000000000000" + "0310000000000000"
        + "0300000000000031" + "01";

    private const ulong PlayerGuid = 4099;

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void TheCapturedSimpleFormIsFortySevenBytesAndParsesToDropItem()
    {
        byte[] payload = Bytes(DropSimple);
        Assert.Equal(RequestUseItem.MinimumLength, payload.Length);
        Assert.True(RequestUseItem.Matches(payload));

        RequestUseItem request = RequestUseItem.Parse(payload);

        Assert.Equal(1ul, request.UnknownA);
        Assert.Equal(4u, request.ItemUseOptionId);
        Assert.Equal(ItemUseOptionKind.DropItem, request.Kind);
        Assert.Equal(PlayerGuid, request.CharacterGuid);
        Assert.Equal(PlayerGuid, request.SourceCharacterGuid);
        Assert.Equal(PlayerGuid, request.TargetCharacterGuid);
        // 0x3100_0000_0000_0006 - LootWorld.DefaultItemGuidBase + 5, an instance the server granted.
        Assert.Equal(0x3100_0000_0000_0006ul, request.ItemGuid);
        Assert.True(request.Simple);
        Assert.Equal(0u, request.Count);
        Assert.Equal(0, request.TrailingBytes);
    }

    [Fact]
    public void TheCapturedQuantityFormCarriesTheStackTheHostLogSaysWasThere()
    {
        byte[] payload = Bytes(DropQuantity);
        Assert.Equal(RequestUseItem.QuantityFormLength, payload.Length);

        RequestUseItem request = RequestUseItem.Parse(payload);

        Assert.Equal(ItemUseOptionKind.DropItem, request.Kind);
        Assert.False(request.Simple);
        // Instance 0x3100000000000009 is item 1429 (7.62mm), granted at 09:10:05.965 and stacked
        // three more times; the host log's bulk column says it held 4 x 30 = 120 rounds when this
        // packet was written. The client put 120 in the quantity block.
        Assert.Equal(0x3100_0000_0000_0009ul, request.ItemGuid);
        Assert.Equal(120u, request.Count);
        Assert.Equal(0, request.TrailingBytes);
    }

    [Fact]
    public void EveryItemUseOptionIdSeenOnTheWireIsARealSheetRow()
    {
        // The ten distinct values across all four captures. That a u32 at one fixed offset lands on
        // a live ItemUseOptions row ten times out of ten is the whole argument for the field map.
        foreach ((uint id, ItemUseOptionKind kind) in new (uint, ItemUseOptionKind)[]
        {
            (2u, ItemUseOptionKind.ConsumeItem),
            (3u, ItemUseOptionKind.ConsumeItem),
            (4u, ItemUseOptionKind.DropItem),
            (5u, ItemUseOptionKind.PlaceItem),
            (6u, ItemUseOptionKind.SalvageItem),
            (7u, ItemUseOptionKind.UnloadWeapon),
            (9u, ItemUseOptionKind.ConsumeItem),
            (12u, ItemUseOptionKind.RemoveItem),
            (63u, ItemUseOptionKind.SalvageItem),
            (99u, ItemUseOptionKind.ConsumeItem),
        })
        {
            Assert.True(ItemUseOptionTable.TryGet(id, out ItemUseOptionDefinition option), $"option {id}");
            Assert.Equal(kind, option.Kind);
        }
    }

    [Fact]
    public void TheOptionTableIsTheSheetAndTheItemJoinIsComplete()
    {
        Assert.Equal(74, ItemUseOptionTable.All.Count);

        // ItemIdUseOptionGroupId -> ItemUseOptionGroups, for the items in the acceptance check.
        Assert.Contains(4u, ItemUseOptionTable.OptionsForItem(2124));    // military backpack, group 4
        Assert.Contains(63u, ItemUseOptionTable.OptionsForItem(2168));   // helmet, group 56
        Assert.Contains(4u, ItemUseOptionTable.OptionsForItem(1429));    // ammunition, group 62
        Assert.True(ItemUseOptionTable.Allows(2168, 4));
        // Group 62 (ammunition) offers 4, 12, 59, 61, 87 - and not SalvageItem 63.
        Assert.False(ItemUseOptionTable.Allows(1429, 63));
    }

    [Fact]
    public void AShortPayloadIsAFormatErrorRatherThanAWildRead()
    {
        byte[] truncated = Bytes(DropSimple)[..40];
        Assert.False(RequestUseItem.Matches(truncated));
        Assert.Throws<PacketFormatException>(() => RequestUseItem.Parse(truncated));
    }

    [Fact]
    public void TheConsumeAndSalvageCapturesParseToTheirOwnActions()
    {
        RequestUseItem consume = RequestUseItem.Parse(Bytes(Consume));
        Assert.Equal(ItemUseOptionKind.ConsumeItem, consume.Kind);
        Assert.Equal(0x3100_0000_0000_0007ul, consume.ItemGuid);

        RequestUseItem salvage = RequestUseItem.Parse(Bytes(Salvage));
        Assert.Equal(ItemUseOptionKind.SalvageItem, salvage.Kind);
        Assert.Equal(0x3100_0000_0000_0003ul, salvage.ItemGuid);
    }
}
