using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private static int? CaptureGroundWeaponMagazine(GatewaySessionState state, ulong itemGuid, uint definitionId)
    {
        int clip = RetailBalance.ClipSize(definitionId);
        if (clip <= 0 || AmmoTypes.AmmoItemFor(definitionId) == 0
            || state.Combat.Shooter.ItemDefinitionOf(itemGuid) != definitionId)
            return null;

        int rounds = state.Combat.Shooter.AmmoOf(itemGuid);
        return rounds >= 0 && rounds <= clip ? rounds : null;
    }

    private static void RestoreGroundWeaponMagazine(GatewaySessionState state, GroundLootItem ground,
        InventoryItemInstance received)
    {
        if (received.Count == 1)
            RestoreGroundWeaponMagazine(state, ground, received.Guid, received.DefinitionId);
    }

    private static void RestoreGroundWeaponMagazine(GatewaySessionState state, GroundLootItem ground,
        ulong receivedGuid, uint definitionId)
    {
        if (ground.Count != 1 || ground.ItemDefinitionId != definitionId
            || ground.MagazineRounds is not { } rounds || rounds < 0
            || AmmoTypes.AmmoItemFor(definitionId) == 0 || rounds > RetailBalance.ClipSize(definitionId))
            return;

        // Ownership has already transferred. Only seed the new instance: a duplicate publication
        // must not restore rounds after this weapon has fired. No reserve or reload clock moves.
        state.Combat.Shooter.EnsureDeclared(receivedGuid, definitionId, rounds);
    }

    private void StopReloadForGroundDrop(SoeConnection connection, GatewaySessionState state, ulong droppedGuid)
    {
        if (WeaponFireArm.CancelReload(state.Combat, droppedGuid,
            "weapon dropped", stopClientReload: true) is not { } stopped)
            return;

        state.WeaponArmResults.Clear();
        state.WeaponArmResults.Add(stopped);
        DrainCombatArm(connection, state);
    }
}
