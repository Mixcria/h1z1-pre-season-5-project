using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public class ZoneServiceTests
{
    private sealed class CaptureRecorder : IPacketRecorder
    {
        private readonly object _gate = new();

        public List<string> Sessions { get; } = [];

        public List<byte[]> Messages { get; } = [];

        public void RecordSession(IPEndPoint remote, in SessionRequest request)
        {
            lock (_gate)
            {
                Sessions.Add(request.ProtocolName);
            }
        }

        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                Messages.Add(bytes.ToArray());
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

    [Fact]
    public void AcceptsOnlyTheExactAugustGatewayProtocol()
    {
        var recorder = new CaptureRecorder();
        var service = new ZoneService(new SilentLog(), recorder);
        var remote = new IPEndPoint(IPAddress.Loopback, 12345);
        var exact = new SessionRequest(3, 1, 512, ZoneService.ProtocolName);
        var lookalike = new SessionRequest(3, 2, 512, "ExternalGatewayApi_4");

        SessionDecision accepted = service.OnSessionRequest(remote, in exact);
        SessionDecision refused = service.OnSessionRequest(remote, in lookalike);

        Assert.True(accepted.Accept);
        Assert.Null(accepted.Key);
        Assert.False(accepted.EncryptFromStart);
        Assert.False(refused.Accept);
        Assert.Equal([ZoneService.ProtocolName, "ExternalGatewayApi_4"], recorder.Sessions);
    }

    [Fact]
    public void GatewayHandshakeAndUnknownMessageAreCapturedOverLoopback()
    {
        var recorder = new CaptureRecorder();
        var service = new ZoneService(new SilentLog(), recorder);
        using var listener = new SoeListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            service,
            new SilentLog());
        listener.Start();

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 3000;
        byte[] request = MakeSessionRequest(0x12345678, ZoneService.ProtocolName);
        client.Send(request, request.Length, listener.LocalEndPoint);

        IPEndPoint from = new(IPAddress.Any, 0);
        byte[] reply = client.Receive(ref from);
        Assert.Equal(SessionReply.Length, reply.Length);
        Assert.Equal((ushort)SoeOpcode.SessionReply, BinaryPrimitives.ReadUInt16BigEndian(reply));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(2)));

        byte[] message = [0xA5, 0x01, 0x02, 0x03];
        byte[] reliable = [0x00, (byte)SoeOpcode.Data, 0x00, 0x00, .. message];
        client.Send(reliable, reliable.Length, listener.LocalEndPoint);

        byte[] ack = client.Receive(ref from);
        Assert.Equal(new byte[] { 0x00, (byte)SoeOpcode.Ack, 0x00, 0x00 }, ack);
        Assert.True(SpinWait.SpinUntil(() => recorder.Messages.Count == 1, 2000));
        Assert.Equal(message, recorder.Messages[0]);
    }

    [Fact]
    public void GatewayNetStatusRequestGetsCompleteFortyTwoByteReply()
    {
        var service = new ZoneService(new SilentLog(), new CaptureRecorder());
        using var listener = new SoeListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            service,
            new SilentLog());
        listener.Start();

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 3000;
        byte[] request = MakeSessionRequest(0x89ABCDEF, ZoneService.ProtocolName);
        client.Send(request, request.Length, listener.LocalEndPoint);

        IPEndPoint from = new(IPAddress.Any, 0);
        _ = client.Receive(ref from);

        byte[] netStatusRequest = [0x00, (byte)SoeOpcode.NetStatusRequest, 0x12, 0x34];
        client.Send(netStatusRequest, netStatusRequest.Length, listener.LocalEndPoint);
        byte[] reply = client.Receive(ref from);

        Assert.Equal(42, reply.Length);
        Assert.Equal((ushort)SoeOpcode.NetStatusReply, BinaryPrimitives.ReadUInt16BigEndian(reply));
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)));
    }

    private static byte[] MakeSessionRequest(uint sessionId, string protocol)
    {
        byte[] name = System.Text.Encoding.ASCII.GetBytes(protocol);
        byte[] request = new byte[2 + 12 + name.Length + 1];
        BinaryPrimitives.WriteUInt16BigEndian(request, (ushort)SoeOpcode.SessionRequest);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(2), 3);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(6), sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(10), 512);
        name.CopyTo(request.AsSpan(14));
        return request;
    }
}
