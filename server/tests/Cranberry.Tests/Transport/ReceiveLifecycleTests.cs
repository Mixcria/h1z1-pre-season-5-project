using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class ReceiveLifecycleTests
{
    private static readonly byte[] Key = [1, 2, 3, 4];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingInFirstMessageStopsRemainingBundleOrMultiChildren(bool multi)
    {
        var service = new ClosingService();
        var request = new SessionRequest(0, 42, 512, "Test");
        var connection = new SoeConnection(new IPEndPoint(IPAddress.Loopback, 1), in request,
            new(), SessionDecision.Clear, service, new SilentLog(), (_, _) => { }, 0);
        byte[] packet = multi
            ? [0, (byte)SoeOpcode.Multi, 2, 0x46, 1, 2, 0x46, 2]
            : [0, (byte)SoeOpcode.Data, 0, 0, 0, (byte)SoeOpcode.Bundle, 2, 0x46, 1, 2, 0x46, 2];

        connection.HandleDatagram(packet, 0);
        connection.HandleDatagram([0x46, 3], 1); // An already queued datagram cannot resurrect it.

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(1, service.Delivered);
        Assert.Equal(0, connection.PendingDatagrams);
    }

    [Fact]
    public async Task HandlerFailureClosesOnlyFaultedEncryptedSessionAndReconnectStartsFresh()
    {
        var service = new ThrowingEchoService();
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service,
            new SilentLog(), new() { EnableDiagnostics = true });
        listener.Start();
        using var failed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var healthy = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        failed.Connect(listener.LocalEndPoint);
        healthy.Connect(listener.LocalEndPoint);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Open(failed, 42, deadline.Token);
        await Open(healthy, 43, deadline.Token);

        byte[] fault = Encrypt([0xf0, 1]);
        await failed.SendAsync(fault, deadline.Token);
        byte[] close = (await failed.ReceiveAsync(deadline.Token)).Buffer;
        Assert.Equal((byte)SoeOpcode.Disconnect, close[1]);
        Assert.Equal(DisconnectReason.Application, DisconnectPacket.Parse(close.AsSpan(2)).Reason);
        await service.Disconnected.Task.WaitAsync(deadline.Token);

        await healthy.SendAsync(Encrypt([0x46, 2]), deadline.Token);
        Assert.Equal(new byte[] { 0x46, 2 }, await ReadEcho(healthy, deadline.Token));

        // A stale retry is never re-dispatched with the already advanced receive cipher.
        await failed.SendAsync(fault, deadline.Token);
        await Open(failed, 44, deadline.Token);
        await failed.SendAsync(Encrypt([0x46, 3]), deadline.Token);
        Assert.Equal(new byte[] { 0x46, 3 }, await ReadEcho(failed, deadline.Token));
        Assert.Equal(1, service.Failures);
        var captured = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Post(() => captured.SetResult(listener.CaptureDiagnostics()));
        var snapshot = JsonSerializer.SerializeToElement(await captured.Task.WaitAsync(deadline.Token));
        Assert.Equal(1, snapshot.GetProperty("counters").GetProperty("ServiceFailureClosures").GetInt64());
        Assert.Equal(3, snapshot.GetProperty("timings").GetProperty("applicationDispatchWork").GetProperty("Count").GetInt64());
        Assert.True(snapshot.GetProperty("timings").GetProperty("udpSendWork").GetProperty("Count").GetInt64() > 0);
    }

    private static async Task Open(UdpClient client, uint session, CancellationToken ct)
    {
        byte[] request = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, (byte)'T', 0];
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(6), session);
        await client.SendAsync(request, ct);
        Assert.Equal((byte)SoeOpcode.SessionReply, (await client.ReceiveAsync(ct)).Buffer[1]);
    }

    private static byte[] Encrypt(byte[] message)
    {
        byte[]? result = null;
        var outbound = new OutboundChannel(new(), bytes => result = bytes.ToArray());
        outbound.Send(message, new Rc4Cipher(Key), 0);
        outbound.Close();
        return result!;
    }

    private static async Task<byte[]> ReadEcho(UdpClient client, CancellationToken ct)
    {
        while (true)
        {
            byte[] packet = (await client.ReceiveAsync(ct)).Buffer;
            if (packet[1] == (byte)SoeOpcode.Ack) continue;
            Assert.Equal((byte)SoeOpcode.Data, packet[1]);
            byte[] body = packet[4..];
            if (body.Length > 1 && body[0] == 0 && body[1] == 0) body = body[1..];
            new Rc4Cipher(Key).Transform(body);
            return body;
        }
    }

    private sealed class ClosingService : ISoeService
    {
        public int Delivered;
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message)
        {
            Delivered++;
            connection.Disconnect();
        }
    }

    private sealed class ThrowingEchoService : ISoeService
    {
        public int Failures;
        public readonly TaskCompletionSource Disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Encrypted(Key);
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) => Disconnected.TrySetResult();
        public void OnMessage(SoeConnection connection, Span<byte> message)
        {
            if (message[0] == 0xf0) { Failures++; throw new InvalidOperationException("Synthetic handler failure"); }
            connection.Send(message);
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
}
