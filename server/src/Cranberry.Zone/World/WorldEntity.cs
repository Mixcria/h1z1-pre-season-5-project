using System.Numerics;

namespace Cranberry.Zone.World;

/// <summary>
/// Everything in the world that is not a player: one class with a small variant tail. Five kinds and
/// about ten fields do not pay for a hierarchy plus casts, and at 10–150 entities the layout is not
/// on the critical path (docs/22 §2, disagreement 2). There is no handle type: the object reference
/// is the handle and <see cref="Removed"/> is the staleness check.
/// </summary>
public sealed class WorldEntity
{
    /// <summary>Stable slot in <see cref="World"/>'s entity table; also its interest-grid key base.</summary>
    public int Slot { get; internal set; } = -1;

    public EntityId Id;
    public EntityKind Kind;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public ushort GridCell;

    /// <summary>Set by a system; swept at the end of the tick by <see cref="World.SweepRemoved"/>.</summary>
    public bool Removed;

    // --- GroundItem (docs/13) ---

    /// <summary>From <c>ClientItemDefinitions.txt</c>; never rides in the spawn packet itself.</summary>
    public uint ItemDefinitionId;

    /// <summary>From <c>Models.txt</c> — what the client actually renders on the ground.</summary>
    public uint GroundModelId;
    public uint NameId;
    public uint Count;

    /// <summary>Single-shot claim: one <c>[F]</c> press emits both PlayerSelect and InteractRequest
    /// for the same object (docs/13 §8), so the second one must find nothing to grant.</summary>
    public bool Claimed;

    // --- Vehicle / parachute (docs/12) ---

    /// <summary>13 = the August parachute (<c>ZoneOptions.ParachuteVehicleId</c>).</summary>
    public uint VehicleId;

    /// <summary>9374 = <c>Common_Parachute01.adr</c> (<c>ZoneOptions.ParachuteModelId</c>).</summary>
    public uint VehicleModelId;

    public EntityId Owner;
    public EntityId Driver;
    public int Seats = 1;
    public float VehicleHealth;

    public override string ToString() => $"{Kind} {Id} slot {Slot}";
}
