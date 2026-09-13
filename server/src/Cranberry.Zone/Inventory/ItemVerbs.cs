using Cranberry.Zone.Crafting;

namespace Cranberry.Zone.Inventory;

/// <summary>
/// The five context-menu verbs wave 8 answered with silence - <c>RemoveItem</c>, <c>MoveItem</c>,
/// <c>EquipItem</c>, <c>SalvageItem</c> and <c>UnloadWeapon</c> - resolved against the inventory
/// model. Pure: <see cref="Apply"/> is the only thing here that changes anything.
///
/// <para>
/// <b>Why this file exists (docs/86).</b> The August client offers seven verbs across 2,345 items.
/// Cranberry answered <b>two</b>, and one of those (<c>ConsumeItem</c>) ships off. The most-offered
/// option in the entire build - <c>RemoveItem</c> (12), on <b>1,382</b> items, more than
/// <c>DropItem</c>'s 1,365 - reached
/// <c>_ =&gt; NotImplemented("no server behaviour is derived for this action yet")</c> and sent
/// nothing at all. Right-clicking a tile did nothing five times out of seven, and the owner's
/// session of 30 Aug shows him opening the inventory window eleven times in five minutes hunting
/// for a verb that worked. That - not one broken packet - is "inventory isn't working correctly".
/// </para>
///
/// <para>
/// <b>What is ported and what is not (D53).</b> The <em>semantics</em> are the owner's own, from
/// <c>C:\Z1\Server\Zone\ZoneItemUse.cs</c> (<c>Remove</c>, <c>Move</c>, <c>Salvage</c>) and
/// <c>C:\Z1\Server\Zone\ZoneInventoryActions.cs</c> (<c>TryEquip</c>, <c>TryUnequip</c>,
/// <c>UnequipRefusal</c>), re-expressed against 1148 and against Cranberry's own primitives. His
/// dispatcher keys on the client's own <c>TYPE_NAME</c> rather than on option ids, so a use-option
/// group nobody censused still lands in the right handler; Cranberry's
/// <see cref="ItemUseOptionKind"/> <em>is</em> that column, generated from the <b>August</b> sheet,
/// so the shape ports onto August data with no new derivation. <b>No value and no datum crosses</b>
/// - where his 1087 table and the August sheet disagree (option 63's <c>BUSY_MSEC</c>: his 3000,
/// August's 1000) the August sheet wins.
/// </para>
///
/// <para>
/// <b>Two earlier hypotheses are falsified here rather than inherited.</b> docs/63 §5.3 concluded
/// <c>MoveItem</c> must ride a different sub because no observed packet form has room for a
/// destination - but his <c>MoveItem</c> carries no destination <em>because it is a reciprocal
/// toggle</em>. And his own <c>d0 02</c> cast bar and <c>f1 01</c> access burst are traps in August,
/// where 0xd0 is <c>TimedGrantBase</c> and 0xf1 is <c>ShaderParameterOverrideBase</c>.
/// </para>
///
/// <para>
/// <b>D29.</b> The requests these answer are client-originated and therefore live. Nothing below has
/// been seen to land - every reply is BUILT and TESTED, never LIVE-VERIFIED.
/// </para>
/// </summary>
public static class ItemVerbs
{
    /// <summary>
    /// <c>RemoveItem</c> (12) - take a worn item off into the bag; on an item that is already in the
    /// bag, repaint the tile.
    /// <para>
    /// Ported from <c>ZoneItemUse.Remove</c>. His comment on the second half is the whole reason it
    /// is not a no-op: <em>"the client asked for a move it has already drawn locally, so the right
    /// answer is the repaint that settles the tile where the server says it is. Answering with
    /// silence is what makes the tile snap back a second later with no explanation."</em>
    /// </para>
    /// </summary>
    public static ItemActionResult Remove(
        PlayerInventory inventory,
        InventoryItemInstance item,
        ItemUseOptionKind option)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.LoadoutSlotId != 0
            ? Unequip(inventory, item, option)
            : Repaint(item, option, "it is already in the carrying container");
    }

    /// <summary>
    /// <c>MoveItem</c> (61) - the reciprocal of <see cref="Remove"/>: worn goes to the bag, and a
    /// bagged item that can be equipped goes to the loadout. Ported from <c>ZoneItemUse.Move</c>.
    /// <para>
    /// This is the half that gives a player a way to arm himself from the panel at all, and it is
    /// why docs/63 §5.3's "the destination must ride in a longer third form" was looking for a field
    /// that was never there.
    /// </para>
    /// </summary>
    public static ItemActionResult Move(
        PlayerInventory inventory,
        InventoryItemInstance item,
        ItemUseOptionKind option)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.LoadoutSlotId != 0)
        {
            return Unequip(inventory, item, option);
        }

        return item.Fact.CanEquip
            ? Equip(inventory, item, option)
            : Repaint(
                item,
                option,
                $"item {item.DefinitionId} is FLAG_CAN_EQUIP = 0 and there is nowhere else for it "
                + "to go - this server models one carrying container");
    }

    /// <summary>
    /// <c>EquipItem</c> (60) - put a bagged item into the loadout slot its own class row names, the
    /// same rule <see cref="InventoryAutoAssign.Resolve"/> applies to a pickup. Ported from
    /// <c>ZoneInventoryActions.TryEquip</c>.
    /// </summary>
    public static ItemActionResult Equip(
        PlayerInventory inventory,
        InventoryItemInstance item,
        ItemUseOptionKind option,
        uint destinationSlot = 0)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(item);

        if (item.LoadoutSlotId != 0 && (destinationSlot == 0 || destinationSlot == item.LoadoutSlotId))
        {
            return Repaint(item, option, $"it is already in loadout slot {item.LoadoutSlotId}");
        }

        if (!item.Fact.CanEquip)
        {
            return Refused(
                option,
                item,
                $"FLAG_CAN_EQUIP is 0 on item {item.DefinitionId}, so the client should not have "
                + "offered Equip",
                ContainerErrorCode.WrongItemType);
        }

        if (!TryResolveEquipSlot(inventory, item, out uint loadoutSlot, out uint bodySlot, destinationSlot))
        {
            return Refused(
                option,
                item,
                $"no free loadout-{inventory.LoadoutId} slot accepts ITEM_CLASS "
                + $"{item.Fact.ItemClass} (LoadoutSlotItemClasses.txt)",
                ContainerErrorCode.WrongItemType);
        }

        // The occupant comes off FIRST, so nothing is ever destroyed by an equip - his own rule
        // (TryEquip's displaced-item path). Where he differs from wave 8 is that he applies NO bulk
        // test to the displaced item at all.
        ulong displacedGuid = 0;
        uint displacedDefinition = 0;
        uint displacedCount = 0;
        bool displacedToGround = false;
        string swap = string.Empty;
        if (inventory.LoadoutSlots.TryGetValue(loadoutSlot, out InventoryItemInstance? occupant))
        {
            if (item.LoadoutSlotId is 1 or 2 or 4 && loadoutSlot is 1 or 2 or 4
                && InventoryAutoAssign.AcceptsPlacement(inventory.LoadoutId, item.LoadoutSlotId,
                    occupant.DefinitionId, occupant.Fact.ItemClass, inventory.Options.FoldItemClassMappings))
            {
                return new(ItemActionKind.Equip, option, "exchange occupied weapon slots",
                    ItemGuid: item.Guid, DefinitionId: item.DefinitionId, Count: item.Count,
                    RemainingCount: item.Count, ClearedLoadoutSlotId: item.LoadoutSlotId,
                    BoundLoadoutSlotId: loadoutSlot, DisplacedItemGuid: occupant.Guid,
                    DisplacedDefinitionId: occupant.DefinitionId, DisplacedCount: occupant.Count,
                    SwapLoadoutSlots: true);
            }
            displacedGuid = occupant.Guid;
            displacedDefinition = occupant.DefinitionId;
            displacedCount = occupant.Count;

            int need = occupant.Fact.Bulk * (int)Math.Max(occupant.Count, 1u);
            int maxAfter = inventory.MaxBulkAfterSwap(loadoutSlot, item.DefinitionId);
            // The incoming item stops being cargo the instant it is worn, so its own bulk comes off
            // the used figure the displaced item has to fit inside.
            int usedAfter = (inventory.BaseBag?.BulkUsed ?? 0)
                - (item.ContainerGuid == inventory.BaseBag?.Guid ? item.Bulk : 0);
            bool fits = usedAfter + need <= maxAfter;
            bool dropFootwear = loadoutSlot == SurvivorLoadout.Feet;

            if (!fits && !dropFootwear && !inventory.Options.SwapNeverRefusedForBulk)
            {
                return Refused(
                    option,
                    item,
                    $"loadout slot {loadoutSlot} holds item {occupant.DefinitionId} and the bag has "
                    + $"no room for it (bulk {usedAfter} + {need} exceeds {maxAfter}); "
                    + "InventoryOptions.SwapNeverRefusedForBulk is off",
                    ContainerErrorCode.InteractionValidationFailed);
            }

            displacedToGround = dropFootwear || !fits;
            swap = dropFootwear ? "; previous footwear is dropped on the ground" : fits
                ? $"; item {occupant.DefinitionId} displaced into the bag"
                : $"; item {occupant.DefinitionId} displaced onto the GROUND - it is {need} bulk "
                    + $"against {maxAfter - usedAfter} free, and an equip is never refused for bulk "
                    + "(SwapNeverRefusedForBulk)";
        }

        return new ItemActionResult(
            ItemActionKind.Equip,
            option,
            $"{option} item {item.DefinitionId} (instance {item.Guid}) -> loadout slot "
            + $"{loadoutSlot}, body slot {bodySlot}{swap}",
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId,
            Count: item.Count,
            RemainingCount: item.Count,
            ClearedLoadoutSlotId: item.LoadoutSlotId,
            ClearedEquipmentSlotId: item.EquipmentSlotId,
            BoundLoadoutSlotId: loadoutSlot,
            BoundEquipmentSlotId: bodySlot,
            DisplacedItemGuid: displacedGuid,
            DisplacedDefinitionId: displacedDefinition,
            DisplacedCount: displacedCount,
            DisplacedToGround: displacedToGround);
    }

    /// <summary>
    /// Take an item out of a loadout slot and put it in the bag. Ported from
    /// <c>ZoneInventoryActions.TryUnequip</c> plus <c>ZoneItemUse.UnequipRefusal</c> - his refusal
    /// text is the point of that second method: <em>"from the chair all three look like a dead menu,
    /// and the commonest one is not a bug at all"</em>.
    /// </summary>
    public static ItemActionResult Unequip(
        PlayerInventory inventory,
        InventoryItemInstance item,
        ItemUseOptionKind option)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(item);

        if (item.LoadoutSlotId == 0)
        {
            return Repaint(item, option, "it is not in a loadout slot");
        }

        // The same two slots the drop path refuses, and for the same reasons: the bag is what the
        // whole grid hangs off, and an empty hand is worse than fists.
        if (item.LoadoutSlotId is SurvivorLoadout.Inventory or SurvivorLoadout.Fists)
        {
            return Refused(
                option,
                item,
                item.LoadoutSlotId == SurvivorLoadout.Fists
                    ? "you cannot put your fists away (loadout slot 7 is FLAG_REQUIRED with "
                        + "ITEM_ID 85)"
                    : "that is the bag everything else is in - equip a different one instead "
                        + "(loadout slot 43)",
                ContainerErrorCode.WrongItemType);
        }

        if (inventory.BaseBag is not InventoryContainer bag)
        {
            return Refused(
                option,
                item,
                "there is no carrying container to put it in",
                ContainerErrorCode.UnknownContainer);
        }

        // His bulk test, and his refusal wording, because the commonest answer is not a defect:
        // every gun in the sheet is BULK 1500 against a carrier of 200, which is exactly why the
        // weapon slots exist. Measured against the capacity the unequip LEAVES BEHIND - taking a
        // backpack off removes its MAX_BULK at the same instant the pack becomes cargo.
        int need = item.Fact.Bulk * (int)Math.Max(item.Count, 1u);
        int maxAfter = inventory.MaxBulkAfterSwap(item.LoadoutSlotId, 0);
        if (bag.BulkUsed + need > maxAfter)
        {
            return Refused(
                option,
                item,
                $"no room: item {item.DefinitionId} is {need} bulk and the bag holds "
                + $"{bag.BulkUsed}/{maxAfter} once this slot is empty"
                + (item.Fact.CodeFactory == ItemCodeFactory.Weapon
                    ? ". A weapon never fits in a KOTK bag - that is what the weapon slots are "
                        + "for. Equip another gun into this slot, or drop this one."
                    : string.Empty),
                ContainerErrorCode.InteractionValidationFailed);
        }

        return new ItemActionResult(
            ItemActionKind.Unequip,
            option,
            $"{option} item {item.DefinitionId} (instance {item.Guid}) out of loadout slot "
            + $"{item.LoadoutSlotId} into the bag ({need} bulk against {maxAfter - bag.BulkUsed} "
            + "free)",
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId,
            Count: item.Count,
            RemainingCount: item.Count,
            ClearedLoadoutSlotId: item.LoadoutSlotId,
            ClearedEquipmentSlotId: item.EquipmentSlotId);
    }

    /// <summary>
    /// <c>SalvageItem</c> (6 and 63) - the client's SHRED. Ported from <c>ZoneItemUse.Salvage</c>,
    /// but scoped to apparel: his <c>SalvageAmmo</c> yields cite a tree this project may not open,
    /// and the August sheets carry no <c>ShreddableItemDatasheet</c> rows at all, so the yields come
    /// from Cranberry's own August-derived <see cref="ShredTable.Yields"/> and nothing else.
    /// <para>
    /// The execution has been written and green since wave 6; only the trigger was missing.
    /// </para>
    /// </summary>
    public static ItemActionResult Salvage(
        PlayerInventory inventory,
        InventoryItemInstance item,
        ItemUseOptionKind option,
        uint optionId)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(item);

        if (item.LoadoutSlotId is SurvivorLoadout.Inventory or SurvivorLoadout.Fists)
        {
            return Refused(
                option,
                item,
                $"loadout slot {item.LoadoutSlotId} is the survivor's own bag/hands",
                ContainerErrorCode.WrongItemType);
        }

        // D260: with InventoryOptions.ShredEveryOfferedItem on, every one of the 645 items whose own
        // ItemUseOptionGroup offers SalvageItem has a yield, so this refusal can no longer fire for
        // an item the client's own context menu offered Shred on. It still fires for body armour and
        // for anything a forged request names, because InventoryActions.Resolve's
        // ItemUseOptionTable.Allows gate is what makes the class table a harmless superset.
        if (!ShredTable.IsShreddable(
                item.DefinitionId, out RecipeIngredient yield, inventory.Options.ShredEveryOfferedItem))
        {
            return Refused(
                option,
                item,
                $"there is no shred yield for ITEM_CLASS {item.Fact.ItemClass} "
                + "(ShredTable.Yields; body armour 25041 carries no shred option in the client's "
                + "own sheet either)",
                ContainerErrorCode.WrongItemType);
        }

        // The client's own BUSY_MSEC for the option that was actually clicked, not a constant.
        // August says 1000 on both 6 and 63; the owner's 1087 table says 3000 on 63 and the August
        // sheet wins (docs/86 §2.4).
        bool hasOption = ItemUseOptionTable.TryGet(optionId, out ItemUseOptionDefinition row);
        int busy = hasOption ? row.BusyMsec : ShredTable.BusyMilliseconds;

        return new ItemActionResult(
            ItemActionKind.Shred,
            option,
            $"{option} item {item.DefinitionId} (instance {item.Guid}) -> {yield.Quantity} x item "
            + $"{yield.ItemDefinitionId}, BUSY_MSEC {busy} from ItemUseOptions row {optionId}",
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId,
            Count: 1,
            RemainingCount: item.Count - 1,
            BusyMilliseconds: busy,
            InteractionAnimationId: hasOption ? row.InteractionAnimationId : 10);
    }

    /// <summary>Skin equipped weapons and apparel owned by this inventory, including spare helmets.</summary>
    public static ItemActionResult Skin(PlayerInventory inventory, InventoryItemInstance item)
    {
        if (!inventory.Items.TryGetValue(item.Guid, out var owned) || !ReferenceEquals(owned, item)
            || !Appearance.AugustWornSkins.TryGetCategory(item.DefinitionId, out uint category))
            return Refused(ItemUseOptionKind.SkinItem, item,
                "the item is not owned or has no cosmetic category", ContainerErrorCode.WrongItemType);

        bool weapon = item.LoadoutSlotId is SurvivorLoadout.Wheel1 or SurvivorLoadout.Wheel2 or SurvivorLoadout.Wheel3
            && inventory.LoadoutSlots.TryGetValue(item.LoadoutSlotId, out var equipped) && equipped.Guid == item.Guid
            && AugustSkinCatalog.Weapons.Any(s => s.CategoryPrototypeId == category);
        bool apparel = AugustSkinCatalog.Apparel.Any(s => s.CategoryPrototypeId == category)
            && (item.LoadoutSlotId == 0 && inventory.BaseBag?.Guid == item.ContainerGuid
                || inventory.EquipmentSlots.TryGetValue(item.EquipmentSlotId, out var worn)
                    && worn.Guid == item.Guid);
        if (!weapon && !apparel)
            return Refused(ItemUseOptionKind.SkinItem, item,
                "this equipped item has no compatible cosmetic category", ContainerErrorCode.WrongItemType);

        return new(ItemActionKind.Skin, ItemUseOptionKind.SkinItem, "choose a skin for this inventory item",
            ItemGuid: item.Guid, DefinitionId: item.DefinitionId, Count: item.Count, RemainingCount: item.Count);
    }

    /// <summary>
    /// Raise or lower a worn hoodie. A head-slot item blocks raising; equipping one later lowers
    /// the hood centrally in <see cref="PlayerInventory.BindLoadout"/>.
    /// </summary>
    public static ItemActionResult SetHood(
        PlayerInventory inventory,
        InventoryItemInstance item,
        ItemUseOptionKind option)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(item);

        bool up = option == ItemUseOptionKind.HoodieUp;
        if (option is not (ItemUseOptionKind.HoodieUp or ItemUseOptionKind.HoodieDown)
            || item.LoadoutSlotId == 0
            || item.EquipmentSlotId != BodySlots.Chest
            || !ItemUseOptionTable.Allows(item.DisplayDefinitionId, 96)
            || !ItemUseOptionTable.Allows(item.DisplayDefinitionId, 97))
        {
            return Refused(
                option,
                item,
                "the hood can only be changed on the hoodie currently worn in the chest slot",
                ContainerErrorCode.WrongItemType);
        }

        if (up && inventory.EquipmentSlots.ContainsKey(BodySlots.Head))
        {
            return Refused(
                option,
                item,
                "remove the hat or helmet before raising the hood",
                ContainerErrorCode.InteractionValidationFailed);
        }

        return new ItemActionResult(
            ItemActionKind.Hood,
            option,
            $"{option} on worn hoodie {item.DefinitionId} (instance {item.Guid})",
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId,
            Count: item.Count,
            RemainingCount: item.Count);
    }

    /// <summary>
    /// <c>UnloadWeapon</c> (7 and 51) - <b>the closing of D118</b>. Take the rounds out of the
    /// weapon's magazine and put them in the bag as a stack of its own calibre.
    /// <para>
    /// <b>Why this is safe now and was not before.</b> D118 refused it in one sentence: <i>"unload /
    /// reload / unload would mint ammunition"</i>, because <c>ShooterCombatState.Reload</c> refilled
    /// the magazine out of nothing. With <see cref="IAmmoStore"/> feeding the reload, every round in
    /// a magazine was taken out of this same bag, so putting it back is conservative by
    /// construction - which is exactly the property the owner's own <c>ZoneItemUse.Unload</c> relies
    /// on (S6 §4.5: <i>"grant rounds x AmmoItemId to the bag ... re-announce the gun with magazine
    /// 0"</i>).
    /// </para>
    /// <para>
    /// The weapon itself does not move: the caller re-announces the gun (so its tile stops claiming
    /// a loaded magazine), the new or grown ammunition stack, and the container.
    /// </para>
    /// </summary>
    public static ItemActionResult Unload(
        PlayerInventory inventory,
        InventoryItemInstance item,
        ItemUseOptionKind option,
        IWeaponMagazines magazines)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(magazines);

        uint ammoItemId = Combat.AmmoTypes.AmmoItemFor(item.DefinitionId);

        if (ammoItemId == 0)
        {
            return Refused(
                option,
                item,
                "the August client's own data pairs no round with this item, so there is nothing "
                + "an unload could produce (AmmoTypes)",
                ContainerErrorCode.WrongItemType);
        }

        int rounds = magazines.MagazineOf(item.Guid);

        if (rounds <= 0)
        {
            return Repaint(
                item,
                option,
                rounds < 0
                    ? "this session has never seen that weapon instance fire or reload, so its "
                        + "magazine is not being tracked"
                    : "its magazine is already empty");
        }

        var changes = new List<AmmoStackChange>();

        if (!new PlayerInventoryAmmoStore(inventory).Grant(ammoItemId, rounds, changes)
            || changes.Count == 0)
        {
            // Nothing was granted, so nothing may be taken out of the magazine either - the two
            // halves are one operation or neither.
            return Refused(
                option,
                item,
                $"there is no room in the bag for {rounds} round(s) of item {ammoItemId}",
                ContainerErrorCode.InteractionValidationFailed);
        }

        int emptied = magazines.UnloadMagazine(item.Guid);
        AmmoStackChange granted = changes[0];

        return new ItemActionResult(
            ItemActionKind.Unload,
            option,
            $"{option} on item {item.DefinitionId} (instance {item.Guid}): {emptied} round(s) of "
            + $"item {ammoItemId} out of the magazine and into the bag as instance "
            + $"{granted.ItemGuid} ({granted.CountAfter} total)",
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId,
            Count: item.Count,
            RemainingCount: item.Count,
            DisplacedItemGuid: granted.ItemGuid,
            DisplacedDefinitionId: ammoItemId,
            DisplacedCount: granted.CountAfter,
            DisplacedCreated: granted.Created);
    }

    /// <summary>
    /// Answer a verb this server will not perform with a <b>named</b> refusal rather than silence.
    /// His rule, and the difference between a menu entry that explains itself and one that looks
    /// broken.
    /// </summary>
    public static ItemActionResult Refused(
        ItemUseOptionKind option,
        InventoryItemInstance item,
        string why,
        ContainerErrorCode error)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ItemActionResult(
            ItemActionKind.Refused,
            option,
            $"{option} on item {item.DefinitionId} (instance {item.Guid}): {why}",
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId,
            Error: error);
    }

    /// <summary>
    /// Nothing moved, and that is the answer: re-announce the tile and the container so the move the
    /// client already drew locally settles where the server says it is.
    /// </summary>
    public static ItemActionResult Repaint(
        InventoryItemInstance item,
        ItemUseOptionKind option,
        string why)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ItemActionResult(
            ItemActionKind.Repaint,
            option,
            $"{option} on item {item.DefinitionId} (instance {item.Guid}): nothing to move - {why}; "
            + "re-announcing the tile and the container so it settles",
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId,
            Count: item.Count,
            RemainingCount: item.Count);
    }

    /// <summary>
    /// Perform the mutation a plan describes. <see cref="ItemActionKind.Shred"/> is deliberately NOT
    /// applied here - it runs through <c>ShredTable.Shred</c>, which owns the yield grant and its own
    /// rollback, on the caller's busy clock.
    /// </summary>
    public static void Apply(PlayerInventory inventory, ItemActionResult plan)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(plan);
        if (!inventory.Items.TryGetValue(plan.ItemGuid, out InventoryItemInstance? item))
        {
            return;
        }

        switch (plan.Kind)
        {
            case ItemActionKind.Hood:
                _ = inventory.TrySetHood(plan.Option == ItemUseOptionKind.HoodieUp);
                return;

            case ItemActionKind.Unequip:
                _ = inventory.TryStow(item);
                return;

            case ItemActionKind.Equip:
                if (plan.SwapLoadoutSlots
                    && inventory.Items.TryGetValue(plan.DisplacedItemGuid, out var exchanged))
                {
                    inventory.ExchangeWeaponSlots(item, exchanged);
                    return;
                }
                if (plan.DisplacedItemGuid != 0
                    && inventory.Items.TryGetValue(
                        plan.DisplacedItemGuid, out InventoryItemInstance? displaced))
                {
                    if (plan.DisplacedToGround)
                    {
                        // It leaves the inventory entirely; the caller spawns the world object.
                        // Done BEFORE the bind so the slot is vacant either way.
                        inventory.Unbind(displaced);
                        inventory.RemoveUnits(plan.DisplacedItemGuid, 0);
                    }
                    else
                    {
                        _ = inventory.TryStow(displaced);
                    }
                }

                // BindLoadout takes the item out of whichever container held it.
                inventory.Unbind(item);
                inventory.BindLoadout(item, plan.BoundLoadoutSlotId, plan.BoundEquipmentSlotId);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// The loadout and body slot an equip resolves to - <see cref="InventoryAutoAssign.Resolve"/>'s
    /// RULE 2 (the item's single <c>FLAG_AUTO_EQUIP</c> apparel slot) and then RULE 3 (the lowest
    /// free wheel slot), applied to an item the player is already holding.
    /// <para>
    /// <b>Regression guard 5 is enforced twice here.</b> Loadout slot 7 can never be a destination -
    /// his rule verbatim, <em>"FISTS is never a destination"</em> - and a resolved BODY slot of 7 is
    /// dropped to 0, because an <c>EquipmentSlotRow</c> naming body slot 7 hard-crashes the August
    /// client while no fire-group data is on the wire (docs/45). This path therefore never wields;
    /// wielding is <c>InventoryOptions.WieldFirstWeapon</c>'s to open, not a context menu's.
    /// </para>
    /// </summary>
    private static bool TryResolveEquipSlot(
        PlayerInventory inventory,
        InventoryItemInstance item,
        out uint loadoutSlotId,
        out uint equipmentSlotId,
        uint destinationSlot = 0)
    {
        loadoutSlotId = 0;
        equipmentSlotId = 0;
        bool fold = inventory.Options.FoldItemClassMappings;
        uint definitionId = item.DefinitionId;

        if (destinationSlot != 0)
        {
            if (destinationSlot is SurvivorLoadout.Fists or SurvivorLoadout.Inventory
                || !InventoryAutoAssign.AcceptsPlacement(inventory.LoadoutId, destinationSlot,
                    definitionId, item.Fact.ItemClass, fold)
                || !LoadoutSlotTable.TryGet(inventory.LoadoutId, destinationSlot, out var destination))
                return false;

            loadoutSlotId = destinationSlot;
            var occupied = new HashSet<uint>(inventory.EquipmentSlots
                .Where(pair => pair.Value.Guid != item.Guid && pair.Value.LoadoutSlotId != destinationSlot)
                .Select(pair => pair.Key));
            equipmentSlotId = destination.AutoEquip
                ? (item.Fact.PassiveEquipSlotId != 0 ? item.Fact.PassiveEquipSlotId : destination.EquipSlotId)
                : InventoryAutoAssign.PassiveBodySlot(item.Fact, occupied);
            return NeverTheActiveHand(ref equipmentSlotId);
        }

        uint apparel = InventoryAutoAssign.AutoEquipLoadoutSlot(definitionId, inventory.LoadoutId, fold);
        if (apparel is not 0 and not SurvivorLoadout.Fists
            && LoadoutSlotTable.TryGet(inventory.LoadoutId, apparel, out LoadoutSlotDefinition slot))
        {
            loadoutSlotId = apparel;
            equipmentSlotId = item.Fact.PassiveEquipSlotId != 0
                ? item.Fact.PassiveEquipSlotId
                : slot.EquipSlotId;
            return NeverTheActiveHand(ref equipmentSlotId);
        }

        IReadOnlyList<uint> candidates =
            InventoryAutoAssign.SupportingLoadoutSlots(definitionId, inventory.LoadoutId, fold);
        foreach (uint wheel in SurvivorLoadout.WheelOrder)
        {
            if (wheel == SurvivorLoadout.Fists
                || !candidates.Contains(wheel)
                || !InventoryAutoAssign.AcceptsPlacement(inventory.LoadoutId, wheel,
                    definitionId, item.Fact.ItemClass, fold)
                || inventory.LoadoutSlots.ContainsKey(wheel))
            {
                continue;
            }

            loadoutSlotId = wheel;
            equipmentSlotId = InventoryAutoAssign.PassiveBodySlot(
                item.Fact, new HashSet<uint>(inventory.EquipmentSlots.Keys));
            return NeverTheActiveHand(ref equipmentSlotId);
        }

        return false;
    }

    /// <summary>
    /// Guard 5, applied to every slot this file resolves: an <c>EquipmentSlotRow</c> naming body
    /// slot 7 is the one packet shape known to hard-crash the August client (docs/45 §5b), so a
    /// context menu never produces one. The item is still equipped; only the hand binding is
    /// withheld, exactly as <see cref="InventoryAutoAssign.Resolve"/> RULE 3 does with
    /// <c>WieldFirstWeapon</c> off.
    /// </summary>
    private static bool NeverTheActiveHand(ref uint bodySlot)
    {
        if (bodySlot == ActiveHandBodySlot)
        {
            bodySlot = 0;
        }

        return true;
    }

    /// <summary>Body slot 7, RHand. Never bound from this file.</summary>
    private const uint ActiveHandBodySlot = 7;
}
