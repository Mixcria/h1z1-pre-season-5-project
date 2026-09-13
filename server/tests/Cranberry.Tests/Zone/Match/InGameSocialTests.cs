using System.Collections;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class InGameSocialTests
{
    private sealed class Fixture : IPacketRecorder, ITransportLog
    {
        public ZoneService Service { get; }
        public List<(SoeConnection Connection, byte[] Packet)> Sent { get; } = [];
        public PartyRegistry Parties => (PartyRegistry)typeof(ZoneService)
            .GetField("_parties", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Service)!;

        public Fixture()
        {
            Service = new(this, this, new GatewayTicketRegistry(), new ZoneOptions());
            var directory = new InGameSocialDirectory(new Dictionary<string, string>
                { ["alice"] = "Alice", ["bob"] = "Bob", ["eve"] = "Eve", ["offline"] = "OfflineFriend" },
                [["alice", "bob"], ["alice", "offline"]]);
            Service.SocialDirectoryProvider = () => directory;
        }

        public SoeConnection Player(uint id, string account, string? name = null)
        {
            var request = new SessionRequest(3, id, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 15000 + (int)id), in request,
                new(), SessionDecision.Clear, Service, this, (_, _) => { }, 0);
            Service.OnConnected(connection);
            Set(connection, "Authenticated", true); Set(connection, "Guid", (ulong)id);
            Set(connection, "AccountId", account); Set(connection, "CharacterName", name ?? account);
            Set(connection, "AppearanceReadySent", true);
            var sessions = (IDictionary)typeof(ZoneService).GetField("_accountSessions", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(Service)!;
            sessions.Add(connection, connection.Tag!);
            return connection;
        }

        public void Action(SoeConnection connection, string action, uint arguments = 0)
        {
            // Reset only the clock guards so each independent click runs without sleeping tests.
            Set(connection, "SocialLastPollMs", long.MinValue / 2);
            Set(connection, "SocialLastActionMs", long.MinValue / 2);
            Set(connection, "SocialLastInviteMs", long.MinValue / 2);
            Send(connection, writer =>
            {
                writer.WriteByte(ZoneOpcodes.WallOfDataBase); writer.WriteByte(5);
                writer.WriteString(ZoneService.SocialWindow); writer.WriteString(action); writer.WriteUInt32(arguments);
            });
        }

        public void Send(SoeConnection connection, Action<PacketWriter> write)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
            write(writer); Service.OnMessage(connection, writer.Written.ToArray());
        }

        public void Transfer(SoeConnection connection, uint world) => Send(connection, writer =>
        {
            writer.WriteByte(0xec); writer.WriteUInt32(world); writer.WriteString("");
            writer.WriteUInt32(0); writer.WriteByte(1); writer.WriteInt32(1);
        });

        public string[] Views(SoeConnection connection) => Sent.Where(p => p.Connection == connection)
            .Select(p => ReadConsole(p.Packet)).Where(t => t?.StartsWith(ZoneService.SocialPrefix + "S|", StringComparison.Ordinal) == true)
            .Select(t => t!).ToArray();

        public static string? ReadConsole(byte[] packet)
        {
            if (packet.Length < 5 || packet[1] != 6 || packet[2] != 3 || packet[3] != 0) return null;
            var reader = new PacketReader(packet.AsSpan(4)); return reader.ReadString();
        }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add((connection, bytes.ToArray())); }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    [Theory]
    [InlineData(2, 6u)]
    [InlineData(5, 7u)]
    public void LauncherAndOverlayInvitesJoinOneGameLobbyAndQueueTogether(int count, uint world)
    {
        var f = new Fixture();
        var players = Enumerable.Range(1, count).Select(id => f.Player((uint)id, "account-" + id)).ToArray();
        f.Service.SocialDirectoryProvider = () => new InGameSocialDirectory(
            Enumerable.Range(1, count).ToDictionary(id => "account-" + id, id => "Player " + id),
            Enumerable.Range(2, count - 1).Select(id => new[] { "account-1", "account-" + id }));
        foreach (var player in players) f.Action(player, "hello");
        for (int i = 1; i < count; i++)
        {
            Set(players[0], "SocialLastInviteMs", long.MinValue / 2);
            if (i % 2 == 1) Overlay(f, players[0], "invite|account-" + (i + 1));
            else Assert.Null(f.Service.LauncherInviteToGame("account-1", "account-" + (i + 1)));
            var invitation = f.Service.LauncherSocial("account-" + (i + 1))!.Invitation!;
            Assert.NotNull(invitation);
            Assert.Contains(f.Views(players[i]), view => view.Contains(";I|" + invitation.Id[5..] + "|"));
            Assert.NotNull(f.Service.LauncherRespondToGame("account-1", invitation.Id, true));
            if (i % 2 == 1) f.Action(players[i], "accept " + invitation.Id[5..]);
            else Assert.Null(f.Service.LauncherRespondToGame("account-" + (i + 1), invitation.Id, true));
            Assert.NotNull(f.Service.LauncherRespondToGame("account-" + (i + 1), invitation.Id, true));
            Assert.Equal(i + 1, f.Service.LauncherSocial("account-1")!.Members.Count);
        }
        f.Transfer(players[0], world);
        Assert.All(players, p => Assert.Equal("Queued", p.Tag!.GetType().GetProperty("Match")!.GetValue(p.Tag)!.ToString()));
        Assert.All(players, p => Assert.Equal(f.Parties.Find(1)!.Id, f.Parties.Find((ulong)Array.IndexOf(players, p) + 1)!.Id));
    }

    [Fact]
    public void LauncherInvitesRejectNonFriendsBusyPlayersDuplicateSessionsAndDeclinesDoNotJoin()
    {
        var f = new Fixture(); var a = f.Player(1, "alice"); var b = f.Player(2, "bob"); var e = f.Player(3, "eve");
        f.Action(a, "hello"); f.Action(b, "hello");
        Assert.NotNull(f.Service.LauncherInviteToGame("alice", "eve"));
        Phase(b, "Queued"); Assert.NotNull(f.Service.LauncherInviteToGame("alice", "bob")); Phase(b, "Menu");
        Assert.Null(f.Service.LauncherInviteToGame("alice", "bob"));
        var invite = f.Service.LauncherSocial("bob")!.Invitation!;
        Assert.Null(f.Service.LauncherRespondToGame("bob", invite.Id, false));
        Assert.Null(f.Service.LauncherSocial("bob")!.Id);
        Assert.Null(f.Service.LauncherSocial("bob")!.Invitation);
        f.Player(4, "bob");
        Assert.NotNull(f.Service.LauncherInviteToGame("alice", "bob"));
    }

    private static void Set(SoeConnection connection, string name, object value) => connection.Tag!.GetType().GetProperty(name)!.SetValue(connection.Tag, value);
    private static void Overlay(Fixture fixture, SoeConnection connection, string action, uint arguments = 0)
    {
        Set(connection, "OverlayLastActionMs", long.MinValue / 2);
        fixture.Send(connection, writer =>
        {
            writer.WriteByte(ZoneOpcodes.WallOfDataBase); writer.WriteByte(5);
            writer.WriteString(ZoneService.OverlayWindow); writer.WriteString(action); writer.WriteUInt32(arguments);
        });
    }

    [Fact]
    public void OverlayRequestsBindTheActorToTheGatewayInMenusAndMatchesAndRespectBackpressure()
    {
        var f = new Fixture(); var a = f.Player(1, "alice"); var requests = new List<SocialOverlayRequest>();
        f.Service.SocialOverlaySubmit = r => { requests.Add(r); return true; };
        Overlay(f, a, "state"); Assert.Single(requests); Assert.Equal("alice", requests[0].Actor);
        requests[0].Reply("S|alice|Alice|");
        Assert.Contains(f.Sent, p => Fixture.ReadConsole(p.Packet) == ZoneService.OverlayPrefix + "S|alice|Alice|");
        Phase(a, "Queued");
        string nonce = Guid.NewGuid().ToString("N");
        Overlay(f, a, "send|bob|" + nonce + "|" + Uri.EscapeDataString("Hello | ; Ω"));
        Assert.Equal(2, requests.Count); Assert.Equal("Hello | ; Ω", requests[1].Text); Assert.Equal(nonce, requests[1].ClientId);
        requests[1].Reply("D|" + nonce + "|1");
        Overlay(f, a, "state", arguments: 1); Assert.Equal(2, requests.Count);
        Set(a, "Authenticated", false); Overlay(f, a, "state"); Assert.Equal(2, requests.Count);
        Set(a, "Authenticated", true); Set(a, "AppearanceReadySent", false); Overlay(f, a, "state"); Assert.Equal(2, requests.Count);
        Set(a, "AppearanceReadySent", true); f.Service.SocialOverlaySubmit = _ => false; Overlay(f, a, "state");
        Assert.Contains(f.Sent, p => Fixture.ReadConsole(p.Packet)?.StartsWith(ZoneService.OverlayPrefix + "E|") == true);
        Assert.False((bool)a.Tag!.GetType().GetProperty("OverlayPending")!.GetValue(a.Tag)!);
    }

    [Fact]
    public void OverlayToggleAndAvatarDeliveryAreScopedToTheAuthenticatedPlayer()
    {
        var f = new Fixture(); var a = f.Player(1, "alice"); var b = f.Player(2, "bob"); var e = f.Player(3, "eve");
        f.Service.SocialDirectoryProvider = () => new(new Dictionary<string, string> { ["alice"] = "Alice", ["bob"] = "Bob", ["eve"] = "Eve" },
            [["alice", "bob"]], new Dictionary<string, InGameAvatar> { ["bob"] = new("version", new string('A', 13824)) });
        string nonce = Guid.NewGuid().ToString("N");
        Assert.Null(f.Service.ToggleSocialOverlay("alice", nonce));
        Assert.Single(f.Sent, p => Fixture.ReadConsole(p.Packet) == ZoneService.OverlayPrefix + "T|" + nonce);
        Assert.Null(f.Service.ToggleSocialOverlay("alice", nonce));
        Assert.Single(f.Sent, p => Fixture.ReadConsole(p.Packet) == ZoneService.OverlayPrefix + "T|" + nonce);
        Assert.DoesNotContain(f.Sent, p => p.Connection == b || p.Connection == e);
        Overlay(f, a, "avatar|bob");
        Assert.Contains(f.Sent, p => p.Connection == a && Fixture.ReadConsole(p.Packet)?.StartsWith(ZoneService.OverlayPrefix + "A|bob|version|") == true);
        Overlay(f, e, "avatar|bob");
        Assert.DoesNotContain(f.Sent, p => p.Connection == e && Fixture.ReadConsole(p.Packet)?.Contains("version") == true);
    }
    private static string Phase(SoeConnection connection) => connection.Tag!.GetType().GetProperty("Match")!.GetValue(connection.Tag)!.ToString()!;
    private static void Phase(SoeConnection connection, string value)
    {
        var property = connection.Tag!.GetType().GetProperty("Match")!;
        property.SetValue(connection.Tag, Enum.Parse(property.PropertyType, value));
    }

    [Fact]
    public void FriendViewIsPrivateEscapesNamesAndTracksRealGameAvailability()
    {
        var f = new Fixture(); var alice = f.Player(1, "alice"); var bob = f.Player(2, "bob", "B|ob;é");
        var eve = f.Player(3, "eve");
        f.Action(alice, "hello"); f.Action(eve, "hello");
        string view = Assert.Single(f.Views(alice));
        Assert.Contains(";F|2|Bob|B%7Cob%3B%C3%A9|Menu|1", view);
        Assert.Contains(";F|0|OfflineFriend||Offline|0", view);
        Assert.DoesNotContain("Eve", view);
        Assert.DoesNotContain(";F|", Assert.Single(f.Views(eve)));
        f.Action(alice, "state"); Assert.Single(f.Views(alice)); // No unchanged retransmission.
        Phase(bob, "Queued"); f.Action(alice, "state");
        Assert.Contains("|Queued|0", f.Views(alice).Last());
        f.Service.OnDisconnected(bob, DisconnectCause.Timeout);
        Assert.Contains(";F|0|Bob||Offline|0", f.Views(alice).Last());
    }

    [Fact]
    public void InGameInviteAcceptQueueCancelAndLeaveUseOneAuthoritativeParty()
    {
        var f = new Fixture(); var alice = f.Player(1, "alice"); var bob = f.Player(2, "bob");
        f.Action(alice, "hello"); f.Action(bob, "hello");
        f.Action(alice, "invite 2");
        var pending = Assert.IsType<PartyInvitation>(f.Parties.Incoming(2, Environment.TickCount64));
        Assert.Contains(";I|" + pending.Token + "|alice", f.Views(bob).Last());
        f.Action(bob, "accept " + pending.Token);
        Assert.Equal(new ulong[] { 1, 2 }, f.Parties.Find(1)!.Members);
        Assert.Contains(";M|2|Bob|bob|Menu", f.Views(alice).Last());
        Assert.DoesNotContain(";I|", f.Views(bob).Last());
        f.Transfer(bob, 6);
        Assert.Equal("Menu", Phase(bob)); // A follower cannot queue independently.
        f.Transfer(alice, 6);
        Assert.Equal("Queued", Phase(alice)); Assert.Equal("Queued", Phase(bob));
        f.Transfer(bob, 6); // Initializes the follower's native transfer; only the leader can accept.
        Assert.Equal("Queued", Phase(alice)); Assert.Equal("Queued", Phase(bob));
        f.Send(bob, writer => writer.WriteByte(ZoneOpcodes.CancelQueueOnWorld));
        Assert.Equal("Menu", Phase(alice)); Assert.Equal("Menu", Phase(bob));
        Assert.Equal(new ulong[] { 1, 2 }, f.Parties.Find(1)!.Members);
        f.Action(bob, "leave");
        Assert.Null(f.Parties.Find(1)); Assert.Null(f.Parties.Find(2));
        Assert.DoesNotContain(";M|2|", f.Views(alice).Last());
    }

    [Fact]
    public void FivePlayersCanAcceptInGameInvitesAndQueueTogether()
    {
        var f = new Fixture();
        var players = Enumerable.Range(1, 5).Select(id => f.Player((uint)id, "account-" + id)).ToArray();
        f.Service.SocialDirectoryProvider = () => new(
            Enumerable.Range(1, 5).ToDictionary(id => "account-" + id, id => "Player " + id),
            Enumerable.Range(2, 4).Select(id => new[] { "account-1", "account-" + id }).ToArray());
        foreach (var player in players) f.Action(player, "hello");
        for (int index = 1; index < players.Length; index++)
        {
            f.Action(players[0], "invite " + (index + 1));
            var invitation = f.Parties.Incoming((ulong)index + 1, Environment.TickCount64)!;
            Assert.Contains(";I|" + invitation.Token + "|", f.Views(players[index]).Last());
            f.Action(players[index], "seen " + invitation.Token);
            f.Action(players[index], "accept " + invitation.Token);
        }
        Assert.Equal(new ulong[] { 1, 2, 3, 4, 5 }, f.Parties.Find(1)!.Members);
        foreach (var player in players)
        {
            string lobby = f.Views(player).Last();
            Assert.Equal(5, lobby.Split(';').Count(row => row.StartsWith("M|")));
            Assert.DoesNotContain(";I|", lobby);
        }
        f.Transfer(players[0], 6); // Duos cannot split a five-player party.
        Assert.All(players, player => Assert.Equal("Menu", Phase(player)));
        f.Transfer(players[0], 7);
        Assert.All(players, player => Assert.Equal("Queued", Phase(player)));
        var matchIds = players.Select(player => ((MatchAdmissionContext)player.Tag!.GetType()
            .GetProperty("BountyAdmission")!.GetValue(player.Tag)!).MatchId).ToArray();
        Assert.NotEqual(0ul, matchIds[0]);
        Assert.All(matchIds, id => Assert.Equal(matchIds[0], id));
    }

    [Fact]
    public void PendingInviteIsRetriedUntilRecipientAcknowledgesAndAcceptIsNotThrottledByReceipt()
    {
        var f = new Fixture(); var alice = f.Player(1, "alice"); var bob = f.Player(2, "bob"); var eve = f.Player(3, "eve");
        foreach (var player in new[] { alice, bob, eve }) f.Action(player, "hello");
        f.Action(alice, "invite 2");
        var invite = f.Parties.Incoming(2, Environment.TickCount64)!;
        int deliveries = f.Views(bob).Length;
        f.Action(bob, "state");
        Assert.Equal(deliveries + 1, f.Views(bob).Length); // Lost/ignored first UI snapshot.
        f.Action(eve, "seen " + invite.Token);
        f.Action(bob, "state");
        Assert.Equal(deliveries + 2, f.Views(bob).Length); // Another account cannot acknowledge it.
        f.Action(bob, "seen " + invite.Token);
        f.Action(bob, "state");
        Assert.Equal(deliveries + 2, f.Views(bob).Length); // Acknowledged snapshots are suppressed again.
        f.Action(bob, "hello");
        Assert.Equal(deliveries + 3, f.Views(bob).Length); // Re-entering UI can recover its prompt.
        Assert.Equal(long.MinValue / 2, bob.Tag!.GetType().GetProperty("SocialLastActionMs")!.GetValue(bob.Tag));
        f.Send(bob, writer =>
        {
            writer.WriteByte(ZoneOpcodes.WallOfDataBase); writer.WriteByte(5);
            writer.WriteString(ZoneService.SocialWindow); writer.WriteString("accept " + invite.Token); writer.WriteUInt32(0);
        });
        Assert.Equal(new ulong[] { 1, 2 }, f.Parties.Find(2)!.Members);
        Assert.DoesNotContain(";I|", f.Views(bob).Last());
    }

    [Fact]
    public void AcknowledgingAnOldInviteDoesNotSuppressItsReplacement()
    {
        var f = new Fixture(); var alice = f.Player(1, "alice"); var bob = f.Player(2, "bob");
        f.Action(alice, "hello"); f.Action(bob, "hello");
        f.Action(alice, "invite 2");
        var first = f.Parties.Incoming(2, Environment.TickCount64)!;
        f.Action(bob, "seen " + first.Token);
        f.Action(alice, "invite 2");
        var second = f.Parties.Incoming(2, Environment.TickCount64)!;
        Assert.NotEqual(first.Token, second.Token);
        int deliveries = f.Views(bob).Length;
        f.Action(bob, "seen " + first.Token);
        f.Action(bob, "state");
        Assert.Equal(deliveries + 1, f.Views(bob).Length);
        Assert.Contains(";I|" + second.Token + "|", f.Views(bob).Last());
        f.Parties.Incoming(2, second.ExpiresAtMs);
        f.Action(bob, "state");
        Assert.DoesNotContain(";I|", f.Views(bob).Last());
        f.Action(bob, "accept " + second.Token);
        Assert.Null(f.Parties.Find(2));
    }

    [Fact]
    public void WrongRecipientDeclineReplayMalformedAndBusyActionsCannotJoin()
    {
        var f = new Fixture(); var alice = f.Player(1, "alice"); var bob = f.Player(2, "bob"); var eve = f.Player(3, "eve");
        foreach (var player in new[] { alice, bob, eve }) f.Action(player, "hello");
        f.Action(eve, "invite 2"); Assert.Null(f.Parties.Incoming(2, Environment.TickCount64));
        f.Action(alice, "invite 2", arguments: 1); Assert.Null(f.Parties.Incoming(2, Environment.TickCount64));
        Set(bob, "AppearanceReadySent", false);
        f.Action(alice, "invite 2"); Assert.Null(f.Parties.Incoming(2, Environment.TickCount64));
        Set(bob, "AppearanceReadySent", true);
        f.Action(alice, "invite 2"); var invite = f.Parties.Incoming(2, Environment.TickCount64)!;
        f.Action(eve, "accept " + invite.Token); Assert.Null(f.Parties.Find(3));
        f.Action(bob, "decline " + invite.Token); f.Action(bob, "accept " + invite.Token);
        Assert.Null(f.Parties.Find(2)); Assert.Null(f.Parties.Incoming(2, Environment.TickCount64));
        f.Action(alice, "invite 2"); invite = f.Parties.Incoming(2, Environment.TickCount64)!;
        f.Parties.Incoming(2, invite.ExpiresAtMs); f.Action(bob, "accept " + invite.Token);
        Assert.Null(f.Parties.Find(2));
        Phase(alice, "Queued"); f.Action(alice, "invite 2");
        Assert.Null(f.Parties.Incoming(2, Environment.TickCount64));
    }

    [Fact]
    public void DisconnectInvalidatesInviteAndLeaderDepartureTransfersOwnership()
    {
        var f = new Fixture(); var a = f.Player(1, "alice"); var b = f.Player(2, "bob"); var c = f.Player(3, "eve");
        foreach (var player in new[] { a, b, c }) f.Action(player, "hello");
        f.Action(a, "invite 2");
        f.Service.OnDisconnected(a, DisconnectCause.Timeout);
        Assert.Null(f.Parties.Incoming(2, Environment.TickCount64));
        Assert.DoesNotContain(";I|", f.Views(b).Last());
        a = f.Player(4, "alice"); f.Action(a, "hello");
        foreach (ulong member in new ulong[] { 2, 3 })
        {
            var invitation = f.Parties.Invite(4, member, Environment.TickCount64)!;
            f.Parties.Respond(invitation.Token, member, true, Environment.TickCount64);
        }
        f.Action(a, "leave");
        Assert.Equal(2ul, f.Parties.Find(2)!.Leader);
        Assert.StartsWith(ZoneService.SocialPrefix + "S|2|2|Menu", f.Views(b).Last());
    }
}
