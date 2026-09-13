using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// What may be on the floor. These are the assertions that encode the owner's *"incorrect items on
/// the floor"*: the shipped tables must be exactly the KOTK-2017 retail roster, nothing outside it
/// may appear, and ammunition may not reach the ground except by its two lawful routes (docs/39
/// §2.2, §2.3, §10).
/// </summary>
public sealed class LootTableRosterTests
{
    private static List<(string Key, LootTableEntry Entry)> AllEntries()
    {
        var rows = new List<(string, LootTableEntry)>();
        foreach (LootCategoryTable table in LootRoster.Tables.Categories)
        {
            foreach (LootTableEntry entry in table.Entries)
            {
                rows.Add((table.Key, entry));
            }
        }

        return rows;
    }

    [Fact]
    public void TheShippedFileIsTheGatedFormatWithItsDensityAndClusterBlocks()
    {
        LootTables tables = LootRoster.Tables;

        Assert.Equal("Z2", tables.Zone);
        Assert.Equal(6, tables.Count);
        Assert.Equal(9, tables.Clusters.Length);
        Assert.Equal(2, tables.BoxesPerGun);

        Assert.Equal(4.0f, tables.Density.RoomRadiusMetres);
        Assert.Equal(2.0f, tables.Density.RoomHeightMetres);
        Assert.Equal(6, tables.Density.MaxItemsPerRoom);
        Assert.Equal(2, tables.Density.MaxWeaponsPerRoom);
        Assert.True(tables.Density.SingletonKinds.Contains(LootItemKind.Backpack));
        Assert.True(tables.Density.SingletonKinds.Contains(LootItemKind.BodyArmor));
        Assert.True(tables.Density.SingletonKinds.Contains(LootItemKind.Helmet));
        Assert.False(tables.Density.SingletonKinds.Contains(LootItemKind.Weapon));

        Assert.Equal(0.5f, tables.ClusterOffsetMetres);
        Assert.Equal(0.35f, tables.ClusterSecondBoxYawOffset, 5);
    }

    /// <summary>
    /// A v1 file has no spawn gate, and reading one leniently would silently mean "every marker
    /// spawns" — the exact defect the gate exists to fix. It must be rejected, loudly.
    /// </summary>
    [Fact]
    public void AGatelessFileIsRejectedRatherThanReadAsAlwaysSpawning()
    {
        const string v1 = """
            {"format":"cranberry.loot-tables","formatVersion":1,"zone":"Z2","categories":[]}
            """;
        Assert.Throws<InvalidDataException>(
            () => LootTables.Parse(System.Text.Encoding.UTF8.GetBytes(v1), "v1"));

        const string gateless = """
            {"format":"cranberry.loot-tables","formatVersion":2,"zone":"Z2",
             "density":{"roomRadiusMetres":4,"roomHeightMetres":2,"maxItemsPerRoom":6,
                        "maxWeaponsPerRoom":2,"singletonKinds":["Backpack"]},
             "categories":[{"key":"Gear01","spawnerCount":1,"entries":[
               {"itemDefinitionId":2423,"nameId":12302,"groundModelId":9066,"count":1,
                "weight":1,"kind":"Medical"}]}]}
            """;
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => LootTables.Parse(System.Text.Encoding.UTF8.GetBytes(gateless), "gateless"));
        Assert.Contains("spawnChance", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Roster closure: nothing on any table, or in the cluster pairs, is off-roster.</summary>
    [Fact]
    public void NoItemOutsideTheRetailRosterCanReachTheFloor()
    {
        foreach ((string key, LootTableEntry entry) in AllEntries())
        {
            Assert.True(
                LootRoster.All.Contains(entry.ItemDefinitionId),
                $"{key} rolls item {entry.ItemDefinitionId} ({entry.Name}), which is not one of the "
                + $"{LootRoster.All.Count} roster ids");
        }

        foreach (LootClusterBox box in LootRoster.Tables.Clusters)
        {
            Assert.Contains(box.WeaponItemDefinitionId, LootRoster.All);
            Assert.Contains(box.ItemDefinitionId, LootRoster.All);
        }
    }

    /// <summary>…and the other direction: every roster id is actually reachable.</summary>
    [Fact]
    public void EveryRosterIdIsReachableFromSomeTableOrCluster()
    {
        HashSet<uint> reachable = [.. AllEntries().Select(x => x.Entry.ItemDefinitionId)];
        foreach (LootClusterBox box in LootRoster.Tables.Clusters)
        {
            reachable.Add(box.ItemDefinitionId);
        }

        uint[] missing = [.. LootRoster.All.Except(reachable).Order()];
        Assert.True(missing.Length == 0, $"roster ids that can never spawn: {string.Join(", ", missing)}");
        // D273 added the Crossbow; the September 8 play-test removed natural bandages.
        Assert.Equal(72, LootRoster.All.Count);
    }

    /// <summary>
    /// The deny list. Eighteen ids were measured on the floor and removed (docs/39 §2.2); a future
    /// edit has to delete this test to bring any of them back.
    /// </summary>
    [Fact]
    public void TheEighteenRemovedItemsAppearInNoTable()
    {
        HashSet<uint> shipped = [.. AllEntries().Select(x => x.Entry.ItemDefinitionId)];
        foreach (LootClusterBox box in LootRoster.Tables.Clusters)
        {
            shipped.Add(box.ItemDefinitionId);
            shipped.Add(box.WeaponItemDefinitionId);
        }

        foreach ((uint id, string why) in LootRoster.Excluded)
        {
            Assert.False(shipped.Contains(id), $"item {id} is back on the floor: {why}");
        }
    }

    /// <summary>
    /// docs/39 §2.3, pinned. Ammunition was 56.5 % of the map's biggest table and that is what the
    /// owner saw as *"bullets here and there"*. It now reaches the ground from <c>Ammo01</c>'s 72
    /// markers and from the boxes beside a gun, and from nowhere else.
    /// </summary>
    [Fact]
    public void NeitherTheGearNorTheWeaponTableCarriesAnyAmmunition()
    {
        foreach (string key in (string[])["Gear01", "Weapons01", "Backpack01", "FirstAidKit01"])
        {
            Assert.True(LootRoster.Tables.TryGet(key, out LootCategoryTable? table));
            foreach (LootTableEntry entry in table!.Entries)
            {
                Assert.NotEqual(LootItemKind.Ammunition, entry.Kind);
                Assert.DoesNotContain(entry.ItemDefinitionId, LootRoster.Ammunition);
                Assert.NotEqual(LootRoster.WoodenArrow, entry.ItemDefinitionId);
            }
        }

        Assert.True(LootRoster.Tables.TryGet("Ammo01", out LootCategoryTable? ammo));
        Assert.All(ammo!.Entries.ToArray(), e => Assert.Equal(LootItemKind.Ammunition, e.Kind));
    }

    /// <summary>Per-category item sets, exactly — membership is the whole of the "wrong items" fix.</summary>
    [Theory]
    [InlineData("Weapons01", 305)]
    [InlineData("Gear01", 498)]
    [InlineData("Backpack01", 100)]
    [InlineData("FirstAidKit01", 40)]
    [InlineData("Ammo01", 120)]
    public void EachCategoryHoldsExactlyItsOwnItemSet(string key, int expectedWeight)
    {
        Assert.True(LootRoster.Tables.TryGet(key, out LootCategoryTable? table));
        Assert.Equal(expectedWeight, table!.TotalWeight);

        uint[] actual = [.. table.Entries.ToArray().Select(e => e.ItemDefinitionId).Order()];
        uint[] expected = key switch
        {
            "Weapons01" => [.. LootRoster.Weapons.Order()],
            "Gear01" =>
            [
                .. new[]
                {
                    LootRoster.TacticalFirstAidKit, LootRoster.DuctTape,
                    LootRoster.WaistPack, LootRoster.LaminatedTacticalBodyArmor,
                }
                .Concat(LootRoster.Shirts).Concat(LootRoster.Trousers).Concat(LootRoster.Caps)
                .Concat(LootRoster.Beanies).Concat(LootRoster.Helmets).Concat(LootRoster.Sneakers)
                .Concat(LootRoster.Boots).Concat(LootRoster.Throwables)
                .Concat(LootRoster.CivilianBackpacks).Order(),
            ],
            "Backpack01" => [.. LootRoster.CivilianBackpacks.Append(LootRoster.TanMilitaryBackpack).Order()],
            "FirstAidKit01" => [LootRoster.TacticalFirstAidKit],
            "Ammo01" => [.. LootRoster.Ammunition.Order()],
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The owner's roster is also the set that auto-equips (docs/39 §7): every weapon, garment,
    /// bag and vest on the floor carries a real <c>PASSIVE_EQUIP_SLOT_ID</c>, which is what the
    /// inventory lane needs to put a pickup in the right slot. The two exceptions are client facts:
    /// the Combat Knife has no back slot in this build, and consumables have no slot at all.
    /// </summary>
    [Fact]
    public void EveryEquippableRosterItemCarriesItsPassiveSlot()
    {
        foreach ((string key, LootTableEntry entry) in AllEntries())
        {
            bool equippable = entry.Kind is LootItemKind.Clothing or LootItemKind.Helmet
                or LootItemKind.BodyArmor or LootItemKind.Backpack
                || (entry.Kind == LootItemKind.Weapon && entry.ItemDefinitionId != 84);

            if (equippable)
            {
                Assert.True(
                    entry.PassiveEquipSlotId != 0,
                    $"{key} item {entry.ItemDefinitionId} ({entry.Name}) has no passive equip slot, "
                    + "so a pickup could never auto-equip");
            }
        }

        Assert.True(LootRoster.Tables.TryGet("Weapons01", out LootCategoryTable? weapons));
        Dictionary<uint, uint> slots = weapons!.Entries.ToArray()
            .ToDictionary(e => e.ItemDefinitionId, e => e.PassiveEquipSlotId);

        Assert.Equal(76u, slots[10]);      // AR-15, R_LongWeapon_1
        Assert.Equal(76u, slots[2229]);    // AK-47
        Assert.Equal(76u, slots[1374]);    // 12GA Pump Shotgun
        Assert.Equal(78u, slots[2]);       // M1911A1, R_ShortWeapon_1 — the roster row, not 1702
        Assert.Equal(78u, slots[1997]);    // M9
        Assert.Equal(78u, slots[1991]);    // R380
        Assert.Equal(78u, slots[1718]);    // .44 Magnum
        Assert.Equal(82u, slots[1986]);    // Recurve Bow, R_bowWeapon_1
        Assert.Equal(106u, slots[83]);     // Machete — the only row in the sheet on slot 106
        Assert.Equal(0u, slots[84]);       // Combat Knife — this build gives it no back slot
    }

    /// <summary>
    /// Every entry names a real ground actor, a real stack size and a plausible name id. The two
    /// pinned model ids are the ones a duplicate <c>MODEL_FILE_NAME</c> would otherwise decide by
    /// tie-break (docs/39 §3.1).
    /// </summary>
    [Fact]
    public void EveryEntryCarriesTheClientIdsTheWireNeeds()
    {
        foreach ((string key, LootTableEntry entry) in AllEntries())
        {
            Assert.True(entry.ItemDefinitionId != 0, $"{key} has an entry with no item id");
            Assert.True(entry.GroundModelId != 0, $"{key} item {entry.ItemDefinitionId} has no ground model");
            Assert.True(entry.NameId != 0, $"{key} item {entry.ItemDefinitionId} has no name id");
            Assert.True(entry.ItemClass != 0, $"{key} item {entry.ItemDefinitionId} has no item class");
            Assert.True(entry.Count >= 1);
            Assert.NotEqual(LootItemKind.Unknown, entry.Kind);
            Assert.True(entry.Weight > 0);
        }

        Assert.True(LootRoster.Tables.TryGet("Weapons01", out LootCategoryTable? weapons));
        LootTableEntry bow = weapons!.Entries.ToArray().Single(e => e.ItemDefinitionId == 1986);
        Assert.Equal(9420u, bow.GroundModelId);

        Assert.True(LootRoster.Tables.TryGet("Gear01", out LootCategoryTable? gear));
        LootTableEntry helmet = gear!.Entries.ToArray().Single(e => e.ItemDefinitionId == 2172);
        Assert.Equal(9418u, helmet.GroundModelId);
    }

    /// <summary>
    /// The cluster table (docs/39 §5): eight guns, each paired with one magazine of its own
    /// calibre. Melee gets none, and the box counts are magazine sizes rather than a range.
    /// </summary>
    [Fact]
    public void EveryFirearmAndTheBowCarryOneMagazineBox()
    {
        LootTables tables = LootRoster.Tables;

        (uint Weapon, uint Ammo, uint Count)[] expected =
        [
            (10, 1429, 30),     // AR-15    → .223, one magazine
            (2229, 2325, 30),   // AK-47    → 7.62x39
            (1997, 1998, 15),   // M9       → 9 mm
            (2, 1428, 7),       // M1911A1  → .45
            (1991, 1992, 7),    // R380     → .380
            (1718, 1719, 6),    // .44      → full cylinder
            (1374, 1511, 6),    // shotgun  → full tube
            (1986, 112, 5),     // bow      → five arrows
            (2246, 112, 5),     // crossbow → five arrows (D273)
        ];

        foreach ((uint weapon, uint ammo, uint count) in expected)
        {
            Assert.True(tables.TryGetCluster(weapon, out LootClusterBox box), $"weapon {weapon} has no cluster");
            Assert.Equal(ammo, box.ItemDefinitionId);
            Assert.Equal(count, box.Count);
            Assert.True(box.GroundModelId != 0);
            Assert.True(box.NameId != 0);
        }

        Assert.False(tables.TryGetCluster(83, out _), "the Machete must not be clustered");
        Assert.False(tables.TryGetCluster(84, out _), "the Combat Knife must not be clustered");
        Assert.False(tables.TryGetCluster(LootRoster.FieldBandage, out _));
        Assert.Equal(expected.Length, tables.Clusters.Length);
    }
}
