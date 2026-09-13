using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.World.Doors;

/// <summary>Which of the two packets one <c>[F]</c> press produced.</summary>
public enum DoorRequestKind
{
    /// <summary><c>Command.PlayerSelect 09 15 00</c> — sent first.</summary>
    PlayerSelect,

    /// <summary><c>Command.InteractRequest 09 07 00</c> — sent ~2 ms later, same press.</summary>
    InteractRequest,
}

/// <summary>
/// The c2s "toggle this door" request — which is <b>not</b> a door packet at all.
/// <para>
/// <c>Character.RequestToggleDoorState (0f 52)</c> is registered in ClientProtocol_1148 and is
/// <b>dead</b>: the client has no receive case for it (docs/21 §1b) and no send site either — a scan
/// of both <c>.text</c> sections for a <c>0x0f</c> base store near a sub store finds exactly the four
/// c2s subs <c>0x2e</c>, <c>0x30</c>, <c>0x45</c>, <c>0x57</c> and nothing for <c>0x52</c>
/// (docs/42 §3a). A server waiting for <c>0f 52</c> waits forever.
/// </para>
/// <para>
/// What the August client actually sends for <c>[F]</c> on a door is the <b>ordinary interaction
/// pair</b> — the same two packets ground loot already uses. The local-player class' interact-attempt
/// method <c>FUN_1411c6d70</c> tests <c>0 &lt; *(int *)(target + 0x4348)</c> — "is this a door" —
/// twice: once to allow the interaction at all, and once to let a door be opened <em>without</em>
/// cancelling an in-flight reload. It then calls <c>FUN_140ad9ad0</c>, which emits
/// <c>Command.PlayerSelect</c> and then <c>Command.InteractRequest</c> (docs/42 §3b).
/// </para>
/// <para>
/// <b>One press produces two packets</b>, so the server-side toggle must fire once —
/// <see cref="MatchDoors.TryToggle"/> is what absorbs the second.
/// </para>
/// <para>
/// Both layouts are byte-exact from the client's own writers <b>and</b> from a live capture
/// (<c>logs/host-20260829-201025.log:297,300</c>):
/// <code>
/// 09 15 00 | 0310000000000000 | 0800000000000020                                 19 B
/// 09 07 00 | 0800000000000020 | D45B4CC2 3E370642 FD7E8043 0000803F | 01         28 B
/// </code>
/// The float4 decoded to <c>(−51.09, 33.55, 257.0, 1.0)</c>, a world position — whether it is the
/// player's, the camera's or the ray hit on the target is still open (docs/42 §9 open question 4),
/// so <see cref="Point"/> is carried but never trusted for anything load-bearing.
/// </para>
/// </summary>
/// <param name="Kind">Which packet this was.</param>
/// <param name="TargetGuid">The guid of the object under the cursor — the guid the door's own
/// <see cref="AddLightweightDoor"/> carried.</param>
/// <param name="SelectingCharacterGuid">The pressing player's character guid; 0 on an
/// <see cref="DoorRequestKind.InteractRequest"/>, which does not carry one.</param>
/// <param name="Point">The <c>09 07</c> float4, or <see cref="Vector4.Zero"/> when absent.</param>
/// <param name="Flag">The <c>09 07</c> trailing u8 (observed 1); 0 when absent.</param>
/// <param name="HasPoint">Whether the full 28-byte <c>09 07</c> tail was present.</param>
public sealed record DoorToggleRequest(
    DoorRequestKind Kind,
    ulong TargetGuid,
    ulong SelectingCharacterGuid = 0,
    Vector4 Point = default,
    byte Flag = 0,
    bool HasPoint = false)
{
    public const byte Opcode = ZoneOpcodes.CommandBase;

    /// <summary><c>Command.InteractRequest</c>: <c>u8 0x09; u16 0x0007</c>.</summary>
    public const ushort InteractRequestSub = 0x0007;

    /// <summary><c>Command.PlayerSelect</c>: <c>u8 0x09; u16 0x0015</c>.</summary>
    public const ushort PlayerSelectSub = 0x0015;

    /// <summary>
    /// Full length of <c>09 07 00</c>: <c>3 + u64 + 4 × f32 + u8</c> — the client's writer
    /// <c>FUN_140b78180</c>, confirmed live.
    /// </summary>
    public const int InteractRequestLength = 28;

    /// <summary>
    /// Shortest <c>09 07 00</c> this parser accepts: header plus the guid. The tail is read when it
    /// is there and reported absent when it is not, so a future client revision that trims it is a
    /// degraded read rather than a rejected press.
    /// </summary>
    public const int InteractRequestMinimumLength = 11;

    /// <summary>Full length of <c>09 15 00</c>: <c>3 + u64 + u64</c>.</summary>
    public const int PlayerSelectLength = 19;

    /// <summary>The world position the client sent, or null when the packet carried none.</summary>
    public Vector3? Position => HasPoint ? new Vector3(Point.X, Point.Y, Point.Z) : null;

    /// <summary>
    /// True when <paramref name="payload"/> is one of the two packets, without parsing it. Cheap
    /// enough for a dispatcher's <c>when</c> clause.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> payload) =>
        payload.Length >= 3
        && payload[0] == Opcode
        && (SubOf(payload) is InteractRequestSub or PlayerSelectSub);

    public static bool TryParse(ReadOnlySpan<byte> payload, out DoorToggleRequest? request)
    {
        request = null;
        if (!Matches(payload))
        {
            return false;
        }

        ushort sub = SubOf(payload);
        if (sub == InteractRequestSub
            ? payload.Length < InteractRequestMinimumLength
            : payload.Length < PlayerSelectLength)
        {
            return false;
        }

        request = Parse(payload);
        return true;
    }

    public static DoorToggleRequest Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        ushort sub = reader.ReadUInt16();

        if (opcode != Opcode || sub is not (InteractRequestSub or PlayerSelectSub))
        {
            throw new PacketFormatException(
                $"Expected Command.InteractRequest 09 07 00 or Command.PlayerSelect 09 15 00, got "
                + $"{opcode:x2} {sub & 0xff:x2} {sub >> 8:x2}.");
        }

        if (sub == PlayerSelectSub)
        {
            // FUN_140ad9ad0: { u64 localPlayerGuid; u64 targetGuid }.
            ulong selecting = reader.ReadUInt64();
            return new DoorToggleRequest(DoorRequestKind.PlayerSelect, reader.ReadUInt64(), selecting);
        }

        // FUN_140b78180: { u64 targetGuid; f32 x, y, z, w; u8 flag }.
        ulong target = reader.ReadUInt64();
        if (reader.Remaining < (4 * sizeof(float)) + 1)
        {
            return new DoorToggleRequest(DoorRequestKind.InteractRequest, target);
        }

        var point = new Vector4(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        byte flag = reader.ReadByte();

        return new DoorToggleRequest(
            DoorRequestKind.InteractRequest,
            target,
            SelectingCharacterGuid: 0,
            Point: point,
            Flag: flag,
            HasPoint: true);
    }

    private static ushort SubOf(ReadOnlySpan<byte> payload) =>
        (ushort)(payload[1] | (payload[2] << 8));
}

/// <summary>
/// The <c>InteractReplicationData.interactionRange</c> a door's <c>ea 04</c> carries — the two
/// values docs/114 §5 puts either side of one switch.
/// </summary>
public static class DoorInteractRange
{
    /// <summary>
    /// <b>2.0 m</b> — the friend's live server's own <c>ClientInteractComponent</c> range, adopted
    /// under D53. His <c>InteractReplicationData</c> payload begins <c>00 00 00 40</c>
    /// (<c>C:\Project\out\ingest-admin-20260822-part1\ops\cPacketIdReplicationBase.txt:13</c>) in the
    /// same field of the same class hash <c>9d1cd550</c> where Cranberry wrote <c>00 00 40 40</c>
    /// (<c>captures\wire-20260903-174857.txt:6692</c>).
    /// <para>
    /// Caveat kept with the value: his 2.0 is on a <em>dropped item</em>. His door records carry no
    /// interact component at all, because they carry no <c>Doors.txt</c> row either and are
    /// non-interactive scenery in that capture (AUDIT-doors §7). So this is his server's interaction
    /// range; it is not, on that evidence alone, provably his DOOR range.
    /// </para>
    /// </summary>
    public const float Retail = 2f;

    /// <summary>
    /// <b>3.0 m</b> — Cranberry's own earlier choice and the revert
    /// (<c>CRANBERRY_DOOR_RETAIL_INTERACT=0</c>). Identical to
    /// <see cref="InteractReplicationData.DefaultRange"/>, which ground loot keeps.
    /// </summary>
    public const float Legacy = InteractReplicationData.DefaultRange;
}
