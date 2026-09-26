using System.Net;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Runtime;
using Cranberry.Harness.Soe;
using Cranberry.Transport;

namespace Cranberry.Harness.Tests;

public sealed class MovementJournalPolicyTests
{
    [Theory]
    [InlineData("0578FC00000100000000")]
    [InlineData("0578010100000100000000")]
    [InlineData("057802000100000100000000")]
    [InlineData("05780300000100000100000000")]
    [InlineData("0578FC0100010000000003010000")]
    public void ExactCompleteMovementPrefixQualifiesWithoutParsingPayload(string hex)
    {
        Assert.True(GatewayWire.HasInboundMovementJournalPrefix(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0578")]
    [InlineData("057803")]
    [InlineData("0578FC000001000000")]
    [InlineData("05780000000100000000")] // no recipient
    [InlineData("05780400000100000000")] // self recipient
    [InlineData("0578010000000100000000")] // noncanonical zero recipient
    [InlineData("0578090000000100000000")] // over-wide recipient two
    [InlineData("0578FC01000100000000")] // missing flags
    [InlineData("0578FC0100010000000003")] // four-byte flags truncated
    [InlineData("4578FC00000100000000")] // wrong channel
    [InlineData("0678FC00000100000000")] // wrong direction
    [InlineData("050F0100000000000000")] // lifecycle packet
    public void ForeignOrMalformedPrefixesRemainAvailableForFailureContext(string hex)
    {
        Assert.False(GatewayWire.HasInboundMovementJournalPrefix(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData(false, AugustClient.GatewayProtocolName)]
    [InlineData(true, AugustClient.GatewayProtocolName)]
    [InlineData(true, AugustClient.LoginProtocolName)]
    public async Task OptInChangesOnlyJournalCoverageNotEncryptedDeliveryOrOrdering(bool omit, string protocol)
    {
        byte[] key = [1, 2, 3, 4, 5];
        var service = new Echo(key);
        using var listener = new SoeListener(new IPEndPoint(IPAddress.Loopback, 0), service, Quiet.Instance);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var journal = new PacketJournal(60);
        int nameCalls = 0;
        await using var client = await SoeClientSession.OpenAsync(
            new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint.Port),
            new SoeClientOptions { ProtocolName = protocol, LinkName = "journal-test", Key = key,
                OmitInboundMovementJournal = omit }, new HarnessClock(), journal,
            _ => { Interlocked.Increment(ref nameCalls); return "authored"; }, deadline.Token);
        // Large movement tail exercises reliable fragmentation, without claiming semantic
        // validity of every tail field. This policy classifies only the complete wire prefix.
        byte[] movement = Convert.FromHexString("0578FC00000100000000");
        byte[] large = [.. movement, .. Enumerable.Range(0, 20000).Select(i => (byte)(i % 251))];
        byte[][] messages =
        [
            movement, [5, 0x0f, 1, 8, 7, 6, 5, 4, 3, 2, 1], large,
            [5, 0x78, 0xfc, 1, 0, 1, 0, 0, 0, 0, 3], // truncated flags, retained
            [5, 0x70, 4, 0, 0, 0, 0],
        ];
        foreach (byte[] message in messages) client.Send(message);
        long position = 0;
        foreach (byte[] expected in messages)
        {
            InboundMessage actual = await client.Messages.ReadAsync(deadline.Token);
            Assert.Equal(expected, actual.Bytes);
            Assert.True(actual.WasEncrypted);
            Assert.Equal(position, actual.KeystreamPosition);
            position += expected.Length;
        }
        bool suppression = omit && protocol == AugustClient.GatewayProtocolName;
        long omitted = suppression ? 2 : 0;
        Assert.Equal(messages.Length, client.MessagesReceived);
        Assert.Equal(position, client.InboundKeystreamPosition);
        Assert.Equal(position, client.OutboundKeystreamPosition);
        Assert.Equal(omitted, client.InboundMovementJournalSuppressed);
        Assert.Equal(omitted, journal.MovementEntriesSuppressed);
        Assert.Equal(messages.Length * 2 - omitted, Volatile.Read(ref nameCalls));
        var application = journal.Snapshot().Where(x => x.Name == "authored").ToArray();
        Assert.Equal(messages.Length, application.Count(x => x.Direction == PacketDirection.ToServer));
        var retained = application.Where(x => x.Direction == PacketDirection.FromServer).ToArray();
        Assert.Equal(messages.Length - omitted, retained.Length);
        var expectedRetained = messages.Where((_, i) => !suppression || (i != 0 && i != 2)).ToArray();
        for (int i = 0; i < retained.Length; i++) Assert.Equal(expectedRetained[i], retained[i].Bytes);
        Assert.Contains(journal.Snapshot(), x => x.Name.StartsWith("RC4 armed"));
        if (suppression) Assert.Contains("2 inbound movement entries omitted", journal.Tail());
        else Assert.DoesNotContain("omitted", journal.Tail());
        Assert.Equal(LinkCloseCause.None, client.CloseCause);
    }

    [Fact]
    public void DefaultOptionPreservesCompleteJournalPolicy()
    {
        Assert.False(new SoeClientOptions { ProtocolName = AugustClient.GatewayProtocolName,
            LinkName = "default" }.OmitInboundMovementJournal);
        Assert.Equal(0, new PacketJournal().MovementEntriesSuppressed);
    }

    private sealed class Echo(byte[] key) : ISoeService
    {
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Encrypted(key);
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) => connection.Send(message);
    }
    private sealed class Quiet : ITransportLog
    {
        public static readonly Quiet Instance = new();
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
}
