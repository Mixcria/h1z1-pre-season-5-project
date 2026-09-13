using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// ResourceEvent type 3 (UpdateCharacterResource), consumed by the August client's
/// <c>FUN_140ce52c0</c>. A complete row can update an existing local resource or create its
/// missing <c>Resources.PlayerResourceDataSource</c> entry.
/// </summary>
public sealed record CharacterResourceUpdate(
    ulong SubjectGuid,
    uint ResourceId,
    uint ResourceType,
    uint Value,
    uint PreviousValue,
    uint GameTime = 0)
{
    public const byte Opcode = ZoneOpcodes.ResourceEventBase;
    public const byte EventType = 3;
    public const int WireLength = 101;

    /// <summary>
    /// The current HUD reads resource 1/1, whose authored maximum is 10,000
    /// (August Resources.txt row 1). ClientUpdate.Hitpoints alone does not update it.
    /// Normalize custom server health scales so both client health paths agree.
    /// </summary>
    public static CharacterResourceUpdate Health(ulong subjectGuid, uint current, uint previous, uint maximum)
    {
        ArgumentOutOfRangeException.ThrowIfZero(maximum);
        static uint OnClientScale(uint value, uint maximum) =>
            (uint)((ulong)Math.Min(value, maximum) * 10_000 / maximum);
        return new(subjectGuid, 1, 1, OnClientScale(current, maximum), OnClientScale(previous, maximum));
    }

    public static CharacterResourceUpdate Initial(ulong subjectGuid, CharacterResource resource) =>
        new(
            subjectGuid,
            resource.ResourceId,
            resource.ResourceType,
            resource.CurrentValue,
            resource.PreviousValue);

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt32(GameTime);
        writer.WriteByte(EventType);
        writer.WriteUInt64(SubjectGuid);
        writer.WriteUInt32(ResourceId);
        writer.WriteUInt32(ResourceType);
        writer.WriteUInt32(Value);
        writer.WriteUInt32(PreviousValue);

        for (int index = 0; index < 4; index++)
        {
            writer.WriteSingle(0f);                 // regen/external regen/burn/external burn
        }

        for (int index = 0; index < 3; index++)
        {
            writer.WriteUInt32(0);                  // counters/state
        }

        for (int index = 0; index < 5; index++)
        {
            writer.WriteUInt64(0);                  // time anchors/state
        }

        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteBool(false);
    }
}
