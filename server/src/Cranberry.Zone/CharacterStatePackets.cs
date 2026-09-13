using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// <c>CharacterState.InteractionStart</c>. August shifts the March server's family base from
/// <c>0xd0</c> to registered <c>0xcf</c>; sub 2 and the 65-byte body are unchanged. A non-zero
/// duration draws the center-screen interaction timer and <see cref="AnimationId"/> plays the
/// associated character animation.
/// </summary>
public sealed record InteractionStart(
    ulong CharacterGuid,
    int DurationMilliseconds,
    uint StringId,
    uint AnimationId)
{
    public const byte Opcode = ZoneOpcodes.CharacterStateBase;
    public const byte SubOpcode = 0x02;
    public const int Length = 67;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32((uint)Math.Max(DurationMilliseconds, 0));
        writer.WriteUInt32(0);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(StringId);
        writer.WriteUInt32(AnimationId);

        // extraData (4 x u32), empty UseOptionItemId string, empty useOptionString.
        for (int index = 0; index < 21; index++)
        {
            writer.WriteByte(0);
        }
    }
}

/// <summary>
/// <c>CharacterState.InteractionStop</c> — <b>10 bytes</b>: the two-byte header and the character
/// guid, and nothing else.
/// <para>
/// <b>Derived from the owner's own admin capture</b>, not from any schema: the friend's server
/// answers every completed cast with two of these, and all six instances are byte-identical apart
/// from the guid —
/// <c>packets_1119_53544.log:6960/:6964</c>, <c>:7446/:7447</c> and <c>:8009/:8010</c>, each pair
/// 8–9 ms apart, each <c>d0 03</c> + the same <c>u64</c> the <c>d0 02</c> before it carried. August
/// shifts the family base from <c>0xd0</c> to registered <c>0xcf</c>, the same shift
/// <see cref="InteractionStart"/> already makes.
/// </para>
/// <para>
/// <b>Why it matters mechanically.</b> A cast bar's duration and its animation are two different
/// clocks. Shred's bar runs 1,000 ms (<c>ItemUseOptions</c> row 6 <c>BUSY_MSEC</c>) while animation
/// 10's <c>InteractionAnimations</c> row is <c>Action / ActionEnd / EXPIRE_MSEC 2000</c>, so without
/// an explicit stop the character keeps playing the shred for a second after the item has already
/// changed. <c>ActionEnd</c> is what cuts it short and this is the packet that fires it.
/// </para>
/// </summary>
public sealed record InteractionStop(ulong CharacterGuid)
{
    public const byte Opcode = ZoneOpcodes.CharacterStateBase;
    public const byte SubOpcode = 0x03;
    public const int Length = 10;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
    }
}
