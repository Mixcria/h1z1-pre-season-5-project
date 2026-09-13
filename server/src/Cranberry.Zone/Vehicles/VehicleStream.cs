using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Zone.Vehicles;

/// <summary>One car this tick decided to put on the wire.</summary>
/// <param name="Vehicle">The fleet entry to spawn.</param>
/// <param name="Metres">Its distance from the streaming centre, for the log line.</param>
public readonly record struct VehicleStreamSpawn(MatchVehicle Vehicle, float Metres);

/// <summary>
/// The result of one <see cref="MatchVehicleStream.PlanRestream"/>. Evictions come first on the
/// wire, for the same reason the loot arm gives (docs/52 §3a): destroying before spawning keeps the
/// peak client-side object count at <see cref="VehicleStreamOptions.MaxLive"/> rather than at
/// <c>MaxLive + MaxPerRestream</c>.
/// </summary>
public sealed class VehicleStreamPlan
{
    internal VehicleStreamPlan(
        IReadOnlyList<ulong> evictions,
        IReadOnlyList<VehicleStreamSpawn> spawns,
        int liveAfter,
        int inRadius,
        int protectedFromEviction,
        bool discExhausted)
    {
        Evictions = evictions;
        Spawns = spawns;
        LiveAfter = liveAfter;
        InRadius = inRadius;
        ProtectedFromEviction = protectedFromEviction;
        DiscExhausted = discExhausted;
    }

    /// <summary>World guids to destroy with <c>0f 01 RemovePlayer</c>.</summary>
    public IReadOnlyList<ulong> Evictions { get; }

    /// <summary>Cars to spawn, nearest first.</summary>
    public IReadOnlyList<VehicleStreamSpawn> Spawns { get; }

    /// <summary>Working-set size once the caller has actually sent this plan.</summary>
    public int LiveAfter { get; }

    /// <summary>How many fleet cars were inside the disc when this tick ran.</summary>
    public int InRadius { get; }

    /// <summary>
    /// Cars that were out of range but kept anyway because somebody is sitting in one (the
    /// occupancy guard in <see cref="MatchVehicleStream.PlanRestream"/>). Non-zero here is normal
    /// and expected exactly once: the car the viewer is driving.
    /// </summary>
    public int ProtectedFromEviction { get; }

    /// <summary>
    /// True when every car inside the disc is now streamed, i.e. this tick did not stop on
    /// <see cref="VehicleStreamOptions.MaxPerRestream"/> or on <see cref="VehicleStreamOptions.MaxLive"/>.
    /// The backfill arm of <see cref="MatchVehicleStream.ShouldRestream"/> reads it so a standing
    /// player costs one comparison per tick rather than a spatial query.
    /// </summary>
    public bool DiscExhausted { get; }

    /// <summary>Did this tick change the client world at all.</summary>
    public bool ChangedTheWorld => Evictions.Count > 0 || Spawns.Count > 0;

    /// <summary>What this tick costs on the wire.</summary>
    public int EstimatedBytes => VehicleStreamOptions.EstimateTickBytes(Spawns.Count, Evictions.Count);

    /// <summary>A plan that does nothing.</summary>
    public static VehicleStreamPlan Empty { get; } =
        new([], [], liveAfter: 0, inRadius: 0, protectedFromEviction: 0, discExhausted: true);
}

/// <summary>
/// The per-session working set of parked cars: which of the match fleet this client currently holds,
/// and what one re-stream tick should add and destroy (docs/61 §2).
///
/// <para><b>Third arm, same pump.</b> The decision half is <see cref="WorldStream.NextStep"/> — the
/// shared function wave 5 lifted out of the door arm for the loot arm — not a copy of it and not a
/// third timer chain. This class is the state and the spatial half only; it reads no clock and names
/// no transport type.</para>
///
/// <para><b>Why the arm exists.</b> Wave 5 spawned the car park exactly once, in the landing burst:
/// <c>ZoneService.SpawnNearbyVehicles</c> is reached only through the one-way
/// <c>state.VehiclesArmed</c> latch, so the live proof (<c>logs/host-20260830-090725.log</c>
/// 09:10:03) reads <i>"vehicles: planned 7 of 300 within 250 m"</i> and the other 293 cars were
/// never mentioned again. Wave 4 made exactly this mistake with ground loot and docs/52 fixed it;
/// this is the same fix for the same latch on the vehicle arm.</para>
///
/// <para><b>The one rule that is not the loot arm.</b> A ground item is inert, so the loot arm may
/// evict anything that leaves its disc. A car can have a person in it — and the person may be the
/// viewer, driving it. Destroying the client vehicle actor out from under a driver would unparent
/// their character, orphan the managed object the client is simulating (<c>0f 3b</c> grant,
/// <c>FUN_140ab20c0</c>) and strand the <c>0x90</c> stream against a transient id whose actor no
/// longer exists. So <b>an occupied car is never evicted</b>, at any distance, and
/// <see cref="VehicleStreamPlan.ProtectedFromEviction"/> counts it.</para>
/// </summary>
public sealed class MatchVehicleStream
{
    private readonly Dictionary<ulong, Vector3> _streamed = [];
    private readonly HashSet<ulong> _spawned = [];
    private Vector3 _lastBurstCentre;
    private bool _hasStreamed;
    private bool _discExhausted;
    private VehicleStreamOptions? _lastOptions;

    /// <summary>Cars this client is currently holding.</summary>
    public int LiveCount => _streamed.Count;

    /// <summary>Total spawns ever paid out, for the log line.</summary>
    public long StreamedCount { get; private set; }

    /// <summary>Total evictions ever paid out.</summary>
    public long EvictedCount { get; private set; }

    /// <summary>Re-stream ticks that actually planned something.</summary>
    public int BurstCount { get; private set; }

    /// <summary>Where the last burst was centred.</summary>
    public Vector3 LastBurstCentre => _lastBurstCentre;

    /// <summary>Has any burst run yet.</summary>
    public bool HasStreamed => _hasStreamed;

    /// <summary>Did the last plan run out of candidates rather than out of budget.</summary>
    public bool DiscExhausted => _discExhausted;

    /// <summary>Is this car reserved by the current streaming plan, including unsent spawns.</summary>
    public bool IsStreamed(ulong vehicleGuid) => _streamed.ContainsKey(vehicleGuid);

    /// <summary>Was the actor sent and not yet removed? Planning alone does not change this.</summary>
    public bool IsSpawned(ulong vehicleGuid) => _spawned.Contains(vehicleGuid);

    public void ConfirmSpawn(ulong vehicleGuid) => _spawned.Add(vehicleGuid);
    public void ConfirmEviction(ulong vehicleGuid) => _spawned.Remove(vehicleGuid);

    /// <summary>
    /// Adopts a car the caller spawned outside <see cref="PlanRestream"/> — which is exactly what
    /// the landing burst in <c>ZoneService.SpawnNearbyVehicles</c> is.
    ///
    /// <para><b>Not optional.</b> Without this call the first re-stream tick re-offers all seven
    /// cars the landing burst just sent, and the client keeps both copies forever: an
    /// <c>AddLightweightVehicle</c> for a guid the client already holds creates a second actor at the
    /// same spot, and nothing ever destroys it because this set can only remember one entry per
    /// guid. It is the vehicle twin of the duplicate the loot arm <c>LandingLootDrained</c> guard
    /// exists to prevent (docs/52 §I3).</para>
    /// </summary>
    /// <returns>False when the guid was already held, so the caller can skip the packet.</returns>
    public bool NoteSpawned(ulong vehicleGuid, in Vector3 position)
    {
        if (!_streamed.TryAdd(vehicleGuid, position))
        {
            return false;
        }

        _spawned.Add(vehicleGuid);
        StreamedCount++;
        return true;
    }

    /// <summary>Forgets a car the caller destroyed by some other route (a wreck, a match reset).</summary>
    public bool NoteEvicted(ulong vehicleGuid)
    {
        bool wasSpawned = _spawned.Remove(vehicleGuid);
        if (!_streamed.Remove(vehicleGuid))
        {
            return wasSpawned;
        }

        EvictedCount++;
        return true;
    }

    /// <summary>Records that a burst has been centred at <paramref name="centre"/>.</summary>
    public void NoteStreamed(in Vector3 centre)
    {
        _lastBurstCentre = centre;
        _hasStreamed = true;
    }

    /// <summary>A new fleet entry invalidates a stationary viewer's exhausted-disc result.</summary>
    public void InvalidateCandidates() => _discExhausted = false;

    /// <summary>
    /// Whether another vehicle burst is due at <paramref name="centre"/> — the same two arms the
    /// loot streamer uses, for the same two reasons.
    /// <list type="number">
    /// <item><b>Movement.</b> True before the first burst, and thereafter once the player has moved
    /// more than <see cref="VehicleStreamOptions.RestreamFraction"/> times the radius, horizontally
    /// only. Height is ignored because Z2 Y range would otherwise re-stream on every step down a
    /// hillside — and, for this arm specifically, on every hill a car drives over.</item>
    /// <item><b>Backfill.</b> True while the working set is under
    /// <see cref="VehicleStreamOptions.MaxLive"/> and the last plan did <i>not</i> exhaust its disc.
    /// This is what repairs the landing burst, which is capped at
    /// <c>ZoneOptions.VehicleMaxPerBurst</c> = 12 inside the same 250 m disc this arm streams.</item>
    /// </list>
    /// </summary>
    public bool ShouldRestream(in Vector3 centre, VehicleStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!_hasStreamed)
        {
            return true;
        }

        float radius = options.StreamRadiusMetres;
        float fraction = options.RestreamFraction;
        if (!float.IsFinite(radius) || radius <= 0f
            || !float.IsFinite(fraction) || fraction <= 0f)
        {
            // The explicit "stream once per match" setting — wave 5 behaviour, kept reachable so a
            // regression can be A/B tested against it.
            return false;
        }

        // A cached exhausted disc describes the options used to query it. A radius
        // or budget change (including a phase/config transition) must query again
        // even when the player has not moved horizontally.
        if (options != _lastOptions) return true;

        float threshold = radius * fraction;
        float dx = centre.X - _lastBurstCentre.X;
        float dz = centre.Z - _lastBurstCentre.Z;
        if ((dx * dx) + (dz * dz) > threshold * threshold)
        {
            return true;
        }

        return !_discExhausted && _streamed.Count < options.MaxLive;
    }

    /// <summary>
    /// The decision one tick of the shared world pump makes for the vehicle arm, mirroring
    /// <c>MatchLoot.NextPumpStep</c> and <c>MatchDoors.NextPumpStep</c> so all three read alike.
    ///
    /// <para><paramref name="stream"/> being null and <paramref name="fleetPlanned"/> being false are
    /// both <see cref="WorldStreamStep.Wait"/> and never <see cref="WorldStreamStep.Stop"/>. The
    /// fleet is built lazily inside the landing burst, which rides
    /// <c>ZoneOptions.GroundLootDelayMs</c>, while this pump is armed at
    /// <see cref="VehicleStreamOptions.RestreamIntervalMs"/>. At the shipped defaults the burst wins
    /// by a second, but the interval is host-overridable and the delay is not — and stopping there
    /// would silently disable the streamer for the whole match, which is exactly the wave-4 defect
    /// the verify pass found in the door pump.</para>
    /// </summary>
    public static WorldStreamStep NextPumpStep(
        bool inMatch,
        bool sendVehicles,
        MatchVehicleStream? stream,
        Vector3? centre,
        VehicleStreamOptions options,
        bool fleetPlanned)
    {
        ArgumentNullException.ThrowIfNull(options);

        return WorldStream.NextStep(
            inMatch,
            sendVehicles && options.Enabled,
            options.RestreamIntervalMs,
            stream is not null && fleetPlanned,
            centre,
            options.StreamRadiusMetres,
            (point, _) => stream!.ShouldRestream(point, options));
    }

    /// <summary>
    /// Plans one re-stream tick: what to destroy, then what to spawn.
    ///
    /// <para><b>This method commits.</b> Evicted guids leave the working set and spawned guids enter
    /// it <i>here</i>, not when the caller sends the packets, because the caller pays the plan out
    /// over several <c>DrainBurst</c> slices and a second plan must not be able to re-offer what the
    /// first already owns. The caller must therefore actually run the plan; <c>DrainBurst</c> does,
    /// even with no listener-thread dispatcher to pace with.</para>
    /// </summary>
    /// <param name="centre">Where the streaming player is now.</param>
    /// <param name="fleet">This match car park.</param>
    /// <param name="options">Radii and caps.</param>
    /// <param name="viewerGuid">
    /// The streaming player character guid. Their own ride is protected from eviction even when the
    /// pose that made it far away is the pose they themselves authored — the centre is the
    /// <i>character</i> last position, which lags a driven car by up to a tick.
    /// </param>
    public VehicleStreamPlan PlanRestream(
        in Vector3 centre,
        VehicleFleet fleet,
        VehicleStreamOptions options,
        ulong viewerGuid = 0)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(options);

        float despawn = options.EffectiveDespawnRadiusMetres;
        float despawnSquared = despawn * despawn;
        float radiusSquared = options.StreamRadiusMetres * options.StreamRadiusMetres;

        // ---- 1. Evict first, so the working set is under the cap before anything is added -------
        List<ulong>? evictions = null;
        int protectedCount = 0;
        foreach ((ulong guid, Vector3 held) in _streamed)
        {
            Vector3 at = held;
            if (fleet.TryGet(guid, out MatchVehicle? live))
            {
                // A car that has moved since it was streamed is a car somebody drove: judge it on
                // where it IS, not on the parking anchor it was spawned at.
                at = live.Position;

                // THE rule that is not the loot arm: never destroy an occupied vehicle actor.
                if (live.OccupantCount > 0 || (viewerGuid != 0 && live.OwnerGuid == viewerGuid))
                {
                    protectedCount++;
                    continue;
                }
            }
            else
            {
                // Gone from the fleet entirely (a wreck reaped, or a match reset that rebuilt it).
                (evictions ??= []).Add(guid);
                continue;
            }

            float dx = at.X - centre.X;
            float dz = at.Z - centre.Z;
            if ((dx * dx) + (dz * dz) > despawnSquared)
            {
                (evictions ??= []).Add(guid);
            }
        }

        if (evictions is not null)
        {
            foreach (ulong guid in evictions)
            {
                _streamed.Remove(guid);
                EvictedCount++;
            }
        }

        // ---- 2. Spawn what is in the disc and not already held, nearest first -------------------
        int room = Math.Max(0, options.MaxLive - _streamed.Count);
        int budget = Math.Min(room, Math.Max(0, options.MaxPerRestream));

        List<VehicleStreamSpawn>? candidates = null;
        int inRadius = 0;
        foreach (MatchVehicle candidate in fleet.Vehicles)
        {
            float dx = candidate.Position.X - centre.X;
            float dz = candidate.Position.Z - centre.Z;
            float distanceSquared = (dx * dx) + (dz * dz);
            if (distanceSquared > radiusSquared)
            {
                continue;
            }

            inRadius++;
            if (_streamed.ContainsKey(candidate.Guid))
            {
                continue;
            }

            (candidates ??= []).Add(new VehicleStreamSpawn(candidate, MathF.Sqrt(distanceSquared)));
        }

        List<VehicleStreamSpawn> spawns = [];
        if (candidates is not null && budget > 0)
        {
            candidates.Sort(static (left, right) => left.Metres.CompareTo(right.Metres));
            foreach (VehicleStreamSpawn spawn in candidates)
            {
                if (spawns.Count >= budget)
                {
                    break;
                }

                if (_streamed.TryAdd(spawn.Vehicle.Guid, spawn.Vehicle.Position))
                {
                    StreamedCount++;
                    spawns.Add(spawn);
                }
            }
        }

        // "Exhausted" means nothing was left behind, so the backfill arm may stand down. A tick that
        // stopped on the budget has candidates left and must NOT set it.
        _discExhausted = candidates is null || spawns.Count == candidates.Count;

        _lastBurstCentre = centre;
        _hasStreamed = true;
        var plan = new VehicleStreamPlan(
            (IReadOnlyList<ulong>?)evictions ?? [],
            spawns,
            _streamed.Count,
            inRadius,
            protectedCount,
            _discExhausted);
        _lastOptions = options;

        if (plan.ChangedTheWorld)
        {
            BurstCount++;
        }

        return plan;
    }

    /// <summary>Drops the whole working set — a match reset, or the session leaving the match.</summary>
    public void Clear()
    {
        _streamed.Clear();
        _spawned.Clear();
        _hasStreamed = false;
        _discExhausted = false;
        _lastBurstCentre = default;
        _lastOptions = null;
    }
}
