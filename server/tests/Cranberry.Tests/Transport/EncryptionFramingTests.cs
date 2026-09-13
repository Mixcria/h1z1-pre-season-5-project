using System.Buffers.Binary;
using System.Net;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public class EncryptionFramingTests
{
    private static readonly byte[] Key = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");

    private sealed class RecordingService : ISoeService
    {
        public List<byte[]> Messages { get; } = [];

        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) =>
            SessionDecision.Encrypted(Key);

        public void OnConnected(SoeConnection connection)
        {
        }

        public void OnMessage(SoeConnection connection, Span<byte> message) =>
            Messages.Add(message.ToArray());

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
    public void AugustCaptureAtKeystream1030DecodesToBothLoginRequests()
    {
        var service = new RecordingService();
        var transmitted = new List<byte[]>();
        SoeConnection connection = CreateConnection(service, transmitted);

        // Reproduce the cipher position after the captured 1,030-byte LoginRequest. RC4 state
        // depends on byte count, not plaintext value, so zeroes are sufficient for the prefix.
        byte[] precedingCiphertext = new byte[1030];
        new Rc4Cipher(Key).Transform(precedingCiphertext);
        connection.HandleDatagram(Data(sequence: 0, precedingCiphertext), now: 0);
        service.Messages.Clear();

        var raw = new List<(long Position, string Hex)>();
        connection.RawInboundSink = (_, bytes, position) =>
            raw.Add((position, Convert.ToHexString(bytes)));

        // Frozen from captures/wire-20260827-214152.txt.
        connection.HandleDatagram(
            Data(sequence: 1, Convert.FromHexString("0019014B0115")), now: 1);

        Assert.Equal([new byte[] { 0x0D }, new byte[] { 0x0B }], service.Messages);
        Assert.Equal([(1030L, "4B"), (1031L, "15")], raw);
        connection.Close(DisconnectCause.ServerRequested);
    }

    [Fact]
    public void MalformedBundleIsValidatedBeforeAnyChunkConsumesRc4()
    {
        var service = new RecordingService();
        SoeConnection connection = CreateConnection(service, []);

        byte[] encryptedRequest = [0x0D];
        new Rc4Cipher(Key).Transform(encryptedRequest);
        byte[] malformed = [0x00, 0x19, 0x01, encryptedRequest[0], 0x05, 0xAA];

        Assert.Throws<SoeProtocolException>(() =>
            connection.HandleDatagram(Data(sequence: 0, malformed), now: 0));
        Assert.Empty(service.Messages);

        // Sequence zero and cipher position zero are both still usable after validation failed.
        connection.HandleDatagram(Data(sequence: 0, encryptedRequest), now: 1);
        Assert.Equal([new byte[] { 0x0D }], service.Messages);
        connection.Close(DisconnectCause.ServerRequested);
    }

    [Fact]
    public void InboundLeadingZeroCiphertextConsumesOneClearEscapeByte()
    {
        var service = new RecordingService();
        SoeConnection connection = CreateConnection(service, []);
        byte[] plaintext = PlaintextWhoseFreshCiphertextStartsWithZero();
        byte[] ciphertext = (byte[])plaintext.Clone();
        new Rc4Cipher(Key).Transform(ciphertext);
        Assert.Equal(0, ciphertext[0]);

        byte[] escaped = [0x00, .. ciphertext];
        connection.HandleDatagram(Data(sequence: 0, escaped), now: 0);

        Assert.Equal([plaintext], service.Messages);
        connection.Close(DisconnectCause.ServerRequested);
    }

    [Fact]
    public void OutboundLeadingZeroCiphertextIsEscapedWithoutConsumingRc4ForThePad()
    {
        var service = new RecordingService();
        var transmitted = new List<byte[]>();
        SoeConnection connection = CreateConnection(service, transmitted);
        byte[] plaintext = PlaintextWhoseFreshCiphertextStartsWithZero();

        connection.Send(plaintext);

        byte[] datagram = Assert.Single(transmitted);
        Assert.Equal((ushort)SoeOpcode.Data, BinaryPrimitives.ReadUInt16BigEndian(datagram));
        Assert.Equal(0, datagram[4]); // Clear escape.
        Assert.Equal(0, datagram[5]); // First ciphertext byte.

        byte[] ciphertext = datagram.AsSpan(5).ToArray();
        new Rc4Cipher(Key).Transform(ciphertext);
        Assert.Equal(plaintext, ciphertext);
        connection.Close(DisconnectCause.ServerRequested);
    }

    [Fact]
    public void LateKeySwitchKeepsEarlierTrafficClearAndStartsBothCiphersAtZero()
    {
        var service = new RecordingService();
        var transmitted = new List<byte[]>();
        SoeConnection connection = CreateConnection(service, transmitted, SessionDecision.Clear);

        connection.HandleDatagram(Data(sequence: 0, [0x11, 0x22]), now: 0);
        connection.Send([0x33, 0x44]);
        byte[] clearOutbound = transmitted[^1];
        Assert.Equal(new byte[] { 0x33, 0x44 }, clearOutbound.AsSpan(4).ToArray());

        connection.EnableEncryption(Key);
        Assert.True(connection.EncryptionEnabled);

        byte[] encryptedInbound = [0x55, 0x66];
        new Rc4Cipher(Key).Transform(encryptedInbound);
        connection.HandleDatagram(Data(sequence: 1, encryptedInbound), now: 1);
        connection.Send([0x77, 0x88]);

        Assert.Equal(
            [new byte[] { 0x11, 0x22 }, new byte[] { 0x55, 0x66 }],
            service.Messages);
        byte[] encryptedOutbound = transmitted[^1];
        byte[] outboundBody = encryptedOutbound.AsSpan(4).ToArray();
        if (outboundBody.Length > 1 && outboundBody[0] == 0 && outboundBody[1] == 0)
        {
            outboundBody = outboundBody.AsSpan(1).ToArray();
        }

        new Rc4Cipher(Key).Transform(outboundBody);
        Assert.Equal(new byte[] { 0x77, 0x88 }, outboundBody);

        int beforeResend = transmitted.Count;
        connection.Tick(now: Environment.TickCount64 + 301);
        Assert.Equal(clearOutbound, transmitted[beforeResend]);
        Assert.Equal(encryptedOutbound, transmitted[beforeResend + 1]);
        connection.Close(DisconnectCause.ServerRequested);
    }

    [Fact]
    public void LateKeyInstallationIsAtomicAndCannotRekey()
    {
        var service = new RecordingService();
        SoeConnection connection = CreateConnection(service, [], SessionDecision.Clear);

        Assert.Throws<ArgumentException>(() => connection.EnableEncryption(Array.Empty<byte>()));
        Assert.False(connection.EncryptionEnabled);

        connection.EnableEncryption(Key);
        Assert.Throws<InvalidOperationException>(() => connection.EnableEncryption(Key));
        connection.Close(DisconnectCause.ServerRequested);
        Assert.Throws<InvalidOperationException>(() => connection.EnableEncryption(Key));

        SoeConnection preProvisioned =
            CreateConnection(service, [], SessionDecision.ClearUntilEnabled(Key));
        Assert.Throws<InvalidOperationException>(() => preProvisioned.EnableEncryption(Key));
        preProvisioned.EnableEncryption();
        Assert.True(preProvisioned.EncryptionEnabled);
        preProvisioned.Close(DisconnectCause.ServerRequested);
    }

    [Fact]
    public void RawUnreliableApplicationPacketInsideMultiReachesTheService()
    {
        var service = new RecordingService();
        SoeConnection connection = CreateConnection(service, [], SessionDecision.Clear);
        byte[] synchronization = [0x06, 0x8C, .. new byte[48]];
        byte[] multi = [0x00, (byte)SoeOpcode.Multi, (byte)synchronization.Length, .. synchronization];

        connection.HandleDatagram(multi, now: 0);

        Assert.Equal([synchronization], service.Messages);
        connection.Close(DisconnectCause.ServerRequested);
    }

    private static SoeConnection CreateConnection(
        RecordingService service,
        List<byte[]> transmitted,
        SessionDecision? decision = null)
    {
        var request = new SessionRequest(3, 0x12345678, 512, "LoginUdp_14");
        return new SoeConnection(
            new IPEndPoint(IPAddress.Loopback, 20042),
            in request,
            new SessionSettings { UdpLength = 512 },
            decision ?? SessionDecision.Encrypted(Key),
            service,
            new SilentLog(),
            (_, datagram) => transmitted.Add(datagram.ToArray()),
            now: 0);
    }

    private static byte[] Data(ushort sequence, ReadOnlySpan<byte> payload)
    {
        byte[] datagram = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(datagram, (ushort)SoeOpcode.Data);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), sequence);
        payload.CopyTo(datagram.AsSpan(4));
        return datagram;
    }

    private static byte[] PlaintextWhoseFreshCiphertextStartsWithZero()
    {
        byte[] firstKeystreamByte = [0];
        new Rc4Cipher(Key).Transform(firstKeystreamByte);
        return [firstKeystreamByte[0], 0x42];
    }
}
