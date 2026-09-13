using System.Diagnostics;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class BountyGatewayTests
{
    [Fact]
    public void PartyWaitingOnAClosingLobbyMovesTogetherIntoTheNextPublicRound()
    {
        using var f = new Fixture(mode: MatchMode.Duos, countdown: 50, lobby: ArrivalLobby,
            admissions: new([new(6, 5, MatchQueueKind.Public, MatchMode.Duos)]));
        var current = f.Connect("a"); f.Zone(current, 6);
        var leader = f.Connect("b"); var guest = f.Connect("c");
        Assert.Null(f.Service.LauncherQueue(["b", "c"], "Duos"));
        static MatchAdmissionContext Admission(Cranberry.Transport.SoeConnection player) =>
            (MatchAdmissionContext)player.Tag!.GetType().GetProperty("BountyAdmission")!.GetValue(player.Tag)!;
        ulong oldMatch = Admission(current).MatchId;
        Assert.Equal(oldMatch, Admission(leader).MatchId);
        Assert.Equal(Admission(leader), Admission(guest));

        f.Ready(current);
        f.Pump(() => f.Sent(current).Any(p => Hud(p, 0x16)));
        f.Transfer(leader, 6); // Refresh the closed forming cohort atomically.
        f.Transfer(leader, 6); // The active old round no longer blocks this cohort.
        f.Pump(() => new[] { leader, guest }.All(p => f.Sent(p).Any(packet =>
            packet.Length > 1 && packet[1] == ZoneOpcodes.ClientBeginZoning)));
        Assert.NotEqual(oldMatch, Admission(leader).MatchId);
        Assert.Equal(Admission(leader), Admission(guest));
    }

    [Theory]
    [InlineData(MatchMode.Solo)]
    [InlineData(MatchMode.Duos)]
    [InlineData(MatchMode.Fives)]
    public void StartedPublicMatchAllowsIndependentSameModeRound(MatchMode mode)
    {
        using var f = new Fixture(mode: mode, countdown: 50, lobby: ArrivalLobby);
        var first = f.Connect("a");
        f.Zone(first); f.Ready(first);
        f.Pump(() => f.Sent(first).Any(p => Hud(p, 0x16)));
        var second = f.Connect("b");
        // Embedded immediate-staging fixtures omit the production timed allocator, which has
        // its own early-accept and frozen-roster gateway regressions in PublicMatchQueueTests.
        f.Transfer(second); f.Transfer(second);
        f.Pump(() => f.Sent(second).Any(p => p.Length > 1 && p[1] == ZoneOpcodes.ClientBeginZoning));
        f.Send(second, w => w.WriteByte(ZoneOpcodes.ClientIsReady)); f.Ready(second);
        f.Pump(() => f.Sent(second).Any(p => Hud(p, 0x16)));
        Assert.Equal(2, f.Service.PeerRegistry.Sessions.Where(p => p.InMatch).Select(p => p.MatchId).Distinct().Count());
    }

    [Fact]
    public void SecondaryPublicWorldAliasesJoinTheSamePregameAndVoiceRoster()
    {
        using var f = new Fixture(countdown: 60000, lobby: ArrivalLobby,
            admissions: new([new(1, 13, MatchQueueKind.Public, MatchMode.Solo),
                new(9, 13, MatchQueueKind.Public, MatchMode.Solo, WorldNumber: 2)]));
        var first = f.Connect("a"); var second = f.Connect("b"); var menu = f.Connect("c");
        f.Zone(first); f.Ready(first); f.Zone(second, 9); f.Ready(second);
        var peers = f.Service.PeerRegistry.Sessions.Where(p => p.InMatch).ToArray();
        Assert.Equal(2, peers.Length);
        Assert.Equal(peers[0].MatchId, peers[1].MatchId);
        foreach (var peer in peers) { peer.Position = new(1, 2, 3); peer.SetPose([0, 0, 0, 0, 0, 0, 0]); }
        var enters = new List<Cranberry.Zone.World.PeerEnter>();
        f.Service.PeerRegistry.Sweep(peers[0], enters, []);
        Assert.Single(enters);
        Assert.Equal(2, f.Service.ProximityVoicePlayers().Count);
        f.Cancel(second);
        Assert.Single(f.Service.ProximityVoicePlayers());
    }

    [Fact]
    public void PublicModesCanRunIndependently()
    {
        using var f = new Fixture(countdown: 50, lobby: ArrivalLobby,
            admissions: new([new(1, 13, MatchQueueKind.Public, MatchMode.Solo),
                new(6, 5, MatchQueueKind.Public, MatchMode.Duos)]));
        var solo = f.Connect("a"); f.Zone(solo); f.Ready(solo);
        f.Pump(() => f.Sent(solo).Any(p => Hud(p, 0x16)));
        var duo = f.Connect("b"); f.Zone(duo, 6); f.Ready(duo);
        f.Pump(() => f.Sent(duo).Any(p => Hud(p, 0x16)));
    }
}
