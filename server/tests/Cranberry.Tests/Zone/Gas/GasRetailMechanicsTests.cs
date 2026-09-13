using System.Numerics;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;

namespace Cranberry.Tests.Zone.Gas;

public sealed class GasRetailMechanicsTests
{
    private static readonly PlayerSample Outside = new(7, new Vector3(20_000, 0, 20_000), true);

    private static GasController Started(GasSettings? settings = null)
    {
        var controller = new GasController(settings ?? new GasSettings { PreMoveRing = GasPreMoveRing.Boundary });
        controller.Start(0, 42);
        return controller;
    }

    [Fact]
    public void FullToxicityActuallyIncreasesDamageOnThe180thExposureTick()
    {
        GasController gas = Started();
        for (int second = 1; second < 180; second++)
        {
            Assert.Equal(90u, gas.Tick(second * 1000, [Outside]).Damage[0].Amount);
            Assert.Equal((uint)second * 1000, gas.ToxicityForPlayer(7));
        }

        Assert.Equal(130u, gas.Tick(180_000, [Outside]).Damage[0].Amount);
        Assert.Equal(180_000u, gas.ToxicityForPlayer(7));
        Assert.Equal(130u, gas.Tick(181_000, [Outside]).Damage[0].Amount);
    }

    [Fact]
    public void OnlyExposedPlayersAccumulateAndReturningToSafetyDrainsTheMeter()
    {
        GasController gas = Started();
        PlayerSample safe = new(2, gas.Schedule!.InitialCircle.Centre, true);
        for (int second = 1; second <= 180; second++) gas.Tick(second * 1000, [Outside, safe]);
        Assert.Equal(0u, gas.ToxicityForPlayer(2));
        Assert.Equal(180_000u, gas.ToxicityForPlayer(7));

        for (int second = 181; second <= 190; second++)
        {
            PlayerSample returned = Outside with { Position = gas.ActiveCircleAt(second * 1000).Centre };
            Assert.Empty(gas.Tick(second * 1000, [returned]).Damage.ToArray());
        }

        Assert.Equal(170_000u, gas.ToxicityForPlayer(7));
        Assert.Equal(90u, gas.Tick(191_000, [Outside]).Damage[0].Amount);
    }

    [Theory]
    [InlineData(1, 130u)]
    [InlineData(2, 160u)]
    [InlineData(3, 180u)]
    [InlineData(4, 230u)]
    [InlineData(5, 300u)]
    [InlineData(6, 400u)]
    [InlineData(7, 600u)]
    [InlineData(8, 900u)]
    [InlineData(9, 900u)]
    [InlineData(10, 900u)]
    public void TheReconstructedFullToxicityTableAppliesAtEveryPhase(int phase, uint expected)
    {
        // The short meter isolates phase selection from exposure duration. The table is secondary
        // evidence; these assertions verify the implementation, not August retail provenance.
        GasController gas = Started(new GasSettings { ToxicityMaxValue = 1000 });
        long now = gas.Schedule!.Phase(phase).ShrinkStartAtMs + 1000;
        Assert.Equal(expected, gas.Tick(now, [Outside]).Damage[0].Amount);
        Assert.Equal(expected, Rulings.Gas.RetailDamageAtFullToxicityPerPhase[phase - 1]);
    }

    [Fact]
    public void HidingTheHudDoesNotDisableToxicityOrItsDamage()
    {
        GasController gas = Started(new GasSettings
        {
            PreMoveRing = GasPreMoveRing.Boundary, SendToxicity = false,
        });
        for (int second = 1; second < 180; second++) gas.Tick(second * 1000, [Outside]);
        Assert.Equal(130u, gas.Tick(180_000, [Outside]).Damage[0].Amount);
        Assert.Equal(180_000u, gas.ToxicityForPlayer(7));
    }

    [Fact]
    public void DeadPlayersAndNewMatchesDoNotKeepOldExposure()
    {
        GasController gas = Started();
        gas.Tick(1000, [Outside]);
        Assert.Equal(1000u, gas.ToxicityForPlayer(7));
        gas.Tick(2000, [Outside with { Alive = false }]);
        Assert.Equal(0u, gas.ToxicityForPlayer(7));
        gas.Tick(3000, [Outside]);
        gas.Stop();
        Assert.Equal(0u, gas.ToxicityForPlayer(7));
        gas.Start(4000, 42);
        Assert.Equal(0u, gas.ToxicityForPlayer(7));
    }

    [Fact]
    public void AStallDoesNotInventExposureAtAnUnobservedPosition()
    {
        GasController gas = Started();
        gas.Tick(1000, [Outside]);
        gas.Tick(181_000, [Outside]);
        Assert.Equal(2000u, gas.ToxicityForPlayer(7));
    }

    [Fact]
    public void DormantGasDoesNotFillTheMeter()
    {
        GasController gas = Started(new GasSettings());
        for (long now = 1000; now < 370_000; now += 1000) gas.Tick(now, [Outside]);
        Assert.Equal(0u, gas.ToxicityForPlayer(7));
    }

    [Fact]
    public void MeterSynchronizationPreservesTheLastValueSentToTheClient()
    {
        var meter = new GasToxicity();
        meter.MarkArmed();
        Assert.True(meter.Synchronize(1000));
        Assert.Equal(0u, meter.Sent);
        Assert.True(meter.Armed);
        meter.MarkSent();
        Assert.False(meter.Synchronize(1000));
        Assert.True(meter.Synchronize(0));
        Assert.Equal(1000u, meter.Sent);
    }

    [Fact]
    public void FractionalMeterProgressIsIndependentOfTickSubdivision()
    {
        var settings = new GasSettings { ToxicityRegenPerMs = 0.001f };
        var split = new GasToxicity();
        var whole = new GasToxicity();
        for (int tick = 0; tick < 1000; tick++) split.Tick(settings, true, 100);
        whole.Tick(settings, true, 100_000);
        Assert.Equal(100u, whole.Value);
        Assert.Equal(whole.Value, split.Value);
        Assert.Equal(1000u, new GasSettings { ToxicityRegenTickMs = 250 }.ToxicityFillPerSecond);
    }

    [Fact]
    public void TheDamageScaleAlsoScalesFullToxicityDamage()
    {
        GasSettings settings = GasTuning.FromEnvironment(
            key => key == GasTuning.DamageScaleVariable ? "2" : null, out _);
        Assert.Equal(180u, settings.DamageForPhase(1));
        Assert.Equal(260u, settings.DamageForPhase(1, true));
        Assert.Equal(1800u, settings.DamageForPhase(10, true));
    }

    [Fact]
    public void TheWallIsSentAtTheFirstLethalInstantAndAtTheFinalEndpoint()
    {
        GasController gas = Started(new GasSettings());
        GasTickResult start = gas.Tick(370_000, [Outside]);
        Assert.True(start.Has(GasTickEvents.ShrinkStarted));
        Assert.True(start.Has(GasTickEvents.SafeZoneUpdate));
        Assert.True(start.Has(GasTickEvents.DamageTick));
        Assert.True(gas.Schedule!.IsClosingAt(370_000));

        long finish = gas.Schedule.FinishedAtMs;
        gas.Tick(finish - 100, []);
        GasTickResult closed = gas.Tick(finish, []);
        Assert.True(closed.Has(GasTickEvents.Finished));
        Assert.True(closed.Has(GasTickEvents.SafeZoneUpdate));
        Assert.Equal(gas.Schedule.FinalCircle, closed.Active);
        Assert.False(gas.Tick(finish + 100, []).Has(GasTickEvents.SafeZoneUpdate));
    }
}
