using System.Numerics;
using System.Reflection;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class InteractionFeedbackTests
{
    [Theory]
    [InlineData(false, 1374u)]
    [InlineData(true, 1374u)]
    [InlineData(false, 83u)]
    [InlineData(true, 83u)]
    public void LootWeaponReplacementPublishesTheReturnToFists(bool bodyBag, uint oldDefinition)
    {
        using var world = new Fixture();
        world.Inventory.TryPickUp(2124, 1, out _); // Room to retain the displaced weapon.
        InventoryItemInstance old;
        if (oldDefinition == 1374)
            old = StartLootReload(world, completedShells: 2).Gun;
        else
        {
            old = world.Inventory.CreateInstance(oldDefinition, 1);
            world.Inventory.BindLoadout(old, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        }
        RegisterReplacementHandComponents(world, old);
        byte[] request = ReplacementLootRequest(world, bodyBag, SurvivorLoadout.Wheel1);
        world.Sent.Clear();

        world.Send(request);

        Assert.Equal(SurvivorLoadout.Fists, world.Inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, world.Inventory.WieldedItemGuid);
        Assert.Equal(1373u, world.Inventory.LoadoutSlots[SurvivorLoadout.Wheel1].DefinitionId);
        Assert.Equal(world.Inventory.BaseBag!.Guid, old.ContainerGuid);
        Assert.Null(world.Combat.Reload);
        if (oldDefinition == 1374)
        {
            Assert.Equal(2, world.Combat.Shooter.AmmoOf(old.Guid));
            // The existing weapon-change path reconciles the retained gun with a final
            // Reload snapshot before publishing the new hand.
            byte[] reload = Assert.Single(world.Sent, IsReload);
            Assert.Equal(WeaponReplyPackets.Reload(WeaponReplyPackets.ImmediateGameTime, old.Guid,
                projectileCount: 0, ammoCount: 2, inventoryAmmoCount: 4,
                reloadCount: world.Combat.Shooter.ReloadCountOf(old.Guid)), reload);
        }

        byte[] slots = Assert.Single(world.Sent, p => Is(p, 0x86, 0x04));
        Assert.Equal(SurvivorLoadout.Fists, BitConverter.ToUInt32(slots, slots.Length - 4));
        byte[] manager = Assert.Single(world.Sent, p => Is(p, 0xa0, 0x05));
        Assert.Equal(AbilityPackets.SetActivatableAbilityManager(world.Inventory.ToLoadoutSlots()), manager);
        int managerIndex = world.Sent.IndexOf(manager);
        Assert.True(world.Sent.IndexOf(slots) < managerIndex);
        ulong fists = world.Inventory.LoadoutSlots[SurvivorLoadout.Fists].Guid;
        int finalBinding = world.Sent.FindLastIndex(p => IsReplacementHandBinding(p, fists));
        Assert.True(finalBinding > managerIndex, "The final fist binding must follow the refreshed ability manager.");
        Assert.DoesNotContain(world.Sent.Skip(finalBinding + 1), p => Is(p, 0x94, 0x01));
        uint oldAbility = AbilityPackets.AbilityIdOf(oldDefinition);
        if (oldAbility != 0)
        {
            byte[] uninit = Assert.Single(world.Sent, p => Is(p, 0xa0, 0x03));
            Assert.Equal(AbilityPackets.UninitAbility(oldAbility), uninit);
            Assert.True(world.Sent.IndexOf(uninit) < world.Sent.IndexOf(slots));
        }
        else Assert.DoesNotContain(world.Sent, p => Is(p, 0xa0, 0x03));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LootWeaponReplacementInAnotherSlotPreservesTheActiveReload(bool bodyBag)
    {
        using var world = new Fixture();
        world.Inventory.TryPickUp(2124, 1, out _);
        var (active, ammo, pending) = StartLootReload(world, completedShells: 2);
        var displaced = world.Inventory.CreateInstance(1889, 1);
        world.Inventory.BindLoadout(displaced, SurvivorLoadout.Wheel2,
            InventoryAutoAssign.PassiveBodySlot(displaced.Fact, world.Inventory.EquipmentSlots.Keys.ToHashSet()));
        RegisterReplacementHandComponents(world, active);
        long deadline = pending.DueAtMs;
        byte[] request = ReplacementLootRequest(world, bodyBag, SurvivorLoadout.Wheel2);
        world.Sent.Clear();

        world.Send(request);

        Assert.Equal(1373u, world.Inventory.LoadoutSlots[SurvivorLoadout.Wheel2].DefinitionId);
        Assert.Equal(world.Inventory.BaseBag!.Guid, displaced.ContainerGuid);
        Assert.Equal(active.Guid, world.Inventory.WieldedItemGuid);
        Assert.Same(pending, world.Combat.Reload);
        Assert.Equal(deadline, pending.DueAtMs);
        Assert.Equal(2, world.Combat.Shooter.AmmoOf(active.Guid));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0xa0, 0x03) || Is(p, 0xa0, 0x05));
        Assert.DoesNotContain(world.Sent, p => p.Length >= 6 && p[0] == 0x82 && p[5] is 0x08 or 0x0b);
        Assert.NotNull(WeaponFireArm.AdvanceReload(world.Combat, pending, deadline, active.Guid, world.Inventory));
        Assert.Equal(3, world.Combat.Shooter.AmmoOf(active.Guid));
        Assert.Equal(3, ammo.Count(1511));
    }

    private static object ReplacementSession(Fixture world)
        => ((SoeConnection)typeof(Fixture).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(world)!).Tag!;

    private static void RegisterReplacementHandComponents(Fixture world, InventoryItemInstance active)
    {
        object state = ReplacementSession(world);
        var weapons = (WeaponSession)state.GetType().GetProperty("Weapons")!.GetValue(state)!;
        weapons.MagazineSource = (guid, _) => world.Combat.Shooter.AmmoOf(guid);
        foreach (var item in new[] { world.Inventory.LoadoutSlots[SurvivorLoadout.Fists], active })
        {
            using var writer = new PacketWriter();
            weapons.CreateItemAdd(0x1001, item.ToRecord(0x1001))(writer);
        }
    }

    private static byte[] ReplacementLootRequest(Fixture world, bool bodyBag, uint destinationSlot)
    {
        var source = world.Loot.Spawn(bodyBag ? BodyBag.ItemDefinitionId : 1373, 1, Vector3.Zero);
        ulong itemGuid = source.WorldGuid;
        if (bodyBag)
        {
            itemGuid = 0x5001;
            var bag = new BodyBag();
            bag.Add(itemGuid, 1373, 1);
            object state = ReplacementSession(world);
            var bags = (Dictionary<ulong, BodyBag>)state.GetType().GetProperty("BodyBags")!.GetValue(state)!;
            bags.Add(source.WorldGuid, bag);
        }
        using var writer = new PacketWriter();
        writer.WriteByte(0xc8); writer.WriteUInt16(1);
        writer.WriteUInt64(PlayerInventory.EquippedContainerGuid);
        writer.WriteUInt64(source.WorldGuid); writer.WriteUInt64(itemGuid); writer.WriteUInt64(0x1001);
        writer.WriteUInt32(1); writer.WriteInt32((int)destinationSlot);
        return writer.Written.ToArray();
    }

    private static bool IsReplacementHandBinding(byte[] packet, ulong itemGuid)
        => packet.Length >= 30 && Is(packet, 0x94, 0x02)
            && BitConverter.ToUInt32(packet, 18) == BodySlots.RightHand
            && BitConverter.ToUInt64(packet, 22) == itemGuid;
}
