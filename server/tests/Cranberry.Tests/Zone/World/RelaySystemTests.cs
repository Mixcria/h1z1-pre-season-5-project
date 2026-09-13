using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/22 §6.1: per (viewer, subject) a relay is a bounds check, a varint and a span copy - never a
// re-encode. The gate is MatchSettings.RelayEnabled, the rollback flag of migration step 7.
public sealed class RelaySystemTests
{
    private static readonly Vector3 Origin = new(0f, 506.25f, 0f);

    private static MatchHarness Harness(bool relay, int budgetBytes = 400) =>
        new(MatchSettings.Default with
        {
            InterestStride = 1,
            RelayStride = 1,
            RelayEnabled = relay,
            RelayBudgetBytes = budgetBytes,
        });

    [Fact]
    public void NothingLeavesWhileTheRelayIsGatedOff()
    {
        MatchHarness harness = Harness(relay: false);
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, Origin);
        harness.MoveTo(bob, Origin with { X = 10f });

        harness.PostMovement(alice, Origin with { X = 1f });
        harness.Step(2);

        Assert.Equal(0, harness.Match.Relay.Sent);
        Assert.True(harness.Match.Relay.Suppressed > 0);
        Assert.DoesNotContain(MatchHarness.Sent(bob), packet => packet.Channel == RelaySystem.MovementChannel);
    }

    [Fact]
    public void AKnownPeersRecordIsRelayedVerbatim()
    {
        MatchHarness harness = Harness(relay: true);
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, Origin);
        harness.MoveTo(bob, Origin with { X = 10f });

        byte[] record = MovementRecord.Position(Origin with { X = 1f });
        harness.Match.Post(new Command(CommandKind.Movement, alice.Slot, EntityId.None, 0, 0, 0), record);
        harness.Step();

        RecordedPacket relayed = Assert.Single(
            MatchHarness.Sent(bob),
            packet => packet.Channel == RelaySystem.MovementChannel);

        uint transientId = bob.View.Transients.TryGet(alice.Id, out uint id) ? id : 0;
        int varIntLength = ClientVarInt.Length(transientId);

        Assert.Equal(varIntLength + record.Length, relayed.Body.Length);
        Assert.Equal(record, relayed.Body[varIntLength..]);
    }

    [Fact]
    public void AnUnmovedPeerCostsNothing()
    {
        MatchHarness harness = Harness(relay: true);
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, Origin);
        harness.MoveTo(bob, Origin with { X = 10f });

        harness.PostMovement(alice, Origin with { X = 1f });
        harness.Step();
        long afterFirst = harness.Match.Relay.Sent;
        Assert.True(afterFirst > 0);

        harness.Step(10);
        Assert.Equal(afterFirst, harness.Match.Relay.Sent);
    }

    [Fact]
    public void APeerNobodyKnowsIsNeverRelayed()
    {
        MatchHarness harness = Harness(relay: true);
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, Origin);
        harness.MoveTo(bob, Origin with { X = 5_000f });

        harness.PostMovement(alice, Origin with { X = 1f });
        harness.Step(4);

        Assert.Equal(0, harness.Match.Relay.Sent);
        Assert.Empty(MatchHarness.Sent(bob));
    }

    [Fact]
    public void TheByteBudgetStopsAPassAndTheCursorResumesIt()
    {
        // One 17-byte body per pass: a crowded view degrades to a slower rate for everyone rather
        // than freezing the far half of it.
        MatchHarness harness = Harness(relay: true, budgetBytes: 20);
        MatchPlayer viewer = harness.AddPlayer("Viewer");
        harness.MoveTo(viewer, Origin);

        var peers = new List<MatchPlayer>();
        for (int i = 0; i < 3; i++)
        {
            MatchPlayer peer = harness.AddPlayer($"Peer{i}");
            harness.MoveTo(peer, Origin with { X = 10f + i });
            peers.Add(peer);
        }

        for (int tick = 0; tick < 6; tick++)
        {
            foreach (MatchPlayer peer in peers)
            {
                harness.PostMovement(peer, Origin with { X = 10f + peers.IndexOf(peer) + (tick * 0.25f) });
            }

            harness.Step();
        }

        List<RecordedPacket> relayed = MatchHarness.Sent(viewer)
            .Where(packet => packet.Channel == RelaySystem.MovementChannel)
            .ToList();

        Assert.True(harness.Match.Relay.BudgetStops > 0, "The budget never bit.");
        Assert.True(relayed.Count >= 3, $"Only {relayed.Count} bodies were relayed.");

        // The cursor rotated: more than one subject was served across the passes.
        Assert.True(relayed.Select(packet => packet.Body[0]).Distinct().Count() > 1);
    }

    [Fact]
    public void APeerDeferredByTheBudgetIsStillRelayedAfterItStopsMoving()
    {
        // Deferred, not lost (docs/22 §6.1). The staleness gate is
        // `subject.LastPoseTick <= view.LastRelayTick`, so marking the pass complete after a budget
        // stop retires the poses the budget skipped: the peer goes still and the viewer keeps its
        // last position for the rest of the match.
        MatchHarness harness = Harness(relay: true, budgetBytes: 20);
        MatchPlayer viewer = harness.AddPlayer("Viewer");
        harness.MoveTo(viewer, Origin);

        var peers = new List<MatchPlayer>();
        for (int i = 0; i < 3; i++)
        {
            MatchPlayer peer = harness.AddPlayer($"Peer{i}");
            harness.MoveTo(peer, Origin with { X = 10f + i });
            peers.Add(peer);
        }

        // One burst that overflows the budget, then silence.
        harness.Step(4);
        for (int i = 0; i < peers.Count; i++)
        {
            harness.PostMovement(peers[i], Origin with { X = 20f + i });
        }

        harness.Step();
        Assert.True(harness.Match.Relay.BudgetStops > 0, "The budget never bit.");

        harness.Step(20);

        // Every peer's one pose eventually reaches the viewer even though none of them moved again.
        List<byte> transients = MatchHarness.Sent(viewer)
            .Where(packet => packet.Channel == RelaySystem.MovementChannel)
            .Select(packet => packet.Body[0])
            .Distinct()
            .ToList();
        Assert.Equal(peers.Count, transients.Count);
    }

    [Fact]
    public void TheRelayRunsOnItsOwnStride()
    {
        var harness = new MatchHarness(MatchSettings.Default with
        {
            InterestStride = 1,
            RelayStride = 8,
            RelayEnabled = true,
        });

        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, Origin);
        harness.MoveTo(bob, Origin with { X = 10f });

        harness.PostMovement(alice, Origin with { X = 1f });
        harness.Step(4);
        Assert.Equal(0, harness.Match.Relay.Sent);

        harness.Step(8);
        Assert.True(harness.Match.Relay.Sent > 0);
    }

    [Fact]
    public void ADisconnectedViewerIsSelectedForButNeverWrittenTo()
    {
        MatchHarness harness = Harness(relay: true);
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, Origin);
        harness.MoveTo(bob, Origin with { X = 10f });
        MatchHarness.SinkOf(bob).IsOpen = false;

        harness.PostMovement(alice, Origin with { X = 1f });
        harness.Step(2);

        Assert.Empty(MatchHarness.Sent(bob));
        Assert.True(harness.Match.Relay.Suppressed > 0);
    }

    /// <summary>
    /// <b>The budget levers the plan can pull (Phase 3A).</b> If the August client will not take an
    /// inbound SOE <c>Multi</c>, one relay body is one retained reliable datagram, and the fallback
    /// is the reduced pair <c>RelayStride 4 / RelayBudgetBytes 200</c>. Both the shipped defaults and
    /// that fallback are pinned here so a change to either is a deliberate one - and the plan's own
    /// text describes the reduced pair as the fallback, not as today's values, which these
    /// assertions make unambiguous.
    /// </summary>
    [Fact]
    public void TheShippedRelayBudgetIsTheOneTheStrideTestsAssume()
    {
        MatchSettings shipped = MatchSettings.Default;

        Assert.Equal(2, shipped.RelayStride);
        Assert.Equal(16 * 1024, shipped.RelayBudgetBytes);
        Assert.Equal(4, shipped.InterestStride);
        Assert.False(shipped.RelayEnabled);
    }

    /// <summary>
    /// The Phase 3A fallback halves the rate and halves the bytes; nothing else about the relay
    /// changes, which is the point of expressing it as two settings rather than a second code path.
    /// </summary>
    [Fact]
    public void TheReducedFallbackBudgetStillRelays()
    {
        var harness = new MatchHarness(MatchSettings.Default with
        {
            InterestStride = 1,
            RelayStride = 4,
            RelayBudgetBytes = 200,
            RelayEnabled = true,
        });

        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, Origin);
        harness.MoveTo(bob, Origin with { X = 10f });

        harness.PostMovement(alice, Origin with { X = 1f });
        harness.Step(8);

        Assert.True(harness.Match.Relay.Sent > 0);
        Assert.Equal(0, harness.Match.Relay.BudgetStops);
    }

}
