namespace Cranberry.Zone.World;

/// <summary>
/// What a world guid names. The kind is the top byte so a guid's domain is readable in a log line
/// and in hex without a lookup. <see cref="None"/> (0x00) is reserved for the login host's roster
/// keys (<c>CharacterRosterStore._nextEntityKey</c>, ascending from 0x1001 in steps of 0x10), which a match never
/// mints. docs/22 §4.1.
/// </summary>
public enum EntityKind : byte
{
    /// <summary>Not a match-allocated guid: roster character keys and the legacy chute offset.</summary>
    None = 0x00,

    /// <summary>A player's match actor; NOT the roster character guid.</summary>
    Character = 0x01,

    /// <summary>Includes the parachute (docs/12).</summary>
    Vehicle = 0x02,

    /// <summary>A lootable world object, named back by <c>Command.InteractRequest</c> (docs/13 §8).</summary>
    GroundItem = 0x03,

    /// <summary>A carried item's instance guid inside a container (<c>ItemAdd</c>).</summary>
    ItemInstance = 0x04,

    Container = 0x05,
    Projectile = 0x06,
}

/// <summary>
/// A 64-bit world guid: <c>kind:8 | matchId:16 | sequence:40</c>. The sequence never repeats inside a
/// match and a match id is not reused while any of its entities live, so no two live entities can
/// share a guid. Replaces <c>state.Guid + ZoneOptions.ParachuteGuidOffset</c> (docs/12) and
/// <see cref="LootWorld"/>'s fixed bases (docs/22 §7). This match layout is Cranberry's own choice.
/// Roster character IDs have a separate native constraint: commerce checks their low nibble is 1.
/// </summary>
public readonly record struct EntityId(ulong Value) : IComparable<EntityId>
{
    public const int SequenceBits = 40;
    public const int MatchIdBits = 16;

    /// <summary>Largest sequence a single match can issue: 2^40 − 1.</summary>
    public const ulong MaxSequence = (1UL << SequenceBits) - 1;

    private const int KindShift = SequenceBits + MatchIdBits;

    public static EntityId None => default;

    public EntityKind Kind => (EntityKind)(byte)(Value >> KindShift);

    public ushort MatchId => (ushort)(Value >> SequenceBits);

    public ulong Sequence => Value & MaxSequence;

    public bool IsNone => Value == 0;

    public static EntityId Create(EntityKind kind, ushort matchId, ulong sequence)
    {
        if (kind == EntityKind.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Kind 0x00 is the login host's roster space and is never minted by a match.");
        }

        if (sequence > MaxSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence does not fit in 40 bits.");
        }

        return new EntityId(((ulong)(byte)kind << KindShift) | ((ulong)matchId << SequenceBits) | sequence);
    }

    /// <summary>
    /// Whether a raw guid lies in the match-allocated space (top byte 0x01–0x06). Roster keys
    /// (top byte 0x00) and the legacy <see cref="LootWorld"/> bases (0x20, 0x31) are outside it,
    /// which is what lets the old and new schemes coexist during migration step 3 (docs/22 §7.1).
    /// </summary>
    public static bool IsMatchAllocated(ulong guid)
    {
        byte kind = (byte)(guid >> KindShift);
        return kind is >= (byte)EntityKind.Character and <= (byte)EntityKind.Projectile;
    }

    public int CompareTo(EntityId other) => Value.CompareTo(other.Value);

    /// <summary>Log-readable: <c>Vehicle#3:0000000012</c>.</summary>
    public override string ToString() =>
        IsNone ? "None" : $"{Kind}#{MatchId}:{Sequence:D10}";
}
