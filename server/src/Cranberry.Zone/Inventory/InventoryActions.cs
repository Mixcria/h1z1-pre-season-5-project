namespace Cranberry.Zone.Inventory;

/// <summary>What the server decided to do about one <see cref="RequestUseItem"/>.</summary>
public enum ItemActionKind
{
    /// <summary>Nothing happened. <see cref="ItemActionResult.Error"/> says why, and it is sent.</summary>
    Refused,

    /// <summary>
    /// The action is a real one the client offered, but this build does not implement it yet. The
    /// caller logs it by name and sends nothing - an unanswered request leaves the item exactly
    /// where it was, which is what the client already shows.
    /// </summary>
    NotImplemented,

    /// <summary>The item left the inventory and must now be spawned on the ground.</summary>
    Drop,

    /// <summary>The item was consumed: one unit gone, no world object.</summary>
    Consume,

    /// <summary>
    /// <c>SalvageItem</c>. The caller runs <c>Crafting.ShredTable.Shred</c> on the named guid inside
    /// the option's own <c>BUSY_MSEC</c> window; <see cref="InventoryActions.Apply"/> deliberately
    /// does nothing, because the shred owns its own grant and its own rollback.
    /// </summary>
    Shred,

    /// <summary>The worn hoodie's model/appearance pair changes without moving the item.</summary>
    Hood,

    /// <summary>Remember the equipped weapon targeted by the next skin selection.</summary>
    Skin,

    /// <summary>
    /// The item moved from the bag into <see cref="ItemActionResult.BoundLoadoutSlotId"/>. The
    /// caller re-announces it (delete then add, his order), the loadout, and the bag - and, when
    /// <see cref="ItemActionResult.DisplacedToGround"/> is set, spawns the displaced item on the
    /// floor.
    /// </summary>
    Equip,

    /// <summary>
    /// The item came out of its loadout slot into the bag.
    /// <see cref="ItemActionResult.ClearedLoadoutSlotId"/> and
    /// <see cref="ItemActionResult.ClearedEquipmentSlotId"/> say what it vacated.
    /// </summary>
    Unequip,

    /// <summary>
    /// <c>UnloadWeapon</c>. The weapon's magazine went back into the bag as a stack.
    /// <see cref="ItemActionResult.DisplacedItemGuid"/> is that stack (new or merged),
    /// <see cref="ItemActionResult.DisplacedDefinitionId"/> the round and
    /// <see cref="ItemActionResult.DisplacedCount"/> its size afterwards; the weapon itself has not
    /// moved, so the caller re-announces both items and the container.
    /// <para>
    /// Reachable only when the caller supplies an <see cref="IWeaponMagazines"/> to
    /// <c>InventoryActions.Resolve</c>. Without one the verb keeps D118's named refusal, because a
    /// server whose reload refills the magazine for free would let a player mint rounds by
    /// unloading it.
    /// </para>
    /// </summary>
    Unload,

    /// <summary>
    /// Nothing changed, and that IS the answer: the client asked for a move it has already drawn
    /// locally, so the server re-announces the tile and the container to settle it where the server
    /// says it is. His rule - <em>"answering with silence is what makes the tile snap back a second
    /// later with no explanation"</em> (<c>ZoneItemUse.Remove</c>).
    /// </summary>
    Repaint,

    /// <summary>An authorized native /item delete removes the instance without a ground object or medical effect.</summary>
    Delete,
}

/// <summary>
/// The outcome of one item action, and everything the caller needs to put it on the wire.
/// </summary>
/// <param name="DefinitionId">The item that moved - what the ground object must be spawned as.</param>
/// <param name="Count">How many units left the inventory.</param>
/// <param name="RemainingCount">
/// What is left on that instance afterwards. <b>Zero means the instance is gone</b> and the caller
/// sends <c>ClientUpdate.ItemDelete (11 04)</c>; non-zero means a partial stack and the caller
/// re-sends <c>ClientUpdate.ItemAdd (11 02)</c> for the same guid at the new count, which
/// <c>FUN_140c35400</c> treats as a change rather than an add (docs/46 §6a).
/// </param>
/// <param name="ClearedLoadoutSlotId">
/// The loadout slot the item vacated, or 0. Non-zero means the caller must re-send the whole
/// loadout (<c>Loadouts.SetLoadoutSlots</c>, 0x86/04) so no binding is left naming a dead guid.
/// </param>
/// <param name="ClearedEquipmentSlotId">
/// The body slot the item vacated, or 0. Non-zero means the caller must re-dress
/// (<c>SendCharacterAppearance</c>). A deliberate hotbar draw (or an explicit test-only
/// <see cref="InventoryOptions.WieldFirstWeapon"/> setting) can make this 7 internally. The
/// appearance writer must still route that redraw through <c>ActiveHandRowGuard</c>, which keeps
/// the client-fatal RHand row off the wire.
/// </param>
/// <param name="BoundLoadoutSlotId">
/// The loadout slot an <see cref="ItemActionKind.Equip"/> filled, or 0.
/// </param>
/// <param name="BoundEquipmentSlotId">
/// The body slot an <see cref="ItemActionKind.Equip"/> attached to, or 0. <b>Never 7</b>:
/// <c>ItemVerbs</c> drops the active hand to 0 before it can reach the wire (docs/45, guard 5).
/// </param>
/// <param name="DisplacedItemGuid">
/// The item an equip pushed out of the slot it took, or 0. It is in the bag afterwards - unless
/// <see cref="DisplacedToGround"/>.
/// </param>
/// <param name="DisplacedDefinitionId">What <see cref="DisplacedItemGuid"/> was.</param>
/// <param name="DisplacedCount">How many units of it.</param>
/// <param name="DisplacedCreated">
/// <see cref="DisplacedItemGuid"/> is a stack the client has never been told about, so it must be
/// announced with <c>11 02 ItemAdd</c> rather than <c>11 03 ItemUpdate</c> - an update for an
/// unknown guid is a no-op in <c>FUN_140dbfcb0</c> (docs/102 §3). Only <see cref="ItemVerbs.Unload"/>
/// sets it; an equip's displaced item is always one the client already holds.
/// </param>
/// <param name="DisplacedToGround">
/// The displaced item would not fit in the bag, so it is <b>gone from the inventory</b> and the
/// caller must spawn it as a ground object. This is <c>InventoryOptions.SwapNeverRefusedForBulk</c>:
/// the owner's session refused a helmet swap outright, because both helmets are BULK 250 against a
/// base carrier of 200 and no helmet can ever be displaced into a Cranberry bag before a backpack is
/// found (docs/86 §3.4).
/// </param>
/// <param name="BusyMilliseconds">
/// The client's own <c>ItemUseOptions.BUSY_MSEC</c> for the option that was clicked - the window the
/// character is locked out for, and the window a second request must be refused inside.
/// </param>
public sealed record ItemActionResult(
    ItemActionKind Kind,
    ItemUseOptionKind Option,
    string Rule,
    ulong ItemGuid = 0,
    uint DefinitionId = 0,
    uint Count = 0,
    uint RemainingCount = 0,
    uint ClearedLoadoutSlotId = 0,
    uint ClearedEquipmentSlotId = 0,
    ContainerErrorCode Error = ContainerErrorCode.None,
    uint BoundLoadoutSlotId = 0,
    uint BoundEquipmentSlotId = 0,
    ulong DisplacedItemGuid = 0,
    uint DisplacedDefinitionId = 0,
    uint DisplacedCount = 0,
    bool DisplacedToGround = false,
    int BusyMilliseconds = 0,
    uint InteractionAnimationId = 0,
    bool DisplacedCreated = false,
    ulong TargetCharacterGuid = 0,
    bool SwapLoadoutSlots = false);

/// <summary>
/// The server half of the client's inventory context menu: <c>Items.RequestUseItem</c> in, an
/// <see cref="ItemActionResult"/> out. Pure - <see cref="Resolve"/> changes nothing; only
/// <see cref="Apply"/> does.
/// <para>
/// <b>Why this exists at all.</b> docs/41 §7 lead L5 asked "how does a manual drag reach the
/// server?" and answered "log every unrecognised c2s opcode during a live drag". That log already
/// existed and already had the answer in it: 21 <c>zone Items sub=0x2c ... (unanswered)</c> lines
/// across four captures, every one of them the owner trying to drop, consume or salvage something.
/// See <see cref="RequestUseItem"/> for the field-by-field derivation.
/// </para>
/// </summary>
public static class InventoryActions
{
    /// <summary>
    /// Decide what to do, without changing anything. The caller may send
    /// <see cref="ItemActionResult.Error"/> as a <c>Container.Error</c> (0xc8/03) on a refusal - the
    /// client's own vocabulary for a server-side no (docs/41 §3a).
    /// </summary>
    public static ItemActionResult Resolve(PlayerInventory inventory, RequestUseItem request)
        => Resolve(inventory, request, magazines: null);

    /// <summary>
    /// The same, plus the weapon magazines this session is tracking - which is what turns
    /// <c>UnloadWeapon</c> from D118's named refusal into a real unload. A null
    /// <paramref name="magazines"/> keeps the refusal, verbatim.
    /// </summary>
    public static ItemActionResult Resolve(
        PlayerInventory inventory, RequestUseItem request, IWeaponMagazines? magazines)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(request);

        ItemUseOptionKind kind = request.Kind;
        if (kind == ItemUseOptionKind.Unknown)
        {
            return new ItemActionResult(
                ItemActionKind.Refused,
                kind,
                $"ITEM_USE_OPTION_ID {request.ItemUseOptionId} is not a row of ItemUseOptions.txt",
                Error: ContainerErrorCode.InteractionValidationFailed);
        }

        if (request.CharacterGuid != inventory.CharacterGuid)
        {
            return new ItemActionResult(
                ItemActionKind.Refused,
                kind,
                $"the request names character {request.CharacterGuid}, not {inventory.CharacterGuid}",
                Error: ContainerErrorCode.InteractionValidationFailed);
        }

        if (!inventory.Items.TryGetValue(request.ItemGuid, out InventoryItemInstance? item))
        {
            // The client's own name for "you asked about an item that is not there".
            return new ItemActionResult(
                ItemActionKind.Refused,
                kind,
                $"item guid {request.ItemGuid} is not in this character's inventory",
                ItemGuid: request.ItemGuid,
                Error: ContainerErrorCode.SlotDoesNotContainItem);
        }

        // The client would not have offered an option outside the item's own ItemUseOptionGroup, so
        // a request for one did not come from that context menu.
        // D351: August's old shoe option groups omit shred/salvage, but the Combat Update
        // added it. Accept only the two apparel verbs on recognised gameplay footwear.
        bool footwearSalvage = Movement.Footwear.AllowsShred(item.DefinitionId, request.ItemUseOptionId);
        bool hoodOption = kind is ItemUseOptionKind.HoodieUp or ItemUseOptionKind.HoodieDown
            && ItemUseOptionTable.Allows(item.DisplayDefinitionId, request.ItemUseOptionId);
        if (!footwearSalvage && !hoodOption && !ItemUseOptionTable.Allows(item.DefinitionId, request.ItemUseOptionId)
            && ItemUseOptionTable.OptionsForItem(item.DefinitionId).Count > 0)
        {
            return new ItemActionResult(
                ItemActionKind.Refused,
                kind,
                $"ItemUseOptions {request.ItemUseOptionId} ({kind}) is not in item "
                + $"{item.DefinitionId}'s ItemIdUseOptionGroupId group",
                ItemGuid: item.Guid,
                DefinitionId: item.DefinitionId,
                Error: ContainerErrorCode.WrongItemType);
        }

        // WAVE 9 - his dispatcher, on the client's own TYPE_NAME. Every one of the 74 ItemUseOptions
        // rows resolves to one of these 25 kinds, so a use-option group nobody censused still lands
        // in the right handler instead of in "no handler". Ported from
        // C:\Z1\Server\Zone\ZoneItemUse.cs's switch (D53), against the AUGUST sheet.
        //
        // The rule the old default broke: an option this server will not perform answers with a
        // NAMED refusal, which is a Container.Error the client can act on. Silence is not an answer,
        // and five of the seven verbs the August client offers - including RemoveItem, the
        // most-offered option in the whole build - used to fall through to it.
        InventoryOptions options = inventory.Options;
        return kind switch
        {
            ItemUseOptionKind.DropItem when !options.AnswerDropItem =>
                NotImplemented(kind, item, "InventoryOptions.AnswerDropItem is off"),
            ItemUseOptionKind.DropItem => Remove(inventory, item, request.Count, ItemActionKind.Drop, kind),

            ItemUseOptionKind.ConsumeItem or ItemUseOptionKind.UseItem when !options.AnswerConsumeItem =>
                NotImplemented(kind, item, "InventoryOptions.AnswerConsumeItem is off"),
            ItemUseOptionKind.ConsumeItem or ItemUseOptionKind.UseItem =>
                Consume(inventory, item, kind, request.ItemUseOptionId, request.TargetCharacterGuid),

            ItemUseOptionKind.RemoveItem when !options.AnswerRemoveItem =>
                NotImplemented(kind, item, "InventoryOptions.AnswerRemoveItem is off"),
            ItemUseOptionKind.RemoveItem => ItemVerbs.Remove(inventory, item, kind),

            ItemUseOptionKind.MoveItem when !options.AnswerMoveItem =>
                NotImplemented(kind, item, "InventoryOptions.AnswerMoveItem is off"),
            ItemUseOptionKind.MoveItem => ItemVerbs.Move(inventory, item, kind),

            ItemUseOptionKind.EquipItem when !options.AnswerEquipItem =>
                NotImplemented(kind, item, "InventoryOptions.AnswerEquipItem is off"),
            ItemUseOptionKind.EquipItem => ItemVerbs.Equip(inventory, item, kind),

            ItemUseOptionKind.SalvageItem when !options.AnswerSalvageItem =>
                NotImplemented(kind, item, "InventoryOptions.AnswerSalvageItem is off"),
            ItemUseOptionKind.SalvageItem =>
                ItemVerbs.Salvage(inventory, item, kind, request.ItemUseOptionId),

            // ANSWERED, and the answer is no. This build's magazine is not fed from the bag at all -
            // ShooterCombatState.Reload sets Ammo = RetailBalance.ClipSize(...) out of nothing - so
            // an unload would let a player mint ammunition by unloading, reloading and unloading
            // again. His ZoneItemUse.Unload is safe only because his reload consumes carried rounds.
            // The weapon -> calibre map already exists (z2-loot-tables.json clusters); flipping this
            // into a real unload is a small change the moment reload draws from the bag, and that
            // model is the shooting lane's.
            ItemUseOptionKind.UnloadWeapon when !options.AnswerUnloadWeapon =>
                NotImplemented(kind, item, "InventoryOptions.AnswerUnloadWeapon is off"),

            // LANE 1F closes D118. With the session's magazines in hand the unload is real: the
            // rounds come out of the weapon and go into the bag as a stack, and they can only ever
            // have reached that magazine by being taken out of the bag first, so the round trip is
            // conservative.
            ItemUseOptionKind.UnloadWeapon when magazines is not null =>
                ItemVerbs.Unload(inventory, item, kind, magazines),

            ItemUseOptionKind.UnloadWeapon => ItemVerbs.Refused(
                kind,
                item,
                "unloading is refused while reload refills the magazine for free "
                + "(ShooterCombatState.Reload) - unload/reload/unload would mint ammunition. The "
                + "shooting lane owns making reload draw from the bag; this becomes a real unload "
                + "the day it does",
                ContainerErrorCode.InteractionValidationFailed),

            // World containers. docs/63 5.2: this server has no lootable container entity, so there
            // is nothing for LootItem to take an item OUT of. Ground pickup is the InteractRequest
            // path and is unaffected.
            ItemUseOptionKind.LootItem => ItemVerbs.Refused(
                kind,
                item,
                "this server models no lootable world container - ground items are picked up by "
                + "walking onto them (docs/63 5.2)",
                ContainerErrorCode.UnknownContainer),

            // His words, and his reason: deployables are a Just Survive mechanic. KOTK has no
            // placement system, and inventing one would put an entity in the world with no owner,
            // no persistence and no removal path.
            ItemUseOptionKind.PlaceItem => ItemVerbs.Refused(
                kind, item, "nothing can be placed in Battle Royale",
                ContainerErrorCode.WrongItemType),

            ItemUseOptionKind.SkinItem => ItemVerbs.Skin(inventory, item),

            ItemUseOptionKind.RepairItem => ItemVerbs.Refused(
                kind,
                item,
                "repair needs a repair kit and an item-durability model this server does not have",
                ContainerErrorCode.WrongItemType),

            // ACCOUNT items live in the wardrobe manager, not in the world inventory, and every one
            // of these options mutates an account rather than a match.
            ItemUseOptionKind.OpenCrate
                or ItemUseOptionKind.ScrapAccountItem
                or ItemUseOptionKind.ShowGrinderUI
                or ItemUseOptionKind.UseAccountRecipeItem
                or ItemUseOptionKind.UseAccountDeliveryTriggerItem
                or ItemUseOptionKind.UseAccountGiveRewardSetItem
                or ItemUseOptionKind.UseDeliveryTriggerItem => ItemVerbs.Refused(
                kind,
                item,
                "that is an account item - crates, scrapping and reward bundles are opened from the "
                + "main menu, not in a match",
                ContainerErrorCode.WrongItemType),

            ItemUseOptionKind.HoodieUp or ItemUseOptionKind.HoodieDown =>
                ItemVerbs.SetHood(inventory, item, kind),

            ItemUseOptionKind.ShowEmoteUI => ItemVerbs.Refused(
                kind, item, "emotes are not wired up yet", ContainerErrorCode.WrongItemType),

            ItemUseOptionKind.GiveItem => ItemVerbs.Refused(
                kind, item, "giving an item to another player is not implemented",
                ContainerErrorCode.InteractionValidationFailed),

            // 62/70/71/72 are the HOTWIRE options and belong to the mount path. KOTK cars start
            // without parts, so one of these arriving at all is worth reporting.
            ItemUseOptionKind.StartVehicle => ItemVerbs.Refused(
                kind,
                item,
                "that vehicle cannot be started from the inventory - the hotwire path is the mount "
                + "lane's, and KOTK cars start without parts",
                ContainerErrorCode.InteractionValidationFailed),

            // 94/100/101/102 exist only on vehicle parts and hotwire items. No definition in this
            // server's roster is in one of those groups, so this arriving is new information.
            ItemUseOptionKind.DragAndDropItem => ItemVerbs.Refused(
                kind,
                item,
                "that is a vehicle-part drag (use-option group 55/57) and no item this server hands "
                + "out is in one of those groups",
                ContainerErrorCode.WrongItemType),

            _ => NotImplemented(kind, item, "no server behaviour is derived for this action yet"),
        };
    }

    /// <summary>
    /// Apply a plan <see cref="Resolve"/> returned. <see cref="ItemActionKind.Drop"/> and
    /// <see cref="ItemActionKind.Consume"/> take units out; <see cref="ItemActionKind.Equip"/> and
    /// <see cref="ItemActionKind.Unequip"/> move the item between the bag and a loadout slot;
    /// everything else - <see cref="ItemActionKind.Shred"/> included, which
    /// <c>Crafting.ShredTable</c> owns - is a no-op, so a caller may apply unconditionally.
    /// </summary>
    public static void Apply(PlayerInventory inventory, ItemActionResult plan)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Kind is ItemActionKind.Drop or ItemActionKind.Consume or ItemActionKind.Delete)
        {
            inventory.RemoveUnits(plan.ItemGuid, plan.Count);
            return;
        }

        if (plan.Kind == ItemActionKind.Hood)
        {
            _ = inventory.TrySetHood(plan.Option == ItemUseOptionKind.HoodieUp);
            return;
        }

        // Equip / Unequip move an item between the bag and a loadout slot rather than destroying
        // it; Shred is applied by ShredTable on the caller's busy clock; Repaint, Refused and
        // NotImplemented change nothing. ItemVerbs.Apply is a no-op for all but the first two, so a
        // caller may still apply unconditionally.
        ItemVerbs.Apply(inventory, plan);
    }

    /// <summary>
    /// <see cref="Resolve"/> and <see cref="Apply"/> in one call, for the common server path.
    /// </summary>
    public static ItemActionResult Perform(PlayerInventory inventory, RequestUseItem request)
    {
        ItemActionResult plan = Resolve(inventory, request);
        Apply(inventory, plan);
        return plan;
    }

    /// <summary>Native developer deletion; retain the required bag/hands guard and normal slot cleanup.</summary>
    public static ItemActionResult ResolveDelete(PlayerInventory inventory, ulong itemGuid)
    {
        if (!inventory.Items.TryGetValue(itemGuid, out InventoryItemInstance? item))
            return new(ItemActionKind.Refused, ItemUseOptionKind.Unknown,
                $"item guid {itemGuid} is not in this character's inventory",
                ItemGuid: itemGuid, Error: ContainerErrorCode.SlotDoesNotContainItem);
        var plan = Remove(inventory, item, item.Count, ItemActionKind.Delete, ItemUseOptionKind.Unknown);
        return plan.Kind == ItemActionKind.Delete
            ? plan with { Rule = $"native /item delete instance {itemGuid}" } : plan;
    }

    private static ItemActionResult NotImplemented(
        ItemUseOptionKind kind,
        InventoryItemInstance item,
        string why) =>
        new(
            ItemActionKind.NotImplemented,
            kind,
            $"{kind} on item {item.DefinitionId} (instance {item.Guid}): {why}",
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId);

    /// <summary>
    /// <b>D341 - a medical is consumed on a cast bar, and then it heals.</b> The plan removes ONE
    /// unit (applied by the caller at the end of the bar, exactly as a shred is), and carries the
    /// option row's own <c>BUSY_MSEC</c> (95 bandage 3,000; 99 kit 5,000; 103 1,000) and animation
    /// (18) for the <c>cf 02</c> the caller sends first. The friend's server ran the bandage bar
    /// for 2,000 ms (capture P:L7539-L8010); the August sheet says 3,000 and the sheet wins. The
    /// heal itself is <see cref="Combat.MedicalModel"/>'s row (10 HP over 10 s, 60 over 60), ticked
    /// by the caller through the <c>11 01</c> health bar. Only a <see cref="Combat.MedicalModel"/>
    /// item is a medical; anything else the client's menu offers as Consume is refused by name.
    /// Reachable from the right-click option and from the Q / E quick-use keys (86 06 slot 40/41).
    /// </summary>
    public static ItemActionResult Consume(
        PlayerInventory inventory,
        InventoryItemInstance item,
        ItemUseOptionKind option,
        uint optionId,
        ulong targetCharacterGuid = 0)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(item);

        if (Vehicles.AugustFuelFacts.IsRefuelItem(item.DefinitionId))
        {
            var fuel = Remove(inventory, item, 1, ItemActionKind.Consume, option);
            ItemUseOptionTable.TryGet(17, out var refuel);
            return fuel.Kind == ItemActionKind.Refused ? fuel : fuel with
            {
                Rule = "refuel vehicle by 25 percent using one fuel can",
                BusyMilliseconds = refuel.BusyMsec,
                InteractionAnimationId = refuel.InteractionAnimationId,
                TargetCharacterGuid = targetCharacterGuid,
            };
        }

        if (Combat.MedicalModel.For(item.DefinitionId) is not { } medical)
        {
            return new ItemActionResult(
                ItemActionKind.Refused,
                option,
                $"item {item.DefinitionId} is not a medical (MedicalModel.Items) - Consume is "
                + "answered only for bandages, gauze, first aid kits and procoagulant",
                ItemGuid: item.Guid,
                DefinitionId: item.DefinitionId,
                Error: ContainerErrorCode.WrongItemType);
        }

        bool hasOption = ItemUseOptionTable.TryGet(optionId, out ItemUseOptionDefinition row);
        int busy = hasOption && row.BusyMsec > 0
            ? row.BusyMsec
            : Combat.MedicalModel.ApplyMsFor(item.DefinitionId);
        uint animation = hasOption && row.InteractionAnimationId != 0 ? row.InteractionAnimationId : 18u;

        ItemActionResult removal = Remove(inventory, item, 1, ItemActionKind.Consume, option);
        return removal.Kind == ItemActionKind.Refused
            ? removal
            : removal with
            {
                Rule = $"consume {medical.Name} (item {item.DefinitionId}, instance {item.Guid}): "
                    + $"{busy} ms cast, then {medical.TotalHp} HP over {medical.OverSeconds} s; "
                    + removal.Rule,
                BusyMilliseconds = busy,
                InteractionAnimationId = animation,
            };
    }

    private static ItemActionResult Remove(
        PlayerInventory inventory,
        InventoryItemInstance item,
        uint requested,
        ItemActionKind outcome,
        ItemUseOptionKind option)
    {
        // The bag itself and the required Fists are not the player's to throw away. Loadout slot 43
        // is FLAG_AUTO_EQUIP with FLAG_IS_VISIBLE = 0 and slot 7 is FLAG_REQUIRED = 1 with
        // ITEM_ID 85 (docs/41 §2a) - the client draws no context menu for either, so a request
        // naming one is not something the UI produced.
        if (item.LoadoutSlotId is SurvivorLoadout.Inventory or SurvivorLoadout.Fists)
        {
            return new ItemActionResult(
                ItemActionKind.Refused,
                option,
                $"loadout slot {item.LoadoutSlotId} is the survivor's own bag/hands and cannot be "
                + "dropped (LoadoutSlots FLAG_REQUIRED / FLAG_IS_VISIBLE = 0)",
                ItemGuid: item.Guid,
                DefinitionId: item.DefinitionId,
                Error: ContainerErrorCode.WrongItemType);
        }

        uint count = requested == 0 || requested > item.Count ? item.Count : requested;
        uint remaining = item.Count - count;
        return new ItemActionResult(
            outcome,
            option,
            $"{option} {count} x item {item.DefinitionId} (instance {item.Guid}); "
            + (remaining == 0
                ? "the instance is gone"
                : $"{remaining} left on the stack (MAX_STACK_SIZE {item.Fact.MaxStackSize})"),
            ItemGuid: item.Guid,
            DefinitionId: item.DefinitionId,
            Count: count,
            RemainingCount: remaining,
            ClearedLoadoutSlotId: remaining == 0 ? item.LoadoutSlotId : 0,
            ClearedEquipmentSlotId: remaining == 0 ? item.EquipmentSlotId : 0);
    }
}
