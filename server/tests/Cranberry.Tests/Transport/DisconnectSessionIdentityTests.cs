using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class DisconnectSessionIdentityTests
{
    [Theory]
    [InlineData(43u, 42u, false)]
    [InlineData(0u, 42u, false)]
    [InlineData(42u, 0u, false)]
    [InlineData(uint.MaxValue, 0u, false)]
    [InlineData(43u, 42u, true)]
    [InlineData(0u, 42u, true)]
    public async Task MismatchedDisconnectPreservesCurrentSessionAndReliableProgress(uint currentId, uint staleId, bool multi)
    {
        var service = new TrackingService();
        var request = new SessionRequest(0, currentId, 512, "Test");
        var connection = new SoeConnection(new IPEndPoint(IPAddress.Loopback, 1), in request,
            new(), SessionDecision.Clear, service, new SilentLog(), (_, _) => { }, 0);
        try
        {
            connection.Send([0x46, 0xaa]);
            Assert.Equal(1, connection.PendingDatagrams);
            byte[] stale = Disconnect(staleId);
            byte[] incoming = multi ? [0, (byte)SoeOpcode.Multi, 8, .. stale] : stale;
            connection.HandleDatagram(incoming, 1);

            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Null(connection.PendingClose);
            Assert.Equal(1, connection.PendingDatagrams);
            connection.HandleDatagram([0, (byte)SoeOpcode.Data, 0, 0, 0x46, 0xbb], 2);
            Assert.True(service.Delivered.Task.IsCompletedSuccessfully);
            Assert.Equal(currentId, await service.Delivered.Task);
            Assert.Equal(new byte[] { 0x46, 0xbb }, Assert.Single(service.Messages));
            connection.HandleDatagram([0, (byte)SoeOpcode.Ack, 0, 0], 3);
            Assert.Equal(0, connection.PendingDatagrams);

            connection.HandleDatagram(Disconnect(currentId), 4);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(DisconnectCause.PeerRequested, connection.PendingClose);
        }
        finally { connection.Close(DisconnectCause.ServerRequested); }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(42u)]
    [InlineData(uint.MaxValue)]
    public void MatchingDisconnectIncludingZeroStillCloses(uint sessionId)
    {
        var request = new SessionRequest(0, sessionId, 512, "Test");
        var connection = new SoeConnection(new IPEndPoint(IPAddress.Loopback, 1), in request,
            new(), SessionDecision.Clear, new TrackingService(), new SilentLog(), (_, _) => { }, 0);
        try
        {
            connection.Send([0x46, 1]);
            connection.HandleDatagram(Disconnect(sessionId), 1);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(DisconnectCause.PeerRequested, connection.PendingClose);
            Assert.Equal(0, connection.PendingDatagrams);
        }
        finally { connection.Close(DisconnectCause.ServerRequested); }
    }

    [Theory]
    [InlineData(42u, 43u)]
    [InlineData(42u, 0u)]
    [InlineData(0u, 42u)]
    public async Task EndpointReuseIgnoresDelayedOldDisconnectAndDeliversNewReliableData(uint oldId, uint newId)
    {
        var service = new TrackingService();
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(listener.LocalEndPoint);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Open(client, oldId, deadline.Token);
        await Open(client, newId, deadline.Token);
        Assert.Contains((oldId, DisconnectCause.Replaced), service.Closed);

        // Both children share one packet so the late disconnect necessarily precedes
        // the new reliable message. No sleep or assumption about separate UDP ordering.
        byte[] packet = [0, (byte)SoeOpcode.Multi, 8, .. Disconnect(oldId),
            6, 0, (byte)SoeOpcode.Data, 0, 0, 0x46, 0xcc];
        await client.SendAsync(packet, deadline.Token);
        Task first = await Task.WhenAny(service.Delivered.Task, service.PeerClosed.Task).WaitAsync(deadline.Token);
        Assert.Same(service.Delivered.Task, first);
        Assert.Equal(newId, await service.Delivered.Task);
        Assert.Equal(new byte[] { 0x46, 0xcc }, Assert.Single(service.Messages));
        Assert.DoesNotContain((newId, DisconnectCause.PeerRequested), service.Closed);

        await client.SendAsync(Disconnect(newId), deadline.Token);
        Assert.Equal(newId, await service.PeerClosed.Task.WaitAsync(deadline.Token));
    }

    private static byte[] Disconnect(uint sessionId)
    {
        byte[] bytes = new byte[DisconnectPacket.Length];
        new DisconnectPacket(sessionId, DisconnectReason.Application).Write(bytes);
        return bytes;
    }

    private static async Task Open(UdpClient client, uint sessionId, CancellationToken token)
    {
        byte[] bytes = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, (byte)'T', 0];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), sessionId);
        await client.SendAsync(bytes, token);
        byte[] reply = (await client.ReceiveAsync(token)).Buffer;
        Assert.Equal((byte)SoeOpcode.SessionReply, reply[1]);
        Assert.Equal(sessionId, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(2)));
    }

    private sealed class TrackingService : ISoeService
    {
        public readonly ConcurrentQueue<byte[]> Messages = new();
        public readonly ConcurrentQueue<(uint, DisconnectCause)> Closed = new();
        public readonly TaskCompletionSource<uint> Delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<uint> PeerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnMessage(SoeConnection connection, Span<byte> message)
        {
            Messages.Enqueue(message.ToArray());
            Delivered.TrySetResult(connection.SessionId);
        }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause)
        {
            Closed.Enqueue((connection.SessionId, cause));
            if (cause == DisconnectCause.PeerRequested) PeerClosed.TrySetResult(connection.SessionId);
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
}
