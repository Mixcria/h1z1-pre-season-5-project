using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

// The slot map generated from the August client's own datasheets by
// tools/data/gen-inventory-slots.py. Every expectation below is a row of a sheet under
// C:\Aug2017\out\data_aug, quoted in docs/41-inventory-slots.md — so a regenerated table that
// silently changes shape fails here rather than on the wire.
public sealed class InventorySlotTableTests
{
    [Fact]
    public void EquipmentSlotSheetHasItsEightySixRows()
    {
        Assert.Equal(86, EquipmentSlotTable.All.Count);
    }

    [Theory]
    [InlineData(1u, "Head")]
    [InlineData(2u, "Hands")]
    [InlineData(3u, "Chest")]
    [InlineData(4u, "Legs")]
    [InlineData(5u, "Feet")]
    [InlineData(7u, "RHand")]
    [InlineData(10u, "Backpack")]
    [InlineData(11u, "Belt")]
    [InlineData(28u, "Face")]
    [InlineData(29u, "Eyes")]
    [InlineData(100u, "ChestArmor")]
    public void BodySlotNamesMatchTheSheet(uint id, string name)
    {
        Assert.True(EquipmentSlotTable.TryGet(id, out EquipmentSlotDefinition slot));
        Assert.Equal(name, slot.SlotName);
    }

    /// <summary>
    /// docs/41 §1d, closing docs/32's open question G10: slot 1 (Head) is the only IS_REQUIRED row
    /// in the whole sheet with an empty DEFAULT_ADR, which is why clearing it crashed the client
    /// while clearing 3/4/105 merely fell back to a default mesh.
    /// </summary>
    [Fact]
    public void HeadIsTheOnlyRequiredSlotWithoutADefaultMesh()
    {
        uint[] dangerous = EquipmentSlotTable.All
            .Where(slot => slot.IsRequired && slot.DefaultAdr.Length == 0)
            .Select(slot => slot.Id)
            .ToArray();

        Assert.Equal([BodySlots.Head], dangerous);
        Assert.True(BodySlots.ClearingCrashesClient(BodySlots.Head));

        foreach (uint safe in new[] { BodySlots.Chest, BodySlots.Legs, BodySlots.Eyeballs })
        {
            Assert.True(EquipmentSlotTable.TryGet(safe, out EquipmentSlotDefinition slot));
            Assert.True(slot.IsRequired);
            Assert.NotEqual(string.Empty, slot.DefaultAdr);
            Assert.False(BodySlots.ClearingCrashesClient(safe));
        }
    }

    /// <summary>docs/41 §1b — EquipSlotItemClasses.txt, minus the non-equipment slot 85.</summary>
    [Theory]
    [InlineData(25036u, new uint[] { 76, 77, 80 })]     // shotguns / assault rifles
    [InlineData(25037u, new uint[] { 76, 77, 80 })]     // bats and axes (sheet also lists 85)
    [InlineData(4096u, new uint[] { 78, 79, 81 })]      // pistols
    [InlineData(4098u, new uint[] { 78, 79, 81 })]      // knives (sheet also lists 85)
    [InlineData(25038u, new uint[] { 82, 83, 84 })]     // bows
    [InlineData(25047u, new uint[] { 103 })]            // crossbow
    public void StowGroupsComeFromEquipSlotItemClasses(uint itemClass, uint[] expected)
    {
        Assert.Equal(expected, EquipmentSlotTable.StowSlotsForItemClass(itemClass));
    }

    /// <summary>
    /// Slot 85 (spineUpper_BackStab) is listed by EquipSlotItemClasses.txt for classes 4098 and
    /// 25037 but is IS_EQUIPMENT = 0, so it must never be chosen as a stow slot.
    /// </summary>
    [Fact]
    public void BackStabSlotIsExcludedBecauseItIsNotAnEquipmentSlot()
    {
        Assert.True(EquipmentSlotTable.TryGet(85, out EquipmentSlotDefinition backStab));
        Assert.False(backStab.IsEquipment);
        Assert.DoesNotContain(85u, EquipmentSlotTable.StowSlotsForItemClass(4098));
        Assert.DoesNotContain(85u, EquipmentSlotTable.StowSlotsForItemClass(25037));
    }

    [Fact]
    public void SurvivorLoadoutIsSeventeenAndCarriesTheKotkSlots()
    {
        Assert.Equal(17u, SurvivorLoadout.Id);
        Assert.Contains(17u, LoadoutSlotTable.LoadoutIds);

        uint[] slots = LoadoutSlotTable.Slots(SurvivorLoadout.Id).Select(s => s.SlotId).ToArray();
        Assert.Equal(
            [1, 2, 4, 5, 7, 10, 11, 12, 13, 14, 16, 25, 27, 28, 29, 38, 40, 41, 43, 45, 47, 48],
            slots);
    }

    /// <summary>docs/41 §2a — the FLAG_AUTO_EQUIP set and the six wheelable slots.</summary>
    [Fact]
    public void AutoEquipAndWheelSetsMatchTheSheet()
    {
        uint[] autoEquip = LoadoutSlotTable.Slots(SurvivorLoadout.Id)
            .Where(s => s.AutoEquip).Select(s => s.SlotId).ToArray();
        Assert.Equal([5, 7, 10, 11, 12, 13, 14, 16, 25, 27, 28, 29, 38, 43, 45, 47, 48], autoEquip);

        uint[] wheel = LoadoutSlotTable.Slots(SurvivorLoadout.Id)
            .Where(s => s.Wheelable).Select(s => s.SlotId).ToArray();
        Assert.Equal([1, 2, 4, 5, 7], wheel);
    }

    /// <summary>
    /// Loadout 17 slot 7 is the required Fists slot, ITEM_ID 85. The client's own
    /// config agrees: Inventory.SpecialEmptyHandsItemId = 85.
    /// </summary>
    [Fact]
    public void FistsSlotIsRequiredAndNamesItemEightyFive()
    {
        Assert.True(LoadoutSlotTable.TryGet(SurvivorLoadout.Id, SurvivorLoadout.Fists,
            out LoadoutSlotDefinition fists));
        Assert.True(fists.Required);
        Assert.True(fists.AutoEquip);
        Assert.Equal(PlayerInventory.SurvivorFistsItemDefinitionId, fists.ItemId);
        Assert.Equal(BodySlots.RightHand, fists.EquipSlotId);
    }

    /// <summary>
    /// docs/41 §5c — grenades (class 25078) are accepted by wheel slots 1 and 4 but not 3. That is
    /// the client's table, and the owner will see it.
    /// </summary>
    [Fact]
    public void LoadoutSeventeenAcceptsGrenadesInAllThreeWeaponSlots()
    {
        Assert.Contains(25078u, LoadoutSlotTable.ItemClasses(SurvivorLoadout.Id, SurvivorLoadout.Wheel1));
        Assert.Contains(25078u, LoadoutSlotTable.ItemClasses(SurvivorLoadout.Id, SurvivorLoadout.Wheel2));
        Assert.Contains(25078u, LoadoutSlotTable.ItemClasses(SurvivorLoadout.Id, SurvivorLoadout.Wheel3));
    }

    /// <summary>
    /// Class 16053 (ammunition, bandages, first aid kits) appears in no loadout-17
    /// slot's class set, so those items are container-only and can never auto-equip.
    /// </summary>
    [Fact]
    public void AmmunitionAndMedicalClassHasNoLoadoutSlot()
    {
        foreach (LoadoutSlotDefinition slot in LoadoutSlotTable.Slots(SurvivorLoadout.Id))
        {
            Assert.DoesNotContain(16053u, LoadoutSlotTable.ItemClasses(slot.LoadoutId, slot.SlotId));
        }
    }

    /// <summary>
    /// docs/41 §4b — container definition 117 is the only IS_DYNAMIC_BULK row in the sheet, and its
    /// own MAX_BULK is 0: its capacity is entirely subsumed.
    /// </summary>
    [Fact]
    public void OnlyTheBaseBagIsDynamicBulk()
    {
        uint[] dynamic = ContainerDefinitionTable.All
            .Where(c => c.IsDynamicBulk).Select(c => c.Id).ToArray();
        Assert.Equal([ContainerDefinitionTable.BaseInventoryDefinitionId], dynamic);

        Assert.True(ContainerDefinitionTable.TryGet(117, out ContainerDefinition bag));
        Assert.Equal(0, bag.MaxBulk);
        Assert.Equal(9999, bag.MaximumSlots);
    }

    /// <summary>docs/41 §4a — the D26 capacities, reproduced from the client's own join.</summary>
    [Theory]
    [InlineData(21u, 50)]       // shirts   (class 25002)
    [InlineData(29u, 50)]       // pants    (class 25003)
    [InlineData(45u, 300)]      // satchel
    [InlineData(22u, 1000)]     // backpack
    [InlineData(32u, 1200)]     // framed backpack
    [InlineData(28u, 2000)]     // military backpack
    public void ContainerCapacitiesReproduceD26(uint definitionId, int maxBulk)
    {
        Assert.True(ContainerDefinitionTable.TryGet(definitionId, out ContainerDefinition definition));
        Assert.Equal(maxBulk, definition.MaxBulk);
    }

    /// <summary>
    /// docs/41 §5c — the item columns the resolver reads, for the items the owner will actually
    /// pick up on Z2.
    /// </summary>
    [Theory]
    // id     class   active passive group canEquip bulk
    [InlineData(1889u, 25036u, 7u, 76u, 1u, true, 1500)]    // AR-15
    [InlineData(1374u, 25036u, 7u, 76u, 1u, true, 1500)]    // 12GA pump
    [InlineData(1702u, 4096u, 7u, 0u, 0u, true, 100)]       // M1911A1
    [InlineData(2168u, 25000u, 0u, 1u, 0u, true, 250)]      // motorcycle helmet -> Head
    [InlineData(2112u, 25004u, 0u, 10u, 0u, true, 150)]     // backpack -> Backpack
    [InlineData(2423u, 16053u, 7u, 0u, 0u, false, 1)]       // field bandage -> container only
    public void ItemFactsMatchClientItemDefinitions(
        uint id, uint itemClass, uint active, uint passive, uint group, bool canEquip, int bulk)
    {
        Assert.True(InventoryItemFacts.TryGet(id, out InventoryItemFact fact));
        Assert.Equal(itemClass, fact.ItemClass);
        Assert.Equal(active, fact.ActiveEquipSlotId);
        Assert.Equal(passive, fact.PassiveEquipSlotId);
        Assert.Equal(group, fact.PassiveEquipSlotGroupId);
        Assert.Equal(canEquip, fact.CanEquip);
        Assert.Equal(bulk, fact.Bulk);
    }

    /// <summary>
    /// docs/41 §5c — ACTIVE_EQUIP_SLOT_ID is only ever 0 or 7 (RHand) across all 2,643 rows, and
    /// FLAG_QUICK_USE is 0 on every row, so this build's hotbar is the wheelable loadout slots.
    /// </summary>
    [Fact]
    public void TheOnlyActiveEquipSlotInTheBuildIsRightHand()
    {
        Assert.Equal(2643, InventoryItemFacts.All.Count);
        uint[] active = InventoryItemFacts.All.Select(f => f.ActiveEquipSlotId).Distinct().Order().ToArray();
        Assert.Equal([0u, BodySlots.RightHand], active);
        Assert.Equal(262, InventoryItemFacts.All.Count(f => f.ActiveEquipSlotId == BodySlots.RightHand));
    }

    /// <summary>
    /// MODEL_NAME is present on only 303 of the 2,643 rows. Weapons, armour and satchels name their
    /// third-person mesh; hats, helmets and backpacks leave it empty and take theirs from the
    /// appearance path. Slot placement does not depend on it either way.
    /// </summary>
    [Fact]
    public void ModelNameIsPresentOnlyWhereTheSheetCarriesIt()
    {
        Assert.Equal(303, InventoryItemFacts.All.Count(f => f.ModelName.Length > 0));

        Assert.True(InventoryItemFacts.TryGet(1889, out InventoryItemFact ar15));
        Assert.Equal("Weapon_M16A4_3p.adr", ar15.ModelName);

        Assert.True(InventoryItemFacts.TryGet(2205, out InventoryItemFact armour));
        Assert.Equal("Survivor<gender>_Armor_Makeshift_WoodMetal.adr", armour.ModelName);

        // The motorcycle helmet and the backpack carry no MODEL_NAME at all.
        Assert.True(InventoryItemFacts.TryGet(2168, out InventoryItemFact helmet));
        Assert.Equal(string.Empty, helmet.ModelName);
        Assert.True(InventoryItemFacts.TryGet(2112, out InventoryItemFact backpack));
        Assert.Equal(string.Empty, backpack.ModelName);
    }

    /// <summary>docs/41 §4b — the base bag is item 3156, class 25068, PARAM1 117.</summary>
    [Fact]
    public void BaseBagItemIsThreeOneFiveSix()
    {
        Assert.True(InventoryItemFacts.TryGet(
            ContainerDefinitionTable.BaseInventoryItemDefinitionId, out InventoryItemFact bag));
        Assert.Equal(25068u, bag.ItemClass);
        Assert.Equal(ContainerDefinitionTable.BaseInventoryDefinitionId, bag.Param1);
        Assert.Equal(ItemCodeFactory.EquippableContainer, bag.CodeFactory);
        Assert.Contains(25068u, LoadoutSlotTable.ItemClasses(
            SurvivorLoadout.Id, SurvivorLoadout.Inventory));
    }
}
