using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;
using Cranberry.Zone.HostedGames;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed partial class HostedGameGatewayTests
{
    private sealed class Fixture : IPacketRecorder, ITransportLog, IDisposable
    {
        private int _next;
        private readonly List<SoeConnection> _connections = [];
        public DateTimeOffset Now { get; set; } = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public HostedGameStore Store { get; }
        public ZoneService Service { get; }
        public ConcurrentQueue<Action> Pending { get; } = new();
        public List<(SoeConnection Connection, byte[] Packet)> Sent { get; } = [];

        public Fixture()
        {
            Store = new(utcNow: () => Now);
            Service = new(this, this, new GatewayTicketRegistry(), new ZoneOptions
            {
                HostedGames = Store,
                EnableGas = false,
                SendContainers = false,
                SendDoors = false,
                SendVehicles = false,
                Console = ConsoleOptions.Default with { LocalIsOwner = false },
            }) { Post = Pending.Enqueue };
        }

        public SoeConnection Player(string account)
        {
            var request = new SessionRequest(3, (uint)++_next, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 10000 + _next), in request,
                new(), SessionDecision.Clear, Service, this, (_, _) => { }, 0);
            Service.OnConnected(connection);
            _connections.Add(connection);
            Set(connection.Tag!, "Authenticated", true);
            Set(connection.Tag!, "AccountId", account);
            Set(connection.Tag!, "Guid", (ulong)_next);
            Set(connection.Tag!, "CharacterName", account);
            Set(connection.Tag!, "Visuals", CharacterVisuals.FromSelection(1, 1, 1, 0, 0));
            Set(connection.Tag!, "Wardrobe", new AugustWardrobeState());
            Field<IDictionary>(Service, "_accountSessions").Add(connection, connection.Tag);
            return connection;
        }

        public HostedGameInfo Host(string account, uint mode = GameWorldCatalog.DuosGameModeId)
        {
            var key = Store.IssueHostKey("administrator", true, "EU", targetAccount: account);
            Assert.True(key.Success, key.Message);
            Assert.True(Store.Redeem(account, key.Secret!).Success);
            var result = Store.CreateGame(account, "EU", mode, account + " friends");
            Assert.True(result.Success, result.Message);
            return Assert.IsType<HostedGameInfo>(result.Game);
        }

        public HostedGameResult Invitation(HostedGameInfo game, string account, TimeSpan? lifetime = null)
        {
            var result = Store.IssuePlayerKey(game.OwnerAccount, false, game.WorldId, lifetime, account);
            Assert.True(result.Success, result.Message);
            return result;
        }

        public void Admit(HostedGameInfo game, SoeConnection player, TimeSpan? lifetime = null)
        {
            var key = Invitation(game, Account(player), lifetime);
            var reply = HostCommand(player, "redeem " + key.Secret);
            Assert.True(reply.Ok, string.Join("; ", reply.Lines));
            Assert.True(Store.CanEnter(Account(player), game.WorldId));
        }

        public ConsoleReply HostCommand(SoeConnection player, string arguments, ConsoleTier tier = ConsoleTier.Player)
        {
            var session = Get<ConsoleSession>(player.Tag!, "DevConsole");
            session.TierOverride = tier;
            var context = (ConsoleContext)Call(Service, "BuildConsoleContext", player, player.Tag)!;
            var command = CommandCatalog.Build().ByName("hostgame")!;
            return command.Run(new(context, command, CommandLine.Parse("hostgame", arguments, command.KeepCase), session));
        }

        public void Transfer(SoeConnection player, uint worldId, string text = "", byte flag = 1, int role = 1)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
            writer.WriteByte(0xec);
            writer.WriteUInt32(worldId);
            writer.WriteString(text);
            writer.WriteUInt32(0);
            writer.WriteByte(flag);
            writer.WriteInt32(role);
            Service.OnMessage(player, writer.Written.ToArray());
        }

        public bool Validate(SoeConnection player) => (bool)Call(Service, "ValidateHostedAdmission", player, player.Tag)!;

        public void Party(SoeConnection leader, SoeConnection guest)
        {
            var parties = Field<PartyRegistry>(Service, "_parties");
            var invitation = parties.Invite(Get<ulong>(leader.Tag!, "Guid"), Get<ulong>(guest.Tag!, "Guid"), 0)!;
            Assert.NotNull(parties.Respond(invitation.Token, invitation.Invitee, true, 1));
        }

        public void Dispose()
        {
            foreach (var connection in _connections) connection.Disconnect();
        }

        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c") Sent.Add((connection, bytes.ToArray()));
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private static object? Call(ZoneService service, string method, params object?[] arguments) =>
        typeof(ZoneService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, arguments);
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);
    private static string Account(SoeConnection player) => Get<string>(player.Tag!, "AccountId");
    private static string Phase(SoeConnection player) => Get<object>(player.Tag!, "Match").ToString()!;
    private static void SetPhase(SoeConnection player, string phase)
    {
        var property = player.Tag!.GetType().GetProperty("Match")!;
        property.SetValue(player.Tag, Enum.Parse(property.PropertyType, phase));
    }
    private static MatchAdmissionContext Admission(SoeConnection player) => Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission");
    private static bool IsTransferReply(byte[] packet) => packet.Length == 7 && packet[1] == 0xed;
    private static void AssertCleared(SoeConnection player)
    {
        Assert.Equal("Menu", Phase(player));
        Assert.Equal(MatchAdmissionContext.Unknown, Admission(player));
        Assert.Null(Get<string?>(player.Tag!, "HostedGameId"));
        Assert.Null(Get<PlayerWorldTransferRequest?>(player.Tag!, "MatchTransferRequest"));
    }

    private sealed record ScheduleRow(uint WorldId, uint GameModeId, bool CanEnter);

    private static (ScheduleRow[] Games, Dictionary<string, StringHashValue> Labels) Schedule(Fixture fixture, SoeConnection player)
    {
        fixture.Sent.Clear();
        Call(fixture.Service, "SendHostedSchedule", player, player.Tag);
        var schedule = Assert.Single(fixture.Sent, sent => sent.Connection == player && sent.Packet.Length > 3
            && sent.Packet[1] == 0x67 && sent.Packet[2] == 0x12).Packet;
        var reader = new PacketReader(schedule.AsSpan(3));
        int count = reader.ReadInt32();
        var games = new List<ScheduleRow>();
        for (int index = 0; index < count; index++)
        {
            uint worldId = reader.ReadUInt32();
            reader.ReadUInt32();
            uint mode = reader.ReadUInt32();
            reader.Skip(32); // title, description, image, unlock/start times, entry fee
            reader.ReadString();
            reader.Skip(12); // tickets and prize fields
            Assert.False(reader.ReadBool());
            bool canEnter = reader.ReadBool();
            reader.ReadString();
            Assert.True(reader.ReadBool());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.False(reader.ReadBool());
            reader.ReadUInt32();
            int trailing = reader.ReadInt32();
            reader.Skip(trailing * sizeof(ulong));
            games.Add(new(worldId, mode, canEnter));
        }
        Assert.True(reader.AtEnd);

        var update = Assert.Single(fixture.Sent, sent => sent.Connection == player && sent.Packet.Length > 1
            && sent.Packet[1] == ZoneOpcodes.StringHashToValueManager).Packet;
        var values = new PacketReader(update.AsSpan(2));
        int labelsCount = values.ReadInt32();
        var labels = new Dictionary<string, StringHashValue>(StringComparer.Ordinal);
        for (int index = 0; index < labelsCount; index++)
        {
            uint hash = values.ReadUInt32();
            string value = values.ReadString();
            Assert.False(values.ReadBool());
            string name = values.ReadString();
            labels.Add(name, new(name, value, hash));
        }
        Assert.True(values.AtEnd);
        foreach (var expected in StringHashValues.Entries) Assert.Equal(expected, labels[expected.Name]);
        Assert.Equal("", labels[WorldDisplayLabel.Key].Value);
        return (games.ToArray(), labels);
    }

    private static bool IsStartMatch(byte[] packet) => packet.Length > 3 && packet[1] == 0xce && packet[2] == 0x16;

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public void RawTransferCannotEnterAPrivateGameOrRedeemAKey(int role, bool includeSecret)
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var player = f.Player("guest");
        var invite = f.Invitation(game, Account(player));

        f.Transfer(player, game.WorldId, includeSecret ? invite.Secret! : "", role: role);

        AssertCleared(player);
        Assert.False(f.Store.CanEnter(Account(player), game.WorldId));
        var response = Assert.Single(f.Sent, sent => sent.Connection == player && IsTransferReply(sent.Packet));
        Assert.Equal(1, response.Packet[2]);
        Assert.Equal(game.WorldId, BitConverter.ToUInt32(response.Packet, 3));
    }

    [Theory]
    [InlineData(4, 1, "")]
    [InlineData(1, 0, "")]
    [InlineData(1, 1, "unexpected")]
    public void EvenAnInvitedAccountMustUseThePlayerTransferEnvelope(int role, byte flag, string text)
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var player = f.Player("guest");
        f.Admit(game, player);

        f.Transfer(player, game.WorldId, text, flag, role);

        AssertCleared(player);
        Assert.True(f.Store.CanEnter(Account(player), game.WorldId));
        Assert.Contains(f.Sent, sent => sent.Connection == player && IsTransferReply(sent.Packet) && sent.Packet[2] == 1);
    }

    [Theory]
    [InlineData(GameWorldCatalog.DuosGameModeId, MatchMode.Duos)]
    [InlineData(GameWorldCatalog.FivesGameModeId, MatchMode.Fives)]
    public void PlayerRedemptionQueuesAndTransfersUsingTheHostsChosenGameMode(uint gameMode, MatchMode mode)
    {
        using var f = new Fixture();
        var game = f.Host("host", gameMode);
        var player = f.Player("guest");
        f.Admit(game, player);

        f.Transfer(player, game.WorldId);

        Assert.Equal("Queued", Phase(player));
        Assert.Equal(MatchQueueKind.Hosted, Admission(player).QueueKind);
        Assert.Equal(mode, Admission(player).Mode);
        Assert.Equal(game.Id, Get<string>(player.Tag!, "HostedGameId"));
        Assert.Equal(gameMode, (uint)Call(f.Service, "AdmittedGameMode", player.Tag)!);
        Assert.True(SpinWait.SpinUntil(() => !f.Pending.IsEmpty, 2000));
        while (f.Pending.TryDequeue(out var callback)) callback();
        var updates = f.Sent.Where(sent => sent.Connection == player && sent.Packet.Length > 3
            && sent.Packet[1] == 0xa6 && sent.Packet[2] == 8).ToArray();
        Assert.NotEmpty(updates);
        Assert.All(updates, update => Assert.Equal(gameMode, BitConverter.ToUInt32(update.Packet, 7)));

        f.Transfer(player, game.WorldId);

        Assert.Equal("Transferring", Phase(player));
        Assert.Contains(f.Sent, sent => sent.Connection == player && IsTransferReply(sent.Packet) && sent.Packet[2] == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryAccessExpiringAfterQueuePreventsAcceptance(bool directAccept)
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var player = f.Player("guest");
        f.Admit(game, player, TimeSpan.FromMinutes(30));
        f.Transfer(player, game.WorldId);
        Assert.Equal("Queued", Phase(player));
        f.Now += TimeSpan.FromMinutes(30);

        if (directAccept) Call(f.Service, "AcceptQueuedMatch", player, player.Tag);
        else f.Transfer(player, game.WorldId);

        AssertCleared(player);
        Assert.Equal(ConnectionState.Open, player.State);
        Assert.DoesNotContain(f.Sent, sent => sent.Connection == player && IsTransferReply(sent.Packet) && sent.Packet[2] == 0);
    }

    [Fact]
    public void RevokingAParentHostKeyEjectsItsPlayersAndLeavesAnotherHostsGameRunning()
    {
        using var f = new Fixture();
        var first = f.Host("first-host");
        var second = f.Host("second-host");
        var affected = f.Player("first-guest");
        var unaffected = f.Player("second-guest");
        f.Admit(first, affected);
        f.Admit(second, unaffected);
        f.Transfer(affected, first.WorldId);
        f.Transfer(unaffected, second.WorldId);
        SetPhase(affected, "InMatch");
        SetPhase(unaffected, "InMatch");
        var otherAdmission = Admission(unaffected);
        Assert.True(f.Store.RevokeKey("administrator", true, first.HostKeyId).Success);

        Assert.False(f.Validate(affected));
        Assert.True(f.Validate(unaffected));

        AssertCleared(affected);
        Assert.Equal(ConnectionState.Closed, affected.State);
        Assert.Equal(ConnectionState.Open, unaffected.State);
        Assert.Equal("InMatch", Phase(unaffected));
        Assert.Equal(otherAdmission, Admission(unaffected));
        Assert.Equal(second.Id, Get<string>(unaffected.Tag!, "HostedGameId"));
    }

    [Fact]
    public void PartyAdmissionRefusesEveryMemberUntilEveryAccountHasAnInvitation()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var leader = f.Player("leader");
        var guest = f.Player("guest");
        Set(leader.Tag!, "AppearanceReadySent", true);
        Set(guest.Tag!, "AppearanceReadySent", true);
        f.Admit(game, leader);
        f.Party(leader, guest);

        f.Transfer(leader, game.WorldId);

        AssertCleared(leader);
        AssertCleared(guest);
        Assert.Empty(Field<IDictionary>(f.Service, "_formingBountyMatches"));
        Assert.DoesNotContain(f.Sent, sent => IsTransferReply(sent.Packet) && sent.Packet[2] == 0);
        f.Admit(game, guest);

        f.Transfer(leader, game.WorldId);

        Assert.Equal("Queued", Phase(leader));
        Assert.Equal("Queued", Phase(guest));
        Assert.NotEqual(0ul, Admission(leader).MatchId);
        Assert.Equal(Admission(leader), Admission(guest));
        Assert.Equal(game.Id, Get<string>(guest.Tag!, "HostedGameId"));
    }

    [Fact]
    public void ReusingAWorldSlotDoesNotLetAnOldSessionStayInTheReplacementGame()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        f.Transfer(host, game.WorldId);
        SetPhase(host, "InMatch");
        Assert.True(f.Store.CloseGame("host", false, game.WorldId).Success);
        var created = f.Store.CreateGame("host", "EU", game.GameModeId, "Replacement");
        Assert.True(created.Success, created.Message);
        var replacement = Assert.IsType<HostedGameInfo>(created.Game);
        Assert.Equal(game.WorldId, replacement.WorldId);
        Assert.NotEqual(game.Id, replacement.Id);
        Assert.True(f.Store.CanEnter("host", replacement.WorldId));

        Assert.False(f.Validate(host));

        AssertCleared(host);
        Assert.Equal(ConnectionState.Closed, host.State);
    }

    [Fact]
    public void HostCannotChangeGameModeWhileAPlayerIsQueued()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(guest, game.WorldId);

        var reply = f.HostCommand(host, $"mode {game.WorldId} fives");

        Assert.False(reply.Ok);
        Assert.Contains(reply.Lines, line => line.Contains("leave", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GameWorldCatalog.DuosGameModeId, f.Store.GetGame(game.WorldId)!.GameModeId);
        Assert.Equal(MatchMode.Duos, Admission(guest).Mode);
        Call(f.Service, "AbandonMatch", guest, guest.Tag, "leave hosted queue");

        var changed = f.HostCommand(host, $"mode {game.WorldId} fives");

        Assert.True(changed.Ok, string.Join("; ", changed.Lines));
        Assert.Equal(GameWorldCatalog.FivesGameModeId, f.Store.GetGame(game.WorldId)!.GameModeId);
    }

    [Fact]
    public void PlayerTierCannotIssueHostKeysThroughTheConsoleFacade()
    {
        using var f = new Fixture();
        var player = f.Player("ordinary-player");

        var reply = f.HostCommand(player, "key EU permanent");

        Assert.False(reply.Ok);
        Assert.Empty(f.Store.ListKeys("administrator", true));
        Assert.Contains(reply.Lines, line => line.Contains("Owner or admin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(GameWorldCatalog.DuosGameModeId, "Duos")]
    [InlineData(GameWorldCatalog.FivesGameModeId, "Fives")]
    public void HostedScheduleShowsOnlyInvitedGamesAndPreservesEveryGameplayDefault(uint mode, string modeName)
    {
        using var f = new Fixture();
        var available = f.Host("first-host", mode);
        var hidden = f.Host("second-host");
        var player = f.Player("guest");

        var unavailable = Schedule(f, player);

        Assert.Empty(unavailable.Games);
        Assert.Equal(StringHashValues.Entries.Count + 2, unavailable.Labels.Count);
        Assert.Equal("0", unavailable.Labels["Cranberry.Healing"].Value);
        f.Admit(available, player, TimeSpan.FromHours(1));

        var personalized = Schedule(f, player);

        Assert.Equal(new ScheduleRow(available.WorldId, mode, true), Assert.Single(personalized.Games));
        Assert.Equal(available.Name, personalized.Labels[$"Cranberry.Hosted.{available.WorldId}.Name"].Value);
        Assert.Equal(modeName, personalized.Labels[$"Cranberry.Hosted.{available.WorldId}.Mode"].Value);
        Assert.DoesNotContain($"Cranberry.Hosted.{hidden.WorldId}.Name", personalized.Labels.Keys);
        Assert.Equal(StringHashValues.Entries.Count + 4, personalized.Labels.Count);
        f.Now += TimeSpan.FromHours(1);

        var expired = Schedule(f, player);

        Assert.Empty(expired.Games);
        Assert.Equal(StringHashValues.Entries.Count + 2, expired.Labels.Count);
    }

    [Theory]
    [InlineData(false, "Lobby")]
    [InlineData(true, "Lobby")]
    [InlineData(false, "Queued")]
    public void OnlyHostOrAdminCanStartAndEveryPlayerMustBeReady(bool administrator, string guestPhase)
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        var admin = f.Player("administrator");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        SetPhase(host, "Lobby");
        SetPhase(guest, guestPhase);
        Set(host.Tag!, "PregameClientReady", true);
        Set(guest.Tag!, "PregameClientReady", guestPhase == "Queued");

        var notOwner = f.HostCommand(guest, $"start {game.WorldId}");
        var waiting = f.HostCommand(administrator ? admin : host, $"start {game.WorldId}",
            administrator ? ConsoleTier.Admin : ConsoleTier.Player);

        Assert.False(notOwner.Ok);
        Assert.False(waiting.Ok);
        Assert.Equal("Lobby", Phase(host));
        Assert.Equal(guestPhase, Phase(guest));
        Assert.DoesNotContain(f.Sent, sent => IsStartMatch(sent.Packet));
        SetPhase(guest, "Lobby");
        Set(guest.Tag!, "PregameClientReady", true);

        var started = f.HostCommand(administrator ? admin : host, $"start {game.WorldId}",
            administrator ? ConsoleTier.Admin : ConsoleTier.Player);

        Assert.True(started.Ok, string.Join("; ", started.Lines));
        Assert.Equal("Dropping", Phase(host));
        Assert.Equal("Dropping", Phase(guest));
        Assert.Single(f.Sent, sent => sent.Connection == host && IsStartMatch(sent.Packet));
        Assert.Single(f.Sent, sent => sent.Connection == guest && IsStartMatch(sent.Packet));
        Assert.DoesNotContain(f.Sent, sent => sent.Connection == admin && IsStartMatch(sent.Packet));
    }

    [Theory]
    [InlineData("Dropping")]
    [InlineData("InMatch")]
    public void RunningHostedGamesRefuseNewInvitedPlayersAndDisableScheduleJoin(string runningPhase)
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId);
        SetPhase(host, runningPhase);

        var schedule = Schedule(f, guest);
        f.Transfer(guest, game.WorldId);

        Assert.Equal(new ScheduleRow(game.WorldId, game.GameModeId, false), Assert.Single(schedule.Games));
        AssertCleared(guest);
        Assert.True(f.Store.CanEnter("guest", game.WorldId));
        Assert.Equal(runningPhase, Phase(host));
        Assert.Contains(f.Sent, sent => sent.Connection == guest && IsTransferReply(sent.Packet) && sent.Packet[2] == 1);
    }

    [Fact]
    public void HostedLobbyDisplaysWaitingForPlayersUntilItsHostStartsIt()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        f.Transfer(host, game.WorldId);
        f.Service.Post = null;
        f.Sent.Clear();

        Call(f.Service, "SendLobbyHud", host, host.Tag);

        Assert.Equal("Lobby", Phase(host));
        var countdown = Assert.Single(f.Sent, sent => sent.Connection == host && sent.Packet.Length > 15
            && sent.Packet[1] == 0xce && sent.Packet[2] == 0x0f).Packet;
        Assert.Equal(0u, BitConverter.ToUInt32(countdown, 8));
        Assert.Equal(Cranberry.Zone.Generated.AugustStrings.HudLabels.WaitingForPlayers, BitConverter.ToUInt32(countdown, 12));
        Assert.DoesNotContain(f.Sent, sent => IsStartMatch(sent.Packet));
    }

    [Fact]
    public void LegacyConsoleStartMatchStartsTheWholeHostedLobbyTogether()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        SetPhase(host, "Lobby");
        SetPhase(guest, "Lobby");
        Set(host.Tag!, "PregameClientReady", true);
        Set(guest.Tag!, "PregameClientReady", true);
        Get<ConsoleSession>(host.Tag!, "DevConsole").TierOverride = ConsoleTier.Owner;
        var context = (ConsoleContext)Call(f.Service, "BuildConsoleContext", host, host.Tag)!;
        f.Sent.Clear();

        var result = context.StartMatch!.Invoke();

        Assert.True(result.Ok, string.Join("; ", result.Lines));
        Assert.Equal("Dropping", Phase(host));
        Assert.Equal("Dropping", Phase(guest));
        Assert.Single(f.Sent, sent => sent.Connection == host && IsStartMatch(sent.Packet));
        Assert.Single(f.Sent, sent => sent.Connection == guest && IsStartMatch(sent.Packet));
    }

    [Fact]
    public void EndedHostedPlayerWaitsForAChoiceWhilePeersPlay()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var ended = f.Player("host");
        var playing = f.Player("guest");
        f.Admit(game, playing);
        f.Transfer(ended, game.WorldId);
        f.Transfer(playing, game.WorldId);
        SetPhase(ended, "Ended");
        SetPhase(playing, "InMatch");
        var otherAdmission = Admission(playing);
        f.Sent.Clear();

        Call(f.Service, "CompleteEndedHold", ended, ended.Tag);

        Assert.Equal("Ended", Phase(ended));
        Assert.Equal(ConnectionState.Open, ended.State);
        var outgoing = f.Sent.Where(sent => sent.Connection == ended).Select(sent => sent.Packet).ToArray();
        Assert.DoesNotContain(outgoing, packet => packet.AsSpan(1).SequenceEqual(new byte[] { 0xce, 0x1b, 0 }));
        Assert.DoesNotContain(outgoing, packet => packet.Length > 3 && packet[1] == 0xce
            && packet[2] is 0x0f or 0x16);
        Assert.Equal("InMatch", Phase(playing));
        Assert.Equal(ConnectionState.Open, playing.State);
        Assert.Equal(otherAdmission, Admission(playing));
        Assert.Equal(game.Id, Get<string>(playing.Tag!, "HostedGameId"));
        Assert.True(f.Validate(playing));
    }
}
