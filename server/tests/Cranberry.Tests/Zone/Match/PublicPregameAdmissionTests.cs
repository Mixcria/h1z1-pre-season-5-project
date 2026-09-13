using System.Diagnostics;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

/// <summary>Admission policy uses a deterministic clock; gateway cases record sent packets, not native rendering.</summary>
public sealed class PublicPregameAdmissionTests
{
    private static ulong Reserve(PublicMatchQueue queue, ulong[] players, MatchMode mode = MatchMode.Solo,
        long now = 0, uint world = 1)
    {
        Assert.True(queue.TryReserve(world, mode, players, now, out ulong id));
        return id;
    }

    [Fact]
    public void OnePlayerCanLoadImmediatelyAndWaitIndefinitelyWithoutABattleRoyaleDeadline()
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 180_000 });
        ulong match = Reserve(queue, [1]);
        queue.Poll(0);
        Assert.True(queue.CanAccept(1, match));
        queue.SetReadyPlayers(match, [1], 100);
        queue.Poll(3_600_000);

        var lobby = Assert.Single(queue.Snapshots);
        Assert.Equal(PublicMatchPhase.PRE_GAME, lobby.Phase);
        Assert.Equal(1, lobby.ReadyPlayers);
        Assert.Null(lobby.CountdownDeadlineMs);
        Assert.Equal(match, Reserve(queue, [2], now: 3_600_001));
    }

    [Fact]
    public void SecondReadyPlayerStartsAFullCountdownAndRosterStaysOpenUntilItsDeadline()
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 180_000 });
        ulong match = Reserve(queue, [1]);
        queue.Poll(0);
        queue.SetReadyPlayers(match, [1], 0);
        Assert.Equal(match, Reserve(queue, [2], now: 300_000));
        queue.Poll(300_000);
        Assert.Null(Assert.Single(queue.Snapshots).CountdownDeadlineMs);

        queue.SetReadyPlayers(match, [1, 2], 310_000);
        Assert.Equal(490_000L, Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        Assert.Equal(match, Reserve(queue, [3], now: 489_000));
        queue.SetReadyPlayers(match, [1, 2, 3], 489_000);
        queue.Poll(489_999);
        Assert.Equal(PublicMatchPhase.PRE_GAME, Assert.Single(queue.Snapshots).Phase);
        Assert.Equal(490_000L, Assert.Single(queue.Snapshots).CountdownDeadlineMs);

        queue.Poll(490_000);
        Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, Assert.Single(queue.Snapshots).Phase);
        Assert.Equal(new ulong[] { 1, 2, 3 }, Assert.Single(queue.Snapshots).Roster);
        ulong successor = Reserve(queue, [4], now: 490_001);
        Assert.NotEqual(match, successor);
        queue.Poll(490_001);
        Assert.True(queue.CanAccept(4, successor));
        Assert.Equal(2, queue.AllocatedMatches);
    }

    [Fact]
    public void LosingTheMinimumCancelsTheCountdownAndReplacementStartsAFullNewCountdown()
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 1000 });
        ulong match = Reserve(queue, [1]); Reserve(queue, [2]);
        queue.Poll(0);
        queue.SetReadyPlayers(match, [1, 2], 100);
        Assert.Equal(1100L, Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        queue.Leave(2);
        Assert.Null(Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        queue.Poll(5000);
        Assert.Equal(PublicMatchPhase.PRE_GAME, Assert.Single(queue.Snapshots).Phase);

        Assert.Equal(match, Reserve(queue, [3], now: 5001));
        queue.SetReadyPlayers(match, [1, 3], 5200);
        Assert.Equal(6200L, Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        queue.Poll(6199);
        Assert.Equal(PublicMatchPhase.PRE_GAME, Assert.Single(queue.Snapshots).Phase);
        queue.Poll(6200);
        Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, Assert.Single(queue.Snapshots).Phase);
    }

    [Fact]
    public void LoadingAndForeignPlayersDoNotCountAsReadyOpponents()
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 1000 });
        ulong match = Reserve(queue, [1]); Reserve(queue, [2]);
        queue.Poll(0);
        queue.SetReadyPlayers(match, [1, 999], 100);
        var lobby = Assert.Single(queue.Snapshots);
        Assert.Equal(2, lobby.PresentPlayers);
        Assert.Equal(1, lobby.ReadyPlayers);
        Assert.Null(lobby.CountdownDeadlineMs);
        queue.SetReadyPlayers(match, [1, 2], 200);
        Assert.Equal(1200L, Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        queue.SetReadyPlayers(match, [1], 300);
        Assert.Null(Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        queue.Poll(1200);
        Assert.Equal(PublicMatchPhase.PRE_GAME, Assert.Single(queue.Snapshots).Phase);
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2)]
    [InlineData(MatchMode.Fives, 5)]
    public void AWholePartyCanLoadButNeedsAnOpposingMinimumBeforeTheCountdown(MatchMode mode, int size)
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 1000 });
        ulong[] party = Enumerable.Range(1, size).Select(i => (ulong)i).ToArray();
        ulong match = Reserve(queue, party, mode);
        queue.Poll(0);
        queue.SetReadyPlayers(match, party, 10);
        Assert.All(party, player => Assert.True(queue.CanAccept(player, match)));
        Assert.Null(Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        Assert.Equal(match, Reserve(queue, party, mode, now: 5000));
        Assert.Equal(size, Assert.Single(queue.Snapshots).PresentPlayers);
        queue.Poll(5000);
        Assert.Equal(PublicMatchPhase.PRE_GAME, Assert.Single(queue.Snapshots).Phase);

        Assert.Equal(match, Reserve(queue, [100], mode, now: 5001));
        queue.SetReadyPlayers(match, [.. party, 100], 5100);
        Assert.Equal(6100L, Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        queue.Poll(6100);
        Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, Assert.Single(queue.Snapshots).Phase);
        Assert.Equal(party, queue.GroupFor(party[0], match));
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2)]
    [InlineData(MatchMode.Fives, 5)]
    public void PartyWithdrawalBeforeFreezeReleasesEverySeatAndCancelsTheCountdown(MatchMode mode, int size)
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 1000 });
        ulong[] party = Enumerable.Range(1, size).Select(i => (ulong)i).ToArray();
        ulong match = Reserve(queue, party, mode);
        Reserve(queue, [100], mode);
        queue.Poll(0);
        queue.SetReadyPlayers(match, [.. party, 100], 100);
        Assert.NotNull(Assert.Single(queue.Snapshots).CountdownDeadlineMs);
        queue.Leave(party[^1]);
        var lobby = Assert.Single(queue.Snapshots);
        Assert.Equal(new ulong[] { 100 }, lobby.Roster);
        Assert.Equal(1, lobby.PresentPlayers);
        Assert.Equal(1, lobby.ReadyPlayers);
        Assert.Null(lobby.CountdownDeadlineMs);
        Assert.All(party, player => Assert.False(queue.CanAccept(player, match)));
        Assert.Equal(match, Reserve(queue, party, mode, now: 5000));
    }
}

public sealed partial class BountyGatewayTests
{
    [Fact]
    public void PublicPartyLogoutDuringTransferCompletesOnceForEveryMemberAndCancelsZoning()
    {
        using var f = new Fixture(mode: MatchMode.Duos, lobby: ArrivalLobby,
            admissions: new([new(6, 13, MatchQueueKind.Public, MatchMode.Duos)]),
            publicQueue: new() { WaitMs = 1000 });
        var leader = f.Connect("leader"); var member = f.Connect("member");
        Assert.Null(f.Service.LauncherQueue(["leader", "member"], "Duos"));
        f.Pump(() => f.Service.ForTest(leader).Step == "Transferring"
            && f.Service.ForTest(member).Step == "Transferring");
        Assert.Equal(2, Assert.Single(f.Service.PublicMatches).PresentPlayers);
        f.Send(leader, w => { w.WriteByte(0x09); w.WriteByte(0x4e); w.WriteUInt16(0); });

        var elapsed = Stopwatch.StartNew();
        f.Pump(() => elapsed.ElapsedMilliseconds >= 750);
        Assert.Empty(f.Service.PublicMatches);
        foreach (var connection in new[] { leader, member })
        {
            Assert.Equal("Menu", f.Service.ForTest(connection).Step);
            Assert.Single(f.Sent(connection), p => Is(p, 0x11, 0x30));
            Assert.DoesNotContain(f.Sent(connection), p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning);
        }
    }

    private static void LoadPublicPregame(Fixture fixture, SoeConnection connection, uint world = 1)
    {
        int zoningBefore = fixture.Sent(connection).Count(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning);
        fixture.Transfer(connection, world);
        fixture.Pump(() => fixture.Sent(connection).Count(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning) > zoningBefore);
        fixture.Send(connection, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
        fixture.Ready(connection);
    }

    [Fact]
    public void PublicPlayAloneLoadsTheLobbyWithoutStartingBattleRoyaleOrNeedingAnotherTransfer()
    {
        using var f = new Fixture(countdown: 50, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 400, LoadTimeoutMs = 1000 });
        var a = f.Connect("a");
        LoadPublicPregame(f, a);
        Assert.Equal("Lobby", f.Service.ForTest(a).Step);
        Assert.Contains(f.Sent(a), p => InBox(p, true));
        var elapsed = Stopwatch.StartNew();
        f.Pump(() => elapsed.ElapsedMilliseconds >= 1500);

        var lobby = Assert.Single(f.Service.PublicMatches);
        Assert.Equal(PublicMatchPhase.PRE_GAME, lobby.Phase);
        Assert.Equal(1, lobby.ReadyPlayers);
        Assert.Null(lobby.CountdownDeadlineMs);
        Assert.Equal("Lobby", f.Service.ForTest(a).Step);
        Assert.DoesNotContain(f.Sent(a), p => Hud(p, 0x16) || Is(p, 0x11, 0x30));
    }

    [Fact]
    public void PublicLobbyStartsWhenSecondPlayerFinishesLoadingAndOpensSuccessorAfterFreeze()
    {
        using var f = new Fixture(countdown: 50, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 700, MaxAllocatedMatches = 2 });
        var a = f.Connect("a"); var b = f.Connect("b");
        LoadPublicPregame(f, a);
        ulong first = Assert.Single(f.Service.PublicMatches).MatchId;
        f.Transfer(b);
        f.Pump(() => f.Sent(b).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning));
        f.Send(b, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
        Assert.Null(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs);
        Assert.DoesNotContain(f.Sent(a), p => Hud(p, 0x16));
        long readyAt = Environment.TickCount64;
        f.Ready(b);
        f.Pump(() => f.Service.PublicMatches.Single().CountdownDeadlineMs is not null);
        var countdown = Assert.Single(f.Service.PublicMatches);
        Assert.InRange(countdown.CountdownDeadlineMs!.Value, readyAt + 700, Environment.TickCount64 + 700);
        Assert.Equal(PublicMatchPhase.PRE_GAME, countdown.Phase);
        Assert.DoesNotContain(f.Sent(a), p => Hud(p, 0x16));
        Assert.DoesNotContain(f.Sent(b), p => Hud(p, 0x16));
        f.Pump(() => f.Sent(a).Any(p => Hud(p, 0x16)) && f.Sent(b).Any(p => Hud(p, 0x16)));

        var c = f.Connect("c");
        LoadPublicPregame(f, c);
        var next = Assert.Single(f.Service.PublicMatches, r => r.MatchId != first);
        Assert.Equal(PublicMatchPhase.PRE_GAME, next.Phase);
        Assert.Null(next.CountdownDeadlineMs);
        Assert.Equal(2, f.Service.PublicMatches.Single(r => r.MatchId == first).Roster.Count);
        Assert.Equal("Lobby", f.Service.ForTest(c).Step);
        Assert.Equal(2, f.Service.PeerRegistry.Sessions.Where(p => p.InMatch).Select(p => p.MatchId).Distinct().Count());
    }

    [Fact]
    public void PublicLobbyDisconnectCancelsPendingStartUntilAnOpponentIsReadyAgain()
    {
        using var f = new Fixture(countdown: 50, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 700 });
        var a = f.Connect("a"); var b = f.Connect("b");
        LoadPublicPregame(f, a); LoadPublicPregame(f, b);
        f.Pump(() => f.Service.PublicMatches.Single().CountdownDeadlineMs is not null);
        f.Disconnect(b);
        var elapsed = Stopwatch.StartNew();
        f.Pump(() => elapsed.ElapsedMilliseconds >= 1000);
        var lobby = Assert.Single(f.Service.PublicMatches);
        Assert.Null(lobby.CountdownDeadlineMs);
        Assert.Equal(PublicMatchPhase.PRE_GAME, lobby.Phase);
        Assert.Equal("Lobby", f.Service.ForTest(a).Step);
        Assert.DoesNotContain(f.Sent(a), p => Hud(p, 0x16));

        var replacement = f.Connect("replacement");
        LoadPublicPregame(f, replacement);
        f.Pump(() => f.Service.PublicMatches.Single().CountdownDeadlineMs is not null);
        Assert.DoesNotContain(f.Sent(a), p => Hud(p, 0x16));
        f.Pump(() => f.Sent(a).Any(p => Hud(p, 0x16)) && f.Sent(replacement).Any(p => Hud(p, 0x16)));
    }
}
