using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone.World;

public enum LifeState : byte
{
    Alive,
    Downed,
    Dead,
    Spectating,
}

/// <summary>
/// One 20 Hz channel-2 record, stored inline. 64 bytes covers every sample observed in
/// <c>C:\Aug2017\captures</c>; a longer record is refused rather than truncated, because a truncated
/// record relayed to a peer would shift every field boundary after the cut
/// (<c>ClientMovementUpdate.Parse</c> rejects trailing bytes for the same reason).
/// A fixed inline buffer, never a <c>byte[]</c> per sample: at 20 Hz × 50 players a per-sample array
/// is 1,000 allocations a second.
/// </summary>
public struct MovementSample
{
    public const int MaxBytes = 64;

    /// <summary>Tick this record was applied on; 0 while the player has never moved.</summary>
    public long ArrivalTick;

    /// <summary>Bytes actually used in <see cref="Bytes"/>.</summary>
    public byte Length;

    [InlineArray(MaxBytes)]
    public struct Buffer
    {
        private byte _first;
    }

    public Buffer Bytes;

    public readonly bool IsEmpty => Length == 0;

    /// <summary>The record verbatim — what a relay copies after the transient id (docs/22 §6.1).</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> Span => Bytes[..Length];

    /// <summary>Copies a client record in. Returns false when it does not fit, leaving the previous
    /// sample untouched.</summary>
    public bool Set(ReadOnlySpan<byte> record, long tick)
    {
        if (record.Length > MaxBytes)
        {
            return false;
        }

        record.CopyTo(Bytes);
        Length = (byte)record.Length;
        ArrivalTick = tick;
        return true;
    }

    public void Clear()
    {
        Length = 0;
        ArrivalTick = 0;
    }
}

/// <summary>One historical pose, kept for the rewind a lag-compensated hit test needs.</summary>
public readonly record struct PoseSnapshot(long Tick, Vector3 Position, Quaternion Rotation);

/// <summary>
/// A one-second ring of poses (20 at 20 Hz). Written by <see cref="MovementSystem"/> from the day it
/// exists, because lag-compensated hit validation is a rewind and cannot be retrofitted cheaply once
/// the samples are gone (docs/22 §4.4).
/// </summary>
public struct PoseHistory
{
    public const int Capacity = 20;

    [InlineArray(Capacity)]
    public struct Buffer
    {
        private PoseSnapshot _first;
    }

    private Buffer _entries;
    private int _count;
    private int _next;

    public readonly int Count => _count;

    public void Push(in PoseSnapshot snapshot)
    {
        _entries[_next] = snapshot;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    /// <summary>Newest first: age 0 is the most recent pose pushed.</summary>
    public readonly PoseSnapshot this[int age]
    {
        get
        {
            if ((uint)age >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(age));
            }

            int index = ((_next - 1 - age) % Capacity + Capacity) % Capacity;
            return _entries[index];
        }
    }

    /// <summary>The newest pose at or before <paramref name="tick"/> — the rewind step of a hit test.</summary>
    public readonly bool TryRewind(long tick, out PoseSnapshot snapshot)
    {
        for (int age = 0; age < _count; age++)
        {
            PoseSnapshot candidate = this[age];
            if (candidate.Tick <= tick)
            {
                snapshot = candidate;
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    public void Clear()
    {
        _count = 0;
        _next = 0;
    }
}

/// <summary>
/// A body in a match. The sink may be null (disconnected but still in the match, or a synthetic
/// player in a test). A player keeps its slot for the life of the match: death does not free it,
/// only leaving does — which is what makes <see cref="Slot"/> a safe interest-grid key.
/// </summary>
public sealed class MatchPlayer
{
    public MatchPlayer(int slot, EntityId id, ulong accountGuid, string name)
    {
        Slot = slot;
        Id = id;
        AccountGuid = accountGuid;
        Name = name;
    }

    public int Slot { get; }

    /// <summary>The match actor's guid, kind <see cref="EntityKind.Character"/>.</summary>
    public EntityId Id { get; }

    /// <summary>The login host's roster character guid — top byte 0x00, never minted here.</summary>
    public ulong AccountGuid { get; }

    public string Name { get; }

    /// <summary>
    /// Where this player's packets go. Null while disconnected: <see cref="Match"/>'s timer dispatch
    /// treats that as the liveness guard <c>Later</c> performs today
    /// (<c>connection.State == ConnectionState.Open</c>, <c>ZoneService.cs:934</c>).
    /// In production this is the player's <see cref="SessionBridge"/>.
    /// </summary>
    public IPlayerSink? Sink;

    /// <summary>What this viewer has been told about, and by which transient ids.</summary>
    public ObserverView View { get; } = new();

    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public float Heading;

    /// <summary>0..<c>GasSettings.MaxHitpoints</c> (10,000) — the client's own integral scale.</summary>
    public int Health;
    public int Stamina;
    public LifeState Life = LifeState.Alive;

    /// <summary>Parachute or vehicle; <see cref="EntityId.None"/> on foot.</summary>
    public EntityId Mount;

    /// <summary>Filled by <see cref="MatchFlowSystem"/> on death.</summary>
    public int Placement;
    public uint Kills;

    /// <summary>The client's own last channel-2 record, verbatim, for relay (docs/22 §6.1).</summary>
    public MovementSample Pose;

    /// <summary>The record staged by the drain step, applied by <see cref="MovementSystem"/>.</summary>
    public MovementSample Staged;

    /// <summary>A record arrived this tick and has not been applied yet.</summary>
    public bool HasStaged;

    public PoseHistory History;

    // --- combat (docs/81) ------------------------------------------------------------------
    // The owner hangs the same state off a ConditionalWeakTable<ZoneSession, RetailState> keyed by
    // session. Cranberry's players are slot-indexed objects that already live for the match, so the
    // same fields sit here as plain structs: no hashing on the hot path, no allocation, and a
    // deterministic self-test.

    /// <summary>Fire hints, magazines and the refire clock for this shooter.</summary>
    public ShooterCombatState Combat { get; } = new();

    /// <summary>Plates and the helmet, with the owner's rearming rule.</summary>
    public Armour Armour;

    /// <summary>Bleeding and the running heal. Neither writes health; both queue.</summary>
    public MedicalState Medical;

    /// <summary>What is in the armour slot right now; 0 = bare.</summary>
    public uint WornBodyArmourItemId;

    /// <summary>What is on the head right now; 0 = bare.</summary>
    public uint WornHelmetItemId;

    /// <summary>The item definition of the weapon in hand; 0 = fists.</summary>
    public uint HeldWeaponItemDefinitionId;

    /// <summary>Who opened the wound that is bleeding. A bleed kill credits the shooter.</summary>
    public EntityId LastWoundedBy;

    public ushort GridCell;

    /// <summary>Tick of the last accepted pose; the relay's staleness key.</summary>
    public long LastPoseTick;

    /// <summary>Samples refused by the speed gate — a server-side sanity counter, not a client fact.</summary>
    public long MovementViolations;

    public bool IsAlive => Life == LifeState.Alive;

    /// <summary>A live outbound path exists for this player right now.</summary>
    public bool IsConnected => Sink is { IsOpen: true };

    public override string ToString() => $"{Name} [{Id}] slot {Slot}";
}
