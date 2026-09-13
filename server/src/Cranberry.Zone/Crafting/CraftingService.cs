using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Crafting;

/// <summary>Why a craft produced nothing.</summary>
public enum CraftRefusal
{
    /// <summary>It did not - the craft succeeded.</summary>
    None = 0,

    /// <summary><see cref="CraftingOptions.AllowCrafting"/> is off.</summary>
    Disabled,

    /// <summary>The client named a recipe this session never delivered.</summary>
    UnknownRecipe,

    /// <summary>Not enough of at least one ingredient. <see cref="CraftOutcome.Shortfall"/> says which.</summary>
    MissingIngredients,

    /// <summary>The output does not fit. <see cref="CraftOutcome.ContainerError"/> carries the
    /// client's own vocabulary for it.</summary>
    NoRoom,
}

/// <summary>What one inventory item the craft touched needs on the wire.</summary>
public enum CraftItemChangeKind
{
    /// <summary>Fully consumed - send <c>ClientUpdate.ItemDelete</c>.</summary>
    Removed,

    /// <summary>Partly consumed - send <c>ItemDelete</c> then <c>ItemAdd</c> at the new count.</summary>
    CountChanged,

    /// <summary>Newly created - send <c>ClientUpdate.ItemAdd</c>.</summary>
    Granted,

    /// <summary>Merged onto an existing stack - send <c>ItemDelete</c> then <c>ItemAdd</c>.</summary>
    GrantedIntoStack,
}

/// <summary>
/// One inventory item the craft changed, and what the caller must send for it.
/// <para>
/// The delete-then-add pairs are not an accident of this lane: <c>ClientUpdate.ItemUpdate</c>
/// (<c>11 03</c>) is the cheaper packet for a count change but its envelope has never been derived,
/// so <c>ZoneService</c>'s ground-pickup path already answers a stack merge with
/// <c>ItemDelete</c> + <c>ItemAdd</c> using two writers that are proven. Crafting reuses that
/// decision rather than inventing a third behaviour for the same problem.
/// </para>
/// </summary>
/// <param name="ItemGuid">The instance guid, valid even for <see cref="CraftItemChangeKind.Removed"/>.</param>
/// <param name="Kind">What to send.</param>
/// <param name="Item">The live instance; <c>null</c> only for <see cref="CraftItemChangeKind.Removed"/>.</param>
public readonly record struct CraftItemChange(
    ulong ItemGuid,
    CraftItemChangeKind Kind,
    InventoryItemInstance? Item)
{
    /// <summary>True when an <c>ItemDelete</c> must precede any <c>ItemAdd</c> for this guid.</summary>
    public bool NeedsDelete => Kind is CraftItemChangeKind.Removed
        or CraftItemChangeKind.CountChanged
        or CraftItemChangeKind.GrantedIntoStack;

    /// <summary>True when an <c>ItemAdd</c> must follow.</summary>
    public bool NeedsAdd => Kind is not CraftItemChangeKind.Removed;
}

/// <summary>
/// The result of one <c>09 1a Command.RecipeStart</c>: what changed, what to send, and - when
/// nothing was crafted - exactly why.
/// </summary>
public sealed record CraftOutcome
{
    /// <summary>The recipe, or <c>null</c> when the id was not known.</summary>
    public RecipeDefinition? Recipe { get; init; }

    /// <summary>The recipe id as the client sent it, even when it resolved to nothing.</summary>
    public uint RecipeId { get; init; }

    /// <summary>How many crafts the client asked for (<c>Craft 1</c> sends 1, <c>Craft Max</c> more).</summary>
    public uint Requested { get; init; }

    /// <summary>How many crafts actually completed. A partial result is a success, not a refusal:
    /// <c>Craft Max</c> asking for 9 with material for 3 makes 3.</summary>
    public uint Crafted { get; init; }

    /// <summary>Why the <em>next</em> craft stopped. <see cref="CraftRefusal.None"/> only when the
    /// full requested count was made.</summary>
    public CraftRefusal Refusal { get; init; }

    /// <summary>The client's own error vocabulary for a <see cref="CraftRefusal.NoRoom"/>, ready to
    /// be sent as <c>Container.Error</c>. <see cref="ContainerErrorCode.None"/> otherwise.</summary>
    public ContainerErrorCode ContainerError { get; init; }

    /// <summary>For <see cref="CraftRefusal.MissingIngredients"/>, the ingredients that ran out and
    /// how many more each craft needed.</summary>
    public IReadOnlyList<RecipeIngredient> Shortfall { get; init; } = [];

    /// <summary>Every inventory change, in the order the packets must go out.</summary>
    public IReadOnlyList<CraftItemChange> Changes { get; init; } = [];

    /// <summary>
    /// The loadout slot the source item vacated, or 0. Non-zero only for a shred of a WORN item
    /// (wave 9), and it means the caller must re-send the whole loadout so no binding is left naming
    /// a dead guid. <c>ZoneService.ApplyCraft</c> already sends <c>SetLoadoutSlots</c> on every
    /// success, so this is a statement of fact for the log and the tests rather than a new duty.
    /// </summary>
    public uint ClearedLoadoutSlotId { get; init; }

    /// <summary>True when at least one item was produced.</summary>
    public bool Succeeded => Crafted > 0;

    /// <summary>
    /// The status the crafting window should be told about. A refused craft gets
    /// <see cref="RecipeCraftingState.Refused"/>, which is the only feedback the crafting window
    /// itself can show - <c>Container.Error</c> prints to the console instead.
    /// </summary>
    public RecipeCraftingState Status => Succeeded ? RecipeCraftingState.Ready : RecipeCraftingState.Refused;

    /// <summary>A single log line naming everything a play-test needs.</summary>
    public string Describe()
    {
        string outputs = Recipe is null
            ? "unknown recipe"
            : $"item {Recipe.OutputItemDefinitionId} ×{Crafted * Recipe.OutputCount}";
        string why = Refusal == CraftRefusal.None
            ? string.Empty
            : Shortfall.Count > 0
                ? $", stopped: {Refusal} ({string.Join(", ", Shortfall.Select(s => $"{s.ItemDefinitionId} needs {s.Quantity} more"))})"
                : ContainerError != ContainerErrorCode.None
                    ? $", stopped: {Refusal} (Container.Error {ContainerError})"
                    : $", stopped: {Refusal}";
        return $"recipe {RecipeId}: crafted {Crafted}/{Requested} → {outputs}, "
            + $"{Changes.Count} inventory change(s){why}";
    }
}

/// <summary>
/// Crafting, server-side. The client ships no recipe data and performs no validation of its own, so
/// everything here - which recipes exist, what they cost, whether a craft may proceed - is the
/// server's word, exactly as bulk is for a ground pickup (docs/41 §4c).
/// <para>
/// Crafting consumes container contents and quick-slot bandages/first aid kits. Other worn
/// equipment is excluded so crafting cannot remove a backpack out from under its contents.
/// The output is granted through
/// <see cref="PlayerInventory.TryPickUp"/> - the identical call the ground-pickup path makes - so a
/// crafted item inherits the existing stacking, bulk and body-slot rules. Crafting preserves an
/// occupied armour slot: a spare vest is carried, while the first vest can still auto-equip.
/// </para>
/// </summary>
public static class CraftingService
{
    /// <summary>
    /// Run one craft request.
    /// </summary>
    /// <param name="inventory">The player's inventory. Mutated on success; untouched on refusal.</param>
    /// <param name="recipeId">The id from <c>09 1a</c>.</param>
    /// <param name="requestedCount">How many crafts, from <c>09 1a</c>. Clamped to at least 1.</param>
    /// <param name="options">Effective options; <c>null</c> means <see cref="CraftingOptions.Default"/>.</param>
    public static CraftOutcome Craft(
        PlayerInventory inventory,
        uint recipeId,
        uint requestedCount,
        CraftingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        CraftingOptions effective = (options ?? CraftingOptions.Default).Effective;
        uint requested = Math.Max(1u, requestedCount);

        if (!effective.AllowCrafting)
        {
            return new CraftOutcome
            {
                RecipeId = recipeId,
                Requested = requested,
                Refusal = CraftRefusal.Disabled,
            };
        }

        if (!CraftingCatalog.ByIdFor(effective.RetailRecipes)
                .TryGetValue(recipeId, out RecipeDefinition? recipe))
        {
            return new CraftOutcome
            {
                RecipeId = recipeId,
                Requested = requested,
                Refusal = CraftRefusal.UnknownRecipe,
            };
        }

        return Craft(inventory, recipe, requested, effective);
    }

    /// <summary>
    /// Run a craft for a recipe that has already been resolved. The id-based
    /// <see cref="Craft(PlayerInventory, uint, uint, CraftingOptions?)"/> is the wire entry point;
    /// this overload exists because the catalogue is not the only possible source of a recipe (a
    /// mid-match <c>0x26 01 Recipe.Add</c> grant would be another) and because it lets a test drive
    /// the refusal paths without inventing a catalogue entry.
    /// </summary>
    public static CraftOutcome Craft(
        PlayerInventory inventory,
        RecipeDefinition recipe,
        uint requestedCount,
        CraftingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(recipe);
        CraftingOptions effective = (options ?? CraftingOptions.Default).Effective;
        uint requested = Math.Max(1u, requestedCount);
        uint recipeId = recipe.RecipeId;

        if (!effective.AllowCrafting)
        {
            return new CraftOutcome
            {
                Recipe = recipe,
                RecipeId = recipeId,
                Requested = requested,
                Refusal = CraftRefusal.Disabled,
            };
        }

        var changes = new List<CraftItemChange>();
        var consumed = new Dictionary<ulong, ConsumedItem>();
        uint crafted = 0;
        CraftRefusal refusal = CraftRefusal.None;
        ContainerErrorCode error = ContainerErrorCode.None;
        IReadOnlyList<RecipeIngredient> shortfall = [];

        while (crafted < requested)
        {
            shortfall = Shortfall(inventory, recipe);
            if (shortfall.Count > 0)
            {
                refusal = CraftRefusal.MissingIngredients;
                break;
            }

            var journal = new List<JournalEntry>();
            foreach (RecipeIngredient ingredient in recipe.Ingredients)
            {
                Consume(inventory, ingredient, journal);
            }

            InventoryPlacement plan = inventory.TryPickUp(
                recipe.OutputItemDefinitionId,
                recipe.OutputCount,
                out InventoryItemInstance? produced,
                preserveEquippedArmour: true);
            if (produced is null)
            {
                // The ingredients are already gone at this point, so put them back before the
                // refusal is reported: a craft that fails must leave the inventory exactly as it
                // found it, or the player pays for an item they never received.
                Rollback(journal);
                refusal = CraftRefusal.NoRoom;
                error = plan.Error;
                break;
            }

            foreach (JournalEntry entry in journal)
            {
                consumed[entry.Item.Guid] = new ConsumedItem(entry.Item, entry.Removed);
            }

            changes.Add(new CraftItemChange(
                produced.Guid,
                plan.Kind == InventoryPlacementKind.Stack
                    ? CraftItemChangeKind.GrantedIntoStack
                    : CraftItemChangeKind.Granted,
                produced));
            crafted++;
        }

        if (crafted == 0)
        {
            return new CraftOutcome
            {
                Recipe = recipe,
                RecipeId = recipeId,
                Requested = requested,
                Crafted = 0,
                Refusal = refusal,
                ContainerError = error,
                Shortfall = refusal == CraftRefusal.MissingIngredients ? shortfall : [],
            };
        }

        // One packet pair per touched ingredient, whatever the craft count - a Craft Max that ate
        // nine cloth from one stack sends one delete and one add, not nine of each. Consumption
        // packets go out before the grants: everything after them names a guid the client must
        // still recognise, and an ItemAdd for a guid the client has just been told to delete is the
        // one ordering that cannot be recovered from.
        var ordered = new List<CraftItemChange>(consumed.Count + changes.Count);
        foreach (ConsumedItem item in consumed.Values.OrderBy(c => c.Item.Guid))
        {
            if (item.Removed)
            {
                // Only now that the craft has committed is it safe to forget the instance: until
                // this point a refused placement could still have needed it put back. RemoveUnits
                // with a zero count takes the whole (already empty) stack, which unbinds it and
                // drops it from PlayerInventory.Items so the guid can never be named again.
                inventory.RemoveUnits(item.Item.Guid, 0);
                ordered.Add(new CraftItemChange(item.Item.Guid, CraftItemChangeKind.Removed, null));
            }
            else
            {
                ordered.Add(new CraftItemChange(item.Item.Guid, CraftItemChangeKind.CountChanged, item.Item));
            }
        }

        // A grant that merged onto a stack the craft also consumed from would otherwise be sent
        // twice; the grant wins, because it carries the final count.
        var granted = new HashSet<ulong>(changes.Select(c => c.ItemGuid));
        ordered.RemoveAll(c => granted.Contains(c.ItemGuid) && c.Kind != CraftItemChangeKind.Removed);
        // A stack created on the first craft and enlarged by later crafts needs one final add.
        // Retain its first change kind so a brand-new guid never receives an unnecessary delete.
        ordered.AddRange(changes.GroupBy(c => c.ItemGuid).Select(group => group.First()));

        return new CraftOutcome
        {
            Recipe = recipe,
            RecipeId = recipeId,
            Requested = requested,
            Crafted = crafted,
            Refusal = refusal,
            ContainerError = error,
            Shortfall = refusal == CraftRefusal.MissingIngredients ? shortfall : [],
            Changes = ordered,
        };
    }

    /// <summary>
    /// Available ingredients in containers, including bandages/first aid kits in quick slots.
    /// Other worn equipment is excluded.
    /// </summary>
    public static uint CountAvailable(PlayerInventory inventory, uint itemDefinitionId)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        uint total = 0;
        foreach (InventoryContainer container in inventory.Containers.Values)
        {
            foreach (InventoryItemInstance item in container.Slots.Values)
            {
                if (item.DefinitionId == itemDefinitionId)
                {
                    total += item.Count;
                }
            }
        }

        if (inventory.Options.QuickUseConsumables && itemDefinitionId is CraftingCatalog.FieldBandage or CraftingCatalog.FirstAidKit)
            foreach (var item in inventory.LoadoutSlots.Values)
                if (item.DefinitionId == itemDefinitionId && item.ContainerGuid == 0) total += item.Count;
        return total;
    }

    /// <summary>How many complete crafts the current inventory could pay for, ignoring room for the
    /// output. This is the number the window's <c>Craft Max</c> is really asking about.</summary>
    public static uint CraftableCount(PlayerInventory inventory, RecipeDefinition recipe)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(recipe);
        uint max = uint.MaxValue;
        foreach (RecipeIngredient ingredient in recipe.Ingredients)
        {
            uint have = CountAvailable(inventory, ingredient.ItemDefinitionId);
            max = Math.Min(max, ingredient.Quantity == 0 ? uint.MaxValue : have / ingredient.Quantity);
        }

        return max == uint.MaxValue ? 0 : max;
    }

    /// <summary>
    /// The <c>0x26 02 Recipe.ComponentUpdate</c> packets that would bring one recipe's ingredient
    /// panel in line with the inventory. Only sent when
    /// <see cref="CraftingOptions.SendComponentCounts"/> is on.
    /// </summary>
    public static IReadOnlyList<RecipeComponentUpdate> ComponentCountUpdates(
        PlayerInventory inventory,
        RecipeDefinition recipe)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(recipe);
        var updates = new List<RecipeComponentUpdate>(recipe.Ingredients.Count);
        for (int index = 0; index < recipe.Ingredients.Count; index++)
        {
            updates.Add(RecipeComponentUpdate.Counts(
                recipe.RecipeId,
                (uint)index,
                CountAvailable(inventory, recipe.Ingredients[index].ItemDefinitionId)));
        }

        return updates;
    }

    /// <summary>
    /// One craft's worth of every ingredient in the catalogue, for
    /// <see cref="CraftingOptions.SeedIngredients"/>. Outputs are never seeded.
    /// </summary>
    public static IReadOnlyList<RecipeIngredient> SeedGrants()
    {
        var needed = new Dictionary<uint, uint>();
        foreach (RecipeDefinition recipe in CraftingCatalog.Recipes)
        {
            foreach (RecipeIngredient ingredient in recipe.Ingredients)
            {
                needed[ingredient.ItemDefinitionId] =
                    Math.Max(needed.GetValueOrDefault(ingredient.ItemDefinitionId), ingredient.Quantity);
            }
        }

        return needed.OrderBy(p => p.Key).Select(p => new RecipeIngredient(p.Key, p.Value)).ToArray();
    }

    private static IReadOnlyList<RecipeIngredient> Shortfall(PlayerInventory inventory, RecipeDefinition recipe)
    {
        List<RecipeIngredient>? missing = null;
        foreach (RecipeIngredient ingredient in recipe.Ingredients)
        {
            uint have = CountAvailable(inventory, ingredient.ItemDefinitionId);
            if (have < ingredient.Quantity)
            {
                missing ??= [];
                missing.Add(new RecipeIngredient(ingredient.ItemDefinitionId, ingredient.Quantity - have));
            }
        }

        return missing ?? (IReadOnlyList<RecipeIngredient>)[];
    }

    private static void Consume(PlayerInventory inventory, RecipeIngredient ingredient, List<JournalEntry> journal)
    {
        uint remaining = ingredient.Quantity;
        foreach (InventoryContainer container in inventory.Containers.Values.OrderBy(c => c.Guid))
        {
            // Snapshot: the slot dictionary is mutated when a stack is exhausted.
            foreach (KeyValuePair<uint, InventoryItemInstance> slot in container.Slots.ToArray())
            {
                if (remaining == 0)
                {
                    return;
                }

                InventoryItemInstance item = slot.Value;
                if (item.DefinitionId != ingredient.ItemDefinitionId)
                {
                    continue;
                }

                uint take = Math.Min(remaining, item.Count);
                var entry = new JournalEntry(item, container, slot.Key, item.Count, Removed: false);
                item.Count -= take;
                remaining -= take;
                if (item.Count == 0)
                {
                    // The slot is freed here but the INSTANCE is kept, because a placement that
                    // fails in a moment still has to be able to put it back (Rollback). The
                    // committed path calls PlayerInventory.RemoveUnits to forget it for good.
                    container.Remove(item);
                    entry = entry with { Removed = true };
                }

                journal.Add(entry);
            }
        }
        if (remaining == 0 || ingredient.ItemDefinitionId is not (CraftingCatalog.FieldBandage or CraftingCatalog.FirstAidKit))
            return;
        foreach (var item in inventory.LoadoutSlots.Values)
        {
            if (item.DefinitionId != ingredient.ItemDefinitionId || item.ContainerGuid != 0) continue;
            uint take = Math.Min(remaining, item.Count);
            journal.Add(new(item, null, 0, item.Count, Removed: take == item.Count));
            item.Count -= take;
            remaining -= take;
            // Keep the binding until commit. A capacity refusal can restore the exact Q/E stack.
            if (remaining == 0) return;
        }
    }

    private static void Rollback(List<JournalEntry> journal)
    {
        for (int i = journal.Count - 1; i >= 0; i--)
        {
            JournalEntry entry = journal[i];
            entry.Item.Count = entry.PreviousCount;
            if (entry.Removed && entry.Container is not null)
            {
                entry.Container.Place(entry.Item, entry.Slot);
            }
        }
    }

    private readonly record struct JournalEntry(
        InventoryItemInstance Item,
        InventoryContainer? Container,
        uint Slot,
        uint PreviousCount,
        bool Removed);

    private readonly record struct ConsumedItem(InventoryItemInstance Item, bool Removed);
}
