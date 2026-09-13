using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/22 §4.7: hysteresis, per-tick budgets and per-viewer transient ids. Nothing here sends a
// packet - the peer-spawn family (0xd9) is still underived - so what is pinned is the set algebra.
public sealed class ObserverViewTests
{
    private static MatchHarness Harness() =>
        new(MatchSettings.Default with { InterestStride = 1, RelayStride = 1 });

    private static Vector3 At(float z) => new(0f, 506f, z);

    [Fact]
    public void MarkForgottenSwapRemovesAndKeepsMembershipExact()
    {
        var view = new ObserverView();
        var ids = new List<EntityId>();
        for (ulong sequence = 1; sequence <= 10; sequence++)
        {
            EntityId id = EntityId.Create(EntityKind.Character, 1, sequence);
            ids.Add(id);
            view.MarkKnown(id);
        }

        view.MarkForgotten(ids[0]);
        view.MarkForgotten(ids[5]);

        Assert.Equal(8, view.KnownCount);
        Assert.False(view.Knows(ids[0]));
        Assert.False(view.Knows(ids[5]));
        foreach (EntityId id in ids.Where((_, index) => index is not 0 and not 5))
        {
            Assert.True(view.Knows(id));
        }
    }

    [Fact]
    public void MarkingTheSameEntityTwiceDoesNotDuplicateIt()
    {
        var view = new ObserverView();
        EntityId id = EntityId.Create(EntityKind.Character, 1, 1);

        view.MarkKnown(id);
        view.MarkKnown(id);

        Assert.Equal(1, view.KnownCount);
    }

    [Fact]
    public void AViewerNeverSeesItself()
    {
        MatchHarness harness = Harness();
        MatchPlayer alice = harness.AddPlayer("Alice");
        harness.Step(2);

        Assert.False(alice.View.Knows(alice.Id));
        Assert.Equal(0, alice.View.KnownCount);
    }

    [Fact]
    public void APeerOutsideTheEnterRadiusIsNotReplicated()
    {
        MatchHarness harness = Harness();
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");

        harness.MoveTo(alice, At(0f));
        harness.MoveTo(bob, At(2300f));
        harness.Step(2);

        Assert.False(alice.View.Knows(bob.Id));
        Assert.False(bob.View.Knows(alice.Id));
    }

    [Fact]
    public void AWalkAcrossTheBoundaryEntersOnceAndLeavesOnce()
    {
        MatchHarness harness = Harness();
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, At(0f));

        harness.MoveTo(bob, At(2300f));
        harness.Step(2);
        Assert.False(alice.View.Knows(bob.Id));

        // Inside the enter radius (310 m, D156).
        harness.MoveTo(bob, At(1900f));
        harness.Step(2);
        Assert.True(alice.View.Knows(bob.Id));
        long enteredOnce = harness.Match.Interest.Entered;

        // Back into the hysteresis band (between 310 m and 372 m): still known, no churn.
        harness.MoveTo(bob, At(2100f));
        harness.Step(10);
        Assert.True(alice.View.Knows(bob.Id));
        Assert.Equal(enteredOnce, harness.Match.Interest.Entered);
        Assert.Equal(0, harness.Match.Interest.Left);

        // Past the leave radius (372 m = 310 x 1.2).
        harness.MoveTo(bob, At(2300f));
        harness.Step(2);
        Assert.False(alice.View.Knows(bob.Id));
        Assert.True(harness.Match.Interest.Left > 0);
    }

    [Fact]
    public void SittingInTheHysteresisBandNeverEntersInTheFirstPlace()
    {
        MatchHarness harness = Harness();
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");

        harness.MoveTo(alice, At(0f));
        harness.MoveTo(bob, At(2100f));
        harness.Step(20);

        Assert.False(alice.View.Knows(bob.Id));
        Assert.Equal(0, harness.Match.Interest.Entered);
    }

    [Fact]
    public void TheSpawnBudgetDefersEntriesRatherThanLosingThem()
    {
        MatchHarness harness = Harness();
        MatchPlayer viewer = harness.AddPlayer("Viewer");
        for (int i = 0; i < 20; i++)
        {
            harness.AddPlayer($"Peer{i}");
        }

        // Everyone spawns on the staging point, so all 20 peers are inside the enter radius.
        harness.Step(1);
        Assert.Equal(viewer.View.SpawnBudgetPerTick, viewer.View.KnownCount);
        Assert.True(harness.Match.Interest.SpawnBudgetStops > 0);

        harness.Step(3);
        Assert.Equal(20, viewer.View.KnownCount);
    }

    [Fact]
    public void EnteringAcquiresATransientIdAndLeavingReleasesIt()
    {
        MatchHarness harness = Harness();
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.MoveTo(alice, At(0f));
        harness.MoveTo(bob, At(100f));
        harness.Step(2);

        Assert.True(alice.View.Transients.TryGet(bob.Id, out uint transientId));
        Assert.Equal(TransientIdTable.FirstAllocated, transientId);
        Assert.Equal(1, alice.View.Transients.LiveCount);

        harness.MoveTo(bob, At(5_000f));
        harness.Step(2);

        Assert.Equal(0, alice.View.Transients.LiveCount);
        Assert.False(alice.View.Transients.TryGet(bob.Id, out _));
    }

    [Fact]
    public void APlayerWhoLeavesTheMatchIsForgottenByEveryViewer()
    {
        MatchHarness harness = Harness();
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");
        harness.Step(2);
        Assert.True(alice.View.Knows(bob.Id));

        harness.Match.RemovePlayer(bob);
        harness.Step(2);

        Assert.False(alice.View.Knows(bob.Id));
        Assert.Equal(0, alice.View.Transients.LiveCount);
    }

    [Fact]
    public void GroundLootUsesItsOwnMuchTighterRadius()
    {
        MatchHarness harness = Harness();
        MatchPlayer alice = harness.AddPlayer("Alice");
        harness.MoveTo(alice, At(0f));

        WorldEntity far = harness.Match.World.SpawnGroundItem(2423, 9066, 12302, 1, At(200f));
        WorldEntity near = harness.Match.World.SpawnGroundItem(2423, 9066, 12302, 1, At(50f));
        harness.Step(2);

        Assert.True(alice.View.Knows(near.Id));
        Assert.False(alice.View.Knows(far.Id));
    }

    /// <summary>
    /// <b>D156.</b> The interest radii are the owner's own numbers, adopted under D53 from
    /// <c>C:\Z1\Server\Zone\ZoneVisibility.cs:79</c> (<c>SpawnRadius = 310f</c>) and <c>:93</c>
    /// (<c>DespawnRadius = SpawnRadius * 1.2f</c>). They are pinned here because they are a ruling,
    /// not a client fact: nothing in the August binary says 310, and a later edit that "rounds" them
    /// would silently change how far away a second player pops in.
    /// </summary>
    [Fact]
    public void PlayerVisibilityReachesTwoKilometresWithAHysteresisMargin()
    {
        Assert.Equal(2000f, ObserverView.PlayerEnterMetres);
        Assert.Equal(2200f, ObserverView.PlayerLeaveMetres, 3);
        Assert.Equal(1.1f, ObserverView.PlayerLeaveMetres / ObserverView.PlayerEnterMetres, 5);
    }

    /// <summary>
    /// The enter burst is ordered, and <c>d5</c> is first for a hard reason: its handler creates the
    /// entity and sets <c>+0x37ec &amp; 0x40</c>, and both <c>82 15</c> (which needs a
    /// <c>ProxiedCharacter</c>) and <c>d9</c> (which tests that bit) are dropped without it.
    /// </summary>
    [Fact]
    public void TheEnterBurstStartsWithTheSpawn()
    {
        Assert.StartsWith("d5 ", InterestSystem.EnterBurst[0], StringComparison.Ordinal);
        Assert.Equal(3, InterestSystem.EnterBurst.Count);
    }

}
