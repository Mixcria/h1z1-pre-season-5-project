using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.Gas;

/// <summary>
/// The wire side of the gas system. Every layout here is cited to the August parser that reads it;
/// the two whose *meaning* is not closed (<c>ce 01</c>'s trailing pair, <c>11 1e</c>'s fields) say
/// so at the field and are gated by <see cref="GasSettings"/> toggles.
/// </summary>
public static class GasPackets
{
    /// <summary>The GameMode / BR-HUD family base opcode (docs/11; dispatcher <c>FUN_140bba510</c>).</summary>
    public const byte Opcode = GameModeHud.Opcode;

    /// <summary>Sub 0x01 — the shrinking ring (parser <c>FUN_140bafe10</c>, 31 bytes).</summary>
    public const ushort RingSubOpcode = 0x0001;

    /// <summary>Sub 0x04 — death info (handler <c>FUN_140bbb120</c>, body <c>FUN_140bb0cc0</c>).</summary>
    public const ushort DeathInfoSubOpcode = 0x0004;

    /// <summary>Byte length of a <c>ce 01</c> ring: 3-byte header + <c>f32×4</c> + <c>f32</c> + <c>u32×2</c>.</summary>
    public const int RingLength = 31;

    /// <summary>Byte length of a <c>ce 02</c> safe zone: 3-byte header + <c>f32×4</c> + <c>f32</c>.</summary>
    public const int SafeZoneLength = 23;

    /// <summary>
    /// <c>ce 01</c>'s first trailing <c>u32</c> (gas object <c>+0x54</c>) is a **blend time in
    /// milliseconds**, and 1000 is the client's own pre-parse default (docs/18 §1, G-07 closed).
    /// The per-frame smoother <c>FUN_140bbed00(gasObj, frameDeltaMs)</c> computes
    /// <c>alpha = min(frameDeltaMs, 1000) / (i32)field6</c> and eases the drawn centre and radius
    /// toward the received ones, i.e. <c>x(t) = target + (x0 − target)·e^(−t/T)</c> with
    /// <c>T</c> = this field. It is a divisor with **no zero guard**, so it must never be 0 —
    /// which supersedes docs/15 §3c's "start time" lead and docs/23's <c>startTime = 0</c>.
    /// </summary>
    public const uint RingBlendMsDefault = 1000;

    /// <summary>
    /// <c>ce 01</c>'s second trailing <c>u32</c> (gas object <c>+0x58</c>) is **stored and never
    /// read** — absence proven across all 38 referrers of <c>DAT_143f69da0</c>, the whole
    /// <c>0xce</c> switch and the per-frame chain (docs/18 §1). Its pre-parse default is the float
    /// 1.0 (<c>DAT_1430ef088</c>), written as raw bits because the field is read as a <c>u32</c>.
    /// </summary>
    public const uint RingUnusedFieldDefault = 0x3F80_0000;

    /// <summary>
    /// A radius of exactly zero is a recognised terminal value: the <c>ce 01</c> handler
    /// <c>FUN_140bbba50</c> special-cases it and clears the hazard flag at
    /// <c>FUN_1423ce540(DAT_143f69e40)+0x180</c> (docs/15 §2). Never send it for an ordinary phase.
    /// </summary>
    public const float RingTerminalRadius = 0f;

    /// <summary>
    /// <c>GameMode.Ring</c> (<c>ce 01 00</c>): <c>f32×4 centre; f32 radius; u32; u32</c>, 31 bytes.
    /// Layout proven — handler <c>FUN_140bbba50</c> consumes the 3-byte header itself and
    /// <c>FUN_140bafe10</c> then reads exactly 7 × <c>u32</c> in one unconditional loop
    /// (docs/11 table, docs/15 §2). G-07 closed the two trailing fields (docs/18 §1): the first is
    /// a **blend time in milliseconds** for the client's own exponential smoother
    /// <c>FUN_140bbed00</c> (a divisor with no zero guard — never send 0), the second is stored at
    /// <c>+0x58</c> and never read by any consumer. <c>radius == 0</c> is terminal: it clears the
    /// volume renderer's enable byte (<c>FUN_1423ce540(DAT_143f69e40)+0x180</c>), so it is never an
    /// ordinary phase value.
    /// </summary>
    public static void WriteRing(
        PacketWriter writer,
        Vector4 centre,
        float radius,
        uint blendMs = RingBlendMsDefault,
        uint unusedField = RingUnusedFieldDefault)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(RingSubOpcode);
        writer.WriteSingle(centre.X);
        writer.WriteSingle(centre.Y);
        writer.WriteSingle(centre.Z);
        writer.WriteSingle(centre.W);
        writer.WriteSingle(radius);
        writer.WriteUInt32(blendMs);
        writer.WriteUInt32(unusedField);
    }

    /// <summary>
    /// The drawn gas wall. No "closes in" value goes on the wire: docs/18 §1 proved <c>ce 01</c>
    /// carries no deadline at all, only the smoothing constant
    /// <see cref="GasSettings.RingBlendMs"/> — the countdown to the close is the HUD's own
    /// <c>ce 0f</c> widget.
    /// <para>
    /// <b>This is the circle the client draws</b>, and the only one: docs/18 §1b proves <c>ce 01</c>
    /// is the sole input of the gas-volume renderer (<c>FUN_140bbed00</c> smoother →
    /// <c>FUN_1423ce5b0(DAT_143f69e40, &amp;shownCentre, -100.0f)</c>) and of the minimap provider
    /// <c>FUN_141209960</c>, while <c>ce 02</c> (<see cref="WriteSafeZone"/>) has no renderer path
    /// at all. So the caller must pass the circle **currently in force** and re-send it as the ring
    /// closes — docs/18 §1b's "re-send <c>ce 01</c> on a tick with a server-interpolated radius and
    /// a small <c>BlendMs</c>". Passing the phase's destination here once would draw the finished
    /// wall the instant the phase is revealed, minutes before it is lethal.
    /// </para>
    /// </summary>
    public static void WriteRing(PacketWriter writer, GasSettings settings, GasCircle circle) =>
        WriteRing(writer, settings, circle, advancing: false);

    /// <summary>
    /// The drawn gas wall, with D280's blend constant.
    /// <para>
    /// <paramref name="advancing"/> says whether <b>another <c>ce 01</c> is coming</b> — true only
    /// on the sends that happen while a ring is travelling, which are the only ones followed by
    /// anything. Under <see cref="GasRingBlendMode.SendPeriod"/> the field then carries
    /// <see cref="GasSettings.SafeZoneUpdateIntervalMs"/> and the client's own exponential ease
    /// (<c>FUN_140bbed00</c>, <c>alpha = min(frameΔms, 1000) / blendMs</c>) finishes exactly as the
    /// next update lands. Under <see cref="GasRingBlendMode.Fixed"/> every send carries the flat
    /// <see cref="GasSettings.RingBlendMs"/>, which is what the tree did before D280 and what made
    /// the drawn wall trail the lethal circle by about a second (AUDIT-gas G-h).
    /// </para>
    /// </summary>
    public static void WriteRing(PacketWriter writer, GasSettings settings, GasCircle circle, bool advancing)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WriteRing(
            writer,
            circle.CentreVector4,
            circle.Radius,
            BlendMsFor(settings, advancing),
            RingUnusedFieldDefault);
    }

    /// <summary>
    /// D280's one decision, isolated so a test can assert the byte without a socket: the blend
    /// constant a <c>ce 01</c> carries in each of the two states. Never 0 — the client divides by
    /// this field with no zero guard.
    /// </summary>
    public static uint BlendMsFor(GasSettings settings, bool advancing)
    {
        ArgumentNullException.ThrowIfNull(settings);
        uint blend = settings.RingBlendMode == GasRingBlendMode.SendPeriod && advancing
            ? settings.SafeZoneUpdateIntervalMs
            : settings.RingBlendMs;
        return Math.Max(1u, blend);
    }

    /// <summary>
    /// <c>GameMode.SafeZone</c> (<c>ce 02 00</c>): <c>f32×4 centre; f32 radius</c>, 23 bytes
    /// (self-framing parser <c>FUN_140baff10</c>, which consumes the packet's own header; docs/11,
    /// docs/15 §2 — no timing field exists on this sub at all). The bytes are produced by the
    /// existing <see cref="GameModeHud.WriteSafeZone"/>; this overload just takes a
    /// <see cref="GasCircle"/>.
    /// <para>
    /// This is the **next** safe zone, not the drawn one: docs/18 §2 proves its only consumers are
    /// the two Flash data providers <c>FUN_14120ac20</c> / <c>FUN_14142ac30</c> — "no per-frame or
    /// renderer consumer at all". Send it once per phase with that phase's destination circle and
    /// leave it there; the moving wall is <see cref="WriteRing"/>. Wave 9 also re-sends it every
    /// <see cref="GasSettings.SafeZoneHealIntervalMs"/> so one lost copy does not empty the map for
    /// the rest of the phase.
    /// </para>
    /// <para>
    /// The centre goes out as <see cref="GasCircle.SafeZoneVector4"/> — <c>[x, 0, z, 0]</c>, the
    /// owner's own bytes (<c>ZoneMatch.cs:2450</c>) — while <see cref="WriteRing"/> keeps
    /// <c>w = 1.0</c>. docs/87 §7 edit 7.
    /// </para>
    /// </summary>
    public static void WriteSafeZone(PacketWriter writer, GasCircle circle) =>
        GameModeHud.WriteSafeZone(writer, circle.SafeZoneVector4, circle.Radius);

    /// <summary>
    /// Death causes of <c>ce 04</c>'s <c>u32 cause</c> — the **wire** enum, recovered from the
    /// handler's own localisation switch <c>FUN_140bbb120:200-248</c> (docs/18 §3b).
    /// <para>
    /// This supersedes docs/15 §5's table, which is reproduced in <see cref="ResultsFileCause"/>:
    /// that one comes from the client's local <c>MatchResults-&lt;matchId&gt;.txt</c> writer
    /// <c>FUN_1413f0ad0</c> switching on <c>*(u32*)(record+0x70)</c> of a client-side record, and
    /// the two value spaces disagree on every shared concept (Falling 10 vs 0x11, Fire 8 vs 0x3e,
    /// Vehicle 6 vs 0x0d, Explosion 7 vs 0x23) — so they cannot be the same enum.
    /// </para>
    /// </summary>
    public static class DeathCause
    {
        /// <summary>No rank key (→ <c>Environment</c>); also names the source "vehicle" when <c>SourceId == 0</c>.</summary>
        public const uint Vehicle = 0x0d;

        /// <summary><c>UI.Results.Rank.Falling</c>.</summary>
        public const uint Falling = 0x11;

        /// <summary>No rank key (→ <c>Environment</c>); also names the source "explosion" when <c>SourceId == 0</c>.</summary>
        public const uint Explosion = 0x23;

        /// <summary><c>UI.Results.Rank.Fire</c>.</summary>
        public const uint Fire = 0x3e;

        /// <summary>
        /// <c>UI.Results.Rank.Gas</c> — the value a gas death reports on the wire (docs/18 §3b/§5).
        /// docs/15 §5's "cause = 9" was the results-file enum and is wrong here.
        /// </summary>
        public const uint Gas = 0x42;

        /// <summary><c>UI.Results.Rank.BombingRun</c>.</summary>
        public const uint BombingRun = 0x43;

        /// <summary><c>UI.Results.Rank.EndOfMatchWinner</c>.</summary>
        public const uint EndOfMatchWinner = 0x48;

        /// <summary><c>UI.Results.Rank.PlayerDisconnected</c>.</summary>
        public const uint PlayerDisconnected = 0x49;

        /// <summary><c>UI.Results.Rank.EndOfMatchRemaining</c>.</summary>
        public const uint EndOfMatchRemaining = 0x4a;

        /// <summary><c>UI.Results.Rank.EndOfMatchExpired</c>.</summary>
        public const uint EndOfMatchExpired = 0x4b;

        /// <summary><c>UI.Results.Rank.StartingAreaViolation</c>.</summary>
        public const uint StartingAreaViolation = 0x4c;

        /// <summary><c>UI.Results.Rank.GameModeKillCondition</c>.</summary>
        public const uint GameModeKillCondition = 0x4d;

        /// <summary><c>UI.Results.Rank.Ignition.Detonation</c>.</summary>
        public const uint IgnitionDetonation = 0x52;
    }

    /// <summary>
    /// The **other** cause enum: the one the client's own <c>MatchResults-&lt;matchId&gt;.txt</c>
    /// writer <c>FUN_1413f0ad0</c> switches on (docs/15 §5). It is client-side bookkeeping, never a
    /// <c>ce 04</c> field — kept only so the two value spaces are not confused again.
    /// </summary>
    public static class ResultsFileCause
    {
        /// <summary><c>[Disconnected]</c>.</summary>
        public const uint Disconnected = 1;

        /// <summary><c>[Vehicle]</c>.</summary>
        public const uint Vehicle = 6;

        /// <summary><c>[Explosion]</c>.</summary>
        public const uint Explosion = 7;

        /// <summary><c>[Fire]</c>.</summary>
        public const uint Fire = 8;

        /// <summary><c>[Toxic Gas]</c> (string at <c>0x143238b38</c>).</summary>
        public const uint ToxicGas = 9;

        /// <summary><c>[Falling]</c>.</summary>
        public const uint Falling = 10;

        /// <summary><c>[Spectate]</c>.</summary>
        public const uint Spectate = 13;
    }

    /// <summary>
    /// <c>GameMode.DeathInfo</c> (<c>ce 04 00</c>): <c>u32 rankIndex; u8 flag; i32 len + bytes
    /// killerName; u32 field4; u32 sourceId; u32 cause</c> = <b>24 + nameLen</b> bytes
    /// (3-byte header + 4 + 1 + (4 + nameLen) + 4 + 4 + 4), exact-length gated at
    /// <c>FUN_140bbb120:70</c> (reader <c>FUN_140bb0cc0</c>, docs/18 §3a). docs/18 §3/§3c's
    /// arithmetic "27 + len" was wrong — it double-counted the 3-byte header; the field table in
    /// the same section sums to 24 + len, and <c>GasPacketTests</c> pins 24 for an empty name.
    /// Because the handler is exact-length gated, writing 27 would make the client drop every
    /// <c>ce 04</c>.
    /// <para>
    /// <c>Rank</c> is a **0-based placement**: the handler displays <c>rankIndex + 1</c>.
    /// <c>SourceId == 0</c> makes the client derive the source name from <c>Cause</c>, which is the
    /// gas-death vector: <see cref="DeathCause.Gas"/>, <c>SourceId = 0</c>, empty killer name
    /// (docs/18 §5).
    /// </para>
    /// </summary>
    /// <param name="Rank">0-based placement (<c>+0x18</c>); the client shows <c>Rank + 1</c>.</param>
    /// <param name="Draw">The UI boolean at <c>+0x1c</c> (<c>DAT_143f6eb68</c>).</param>
    /// <param name="Killer">Killer display name (<c>+0x20</c>); empty for an environmental death.</param>
    /// <param name="Field4">
    /// <c>+0x38</c>, published straight to <c>DAT_143f6ea28</c>. Its meaning is unrecovered
    /// (docs/18 §3a), so it is deliberately named after its offset and not after a guess.
    /// </param>
    /// <param name="SourceId">
    /// <c>+0x3c</c> — a damage-**source definition** id, not an entity guid: non-zero makes the
    /// client walk its own table at <c>DAT_143f69eb0+0x1c8</c> (bucket <c>SourceId &amp; 0x3ff</c>)
    /// for a display name. Send 0 so the name is derived from <paramref name="Cause"/> instead.
    /// </param>
    /// <param name="Cause">The wire cause enum of <see cref="DeathCause"/> (<c>+0x40</c>).</param>
    public sealed record DeathInfo(
        int Rank = 0,
        bool Draw = false,
        string Killer = "",
        uint Field4 = 0,
        uint SourceId = 0,
        uint Cause = DeathCause.Gas)
    {
        /// <summary>
        /// Bytes written for an empty killer name: <c>3 + 4 + 1 + 4 + 4 + 4 + 4</c>.
        /// </summary>
        public const int BaseLength = 24;

        /// <summary>
        /// The gas death of a player whose 0-based placement is <paramref name="rankIndex"/> — the
        /// client shows <c>rankIndex + 1</c>.
        /// </summary>
        public static DeathInfo Gas(int rankIndex) =>
            new(Rank: Math.Max(0, rankIndex), Cause: DeathCause.Gas);

        /// <summary>
        /// Byte length this record writes: <see cref="BaseLength"/> plus the killer name's bytes.
        /// The handler is exact-length gated (<c>FUN_140bbb120:70</c>), so this is the number the
        /// client will accept — never docs/18 §3's "27 + len".
        /// </summary>
        public int Length => BaseLength + System.Text.Encoding.UTF8.GetByteCount(Killer);

        public void WriteTo(PacketWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteByte(Opcode);
            writer.WriteUInt16(DeathInfoSubOpcode);
            writer.WriteInt32(Rank);
            writer.WriteBool(Draw);
            writer.WriteString(Killer);
            writer.WriteUInt32(Field4);
            writer.WriteUInt32(SourceId);
            writer.WriteUInt32(Cause);
        }
    }

    /// <summary>
    /// <c>ClientUpdate.Hitpoints</c> (<c>11 01 00</c>): reader <c>FUN_140a357d0</c> reads
    /// <c>u8; u16; u32; u32</c> for 11 bytes total (docs/16 §4d) — i.e. it self-frames the packet's
    /// own <c>11 01 00</c> header exactly as <c>ce 02</c>'s reader does, leaving the two <c>u32</c>s
    /// as the payload. The outer handler (<c>FUN_140afc660</c> case 1) immediately computes
    /// <c>(field3 * 100) / field4</c> and feeds it to the health percentage, which is what anchors
    /// field 3 = current and field 4 = maximum.
    /// </summary>
    public sealed record Hitpoints(uint Current, uint Maximum)
    {
        public const byte Family = ZoneOpcodes.ClientUpdateBase;
        public const ushort SubOpcode = 0x0001;
        public const int Length = 11;

        public void WriteTo(PacketWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteByte(Family);
            writer.WriteUInt16(SubOpcode);
            writer.WriteUInt32(Current);
            writer.WriteUInt32(Maximum);
        }
    }

    /// <summary>
    /// <c>ClientUpdate.DamageInfo</c> (<c>11 1e 00</c>), reader <c>FUN_140a34ea0</c>:
    /// <c>u8; u16; u32; varint; u32×6; u8</c> (docs/15 §4, docs/16 §4e). The packet is proven to
    /// drive the local player's take-damage path — the case-<c>0x1e</c> handler resolves the actor
    /// with no guid argument at all and calls <c>vtable+0x9d8</c> on it — and it is the leading
    /// candidate for the periodic gas tick.
    ///
    /// **Unverified.** Which field is the damage amount, which is a resulting health, and which is
    /// an attacker/cause id is BLOCKED in both docs (the blocker is one live tick: research-gaps
    /// G-08). The whole packet is therefore gated by <see cref="GasSettings.SendDamageInfo"/>,
    /// default false, and every field is a separate parameter so a live run can drive one at a
    /// time. <see cref="GasSettings.DamageInfoRepeatsPrefix"/> switches between the two readings of
    /// the reader's leading <c>u8; u16</c> (this packet's own header, or a repeat of it as
    /// payload).
    /// </summary>
    public sealed record DamageInfo(
        uint Field3 = 0,
        uint Field4 = 0,
        uint Field5 = 0,
        uint Field6 = 0,
        uint Field7 = 0,
        uint Field8 = 0,
        uint Field9 = 0,
        uint Field10 = 0,
        byte Flags = 0)
    {
        public const byte Family = ZoneOpcodes.ClientUpdateBase;
        public const ushort SubOpcode = 0x001e;

        /// <summary>
        /// One gas tick with the damage in field 5 — the first <c>u32</c> after the varint and the
        /// first candidate the live experiment should try (docs/15 §9 step 3: "start with only one
        /// non-zero field at a time"). Unverified.
        /// </summary>
        public static DamageInfo GasTick(uint amount) => new(Field5: amount);

        public void WriteTo(PacketWriter writer, bool repeatPrefix = false)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteByte(Family);
            writer.WriteUInt16(SubOpcode);
            if (repeatPrefix)
            {
                // docs/15 §4's reading: the reader's u8/u16 are payload after the 3-byte header.
                writer.WriteByte(Family);
                writer.WriteUInt16(SubOpcode);
            }

            writer.WriteUInt32(Field3);
            ClientVarInt.Write(writer, Field4);
            writer.WriteUInt32(Field5);
            writer.WriteUInt32(Field6);
            writer.WriteUInt32(Field7);
            writer.WriteUInt32(Field8);
            writer.WriteUInt32(Field9);
            writer.WriteUInt32(Field10);
            writer.WriteByte(Flags);
        }

        public void WriteTo(PacketWriter writer, GasSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            WriteTo(writer, settings.DamageInfoRepeatsPrefix);
        }
    }
}
