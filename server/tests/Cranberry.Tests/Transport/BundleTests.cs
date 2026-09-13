using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

/// <summary>
/// A reliable payload that starts with the bundle marker (00 19) carries several messages, each
/// prefixed by its length. On an encrypted link the marker and prefixes are clear and only the
/// message bodies are ciphertext, with one keystream running across all of them — exactly what
/// the August client sends after LoginReply (ServerListRequest + CharacterSelectInfoRequest).
/// </summary>
public class BundleTests
{
    private static readonly byte[] Key = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");

    private sealed class RecordingService : ISoeService
    {
        public List<string> Messages { get; } = new();

        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Encrypted(Key);

        public void OnConnected(SoeConnection connection)
        {
        }

        public void OnMessage(SoeConnection connection, Span<byte> message) => Messages.Add(Convert.ToHexString(message));

        public void OnDisconnected(SoeConnection connection, DisconnectCause cause)
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
    public void BundledChunksAreDecryptedWithOneContinuingKeystream()
    {
        var service = new RecordingService();
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 3000;
        IPEndPoint server = listener.LocalEndPoint;

        byte[] request =
        [
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x03,
            0x0D, 0xBA, 0x01, 0x0A,
            0x00, 0x00, 0x02, 0x00,
            (byte)'L', (byte)'o', (byte)'g', (byte)'i', (byte)'n', (byte)'U', (byte)'d', (byte)'p', (byte)'_', (byte)'1', (byte)'4', 0x00,
        ];
        client.Send(request, request.Length, server);
        IPEndPoint from = new(IPAddress.Any, 0);
        byte[] reply = client.Receive(ref from);
        Assert.Equal((ushort)SoeOpcode.SessionReply, BinaryPrimitives.ReadUInt16BigEndian(reply));

        // The client's keystream: one cipher for the whole link, advanced only by message bodies.
        var cipher = new Rc4Cipher(Key);
        byte[] first = [0x01, 0xAA, 0xBB];
        cipher.Transform(first);
        byte[] second = [0x0D];
        cipher.Transform(second);
        byte[] third = [0x0B];
        cipher.Transform(third);
        byte[] fourth = [0x05, 0x01, 0x02, 0x03, 0x04];
        cipher.Transform(fourth);

        // Datagram 1: one plain message. Datagram 2: a bundle of two. Datagram 3: plain again.
        byte[] plain = [0x00, 0x09, 0x00, 0x00, .. first];
        byte[] bundle = [0x00, 0x09, 0x00, 0x01, 0x00, 0x19, (byte)second.Length, .. second, (byte)third.Length, .. third];
        byte[] after = [0x00, 0x09, 0x00, 0x02, .. fourth];
        client.Send(plain, plain.Length, server);
        client.Send(bundle, bundle.Length, server);
        client.Send(after, after.Length, server);

        // A bounded receive pass can acknowledge all three datagrams cumulatively.
        // Wait for the final sequence so the service has seen every encrypted chunk.
        while (true)
        {
            byte[] ack = client.Receive(ref from);
            Assert.Equal((ushort)SoeOpcode.Ack, BinaryPrimitives.ReadUInt16BigEndian(ack));
            ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(ack.AsSpan(2));
            Assert.InRange(sequence, (ushort)0, (ushort)2);
            if (sequence == 2) break;
        }

        Assert.Equal(["01AABB", "0D", "0B", "0501020304"], service.Messages);
    }

    [Fact]
    public void BundleChunkRunningPastThePayloadIsRejected()
    {
        var service = new RecordingService();
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, new SilentLog());
        listener.Start();

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 3000;
        IPEndPoint server = listener.LocalEndPoint;

        byte[] request =
        [
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x03,
            0x0D, 0xBA, 0x01, 0x0B,
            0x00, 0x00, 0x02, 0x00,
            (byte)'L', (byte)'o', (byte)'g', (byte)'i', (byte)'n', (byte)'U', (byte)'d', (byte)'p', (byte)'_', (byte)'1', (byte)'4', 0x00,
        ];
        client.Send(request, request.Length, server);
        IPEndPoint from = new(IPAddress.Any, 0);
        _ = client.Receive(ref from);

        // Marker, then a chunk claiming 9 bytes with only 1 present.
        byte[] bad = [0x00, 0x09, 0x00, 0x00, 0x00, 0x19, 0x09, 0x42];
        client.Send(bad, bad.Length, server);

        // A protocol error closes the session: the next thing we hear is a disconnect, not an ack.
        byte[] answer = client.Receive(ref from);
        Assert.Equal((ushort)SoeOpcode.Disconnect, BinaryPrimitives.ReadUInt16BigEndian(answer));
        Assert.Empty(service.Messages);
    }
}
