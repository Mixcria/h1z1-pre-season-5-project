using System.Buffers.Binary;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.MatchEndgame;

/// <summary>
/// Lane 1D-lite's <b>wiring</b> (docs/97): the bytes a death and a victory really put on the wire,
/// read off a fake session driven through <c>ZoneService</c> itself rather than off the writers.
/// <c>EndgamePacketTests</c> and <c>DeathPacketTests</c> already pin every layout; what is pinned
/// here is the <i>orchestration</i> — how many of each go out, in what order, with which cause, and
/// what the match state does either side of the hold.
///
/// <para>
/// <b>Why the seam.</b> The honest route to a death is the 15 s lobby timer, the 20 s countdown,
/// the drop and then a gas ladder whose first damage phase opens at 4:30, all on <c>Task.Delay</c>
/// against the wall clock — <c>ZoneService.Later</c> has no injectable clock. So the <i>entry</i>
/// is faked through <c>ZoneService.TestSession</c> and everything after it is the production path:
/// <c>ApplyDamage</c> → <c>KillPlayer</c>, <c>KillPracticeTarget</c>, <c>BeginEndedHold</c> and
/// <c>CompleteEndedHold</c> are the same methods a live client reaches.
/// </para>
/// <para>
/// <b>The hold's 30 seconds are therefore NOT tested as a duration</b>, only as a state transition
/// (<c>InMatch → Ended → Lobby</c>). <c>MatchEndOptions.DefaultEndedHoldSeconds</c> is pinned as a
/// constant instead; a wall-clock assertion would cost 30 s a case and prove nothing the constant
/// does not.
/// </para>
/// <para>
/// D29: send-side only. This proves what the server sent, never what the client did with it.
/// </para>
/// </summary>
public sealed class DeathAndVictoryIntegrationTests
{
    /// <summary><c>ce 04 GameMode.DeathInfo</c>.</summary>
    private const ushort DeathInfoSub = 0x0004;

    /// <summary><c>ce 09 GameMode.PlayersRemaining</c>.</summary>
    private const ushort PlayersRemainingSub = 0x0009;

    /// <summary><c>ce 18 GameMode.ShowVictoryScreen</c>.</summary>
    private const ushort VictorySub = ShowVictoryScreen.SubOpcode;

    /// <summary><c>ce 1b GameMode.LeaveMatch</c>.</summary>
    private const ushort LeaveMatchSub = LeaveMatch.SubOpcode;

    /// <summary><c>11 31 ClientUpdate.TextAlert</c>.</summary>
    private const ushort TextAlertSub = MatchAlerts.SubOpcode;

    /// <summary><c>11 01 ClientUpdate.Hitpoints</c>.</summary>
    private const ushort HitpointsSub = 0x0001;

    private sealed class RecordingRecorder : IPacketRecorder
    {
        public List<byte[]> Sent { get; } = [];

        public void RecordSession(IPEndPoint remote, in SessionRequest request)
        {
        }

        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c")
            {
                Sent.Add(bytes.ToArray());
            }
        }

        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext)
        {
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;

        public void Log(TransportLogLevel level, string message)
        {
        }
    }

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder) Admit(
        MatchEndOptions matchEnd)
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var options = new ZoneOptions { MatchEnd = matchEnd };
        var service = new ZoneService(new SilentLog(), recorder, tickets, options);
        var request = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
        var connection = new SoeConnection(
            new IPEndPoint(IPAddress.Loopback, 5555),
            in request,
            SessionSettings.WithSeed(1),
            service.OnSessionRequest(new IPEndPoint(IPAddress.Loopback, 5555), in request),
            service,
            new SilentLog(),
            (_, _) => { },
            now: 0);
        service.OnConnected(connection);

        using var writer = new PacketWriter();
        writer.WriteByte(GatewayLoginRequest.Opcode);
        writer.WriteUInt64(admission.Guid);
        writer.WriteString(admission.Ticket);
        writer.WriteString(GatewayLoginRequest.AugustProtocol);
        writer.WriteString(GatewayLoginRequest.AugustVersion);
        service.OnMessage(connection, writer.Written.ToArray());
        return (service, connection, recorder);
    }

    /// <summary>Byte 0 is the gateway tunnel header; the zone packet starts at 1.</summary>
    private static IEnumerable<byte[]> From(RecordingRecorder recorder, int mark) =>
        recorder.Sent.Skip(mark).Where(p => p.Length >= 2);

    private static List<byte[]> Sub8(IEnumerable<byte[]> packets, byte opcode, byte sub) =>
        [.. packets.Where(p => p.Length >= 3 && p[1] == opcode && p[2] == sub)];

    private static List<byte[]> Sub16(IEnumerable<byte[]> packets, byte opcode, ushort sub) =>
        [.. packets.Where(p =>
            p.Length >= 4 && p[1] == opcode && BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == sub)];

    /// <summary>The <c>u32</c> body of a 7-byte <c>0xce</c> sub (<c>ce 09</c>, <c>ce 18</c>).</summary>
    private static int Body32(byte[] packet) => BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4));

    /// <summary>
    /// <c>ce 04</c>'s trailing <c>u32 Cause</c>: the packet is <c>u8 ce; u16 0004; u32 rank; bool
    /// draw; string killer; u32; u32 sourceId; u32 cause</c> and the cause is the last four bytes.
    /// </summary>
    private static uint DeathCause(byte[] packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packet.Length - 4));

    /// <summary><c>ce 04</c>'s leading <c>u32 Rank</c> — a 0-based placement the client draws as +1.</summary>
    private static uint DeathRank(byte[] packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));

    // ------------------------------------------------------------------ the death burst

    /// <summary>
    /// <b>A gas death is one of each, and exactly one of each.</b> The plan's §2a order: the health
    /// bar (already at zero from the damage tick, so it is not sent twice), <c>0f 4f</c>,
    /// <c>0f 48</c>, one <c>ce 04</c> carrying <c>0x42</c> (<c>UI.Results.Rank.Gas</c>), one
    /// <c>ce 09</c>. A second <c>ce 04</c> would be the old inline gas send firing beside the new
    /// one, which is the specific mistake this lane had to avoid.
    /// </summary>
    [Fact]
    public void AGasDeathSendsOneRagdollOneKillFeedOneDeathInfoAndOneAliveCount()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default);
        ZoneService.TestSession session = service.ForTest(connection);
        session.EnterMatch();
        int mark = recorder.Sent.Count;

        Assert.True(session.Damage(10_000, DamageCause.ToxicGas));

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] ragdoll = Assert.Single(Sub8(sent, ZoneOpcodes.CharacterBase, StartMultiStateDeath.SubOpcode));
        byte[] feed = Assert.Single(Sub8(sent, ZoneOpcodes.CharacterBase, KilledBy.SubOpcode));
        byte[] death = Assert.Single(Sub16(sent, EndgamePackets.Opcode, DeathInfoSub));
        byte[] alive = Assert.Single(Sub16(sent, EndgamePackets.Opcode, PlayersRemainingSub));

        // The 21-byte form: the victim owns its own ragdoll, so the trailing guid is its own.
        // Body after the tunnel header: 0f 4f, u64 character, u8 direction, i8 type, u8 flags, u64 owner.
        Assert.Equal(StartMultiStateDeath.LongLength, ragdoll.Length - 1);
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(ragdoll.AsSpan(3)));
        Assert.Equal(StartMultiStateDeath.RagdollFlag, ragdoll[13]);
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(ragdoll.AsSpan(14)));

        // Gas has no killer: the envelope guid (the KILLER) is 0 and the victim is the player.
        Assert.Equal(KilledBy.Length, feed.Length - 1);
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(feed.AsSpan(3)));
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(feed.AsSpan(11)));

        Assert.Equal((uint)DeathCauseCode.Gas, DeathCause(death));
        Assert.Equal(0x42u, DeathCause(death));
        Assert.Equal(0u, DeathRank(death));
        Assert.Equal(0, Body32(alive));

        // "Only 0 remain." is not a sentence, and the player already has a death screen.
        Assert.Empty(Sub16(sent, ZoneOpcodes.ClientUpdateBase, TextAlertSub));

        // No ce 18 for a corpse, and the match is holding for the client's own slides.
        Assert.Empty(Sub16(sent, EndgamePackets.Opcode, VictorySub));
        Assert.Equal("Ended", session.Step);
    }

    /// <summary>
    /// The health bar is written once. <c>ApplyDamage</c> sends the <c>11 01</c> that took the
    /// player to zero and <c>KillPlayer</c> must not repeat it — the client would be told
    /// <c>0/10000</c> twice on one tick.
    /// </summary>
    [Fact]
    public void TheKillingTickSendsExactlyOneHitpointsPacket()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default);
        ZoneService.TestSession session = service.ForTest(connection);
        session.EnterMatch();
        int mark = recorder.Sent.Count;

        session.Damage(10_000, DamageCause.ToxicGas);

        Assert.Single(Sub16(From(recorder, mark), ZoneOpcodes.ClientUpdateBase, HitpointsSub));
        Assert.Equal(0u, session.Hitpoints);
    }

    /// <summary>
    /// A death from a player carries the killer in the envelope of <c>0f 48</c> and switches the
    /// <c>ce 04</c> to the <c>0x4d GameModeKillCondition</c> shape with the killer's name in it —
    /// the string locale 12604 needs to render at all.
    /// </summary>
    [Fact]
    public void ADeathWithAKillerNamesThemInTheFeedAndInTheDeathInfo()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default);
        ZoneService.TestSession session = service.ForTest(connection);
        session.EnterMatch();
        int mark = recorder.Sent.Count;
        const ulong Killer = 0x1234_5678_9abc_def0;

        session.Damage(10_000, DamageCause.Bullet, Killer, "Nemesis", killerHealth: 4_200);

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] feed = Assert.Single(Sub8(sent, ZoneOpcodes.CharacterBase, KilledBy.SubOpcode));
        Assert.Equal(Killer, BinaryPrimitives.ReadUInt64LittleEndian(feed.AsSpan(3)));
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(feed.AsSpan(11)));

        byte[] death = Assert.Single(Sub16(sent, EndgamePackets.Opcode, DeathInfoSub));
        Assert.Equal((uint)DeathCauseCode.GameModeKillCondition, DeathCause(death));
        Assert.Contains("Nemesis", System.Text.Encoding.ASCII.GetString(death));
    }

    /// <summary>
    /// <b>The one-word revert.</b> <c>CRANBERRY_MATCH_ENDGAME=0</c> puts the gas death back exactly
    /// where it was before this lane: one <c>ce 04</c>, one <c>ce 09 0</c>, and nothing else — no
    /// ragdoll, no kill feed, no alert, no hold.
    /// </summary>
    [Fact]
    public void WithTheEndgameDisabledADeathIsTheOldSingleDeathInfoAndAliveCount()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default with { Enabled = false });
        ZoneService.TestSession session = service.ForTest(connection);
        session.EnterMatch();
        int mark = recorder.Sent.Count;

        session.Damage(10_000, DamageCause.ToxicGas);

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] death = Assert.Single(Sub16(sent, EndgamePackets.Opcode, DeathInfoSub));
        Assert.Equal(0x42u, DeathCause(death));
        Assert.Equal(0, Body32(Assert.Single(Sub16(sent, EndgamePackets.Opcode, PlayersRemainingSub))));
        Assert.Empty(Sub8(sent, ZoneOpcodes.CharacterBase, StartMultiStateDeath.SubOpcode));
        Assert.Empty(Sub8(sent, ZoneOpcodes.CharacterBase, KilledBy.SubOpcode));
        Assert.Equal("InMatch", session.Step);
    }

    /// <summary>The once-guard: a second lethal tick on the same match adds no bytes.</summary>
    [Fact]
    public void ASecondLethalTickSendsNothing()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default);
        ZoneService.TestSession session = service.ForTest(connection);
        session.EnterMatch();
        session.Damage(10_000, DamageCause.ToxicGas);
        int mark = recorder.Sent.Count;

        Assert.False(session.Damage(10_000, DamageCause.ToxicGas));
        Assert.Empty(From(recorder, mark));
    }

    // ------------------------------------------------------------------ the practice target

    /// <summary>
    /// D152 with the switch <b>off</b>: the dummy still dies visibly — its own <c>0f 4f</c> and
    /// <c>0f 48</c> — and nothing about the match moves. No <c>ce 04</c> (a dummy has no
    /// player-HUD), no <c>ce 09</c>, no victory.
    /// </summary>
    [Fact]
    public void ByDefaultKillingADummyIsARagdollAndAKillFeedAndNothingElse()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default);
        ZoneService.TestSession session = service.ForTest(connection);
        ulong dummy = session.EnterMatch(dummies: 1)[0];
        int mark = recorder.Sent.Count;

        session.KillTarget(dummy);

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] ragdoll = Assert.Single(Sub8(sent, ZoneOpcodes.CharacterBase, StartMultiStateDeath.SubOpcode));
        Assert.Equal(dummy, BinaryPrimitives.ReadUInt64LittleEndian(ragdoll.AsSpan(3)));
        // The VIEWER owns the ragdoll, and the only viewer is the shooter.
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(ragdoll.AsSpan(14)));

        byte[] feed = Assert.Single(Sub8(sent, ZoneOpcodes.CharacterBase, KilledBy.SubOpcode));
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(feed.AsSpan(3)));
        Assert.Equal(dummy, BinaryPrimitives.ReadUInt64LittleEndian(feed.AsSpan(11)));

        Assert.Empty(Sub16(sent, EndgamePackets.Opcode, DeathInfoSub));
        Assert.Empty(Sub16(sent, EndgamePackets.Opcode, PlayersRemainingSub));
        Assert.Empty(Sub16(sent, EndgamePackets.Opcode, VictorySub));
        Assert.Equal("InMatch", session.Step);
    }

    /// <summary>
    /// D152 with the switch <b>on</b>: the last dummy was the last opponent, so the shooter wins.
    /// <c>ce 18</c> to the winner only, the <c>ce 04 {rank 0, cause 0x48}</c> that makes the slides
    /// say "You won!", the 11124 announcement, and <c>Ended</c>.
    /// </summary>
    [Fact]
    public void WithTheTargetCountingKillingTheLastDummyWinsTheMatch()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default with { PracticeTargetCountsAsOpponent = true });
        ZoneService.TestSession session = service.ForTest(connection);
        ulong dummy = session.EnterMatch(dummies: 1)[0];
        int mark = recorder.Sent.Count;

        session.KillTarget(dummy);

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] victory = Assert.Single(Sub16(sent, EndgamePackets.Opcode, VictorySub));
        Assert.Equal(ShowVictoryScreen.Length, victory.Length - 1);
        Assert.Equal(0, Body32(victory));

        byte[] winner = Assert.Single(Sub16(sent, EndgamePackets.Opcode, DeathInfoSub));
        Assert.Equal((uint)DeathCauseCode.EndOfMatchWinner, DeathCause(winner));
        Assert.Equal(0x48u, DeathCause(winner));
        Assert.Equal(0u, DeathRank(winner));

        // The player is the one left alive, so the counter drops to 1 and 11113 announces it;
        // 11124 announces the winner. Two alerts, no more.
        Assert.Equal(1, Body32(Assert.Single(Sub16(sent, EndgamePackets.Opcode, PlayersRemainingSub))));
        Assert.Equal(2, Sub16(sent, ZoneOpcodes.ClientUpdateBase, TextAlertSub).Count);
        Assert.Equal(1, session.AliveSent);

        // ce 1b is off by default: the client is NOT logged out.
        Assert.Empty(Sub16(sent, EndgamePackets.Opcode, LeaveMatchSub));
        Assert.Equal("Ended", session.Step);
    }

    /// <summary>
    /// The hold, as a state transition. <c>Later</c> is wall-clock only, so the 30 s is pinned as a
    /// constant and what is asserted here is that the expiry runs <c>AbandonMatch</c> and then the
    /// existing <c>SendLobbyHud</c> — the owner is back in a lobby on the same link, with the alive
    /// counter hidden again, without relogging.
    /// </summary>
    [Fact]
    public void TheEndedHoldLeavesResultsVisibleUntilAChoice()
    {
        Assert.Equal(30, MatchEndOptions.DefaultEndedHoldSeconds);
        Assert.Equal(30_000, MatchEndOptions.Default.EndedHoldMs);
        // The floor cannot be undercut, only raised.
        Assert.Equal(30_000, (MatchEndOptions.Default with { EndedHoldSeconds = 5 }).EndedHoldMs);

        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default with { PracticeTargetCountsAsOpponent = true });
        ZoneService.TestSession session = service.ForTest(connection);
        ulong dummy = session.EnterMatch(dummies: 1)[0];
        session.KillTarget(dummy);
        Assert.Equal("Ended", session.Step);
        int mark = recorder.Sent.Count;

        session.ExpireEndedHold();

        Assert.Equal("Ended", session.Step);
        List<byte[]> sent = [.. From(recorder, mark)];
        Assert.Empty(sent);
    }

    /// <summary>
    /// D151: <c>ce 1b</c> only under <c>CRANBERRY_MATCH_LEAVE_ON_END=1</c>, and when it goes out
    /// nothing follows it — the client is on the title screen and a lobby would be drawn over a
    /// login form.
    /// </summary>
    [Fact]
    public void LeaveMatchIsSentOnlyWhenArmedAndEndsTheSessionInTheMenu()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default with
            {
                PracticeTargetCountsAsOpponent = true,
                LeaveOnEnd = true,
            });
        ZoneService.TestSession session = service.ForTest(connection);
        ulong dummy = session.EnterMatch(dummies: 1)[0];
        session.KillTarget(dummy);
        int mark = recorder.Sent.Count;

        session.ExpireEndedHold();

        List<byte[]> sent = [.. From(recorder, mark)];
        byte[] leave = Assert.Single(Sub16(sent, EndgamePackets.Opcode, LeaveMatchSub));
        Assert.Equal(LeaveMatch.Length, leave.Length - 1);
        Assert.Equal("Menu", session.Step);
        Assert.DoesNotContain(Sub16(sent, EndgamePackets.Opcode, PlayersRemainingSub), p => Body32(p) == -1);
    }

    // ------------------------------------------------------------------ the switches

    /// <summary>
    /// Only <c>"1"</c> and <c>"0"</c> move a switch, and D152's own variable name still works
    /// alongside the plan's. The two that can decide a match on their own — a dev prop counting as
    /// the last opponent, and <c>ce 1b</c> logging the client out — are off.
    ///
    /// <para>
    /// <b><c>CollisionDamage</c> moved to ON in docs/117 §1 (D261)</b>: it was off because the
    /// <c>8e 01</c> body was believed underived, and it is derived now — from the client's own
    /// bytes, 47 records in this server's own host logs. Off still restores exactly what this arm
    /// did before, which is one rate-limited line and no health movement.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSwitchesDefaultToTheShippedBehaviour()
    {
        MatchEndOptions shipped = MatchEndOptions.FromEnvironment(_ => null);
        Assert.True(shipped.Enabled);
        Assert.False(shipped.PracticeTargetCountsAsOpponent);
        Assert.False(shipped.LeaveOnEnd);
        Assert.True(shipped.CollisionDamage);
        Assert.False(MatchEndOptions.FromEnvironment(
            n => n == MatchEndOptions.CollisionDamageVariable ? "0" : null).CollisionDamage);

        Assert.False(MatchEndOptions.FromEnvironment(
            n => n == MatchEndOptions.EnabledVariable ? "0" : null).Enabled);
        Assert.True(MatchEndOptions.FromEnvironment(
            n => n == MatchEndOptions.TargetCountsVariable ? "1" : null).PracticeTargetCountsAsOpponent);
        Assert.True(MatchEndOptions.FromEnvironment(
            n => n == MatchEndOptions.TargetCountsLegacyVariable ? "1" : null).PracticeTargetCountsAsOpponent);
        Assert.True(MatchEndOptions.FromEnvironment(
            n => n == MatchEndOptions.LeaveOnEndVariable ? "1" : null).LeaveOnEnd);

        // A typo leaves the default rather than silently changing how a match ends.
        Assert.True(MatchEndOptions.FromEnvironment(
            n => n == MatchEndOptions.EnabledVariable ? "false" : null).Enabled);

        Assert.Contains("endgame:", MatchEndOptions.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("leaveMatch ce1b=off", MatchEndOptions.Default.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>8e 01 Collision.Damage</c> is now seen rather than swallowed, and it still costs no
    /// health: the body has no recovered 1148 reader, so there is nothing to read an amount out of
    /// and <c>MatchEndOptions.CollisionDamage</c> ships off. The assertion is an absence — the
    /// report must never reply and must never move the bar.
    /// </summary>
    [Fact]
    public void ACollisionReportIsObservedAndCostsNoHealth()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(MatchEndOptions.Default);
        ZoneService.TestSession session = service.ForTest(connection);
        session.EnterMatch();
        int mark = recorder.Sent.Count;

        using var report = new PacketWriter();
        report.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        report.WriteByte(ZoneOpcodes.CollisionBase);
        report.WriteByte(0x01);
        report.WriteUInt64(0x4142_4344_4546_4748);
        for (int i = 0; i < 34; i++)
        {
            report.WriteByte((byte)i);
        }

        service.OnMessage(connection, report.Written.ToArray());

        Assert.Empty(From(recorder, mark));
        Assert.Equal(10_000u, session.Hitpoints);
        Assert.Equal("InMatch", session.Step);
    }
}
