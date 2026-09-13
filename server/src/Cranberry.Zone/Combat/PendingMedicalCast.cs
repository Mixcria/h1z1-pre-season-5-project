using System.Numerics;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Movement;

namespace Cranberry.Zone.Combat;

/// <summary>
/// Identity and movement origin for one unspent medical use. An old timer can only finish this
/// particular cast, in this inventory and world; cancellation never needs an item refund.
/// </summary>
public sealed class PendingMedicalCast(PlayerInventory inventory, int worldGeneration, Vector3? origin)
{
    public PlayerInventory Inventory { get; } = inventory;
    public int WorldGeneration { get; } = worldGeneration;
    private readonly StationaryCastPosition _position = new(origin, MedicalModel.CastCancelRadiusUnits);
    public Vector3? Origin => _position.Origin;

    public bool HasMoved(Vector3? position) => _position.HasMoved(position);
}
