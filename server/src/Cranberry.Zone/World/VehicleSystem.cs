namespace Cranberry.Zone.World;

/// <summary>
/// Mounts, the parachute lifecycle and touchdown.
/// <para>
/// <b>Scaffold stub (migration step 8).</b> It collects what the drain step stages so the tick order
/// is written once and never re-litigated, but it applies nothing: the managed-pose ownership rule
/// today lives in <c>SessionMovementState.TryApplyManaged</c> and the chute lifecycle in
/// <c>ZoneService</c>, and both move here as a whole in step 8 (docs/12, docs/22 §8).
/// </para>
/// </summary>
public sealed class VehicleSystem : ISystem
{
    private readonly List<int> _managedPoseSlots = [];
    private readonly List<Command> _mountRequests = [];

    /// <summary>Channel-3 records staged this tick.</summary>
    public int StagedManagedPoses => _managedPoseSlots.Count;

    /// <summary>Mount / dismount / auto-mount echoes staged this tick.</summary>
    public int StagedMountRequests => _mountRequests.Count;

    public long DroppedManagedPoses { get; private set; }

    public long DroppedMountRequests { get; private set; }

    /// <summary>Channel 3: a pose for an object the client owns.</summary>
    public void Stage(MatchPlayer player, in Command command, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(player);
        _ = command;
        _ = payload.Length;
        _managedPoseSlots.Add(player.Slot);
    }

    public void StageMount(MatchPlayer player, in Command command)
    {
        ArgumentNullException.ThrowIfNull(player);
        _mountRequests.Add(command);
    }

    public void Tick(in TickContext context)
    {
        _ = context;

        // Step 8 fills this in. Until then staged input is counted and discarded, which is exactly
        // what happens today for a player the zone has no vehicle model for.
        DroppedManagedPoses += _managedPoseSlots.Count;
        DroppedMountRequests += _mountRequests.Count;
        _managedPoseSlots.Clear();
        _mountRequests.Clear();
    }
}
