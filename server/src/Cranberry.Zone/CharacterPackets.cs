using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// <c>Character.RemovePlayer</c> (base 0x0f, u8 sub 1; parser <c>FUN_140a5fd50</c>): a world
/// object guid followed by a 16-bit effect flag. Zero removes an entity without the death/ragdoll
/// presentation. Total length: 12 bytes.
/// </summary>
public sealed record RemovePlayer(ulong CharacterId, ushort EffectFlag = 0)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 0x01;
    public const int Length = 12;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterId);
        writer.WriteUInt16(EffectFlag);
    }
}

/// <summary>
/// <c>Equipment.SetCharacterEquipment</c> (base 0x94 <c>cPacketIdEquipmentBase</c>, u8 sub 1;
/// head <c>FUN_140a2cda0</c>, body <c>FUN_140cd4d40</c>): <c>u32 profileId; u64 characterId; u32;
/// str tintAlias; str decalAlias; i32 slots {u32 slotId; u32 slotId; u64 itemGuid; str; str};
/// i32 attachments {str model; str texture; str tint; str decal; u32×5; i32 n + u32[n]; u8}; u8</c>.
/// The actor creator hides the local player (<c>FUN_140acccd0</c>: hide bit 2) and the per-frame
/// update <c>FUN_1411af6c0</c> clears that bit only once the equipment manager flag
/// (self+0xdd18) is set — which happens in <c>FUN_140cd85e0</c> when this packet's characterId
/// equals the local guid, whatever the contents. Attachments are the outfit meshes
/// (<c>ProcessNewAttachment</c>); an empty dress only un-hides the base body.
/// </summary>
public sealed record SetCharacterEquipment(
    ulong CharacterId,
    uint ProfileId = 5,
    IReadOnlyList<CharacterEquipmentAttachment>? Attachments = null)
{
    public const byte Opcode = ZoneOpcodes.EquipmentBase;
    public const byte SubOpcode = 0x01;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(ProfileId);
        writer.WriteUInt64(CharacterId);
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);   // tint alias
        writer.WriteString(string.Empty);   // decal alias
        writer.WriteInt32(0);               // equipment-slot rows (inventory identity; not meshes)

        IReadOnlyList<CharacterEquipmentAttachment> attachments = Attachments ?? [];
        writer.WriteInt32(attachments.Count);
        foreach (CharacterEquipmentAttachment attachment in attachments)
        {
            attachment.WriteTo(writer);
        }

        writer.WriteBool(true);             // trailing flag the 2016 server sent as 1
    }
}

/// <summary>
/// <c>Equipment.UnsetCharacterEquipmentSlot</c> (base 0x94, u8 sub 3): removes one attachment
/// from a character. The August client reads the same 22-byte equipment-character prefix used
/// by the neighbouring single-slot update: <c>u32 profileId; u64 characterId; u32 unknown;
/// u32 slotId</c>. A full equipment packet updates the rows it names but does not reliably evict
/// a skin-manager attachment in an omitted slot, so wardrobe resyncs explicitly clear those
/// absent slots.
/// </summary>
public sealed record UnsetCharacterEquipmentSlot(
    ulong CharacterId,
    uint SlotId,
    uint ProfileId = 3,
    uint Unknown = 0)
{
    public const byte Opcode = ZoneOpcodes.EquipmentBase;
    public const byte SubOpcode = 0x03;
    public const int Length = 2 + sizeof(uint) + sizeof(ulong) + (2 * sizeof(uint));

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(ProfileId);
        writer.WriteUInt64(CharacterId);
        writer.WriteUInt32(Unknown);
        writer.WriteUInt32(SlotId);
    }
}

/// <summary>
/// One mesh in <see cref="SetCharacterEquipment"/>. The five dwords and the trailing flag are
/// retained even when zero because <c>FUN_140cd4d40</c> consumes the complete attachment schema.
/// <see cref="AppearanceIds"/> are rows from DynamicAppearanceDefinitions; an empty set is the
/// safe form when the attachment selects a table-3 shader group directly.
/// </summary>
public sealed record CharacterEquipmentAttachment(
    string ModelName,
    uint SlotId,
    string TextureAlias = "Default",
    string TintAlias = "Default",
    string DecalAlias = "#",
    uint TintId = 0,
    uint CompositeEffectId = 0,
    uint EffectId = 0,
    uint ShaderParameterGroupId = 0,
    IReadOnlyList<uint>? AppearanceIds = null,
    bool Flag = false)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteString(ModelName);
        writer.WriteString(TextureAlias);
        writer.WriteString(TintAlias);
        writer.WriteString(DecalAlias);
        writer.WriteUInt32(TintId);
        writer.WriteUInt32(CompositeEffectId);
        writer.WriteUInt32(EffectId);
        writer.WriteUInt32(SlotId);
        writer.WriteUInt32(ShaderParameterGroupId);

        IReadOnlyList<uint> appearanceIds = AppearanceIds ?? [];
        writer.WriteInt32(appearanceIds.Count);
        foreach (uint appearanceId in appearanceIds)
        {
            writer.WriteUInt32(appearanceId);
        }

        writer.WriteBool(Flag);
    }
}

/// <summary>
/// <c>0f 4a cCharacterPacketIdDroppedIemNotification</c> — <b>18 bytes</b>, the "you dropped X"
/// toast the friend's server sends <b>last</b> in its drop chain (the client's own registration
/// carries the typo in <c>Iem</c>).
/// <para>
/// <b>Registered in 1148</b>: <c>out/registrations-1148.md:1151</c>, sub <c>0x4a</c> under character
/// base <c>0x0f</c>, handler <c>FUN_1413c1ba0</c> — which is the registrar itself, so the binary
/// names the packet but does not disclose its body. The <b>body is derived from the capture</b>: two
/// instances, <c>packets_1119_53544.log:5288</c> and <c>:5553</c>, both
/// <c>0f 4a | u64 4c0d4b23321b488f | u32 itemDefinitionId | u32 count</c> — the guid is the
/// character's, the definition ids resolve to real August items (2109 and 3529) and both counts are
/// 1. The capture therefore cannot say what a partial stack sends, so Cranberry sends the count it
/// actually removed.
/// </para>
/// </summary>
/// <param name="CharacterGuid">The player who dropped it.</param>
/// <param name="ItemDefinitionId">The <c>ClientItemDefinitions</c> row that hit the floor.</param>
/// <param name="Count">How many units left the bag.</param>
public sealed record DroppedItemNotification(
    ulong CharacterGuid,
    uint ItemDefinitionId,
    uint Count)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 0x4a;
    public const int Length = 18;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32(ItemDefinitionId);
        writer.WriteUInt32(Count);
    }
}

/// <summary>
/// <c>0f 43 Character.PlayWorldCompositeEffect</c>, s2c - <b>a composite effect at a position</b>
/// (docs/120 §4.2). Registered at 1148 as <c>cCharacterPacketIdPlayWorldCompositeEffect</c>
/// (<c>FUN_1413c1ba0</c>). The zone dispatcher <c>FUN_140af9ca0</c> case <c>0x43</c> reads it with
/// <c>FUN_140a2f860</c>: the family envelope (<c>u8 0x0f; u8 0x43; u64 guid</c>, base reader
/// <c>FUN_140a2cc00</c>), then <c>u32</c> into <c>rec+0x20</c>, four <c>f32</c> into
/// <c>rec+0x30..+0x3c</c> and one <c>u32</c> into <c>rec+0x40</c>; it then copies <c>rec+0x20</c>
/// into the effect-queue request's id and <c>rec+0x30</c> into its position and calls
/// <c>FUN_1423b2f30(DAT_143f69470, ...)</c> - the same <c>QueueCompositeEffectAtLocation</c> the
/// door swing sound goes through client-locally (docs/42 §6c). <b>34 bytes.</b>
/// </summary>
/// <param name="CharacterGuid">The envelope's guid. The effect is positioned by the vector, so this is the entity the request is attributed to - the thrower.</param>
/// <param name="EffectId">A row of <c>ActorCompositeEffectDefinitions.xml</c> (<c>AugustEffectCatalog</c>); never 0 (D100).</param>
/// <param name="Position">Where it plays; the fourth float ships 1.0 (the record's own preset vector is a point).</param>
/// <param name="Trailer"><c>rec+0x40</c>, meaning [U]; 0.</param>
public sealed record PlayWorldCompositeEffect(
    ulong CharacterGuid,
    uint EffectId,
    System.Numerics.Vector3 Position,
    uint Trailer = 0)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;

    public const byte SubOpcode = 0x43;

    public const int Length = 34;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32(EffectId);
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteSingle(1.0f);
        writer.WriteUInt32(Trailer);
    }

    public byte[] ToArray()
    {
        using var writer = new PacketWriter(Length);
        WriteTo(writer);
        return writer.Written.ToArray();
    }
}
