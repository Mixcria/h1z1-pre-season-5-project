using System.Collections;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class PartyGatewayTests
{
    private sealed class Fixture : IPacketRecorder, ITransportLog
    {
        public ZoneService Service { get; }
        public List<(SoeConnection Connection, byte[] Packet)> Sent { get; } = [];
        public PartyRegistry Parties => (PartyRegistry)typeof(ZoneService)
            .GetField("_parties", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Service)!;

        public Fixture() => Service = new(this, this, new GatewayTicketRegistry(), new ZoneOptions());

        public SoeConnection Player(uint id, string name)
        {
            var request = new SessionRequest(3, id, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 10000 + (int)id), in request,
                new(), SessionDecision.Clear, Service, this, (_, _) => { }, 0);
            Service.OnConnected(connection);
            object state = connection.Tag!;
            Set(state, "Authenticated", true);
            Set(state, "Guid", (ulong)id);
            Set(state, "CharacterName", name);
            var sessions = (IDictionary)typeof(ZoneService).GetField("_accountSessions", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(Service)!;
            sessions.Add(connection, state);
            return connection;
        }

        public void Send(SoeConnection connection, PartyPacket packet)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
            packet.WriteTo(writer);
            Service.OnMessage(connection, writer.Written.ToArray());
        }

        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add((connection, bytes.ToArray())); }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);
    private static bool IsGroup(byte[] packet, byte sub) => packet.Length > 3 && packet[1] == 0x13 && packet[2] == sub;
    private static PartyPacket Invite(ulong source, ulong target, string targetName) => new(1, 1, 0, 0,
        new(0, 0, new(source, new()), new(target, new SelfIdentity { Name = targetName }), 0));

    [Fact]
    public void NativeNameInviteAcceptAndLeavePublishOnlyAuthorizedMenuParty()
    {
        var f = new Fixture();
        SoeConnection leader = f.Player(1, "Leader"), guest = f.Player(2, "Guest"), outsider = f.Player(3, "Outsider");
        f.Send(leader, Invite(1, 0, "guest"));
        var notification = Assert.Single(f.Sent, sent => sent.Connection == guest && IsGroup(sent.Packet, 1));
        Assert.True(PartyPacket.TryParse(notification.Packet.AsSpan(1), out PartyPacket? delivered));
        Assert.Equal("Leader", delivered.Invite!.Source.Identity.Name);
        Assert.Equal(2ul, delivered.Invite.Target.Guid);
        Assert.NotEqual(0ul, delivered.Invite.Token);
        Assert.Null(f.Parties.Find(1));

        PartyPacket accept = delivered with { SubOpcode = 2, JoinState = 1 };
        f.Send(outsider, accept);
        Assert.Null(f.Parties.Find(3));
        Assert.Null(f.Parties.Find(1));
        f.Send(guest, accept);
        Assert.Equal(new ulong[] { 1, 2 }, f.Parties.Find(1)!.Members);
        Assert.Single(f.Sent, sent => sent.Connection == leader && IsGroup(sent.Packet, 0x12));
        Assert.Single(f.Sent, sent => sent.Connection == guest && IsGroup(sent.Packet, 0x12));
        Assert.DoesNotContain(f.Sent, sent => sent.Connection == outsider && IsGroup(sent.Packet, 0x12));
        int rosters = f.Sent.Count(sent => IsGroup(sent.Packet, 0x12));
        f.Send(guest, accept);
        Assert.Equal(rosters, f.Sent.Count(sent => IsGroup(sent.Packet, 0x12)));

        f.Send(guest, new(4, 1, 0, 0));
        Assert.Null(f.Parties.Find(1));
        Assert.Null(f.Parties.Find(2));
        Assert.Single(f.Sent, sent => sent.Connection == leader && IsGroup(sent.Packet, 0x16));
        Assert.Single(f.Sent, sent => sent.Connection == guest && IsGroup(sent.Packet, 0x16));
    }

    [Fact]
    public void SourceSpoofAndPartyChangesWhileQueuedAreRefused()
    {
        var f = new Fixture();
        SoeConnection leader = f.Player(1, "Leader"), guest = f.Player(2, "Guest");
        f.Send(leader, Invite(99, 2, "Guest"));
        Assert.DoesNotContain(f.Sent, sent => sent.Connection == guest && IsGroup(sent.Packet, 1));
        var phase = leader.Tag!.GetType().GetProperty("Match")!;
        phase.SetValue(leader.Tag, Enum.Parse(phase.PropertyType, "Queued"));
        f.Send(leader, Invite(1, 2, "Guest"));
        Assert.DoesNotContain(f.Sent, sent => sent.Connection == guest && IsGroup(sent.Packet, 1));
    }

    [Fact]
    public void LauncherPartyQueuesBothAccountsInOneMatchAndRejectsUnavailableMembers()
    {
        var f = new Fixture();
        f.Service.Post = _ => { }; // Keep delayed queue work pending; inspect the atomic admission first.
        var a = f.Player(1, "Leader"); var b = f.Player(2, "Guest");
        Set(a.Tag!, "AccountId", "alice"); Set(b.Tag!, "AccountId", "bob");
        Assert.NotNull(f.Service.LauncherQueue(["alice", "missing"], "Duos"));
        Assert.Null(f.Parties.Find(1));
        Assert.NotNull(f.Service.LauncherQueue(["alice", "bob"], "Solo"));
        Assert.NotNull(f.Service.LauncherQueue(["alice", "bob"], "Duos")); // Neither client has sent ClientIsReady yet.
        Set(a.Tag!, "AppearanceReadySent", true); Set(b.Tag!, "AppearanceReadySent", true);
        Assert.Null(f.Service.LauncherQueue(["alice", "bob"], "Duos"));
        Assert.Equal(new ulong[] { 1, 2 }, f.Parties.Find(1)!.Members);
        Assert.Equal("Queued", a.Tag!.GetType().GetProperty("Match")!.GetValue(a.Tag)!.ToString());
        Assert.Equal("Queued", b.Tag!.GetType().GetProperty("Match")!.GetValue(b.Tag)!.ToString());
        object admissionA = a.Tag.GetType().GetProperty("BountyAdmission")!.GetValue(a.Tag)!;
        object admissionB = b.Tag.GetType().GetProperty("BountyAdmission")!.GetValue(b.Tag)!;
        Assert.Equal(admissionA, admissionB);
        Assert.Null(f.Service.LauncherCancel("alice"));
        Assert.Equal("Menu", b.Tag.GetType().GetProperty("Match")!.GetValue(b.Tag)!.ToString());
    }

    [Fact]
    public void RelayedFriendCannotAcquireTheLocalHostsConsolePowers()
    {
        var f = new Fixture();
        f.Service.LocalOwnerAccountId = "owner";
        var owner = f.Player(1, "Owner"); var friend = f.Player(2, "Friend");
        Set(owner.Tag!, "AccountId", "owner"); Set(friend.Tag!, "AccountId", "friend");
        var resolve = typeof(ZoneService).GetMethod("ResolveConsoleTier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(Cranberry.Zone.DevConsole.ConsoleTier.Owner, resolve.Invoke(f.Service, [owner, owner.Tag]));
        Assert.Equal(Cranberry.Zone.DevConsole.ConsoleTier.Player, resolve.Invoke(f.Service, [friend, friend.Tag]));
    }
}
