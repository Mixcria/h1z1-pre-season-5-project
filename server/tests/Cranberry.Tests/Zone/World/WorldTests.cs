using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// Slot tables, not compacting lists: a slot is an interest-grid key and must never move under a live
// entity. docs/22 §4.4.
public sealed class WorldTests
{
    private static readonly Vector3 Ground = new(0f, 506f, 0f);

    [Fact]
    public void PlayersSpawnAtTheStagingPointWithFullHealth()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        Vector4 spawn = harness.Settings.StagingSpawn;

        Assert.Equal(new Vector3(spawn.X, spawn.Y, spawn.Z), player.Position);
        Assert.Equal(harness.Settings.StartingHealth, player.Health);
        Assert.Equal(LifeState.Alive, player.Life);
        Assert.Equal(EntityKind.Character, player.Id.Kind);
    }

    [Fact]
    public void SlotsAreDenseAndRecycledOnlyWhenAPlayerLeaves()
    {
        var harness = new MatchHarness();
        MatchPlayer first = harness.AddPlayer("First");
        MatchPlayer second = harness.AddPlayer("Second");

        Assert.Equal(0, first.Slot);
        Assert.Equal(1, second.Slot);

        harness.Match.RemovePlayer(first);
        MatchPlayer third = harness.AddPlayer("Third");

        Assert.Equal(0, third.Slot);
        Assert.NotEqual(first.Id, third.Id);
        Assert.Equal(2, harness.Match.World.PlayerCount);
    }

    [Fact]
    public void DeathKeepsTheSlotAndOnlyLeavingFreesIt()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.Match.Damage(player, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.Step();

        Assert.Equal(1, harness.Match.World.PlayerCount);
        Assert.Equal(0, harness.Match.World.AliveCount);
        Assert.Same(player, harness.Match.World.PlayerAt(player.Slot));

        harness.Match.RemovePlayer(player);
        Assert.Null(harness.Match.World.PlayerAt(player.Slot));
    }

    [Fact]
    public void AFullMatchRefusesAnotherPlayer()
    {
        var harness = new MatchHarness(MatchSettings.Default with { MaxPlayers = 2 });
        harness.AddPlayer("First");
        harness.AddPlayer("Second");

        Assert.Throws<InvalidOperationException>(() => harness.AddPlayer("Third"));
    }

    [Fact]
    public void PlayersAndEntitiesNeverShareAnInterestGridKey()
    {
        var harness = new MatchHarness(MatchSettings.Default with { MaxPlayers = 4, EntityCapacity = 8 });
        var keys = new HashSet<int>();

        for (int i = 0; i < 4; i++)
        {
            MatchPlayer player = harness.AddPlayer($"Peer{i}");
            Assert.True(keys.Add(harness.Match.World.PlayerKey(player)));
        }

        for (int i = 0; i < 8; i++)
        {
            WorldEntity entity = harness.Match.World.SpawnGroundItem(2423, 9066, 12302, 1, Ground);
            Assert.True(keys.Add(harness.Match.World.EntityKey(entity)));
        }

        Assert.Equal(12, keys.Count);
    }

    [Fact]
    public void AGridKeyResolvesBackToWhateverOwnsIt()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        WorldEntity entity = harness.Match.World.SpawnGroundItem(2423, 9066, 12302, 1, Ground);

        Assert.Same(player, harness.Match.World.PlayerFromKey(harness.Match.World.PlayerKey(player)));
        Assert.Same(entity, harness.Match.World.EntityFromKey(harness.Match.World.EntityKey(entity)));
        Assert.Null(harness.Match.World.EntityFromKey(harness.Match.World.PlayerKey(player)));
    }

    [Fact]
    public void EntitySlotsAreRecycledAfterASweep()
    {
        var harness = new MatchHarness();
        WorldEntity first = harness.Match.World.SpawnGroundItem(2423, 9066, 12302, 1, Ground);
        int slot = first.Slot;

        harness.Match.World.Despawn(first);
        harness.Step();

        WorldEntity second = harness.Match.World.SpawnGroundItem(2423, 9066, 12302, 1, Ground);

        Assert.Equal(slot, second.Slot);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(-1, first.Slot);
    }

    [Fact]
    public void AParachuteIsAnOrdinaryVehicleEntityWithItsOwnGuid()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        WorldEntity chute = harness.Match.World.SpawnVehicle(
            vehicleId: 13,
            modelId: 9374,
            position: player.Position,
            rotation: Quaternion.Identity,
            owner: player.Id);

        // No guid + 0x1000 aliasing and no literal transient id 2 anywhere (docs/22 §7.1).
        Assert.Equal(EntityKind.Vehicle, chute.Id.Kind);
        Assert.NotEqual(player.Id, chute.Id);
        Assert.NotEqual(player.Id.Value + 0x1000, chute.Id.Value);
        Assert.Equal(player.Id, chute.Owner);
    }

    [Fact]
    public void EveryGuidInAWorldIsUnique()
    {
        var harness = new MatchHarness();
        var seen = new HashSet<EntityId>();

        for (int i = 0; i < 20; i++)
        {
            Assert.True(seen.Add(harness.AddPlayer($"Peer{i}").Id));
        }

        for (int i = 0; i < 200; i++)
        {
            Assert.True(seen.Add(harness.Match.World.SpawnGroundItem(2423, 9066, 12302, 1, Ground).Id));
            Assert.True(seen.Add(harness.Match.World
                .SpawnVehicle(13, 9374, Ground, Quaternion.Identity, EntityId.None).Id));
        }

        Assert.Equal(420, seen.Count);
    }

    [Fact]
    public void AliveCountFollowsDeathsAndDepartures()
    {
        var harness = new MatchHarness();
        MatchPlayer first = harness.AddPlayer("First");
        MatchPlayer second = harness.AddPlayer("Second");
        harness.AddPlayer("Third");
        Assert.Equal(3, harness.Match.World.AliveCount);

        harness.Match.Damage(first, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.Step();
        Assert.Equal(2, harness.Match.World.AliveCount);

        harness.Match.RemovePlayer(second);
        Assert.Equal(1, harness.Match.World.AliveCount);
    }
}
