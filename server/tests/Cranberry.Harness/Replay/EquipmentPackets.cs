using Cranberry.Harness.Wire;
using Cranberry.Zone;

namespace Cranberry.Harness.Replay;

/// <summary>One row of the equipment-slot list inside <c>SetCharacterEquipment</c>.</summary>
public sealed record EquipmentSlotRow(uint Key, uint SlotId, ulong ItemGuid, string Tint, string Decal);

/// <summary>One attachment (a mesh) inside <c>SetCharacterEquipment</c>.</summary>
public sealed record EquipmentAttachment(string Model, uint SlotId, IReadOnlyList<uint> AppearanceIds);

/// <summary>
/// A decoded <c>Equipment.SetCharacterEquipment</c> (base 0x94, u8 sub 1).
///
/// <b>The distinction this type exists to make.</b> The packet carries two independent lists and
/// docs/45 is about only one of them. The <see cref="Attachments"/> are meshes, and a slot-7
/// attachment (<c>Weapon_Empty.adr</c>) is present in <i>every</i> capture the client accepted,
/// including both known-good reference sessions. The <see cref="SlotRows"/> are the inventory
/// identity rows added in wave 3, and it is a row naming body slot 7 that null-derefs the client's
/// per-frame weapon update (docs/45 §1, §2a — nine packets, one discriminator, a minidump).
/// A guard that conflated the two would fire on every healthy session and be switched off.
/// </summary>
public sealed record SetCharacterEquipmentPacket(
    uint ProfileId,
    ulong CharacterId,
    string TintAlias,
    string DecalAlias,
    IReadOnlyList<EquipmentSlotRow> SlotRows,
    IReadOnlyList<EquipmentAttachment> Attachments,
    int Length,
    int BytesConsumed)
{
    /// <summary>True when the whole packet was consumed — a layout check, not just a parse.</summary>
    public bool FullyConsumed => BytesConsumed == Length;

    public IEnumerable<uint> SlotRowIds => SlotRows.Select(r => r.SlotId);

    public IEnumerable<uint> AttachmentSlotIds => Attachments.Select(a => a.SlotId);
}

/// <summary>
/// Decodes the two equipment packets the guards need. The layouts come from
/// <c>Cranberry.Zone.CharacterPackets</c>, which cites the client's own parsers
/// (<c>FUN_140cd4d40</c> for the body); a mis-parse is caught by
/// <see cref="SetCharacterEquipmentPacket.FullyConsumed"/> rather than passed off as a finding.
/// </summary>
public static class EquipmentPackets
{
    public const byte SetSubOpcode = 0x01;

    /// <summary>Sub 2 — <c>SetCharacterEquipmentSlot</c>, the one-slot binding of docs/95 §1.</summary>
    public const byte SetSlotSubOpcode = 0x02;

    public const byte UnsetSubOpcode = 0x03;

    /// <summary>True for a tunnelled <c>94 01</c>, whichever direction.</summary>
    public static bool IsSetCharacterEquipment(PacketSignature signature) =>
        signature.Kind == SignatureKind.Zone
        && signature.Opcode == ZoneOpcodes.EquipmentBase
        && signature.SubOpcode == SetSubOpcode;

    /// <summary>
    /// True for a tunnelled <c>94 02</c> — <c>SetCharacterEquipmentSlot</c>, the single-slot
    /// binding docs/95 §1 derives from <c>FUN_140cd8be0</c> / <c>FUN_140cd4f90</c>. It is the
    /// packet that closes a draw burst, and G1 uses it to tell a draw from a dress.
    /// </summary>
    public static bool IsSetCharacterEquipmentSlot(PacketSignature signature) =>
        signature.Kind == SignatureKind.Zone
        && signature.Opcode == ZoneOpcodes.EquipmentBase
        && signature.SubOpcode == SetSlotSubOpcode;

    /// <summary>True for a tunnelled <c>94 03</c> — the burst docs/32 blames for the Z2 hang.</summary>
    public static bool IsUnsetCharacterEquipmentSlot(PacketSignature signature) =>
        signature.Kind == SignatureKind.Zone
        && signature.Opcode == ZoneOpcodes.EquipmentBase
        && signature.SubOpcode == UnsetSubOpcode;

    /// <summary>True for <c>ac 24</c> — <c>Items.SetSkinItem</c>, one worn wardrobe selection.</summary>
    public static bool IsSetSkinItem(PacketSignature signature) =>
        signature.Kind == SignatureKind.Zone
        && signature.Opcode == ZoneOpcodes.ItemsBase
        && signature.SubOpcode == 0x24;

    /// <summary>
    /// Decodes a gateway message whose signature is <see cref="IsSetCharacterEquipment"/>. Returns
    /// null when the bytes are truncated or do not match the layout, so a capture whose message was
    /// too long to materialise is reported as unparsed rather than silently passing every guard.
    /// </summary>
    public static SetCharacterEquipmentPacket? TryParseSetCharacterEquipment(ReadOnlySpan<byte> gatewayMessage)
    {
        try
        {
            return ParseSetCharacterEquipment(gatewayMessage);
        }
        catch (Exception exception) when (exception is WireFormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    private static SetCharacterEquipmentPacket ParseSetCharacterEquipment(ReadOnlySpan<byte> gatewayMessage)
    {
        var reader = new WireReader(gatewayMessage);
        reader.U8();                       // gateway header
        if (reader.U8() != ZoneOpcodes.EquipmentBase || reader.U8() != SetSubOpcode)
        {
            throw new WireFormatException("not a SetCharacterEquipment packet");
        }

        uint profileId = reader.LeU32();
        ulong characterId = reader.LeU64();
        reader.LeU32();                    // unknown, always zero in every capture
        string tint = reader.CountedString();
        string decal = reader.CountedString();

        int rowCount = (int)reader.LeU32();
        if (rowCount is < 0 or > 4096)
        {
            throw new WireFormatException($"implausible equipment-slot row count {rowCount}");
        }

        var rows = new List<EquipmentSlotRow>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            rows.Add(new EquipmentSlotRow(
                reader.LeU32(), reader.LeU32(), reader.LeU64(), reader.CountedString(), reader.CountedString()));
        }

        int attachmentCount = (int)reader.LeU32();
        if (attachmentCount is < 0 or > 4096)
        {
            throw new WireFormatException($"implausible attachment count {attachmentCount}");
        }

        var attachments = new List<EquipmentAttachment>(attachmentCount);
        for (int i = 0; i < attachmentCount; i++)
        {
            string model = reader.CountedString();
            reader.CountedString();        // texture alias
            reader.CountedString();        // tint alias
            reader.CountedString();        // decal alias
            reader.LeU32();                // tint id
            reader.LeU32();                // composite effect id
            reader.LeU32();                // effect id
            uint slotId = reader.LeU32();
            reader.LeU32();                // shader parameter group id

            int appearanceCount = (int)reader.LeU32();
            if (appearanceCount is < 0 or > 4096)
            {
                throw new WireFormatException($"implausible appearance-id count {appearanceCount}");
            }

            var appearanceIds = new List<uint>(appearanceCount);
            for (int j = 0; j < appearanceCount; j++)
            {
                appearanceIds.Add(reader.LeU32());
            }

            reader.U8();                   // per-attachment flag
            attachments.Add(new EquipmentAttachment(model, slotId, appearanceIds));
        }

        reader.U8();                       // trailing flag

        return new SetCharacterEquipmentPacket(
            profileId, characterId, tint, decal, rows, attachments, gatewayMessage.Length, reader.Position);
    }
}
