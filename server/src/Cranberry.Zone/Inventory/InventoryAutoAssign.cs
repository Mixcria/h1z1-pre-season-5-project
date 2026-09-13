using Cranberry.Zone.Appearance;

using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Inventory;

/// <summary>What kind of home the resolver found for an item.</summary>
public enum InventoryPlacementKind
{
    /// <summary>Nothing was changed. <see cref="InventoryPlacement.Error"/> says why.</summary>
    Refused,

    /// <summary>Bound to a loadout slot (and usually attached to a body slot as well).</summary>
    LoadoutSlot,

    /// <summary>Put in a container slot.</summary>
    Container,

    /// <summary>Merged into an existing stack in a container.</summary>
    Stack,
}

/// <summary>The item and appearance to put on the ground after a pickup replaces equipment.</summary>
public sealed record PickupDisplacement(uint DefinitionId, uint DisplayDefinitionId, uint Count);

/// <summary>
/// Where one item should go, and the rule that decided it. <see cref="Rule"/> is meant for the host
/// log: when the owner says "it went to the wrong place", the log line names the sheet rule that
/// chose that place.
/// </summary>
/// <param name="LoadoutSlotId">The <c>LoadoutSlots.SLOT_ID</c> to bind, or 0.</param>
/// <param name="EquipmentSlotId">
/// The body slot the mesh attaches to, or 0 when the item shows nothing. 7 (RHand) means the item
/// is <em>wielded</em>: that is the slot <c>EquipmentSlotRow</c> must name for the client to have
/// something to fire (docs/36 §W3).
/// <para>
/// <b>7 is unreachable today (docs/45).</b> <c>InventoryOptions.WieldFirstWeapon</c> is off because
/// a body-slot-7 row hard-crashes the August client while the bound weapon has no fire-group data,
/// and <c>ActiveHandRowGuard</c> drops any such row that reaches the serialiser anyway. Re-enable
/// both together with docs/45 §5b, never separately.
/// </para>
/// </param>
/// <param name="Wielded">True when this placement puts the item in the player's hands.</param>
public sealed record InventoryPlacement(
    InventoryPlacementKind Kind,
    string Rule,
    uint LoadoutSlotId = 0,
    uint EquipmentSlotId = 0,
    bool Wielded = false,
    ulong ContainerGuid = 0,
    uint ContainerDefinitionId = 0,
    uint ContainerSlotId = 0,
    ulong StackTargetItemGuid = 0,
    ulong DisplacedItemGuid = 0,
    ContainerErrorCode Error = ContainerErrorCode.None,
    PickupDisplacement? GroundDrop = null);

/// <summary>
/// The auto-assign resolver: item definition -> destination slot or container.
/// <para>
/// <b>This is the client's own rule, not a design.</b> <c>FUN_140d35510</c> - reached from
/// <c>ClientLoadoutManager::FindFirstSupportingLoadoutSlotByItemId</c> (<c>FUN_140d36680</c>, named
/// by its own error string at <c>0x143143650</c>) and <c>FindAllSupportingLoadoutSlotsByItemId</c>
/// (<c>FUN_140d36390</c>) - accepts a loadout slot for an item iff:
/// </para>
/// <list type="number">
/// <item>the slot definition exists in the hash keyed <c>(loadoutId ^ slotId) &amp; 0xff</c>;</item>
/// <item>slot flag bit <c>0x20</c> at <c>def+0x30</c> is set and the slot's requirement set at
/// <c>def+0x38</c> passes;</item>
/// <item>the item definition exists in the hash keyed <c>itemId &amp; 0x3ff</c> and its client
/// requirement at <c>def+0x144</c> passes;</item>
/// <item>the item's class set intersects the slot's class set at <c>slotDef+0x100</c> - which is
/// <c>LoadoutSlotItemClasses.txt</c>. <b>The item's class set is
/// <c>ITEM_CLASS</c> (<c>itemDef+0x40</c>) UNION <c>ItemClassMappings.txt</c></b>, not
/// <c>ITEM_CLASS</c> alone; see <see cref="ItemClassMappings"/> for the proof, which is that two of
/// loadout 17's own slot classes have zero primary members and are reachable no other way. Cranberry
/// tested <c>ITEM_CLASS</c> alone until wave 8 (docs/78 4.5).</item>
/// </list>
/// <para>
/// Cranberry must produce the same answer, or the server and the client's UI will disagree about
/// where an item is. Requirement sets (2 and 3) are not modelled: every active loadout-17 row
/// carries <c>REQ_SET_ID = 0</c> and <c>CLIENT_REQ_SET_ID = 0</c>, so they always pass.
/// </para>
/// Full derivation: docs/41-inventory-slots.md §5.
/// </summary>
public static class InventoryAutoAssign
{
    /// <summary>
    /// Every loadout slot of <paramref name="loadoutId"/> that accepts
    /// <paramref name="itemDefinitionId"/>, ascending by slot id - the direct translation of
    /// <c>FindAllSupportingLoadoutSlotsByItemId</c>.
    /// </summary>
    /// <param name="foldClassMappings">
    /// Test the slot's class set against <c>ITEM_CLASS</c> UNION <c>ItemClassMappings.txt</c>
    /// (the client's own rule, wave 8) rather than against <c>ITEM_CLASS</c> alone. Defaults to the
    /// client's rule; pass false for the pre-wave-8 answer.
    /// </param>
    public static IReadOnlyList<uint> SupportingLoadoutSlots(
        uint itemDefinitionId,
        uint loadoutId = SurvivorLoadout.Id,
        bool foldClassMappings = true)
    {
        if (!InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact fact))
        {
            return [];
        }

        var slots = new List<uint>();
        foreach (LoadoutSlotDefinition slot in LoadoutSlotTable.Slots(loadoutId))
        {
            if (Accepts(loadoutId, slot.SlotId, itemDefinitionId, fact.ItemClass, foldClassMappings))
            {
                slots.Add(slot.SlotId);
            }
        }

        slots.Sort();
        return slots;
    }

    /// <summary>
    /// Does loadout slot <paramref name="slotId"/> accept this item - the class test of rule 4,
    /// factored out so the two callers and the tests cannot drift apart.
    /// <para>
    /// The set on the item's side is <c>{ITEM_CLASS} union ItemClassMappings(itemId)</c>. The sets
    /// are tiny on both sides (a slot names 1-12 classes, an item carries 1 plus at most 5 mapped),
    /// so this is a nested scan over spans and allocates nothing; a <c>FrozenSet</c> per item would
    /// allocate 2,643 collections to express 482 facts.
    /// </para>
    /// </summary>
    public static bool Accepts(
        uint loadoutId,
        uint slotId,
        uint itemDefinitionId,
        uint primaryClass,
        bool foldClassMappings = true)
    {
        IReadOnlyList<uint> accepted = LoadoutSlotTable.ItemClasses(loadoutId, slotId);
        for (int i = 0; i < accepted.Count; i++)
        {
            if (accepted[i] == primaryClass)
            {
                return true;
            }
        }

        if (!foldClassMappings)
        {
            return false;
        }

        foreach (uint mapped in ItemClassMappings.For(itemDefinitionId))
        {
            for (int i = 0; i < accepted.Count; i++)
            {
                if (accepted[i] == mapped)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gameplay keeps Q for Field Bandages and E for First Aid Kits. The native mappings
    /// accept both in both slots, so this rule is separate from the raw sheet query.
    /// </summary>
    public static bool AcceptsPlacement(uint loadoutId, uint slotId, uint itemDefinitionId,
        uint primaryClass, bool foldClassMappings = true)
    {
        if (loadoutId == SurvivorLoadout.Id
            && ((itemDefinitionId == 2423 && slotId == SurvivorLoadout.QuickUse2)
                || (itemDefinitionId == 2424 && slotId == SurvivorLoadout.QuickUse1)))
            return false;
        return Accepts(loadoutId, slotId, itemDefinitionId, primaryClass, foldClassMappings);
    }

    /// <summary>
    /// The single <c>FLAG_AUTO_EQUIP</c> apparel slot of <paramref name="loadoutId"/> that accepts
    /// <paramref name="itemDefinitionId"/>, or 0 when there is none.
    /// <para>
    /// This is RULE 2 of <see cref="Resolve"/>, factored out so <c>PlayerInventory.Bootstrap</c>
    /// can place the starting outfit by the client's own rule rather than by a hard-coded table
    /// (docs/46 §7b). The wheel slots are excluded because <c>FLAG_AUTO_EQUIP</c> on a wheelable
    /// slot means the required Fists slot, not "the server fills this"; slot 43 is excluded because
    /// it is the hidden bag.
    /// </para>
    /// </summary>
    public static uint AutoEquipLoadoutSlot(
        uint itemDefinitionId,
        uint loadoutId = SurvivorLoadout.Id,
        bool foldClassMappings = true)
    {
        foreach (uint slotId in SupportingLoadoutSlots(itemDefinitionId, loadoutId, foldClassMappings))
        {
            if (LoadoutSlotTable.TryGet(loadoutId, slotId, out LoadoutSlotDefinition slot)
                && slot.AutoEquip
                && !slot.Wheelable
                && slotId != SurvivorLoadout.Inventory)
            {
                return slotId;
            }
        }

        return 0;
    }

    /// <summary>
    /// The body slot a worn or stowed item attaches to.
    /// <list type="bullet">
    /// <item>Apparel: <c>PASSIVE_EQUIP_SLOT_ID</c> of the item row (252 shirts -> 3, 146 hats and
    /// helmets -> 1, 109 pants -> 4, 63 boots -> 5, 61 masks -> 28, 57 backpacks -> 10,
    /// 39 armour -> 100, 34 goggles -> 29, 21 gloves -> 2).</item>
    /// <item>A weapon with <c>PASSIVE_EQUIP_SLOT_GROUP_ID = 1</c> (all 82 long guns): the lowest
    /// <em>free</em> slot of the group that accepts its <c>ITEM_CLASS</c> in
    /// <c>EquipSlotItemClasses.txt</c> - 25036/25037 -> {76, 77, 80}. That group rule is what puts
    /// a second rifle on 77 instead of colliding on 76 (docs/41 §1b).</item>
    /// <item>Anything else: its own <c>PASSIVE_EQUIP_SLOT_ID</c>, which is 0 for the Combat Knife,
    /// Hatchet, Crowbar, bats and axes - those simply show no attachment while stowed.</item>
    /// </list>
    /// </summary>
    public static uint PassiveBodySlot(InventoryItemFact fact, IReadOnlySet<uint> occupiedBodySlots)
    {
        ArgumentNullException.ThrowIfNull(occupiedBodySlots);
        if (fact.PassiveEquipSlotGroupId != 1)
        {
            return fact.PassiveEquipSlotId;
        }

        foreach (uint candidate in EquipmentSlotTable.StowSlotsForItemClass(fact.ItemClass))
        {
            if (!occupiedBodySlots.Contains(candidate))
            {
                return candidate;
            }
        }

        // Every slot of the group is taken. The item is still carried; it just shows no mesh.
        return 0;
    }

    /// <summary>
    /// Decide where an item goes. Pure: nothing on <paramref name="inventory"/> is changed.
    /// </summary>
    public static InventoryPlacement Resolve(
        PlayerInventory inventory,
        uint itemDefinitionId,
        uint count,
        bool preserveEquippedArmour = false)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (!InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact fact))
        {
            // Not a row of ClientItemDefinitions. Carry it, cost nothing, equip nothing.
            return ToContainer(inventory, fact, count, "item is not a ClientItemDefinitions row");
        }

        bool fold = inventory.Options.FoldItemClassMappings;
        uint medicalSlot = itemDefinitionId switch
        {
            PlayerInventory.StarterBandageItemDefinitionId => SurvivorLoadout.QuickUse1,
            2424 => SurvivorLoadout.QuickUse2,
            _ => 0,
        };
        if (medicalSlot != 0
            && inventory.Options.QuickUseConsumables && fold
            && inventory.LoadoutSlots.TryGetValue(medicalSlot, out var medical)
            && medical.DefinitionId == itemDefinitionId
            && (ulong)medical.Count + count <= (ulong)InventoryStacking.Maximum(fact))
        {
            return new(InventoryPlacementKind.Stack, "merge medical items into the dedicated quick-use stack",
                LoadoutSlotId: medicalSlot, StackTargetItemGuid: medical.Guid);
        }
        uint autoEquipSlot = AutoEquipLoadoutSlot(itemDefinitionId, inventory.LoadoutId, fold);

        // RULE 1 (docs/41 §5b). FLAG_CAN_EQUIP = 0 -> container, full stop. This is the gate that
        // sends ammunition, bandages and first aid kits (class 16053, 168 rows) to the bag.
        //
        // WAVE 8: this rule used to be justified as redundant - "class 16053 appears in no loadout-3
        // class set at all, so the two agree". Once ItemClassMappings is folded in that is no longer
        // true: the sheet gives 2423/2424 the classes 25009, 25054 and 25080, which ARE loadout
        // slot classes, so RULE 1 becomes the thing that decides where a first aid kit goes. It is
        // also NOT one of the four conditions the client's own FUN_140d35510 tests. See
        // InventoryOptions.QuickUseConsumables owns the narrow Q/E carve-out. The vehicle key
        // also has FLAG_CAN_EQUIP = 0, but its mapped class 25095 explicitly belongs to the
        // auto-equipped key slot (17, 48). Let that native binding reach RULE 2.
        if (!fact.CanEquip
            && autoEquipSlot != SurvivorLoadout.VehicleKey
            && !(inventory.Options.QuickUseConsumables
                && QuickUseSlot(itemDefinitionId, inventory, fold) != 0))
        {
            return ToContainer(inventory, fact, count,
                $"FLAG_CAN_EQUIP = 0 on item {itemDefinitionId} (class {fact.ItemClass})");
        }

        IReadOnlyList<uint> candidates =
            SupportingLoadoutSlots(itemDefinitionId, inventory.LoadoutId, fold);
        if (candidates.Count == 0)
        {
            return ToContainer(inventory, fact, count,
                $"no loadout-{inventory.LoadoutId} slot accepts ITEM_CLASS {fact.ItemClass}"
                + (fold ? " or any of its ItemClassMappings rows" : string.Empty)
                + " (LoadoutSlotItemClasses.txt)");
        }

        // RULE 2 (docs/41 §5b). An apparel item's candidate set is a single FLAG_AUTO_EQUIP slot
        // (10 Chest, 11 Head, 12 Backpack, 13 Feet, 14 Legs, 16 Hands, 25 Belt, 28 Face, 29 Eyes,
        // 30 Jacket, 38 ChestArmor). AUTO_EQUIP is precisely the client's marker for "the server
        // fills this without being asked", so a helmet goes on the head.
        if (autoEquipSlot is uint slotId and not 0
            && LoadoutSlotTable.TryGet(inventory.LoadoutId, slotId, out LoadoutSlotDefinition slot))
        {
            // The item's own PASSIVE_EQUIP_SLOT_ID wins over the loadout slot's EQUIP_SLOT_ID: they
            // agree on every apparel row, and the item column is the one docs/41 §5c derives from.
            uint bodySlot = fact.PassiveEquipSlotId != 0 ? fact.PassiveEquipSlotId : slot.EquipSlotId;
            ulong displaced = inventory.LoadoutSlots.TryGetValue(slotId, out InventoryItemInstance? occupant)
                ? occupant.Guid
                : 0;

            if (displaced != 0)
            {
                // Crafting another vest must preserve the armour the player chose to wear.
                // The crafted item still pays its normal cargo bulk and needs a free bag slot.
                if (preserveEquippedArmour && slotId == SurvivorLoadout.ChestArmor)
                    return ToContainer(inventory, fact, count, "armour slot is occupied; carry the crafted armour");

                // Swap, not drop. The displaced item needs bag room, and the server is the only
                // thing that will check that (docs/41 §4c).
                //
                // AGAINST THE CAPACITY THE SWAP LEAVES BEHIND, not the current one. When the slot
                // being taken is the backpack slot, the old pack's MAX_BULK leaves the bag at the
                // same instant the pack itself becomes cargo in it: swapping a Military Backpack
                // (PARAM1 28 -> 2000) for a plain one (PARAM1 22 -> 1000) costs 1000 capacity and
                // adds 500 bulk in one step. Testing against inventory.MaxBulkOf(bag) - the
                // capacity that is about to disappear - let a near-full bag go 1500 over its own
                // maximum without a single Container.Error, which is precisely the thing docs/41
                // §4c says nothing else will catch.
                InventoryItemInstance old = inventory.Items[displaced];
                if (slotId == SurvivorLoadout.Chest)
                    return ToContainer(inventory, fact, count, "chest clothing is occupied; carry the original garment");
                if (slotId == SurvivorLoadout.VehicleKey)
                    return ToContainer(inventory, fact, count, "key slot is occupied; carry the spare vehicle key");
                if (slotId == SurvivorLoadout.Head
                    && !(Combat.ArmourModel.IsHelmet(itemDefinitionId)
                        && !Combat.ArmourModel.IsHelmet(old.DefinitionId)))
                    return ToContainer(inventory, fact, count, "head slot is occupied; carry the spare helmet or hat");
                if (slotId == SurvivorLoadout.Feet
                    && Movement.Footwear.TierFor(itemDefinitionId) <= Movement.Footwear.TierFor(old.DefinitionId))
                    return ToContainer(inventory, fact, count, "equipped footwear is the same tier or better");
                if (slotId == SurvivorLoadout.Feet)
                    return new(InventoryPlacementKind.LoadoutSlot, "footwear upgrade drops the previous shoes",
                        LoadoutSlotId: slotId, EquipmentSlotId: bodySlot, DisplacedItemGuid: old.Guid,
                        GroundDrop: new(old.DefinitionId, old.DisplayDefinitionId, old.Count));
                if (slotId == SurvivorLoadout.Backpack
                    && PlayerInventory.MaxBulkProvidedBy(fact) <= PlayerInventory.MaxBulkProvidedBy(old.Fact))
                    // A pickup is not a request to shrink the equipped container. Spare packs
                    // cost their own bulk in the current bag; explicit Equip still permits swaps.
                    return ToContainer(inventory, fact, count, "equipped backpack has the same capacity or more; carry the spare backpack");

                InventoryPlacement room = ToContainer(
                    inventory,
                    old.Fact,
                    old.Count,
                    "displaced",
                    inventory.MaxBulkAfterSwap(slotId, itemDefinitionId));
                if (room.Kind == InventoryPlacementKind.Refused)
                {
                    return room with
                    {
                        Rule = $"loadout slot {slotId} holds item {old.DefinitionId} and the bag has "
                            + $"no room for it ({room.Rule})",
                    };
                }
            }

            return new InventoryPlacement(
                InventoryPlacementKind.LoadoutSlot,
                $"FLAG_AUTO_EQUIP loadout slot {slotId} accepts ITEM_CLASS {fact.ItemClass}; "
                + $"body slot {bodySlot} from PASSIVE_EQUIP_SLOT_ID",
                LoadoutSlotId: slotId,
                EquipmentSlotId: bodySlot,
                DisplacedItemGuid: displaced);
        }

        // RULE 3 (docs/41 §5b). Take the first free candidate in loadout-17 hotbar order:
        // Slot1(1), Slot2(2), Slot3(4), Slot5/binoculars(5), Q(40), E(41).
        // Never loadout slot 7: that is the required Slot5/Fists binding (FLAG_REQUIRED, ITEM_ID 85).
        // (Loadout slot 7 and BODY slot 7 are unrelated numbers that happen to collide; the body
        // slot is the one docs/45 forbids.)
        foreach (uint wheel in SurvivorLoadout.WheelOrder)
        {
            if (!candidates.Contains(wheel) || inventory.LoadoutSlots.ContainsKey(wheel)
                || !AcceptsPlacement(inventory.LoadoutId, wheel, itemDefinitionId, fact.ItemClass, fold))
            {
                continue;
            }

            var occupiedBodySlots = new HashSet<uint>(inventory.EquipmentSlots.Keys);

            // An item may only be WIELDED - body slot 7, the active hand - when it has a
            // third-person mesh to put in that hand. All 213 CODE_FACTORY_NAME = Weapon rows carry
            // ACTIVE_EQUIP_SLOT_ID = 7, but only 108 carry a non-empty MODEL_NAME (docs/45 §4):
            // definitions 10 ("AR-15") and 1991 ("R380") are model-less duplicate rows, and wielding
            // one attaches nothing, so the player holds an invisible gun. This is a COSMETIC rule
            // and deliberately not the crash guard - the docs/45 crash is item-independent (it fired
            // on 83, 1991 and 10 alike). The crash guard is Options.WieldFirstWeapon = false plus
            // ActiveHandRowGuard on the wire.
            bool hasHeldMesh = AugustWornVisuals.TryResolveMesh(
                itemDefinitionId,
                CharacterVisuals.Male,
                fact.ModelName,
                out _,
                out _);
            // The wave-10 follow-up (docs/95, D183): ACTIVE_EQUIP_SLOT_ID 7 is NOT "this is a
            // weapon". The Tactical First Aid
            // Kit (2424) and the Field Bandage (2423) carry it too, with real 3P meshes - and on
            // 2026-08-31 the kit took the hand, the guard correctly refused to put a fire-group-less
            // item on the wire, and the AR-15 picked up 1.8 s later found the hand full and went to
            // body slot 76. The owner's report: "the gun went on my back instead".
            //
            // So the model may only give the hand to an item whose binding can actually REACH the
            // wire, which is the same condition ActiveHandRowGuard applies: a CODE_FACTORY_NAME =
            // Weapon row whose datasheet names a non-zero fire group. Anything else stows, and the
            // server's idea of the hand cannot drift from the client's.
            bool canBeBoundOnTheWire = AugustWeaponTable.HasFireGroup(itemDefinitionId, out _);
            bool wield = inventory.Options.WieldFirstWeapon
                && fact.ActiveEquipSlotId != 0
                && canBeBoundOnTheWire
                && hasHeldMesh
                && (inventory.WieldedItemGuid == 0 || inventory.HandIsFists);

            uint bodySlot = wield ? fact.ActiveEquipSlotId : PassiveBodySlot(fact, occupiedBodySlots);
            string why = fact.ActiveEquipSlotId == 0 ? string.Empty
                : !inventory.Options.WieldFirstWeapon
                    ? "; not wielded: WieldFirstWeapon is off (docs/45 - a body-slot-7 "
                        + "EquipmentSlotRow crashes the client)"
                    : !canBeBoundOnTheWire
                        ? "; not wielded: no fire group resolves for the item, so a body-slot-7 row "
                            + "could never reach the wire (docs/95, D183 - this is what let a first aid kit "
                            + "take the hand and push a rifle onto the back)"
                        : !hasHeldMesh
                            ? "; not wielded: no third-person mesh resolves for the item"
                            : "; not wielded: the hand is already full";
            string how = wield
                ? $"hand is empty, so ACTIVE_EQUIP_SLOT_ID {fact.ActiveEquipSlotId} (RHand) - wielded"
                : $"stowed on body slot {bodySlot} "
                    + (fact.PassiveEquipSlotGroupId == 1
                        ? "(PASSIVE_EQUIP_SLOT_GROUP_ID 1 -> lowest free EquipSlotItemClasses slot)"
                        : "(PASSIVE_EQUIP_SLOT_ID)")
                    + why;

            return new InventoryPlacement(
                InventoryPlacementKind.LoadoutSlot,
                $"wheel loadout slot {wheel} is the lowest free slot accepting ITEM_CLASS "
                + $"{fact.ItemClass}; {how}",
                LoadoutSlotId: wheel,
                EquipmentSlotId: bodySlot,
                Wielded: wield);
        }

        // RULE 4 (docs/41 §5b). Every wheel slot is full. Carry it rather than displacing: picking
        // up a third rifle must not throw the held one away.
        return ToContainer(inventory, fact, count,
            $"every wheel slot accepting ITEM_CLASS {fact.ItemClass} is occupied");
    }

    /// <summary>
    /// The free quick-use wheel slot this item could take, or 0 - the carve-out
    /// <see cref="InventoryOptions.QuickUseConsumables"/> opens in RULE 1.
    /// <para>
    /// It only ever names Q or E, never an apparel slot and never the required Fists slot, so the
    /// carve-out can put a medical item on a quick-use tile and can do nothing else.
    /// </para>
    /// </summary>
    private static uint QuickUseSlot(uint itemDefinitionId, PlayerInventory inventory, bool fold)
    {
        foreach (uint wheel in SurvivorLoadout.WheelOrder)
        {
            if (inventory.LoadoutSlots.ContainsKey(wheel)
                || !LoadoutSlotTable.TryGet(inventory.LoadoutId, wheel, out LoadoutSlotDefinition slot)
                || (!slot.Wheelable
                    && slot.SlotInputAction is not ("ConsumeItem1" or "ConsumeItem2")))
            {
                continue;
            }

            if (!InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact fact))
            {
                return 0;
            }

            if (AcceptsPlacement(inventory.LoadoutId, wheel, itemDefinitionId, fact.ItemClass, fold))
            {
                return wheel;
            }
        }

        return 0;
    }

    /// <summary>
    /// The container path: stack merge, bulk check, slot allocation. Everything goes into the
    /// character's base bag (container definition 117), because that bag <em>is</em> the aggregate -
    /// its <c>IS_DYNAMIC_BULK</c> capacity is subsumed from the worn containers, which is why the
    /// client draws one inventory grid rather than one per garment (docs/41 §4b).
    /// </summary>
    private static InventoryPlacement ToContainer(
        PlayerInventory inventory,
        InventoryItemFact fact,
        uint count,
        string why,
        int? capacityOverride = null)
    {
        InventoryContainer? bag = inventory.BaseBag;
        if (bag is null)
        {
            // No container packet has been sent, so there is nowhere to put it. This is exactly the
            // state Cranberry shipped before this lane: ItemAdd with ContainerGuid = 0, which the
            // client's own enum calls UnknownContainer (docs/41 §0).
            return new InventoryPlacement(
                InventoryPlacementKind.Refused,
                $"{why}, but the character has no container - call PlayerInventory.Bootstrap first",
                Error: ContainerErrorCode.UnknownContainer);
        }

        // Bulk BEFORE stacking. InventoryContainer.BulkUsed is the sum of bulk x count over the
        // slots, and PlayerInventory.TryPickUp's Stack arm does a bare `instance.Count += count`, so
        // a merge costs exactly the same bulk as a new slot would. Testing bulk only on the
        // new-slot path let ammunition — MAX_STACK_SIZE 9999, and now carpeting the floor as
        // clustered boxes of the calibre already in the bag — walk straight past BaseCarryBulk
        // without a single Container.Error. The docstring above is explicit that the server is the
        // only authority here: if this class does not refuse an over-capacity pickup, nothing will.
        int max = capacityOverride ?? inventory.MaxBulkOf(bag);
        int cost = fact.Bulk * (int)count;
        if (bag.BulkUsed + cost > max)
        {
            // Error code: the client's CantMoveFullContainer string (0x143149578) belongs to the
            // CanMoveItem action's enum, NOT to the 0xc8/03 container error enum, so it cannot be
            // sent here. InteractionValidationFailed (6) is the container enum's own name for "the
            // server refused this interaction", which is precisely what happened.
            return new InventoryPlacement(
                InventoryPlacementKind.Refused,
                $"{why}, but bulk {bag.BulkUsed} + {cost} exceeds {max}",
                Error: ContainerErrorCode.InteractionValidationFailed);
        }

        // Stacking (docs/41 §5d): MAX_STACK_SIZE is 9999 on ammunition and 1 on weapons and
        // apparel, so only the former ever merges.
        if (InventoryStacking.Maximum(fact) > 1)
        {
            foreach (KeyValuePair<uint, InventoryItemInstance> slot in bag.Slots)
            {
                InventoryItemInstance held = slot.Value;
                if (held.DefinitionId == fact.DefinitionId
                    && (ulong)held.Count + count <= (ulong)InventoryStacking.Maximum(fact))
                {
                    return new InventoryPlacement(
                        InventoryPlacementKind.Stack,
                        $"{why}; merged into the existing stack in container slot {slot.Key} "
                        + $"(MAX_STACK_SIZE {fact.MaxStackSize})",
                        ContainerGuid: bag.Guid,
                        ContainerDefinitionId: bag.DefinitionId,
                        ContainerSlotId: slot.Key,
                        StackTargetItemGuid: held.Guid);
                }
            }
        }

        // The server is the only authority here (docs/41 §4c): the client renders the
        // maxBulk/bulkUsed we send and gates local drags, but a ground pickup is
        // Command.InteractRequest -> server and never passes through client validation. The bulk
        // test itself is above, before the stacking loop, so it covers both arms.
        if (bag.Slots.Count >= bag.Definition.MaximumSlots)
        {
            return new InventoryPlacement(
                InventoryPlacementKind.Refused,
                $"{why}, but the container is full ({bag.Definition.MaximumSlots} slots)",
                Error: ContainerErrorCode.UnknownContainerSlot);
        }

        return new InventoryPlacement(
            InventoryPlacementKind.Container,
            why,
            ContainerGuid: bag.Guid,
            ContainerDefinitionId: bag.DefinitionId,
            ContainerSlotId: bag.NextFreeSlot());
    }
}
