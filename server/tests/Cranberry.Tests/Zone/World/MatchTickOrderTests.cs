using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/22 §5.2: the system order is the contract. Reading Match.Tick is reading the whole game, and
// these tests are what stop the order being re-litigated one migration step at a time.
public sealed class MatchTickOrderTests
{
    [Fact]
    public void TheSystemOrderIsFrozen()
    {
        var match = new Match(1);

        Assert.Equal(
            new[]
            {
                typeof(MovementSystem),
                typeof(VehicleSystem),
                typeof(CombatSystem),
                typeof(GasSystem),
                typeof(LootSystem),
                typeof(MatchFlowSystem),
                typeof(InterestSystem),
                typeof(RelaySystem),
            },
            match.Systems.Select(system => system.GetType()).ToArray());
    }

    [Fact]
    public void EverySystemInTheOrderIsAlsoAPropertyOnTheMatch()
    {
        var match = new Match(1);
        object[] properties =
        [
            match.Movement, match.Vehicles, match.Combat, match.Gas,
            match.Loot, match.Flow, match.Interest, match.Relay,
        ];

        Assert.Equal(properties, match.Systems.Cast<object>().ToArray());
    }

    [Fact]
    public void DamageProducedInsideATickResolvesInThatSameTick()
    {
        // A producer runs before the resolver, so gas damage never waits a tick to land.
        var harness = new MatchHarness(MatchSettings.Default with
        {
            GasEnabled = true,
            MinPlayersToStart = 2,
            LobbyCountdownMs = 1_000,
            DropDurationMs = 1_000,

            // This test is about tick ORDER — a producer runs before the resolver — so it needs a
            // damage tick within its first two seconds. Wave 8 made the shipped gas lethal only
            // once phase 1's ring starts moving at 4:30 (GasPreMoveRing.None, docs/77 §6), so it
            // asks for the pre-wave-8 Boundary position rather than running a four-minute match.
            Gas = MatchSettings.Default.Gas with
            {
                PreMoveRing = GasPreMoveRing.Boundary,
                // Tick ordering must not depend on the production opening radius/POI plan.
                // The August radius reconstruction is now 8 km, which contains the old
                // test's 7.5 km "outside" point. Give this fixture an explicit boundary.
                InitialRadius = 4_000f,
                PlayAreaCentre = Vector3.Zero,
                CentrePlan = GasCentrePlan.Drift,
            },
        });

        MatchPlayer outside = harness.AddPlayer("Outside");
        MatchPlayer inside = harness.AddPlayer("Inside");
        harness.MoveTo(outside, new Vector3(7_500f, 506f, 0f));
        harness.MoveTo(inside, new Vector3(0f, 506f, 0f));

        harness.Step(120);

        Assert.True(harness.Match.Gas.Running);
        Assert.True(harness.Match.Gas.DamageTicks > 0, "The gas produced no damage tick.");
        Assert.True(outside.Health < harness.Settings.StartingHealth, "The player outside took no damage.");
        Assert.Equal(harness.Settings.StartingHealth, inside.Health);

        // The damage went through the queue, not through a system writing health directly.
        Assert.Equal(0, harness.Match.PendingDamageCount);
        Assert.Equal(
            (int)harness.Settings.Gas.DamageForPhase(1) * (int)harness.Match.Gas.DamageTicks,
            harness.Settings.StartingHealth - outside.Health);
    }

    [Fact]
    public void GasIsGatedOffByDefaultAndNeverTicks()
    {
        var harness = new MatchHarness();
        harness.AddPlayer();
        harness.Step(200);

        Assert.False(harness.Settings.GasEnabled);
        Assert.False(harness.Match.Gas.Running);
        Assert.Equal(0, harness.Match.Gas.DamageTicks);
    }

    [Fact]
    public void DamageQueuedBetweenTicksWaitsForTheNextTick()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.Step();

        harness.Match.Damage(player, 100, DamageCause.Bullet);
        Assert.Equal(harness.Settings.StartingHealth, player.Health);

        harness.Step();
        Assert.Equal(harness.Settings.StartingHealth - 100, player.Health);
    }

    [Fact]
    public void NothingWritesHealthWhenNoDamageIsQueued()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.Step(200);

        Assert.Equal(harness.Settings.StartingHealth, player.Health);
        Assert.Equal(0, harness.Match.Deaths);
    }

    [Fact]
    public void InterestRunsOnItsStrideAndNotEveryTick()
    {
        var harness = new MatchHarness(MatchSettings.Default with { InterestStride = 8 });
        MatchPlayer alice = harness.AddPlayer("Alice");
        MatchPlayer bob = harness.AddPlayer("Bob");

        harness.Step(4);
        Assert.False(alice.View.Knows(bob.Id));

        harness.Step(8);
        Assert.True(alice.View.Knows(bob.Id));
    }

    [Fact]
    public void RemovedEntitiesSurviveTheTickTheyDieInAndAreSweptAtItsEnd()
    {
        var harness = new MatchHarness();
        WorldEntity item = harness.Match.World.SpawnGroundItem(2423, 9066, 12302, 1, new Vector3(0f, 506f, 0f));

        harness.Match.World.Despawn(item);
        Assert.True(harness.Match.World.TryGetEntity(item.Id, out _));
        Assert.Equal(1, harness.Match.World.EntityCount);

        harness.Step();

        Assert.False(harness.Match.World.TryGetEntity(item.Id, out _));
        Assert.Equal(0, harness.Match.World.EntityCount);
    }

    [Fact]
    public void TheInboundBudgetBoundsOneTicksWork()
    {
        var harness = new MatchHarness(MatchSettings.Default with { InboundBudgetPerTick = 4 });
        MatchPlayer player = harness.AddPlayer();

        for (int i = 0; i < 10; i++)
        {
            harness.PostCommand(player, CommandKind.Fire);
        }

        harness.Step();
        Assert.Equal(6, harness.Match.Commands.Count);

        harness.Step();
        Assert.Equal(2, harness.Match.Commands.Count);
    }

    [Fact]
    public void ACommandForAnEmptySlotIsDiscardedNotDispatched()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.PostCommand(player, CommandKind.Fire);
        harness.Match.RemovePlayer(player);

        harness.Step();

        Assert.Equal(0, harness.Match.Combat.ShotsSeen);
        Assert.Equal(0, harness.Match.Commands.Count);
    }

    [Fact]
    public void ASteadyStateTickWithNoInputIsCheapAndStable()
    {
        var harness = new MatchHarness(MatchSettings.Default with { InterestStride = 1, RelayStride = 1 });
        for (int i = 0; i < 8; i++)
        {
            harness.AddPlayer($"Peer{i}");
        }

        harness.Step(40);
        long tickBefore = harness.Match.Clock.Tick;

        harness.Step(100);

        Assert.Equal(tickBefore + 100, harness.Match.Clock.Tick);
        Assert.Equal(0, harness.Match.TickDebt);
        Assert.Equal(0, harness.Match.Commands.Dropped);
    }
}
