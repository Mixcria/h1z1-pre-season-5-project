using Cranberry.Zone.Generated;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone.Crafting;

/// <summary>
/// What one <c>SalvageItem</c> ("shred") produces, and what the server would have to do to run one.
/// <para>
/// <b>WAVE 9: it is wired, and the answer was already in the tree.</b> This header used to read
/// "shred is NOT wired this wave, deliberately", and named its blocker as <em>the packet the client
/// sends when a player right-clicks Shred</em>, with two candidates - <c>09 0b InteractionSelect</c>
/// and <c>ac 2c Items::RequestUseItem</c>. docs/63 SS1, written the same wave by a different lane,
/// had already proved from <b>21 client-originated packets</b> that it is <c>ac 2c</c>, and that its
/// <c>ITEM_USE_OPTION_ID</c> takes the values 6 and 63 - both <c>SalvageItem</c> rows. Two wave-6
/// documents each held half the answer and were never joined, so the owner right-clicked Shred four
/// times on 30 Aug 2026 and got <c>no server behaviour is derived for this action yet</c> from code
/// that was already written and green. Step 1 below is now
/// <c>Inventory.ItemVerbs.Salvage</c>; nothing about steps 2-5 changed.
/// </para>
/// <para>
/// <b>What the server owes a shred, in full</b>, so the next wave has no design left to do:
/// </para>
/// <list type="number">
/// <item>Resolve the c2s request to an item guid the player actually holds. The verb's own
/// <c>BUSY_MSEC</c> is <b>1,000 ms on rows 6 and 63 and 0 on the other five</b> - see
/// <see cref="BusyMilliseconds"/> for the 2026-09-03 correction - so a shred on apparel owns a
/// one-second busy window and the server must reject a second request inside it.</item>
/// <item>Refuse anything not eligible (<see cref="IsShreddable"/>). Body armour (class 25041) has
/// <b>no</b> shred option in <c>ItemUseOptions.txt</c> - which is precisely why Armor Scrap is
/// described as the remnant of a destroyed helmet rather than of shredded armour.</item>
/// <item>Consume the source item: <c>ClientUpdate.ItemDelete</c>, or delete-then-add when the
/// source was a stack. A worn item is unbound from its loadout slot first (wave 9 - the owner's own
/// four refused shreds were on a WORN shirt, item 2144, which this method used to reject outright),
/// and a worn <em>container</em> is still refused while anything is inside it.</item>
/// <item>Grant the yield through <see cref="PlayerInventory.TryPickUp"/> and send
/// <c>ClientUpdate.ItemAdd</c> - the same grant path crafting and ground pickup use, so bulk,
/// stacking and auto-assign all apply unchanged.</item>
/// <item>Refresh the ingredient panel when <see cref="CraftingOptions.SendComponentCounts"/> is on,
/// because a shred is the event that changes a crafting count.</item>
/// </list>
/// <para>
/// Steps 2-5 are implemented here and tested. Step 1 is <c>Inventory.ItemVerbs.Salvage</c>, and the
/// one-second busy window is <c>ZoneService</c>'s (docs/86 edit 6a) - read from the clicked option's
/// own <c>BUSY_MSEC</c> rather than from <see cref="BusyMilliseconds"/>, because the two agree on
/// the two rows that matter (1000 on both 6 and 63) and the sheet is the authority everywhere else:
/// five of the seven <c>SalvageItem</c> rows are 0.
/// </para>
/// <para>
/// <b>D260 - the thirteen refusals are closed.</b> The exact shreddable set is 645 items over the
/// eight option groups that carry a <c>SalvageItem</c> row, and eleven <c>ITEM_CLASS</c> values
/// appear in it. Seven had yields; four - 25005 (Boots), 25010 (Improvised Compass), 16050
/// (Blackberry Pie) and 16053 (the eight ammunition rounds, Broken Wooden Item and one dev item) -
/// did not, so thirteen items whose own context menu offered <b>Shred</b> were answered with
/// <c>Container.Error WrongItemType</c>. <see cref="Yields"/> now covers all eleven, and
/// <see cref="ItemOverrides"/> handles the one row where the client's own text names a different
/// product from its class-mates.
/// </para>
/// <para>
/// <b>The second feeder is blocked for a different reason.</b> Armor Scrap is the remnant of a
/// <em>destroyed</em> helmet, which needs item durability - a system Cranberry does not have and
/// which <c>derived/loot.json</c> already flags as server-owned and unbuilt. Rather than leave
/// Makeshift Armor permanently unreachable, this table lets a shredded helmet yield Armor Scrap
/// (<b>DESIGN</b>, see <see cref="Yields"/>); if durability ever lands, that row is the one to
/// revisit.
/// </para>
/// </summary>
public static class ShredTable
{
    /// <summary>
    /// The busy window a shred on apparel occupies, from the client's own <c>ItemUseOptions.txt</c>.
    /// This is <b>DERIVED</b>; the craft durations in <see cref="CraftingCatalog"/> are not.
    /// <para>
    /// <b>CORRECTION, 2026-09-03.</b> This comment, <c>docs/62 §8</c> and the
    /// <c>rulings/crafting.json</c> row all used to say <c>BUSY_MSEC</c> is 1,000 <em>"on every one
    /// of the eight groups carrying SalvageItem"</em>. It is not. <c>ItemUseOptions.txt</c> carries
    /// <b>seven</b> <c>SalvageItem</c> rows - 6, 15, 18, 31, 35, 63 and 87 - and <c>BUSY_MSEC</c> is
    /// 1,000 on exactly <b>two</b> of them, 6 and 63; the other five are <b>0</b>. The value here is
    /// nonetheless right, because rows 6 and 63 are the two the client offers on apparel and
    /// <c>ItemVerbs.Salvage</c> reads the clicked row's own <c>BusyMsec</c>, falling back to this
    /// constant only when the row is unknown.
    /// </para>
    /// </summary>
    public const int BusyMilliseconds = Rulings.Crafting.ShredBusyMilliseconds;

    /// <summary>
    /// Item class → what one shred of it produces. <b>Every quantity is DESIGN except two</b>: no
    /// item row in the 2,643-row catalogue carries <c>DATASHEET_ID</c> 25
    /// (<c>ShreddableItemDatasheet</c>), so the client ships no yields at all (docs/59 §1.1) - but
    /// the owner's admin capture <em>proves</em> two of them, because the friend's server answered a
    /// class-25008 shred with one Scrap of Cloth and a class-25040 shred with one more.
    /// <para>
    /// <b>The class list is the client's own, and now complete.</b> Joining
    /// <c>ItemUseOptions.txt</c> × <c>ItemUseOptionGroups.txt</c> × <c>ItemIdUseOptionGroupId.txt</c>
    /// gives the exact 645-item shreddable set over the eight groups 4/14/18/33/56/62/65/68, whose
    /// <c>ITEM_CLASS</c> values are 25002 (248), 25000 (<b>137</b>, not docs/59 §1.4.3's 133), 25003
    /// (109), 25040 (59), 25004 (58), 25008 (17), 16053 (10), 25013 (4), 25005 (1), 25010 (1) and
    /// 16050 (1). All eleven are here (D260).
    /// </para>
    /// <para>
    /// Cranberry still keys on class rather than on item id, because <c>InventoryItemFacts</c>
    /// carries no use-option column, which makes this rule a deliberate <b>superset</b> of the real
    /// 645. The surplus is unreachable: <c>InventoryActions.Resolve</c> gates on
    /// <c>ItemUseOptionTable.Allows(definitionId, optionId)</c> before any of this is consulted, so
    /// only <em>under</em>-coverage was ever a live defect - and that is what D260 fixed.
    /// </para>
    /// <para>
    /// The outputs the client's own text pins are honoured: "Shred cloth items to get scrap cloth"
    /// (item 23), "Shred large backpacks to get this fabric" (item 3500), and item 1694's "can be
    /// dismantled for a wooden stick" (<see cref="ItemOverrides"/>). The two rows that read against
    /// the client's own wording are called out where they are: 25000 → Armor Scrap, which the client
    /// calls the remnant of <em>destroyed</em> helmets, and 16053 → Gunpowder, which is this
    /// project's answer to "what does a salvaged round become" and is DESIGN.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<uint, RecipeIngredient> Yields { get; } = Build(
        Rulings.Crafting.ShredYieldItemClasses,
        Rulings.Crafting.ShredYieldOutputItemIds,
        Rulings.Crafting.ShredYieldQuantities);

    /// <summary>
    /// The wave-9 seven, restored by <c>CRANBERRY_SHRED_ALL=0</c>: shirts, pants, face, 25008,
    /// waist packs, backpacks and helmets, and a <c>WrongItemType</c> refusal for the thirteen
    /// items of the other four classes.
    /// </summary>
    public static IReadOnlyDictionary<uint, RecipeIngredient> LegacyYields { get; } = Build(
        Rulings.Crafting.ShredYieldItemClasses[..7],
        Rulings.Crafting.ShredYieldOutputItemIds[..7],
        Rulings.Crafting.ShredYieldQuantities[..7]);

    /// <summary>
    /// Item id → yield, checked <b>before</b> <see cref="Yields"/>. One row: item 1694 Broken Wooden
    /// Item, whose own description says <em>"This item is beyond repair, but can be dismantled for a
    /// wooden stick"</em>, shares <c>ITEM_CLASS</c> 16053 with the eight ammunition rounds, so the
    /// class rule alone would turn it into gunpowder (D260).
    /// </summary>
    public static IReadOnlyDictionary<uint, RecipeIngredient> ItemOverrides { get; } = Build(
        Rulings.Crafting.ShredItemOverrideItemIds,
        Rulings.Crafting.ShredItemOverrideOutputItemIds,
        Rulings.Crafting.ShredItemOverrideQuantities);

    /// <summary>True when an item has a shred yield. Body armour (25041) never does.</summary>
    /// <param name="itemDefinitionId">The item to shred.</param>
    /// <param name="yield">What one shred of it produces.</param>
    /// <param name="everyOfferedItem">
    /// <see cref="InventoryOptions.ShredEveryOfferedItem"/>. False restores the wave-9 seven-class
    /// table and the thirteen refusals it caused.
    /// </param>
    public static bool IsShreddable(
        uint itemDefinitionId, out RecipeIngredient yield, bool everyOfferedItem = true)
    {
        // Military backpacks and their cosmetic variants share container definition 28.
        if (InventoryItemFacts.TryGet(itemDefinitionId, out var backpack)
            && backpack.ItemClass == 25004 && backpack.CodeFactory == ItemCodeFactory.EquippableContainer
            && backpack.Param1 == 28)
        {
            yield = new RecipeIngredient(CraftingCatalog.CompositeFabric, 4);
            return true;
        }
        // Hats and protective helmets share ITEM_CLASS 25000. Only the IS_ARMOR rows
        // produce armour scrap; ordinary headwear produces cloth like other small garments.
        if (InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact fact)
            && fact.ItemClass == AugustArmourFacts.HeadClass
            && !ArmourModel.IsHelmet(itemDefinitionId))
        {
            yield = new RecipeIngredient(CraftingCatalog.ScrapOfCloth, 1);
            return true;
        }
        var footwear = Movement.Footwear.TierFor(itemDefinitionId);
        if (footwear is Movement.FootwearTier.Fast or Movement.FootwearTier.Sturdy)
        {
            yield = new RecipeIngredient(CraftingCatalog.CompositeFabric, 2);
            return true;
        }
        if (footwear == Movement.FootwearTier.Stealth)
        {
            yield = new RecipeIngredient(23, 2);
            return true;
        }
        if (everyOfferedItem && ItemOverrides.TryGetValue(itemDefinitionId, out yield))
        {
            return true;
        }

        yield = default;
        return InventoryItemFacts.TryGet(itemDefinitionId, out fact)
            && (everyOfferedItem ? Yields : LegacyYields).TryGetValue(fact.ItemClass, out yield);
    }

    private static Dictionary<uint, RecipeIngredient> Build(
        ReadOnlySpan<uint> keys, ReadOnlySpan<uint> outputs, ReadOnlySpan<uint> quantities)
    {
        var rows = new Dictionary<uint, RecipeIngredient>(keys.Length);
        for (int index = 0; index < keys.Length; index++)
        {
            rows[keys[index]] = new RecipeIngredient(outputs[index], quantities[index]);
        }

        return rows;
    }

    /// <summary>
    /// Steps 2-5 of the list in the type remarks: validate, consume one of the named item, grant
    /// the yield, and report the changes in wire order. Step 1 - working out which item guid the
    /// client meant - is <c>Inventory.ItemVerbs.Salvage</c> off <c>ac 2c Items.RequestUseItem</c>.
    /// </summary>
    /// <param name="inventory">The player's inventory. Mutated only on success.</param>
    /// <param name="itemGuid">The instance to shred.</param>
    public static CraftOutcome Shred(PlayerInventory inventory, ulong itemGuid)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (!inventory.Items.TryGetValue(itemGuid, out InventoryItemInstance? item))
        {
            return new CraftOutcome
            {
                Requested = 1,
                Refusal = CraftRefusal.UnknownRecipe,
                ContainerError = ContainerErrorCode.SlotDoesNotContainItem,
            };
        }

        // WAVE 9. A WORN item used to be refused here alongside a missing one, on the reasoning that
        // "a worn item would have to be unbound first... until the trigger is known". The trigger is
        // known now, and the owner's own four refused shreds were all on item 2144 - the starter
        // shirt, worn, ContainerGuid 0. So unbind it, and record what it vacated so the caller can
        // rebuild the loadout; the slot ids are read BEFORE the removal, the same order
        // ZoneItemUse.ShredNow reads the outfit index in.
        //
        // A worn CONTAINER stays refused: shredding the bag would strand every item pointing at its
        // guid, and there is no second container to move them to.
        uint clearedLoadoutSlot = 0;
        uint wornBodySlot = 0;
        if (item.ContainerGuid == 0)
        {
            if (item.LoadoutSlotId == 0)
            {
                return new CraftOutcome
                {
                    Requested = 1,
                    Refusal = CraftRefusal.UnknownRecipe,
                    ContainerError = ContainerErrorCode.SlotDoesNotContainItem,
                };
            }

            if (inventory.Containers.ContainsKey(item.Guid)
                || item.LoadoutSlotId == SurvivorLoadout.Inventory)
            {
                return new CraftOutcome
                {
                    Requested = 1,
                    Refusal = CraftRefusal.UnknownRecipe,
                    ContainerError = ContainerErrorCode.ContainerInUse,
                };
            }

            if (!IsShreddable(item.DefinitionId, out _, inventory.Options.ShredEveryOfferedItem))
            {
                return new CraftOutcome
                {
                    Requested = 1,
                    Refusal = CraftRefusal.UnknownRecipe,
                    ContainerError = ContainerErrorCode.WrongItemType,
                };
            }

            clearedLoadoutSlot = item.LoadoutSlotId;
            wornBodySlot = item.EquipmentSlotId;

            // Into the bag first, so the code below is the single bagged path and the rollback it
            // already has covers a worn item too. A shred yields less bulk than it consumes on every
            // row of Yields, so this can never overflow.
            _ = inventory.TryStow(item);
        }

        if (item.ContainerGuid == 0
            || !inventory.Containers.TryGetValue(item.ContainerGuid, out InventoryContainer? container))
        {
            return new CraftOutcome
            {
                Requested = 1,
                Refusal = CraftRefusal.UnknownRecipe,
                ContainerError = ContainerErrorCode.SlotDoesNotContainItem,
            };
        }

        if (!IsShreddable(item.DefinitionId, out RecipeIngredient yield, inventory.Options.ShredEveryOfferedItem))
        {
            return new CraftOutcome
            {
                Requested = 1,
                Refusal = CraftRefusal.UnknownRecipe,
                ContainerError = ContainerErrorCode.WrongItemType,
            };
        }

        uint slot = item.ContainerSlotId;
        uint previous = item.Count;
        item.Count -= 1;
        bool removed = item.Count == 0;
        if (removed)
        {
            container.Remove(item);
        }

        InventoryPlacement plan = inventory.TryPickUp(
            yield.ItemDefinitionId,
            yield.Quantity,
            out InventoryItemInstance? produced);
        if (produced is null)
        {
            item.Count = previous;
            if (removed)
            {
                container.Place(item, slot);
            }

            if (clearedLoadoutSlot != 0)
            {
                // It came off the BODY to get here (wave 9). A rollback that left it in the bag
                // would take a garment off the character and give nothing back - and the bag it
                // landed in is precisely the one that just proved it had no room.
                inventory.BindLoadout(item, clearedLoadoutSlot, wornBodySlot);
            }

            return new CraftOutcome
            {
                Requested = 1,
                Refusal = CraftRefusal.NoRoom,
                ContainerError = plan.Error,
            };
        }

        if (removed)
        {
            // Committed: forget the instance so its guid can never be named again. Held back until
            // now so the refusal above could still put it back.
            inventory.RemoveUnits(itemGuid, 0);
        }

        var changes = new List<CraftItemChange>(2)
        {
            removed
                ? new CraftItemChange(itemGuid, CraftItemChangeKind.Removed, null)
                : new CraftItemChange(itemGuid, CraftItemChangeKind.CountChanged, item),
            new CraftItemChange(
                produced.Guid,
                plan.Kind == InventoryPlacementKind.Stack
                    ? CraftItemChangeKind.GrantedIntoStack
                    : CraftItemChangeKind.Granted,
                produced),
        };

        return new CraftOutcome
        {
            Requested = 1,
            Crafted = 1,
            Changes = changes,
            ClearedLoadoutSlotId = clearedLoadoutSlot,
        };
    }
}
