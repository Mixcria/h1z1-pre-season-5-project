using System.Numerics;
using Cranberry.Zone.Gas;

namespace Cranberry.Tests.Zone.Gas;

// The running-match half of the gas system: reveals, rate limiting, who takes damage, and the
// determinism the seed buys. No packet bytes here — GasController does no I/O by design.
public sealed class GasControllerTests
{
    private const ulong Seed = 0x0BADC0DE_0BADC0DEUL;

    /// <summary>Inside the initial circle (radius 4400 around (-250, 0, 100)) at Z2 ground height.</summary>
    private static readonly PlayerSample Inside = new(0, new Vector3(10f, 506f, -20f), Alive: true);

    /// <summary>Well outside every circle of a default match.</summary>
    private static readonly PlayerSample Outside = new(1, new Vector3(20_000f, 506f, 0f), Alive: true);

    private static GasController Started(GasSettings? settings = null)
    {
        var controller = new GasController(settings ?? new GasSettings());
        controller.Start(0, Seed);
        return controller;
    }

    /// <summary>
    /// A controller whose gas is lethal from the first tick — <c>GasPreMoveRing.Boundary</c>, the
    /// pre-wave-8 rule. The shipped default is <c>None</c>: nothing is drawn and nothing burns until
    /// phase 1's ring first moves at 4:30 (docs/77 §6), which is the owner's own ruling and is
    /// covered by <see cref="NothingBurnsBeforeTheGasFirstMoves"/>. Every test below that is about
    /// the DAMAGE PLUMBING rather than about that rule starts from here, so it keeps measuring what
    /// it was written to measure.
    /// </summary>
    private static GasController StartedBurning() =>
        Started(new GasSettings { PreMoveRing = GasPreMoveRing.Boundary });

    /// <summary>
    /// The owner's ruling, as an assertion: <i>"The gas is already on the map — it shouldn't be on
    /// the map until it starts moving and closing in on the circle."</i> The damage gate moves with
    /// the draw gate, so a player outside the play area during the descent is not burned by a circle
    /// their client is not being shown.
    /// </summary>
    [Fact]
    public void NothingBurnsBeforeTheGasFirstMoves()
    {
        GasController controller = Started();
        Assert.Equal(GasPreMoveRing.None, controller.Settings.PreMoveRing);

        for (long now = 1_000; now < 370_000; now += 1_000)
        {
            Assert.False(controller.Tick(now, [Outside]).Has(GasTickEvents.DamageTick));
        }

        // And from the first movement it bites, at phase 1's rate.
        GasTickResult first = controller.Tick(371_000, [Outside]);
        Assert.True(first.Has(GasTickEvents.DamageTick));
        Assert.Equal(90u, first.Damage[0].Amount);

        // Boundary is the pre-wave-8 position and still burns from the first tick.
        Assert.True(StartedBurning().Tick(1_000, [Outside]).Has(GasTickEvents.DamageTick));
    }

    /// <summary>
    /// <c>ShrinkStarted</c> is raised once per phase, on the tick the ring begins to travel — the
    /// event the HUD's "Gas is spreading!" label and the first <c>ce 01</c> of a
    /// <c>GasPreMoveRing.None</c> match both hang off (docs/77 §5, §6).
    /// </summary>
    [Fact]
    public void ShrinkStartedIsRaisedOncePerPhase()
    {
        GasController controller = Started();
        int starts = 0;
        long firstAt = 0;
        for (long now = 0; now <= controller.Schedule!.FinishedAtMs + 1_000; now += 100)
        {
            if (controller.Tick(now, []).Has(GasTickEvents.ShrinkStarted))
            {
                starts++;
                if (firstAt == 0)
                {
                    firstAt = now;
                }
            }
        }

        Assert.Equal(10, starts);
        Assert.Equal(370_000, firstAt);
    }

    [Fact]
    public void TickBeforeStartProducesNothing()
    {
        var controller = new GasController(new GasSettings());
        Assert.False(controller.Running);

        GasTickResult result = controller.Tick(5_000, [Outside]);
        Assert.Equal(GasTickEvents.None, result.Events);
        Assert.True(result.Damage.IsEmpty);
    }

    [Fact]
    public void StartOpensTheMatchAndStopClosesIt()
    {
        GasController controller = Started();
        Assert.True(controller.Running);
        Assert.NotNull(controller.Schedule);
        Assert.Equal(0, controller.StartMs);

        controller.Stop();
        Assert.False(controller.Running);
        Assert.Equal(GasTickEvents.None, controller.Tick(200_000, [Outside]).Events);
    }

    [Fact]
    public void TheFirstRevealArrivesAtTheConfiguredDelay()
    {
        GasController controller = Started();
        Assert.Equal(GasTickEvents.None, controller.Tick(119_999, []).Events);

        GasTickResult result = controller.Tick(120_000, []);
        Assert.True(result.Has(GasTickEvents.RevealSafeZone));
        Assert.True(result.Has(GasTickEvents.PhaseChanged));
        Assert.Equal(1, result.PhaseIndex);
        Assert.Equal(controller.Schedule!.Phase(1).Target, result.Revealed);
        Assert.Equal(550_000u, result.RevealClosesInMs);
    }

    [Fact]
    public void APhaseIsRevealedExactlyOnce()
    {
        GasController controller = Started();
        int reveals = 0;
        for (long now = 0; now <= 705_800; now += 250)
        {
            if (controller.Tick(now, []).Has(GasTickEvents.RevealSafeZone))
            {
                reveals++;
            }
        }

        // Phase 1 at 2:00 and phase 2 at 11:45.6 (D285 ladder shifted the split).
        Assert.Equal(2, reveals);
    }

    [Fact]
    public void RevealClosesInIsMeasuredFromTheTickThatSawIt()
    {
        // A coarse timer can notice the reveal late; the countdown handed to the client must be
        // the time still left, not the phase's whole window.
        GasController controller = Started();
        GasTickResult result = controller.Tick(125_000, []);
        Assert.True(result.Has(GasTickEvents.RevealSafeZone));
        Assert.Equal(545_000u, result.RevealClosesInMs);
    }

    [Fact]
    public void NoDamageWhileEverybodyIsInsideTheCircle()
    {
        GasController controller = Started();
        for (long now = 1_000; now <= 60_000; now += 1_000)
        {
            Assert.False(controller.Tick(now, [Inside]).Has(GasTickEvents.DamageTick));
        }
    }

    [Fact]
    public void OnlyPlayersOutsideTheActiveCircleAreDamaged()
    {
        GasController controller = StartedBurning();
        GasTickResult result = controller.Tick(1_000, [Inside, Outside]);

        Assert.True(result.Has(GasTickEvents.DamageTick));
        GasDamageTick tick = Assert.Single(result.Damage.ToArray());
        Assert.Equal(Outside.PlayerIndex, tick.PlayerIndex);
        Assert.Equal(90u, tick.Amount);
    }

    [Fact]
    public void DeadPlayersAreNeverDamaged()
    {
        GasController controller = StartedBurning();
        PlayerSample dead = Outside with { Alive = false };
        Assert.False(controller.Tick(1_000, [dead]).Has(GasTickEvents.DamageTick));
    }

    [Fact]
    public void DamageIsRateLimitedToTheTickPeriod()
    {
        GasController controller = StartedBurning();
        int ticks = 0;
        for (long now = 100; now <= 10_000; now += 100)
        {
            if (controller.Tick(now, [Outside]).Has(GasTickEvents.DamageTick))
            {
                ticks++;
            }
        }

        Assert.Equal(10, ticks);
    }

    [Fact]
    public void TheHostTimerCadenceDoesNotChangeTheMatch()
    {
        GasController fast = StartedBurning();
        GasController slow = StartedBurning();

        int fastTicks = 0;
        for (long now = 100; now <= 60_000; now += 100)
        {
            fastTicks += fast.Tick(now, [Outside]).Damage.Length;
        }

        int slowTicks = 0;
        for (long now = 250; now <= 60_000; now += 250)
        {
            slowTicks += slow.Tick(now, [Outside]).Damage.Length;
        }

        Assert.Equal(60, fastTicks);
        Assert.Equal(fastTicks, slowTicks);
    }

    [Fact]
    public void AStalledHostDoesNotReplayABacklogOfTicks()
    {
        GasController controller = StartedBurning();
        Assert.Equal(1, controller.Tick(1_000, [Outside]).Damage.Length);

        // Ten seconds of missed ticks produce one tick, not ten.
        Assert.Equal(1, controller.Tick(11_000, [Outside]).Damage.Length);
        Assert.Equal(0, controller.Tick(11_500, [Outside]).Damage.Length);
    }

    [Fact]
    public void TheDamageAmountFollowsThePhaseCurve()
    {
        GasController controller = StartedBurning();
        uint[] damage = [90, 100, 120, 150, 200, 270, 400, 600, 600, 600];
        foreach (GasPhase phase in controller.Schedule!.Phases)
        {
            Assert.Equal(damage[phase.Index - 1], DamageAt(controller, phase.RevealAtMs + 1_000));
        }
    }

    [Fact]
    public void SafeZoneUpdatesOnlyStartWhenTheRingDoes()
    {
        GasController controller = Started();
        for (long now = 0; now < 370_000; now += 500)
        {
            Assert.False(controller.Tick(now, []).Has(GasTickEvents.SafeZoneUpdate));
        }

        Assert.True(controller.Tick(370_000, []).Has(GasTickEvents.SafeZoneUpdate));
    }

    [Fact]
    public void SafeZoneUpdatesAreRateLimitedToTheConfiguredInterval()
    {
        GasController controller = Started();
        int updates = 0;
        for (long now = 0; now <= 380_000; now += 100)
        {
            if (controller.Tick(now, []).Has(GasTickEvents.SafeZoneUpdate))
            {
                updates++;
            }
        }

        // 370_000 .. 380_000 at one per 500 ms, including the exact movement boundary.
        Assert.Equal(21, updates);
    }

    [Fact]
    public void ShorteningTheUpdateIntervalProducesMoreUpdates()
    {
        var controller = new GasController(new GasSettings { SafeZoneUpdateIntervalMs = 250 });
        controller.Start(0, Seed);
        int updates = 0;
        for (long now = 0; now <= 380_000; now += 100)
        {
            if (controller.Tick(now, []).Has(GasTickEvents.SafeZoneUpdate))
            {
                updates++;
            }
        }

        // A 250 ms limiter pumped every 100 ms fires every 300 ms in practice.
        Assert.Equal(34, updates);
    }

    [Fact]
    public void TheActiveCircleShrinksBetweenTheOriginAndTheTarget()
    {
        GasController controller = Started();
        GasPhase phase = controller.Schedule!.Phase(1);

        Assert.Equal(phase.Origin.Radius, controller.Tick(370_000, []).Active.Radius);
        float middle = controller.Tick(473_600, []).Active.Radius;
        Assert.InRange(middle, phase.Target.Radius, phase.Origin.Radius);
        Assert.Equal(phase.Target.Radius, controller.Tick(670_000, []).Active.Radius);
    }

    [Fact]
    public void FinishedIsRaisedOnceAtTheEndOfTheLastPhase()
    {
        GasController controller = Started();
        int finished = 0;
        for (long now = 0; now <= controller.Schedule!.FinishedAtMs + 1_000; now += 1_000)
        {
            if (controller.Tick(now, []).Has(GasTickEvents.Finished))
            {
                finished++;
            }
        }

        Assert.Equal(1, finished);
    }

    [Fact]
    public void TheGasKeepsBitingAfterTheMatchClosed()
    {
        GasController controller = StartedBurning();
        Assert.True(controller.Tick(controller.Schedule!.FinishedAtMs + 1_000, [Outside]).Has(GasTickEvents.DamageTick));
    }

    [Fact]
    public void IsInsideSafeZoneMatchesTheActiveCircle()
    {
        GasController controller = Started();
        Assert.True(controller.IsInsideSafeZone(1_000, Inside.Position));
        Assert.False(controller.IsInsideSafeZone(1_000, Outside.Position));
        Assert.Equal(controller.Schedule!.InitialCircle, controller.ActiveCircleAt(1_000));

        // Before Start the whole world is safe: nothing may damage a player outside a match.
        var idle = new GasController(new GasSettings());
        Assert.True(idle.IsInsideSafeZone(1_000, Outside.Position));
    }

    [Fact]
    public void TheMatchClockIsRelativeToTheStartValueAndNeverNegative()
    {
        var controller = new GasController(new GasSettings());
        controller.Start(1_000_000, Seed);
        Assert.Equal(0, controller.MatchClockAt(999_000));
        Assert.Equal(5_000, controller.MatchClockAt(1_005_000));
        Assert.True(controller.Tick(1_120_000, []).Has(GasTickEvents.RevealSafeZone));
    }

    [Fact]
    public void TwoControllersOnTheSameSeedReplayTheSameMatch()
    {
        var first = new GasController(new GasSettings());
        var second = new GasController(new GasSettings());
        first.Start(0, 7);
        second.Start(9_000_000, 7);

        long firstDamage = 0;
        long secondDamage = 0;
        for (long now = 500; now <= 1_500_000; now += 500)
        {
            foreach (GasDamageTick tick in first.Tick(now, [Inside, Outside]).Damage)
            {
                firstDamage += tick.Amount;
            }

            foreach (GasDamageTick tick in second.Tick(9_000_000 + now, [Inside, Outside]).Damage)
            {
                secondDamage += tick.Amount;
            }
        }

        Assert.True(firstDamage > 0);
        Assert.Equal(firstDamage, secondDamage);
    }

    [Fact]
    public void ManyPlayersAreReportedInOnePass()
    {
        GasController controller = StartedBurning();
        var players = new PlayerSample[32];
        for (int index = 0; index < players.Length; index++)
        {
            players[index] = new PlayerSample(index, new Vector3(9_000f, 506f, index), Alive: true);
        }

        GasTickResult result = controller.Tick(1_000, players);
        Assert.Equal(32, result.Damage.Length);
        Assert.Equal(31, result.Damage[^1].PlayerIndex);
    }

    private static uint DamageAt(GasController controller, long nowMs)
    {
        GasTickResult result = controller.Tick(nowMs, [Outside]);
        return result.Damage.Length == 1 ? result.Damage[0].Amount : 0;
    }
}
