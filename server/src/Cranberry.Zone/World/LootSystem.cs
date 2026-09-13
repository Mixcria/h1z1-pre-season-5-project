namespace Cranberry.Zone.World;

/// <summary>One resolved pickup: what was taken, by whom, and the instance guid minted for it.</summary>
public readonly record struct LootGrant(
    EntityId Item,
    EntityId Instance,
    int ClaimantSlot,
    uint ItemDefinitionId,
    uint Count);

/// <summary>
/// Ground-loot claims.
/// <para>
/// One <c>[F]</c> press emits <b>both</b> <c>Command.PlayerSelect</c> and
/// <c>Command.InteractRequest</c> for the same object (docs/13 §8), so a claim must be idempotent —
/// which a destructive single-shot claim gives for free, exactly as <c>LootWorld.TryClaim</c> does
/// today. Two players racing for one item take the same path: the first staged claim wins.
/// </para>
/// <para>
/// <b>Scaffold stub (migration step 8).</b> Claims resolve against the world and mint the instance
/// guid, but nothing is sent: <c>0f 45 Character.FullCharacterDataRequest</c> →
/// <c>0xda</c>/<c>0xd9</c> is underived and there is no inventory/container model yet
/// (<c>ZoneOptions.GroundLootContainerGuid</c> defaults to 0), so a grant packet would land nowhere
/// visible (docs/22 §6.4, §13).
/// </para>
/// </summary>
public sealed class LootSystem : ISystem
{
    private readonly List<(int Slot, EntityId Target)> _claims = [];
    private readonly List<LootGrant> _grants = [];

    /// <summary>Grants resolved on the most recent tick.</summary>
    public IReadOnlyList<LootGrant> Grants => _grants;

    public long TotalGrants { get; private set; }

    /// <summary>Claims that found nothing: the duplicate of one press, or a lost race.</summary>
    public long DroppedClaims { get; private set; }

    public int StagedClaims => _claims.Count;

    public void StageClaim(MatchPlayer player, EntityId target)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (target.IsNone)
        {
            return;
        }

        _claims.Add((player.Slot, target));
    }

    public void Tick(in TickContext context)
    {
        _grants.Clear();
        if (_claims.Count == 0)
        {
            return;
        }

        World world = context.World;
        foreach ((int slot, EntityId target) in _claims)
        {
            if (world.PlayerAt(slot) is not { Life: LifeState.Alive })
            {
                DroppedClaims++;
                continue;
            }

            if (!world.TryGetEntity(target, out WorldEntity? item)
                || item.Kind != EntityKind.GroundItem
                || item.Claimed
                || item.Removed)
            {
                DroppedClaims++;
                continue;
            }

            item.Claimed = true;
            world.Despawn(item);

            _grants.Add(new LootGrant(
                Item: item.Id,
                Instance: world.Ids.Next(EntityKind.ItemInstance),
                ClaimantSlot: slot,
                ItemDefinitionId: item.ItemDefinitionId,
                Count: item.Count));
            TotalGrants++;
        }

        _claims.Clear();
    }
}
