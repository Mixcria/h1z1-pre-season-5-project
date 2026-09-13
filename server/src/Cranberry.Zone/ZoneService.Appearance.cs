using Cranberry.Zone.Appearance;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    /// <summary>
    /// An established world actor can replace individual occupied equipment slots. Lifecycle
    /// callers forget Dress when the actor is recreated; that still forces its complete baseline.
    /// </summary>
    private static IReadOnlyList<byte[]>? InWorldEquipmentDelta(
        GatewaySessionState state, EquipmentDressSnapshot? snapshot)
    {
        if (state.Match is not (MatchStep.Lobby or MatchStep.InMatch)
            || (!state.Dress.HasBaseline && !state.Dress.RequiresSlotReassert) || snapshot is null
            || state.EquipmentDress is not { } previous)
            return null;

        return snapshot.DeltaFrom(previous, state.Guid, state.Weapons.Clearance,
            handBoundSeparately: state.Inventory?.Options.UseWieldSequence == true,
            reassertBindings: state.Dress.RequiresSlotReassert);
    }
}
