using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone.HostedGames;

public sealed partial class HostedGameGatewayTests
{
    private static Dictionary<string, string> PanelValues(Fixture fixture, SoeConnection player)
    {
        var values = new Dictionary<string, string>();
        foreach (var packet in fixture.Sent.Where(sent => sent.Connection == player).Select(sent => sent.Packet))
        {
            if (packet.Length < 3 || packet[1] != ZoneOpcodes.UpdateStringHashToValueManager) continue;
            var reader = new PacketReader(packet.AsSpan(2));
            values[reader.ReadString()] = reader.ReadString();
            Assert.False(reader.ReadBool());
        }
        return values;
    }

    private static string[][] PanelRows(Dictionary<string, string> values) => values["Cranberry.Hosted.Panel"].Split('\n')
        .Select(row => row.Split('|').Select(Uri.UnescapeDataString).ToArray()).ToArray();

    [Fact]
    public void PanelDoesNotExposePrivateGamesOrRosterToAnUninvitedAccount()
    {
        using var f = new Fixture();
        f.Host("host");
        var stranger = f.Player("stranger");
        f.Sent.Clear();
        var reply = f.HostCommand(stranger, "panel 8");
        Assert.True(reply.Ok);
        Assert.True(reply.SuppressOutput);
        var rows = PanelRows(PanelValues(f, stranger));
        Assert.Single(rows);
        Assert.Equal(new[] { "H", "stranger", "0", "0", "", "0", "0", "0", "0x1", "0", "", "0", "0", "0" }, rows[0]);
        Assert.DoesNotContain(f.Sent, item => item.Packet.Length > 1 && item.Packet[1] == StringHashToValueManager.Opcode);
    }

    [Fact]
    public void PanelEscapesNamesShowsAccurateHostRightsAndKeepsGuestsOutOfPlayerTools()
    {
        using var f = new Fixture();
        var game = f.Host("host|percent%");
        var host = f.Player(game.OwnerAccount);
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(guest, game.WorldId);
        SetPhase(guest, "Lobby");
        Set(guest.Tag!, "Hitpoints", 10000u);
        Set(guest.Tag!, "CharacterName", "Friend | % 雪");
        f.Sent.Clear();
        Assert.True(f.HostCommand(host, $"panel {game.WorldId}").Ok);
        var rows = PanelRows(PanelValues(f, host));
        Assert.Equal("1", rows[0][3]);
        Assert.Equal("1", rows[0][7]);
        var gameRow = Assert.Single(rows, row => row[0] == "G");
        Assert.Equal(game.Name, gameRow[2]);
        Assert.Equal("1", gameRow[5]);
        Assert.Equal("1", gameRow[6]);
        Assert.Equal("1", gameRow[8]);
        Assert.Equal("Friend | % 雪", Assert.Single(rows, row => row[0] == "P")[2]);
        f.Sent.Clear();
        Assert.True(f.HostCommand(guest, $"panel {game.WorldId}").Ok);
        rows = PanelRows(PanelValues(f, guest));
        Assert.DoesNotContain(rows, row => row[0] == "P");
        Assert.Equal("0", Assert.Single(rows, row => row[0] == "G")[5]);
    }

    [Fact]
    public void PanelFeedbackReturnsIssuedSecretOnlyToCallerAndClearsItOnNextAction()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var other = f.Player("other");
        f.Sent.Clear();
        Assert.True(f.HostCommand(host, $"invite {game.WorldId} 2h").Ok);
        var values = PanelValues(f, host);
        string secret = values["Cranberry.Hosted.Secret"];
        Assert.StartsWith("HGK-", secret);
        Assert.DoesNotContain(secret, values["Cranberry.Hosted.Feedback"]);
        Assert.DoesNotContain(PanelValues(f, other).Values, value => value.Contains(secret, StringComparison.Ordinal));
        f.Sent.Clear();
        Assert.True(f.HostCommand(host, $"panel {game.WorldId}").Ok);
        Assert.DoesNotContain("Cranberry.Hosted.Secret", PanelValues(f, host).Keys);
        f.Sent.Clear();
        Assert.False(f.HostCommand(host, "mode invalid solo").Ok);
        values = PanelValues(f, host);
        Assert.Equal("", values["Cranberry.Hosted.Secret"]);
        Assert.Contains("/hostgame mode", values["Cranberry.Hosted.Feedback"]);
    }

    [Fact]
    public void ModeratorPanelHasGameControlsWithoutCreatingOrAppointingMoreAdministrators()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var owner = f.Player("host");
        var moderator = f.Player("moderator");
        Assert.True(f.HostCommand(owner, $"admininvite {game.WorldId} 2h moderator").Ok);
        Assert.True(f.HostCommand(moderator, "redeem " + PanelValues(f, owner)["Cranberry.Hosted.Secret"]).Ok);
        f.Sent.Clear();
        Assert.True(f.HostCommand(moderator, $"panel {game.WorldId}").Ok);
        var rows = PanelRows(PanelValues(f, moderator));
        Assert.Equal("0", rows[0][3]);
        Assert.Equal("0", rows[0][7]);
        Assert.Equal("1", Assert.Single(rows, row => row[0] == "G")[5]);
        Assert.True(f.HostCommand(moderator, $"mode {game.WorldId} fives").Ok);
        Assert.False(f.HostCommand(moderator, $"admininvite {game.WorldId} 2h another").Ok);
        Assert.False(f.HostCommand(moderator, $"close {game.WorldId}").Ok);
    }

    [Fact]
    public void IdenticalActionRepliesStillPublishANewCompletionSequenceAndPollingDoesNot()
    {
        using var f = new Fixture();
        var player = f.Player("player");
        Assert.False(f.HostCommand(player, "mode invalid solo").Ok);
        var first = PanelValues(f, player);
        f.Sent.Clear();
        Assert.False(f.HostCommand(player, "mode invalid solo").Ok);
        var second = PanelValues(f, player);
        Assert.Equal(first["Cranberry.Hosted.Feedback"], second["Cranberry.Hosted.Feedback"]);
        Assert.NotEqual(first["Cranberry.Hosted.FeedbackSequence"], second["Cranberry.Hosted.FeedbackSequence"]);
        f.Sent.Clear();
        Assert.True(f.HostCommand(player, "panel").Ok);
        Assert.DoesNotContain("Cranberry.Hosted.FeedbackSequence", PanelValues(f, player).Keys);
    }

    [Fact]
    public void LoadingPlayersHaveFriendlyRosterStatusAndCannotBeTeleportedBeforeReady()
    {
        using var f = new Fixture();
        var game = f.Host("host");
        var host = f.Player("host");
        var guest = f.Player("guest");
        f.Admit(game, guest);
        f.Transfer(host, game.WorldId);
        f.Transfer(guest, game.WorldId);
        Loaded(host, System.Numerics.Vector3.One);
        Loaded(guest, System.Numerics.Vector3.One * 100);
        Set(guest.Tag!, "PregameClientReady", false);
        f.Sent.Clear();
        Assert.True(f.HostCommand(host, $"panel {game.WorldId}").Ok);
        var rows = PanelRows(PanelValues(f, host));
        Assert.Equal("Loading players", Assert.Single(rows, row => row[0] == "G")[7]);
        Assert.Equal("Loading", Assert.Single(rows, row => row[0] == "P" && row[2] == "guest")[4]);
        Assert.False(f.HostCommand(host, $"bring {game.WorldId} 0x{Get<ulong>(guest.Tag!, "Guid"):X}").Ok);
        Assert.DoesNotContain(f.Sent, sent => IsHostedTeleport(sent.Packet));
    }
}
