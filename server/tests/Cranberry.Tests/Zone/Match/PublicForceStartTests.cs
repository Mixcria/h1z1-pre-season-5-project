using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class PublicForceStartTests
{
    [Fact]
    public void ForceFreezeRequiresAnAllocatedReadyPregameAndRefusesEndedOrUnknownRounds()
    {
        var queue = new PublicMatchQueue(new());
        Assert.True(queue.TryReserve(1, MatchMode.Solo, [1], 0, out ulong match));
        Assert.False(queue.TryForceFreeze(match, 0)); // Reservation is not allocated.
        queue.Poll(0);
        Assert.False(queue.TryForceFreeze(match, 1)); // Allocated, but nobody is ready.
        queue.SetReadyPlayers(match, [], 2);
        Assert.False(queue.TryForceFreeze(match, 2));
        queue.SetReadyPlayers(match, [1], 3);
        queue.Advance(match, PublicMatchPhase.ENDING);
        Assert.False(queue.TryForceFreeze(match, 4));
        Assert.False(queue.TryForceFreeze(ulong.MaxValue, 4));
        Assert.Equal(PublicMatchPhase.ENDING, Assert.Single(queue.Snapshots).Phase);
    }

    [Fact]
    public void ForceFreezePreservesWholePartyIncludingLateLoadersAndOpensANewGeneration()
    {
        var queue = new PublicMatchQueue(new());
        ulong[] party = [1, 2, 3, 4, 5];
        Assert.True(queue.TryReserve(7, MatchMode.Fives, party, 0, out ulong match));
        queue.Poll(0);
        queue.SetReadyPlayers(match, [1], 100);
        Assert.Null(Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        Assert.True(queue.TryForceFreeze(match, 101));
        var frozen = Assert.Single(queue.Snapshots);
        Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, frozen.Phase);
        Assert.Equal(party, frozen.Roster);
        Assert.Equal(101L, frozen.CountdownDeadlineMs);
        Assert.False(queue.TryForceFreeze(match, 102));
        Assert.All(party, player => Assert.True(queue.CanAccept(player, match)));

        Assert.True(queue.TryReserve(7, MatchMode.Fives, [100], 102, out ulong next));
        Assert.NotEqual(match, next);
        queue.Poll(102);
        Assert.True(queue.CanAccept(100, next));
        queue.Advance(match, PublicMatchPhase.ACTIVE);
        Assert.True(queue.CanAccept(2, match));
        queue.Leave(5);
        Assert.Equal(party, queue.Snapshots.Single(r => r.MatchId == match).Roster);
        Assert.Equal(4, queue.Snapshots.Single(r => r.MatchId == match).PresentPlayers);
    }
}

public sealed partial class BountyGatewayTests
{
    private static ConsoleOptions PublicForceConsole => ConsoleOptions.Default with
    {
        ModMenuEnabled = false,
        SelfFlagOpensConsole = false,
        RateLimitMs = 0,
        ClientLogsPath = Path.Combine(Path.GetTempPath(), "cranberry-force-start-no-client-logs"),
    };

    private static void ForcePublicStart(Fixture f, SoeConnection connection) => f.Send(connection, writer =>
    {
        writer.WriteByte(ZoneOpcodes.CommandBase);
        writer.WriteUInt16(0x0042);
        writer.WriteUInt32(CommandHash.Compute("startmatch"));
        writer.WriteString(string.Empty);
    });

    [Fact]
    public void PublicForceStartOwnerCanStartAloneExactlyOnceThroughTheCommandDispatcher()
    {
        using var f = new Fixture(lobby: ArrivalLobby, publicQueue: new() { WaitMs = 180_000 },
            console: PublicForceConsole);
        f.Service.LocalOwnerAccountId = "owner";
        var owner = f.Connect("owner");
        LoadPublicPregame(f, owner);
        Assert.Null(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs);
        Assert.DoesNotContain(f.Sent(owner), p => Hud(p, 0x16));

        ForcePublicStart(f, owner);
        f.Pump(() => f.Service.ForTest(owner).Step == "Dropping");
        Assert.Single(f.Sent(owner), p => Hud(p, 0x16));
        Assert.Equal(PublicMatchPhase.STARTING, Assert.Single(f.Service.PublicMatches).Phase);
        Assert.Single(Assert.Single(f.Service.PublicMatches).Roster);
        ForcePublicStart(f, owner);
        Assert.Single(f.Sent(owner), p => Hud(p, 0x16));
        Assert.Single(f.Service.PublicMatches);
    }

    [Fact]
    public void PublicForceStartNonOwnerCannotFreezeTheLoneLobby()
    {
        using var f = new Fixture(lobby: ArrivalLobby, publicQueue: new() { WaitMs = 180_000 },
            console: PublicForceConsole);
        f.Service.LocalOwnerAccountId = "owner";
        var visitor = f.Connect("visitor");
        LoadPublicPregame(f, visitor);
        int before = f.Sent(visitor).Length;
        ForcePublicStart(f, visitor);

        var lobby = Assert.Single(f.Service.PublicMatches);
        Assert.Equal(PublicMatchPhase.PRE_GAME, lobby.Phase);
        Assert.Null(lobby.CountdownDeadlineMs);
        Assert.Equal("Lobby", f.Service.ForTest(visitor).Step);
        Assert.DoesNotContain(f.Sent(visitor), p => Hud(p, 0x16));
        var console = Assert.IsType<ConsoleSession>(visitor.Tag!.GetType().GetProperty("DevConsole")!.GetValue(visitor.Tag));
        Assert.Equal(ConsoleTier.Player, console.Tier);
        // The existing Player command allowlist silently ignores privileged command hashes.
        Assert.Empty(f.Sent(visitor).Skip(before));
    }

    [Fact]
    public void PublicForceStartOwnerWithPendingLogoutCannotFreezeOrDrop()
    {
        using var f = new Fixture(lobby: ArrivalLobby, publicQueue: new() { WaitMs = 180_000 },
            console: PublicForceConsole);
        f.Service.LocalOwnerAccountId = "owner";
        var owner = f.Connect("owner");
        LoadPublicPregame(f, owner);
        f.Send(owner, writer => { writer.WriteByte(0x09); writer.WriteByte(0x4e); writer.WriteUInt16(0); });
        Assert.NotNull(owner.Tag!.GetType().GetProperty("PendingLogout")!.GetValue(owner.Tag));
        ForcePublicStart(f, owner);

        Assert.Equal("Lobby", f.Service.ForTest(owner).Step);
        Assert.Equal(PublicMatchPhase.PRE_GAME, Assert.Single(f.Service.PublicMatches).Phase);
        Assert.Null(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs);
        Assert.DoesNotContain(f.Sent(owner), p => Hud(p, 0x16));
    }

    [Fact]
    public void PublicForceStartStartsEveryReadyPeerButLeavesTheSuccessorInPregame()
    {
        using var f = new Fixture(lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 180_000, MaxAllocatedMatches = 2 }, console: PublicForceConsole);
        f.Service.LocalOwnerAccountId = "owner";
        var owner = f.Connect("owner"); var peer = f.Connect("peer");
        LoadPublicPregame(f, owner); LoadPublicPregame(f, peer);
        ulong first = Assert.Single(f.Service.PublicMatches).MatchId;
        Assert.NotNull(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs);
        ForcePublicStart(f, owner);
        f.Pump(() => f.Service.ForTest(owner).Step == "Dropping" && f.Service.ForTest(peer).Step == "Dropping");
        foreach (var connection in new[] { owner, peer }) Assert.Single(f.Sent(connection), p => Hud(p, 0x16));

        var next = f.Connect("next");
        LoadPublicPregame(f, next);
        var successor = Assert.Single(f.Service.PublicMatches, r => r.MatchId != first);
        Assert.Equal(PublicMatchPhase.PRE_GAME, successor.Phase);
        Assert.Null(successor.CountdownDeadlineMs);
        ForcePublicStart(f, owner); // An old owner in Dropping cannot start a different round.
        Assert.Equal("Lobby", f.Service.ForTest(next).Step);
        Assert.DoesNotContain(f.Sent(next), p => Hud(p, 0x16));
        Assert.Equal(2, f.Service.PublicMatches.Single(r => r.MatchId == first).Roster.Count);
    }

    [Fact]
    public void PublicForceStartKeepsAReservedPartyLoaderInTheSameFrozenMatch()
    {
        using var f = new Fixture(mode: MatchMode.Duos, lobby: ArrivalLobby,
            admissions: new([new(6, 13, MatchQueueKind.Public, MatchMode.Duos)]),
            publicQueue: new() { WaitMs = 180_000 }, console: PublicForceConsole);
        f.Service.LocalOwnerAccountId = "owner";
        var owner = f.Connect("owner"); var loading = f.Connect("loading");
        Assert.Null(f.Service.LauncherQueue(["owner", "loading"], "Duos"));
        f.Pump(() => new[] { owner, loading }.All(connection =>
            f.Sent(connection).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning)));
        foreach (var connection in new[] { owner, loading })
            f.Send(connection, writer => writer.WriteByte(ZoneOpcodes.ClientIsReady));
        f.Ready(owner);
        Assert.Equal(1, Assert.Single(f.Service.PublicMatches).ReadyPlayers);
        ForcePublicStart(f, owner);
        f.Pump(() => f.Service.ForTest(owner).Step == "Dropping");
        ulong match = Assert.Single(f.Service.PublicMatches).MatchId;
        var roster = Assert.Single(f.Service.PublicMatches).Roster.ToArray();
        Assert.Equal(2, roster.Length);
        Assert.DoesNotContain(f.Sent(loading), p => Hud(p, 0x16));

        f.Ready(loading);
        f.Pump(() => f.Service.ForTest(loading).Step == "Dropping");
        Assert.Single(f.Sent(owner), p => Hud(p, 0x16));
        Assert.Single(f.Sent(loading), p => Hud(p, 0x16));
        Assert.Equal(match, Assert.Single(f.Service.PublicMatches).MatchId);
        Assert.Equal(roster, Assert.Single(f.Service.PublicMatches).Roster);
    }
}
