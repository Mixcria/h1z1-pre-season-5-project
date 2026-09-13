using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/13 §8 and docs/22 §6.4: one [F] press emits both PlayerSelect and InteractRequest for the
// same object, so a claim must be idempotent - and two players racing for one pile take the same
// path. Nothing is granted on the wire yet: the container model and 0xda/0xd9 are still open.
public sealed class LootSystemTests
{
    private static readonly Vector3 Ground = new(0f, 506f, 0f);

    private static WorldEntity SpawnItem(MatchHarness harness) =>
        harness.Match.World.SpawnGroundItem(
            itemDefinitionId: 2423,
            groundModelId: 9066,
            nameId: 12302,
            count: 1,
            position: Ground);

    [Fact]
    public void OnePressWithItsTwoCommandsGrantsExactlyOnce()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        WorldEntity item = SpawnItem(harness);

        harness.PostCommand(player, CommandKind.PlayerSelect, item.Id);
        harness.PostCommand(player, CommandKind.Interact, item.Id);
        harness.Step();

        Assert.Equal(1, harness.Match.Loot.TotalGrants);
        Assert.Equal(1, harness.Match.Loot.DroppedClaims);
        Assert.True(item.Claimed);
        Assert.False(harness.Match.World.TryGetEntity(item.Id, out _));
    }

    [Fact]
    public void TwoPlayersRacingForOnePileProduceOneGrant()
    {
        var harness = new MatchHarness();
        MatchPlayer first = harness.AddPlayer("First");
        MatchPlayer second = harness.AddPlayer("Second");
        WorldEntity item = SpawnItem(harness);

        harness.PostCommand(first, CommandKind.Interact, item.Id);
        harness.PostCommand(second, CommandKind.Interact, item.Id);
        harness.Step();

        LootGrant grant = Assert.Single(harness.Match.Loot.Grants);
        Assert.Equal(first.Slot, grant.ClaimantSlot);
        Assert.Equal(1, harness.Match.Loot.TotalGrants);
    }

    [Fact]
    public void AGrantMintsAnItemInstanceGuidInTheMatchSpace()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        WorldEntity item = SpawnItem(harness);

        harness.PostCommand(player, CommandKind.Interact, item.Id);
        harness.Step();

        LootGrant grant = Assert.Single(harness.Match.Loot.Grants);
        Assert.Equal(EntityKind.ItemInstance, grant.Instance.Kind);
        Assert.Equal(EntityKind.GroundItem, grant.Item.Kind);
        Assert.NotEqual(grant.Item, grant.Instance);
        Assert.Equal(2423u, grant.ItemDefinitionId);
        Assert.Equal(1u, grant.Count);
    }

    [Fact]
    public void AClaimForAGuidTheWorldDoesNotKnowIsDropped()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.PostCommand(player, CommandKind.Interact, EntityId.Create(EntityKind.GroundItem, 1, 999));
        harness.Step();

        Assert.Equal(0, harness.Match.Loot.TotalGrants);
        Assert.Equal(1, harness.Match.Loot.DroppedClaims);
    }

    [Fact]
    public void AClaimNamingNothingIsNotEvenStaged()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.PostCommand(player, CommandKind.Interact);
        harness.Step();

        Assert.Equal(0, harness.Match.Loot.DroppedClaims);
    }

    [Fact]
    public void ADeadPlayerCannotClaim()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        WorldEntity item = SpawnItem(harness);

        harness.Match.Damage(player, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.PostCommand(player, CommandKind.Interact, item.Id);
        harness.Step();

        Assert.Equal(LifeState.Dead, player.Life);
        Assert.Equal(0, harness.Match.Loot.TotalGrants);
        Assert.True(harness.Match.World.TryGetEntity(item.Id, out _));
    }

    [Fact]
    public void GrantsAreClearedAtTheStartOfEveryTick()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        WorldEntity item = SpawnItem(harness);

        harness.PostCommand(player, CommandKind.Interact, item.Id);
        harness.Step();
        Assert.Single(harness.Match.Loot.Grants);

        harness.Step();
        Assert.Empty(harness.Match.Loot.Grants);
    }

    [Fact]
    public void TwoWorldsMintDisjointItemGuidsWhereTwoSessionsWouldCollide()
    {
        // The collision this replaces: LootWorld is a field of the per-connection session state and
        // is rebuilt with the same fixed bases every time (docs/22 §7.1).
        var first = new MatchHarness(matchId: 1);
        var second = new MatchHarness(matchId: 2);

        WorldEntity a = SpawnItem(first);
        WorldEntity b = SpawnItem(second);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id.Sequence, b.Id.Sequence);
        Assert.NotEqual(a.Id.MatchId, b.Id.MatchId);
    }
}
