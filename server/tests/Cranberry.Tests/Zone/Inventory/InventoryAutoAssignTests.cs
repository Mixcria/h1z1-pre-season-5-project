using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Inventory;

// The auto-assign decisions of docs/41-inventory-slots.md §5 — the owner's brief this wave:
// "correct positions / auto assign. I did pick up some items but they just went into the inventory
// and not the correct slot so I wasn't able to test."
public sealed class InventoryAutoAssignTests
{
    // The Z2 loot set (src/Cranberry.Zone/Data/Loot/z2-loot-tables.json), resolved in docs/41 §5c.
    private const uint Ar15 = 1889;
    private const uint Ak47 = 2229;
    private const uint Shotgun12Ga = 1374;
    private const uint HuntingRifle = 1899;
    private const uint M1911 = 1702;
    private const uint MotorcycleHelmet = 2168;
    private const uint Backpack = 2112;
    private const uint MilitaryBackpack = 2118;
    private const uint Satchel = 2125;
    private const uint BodyArmour = 2205;
    private const uint FieldBandage = 2423;
    private const uint FirstAidKit = 2424;
    private const uint FragGrenade = 65;
    private const uint Ammo762 = 1429;

    /// <summary>
    /// A bootstrapped inventory with <b>no starting outfit</b>. Every bulk figure in this file is
    /// the resolver's arithmetic against a bare bag (base carry only), which is what docs/41 §4
    /// derives; the starting outfit adds +50 shirt and +50 pants on top of it and is pinned
    /// separately in <c>StarterOutfitTests</c> (docs/46 §7b). Turning it off here keeps the two
    /// concerns from moving each other's numbers.
    /// </summary>
    private static PlayerInventory Fresh(InventoryOptions? options = null)
    {
        ulong next = 0x4000;
        var inventory = new PlayerInventory(
            0xdead_beef, () => ++next, (options ?? new InventoryOptions()) with { StarterOutfit = [] });
        inventory.Bootstrap();
        return inventory;
    }

    // -------------------------------------------------------------------------------------
    // Bootstrap
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// docs/41 §4b — the character's bag is item 3156 in loadout slot 43, providing container
    /// definition 117; and loadout slot 7 carries the Fists (item 85, FLAG_REQUIRED) as the
    /// selected empty-hand tile. August must not receive a body-slot-7 Fists binding.
    /// </summary>
    [Fact]
    public void BootstrapCreatesTheHiddenBagAndTheFists()
    {
        PlayerInventory inventory = Fresh();

        Assert.NotNull(inventory.BaseBag);
        Assert.Equal(ContainerDefinitionTable.BaseInventoryDefinitionId, inventory.BaseBag!.DefinitionId);

        InventoryItemInstance bagItem = inventory.LoadoutSlots[SurvivorLoadout.Inventory];
        Assert.Equal(ContainerDefinitionTable.BaseInventoryItemDefinitionId, bagItem.DefinitionId);
        // Lead L4: the container is keyed by the providing item's own instance guid.
        Assert.Equal(bagItem.Guid, inventory.BaseBag.Guid);

        InventoryItemInstance fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];
        Assert.Equal(PlayerInventory.SurvivorFistsItemDefinitionId, fists.DefinitionId);
        Assert.Equal(0u, fists.EquipmentSlotId);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
    }

    // -------------------------------------------------------------------------------------
    // Apparel — the owner's helmet
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The acceptance case: a helmet must land on loadout slot 11 and body slot 1 (Head), not in
    /// the bag. Rule: LoadoutSlotItemClasses(3, 11) = {25000}, FLAG_AUTO_EQUIP = 1, and the item's
    /// PASSIVE_EQUIP_SLOT_ID is 1.
    /// </summary>
    [Fact]
    public void HelmetGoesOnTheHead()
    {
        PlayerInventory inventory = Fresh();
        InventoryPlacement plan = inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.Equal(SurvivorLoadout.Head, plan.LoadoutSlotId);
        Assert.Equal(BodySlots.Head, plan.EquipmentSlotId);
        Assert.False(plan.Wielded);
        Assert.NotNull(helmet);
        Assert.Equal(helmet, inventory.EquipmentSlots[BodySlots.Head]);
        Assert.Equal(0ul, helmet!.ContainerGuid);
        Assert.Empty(inventory.BaseBag!.Slots);
    }

    [Theory]
    [InlineData(Backpack, SurvivorLoadout.Backpack, BodySlots.Backpack)]
    [InlineData(Satchel, SurvivorLoadout.Backpack, BodySlots.Backpack)]
    [InlineData(BodyArmour, SurvivorLoadout.ChestArmor, BodySlots.ChestArmor)]
    public void ApparelGoesToItsAutoEquipSlot(uint itemId, uint loadoutSlot, uint bodySlot)
    {
        PlayerInventory inventory = Fresh();
        InventoryPlacement plan = inventory.TryPickUp(itemId, 1, out _);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.Equal(loadoutSlot, plan.LoadoutSlotId);
        Assert.Equal(bodySlot, plan.EquipmentSlotId);
    }

    /// <summary>Retail behaviour is swap: the old backpack goes into the bag, not on the floor.</summary>
    [Fact]
    public void SecondBackpackDisplacesTheFirstIntoTheBag()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Satchel, 1, out InventoryItemInstance? satchel);

        InventoryPlacement plan = inventory.TryPickUp(MilitaryBackpack, 1, out InventoryItemInstance? military);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.Equal(satchel!.Guid, plan.DisplacedItemGuid);
        Assert.Equal(military, inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Equal(inventory.BaseBag!.Guid, satchel.ContainerGuid);
        Assert.Contains(satchel, inventory.BaseBag.Slots.Values);
    }

    // -------------------------------------------------------------------------------------
    // Weapons — the owner's rifle
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The first weapon picked up goes to wheel slot 1 (Slot2) and <b>stows</b> on the long-gun
    /// group, body slot 76.
    /// <para>
    /// This test deliberately uses the default rollback option, so the rifle stays slung and the
    /// selected Fists tile leaves RHand unbound. The live weapon path may explicitly select a real
    /// weapon after its definition and item data have been delivered; the crash-side wire
    /// invariants remain pinned in <c>GunPickupCrashGuardTests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void FirstWeaponStowsRatherThanGoingIntoTheHand()
    {
        PlayerInventory inventory = Fresh();
        InventoryPlacement plan = inventory.TryPickUp(Ar15, 1, out InventoryItemInstance? rifle);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.Equal(SurvivorLoadout.Wheel1, plan.LoadoutSlotId);
        Assert.Equal(76u, plan.EquipmentSlotId);
        Assert.NotEqual(BodySlots.RightHand, plan.EquipmentSlotId);
        Assert.False(plan.Wielded);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
        Assert.Equal(rifle!.Guid, inventory.EquipmentSlots[76].Guid);
    }

    /// <summary>
    /// The second and third weapons take the next wheel slots in DISPLAY_INDEX order 1 -> 3 -> 4,
    /// and stow on the long-gun group {76, 77, 80} rather than colliding (docs/41 §1b). With
    /// wielding off (docs/45) the <em>first</em> rifle already occupies 76, so the group now fills
    /// 76 -> 77 -> 80 instead of 76 -> 77.
    /// </summary>
    [Fact]
    public void FurtherLongGunsFillTheWheelAndTheStowGroup()
    {
        PlayerInventory inventory = Fresh();
        InventoryPlacement first = inventory.TryPickUp(Ar15, 1, out _);
        Assert.Equal(76u, first.EquipmentSlotId);

        InventoryPlacement second = inventory.TryPickUp(Ak47, 1, out _);
        Assert.Equal(SurvivorLoadout.Wheel2, second.LoadoutSlotId);
        Assert.Equal(77u, second.EquipmentSlotId);
        Assert.False(second.Wielded);

        InventoryPlacement third = inventory.TryPickUp(Shotgun12Ga, 1, out _);
        Assert.Equal(SurvivorLoadout.Wheel3, third.LoadoutSlotId);
        Assert.Equal(80u, third.EquipmentSlotId);
    }

    /// <summary>Every wheel slot full: carry it, never throw the held weapon away (docs/41 §5b).</summary>
    [Fact]
    public void FourthWeaponGoesInTheBagRatherThanDisplacingOne()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Ar15, 1, out _);
        inventory.TryPickUp(Ak47, 1, out _);
        inventory.TryPickUp(Shotgun12Ga, 1, out _);

        InventoryPlacement fourth = inventory.TryPickUp(M1911, 1, out InventoryItemInstance? pistol);

        Assert.Equal(InventoryPlacementKind.Container, fourth.Kind);
        Assert.Equal(inventory.BaseBag!.Guid, fourth.ContainerGuid);
        Assert.Equal(0u, fourth.LoadoutSlotId);
        Assert.Equal(inventory.BaseBag.Guid, pistol!.ContainerGuid);
        // Still carrying the AR-15 - stowed on the long-gun group while the empty hand stays unbound.
        Assert.Equal(Ar15, inventory.EquipmentSlots[76].DefinitionId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
    }

    /// <summary>A pistol stows on the short-weapon group {78, 79, 81}, never on 76.</summary>
    [Fact]
    public void PistolStowsOnTheShortWeaponGroup()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Ar15, 1, out _);

        InventoryPlacement plan = inventory.TryPickUp(M1911, 1, out _);
        // M1911A1 has PASSIVE_EQUIP_SLOT_ID 0 and no group, so it shows no attachment when stowed.
        Assert.Equal(SurvivorLoadout.Wheel2, plan.LoadoutSlotId);
        Assert.Equal(0u, plan.EquipmentSlotId);
    }

    /// <summary>docs/41 §5c — grenades are accepted by wheel 1 and 4, never wheel 3.</summary>
    [Fact]
    public void GrenadeUsesTheSecondWeaponSlotInLoadoutSeventeen()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Ar15, 1, out _);        // takes wheel slot 1

        InventoryPlacement plan = inventory.TryPickUp(FragGrenade, 1, out _);
        Assert.Equal(SurvivorLoadout.Wheel2, plan.LoadoutSlotId);
    }

    // -------------------------------------------------------------------------------------
    // Container-only items
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Ammunition has no quick-use mapping and remains bag-only.
    /// </summary>
    [Fact]
    public void AmmunitionIsContainerOnly()
    {
        PlayerInventory inventory = Fresh();
        InventoryPlacement plan = inventory.TryPickUp(Ammo762, 1, out InventoryItemInstance? item);

        Assert.Equal(InventoryPlacementKind.Container, plan.Kind);
        Assert.Equal(0u, plan.LoadoutSlotId);
        Assert.Equal(0u, plan.EquipmentSlotId);
        Assert.Equal(inventory.BaseBag!.Guid, item!.ContainerGuid);
        Assert.Equal(inventory.BaseBag.DefinitionId, item.ContainerDefinitionId);
    }

    [Fact]
    public void FirstAidKitUsesTheFreeEQuickSlot()
    {
        PlayerInventory inventory = Fresh();
        InventoryPlacement plan = inventory.TryPickUp(FirstAidKit, 1, out _);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.Equal(SurvivorLoadout.QuickUse2, plan.LoadoutSlotId);
        Assert.Equal(0u, plan.EquipmentSlotId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdditionalFirstAidKitsIncreaseTheDedicatedSlotCountEvenWithAFullBag(bool fullBag)
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { BaseCarryBulk = 100 });
        if (fullBag) inventory.TryPickUp(Ammo762, 50, out _);
        var initialCapacity = inventory.Capacity;
        var bandages = inventory.LoadoutSlots[SurvivorLoadout.QuickUse1];
        inventory.TryPickUp(FirstAidKit, 1, out var first);

        var plan = inventory.TryPickUp(FirstAidKit, 3, out var merged);

        Assert.Equal(InventoryPlacementKind.Stack, plan.Kind);
        Assert.Same(first, merged);
        Assert.Same(merged, inventory.LoadoutSlots[SurvivorLoadout.QuickUse2]);
        Assert.Equal(4u, merged!.Count);
        Assert.Single(inventory.Items.Values, item => item.DefinitionId == FirstAidKit);
        Assert.DoesNotContain(inventory.BaseBag!.Slots.Values, item => item.DefinitionId == FirstAidKit);
        Assert.Equal(initialCapacity, inventory.Capacity);
        Assert.Same(bandages, inventory.LoadoutSlots[SurvivorLoadout.QuickUse1]);
        Assert.Equal(4u, bandages.Count);
        Assert.Equal(0u, merged.EquipmentSlotId);
        var record = merged.ToRecord(inventory.CharacterGuid);
        Assert.Equal(PlayerInventory.EquippedContainerGuid, record.ContainerGuid);
        Assert.Equal(SurvivorLoadout.QuickUse2, record.SlotId);
        Assert.Equal(4u, record.Count);
    }

    [Fact]
    public void ExtraBandagesMergeIntoQAndLeaveTheFirstAidSlotFree()
    {
        PlayerInventory inventory = Fresh();
        var starter = inventory.LoadoutSlots[SurvivorLoadout.QuickUse1];
        for (int i = 0; i < 3; i++)
        {
            var plan = inventory.TryPickUp(FieldBandage, 1, out var bandage);
            Assert.Equal(InventoryPlacementKind.Stack, plan.Kind);
            Assert.Same(starter, bandage);
            Assert.Equal((uint)(5 + i), bandage!.Count);
            Assert.False(inventory.LoadoutSlots.ContainsKey(SurvivorLoadout.QuickUse2));
        }
        Assert.Same(starter, inventory.LoadoutSlots[SurvivorLoadout.QuickUse1]);
        inventory.TryPickUp(FirstAidKit, 1, out var kit);
        Assert.Same(kit, inventory.LoadoutSlots[SurvivorLoadout.QuickUse2]);
    }

    [Theory]
    [InlineData(FieldBandage, SurvivorLoadout.QuickUse1)]
    [InlineData(FirstAidKit, SurvivorLoadout.QuickUse2)]
    public void WithBothSlotsEmptyMedicalPickupUsesItsDesignatedSlot(uint itemId, uint expectedSlot)
    {
        PlayerInventory inventory = Fresh();
        inventory.RemoveUnits(inventory.LoadoutSlots[SurvivorLoadout.QuickUse1].Guid, 0);
        var plan = inventory.TryPickUp(itemId, 1, out _);
        Assert.Equal(expectedSlot, plan.LoadoutSlotId);
        Assert.Equal(0u, plan.EquipmentSlotId);
    }

    /// <summary>Container slot ids are 1-based and per container, not a global counter (docs/41 §0).</summary>
    [Fact]
    public void ContainerSlotsAreOneBasedAndPerContainer()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { QuickUseConsumables = false });
        inventory.TryPickUp(FieldBandage, 1, out InventoryItemInstance? first);
        inventory.TryPickUp(FirstAidKit, 1, out InventoryItemInstance? second);

        Assert.Equal(1u, first!.ContainerSlotId);
        Assert.Equal(2u, second!.ContainerSlotId);
    }

    /// <summary>
    /// docs/41 §5d — MAX_STACK_SIZE decides whether a pickup merges.
    /// <para>
    /// The bag needs real headroom for this: item 1429 is BULK 2, so two boxes of 30 cost 120 and
    /// the default <c>BaseCarryBulk</c> of 100 refuses the second — correctly, see
    /// <see cref="AMergeIsChargedBulkJustLikeANewSlot"/>. This test is about the merge, so it gives
    /// the merge room to happen rather than removing the capacity rule that got in its way.
    /// </para>
    /// </summary>
    [Fact]
    public void StackableAmmunitionMergesInsteadOfTakingANewSlot()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { BaseCarryBulk = 1000 });
        inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? first);

        InventoryPlacement plan = inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? merged);

        Assert.Equal(InventoryPlacementKind.Stack, plan.Kind);
        Assert.Equal(first!.Guid, plan.StackTargetItemGuid);
        Assert.Same(first, merged);
        Assert.Equal(60u, first.Count);
        Assert.Single(inventory.BaseBag!.Slots);
        Assert.Equal(120, inventory.BaseBag.BulkUsed);
    }

    /// <summary>
    /// A merge costs exactly the bulk a new slot would, because <c>InventoryContainer.BulkUsed</c>
    /// is the sum of <c>BULK x Count</c> and the Stack arm of <c>TryPickUp</c> does a bare
    /// <c>instance.Count += count</c>.
    /// <para>
    /// The bulk test used to sit <b>after</b> the stacking loop, so a merge never reached it: with
    /// MAX_STACK_SIZE 9999 on every calibre, and the loot lane now carpeting the floor with
    /// clustered boxes of the calibre already in the bag, a player could walk past
    /// <c>BaseCarryBulk</c> without a single <c>Container.Error</c>. The class docstring is explicit
    /// that the server is the only authority here.
    /// </para>
    /// </summary>
    [Fact]
    public void AMergeIsChargedBulkJustLikeANewSlot()
    {
        // BULK 2 x 30 rounds = 60 of a 100 bag. The first box fits, the second does not.
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            BaseCarryBulk = 100,
            QuickUseConsumables = false,
        });
        Assert.Equal(
            InventoryPlacementKind.Container,
            inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? first).Kind);
        Assert.Equal(60, inventory.BaseBag!.BulkUsed);

        InventoryPlacement plan = inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? refused);

        Assert.Equal(InventoryPlacementKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.InteractionValidationFailed, plan.Error);
        Assert.Null(refused);
        // Nothing changed: the held stack still holds exactly what it did.
        Assert.Equal(30u, first!.Count);
        Assert.Equal(60, inventory.BaseBag.BulkUsed);

        // 20 more rounds is 40 bulk, which fits exactly, and it merges rather than taking a slot.
        Assert.Equal(InventoryPlacementKind.Stack, inventory.TryPickUp(Ammo762, 20, out _).Kind);
        Assert.Equal(50u, first.Count);
        Assert.Equal((100, 100), inventory.Capacity);
        Assert.Single(inventory.BaseBag.Slots);
    }

    // -------------------------------------------------------------------------------------
    // Bulk (D26)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// docs/41 §4b — the dynamic bag's capacity is the owner-decision base plus the MAX_BULK of
    /// every worn container item. Backpack (PARAM1 22) is +1000, military (PARAM1 28) is +2000.
    /// </summary>
    [Fact]
    public void WornContainersSubsumeTheirCapacityIntoTheBag()
    {
        var options = new InventoryOptions { BaseCarryBulk = 100 };
        PlayerInventory inventory = Fresh(options);

        Assert.Equal(100, inventory.MaxBulkOf(inventory.BaseBag!));

        inventory.TryPickUp(Backpack, 1, out _);
        Assert.Equal(1100, inventory.MaxBulkOf(inventory.BaseBag!));

        // Swapping up to the military pack replaces, not adds: the old one is displaced into the
        // bag, where it is cargo and no longer contributes capacity.
        inventory.TryPickUp(MilitaryBackpack, 1, out _);
        Assert.Equal(2100, inventory.MaxBulkOf(inventory.BaseBag!));
    }

    /// <summary>
    /// docs/41 §4c — the server is authoritative for bulk, so it must refuse an over-capacity
    /// pickup itself; the client never validates a ground pickup. The refusal carries a code from
    /// the client's own 0xc8/03 enum.
    /// </summary>
    [Fact]
    public void OverCapacityPickupIsRefusedWithAContainerErrorCode()
    {
        // Base 100 with no worn container. The first three long guns take the wheel slots and cost
        // nothing; the fourth has to go in the bag, where its BULK of 1500 does not fit.
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            BaseCarryBulk = 100,
            QuickUseConsumables = false,
        });
        inventory.TryPickUp(Ar15, 1, out _);
        inventory.TryPickUp(Ak47, 1, out _);
        inventory.TryPickUp(Shotgun12Ga, 1, out _);

        InventoryPlacement plan = inventory.TryPickUp(HuntingRifle, 1, out InventoryItemInstance? refused);

        Assert.Equal(InventoryPlacementKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.InteractionValidationFailed, plan.Error);
        Assert.Null(refused);
        Assert.Empty(inventory.BaseBag!.Slots);

        // The boundary is exact: a pistol at BULK 100 fills the bag to precisely 100/100.
        Assert.Equal(
            InventoryPlacementKind.Container,
            inventory.TryPickUp(M1911, 1, out _).Kind);
        Assert.Equal((100, 100), inventory.Capacity);
        Assert.Equal(
            InventoryPlacementKind.Refused,
            inventory.TryPickUp(FieldBandage, 1, out _).Kind);
    }

    /// <summary>
    /// Worn and wielded items are bound to loadout slots, not to a container, so they cost no
    /// bulk — which is also why item 85 (BULK 1,000,000) and item 3156 (BULK 9,999) are free.
    /// </summary>
    [Fact]
    public void WornItemsCostNoBulk()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { BaseCarryBulk = 100 });
        inventory.TryPickUp(MotorcycleHelmet, 1, out _);     // BULK 250, worn

        Assert.Equal(0, inventory.BaseBag!.BulkUsed);
        Assert.Equal((0, 100), inventory.Capacity);
    }

    /// <summary>
    /// Cranberry's pre-lane behaviour, made explicit: with no container the client's own enum name
    /// for ContainerGuid = 0 is UnknownContainer (docs/41 §0).
    /// </summary>
    [Fact]
    public void WithoutBootstrapEveryContainerPlacementIsUnknownContainer()
    {
        ulong next = 1;
        var inventory = new PlayerInventory(7, () => ++next);

        InventoryPlacement plan = inventory.Plan(Ammo762);

        Assert.Equal(InventoryPlacementKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.UnknownContainer, plan.Error);
    }

    /// <summary>
    /// The resolver mirrors the client's own FindAllSupportingLoadoutSlotsByItemId, so a slot the
    /// server picks is always one the client's UI would accept.
    /// </summary>
    [Fact]
    public void SupportingSlotsMatchTheClientPredicate()
    {
        Assert.Equal([SurvivorLoadout.Head], InventoryAutoAssign.SupportingLoadoutSlots(MotorcycleHelmet));
        Assert.Equal([SurvivorLoadout.Backpack], InventoryAutoAssign.SupportingLoadoutSlots(Backpack));
        Assert.Equal(
            [SurvivorLoadout.Wheel1, SurvivorLoadout.Wheel2, SurvivorLoadout.Wheel3],
            InventoryAutoAssign.SupportingLoadoutSlots(Ar15));

        // The Field Bandage's primary ITEM_CLASS 16053 is not accepted directly. The client's rule
        // is ITEM_CLASS UNION ItemClassMappings, and those mappings make it eligible for loadout
        // 17's Q/E slots. Both folded and primary-only answers are pinned here.
        Assert.Equal(
            [SurvivorLoadout.Utility1, SurvivorLoadout.Utility2],
            InventoryAutoAssign.SupportingLoadoutSlots(FieldBandage));
        Assert.Empty(
            InventoryAutoAssign.SupportingLoadoutSlots(
                FieldBandage, SurvivorLoadout.Id, foldClassMappings: false));
    }

    /// <summary>
    /// <b>docs/95 / D183 (the wave-10 follow-up) — the first aid kit must not take the hand.</b>
    /// Item 2424 carries
    /// <c>ACTIVE_EQUIP_SLOT_ID 7</c> and a real 3P mesh, so the old rule wielded it; the wire guard
    /// then refused a binding it had no fire group for, and the server's hand was full while the
    /// client's was empty. The next rifle went to body slot 76 — the owner's "the gun went on my
    /// back instead", 2026-08-31 19:24:43.
    /// </summary>
    [Theory]
    [InlineData(2424u)]   // Tactical First Aid Kit  - ACTIVE_EQUIP_SLOT_ID 7, has a 3P mesh
    [InlineData(2423u)]   // Field Bandage           - the same shape
    public void AnItemWithNoFireGroupNeverTakesTheHandHoweverActiveItsSlotIs(uint definitionId)
    {
        Assert.True(InventoryItemFacts.TryGet(definitionId, out InventoryItemFact fact));
        Assert.Equal(7u, fact.ActiveEquipSlotId);      // the trap: it LOOKS wieldable
        Assert.False(AugustWeaponTable.HasFireGroup(definitionId, out _));

        PlayerInventory inventory = Fresh(new InventoryOptions { WieldFirstWeapon = true });
        InventoryPlacement plan = inventory.TryPickUp(definitionId, 1, out InventoryItemInstance? instance);

        Assert.NotNull(instance);
        Assert.False(plan.Wielded);
        Assert.NotEqual(AugustHeldWeapon.RightHandSlotId, plan.EquipmentSlotId);
        if (plan.Kind == InventoryPlacementKind.LoadoutSlot)
            Assert.Contains("no fire group resolves", plan.Rule);
    }

    /// <summary>The other half: a real weapon still wields, so the fix narrows and does not disable.</summary>
    [Fact]
    public void AWeaponWithAFireGroupStillTakesTheHand()
    {
        uint rifle = AugustHeldWeapon.ItemDefinitionId;
        Assert.True(AugustWeaponTable.HasFireGroup(rifle, out _));

        PlayerInventory inventory = Fresh(new InventoryOptions { WieldFirstWeapon = true });
        InventoryPlacement plan = inventory.TryPickUp(rifle, 1, out InventoryItemInstance? instance);

        Assert.NotNull(instance);
        Assert.True(plan.Wielded);
        Assert.Equal(AugustHeldWeapon.RightHandSlotId, plan.EquipmentSlotId);
    }
}
