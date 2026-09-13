using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Cranberry.Zone;

/// <summary>
/// Server-owned view of an entity's latest movement. Client packets are sparse deltas, so fields
/// omitted by a later packet retain their last value. <see cref="Position"/> and
/// <see cref="Rotation"/> normalize the ordinary and precise-pose wire forms into one world pose.
/// </summary>
public sealed record EntityMovementState(
    uint ClientTime,
    byte State,
    uint? Posture,
    Vector3? Position,
    Quaternion? Rotation,
    float? Orientation,
    float? VerticalSpeed,
    float? HorizontalSpeed,
    Vector3? AuxiliaryVector,
    float? Scalar14C,
    float? Scalar150,
    float? Scalar154,
    float? Scalar140,
    float? Scalar144,
    ClientMovementUpdate LastUpdate)
{
    /// <summary>
    /// Native 0x200 lookInfo.Y, in radians (negative looks down). Keep it separate from
    /// Rotation, which can also hold a managed actor's precise-pose quaternion.
    /// Sparse movement retains aim until the next lookInfo update; zoning clears it.
    /// </summary>
    public float? LookPitch { get; init; }

    public static EntityMovementState Apply(
        EntityMovementState? previous,
        ClientMovementUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        return new EntityMovementState(
            update.ClientTime,
            update.State,
            update.Posture ?? previous?.Posture,
            update.EffectivePosition ?? previous?.Position,
            update.Rotation ?? update.PrecisePose?.Rotation ?? previous?.Rotation,
            update.Orientation ?? previous?.Orientation,
            update.VerticalSpeed ?? previous?.VerticalSpeed,
            update.HorizontalSpeed ?? previous?.HorizontalSpeed,
            update.AuxiliaryVector ?? previous?.AuxiliaryVector,
            update.Scalar14C ?? previous?.Scalar14C,
            update.Scalar150 ?? previous?.Scalar150,
            update.Scalar154 ?? previous?.Scalar154,
            update.Scalar140 ?? previous?.Scalar140,
            update.Scalar144 ?? previous?.Scalar144,
            update)
        {
            LookPitch = update.Rotation?.Y ?? previous?.LookPitch
        };
    }
}

/// <summary>An entity for which this client has explicitly been given simulation ownership.</summary>
public sealed record ManagedEntityMovementState(
    uint TransientId,
    ulong Guid,
    EntityMovementState? Movement);

/// <summary>
/// Movement authority scoped to one gateway session. Managed updates are accepted only after the
/// server registers the transient-id/guid pair it handed to the client; arbitrary channel-3 ids
/// cannot create authoritative entities.
/// </summary>
public sealed class SessionMovementState
{
    private readonly Dictionary<uint, ManagedEntityMovementState> _managed = [];

    public EntityMovementState? Player { get; private set; }
    public int ManagedEntityCount => _managed.Count;

    /// <summary>
    /// True after a managed mount pose has been promoted and until the first real channel-2 world
    /// position arrives. The August client can leave one in-flight zero pose behind at dismount.
    /// </summary>
    public bool AwaitingPostDismountPose { get; private set; }

    /// <summary>
    /// A new world creates a new player actor. Its sparse records must not inherit the previous
    /// world's position, body angles or animation inputs, including intervening LoginZone input.
    /// Managed actor ownership is maintained by its separate lifecycle.
    /// </summary>
    public void ResetPlayerWorldPose()
    {
        Player = null;
        AwaitingPostDismountPose = false;
    }

    public EntityMovementState ApplyPlayer(ClientMovementUpdate update)
    {
        Player = EntityMovementState.Apply(Player, update);
        return Player;
    }

    /// <summary>
    /// D340: a seated rider's world position is the car's. The seated client's own channel-2 record
    /// carries a dummy (0,0,0) pose, so the handler refuses it and pins the player here instead, and
    /// the world pump keeps streaming around the vehicle rather than around the origin or the
    /// entry point. A no-op before the first on-foot pose.
    /// </summary>
    public void PinPlayer(Vector3 position)
    {
        if (Player is { } player)
        {
            Player = player with { Position = position };
        }
    }

    /// <summary>
    /// Applies an ordinary player update unless it is the stale zero/sparse pose that can cross
    /// the channel-3 to channel-2 dismount boundary. Returns false while preserving the promoted
    /// landing pose; the first finite non-zero position clears the guard.
    /// </summary>
    public bool TryApplyPlayer(
        ClientMovementUpdate update,
        [NotNullWhen(true)] out EntityMovementState? movement)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (AwaitingPostDismountPose && !IsFiniteNonZero(update.EffectivePosition))
        {
            movement = Player;
            return false;
        }

        AwaitingPostDismountPose = false;
        movement = ApplyPlayer(update);
        return true;
    }

    public void RegisterManagedEntity(uint transientId, ulong guid)
    {
        if (guid == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(guid), "A managed entity cannot use the null guid.");
        }

        if (_managed.TryGetValue(transientId, out ManagedEntityMovementState? existing))
        {
            if (existing.Guid != guid)
            {
                throw new InvalidOperationException(
                    $"Transient id {transientId} is already registered to guid {existing.Guid}.");
            }

            return;
        }

        _managed.Add(transientId, new ManagedEntityMovementState(transientId, guid, Movement: null));
        AwaitingPostDismountPose = false;
    }

    public bool RemoveManagedEntity(uint transientId) => _managed.Remove(transientId);

    /// <summary>
    /// Carries the last authoritative pose of a client-managed mount back to its rider before the
    /// mount is removed. The August client sends zero-position channel-2 records while mounted;
    /// without this hand-off the server believes a landed player is at world origin until their
    /// first post-dismount sample.
    /// </summary>
    public bool TryPromoteManagedPoseToPlayer(uint transientId)
    {
        if (!_managed.TryGetValue(transientId, out ManagedEntityMovementState? entity)
            || entity.Movement is null)
        {
            return false;
        }

        Player = entity.Movement;
        return true;
    }

    /// <summary>
    /// Promotes and removes a dismounted client-managed entity, then guards that landing pose
    /// against the client's in-flight zero player stream.
    /// </summary>
    public bool BeginPostDismountPoseHandoff(uint transientId)
    {
        bool promoted = TryPromoteManagedPoseToPlayer(transientId);
        _managed.Remove(transientId);
        AwaitingPostDismountPose = true;
        return promoted;
    }

    public bool TryApplyManaged(
        ClientManagedMovementUpdate update,
        [NotNullWhen(true)] out ManagedEntityMovementState? entity)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!_managed.TryGetValue(update.TransientId, out ManagedEntityMovementState? current))
        {
            entity = null;
            return false;
        }

        entity = current with
        {
            Movement = EntityMovementState.Apply(current.Movement, update.Movement),
        };
        _managed[update.TransientId] = entity;
        return true;
    }

    public bool TryGetManaged(
        uint transientId,
        [NotNullWhen(true)] out ManagedEntityMovementState? entity) =>
        _managed.TryGetValue(transientId, out entity);

    private static bool IsFiniteNonZero(Vector3? position) =>
        position is Vector3 value
        && float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && value != Vector3.Zero;
}

/// <summary>
/// Fallback detector for an August parachute settling on world collision from the position samples
/// the owning client sends on gateway channel 3. Live touchdown normally sends
/// <c>Vehicle.Dismiss</c>; this path completes the transition if that signal is lost.
/// Sparse movement records which omit position must not be passed here: treating their retained
/// position as a fresh sample would look like a false mid-air stop.
/// </summary>
public sealed class ParachuteTouchdownDetector
{
    private const float MinimumDescentMetres = 20f;
    private const float MaximumSettledVerticalSpeed = 2f;
    private const uint MaximumContinuousSampleGapMs = 10_000;
    private const uint RequiredSettledTimeMs = 1_000;
    private const int RequiredSettledIntervals = 2;

    private float _spawnY;
    private float? _lastY;
    private uint _lastClientTime;
    private uint _settledTimeMs;
    private int _settledIntervals;
    private bool _triggered;

    public bool DescentObserved { get; private set; }

    public void Reset(float spawnY)
    {
        _spawnY = spawnY;
        _lastY = null;
        _lastClientTime = 0;
        _settledTimeMs = 0;
        _settledIntervals = 0;
        _triggered = false;
        DescentObserved = false;
    }

    /// <summary>Returns true once, after a real descent followed by two settled intervals.</summary>
    public bool Observe(uint clientTime, Vector3 position)
    {
        if (_triggered)
        {
            return false;
        }

        if (!float.IsFinite(position.Y))
        {
            ClearSettledWindow();
            return false;
        }

        DescentObserved |= position.Y <= _spawnY - MinimumDescentMetres;
        if (_lastY is not float previousY)
        {
            _lastY = position.Y;
            _lastClientTime = clientTime;
            return false;
        }

        uint elapsedMs = unchecked(clientTime - _lastClientTime);
        _lastY = position.Y;
        _lastClientTime = clientTime;
        if (elapsedMs == 0 || elapsedMs > MaximumContinuousSampleGapMs)
        {
            ClearSettledWindow();
            return false;
        }

        float verticalSpeed = MathF.Abs(position.Y - previousY) * 1_000f / elapsedMs;
        if (!DescentObserved || verticalSpeed > MaximumSettledVerticalSpeed)
        {
            ClearSettledWindow();
            return false;
        }

        _settledTimeMs = unchecked(_settledTimeMs + elapsedMs);
        _settledIntervals++;
        _triggered = _settledIntervals >= RequiredSettledIntervals
            && _settledTimeMs >= RequiredSettledTimeMs;
        return _triggered;
    }

    private void ClearSettledWindow()
    {
        _settledTimeMs = 0;
        _settledIntervals = 0;
    }
}
