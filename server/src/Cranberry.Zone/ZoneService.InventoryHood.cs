using Cranberry.Zone.Equipment;
using Cranberry.Zone.Inventory;
using Cranberry.Transport;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void SendInventoryActionState(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Inventory is not PlayerInventory inventory)
            return;

        ulong guid = inventory.EquipmentSlots.TryGetValue(BodySlots.Chest, out var chest)
            ? chest.Guid : 0;
        string hood = FormattableString.Invariant($"{guid}:{(inventory.HoodUp ? 1 : 0)}");
        var selections = state.Wardrobe.Snapshot();
        var account = _economy is null ? null : ReadAccountEconomy(state);
        string skinTargets = string.Join(';', inventory.Items.Values
            .Where(item => TryGetInventorySkinTarget(state, item, selections, account, out _))
            .Select(item => item.Guid).Distinct().Order()
            .Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // One cache covers both values so full string-table replacements resend both.
        string value = hood + "|" + skinTargets;
        if (state.InventoryActionUiState == value)
            return;

        SendTunnel(connection, writer => new UpdateStringHashToValueManager(
            "Cranberry.Inventory.SkinTargets", skinTargets).WriteTo(writer));
        SendTunnel(connection, writer => new UpdateStringHashToValueManager(
            "Cranberry.Inventory.Hood", hood).WriteTo(writer));
        state.InventoryActionUiState = value;
    }
}
