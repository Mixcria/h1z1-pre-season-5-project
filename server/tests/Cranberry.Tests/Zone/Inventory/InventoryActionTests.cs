using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

// The server half of the client's inventory context menu - docs/63 §2. "Dropping an item returns it
// to the world" is the owner's own line item this wave, and it was the one inventory verb with no
// server code at all: the client asked 21 times across four captures and Cranberry logged every one
// as "(unanswered)".
public sealed class InventoryActionTests
{
    private const uint MotorcycleHelmet = 2168;
    private const uint MilitaryBackpack = 2124;
    private const uint Backpack = 2112;
    private const uint Ammo762 = 1429;
    private const uint FieldBandage = 2423;
    private const uint Hoodie = 3405;

    private const uint DropItemOption = 4;

    // The Field Bandage's own ConsumeItem row: ItemIdUseOptionGroupId puts item 2423 in group 64,
    // whose options are 4, 12, 59, 61 and 95 - and 95 is ConsumeItem with BUSY_MSEC 3000, the
    // three-second bandage animation. 99 is the five-second ConsumeItem of a different group; it is
    // in the captures, but not for this item.
    private const uint ConsumeItemOption = 95;
    private const uint SalvageItemOption = 63;
    private const uint HoodieUpOption = 96;
    private const uint HoodieDownOption = 97;

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

    private static RequestUseItem Request(PlayerInventory inventory, ulong itemGuid, uint option, uint count = 0) =>
        new(
            UnknownA: 1,
            ItemUseOptionId: option,
            CharacterGuid: inventory.CharacterGuid,
            SourceCharacterGuid: inventory.CharacterGuid,
            TargetCharacterGuid: inventory.CharacterGuid,
            ItemGuid: itemGuid,
            Simple: count == 0,
            Count: count,
            TrailingBytes: 0);

    [Fact]
    public void AHoodieSkinControlsTheContextActionEvenWhenTheBaseShirtHasNoHood()
    {
        ulong next = 100;
        var inventory = new PlayerInventory(1, () => ++next,
            new InventoryOptions { StarterOutfit = [3384] })
        { SkinDefinition = id => id == 3384 ? 4266u : id };
        inventory.Bootstrap();
        var chest = inventory.LoadoutSlots[SurvivorLoadout.Chest];
        Assert.Equal(ItemActionKind.Hood,
            InventoryActions.Perform(inventory, Request(inventory, chest.Guid, HoodieUpOption)).Kind);
        Assert.True(inventory.HoodUp);
        inventory.TryPickUp(MotorcycleHelmet, 1, out _);
        Assert.False(inventory.HoodUp);
        Assert.False(inventory.TrySetHood(true));
    }

    [Fact]
    public void DroppingAWornHelmetEmptiesItsLoadoutAndBodySlot()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);
        Assert.NotNull(helmet);
        Assert.Equal(helmet, inventory.EquipmentSlots[BodySlots.Head]);

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, helmet!.Guid, DropItemOption));

        Assert.Equal(ItemActionKind.Drop, plan.Kind);
        Assert.Equal(ItemUseOptionKind.DropItem, plan.Option);
        Assert.Equal(MotorcycleHelmet, plan.DefinitionId);
        Assert.Equal(1u, plan.Count);
        Assert.Equal(0u, plan.RemainingCount);              // the instance is gone
        Assert.Equal(SurvivorLoadout.Head, plan.ClearedLoadoutSlotId);
        Assert.Equal(BodySlots.Head, plan.ClearedEquipmentSlotId);

        Assert.DoesNotContain(helmet.Guid, inventory.Items.Keys);
        Assert.DoesNotContain(SurvivorLoadout.Head, inventory.LoadoutSlots.Keys);
        Assert.DoesNotContain(BodySlots.Head, inventory.EquipmentSlots.Keys);
    }

    [Fact]
    public void DroppingSomeOfAStackLeavesTheRestBehind()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? ammo);
        Assert.NotNull(ammo);
        Assert.Equal(30u, ammo!.Count);

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, ammo.Guid, DropItemOption, count: 12));

        Assert.Equal(ItemActionKind.Drop, plan.Kind);
        Assert.Equal(12u, plan.Count);
        Assert.Equal(18u, plan.RemainingCount);             // the caller re-sends ItemAdd, not ItemDelete
        Assert.Equal(0u, plan.ClearedLoadoutSlotId);
        Assert.Equal(18u, inventory.Items[ammo.Guid].Count);
        Assert.Contains(ammo.Guid, inventory.Items.Keys);
    }

    [Fact]
    public void DroppingAWornBackpackGivesTheCapacityBackAndReleasesTheContainerSlot()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(MilitaryBackpack, 1, out InventoryItemInstance? pack);
        Assert.NotNull(pack);
        Assert.Equal(2100, inventory.Capacity.Max);         // base 100 + PARAM1 28 -> MAX_BULK 2000

        inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? ammo);
        Assert.Equal(1u, ammo!.ContainerSlotId);

        InventoryActions.Perform(inventory, Request(inventory, pack!.Guid, DropItemOption));

        Assert.Equal(100, inventory.Capacity.Max);          // the 2000 left with the pack
        Assert.Single(inventory.Containers);                // one backing cargo bag; the provider record is gone

        // The freed cell is reused, because the client keys its per-container collection on the
        // slot index and a leaked index costs a cell for the rest of the match (docs/41 §5d).
        InventoryActions.Perform(inventory, Request(inventory, ammo.Guid, DropItemOption));
        inventory.TryPickUp(Ammo762, 10, out InventoryItemInstance? again);
        Assert.Equal(1u, again!.ContainerSlotId);
    }

    [Fact]
    public void TheBagAndTheFistsCannotBeDropped()
    {
        PlayerInventory inventory = Fresh();
        ulong bag = inventory.LoadoutSlots[SurvivorLoadout.Inventory].Guid;
        ulong fists = inventory.LoadoutSlots[SurvivorLoadout.Fists].Guid;

        foreach (ulong guid in new[] { bag, fists })
        {
            ItemActionResult plan = InventoryActions.Perform(
                inventory, Request(inventory, guid, DropItemOption));
            Assert.Equal(ItemActionKind.Refused, plan.Kind);
            Assert.Equal(ContainerErrorCode.WrongItemType, plan.Error);
        }

        Assert.NotNull(inventory.BaseBag);
        Assert.Contains(SurvivorLoadout.Fists, inventory.LoadoutSlots.Keys);
    }

    [Fact]
    public void AnItemGuidTheCharacterDoesNotOwnIsRefusedInTheClientsOwnVocabulary()
    {
        PlayerInventory inventory = Fresh();

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, 0xDEAD_BEEFul, DropItemOption));

        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.SlotDoesNotContainItem, plan.Error);
    }

    [Fact]
    public void ARequestForAnotherCharactersInventoryIsRefused()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);

        RequestUseItem spoofed = Request(inventory, helmet!.Guid, DropItemOption) with
        {
            CharacterGuid = 9999,
        };

        ItemActionResult plan = InventoryActions.Perform(inventory, spoofed);

        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.InteractionValidationFailed, plan.Error);
        Assert.Contains(helmet.Guid, inventory.Items.Keys);
    }

    [Fact]
    public void AnOptionTheClientCouldNotHaveOfferedForThatItemIsRefused()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? ammo);

        // Ammunition is ItemIdUseOptionGroupId group 62: 4, 12, 59, 61, 87. SalvageItem 63 is in
        // group 56 (the helmet's) and was never on this item's menu.
        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, ammo!.Guid, SalvageItemOption));

        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.WrongItemType, plan.Error);
        Assert.Equal(30u, inventory.Items[ammo.Guid].Count);
    }

    [Fact]
    public void ConsumeIsOnByDefaultAndPlansACastBeforeAnythingMoves()
    {
        // D341: consume is answered by default with a PLAN - the option row's own BUSY_MSEC (95:
        // 3,000) and animation (18) for the cf 02 - and the unit leaves only when the caller applies
        // the plan at the bar's end, exactly like a shred.
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(FieldBandage, 1, out InventoryItemInstance? bandage);

        ItemActionResult plan = InventoryActions.Resolve(
            inventory, Request(inventory, bandage!.Guid, ConsumeItemOption));

        Assert.Equal(ItemActionKind.Consume, plan.Kind);
        Assert.Equal(ItemUseOptionKind.ConsumeItem, plan.Option);
        Assert.Equal(3000, plan.BusyMilliseconds);
        Assert.Equal(18u, plan.InteractionAnimationId);
        Assert.Equal(1u, plan.Count);
        Assert.Contains(bandage.Guid, inventory.Items.Keys);   // not applied yet

        InventoryActions.Apply(inventory, plan);
        Assert.Equal(4u, inventory.Items[bandage.Guid].Count);
    }

    [Fact]
    public void ConsumeRefusesAnythingThatIsNotAMedical()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(1429, 30, out InventoryItemInstance? ammo);   // .223 rounds

        ItemActionResult plan = InventoryActions.Resolve(
            inventory, Request(inventory, ammo!.Guid, ConsumeItemOption));

        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.WrongItemType, plan.Error);
        Assert.Contains(ammo.Guid, inventory.Items.Keys);
    }

    [Fact]
    public void ConsumeRemovesOneUnitWhenTheEffectLaneTurnsItOn()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            StarterOutfit = [],
            AnswerConsumeItem = true,
        });
        // Newly looted bandages join the starter Q stack; consume removes exactly one unit.
        inventory.TryPickUp(FieldBandage, 1, out InventoryItemInstance? bandage);

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, bandage!.Guid, ConsumeItemOption));

        Assert.Equal(ItemActionKind.Consume, plan.Kind);
        Assert.Equal(1u, plan.Count);
        Assert.Equal(4u, plan.RemainingCount);
        Assert.Equal(4u, inventory.Items[bandage.Guid].Count);
    }

    [Fact]
    public void DropCanBeSwitchedOffWithoutTouchingTheSendOrder()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            StarterOutfit = [],
            AnswerDropItem = false,
        });
        inventory.TryPickUp(Backpack, 1, out InventoryItemInstance? pack);

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, pack!.Guid, DropItemOption));

        Assert.Equal(ItemActionKind.NotImplemented, plan.Kind);
        Assert.Contains(pack.Guid, inventory.Items.Keys);
    }

    [Fact]
    public void ADropNeverCreatesAnActiveHandBinding()
    {
        // Regression guard 5. Fists is a required loadout-only selection, and none of these
        // default-option placements creates body slot 7. Therefore no drop can produce a
        // ClearedEquipmentSlotId of 7 either - which is what would make the caller re-dress with
        // an unsafe active-hand row.
        PlayerInventory inventory = Fresh();
        foreach (uint definitionId in new uint[] { 1889, 2229, 1702, 65, MotorcycleHelmet, MilitaryBackpack })
        {
            inventory.TryPickUp(definitionId, 1, out InventoryItemInstance? item);
            if (item is null)
            {
                continue;
            }

            ItemActionResult plan = InventoryActions.Perform(
                inventory, Request(inventory, item.Guid, DropItemOption));
            Assert.NotEqual(BodySlots.RightHand, plan.ClearedEquipmentSlotId);
        }

        InventoryItemInstance fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];
        Assert.Equal(PlayerInventory.SurvivorFistsItemDefinitionId, fists.DefinitionId);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0u, fists.EquipmentSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
    }

    [Fact]
    public void DroppingAnExplicitlyDrawnRifleRestoresFistsWithoutLeavingAnActiveHandBinding()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            StarterOutfit = [],
            WieldFirstWeapon = true,
        });
        InventoryPlacement placement = inventory.TryPickUp(10, 1, out InventoryItemInstance? rifle);
        Assert.True(placement.Wielded);
        Assert.NotNull(rifle);
        Assert.Equal(BodySlots.RightHand, rifle!.EquipmentSlotId);

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, rifle.Guid, DropItemOption));

        // The removal model correctly identifies the vacated hand, then PlayerInventory restores
        // the required fists tile without manufacturing a body-slot-7 binding.
        Assert.Equal(ItemActionKind.Drop, plan.Kind);
        Assert.Equal(BodySlots.RightHand, plan.ClearedEquipmentSlotId);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
        Assert.DoesNotContain(rifle.Guid, inventory.Items.Keys);
    }

    [Fact]
    public void AHelmetLowersTheHoodAndBlocksRaisingItUntilTheHeadIsClear()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { StarterOutfit = [Hoodie] });
        InventoryItemInstance hoodie = inventory.LoadoutSlots[SurvivorLoadout.Chest];

        Assert.False(inventory.HoodUp);
        ItemActionResult raised = InventoryActions.Perform(
            inventory, Request(inventory, hoodie.Guid, HoodieUpOption));
        Assert.Equal(ItemActionKind.Hood, raised.Kind);
        Assert.True(inventory.HoodUp);

        inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);
        Assert.NotNull(helmet);
        Assert.False(inventory.HoodUp);

        ItemActionResult blocked = InventoryActions.Perform(
            inventory, Request(inventory, hoodie.Guid, HoodieUpOption));
        Assert.Equal(ItemActionKind.Refused, blocked.Kind);
        Assert.Equal(ContainerErrorCode.InteractionValidationFailed, blocked.Error);
        Assert.False(inventory.HoodUp);

        InventoryActions.Perform(inventory, Request(inventory, helmet!.Guid, DropItemOption));
        Assert.DoesNotContain(BodySlots.Head, inventory.EquipmentSlots.Keys);
        Assert.Equal(
            ItemActionKind.Hood,
            InventoryActions.Perform(
                inventory, Request(inventory, hoodie.Guid, HoodieUpOption)).Kind);
        Assert.True(inventory.HoodUp);

        Assert.Equal(
            ItemActionKind.Hood,
            InventoryActions.Perform(
                inventory, Request(inventory, hoodie.Guid, HoodieDownOption)).Kind);
        Assert.False(inventory.HoodUp);
    }

    [Fact]
    public void ANewlyEquippedHoodieNeverInheritsRaisedState()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { StarterOutfit = [Hoodie] });
        InventoryItemInstance first = inventory.LoadoutSlots[SurvivorLoadout.Chest];
        InventoryActions.Perform(inventory, Request(inventory, first.Guid, HoodieUpOption));
        Assert.True(inventory.HoodUp);

        InventoryActions.Perform(inventory, Request(inventory, first.Guid, DropItemOption));
        InventoryPlacement placement = inventory.TryPickUp(Hoodie, 1, out InventoryItemInstance? replacement);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, placement.Kind);
        Assert.NotNull(replacement);
        Assert.Equal(BodySlots.Chest, replacement!.EquipmentSlotId);
        Assert.False(inventory.HoodUp);
    }
}
