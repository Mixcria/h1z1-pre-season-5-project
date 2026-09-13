using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class BountyGatewayTests
{
    [Fact]
    public void PublicDropLoadingCallbackCannotRetireTheNextQueueGeneration()
    {
        using var f = new Fixture(countdown: 100, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 0, LoadTimeoutMs = 1000 });
        var a = f.Connect("a"); var b = f.Connect("b");
        f.Transfer(a); f.Transfer(b);
        f.Pump(() => f.Service.ForTest(a).Step == "Zoning" && f.Service.ForTest(b).Step == "Zoning");
        foreach (var link in new[] { a, b })
        {
            f.Send(link, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
            f.Ready(link);
        }
        ulong oldMatch = f.Service.PublicMatches.Single().MatchId;
        f.Cancel(a);
        LoadPublicPregame(f, a); // The successor is a physical lobby even with only one player.
        Assert.Equal("Lobby", f.Service.ForTest(a).Step);
        bool oldDeadlinePassed = false;
        _ = Task.Delay(1500).ContinueWith(_ => f.Pending.Enqueue(() => oldDeadlinePassed = true));
        f.Pump(() => oldDeadlinePassed);
        Assert.Equal("Lobby", f.Service.ForTest(a).Step);
        Assert.Contains(f.Service.PublicMatches, match => match.MatchId != oldMatch
            && match.Phase == PublicMatchPhase.PRE_GAME && match.PresentPlayers == 1
            && match.CountdownDeadlineMs is null);
        Assert.DoesNotContain(f.Sent(a), p => Is(p, 0x11, 0x30));
    }

    [Fact]
    public void PublicDropLoadingTimeoutRetiresAnUnreleasedPlayerButKeepsTheReadyCanopyPeer()
    {
        using var f = new Fixture(countdown: 100, lobby: ArrivalLobby,
            publicQueue: new() { WaitMs = 0, LoadTimeoutMs = 1000 });
        var ready = f.Connect("ready"); var stalled = f.Connect("stalled");
        f.Transfer(ready); f.Transfer(stalled);
        f.Pump(() => f.Service.ForTest(ready).Step == "Zoning"
            && f.Service.ForTest(stalled).Step == "Zoning");
        foreach (var link in new[] { ready, stalled })
        {
            f.Send(link, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
            f.Ready(link);
        }
        f.Pump(() => f.Service.ForTest(ready).Step == "Dropping"
            && f.Service.ForTest(stalled).Step == "Dropping");
        var roster = f.Service.PublicMatches.Single().Roster.ToArray();
        // This is the actual August readiness input. ReleaseTeleport changes InMatch before
        // descent/landing, so an accepted parachute must not be mistaken for pending loading.
        f.Send(ready, new SynchronizedTeleport(SynchronizedTeleport.ClientReady).WriteTo);
        Assert.Equal("InMatch", f.Service.ForTest(ready).Step);
        f.Pump(() => f.Sent(stalled).Any(p => Is(p, 0x11, 0x30)));
        Assert.Single(f.Sent(stalled), p => Is(p, 0x11, 0x30));
        Assert.DoesNotContain(f.Sent(ready), p => Is(p, 0x11, 0x30));
        Assert.Equal("Menu", f.Service.ForTest(stalled).Step);
        Assert.Equal(roster, f.Service.PublicMatches.Single().Roster);
        Assert.Equal(1, f.Service.PublicMatches.Single().PresentPlayers);
    }
}
