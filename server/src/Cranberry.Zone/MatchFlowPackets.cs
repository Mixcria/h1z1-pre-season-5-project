using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone;

// Packets of the match flow (docs/11-match-flow-map.md): queue → transfer → zone into Z2 →
// pre-game lobby HUD → synchronized teleport → drop. Layouts from the August binary; the
// numeric contents marked [INF] in docs/11 are the 2016 server's values (lead).

/// <summary>
/// <c>LoginBase.QueueUpdateGameMode</c> (base 0xa6, u8 sub 8; parser <c>FUN_140f0d710</c> =
/// QueueUpdate's <c>u32×5; u8; u32×5</c> plus <c>u32×4</c>, 59 bytes; handler
/// <c>FUN_1413dddb0</c> → <c>EVENT_SERVER_QUEUE_UPDATE_GAMEMODE</c>, the "IN QUEUE" strip).
/// </summary>
public sealed record QueueUpdateGameMode(uint Position = 1, uint GameMode = 13)
{
    public const byte Opcode = ZoneOpcodes.LoginBase;
    public const byte SubOpcode = 0x08;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(Position);
        for (int index = 0; index < 4; index++)
        {
            writer.WriteUInt32(GameMode);
        }

        writer.WriteBool(true);
        for (int index = 0; index < 9; index++)
        {
            writer.WriteUInt32(GameMode);
        }
    }
}

/// <summary>
/// <c>LoginBase.QueueExit</c> (base 0xa6, u8 sub 4; parser <c>FUN_140f0d8c0</c>: <c>u32 seconds;
/// u64 id; u8 cancel</c>, 15 bytes; handler <c>FUN_1413ddfa0</c> → <c>EVENT_SERVER_QUEUE_EXIT</c>
/// = "Joining match in N seconds" with Accept/Decline; the client answers with a second
/// <c>PlayerWorldTransferRequest</c>).
/// </summary>
public sealed record QueueExit(uint Seconds, ulong Id, bool Cancel = false)
{
    public const byte Opcode = ZoneOpcodes.LoginBase;
    public const byte SubOpcode = 0x04;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(Seconds);
        writer.WriteUInt64(Id);
        writer.WriteBool(Cancel);
    }
}

/// <summary>
/// <c>PlayerWorldTransferReply</c> (0xed; parser <c>FUN_140a63230</c>: <c>u8 result; u32 serverId</c>,
/// exact consume; handler <c>FUN_140f286f0</c>: result 0 raises <c>EVENT_CHARACTER_TRANSFER(true)</c>
/// and the "Sending you to the game momentarily" window).
/// </summary>
public sealed record PlayerWorldTransferReply(uint ServerId, byte Result = 0)
{
    public const byte Opcode = ZoneOpcodes.PlayerWorldTransferReply;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(Result);
        writer.WriteUInt32(ServerId);
    }
}

/// <summary>
/// The unregistered GameMode / BR-HUD family (base 0xce, u16 sub; dispatcher →
/// <c>FUN_140bba510(DAT_143f69da0)</c>).
/// </summary>
public static class GameModeHud
{
    public const byte Opcode = 0xce;
    public const uint StartingMatchLabelId = 13356;

    /// <summary>
    /// <c>14153 BR.RevealingSafeZone "Revealing safe zone in"</c> (the client's own
    /// <c>CodeStringMappings.txt</c>). Sent while the player is airborne and until phase 1 is
    /// revealed; the timer is the seconds to that reveal.
    /// </summary>
    public const uint RevealingSafeZoneLabelId = 14153;

    /// <summary>
    /// <c>14151 BR.GasAdvancesIn "Gas advances in"</c>. Sent when a circle has been revealed and its
    /// ring is still holding; the timer is the seconds until it starts moving. docs/66 section 6,
    /// docs/77 section 5.
    /// </summary>
    public const uint GasAdvancesInLabelId = 14151;

    /// <summary>
    /// <c>14152 BR.GasIsSpreading "Gas is spreading!"</c>. Sent when the ring starts travelling; the
    /// timer is the remaining advance, i.e. the seconds until it has fully closed, and 0 once the
    /// last phase is parked (which renders the label with an empty timer). This is the owner's own
    /// report: "should say gas is spreading not zone will reveal in x amount of time". docs/77
    /// section 5.
    /// </summary>
    public const uint GasIsSpreadingLabelId = 14152;

    /// <summary>Sub 0x10: the mode trio (<c>u32×4</c>, <c>FUN_140bb0690</c>) — must precede the HUD load.</summary>
    public static void WriteModeTrio(PacketWriter writer, uint gameMode = 13, uint a = 1, uint b = 1, uint c = 0)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(0x0010);
        writer.WriteUInt32(gameMode);
        writer.WriteUInt32(a);
        writer.WriteUInt32(b);
        writer.WriteUInt32(c);
    }

    /// <summary>Sub 0x09: players remaining (<c>i32</c>; −1 hides the counter).</summary>
    public static void WritePlayersRemaining(PacketWriter writer, int players)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(0x0009);
        writer.WriteInt32(players);
    }

    /// <summary>
    /// Sub 0x0f: countdown widget (<c>u32; u32 ms; u32 labelId; str</c>, <c>FUN_140bb9e60</c>).
    /// The client stores <c>now + ms</c> as an absolute deadline and counts down itself, so this is
    /// sent on state transitions rather than every tick; <c>FUN_140bb67b0</c> gates the whole widget
    /// on <c>labelId &gt; 0</c>, and <c>milliseconds = 0</c> renders the label with an empty timer.
    /// </summary>
    public static void WriteCountdown(
        PacketWriter writer,
        uint milliseconds,
        uint labelId = 0,
        string label = "")
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(0x000f);
        writer.WriteUInt32(0);
        writer.WriteUInt32(milliseconds);
        writer.WriteUInt32(labelId);
        writer.WriteString(label);
    }

    /// <summary>
    /// The first string of <c>ce 14</c>: the client's own <c>CodeStringMappings.txt</c> name for
    /// locale id <b>11107</b> (<c>out\data_aug\CodeStringMappings.txt:41</c>). The handler resolves
    /// the wire string through the locale manager, so this packet carries KEYS, not text.
    /// </summary>
    public const string MatchStartBannerKey = "BR.MatchStart";

    /// <summary>
    /// The second string of <c>ce 14</c>: <c>CodeStringMappings.txt:58</c>, locale id <b>11103</b> -
    /// the unit the number is spoken in.
    /// </summary>
    public const string SecondBannerKey = "BR.Second";

    /// <summary>
    /// Sub 0x14: the centred <b>"Match starts in N seconds."</b> banner - two locale KEYS and a
    /// count.
    ///
    /// <para>
    /// <b>Layout, from the client's own parser <c>FUN_140bb0f40</c></b>
    /// (<c>out\gas-safezone-research\ce-dispatch\_140bba510\FUN_140bb0f40_140bb0f40.c</c>): the
    /// case re-reads the base and the sub, then two <c>SoeUtil::IString</c>s
    /// (<c>FUN_140b78f60</c> into <c>+0x18</c> and <c>+0x30</c>) and one <c>u32</c> at
    /// <c>+0x48</c>.
    /// </para>
    /// <code>
    ///   u8   base   0xce
    ///   u16  sub    0x0014
    ///   str  key1                  u32-length-prefixed UTF-8
    ///   str  key2
    ///   u32  count
    /// </code>
    /// <para>
    /// <b>Both strings are locale KEYS.</b> The dispatcher hands them to
    /// <c>FUN_1412c8560</c>, which pushes each through the string manager's
    /// <c>DAT_143f696c0</c> vtable+0x18 lookup before building a three-element argument array
    /// <c>[text1, text2, count]</c> and raising the notification event named by
    /// <c>PTR_s_ServerShutdown_143d02608</c>. The event name is the client's generic
    /// centred-announcement-with-a-number widget - <b>this sub is not itself a shutdown
    /// notice</b>; docs/15 §7 recorded it only as "two strings + a u32 … unnamed, not gas-shaped".
    /// </para>
    /// <para>
    /// Which keys go in it is the owner's own value, adopted under D53 from
    /// <c>C:\Z1\Server\Zone\ZoneMatchFlow.cs:4903-4909</c> - his <c>cf 14</c> writer sends
    /// <c>("BR.MatchStart", "BR.Second", N)</c> and his comment is "a red banner reading
    /// <i>Match starts in 60 seconds.</i>". Both names exist in the August client's own
    /// <c>CodeStringMappings.txt</c> (11107 and 11103), which is what makes the 1087 value portable
    /// to 1148. 37 bytes with this pair.
    /// </para>
    /// </summary>
    public static void WriteMatchStartBanner(
        PacketWriter writer,
        uint seconds,
        string headlineKey = MatchStartBannerKey,
        string unitKey = SecondBannerKey)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(0x0014);
        writer.WriteString(headlineKey);
        writer.WriteString(unitKey);
        writer.WriteUInt32(seconds);
    }

    /// <summary>
    /// Sub 0x15: one bool (<c>FUN_140bba020</c>). Despite this legacy API name, true means
    /// IsInBox (the pre-game lobby); false leaves it. New code uses BountyLobbyState explicitly.
    /// Setting true after StartMatch was the cause of the bounty screen opening during the drop.
    /// </summary>
    public static void WriteInMatchState(PacketWriter writer, bool inMatch)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(0x0015);
        writer.WriteBool(inMatch);
    }

    /// <summary>Sub 0x16: StartMatch (<c>u32</c>, <c>FUN_140bba2d0</c> → <c>EVENT_START_MATCH</c>).</summary>
    public static void WriteStartMatch(PacketWriter writer, uint value = 1)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(0x0016);
        writer.WriteUInt32(value);
    }

    /// <summary>Sub 0x02: safe zone (<c>f32×4 centre; f32 radius</c>, <c>FUN_140baff10</c>).</summary>
    public static void WriteSafeZone(PacketWriter writer, Vector4 centre, float radius)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(0x0002);
        writer.WriteSingle(centre.X);
        writer.WriteSingle(centre.Y);
        writer.WriteSingle(centre.Z);
        writer.WriteSingle(centre.W);
        writer.WriteSingle(radius);
    }

    /// <summary>Sub 0x1b: leave match (header only, <c>EVENT_LEAVE_MATCH</c>).</summary>
    public static void WriteLeaveMatch(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(0x001b);
    }
}

/// <summary>
/// <c>SynchronizedTeleport</c> (base 0xe8, u16 sub, no payload; <c>FUN_1413d8e80</c>): 1 = start
/// ("SyncTeleport.WaitingForPlayers", resets the manager), 3 = "SyncTeleport.StartingMatch",
/// 4 = release. The client sends <c>e8 02 00</c> when it is ready.
/// </summary>
public sealed record SynchronizedTeleport(ushort SubOpcode)
{
    public const byte Opcode = ZoneOpcodes.SynchronizedTeleportBase;
    public const ushort Start = 1;
    public const ushort ClientReady = 2;
    public const ushort StartingMatch = 3;
    public const ushort Release = 4;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
    }
}

/// <summary>
/// <c>ClientUpdate.UpdateLocation</c> (base 0x11, u16 sub 0x0a; parser <c>FUN_140a369c0</c>:
/// <c>f32×4 position; f32×4 rotation; u8 apply; u8; u8 waitForTeleport</c>, 38 bytes). apply = 1
/// moves the local actor (<c>FUN_140c77820</c>); waitForTeleport = 1 sets client+0x32839 and the
/// next frame enters run state 0x1b WaitForTeleport (the loading card of the drop).
/// </summary>
public sealed record UpdateLocation(Vector4 Position, Vector4 Rotation, bool Apply = true, bool WaitForTeleport = false)
{
    public const byte Family = ZoneOpcodes.ClientUpdateBase;
    public const ushort SubOpcode = 0x000a;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Family);
        writer.WriteUInt16(SubOpcode);
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteSingle(Position.W);
        writer.WriteSingle(Rotation.X);
        writer.WriteSingle(Rotation.Y);
        writer.WriteSingle(Rotation.Z);
        writer.WriteSingle(Rotation.W);
        writer.WriteBool(Apply);
        writer.WriteBool(false);
        writer.WriteBool(WaitForTeleport);
    }
}

/// <summary>
/// <c>Vehicle.AutoMount</c> (base 0x88, u8 sub 0x19; <c>FUN_140c95340</c> → <c>FUN_140c98dd0</c>:
/// <c>u64 vehicleGuid; u8; u32</c>, 15 bytes) — makes the client mount the vehicle (the parachute).
/// </summary>
public sealed record VehicleAutoMount(ulong VehicleGuid, bool Flag = true, uint Value = 0)
{
    public const byte Opcode = ZoneOpcodes.VehicleBase;
    public const byte SubOpcode = 0x19;
    public const int Length = 15;

    public static VehicleAutoMount Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        byte subOpcode = reader.ReadByte();
        if (opcode != Opcode || subOpcode != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected Vehicle.AutoMount 88 19, got {opcode:x2} {subOpcode:x2}.");
        }

        var packet = new VehicleAutoMount(
            reader.ReadUInt64(),
            reader.ReadBool(),
            reader.ReadUInt32());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"Vehicle.AutoMount has {reader.Remaining} trailing byte(s).");
        }

        return packet;
    }

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(VehicleGuid);
        writer.WriteBool(Flag);
        writer.WriteUInt32(Value);
    }
}

/// <summary>
/// <c>Mount.MountResponse</c> (base 0x70, u8 sub 2; parser <c>FUN_140c9e170</c>: <c>u64 rider;
/// u64 mount; u32 seat; u32 status (1 = success); u32 isDriver; u32; identity (FUN_140a40000:
/// u32×3; str; str×3; u64); str</c>; handler case 2 of <c>FUN_140c9fee0</c>).
/// </summary>
public sealed record MountResponse(ulong Rider, ulong Mount, uint Seat = 0, uint Status = 1, uint IsDriver = 1)
{
    public const byte Opcode = ZoneOpcodes.MountBase;
    public const byte SubOpcode = 0x02;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(Rider);
        writer.WriteUInt64(Mount);
        writer.WriteUInt32(Seat);
        writer.WriteUInt32(Status);
        writer.WriteUInt32(IsDriver);
        writer.WriteUInt32(0);
        // identity sub-record, empty
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteString(string.Empty);
        writer.WriteUInt64(0);
        writer.WriteString(string.Empty);
    }
}
