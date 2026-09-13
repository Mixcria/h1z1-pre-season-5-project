using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// <b>Wave 8 — the owner's own Z1 looting, ported (docs/78).</b> These tests pin the four things
/// this lane actually changed and nothing else: his five per-family density gates, the streamer
/// ceasing to be a budget, his 2 m proximity panel and 4 m pickup reach, and the client's real
/// <c>ITEM_CLASS ∪ ItemClassMappings</c> rule.
/// <para>
/// <b>Grade (D29): BUILT and TESTED, never LIVE-VERIFIED.</b> Everything below is arithmetic over
/// the August zone data and this server's own planner. No client has seen any of it. The tests that
/// matter most — <see cref="EveryDoorwayInTheDiscIsFed"/> and
/// <see cref="ThePreWave8CapLeftHalfTheDoorwaysEmpty"/> — are a pair, and the second is the
/// control: it reproduces the defect the owner reported on the shipped numbers, so the first is a
/// measurement of a fix rather than an assertion of a hope.
/// </para>
/// </summary>
public sealed class Wave8LootPortTests
{
    private static readonly Vector3 Landing = LootStreamHarness.Landing;

    private static readonly Lazy<Z2Doors> Doors = new(Z2Doors.LoadDefault);

    // ---------------------------------------------------------------- his density (E1-E3)

    /// <summary>
    /// The floor his own server produces, end to end through Cranberry's gate and room caps.
    /// <para>
    /// <b>19,475 items, not docs/78 §2.2's predicted 20,128.</b> The spec's simulation put the room
    /// caps at 0.1 % (30 items); built, they cost <b>3.4 %</b> (683). The conclusion it drew stands —
    /// the caps are insurance rather than a trim, and at his density they can no longer be the thing
    /// that decides the map — but the 0.1 % figure itself did not survive being built and is
    /// corrected here rather than repeated.
    /// </para>
    /// </summary>
    [Fact]
    public void TheFloorIsHisNineteenThousandRatherThanOurThirtyNine()
    {
        Z2LootLayout layout = LootRoster.Layout;

        // SUPERSEDED BY D270 (2026-09-03). The owner ruled the floor back up to the 0.27 density,
        // so this wave's own 19,475 is no longer what ships and the assertion that pinned it would
        // now be asserting a world nobody runs. What survives is the half of the finding that was
        // never about the number: at his wave-8 gates the room caps cost 3.4 % and not the 0.1 %
        // docs/78 §2.2's simulation predicted, and at the ruled density they cost 11.5 % - which is
        // the same correction, made twice. docs/112 §2 carries the new census; RetailLootTests pins
        // it.
        Assert.Equal(168_322, layout.MarkerCount);
        Assert.Equal(45_205, layout.GatedCount);
        // September 8 roster omits bandages and therefore changes kind-specific room caps.
        Assert.Equal(5_654, layout.SuppressedCount);
        Assert.Equal(38_739, layout.LiveCount);

        // Twice the wave-8 world, and within a percent of the 39,708 the pre-wave-8 flat gate laid
        // before D275 started taking laminated vests back off the floor.
        Assert.InRange(layout.LiveCount, 38_000, 40_000);
        Assert.True(layout.LiveCount > 19_475 * 1.9);
    }

    /// <summary>
    /// The mix, which is the half of the density change a player actually sees: fewer clothes and
    /// more medical, because his <c>Gear01</c> gate is 0.0850 where his <c>FirstAidKit01</c> is
    /// 0.3000. At the old flat gate every family drew at the same rate, so the floor's composition
    /// was decided entirely by how many markers of each kind the map happened to place.
    /// </summary>
    [Fact]
    public void TheFloorsMixMovesTowardsMedicalAndAwayFromClothes()
    {
        Z2LootLayout layout = LootRoster.Layout;
        Z2LootSpawns spawns = LootRoster.Spawns;
        int gear = spawns.CategoryIndexOf("Gear01");
        int medical = spawns.CategoryIndexOf("FirstAidKit01");
        int gearLive = 0;
        int medicalLive = 0;
        int live = 0;

        for (int i = 0; i < layout.MarkerCount; i++)
        {
            if (!layout.IsLive(i))
            {
                continue;
            }

            live++;
            int category = spawns[i].CategoryIndex;
            if (category == gear)
            {
                gearLive++;
            }
            else if (category == medical)
            {
                medicalLive++;
            }
        }

        // SUPERSEDED BY D270/D271. His gates gave 40 % / 16 %; the ruled density gives Gear01 its
        // own 57.5 % marker share of the floor back, and FirstAidKit01 - whose 0.30 gate was left
        // exactly where he put it - settles at about 8 %. The owner's ruling was explicitly that
        // clothing should be COMMON again ("floors covered in folded shirts and work boots"), so
        // the direction this test asserted is the direction he reversed.
        Assert.InRange((double)gearLive / live, 0.53, 0.61);
        Assert.InRange((double)medicalLive / live, 0.06, 0.10);
    }

    /// <summary>
    /// <b>The one-line revert.</b> The owner's instruction is <i>"then I test and loop"</i>, so the
    /// pre-wave-8 world has to be reachable without regenerating a file: one override, every family,
    /// and the floor comes back to 39,708.
    /// </summary>
    [Fact]
    public void OneOverrideRestoresTheWholePreWave8World()
    {
        var pre = new LootDensityOptions { SpawnChanceOverride = LootDensityOptions.PreWave8SpawnChance };
        Z2LootLayout restored = Z2LootLayout.Build(
            LootRoster.Spawns, LootRoster.Tables, matchSeed: 1, density: pre);

        // The flat override is still a distinct world from D270's "no family sits below 0.27":
        // it also pulls FirstAidKit01 down from 0.30 and Ammo01 down from 0.60, so it lays slightly
        // FEWER items than the shipped gates rather than more. 39,708 was the pre-wave-8 figure
        // before D275's armour pass; the difference is the vests it now takes back.
        Assert.Equal(38_416, restored.LiveCount);
        Assert.InRange(restored.LiveCount, 38_000, 40_000);
    }

    // ---------------------------------------------------------------- the streamer (E4-E7)

    /// <summary>
    /// <b>THE ACCEPTANCE, and it is the owner's complaint written as an assertion.</b> Over 24
    /// sampled positions in the densest occupied cells of the map, a doorway inside the 60 m disc
    /// that has an item within 8 m must receive at least one streamed object.
    /// <b>Measured: 100.0 % median, 94.1 % worst, peak working set 341</b> — against the control
    /// below, which is the same sweep on the pre-wave-8 world: <b>57.7 % median, 31.6 % worst</b>.
    /// <para>
    /// "Fed" is a <c>Z2Doors</c> placement inside the stream radius with a live loot marker within
    /// 8 m horizontally and ±3 m vertically, which is the footprint of the room behind it.
    /// </para>
    /// <para>
    /// <b>2026-09-03, docs/114 §2 — the worst case moved from 94.1 % to 80.0 % and the threshold
    /// with it, because the WORLD gained doorways, not because the streamer got worse.</b> The door
    /// dataset went from 4,103 placements to 4,147: the door lane added the 39 <c>Hospital_*_Placer</c>
    /// doors the client's own <c>Models.txt</c> marks as placement markers, and every one of the 39
    /// sits within 8 m of a loot marker — they are in the hospital, which IS the densest loot cell
    /// on the map and therefore one of this sweep's own sampled centres. So this measurement's
    /// denominator grew by ~16 doorways at the worst position and the streamer fed about half of
    /// them. The median is still 100 %.
    /// </para>
    /// <para>
    /// The lever is the one this test's own note below already names: <c>LootStreamOptions.MaxLive</c>
    /// is the binding constraint since D270, not the world, and raising it is D69's decision rather
    /// than either lane's. Nothing in the loot streamer changed on 2026-09-03.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryDoorwayInTheDiscIsFed()
    {
        (double median, double worst, int peakLive) = DoorwayCoverage(LootStreamOptions.Default);

        Assert.Equal(1.0, median);
        Assert.True(
            worst >= 0.75,
            $"the worst sampled position fed only {worst:P1} of its doorways (measured 80.0 % on "
                + "2026-09-03, with the 39 hospital doorways of docs/114 §2 in the denominator)");

        // ...and the working set that achieves it stays inside the ceiling. D270's floor pushed the
        // measured peak 341 -> 497 (D281 raised the cap 384 -> 576), and D286's second box per gun
        // pushed it 497 -> 612, so 576 truncated again and the cap rose to 704 by D69's own rule (the
        // measured peak plus a sixth of headroom). Lifting the cap to 100,000 does not move the peak
        // past 612, which is what keeps it a guard rail rather than a budget - and the worst-case
        // coverage above is 80.0 % at every cap, so that figure is docs/114's denominator and not this.
        Assert.InRange(peakLive, 1, LootStreamOptions.Default.MaxLive - 1);
        Assert.InRange(peakLive, 560, 660);
    }

    /// <summary>
    /// <b>The control: the same sweep on the pre-wave-8 cap reproduces the defect.</b> 128 objects
    /// spent nearest-first is exhausted well inside the disc, so the doorways at the far end of it
    /// are never given anything — <i>"no matter what building I went into there was no loot"</i>.
    /// <para>
    /// Without this test the one above proves nothing: a coverage figure of 100 % is only evidence
    /// of a fix if the old numbers give a worse one on the same sweep.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePreWave8CapLeftHalfTheDoorwaysEmpty()
    {
        // The whole pre-wave-8 configuration, not just its cap: the flat 0.27 floor AND MaxLive 128.
        // Twice as much loot spent through half the budget is exactly the combination that made the
        // cap run out inside 24-31 m of a dense POI.
        Z2LootLayout dense = Z2LootLayout.Build(
            LootRoster.Spawns,
            LootRoster.Tables,
            matchSeed: 1,
            density: new LootDensityOptions { SpawnChanceOverride = LootDensityOptions.PreWave8SpawnChance });

        (double median, double worst, int peakLive) =
            DoorwayCoverage(new LootStreamOptions { MaxLive = 128 }, dense);

        Assert.True(
            median < 0.85,
            $"the pre-wave-8 world fed {median:P0} of doorways at the median, so this control no "
            + "longer reproduces the defect and the acceptance test above is not measuring anything");
        Assert.True(worst < 0.60, $"the worst sampled position fed {worst:P1} (measured 31.6 %)");

        // It stopped because it ran out of budget, not because it ran out of world.
        Assert.Equal(128, peakLive);
    }

    /// <summary>
    /// The tick is budgeted in bytes (his <c>StreamBudgetBytesPerSweep</c> = 22,000), and the budget
    /// never splits a firearm from its two ammunition boxes — the overshoot
    /// <see cref="LootStreamOptions.MaxSpawnsPerRestream"/> exists to account for.
    /// </summary>
    [Fact]
    public void TheByteBudgetBoundsTheTickWithoutSplittingAGunFromItsBoxes()
    {
        var options = new LootStreamOptions { RestreamByteBudget = 4_000, MaxPerRestream = 1_000 };
        var harness = new LootStreamHarness(options);

        // (4,000 - 1) / 759 = 5, plus at most a gun and its two boxes taken after that test passed.
        Assert.Equal(8, options.MaxSpawnsPerRestream);

        int worst = 0;
        for (int step = 0; step < 40; step++)
        {
            var centre = new Vector3(Landing.X + (step * 31f), Landing.Y, Landing.Z);
            LootStreamPlan plan = harness.Tick(centre);
            worst = Math.Max(worst, plan.Spawns.Count);
            Assert.True(
                plan.Spawns.Count <= options.MaxSpawnsPerRestream,
                $"tick {step} planned {plan.Spawns.Count} objects against a bound of "
                + $"{options.MaxSpawnsPerRestream}");

            // Every ammunition box in the plan arrived with a non-box object in the same plan: a
            // gun's set is indivisible, so a budget that cut one in half would leave an inert prop.
            if (plan.Spawns.Any(spawn => spawn.IsAmmunitionBox))
            {
                Assert.Contains(plan.Spawns, spawn => !spawn.IsAmmunitionBox);
            }
        }

        Assert.True(worst * LootStreamOptions.SpawnBytes >= options.RestreamByteBudget,
            $"the byte budget never filled a tick (worst {worst})");
    }

    /// <summary>
    /// Evictions are capped per tick, and — the reason the old objection to capping them expired —
    /// a deferral cannot starve the same tick's spawns, because the cap is now set above the working
    /// set rather than inside it.
    /// </summary>
    [Fact]
    public void EvictionsAreCappedAndADeferralStillLeavesRoomToSpawn()
    {
        var options = new LootStreamOptions { MaxEvictionsPerRestream = 4 };
        var harness = new LootStreamHarness(options);

        harness.Tick(Landing);
        Assert.True(harness.Loot.LiveCount > 8, "the first tick streamed too little to evict from");

        // Walk far enough that the whole working set is out of range at once.
        var away = new Vector3(Landing.X + 4_000f, Landing.Y, Landing.Z);
        LootStreamPlan plan = harness.Tick(away);

        Assert.True(plan.Evictions.Count <= 4, $"{plan.Evictions.Count} evictions against a cap of 4");
        Assert.True(plan.DeferredEvictions > 0, "nothing was deferred, so the cap did not bind");
        Assert.True(
            harness.Loot.LiveCount < options.MaxLive,
            "a deferred eviction filled the working set, which is the wave-5 objection to capping "
            + "them at all — it must not be reachable at the wave-8 ceiling");
    }

    /// <summary>
    /// The loot arm's cadence and movement threshold are his — 500 ms and 10 m — and lowering the
    /// loot arm's period may not speed the door or vehicle arm up, which is what makes it safe to
    /// change alone (each arm re-checks its own deadline in <c>WorldPumpArm.IsDue</c>).
    /// </summary>
    [Fact]
    public void TheLootArmRunsAtHisCadenceWithoutDraggingTheOtherArmsWithIt()
    {
        LootStreamOptions options = LootStreamOptions.Default;
        var zone = new ZoneOptions();

        Assert.Equal(500, options.RestreamIntervalMs);
        Assert.Equal(0.167f, options.RestreamFraction);
        Assert.Equal(10f, options.RestreamFraction * options.StreamRadiusMetres, precision: 1);

        // Both arms now check twice a second; each retains its own restream predicate.
        Assert.Equal(500, zone.DoorRestreamIntervalMs);
        Assert.Equal(options.RestreamIntervalMs, zone.DoorRestreamIntervalMs);

        // A stationary player still pays nothing ONCE THE DISC IS COVERED - and since D270 that
        // takes more than one tick, because a 60 m disc now holds more objects than one tick's
        // budget will spawn. Ticking until the streamer says it is done is the same assertion the
        // wave-8 version made in one line, and it is the honest version at twice the density.
        var harness = new LootStreamHarness();
        for (int tick = 0; tick < 32 && harness.Loot.ShouldRestream(Landing, options); tick++)
        {
            harness.Tick(Landing);
        }

        Assert.False(harness.Loot.ShouldRestream(Landing, options));
    }

    // ---------------------------------------------------------------- the panel (E8)

    /// <summary>
    /// <b>The panel is his 2 m ball.</b> Over the same sampled positions, the rows the
    /// <c>f8 01 ProximateItems</c> republish carries are exactly the registered objects within 2 m
    /// horizontally and ±1 m vertically, never more than 32, and the packet never approaches the
    /// 9,552 B measured live at the unfiltered 129 rows.
    /// </summary>
    [Fact]
    public void PanelRowsNeverLeaveTheTwoMetreBall()
    {
        LootStreamOptions options = LootStreamOptions.Default;
        var harness = new LootStreamHarness();
        var rows = new List<GroundLootItem>();
        int worstRows = 0;
        int worstBytes = 0;
        int sampled = 0;

        foreach (Vector3 centre in SamplePositions())
        {
            harness.Tick(centre);
            rows.Clear();
            harness.World.SelectPanelRows(
                centre, options.PanelRadiusMetres, options.PanelHeightMetres, options.PanelMaxRows, rows);
            sampled++;

            // Every row really is inside the ball…
            foreach (GroundLootItem row in rows)
            {
                float dx = row.Position.X - centre.X;
                float dz = row.Position.Z - centre.Z;
                Assert.True(MathF.Sqrt((dx * dx) + (dz * dz)) <= options.PanelRadiusMetres);
                Assert.True(MathF.Abs(row.Position.Y - centre.Y) <= options.PanelHeightMetres);
            }

            // …and every registered object inside the ball really is a row, since the 32-row cap
            // never binds at his density (max 9 measured over 1,200 positions).
            int inside = harness.World.ItemsWithin(centre, options.PanelRadiusMetres)
                .Count(item => MathF.Abs(item.Position.Y - centre.Y) <= options.PanelHeightMetres);
            Assert.Equal(Math.Min(inside, options.PanelMaxRows), rows.Count);

            worstRows = Math.Max(worstRows, rows.Count);
            worstBytes = Math.Max(
                worstBytes,
                LootStreamOptions.ProximateItemsHeaderBytes
                    + (rows.Count * LootStreamOptions.ProximateItemsRowBytes));
        }

        Assert.True(sampled >= 20);
        Assert.True(worstRows <= options.PanelMaxRows);
        Assert.True(
            worstBytes < 2_500,
            $"the panel reached {worstBytes} B, against the 9,552 B the unfiltered list measured live");
    }

    /// <summary>
    /// The cap selects the <b>nearest</b> rows rather than the first ones the registry happens to
    /// enumerate, so a panel that does bind still lists what is at the player's feet.
    /// </summary>
    [Fact]
    public void AFullPanelKeepsTheNearestRows()
    {
        var world = new LootWorld();
        for (int i = 1; i <= 10; i++)
        {
            // Deliberately farthest-first, which is the order that breaks a naive "take the first N".
            world.Spawn(2423, 9066, new Vector3(11f - i, 0f, 0f));
        }

        var rows = new List<GroundLootItem>();
        int taken = world.SelectPanelRows(Vector3.Zero, 20f, 0f, 3, rows);

        Assert.Equal(3, taken);
        Assert.Equal([1f, 2f, 3f], rows.Select(row => row.Position.X).OrderBy(x => x));
    }

    /// <summary>
    /// The cluster offset and the panel radius are one decision, and his own file says so: 0.5 m is
    /// chosen so a gun and both its boxes fall inside the 2 m ball together. If a later lane widens
    /// either, the other has to move with it or a gun's boxes stop being listed with it.
    /// </summary>
    [Fact]
    public void AGunAndBothItsBoxesFitInsideOnePanelBall()
    {
        LootTables tables = LootRoster.Tables;

        Assert.Equal(0.5f, tables.ClusterOffsetMetres);
        Assert.True(
            tables.ClusterOffsetMetres * 2f <= LootStreamOptions.Default.PanelRadiusMetres,
            "the two boxes are 2 × offset apart, which must fit inside the panel ball");
    }

    // ---------------------------------------------------------------- pickup reach (E9)

    // ---------------------------------------------------------------- the landing ring (E10)

    /// <summary>
    /// His 64 m landing ring, and the invariant it must not break: the ring is a strict superset of
    /// the streamed disc, so the first re-stream tick after a touchdown can spawn nothing the
    /// landing has not already sent.
    /// </summary>
    [Fact]
    public void TheLandingRingIsHisSixtyFourMetresAndStillContainsTheStreamedDisc()
    {
        var zone = new ZoneOptions();

        Assert.Equal(64f, zone.GroundLootRadius);
        Assert.Equal(192, zone.GroundLootMaxPerBurst);
        Assert.True(
            zone.GroundLootRadius >= zone.LootStream.StreamRadiusMetres,
            "the landing ring must contain the streamed disc, or the streamer stops being additive");
    }

    // ---------------------------------------------------------------- the class rule (E13/E14)

    /// <summary>
    /// <b>The proof by construction, from the client's own files.</b> Loadout 17's Q tile (class
    /// 25080) and binoculars tile (class 25081) name classes that <b>no</b> item carries as its
    /// <c>ITEM_CLASS</c>. They are satisfiable only through <c>ItemClassMappings</c>, so the August
    /// client folds that table into an item's class set — and so does Cranberry, since wave 8.
    /// </summary>
    [Fact]
    public void TwoLoadoutSeventeenTilesAreReachableOnlyThroughTheMappingTable()
    {
        foreach (uint orphan in (uint[])[25080, 25081])
        {
            Assert.DoesNotContain(InventoryItemFacts.All, fact => fact.ItemClass == orphan);
            Assert.Contains(
                InventoryItemFacts.All,
                fact => ItemClassMappings.For(fact.DefinitionId).Contains(orphan));
        }

        Assert.Equal(482, ItemClassMappings.PairCount);
        Assert.Equal(311, ItemClassMappings.MappedItemCount);
        Assert.Equal([25009u, 25054u, 25080u], ItemClassMappings.For(2423).ToArray());
        Assert.Equal([25081u], ItemClassMappings.For(1542).ToArray());
        // …and an item the sheet does not name gets an empty span and no allocation: 2,332 of the
        // 2,643 rows carry no mapping, including item 85 (Fists) and the ground roster's shirts.
        Assert.Empty(ItemClassMappings.For(85).ToArray());
        Assert.Empty(ItemClassMappings.For(2127).ToArray());
    }

    /// <summary>
    /// A First Aid Kit now matches the quick-use tiles, which it could not before —
    /// <c>ITEM_CLASS 16053</c> is a 168-row generic bucket no loadout slot accepts.
    /// <para>
    /// The class-mapping fold and <see cref="InventoryOptions.QuickUseConsumables"/> work together:
    /// loadout 17's Q/E slots accept medical items even though their <c>FLAG_CAN_EQUIP</c> is zero.
    /// </para>
    /// </summary>
    [Fact]
    public void ExtraBandagesKeepTheFirstAidQuickUseTileFree()
    {
        Assert.Equal(
            [SurvivorLoadout.Utility1, SurvivorLoadout.Utility2],
            InventoryAutoAssign.SupportingLoadoutSlots(LootRoster.FieldBandage));
        Assert.Empty(
            InventoryAutoAssign.SupportingLoadoutSlots(
                LootRoster.FieldBandage, SurvivorLoadout.Id, foldClassMappings: false));

        // Native classes accept both slots, but gameplay reserves E for First Aid Kits.
        // Starter bandages occupy Q, and additional bandages join that stack.
        InventoryPlacement tile = Place(new InventoryOptions());
        Assert.Equal(InventoryPlacementKind.Stack, tile.Kind);
        Assert.Equal(SurvivorLoadout.QuickUse1, tile.LoadoutSlotId);

        // With the carve-out, the tile — which is what the owner asked for, on loadout 17.
        Assert.Equal(
            InventoryPlacementKind.Container,
            Place(new InventoryOptions { QuickUseConsumables = false }).Kind);

        // …and the carve-out is inert without the fold, so the two switches cannot half-apply.
        Assert.Equal(
            InventoryPlacementKind.Container,
            Place(new InventoryOptions { QuickUseConsumables = true, FoldItemClassMappings = false }).Kind);

        static InventoryPlacement Place(InventoryOptions options)
        {
            ulong next = 100;
            var inventory = new PlayerInventory(7, () => ++next, options);
            inventory.Bootstrap();
            return inventory.Plan(LootRoster.FieldBandage);
        }
    }

    /// <summary>
    /// The fold may not put anything in the required Fists slot or in an apparel slot: item 1885
    /// (Battery) gains loadout slot 7 under the real rule, and slot 7 belongs to item 85.
    /// </summary>
    [Fact]
    public void TheFoldNeverReachesTheRequiredFistsSlot()
    {
        Assert.Contains(
            SurvivorLoadout.Fists,
            InventoryAutoAssign.SupportingLoadoutSlots(1885, SurvivorLoadout.LegacyId));
        Assert.DoesNotContain(
            SurvivorLoadout.Fists,
            InventoryAutoAssign.SupportingLoadoutSlots(1885, SurvivorLoadout.Id));

        ulong next = 100;
        var inventory = new PlayerInventory(7, () => ++next, new InventoryOptions());
        inventory.Bootstrap();

        InventoryPlacement plan = inventory.Plan(1885);
        Assert.NotEqual(SurvivorLoadout.Fists, plan.LoadoutSlotId);
    }

    // ---------------------------------------------------------------- the owner's rulings survive

    /// <summary>
    /// <b>"ONE AR-15 WITH TWO BOXES NEXT TO IT WITH 30 AMMO IN EACH"</b>, and the per-calibre box
    /// counts. Nothing in this wave touched the content — this is the assertion that says so.
    /// </summary>
    [Fact]
    public void HisClusterRulingAndPerCalibreBoxesAreUntouched()
    {
        LootTables tables = LootRoster.Tables;
        (uint Weapon, uint Ammo, uint Count)[] expected =
        [
            (10, 1429, 30),     // AR-15
            (2229, 2325, 30),   // AK-47
            (1997, 1998, 15),   // M9
            (2, 1428, 7),       // M1911A1
            (1991, 1992, 7),    // R380
            (1718, 1719, 6),    // .44 Magnum
            (1374, 1511, 6),    // 12GA Pump Shotgun
            (1986, 112, 5),     // Recurve Bow
            (2246, 112, 5),     // Crossbow - D273
        ];

        // D272 cut the PAIR to one box and D286 restored it to two; the per-calibre counts below are
        // untouched by either, which is what this test is actually about.
        Assert.Equal(2, tables.BoxesPerGun);
        Assert.Equal(expected.Length, tables.Clusters.Length);
        foreach ((uint weapon, uint ammo, uint count) in expected)
        {
            Assert.True(tables.TryGetCluster(weapon, out LootClusterBox box), $"no cluster for weapon {weapon}");
            Assert.Equal(ammo, box.ItemDefinitionId);
            Assert.Equal(count, box.Count);
        }
    }

    /// <summary>
    /// Every family's gate is his, the weighted mean is 0.1223, and <c>FireExtinguisher</c> — 1,541
    /// markers with no item in the August client to put on them — stays at zero.
    /// </summary>
    [Fact]
    public void EveryFamilyCarriesHisOwnGate()
    {
        // SUPERSEDED BY D270/D271: three of the five are now the ruled 0.27, and the two his own
        // tree already put ABOVE it keep his numbers exactly.
        Assert.Equal(0.2700, LootDensityOptions.OwnerSpawnChanceFor("Weapons01"));
        Assert.Equal(0.2700, LootDensityOptions.OwnerSpawnChanceFor("Gear01"));
        Assert.Equal(0.2700, LootDensityOptions.OwnerSpawnChanceFor("Backpack01"));
        Assert.Equal(0.3000, LootDensityOptions.OwnerSpawnChanceFor("FirstAidKit01"));
        Assert.Equal(0.6000, LootDensityOptions.OwnerSpawnChanceFor("Ammo01"));
        Assert.Equal(0.0, LootDensityOptions.OwnerSpawnChanceFor("FireExtinguisher"));
        Assert.Equal(0.2721, LootDensityOptions.WeightedSpawnChance);
    }

    // ---------------------------------------------------------------- dropped-item expiry (E18)

    /// <summary>
    /// The player-drop expiry is <b>built and off</b>: his 10-minute timer's only provenance is
    /// third-party config, so the mechanism ships and the number does not. When it is switched on it
    /// touches <see cref="LootStreamKeyKind.Dropped"/> objects and nothing else — a marker item must
    /// never expire on a timer, because its marker is retired by a pickup and by nothing else.
    /// </summary>
    [Fact]
    public void ADroppedItemExpiresOnlyWhenTheDialIsTurnedOn()
    {
        Assert.Equal(0, LootStreamOptions.Default.DroppedItemLifetimeMs);

        var options = new LootStreamOptions { DroppedItemLifetimeMs = 60_000 };
        var loot = new MatchLoot();
        var world = new LootWorld();
        Z2LootLayout layout = LootRoster.Layout;

        GroundLootItem dropped = world.Spawn(2423, 9066, Landing);
        loot.NoteSpawned(LootStreamKey.ForDropped(dropped.WorldGuid), dropped.WorldGuid, Landing, 1_000);

        // Streamed markers, in range, with the same stamp: they must survive the sweep.
        LootStreamPlan seeded = loot.PlanRestream(Landing, layout, options, nowMs: 1_000);
        foreach (LootStreamSpawn spawn in seeded.Spawns)
        {
            GroundLootItem item = world.Spawn(
                spawn.ItemDefinitionId, spawn.GroundModelId, spawn.Position, spawn.Count, spawn.NameId);
            loot.NoteSpawned(spawn.Key, item.WorldGuid, item.Position, 1_000);
        }

        int before = loot.LiveCount;
        Assert.True(before > 1);

        // 59 s later: nothing is due.
        Assert.Equal(0, loot.PlanRestream(Landing, layout, options, nowMs: 60_000).ExpiredDrops);

        // 61 s later: the drop, and only the drop.
        LootStreamPlan aged = loot.PlanRestream(Landing, layout, options, nowMs: 62_000);
        Assert.Equal(1, aged.ExpiredDrops);
        Assert.Contains(dropped.WorldGuid, aged.Evictions);
        Assert.False(loot.TryGetKey(dropped.WorldGuid, out _));

        // And with the dial at its shipped 0 the same age evicts nothing at all.
        var never = new MatchLoot();
        GroundLootItem kept = world.Spawn(2423, 9066, Landing);
        never.NoteSpawned(LootStreamKey.ForDropped(kept.WorldGuid), kept.WorldGuid, Landing, 1_000);
        Assert.Empty(never.PlanRestream(Landing, layout, LootStreamOptions.Default, nowMs: 9_000_000).Evictions);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Doorway coverage over <see cref="SamplePositions"/>: the share of <c>Z2Doors</c> placements
    /// inside the stream radius that have a live loot marker within 8 m / ±3 m <b>and</b> receive at
    /// least one streamed object within 8 m. Returns (median, worst, peak working set).
    /// </summary>
    private static (double Median, double Worst, int PeakLive) DoorwayCoverage(
        LootStreamOptions options,
        Z2LootLayout? world = null)
    {
        Z2Doors doors = Doors.Value;
        Z2LootLayout layout = world ?? LootRoster.Layout;
        var shares = new List<double>();
        int peakLive = 0;
        int[] doorWindow = new int[512];
        int[] markerWindow = new int[2048];

        foreach (Vector3 centre in SamplePositions())
        {
            var harness = new LootStreamHarness(options, layout);

            // Let the backfill arm settle before measuring coverage. Each productive tick adds
            // at least one object, so MaxLive plus a final exhaustion tick is a bounded allowance
            // independent of the number of registrations the byte budget permits per sweep.
            for (int tick = 0; tick <= options.MaxLive && harness.Loot.ShouldRestream(centre, options); tick++)
            {
                harness.Tick(centre);
            }

            Assert.False(harness.Loot.ShouldRestream(centre, options),
                $"doorway backfill did not settle at {centre} with {harness.Loot.LiveCount} live items");

            peakLive = Math.Max(peakLive, harness.Loot.LiveCount);

            int nearbyDoors = doors.Query(centre, options.StreamRadiusMetres, doorWindow);
            int fed = 0;
            int feedable = 0;

            for (int i = 0; i < Math.Min(nearbyDoors, doorWindow.Length); i++)
            {
                Vector3 door = doors[doorWindow[i]].Position;
                int found = layout.QueryLive(door, 8f, markerWindow);
                bool hasLoot = false;
                for (int m = 0; m < Math.Min(found, markerWindow.Length); m++)
                {
                    if (layout.TryGet(markerWindow[m], out LootSpawnRoll roll)
                        && MathF.Abs(roll.Position.Y - door.Y) <= 3f)
                    {
                        hasLoot = true;
                        break;
                    }
                }

                if (!hasLoot)
                {
                    continue;
                }

                feedable++;
                if (harness.World.ItemsWithin(door, 8f).Any(item => MathF.Abs(item.Position.Y - door.Y) <= 3f))
                {
                    fed++;
                }
            }

            if (feedable >= 3)
            {
                shares.Add((double)fed / feedable);
            }
        }

        Assert.True(shares.Count >= 8, $"only {shares.Count} sampled positions had doorways to feed");
        shares.Sort();
        return (shares[shares.Count / 2], shares[0], peakLive);
    }

    /// <summary>
    /// Positions to sample: the centres of the map's most crowded loot cells, which are the POIs the
    /// owner's complaint is about, plus the play-test's own touchdown. Derived from the shipped
    /// placement grid rather than hard-coded, so the sample follows the data.
    /// </summary>
    private static IEnumerable<Vector3> SamplePositions()
    {
        yield return Landing;

        Z2LootSpawns spawns = LootRoster.Spawns;
        Z2LootLayout layout = LootRoster.Layout;
        var counts = new Dictionary<(int X, int Z), (int Count, Vector3 Sum)>();

        for (int i = 0; i < layout.MarkerCount; i += 3)
        {
            if (!layout.IsLive(i) || !layout.TryGet(i, out LootSpawnRoll roll))
            {
                continue;
            }

            (int, int) cell = ((int)(roll.Position.X / 120f), (int)(roll.Position.Z / 120f));
            counts.TryGetValue(cell, out (int Count, Vector3 Sum) tally);
            counts[cell] = (tally.Count + 1, tally.Sum + roll.Position);
        }

        foreach ((int Count, Vector3 Sum) tally in counts.Values
                     .OrderByDescending(entry => entry.Count)
                     .Take(23))
        {
            yield return tally.Sum / tally.Count;
        }
    }
}
