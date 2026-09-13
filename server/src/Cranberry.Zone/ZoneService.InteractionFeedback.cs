using Cranberry.Transport;
using Cranberry.Zone.Loot;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void SendPickupSound(SoeConnection connection, GroundLootItem item)
    {
        // After a successful server-side grant, send before the inventory/appearance packets
        // and RemovePlayer. The client can queue sound before those handlers load item assets;
        // duplicate F packets and refused grants stay silent.
        var effect = new PlayWorldCompositeEffect(
            item.WorldGuid, PickupEffects.ForItem(item.ItemDefinitionId), item.Position);
        SendTunnel(connection, effect.WriteTo);
    }
}
