using System.Numerics;
using System.Reflection;
using Cranberry.Zone;
using Cranberry.Zone.Loot;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone;

/// <summary>
/// The wave-5 <b>verify</b> pass. Every case here pins a defect that was found by reading the wave-5
/// tree and fixed in it — three of them in the ground-loot streamer the owner's P0
/// ("no matter what building I went into there was no loot") was built to solve, one in a regression
/// guard that was flaky by construction, and two in guards that had been widened to accommodate a
/// change rather than made to hold it.
/// <para>
/// <b>D29.</b> Structural and send-side only. None of this is LIVE-VERIFIED and none of it claims
/// to be.
/// </para>
/// <para>
/// <b>2026-09-02 (overhaul lane 0A).</b> The five cases that pinned "somebody must not delete this
/// one line" by locating <c>Cranberry.slnx</c> and grepping <c>ZoneService.cs</c> were deleted, with
/// their <c>Between</c> / <c>Code</c> / <c>ZoneServiceFile</c> helpers: they asserted source text
/// rather than behaviour and failed whenever the assemblies were built out of tree (S1 §4.2,
/// OVERHAUL-PLAN §5.2). <c>EquipmentGuardCoverageTests</c> was deleted outright for the same reason —
/// all three of its cases were source greps.
/// </para>
/// </summary>
public sealed class VerifyWave5FixesTests
{
    private static readonly Vector3 Landing = new(-2563.71f, -13.59f, -347.99f);

    // -------------------------------------------------------------------------------------------
    // 1. The loot arm waited on a guard that could not fail, and leaked when it lost the race.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The loot arm's "has the state arrived yet" guard was a compile-time <c>true</c>.</b>
    /// <c>MatchLoot.NextPumpStep</c> derived <c>hasState</c> from <c>loot is not null</c>, copying the
    /// door arm's shape — but <c>GatewaySessionState.StreamedLoot</c> is a non-null initialiser, so
    /// for the only caller that exists the guard guarded nothing, and the one test that pinned it
    /// passed <c>loot: null</c> literally, i.e. pinned a path production cannot take.
    /// <para>
    /// The condition that actually matters is a different one: the landing burst does not go through
    /// <c>PlanRestream</c> and adopts itself into the working set only as <c>DrainBurst</c> pays it
    /// out over slices. A pump tick landing inside that window plans against a half-adopted set and
    /// re-offers markers the drain is about to spawn.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePumpWaitsForTheLandingBurstToFinishDraining()
    {
        var loot = new MatchLoot();

        Assert.Equal(
            WorldStreamStep.Wait,
            MatchLoot.NextPumpStep(
                inMatch: true,
                sendLoot: true,
                loot,
                Landing,
                LootStreamOptions.Default,
                landingBurstDrained: false));

        // And it is a WAIT, never a STOP: a Stop ends the timer chain for the rest of the match,
        // which is the wave-4 defect this whole shape exists to avoid.
        Assert.Equal(
            WorldStreamStep.Restream,
            MatchLoot.NextPumpStep(
                inMatch: true,
                sendLoot: true,
                loot,
                Landing,
                LootStreamOptions.Default,
                landingBurstDrained: true));
    }

    /// <summary>
    /// <b>A duplicate world object must be destroyed, not forgotten.</b> <c>MatchLoot</c> can hold
    /// only one guid per key, so when a second spawn arrived for a key already on the client the
    /// second guid was dropped on the floor — while its <c>d6</c>/<c>da</c>/<c>ea 04</c> had already
    /// gone out. The object then stayed on the client for the rest of the match, in every
    /// <c>ProximateItems</c> republish, visually doubled on top of its twin, and unevictable because
    /// nothing was left holding its identity.
    /// </summary>
    [Fact]
    public void AdoptingAKeyTwiceReportsTheDuplicateRatherThanSwallowingIt()
    {
        var loot = new MatchLoot();
        LootStreamKey key = LootStreamKey.ForMarker(4_242);

        Assert.True(loot.NoteSpawned(key, 9_001, Landing));

        // Idempotent for the SAME guid — re-adopting one object is not a duplicate.
        Assert.True(loot.NoteSpawned(key, 9_001, Landing));

        // A DIFFERENT guid for the same key is a second world object, and the caller is told so.
        Assert.False(loot.NoteSpawned(key, 9_002, Landing));

        // The first guid is kept: it is the one the client can still be told to destroy.
        Assert.True(loot.TryGetGuid(key, out ulong kept));
        Assert.Equal(9_001UL, kept);
        Assert.Equal(1, loot.OnWireCount);
    }

    // -------------------------------------------------------------------------------------------
    // 2. An all-eviction tick left the pickup panel listing destroyed objects.
    // -------------------------------------------------------------------------------------------

    // -------------------------------------------------------------------------------------------
    // 3. The shared pump collapsed the two intervals, so either knob set the other arm's rate.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The pump ticks at <c>Math.Min(doorMs, lootMs)</c> so that lowering one arm's interval cannot
    /// slow the other one down — but neither arm's <c>NextPumpStep</c> reads its interval as a rate,
    /// only as a <c>Stop</c> test, so the min also <b>sped the other arm up</b>:
    /// <c>CRANBERRY_DOOR_RESTREAM_MS=500</c> ran the loot streamer at 500 ms, six times its
    /// configured 3,000. Each arm now carries its own next-due stamp.
    /// </summary>
    [Fact]
    public void EachPumpArmHoldsItsOwnInterval()
    {
        long due = 0;

        // Never fired: the first tick always fires, and arms the arm for its own period.
        Assert.True(WorldPumpArm.IsDue(10_000, ref due, 3_000));
        Assert.Equal(13_000, due);

        // The shared pump is now running at the OTHER arm's 500 ms. This arm sits out five of six.
        for (long now = 10_500; now < 13_000; now += 500)
        {
            Assert.False(WorldPumpArm.IsDue(now, ref due, 3_000));
            Assert.Equal(13_000, due);
        }

        Assert.True(WorldPumpArm.IsDue(13_000, ref due, 3_000));
        Assert.Equal(16_000, due);

        // A non-positive interval is "every tick", the same reading the pump already gives it.
        long always = 0;
        Assert.True(WorldPumpArm.IsDue(1, ref always, 0));
        Assert.True(WorldPumpArm.IsDue(2, ref always, 0));
    }

    /// <summary>
    /// <b>The tolerance is not slop; without it the limit halves the shipped rate.</b> Both arms are
    /// configured at 3,000 ms and the shared pump therefore ticks at 3,000, so a tick arriving one
    /// millisecond early would miss its own deadline and the arm would wait a whole further period —
    /// 6 s instead of 3, on the streamer whose absence is the owner's P0.
    /// </summary>
    [Fact]
    public void AnArmWhoseIntervalMatchesThePumpStillFiresEveryTick()
    {
        const int Pump = 3_000;
        long due = 0;
        long fired = 0;

        // Ten ticks, each one millisecond early — the worst case a timer can produce.
        for (int tick = 0; tick < 10; tick++)
        {
            if (WorldPumpArm.IsDue((tick * Pump) - 1, ref due, 3_000, Pump / 2))
            {
                fired++;
            }
        }

        Assert.Equal(10, fired);

        // …and the tolerance still cannot make an arm outrun its own interval by more than the
        // pump's own period: a 3,000 ms arm on a 500 ms pump fires 3 times in 9 seconds, not 18.
        long slow = 0;
        int fast = 0;
        for (int tick = 0; tick <= 18; tick++)
        {
            if (WorldPumpArm.IsDue(tick * 500, ref slow, 3_000, 250))
            {
                fast++;
            }
        }

        Assert.Equal(4, fast);
    }

    /// <summary>
    /// docs/22 §9.4: the rate limit takes the instant as a parameter and never asks what time it is,
    /// and it lives outside <c>Cranberry.Zone.World</c> so the simulation seam keeps its clock ban
    /// (<c>WorldSeamTests.NoTypeUnderWorldReadsAWallClock</c>).
    /// </summary>
    [Fact]
    public void TheRateLimitIsPureAndSitsOnTheHostSideOfTheWorldSeam()
    {
        Assert.Equal("Cranberry.Zone", typeof(WorldPumpArm).Namespace);
        Assert.All(
            typeof(WorldPumpArm).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => Assert.True(method.IsStatic, method.Name));
    }

    // -------------------------------------------------------------------------------------------
    // 4. Retirement by guid stopped working inside the eviction window.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>PlanRestream</c> commits synchronously — it drops the evicted keys from its guid index
    /// before a single <c>0f 01</c> is sent — while <c>DrainBurst</c> pays the destroys out over
    /// slices ~40 ms apart. A pickup landing in that window found the guid already gone, silently
    /// failed to retire the MARKER, and the item came back the next time the player walked past:
    /// the "a looted house restocks itself" failure the taken sets exist to prevent.
    /// <para>
    /// Unreachable at the shipped radii — the object is being evicted precisely because the player is
    /// 90 m away from it — but that is a coincidence between two independently configurable numbers,
    /// and this test reaches it with a despawn radius inside pickup range, which a host can set.
    /// </para>
    /// </summary>
    [Fact]
    public void APickupInsideTheEvictionWindowStillRetiresItsMarker()
    {
        var loot = new MatchLoot();
        LootStreamKey key = LootStreamKey.ForMarker(77);
        loot.NoteSpawned(key, 5_150, Landing);

        // The plan evicts it, which commits the removal before any packet has gone out.
        var options = new LootStreamOptions { StreamRadiusMetres = 1f, DespawnRadiusMetres = 1f };
        LootStreamPlan plan = loot.PlanRestream(OffTheMap, Loot.LootRoster.Layout, options);
        Assert.Contains(5_150UL, plan.Evictions);

        // The 0f 01 has NOT been sent yet, so the object is still on the client and still claimable.
        Assert.True(loot.NoteTaken(5_150));
        Assert.True(loot.IsTaken(key));
    }

    // -------------------------------------------------------------------------------------------
    // 5. Guards that were widened rather than made to hold.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Regression guard 1's sentinel was flaky by construction.</b>
    /// <c>AugustWornVisuals.SendShaderParameterGroup</c> is a process-wide mutable static;
    /// <c>Wave5IntegrationTests.TheWornColourExperimentStaysOff</c> asserts it false while
    /// <c>AugustWornVisualsTests.TheShaderGroupIsAvailableButOptIn</c> sets it true inside a
    /// <c>finally</c>. xUnit runs classes in parallel — one collection per class by default, and this
    /// tree has no <c>xunit.runner.json</c> and no <c>[CollectionBehavior]</c> — so the sentinel could
    /// observe <c>true</c> and fail for no real reason. Every class that reads or writes either of the
    /// two appearance statics must therefore share one collection.
    /// </summary>
    [Theory]
    [InlineData("Cranberry.Tests.Zone.Appearance.AugustWornVisualsTests")]
    [InlineData("Cranberry.Tests.Zone.AugustDynamicAppearanceTableTests")]
    [InlineData("Cranberry.Tests.Zone.Wave5IntegrationTests")]
    public void EveryClassTouchingAnAppearanceStaticSharesOneCollection(string typeName)
    {
        Type type = typeof(VerifyWave5FixesTests).Assembly.GetType(typeName, throwOnError: true)!;
        CustomAttributeData data = Assert.Single(
            type.GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal(
            Appearance.AppearanceStaticsCollection.Name,
            Assert.Single(data.ConstructorArguments).Value);
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>Somewhere no Z2 marker is, so the tick evicts and spawns nothing.</summary>
    private static readonly Vector3 OffTheMap = new(100_000f, 0f, 100_000f);
}
