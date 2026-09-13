using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool TryHandleNativeItemCommand(
        SoeConnection connection, GatewaySessionState state, byte[] payload)
    {
        bool add = NativeItemPackets.Matches(payload, NativeItemPackets.AddSubOpcode);
        bool list = NativeItemPackets.Matches(payload, NativeItemPackets.ListSubOpcode);
        bool delete = NativeItemPackets.Matches(payload, NativeItemPackets.DeleteSubOpcode);
        bool drop = NativeItemDrop.Matches(payload);
        if (!add && !list && !delete && !drop) return false;
        string command = add ? "item add" : list ? "item list" : delete ? "item delete" : "item drop";
        if (!TryAuthorizeNativeConsole(connection, state, command)) return true;

        ConsoleReply reply;
        try
        {
            if (add)
            {
                var request = NativeItemAdd.Parse(payload);
                reply = request.TargetGuid != 0 && request.TargetGuid != state.Guid
                    ? ConsoleReply.Failed("/item add currently supports your own inventory only; target was not changed")
                    : request.TintId != 0
                        ? ConsoleReply.Failed("/item add tint overrides are not supported; use tint 0")
                        : request.RentalTerm != 0
                            ? ConsoleReply.Failed("/item addRental is not supported; use /item add for a normal item")
                            // Share the normal pickup path, including capacity and stack splitting.
                            : ConsoleGive(connection, state, request.DefinitionId, request.Count);
            }
            else if (list)
            {
                var request = NativeItemList.Parse(payload);
                reply = request.TargetGuid != 0 && request.TargetGuid != state.Guid
                    ? ConsoleReply.Failed("/item list currently supports your own inventory only")
                    : request.Mode != 0
                        ? ConsoleReply.Failed($"/item list mode {request.Mode} is not supported")
                        : ConsoleInventory(state);
            }
            else if (delete)
            {
                var request = NativeItemDelete.Parse(payload);
                reply = request.TargetGuid != 0 && request.TargetGuid != state.Guid
                    ? ConsoleReply.Failed("/item delete currently supports your own inventory only; target was not changed")
                    : ConsoleNativeRemoveItem(connection, state, request.ItemGuid, count: 0, delete: true);
            }
            else
            {
                var request = NativeItemDrop.Parse(payload);
                reply = request.Count == 0
                    ? ConsoleReply.Failed("/item drop count must be positive")
                    : ConsoleNativeRemoveItem(connection, state, request.ItemGuid, request.Count, delete: false);
            }
        }
        catch (PacketFormatException ex)
        {
            reply = ConsoleReply.Failed(ex.Message);
        }
        NativeConsoleReply(connection, state, reply);
        _log.Info($"{connection} native console: /{command} -> {string.Join("; ", reply.Lines)}");
        return true;
    }

    private ConsoleReply ConsoleNativeRemoveItem(
        SoeConnection connection, GatewaySessionState state, ulong itemGuid, uint count, bool delete)
    {
        if (state.Inventory is not PlayerInventory inventory) return ConsoleReply.Failed("inventory unavailable");
        if (!inventory.Items.TryGetValue(itemGuid, out InventoryItemInstance? item))
            return ConsoleReply.Failed($"item guid {itemGuid} is not in your inventory; /item list shows item instances");
        if (!delete && count > item.Count)
            return ConsoleReply.Failed($"the item stack contains only {item.Count}; requested {count}");

        ItemActionResult plan = delete ? InventoryActions.ResolveDelete(inventory, itemGuid)
            : InventoryActions.Resolve(inventory, new RequestUseItem(0, 4,
                state.Guid, state.Guid, state.Guid, itemGuid, false, count, 0));
        if (plan.Kind != (delete ? ItemActionKind.Delete : ItemActionKind.Drop)) return ConsoleReply.Failed(plan.Rule);
        uint previous = item.Count;
        ApplyInventoryAction(connection, state, inventory, plan);
        uint remaining = inventory.Items.TryGetValue(itemGuid, out var after) ? after.Count : 0;
        return remaining == previous - plan.Count
            ? ConsoleReply.Did($"{(delete ? "deleted" : "dropped")} {ItemNames.Label(item.DefinitionId)} x{plan.Count}")
            : ConsoleReply.Failed($"{(delete ? "delete" : "drop")} was refused");
    }
}
