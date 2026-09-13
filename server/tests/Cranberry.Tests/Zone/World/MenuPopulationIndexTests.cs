using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

public sealed class MenuPopulationIndexTests
{
    [Fact]
    public void FiveThousandMenuSessionsDoNotEnterMatchCandidateScans()
    {
        var registry = new SessionRegistry();
        for (ulong i = 1000; i < 6000; i++) registry.Register(i, new Sink());
        var players = Enumerable.Range(1, 150).Select(i => Join(registry, (ulong)i, 7)).ToArray();
        List<PeerEnter> enters = []; List<PeerLeave> leaves = []; List<PeerViewer> viewers = [];
        foreach (PeerSession player in players)
            for (int pass = 0; pass < 20; pass++) registry.Sweep(player, enters, leaves);
        long visited = registry.RelayCandidatesVisited;
        foreach (PeerSession player in players)
        {
            Assert.Equal(149, player.View.KnownCount);
            registry.CollectViewers(player, viewers);
            Assert.Equal(149, viewers.Count);
        }
        Assert.Equal(150 * 149, registry.RelayCandidatesVisited - visited);
        visited = registry.InterestCandidatesVisited;
        foreach (PeerSession player in players) registry.Sweep(player, enters, leaves);
        Assert.Equal(150 * 150, registry.InterestCandidatesVisited - visited);
        Assert.All(registry.Sessions.Where(p => !p.InMatch), p => Assert.Equal(0, p.View.KnownCount));
    }

    [Fact]
    public void TransferReplacementAndClosedSessionSweepMaintainMembership()
    {
        var registry = new SessionRegistry();
        var alice = Join(registry, 1, 10); var bob = Join(registry, 2, 10);
        List<PeerEnter> enters = []; List<PeerLeave> leaves = [];
        registry.Sweep(alice, enters, leaves);
        Assert.Single(enters);
        bob.MatchId = 20;
        registry.Sweep(alice, enters, leaves);
        Assert.Empty(enters); Assert.Single(leaves);
        bob.InMatch = false; bob.MatchId = 10;
        registry.Sweep(alice, enters, leaves); Assert.Empty(enters);
        bob.InMatch = true;
        registry.Sweep(alice, enters, leaves); Assert.Single(enters);
        registry.ForgetEverywhere(bob.CharacterGuid, []);
        var replacement = Join(registry, 2, 20);
        bob.InMatch = false; bob.InMatch = true; // Stale reference cannot restore its membership.
        registry.Sweep(alice, enters, leaves); Assert.Empty(enters);
        replacement.MatchId = 10;
        registry.Sweep(alice, enters, leaves); Assert.Same(replacement, Assert.Single(enters).Subject);
        ((Sink)replacement.Sink).IsOpen = false;
        registry.SweepClosed();
        registry.Sweep(alice, enters, leaves);
        Assert.Single(leaves); Assert.Single(registry.Sessions);
        Assert.True(registry.Remove(1)); Assert.Empty(registry.Sessions);
    }

    private static PeerSession Join(SessionRegistry registry, ulong id, ulong match)
    {
        var peer = registry.Register(id, new Sink());
        peer.MatchId = match; peer.InMatch = true; peer.Position = Vector3.Zero;
        peer.SetPose(MovementRecord.Position(Vector3.Zero));
        return peer;
    }
    private sealed class Sink : IPeerSink
    {
        public bool IsOpen { get; set; } = true;
        public void Send(byte[] packet) { }
    }
}
