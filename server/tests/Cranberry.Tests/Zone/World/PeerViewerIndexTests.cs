using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

public sealed class PeerViewerIndexTests
{
    [Fact]
    public void SpreadOutMatchVisitsOnlyTheViewersThatHoldEachPlayer()
    {
        var registry = new SessionRegistry();
        var players = Enumerable.Range(0, 150).Select(i => Join(registry, (ulong)i + 1,
            new Vector3(i / 10 * 5000, 0, i % 10))).ToArray();
        List<PeerEnter> enters = []; List<PeerLeave> leaves = []; List<PeerViewer> viewers = [];
        foreach (var player in players)
            for (int pass = 0; pass < 2; pass++) registry.Sweep(player, enters, leaves);
        long before = registry.RelayCandidatesVisited;
        foreach (var player in players)
        {
            registry.CollectViewers(player, viewers);
            Assert.Equal(9, viewers.Count);
            Assert.All(viewers, v => Assert.True(v.Viewer.View.Transients.TryGet(player.Key, out uint id)
                && id == v.TransientId));
        }
        Assert.Equal(150 * 9, registry.RelayCandidatesVisited - before);
    }

    [Fact]
    public void MappingChangesAndTransfersPreserveMemberOrderAndTransientIds()
    {
        var registry = new SessionRegistry();
        var subject = Join(registry, 1, Vector3.Zero);
        var first = Join(registry, 2, Vector3.Zero);
        var second = Join(registry, 3, Vector3.Zero);
        // Acquisition order is deliberately the opposite of match membership order.
        Hold(second, subject); Hold(first, subject); Hold(subject, subject);
        Check(first, second);
        first.View.Transients.Acquire(subject.Key); // Idempotent, no duplicate viewer.
        Check(first, second);
        first.View.Clear(); Check(second);
        Hold(first, subject); Check(first, second);
        first.InMatch = false; Check(second);
        first.InMatch = true; Check(second, first);
        first.MatchId = 2; Check(second);
        first.MatchId = 1; Check(second, first);
        first.View.Transients.Release(subject.Key); Check(second);
        Hold(first, subject); Check(second, first);
        ((Sink)second.Sink).IsOpen = false; Check(first);
        registry.SweepClosed(); Check(first);
        registry.ForgetEverywhere(subject.CharacterGuid, []); Check();

        void Check(params PeerSession[] expected)
        {
            List<PeerViewer> actual = [];
            registry.CollectViewers(subject, actual);
            Assert.Equal(expected, actual.Select(v => v.Viewer));
            foreach (var v in actual)
            {
                Assert.True(v.Viewer.View.Transients.TryGet(subject.Key, out uint id));
                Assert.Equal(id, v.TransientId);
            }
        }
    }

    [Fact]
    public void ReplacementAndRemovalDetachOldTablesAndAllowPreMembershipMappings()
    {
        var registry = new SessionRegistry();
        var subject = Join(registry, 1, Vector3.Zero);
        var old = Join(registry, 2, Vector3.Zero);
        Hold(old, subject);
        var replacement = registry.Register(2, new Sink());
        Hold(replacement, subject);
        Check();
        replacement.MatchId = 1; replacement.InMatch = true;
        Check(replacement);
        old.View.Clear(); Hold(old, subject); old.InMatch = false; old.InMatch = true;
        Check(replacement);
        // Subject replacement retains viewers' exact existing bindings, like the old scan.
        subject = Join(registry, 1, Vector3.Zero); Check(replacement);
        Assert.True(registry.Remove(2));
        replacement.View.Clear(); Hold(replacement, subject);
        replacement.InMatch = false; replacement.InMatch = true;
        Check();

        void Check(params PeerSession[] expected)
        {
            List<PeerViewer> viewers = [];
            registry.CollectViewers(subject, viewers);
            Assert.Equal(expected, viewers.Select(v => v.Viewer));
        }
    }

    [Fact]
    public void RandomLifecycleChangesAgreeWithIndependentFullScan()
    {
        var random = new Random(50913);
        var registry = new SessionRegistry();
        var peers = Enumerable.Range(1, 40).Select(i => Join(registry, (ulong)i, Vector3.Zero)).ToArray();
        long sequence = peers.Length;
        var order = peers.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => (long)x.i);
        List<PeerViewer> actual = [];
        for (int step = 0; step < 1200; step++)
        {
            int index = random.Next(peers.Length);
            var viewer = peers[index];
            var subject = peers[random.Next(peers.Length)];
            switch (random.Next(9))
            {
                case 0: case 1: Hold(viewer, subject); break;
                case 2: viewer.View.Transients.Release(subject.Key); break;
                case 3: viewer.View.Clear(); break;
                case 4:
                    viewer.InMatch = !viewer.InMatch;
                    if (viewer.InMatch) order[viewer] = ++sequence;
                    break;
                case 5:
                    viewer.MatchId = viewer.MatchId == 1 ? 2UL : 1UL;
                    if (viewer.InMatch) order[viewer] = ++sequence;
                    break;
                case 6: ((Sink)viewer.Sink).IsOpen = !viewer.IsOpen; break;
                case 7:
                    var next = Join(registry, viewer.CharacterGuid, Vector3.Zero);
                    peers[index] = next; order[next] = ++sequence;
                    // Mutating the old reference must not put it back in the index.
                    viewer.View.Clear(); Hold(viewer, subject);
                    viewer.InMatch = false; viewer.InMatch = true;
                    break;
                case 8: registry.ForgetEverywhere(subject.CharacterGuid, []); break;
            }
            foreach (var current in peers)
            {
                var expected = peers.Where(v => !ReferenceEquals(v, current) && v.IsOpen && v.InMatch
                        && v.MatchId == current.MatchId && v.View.Transients.TryGet(current.Key, out _))
                    .OrderBy(v => order[v]).Select(v => new PeerViewer(v, GetTransient(v, current))).ToArray();
                registry.CollectViewers(current, actual);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void RepeatedMovementFanOutAllocatesNoMemoryAfterWarmup()
    {
        var registry = new SessionRegistry();
        var subject = Join(registry, 1, Vector3.Zero);
        for (ulong id = 2; id <= 150; id++) Hold(Join(registry, id, Vector3.Zero), subject);
        List<PeerViewer> viewers = new(150);
        for (int i = 0; i < 100; i++) registry.CollectViewers(subject, viewers);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) registry.CollectViewers(subject, viewers);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(149, viewers.Count);
    }

    private static uint GetTransient(PeerSession viewer, PeerSession subject)
    {
        Assert.True(viewer.View.Transients.TryGet(subject.Key, out uint id));
        return id;
    }

    private static void Hold(PeerSession viewer, PeerSession subject)
    {
        viewer.View.MarkKnown(subject.Key);
        viewer.View.Transients.Acquire(subject.Key);
    }

    private static PeerSession Join(SessionRegistry registry, ulong id, Vector3 position)
    {
        var peer = registry.Register(id, new Sink());
        peer.MatchId = 1; peer.InMatch = true;
        peer.Position = position;
        peer.SetPose(MovementRecord.Position(position));
        return peer;
    }

    private sealed class Sink : IPeerSink
    {
        public bool IsOpen { get; set; } = true;
        public void Send(byte[] packet) { }
    }
}
