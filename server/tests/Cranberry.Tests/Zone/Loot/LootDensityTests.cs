using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// How much is on the floor. These are the assertions that encode the owner's *"there's too much
/// on the floor"* (docs/39 §2.1, §4, §10): the gate fires at the rate the file declares, the same
/// seed reproduces the same map, and a 4 m room holds the 0-6 items his own retail footage counted
/// rather than the ~12 that a gateless roll produced.
/// </summary>
public sealed class LootDensityTests
{
    /// <summary>Markers that are not <c>FireExtinguisher</c> — the 166,781 that can hold an item.</summary>
    private static readonly Lazy<int[]> ItemMarkers = new(() =>
    {
        Z2LootSpawns spawns = LootRoster.Spawns;
        int fireExtinguisher = spawns.CategoryIndexOf("FireExtinguisher");
        var markers = new List<int>(167_000);
        for (int i = 0; i < spawns.Count; i++)
        {
            if (spawns[i].CategoryIndex != fireExtinguisher)
            {
                markers.Add(i);
            }
        }

        return [.. markers];
    });

    /// <summary>
    /// <b>WAVE 8 (docs/78 §2.2): one number PER FAMILY, and it is the owner's own.</b> The runtime
    /// reads each gate from the generated file so the world can be retuned without a rebuild; this
    /// asserts the file and the code's documented constants have not drifted apart, and that every
    /// category that can spawn carries the owner's number for that family rather than one flat
    /// Cranberry-solved value.
    /// </summary>
    [Fact]
    public void EveryFamilyCarriesItsRuledGate()
    {
        // D270/D271: the three gates that were below the ruled density came up to it; the two
        // his own tree already put ABOVE it are untouched.
        Assert.Equal(0.2700, LootDensityOptions.Weapons01SpawnChance);
        Assert.Equal(0.2700, LootDensityOptions.Gear01SpawnChance);
        Assert.Equal(0.2700, LootDensityOptions.Backpack01SpawnChance);
        Assert.Equal(0.3000, LootDensityOptions.FirstAidKit01SpawnChance);
        Assert.Equal(0.6000, LootDensityOptions.Ammo01SpawnChance);
        Assert.Equal(0.27, LootDensityOptions.PreWave8SpawnChance);

        double weightedNumerator = 0;
        long markers = 0;
        foreach (LootCategoryTable table in LootRoster.Tables.Categories)
        {
            if (table.Count == 0)
            {
                // FireExtinguisher: 1,541 markers, no item in the client to put on them.
                Assert.Equal("FireExtinguisher", table.Key);
                Assert.Equal(0.0, table.SpawnChance);
                continue;
            }

            Assert.Equal(LootDensityOptions.OwnerSpawnChanceFor(table.Key), table.SpawnChance);
            Assert.Equal(table.SpawnChance, LootDensityOptions.Default.SpawnChanceFor(table));
            weightedNumerator += table.SpawnChance * table.SpawnerCount;
            markers += table.SpawnerCount;
        }

        // The marker-weighted mean over the 166,781 item markers the August field actually places.
        Assert.Equal(166_781, markers);
        Assert.Equal(
            LootDensityOptions.WeightedSpawnChance,
            weightedNumerator / markers,
            precision: 4);
        Assert.Equal(0.2721, LootDensityOptions.WeightedSpawnChance);

        // …and the dial still turns, including back to the whole pre-wave-8 world in one line.
        var sparse = new LootDensityOptions { SpawnChanceOverride = 0.02 };
        Assert.True(LootRoster.Tables.TryGet("Gear01", out LootCategoryTable? gear));
        Assert.Equal(0.02, sparse.SpawnChanceFor(gear!));

        var pre = new LootDensityOptions { SpawnChanceOverride = LootDensityOptions.PreWave8SpawnChance };
        foreach (LootCategoryTable table in LootRoster.Tables.Categories)
        {
            Assert.Equal(0.27, pre.SpawnChanceFor(table));
        }
    }

    /// <summary>
    /// Gate fidelity, over the whole placement set rather than a sample. The tolerance is four
    /// standard errors of a binomial at the shipped rate, floored at ±0.005, so it is tight where
    /// the category is big (Gear01's 96,722 markers give ±0.006) and honest where it is not
    /// (<c>Ammo01</c> has 72 markers; no per-category rate is measurable there).
    /// </summary>
    [Fact]
    public void TheGateFiresAtTheDeclaredRateInEveryCategory()
    {
        Z2LootSpawns spawns = LootRoster.Spawns;
        Z2LootLayout layout = LootRoster.Layout;
        var gated = new int[spawns.Categories.Length];
        var total = new int[spawns.Categories.Length];

        for (int i = 0; i < spawns.Count; i++)
        {
            int category = spawns[i].CategoryIndex;
            double expected = LootDensityOptions.OwnerSpawnChanceFor(spawns.Categories[category]);
            total[category]++;
            if (expected > 0
                && Z2LootSpawns.PassesGate(layout.MatchSeed, spawns[i].InstanceId, expected))
            {
                gated[category]++;
            }
        }

        for (int category = 0; category < total.Length; category++)
        {
            string key = spawns.Categories[category];
            double expected = LootDensityOptions.OwnerSpawnChanceFor(key);
            if (total[category] < 1_000 || expected <= 0)
            {
                continue;
            }

            double rate = (double)gated[category] / total[category];
            double tolerance = Math.Max(0.005, 4.0 * Math.Sqrt(expected * (1 - expected) / total[category]));
            Assert.True(
                Math.Abs(rate - expected) <= tolerance,
                $"{key}: {gated[category]:N0} of {total[category]:N0} markers gated in = "
                + $"{rate:F4}, expected {expected:F4} ± {tolerance:F4}");
        }
    }

    /// <summary>
    /// <b>The world census, at the owner's own density (docs/78 §2.2).</b> Before the gate every one
    /// of the 166,781 item markers became an item; at his five gates about an eighth do — <b>20,158
    /// gated, 20,128 after the caps</b>, against 44,830 / 43,095 at the pre-wave-8 flat 0.27. The
    /// bounds are wide on purpose: this is a regression guard against the gate being bypassed, not a
    /// re-assertion of the rate the test above measures.
    /// <para>
    /// The second half of this test is the wave-8 finding worth pinning: <b>at his density the room
    /// caps go inert</b>, suppressing 0.1 % of the gated set rather than 11.4 %. They stay in as the
    /// guard against his own "sixteen items in one small room" report, but the world can no longer
    /// build that room in the first place.
    /// </para>
    /// </summary>
    [Fact]
    public void RoughlyAQuarterOfTheMapsMarkersEndUpCarryingAnItem()
    {
        Z2LootLayout layout = LootRoster.Layout;
        int markers = ItemMarkers.Value.Length;

        Assert.Equal(168_322, layout.MarkerCount);
        Assert.InRange(
            layout.GatedCount,
            (int)(markers * (LootDensityOptions.WeightedSpawnChance - 0.005)),
            (int)(markers * (LootDensityOptions.WeightedSpawnChance + 0.005)));

        // D270 makes the caps live again: 5,186 of 45,205 (11.5 %), against 683 of 20,158 (3.4 %)
        // at the wave-8 gates. The bound stays generous so that a later density change does not
        // fail here for the wrong reason.
        Assert.InRange(layout.SuppressedCount, 1, (int)(layout.GatedCount * 0.20));
        // MEASURED, and it corrects docs/78 §2.2's own simulation, which put this at 0.1 % (30
        // items). The real layout suppresses 683 of 20,158 - 3.4 %, against 11.4 % at the flat 0.27.
        // The conclusion the sim drew from its number survives: the caps are now insurance rather
        // than a trim, and at his density they can no longer be what decides the map. The 0.1 %
        // figure itself does not, and is not worth repeating.
        Assert.True(
            layout.SuppressedCount < layout.GatedCount * 0.15,
            $"the room caps suppressed {layout.SuppressedCount:N0} of {layout.GatedCount:N0} gated "
            + "markers; at the ruled density they cost 11.5 % (docs/112 §2)");
        // D275's armour pass is the third subtraction, after the gate and the caps.
        Assert.Equal(
            layout.GatedCount - layout.SuppressedCount - layout.Armour.Refused,
            layout.LiveCount);

        // Every live marker really does roll something, and nothing rolls on a dead one.
        int fireExtinguisher = LootRoster.Spawns.CategoryIndexOf("FireExtinguisher");
        for (int i = 0; i < layout.MarkerCount; i += 997)
        {
            bool rolled = layout.TryGet(i, out LootSpawnRoll roll);
            Assert.Equal(layout.IsLive(i), rolled);
            if (rolled)
            {
                Assert.NotEqual(0u, roll.ItemDefinitionId);
                Assert.NotEqual(fireExtinguisher, roll.CategoryIndex);
                Assert.NotEqual(LootItemKind.Unknown, layout.KindAt(i));
            }
        }
    }

    /// <summary>
    /// <b>How full a room is, and WAVE 8 halved it on purpose.</b> Sampling 2,000 of the map's own
    /// markers, a 4 m / ±2 m room around one held a mean of <b>2.80</b> items at the pre-wave-8 flat
    /// 0.27 — the owner's counted retail census (0-6, mean about 3, every count a floor). At his own
    /// five gates the same sample holds <b>1.38</b>, which is the world his running server actually
    /// produced and which D53 says outranks the census. Before the gate the same sample held
    /// <b>12.45</b>.
    /// <para>
    /// <b>This is the number to look at first if he says the floor is too thin.</b> The revert is
    /// <c>LootDensityOptions.SpawnChanceOverride = 0.27</c>, which puts it back to 2.80.
    /// </para>
    /// </summary>
    [Fact]
    public void AFourMetreRoomHoldsTheThreeItemsHisOwnFootageCounted()
    {
        Z2LootLayout layout = LootRoster.Layout;
        int[] markers = ItemMarkers.Value;
        int stride = markers.Length / 2_000;

        long items = 0;
        int rooms = 0;
        Span<int> found = stackalloc int[256];

        for (int i = 0; i < markers.Length; i += stride)
        {
            items += RoomItems(layout, markers[i], found, kinds: null);
            rooms++;
        }

        // D270: 2.9 at the ruled density, against 1.38 at the wave-8 gates and the owner's own
        // counted retail census of 0-6 with a mean of about 3.
        double mean = (double)items / rooms;
        Assert.InRange(mean, 2.6, 3.2);
    }

    /// <summary>
    /// …and the caps hold where the rule places them: around every item on the floor. Six items,
    /// two weapons, one bag, one vest, one helmet (docs/39 §4.3 — the cap is evaluated from every
    /// member's own room, which is what "symmetric" means there).
    /// </summary>
    [Fact]
    public void NoItemOnTheFloorSitsInAnOverfilledRoom()
    {
        Z2LootLayout layout = LootRoster.Layout;
        LootDensityOptions density = layout.Density;
        Span<int> found = stackalloc int[256];
        var kinds = new int[Enum.GetValues<LootItemKind>().Length];
        int checkedRooms = 0;

        // Stride 7 rather than 31: at the owner's density only one marker in eight carries an item,
        // so the old stride left fewer than 1,000 live rooms to check and the sanity floor below
        // failed for want of samples rather than for a real defect.
        for (int i = 0; i < layout.MarkerCount; i += 7)
        {
            if (!layout.IsLive(i))
            {
                continue;
            }

            Array.Clear(kinds);
            int total = RoomItems(layout, i, found, kinds);
            checkedRooms++;

            Assert.True(
                total <= density.MaxItemsPerRoom,
                $"marker {LootRoster.Spawns[i].InstanceId} sits in a room of {total} items");
            Assert.True(
                kinds[(int)LootItemKind.Weapon] <= density.MaxWeaponsPerRoom,
                $"marker {LootRoster.Spawns[i].InstanceId} sits in a room of "
                + $"{kinds[(int)LootItemKind.Weapon]} weapons");

            foreach (LootItemKind singleton in (LootItemKind[])
                     [LootItemKind.Backpack, LootItemKind.BodyArmor, LootItemKind.Helmet])
            {
                Assert.True(
                    kinds[(int)singleton] <= 1,
                    $"marker {LootRoster.Spawns[i].InstanceId} sits in a room of "
                    + $"{kinds[(int)singleton]} × {singleton}");
            }
        }

        Assert.True(checkedRooms > 1_000, $"only {checkedRooms} live rooms were checked");
    }

    /// <summary>
    /// Determinism, which the whole design rests on: a layout is a pure function of its seed, so a
    /// match replays exactly and a streaming top-up can re-derive any marker without state.
    /// </summary>
    [Fact]
    public void TheSameSeedRebuildsTheSameMapAndADifferentSeedDoesNot()
    {
        Z2LootSpawns spawns = LootRoster.Spawns;
        LootTables tables = LootRoster.Tables;
        Z2LootLayout first = LootRoster.Layout;
        Z2LootLayout again = Z2LootLayout.Build(spawns, tables, matchSeed: 1);

        Assert.Equal(first.GatedCount, again.GatedCount);
        Assert.Equal(first.LiveCount, again.LiveCount);

        int compared = 0;
        for (int i = 0; i < spawns.Count; i += 7)
        {
            Assert.Equal(first.IsLive(i), again.IsLive(i));
            if (!first.TryGet(i, out LootSpawnRoll roll))
            {
                continue;
            }

            Assert.True(again.TryGet(i, out LootSpawnRoll repeat));
            Assert.Equal(roll, repeat);
            compared++;
        }

        Assert.True(compared > 1_000);

        // A different seed must lay out a different map, or the seed is decorative.
        Z2LootLayout other = Z2LootLayout.Build(spawns, tables, matchSeed: 20260829);
        int differences = 0;
        for (int i = 0; i < spawns.Count; i += 7)
        {
            if (first.IsLive(i) != other.IsLive(i))
            {
                differences++;
            }
        }

        Assert.True(differences > 1_000, $"only {differences} markers differ between two seeds");
    }

    /// <summary>
    /// docs/39 §L4, the other half of "too much on the floor": the old burst put 64 items inside a
    /// 20 m circle and <b>nothing</b> beyond, because the cap and the marker count inside 20 m were
    /// the same number. With the gate in place the drop point holds roughly a sixth of that inside
    /// 20 m, and — the part that was broken — the loot keeps going outwards.
    /// </summary>
    [Fact]
    public void TheDropPointNoLongerCarriesABlobOfItemsAndAnEmptyFieldBeyond()
    {
        Z2LootLayout layout = LootRoster.Layout;
        var options = new ZoneOptions();
        var centre = new Vector3(options.MatchDropSpawn.X, options.MatchDropSpawn.Y, options.MatchDropSpawn.Z);

        Span<int> none = [];
        int within20 = layout.QueryLive(centre, 20f, none);
        int within60 = layout.QueryLive(centre, 60f, none);
        int within120 = layout.QueryLive(centre, 120f, none);

        // 64 markers sit within 20 m of the drop point; a quarter of them, give or take, now carry
        // an item. The band is generous — this pins the shape, not a number.
        Assert.InRange(within20, 6, 30);
        Assert.True(within60 > within20 * 2, $"{within60} items within 60 m against {within20} within 20 m");
        Assert.True(within120 > within60, $"{within120} items within 120 m against {within60} within 60 m");

        // …and the nearest-first contract survives the filter: every index handed back is live and
        // they arrive closest first.
        Span<int> nearest = stackalloc int[32];
        int matched = layout.QueryLive(centre, 120f, nearest);
        Assert.Equal(within120, matched);

        float previous = 0f;
        foreach (int index in nearest)
        {
            Assert.True(layout.IsLive(index));
            float dx = LootRoster.Spawns[index].X - centre.X;
            float dz = LootRoster.Spawns[index].Z - centre.Z;
            float distance = MathF.Sqrt((dx * dx) + (dz * dz));
            Assert.True(distance >= previous);
            previous = distance;
        }
    }

    /// <summary>
    /// Items inside the room around <paramref name="index"/> — 4 m horizontally, ±2 m vertically —
    /// counting the centre marker itself when it carries one. Optionally tallies them by kind.
    /// </summary>
    private static int RoomItems(Z2LootLayout layout, int index, Span<int> found, int[]? kinds)
    {
        LootDensityOptions density = layout.Density;
        ref readonly LootSpawnPoint centre = ref layout.Spawns[index];
        int matched = layout.QueryLive(
            new Vector3(centre.X, centre.Y, centre.Z), density.RoomRadiusMetres, found);
        Assert.True(matched <= found.Length, $"{matched} live markers in one 4 m disc overflowed the buffer");

        int items = 0;
        for (int i = 0; i < matched; i++)
        {
            int other = found[i];
            if (MathF.Abs(layout.Spawns[other].Y - centre.Y) > density.RoomHeightMetres)
            {
                continue;
            }

            items++;
            if (kinds is not null)
            {
                kinds[(int)layout.KindAt(other)]++;
            }
        }

        return items;
    }
}
