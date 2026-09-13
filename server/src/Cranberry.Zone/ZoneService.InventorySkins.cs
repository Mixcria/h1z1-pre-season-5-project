using Cranberry.Transport;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Inventory actions travel over the same authenticated WindowEvent binding used by the
    // other custom in-game controls. The native hood binding sent no request in the live
    // playtest, and the cached skin selector returned no rows outside the main-menu editor.
    private const string InventoryActionWindow = "Cranberry.Inventory";

    private void HandleInventoryAction(SoeConnection connection, GatewaySessionState state, string action)
    {
        string[] parts = action.Split(':');
        if (parts.Length != 3 || state.DeathSent || state.Match == MatchStep.Menu
            || !ulong.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong guid)
            || state.Inventory is not { } inventory)
        {
            RefuseInventoryAction(connection, state, "invalid or missing item");
            return;
        }

        if (parts[0] == "shred" && parts[2] == "1")
        {
            HandleFootwearShred(connection, state, inventory, guid);
            return;
        }
        if (!inventory.Items.TryGetValue(guid, out var item))
        {
            RefuseInventoryAction(connection, state, "invalid or missing item");
            return;
        }

        if (parts[0] == "skin" && parts[2] == "selected")
        {
            ApplyInventoryItemSkin(connection, state, item);
            return;
        }
        if (parts[0] == "hood" && parts[2] is "0" or "1")
        {
            var plan = ItemVerbs.SetHood(inventory, item,
                parts[2] == "1" ? ItemUseOptionKind.HoodieUp : ItemUseOptionKind.HoodieDown);
            if (plan.Kind != ItemActionKind.Hood)
            {
                RefuseInventoryAction(connection, state, plan.Rule);
                return;
            }
            InventoryActions.Apply(inventory, plan);
            SendCharacterAppearance(connection, state, $"inventory hood {parts[2]}");
            _log.Info($"{connection} inventory: hoodie {guid} is now {(inventory.HoodUp ? "up" : "down")}");
            return;
        }
        RefuseInventoryAction(connection, state, "unknown inventory action");
    }

    private void RefuseInventoryAction(SoeConnection connection, GatewaySessionState state, string reason)
    {
        SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
        _log.Info($"{connection} inventory: refused action: {reason}");
    }

    private static bool TryGetInventorySkinTarget(GatewaySessionState state, InventoryItemInstance item,
        IReadOnlyList<AugustSkinCatalogEntry> selections, AccountEconomySnapshot? account,
        out AugustSkinCatalogEntry selected)
    {
        selected = default;
        if (state.DeathSent || state.Match == MatchStep.Menu || state.Inventory is not { } inventory
            || ItemVerbs.Skin(inventory, item).Kind != ItemActionKind.Skin
            || !AugustWornSkins.TryGetCategory(item.DefinitionId, out uint category))
            return false;

        selected = selections.FirstOrDefault(s => s.CategoryPrototypeId == category);
        // The live inventory's display resolver projects the wardrobe preset before an
        // item has been explicitly reskinned. That projection must not hide Skin on a
        // picked-up garment. Pinning the preset below also refreshes its native item and
        // equipment appearance. A matching explicit skin remains an idempotent refusal.
        bool unappliedApparel = item.SkinOverrideDefinitionId is null
            && item.DefinitionId != selected.RewardItemId
            && AugustSkinCatalog.Apparel.Any(s => s.CategoryPrototypeId == category);
        return selected.RewardItemId != 0
            && (item.DisplayDefinitionId != selected.RewardItemId || unappliedApparel)
            && (account is null || account.Owns(selected.AccountItemId));
    }

    private void ApplyInventoryItemSkin(SoeConnection connection, GatewaySessionState state,
        InventoryItemInstance item)
    {
        if (!TryGetInventorySkinTarget(state, item, state.Wardrobe.Snapshot(),
            _economy is null ? null : ReadAccountEconomy(state), out var selected))
        {
            RefuseInventoryAction(connection, state, $"item {item.Guid} is not eligible for a different owned preset");
            return;
        }
        var inventory = state.Inventory!;
        if (!inventory.TrySetItemSkin(item.Guid, selected.RewardItemId))
        {
            RefuseInventoryAction(connection, state, "item skin validation failed");
            return;
        }
        RefreshWeaponSkinItems(connection, state, inventory, [item]);
        _log.Info($"{connection} inventory: applied saved skin {selected.AccountItemId} (reward {selected.RewardItemId}) to item {item.Guid}");
    }

    private void ApplyInventoryWeaponSkin(
        SoeConnection connection, GatewaySessionState state, SkinItemSelectionRequest request)
    {
        ulong targetGuid = state.InventorySkinTargetItemGuid;
        state.InventorySkinTargetItemGuid = 0;
        if (state.DeathSent || state.Inventory is not { } inventory
            || targetGuid == 0 || Environment.TickCount64 > state.InventorySkinTargetExpiresAtMs
            || !inventory.Items.TryGetValue(targetGuid, out var item)
            || ItemVerbs.Skin(inventory, item).Kind != ItemActionKind.Skin
            || !AugustWornSkins.TryGetCategory(item.DefinitionId, out uint category)
            || state.Wardrobe.Snapshot().Any(s => s.CategoryPrototypeId == category
                && s.RewardItemId == item.DisplayDefinitionId)
            || category != request.CategoryPrototypeId
            || request.SlotType != SetSkinItemManager.WeaponCollectionId
            || request.SubOpcode == SkinItemSelectionRequest.RequestUnsetSkinItem
            || !AugustWardrobeCatalog.TryResolveClicked(request.ClickedId, out var selected)
            || selected.CategoryPrototypeId != category
            || selected.RewardItemId == item.DisplayDefinitionId
            || (_economy is not null && !ReadAccountEconomy(state).Owns(selected.AccountItemId)))
        {
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
            _log.Info($"{connection} inventory: refused stale, unowned or incompatible weapon skin target {targetGuid}");
            return;
        }

        bool changed = item.DisplayDefinitionId != selected.RewardItemId;
        inventory.TrySetItemSkin(item.Guid, selected.RewardItemId);
        // This choice belongs to this match item. Keep the account's saved category preset and
        // other copies of the weapon intact, and never grant ownership of a looted cosmetic.
        if (changed)
            RefreshWeaponSkinItems(connection, state, inventory, [item]);
    }

    /// <summary>
    /// The inventory skin picker uses the existing category selection request. Its accepted
    /// selection must also repaint already-carried weapons. ItemUpdate intentionally ignores
    /// definition id in August (141479ae0), so use the established delete/add refresh with the
    /// same instance, magazine, durability and bindings; no inventory or combat state is replaced.
    /// </summary>
    private void RefreshCarriedWeaponSkin(
        SoeConnection connection, GatewaySessionState state, uint categoryPrototypeId)
    {
        if (state.Match == MatchStep.Menu || state.Inventory is not PlayerInventory inventory
            || !AugustSkinCatalog.Weapons.Any(s => s.CategoryPrototypeId == categoryPrototypeId))
            return;

        var affected = inventory.Items.Values
            .Where(item => AugustWornSkins.TryGetCategory(item.DefinitionId, out uint category)
                && category == categoryPrototypeId).ToArray();
        if (affected.Length == 0) return;

        RefreshWeaponSkinItems(connection, state, inventory, affected);
    }

    private void RefreshWeaponSkinItems(SoeConnection connection, GatewaySessionState state,
        PlayerInventory inventory, IReadOnlyList<InventoryItemInstance> affected)
    {
        foreach (var item in affected)
        {
            var record = item.ToRecord(state.Guid);
            int durability = state.Combat.Shooter.DurabilityOf(item.Guid);
            if (durability >= 0)
                record = record with
                {
                    BaseDurability = (uint)_options.Combat.Ammo.MaxDurability,
                    CurrentDurability = (uint)durability,
                    MaxDurability = (uint)_options.Combat.Ammo.MaxDurability,
                };
            SendTunnel(connection, new ItemDelete(state.Guid, item.Guid).WriteTo);
            SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, record));
        }
        SendLoadoutSlots(connection, inventory);
        foreach (var container in inventory.Containers.Values)
            SendTunnel(connection, inventory.ToUpdate(container).WriteTo);
        SendCharacterAppearance(connection, state, "carried weapon skin changed");
    }
}
