using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Loot;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// docs/52 — <i>"Loot only spawned where I landed? After that no matter what building I went into
/// there was no loot."</i>
/// <para>
/// Wave 4 gave the doors a re-streaming system and left ground loot on the one-way
/// <c>RealGroundLootArmed</c> latch, so 128 objects went out at the touchdown and the other 39,708
/// items on the Z2 floor were never mentioned again. These tests pin the streamer that replaces the
/// latch, and they run against the <b>real</b> Z2 layout — 168,322 markers rolled at seed 1 — because
/// the failures worth catching (a marker respawning after a pickup, a working set that grows without
/// bound across a POI) only appear at real densities.
/// </para>
/// </summary>
public sealed class LootStreamingTests
{
    private static readonly Vector3 Landing = LootStreamHarness.Landing;

    /// <summary>Somewhere with nothing much on the floor, for the "no candidates" arms.</summary>
    private static readonly Vector3 OpenGround = new(-3800f, 40f, 3800f);

    /// <summary>The door dataset, loaded once for the arm-for-arm comparison below.</summary>
    private static readonly Lazy<Z2Doors> DoorDataset = new(Z2Doors.LoadDefault);

    // ---------------------------------------------------------------- when a burst is due

    /// <summary>The first burst is always due; that is what makes the landing work at all.</summary>
    [Fact]
    public void TheFirstBurstIsAlwaysDue()
    {
        var loot = new MatchLoot();

        Assert.False(loot.HasStreamed);
        Assert.True(loot.ShouldRestream(Landing, LootStreamOptions.Default));
    }

    /// <summary>
    /// The movement threshold, horizontally only, so a player walking down a hillside does not
    /// re-stream on every step.
    /// <para>
    /// <b>WAVE 8: 10 m, his <c>RescanDistance</c>, not 30.</b> 30 m was half the radius and was
    /// matched to the door arm so both fired together; each arm has had its own rate limit since,
    /// and 30 m of walking at 128 live objects is what let a player cross a whole POI between
    /// bursts.
    /// </para>
    /// </summary>
    [Fact]
    public void MovingTenMetresAsksForAnotherBurst()
    {
        var harness = new LootStreamHarness();
        harness.Land(Landing);
        harness.Tick(Landing);

        Assert.Equal(10f, harness.Options.RestreamFraction * harness.Options.StreamRadiusMetres, 1);
        Assert.False(harness.Loot.ShouldRestream(Landing + new Vector3(9f, 0f, 0f), harness.Options));
        Assert.False(harness.Loot.ShouldRestream(Landing + new Vector3(0f, 500f, 0f), harness.Options));
        Assert.True(harness.Loot.ShouldRestream(Landing + new Vector3(11f, 0f, 0f), harness.Options));
    }

    /// <summary>
    /// A non-positive fraction is the explicit "stream once per match" switch — wave 4's behaviour,
    /// kept reachable for an A/B. It must take the backfill arm with it: half a streamer is harder to
    /// reason about than none.
    /// </summary>
    [Fact]
    public void ANonPositiveFractionCollapsesTheStreamerBackToTheWaveFourLatch()
    {
        var options = new LootStreamOptions { RestreamFraction = 0f };
        var harness = new LootStreamHarness(options);
        harness.Tick(Landing);

        Assert.False(harness.Loot.ShouldRestream(Landing, options));
        Assert.False(harness.Loot.ShouldRestream(Landing + new Vector3(500f, 0f, 0f), options));
    }

    // ---------------------------------------------------------------- the pump decision

    /// <summary>
    /// The wave-4 defect the verify pass found in the door pump, reproduced for loot: the per-match
    /// state is created lazily inside the landing burst, which rides <c>GroundLootDelayMs</c> (2,000
    /// ms), while the pump is armed at <c>RestreamIntervalMs</c> (3,000 ms). The default ordering
    /// holds only because 3000 &gt; 2000, and the interval is host-overridable while the delay is not
    /// — so "no state yet" must be a <b>wait</b>. Folding it into <see cref="WorldStreamStep.Stop"/>
    /// would silently disable the streamer for the whole match.
    /// </summary>
    [Fact]
    public void NoMatchLootYetIsAWaitAndNeverAStop()
    {
        WorldStreamStep step = MatchLoot.NextPumpStep(
            inMatch: true,
            sendLoot: true,
            loot: null,
            centre: Landing,
            LootStreamOptions.Default);

        Assert.Equal(WorldStreamStep.Wait, step);
    }

    /// <summary>No pose yet — no movement packet has arrived — is a wait too.</summary>
    [Fact]
    public void NoPlayerPoseYetIsAWait()
    {
        WorldStreamStep step = MatchLoot.NextPumpStep(
            inMatch: true,
            sendLoot: true,
            loot: new MatchLoot(),
            centre: null,
            LootStreamOptions.Default);

        Assert.Equal(WorldStreamStep.Wait, step);
    }

    /// <summary>Only three things may end the chain, and all three are permanent for the match.</summary>
    [Theory]
    [InlineData(false, true, 3000, true)]   // the match is over
    [InlineData(true, false, 3000, true)]   // loot streaming is switched off
    [InlineData(true, true, 0, true)]       // the interval is not positive
    [InlineData(true, true, 3000, false)]   // …and otherwise it does not stop
    public void OnlyMatchOverFeatureOffOrANonPositiveIntervalEndsTheChain(
        bool inMatch,
        bool sendLoot,
        int intervalMs,
        bool expectStop)
    {
        var loot = new MatchLoot();
        var options = new LootStreamOptions { RestreamIntervalMs = intervalMs };

        WorldStreamStep step = MatchLoot.NextPumpStep(inMatch, sendLoot, loot, Landing, options);

        Assert.Equal(expectStop, step == WorldStreamStep.Stop);
    }

    /// <summary>
    /// docs/52 §2c puts both arms on one tick, so the two decision functions must not drift apart.
    /// <see cref="MatchDoors.NextPumpStep"/> is left exactly as wave 4 shipped it (it belongs to
    /// another area's files); this pins it against <see cref="WorldStream.NextStep"/> arm for arm, so
    /// a change to either that is not made to the other fails here instead of on the wire.
    /// </summary>
    [Theory]
    [InlineData(false, true, 3000, true, true)]
    [InlineData(true, false, 3000, true, true)]
    [InlineData(true, true, 0, true, true)]
    [InlineData(true, true, 3000, false, true)]
    [InlineData(true, true, 3000, true, false)]
    [InlineData(true, true, 3000, true, true)]
    public void TheSharedDecisionFunctionAgreesWithTheDoorPumpArmForArm(
        bool inMatch,
        bool enabled,
        int intervalMs,
        bool hasState,
        bool hasPose)
    {
        MatchDoors? doors = hasState ? new MatchDoors(DoorDataset.Value) : null;
        Vector3? centre = hasPose ? Landing : null;

        DoorPumpStep door = MatchDoors.NextPumpStep(inMatch, enabled, intervalMs, doors, centre, 60f);
        WorldStreamStep shared = WorldStream.NextStep(
            inMatch,
            enabled,
            intervalMs,
            hasState,
            centre,
            60f,
            (point, radius) => doors!.ShouldRestream(point, radius));

        Assert.Equal(door.ToString(), shared.ToString());
    }

    // ---------------------------------------------------------------- the backfill arm

    /// <summary>
    /// docs/52 §3d — the backfill arm. A player who has not moved still gets the rest of the
    /// building, a budget at a time, because "already spawned" is tracked per marker rather than per
    /// burst. Without it nothing at all happens until the movement threshold, which is most of why
    /// the owner's landing building read as half empty: the landing burst's cap truncates inside its
    /// own disc in every measured session.
    /// <para>
    /// <b>WAVE 8:</b> deliberately run at an 8-object budget rather than the shipped one. At the
    /// owner's density the whole 60 m disc around this touchdown now fits inside a single 49-object
    /// tick — which is the point of the wave and is asserted separately below — so the shipped
    /// numbers no longer exercise the mechanism this test exists to pin.
    /// </para>
    /// </summary>
    [Fact]
    public void AStationaryPlayerFillsInTheDiscABudgetAtATime()
    {
        var harness = new LootStreamHarness(
            new LootStreamOptions { MaxPerRestream = 8, RestreamByteBudget = 0 });

        LootStreamPlan first = harness.Tick(Landing);
        Assert.InRange(first.Spawns.Count, 1, harness.Options.MaxPerRestream);
        Assert.False(first.DiscExhausted);
        Assert.True(harness.Loot.ShouldRestream(Landing, harness.Options));

        int ticks = 1;
        while (harness.Loot.ShouldRestream(Landing, harness.Options) && ticks < 32)
        {
            LootStreamPlan plan = harness.Tick(Landing);
            Assert.True(plan.Spawns.Count <= harness.Options.MaxPerRestream);
            Assert.Empty(plan.Evictions);
            ticks++;
        }

        // It stopped because it ran out of room or ran out of markers, not because it gave up.
        Assert.True(ticks < 32);
        Assert.True(
            harness.Loot.LiveCount >= harness.Options.MaxLive || harness.Loot.DiscExhausted,
            $"stopped at {harness.Loot.LiveCount} live with the disc not exhausted");
        Assert.True(harness.Loot.LiveCount > first.Spawns.Count);
    }

    /// <summary>
    /// <b>The wave-8 acceptance, and the assertion that encodes the owner's complaint.</b> The
    /// backfill arm covers the whole 60 m disc around the touchdown and then switches itself off —
    /// the disc reports itself exhausted and the objects it holds stay inside the ceiling. Before
    /// wave 8 the same disc took 128 objects nearest-first, ran out inside 24-31 m, and left the far
    /// half of the buildings with nothing (docs/78 §3.2).
    /// <para>
    /// <b>D270 made this take more than one tick, and that is arithmetic rather than a regression.</b>
    /// A tick is bounded by <see cref="LootStreamOptions.RestreamByteBudget"/> (22,000 B) and
    /// <see cref="LootStreamOptions.MaxPerRestream"/> (64) — about 48 objects — while at the ruled
    /// density the 60 m disc holds several hundred. What matters is that the arm FINISHES and then
    /// costs nothing, which is what the loop below asserts, and it still fails loudly at
    /// <c>MaxLive = 128</c>: the working set fills, the disc is never exhausted, and the arm stays
    /// armed forever.
    /// </para>
    /// </summary>
    [Fact]
    public void TheBackfillArmCoversTheWholeDiscAndThenStops()
    {
        var harness = new LootStreamHarness();

        LootStreamPlan plan = harness.Tick(Landing);
        Assert.True(plan.Spawns.Count > 0, "the touchdown disc streamed nothing at all");

        int ticks = 1;
        while (ticks < 64 && harness.Loot.ShouldRestream(Landing, harness.Options))
        {
            plan = harness.Tick(Landing);
            ticks++;
        }

        Assert.True(
            plan.DiscExhausted,
            $"the backfill arm left the 60 m disc unfinished after {ticks} tick(s) at "
            + $"{harness.Loot.LiveCount} live objects (cap {harness.Options.MaxLive}) — this is the "
            + "pre-wave-8 defect");
        Assert.False(harness.Loot.ShouldRestream(Landing, harness.Options));
        Assert.True(
            harness.Loot.LiveCount < harness.Options.MaxLive,
            $"the working set reached {harness.Loot.LiveCount} against a {harness.Options.MaxLive} "
            + "ceiling, so the cap is binding again");

        // …and the panel that used to make that ceiling necessary is now bounded by rows, not by
        // the working set: 32 rows is 2,375 B against the 9,552 B measured live at 129 rows.
        Assert.Equal(32, plan.PanelRowCap);
    }

    /// <summary>
    /// The other half of the backfill arm: once the disc really is covered, a stationary player must
    /// cost one comparison per tick and no spatial query at all.
    /// </summary>
    [Fact]
    public void AnExhaustedDiscSwitchesTheBackfillArmOff()
    {
        var harness = new LootStreamHarness();

        LootStreamPlan plan = harness.Tick(OpenGround);

        Assert.Empty(plan.Spawns);
        Assert.True(plan.DiscExhausted);
        Assert.False(harness.Loot.ShouldRestream(OpenGround, harness.Options));
    }

    // ---------------------------------------------------------------- the caps

    /// <summary>
    /// The brief's first acceptance: the working set stays under its cap as the player crosses the
    /// map. 2 km of travel through the Harris Bluffs POIs, 25 m at a time, ticking whenever a burst
    /// is due.
    /// </summary>
    [Fact]
    public void TheWorkingSetStaysUnderItsCapAcrossTwoKilometresOfTravel()
    {
        var harness = new LootStreamHarness();
        int bursts = 0;
        int peak = 0;

        for (int step = 0; step <= 80; step++)
        {
            var centre = new Vector3(Landing.X, Landing.Y, Landing.Z + (step * 25f));
            if (harness.TickIfDue(centre) is null)
            {
                continue;
            }

            bursts++;
            peak = Math.Max(peak, harness.Loot.LiveCount);
            Assert.True(
                harness.Loot.LiveCount <= harness.Options.MaxLive,
                $"working set reached {harness.Loot.LiveCount} at step {step}");
            Assert.Equal(harness.Loot.OnWireCount, harness.World.Count);
            Assert.Equal(harness.World.Count, harness.FullNpcSent.Count);
        }

        Assert.True(bursts >= 5, $"only {bursts} bursts over 2 km");
        Assert.True(peak > 0);
        Assert.True(harness.Loot.EvictedCount > 0, "nothing was ever streamed back out");

        // The streamer really churned rather than filling once and stopping. Against `peak` and not
        // against MaxLive: since wave 8 the ceiling is a guard rail set ABOVE the working set, so a
        // whole 2 km crossing at the owner's density legitimately never reaches it.
        Assert.True(
            harness.Loot.StreamedCount > peak,
            $"the streamer moved {harness.Loot.StreamedCount} objects against a peak working set of "
            + $"{peak} — it never went past one working set");
    }

    /// <summary>One tick may never plan more than <see cref="LootStreamOptions.MaxPerRestream"/>.</summary>
    [Fact]
    public void OneTickNeverPlansMoreThanItsPerTickCap()
    {
        var harness = new LootStreamHarness(new LootStreamOptions { MaxPerRestream = 7 });

        for (int step = 0; step < 40; step++)
        {
            var centre = new Vector3(Landing.X + (step * 31f), Landing.Y, Landing.Z);
            LootStreamPlan plan = harness.Tick(centre);
            Assert.True(plan.Spawns.Count <= 7, $"tick {step} planned {plan.Spawns.Count}");
        }
    }

    /// <summary>
    /// A gun and its two ammunition boxes are one indivisible set, so the cap must never be
    /// overshot by taking a gun it cannot afford the pair for (docs/39 §5 — a gun without its pair is
    /// an inert prop, and two extra objects would put the working set over <c>MaxLive</c>).
    /// </summary>
    [Fact]
    public void AGunIsNeverStreamedWithoutRoomForItsAmmunition()
    {
        var harness = new LootStreamHarness(new LootStreamOptions { MaxLive = 40, MaxPerRestream = 40 });

        for (int step = 0; step < 12; step++)
        {
            var centre = new Vector3(Landing.X, Landing.Y, Landing.Z + (step * 40f));
            LootStreamPlan plan = harness.Tick(centre);
            Assert.True(plan.LiveAfter <= 40, $"tick {step} left {plan.LiveAfter} live");

            // Every box in the plan is immediately preceded by a gun, never orphaned at the end.
            for (int i = 0; i < plan.Spawns.Count; i++)
            {
                if (plan.Spawns[i].IsAmmunitionBox)
                {
                    Assert.True(i > 0);
                }
            }
        }
    }

    // ---------------------------------------------------------------- identity

    /// <summary>
    /// The single most load-bearing line of the integration. The landing burst does not go through
    /// <see cref="MatchLoot.PlanRestream"/>, so unless <c>ZoneService</c> hands each of its objects to
    /// <see cref="MatchLoot.NoteSpawned"/> the first re-stream tick — whose 60 m disc lies inside the
    /// landing's 80 m one — offers every one of them a second time. This test pins both directions,
    /// so the failure mode is documented rather than merely avoided.
    /// </summary>
    [Fact]
    public void AdoptingTheLandingBurstIsWhatStopsTheFirstTickDuplicatingIt()
    {
        var adopted = new LootStreamHarness();
        int landed = adopted.Land(Landing);
        Assert.True(landed > 64, $"the landing burst only placed {landed} objects");

        LootStreamPlan first = adopted.Tick(Landing);
        Assert.Empty(first.Spawns);

        var forgotten = new LootStreamHarness();
        forgotten.Land(Landing, adopt: false);
        LootStreamPlan duplicated = forgotten.Tick(Landing);
        Assert.NotEmpty(duplicated.Spawns);
    }

    /// <summary>No marker is ever offered twice while it is still on the client.</summary>
    [Fact]
    public void NoMarkerIsSpawnedTwiceWhileItIsStillOnTheClient()
    {
        var harness = new LootStreamHarness();
        var live = new HashSet<LootStreamKey>();

        for (int step = 0; step <= 60; step++)
        {
            var centre = new Vector3(Landing.X + (step * 20f), Landing.Y, Landing.Z);
            if (harness.TickIfDue(centre) is not LootStreamPlan plan)
            {
                continue;
            }

            foreach (ulong guid in plan.Evictions)
            {
                // The plan removed the entry before returning, so the key is no longer resolvable;
                // the live set here is the test's own independent shadow of what the client holds.
                Assert.False(harness.Loot.TryGetKey(guid, out _));
            }

            live.RemoveWhere(key => !harness.Loot.IsStreamed(key));

            foreach (LootStreamSpawn spawn in plan.Spawns)
            {
                Assert.True(live.Add(spawn.Key), $"{spawn.Key} was spawned twice while still live");
            }

            Assert.Equal(harness.Loot.LiveCount, live.Count);
        }
    }

    /// <summary>
    /// The rule that separates loot from doors: an item that has been picked up must never come back.
    /// Loot the marker, walk 200 m away so it is evicted, walk back — and it stays gone.
    /// </summary>
    [Fact]
    public void AnItemYouPickedUpNeverComesBack()
    {
        var harness = new LootStreamHarness();
        LootStreamPlan first = harness.Tick(Landing);
        Assert.NotEmpty(first.Spawns);

        LootStreamSpawn target = first.Spawns[0];
        Assert.True(harness.Loot.TryGetGuid(target.Key, out ulong guid));
        harness.PickUp(guid);
        Assert.True(harness.Loot.IsTaken(target.Key));
        Assert.False(harness.Loot.IsStreamed(target.Key));

        var away = new Vector3(Landing.X + 400f, Landing.Y, Landing.Z);
        harness.Tick(away);
        harness.Tick(Landing);
        harness.Tick(Landing);

        Assert.False(harness.Loot.IsStreamed(target.Key));
        Assert.True(harness.Loot.IsTaken(target.Key));
    }

    /// <summary>
    /// An item you only walked away from does come back — with a <b>new</b> world guid and a new
    /// transient id, because its old world object was destroyed client-side and docs/52 §6c forbids
    /// re-using either.
    /// </summary>
    [Fact]
    public void AnItemYouOnlyWalkedAwayFromComesBackWithNewIds()
    {
        var harness = new LootStreamHarness();
        LootStreamPlan first = harness.Tick(Landing);
        LootStreamSpawn target = first.Spawns[0];
        Assert.True(harness.Loot.TryGetGuid(target.Key, out ulong original));

        var away = new Vector3(Landing.X + 400f, Landing.Y, Landing.Z);
        LootStreamPlan leaving = harness.Tick(away);
        Assert.Contains(original, leaving.Evictions);
        Assert.False(harness.Loot.IsStreamed(target.Key));
        Assert.False(harness.World.TryGet(original, out _));

        // …but it is still a loot guid, which is what stops a loot press from swinging a door.
        Assert.True(harness.World.IsLootGuid(original));
        Assert.True(harness.World.WasEvicted(original));

        harness.Tick(Landing);
        Assert.True(harness.Loot.IsStreamed(target.Key));
        Assert.True(harness.Loot.TryGetGuid(target.Key, out ulong reissued));
        Assert.NotEqual(original, reissued);
        Assert.True(reissued > original);
    }

    /// <summary>
    /// docs/52 §3c — the box key space and the marker key space are not comparable. A box whose gun's
    /// <c>InstanceId</c> is 0 has <see cref="LootClusterItem.Key"/> 0 or 1, which in a single shared
    /// <c>ulong</c> space would be marker index 0 or 1: picking that box up would permanently
    /// suppress an unrelated marker on the other side of the map.
    /// </summary>
    [Fact]
    public void TheBoxKeySpaceCannotCollideWithTheMarkerKeySpace()
    {
        Assert.NotEqual(LootStreamKey.ForMarker(1), LootStreamKey.ForBox(1));
        Assert.NotEqual(LootStreamKey.ForMarker(0), LootStreamKey.ForBox(0));

        var loot = new MatchLoot();
        loot.NoteTaken(LootStreamKey.ForBox(1));

        Assert.True(loot.IsTaken(LootStreamKey.ForBox(1)));
        Assert.False(loot.IsTaken(LootStreamKey.ForMarker(1)));
    }

    /// <summary>
    /// docs/52 §8 open question 3, closed here rather than left open: no Z2 marker carries instance
    /// id 0, so <see cref="LootClusterItem.Key"/> never actually takes the values 0 and 1. The two-set
    /// design above makes it harmless either way, which is why this is an observation and not a
    /// guard.
    /// </summary>
    [Fact]
    public void NoZ2MarkerCarriesInstanceIdZero()
    {
        Z2LootSpawns spawns = LootRoster.Spawns;
        int zeroes = 0;

        for (int i = 0; i < spawns.Count; i++)
        {
            if (spawns[i].InstanceId == 0)
            {
                zeroes++;
            }
        }

        Assert.Equal(0, zeroes);
    }

    // ---------------------------------------------------------------- eviction

    /// <summary>Walking away destroys what you left behind — the streamer's whole eviction half.</summary>
    [Fact]
    public void WalkingAwayDestroysWhatYouLeftBehind()
    {
        var harness = new LootStreamHarness();
        harness.Tick(Landing);
        int before = harness.Loot.LiveCount;
        Assert.True(before > 0);

        LootStreamPlan leaving = harness.Tick(new Vector3(Landing.X + 500f, Landing.Y, Landing.Z));

        Assert.Equal(before, leaving.Evictions.Count);
        foreach (ulong guid in leaving.Evictions)
        {
            Assert.False(harness.World.TryGet(guid, out _));
            Assert.DoesNotContain(guid, harness.FullNpcSent);
        }
    }

    /// <summary>
    /// docs/52 §3e — the hysteresis is not a nicety. With the despawn radius equal to the stream
    /// radius, a player on the boundary would spawn and destroy the same item every tick forever at
    /// 490 B a go. 90 ≥ 60 + 0.5 × 60 is the arithmetic that makes it impossible for the very
    /// movement that triggers a burst to evict what that burst just sent.
    /// </summary>
    [Fact]
    public void TheDespawnRadiusLeavesAFullRestreamThresholdOfDeadBand()
    {
        LootStreamOptions options = LootStreamOptions.Default;

        Assert.True(
            options.EffectiveDespawnRadiusMetres
                >= options.StreamRadiusMetres * (1f + options.RestreamFraction),
            "the despawn radius is inside the distance one re-stream threshold can carry an item");
    }

    /// <summary>The behavioural half: a burst triggered by exactly the threshold evicts nothing.</summary>
    [Fact]
    public void AMovementThatOnlyJustTriggersABurstEvictsNothing()
    {
        var harness = new LootStreamHarness();
        harness.Tick(Landing);
        Assert.True(harness.Loot.LiveCount > 0);

        float threshold = harness.Options.StreamRadiusMetres * harness.Options.RestreamFraction;
        var nudged = new Vector3(Landing.X + threshold + 0.01f, Landing.Y, Landing.Z);

        Assert.True(harness.Loot.ShouldRestream(nudged, harness.Options));
        Assert.Empty(harness.Loot.PlanRestream(nudged, harness.Layout, harness.Options).Evictions);
    }

    /// <summary>
    /// An item that oscillates would show up as an endless spawn/evict pair. Walk back and forth
    /// across the re-stream threshold twenty times and count how much churn it produces.
    /// </summary>
    [Fact]
    public void StandingOnTheBoundaryDoesNotOscillate()
    {
        var harness = new LootStreamHarness();
        harness.Tick(Landing);
        long evictedAfterFirst = harness.Loot.EvictedCount;

        for (int i = 0; i < 20; i++)
        {
            float offset = (i % 2 == 0) ? 31f : 0f;
            harness.TickIfDue(new Vector3(Landing.X + offset, Landing.Y, Landing.Z));
        }

        Assert.Equal(evictedAfterFirst, harness.Loot.EvictedCount);
    }

    // ---------------------------------------------------------------- id headroom

    /// <summary>
    /// docs/52 §4f — the transient-id ceiling. Ground loot mints from 1,000 and doors from
    /// 1,000,000, and a collision would be severe rather than untidy: the <c>0xda</c> applier matches
    /// its record to an object by transient id, so a shared id would apply a loot record to a door.
    /// Exhaustion has to degrade into "no more loot streams", never into a throw on the listener
    /// thread.
    /// </summary>
    [Fact]
    public void TheTransientIdHeadroomBoundsWhatATickMaySpawn()
    {
        var world = new LootWorld(transientIdCeiling: LootWorld.DefaultTransientIdBase + 3);
        Assert.Equal(3, world.TransientIdHeadroom);

        var loot = new MatchLoot();
        LootStreamPlan plan = loot.PlanRestream(
            Landing,
            LootRoster.Layout,
            LootStreamOptions.Default,
            spawnBudget: world.TransientIdHeadroom);

        Assert.True(plan.Spawns.Count <= 3);

        for (int i = 0; i < 3; i++)
        {
            world.Spawn(1, 1, Landing);
        }

        Assert.Equal(0, world.TransientIdHeadroom);
        Assert.Empty(loot.PlanRestream(Landing, LootRoster.Layout, LootStreamOptions.Default, spawnBudget: 0).Spawns);
    }

    /// <summary>
    /// docs/52 §5d — an eviction must not rewind the guid allocator. <see cref="LootWorld.IsLootGuid"/>
    /// is a range test over every guid this world ever minted and it is what stops a loot press from
    /// swinging a door; an evicted guid has to stay a loot guid for ever.
    /// </summary>
    [Fact]
    public void EvictionDoesNotRewindTheGuidAllocator()
    {
        var world = new LootWorld();
        GroundLootItem first = world.Spawn(1, 1, Landing);
        GroundLootItem second = world.Spawn(1, 1, Landing);

        Assert.True(world.TryEvict(second.WorldGuid, out _));
        Assert.False(world.TryEvict(second.WorldGuid, out _));
        Assert.True(world.IsLootGuid(second.WorldGuid));
        Assert.True(world.WasEvicted(second.WorldGuid));
        Assert.False(world.WasEvicted(first.WorldGuid));

        GroundLootItem third = world.Spawn(1, 1, Landing);
        Assert.NotEqual(second.WorldGuid, third.WorldGuid);
        Assert.NotEqual(second.TransientId, third.TransientId);
    }

    /// <summary>
    /// A pickup and an eviction leave the registry in the same state but must not be reported the
    /// same way: before streaming, an unresolvable loot guid could only be the second packet of one
    /// <c>[F]</c> press, and the log said so. After streaming it can equally be an object the player
    /// walked away from (docs/52 §5d).
    /// </summary>
    [Fact]
    public void AClaimAndAnEvictionAreDistinguishable()
    {
        var world = new LootWorld();
        GroundLootItem claimed = world.Spawn(1, 1, Landing);
        GroundLootItem streamedOut = world.Spawn(1, 1, Landing);

        Assert.True(world.TryClaim(claimed.WorldGuid, out _));
        Assert.True(world.TryEvict(streamedOut.WorldGuid, out _));

        Assert.True(world.IsLootGuid(claimed.WorldGuid));
        Assert.True(world.IsLootGuid(streamedOut.WorldGuid));
        Assert.False(world.WasEvicted(claimed.WorldGuid));
        Assert.True(world.WasEvicted(streamedOut.WorldGuid));
    }

    /// <summary>
    /// <see cref="LootWorld.ItemsWithin"/> — the narrowing that becomes available if
    /// <c>MaxLive</c> is ever raised past the size <c>ProximateItems</c> can carry comfortably.
    /// </summary>
    [Fact]
    public void ItemsWithinMeasuresInTheHorizontalPlane()
    {
        var world = new LootWorld();
        GroundLootItem near = world.Spawn(1, 1, Landing);
        GroundLootItem high = world.Spawn(1, 1, Landing + new Vector3(0f, 300f, 0f));
        world.Spawn(1, 1, Landing + new Vector3(200f, 0f, 0f));

        ulong[] within = world.ItemsWithin(Landing, 50f).Select(item => item.WorldGuid).Order().ToArray();

        Assert.Equal([near.WorldGuid, high.WorldGuid], within);
        Assert.Empty(world.ItemsWithin(Landing, 0f));
    }

    /// <summary>
    /// <b>Wave-6 verify fix: a dropped item must be evictable.</b>
    /// <para>
    /// <c>ZoneService</c>'s <c>RequestUseItem</c> drop path spawns an ordinary ground object at the
    /// player's feet, but it did not adopt it into the streaming working set. <c>MatchLoot</c>'s
    /// evictions come <b>only</b> from that key-indexed set, so a dropped object lived in
    /// <c>LootWorld</c> - which has no cap - and in nothing else: never counted against
    /// <see cref="LootStreamOptions.MaxLive"/>, never destroyed client-side, and adding 74 B to
    /// every later <c>ProximateItems</c> republish for the rest of the match. Twenty-five routine
    /// drops turned a 9,479 B panel rebuild into 11,329 B, permanently.
    /// </para>
    /// <para>
    /// <see cref="LootStreamKeyKind.Dropped"/> is keyed on the world guid <c>LootWorld</c> just
    /// minted - monotonic and never reused - so it is a key space of its own, it can never collide
    /// with a marker index or a <c>LootClusterItem.Key</c> whose high half is 0, and nothing ever
    /// re-offers it as a spawn candidate.
    /// </para>
    /// </summary>
    [Fact]
    public void ADroppedItemIsAdoptedSoTheStreamerCanEvictIt()
    {
        var harness = new LootStreamHarness();
        harness.Tick(Landing);
        int afterLanding = harness.Loot.LiveCount;

        // The drop, exactly as ZoneService performs it: spawn, then adopt under a Dropped key.
        GroundLootItem dropped = harness.World.Spawn(10, 23, Landing);
        LootStreamKey key = LootStreamKey.ForDropped(dropped.WorldGuid);
        Assert.True(harness.Loot.NoteSpawned(key, dropped.WorldGuid, dropped.Position));

        // It counts against the working set, and it is findable by guid.
        Assert.Equal(afterLanding + 1, harness.Loot.LiveCount);
        Assert.True(harness.Loot.TryGetKey(dropped.WorldGuid, out LootStreamKey found));
        Assert.Equal(key, found);
        Assert.False(harness.Loot.IsTaken(key));                 // a drop retires no marker

        // Walk far past the despawn radius: the ordinary distance rule reaps it like anything else.
        var faraway = Landing + new Vector3(5_000f, 0f, 5_000f);
        LootStreamPlan plan = harness.Loot.PlanRestream(faraway, harness.Layout, harness.Options);
        Assert.Contains(dropped.WorldGuid, plan.Evictions);
        Assert.False(harness.Loot.TryGetKey(dropped.WorldGuid, out _));

        // And nothing ever offers it back: a Dropped key has no marker and no cluster behind it.
        Assert.DoesNotContain(plan.Spawns, spawn => spawn.Key.Kind == LootStreamKeyKind.Dropped);
    }

    /// <summary>
    /// Picking a dropped item back up must retire nothing. Its key carries a world guid in the same
    /// field a box key uses, so routing it into <c>_takenBoxes</c> would blacklist whichever
    /// <c>LootClusterItem.Key</c> happened to share the value for the rest of the match.
    /// </summary>
    [Fact]
    public void PickingUpADroppedItemPoisonsNoOtherKeySpace()
    {
        var harness = new LootStreamHarness();
        GroundLootItem dropped = harness.World.Spawn(10, 23, Landing);
        LootStreamKey key = LootStreamKey.ForDropped(dropped.WorldGuid);
        harness.Loot.NoteSpawned(key, dropped.WorldGuid, dropped.Position);

        Assert.True(harness.Loot.NoteTaken(dropped.WorldGuid));

        Assert.Equal(0, harness.Loot.TakenBoxCount);
        Assert.Equal(0, harness.Loot.TakenMarkerCount);
        Assert.False(harness.Loot.IsTaken(LootStreamKey.ForBox(dropped.WorldGuid)));
    }
}
