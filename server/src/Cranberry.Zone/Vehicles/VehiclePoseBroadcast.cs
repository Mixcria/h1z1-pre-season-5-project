using System.Numerics;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// One client that might need to be told where somebody else car is. Deliberately transport-free:
/// the simulation side names no <c>SoeConnection</c> and no <c>PacketWriter</c>, exactly as
/// <c>IPlayerSink</c> does for the world systems (docs/22 §4.9, §9.4).
/// </summary>
public interface IVehicleObserver
{
    /// <summary>This viewer own character guid. The reporter is never relayed to itself.</summary>
    ulong CharacterGuid { get; }

    /// <summary>Match instance owning these vehicle identities; zero for legacy embedders.</summary>
    ulong MatchId => 0;

    /// <summary>False once the link is closed; a closed observer is skipped and swept.</summary>
    bool IsOpen { get; }

    /// <summary>Where this viewer character is, or null before its first movement packet.</summary>
    Vector3? Position { get; }

    /// <summary>
    /// <b>The gate that matters.</b> Whether this viewer has actually been sent this vehicle.
    ///
    /// <para>A <c>0x78</c> naming a transient id whose actor the receiving client has never been
    /// given is, at best, discarded — <c>FUN_140a600b0</c> resolves the transient through the zone
    /// client managed table before it applies anything — and at worst it is the kind of unknown-id
    /// traffic that has produced <c>ClientBadData.log</c> entries before. Backed by that session own
    /// <see cref="MatchVehicleStream"/>, this is exact rather than approximate: the streamer knows
    /// precisely which cars it has spawned on this client and which it has destroyed.</para>
    /// </summary>
    bool Holds(ulong vehicleGuid);

    /// <summary>Writes one relay. Called on the caller thread; implementations must not block.</summary>
    void Relay(VehiclePoseRelay pose);
}

/// <summary>Tuning for the bystander relay. All <b>[DESIGN]</b>.</summary>
public sealed class VehicleRelayOptions
{
    public static VehicleRelayOptions Default { get; } = new();

    /// <summary>
    /// Is the relay on. <b>Default on</b> — with one player in the match it is a provable no-op (the
    /// reporter is the only observer and is always excluded), so there is nothing to be cautious
    /// about, and it is the D25 line the lane exists for.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Metres from the car past which a viewer is not told about it. A second, cheap gate behind
    /// <see cref="IVehicleObserver.Holds"/>: a viewer that has driven out of range but whose streamer
    /// has not run its eviction tick yet still holds the car for up to
    /// <see cref="VehicleStreamOptions.RestreamIntervalMs"/>, and there is no point spending 5 Hz of
    /// pose on something nobody can see. 350 m is <see cref="VehicleStreamOptions.DespawnRadiusMetres"/>,
    /// so the two gates never disagree in the direction that drops a pose for a car still on screen.
    /// </summary>
    public float RelayRadiusMetres { get; init; } = 350f;

    /// <summary>
    /// The floor on the interval between two relays of the same car to the same viewer.
    ///
    /// <para>Disabled by default: records are sparse deltas, so dropping an intervening
    /// rotation, velocity or position update loses state. Explicit throttling remains available
    /// for experiments; it must not be enabled for raw deltas in ordinary matches.</para>
    /// </summary>
    public int MinIntervalMs { get; init; } = 0;

    /// <summary>
    /// The most viewers one pose may be relayed to, before the round-robin cursor defers the rest to
    /// the next pose.
    ///
    /// <para>The default covers the full public match. Each eligible viewer receives every
    /// sparse delta; a lower experimental cap can leave observers with incomplete state.</para>
    ///
    /// <para><b>Why round-robin and not nearest-first.</b> The same reason
    /// <c>RelaySystem</c> gives for peer movement (docs/22 §6.1): a crowded area then degrades to a
    /// slower update rate for everyone, instead of freezing the far half of the view solid.</para>
    /// </summary>
    public int MaxObserversPerPose { get; init; } = 150;
}

/// <summary>What one <see cref="VehiclePoseBroadcast.Relay"/> did.</summary>
/// <param name="Sent">Viewers actually written to.</param>
/// <param name="Skipped">Viewers considered and passed over (out of range, does not hold the car, rate limited).</param>
/// <param name="Deferred">Viewers left for the next pose because the per-pose cap was reached.</param>
/// <param name="Bytes">Bytes written.</param>
public readonly record struct VehicleRelayResult(int Sent, int Skipped, int Deferred, int Bytes);

/// <summary>
/// The <c>0x78</c> bystander relay call site (docs/61 §1) — <i>"the highest-value line of code left
/// in this feature for D25"</i> (docs/59 §2.3), which until this wave had none:
/// <c>VehiclePoseRelay</c> was written in wave 4 and <c>grep VehiclePoseRelay ZoneService.cs</c>
/// returned nothing.
///
/// <para><b>What it does is a copy, not a re-encode.</b> <c>0x78 PlayerUpdatePosition</c> and
/// <c>0x90 PlayerUpdateManagedPosition</c> share both readers — the varint <c>FUN_140a190f0</c> and
/// the movement record <c>FUN_140a3ca40</c> — and the client has a <c>case 0x78</c> and <b>no
/// <c>case 0x90</c></b>, so 0x90 is send-only and 0x78 is its receive side. The relay is therefore
/// the driver own bytes with byte 0 changed, and Cranberry <c>TransientIdTable</c> is zone-wide so
/// the varint does not even need re-encoding.</para>
///
/// <para><b>Still unverified, and the test is two clients.</b> Whether a bystander client accepts a
/// <c>0x78</c> for an entity it holds as a <i>vehicle</i> actor rather than a character actor.
/// <c>case 0x78</c> dispatches through the zone client <c>vtable+0x250</c> with no type test visible
/// in the arm, so it should be uniform — but nothing here is LIVE-VERIFIED until two clients are in
/// one match and one of them drives (docs/43 blocker 4).</para>
///
/// <para>It reads no clock; the caller passes the instant in.</para>
/// </summary>
public sealed class VehiclePoseBroadcast
{
    private readonly List<IVehicleObserver> _observers = [];
    private readonly Dictionary<ulong, int> _cursors = [];
    private readonly Dictionary<(ulong Observer, ulong Vehicle), long> _lastSentMs = [];

    /// <summary>Registered observers, closed ones included until the next sweep.</summary>
    public int ObserverCount => _observers.Count;

    /// <summary>Relays written since this broadcaster was created.</summary>
    public long SentTotal { get; private set; }

    /// <summary>Relay bodies dropped because the per-pose cap was reached.</summary>
    public long DeferredTotal { get; private set; }

    /// <summary>Bytes written.</summary>
    public long BytesTotal { get; private set; }

    /// <summary>
    /// Adds or replaces a viewer. Replacing by character guid rather than appending is what makes a
    /// reconnect on the same character not double every relay it receives.
    /// </summary>
    public void Register(IVehicleObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        for (int index = 0; index < _observers.Count; index++)
        {
            if (_observers[index].CharacterGuid == observer.CharacterGuid)
            {
                _observers[index] = observer;
                return;
            }
        }

        _observers.Add(observer);
    }

    /// <summary>Removes a viewer and forgets its rate-limit stamps.</summary>
    public bool Unregister(ulong characterGuid)
    {
        bool removed = false;
        for (int index = _observers.Count - 1; index >= 0; index--)
        {
            if (_observers[index].CharacterGuid == characterGuid)
            {
                _observers.RemoveAt(index);
                removed = true;
            }
        }

        if (removed)
        {
            Forget(characterGuid);
        }

        return removed;
    }

    /// <summary>
    /// Relays one owner-authored pose to everyone else who can see the car.
    ///
    /// <para>Callers pass the <see cref="VehiclePoseRelay"/> the movement handler already built, so
    /// the driver bytes are carried through by reference and are written once per observer with no
    /// intermediate copy.</para>
    /// </summary>
    /// <param name="vehicle">The car the pose is for — its position is the range test centre.</param>
    /// <param name="pose">The relay form of the owner <c>0x90</c>.</param>
    /// <param name="reporterGuid">The owner, who is never relayed to.</param>
    /// <param name="nowMs">The caller monotonic instant.</param>
    /// <param name="options">Range, rate and fan-out policy.</param>
    /// <param name="matchId">Only viewers in this match receive the movement.</param>
    /// <param name="flushMotionStop">A transition to rest reaches all eligible viewers immediately,
    /// because the client may sleep without sending another movement record.</param>
    public VehicleRelayResult Relay(
        MatchVehicle vehicle,
        VehiclePoseRelay pose,
        ulong reporterGuid,
        long nowMs,
        VehicleRelayOptions options,
        ulong matchId = 0,
        bool flushMotionStop = false)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled || _observers.Count == 0)
        {
            return default;
        }

        SweepClosed();
        int count = _observers.Count;
        if (count == 0)
        {
            return default;
        }

        float radiusSquared = options.RelayRadiusMetres * options.RelayRadiusMetres;
        int cap = Math.Max(0, options.MaxObserversPerPose);
        int bytes = pose.Length;
        int sent = 0;
        int skipped = 0;
        int deferred = 0;

        _cursors.TryGetValue(vehicle.Guid, out int cursor);
        int next = cursor;
        for (int step = 0; step < count; step++)
        {
            int index = (cursor + step) % count;
            IVehicleObserver observer = _observers[index];
            next = index + 1;

            if (observer.CharacterGuid == reporterGuid || !observer.IsOpen || observer.MatchId != matchId)
            {
                skipped++;
                continue;
            }

            // The exact gate: has this viewer actually been sent this car. Everything else is an
            // optimisation; this one is correctness.
            if (!observer.Holds(vehicle.Guid))
            {
                skipped++;
                continue;
            }

            if (observer.Position is Vector3 seen)
            {
                float dx = seen.X - vehicle.Position.X;
                float dz = seen.Z - vehicle.Position.Z;
                if ((dx * dx) + (dz * dz) > radiusSquared)
                {
                    skipped++;
                    continue;
                }
            }

            (ulong, ulong) key = (observer.CharacterGuid, vehicle.Guid);
            // A transition to rest can be the final report before PhysX sleeps. There is
            // no later pose to repair a dropped zero-speed delta, so deliver it to every
            // eligible viewer even when it follows a movement report within this window.
            if (!flushMotionStop && _lastSentMs.TryGetValue(key, out long last)
                && nowMs >= last
                && nowMs - last < Math.Max(0, options.MinIntervalMs))
            {
                skipped++;
                continue;
            }

            if (sent >= cap && (!flushMotionStop || cap == 0))
            {
                // Out of budget for this pose. Leave the cursor here so the very next pose starts
                // with the viewers this one could not reach — deferred, never dropped for good.
                next = index;
                deferred = count - step;
                break;
            }

            _lastSentMs[key] = nowMs;
            observer.Relay(pose);
            sent++;
        }

        _cursors[vehicle.Guid] = next % Math.Max(1, count);
        SentTotal += sent;
        DeferredTotal += deferred;
        BytesTotal += (long)sent * bytes;
        return new VehicleRelayResult(sent, skipped, deferred, sent * bytes);
    }

    /// <summary>Forgets one car — it was destroyed, or the match ended.</summary>
    public void ForgetVehicle(ulong vehicleGuid)
    {
        _cursors.Remove(vehicleGuid);
        foreach ((ulong Observer, ulong Vehicle) key in _lastSentMs.Keys.Where(k => k.Vehicle == vehicleGuid).ToArray())
        {
            _lastSentMs.Remove(key);
        }
    }

    /// <summary>Drops everything — a match reset.</summary>
    public void Clear()
    {
        _observers.Clear();
        _cursors.Clear();
        _lastSentMs.Clear();
    }

    private void Forget(ulong characterGuid)
    {
        foreach ((ulong Observer, ulong Vehicle) key in _lastSentMs.Keys.Where(k => k.Observer == characterGuid).ToArray())
        {
            _lastSentMs.Remove(key);
        }
    }

    /// <summary>
    /// Drops observers whose link has closed. Done here rather than on disconnect because the relay
    /// is the only thing that walks the list at 5 Hz, and a stale entry that is merely skipped still
    /// costs a slot of <see cref="VehicleRelayOptions.MaxObserversPerPose"/> on every pose.
    /// </summary>
    private void SweepClosed()
    {
        for (int index = _observers.Count - 1; index >= 0; index--)
        {
            if (!_observers[index].IsOpen)
            {
                ulong guid = _observers[index].CharacterGuid;
                _observers.RemoveAt(index);
                Forget(guid);
            }
        }
    }
}
