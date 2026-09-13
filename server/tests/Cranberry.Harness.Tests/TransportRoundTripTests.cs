using System.Net;
using Cranberry.Harness.Runtime;
using Cranberry.Harness.Soe;
using Cranberry.Harness.Wire;
using Cranberry.Transport;

namespace Cranberry.Harness.Tests;

/// <summary>
/// The harness's transport against the server's real <see cref="SoeListener"/>, in process. This
/// is where the "independent client half" claim is actually paid for: nothing below the
/// application message is shared, so a round trip here means the two implementations agree about
/// sequencing, fragmentation, acknowledgement, the bundle envelope, the leading-zero escape and
/// the RC4 arming point — rather than agreeing because they are the same code.
/// </summary>
public sealed class TransportRoundTripTests
{
    private static readonly byte[] Key = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");

    private static async Task<SoeClientSession> ConnectAsync(
        SoeListener listener, string protocol, byte[]? key, CancellationToken token) =>
        await SoeClientSession.OpenAsync(
            new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint.Port),
            new SoeClientOptions { ProtocolName = protocol, LinkName = protocol, Key = key },
            new HarnessClock(),
            new PacketJournal(200),
            _ => "test",
            token);

    private static async Task<byte[]> ReceiveAsync(SoeClientSession session, TimeSpan budget)
    {
        using var deadline = new CancellationTokenSource(budget);
        await foreach (InboundMessage message in session.Messages.ReadAllAsync(deadline.Token))
        {
            return message.Bytes;
        }

        throw new TimeoutException("no message arrived");
    }

    [Fact]
    public async Task Posted_actions_wake_an_idle_link_without_waiting_for_its_tick()
    {
        var service = new EchoService(Key, encryptFromStart: true);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, NullLog.Instance);
        listener.Start();
        await using var client = await SoeClientSession.OpenAsync(
            new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint.Port),
            new SoeClientOptions { ProtocolName = "LoginUdp_14", LinkName = "idle", Key = Key,
                IdleTickIntervalMs = 5000, TickIntervalMs = 5000 },
            new HarnessClock(), new PacketJournal(24), _ => "test", CancellationToken.None);
        await Task.Delay(100);
        for (int i = 0; i < 300; i++) client.Send([0x42, (byte)(i >> 8), (byte)i]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 300; i++)
        {
            InboundMessage message = await client.Messages.ReadAsync(deadline.Token);
            Assert.Equal(new byte[] { 0x42, (byte)(i >> 8), (byte)i }, message.Bytes);
        }
    }

    [Fact]
    public async Task A_small_message_round_trips_on_a_link_encrypted_from_the_first_byte()
    {
        var service = new EchoService(Key, encryptFromStart: true);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, NullLog.Instance);
        listener.Start();

        await using SoeClientSession client = await ConnectAsync(listener, "LoginUdp_14", Key, CancellationToken.None);
        Assert.NotNull(client.Parameters);
        Assert.Equal(0, client.Parameters!.CrcLength);

        client.Send([0x01, 0x02, 0x03, 0x04]);
        byte[] echoed = await ReceiveAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal("01020304", Convert.ToHexString(echoed));

        listener.Stop();
    }

    [Fact]
    public async Task A_message_far_larger_than_one_datagram_is_fragmented_and_reassembled_both_ways()
    {
        // The Z2 bootstrap sends a 1.2 MB ReferenceData blob as ~2,500 datagrams in one burst.
        var service = new EchoService(Key, encryptFromStart: true);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, NullLog.Instance);
        listener.Start();

        await using SoeClientSession client = await ConnectAsync(listener, "LoginUdp_14", Key, CancellationToken.None);

        byte[] big = new byte[200_000];
        Random.Shared.NextBytes(big);
        big[0] = 0x42;
        client.Send(big);

        byte[] echoed = await ReceiveAsync(client, TimeSpan.FromSeconds(30));
        Assert.Equal(big.Length, echoed.Length);
        Assert.Equal(Convert.ToHexString(big), Convert.ToHexString(echoed));

        listener.Stop();
    }

    [Fact]
    public async Task Many_messages_survive_the_leading_zero_escape_and_stay_in_order()
    {
        // About one ciphertext in 256 begins with a zero byte and is escaped by a clear 00, which
        // must not be confused with the 00 19 bundle marker and must not consume keystream. Five
        // hundred messages make that case certain rather than lucky.
        var service = new EchoService(Key, encryptFromStart: true);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, NullLog.Instance);
        listener.Start();

        await using SoeClientSession client = await ConnectAsync(listener, "LoginUdp_14", Key, CancellationToken.None);

        const int Count = 500;
        for (int i = 0; i < Count; i++)
        {
            var w = new WireWriter();
            w.U8(0x47).LeU32((uint)i).CountedString($"message {i}");
            client.Send(w.ToArray());
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        int received = 0;
        await foreach (InboundMessage message in client.Messages.ReadAllAsync(deadline.Token))
        {
            var r = new WireReader(message.Bytes);
            Assert.Equal(0x47, r.U8());
            Assert.Equal((uint)received, r.LeU32());
            Assert.Equal($"message {received}", r.CountedString());
            if (++received == Count)
            {
                break;
            }
        }

        Assert.Equal(Count, received);
        listener.Stop();
    }

    [Fact]
    public async Task A_bundle_of_several_messages_arrives_as_several_messages()
    {
        // The August client bundled its ServerListRequest and CharacterSelectInfoRequest into one
        // reliable payload (docs/71 §2); the chunk bodies are ciphertext with a running keystream.
        var service = new EchoService(Key, encryptFromStart: true);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, NullLog.Instance);
        listener.Start();

        await using SoeClientSession client = await ConnectAsync(listener, "LoginUdp_14", Key, CancellationToken.None);
        client.SendBundle([[0x0D], [0x0B]]);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<string>();
        await foreach (InboundMessage message in client.Messages.ReadAllAsync(deadline.Token))
        {
            seen.Add(Convert.ToHexString(message.Bytes));
            if (seen.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(["0D", "0B"], seen);
        Assert.Equal(2, service.MessagesReceived);
        listener.Stop();
    }

    [Fact]
    public async Task The_clear_request_then_arm_sequence_leaves_both_keystreams_at_position_zero()
    {
        // docs/71 §3, reproduced end to end: the client sends one clear message and arms RC4
        // immediately; the server installs the key after delivering that message and before
        // serialising its reply, so the reply must decrypt at inbound position 0 while the clear
        // request consumed no keystream at all.
        var service = new EchoService(Key, encryptFromStart: false);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, NullLog.Instance);
        listener.Start();

        await using SoeClientSession client =
            await ConnectAsync(listener, "ExternalGatewayApi_3", null, CancellationToken.None);
        Assert.False(client.EncryptionArmed);

        client.SendThenArmEncryption([0x01, 0xAA, 0xBB], Key);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        InboundMessage reply = await FirstAsync(client, deadline.Token);

        Assert.True(client.EncryptionArmed);
        Assert.True(reply.WasEncrypted);
        Assert.Equal(0, reply.KeystreamPosition);
        Assert.Equal("01AABB", Convert.ToHexString(reply.Bytes));

        listener.Stop();
    }

    [Fact]
    public async Task The_harness_refuses_a_session_reply_that_demands_a_CRC_it_has_no_evidence_for()
    {
        var service = new EchoService(Key, encryptFromStart: true);
        var options = new SoeListenerOptions
        {
            Settings = new SessionSettings { CrcLength = 2, CrcSeed = 7 },
        };
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, NullLog.Instance, options);
        listener.Start();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            ConnectAsync(listener, "LoginUdp_14", Key, CancellationToken.None));

        listener.Stop();
    }

    [Fact]
    public async Task Opening_a_session_against_nothing_times_out_with_a_message_that_names_the_link()
    {
        var options = new SoeClientOptions
        {
            ProtocolName = "LoginUdp_14",
            LinkName = "LoginUdp_14",
            SessionReplyTimeout = TimeSpan.FromMilliseconds(600),
        };

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            SoeClientSession.OpenAsync(
                new IPEndPoint(IPAddress.Loopback, 1),
                options,
                new HarnessClock(),
                new PacketJournal(10),
                _ => "test",
                CancellationToken.None));

        Assert.Contains("LoginUdp_14", failure.Message, StringComparison.Ordinal);
        Assert.Contains("SessionReply", failure.Message, StringComparison.Ordinal);
    }

    private static async Task<InboundMessage> FirstAsync(SoeClientSession session, CancellationToken token)
    {
        await foreach (InboundMessage message in session.Messages.ReadAllAsync(token))
        {
            return message;
        }

        throw new TimeoutException("no message arrived");
    }

    private sealed class EchoService(byte[] key, bool encryptFromStart) : ISoeService
    {
        private int _messages;

        public int MessagesReceived => _messages;

        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) =>
            encryptFromStart ? SessionDecision.Encrypted(key) : SessionDecision.Clear;

        public void OnConnected(SoeConnection connection)
        {
        }

        public void OnMessage(SoeConnection connection, Span<byte> message)
        {
            _messages++;
            byte[] copy = message.ToArray();
            if (!encryptFromStart && !connection.EncryptionEnabled)
            {
                connection.EnableEncryption(key);
            }

            connection.Send(copy);
        }

        public void OnDisconnected(SoeConnection connection, DisconnectCause cause)
        {
        }
    }

    private sealed class NullLog : ITransportLog
    {
        public static readonly NullLog Instance = new();

        public bool IsEnabled(TransportLogLevel level) => false;

        public void Log(TransportLogLevel level, string message)
        {
        }
    }
}
