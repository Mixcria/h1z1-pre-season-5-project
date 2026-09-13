namespace Cranberry.Zone.World;

/// <summary>
/// One per match. Hands out world guids per kind from a single 40-bit sequence, so a guid is unique
/// within the match by construction and comparable across kinds for logging. Two matches never
/// collide because the match id is baked into every guid (docs/22 §4.1, §7.1).
/// <para>
/// This is what replaces the two identity bugs the tree has today: <c>LootWorld</c> is a field of
/// the per-connection <c>GatewaySessionState</c> and is rebuilt with the same fixed bases for every
/// session, and the parachute guid is <c>rosterKey + 0x1000</c> with the literal transient id 2
/// (docs/22 §7.1).
/// </para>
/// </summary>
public sealed class EntityAllocator
{
    private ulong _sequence;

    public EntityAllocator(ushort matchId) => MatchId = matchId;

    public ushort MatchId { get; }

    /// <summary>How many guids this match has minted, across every kind.</summary>
    public ulong Issued => _sequence;

    public EntityId Next(EntityKind kind)
    {
        if (_sequence >= EntityId.MaxSequence)
        {
            throw new InvalidOperationException(
                $"Match {MatchId} exhausted its 40-bit entity sequence.");
        }

        return EntityId.Create(kind, MatchId, ++_sequence);
    }
}
