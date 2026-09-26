using Cranberry.Zone.Match;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class PublicMatchQueueTests
{
    [Theory]
    [InlineData(MatchMode.Solo, 1u)]
    [InlineData(MatchMode.Duos, 6u)]
    [InlineData(MatchMode.Fives, 7u)]
    public void LocalPolicyWaitsForTwoReadyPlayersThenCountsThreeMinutesDespiteOldSavedSettings(MatchMode mode, uint world)
    {
        var saved = PublicQueueOptions.FromEnvironment(key => key switch
        { "CRANBERRY_QUEUE_WAIT_MS" => "5000", "CRANBERRY_QUEUE_MIN_PLAYERS" => "1", _ => null });
        var queue = new PublicMatchQueue(saved.ForLocalPlay());
        ulong match = Add(queue, 1, world: world, mode: mode);
        queue.Poll(0); queue.Poll(300000);
        Assert.Null(queue.Snapshots.Single().CountdownDeadlineMs);
        queue.SetReadyPlayers(match, [1], 300000);
        queue.Poll(600000);
        Assert.Equal(PublicMatchPhase.PRE_GAME, queue.Snapshots.Single().Phase);
        Assert.Null(queue.Snapshots.Single().CountdownDeadlineMs);

        Assert.Equal(match, Add(queue, 2, world: world, mode: mode, now: 600000));
        queue.SetReadyPlayers(match, [1], 600000); // A second player still loading is not enough.
        queue.Poll(900000);
        Assert.Null(queue.Snapshots.Single().CountdownDeadlineMs);
        queue.SetReadyPlayers(match, [1, 2], 900000);
        Assert.Equal(1080000L, queue.Snapshots.Single().CountdownDeadlineMs);
        queue.Poll(1079999);
        Assert.Equal(PublicMatchPhase.PRE_GAME, queue.Snapshots.Single().Phase);
        queue.Poll(1080000);
        Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, queue.Snapshots.Single().Phase);
        Assert.Equal(new ulong[] { 1, 2 }, queue.Snapshots.Single().Roster);
        Assert.False(saved.AllowSinglePlayer);
        Assert.Equal(5000, saved.WaitMs); // Applying local policy does not mutate saved options.
    }

    [Theory]
    [InlineData(MatchMode.Solo, 1u)]
    [InlineData(MatchMode.Duos, 6u)]
    [InlineData(MatchMode.Fives, 7u)]
    public void LocalCountdownCancelsBelowTwoAndRestartsInFullWhenTheMinimumReturns(MatchMode mode, uint world)
    {
        var queue = new PublicMatchQueue(new PublicQueueOptions().ForLocalPlay());
        ulong match = Add(queue, 1, world: world, mode: mode);
        Add(queue, 2, world: world, mode: mode);
        queue.Poll(0);
        queue.SetReadyPlayers(match, [1, 2], 0);
        queue.SetReadyPlayers(match, [1], 100000);
        Assert.Null(queue.Snapshots.Single().CountdownDeadlineMs);
        queue.Poll(180000);
        Assert.Equal(PublicMatchPhase.PRE_GAME, queue.Snapshots.Single().Phase);

        queue.SetReadyPlayers(match, [1, 2], 200000);
        Assert.Equal(380000L, queue.Snapshots.Single().CountdownDeadlineMs);
        queue.Leave(2);
        Assert.Null(queue.Snapshots.Single().CountdownDeadlineMs);
        Add(queue, 3, world: world, mode: mode, now: 300000);
        queue.SetReadyPlayers(match, [1, 3], 300000);
        Assert.Equal(480000L, queue.Snapshots.Single().CountdownDeadlineMs);
        queue.Poll(479999);
        Assert.Equal(PublicMatchPhase.PRE_GAME, queue.Snapshots.Single().Phase);
        queue.Poll(480000);
        Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, queue.Snapshots.Single().Phase);
        Assert.Equal(new ulong[] { 1, 3 }, queue.Snapshots.Single().Roster);
    }

    private static ulong Add(PublicMatchQueue queue, ulong first, int size = 1, uint world = 1, MatchMode mode = MatchMode.Solo, long now = 0)
    {
        Assert.True(queue.TryReserve(world, mode, Enumerable.Range(0, size).Select(i => first + (ulong)i).ToArray(), now, out ulong id));
        return id;
    }

    [Fact]
    public void ThreeMinuteDeadlineFreezesImmutableRosterAndImmediatelyOpensNextGeneration()
    {
        var queue = new PublicMatchQueue(new());
        ulong first = Add(queue, 1); Assert.Equal(first, Add(queue, 2));
        queue.Poll(0); Assert.True(queue.CanAccept(1, first));
        queue.SetReadyPlayers(first, [1, 2], 0);
        queue.Poll(179999); Assert.Equal(PublicMatchPhase.PRE_GAME, queue.Snapshots.Single().Phase);
        queue.Poll(180000); Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, queue.Snapshots.Single().Phase);
        var frozen = queue.Snapshots.Single();
        queue.Advance(first, PublicMatchPhase.ACTIVE);
        ulong next = Add(queue, 3, now: 180001); Assert.NotEqual(first, next);
        Assert.Equal(next, Add(queue, 4, now: 180001));
        queue.Poll(180001);
        queue.SetReadyPlayers(next, [3, 4], 180001);
        queue.Poll(360001);
        Assert.Equal(2, queue.AllocatedMatches);
        queue.Leave(2);
        Assert.Equal(new ulong[] { 1, 2 }, frozen.Roster);
        Assert.Equal(new ulong[] { 1, 2 }, queue.Snapshots.Single(r => r.MatchId == first).Roster);
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2)]
    [InlineData(MatchMode.Fives, 5)]
    public void WholePartyReservationIsIdempotentAndCannotCrossWorldOrOverflow(MatchMode mode, int size)
    {
        var queue = new PublicMatchQueue(new() { MaxPlayers = size * 2 + 1 });
        ulong first = Add(queue, 1, size, mode: mode);
        Assert.Equal(first, Add(queue, 1, size, mode: mode));
        Assert.False(queue.TryReserve(1, mode, [1, 90], 0, out _));
        Assert.False(queue.TryReserve(9, mode, Enumerable.Range(1, size).Select(i => (ulong)i).ToArray(), 0, out _));
        Assert.Equal(first, Add(queue, 20, size, mode: mode));
        Assert.False(queue.TryReserve(1, mode, Enumerable.Range(40, size).Select(i => (ulong)i).ToArray(), 0, out _));
        queue.Poll(0);
        queue.SetReadyPlayers(first, queue.Snapshots.Single().Roster.ToArray(), 0);
        ulong next = Add(queue, 40, size, mode: mode);
        Assert.NotEqual(first, next);
        Assert.Equal(PublicMatchPhase.ROSTER_FROZEN, queue.Snapshots.Single(r => r.MatchId == first).Phase);
        Assert.Equal(size * 2, queue.Snapshots.Single(r => r.MatchId == first).Roster.Count);
        Assert.Equal(size, queue.GroupFor(40, next).Count);
    }

    [Fact]
    public void CapacityHoldPreservesOpenRosterAndReleasesWhenOldRoundDrains()
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 0, MaxAllocatedMatches = 1 });
        ulong first = Add(queue, 1); Add(queue, 2); queue.Poll(0);
        queue.SetReadyPlayers(first, [1, 2], 0); queue.Poll(0);
        ulong next = Add(queue, 3); Add(queue, 4); queue.Poll(0);
        Assert.False(queue.CanAccept(3, next));
        queue.Leave(1); queue.Leave(2); queue.Poll(1);
        Assert.True(queue.CanAccept(3, next));
        Assert.Single(queue.Snapshots);
    }

    [Fact]
    public void PartyCancellationReleasesAllOpenSeatsAndMinTeamsPreventsOpponentlessStart()
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 0 });
        ulong match = Add(queue, 1, 5, mode: MatchMode.Fives);
        queue.Poll(0); Assert.True(queue.CanAccept(1, match));
        queue.SetReadyPlayers(match, [1, 2, 3, 4, 5], 0); queue.Poll(0);
        Assert.Equal(PublicMatchPhase.PRE_GAME, queue.Snapshots.Single().Phase);
        Assert.Null(queue.Snapshots.Single().CountdownDeadlineMs);
        queue.Leave(3); Assert.Empty(queue.Snapshots);
        Assert.NotEqual(match, Add(queue, 1, 5, mode: MatchMode.Fives));
    }

    [Fact]
    public void ModesProgressIndependentlyAndRosteredLateAcceptsStayInOriginalMatch()
    {
        var queue = new PublicMatchQueue(new() { WaitMs = 0, MaxAllocatedMatches = 3 });
        ulong solo = Add(queue, 1); Add(queue, 2);
        ulong duo = Add(queue, 10, 2, 6, MatchMode.Duos); Add(queue, 20, 2, 6, MatchMode.Duos);
        ulong fives = Add(queue, 30, 5, 7, MatchMode.Fives); Add(queue, 40, 5, 7, MatchMode.Fives);
        queue.Poll(0); Assert.Equal(3, queue.AllocatedMatches);
        foreach (var round in queue.Snapshots) queue.SetReadyPlayers(round.MatchId, round.Roster.ToArray(), 0);
        queue.Poll(0);
        Assert.True(queue.CanAccept(10, duo)); Assert.True(queue.CanAccept(30, fives));
        queue.Advance(solo, PublicMatchPhase.STARTING); Assert.True(queue.CanAccept(2, solo));
        queue.Advance(solo, PublicMatchPhase.ACTIVE); Assert.True(queue.CanAccept(2, solo));
        Assert.NotEqual(solo, Add(queue, 100));
        Assert.Equal(new ulong[] { 1, 2 }, queue.Snapshots.Single(r => r.MatchId == solo).Roster);
        queue.Advance(solo, PublicMatchPhase.ENDING); Assert.False(queue.CanAccept(2, solo));
    }
}

public sealed partial class BountyGatewayTests
{
    [Theory]
    [InlineData(MatchMode.Solo, 1u)]
    [InlineData(MatchMode.Duos, 6u)]
    [InlineData(MatchMode.Fives, 7u)]
    public void LocalPlayWaitsAloneAfterWorldReadinessUntilTheOwnerStartsMatch(MatchMode mode, uint world)
    {
        using var f = new Fixture(mode: mode, lobby: ArrivalLobby,
            admissions: MatchAdmissionRegistry.Default,
            publicQueue: new PublicQueueOptions().ForLocalPlay(), console: PublicForceConsole);
        f.Service.LocalOwnerAccountId = "local-player";
        var player = f.Connect("local-player");
        bool initialized = false;
        f.Service.DoorSwingClientReady = _ => initialized;
        f.Transfer(player, world);
        Assert.Empty(f.Service.PublicMatches);
        Assert.Equal("Menu", f.Service.ForTest(player).Step);
        initialized = true;
        // Readiness/queue polls, the 3-second prompt and zoning delay already need
        // about 4 seconds. Allow CI scheduling margin without changing those timers.
        f.Pump(() => f.Sent(player).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning), timeoutMs: 10_000);
        f.Send(player, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
        Assert.Null(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs);
        f.Ready(player);
        f.Pump(() => f.Sent(player).Any(p => Hud(p, 0x0f)));
        Assert.Equal("Lobby", f.Service.ForTest(player).Step);
        Assert.Null(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs);
        byte[] waiting = f.Sent(player).Last(p => Hud(p, 0x0f));
        Assert.Equal(0u, BitConverter.ToUInt32(waiting, 8));
        Assert.Equal(13198u, BitConverter.ToUInt32(waiting, 12));
        Assert.DoesNotContain(f.Sent(player), p => Hud(p, 0x16));

        ForcePublicStart(f, player);
        f.Pump(() => f.Service.ForTest(player).Step == "Dropping");
        Assert.Single(f.Sent(player), p => Hud(p, 0x16));
        Assert.DoesNotContain(f.Sent(player), p => Hud(p, 0x18)); // No immediate victory with one player.
        Assert.Equal(mode, Assert.Single(f.Service.PublicMatches).Mode);
        Assert.Single(f.Service.PublicMatches.Single().Roster);
        ForcePublicStart(f, player);
        Assert.Single(f.Sent(player), p => Hud(p, 0x16));
    }

    [Theory]
    [InlineData(MatchMode.Solo, 1u)]
    [InlineData(MatchMode.Duos, 6u)]
    [InlineData(MatchMode.Fives, 7u)]
    public void LocalSecondReadyPlayerPublishesThreeMinuteCountdownAndLeavingRestoresWaiting(MatchMode mode, uint world)
    {
        using var f = new Fixture(mode: mode, lobby: ArrivalLobby,
            admissions: MatchAdmissionRegistry.Default,
            publicQueue: new PublicQueueOptions().ForLocalPlay());
        var first = f.Connect("first"); var second = f.Connect("second");
        LoadPublicPregame(f, first, world);
        f.Transfer(second, world);
        f.Pump(() => f.Sent(second).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning));
        f.Send(second, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
        Assert.Null(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs);
        long readyAt = Environment.TickCount64;
        f.Ready(second);
        f.Pump(() => f.Service.PublicMatches.Single().CountdownDeadlineMs is not null);
        Assert.InRange(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs!.Value,
            readyAt + 180_000, Environment.TickCount64 + 180_000);
        foreach (var player in new[] { first, second })
        {
            byte[] countdown = f.Sent(player).Last(p => Hud(p, 0x0f));
            Assert.InRange(BitConverter.ToUInt32(countdown, 8), 179_000u, 180_000u);
            Assert.Equal(13356u, BitConverter.ToUInt32(countdown, 12));
            Assert.DoesNotContain(f.Sent(player), p => Hud(p, 0x16));
        }
        f.Disconnect(second);
        f.Pump(() => BitConverter.ToUInt32(f.Sent(first).Last(p => Hud(p, 0x0f)), 12) == 13198);
        Assert.Null(Assert.Single(f.Service.PublicMatches).CountdownDeadlineMs);
        Assert.Equal(0u, BitConverter.ToUInt32(f.Sent(first).Last(p => Hud(p, 0x0f)), 8));
        Assert.Equal("Lobby", f.Service.ForTest(first).Step);
        Assert.DoesNotContain(f.Sent(first), p => Hud(p, 0x16));
    }

    [Theory]
    [InlineData("play")]
    [InlineData("menu")]
    public void PublicResultChoiceSurvivesNativeReloginAndPlayLoadsPregameWithoutAnotherClick(string choice)
    {
        using var f = new Fixture(countdown: 60000, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 0 });
        var old = f.Connect("replay", 0x1001);
        LoadPublicPregame(f, old);
        ulong previousMatch = f.Service.PublicMatches.Single().MatchId;
        f.Service.ForTest(old).EnterMatch();
        f.Service.ForTest(old).Damage(10000, Cranberry.Zone.World.DamageCause.ToxicGas);
        Assert.Equal("Ended", f.Service.ForTest(old).Step);
        f.Send(old, w =>
        {
            w.WriteByte(ZoneOpcodes.WallOfDataBase); w.WriteByte(5);
            w.WriteString("CRANBERRY_MATCH_ACTION_V1"); w.WriteString(choice); w.WriteUInt32(0);
        });
        Assert.DoesNotContain(f.Sent(old), p => Is(p, 0x11, 0x30));
        f.Send(old, w => { w.WriteByte(9); w.WriteUInt16(0x4e); w.WriteByte(0); });
        Assert.Single(f.Sent(old), p => Is(p, 0x11, 0x30));
        f.Disconnect(old);
        var next = f.Connect("replay", 0x1001);
        f.Send(next, w =>
        {
            w.WriteByte(ZoneOpcodes.WallOfDataBase); w.WriteByte(5);
            w.WriteString("LoadingScreenWindow"); w.WriteString("close"); w.WriteUInt32(0);
        });
        if (choice == "menu")
        {
            Assert.Equal("Menu", f.Service.ForTest(next).Step);
            Assert.Empty(f.Service.PublicMatches);
            return;
        }
        f.Pump(() => f.Sent(next).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning));
        f.Send(next, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
        f.Ready(next);
        Assert.Equal("Lobby", f.Service.ForTest(next).Step);
        Assert.NotEqual(previousMatch, f.Service.PublicMatches.Single().MatchId);
        Assert.Single(f.Service.PublicMatches.Single().Roster);
        Assert.DoesNotContain(f.Sent(next), p => Is(p, QueueExit.Opcode, QueueExit.SubOpcode));
    }

    [Fact]
    public void LoadingTimeoutReleasesOnlyTheFailedReservationFromTheUnfrozenPregame()
    {
        using var f = new Fixture(countdown: 60000, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 0, LoadTimeoutMs = 1000 });
        var a = f.Connect("a"); var b = f.Connect("b"); f.Transfer(a); f.Transfer(b);
        f.Pump(() => f.Sent(a).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning)
            && f.Sent(b).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning));
        f.Send(a, w => w.WriteByte(ZoneOpcodes.ClientIsReady)); f.Ready(a);
        Assert.Equal(2, f.Service.PublicMatches.Single().Roster.Count);
        f.Pump(() => f.Sent(b).Any(p => Is(p, 0x11, 0x30)));
        Assert.DoesNotContain(f.Sent(a), p => Is(p, 0x11, 0x30));
        Assert.Single(f.Service.PublicMatches.Single().Roster);
        Assert.Equal(1, f.Service.PublicMatches.Single().PresentPlayers);
        Assert.Equal(PublicMatchPhase.PRE_GAME, f.Service.PublicMatches.Single().Phase);
        Assert.Null(f.Service.PublicMatches.Single().CountdownDeadlineMs);
    }
    [Fact]
    public void PublicResultsWaitForExplicitExitWithoutSilentlyJoiningAnotherRoster()
    {
        using var f = new Fixture(countdown: 60000, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 0 });
        var a = f.Connect("a"); var b = f.Connect("b");
        LoadPublicPregame(f, a); LoadPublicPregame(f, b);
        f.Pump(() => f.Sent(a).Any(p => Hud(p, 0x16)) && f.Sent(b).Any(p => Hud(p, 0x16)));
        f.Service.ForTest(a).EnterMatch(); f.Service.ForTest(b).EnterMatch();
        f.Service.ForTest(a).Damage(10000, Cranberry.Zone.World.DamageCause.ToxicGas);
        var queue = Assert.IsType<PublicMatchQueue>(typeof(ZoneService).GetField("_publicQueues",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Service));
        List<PublicMatchPhase> transitions = [];
        queue.Transitioned += (_, phase) => transitions.Add(phase);
        f.Service.ForTest(a).ExpireEndedHold(); f.Service.ForTest(b).ExpireEndedHold();
        Assert.DoesNotContain(PublicMatchPhase.QUEUE_OPEN, transitions);
        Assert.Equal("Ended", f.Service.ForTest(a).Step);
        Assert.Equal("Ended", f.Service.ForTest(b).Step);
        foreach (var connection in new[] { a, b })
        {
            Assert.DoesNotContain(f.Sent(connection), p => Is(p, 0x11, 0x30));
            f.Send(connection, w => { w.WriteByte(9); w.WriteUInt16(0x4e); w.WriteByte(0); });
        }
        Assert.Equal("Menu", f.Service.ForTest(a).Step);
        Assert.Equal("Menu", f.Service.ForTest(b).Step);
        Assert.Empty(f.Service.PublicMatches);
        foreach (var connection in new[] { a, b })
            Assert.Single(f.Sent(connection), p => Is(p, 0x11, 0x30));
        f.Service.ForTest(a).ExpireEndedHold();
        Assert.Single(f.Sent(a), p => Is(p, 0x11, 0x30));
    }
    [Fact]
    public void AllocatedPregameLoadsWithoutAnotherNativeTransferRetry()
    {
        using var f = new Fixture(countdown: 60000, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 600 });
        var a = f.Connect("a"); var b = f.Connect("b");
        f.Transfer(a); f.Transfer(b);
        f.Pump(() => f.Sent(a).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning)
            && f.Sent(b).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning));
        Assert.Single(f.Service.PublicMatches);
        Assert.Equal(2, f.Service.PublicMatches[0].Roster.Count);
        Assert.Null(f.Service.PublicMatches[0].CountdownDeadlineMs);
    }
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)5)]
    [InlineData((byte)17)]
    public void NativeVersionRequestReturnsOnlyObservedVersionOfKnownSameMatchInfantry(byte version)
    {
        using var f = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var a = f.Connect("a"); var b = f.Connect("b"); var menu = f.Connect("menu");
        f.Zone(a); f.Ready(a); f.Zone(b); f.Ready(b);
        var peers = f.Service.PeerRegistry.Sessions.Where(p => p.InMatch).ToArray();
        foreach (var peer in peers) { peer.Position = new(1, 2, 3); peer.SetPose([0, 0, 0, 0, 0, 0, version]); }
        f.Pump(() => peers.All(p => p.View.KnownCount == 1));
        ulong guid = peers[1].CharacterGuid;
        void Request(Cranberry.Transport.SoeConnection link, ulong id) => f.Send(link, w =>
            { w.WriteByte(0x0f); w.WriteByte(0x57); w.WriteUInt64(id); });
        Request(a, guid); Request(a, guid);
        var reply = Assert.Single(f.Sent(a), p => Is(p, 0x0f, 0x56));
        Assert.Equal(12, reply.Length);
        Assert.Equal(guid, BitConverter.ToUInt64(reply, 3)); Assert.Equal(version, reply[11]);
        Request(menu, guid); Request(a, peers[0].CharacterGuid); Request(a, ulong.MaxValue);
        Assert.DoesNotContain(f.Sent(menu), p => Is(p, 0x0f, 0x56));
        Assert.Single(f.Sent(a), p => Is(p, 0x0f, 0x56));
        peers[1].MatchId++;
        peers[1].SetPose([0, 0, 0, 0, 0, 0, (byte)(version + 1)]);
        Request(a, guid);
        Assert.Single(f.Sent(a), p => Is(p, 0x0f, 0x56));
    }
    [Fact]
    public void StationaryPregameViewerDiscoversAndRemovesPeerWithoutFurtherMovementInput()
    {
        using var f = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var a = f.Connect("a"); var b = f.Connect("b"); f.Zone(a); f.Ready(a); f.Zone(b); f.Ready(b);
        foreach (var peer in f.Service.PeerRegistry.Sessions)
        {
            peer.Position = new(1, 2, 3);
            peer.SetPose([0, 0, 0, 0, 0, 0, 5]);
        }
        f.Pump(() => f.Sent(a).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.AddLightweightPc)
            && f.Sent(b).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.AddLightweightPc));
        f.Cancel(b);
        f.Pump(() => f.Sent(a).Any(p => Is(p, 0x0f, 1)));
    }
    [Fact]
    public void ProductionQueueLoadsBeforeCountdownAndAllowsSameModePregameWhileFirstRoundStarts()
    {
        using var f = new Fixture(countdown: 60000, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 600, MaxAllocatedMatches = 2 });
        var a = f.Connect("a"); var b = f.Connect("b");
        LoadPublicPregame(f, a); LoadPublicPregame(f, b);
        ulong first = f.Service.PublicMatches.Single().MatchId;
        f.Pump(() => f.Sent(a).Any(p => Hud(p, 0x16)) && f.Sent(b).Any(p => Hud(p, 0x16)));
        var c = f.Connect("c"); var d = f.Connect("d");
        LoadPublicPregame(f, c); LoadPublicPregame(f, d);
        Assert.Equal(2, f.Service.PublicMatches.Count);
        Assert.Equal(PublicMatchPhase.PRE_GAME, f.Service.PublicMatches.Single(r => r.MatchId != first).Phase);
        Assert.True(f.Service.PublicMatches.Single(r => r.MatchId == first).Phase >= PublicMatchPhase.STARTING);
        Assert.Equal(new ulong[] { GetGuid(a), GetGuid(b) }, f.Service.PublicMatches.Single(r => r.MatchId == first).Roster);
        Assert.Equal(2, f.Service.PeerRegistry.Sessions.Where(p => p.InMatch).Select(p => p.MatchId).Distinct().Count());
        static ulong GetGuid(Cranberry.Transport.SoeConnection link) =>
            (ulong)link.Tag!.GetType().GetProperty("Guid")!.GetValue(link.Tag)!;
    }

    [Fact]
    public void QueueTimerSurvivesFirstConnectionLeavingAndExpiredPartyReleasesAllocation()
    {
        using var f = new Fixture(lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 300, AcceptTimeoutMs = 500 });
        var a = f.Connect("a"); var b = f.Connect("b"); var c = f.Connect("c");
        f.Transfer(a); f.Transfer(b); f.Transfer(c); f.Disconnect(a);
        f.Pump(() => f.Sent(b).Any(p => Is(p, 0xa6, 4)));
        f.Pump(() => f.Service.PublicMatches.Count == 0);
    }
}
