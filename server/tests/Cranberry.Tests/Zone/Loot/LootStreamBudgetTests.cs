using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// docs/52 §4 — the byte budget, which is the whole reason the caps are what they are.
/// <para>
/// <c>OutboundChannel</c> has no send window and no backpressure: every <c>Emit</c> transmits
/// immediately, which is why <c>DrainBurst</c> exists. So the streamer's safety argument is entirely
/// arithmetic — the per-tick peak must be a <b>constant</b>, not a function of how dense the POI is
/// or how fast the player runs. These tests hold a simulated map crossing to that constant.
/// </para>
/// </summary>
public sealed class LootStreamBudgetTests
{
    private static readonly Vector3 Landing = LootStreamHarness.Landing;

    /// <summary>
    /// The per-object costs are measured, not assumed: <c>d6</c> 201 B and <c>da</c> 210 B are
    /// invariant over 513 spawns in <c>logs/host-20260829-220829.log</c>, the <c>ea 04</c> computes to
    /// 63 B for interact and 129 B for the NPC world-item component. The native inventory
    /// registration adds up to 149 B, and all five packets carry
    /// a 1-byte gateway tunnel envelope. A despawn is
    /// <c>RemovePlayer.Length</c> + 1.
    /// </summary>
    [Fact]
    public void ThePerObjectCostsMatchTheMeasuredWire()
    {
        Assert.Equal(129, CreateComponentWithRepData.ForWorldItemNpc(LootWorld.DefaultTransientIdCeiling - 1).Length);
        Assert.Equal(202 + 211 + 129 + 63 + 149 + 5, LootStreamOptions.SpawnBytes);
        Assert.Equal(RemovePlayer.Length + 1, LootStreamOptions.DespawnBytes);
        Assert.Equal(ProximateItems.ElementLength, LootStreamOptions.ProximateItemsRowBytes);

        // 9,552 B at 129 rows, measured at logs/host-20260830-090725.log 09:12:49.975 — which is what
        // fixes the header at 6 B and not the 3 B docs/52 §4a wrote down.
        var measured = new ProximateItems(new ProximateItem[129]);
        Assert.Equal(9552, measured.Length);
        Assert.Equal(
            LootStreamOptions.ProximateItemsHeaderBytes + (129 * LootStreamOptions.ProximateItemsRowBytes),
            measured.Length + 1);
    }

    [Fact]
    public void EveryGroundItemRegistrationFitsTheSpawnByteBudget()
    {
        var weapons = new WeaponSession(crateOpeningWeapon: true);
        uint transient = LootWorld.DefaultTransientIdCeiling - 1;
        int worldBytes = new AddLightweightItem(1, transient, 1, Vector3.Zero).Length
            + new LightweightToFullNpc(transient, 1).Length
            + CreateComponentWithRepData.ForWorldItemNpc(transient).Length
            + CreateComponentWithRepData.ForGroundItem(transient, transient).Length;
        int largestRegistration = 0;
        int genericRows = 0;
        int weaponRows = 0;
        foreach (InventoryItemFact fact in InventoryItemFacts.All)
        {
            var item = new InventoryItem(fact.DefinitionId, ItemGuid: 2, Count: 1, OwnerGuid: 1,
                ContainerGuid: 0, ContainerDefinitionId: 0, SlotId: 0);
            WeaponItemAddTail? tail = weapons.CreateTail(fact.DefinitionId, magazine: 0);
            using var writer = new PacketWriter();
            if (tail is null)
            {
                new ItemAdd(item.OwnerGuid, item).WriteTo(writer);
                Assert.Equal(78, writer.Position);
                genericRows++;
            }
            else
            {
                new WeaponItemAdd(item.OwnerGuid, item, tail).WriteTo(writer);
                weaponRows++;
            }

            largestRegistration = Math.Max(largestRegistration, writer.Position);
            int spawnBytes = worldBytes + writer.Position + 5;
            Assert.True(spawnBytes <= LootStreamOptions.SpawnBytes,
                $"item {fact.DefinitionId} needs {spawnBytes} B against {LootStreamOptions.SpawnBytes} B");
        }

        Assert.True(genericRows > 0);
        Assert.True(weaponRows > 100, $"only {weaponRows} weapon tails checked");
        Assert.Equal(149, largestRegistration);
    }

    /// <summary>
    /// A worst-case tick spawns
    /// <c>MaxSpawnsPerRestream</c> = 31 objects (the 22,000 B budget plus the two boxes an
    /// indivisible gun set may add past the test), destroys a full
    /// <c>MaxEvictionsPerRestream</c> = 64, and republishes a <b>32-row</b> panel rather than a
    /// <c>MaxLive</c>-row one. <b>26,736 B</b>, which <c>DrainBurst</c> cuts into 16-object slices
    /// 40 ms apart — about 280 ms of a 500 ms tick.
    /// </summary>
    [Fact]
    public void TheStatedPerTickBoundIncludesNativeGroundInventoryRegistration()
    {
        LootStreamOptions options = LootStreamOptions.Default;

        Assert.Equal(31, options.MaxSpawnsPerRestream);
        Assert.Equal(32, options.MaxPanelRows);
        Assert.Equal(26_736, options.WorstCaseTickBytes);

        // The panel's share of the worst tick, which is the number wave 8 set out to cut.
        int panel = LootStreamOptions.ProximateItemsHeaderBytes
            + (options.MaxPanelRows * LootStreamOptions.ProximateItemsRowBytes);
        Assert.Equal(2_375, panel);
        Assert.True(panel < 9_552 / 3, "the 2 m panel should be under a third of the measured 9,552 B");

        // Both NPC world identity and native inventory registration are part of each slice.
        Assert.Equal(12_144, 16 * LootStreamOptions.SpawnBytes);

        // The worst tick is only reachable while the disc is cold, but the ceiling it implies is
        // worth stating: 54 KB/s against the owner's own streamer at up to 220 KB/s.
        Assert.Equal(53_472, options.WorstCaseTickBytes * 1000 / options.RestreamIntervalMs);
    }

    /// <summary>A tick that changed no ground item pays nothing at all — the panel included.</summary>
    [Fact]
    public void ATickThatChangedNothingCostsNothing()
    {
        Assert.Equal(0, LootStreamPlan.Empty.EstimatedBytes);
        Assert.False(LootStreamPlan.Empty.ChangedTheWorld);
        Assert.Equal(0, LootStreamOptions.EstimateTickBytes(0, 0, -1));
    }

    /// <summary>
    /// The brief's byte-budget acceptance: no tick of a real 2 km crossing of the Harris Bluffs POIs
    /// ever exceeds the stated bound, and every <c>ProximateItems</c> republish stays at or under the
    /// 9,552 B the owner has already run live.
    /// </summary>
    [Fact]
    public void NoTickOfARealCrossingExceedsTheStatedBound()
    {
        var harness = new LootStreamHarness();
        LootStreamOptions options = harness.Options;
        int worstTick = 0;
        int worstPanel = 0;
        int bursts = 0;

        for (int step = 0; step <= 80; step++)
        {
            var centre = new Vector3(Landing.X, Landing.Y, Landing.Z + (step * 25f));
            if (harness.TickIfDue(centre) is not LootStreamPlan plan)
            {
                continue;
            }

            bursts++;
            worstTick = Math.Max(worstTick, plan.EstimatedBytes);
            if (plan.ChangedTheWorld)
            {
                // WAVE 8: the panel is Math.Min(PanelRowCap, LiveAfter) rows, not LiveAfter - the
                // 2 m ball, not the working set. LiveAfter is still what the working set holds.
                worstPanel = Math.Max(
                    worstPanel,
                    LootStreamOptions.ProximateItemsHeaderBytes
                        + (Math.Min(plan.PanelRowCap, plan.LiveAfter)
                            * LootStreamOptions.ProximateItemsRowBytes));
            }

            Assert.True(
                plan.EstimatedBytes <= options.WorstCaseTickBytes,
                $"tick {step} cost {plan.EstimatedBytes} B against a bound of {options.WorstCaseTickBytes} B");
        }

        Assert.True(bursts >= 5, $"only {bursts} bursts over 2 km");
        Assert.True(worstTick > 0);

        // The panel used to be held to the 9,552 B the owner has run live. Since the 2 m filter it
        // is bounded by construction at 32 rows, and this asserts the tighter number: if the filter
        // is ever removed, a real crossing puts this straight back over 9 KB.
        Assert.True(
            worstPanel <= 2_375,
            $"the panel reached {worstPanel} B, past the 32-row / 2,375 B ceiling the 2 m filter "
            + "puts on it (docs/78 §3.3)");
    }

    /// <summary>
    /// The whole-match cost, which is what makes the streamer noise against the landing burst's
    /// 190 KB/s peak: a 20-minute match at the play-test's observed pace is under 2 KB/s.
    /// </summary>
    [Fact]
    public void AWholeMatchOfStreamingIsUnderTwoKilobytesASecond()
    {
        var harness = new LootStreamHarness();

        // 3 m/s for 20 minutes = 3,600 m. WAVE 8: the tick is 500 ms, so a 20-minute match is 2,400
        // ticks of 1.5 m each rather than 400 of 9 m. Same walk, same distance, 6× the sampling.
        const int ticks = 2_400;
        const double tickSeconds = 0.5;
        for (int tick = 0; tick < ticks; tick++)
        {
            harness.TickIfDue(new Vector3(Landing.X, Landing.Y, Landing.Z + (tick * 1.5f)));
        }

        long total = harness.TickBytes.Sum();
        long bytesPerSecond = (long)(total / (ticks * tickSeconds));

        // 2 KB/s was the wave-5 acceptance and it still holds: the streamer moves the same objects
        // over the same walk, it just stops making the player wait 24 s for them.
        Assert.True(bytesPerSecond < 2_000, $"{bytesPerSecond} B/s over a 20-minute match");

        // A straight 3.6 km line runs out of the Harris Bluffs POI belt and into empty terrain
        // long before the twenty minutes are up, so this is a floor on what a real match streams,
        // not an estimate of it: docs/52 §4e projects 1,000-2,000 objects for a player who keeps
        // moving POI to POI, and the line above is what bounds the cost either way.
        // 284 at the owner's density, against 300+ at the pre-wave-8 flat gate over the same walk:
        // this is the floor moving, not the streamer doing less work. The bound is a guard against
        // a streamer that streams nothing, so it tracks the density.
        Assert.True(harness.Loot.StreamedCount > 200, $"only {harness.Loot.StreamedCount} objects streamed");
    }

    /// <summary>
    /// Whatever the caps are set to, the arithmetic bound has to hold. Note what wave 8 changed
    /// here: raising <c>MaxLive</c> no longer changes the bound at all, because the panel is capped
    /// at <c>PanelMaxRows</c> instead of scaling with the working set. That is the whole reason
    /// <c>MaxLive</c> could go from 128 to 384 (docs/78 §3.3).
    /// </summary>
    [Theory]
    [InlineData(96, 64, 22_000, 26_736)]
    [InlineData(128, 64, 22_000, 26_736)]
    [InlineData(384, 64, 22_000, 26_736)]
    [InlineData(384, 16, 22_000, 15_351)]
    [InlineData(384, 64, 0, 51_783)]
    public void TheBoundTracksTheCaps(int maxLive, int maxPerRestream, int byteBudget, int expected)
    {
        var options = new LootStreamOptions
        {
            MaxLive = maxLive,
            MaxPerRestream = maxPerRestream,
            RestreamByteBudget = byteBudget,
        };

        Assert.Equal(expected, options.WorstCaseTickBytes);
    }

    /// <summary>
    /// The panel row cap is what unpins <c>MaxLive</c>: with the filter off (radius 0) the whole
    /// working set becomes panel rows, which is why 128 was the ceiling for five waves and why
    /// raising it without <c>PanelRadiusMetres</c> would have been a mistake. At D281's 576 that is
    /// 42.6 KB of <c>f8 01</c> per republish on its own - the argument gets STRONGER as the ceiling
    /// rises, which is exactly why the row cap had to come first.
    /// </summary>
    [Fact]
    public void TheOldCapWasThePanelAndTheFilterIsWhatRemovedIt()
    {
        var unfiltered = new LootStreamOptions { PanelMaxRows = 0 };
        int live = LootStreamOptions.Default.MaxLive;

        Assert.Equal(704, live);
        Assert.Equal(live, unfiltered.MaxPanelRows);
        Assert.Equal(
            52_103,
            LootStreamOptions.ProximateItemsHeaderBytes + (live * LootStreamOptions.ProximateItemsRowBytes));
        // 76,464 B a tick against the filtered 26,736 - the panel alone is more than twice the whole
        // of the rest of the tick put together.
        Assert.Equal(76_464, unfiltered.WorstCaseTickBytes);
    }
}
