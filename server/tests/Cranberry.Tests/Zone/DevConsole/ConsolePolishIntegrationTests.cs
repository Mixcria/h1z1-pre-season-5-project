using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    [Theory]
    [InlineData("gas", "pause extra")]
    [InlineData("startmatch", "extra")]
    [InlineData("pausegas", "extra")]
    [InlineData("bots", "0")]
    [InlineData("bots", "51")]
    [InlineData("bots", "spawn 2 awful")]
    [InlineData("bots", "spawn 2 easy NaN")]
    [InlineData("bots", "spawn 2 normal 101")]
    [InlineData("bots", "clear extra")]
    [InlineData("bots", "spawn 1 normal 20 extra")]
    [InlineData("tp", "10 20 30 extra")]
    public void ConsolePolishRejectsBadArgumentsBeforeAnyWorldMutation(string name, string tail)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, name, tail);
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
        Assert.StartsWith("?", Assert.Single(ConsoleLines(recorder, before)));
    }

    [Fact]
    public void NamedTeleportBookmarksAndBackUseRealPositionsAndKeepHistoryAfterRefusal()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var locations = MatchDropChooser.Default().Places(out _)!;
        var expected = locations.Find("PVResidential")!.Anchor + new Vector3(0, 0.5f, 0);
        SendExecuteCommand(service, connection, "tp", "save My Range");
        SendExecuteCommand(service, connection, "tp", "Pleasant Valley");
        Assert.Equal(expected, Member<SessionMovementState>(connection, "Movement").Player!.Position);
        Assert.Equal(new Vector3(100, 20, 100), Session(connection).LastPosition);
        SendExecuteCommand(service, connection, "tp", "back");
        Assert.Equal(new Vector3(100, 20, 100), Member<SessionMovementState>(connection, "Movement").Player!.Position);
        SetSessionMember(connection, "ChuteGuid", 999ul);
        SendExecuteCommand(service, connection, "tp", "10 20 30");
        Assert.Equal(expected, Session(connection).LastPosition);
        SetSessionMember(connection, "ChuteGuid", 0ul);
        SendExecuteCommand(service, connection, "tp", "cranberry");
        SendExecuteCommand(service, connection, "tp", "my range");
        Assert.Equal(new Vector3(100, 20, 100), Member<SessionMovementState>(connection, "Movement").Player!.Position);
    }

    [Fact]
    public void PlacesShowRealPagedSearchAndAmbiguousNamesDoNotMoveThePlayer()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "places", "2");
        Assert.Contains(ConsoleLines(recorder, before), l => l.Contains("page 2/"));
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "tp", "camp");
        Assert.Contains(ConsoleLines(recorder, before), l => l.Contains("matches") && l.Contains("/places camp"));
        Assert.Equal(new Vector3(100, 20, 100), Member<SessionMovementState>(connection, "Movement").Player!.Position);
    }

    [Fact]
    public void TypedBotsCommandSupportsCountAliasFreezeResumeAndTheMatchLimit()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        service.Post = _ => { };
        SendExecuteCommand(service, connection, "bots", "10");
        var targets = Member<SessionCombat>(connection, "Combat").Targets;
        Assert.Equal(10, targets.Count);
        SendExecuteCommand(service, connection, "spawnbots", "2 hard 20");
        Assert.Equal(12, targets.Count);
        SendExecuteCommand(service, connection, "bots", "freeze");
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "bots", "status");
        Assert.Contains(ConsoleLines(recorder, before), line => line.Contains("FROZEN"));
        SendExecuteCommand(service, connection, "bots", "resume");
        SendExecuteCommand(service, connection, "bots", "50");
        Assert.Equal(12, targets.Count);
        Session(connection).TierOverride = Cranberry.Zone.DevConsole.ConsoleTier.Tester;
        SendExecuteCommand(service, connection, "bots", "clear");
        Assert.Equal(12, targets.Count);
        Session(connection).TierOverride = Cranberry.Zone.DevConsole.ConsoleTier.Owner;
        SendExecuteCommand(service, connection, "bots", "clear");
        Assert.Empty(targets.All);
    }

    [Fact]
    public void EveryMapShortcutResolvesAndQuickHelpExplainsTheNewCommands()
    {
        var places = MatchDropChooser.Default().Places(out _)!;
        foreach (var alias in Cranberry.Zone.DevConsole.TeleportPlaces.Aliases)
            Assert.Single(Cranberry.Zone.DevConsole.TeleportPlaces.Find(places, alias.Key));
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "commands", "quick");
        var lines = ConsoleLines(recorder, before);
        Assert.Contains(lines, line => line.Contains("/bots 10"));
        Assert.Contains(lines, line => line.Contains("/gas pause"));
        Assert.Contains(lines, line => line.Contains("/tp pv"));
    }
}
