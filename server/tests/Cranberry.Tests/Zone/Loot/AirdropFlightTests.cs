using System.Numerics;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Lighting;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed class AirdropFlightTests
{
    private static readonly GasCircle Circle = new(new Vector3(300, 40, -800), 1_000);
    private static AirdropOptions Options => AirdropOptions.Default with
    {
        FirstDropAtMs = 0, DropIntervalMs = 1_000, MaxDropsPerMatch = 2,
        BombRunChance = 1, BombRunDelayMs = 0,
    };

    [Fact]
    public void PlanesCrossTheSafeZoneCentreAndPayloadsLeaveThePlane()
    {
        var match = new MatchAirdrops(Options, 42);
        var events = new List<AirdropEvent>();
        match.Tick(0, 150, Circle, Vector3.Zero, events);
        Assert.Equal(2, match.Flights.Count);
        foreach (AirdropFlight flight in match.Flights)
        {
            Vector3 mid = (flight.Start + flight.End) / 2;
            Assert.Equal(Circle.Centre.X, mid.X, 3);
            Assert.Equal(Circle.Centre.Z, mid.Z, 3);
            Assert.Equal(Circle.Centre, flight.Centre);
            Assert.InRange(flight.SpeedMetresPerSecond, 149.99f, 150);
            Assert.Equal(flight.Start, flight.PositionAt(flight.SpawnAtMs - 1));
            Assert.Equal(flight.End, flight.PositionAt(flight.DepartAtMs + 1));
            foreach (AirdropPayload payload in flight.Payloads)
            {
                Assert.InRange(Vector3.Distance(flight.PositionAt(payload.ReleaseAtMs),
                    payload.ReleasePosition), 0, 0.1f);
                Assert.True(payload.ReleaseAtMs > flight.SpawnAtMs);
                Assert.True(payload.ReleaseAtMs < flight.DepartAtMs);
                Assert.InRange(Circle.HorizontalDistanceTo(payload.ImpactPosition), 0, 800);
                Assert.Equal(payload.ReleasePosition, payload.PositionAt(payload.ReleaseAtMs));
                Assert.Equal(payload.ImpactPosition, payload.PositionAt(payload.ImpactAtMs));
            }
        }
        Assert.Single(match.Flights.Single(f => f.Kind == AirdropFlightKind.Supply).Payloads);
    }

    [Fact]
    public void BombsStopAtTwentyWithoutASoloBypassAndAlreadyLaunchedBombersFinish()
    {
        var match = new MatchAirdrops(Options, 42);
        var events = new List<AirdropEvent>();
        match.Tick(0, 21, Circle, Vector3.Zero, events);
        Assert.Equal(1, match.BombRuns);
        var bomber = match.Flights.Single(f => f.Kind == AirdropFlightKind.Bomber);
        Assert.All(bomber.Payloads, p => Assert.True(p.ReleaseAtMs > 1_000));
        events.Clear();
        match.Tick(1_000, 20, Circle, Vector3.Zero, events);
        match.Tick(300_000, 1, Circle, Vector3.Zero, events);
        Assert.Equal(1, match.BombRuns);
        Assert.Equal(Options.BombsPerRun, match.BombsExploded);
        Assert.Equal(2, match.Landed);
        Assert.Equal(Options.BombsPerRun, events.Count(e => e.Kind == AirdropEventKind.BombExploded));
        Assert.False(match.BombsAllowed(20));
        Assert.False(match.BombsAllowed(1));
        Assert.True(match.BombsAllowed(21));
    }

    [Fact]
    public void SuppressedBomberSlotsAreConsumedAndNeverBackfilled()
    {
        var match = new MatchAirdrops(Options, 42);
        var events = new List<AirdropEvent>();
        match.Tick(0, 20, Circle, Vector3.Zero, events);
        match.Tick(1_000, 21, Circle, Vector3.Zero, events);
        Assert.Equal(1, match.BombRuns);
        Assert.Equal(1, match.Flights.Single(f => f.Kind == AirdropFlightKind.Bomber).DropIndex);
    }

    [Fact]
    public void CatchupUsesEachScheduledCircleAndDoesNotInventEarlierSurvivorCounts()
    {
        var match = new MatchAirdrops(Options, 42);
        var events = new List<AirdropEvent>();
        var nextCircle = new GasCircle(new Vector3(-700, 90, 1800), 300);
        match.Tick(300_000, 20, nextCircle, Vector3.Zero, events,
            circleAtClock: due => due < 1_000 ? Circle : nextCircle);
        Assert.Equal(Circle.Centre, match.Flights[0].Centre);
        Assert.Equal(nextCircle.Centre, match.Flights[1].Centre);
        Assert.Equal(0, match.BombRuns);
        Assert.Equal(2, match.Landed);
        Assert.DoesNotContain(events, e => e.Kind == AirdropEventKind.BombReleased);
    }

    [Fact]
    public void LargeClockJumpsAndSmallTicksYieldTheSameOrderedEvents()
    {
        var coarse = new MatchAirdrops(Options, 81);
        var fine = new MatchAirdrops(Options, 81);
        var a = new List<AirdropEvent>();
        var b = new List<AirdropEvent>();
        coarse.Tick(300_000, 150, Circle, Vector3.Zero, a);
        for (long time = 0; time <= 300_000; time += 100)
            fine.Tick(time, 150, Circle, Vector3.Zero, b);
        Assert.Equal(a, b);
        Assert.Equal(a.OrderBy(e => e.MatchClockMs), a);
        Assert.Equal(0, coarse.Tick(300_000, 150, Circle, Vector3.Zero, a));
        Assert.Equal(0, coarse.Tick(0, 150, Circle, Vector3.Zero, a));
        Assert.Equal(Options.MaxDropsPerMatch, a.Count(e => e.Kind == AirdropEventKind.Landed));
        Assert.Equal(Options.MaxDropsPerMatch * Options.BombsPerRun,
            a.Count(e => e.Kind == AirdropEventKind.BombExploded));
    }

    [Fact]
    public void TerrainResolvedAtSchedulingIsUsedByDescentAndImpact()
    {
        var match = new MatchAirdrops(Options, 42);
        var events = new List<AirdropEvent>();
        static float Ground(float x, float z) => 1_000 + (x * .01f) + (z * .02f);
        match.Tick(0, 150, Circle, Vector3.Zero, events, Ground);
        foreach (AirdropPayload p in match.Flights.SelectMany(f => f.Payloads))
        {
            Assert.Equal(Ground(p.ImpactPosition.X, p.ImpactPosition.Z), p.ImpactPosition.Y);
            Assert.True(p.ReleasePosition.Y >= p.ImpactPosition.Y + 200);
            Assert.Equal(p.ImpactPosition, p.PositionAt(p.ImpactAtMs + 1));
        }
        match.Tick(300_000, 20, Circle, Vector3.Zero, events, Ground);
        foreach (AirdropEvent e in events.Where(e => e.Kind is AirdropEventKind.Landed or AirdropEventKind.BombExploded))
            Assert.Equal(Ground(e.Position.X, e.Position.Z), e.Position.Y);
    }

    [Fact]
    public void APlannedFlightDoesNotMoveWhenTheNextSafeZoneChanges()
    {
        var match = new MatchAirdrops(Options with { BombsEnabled = false }, 42);
        var events = new List<AirdropEvent>();
        match.Tick(0, 150, Circle, Vector3.Zero, events);
        AirdropFlight first = Assert.Single(match.Flights);
        var moved = new GasCircle(new Vector3(-500, 120, 1000), 100);
        match.Tick(1_000, 150, moved, Vector3.Zero, events);
        Assert.Same(first, match.Flights[0]);
        Assert.Equal(Circle.Centre, first.Centre);
        Assert.Equal(moved.Centre, match.Flights[1].Centre);
    }

    [Fact]
    public void PlaneClimbsOnlyAfterItsLastPayloadAndSnapshotsDoNotAllocateOnRead()
    {
        var match = new MatchAirdrops(Options, 42);
        var events = new List<AirdropEvent>();
        match.Tick(0, 150, Circle, Vector3.Zero, events);
        IReadOnlyList<AirdropFlight> snapshot = match.Flights;
        Assert.Same(snapshot, match.Flights);
        foreach (AirdropFlight flight in snapshot)
        {
            Assert.Equal(flight.Payloads.Max(p => p.ReleaseAtMs), flight.ClimbStartAtMs);
            Assert.Equal(flight.Start.Y, flight.PositionAt(flight.ClimbStartAtMs).Y);
            long midway = flight.ClimbStartAtMs + (flight.DepartAtMs - flight.ClimbStartAtMs) / 2;
            Assert.InRange(flight.PositionAt(midway).Y, flight.Start.Y + 60, flight.Start.Y + 65);
            Assert.True(flight.VelocityAt(midway).Y > 0);
            Assert.Equal(flight.Start.Y + Options.DepartureClimbMetres, flight.End.Y);
        }
        match.Tick(1_000, 150, Circle, Vector3.Zero, events);
        Assert.NotSame(snapshot, match.Flights);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(4, match.Flights.Count);
    }

    [Fact]
    public void BombFallAcceleratesAndParachuteDescentRemainsLinear()
    {
        var bomb = new AirdropPayload(0, 0, 10_000, new Vector3(0, 100, 0), new Vector3(100, 0, 0), true);
        var crate = bomb with { IsBomb = false };
        Assert.Equal(new Vector3(50, 75, 0), bomb.PositionAt(5_000));
        Assert.Equal(new Vector3(50, 50, 0), crate.PositionAt(5_000));
        Assert.False(bomb.IsActive(-1));
        Assert.True(bomb.IsActive(0));
        Assert.False(bomb.IsActive(10_000));
    }

    [Fact]
    public void BombBlastHasFiniteBoundaryAndVehicleLethality()
    {
        Assert.Equal(10_000, AirdropBombDamage.At(Options, Vector3.Zero, Vector3.Zero));
        Assert.Equal(100_000, AirdropBombDamage.At(Options, Vector3.Zero, Vector3.Zero, true));
        Assert.Equal(5_000, AirdropBombDamage.At(Options, Vector3.Zero, new Vector3(6.5f, 0, 0)));
        Assert.Equal(0, AirdropBombDamage.At(Options, Vector3.Zero, new Vector3(10, 0, 0)));
        Assert.Equal(0, AirdropBombDamage.At(Options, Vector3.Zero, new Vector3(0, 11, 0)));
        Assert.Equal(0, AirdropBombDamage.At(Options, Vector3.Zero, new Vector3(float.NaN, 0, 0)));
    }

    [Fact]
    public void TheAugustBombEffectAndModelsUseClientIdentities()
    {
        Assert.Equal(9372u, Options.BombModelId);
        Assert.Equal(9215u, Options.PlaneModelId);
        Assert.Equal("PFX_Impact_Explosion_AirdropBomb_Default_10m", AugustEffectCatalog.ById(Options.BombExplosionEffectId)!.Value.Name);
        Assert.Equal("SFX_Bomb_Falling", AugustEffectCatalog.ById(Options.BombFallingEffectId)!.Value.Name);
    }

    [Fact]
    public void InvalidTimingOrGeometryCannotCreateAnUnboundedSimulation()
    {
        foreach (AirdropOptions bad in new[]
        {
            Options with { DropIntervalMs = 0 }, Options with { PlaneSpeedMetresPerSecond = 0 },
            Options with { BombGravityMetresPerSecondSquared = float.NaN },
            Options with { BombRunChance = double.NaN }, Options with { BombsPerRun = -1 },
            Options with { FlightMarginMetres = 0 }, Options with { BombLethalRadiusMetres = 10 },
        }) Assert.Throws<InvalidDataException>(() => new MatchAirdrops(bad, 1));
    }

    [Fact]
    public void SupplyAndBombImpactsRemainInsideTheAugustMapAcrossTwoThousandSchedules()
    {
        int outside = 0;
        int total = 0;
        float largest = 0;
        for (ulong seed = 0; seed < 2_000; seed++)
        {
            GasSchedule gas = GasSchedule.Create(new GasSettings(), seed);
            var match = new MatchAirdrops(AirdropOptions.Default with { BombRunChance = 1 }, seed);
            var events = new List<AirdropEvent>();
            match.Tick(2_000_000, 150, null, gas.InitialCircle.Centre, events,
                circleAtClock: at => gas.PhaseAt(at)?.Target);
            foreach (AirdropFlight flight in match.Flights)
            foreach (AirdropPayload payload in flight.Payloads)
            {
                total++;
                Vector3 p = payload.ImpactPosition;
                largest = Math.Max(largest, Math.Max(Math.Abs(p.X), Math.Abs(p.Z)));
                if (Math.Abs(p.X) >= 4096 || Math.Abs(p.Z) >= 4096) outside++;
            }
        }
        Assert.Equal(48_000, total);
        Assert.True(outside == 0, $"{outside}/{total} payload impacts outside +/-4096; largest coordinate {largest}.");
    }
}
