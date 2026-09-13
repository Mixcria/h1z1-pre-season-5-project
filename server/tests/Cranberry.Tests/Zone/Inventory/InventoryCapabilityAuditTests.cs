using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Inventory;

// docs/63 - the wave-6 audit. Where the other files in this folder pin individual decisions, these
// tests sweep the WHOLE of the client's own tables and assert the server can never disagree with
// them. They are the "make sure the inventory is 100%" acceptance in executable form.
public sealed class InventoryCapabilityAuditTests
{
    private static PlayerInventory Fresh(InventoryOptions? options = null)
    {
        ulong next = 0x3100_0000_0000_0001;
        var inventory = new PlayerInventory(
            characterGuid: 4099,
            guidAllocator: () => next++,
            options: options ?? new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        return inventory;
    }

    // ---------------------------------------------------------------------------------------
    // 1. Every equipment slot maps correctly - EquipmentSlotDefinitions.txt, 86 rows
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheBodySlotTableIsTheWholeSheetAndSlotOneIsStillTheOnlyFatalOne()
    {
        Assert.Equal(86, EquipmentSlotTable.All.Count);

        // docs/41 §1d: the only IS_REQUIRED row with an empty DEFAULT_ADR. Clearing 3, 4 or 105
        // falls back to a default mesh; clearing 1 leaves a required slot with nothing at all,
        // which is the crash-vs-hang split docs/32 measured.
        List<uint> fatal = [.. EquipmentSlotTable.All
            .Where(slot => BodySlots.ClearingCrashesClient(slot.Id))
            .Select(slot => slot.Id)];
        Assert.Equal([BodySlots.Head], fatal);

        foreach (uint required in new[] { BodySlots.Chest, BodySlots.Legs, BodySlots.Eyeballs })
        {
            Assert.True(EquipmentSlotTable.TryGet(required, out EquipmentSlotDefinition slot));
            Assert.True(slot.IsRequired);
            Assert.NotEqual(string.Empty, slot.DefaultAdr);
        }
    }

    [Fact]
    public void EveryNamedBodySlotIsTheSheetRowItClaimsToBe()
    {
        foreach ((uint id, string name) in new (uint, string)[]
        {
            (BodySlots.Head, "Head"),
            (BodySlots.Hands, "Hands"),
            (BodySlots.Chest, "Chest"),
            (BodySlots.Legs, "Legs"),
            (BodySlots.Feet, "Feet"),
            (BodySlots.RightHand, "RHand"),
            (BodySlots.Backpack, "Backpack"),
            (BodySlots.Belt, "Belt"),
            (BodySlots.Face, "Face"),
            (BodySlots.Eyes, "Eyes"),
            (BodySlots.ChestArmor, "ChestArmor"),
            (BodySlots.Jacket, "Jacket"),
            (BodySlots.Hair, "Hair"),
        })
        {
            Assert.True(EquipmentSlotTable.TryGet(id, out EquipmentSlotDefinition slot), $"slot {id}");
            Assert.Equal(name, slot.SlotName);
        }
    }

    [Fact]
    public void EveryStowSlotTheResolverCanPickIsAnAttachableSlot()
    {
        // EquipSlotItemClasses also lists slot 85 (spineUpper_BackStab) for classes 4098 and 25037,
        // but 85 is IS_EQUIPMENT = 0 - the client will not attach a mesh there, so it must never be
        // a candidate (docs/41 §1b). The generator drops it; this asserts the property, not the row.
        foreach (uint itemClass in new uint[] { 4096, 4098, 25036, 25037, 25038, 25047 })
        {
            IReadOnlyList<uint> slots = EquipmentSlotTable.StowSlotsForItemClass(itemClass);
            Assert.NotEmpty(slots);
            foreach (uint slotId in slots)
            {
                Assert.True(EquipmentSlotTable.TryGet(slotId, out EquipmentSlotDefinition slot));
                Assert.True(slot.IsEquipment, $"class {itemClass} may not stow on non-equipment slot {slotId}");
                Assert.NotEqual(BodySlots.RightHand, slotId);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // 2. Auto-assign agrees with the client's own predicate, for all 2,643 rows
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void NoItemInTheBuildIsEverPlacedInASlotTheClientWouldReject()
    {
        // FUN_140d35510: a loadout slot supports an item iff the item's CLASS SET intersects
        // LoadoutSlotItemClasses(loadoutId, slotId). If the server ever binds outside that set, the
        // panel and the server disagree about where the item is - which is the whole failure mode
        // docs/41 was written for. Swept over every row of ClientItemDefinitions.
        //
        // The item's class set is ITEM_CLASS *union* ItemClassMappings, not ITEM_CLASS alone. Check
        // every resulting placement against the active August survivor loadout (17).
        int placed = 0;
        foreach (InventoryItemFact fact in InventoryItemFacts.All)
        {
            PlayerInventory inventory = Fresh();
            InventoryPlacement plan = inventory.Plan(fact.DefinitionId);
            if (plan.Kind != InventoryPlacementKind.LoadoutSlot)
            {
                continue;
            }

            placed++;
            Assert.True(
                InventoryAutoAssign.Accepts(
                    SurvivorLoadout.Id, plan.LoadoutSlotId, fact.DefinitionId, fact.ItemClass),
                $"item {fact.DefinitionId} (ITEM_CLASS {fact.ItemClass}) was bound to loadout slot "
                + $"{plan.LoadoutSlotId}, which accepts "
                + $"[{string.Join(", ", LoadoutSlotTable.ItemClasses(SurvivorLoadout.Id, plan.LoadoutSlotId))}] "
                + $"and none of its ItemClassMappings rows "
                + $"[{string.Join(", ", ItemClassMappings.For(fact.DefinitionId).ToArray())}]");
            Assert.NotEqual(SurvivorLoadout.Fists, plan.LoadoutSlotId);
            Assert.NotEqual(SurvivorLoadout.Inventory, plan.LoadoutSlotId);
        }

        // A sanity floor so an accidental "nothing is ever equippable" regression cannot pass this.
        Assert.True(placed > 400, $"only {placed} of {InventoryItemFacts.All.Count} rows equip");
    }

    [Fact]
    public void NoItemInTheBuildEverProducesAnActiveHandRow()
    {
        // REGRESSION GUARD 5, swept. WieldFirstWeapon is off by default and body slot 7 is the only
        // ACTIVE_EQUIP_SLOT_ID any of the 262 wieldable rows carries, so with the default options no
        // item in the build may resolve to an equipment slot of 7. The weapons lane owns the only
        // sanctioned narrowing of that guard this wave; this lane leaves slot 7 alone entirely.
        foreach (InventoryItemFact fact in InventoryItemFacts.All)
        {
            PlayerInventory inventory = Fresh();
            InventoryPlacement plan = inventory.Plan(fact.DefinitionId);
            Assert.NotEqual(BodySlots.RightHand, plan.EquipmentSlotId);
            Assert.False(plan.Wielded);
        }
    }

    [Fact]
    public void EveryApparelClassLandsOnItsOwnAutoEquipSlotAndItsOwnBodySlot()
    {
        // The FLAG_AUTO_EQUIP loadout slots of loadout 17 and the class each of them accepts, read
        // straight off LoadoutSlots.txt / LoadoutSlotItemClasses.txt (docs/41 §2a).
        foreach ((uint itemClass, uint loadoutSlot, uint bodySlot) in new (uint, uint, uint)[]
        {
            (25000u, SurvivorLoadout.Head, BodySlots.Head),
            (25002u, SurvivorLoadout.Chest, BodySlots.Chest),
            (25003u, SurvivorLoadout.Legs, BodySlots.Legs),
            (25004u, SurvivorLoadout.Backpack, BodySlots.Backpack),
            (25005u, SurvivorLoadout.Feet, BodySlots.Feet),
            (25008u, SurvivorLoadout.Gloves, BodySlots.Hands),
            (25013u, SurvivorLoadout.Belt, BodySlots.Belt),
            (25040u, SurvivorLoadout.Face, BodySlots.Face),
            (25041u, SurvivorLoadout.ChestArmor, BodySlots.ChestArmor),
            (25045u, SurvivorLoadout.EyeWear, BodySlots.Eyes),
        })
        {
            InventoryItemFact fact = InventoryItemFacts.All.First(row =>
                row.ItemClass == itemClass && row.CanEquip && row.PassiveEquipSlotId == bodySlot);
            PlayerInventory inventory = Fresh();
            InventoryPlacement plan = inventory.Plan(fact.DefinitionId);

            Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
            Assert.Equal(loadoutSlot, plan.LoadoutSlotId);
            Assert.Equal(bodySlot, plan.EquipmentSlotId);
        }
    }

    [Fact]
    public void TheHotbarMatchesLoadoutSeventeensInputActions()
    {
        Assert.Equal(
            [SurvivorLoadout.Wheel1, SurvivorLoadout.Wheel2, SurvivorLoadout.Wheel3,
                SurvivorLoadout.Binoculars, SurvivorLoadout.QuickUse1, SurvivorLoadout.QuickUse2],
            SurvivorLoadout.WheelOrder.ToArray());

        Assert.Equal("Slot1", LoadoutSlotTable.Slots(SurvivorLoadout.Id)
            .Single(slot => slot.SlotId == SurvivorLoadout.Wheel1).SlotInputAction);
        Assert.Equal("Slot2", LoadoutSlotTable.Slots(SurvivorLoadout.Id)
            .Single(slot => slot.SlotId == SurvivorLoadout.Wheel2).SlotInputAction);
        Assert.Equal("Slot3", LoadoutSlotTable.Slots(SurvivorLoadout.Id)
            .Single(slot => slot.SlotId == SurvivorLoadout.Wheel3).SlotInputAction);
        Assert.Equal("Slot4", LoadoutSlotTable.Slots(SurvivorLoadout.Id)
            .Single(slot => slot.SlotId == SurvivorLoadout.Fists).SlotInputAction);
        Assert.Equal("Slot5", LoadoutSlotTable.Slots(SurvivorLoadout.Id)
            .Single(slot => slot.SlotId == SurvivorLoadout.Binoculars).SlotInputAction);
    }

    [Fact]
    public void ThreeRiflesFillTheThreeWheelBoxesAndThenGoInTheBag()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            StarterOutfit = [],
            BaseCarryBulk = 10_000,      // so the fourth rifle is refused for slots, not for bulk
        });

        foreach (uint expected in new[] { SurvivorLoadout.Wheel1, SurvivorLoadout.Wheel2, SurvivorLoadout.Wheel3 })
        {
            InventoryPlacement plan = inventory.TryPickUp(2229, 1, out _);
            Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
            Assert.Equal(expected, plan.LoadoutSlotId);
        }

        // RULE 4: a fourth rifle is carried, never displacing one of the three (docs/41 §5b).
        InventoryPlacement fourth = inventory.TryPickUp(2229, 1, out _);
        Assert.Equal(InventoryPlacementKind.Container, fourth.Kind);
        Assert.Equal(3, inventory.LoadoutSlots.Keys.Count(slot =>
            slot is SurvivorLoadout.Wheel1 or SurvivorLoadout.Wheel2 or SurvivorLoadout.Wheel3));
    }

    [Fact]
    public void TheThreeRiflesTakeThreeDifferentStowSlotsRatherThanColliding()
    {
        // PASSIVE_EQUIP_SLOT_GROUP_ID = 1 on all 82 long guns: pick a FREE slot of the group that
        // accepts the class - 25036 -> {76, 77, 80} (docs/41 §1b).
        PlayerInventory inventory = Fresh(new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 10_000 });
        List<uint> bodySlots = [];
        for (int i = 0; i < 3; i++)
        {
            bodySlots.Add(inventory.TryPickUp(2229, 1, out _).EquipmentSlotId);
        }

        Assert.Equal([76u, 77u, 80u], bodySlots.Order());
        // Bootstrap selects Fists without creating an RHand body binding. The collision property
        // under test is that the three rifles still consume the three distinct passive long-gun
        // slots.
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
        Assert.Equal(3, inventory.EquipmentSlots.Count);
    }

    [Fact]
    public void LegacyUtilityItemsGoInTheBagWhileBinocularsOwnKeyFive()
    {
        // Loadout-3 slots 40 (slot5) and 41 (slot6) accept ITEM_CLASS 25054 and nothing else. Before
        // this wave the resolver only ever tried 1, 3 and 4, so a 25054 item fell through every
        // wheel rule into the grid - a slot the client's own table says it does not belong in.
        InventoryItemFact utility = InventoryItemFacts.All
            .First(row => row.ItemClass == 25054 && row.CanEquip);

        PlayerInventory inventory = Fresh();
        InventoryPlacement placement = inventory.TryPickUp(utility.DefinitionId, 1, out _);
        Assert.Equal(InventoryPlacementKind.Container, placement.Kind);
        Assert.Equal(
            PlayerInventory.SurvivorBinocularsItemDefinitionId,
            inventory.LoadoutSlots[SurvivorLoadout.Binoculars].DefinitionId);
    }

    // ---------------------------------------------------------------------------------------
    // 3. Bulk - D26's model, and the swap that used to slip past it
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheD26CapacityLadderIsTheClientsOwnParamOneJoin()
    {
        // base 100 + shirt 50 + pants 50 + satchel 300 / backpack 1000 / framed 1200 / military 2000
        foreach ((uint definitionId, int contributed) in new (uint, int)[]
        {
            (2127u, 50),      // shirt,    PARAM1 21
            (2173u, 50),      // pants,    PARAM1 29
            (2125u, 300),     // satchel,  PARAM1 45
            (2112u, 1000),    // backpack, PARAM1 22
            (2111u, 1200),    // framed,   PARAM1 32
            (2124u, 2000),    // military, PARAM1 28
        })
        {
            PlayerInventory inventory = Fresh();
            Assert.Equal(100, inventory.Capacity.Max);
            inventory.TryPickUp(definitionId, 1, out _);
            Assert.Equal(100 + contributed, inventory.Capacity.Max);
            Assert.Single(inventory.Containers);     // one server-owned cargo bag
            Assert.Equal(2, inventory.ToInitContainers().Containers.Count); // bag + provider
        }
    }

    [Fact]
    public void AWornBackpackRaisesTheExistingBagAndReplacesItsProtocolRecord()
    {
        PlayerInventory inventory = Fresh();
        ulong bagGuid = inventory.BaseBag!.Guid;

        inventory.TryPickUp(2112, 1, out _);          // backpack, +1000
        inventory.TryPickUp(2124, 1, out _);          // military backpack, swaps in, +2000

        Assert.Single(inventory.Containers);
        Assert.Equal(bagGuid, inventory.BaseBag!.Guid);
        Assert.Equal(2, inventory.ToInitContainers().Containers.Count);
        Assert.Equal(2100, inventory.Capacity.Max);
    }

    [Fact]
    public void PickingUpASmallerBackpackKeepsTheWornCapacityAndCarriesTheSpare()
    {
        // A pickup is not an explicit equip request. Keeping the military bag allows a spare
        // small backpack to fit without reducing the capacity or displacing the equipped pack.
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(2124, 1, out var military);         // military: 100 + 2000 = 2100
        Assert.Equal(2100, inventory.Capacity.Max);

        // This fits the spare's 150 bulk but would fail an unwanted downgrade and displacement.
        while (inventory.Capacity.Used + 2 <= 1900)
        {
            inventory.TryPickUp(1429, 1, out _);                 // 7.62mm, BULK 2
        }

        int before = inventory.Capacity.Used;
        InventoryPlacement plan = inventory.TryPickUp(2112, 1, out var spare);

        Assert.Equal(InventoryPlacementKind.Container, plan.Kind);
        Assert.Equal(ContainerErrorCode.None, plan.Error);
        Assert.Same(military, inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Equal(inventory.BaseBag!.Guid, spare!.ContainerGuid);
        Assert.Equal(before + 150, inventory.Capacity.Used);
        Assert.Equal(2100, inventory.Capacity.Max);
    }

    [Fact]
    public void AnOverCapacityPickupIsRefusedAndTheModelIsUntouched()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            StarterOutfit = [],
            QuickUseConsumables = false,
        });
        int items = inventory.Items.Count;

        InventoryPlacement plan = inventory.TryPickUp(2424, 5, out InventoryItemInstance? kits);

        Assert.Equal(InventoryPlacementKind.Refused, plan.Kind);   // 5 x BULK 25 = 125 > 100
        Assert.Null(kits);
        Assert.Equal(items, inventory.Items.Count);
        Assert.Empty(inventory.BaseBag!.Slots);
    }

    // ---------------------------------------------------------------------------------------
    // 4. Stacking
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void StackPolicyPreservesAmmoAndAddsBandagesWithoutEditingTheAugustFacts()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            StarterOutfit = [],
            BaseCarryBulk = 10_000,
            QuickUseConsumables = false,
        });

        // 7.62mm: MAX_STACK_SIZE 9999.
        InventoryPlacement first = inventory.TryPickUp(1429, 30, out InventoryItemInstance? ammo);
        Assert.Equal(InventoryPlacementKind.Container, first.Kind);
        InventoryPlacement merge = inventory.TryPickUp(1429, 30, out InventoryItemInstance? merged);
        Assert.Equal(InventoryPlacementKind.Stack, merge.Kind);
        Assert.Same(ammo, merged);
        Assert.Equal(60u, merged!.Count);
        Assert.Single(inventory.BaseBag!.Slots);

        // Keep the raw fact while applying the requested bandage stack policy.
        Assert.Equal(1, InventoryItemFacts.All.First(row => row.DefinitionId == 2423).MaxStackSize);
        inventory.TryPickUp(2423, 1, out _);
        InventoryPlacement second = inventory.TryPickUp(2423, 1, out _);
        Assert.Equal(InventoryPlacementKind.Stack, second.Kind);
        Assert.Equal(2, inventory.BaseBag.Slots.Count);
    }

    [Fact]
    public void AMergeIsChargedTheSameBulkAsANewCell()
    {
        PlayerInventory inventory = Fresh();               // 100 bulk
        inventory.TryPickUp(1429, 30, out _);              // 30 x BULK 2 = 60
        Assert.Equal(60, inventory.Capacity.Used);

        // A merge of another 30 would cost 60 more and the bag holds 100: refused, not silently
        // merged past the maximum.
        InventoryPlacement plan = inventory.TryPickUp(1429, 30, out _);
        Assert.Equal(InventoryPlacementKind.Refused, plan.Kind);
        Assert.Equal(60, inventory.Capacity.Used);
    }

    // ---------------------------------------------------------------------------------------
    // 5. Swap - the displaced item, and what the caller has to re-send for it
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void PickingUpASpareHelmetCarriesItWithoutReplacingTheWornHelmet()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 10_000 });
        inventory.TryPickUp(2168, 1, out InventoryItemInstance? first);
        InventoryPlacement plan = inventory.TryPickUp(2169, 1, out InventoryItemInstance? second);

        Assert.Equal(InventoryPlacementKind.Container, plan.Kind);
        Assert.Equal(0ul, plan.DisplacedItemGuid);
        Assert.Same(first, inventory.LoadoutSlots[SurvivorLoadout.Head]);
        Assert.Equal(0u, second!.LoadoutSlotId);
        Assert.Equal(inventory.BaseBag!.Guid, second.ContainerGuid);
        Assert.Equal(inventory.BaseBag.Guid, second.ToRecord(inventory.CharacterGuid).ContainerGuid);
    }

    // ---------------------------------------------------------------------------------------
    // 6. Drop - everything the player can pick up, the player can put back
    // ---------------------------------------------------------------------------------------

    private static readonly Lazy<LootTables> Tables = new(LootTables.LoadDefault);

    [Fact]
    public void EveryItemTheFloorCanProduceCanBePutBackOnIt()
    {
        // The drop can only spawn an item the loot tables know a ground actor for (Models.txt row
        // ids are hand-resolved by tools/data/gen-loot-tables.py, docs/39 D4). That set is exactly
        // "everything the player could have picked up", which is the closure that matters: no item
        // can enter the inventory off the floor and then be undroppable.
        var catalogue = new DroppedItemCatalogue(Tables.Value);
        Assert.True(catalogue.Count > 60, $"only {catalogue.Count} droppable items");

        foreach (LootCategoryTable category in Tables.Value.Categories)
        {
            foreach (LootTableEntry entry in category.Entries)
            {
                Assert.True(
                    catalogue.TryGet(entry.ItemDefinitionId, out uint model, out _),
                    $"item {entry.ItemDefinitionId} ({entry.Name}) has no ground actor");
                Assert.Equal(entry.GroundModelId, model);
            }
        }

        foreach (LootClusterBox box in Tables.Value.Clusters)
        {
            Assert.True(catalogue.TryGet(box.ItemDefinitionId, out _, out _), $"ammo {box.ItemDefinitionId}");
        }
    }

    [Fact]
    public void AnItemWithNoGroundActorIsNotSilentlyDroppable()
    {
        // These starter garments have no ground actors. Blue Jeans 2177 do have an
        // August ground actor, so the basic outfit correction must retain that distinction.
        var catalogue = new DroppedItemCatalogue(Tables.Value);
        foreach (uint garment in new[] { SurvivorStarterOutfit.Gloves, SurvivorStarterOutfit.Shirt, SurvivorStarterOutfit.Boots })
        {
            Assert.False(catalogue.TryGet(garment, out _, out _), $"garment {garment}");
        }
        Assert.True(catalogue.TryGet(SurvivorStarterOutfit.Pants, out _, out _));
    }
}
