using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class WorldModeRoutingTests
{
    private sealed class Fixture : IPacketRecorder, ITransportLog
    {
        public ZoneService Service { get; }
        public ConcurrentQueue<Action> Pending { get; } = new();
        public List<byte[]> Sent { get; } = [];
        public List<(SoeConnection Connection, byte[] Bytes)> Recorded { get; } = [];
        private int _next;
        public Fixture(bool enableGas = false) => Service = new(this, this, new GatewayTicketRegistry(), new ZoneOptions
        {
            EnableGas = enableGas, SendContainers = false, SendDoors = false, SendVehicles = false,
        }) { Post = Pending.Enqueue };
        public SoeConnection Player()
        {
            var request = new SessionRequest(3, (uint)++_next, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 10000 + _next), in request,
                new(), SessionDecision.Clear, Service, this, (_, _) => { }, 0);
            Service.OnConnected(connection);
            Set(connection.Tag!, "Authenticated", true);
            Set(connection.Tag!, "Guid", (ulong)_next);
            Set(connection.Tag!, "CharacterName", "Player " + _next);
            Set(connection.Tag!, "Visuals", CharacterVisuals.FromSelection(1, 1, 1, 0, 0));
            Set(connection.Tag!, "Wardrobe", new AugustWardrobeState());
            return connection;
        }
        public void Transfer(SoeConnection connection, uint worldId, byte flag = 1)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
            writer.WriteByte(0xec); writer.WriteUInt32(worldId); writer.WriteString("");
            writer.WriteUInt32(0); writer.WriteByte(flag); writer.WriteInt32(1);
            Service.OnMessage(connection, writer.Written.ToArray());
        }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction != "s2c") return;
            byte[] packet = bytes.ToArray();
            Sent.Add(packet);
            Recorded.Add((connection, packet));
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
    private static object? Call(ZoneService service, string name, params object?[] args) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args);
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);

    private static void RegisterAccounts(Fixture f, params SoeConnection[] players)
    {
        var sessions = (IDictionary)typeof(ZoneService).GetField("_accountSessions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(f.Service)!;
        foreach (var player in players)
        {
            Set(player.Tag!, "AccountId", "routing-test-" + Get<ulong>(player.Tag!, "Guid"));
            sessions.Add(player, player.Tag);
        }
    }

    private static (Fixture Fixture, SoeConnection[] Players) TeamMatch(uint world, int count)
    {
        var f = new Fixture();
        var players = Enumerable.Range(0, count).Select(_ => f.Player()).ToArray();
        RegisterAccounts(f, players);
        foreach (var player in players)
        {
            f.Transfer(player, world); f.Transfer(player, world);
            var phase = player.Tag!.GetType().GetProperty("Match")!;
            phase.SetValue(player.Tag, Enum.Parse(phase.PropertyType, "Lobby"));
        }
        foreach (var player in players) f.Service.ForTest(player).EnterMatch();
        f.Recorded.Clear();
        f.Sent.Clear();
        return (f, players);
    }

    private sealed record NativeResult(uint PlayerRank, uint TeamRank, uint GroupId,
        bool PlayerFinished, bool TeamFinished, uint LivingTeamMembers);

    private static NativeResult LastResult(Fixture f, SoeConnection player)
    {
        var packet = f.Recorded.Last(record => record.Connection == player
            && record.Bytes.Length > 3 && record.Bytes[1] == 0x67 && record.Bytes[2] == 8).Bytes;
        var reader = new PacketReader(packet.AsSpan(1));
        reader.Skip(18);
        uint rank = reader.ReadUInt32(), teamRank = reader.ReadUInt32();
        reader.Skip(15 * 4);
        uint group = reader.ReadUInt32();
        reader.Skip(4 + 9);
        bool playerFinished = reader.ReadBool(), teamFinished = reader.ReadBool();
        uint living = reader.ReadUInt32();
        Assert.True(reader.AtEnd);
        return new(rank, teamRank, group, playerFinished, teamFinished, living);
    }

    [Fact]
    public void PracticeKillFeedReachesSameMatchOnceAndDoesNotLeakToAnotherRound()
    {
        var (f, players) = TeamMatch(1, 3);
        var admission = Get<MatchAdmissionContext>(players[2].Tag!, "BountyAdmission");
        Set(players[2].Tag!, "BountyAdmission", admission with { MatchId = admission.MatchId + 1 });
        ulong dummy = f.Service.ForTest(players[0]).EnterMatch(dummies: 1)[0];
        f.Service.ForTest(players[0]).KillTarget(dummy);
        f.Service.ForTest(players[0]).KillTarget(dummy);
        var feeds = f.Recorded.Where(r => r.Bytes.Length > 4 && r.Bytes[1] == 0xce
            && r.Bytes[2] == 0x0e && r.Bytes[3] == 0).ToArray();
        Assert.Single(feeds, r => r.Connection == players[0]);
        Assert.Single(feeds, r => r.Connection == players[1]);
        Assert.DoesNotContain(feeds, r => r.Connection == players[2]);
        Assert.Equal(feeds[0].Bytes, feeds[1].Bytes);
    }

    [Theory]
    [InlineData(6u, 2)]
    [InlineData(7u, 5)]
    public void DeadTeammateGetsNativeSpectateTriggerThenOneTeamVictoryWithOwnRankPreserved(uint world, int size)
    {
        var (f, players) = TeamMatch(world, size + 1);
        ulong target = f.Service.ForTest(players[0]).EnterMatch(dummies: 1)[0];
        f.Service.ForTest(players[0]).KillTarget(target);
        f.Service.ForTest(players[0]).Damage(10_000, DamageCause.ToxicGas);
        var pending = LastResult(f, players[0]);
        Assert.Equal((uint)players.Length, pending.PlayerRank);
        Assert.Equal(0u, pending.TeamRank);
        Assert.True(pending.PlayerFinished);
        Assert.False(pending.TeamFinished);
        Assert.Equal((uint)size - 1, pending.LivingTeamMembers);
        Assert.Equal(0x40000001u, pending.GroupId);
        Assert.False(Get<MatchScore>(players[0].Tag!, "Score").Settled);

        // August's 67/08 applier requires PlayerRank>1 before emitting GROUP_MEMBER_DEATH.
        Assert.True(pending.PlayerRank > 1 && pending.PlayerFinished);
        f.Service.ForTest(players[^1]).Damage(10_000, DamageCause.ToxicGas);
        foreach (var winner in players[..size])
        {
            var result = LastResult(f, winner);
            Assert.Equal(1u, result.TeamRank);
            Assert.True(result.TeamFinished); // GROUP_TEAM_WIN; independent of personal death.
            Assert.Equal(0u, result.LivingTeamMembers);
            Assert.Equal(winner == players[0] ? (uint)players.Length : 1u, result.PlayerRank);
            Assert.Single(f.Recorded, record => record.Connection == winner && record.Bytes.Length == 110
                && record.Bytes[1] == 0x67 && record.Bytes[2] == 8 && record.Bytes[105] != 0);
            var deliveries = f.Recorded.Where(record => record.Connection == winner).Select(record => record.Bytes).ToArray();
            int memberIndex = Array.FindIndex(deliveries, packet => packet.Length > 3 && packet[1] == 0x67 && packet[2] == 0x22);
            int panelIndex = Array.FindIndex(deliveries, packet => packet.Length == 5 && packet[1] == 0xce && packet[2] == 0x1a);
            int finalIndex = Array.FindIndex(deliveries, packet => packet.Length == 110 && packet[1] == 0x67
                && packet[2] == 8 && packet[105] != 0);
            Assert.InRange(memberIndex, 0, finalIndex - 1);
            Assert.InRange(panelIndex, 0, memberIndex - 1);
            var members = new PacketReader(deliveries[memberIndex].AsSpan(1));
            members.Skip(2);
            Assert.Equal((uint)size, members.ReadUInt32());
            for (int index = 0; index < size; index++)
            {
                Assert.Equal("Player " + (index + 1), members.ReadString());
                Assert.Equal(0ul, members.ReadUInt64());
                Assert.Equal(Get<ulong>(players[index].Tag!, "Guid"), members.ReadUInt64());
                Assert.Equal(index == 0 ? (uint)players.Length : 1u, members.ReadUInt32());
                Assert.Equal(index == 0 ? 1u : 0u, members.ReadUInt32()); // dummy credit survives the player's death
                Assert.Equal(index == 0 ? 1_000u : 0u, members.ReadUInt32());
                Assert.Equal(0u, members.ReadUInt32());
                Assert.Equal(0u, members.ReadUInt32());
                Assert.Equal(index == 0 ? 176_000u : 175_000u, members.ReadUInt32());
                Assert.Equal(players[index] == winner, members.ReadBool());
            }
            Assert.True(members.AtEnd);
        }
        Assert.DoesNotContain(f.Sent, bytes => bytes.Length > 3 && bytes[1] == 0xce && bytes[2] == 0x18);
        Get<MatchScore>(players[0].Tag!, "Score").Reset();
        Assert.Null(Get<MatchScore>(players[0].Tag!, "Score").PlayerPlacement);
    }

    [Theory]
    [InlineData(6u, 2)]
    [InlineData(7u, 5)]
    public void EliminatedSquadPublishesDefeatWithoutTriggeringAVictory(uint world, int size)
    {
        var (f, players) = TeamMatch(world, size * 2 + 1);
        foreach (var loser in players[..size]) f.Service.ForTest(loser).Damage(10_000, DamageCause.ToxicGas);
        foreach (var loser in players[..size])
        {
            var result = LastResult(f, loser);
            Assert.Equal(3u, result.TeamRank);
            Assert.True(result.TeamFinished); // GROUP_TEAM_DEFEAT, never rank 1 / GROUP_TEAM_WIN.
            Assert.True(result.PlayerRank > result.TeamRank);
            Assert.Equal(0u, result.LivingTeamMembers);
            Assert.False(Get<bool>(loser.Tag!, "VictorySent"));
            var deliveries = f.Recorded.Where(record => record.Connection == loser).Select(record => record.Bytes).ToArray();
            int panels = Array.FindIndex(deliveries, packet => packet.Length == 5 && packet[1] == 0xce && packet[2] == 0x1a);
            int members = Array.FindIndex(deliveries, packet => packet.Length > 3 && packet[1] == 0x67 && packet[2] == 0x22);
            int final = Array.FindIndex(deliveries, packet => packet.Length == 110 && packet[1] == 0x67
                && packet[2] == 8 && packet[105] != 0);
            Assert.InRange(panels, 0, members - 1);
            Assert.InRange(members, 0, final - 1);
        }
        Assert.All(players[size..], survivor => Assert.False(Get<MatchScore>(survivor.Tag!, "Score").Settled));
        Assert.DoesNotContain(f.Sent, bytes => bytes.Length > 3 && bytes[1] == 0xce && bytes[2] == 0x18);
    }

    [Theory]
    [InlineData(6u, 2)]
    [InlineData(7u, 5)]
    public void TeamScoresUseTheWholeSquadAndKeepLivingTeammatesWhenOnePlayerDies(uint world, int size)
    {
        var f = new Fixture();
        var players = Enumerable.Range(0, size + 1).Select(_ => f.Player()).ToArray();
        RegisterAccounts(f, players);
        foreach (var player in players)
        {
            f.Transfer(player, world); f.Transfer(player, world);
            var phase = player.Tag!.GetType().GetProperty("Match")!;
            phase.SetValue(player.Tag, Enum.Parse(phase.PropertyType, "Lobby"));
        }
        Get<MatchScore>(players[0].Tag!, "Score").Credit(1000, false);
        Get<MatchScore>(players[1].Tag!, "Score").Credit(1001, false);
        Get<MatchScore>(players[1].Tag!, "Score").Credit(1002, false);
        Set(players[1].Tag!, "DeathSent", true);
        Call(f.Service, "PublishScore", players[1], players[1].Tag, 0u, false);
        byte[] packet = f.Sent.Last(bytes => bytes.Length > 3 && bytes[1] == 0x67 && bytes[2] == 8);
        var reader = new PacketReader(packet.AsSpan(1));
        reader.Skip(18);
        uint[] words = new uint[19];
        for (int i = 0; i < words.Length; i++) words[i] = reader.ReadUInt32();
        Assert.Equal(2u, words[3]); // individual's kills
        Assert.Equal(3u, words[9]); // squad's kills
        Assert.Equal((uint)(size + 1), words[13]); // roster includes the dead member
        Assert.Equal((uint)size, words[15]);
        Assert.Equal(2u, words[16]);
        reader.Skip(9);
        Assert.True(reader.ReadBool());
        Assert.False(reader.ReadBool());
        Assert.Equal((uint)(size - 1), reader.ReadUInt32());
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void CancelAndRequeueSameFormingMatchDoesNotReviveTheOldQueueAttempt()
    {
        var f = new Fixture(); var player = f.Player(); var teammate = f.Player();
        RegisterAccounts(f, player, teammate);
        f.Transfer(player, 6);
        Assert.True(SpinWait.SpinUntil(() => !f.Pending.IsEmpty, 2000));
        Assert.True(f.Pending.TryDequeue(out var oldQueueTick));
        f.Transfer(teammate, 6);
        ulong formingId = Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission").MatchId;

        Call(f.Service, "AbandonMatch", player, player.Tag, "cancel first queue attempt");
        f.Transfer(player, 6);
        Assert.Equal(formingId, Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission").MatchId);
        Assert.Equal("Queued", Get<object>(player.Tag!, "Match").ToString());
        int sentBeforeStaleCallback = f.Sent.Count;
        oldQueueTick();
        Assert.Equal(sentBeforeStaleCallback, f.Sent.Count);
    }

    [Fact]
    public void ResultsHoldDoesNotCreateAnUnrequestedMatchOrTeam()
    {
        var f = new Fixture(); var player = f.Player();
        RegisterAccounts(f, player);
        f.Transfer(player, 6); f.Transfer(player, 6);
        ulong oldId = Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission").MatchId;
        var phase = player.Tag!.GetType().GetProperty("Match")!;
        phase.SetValue(player.Tag, Enum.Parse(phase.PropertyType, "Ended"));
        Call(f.Service, "CompleteEndedHold", player, player.Tag);

        ulong newId = Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission").MatchId;
        Assert.Equal(oldId, newId);
        Assert.Equal(MatchMode.Duos, Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission").Mode);
        Assert.Equal("Ended", Get<object>(player.Tag!, "Match").ToString());
        var membership = (IDictionary)typeof(ZoneService).GetField("_sharedLootMembership", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(f.Service)!;
        Assert.True(membership.Contains(player.Tag));
        Assert.Equal(newId, membership[player.Tag]);
        var matches = (IDictionary)typeof(ZoneService).GetField("_sharedLootMatches", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(f.Service)!;
        object match = matches[newId]!;
        var teams = (IDictionary)match.GetType().GetProperty("Teams")!.GetValue(match)!;
        Assert.True(teams.Contains(player.Tag));
    }

    [Theory]
    [InlineData(1u, 13u, MatchMode.Solo)]
    [InlineData(6u, 5u, MatchMode.Duos)]
    [InlineData(7u, 7u, MatchMode.Fives)]
    [InlineData(9u, 13u, MatchMode.Solo)]
    [InlineData(10u, 5u, MatchMode.Duos)]
    [InlineData(11u, 7u, MatchMode.Fives)]
    public void NativeTransferQueuesAndAcknowledgesTheSelectedWorld(uint worldId, uint gameMode, MatchMode mode)
    {
        var f = new Fixture();
        var player = f.Player();
        f.Transfer(player, worldId);
        Assert.Equal("Queued", Get<object>(player.Tag!, "Match").ToString());
        var admission = Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission");
        Assert.Equal(mode, admission.Mode);
        Assert.Equal(MatchQueueKind.Public, admission.QueueKind);
        Assert.True(SpinWait.SpinUntil(() => !f.Pending.IsEmpty, 2000));
        while (f.Pending.TryDequeue(out var work)) work();
        var queue = Assert.Single(f.Sent, bytes => bytes.Length > 3 && bytes[1] == 0xa6 && bytes[2] == 8);
        Assert.Equal(gameMode, BitConverter.ToUInt32(queue, 7));
        f.Transfer(player, worldId);
        Assert.Equal("Transferring", Get<object>(player.Tag!, "Match").ToString());
        var reply = Assert.Single(f.Sent, bytes => bytes.Length == 7 && bytes[1] == 0xed);
        Assert.Equal(0, reply[2]);
        uint canonical = worldId switch { 9 => 1, 10 => 6, 11 => 7, _ => worldId };
        Assert.Equal(canonical, BitConverter.ToUInt32(reply, 3));
    }

    [Fact]
    public void ZoningPublishesTheWorldLabelBeforeTheClientRebuildsItsHud()
    {
        var f = new Fixture(); var player = f.Player();
        f.Transfer(player, 11); f.Transfer(player, 11);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (f.Pending.TryDequeue(out var work)) work();
            return f.Sent.Any(bytes => bytes.Length > 1 && bytes[1] == 0xfb);
        }, 3000));
        int labelIndex = f.Sent.FindIndex(bytes => bytes.Length > 1 && bytes[1] == 0xfb);
        int zoningIndex = f.Sent.FindIndex(bytes => bytes.Length > 1 && bytes[1] == ZoneOpcodes.ClientBeginZoning);
        Assert.True(zoningIndex > labelIndex);
        Assert.Contains("Fives EU World 1", System.Text.Encoding.UTF8.GetString(f.Sent[labelIndex]));
    }

    private static void Party(Fixture f, SoeConnection leader, params SoeConnection[] others)
    {
        var registry = (PartyRegistry)typeof(ZoneService).GetField("_parties", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Service)!;
        foreach (var member in others)
        {
            var invite = registry.Invite(Get<ulong>(leader.Tag!, "Guid"), Get<ulong>(member.Tag!, "Guid"), 0)!;
            Assert.NotNull(registry.Respond(invite.Token, invite.Invitee, true, 1));
        }
    }

    [Theory]
    [InlineData(6u, 2)]
    [InlineData(7u, 5)]
    public void LeaderQueuesAndTransfersWholePartyWithoutSplittingItAcrossTeams(uint worldId, int count)
    {
        var f = new Fixture(); var lone = f.Player();
        var party = Enumerable.Range(0, count).Select(_ => f.Player()).ToArray();
        RegisterAccounts(f, [lone, .. party]);
        foreach (var member in party) Set(member.Tag!, "AppearanceReadySent", true);
        f.Transfer(lone, worldId); f.Transfer(lone, worldId);
        Party(f, party[0], party[1..]);
        f.Transfer(party[0], worldId);
        Assert.All(party, player => Assert.Equal("Queued", Get<object>(player.Tag!, "Match").ToString()));
        Assert.Single(party.Select(player => Get<MatchAdmissionContext>(player.Tag!, "BountyAdmission").MatchId).Distinct());
        f.Transfer(party[1], worldId);
        Assert.All(party, player => Assert.Equal("Queued", Get<object>(player.Tag!, "Match").ToString()));
        f.Transfer(party[0], worldId);
        Assert.All(party, player => Assert.Equal("Transferring", Get<object>(player.Tag!, "Match").ToString()));
        var teams = party.Select(player => (ulong)Call(f.Service, "TeamGuid", player.Tag)!).Distinct().ToArray();
        Assert.Single(teams);
        Assert.NotEqual((ulong)Call(f.Service, "TeamGuid", lone.Tag)!, teams[0]);
    }

    [Fact]
    public void PartyCannotJoinAnUndersizedModeAndCancellationReleasesEveryone()
    {
        var f = new Fixture(); var players = Enumerable.Range(0, 3).Select(_ => f.Player()).ToArray();
        RegisterAccounts(f, players); Party(f, players[0], players[1..]);
        f.Transfer(players[0], 6);
        Assert.All(players, player => Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString()));
        f.Transfer(players[1], 7);
        Assert.All(players, player => Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString()));
        f.Transfer(players[0], 7);
        Call(f.Service, "AbandonMatch", players[1], players[1].Tag, "cancel party test");
        Assert.All(players, player => Assert.Equal("Menu", Get<object>(player.Tag!, "Match").ToString()));
        Assert.All(players, player => Assert.Equal(0u, Get<uint>(player.Tag!, "MatchPartyId")));
    }

    [Fact]
    public void ChangedOrMalformedQueueAcceptanceCannotSwitchWorlds()
    {
        var f = new Fixture(); var player = f.Player();
        f.Transfer(player, 6); f.Transfer(player, 7); f.Transfer(player, 6, flag: 0);
        Assert.Equal("Queued", Get<object>(player.Tag!, "Match").ToString());
        Assert.Equal(6u, Get<uint>(player.Tag!, "BountyWorldId"));
        Assert.DoesNotContain(f.Sent, bytes => bytes.Length > 1 && bytes[1] == 0xed);
        f.Transfer(player, 6);
        Assert.Equal("Transferring", Get<object>(player.Tag!, "Match").ToString());
    }

    [Fact]
    public void PlayKeepsTheSelectedModeAndOpensAnIndependentRound()
    {
        var f = new Fixture(); var first = f.Player(); var next = f.Player();
        RegisterAccounts(f, [first, next]);
        f.Transfer(first, 7); f.Transfer(first, 7);
        var phase = first.Tag!.GetType().GetProperty("Match")!;
        phase.SetValue(first.Tag, Enum.Parse(phase.PropertyType, "InMatch"));
        Call(f.Service, "LockBackingForDrop", first, first.Tag);
        f.Transfer(next, 7);
        Assert.Equal(7u, Get<uint>(next.Tag!, "BountyWorldId"));
        f.Transfer(next, 7); // Native queue confirmation still carries the original selection.
        Assert.Equal("Transferring", Get<object>(next.Tag!, "Match").ToString());
        Assert.Equal(2, f.Sent.Count(bytes => bytes.Length == 7 && bytes[1] == 0xed));
        Assert.NotEqual(Get<MatchAdmissionContext>(first.Tag!, "BountyAdmission").MatchId,
            Get<MatchAdmissionContext>(next.Tag!, "BountyAdmission").MatchId);
    }

    [Fact]
    public void EliminatedPlayerWaitsForAChoiceWhileTheirOldRoundIsLive()
    {
        var (f, players) = TeamMatch(1, 3);
        var first = players[0]; var survivor = players[1];
        Call(f.Service, "LockBackingForDrop", first, first.Tag);
        f.Service.ForTest(first).Damage(10000, DamageCause.ToxicGas);
        Call(f.Service, "CompleteEndedHold", first, first.Tag);
        Assert.Equal("Ended", Get<object>(first.Tag!, "Match").ToString());
        Assert.Equal(Get<MatchAdmissionContext>(survivor.Tag!, "BountyAdmission").MatchId,
            Get<MatchAdmissionContext>(first.Tag!, "BountyAdmission").MatchId);
        f.Transfer(first, 1);
        Assert.Equal("Ended", Get<object>(first.Tag!, "Match").ToString());
        Assert.Equal("InMatch", Get<object>(survivor.Tag!, "Match").ToString());
    }

    [Fact]
    public void FormingMatchesAndDoorStateAreSeparatedByMode()
    {
        var f = new Fixture();
        var players = Enumerable.Range(0, 3).Select(_ => f.Player()).ToArray();
        f.Transfer(players[0], 6); f.Transfer(players[1], 10); f.Transfer(players[2], 7);
        ulong Id(int index) => Get<MatchAdmissionContext>(players[index].Tag!, "BountyAdmission").MatchId;
        Assert.Equal(Id(0), Id(1)); Assert.NotEqual(Id(0), Id(2));
        var doors = players.Select(player => (MatchDoors)Call(f.Service, "EnsureMatchDoors", player, player.Tag)!).ToArray();
        Assert.Same(doors[0].Shared, doors[1].Shared);
        Assert.NotSame(doors[0].Shared, doors[2].Shared);
        foreach (var player in players) Call(f.Service, "LeaveSharedDoors", player.Tag);
        var worlds = (IDictionary)typeof(ZoneService).GetField("_sharedDoorMatches", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Service)!;
        Assert.Empty(worlds);
    }

    [Fact]
    public void GasJoinsOnlyTheSameMatchAndDepartureRetainsOtherWorlds()
    {
        var f = new Fixture();
        var players = Enumerable.Range(0, 3).Select(_ => f.Player()).ToArray();
        f.Transfer(players[0], 1); f.Transfer(players[1], 9); f.Transfer(players[2], 6);
        object?[] first = [players[0].Tag, 11ul, 100L];
        object?[] same = [players[1].Tag, 22ul, 200L];
        object?[] other = [players[2].Tag, 33ul, 300L];
        Assert.False((bool)Call(f.Service, "JoinSharedGas", first)!);
        Assert.True((bool)Call(f.Service, "JoinSharedGas", same)!);
        Assert.Equal(11ul, same[1]); Assert.Equal(100L, same[2]);
        Assert.False((bool)Call(f.Service, "JoinSharedGas", other)!);
        Assert.Equal(33ul, other[1]); Assert.Equal(300L, other[2]);
        Call(f.Service, "LeaveSharedGas", players[0].Tag);
        Call(f.Service, "LeaveSharedGas", players[1].Tag);
        var worlds = (IDictionary)typeof(ZoneService).GetField("_sharedGasMatches", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Service)!;
        Assert.Single(worlds);
        Call(f.Service, "LeaveSharedGas", players[2].Tag);
        Assert.Empty(worlds);
    }
}
