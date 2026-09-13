using System.Numerics;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// <b>D270-D275 — the owner's loot rulings of 2026-09-03, pinned at seed 1.</b>
///
/// <para>These are whole-map assertions rather than samples: <c>LootRoster.Layout</c> is the real
/// 168,322-marker layout the server runs, built once for the suite, so every number here is the
/// number the world actually has. They exist because all five rulings are <i>quantities</i> — how
/// much is on the floor, how much of it is ammunition, how much of it is clothing, how many vests —
/// and a quantity that is not pinned is a quantity that drifts.</para>
///
/// <para>Where a figure came from is in <c>AUDIT-loot.md</c> and in docs/112.</para>
/// </summary>
public sealed class RetailLootTests
{
    /// <summary>Where the laminated vests actually stand, in marker order — D275's own output.</summary>
    private static List<Vector3> VestPositions(Z2LootLayout layout)
    {
        var vests = new List<Vector3>();
        for (int i = 0; i < layout.MarkerCount; i++)
        {
            if (layout.IsLive(i)
                && layout.KindAt(i) == LootItemKind.BodyArmor
                && layout.TryGet(i, out LootSpawnRoll roll)
                && roll.ItemDefinitionId == LaminatedArmour)
            {
                vests.Add(roll.Position);
            }
        }

        return vests;
    }

    private const uint Crossbow = 2246;
    private const uint WoodenArrow = 112;
    private const uint LaminatedArmour = 2271;

    /// <summary>
    /// Every item standing on the floor at seed 1, by kind, and the cluster boxes beside the guns.
    /// One pass, shared by the assertions below.
    /// </summary>
    private static readonly Lazy<Census> Floor = new(() =>
    {
        Z2LootLayout layout = LootRoster.Layout;
        var kinds = new Dictionary<LootItemKind, int>();
        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new Dictionary<uint, int>();
        Span<LootClusterItem> cluster = stackalloc LootClusterItem[2];
        int boxes = 0;

        for (int i = 0; i < layout.MarkerCount; i++)
        {
            if (!layout.TryGet(i, out LootSpawnRoll roll) || !layout.IsLive(i))
            {
                continue;
            }

            LootItemKind kind = layout.KindAt(i);
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            string category = layout.Spawns.Categories[layout.Spawns[i].CategoryIndex];
            categories[category] = categories.GetValueOrDefault(category) + 1;
            ids[roll.ItemDefinitionId] = ids.GetValueOrDefault(roll.ItemDefinitionId) + 1;
            boxes += layout.ClusterFor(roll, cluster);
        }

        return new Census(kinds, categories, ids, boxes);
    });

    private sealed record Census(
        Dictionary<LootItemKind, int> Kinds,
        Dictionary<string, int> Categories,
        Dictionary<uint, int> Ids,
        int Boxes)
    {
        public int Items => Kinds.Values.Sum();

        public int GroundObjects => Items + Boxes;

        public double ShareOfObjects(LootItemKind kind) =>
            Kinds.GetValueOrDefault(kind) / (double)GroundObjects;
    }

    /// <summary>
    /// <b>D270 — the floor at the ruled 0.27 density.</b> The owner's ruling replaced the weighted
    /// 0.1223 that <c>AUDIT-loot.md</c> G1 measured at 1.50 items in a 4 m room, against his own
    /// ten-room retail census of 0-6 with a mean of about 3.
    ///
    /// <para>The rule is <i>no family sits below the ruled density</i>: the three gates that were
    /// under it come up to 0.27, and the two his own tree already put above it — FirstAidKit01 0.30
    /// and Ammo01 0.60 — stay exactly where he put them, because the ruling raises the floor and
    /// does not flatten it.</para>
    /// </summary>
    [Fact]
    public void EveryFamilySitsAtOrAboveTheRuledDensityAndTheWeightedMeanIsIt()
    {
        Assert.Equal(0.27, LootDensityOptions.Weapons01SpawnChance);
        Assert.Equal(0.27, LootDensityOptions.Gear01SpawnChance);
        Assert.Equal(0.27, LootDensityOptions.Backpack01SpawnChance);
        Assert.Equal(0.30, LootDensityOptions.FirstAidKit01SpawnChance);
        Assert.Equal(0.60, LootDensityOptions.Ammo01SpawnChance);
        Assert.Equal(0.2721, LootDensityOptions.WeightedSpawnChance);

        // The two the ruling deliberately did not touch are the only two still ABOVE it.
        foreach (LootCategoryTable table in LootRoster.Tables.Categories)
        {
            if (table.Count == 0)
            {
                continue;
            }

            Assert.True(
                table.SpawnChance >= LootDensityOptions.PreWave8SpawnChance,
                $"{table.Key} is gated at {table.SpawnChance}, below the ruled "
                + $"{LootDensityOptions.PreWave8SpawnChance}");
        }
    }

    /// <summary>
    /// September 8 census after natural bandages were removed: 45,205 gated markers,
    /// 5,654 room-cap removals and 812 armour removals leave 38,739 items.
    /// </summary>
    [Fact]
    public void TheRuledDensityLays38739ItemsWithoutBandagesAtSeedOne()
    {
        Z2LootLayout layout = LootRoster.Layout;

        Assert.Equal(1ul, layout.MatchSeed);
        Assert.Equal(168_322, layout.MarkerCount);
        Assert.Equal(45_205, layout.GatedCount);
        Assert.Equal(5_654, layout.SuppressedCount);
        Assert.Equal(812, layout.Armour.Refused);
        Assert.Equal(38_739, layout.LiveCount);

        // Room caps now suppress 12.5% with the changed mix of item kinds.
        Assert.InRange(layout.SuppressedCount / (double)layout.GatedCount, 0.12, 0.13);

        // The owner's own retail census is 0-6 loose items per 4 m room, mean about 3. The client
        // packs 12.45 markers into such a room (docs/39 §4.2), so items-per-room is
        // (items / markers) x 12.45.
        double perRoom = layout.LiveCount / (double)layout.MarkerCount * 12.45;
        Assert.InRange(perRoom, 2.8, 3.1);

        Assert.Equal(layout.LiveCount, Floor.Value.Items);
    }

    /// <summary>
    /// <b>D286 (reverses D272) — two ammunition boxes per gun again.</b> The owner played the
    /// one-box floor and ruled it back to two. <c>AUDIT-loot.md</c> G2 measured the two-box world at
    /// 34.7 % of every ground object being ammunition; D286 restores it, so ammunition is about a
    /// third of the floor again and boxes outnumber weapons. The 72 client-paired <c>Ammo01</c>
    /// markers are untouched — they are marker rolls, not cluster boxes.
    /// </summary>
    [Fact]
    public void EveryGunGetsTwoBoxesAndAmmunitionIsAThirdOfTheFloorAgain()
    {
        LootTables tables = LootRoster.Tables;
        Assert.Equal(2, tables.BoxesPerGun);
        Assert.Equal(Rulings.LootGates.BoxesPerGun, tables.BoxesPerGun);

        Z2LootLayout layout = LootRoster.Layout;
        Span<LootClusterItem> two = stackalloc LootClusterItem[2];
        int clustered = 0;

        for (int i = 0; i < layout.MarkerCount && clustered < 200; i++)
        {
            if (!layout.IsLive(i)
                || layout.KindAt(i) != LootItemKind.Weapon
                || !layout.TryGet(i, out LootSpawnRoll roll)
                || !tables.TryGetCluster(roll.ItemDefinitionId, out LootClusterBox _))
            {
                continue;
            }

            // Two boxes, both of the gun's own calibre, straddling it on the marker's perpendicular.
            Assert.Equal(2, layout.ClusterFor(roll, two));
            Assert.Equal(0, two[0].BoxIndex);
            Assert.Equal(1, two[1].BoxIndex);
            Assert.Equal(roll.InstanceId, two[0].GunInstanceId);
            Assert.Equal(roll.InstanceId, two[1].GunInstanceId);
            Assert.Equal(
                tables.ClusterOffsetMetres,
                Vector3.Distance(two[0].Position, roll.Position),
                3);
            Assert.Equal(
                tables.ClusterOffsetMetres,
                Vector3.Distance(two[1].Position, roll.Position),
                3);
            clustered++;
        }

        Assert.True(clustered > 0, "no clustered gun was found on the whole map");

        Census floor = Floor.Value;

        // September 8 roster: 10,186 surviving clustered guns, each with two boxes.
        Assert.Equal(20_372, floor.Boxes);
        Assert.Equal(59_111, floor.GroundObjects);

        // Ammunition = the loose Ammo01 stacks plus every cluster box — a third of the floor again.
        double ammunition =
            (floor.Kinds.GetValueOrDefault(LootItemKind.Ammunition) + floor.Boxes)
            / (double)floor.GroundObjects;
        Assert.InRange(ammunition, 0.33, 0.36);

        // ...and boxes outnumber weapons, the two-box signature AUDIT-loot.md G2 measured.
        Assert.True(
            floor.Boxes > floor.Kinds[LootItemKind.Weapon],
            $"{floor.Boxes} boxes against {floor.Kinds[LootItemKind.Weapon]} weapons — "
            + "two boxes per gun did not restore the ammunition-heavy floor");

        // The 72 Ammo01 markers keep their own gate and their own draw, untouched by D272/D286.
        Assert.True(LootRoster.Tables.TryGet("Ammo01", out LootCategoryTable? ammo));
        Assert.Equal(0.60, ammo!.SpawnChance);
        Assert.Equal(72, ammo.SpawnerCount);
    }

    /// <summary>
    /// <b>D271 — the clothing gate.</b> The owner's note is <i>"floors covered in folded shirts and
    /// work boots"</i>; <c>AUDIT-loot.md</c> G3 measured one shirt in a typical 74-object landing
    /// ring, because <c>Gear01</c> owned 57.5 % of the markers on the lowest gate of the five.
    /// Ruling its gate to the ruled density makes its share of the FLOOR equal its share of the
    /// MARKERS, which is the number the ruling asked for.
    /// </summary>
    [Fact]
    public void ClothingTriplesAndGear01GetsItsOwnMarkerShareOfTheFloor()
    {
        Census floor = Floor.Value;

        // September 8 roster: Gear01 keeps 55.7% of live markers after room and armour caps.
        double gearShare = floor.Categories["Gear01"] / (double)floor.Items;
        Assert.InRange(gearShare, 0.55, 0.58);

        // Removing bandages raises clothing's share of Gear01's weighted draws.
        Assert.Equal(7_690, floor.Kinds[LootItemKind.Clothing]);
        Assert.InRange(floor.ShareOfObjects(LootItemKind.Clothing), 0.12, 0.14);

        // Shirts and work boots specifically — the two the owner named.
        int shirts = LootRoster.Shirts.Sum(id => floor.Ids.GetValueOrDefault(id));
        int boots = LootRoster.Boots.Sum(id => floor.Ids.GetValueOrDefault(id));
        Assert.True(shirts > 1_400, $"only {shirts} shirts on the whole map");
        Assert.True(boots > 900, $"only {boots} pairs of work boots on the whole map");
    }

    /// <summary>
    /// <b>D273 — the crossbow joins the floor roster.</b> The owner's own retail research names it
    /// live KOTK loot in this window; his Z1 roster removed it on an explicit UNPROVEN-BY-ABSENCE
    /// note, and the dated research outranks that. Its ammunition is the Wooden Arrow it already
    /// shares with the Recurve Bow, and the chamber stays at the client's own CLIP_SIZE of 1 — that
    /// is <c>ShooterCombatState.DefaultMagazineFor</c>'s number, not this table's.
    /// </summary>
    [Fact]
    public void TheCrossbowIsOnTheFloorWithArrowsBesideIt()
    {
        LootTables tables = LootRoster.Tables;
        Assert.True(tables.TryGet("Weapons01", out LootCategoryTable? weapons));
        Assert.Equal(11, weapons!.Count);
        Assert.Equal(305, weapons.TotalWeight);

        LootTableEntry crossbow = weapons.Entries.ToArray().Single(e => e.ItemDefinitionId == Crossbow);
        Assert.Equal(LootItemKind.Weapon, crossbow.Kind);
        Assert.Equal(20, crossbow.Weight);
        Assert.Equal(1u, crossbow.Count);

        // 9202 Weapon_Crossbow01_OnGround.adr, DESCRIPTION "NPC_Spawn_Weapon_Crossbow01_OnGround".
        Assert.Equal(9202u, crossbow.GroundModelId);

        // Its cluster is the bow's five-arrow quantum, not one arrow: a box of one is a prop.
        Assert.True(tables.TryGetCluster(Crossbow, out LootClusterBox box));
        Assert.Equal(WoodenArrow, box.ItemDefinitionId);
        Assert.Equal(5u, box.Count);
        Assert.Equal(9, tables.Clusters.Length);

        // And it is really on the map, in numbers a player could meet.
        Assert.True(
            Floor.Value.Ids.GetValueOrDefault(Crossbow) > 500,
            $"only {Floor.Value.Ids.GetValueOrDefault(Crossbow)} crossbows on the whole map");
    }

    /// <summary>
    /// <b>D275 — the 2017-06-29 laminated-armour rules.</b> Retail cut the vest's world spawn
    /// chance from 10 % to 5 % and gave it a 250 m anti-cluster radius and a cap of three per map
    /// square. Cranberry had a flat 18/608 share of <c>Gear01</c> and no spacing rule at all, which
    /// at the ruled density lays <b>700</b> vests. After the rules: 27.
    /// </summary>
    [Fact]
    public void TheLaminatedArmourRulesLeaveThirtySevenVestsAtLeast250MetresApart()
    {
        Z2LootLayout layout = LootRoster.Layout;
        ArmourPassResult armour = layout.Armour;

        // September 8 roster changes the draws, while chance, spacing and square limits stay fixed.
        Assert.Equal(849, armour.Rolled);
        Assert.Equal(37, armour.Kept);
        Assert.Equal(800, armour.RefusedByChance);
        Assert.Equal(9, armour.RefusedBySpacing);
        Assert.Equal(3, armour.RefusedBySquare);
        Assert.Equal(armour.Rolled, armour.Kept + armour.Refused);

        // ...and the survivors really are that far apart, checked over every pair rather than
        // trusted from the pass's own bookkeeping.
        List<Vector3> vests = VestPositions(layout);
        Assert.Equal(37, vests.Count);
        float spacing = LootDensityOptions.Default.LaminatedArmourSpacingMetres;
        for (int a = 0; a < vests.Count; a++)
        {
            for (int b = a + 1; b < vests.Count; b++)
            {
                float dx = vests[a].X - vests[b].X;
                float dz = vests[a].Z - vests[b].Z;
                float distance = MathF.Sqrt((dx * dx) + (dz * dz));
                Assert.True(
                    distance > spacing,
                    $"two vests {distance:F1} m apart, inside the {spacing} m anti-cluster radius");
            }
        }

        // No map square holds more than three.
        float square = LootDensityOptions.Default.LaminatedArmourMapSquareMetres;
        Dictionary<(int, int), int> perSquare = vests
            .GroupBy(v => ((int)MathF.Floor(v.X / square), (int)MathF.Floor(v.Z / square)))
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.True(perSquare.Values.Max() <= LootDensityOptions.Default.LaminatedArmourMaxPerSquare);
    }

    /// <summary>
    /// The per-square cap does not bind at the ruled 5 % — the 250 m radius has already spaced the
    /// vests below it — so it is proved here by driving the chance to 1.0, which is the only way to
    /// show a ceiling guard actually holds. Without this the rule would ship untested by
    /// construction.
    /// </summary>
    [Fact]
    public void ThePerSquareCapBindsWhenTheChanceIsDrivenToCertainty()
    {
        var certain = LootDensityOptions.Default with { LaminatedArmourWorldChance = 1.0 };
        Z2LootLayout layout = Z2LootLayout.Build(
            LootRoster.Spawns, LootRoster.Tables, matchSeed: 1, certain);

        ArmourPassResult armour = layout.Armour;
        Assert.Equal(0, armour.RefusedByChance);
        Assert.True(armour.RefusedBySquare > 0, "the per-square cap suppressed nothing at p = 1");
        Assert.True(armour.RefusedBySpacing > 0, "the anti-cluster radius suppressed nothing at p = 1");
        Assert.Equal(armour.Rolled, armour.Kept + armour.Refused);

        // The cap is a global ceiling: 8 x 8 squares over the +-4,096 m map, three each.
        Assert.True(armour.Kept <= 8 * 8 * LootDensityOptions.Default.LaminatedArmourMaxPerSquare);
    }

    /// <summary>
    /// The rules are a switch, and off restores the pre-D275 world exactly — 700 vests, no spacing.
    /// That is the A/B the owner gets if 24 reads as too few in play.
    /// </summary>
    [Fact]
    public void TurningTheArmourRulesOffRestoresTheFlatWeight()
    {
        var off = LootDensityOptions.Default with { LaminatedArmourRules = false };
        Z2LootLayout layout = Z2LootLayout.Build(LootRoster.Spawns, LootRoster.Tables, matchSeed: 1, off);

        Assert.Equal(default, layout.Armour);
        Assert.Equal(0, layout.Armour.Refused);
        Assert.Equal(39_551, layout.LiveCount);
    }

    /// <summary>
    /// Determinism survives all five rulings: the same seed builds the same map, vests and all, and
    /// a different seed builds a different one.
    /// </summary>
    [Fact]
    public void TheSameSeedStillReproducesTheSameMap()
    {
        Z2LootLayout again = Z2LootLayout.Build(LootRoster.Spawns, LootRoster.Tables, matchSeed: 1);
        Assert.Equal(LootRoster.Layout.LiveCount, again.LiveCount);
        Assert.Equal(LootRoster.Layout.Armour, again.Armour);

        // A different seed may coincidentally keep the same NUMBER of vests, so the assertion is
        // that it keeps DIFFERENT ONES - which is the claim the seed actually makes.
        Z2LootLayout other = Z2LootLayout.Build(LootRoster.Spawns, LootRoster.Tables, matchSeed: 2);
        Assert.NotEqual(VestPositions(LootRoster.Layout), VestPositions(other));
    }
}
