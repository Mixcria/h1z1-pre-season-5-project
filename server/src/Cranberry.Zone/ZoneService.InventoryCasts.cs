using Cranberry.Transport;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // A cast remains pending until its listener-thread callback commits or is cancelled.
    // The displayed deadline alone is insufficient: its callback may still be queued.
    private sealed record PendingInventoryCast(PlayerInventory Inventory, int WorldGeneration,
        int InteractionGeneration);

    private static bool InventoryCastContextMatches(GatewaySessionState state, PendingInventoryCast cast) =>
        ReferenceEquals(state.Inventory, cast.Inventory)
        && state.WorldGeneration == cast.WorldGeneration
        && state.InteractionGeneration == cast.InteractionGeneration;

    private static void DiscardStaleInventoryCasts(GatewaySessionState state)
    {
        if (state.PendingCraft is { } craft && !InventoryCastContextMatches(state, craft))
        {
            state.PendingCraft = null;
            state.CraftBusyUntil = 0;
        }
        if (state.PendingShred is { } shred && !InventoryCastContextMatches(state, shred))
        {
            state.PendingShred = null;
            state.ShredBusyUntil = 0;
        }
    }

    // August cf02 replaces one subject-owned timer; cf03 has no cast identifier and
    // clears that same timer (140ccfd50 / 140ccff80 -> 140cd0d90). Refuse overlap as
    // server policy, preserving the current owner until its callback actually runs.
    // This does not establish the original server's refuse/queue/interrupt policy.
    private bool RefuseOverlappingInventoryCast(SoeConnection connection, GatewaySessionState state)
    {
        DiscardStaleInventoryCasts(state);
        if (state.PendingMedicalCast is { } medical
            && (medical.WorldGeneration != state.WorldGeneration
                || !ReferenceEquals(medical.Inventory, state.Inventory)))
            CancelMedicalCast(connection, state, "world or inventory changed", notify: false);

        long now = Environment.TickCount64;
        if (state.PendingCraft is null && state.PendingShred is null
            && state.PendingMedicalCast is null && state.PendingVehicleRemoval is null
            && state.CraftBusyUntil <= now && state.ShredBusyUntil <= now && state.ConsumeBusyUntil <= now)
            return false;

        SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.ContainerInUse).WriteTo);
        _log.Info($"{connection} inventory: timed action refused because another cast still owns the interaction timer");
        return true;
    }

    private static void CancelInventoryCasts(GatewaySessionState state)
    {
        state.PendingCraft = null;
        state.PendingShred = null;
        state.CraftBusyUntil = 0;
        state.ShredBusyUntil = 0;
    }
}
