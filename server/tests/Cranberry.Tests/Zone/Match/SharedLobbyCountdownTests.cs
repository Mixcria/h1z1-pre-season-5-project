using System.Diagnostics;
using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class BountyGatewayTests
{
    private static LobbyOptions ArrivalLobby => LobbyOptions.Default with { FallbackArmMs = 60_000 };

    private static uint LobbyCountdown(Fixture fixture, SoeConnection connection) =>
        BitConverter.ToUInt32(fixture.Sent(connection).Last(packet => Hud(packet, 0x0f)), 8);

    [Theory]
    [InlineData(MatchMode.Solo)]
    [InlineData(MatchMode.Duos)]
    [InlineData(MatchMode.Fives)]
    public void StaggeredArrivalsShareTheCountdownAndStartTogether(MatchMode mode)
    {
        using var fixture = new Fixture(mode: mode, countdown: 1600, lobby: ArrivalLobby);
        var first = fixture.Connect("a");
        var second = fixture.Connect("b");
        fixture.Zone(first); fixture.Zone(second);
        fixture.Ready(first);
        Thread.Sleep(400);
        fixture.Ready(second);

        Assert.Equal(1600u, LobbyCountdown(fixture, first));
        Assert.InRange(LobbyCountdown(fixture, second), 1u, 1250u);
        fixture.Pump(() => fixture.Sent(first).Any(packet => Hud(packet, 0x16)));
        var gap = Stopwatch.StartNew();
        fixture.Pump(() => fixture.Sent(second).Any(packet => Hud(packet, 0x16)));
        Assert.True(gap.ElapsedMilliseconds < 250, $"Second player started {gap.ElapsedMilliseconds} ms later.");
    }

    [Fact]
    public void LateArrivalDoesNotReplayBannerThresholdsThatHavePassed()
    {
        using var fixture = new Fixture(countdown: 2200,
            lobby: ArrivalLobby with { BannerSeconds = [1u] });
        var first = fixture.Connect("a");
        var second = fixture.Connect("b");
        fixture.Zone(first); fixture.Zone(second);
        fixture.Ready(first);
        fixture.Pump(() => fixture.Sent(first).Any(packet => Hud(packet, 0x14)));
        Thread.Sleep(100);
        fixture.Ready(second);

        Assert.InRange(LobbyCountdown(fixture, second), 1u, 1000u);
        fixture.Pump(() => fixture.Sent(second).Any(packet => Hud(packet, 0x16)));
        Assert.DoesNotContain(fixture.Sent(second), packet => Hud(packet, 0x14));
        Assert.Single(fixture.Sent(first), packet => Hud(packet, 0x14));
    }

    [Fact]
    public void CountdownSurvivesTheFirstArrivalDisconnecting()
    {
        using var fixture = new Fixture(countdown: 1600, lobby: ArrivalLobby);
        var first = fixture.Connect("a");
        var second = fixture.Connect("b");
        fixture.Zone(first); fixture.Zone(second);
        fixture.Ready(first);
        Thread.Sleep(400);
        fixture.Disconnect(first);
        fixture.Ready(second);

        Assert.InRange(LobbyCountdown(fixture, second), 1u, 1250u);
        fixture.Pump(() => fixture.Sent(second).Any(packet => Hud(packet, 0x16)));
        Assert.DoesNotContain(fixture.Sent(first), packet => Hud(packet, 0x16));
    }

    [Fact]
    public void AlreadyAdmittedPlayerFinishingAfterTheDeadlineStartsWithoutAnotherCountdown()
    {
        using var fixture = new Fixture(countdown: 200, lobby: ArrivalLobby);
        var first = fixture.Connect("a");
        var second = fixture.Connect("b");
        fixture.Zone(first); fixture.Zone(second);
        fixture.Ready(first);
        fixture.Pump(() => fixture.Sent(first).Any(packet => Hud(packet, 0x16)));
        fixture.Ready(second);

        Assert.Equal(0u, LobbyCountdown(fixture, second));
        fixture.Pump(() => fixture.Sent(second).Any(packet => Hud(packet, 0x16)));
    }

    [Fact]
    public void SeparateWorldMatchesHaveIndependentCountdowns()
    {
        using var fixture = new Fixture(countdown: 1600, lobby: ArrivalLobby,
            admissions: new([
                new(1, 13, MatchQueueKind.Public, MatchMode.Solo),
                new(2, 13, MatchQueueKind.Public, MatchMode.Duos),
            ]));
        var first = fixture.Connect("a");
        var second = fixture.Connect("b");
        fixture.Zone(first); fixture.Zone(second, world: 2);
        fixture.Ready(first);
        Thread.Sleep(400);
        fixture.Ready(second);

        Assert.Equal(1600u, LobbyCountdown(fixture, first));
        Assert.Equal(1600u, LobbyCountdown(fixture, second));
    }

    [Fact]
    public void LeavingAnEmptyLobbyAndRejoiningStartsANewCountdown()
    {
        using var fixture = new Fixture(countdown: 1600, lobby: ArrivalLobby);
        var connection = fixture.Connect();
        fixture.Zone(connection); fixture.Ready(connection);
        Thread.Sleep(400);
        fixture.Cancel(connection);
        fixture.Zone(connection); fixture.Ready(connection);

        Assert.Equal(1600u, LobbyCountdown(fixture, connection));
        var elapsed = Stopwatch.StartNew();
        fixture.Pump(() => elapsed.ElapsedMilliseconds >= 900);
        Assert.DoesNotContain(fixture.Sent(connection), packet => Hud(packet, 0x16));
    }

    [Fact]
    public void HostedLobbyKeepsItsManualStartWithoutAutomaticCountdownOrBanners()
    {
        using var fixture = new Fixture(MatchQueueKind.Hosted, countdown: 200,
            lobby: ArrivalLobby with { BannerSeconds = [1u] });
        var connection = fixture.Connect();
        fixture.Zone(connection); fixture.Ready(connection);
        Assert.Equal(0u, LobbyCountdown(fixture, connection));

        var elapsed = Stopwatch.StartNew();
        fixture.Pump(() => elapsed.ElapsedMilliseconds >= 400);
        Assert.DoesNotContain(fixture.Sent(connection), packet => Hud(packet, 0x16) || Hud(packet, 0x14));
    }
}
