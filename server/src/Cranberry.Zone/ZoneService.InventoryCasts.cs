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

    private static void CancelInventoryCasts(GatewaySessionState state)
    {
        state.PendingCraft = null;
        state.PendingShred = null;
        state.CraftBusyUntil = 0;
        state.ShredBusyUntil = 0;
    }
}
