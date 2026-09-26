using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Theory]
    [InlineData("wrong_character")]
    [InlineData("no_room")]
    [InlineData("disabled")]
    public void InventoryReload_RefusedUnloadKeepsThePendingReload(string refusal)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, new(5, 0, 0));
        InventoryReloadInventory(player, refusal == "no_room" ? 1 : 10000,
            answerUnload: refusal != "disabled");
        var gun = SkinReloadGun(f, player, 2, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        CombatInterest(f);
        CombatPacket(f, player, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            Environment.TickCount64 + 10_000);
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        ulong counter = combat.Shooter.ReloadCountOf(gun.Guid);
        int mark = f.Recorder.Routed.Count;

        InventoryReloadUse(f, player, gun.Guid, 7, refusal == "wrong_character" ? 999ul : 1ul);

        Assert.Same(pending, combat.Reload);
        Assert.Equal(counter, combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Equal(2, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(12, SkinReloadAmmo(player).Count(1511));
        byte[][] sent = SkinReloadSelf(f, player, mark);
        Assert.DoesNotContain(sent, p => InventoryReloadStopped(p, gun.Guid)
            || SkinReloadIsReply(p, gun.Guid) || SkinReloadIsItem(p, 2, gun.Guid));
        if (refusal != "disabled") Assert.Contains(sent, p => p.Length > 1 && p[0] == 0xc8 && p[1] == 3);
        Assert.DoesNotContain(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.ReloadInterrupt(CombatOwner(player, viewer), gun.Guid)));
        Assert.NotNull(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, inventory));
        Assert.Equal(3, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(11, SkinReloadAmmo(player).Count(1511));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void InventoryReload_AcceptedUnloadStopsBeforeItemRefreshAndCannotCompleteOldWork(int magazine)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, new(5, 0, 0));
        InventoryReloadInventory(player, 10000);
        var gun = SkinReloadGun(f, player, magazine, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        for (int i = 0; i < 255; i++) combat.Shooter.Load(gun.Guid, 0);
        CombatInterest(f);
        CombatPacket(f, player, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            Environment.TickCount64 + 10_000);
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        ulong counter = combat.Shooter.ReloadCountOf(gun.Guid);
        int mark = f.Recorder.Routed.Count;

        InventoryReloadUse(f, player, gun.Guid, 7);

        Assert.Null(combat.Reload);
        Assert.Equal(counter + 1, combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Equal(0, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(12 + magazine, SkinReloadAmmo(player).Count(1511));
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, inventory));
        Assert.Equal(0, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(12 + magazine, SkinReloadAmmo(player).Count(1511));
        InventoryReloadAssertRefresh(f, player, viewer, gun, mark, counter + 1, 0);
    }

    [Theory]
    [InlineData("repaint")]
    [InlineData("equip")]
    [InlineData("unequip")]
    public void InventoryReload_ItemRefreshStopsItsReloadAndResynchronizesTheNativeCounter(string action)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, new(5, 0, 0));
        // Deliberately large test capacity reaches the ordinary unequip handler for a gun.
        // This is fixture setup, not a proposed change to retail carrying capacity.
        InventoryReloadInventory(player, 10000);
        var gun = SkinReloadGun(f, player, 2, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        combat.Shooter.Load(gun.Guid, 0);
        CombatInterest(f);
        CombatPacket(f, player, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            Environment.TickCount64 + 10_000);
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        ulong counter = combat.Shooter.ReloadCountOf(gun.Guid);
        int mark = f.Recorder.Routed.Count;

        if (action == "equip") InventoryReloadMove(f, player, gun.Guid, SurvivorLoadout.Wheel2);
        else InventoryReloadUse(f, player, gun.Guid, action == "unequip" ? 12u : 60u);

        Assert.Null(combat.Reload);
        Assert.Equal(counter + 1, combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Equal(2, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(12, SkinReloadAmmo(player).Count(1511));
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, inventory));
        if (action == "unequip")
        {
            Assert.Equal(0u, gun.LoadoutSlotId);
            Assert.Equal(inventory.BaseBag!.Guid, gun.ContainerGuid);
        }
        else Assert.Equal(action == "equip" ? SurvivorLoadout.Wheel2 : SurvivorLoadout.Wheel1, gun.LoadoutSlotId);
        InventoryReloadAssertRefresh(f, player, viewer, gun, mark, counter + 1, 2);
    }

    [Fact]
    public void InventoryReload_RepaintingASpareDoesNotCancelTheHeldWeapon()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, new(5, 0, 0));
        InventoryReloadInventory(player, 10000);
        var held = SkinReloadGun(f, player, 2, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        var spare = inventory.CreateInstance(1374, 1);
        inventory.BindLoadout(spare, SurvivorLoadout.Wheel2, 77);
        combat.Shooter.DeclareWeapon(spare.Guid, 1374, 1);
        combat.Shooter.Load(spare.Guid, 0);
        CombatInterest(f);
        CombatPacket(f, player, held, ShootingPacketBuilder.ReloadRequest(held.Guid),
            Environment.TickCount64 + 10_000);
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        ulong counter = combat.Shooter.ReloadCountOf(held.Guid);
        int mark = f.Recorder.Routed.Count;

        InventoryReloadUse(f, player, spare.Guid, 60);

        Assert.Same(pending, combat.Reload);
        Assert.Equal(counter, combat.Shooter.ReloadCountOf(held.Guid));
        Assert.Equal(held.Guid, inventory.WieldedItemGuid);
        byte[][] sent = SkinReloadSelf(f, player, mark);
        Assert.DoesNotContain(sent, p => InventoryReloadStopped(p, held.Guid) || SkinReloadIsItem(p, 2, held.Guid));
        Assert.DoesNotContain(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.ReloadInterrupt(CombatOwner(player, viewer), held.Guid)));
        InventoryReloadAssertCounter(sent, spare.Guid, 1, 1);
        Assert.NotNull(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, held.Guid, inventory));
        Assert.Equal(3, combat.Shooter.AmmoOf(held.Guid));
        Assert.Equal(11, SkinReloadAmmo(player).Count(1511));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InventoryReload_SlotExchangeResynchronizesBothItemsIncludingTheDisplacedReload(bool moveHeld)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, new(5, 0, 0));
        InventoryReloadInventory(player, 10000);
        var held = SkinReloadGun(f, player, 2, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        var spare = inventory.CreateInstance(1374, 1);
        inventory.BindLoadout(spare, SurvivorLoadout.Wheel2, 77);
        combat.Shooter.DeclareWeapon(spare.Guid, 1374, 1);
        for (int i = 0; i < 255; i++) combat.Shooter.Load(held.Guid, 0);
        for (int i = 0; i < 5; i++) combat.Shooter.Load(spare.Guid, 0);
        CombatInterest(f);
        CombatPacket(f, player, held, ShootingPacketBuilder.ReloadRequest(held.Guid),
            Environment.TickCount64 + 10_000);
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        ulong counter = combat.Shooter.ReloadCountOf(held.Guid);
        int mark = f.Recorder.Routed.Count;

        InventoryReloadMove(f, player, moveHeld ? held.Guid : spare.Guid,
            moveHeld ? SurvivorLoadout.Wheel2 : SurvivorLoadout.Wheel1);

        Assert.Null(combat.Reload);
        Assert.Same(held, inventory.LoadoutSlots[SurvivorLoadout.Wheel2]);
        Assert.Same(spare, inventory.LoadoutSlots[SurvivorLoadout.Wheel1]);
        Assert.Equal(held.Guid, inventory.WieldedItemGuid);
        Assert.Equal(counter + 1, combat.Shooter.ReloadCountOf(held.Guid));
        Assert.Equal(5ul, combat.Shooter.ReloadCountOf(spare.Guid));
        Assert.Equal(2, combat.Shooter.AmmoOf(held.Guid));
        Assert.Equal(1, combat.Shooter.AmmoOf(spare.Guid));
        Assert.Equal(12, SkinReloadAmmo(player).Count(1511));
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, held.Guid, inventory));
        byte[][] sent = SkinReloadSelf(f, player, mark);
        Assert.DoesNotContain(sent, p => SkinReloadIsItem(p, 4, held.Guid) || SkinReloadIsItem(p, 4, spare.Guid));
        InventoryReloadAssertRefresh(f, player, viewer, held, mark, counter + 1, 2);
        InventoryReloadAssertCounter(sent, spare.Guid, 5, 1);
    }

    private static void InventoryReloadInventory(SoeConnection player, int capacity, bool answerUnload = true)
    {
        var inventory = new PlayerInventory(Get<ulong>(player.Tag!, "Guid"),
            Get<LootWorld>(player.Tag!, "Loot").NextItemGuid,
            new InventoryOptions { StarterOutfit = [], BaseCarryBulk = capacity, AnswerUnloadWeapon = answerUnload });
        inventory.Bootstrap();
        Set(player.Tag!, "Inventory", inventory);
    }

    private static void InventoryReloadUse(Fixture f, SoeConnection player, ulong item, uint option, ulong character = 1)
    {
        ulong owner = Get<ulong>(player.Tag!, "Guid");
        using var writer = new PacketWriter();
        writer.WriteByte(0xac); writer.WriteByte(0x2c); writer.WriteUInt64(1); writer.WriteUInt32(option);
        writer.WriteUInt64(character); writer.WriteUInt64(owner); writer.WriteUInt64(owner);
        writer.WriteUInt64(item); writer.WriteBool(true);
        CombatGatewayPacket(f, player, writer.Written.ToArray(), channel: 0);
    }

    private static void InventoryReloadMove(Fixture f, SoeConnection player, ulong item, uint slot)
    {
        ulong owner = Get<ulong>(player.Tag!, "Guid");
        using var writer = new PacketWriter();
        writer.WriteByte(0xc8); writer.WriteUInt16(1); writer.WriteUInt64(PlayerInventory.EquippedContainerGuid);
        writer.WriteUInt64(owner); writer.WriteUInt64(item); writer.WriteUInt64(owner);
        writer.WriteUInt32(1); writer.WriteInt32((int)slot);
        CombatGatewayPacket(f, player, writer.Written.ToArray(), channel: 0);
    }

    private static bool InventoryReloadStopped(byte[] packet, ulong guid) => packet.Length >= 14
        && packet[0] == 0x82 && packet[5] == WeaponReplyPackets.SubReloadRejected
        && BitConverter.ToUInt64(packet, 6) == guid;

    private static void InventoryReloadAssertCounter(byte[][] sent, ulong guid, ulong count, int magazine)
    {
        int added = Array.FindIndex(sent, p => SkinReloadIsItem(p, 2, guid));
        Assert.True(added >= 0);
        byte[] snapshot = Assert.Single(sent.Skip(added + 1), p => SkinReloadIsReply(p, guid));
        Assert.Equal(0u, BitConverter.ToUInt32(snapshot, 14));
        var client = new AugustPumpReloadClient(magazine); // ItemAdd initializes native counters to zero.
        client.Apply(snapshot);
        Assert.Equal(count, client.Acknowledged);
        Assert.Equal(magazine, client.Magazine);
    }

    private static void InventoryReloadAssertRefresh(Fixture f, SoeConnection player, SoeConnection viewer,
        InventoryItemInstance gun, int mark, ulong counter, int magazine)
    {
        byte[][] sent = SkinReloadSelf(f, player, mark);
        int added = Array.FindIndex(sent, p => SkinReloadIsItem(p, 2, gun.Guid));
        byte[] stopped = Assert.Single(sent, p => InventoryReloadStopped(p, gun.Guid));
        Assert.True(Array.IndexOf(sent, stopped) < added);
        InventoryReloadAssertCounter(sent, gun.Guid, counter, magazine);
        byte[] interrupt = RemoteWeaponPackets.ReloadInterrupt(CombatOwner(player, viewer), gun.Guid);
        Assert.Single(CombatRemote(f, viewer, mark), p => p.SequenceEqual(interrupt));
        var routed = f.Recorder.Routed.Skip(mark).ToArray();
        int observerStop = Array.FindIndex(routed, r => ReferenceEquals(r.Connection, viewer)
            && r.Packet[1..].SequenceEqual(interrupt));
        int firstRefresh = Array.FindIndex(routed, r => ReferenceEquals(r.Connection, player)
            && r.Packet.Length > 3 && r.Packet[1] == 0x11 && r.Packet[2] == 2);
        Assert.True(observerStop >= 0 && observerStop < firstRefresh);
    }
}
