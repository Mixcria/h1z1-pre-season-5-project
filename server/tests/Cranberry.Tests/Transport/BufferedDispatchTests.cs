using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Cranberry.Transport;
using Xunit.Abstractions;

namespace Cranberry.Tests.Transport;

public sealed class BufferedDispatchTests(ITestOutputHelper output)
{
    private static readonly byte[] Request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];
    private sealed class Service(bool encrypted) : ISoeService
    {
        public static readonly byte[] Key = [1, 2, 3, 4, 5, 6, 7, 8];
        public SoeConnection? Connection;
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) =>
            encrypted ? new SessionDecision(true, Key, true) : SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) => Connection = connection;
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message)
        {
            // Two events produced by one dispatch still share a single reliable bundle.
            connection.SendBuffered([0x46, 1]);
            connection.SendBuffered([0x46, 2]);
        }
    }
    private sealed class TestLog : ITransportLog
    {
        public readonly ConcurrentQueue<string> Errors = new();
        public bool IsEnabled(TransportLogLevel level) => true;
        public void Log(TransportLogLevel level, string message)
        { if (level == TransportLogLevel.Error) Errors.Enqueue(message); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task GameplayBatchLeavesBeforeMaintenanceAndRetainsCipherOrder(bool local, bool posted, bool encrypted)
    {
        var log = new TestLog(); var service = new Service(encrypted);
        using var listener = new SoeListener(new(IPAddress.Loopback, 0), service, log,
            new() { TickIntervalMs = 5000 });
        listener.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Connect(listener.LocalEndPoint);
        await using var peer = local ? listener.OpenLocalPeer((IPEndPoint)udp.Client.LocalEndPoint!) : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        async Task Send(byte[] bytes)
        {
            if (peer is not null) peer.Send(bytes);
            else await udp.SendAsync(bytes.AsMemory(), deadline.Token);
        }
        async Task<byte[]> Receive() => peer is not null ? await peer.ReceiveAsync(deadline.Token)
            : (await udp.ReceiveAsync(deadline.Token)).Buffer;
        await Send(Request);
        Assert.Equal((byte)SoeOpcode.SessionReply, (await Receive())[1]);
        var decrypt = encrypted ? new Rc4Cipher(Service.Key) : null;
        for (int round = 0; round < 3; round++)
        {
            var elapsed = Stopwatch.StartNew();
            if (posted) listener.Post(() => service.OnMessage(service.Connection!, []));
            else await Send([0x46, 0xAB]);
            byte[] packet = await Receive();
            output.WriteLine($"local={local}, posted={posted}, encrypted={encrypted}, round={round}: {elapsed.Elapsed.TotalMilliseconds:F3} ms");
            Assert.Equal((byte)SoeOpcode.Data, packet[1]);
            Assert.Equal(round, (packet[2] << 8) | packet[3]);
            byte[] payload = packet[4..];
            Assert.Equal(new byte[] { 0, (byte)SoeOpcode.Bundle, 2 }, payload[..3]);
            // Bundle framing is clear; each payload consumes the next bytes of one RC4 stream.
            byte[] first = payload[3..5], second = payload[6..8];
            decrypt?.Transform(first); decrypt?.Transform(second);
            Assert.Equal(new byte[] { 0x46, 1 }, first);
            Assert.Equal(2, payload[5]);
            Assert.Equal(new byte[] { 0x46, 2 }, second);
            Assert.Equal(8, payload.Length);
            await Send([0, (byte)SoeOpcode.Ack, 0, (byte)round]);
        }
        Assert.Empty(log.Errors);
    }
}
