using System.Collections;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone;

public sealed class ProximityVoiceHudTests
{
    private sealed class Fixture : IPacketRecorder, ITransportLog, IPeerSink
    {
        public ZoneService Zone { get; }
        public readonly List<(SoeConnection Connection, string Text)> Sent = [];
        public bool IsOpen => true;
        public void Send(byte[] packet) { }
        public Fixture() => Zone = new(this, this, new GatewayTicketRegistry(), new ZoneOptions());
        public SoeConnection Player(uint id, string account, string name, float x = 0, ulong match = 1)
        {
            var request = new SessionRequest(3, id, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 15000 + (int)id), in request,
                new(), SessionDecision.Clear, Zone, this, (_, _) => { }, 0);
            Zone.OnConnected(connection);
            Set(connection, "Authenticated", true); Set(connection, "Guid", (ulong)id);
            Set(connection, "AccountId", account); Set(connection, "CharacterName", name);
            Set(connection, "AppearanceReadySent", true); Set(connection, "Hitpoints", 10000u);
            var matchProperty = connection.Tag!.GetType().GetProperty("Match")!;
            matchProperty.SetValue(connection.Tag, Enum.Parse(matchProperty.PropertyType, "InMatch"));
            var peer = new PeerSession(id, this) { Position = new(x, 0, 0), MatchId = match, InMatch = true };
            peer.SetPose([]); Set(connection, "Peer", peer);
            var sessions = (IDictionary)typeof(ZoneService).GetField("_accountSessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Zone)!;
            sessions.Add(connection, connection.Tag!);
            return connection;
        }
        public void Link(SoeConnection connection, string action = "open", uint arguments = 0)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
            writer.WriteByte(ZoneOpcodes.WallOfDataBase); writer.WriteByte(5);
            writer.WriteString(ZoneService.VoiceHudWindow); writer.WriteString(action); writer.WriteUInt32(arguments);
            Zone.OnMessage(connection, writer.Written.ToArray());
        }
        public void Publish(ProximityVoiceHud[] views, long now = 1000) => Zone.PublishProximityVoiceHud(views, 75, now);
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction != "s2c" || bytes.Length < 8 || bytes[1] != 6 || bytes[2] != 3 || bytes[3] != 0) return;
            var reader = new PacketReader(bytes[4..]);
            string text = reader.ReadString();
            if (text.StartsWith(ZoneService.VoiceHudPrefix)) Sent.Add((connection, text));
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
    private static void Set(SoeConnection connection, string name, object value) => connection.Tag!.GetType().GetProperty(name)!.SetValue(connection.Tag, value);

    [Fact]
    public void OnlyLinkedAuthenticatedHudGetsAuthoritativeNamesAndPeriodicRefresh()
    {
        var f = new Fixture(); var a = f.Player(1, "a", "Alice"); var b = f.Player(2, "b", "B|ob; 🎙", 5);
        ProximityVoiceHud[] views = [new("a", 1, 1, [1, 2])];
        f.Publish(views); Assert.Empty(f.Sent);
        f.Link(a, arguments: 1); f.Publish(views); Assert.Empty(f.Sent);
        f.Link(a); f.Publish(views);
        var first = Assert.Single(f.Sent);
        Assert.Same(a, first.Connection);
        Assert.Equal(ZoneService.VoiceHudPrefix + "1|Alice;2|" + Uri.EscapeDataString("B|ob; 🎙"), first.Text);
        f.Publish(views, 1499); Assert.Single(f.Sent);
        f.Publish(views, 1500); Assert.Equal(2, f.Sent.Count);
        f.Publish([], 1501); Assert.Equal(ZoneService.VoiceHudPrefix, f.Sent[^1].Text);
        f.Publish([], 2001); Assert.Equal(3, f.Sent.Count);
        f.Link(a, "close"); f.Publish(views, 2100); Assert.Equal(3, f.Sent.Count);
    }

    [Fact]
    public void StaleOrSpoofedListenerAndDeadFarOrOtherMatchSpeakersAreExcluded()
    {
        var f = new Fixture(); var a = f.Player(1, "a", "Alice");
        var b = f.Player(2, "b", "Bob", 5); var dead = f.Player(3, "dead", "Dead", 4);
        f.Player(4, "far", "Far", 75); f.Player(5, "other", "Other", 3, 2);
        Set(dead, "Hitpoints", 0u); f.Link(a);
        f.Publish([new("a", 1, 1, [1, 2, 3, 4, 5, 2, 999])]);
        Assert.Equal(ZoneService.VoiceHudPrefix + "1|Alice;2|Bob", Assert.Single(f.Sent).Text);
        f.Publish([new("a", 1, 2, [1, 2])], 1100);
        Assert.Equal(ZoneService.VoiceHudPrefix, f.Sent[^1].Text);
        f.Publish([new("a", 2, 1, [1, 2])], 1200); Assert.Equal(2, f.Sent.Count);
        f.Publish([new("a", 1, 1, [1, 2])], 1300); Assert.Equal(3, f.Sent.Count);
        Set(a, "DeathSent", true); f.Publish([new("a", 1, 1, [1, 2])], 1400);
        Assert.Equal(ZoneService.VoiceHudPrefix, f.Sent[^1].Text);
    }

    [Fact]
    public void AmbiguousAccountsAndNonFinitePositionsCannotPublishSpeakerNames()
    {
        var f = new Fixture(); var a = f.Player(1, "a", "Alice");
        f.Player(2, "b", "Bob", 5); f.Player(3, "b", "Duplicate", 5); f.Player(4, "nan", "Nan", float.NaN);
        f.Link(a); f.Publish([new("a", 1, 1, [1, 2, 3, 4])]);
        Assert.Equal(ZoneService.VoiceHudPrefix + "1|Alice", Assert.Single(f.Sent).Text);
        f.Player(5, "a", "Duplicate listener", 1); f.Publish([new("a", 1, 1, [1])], 1100);
        Assert.Equal(ZoneService.VoiceHudPrefix, f.Sent[^1].Text);
    }
}
