using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Emotes;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Emotes;

public sealed class EmoteIntegrationTests
{
    [Fact]
    public void PredictedFunctionKeyRequestReachesKnownPlayersInSameMatchOnly()
    {
        using var f = new Fixture();
        var player = f.Add(1);
        var nearby = f.Add(2);
        var otherMatch = f.Add(3, matchId: 2);
        var unknown = f.Add(4);
        f.Know(nearby, player);
        f.Know(otherMatch, player); // stale interest must not cross a match boundary.
        f.Send(player, "F70102000000");
        Assert.Empty(f.Sent(player));
        Assert.Equal(EmotePackets.Start(1, 2), Assert.Single(f.Sent(nearby)));
        Assert.Empty(f.Sent(otherMatch));
        Assert.Empty(f.Sent(unknown));
    }

    [Fact]
    public void StopMustMatchTheActiveEmoteAndDuplicatesDoNothing()
    {
        using var f = new Fixture();
        var player = f.Add(1);
        var viewer = f.Add(2);
        f.Know(viewer, player);
        f.Send(player, "F70102000000");
        f.Send(player, "F70203000000");
        Assert.Single(f.Sent(viewer));
        f.Send(player, "F70202000000");
        f.Send(player, "F70202000000");
        Assert.Empty(f.Sent(player));
        Assert.Equal(2, f.Sent(viewer).Length);
        Assert.Equal(EmotePackets.Stop(1, 2), f.Sent(viewer)[1]);
    }

    [Theory]
    [InlineData("F7011A000000")] // premium animation, not equipped in a default slot
    [InlineData("F701FFFFFFFF")]
    [InlineData("F70100000000")]
    [InlineData("F7010200000000")]
    [InlineData("F703010000000000000002000000")] // server playback is not a request
    [InlineData("F701")]
    public void UnsupportedAndMalformedRequestsDoNotAnimateAnyone(string request)
    {
        using var f = new Fixture();
        var player = f.Add(1);
        var viewer = f.Add(2);
        f.Know(viewer, player);
        f.Send(player, request);
        Assert.Empty(f.Sent(player));
        Assert.Empty(f.Sent(viewer));
    }

    [Theory]
    [InlineData("DeathSent", true)]
    [InlineData("MountRequested", true)]
    [InlineData("Hitpoints", 0u)]
    public void DeadAndParachutingPlayersCannotStartEmotes(string property, object value)
    {
        using var f = new Fixture();
        var player = f.Add(1);
        var viewer = f.Add(2);
        f.Know(viewer, player);
        Set(player, property, value);
        f.Send(player, "F70102000000");
        Assert.Empty(f.Sent(player));
        Assert.Empty(f.Sent(viewer));
    }

    [Fact]
    public void RepeatedKeyPressIsBoundedAndLaterDifferentEmoteReplacesTheFirst()
    {
        using var f = new Fixture();
        var player = f.Add(1);
        var viewer = f.Add(2);
        f.Know(viewer, player);
        f.Send(player, "F70102000000");
        f.Send(player, "F70102000000");
        Assert.Single(f.Sent(viewer));
        Set(player, "LastEmoteStartAtMs", Environment.TickCount64 - 1000);
        f.Send(player, "F70103000000");
        Assert.Empty(f.Sent(player));
        Assert.Equal(3, f.Sent(viewer).Length);
        Assert.Equal(EmotePackets.Stop(1, 2), f.Sent(viewer)[1]);
        Assert.Equal(EmotePackets.Start(1, 3), f.Sent(viewer)[2]);
    }

    private static void Set(SoeConnection c, string name, object value) =>
        c.Tag!.GetType().GetProperty(name)!.SetValue(c.Tag, value);

    private sealed class Fixture : IDisposable
    {
        private readonly SilentLog _log = new();
        private readonly Recorder _recorder = new();
        private readonly List<SoeConnection> _connections = [];
        private readonly ZoneService _service;
        public Fixture() => _service = new(_log, _recorder, new GatewayTicketRegistry());

        public SoeConnection Add(ulong guid, ulong matchId = 1)
        {
            var request = new SessionRequest(3, (uint)guid, 512, ZoneService.ProtocolName);
            var c = new SoeConnection(new(IPAddress.Loopback, 12000 + (int)guid), in request,
                new(), SessionDecision.Clear, _service, _log, (_, _) => { }, 0);
            _service.OnConnected(c);
            Set(c, "Authenticated", true);
            Set(c, "Guid", guid);
            Set(c, "CharacterName", $"Emote{guid}");
            Set(c, "BountyAdmission", new MatchAdmissionContext(matchId, MatchQueueKind.Public, MatchMode.Solo));
            _service.ForTest(c).EnterMatch();
            typeof(ZoneService).GetMethod("RegisterPeerSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_service, [c, c.Tag]);
            Peer(c).MatchId = matchId;
            Peer(c).InMatch = true;
            _connections.Add(c);
            return c;
        }

        private static PeerSession Peer(SoeConnection c) =>
            (PeerSession)c.Tag!.GetType().GetProperty("Peer")!.GetValue(c.Tag)!;

        public void Know(SoeConnection viewer, SoeConnection subject)
        {
            Peer(viewer).View.MarkKnown(Peer(subject).Key);
            Peer(viewer).View.Transients.Acquire(Peer(subject).Key);
        }

        public void Send(SoeConnection c, string hex)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            writer.WriteRaw(Convert.FromHexString(hex));
            _service.OnMessage(c, writer.Written.ToArray());
        }

        public byte[][] Sent(SoeConnection c) => _recorder.Sent
            .Where(r => r.Connection == c && r.Packet.Length > 1 && r.Packet[1] == 0xf7)
            .Select(r => r.Packet[1..]).ToArray();

        public void Dispose()
        {
            foreach (var c in _connections) { c.Disconnect(); _service.OnDisconnected(c, DisconnectCause.ServerRequested); }
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private sealed class Recorder : IPacketRecorder
    {
        public List<(SoeConnection Connection, byte[] Packet)> Sent { get; } = [];
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add((connection, bytes.ToArray())); }
    }
}
