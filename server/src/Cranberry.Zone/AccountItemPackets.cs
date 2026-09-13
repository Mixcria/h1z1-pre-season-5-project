using Cranberry.Protocol;
using Cranberry.Zone.Emotes;
using Cranberry.Zone.Economy;

namespace Cranberry.Zone;

/// <summary>
/// <c>Items.SetAccountItemManager</c> (base 0xac, u8 sub 0x11; handler
/// <c>FUN_140d0f6b0</c>, body parser <c>FUN_140a2b910</c>). The body contains three
/// independent collections. The host publishes the saved account's actual instances and counts.
/// The optional synthetic catalogue remains available only to legacy in-memory fixtures.
/// </summary>
public sealed record SetAccountItemManager(bool IncludeCatalogRecords = false,
    IReadOnlyList<OwnedAccountItem>? OwnedItems = null)
{
    public const byte Opcode = ZoneOpcodes.ItemsBase;
    public const byte SubOpcode = 0x11;
    public const int EmptyLength = 2 + (3 * sizeof(int));

    private static readonly uint[] CatalogItemIds =
    [
        .. AugustSkinCatalog.Apparel
            .Concat(AugustSkinCatalog.Weapons)
            .Select(entry => entry.AccountItemId)
            .Concat(Vehicles.VehicleSkinCatalog.All.Select(entry => entry.ItemId))
            .Distinct(),
    ];

    public static IReadOnlyList<uint> CatalogAccountItemIds => CatalogItemIds;
    public static int FullCatalogLength => EmptyLength + (CatalogItemIds.Length * 28);

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);

        if (OwnedItems is not null)
        {
            writer.WriteInt32(OwnedItems.Count);
            foreach (OwnedAccountItem item in OwnedItems)
            {
                writer.WriteUInt64(item.InstanceId);
                writer.WriteUInt64(item.InstanceId);
                writer.WriteUInt32(item.AccountItemId);
                writer.WriteUInt32(item.ItemType);
                writer.WriteUInt32(item.Count);
            }
        }
        else if (!IncludeCatalogRecords)
        {
            writer.WriteInt32(0); // account items
        }
        else
        {
            writer.WriteInt32(CatalogItemIds.Length);
            foreach (uint accountItemId in CatalogItemIds)
            {
                ulong instanceId = SetSkinItemManager.CatalogInstanceBase + accountItemId;
                writer.WriteUInt64(instanceId); // collection key
                writer.WriteUInt64(instanceId); // account-item body id
                writer.WriteUInt32(accountItemId);
                writer.WriteUInt32(0); // item type / unknown dword
                writer.WriteUInt32(1); // one owned copy: every testing skin is selectable
            }
        }

        writer.WriteInt32(0); // new-account-item records
        writer.WriteInt32(0); // account-item conversions
    }
}

/// <summary>
/// <c>Items.AddAccountItem</c> (base 0xac, u8 sub 0x12; handler
/// <c>FUN_140d0e6a0</c>, record parser <c>FUN_140a2b830</c>). Account-item instances are
/// separate from ordinary character inventory and gate owned wardrobe tiles.
/// </summary>
public sealed record AddAccountItem(
    ulong ItemInstanceId,
    uint ItemId,
    uint ItemType = 0,
    uint StackCount = 1,
    bool IsNew = false)
{
    public const byte Opcode = ZoneOpcodes.ItemsBase;
    public const byte SubOpcode = 0x12;
    public const int Length = 2 + sizeof(ulong) + (3 * sizeof(uint)) + sizeof(byte);

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(ItemInstanceId);
        writer.WriteUInt32(ItemId);
        writer.WriteUInt32(ItemType);
        writer.WriteUInt32(StackCount);
        writer.WriteBool(IsNew);
    }
}

/// <summary>
/// <c>Items.AccountItemManagerStateChanged</c> (base 0xac, u8 sub 0x19; handler
/// <c>FUN_140d0e640</c>, parser <c>FUN_140d08fc0</c>). The August UI data source exposes these
/// four fields as EscrowEnabled, EscrowServerConnected, EscrowAccountLoadSucceeded and
/// EscrowAccountAllowTrading. Values are enum dwords; <c>1</c> is the affirmative state.
/// </summary>
public sealed record AccountItemManagerStateChanged(
    uint EscrowEnabled = 1,
    uint EscrowServerConnected = 1,
    uint EscrowAccountLoadSucceeded = 1,
    uint EscrowAccountAllowTrading = 1)
{
    public const byte Opcode = ZoneOpcodes.ItemsBase;
    public const byte SubOpcode = 0x19;
    public const int Length = 2 + (4 * sizeof(uint));

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(EscrowEnabled);
        writer.WriteUInt32(EscrowServerConnected);
        writer.WriteUInt32(EscrowAccountLoadSucceeded);
        writer.WriteUInt32(EscrowAccountAllowTrading);
    }
}

/// <summary>
/// <c>Items.SetSkinItemManager</c> (base 0xac, u8 sub 0x23; handler
/// <c>FUN_140d2edd0</c>, body parser <c>FUN_140d293e0</c>). The two ids and custom name describe
/// manager state, followed by worn items, emotes and the collections used to populate the grids.
/// Catalogue entries carry real August account-item conversion ids; ownership remains a separate
/// concern in <see cref="SetAccountItemManager"/>.
/// </summary>
public sealed record SetSkinItemManager(
    uint SelectedCollectionId = 0,
    uint CurrentCollectionId = 0,
    string CurrentCollectionName = "",
    bool IncludeCatalog = false,
    IReadOnlyList<AugustSkinCatalogEntry>? WornItems = null,
    IReadOnlyList<AugustSkinCatalogEntry>? SelectedItems = null,
    IReadOnlyDictionary<uint, ulong>? OwnedInstanceIds = null,
    IReadOnlyList<AugustEmote>? Emotes = null)
{
    public const byte Opcode = ZoneOpcodes.ItemsBase;
    public const byte SubOpcode = 0x23;
    public const int EmptyLength = 2 + (2 * sizeof(uint)) + (4 * sizeof(int));
    public const uint ApparelCollectionId = 1;
    public const uint WeaponCollectionId = 2;
    public const byte PreviewOnlyOverride = 0x80;

    public const ulong CatalogInstanceBase = 0x4352_4200_0000_0000;

    public static int FullCatalogLength => EmptyLength + (2 * (5 * sizeof(uint)));

    public int Length => (IncludeCatalog ? FullCatalogLength + ((SelectedItems?.Count ?? 0) * 21) : EmptyLength)
        + System.Text.Encoding.UTF8.GetByteCount(CurrentCollectionName) + ((WornItems?.Count ?? 0) * 21)
        + ((Emotes?.Count ?? 0) * EmotePackets.ItemRowLength * (IncludeCatalog ? 3 : 1));

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(SelectedCollectionId);
        writer.WriteUInt32(CurrentCollectionId);
        writer.WriteString(CurrentCollectionName);
        WriteWornSkinItems(writer, WornItems ?? [], OwnedInstanceIds);
        EmotePackets.WriteItems(writer, Emotes ?? []);

        if (!IncludeCatalog)
        {
            writer.WriteInt32(0);
            return;
        }

        writer.WriteInt32(2);
        WriteCollection(writer, ApparelCollectionId, (SelectedItems ?? []).Where(AugustWardrobeCatalog.IsApparel).ToArray(), OwnedInstanceIds, Emotes ?? []);
        WriteCollection(writer, WeaponCollectionId, (SelectedItems ?? []).Where(e => !AugustWardrobeCatalog.IsApparel(e)).ToArray(), OwnedInstanceIds, Emotes ?? []);
    }

    private static void WriteCollection(
        PacketWriter writer,
        uint collectionId,
        IReadOnlyList<AugustSkinCatalogEntry> entries,
        IReadOnlyDictionary<uint, ulong>? ownedInstanceIds,
        IReadOnlyList<AugustEmote> emotes)
    {
        writer.WriteUInt32(collectionId);
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        WriteCatalogSkinItems(writer, entries, ownedInstanceIds);

        EmotePackets.WriteItems(writer, emotes);
    }

    internal static void WriteCatalogSkinItems(
        PacketWriter writer,
        IReadOnlyList<AugustSkinCatalogEntry> entries,
        IReadOnlyDictionary<uint, ulong>? ownedInstanceIds = null)
    {
        writer.WriteInt32(entries.Count);
        foreach (AugustSkinCatalogEntry entry in entries)
        {
            // A collection is an outfit, not the ownership catalogue. Each category occurs once;
            // account ownership supplies the available tiles through SetAccountItemManager.
            writer.WriteUInt32(entry.CategoryPrototypeId);
            writer.WriteUInt32(entry.CategoryPrototypeId);
            writer.WriteUInt64(OwnedInstanceId(entry.AccountItemId, ownedInstanceIds));
            writer.WriteUInt32(entry.AccountItemId);
            writer.WriteByte(PreviewOnlyOverride);
        }
    }

    private static void WriteWornSkinItems(
        PacketWriter writer,
        IReadOnlyList<AugustSkinCatalogEntry> entries,
        IReadOnlyDictionary<uint, ulong>? ownedInstanceIds)
    {
        writer.WriteInt32(entries.Count);
        foreach (AugustSkinCatalogEntry entry in entries)
        {
            // Manager worn rows are keyed by category, but their body names the selected reward.
            // This is intentionally different from a catalogue row, whose body must name the
            // category prototype so the native client reaches the account-item conversion lookup.
            writer.WriteUInt32(entry.CategoryPrototypeId);
            writer.WriteUInt32(entry.RewardItemId);
            writer.WriteUInt64(OwnedInstanceId(entry.AccountItemId, ownedInstanceIds));
            writer.WriteUInt32(entry.AccountItemId);
            writer.WriteByte(AugustWardrobeCatalog.IsApparel(entry) ? (byte)0 : PreviewOnlyOverride);
        }
    }

    private static ulong OwnedInstanceId(uint accountItemId, IReadOnlyDictionary<uint, ulong>? instances) =>
        instances is null ? CatalogInstanceBase + accountItemId : instances[accountItemId];
}

/// <summary>
/// <c>Items.SetCurrentSkinItemCollection</c> (base 0xac, u8 sub 0x28; handler
/// <c>FUN_140d2e680</c>). The August parser reads a collection id, custom name, skin-item list and
/// emote list. Selecting collection 1 after publishing the manager makes its apparel catalogue
/// the active Appearance data source.
/// </summary>
public sealed record SetCurrentSkinItemCollection(uint CollectionId = SetSkinItemManager.ApparelCollectionId,
    IReadOnlyList<AugustSkinCatalogEntry>? SelectedItems = null,
    IReadOnlyDictionary<uint, ulong>? OwnedInstanceIds = null,
    IReadOnlyList<AugustEmote>? Emotes = null)
{
    public const byte Opcode = ZoneOpcodes.ItemsBase;
    public const byte SubOpcode = 0x28;
    public static int Length => 2 + sizeof(uint) + (3 * sizeof(int));
    public int WireLength => Length + ((SelectedItems?.Count ?? 0) * 21)
        + ((Emotes?.Count ?? 0) * EmotePackets.ItemRowLength);

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(CollectionId);
        writer.WriteString(string.Empty);
        SetSkinItemManager.WriteCatalogSkinItems(writer, SelectedItems ?? [], OwnedInstanceIds);
        EmotePackets.WriteItems(writer, Emotes ?? []);
    }
}

/// <summary>
/// <c>Items.SetSkinItem</c> (base 0xac, u8 sub 0x24; handler
/// <c>FUN_140d2ea30</c>). The category prototype and account-item id are deliberately separate:
/// together they say which category changed and which conversion-backed reward is now selected.
/// </summary>
public sealed record SetSkinItem(
    ulong CharacterId,
    uint CategoryPrototypeId,
    uint AccountItemId,
    uint CollectionId = SetSkinItemManager.ApparelCollectionId,
    byte Flags = 1)
{
    public const byte Opcode = ZoneOpcodes.ItemsBase;
    public const byte SubOpcode = 0x24;
    public const int Length = 2 + sizeof(uint) + sizeof(uint) + sizeof(ulong) + sizeof(uint) + sizeof(byte);

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(CollectionId);
        writer.WriteUInt32(CategoryPrototypeId);
        writer.WriteUInt64(CharacterId);
        writer.WriteUInt32(AccountItemId);
        writer.WriteByte(Flags);
    }
}

/// <summary>
/// The five-dword body sent by the August Appearance editor for
/// <c>RequestSetSkinItem</c>/<c>RequestSetSkinItemByItemId</c>. The clicked field normally carries
/// the account-item id that the server announced, while the preceding field is the category
/// prototype.
/// </summary>
public sealed record SkinItemSelectionRequest(
    byte SubOpcode,
    uint Field1,
    uint Field2,
    uint SlotType,
    uint CategoryPrototypeId,
    uint ClickedId)
{
    public const byte RequestSetSkinItem = 0x31;
    public const byte RequestSetSkinItemByItemId = 0x32;
    public const byte RequestUnsetSkinItem = 0x34;
    public const int Length = 2 + (5 * sizeof(uint));

    public static SkinItemSelectionRequest Parse(ReadOnlySpan<byte> packet)
    {
        var reader = new PacketReader(packet);
        byte opcode = reader.ReadByte();
        byte subOpcode = reader.ReadByte();
        if (opcode != ZoneOpcodes.ItemsBase)
        {
            throw new PacketFormatException($"Skin-item request opcode 0x{opcode:x2}, expected 0xac.");
        }

        if (subOpcode is not RequestSetSkinItem
            and not RequestSetSkinItemByItemId
            and not RequestUnsetSkinItem)
        {
            throw new PacketFormatException($"Unsupported skin-item request sub-opcode 0x{subOpcode:x2}.");
        }

        var request = new SkinItemSelectionRequest(
            subOpcode,
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"Skin-item request has {reader.Remaining} trailing byte(s).");
        }

        return request;
    }
}
