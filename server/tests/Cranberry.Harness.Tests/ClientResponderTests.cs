using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Tests;

/// <summary>
/// The state machine on its own, with no sockets. These are the tests that decide whether the
/// harness is worth trusting: they check that the modelled client only advances when the server
/// actually delivered the preconditions the captures show it needs, and that when it does not
/// advance the harness can say which precondition was missing.
/// </summary>
public sealed class ClientResponderTests
{
    private const ulong Self = 0x1003;

    private static ClientResponder NewResponder(ClientTimings? timings = null) =>
        new(timings ?? new ClientTimings(), Self, seed: 1234);

    private static ObservedPacket Server(byte[] zoneBytes) =>
        ObservedPacket.ParseGateway([GatewayWire.Header(GatewayWire.OpcodeTunnelToClient, 0), .. zoneBytes]);

    private static byte[] ZoneDetails(string name, uint type)
    {
        var w = new WireWriter();
        w.U8(ZoneWire.SendZoneDetails).CountedString(name).LeU32(type).U8(0);
        return w.ToArray();
    }

    private static byte[] SelfRecord() => [ZoneWire.SendSelfToClient, 0, 0, 0, 0];

    private static byte[] ZoneDone() => [ZoneWire.ZoneDoneSendingInitialData, 0];

    private static byte[] BeginZoning() => [ZoneWire.ClientBeginZoning, 0, 0];

    /// <summary>Drains everything the responder owes up to <paramref name="until"/>.</summary>
    private static List<ScheduledMessage> DrainTo(ClientResponder responder, TimeSpan until, TimeSpan step)
    {
        var all = new List<ScheduledMessage>();
        for (TimeSpan t = TimeSpan.Zero; t <= until; t += step)
        {
            all.AddRange(responder.Drain(t));
        }

        return all;
    }

    private static bool Sent(IEnumerable<ScheduledMessage> messages, HarnessMilestone milestone) =>
        messages.Any(m => m.Milestone == milestone);

    private static ScheduledMessage? First(IEnumerable<ScheduledMessage> messages, HarnessMilestone milestone) =>
        messages.FirstOrDefault(m => m.Milestone == milestone);

    [Fact]
    public void A_complete_menu_bootstrap_produces_ClientIsReady_1615_ms_after_0x57()
    {
        ClientResponder responder = NewResponder();
        responder.EnterGateway(TimeSpan.Zero);

        responder.OnServerMessage(Server(ZoneDetails("Z1", 4)), TimeSpan.Zero);
        responder.OnServerMessage(Server(SelfRecord()), TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDone()), TimeSpan.Zero);

        List<ScheduledMessage> sent = DrainTo(responder, TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(5));

        ScheduledMessage? tag = First(sent, HarnessMilestone.Menu0x57Sent);
        ScheduledMessage? ready = First(sent, HarnessMilestone.MenuClientIsReadySent);
        Assert.NotNull(tag);
        Assert.NotNull(ready);

        TimeSpan gap = ready.DueAt - tag.DueAt;
        Assert.InRange(gap.TotalMilliseconds, 1590, 1630);
        Assert.Empty(responder.MissingPreconditions());
        Assert.Equal("Z1", responder.ZoneName);
        Assert.Equal(4u, responder.ZoneType);
    }

    [Fact]
    public void The_menu_sequence_is_in_the_order_the_client_used()
    {
        ClientResponder responder = NewResponder();
        responder.EnterGateway(TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDetails("Z1", 4)), TimeSpan.Zero);
        responder.OnServerMessage(Server(SelfRecord()), TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDone()), TimeSpan.Zero);

        List<ScheduledMessage> sent = DrainTo(responder, TimeSpan.FromSeconds(12), TimeSpan.FromMilliseconds(5));
        HarnessMilestone[] order =
        [
            HarnessMilestone.MenuPingInfoLogSent,
            HarnessMilestone.MenuSetLocaleSent,
            HarnessMilestone.Menu0x57Sent,
            HarnessMilestone.MenuClientIsReadySent,
            HarnessMilestone.MenuLobbyGameDefinitionRequestSent,
            HarnessMilestone.MenuClientFinishedLoadingSent,
            HarnessMilestone.LoadingScreenClosed,
            HarnessMilestone.MenuBurstSent,
        ];

        int cursor = -1;
        foreach (HarnessMilestone milestone in order)
        {
            int index = sent.FindIndex(m => m.Milestone == milestone);
            Assert.True(index >= 0, $"{milestone} was never sent");
            Assert.True(index > cursor, $"{milestone} came out of order");
            cursor = index;
        }
    }

    [Fact]
    public void Without_a_self_record_the_client_never_becomes_ready_and_says_so()
    {
        ClientResponder responder = NewResponder();
        responder.EnterGateway(TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDetails("Z1", 4)), TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDone()), TimeSpan.Zero);

        List<ScheduledMessage> sent = DrainTo(responder, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));

        Assert.False(Sent(sent, HarnessMilestone.MenuClientIsReadySent));
        Assert.Contains(responder.MissingPreconditions(), m => m.Contains("SendSelfToClient", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_done_marker_the_client_never_starts_the_bootstrap_at_all()
    {
        ClientResponder responder = NewResponder();
        responder.EnterGateway(TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDetails("Z1", 4)), TimeSpan.Zero);
        responder.OnServerMessage(Server(SelfRecord()), TimeSpan.Zero);

        List<ScheduledMessage> sent = DrainTo(responder, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));

        Assert.False(Sent(sent, HarnessMilestone.MenuPingInfoLogSent));
        Assert.False(Sent(sent, HarnessMilestone.MenuClientIsReadySent));
        Assert.Contains(responder.MissingPreconditions(), m => m.Contains("ZoneDoneSendingInitialData", StringComparison.Ordinal));
    }

    [Fact]
    public void A_world_type_other_than_four_is_reported_as_a_missing_precondition()
    {
        ClientResponder responder = NewResponder();
        responder.EnterGateway(TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDetails("Z1", 1)), TimeSpan.Zero);

        Assert.Contains(responder.MissingPreconditions(), m => m.Contains("world type 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Zoning_produces_ClientIsReady_inside_the_observed_window()
    {
        ClientResponder responder = ReadyInMenu(out TimeSpan menuEnd);

        TimeSpan begunAt = menuEnd;
        responder.OnServerMessage(Server(BeginZoning()), begunAt);
        responder.OnServerMessage(Server(SelfRecord()), begunAt + TimeSpan.FromMilliseconds(400));
        responder.OnServerMessage(Server(ZoneDone()), begunAt + TimeSpan.FromMilliseconds(420));

        List<ScheduledMessage> sent = [];
        for (TimeSpan t = begunAt; t <= begunAt + TimeSpan.FromSeconds(8); t += TimeSpan.FromMilliseconds(5))
        {
            sent.AddRange(responder.Drain(t));
        }

        ScheduledMessage? ready = First(sent, HarnessMilestone.ZoningClientIsReadySent);
        Assert.NotNull(ready);
        double offsetMs = (ready.DueAt - begunAt).TotalMilliseconds;
        Assert.InRange(offsetMs, 1970, 2926);

        Assert.True(Sent(sent, HarnessMilestone.ZoningFirstMovementSent));
        Assert.True(Sent(sent, HarnessMilestone.ZoningClientFinishedLoadingSent));
        Assert.True(Sent(sent, HarnessMilestone.ZoningMovementResumed));
    }

    [Fact]
    public void A_zoning_burst_with_no_done_marker_reproduces_the_hung_client_fingerprint()
    {
        // docs/71 §12.1: a hung client still sends WallOfData x7, 0x57 and exactly one channel-2
        // packet. What it never sends is ClientIsReady, ClientFinishedLoading or a SECOND
        // channel-2 packet. The responder must show the same shape when the server's burst is
        // incomplete, or an F1 failure would not look like the real thing.
        ClientResponder responder = ReadyInMenu(out TimeSpan menuEnd);
        TimeSpan begunAt = menuEnd;
        responder.OnServerMessage(Server(BeginZoning()), begunAt);
        responder.OnServerMessage(Server(SelfRecord()), begunAt + TimeSpan.FromMilliseconds(400));

        List<ScheduledMessage> sent = [];
        for (TimeSpan t = begunAt; t <= begunAt + TimeSpan.FromSeconds(30); t += TimeSpan.FromMilliseconds(20))
        {
            sent.AddRange(responder.Drain(t));
        }

        Assert.True(Sent(sent, HarnessMilestone.Zoning0x57Sent));
        Assert.True(Sent(sent, HarnessMilestone.ZoningFirstMovementSent));
        Assert.False(Sent(sent, HarnessMilestone.ZoningClientIsReadySent));
        Assert.False(Sent(sent, HarnessMilestone.ZoningClientFinishedLoadingSent));
        Assert.False(Sent(sent, HarnessMilestone.ZoningMovementResumed));
        Assert.Single(sent, m => m.Description.StartsWith("ch2 PlayerMovement (the single", StringComparison.Ordinal));
        Assert.Contains(responder.MissingPreconditions(), m => m.Contains("ZoneDoneSendingInitialData", StringComparison.Ordinal));
    }

    [Fact]
    public void The_transfer_request_is_re_sent_on_the_clients_own_timer_not_on_a_reply()
    {
        ClientResponder responder = NewResponder();
        responder.EnterGateway(TimeSpan.Zero);
        responder.ClickPlay(TimeSpan.FromSeconds(10));

        List<ScheduledMessage> sent = [];
        for (TimeSpan t = TimeSpan.FromSeconds(10); t <= TimeSpan.FromSeconds(20); t += TimeSpan.FromMilliseconds(10))
        {
            sent.AddRange(responder.Drain(t));
        }

        ScheduledMessage? first = First(sent, HarnessMilestone.TransferRequestSent);
        ScheduledMessage? second = First(sent, HarnessMilestone.TransferRequestResent);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.InRange((second.DueAt - first.DueAt).TotalMilliseconds, 4514, 4564);
        Assert.Equal(Convert.ToHexString(first.Bytes), Convert.ToHexString(second.Bytes));
    }

    [Fact]
    public void The_AutoMount_echo_waits_for_the_teleport_ack_and_the_06_02_01()
    {
        ClientResponder responder = ReadyInMenu(out TimeSpan menuEnd);
        TimeSpan t0 = menuEnd;

        byte[] autoMount = Convert.FromHexString("05881903200000000000000100000000");
        responder.OnServerMessage(ObservedPacket.ParseGateway(autoMount), t0);

        // The AutoMount alone must not produce an echo: it is ordered behind the handshake.
        List<ScheduledMessage> beforeHandshake = [];
        for (TimeSpan t = t0; t <= t0 + TimeSpan.FromSeconds(5); t += TimeSpan.FromMilliseconds(20))
        {
            beforeHandshake.AddRange(responder.Drain(t));
        }

        Assert.False(Sent(beforeHandshake, HarnessMilestone.AutoMountEchoSent));

        TimeSpan teleportAt = t0 + TimeSpan.FromSeconds(5);
        responder.OnServerMessage(ObservedPacket.ParseGateway(Convert.FromHexString("05E80300")), teleportAt);

        List<ScheduledMessage> after = [];
        for (TimeSpan t = teleportAt; t <= teleportAt + TimeSpan.FromSeconds(10); t += TimeSpan.FromMilliseconds(10))
        {
            after.AddRange(responder.Drain(t));
        }

        ScheduledMessage? ack = First(after, HarnessMilestone.TeleportAckSent);
        ScheduledMessage? loaded = First(after, HarnessMilestone.MountedClientFinishedLoadingSent);
        ScheduledMessage? echo = First(after, HarnessMilestone.AutoMountEchoSent);
        Assert.NotNull(ack);
        Assert.NotNull(loaded);
        Assert.NotNull(echo);
        Assert.True(ack.DueAt < loaded.DueAt);
        Assert.True(loaded.DueAt <= echo.DueAt);
        Assert.Equal("06881903200000000000000000000000", Convert.ToHexString(echo.Bytes));
        Assert.Equal("060201", Convert.ToHexString(loaded.Bytes));
    }

    [Fact]
    public void Each_match_waits_for_its_own_teleport_before_echoing_its_new_canopy()
    {
        ClientResponder responder = ReadyInMenu(out TimeSpan start);
        for (byte round = 0; round < 3; round++)
        {
            responder.OnServerMessage(Server(BeginZoning()), start);
            byte[] mount = Convert.FromHexString("05881903200000000000000100000000");
            mount[3] += round;
            responder.OnServerMessage(ObservedPacket.ParseGateway(mount), start);
            var before = new List<ScheduledMessage>();
            for (TimeSpan t = start; t < start + TimeSpan.FromSeconds(2); t += TimeSpan.FromMilliseconds(10))
                before.AddRange(responder.Drain(t));
            Assert.DoesNotContain(before, m => m.Milestone == HarnessMilestone.AutoMountEchoSent);

            TimeSpan teleport = start + TimeSpan.FromSeconds(2);
            responder.OnServerMessage(ObservedPacket.ParseGateway(Convert.FromHexString("05E80300")), teleport);
            var after = new List<ScheduledMessage>();
            for (TimeSpan t = teleport; t <= start + TimeSpan.FromSeconds(12); t += TimeSpan.FromMilliseconds(10))
                after.AddRange(responder.Drain(t));
            var echo = Assert.Single(after, m => m.Milestone == HarnessMilestone.AutoMountEchoSent);
            var loaded = Assert.Single(after, m => m.Milestone == HarnessMilestone.MountedClientFinishedLoadingSent);
            Assert.True(loaded.DueAt <= echo.DueAt);
            byte[] expected = Convert.FromHexString("06881903200000000000000000000000");
            expected[3] += round;
            Assert.Equal(expected, echo.Bytes);
            start += TimeSpan.FromSeconds(15);
        }
    }

    [Fact]
    public void The_free_running_timers_keep_their_observed_periods()
    {
        ClientResponder responder = ReadyInMenu(out TimeSpan menuEnd);

        var keepAlives = new List<TimeSpan>();
        var gameTimeSyncs = new List<TimeSpan>();
        for (TimeSpan t = menuEnd; t <= menuEnd + TimeSpan.FromSeconds(40); t += TimeSpan.FromMilliseconds(20))
        {
            foreach (ScheduledMessage message in responder.Drain(t))
            {
                if (message.Milestone == HarnessMilestone.KeepAlivePairSent)
                {
                    keepAlives.Add(message.DueAt);
                }
                else if (message.Milestone == HarnessMilestone.GameTimeSyncSent)
                {
                    gameTimeSyncs.Add(message.DueAt);
                }
            }
        }

        Assert.True(keepAlives.Count >= 30, $"only {keepAlives.Count} KeepAlives in 40 s");
        for (int i = 1; i < keepAlives.Count; i++)
        {
            Assert.InRange((keepAlives[i] - keepAlives[i - 1]).TotalMilliseconds, 990, 1015);
        }

        Assert.True(gameTimeSyncs.Count >= 3);
        for (int i = 1; i < gameTimeSyncs.Count; i++)
        {
            Assert.InRange((gameTimeSyncs[i] - gameTimeSyncs[i - 1]).TotalMilliseconds, 10980, 11020);
        }
    }

    [Fact]
    public void Movement_packets_are_replayed_bytes_and_never_synthesised()
    {
        var pool = new MovementReplay([[0x46, 1, 2], [0x46, 3, 4]]);
        var responder = new ClientResponder(new ClientTimings(), Self, pool, null, seed: 7);
        responder.EnterGateway(TimeSpan.Zero);

        Assert.Equal("460102", Convert.ToHexString(pool.Next()));
        Assert.Equal("460304", Convert.ToHexString(pool.Next()));
        Assert.Equal("460102", Convert.ToHexString(pool.Next()));
        Assert.Equal(2, pool.Count);
        Assert.Equal(ClientPhase.MenuBootstrap, responder.Phase);
    }

    private static ClientResponder ReadyInMenu(out TimeSpan menuEnd)
    {
        ClientResponder responder = NewResponder();
        responder.EnterGateway(TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDetails("Z1", 4)), TimeSpan.Zero);
        responder.OnServerMessage(Server(SelfRecord()), TimeSpan.Zero);
        responder.OnServerMessage(Server(ZoneDone()), TimeSpan.Zero);

        menuEnd = TimeSpan.FromSeconds(12);
        for (TimeSpan t = TimeSpan.Zero; t <= menuEnd; t += TimeSpan.FromMilliseconds(5))
        {
            responder.Drain(t);
        }

        return responder;
    }
}
