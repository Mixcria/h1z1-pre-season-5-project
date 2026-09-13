using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Match;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Generated;

/// <summary>
/// <b>Length theory</b> for every hand-written packet body lane 2B owns: <c>GasPackets</c>,
/// <c>GasHud</c>'s countdown, <c>InteractionStringPackets</c> and <c>GasAlerts</c>/
/// <c>MatchAlerts</c>.
/// <para>
/// Each body states its own length as a constant or as a rule. The point of these tests is that
/// the constant and the writer are checked against <i>the same third thing</i> — the field list —
/// rather than against each other: every case below writes the packet, asserts the byte count the
/// class promises, and then re-derives that count from the family's header width
/// (<see cref="ZoneOpcodes.SubOpcodeWidth"/>, itself read off the client's own dispatcher) plus
/// the payload's field sizes. A body that grows a field without moving its constant fails here.
/// </para>
/// <para>
/// This matters more than it looks for two of them. <c>ce 04 DeathInfo</c> is <b>exact-length
/// gated</b> at <c>FUN_140bbb120:70</c>, so an off-by-three makes the client drop every death
/// report; and <c>09 2d</c>'s reply must be 19 bytes with no entries or the prompt stays blank.
/// </para>
/// </summary>
public sealed class ClientTableLayoutTests
{
    private const int F32 = sizeof(float);
    private const int U32 = sizeof(uint);
    private const int U64 = sizeof(ulong);

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>Header bytes of a family: the base byte plus its own sub-opcode width.</summary>
    private static int Header(byte baseOpcode)
    {
        int? width = ZoneOpcodes.SubOpcodeWidth.For(baseOpcode);
        Assert.NotNull(width);
        ZoneSubOpcodeWidth row = ZoneOpcodes.SubOpcodeWidth.Derived.Single(r => r.BaseOpcode == baseOpcode);
        return sizeof(byte) + row.PrefixBytes + width!.Value;
    }

    // ==========================================================================================
    // Gas/GasPackets.cs
    // ==========================================================================================

    /// <summary>
    /// <c>ce 01 Ring</c>: header + <c>f32×4</c> centre + <c>f32</c> radius + <c>u32×2</c>.
    /// Parser <c>FUN_140bafe10</c> reads exactly 7 × <c>u32</c> in one unconditional loop.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1234.5f)]
    public void GasRingIsItsFieldList(float radius)
    {
        byte[] bytes = Bytes(w => GasPackets.WriteRing(w, new Vector4(1, 2, 3, 1), radius));

        Assert.Equal(GasPackets.RingLength, bytes.Length);
        Assert.Equal(Header(GasPackets.Opcode) + (4 * F32) + F32 + (2 * U32), bytes.Length);
        Assert.Equal(31, bytes.Length);
        Assert.Equal(GasPackets.Opcode, bytes[0]);
        Assert.Equal(GasPackets.RingSubOpcode, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1)));
    }

    /// <summary>
    /// <c>ce 02 SafeZone</c>: header + <c>f32×4</c> centre + <c>f32</c> radius. No timing field
    /// exists on this sub at all (docs/15 §2).
    /// </summary>
    [Fact]
    public void GasSafeZoneIsItsFieldList()
    {
        byte[] bytes = Bytes(w => GameModeHud.WriteSafeZone(w, new Vector4(1, 0, 3, 0), 900f));

        Assert.Equal(GasPackets.SafeZoneLength, bytes.Length);
        Assert.Equal(Header(GasPackets.Opcode) + (4 * F32) + F32, bytes.Length);
        Assert.Equal(23, bytes.Length);
    }

    /// <summary>
    /// <c>ce 04 DeathInfo</c>: header + <c>u32</c> rank + <c>u8</c> draw + (<c>u32</c> length +
    /// name bytes) + <c>u32×3</c> = <b>24 + nameLen</b>, and the handler is exact-length gated.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Sam")]
    [InlineData("a name with spaces and é")]
    public void DeathInfoIsTwentyFourPlusItsName(string killer)
    {
        var record = new GasPackets.DeathInfo(Rank: 3, Killer: killer, Cause: GasPackets.DeathCause.Gas);
        byte[] bytes = Bytes(record.WriteTo);

        int nameBytes = Encoding.UTF8.GetByteCount(killer);
        Assert.Equal(record.Length, bytes.Length);
        Assert.Equal(GasPackets.DeathInfo.BaseLength + nameBytes, bytes.Length);
        Assert.Equal(
            Header(GasPackets.Opcode) + U32 + sizeof(byte) + (U32 + nameBytes) + (3 * U32),
            bytes.Length);
        Assert.Equal(24 + nameBytes, bytes.Length);
    }

    /// <summary><c>11 01 Hitpoints</c>: header + <c>u32×2</c> = 11 bytes (reader <c>FUN_140a357d0</c>).</summary>
    [Fact]
    public void HitpointsIsElevenBytes()
    {
        byte[] bytes = Bytes(new GasPackets.Hitpoints(76, 100).WriteTo);

        Assert.Equal(GasPackets.Hitpoints.Length, bytes.Length);
        Assert.Equal(Header(GasPackets.Hitpoints.Family) + (2 * U32), bytes.Length);
        Assert.Equal(11, bytes.Length);
    }

    /// <summary>
    /// <c>11 1e DamageInfo</c>: header + <c>u32</c> + varint + <c>u32×6</c> + <c>u8</c>, and the
    /// <c>DamageInfoRepeatsPrefix</c> reading adds one more copy of the 3-byte header as payload.
    /// The varint is the only variable-width field, so a small value costs one byte.
    /// </summary>
    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 0u)]
    [InlineData(false, 300u)]
    public void DamageInfoIsItsFieldListPlusOneVarint(bool repeatPrefix, uint field4)
    {
        byte[] bytes = Bytes(w => new GasPackets.DamageInfo(Field4: field4, Field5: 12).WriteTo(w, repeatPrefix));

        int header = Header(GasPackets.DamageInfo.Family);
        int varint = field4 < 0x80 ? 1 : 2;
        Assert.Equal(
            header + (repeatPrefix ? header : 0) + U32 + varint + (6 * U32) + sizeof(byte),
            bytes.Length);
    }

    // ==========================================================================================
    // MatchFlowPackets.cs (GameModeHud) - the body GasHud feeds
    // ==========================================================================================

    /// <summary>
    /// <c>ce 0f Countdown</c>: header + <c>u32</c> + <c>u32</c> ms + <c>u32</c> labelId +
    /// (<c>u32</c> length + label bytes). <see cref="GasHud.Countdown"/> only ever supplies the
    /// first three, so the packet it drives is a fixed 19 bytes.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("override")]
    public void GameModeCountdownIsItsFieldList(string label)
    {
        var settings = new GasSettings();
        (uint labelId, uint milliseconds) = GasHud.Countdown(settings, schedule: null, 0, airborne: false);

        byte[] bytes = Bytes(w => GameModeHud.WriteCountdown(w, milliseconds, labelId, label));

        int labelBytes = Encoding.UTF8.GetByteCount(label);
        Assert.Equal(Header(GameModeHud.Opcode) + U32 + U32 + U32 + (U32 + labelBytes), bytes.Length);
        Assert.Equal(19 + labelBytes, bytes.Length);

        // The label id the widget is gated on is never 0 and is always one of the three the
        // generated table carries.
        Assert.True(labelId > 0);
        Assert.Contains(labelId, new[]
        {
            AugustStrings.HudLabels.RevealingSafeZone,
            AugustStrings.HudLabels.GasAdvancesIn,
            AugustStrings.HudLabels.GasIsSpreading,
        });
        Assert.Equal(labelId, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11)));
    }

    // ==========================================================================================
    // Gas/GasAlerts.cs and Match/MatchAlerts.cs
    // ==========================================================================================

    /// <summary>
    /// <c>11 31 TextAlert</c>: header + <c>u32</c> length + UTF-8 bytes, and nothing else — the
    /// dispatcher installs exactly one <c>IString</c>. Every generated sentence is checked, so a
    /// multi-byte character in a future locale cannot break the rule quietly.
    /// </summary>
    [Fact]
    public void EveryAlertSentenceIsSevenBytesPlusItsUtf8()
    {
        Assert.Equal(GasAlerts.HeaderLength, Header(GasAlerts.Family) + U32);
        Assert.Equal(7, GasAlerts.HeaderLength);

        foreach (string message in new[]
                 {
                     GasAlerts.MatchBegun,
                     GasAlerts.Proceed,
                     GasAlerts.ReleasingGas,
                     GasAlerts.SafeZoneMarked(150),
                     MatchAlerts.Remaining(9),
                     MatchAlerts.WinnerAnnounced("a player with a very long name indeed"),
                     MatchAlerts.CelebrationEnding(30),
                     MatchAlerts.Connected(150),
                 })
        {
            byte[] bytes = Bytes(w => GasAlerts.Write(w, message));

            Assert.Equal(GasAlerts.LengthOf(message), bytes.Length);
            Assert.Equal(MatchAlerts.LengthOf(message), bytes.Length);
            Assert.Equal(GasAlerts.HeaderLength + Encoding.UTF8.GetByteCount(message), bytes.Length);
            Assert.Equal(GasAlerts.Family, bytes[0]);
            Assert.Equal(GasAlerts.SubOpcode, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1)));
            Assert.Equal(
                (uint)Encoding.UTF8.GetByteCount(message),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(3)));
        }
    }

    // ==========================================================================================
    // World/Doors/InteractionStringPackets.cs
    // ==========================================================================================

    /// <summary>
    /// <c>09 2d</c> reply, no entries: header + <c>u64</c> guid + <c>u32</c> + <c>u32</c> = 19,
    /// the length the live request itself carries, with the string id first (lane 0D deleted the
    /// alternative order with its switch).
    /// </summary>
    [Fact]
    public void TheEmptyInteractionReplyIsNineteenBytes()
    {
        var reply = InteractionStringReply.For(0x2000_0000_0000_0010UL, InteractionTargetKind.ClosedDoor);
        byte[] bytes = Bytes(reply.WriteTo);

        Assert.Equal(InteractionStringReply.MinimalLength, bytes.Length);
        Assert.Equal(InteractionStringRequest.Length, bytes.Length);
        Assert.Equal(Header(InteractionStringReply.Opcode) + U64 + U32 + U32, bytes.Length);
        Assert.Equal(19, bytes.Length);

        Assert.Equal(
            AugustStrings.Prompts.Open,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11)));
    }

    /// <summary>
    /// With entries the reply grows by the 32-byte-stride struct the client walks:
    /// <c>IString</c> (<c>u32</c> length + bytes) + <c>f32</c> range + <c>u32</c> string id.
    /// </summary>
    [Fact]
    public void EachInteractionEntryAddsItsOwnFieldList()
    {
        var entry = new InteractionStringEntry("DoorHandle", 2.5f, AugustStrings.Prompts.Open);
        var reply = new InteractionStringReply(
            0x2000_0000_0000_0010UL, AugustStrings.Prompts.CloseDoor, [entry, entry]);

        byte[] bytes = Bytes(reply.WriteTo);
        int perEntry = U32 + Encoding.UTF8.GetByteCount("DoorHandle") + F32 + U32;

        Assert.Equal(2, reply.EntryCount);
        Assert.Equal(InteractionStringReply.MinimalLength + (2 * perEntry), bytes.Length);
    }

    /// <summary>The c2s request the reply answers is the same 19 bytes, and round-trips.</summary>
    [Fact]
    public void TheInteractionRequestIsNineteenBytesAndRoundTrips()
    {
        byte[] live =
        [
            0x09, 0x2D, 0x00,
            0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];

        Assert.Equal(InteractionStringRequest.Length, live.Length);
        Assert.Equal(Header(InteractionStringRequest.Opcode) + U64 + U32 + U32, live.Length);
        Assert.True(InteractionStringRequest.TryParse(live, out InteractionStringRequest? request));
        Assert.Equal(0x2000_0000_0000_0010UL, request!.TargetGuid);
        Assert.Equal(0u, request.FirstWord);
        Assert.Equal(0u, request.SecondWord);
    }

    // ==========================================================================================
    // The widths those headers are computed from
    // ==========================================================================================

    /// <summary>
    /// The six derived sub-opcode widths, pinned against what every writer in the tree actually
    /// emits: <c>0x0f</c>, <c>0x82</c> and <c>0x86</c> write one byte, <c>0x09</c>, <c>0x11</c> and
    /// <c>0xce</c> write two. A width that moved would break every header arithmetic above.
    /// </summary>
    [Theory]
    [InlineData(0x09, 2, 0)]
    [InlineData(0x0f, 1, 0)]
    [InlineData(0x11, 2, 0)]
    [InlineData(0x82, 1, 4)]
    [InlineData(0x86, 1, 0)]
    [InlineData(0xce, 2, 0)]
    public void TheDerivedSubOpcodeWidthsAreWhatTheWritersEmit(int baseOpcode, int width, int prefix)
    {
        ZoneSubOpcodeWidth row = Assert.Single(
            ZoneOpcodes.SubOpcodeWidth.Derived, r => r.BaseOpcode == (byte)baseOpcode);

        Assert.Equal(width, row.Bytes);
        Assert.Equal(prefix, row.PrefixBytes);
        Assert.Equal(width, ZoneOpcodes.SubOpcodeWidth.For((byte)baseOpcode));
        Assert.StartsWith("FUN_", row.Dispatcher, StringComparison.Ordinal);
        Assert.EndsWith(".c", row.Dump, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(row.Evidence));
    }

    /// <summary>The named constants say the same thing, and a family with no derivation says null.</summary>
    [Fact]
    public void TheSubOpcodeWidthConstantsMatchTheTable()
    {
        Assert.Equal(2, ZoneOpcodes.SubOpcodeWidth.Command);
        Assert.Equal(1, ZoneOpcodes.SubOpcodeWidth.Character);
        Assert.Equal(2, ZoneOpcodes.SubOpcodeWidth.ClientUpdate);
        Assert.Equal(1, ZoneOpcodes.SubOpcodeWidth.Weapon);
        Assert.Equal(4, ZoneOpcodes.SubOpcodeWidth.WeaponPrefixBytes);
        Assert.Equal(1, ZoneOpcodes.SubOpcodeWidth.Loadouts);
        Assert.Equal(2, ZoneOpcodes.SubOpcodeWidth.GameMode);

        Assert.Equal(6, ZoneOpcodes.SubOpcodeWidth.Derived.Count);
        Assert.Null(ZoneOpcodes.SubOpcodeWidth.For(ZoneOpcodes.MountBase));
    }

    /// <summary>
    /// The <c>0x0f</c> width is the one with a visible consequence: <c>RemovePlayer</c> is 12
    /// bytes because the guid starts at offset 2, and the live burst
    /// (<c>wire-20260831-192743.txt:5313-5330</c>) shows exactly that.
    /// </summary>
    [Fact]
    public void TheCharacterWidthIsWhatMakesRemovePlayerTwelveBytes()
    {
        byte[] bytes = Bytes(new RemovePlayer(0x0000_0000_0000_2004UL).WriteTo);

        Assert.Equal(RemovePlayer.Length, bytes.Length);
        Assert.Equal(
            sizeof(byte) + ZoneOpcodes.SubOpcodeWidth.Character + U64 + sizeof(ushort),
            bytes.Length);
        Assert.Equal(12, bytes.Length);
        Assert.Equal(ZoneOpcodes.CharacterBase, bytes[0]);
        Assert.Equal(0x01, bytes[1]);
        Assert.Equal(0x2004UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(2)));
    }
}
