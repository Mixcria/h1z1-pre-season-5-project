using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

/// <summary>
/// August Groups 13/24: FUN_140d65ca0 and FUN_140075af4 read the GUID,
/// u16 presence mask, then color slot, state flags and health percentage.
/// FUN_1414fd190 writes these into the SQL GroupMembers table used by the HUD.
/// Slots 1..5 select the client's authored ContourColors 17..21.
/// </summary>
public sealed record GroupMemberHudStatus(ulong CharacterGuid, byte ColorSlot, bool IsDead,
    bool IsDriving, byte HealthPercent, uint? EquippedItemDefinitionId = null,
    bool? HasArmor = null, bool? HasHelmet = null)
{
    public const byte SubOpcode = 0x24;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ColorSlot, (byte)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ColorSlot, (byte)5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HealthPercent, (byte)100);
        writer.WriteByte(ZoneOpcodes.GroupsBase);
        writer.WriteByte(SubOpcode);
        writer.WriteByte(2);
        writer.WriteUInt64(CharacterGuid);
        // FUN_140075af4 reads bit 3 as an item-definition ID. FUN_1414fd190 resolves
        // that definition's icon locally; sending the icon ID itself looks up the wrong item.
        ushort fields = 0x0007;
        if (EquippedItemDefinitionId.HasValue) fields |= 0x0008;
        if (HasArmor.HasValue) fields |= 0x0010;
        if (HasHelmet.HasValue) fields |= 0x0020;
        writer.WriteUInt16(fields);
        writer.WriteByte(ColorSlot);
        writer.WriteByte((byte)((IsDead ? 1 : 0) | (IsDriving ? 2 : 0)));
        writer.WriteByte(HealthPercent);
        if (EquippedItemDefinitionId is uint item) writer.WriteUInt32(item);
        if (HasArmor is bool armor) writer.WriteByte(armor ? (byte)1 : (byte)0);
        if (HasHelmet is bool helmet) writer.WriteByte(helmet ? (byte)1 : (byte)0);
    }
}
