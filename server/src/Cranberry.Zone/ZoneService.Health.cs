using Cranberry.Transport;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // The original ClientUpdate percent and the current HUD resource datasource are
    // separate consumers. Publish both from the same authoritative health transition.
    private void PublishPlayerHealth(SoeConnection connection, GatewaySessionState state, uint previous)
    {
        uint current = state.Hitpoints;
        uint maximum = _options.Gas.MaxHitpoints;
        SendTunnel(connection, writer => new GasPackets.Hitpoints(current, maximum).WriteTo(writer));
        SendTunnel(connection, CharacterResourceUpdate.Health(state.Guid, current, previous, maximum).WriteTo);
        PublishTeamHudStatus(state);
    }
}
