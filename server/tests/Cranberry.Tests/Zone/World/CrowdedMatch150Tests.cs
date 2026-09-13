using System.Diagnostics;
using System.Numerics;
using Cranberry.Zone.World;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.World;

public sealed class CrowdedMatch150Tests(ITestOutputHelper output)
{
    private sealed class Sink : IPeerSink
    {
        public bool IsOpen => true;
        public void Send(byte[] packet) { }
    }

    [Fact]
    public void EveryPlayerCanSee149NeighboursAndOtherMatchesAreIsolated()
    {
        var registry = new SessionRegistry();
        var peers = new List<PeerSession>();
        for (ulong n = 1; n <= 150; n++)
        {
            var peer = registry.Register(n, new Sink());
            peer.InMatch = true; peer.MatchId = 1;
            peer.Position = new Vector3(n % 10, 0, n / 10);
            peer.SetPose(MovementRecord.Position(peer.Position));
            peers.Add(peer);
        }
        var enters = new List<PeerEnter>();
        var leaves = new List<PeerLeave>();
        var viewers = new List<PeerViewer>();
        for (int tick = 0; tick < 20; tick++)
            foreach (var peer in peers) registry.Sweep(peer, enters, leaves);
        foreach (var peer in peers)
        {
            Assert.Equal(149, peer.View.KnownCount);
            registry.CollectViewers(peer, viewers);
            Assert.Equal(149, viewers.Count);
        }
        var elapsed = new List<double>();
        for (int tick = 0; tick < 1000; tick++)
        {
            long start = Stopwatch.GetTimestamp();
            foreach (var peer in peers)
            {
                registry.Sweep(peer, enters, leaves);
                registry.CollectViewers(peer, viewers);
                foreach (var viewer in viewers)
                    viewer.Viewer.Sink.Send(PeerBurst.Pose(viewer.TransientId, peer.Pose));
            }
            elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        elapsed.Sort();
        output.WriteLine($"150-player interest + 22,350 pose encodes per pass: p50={elapsed[500]:F2}ms p95={elapsed[950]:F2}ms p99={elapsed[990]:F2}ms max={elapsed[^1]:F2}ms (in-memory sinks, no sockets).");
        peers[0].MatchId = 2;
        registry.CollectViewers(peers[0], viewers);
        Assert.Empty(viewers);
        for (int tick = 0; tick < 10; tick++) registry.Sweep(peers[0], enters, leaves);
        Assert.Equal(0, peers[0].View.KnownCount);
    }

    [Fact]
    public void DefaultSimulationAccepts150PlayersAndDrainsTheirMovementEachTick()
    {
        var harness = new MatchHarness();
        var players = Enumerable.Range(0, 150).Select(_ => harness.AddPlayer()).ToArray();
        for (int tick = 0; tick < 40; tick++)
        {
            foreach (var player in players)
                Assert.True(harness.PostMovement(player, player.Position, (uint)tick * 50));
            harness.Step();
            Assert.Equal(0, harness.Match.Commands.Count);
        }
    }
}
