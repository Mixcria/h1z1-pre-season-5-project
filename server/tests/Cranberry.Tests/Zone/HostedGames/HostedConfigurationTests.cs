using System.Collections;
using System.Text.Json.Nodes;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.HostedGames;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed partial class HostedGameGatewayTests
{
    private static HostedGameInfo ConfiguredGame(Fixture f, int minutes = 5, int maxPlayers = 150)
    {
        var key = f.Store.IssueHostKey("admin", true, "EU", targetAccount: "host");
        Assert.True(f.Store.Redeem("host", key.Secret!).Success);
        var result = f.Store.CreateGame("host", "EU", GameWorldCatalog.DuosGameModeId, "Configured friends", minutes, maxPlayers);
        Assert.True(result.Success, result.Message);
        return result.Game!;
    }

    private static object SharedMatch(Fixture f, SoeConnection player) =>
        Field<IDictionary>(f.Service, "_sharedLootMatches")[Admission(player).MatchId]!;

    private static void HostedLobby(Fixture f, SoeConnection player, bool ready = true)
    {
        Set(player.Tag!, "PregameClientReady", ready);
        Call(f.Service, "SendLobbyHud", player, player.Tag);
    }

    [Fact]
    public void CreationFormPersistsModeDurationCapacityAndPanelFields()
    {
        using var f = new Fixture();
        var key = f.Store.IssueHostKey("admin", true, "EU", targetAccount: "host");
        Assert.True(f.Store.Redeem("host", key.Secret!).Success);
        var host = f.Player("host");
        var result = f.HostCommand(host, "createform EU fives 12 37 Friday friends");
        Assert.True(result.Ok, string.Join(";", result.Lines));
        var game = Assert.Single(f.Store.ListGames("host"));
        Assert.Equal(GameWorldCatalog.FivesGameModeId, game.GameModeId);
        Assert.Equal(12, game.QueueDurationMinutes);
        Assert.Equal(37, game.MaxPlayers);
        Assert.Equal("Friday friends", game.Name);
        var rows = PanelRows(PanelValues(f, host));
        Assert.Equal(game.WorldId.ToString(), rows[0][2]);
        var row = Assert.Single(rows, row => row[0] == "G");
        Assert.Equal("12", row[9]);
        Assert.Equal("37", row[10]);
        Assert.Contains(result.Lines, line => line.Contains("12 minute(s)") && line.Contains("37 players"));
        Assert.StartsWith("1|", PanelValues(f, host)["Cranberry.Hosted.CreateResult"]);
        Assert.Equal("1", PanelValues(f, host)["Cranberry.Hosted.CreateSequence"]);
        Assert.False(f.HostCommand(host, "createform EU event 5 150 Unsupported").Ok);
        Assert.StartsWith("0|", PanelValues(f, host)["Cranberry.Hosted.CreateResult"]);
        Assert.Equal("2", PanelValues(f, host)["Cranberry.Hosted.CreateSequence"]);
        f.Sent.Clear();
        f.HostCommand(host, "panel");
        Assert.DoesNotContain("Cranberry.Hosted.CreateResult", PanelValues(f, host).Keys);
    }

    [Theory]
    [InlineData("0", "150")]
    [InlineData("61", "150")]
    [InlineData("1", "0")]
    [InlineData("1", "151")]
    [InlineData("1.5", "25")]
    [InlineData("NaN", "25")]
    public void InvalidCreationFormNeverConsumesAHostedSlot(string duration, string maximum)
    {
        using var f = new Fixture();
        var key = f.Store.IssueHostKey("admin", true, "EU", targetAccount: "host");
        Assert.True(f.Store.Redeem("host", key.Secret!).Success);
        Assert.False(f.HostCommand(f.Player("host"), $"createform EU solo {duration} {maximum} Friends").Ok);
        Assert.Empty(f.Store.ListGames("host"));
    }

    [Fact]
    public void NativeCreationRepliesCorrelateSuccessAndValidationFailureWithoutReplayingOldResults()
    {
        using var f = new Fixture();
        var key = f.Store.IssueHostKey("admin", true, "EU", targetAccount: "host");
        Assert.True(f.Store.Redeem("host", key.Secret!).Success);
        var host = f.Player("host");
        Assert.True(f.HostCommand(host, "createui aF01 EU duos 5 24 Friends").Ok);
        Assert.StartsWith("aF01|1|", PanelValues(f, host)["Cranberry.Hosted.CreateResult"]);
        Assert.Equal("1", PanelValues(f, host)["Cranberry.Hosted.CreateSequence"]);
        var game = Assert.Single(f.Store.ListGames("host"));
        Assert.Equal(5, game.QueueDurationMinutes); Assert.Equal(24, game.MaxPlayers);
        Assert.False(f.HostCommand(host, "createui aF02 EU event 5 24 Invalid mode").Ok);
        Assert.StartsWith("aF02|0|", PanelValues(f, host)["Cranberry.Hosted.CreateResult"]);
        Assert.Equal("2", PanelValues(f, host)["Cranberry.Hosted.CreateSequence"]);
        f.Sent.Clear();
        Assert.False(f.HostCommand(host, "createui invalid-nonce EU solo 5 24 Refused").Ok);
        Assert.DoesNotContain("Cranberry.Hosted.CreateResult", PanelValues(f, host).Keys);
        f.HostCommand(host, "panel");
        Assert.DoesNotContain("Cranberry.Hosted.CreateResult", PanelValues(f, host).Keys);
        Assert.Single(f.Store.ListGames("host"));
    }

    [Fact]
    public void CapacityIncludesQueuedPlayersAndReleasesCancelledReservations()
    {
        using var f = new Fixture();
        var game = ConfiguredGame(f, maxPlayers: 2);
        var host = f.Player("host");
        var guest = f.Player("guest");
        var next = f.Player("next");
        f.Admit(game, guest); f.Admit(game, next);
        f.Transfer(host, game.WorldId); f.Transfer(guest, game.WorldId);
        Assert.False(Assert.Single(Schedule(f, next).Games).CanEnter);
        var refused = f.HostCommand(next, $"join {game.WorldId}");
        Assert.False(refused.Ok);
        Assert.Contains(refused.Lines, line => line.Contains("full"));
        f.Transfer(next, game.WorldId);
        AssertCleared(next);
        Assert.Equal("Queued", Phase(host));
        Assert.Equal("Queued", Phase(guest));
        Call(f.Service, "AbandonMatch", guest, guest.Tag, "cancel queue");
        f.Transfer(next, game.WorldId);
        Assert.Equal("Queued", Phase(next));
        Assert.Equal(Admission(host).MatchId, Admission(next).MatchId);
    }

    [Fact]
    public void WholePartyCapacityFailureDoesNotPartiallyAdmitAnyMember()
    {
        using var f = new Fixture();
        var game = ConfiguredGame(f, maxPlayers: 2);
        var host = f.Player("host");
        var leader = f.Player("leader");
        var friend = f.Player("friend");
        f.Admit(game, leader); f.Admit(game, friend);
        Set(leader.Tag!, "AppearanceReadySent", true); Set(friend.Tag!, "AppearanceReadySent", true);
        f.Party(leader, friend);
        f.Transfer(host, game.WorldId);
        f.Transfer(leader, game.WorldId);
        AssertCleared(leader); AssertCleared(friend);
        Assert.Equal("Queued", Phase(host));
        Assert.Contains(f.Sent, sent => sent.Connection == leader && IsTransferReply(sent.Packet) && sent.Packet[2] == 1);
    }

    [Fact]
    public void HostedQueueClockWaitsForLoadedHostAndHoldsAllPlayersAtExpiry()
    {
        using var f = new Fixture();
        f.Service.Post = null;
        var game = ConfiguredGame(f, minutes: 2);
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId); f.Transfer(guest, game.WorldId);
        HostedLobby(f, guest);
        Assert.Null(Get<long?>(SharedMatch(f, guest), "LobbyDeadlineMs"));
        HostedLobby(f, host, ready: false);
        Assert.Null(Get<long?>(SharedMatch(f, guest), "LobbyDeadlineMs"));
        Set(host.Tag!, "PregameClientReady", true);
        long now = Environment.TickCount64;
        Call(f.Service, "RefreshHostedLobby", Admission(host).MatchId, now);
        long deadline = Assert.IsType<long>(Get<long?>(SharedMatch(f, host), "LobbyDeadlineMs"));
        Assert.Equal(now + 120_000, deadline);
        Call(f.Service, "RefreshHostedLobby", Admission(host).MatchId, deadline - 1);
        Assert.Equal("Lobby", Phase(host));
        Set(guest.Tag!, "PregameClientReady", false);
        Call(f.Service, "RefreshHostedLobby", Admission(host).MatchId, deadline);
        Assert.Equal("Lobby", Phase(host)); Assert.Equal("Lobby", Phase(guest));
        Assert.DoesNotContain(f.Sent, sent => IsStartMatch(sent.Packet));
        Set(guest.Tag!, "PregameClientReady", true);
        Call(f.Service, "RefreshHostedLobby", Admission(host).MatchId, deadline + 1);
        Assert.Equal("Dropping", Phase(host)); Assert.Equal("Dropping", Phase(guest));
        Assert.Single(f.Sent, sent => sent.Connection == host && IsStartMatch(sent.Packet));
        Assert.Single(f.Sent, sent => sent.Connection == guest && IsStartMatch(sent.Packet));
    }

    [Fact]
    public void AdminWorldTracksCurrentMembershipAndRevokedModeratorAccess()
    {
        using var f = new Fixture();
        var game = ConfiguredGame(f);
        var host = f.Player("host");
        var moderator = f.Player("moderator");
        var grant = f.Store.IssuePlayerKey("host", false, game.WorldId, targetAccount: "moderator", moderator: true);
        Assert.True(f.Store.Redeem("moderator", grant.Secret!).Success);
        f.Admit(game, moderator); // Revoking moderation alone must clear tools while player access survives.
        f.HostCommand(host, $"panel {game.WorldId}");
        Assert.Equal("0", PanelValues(f, host)["Cranberry.Hosted.AdminWorld"]);
        f.Transfer(host, game.WorldId); f.Transfer(moderator, game.WorldId);
        f.HostCommand(moderator, "panel");
        Assert.Equal(game.WorldId.ToString(), PanelValues(f, moderator)["Cranberry.Hosted.AdminWorld"]);
        Assert.True(f.HostCommand(host, "revoke " + grant.Key!.Id).Ok);
        Assert.Equal("0", PanelValues(f, moderator)["Cranberry.Hosted.AdminWorld"]);
        Assert.Equal("Queued", Phase(moderator));
        f.HostCommand(host, "panel");
        Assert.Equal(game.WorldId.ToString(), PanelValues(f, host)["Cranberry.Hosted.AdminWorld"]);
        Call(f.Service, "AbandonMatch", host, host.Tag, "return to menu");
        Assert.Equal("0", PanelValues(f, host)["Cranberry.Hosted.AdminWorld"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFullTableReplacementCannotEraseCurrentHostedAdminAccess(bool enterLobby)
    {
        using var f = new Fixture();
        f.Service.Post = null;
        var game = ConfiguredGame(f); var host = f.Player("host");
        f.Transfer(host, game.WorldId);
        f.HostCommand(host, "panel"); // Authority is already cached as sent before the replacement.
        f.Sent.Clear();
        if (enterLobby) HostedLobby(f, host);
        else Call(f.Service, "SendHostedSchedule", host, host.Tag);
        string? authority = null;
        bool replaced = false;
        foreach (var packet in f.Sent.Where(item => item.Connection == host).Select(item => item.Packet))
        {
            if (packet.Length < 3) continue;
            if (packet[1] == ZoneOpcodes.StringHashToValueManager)
            {
                authority = null; replaced = true; // The August consumer clears all prior deltas.
                var reader = new PacketReader(packet.AsSpan(2));
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    reader.ReadUInt32(); string value = reader.ReadString(); reader.ReadBool();
                    if (reader.ReadString() == "Cranberry.Hosted.AdminWorld") authority = value;
                }
            }
            else if (packet[1] == ZoneOpcodes.UpdateStringHashToValueManager)
            {
                var reader = new PacketReader(packet.AsSpan(2));
                if (reader.ReadString() == "Cranberry.Hosted.AdminWorld") authority = reader.ReadString();
            }
        }
        Assert.True(replaced);
        Assert.Equal(game.WorldId.ToString(), authority);
    }

    [Fact]
    public void RetailRedemptionReplyIsCorrelatedAndDoesNotEchoSecret()
    {
        using var f = new Fixture();
        var game = ConfiguredGame(f);
        var guest = f.Player("guest");
        var stranger = f.Player("stranger");
        var invitation = f.Invitation(game, "guest");
        Assert.True(f.HostCommand(guest, "redeemui 0123Ab " + invitation.Secret).Ok);
        string result = PanelValues(f, guest)["Cranberry.Hosted.RedeemResult"];
        Assert.StartsWith("0123Ab|1|", result);
        Assert.DoesNotContain(invitation.Secret!, result);
        Assert.DoesNotContain("Cranberry.Hosted.RedeemResult", PanelValues(f, stranger).Keys);
        Assert.False(f.HostCommand(stranger, "redeemui fed1 " + invitation.Secret).Ok);
        Assert.StartsWith("fed1|0|", PanelValues(f, stranger)["Cranberry.Hosted.RedeemResult"]);
        Assert.True(f.HostCommand(guest, "redeemui 0123Ac " + invitation.Secret).Ok);
        Assert.StartsWith("0123Ac|1|", PanelValues(f, guest)["Cranberry.Hosted.RedeemResult"]);
        f.Sent.Clear();
        Assert.False(f.HostCommand(guest, "redeemui not-a-nonce " + invitation.Secret).Ok);
        Assert.DoesNotContain("Cranberry.Hosted.RedeemResult", PanelValues(f, guest).Keys);
    }
}

public sealed class HostedConfigurationPersistenceTests
{
    [Fact]
    public void SavedConfigurationSurvivesReloadAndLegacyRowsKeepManualDefaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cranberry-hosted-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "hosted.json");
        try
        {
            var store = new HostedGameStore(path);
            var grant = store.IssueHostKey("admin", true, "EU", targetAccount: "host");
            Assert.True(store.Redeem("host", grant.Secret!).Success);
            Assert.True(store.CreateGame("host", "EU", 13, "Friends", 23, 41).Success);
            var loaded = Assert.Single(new HostedGameStore(path).ListGames("host"));
            Assert.Equal(23, loaded.QueueDurationMinutes); Assert.Equal(41, loaded.MaxPlayers);
            var legacy = JsonNode.Parse(File.ReadAllText(path))!;
            var row = legacy["Games"]![0]!.AsObject();
            row.Remove("QueueDurationMinutes"); row.Remove("MaxPlayers");
            File.WriteAllText(path, legacy.ToJsonString());
            loaded = Assert.Single(new HostedGameStore(path).ListGames("host"));
            Assert.Equal(0, loaded.QueueDurationMinutes); Assert.Equal(150, loaded.MaxPlayers);
            row["MaxPlayers"] = 151;
            File.WriteAllText(path, legacy.ToJsonString());
            Assert.Throws<InvalidDataException>(() => new HostedGameStore(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
