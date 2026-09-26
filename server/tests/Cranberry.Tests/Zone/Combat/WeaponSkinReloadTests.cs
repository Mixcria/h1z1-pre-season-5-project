using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    public static IEnumerable<object[]> SkinReloadCounters()
    {
        foreach (bool savedPreset in new[] { false, true })
        foreach (int counter in new[] { 0, 1, 255, 256 })
        foreach (int magazine in new[] { 0, 2 })
        foreach (bool held in new[] { false, true })
            yield return [savedPreset, counter, magazine, held];
    }

    [Theory]
    [MemberData(nameof(SkinReloadCounters))]
    public void SkinRefresh_RestoresCounterBeforeTheNextPumpReload(
        bool savedPreset, int counter, int magazine, bool held)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { }; // complete reload work explicitly, without wall-clock sleeps
        var player = f.Add(1, Vector3.Zero);
        var gun = SkinReloadGun(f, player, magazine, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        for (int i = 0; i < counter; i++) combat.Shooter.Load(gun.Guid, 0);
        if (!held) SkinReloadSelectSlot(f, player, SurvivorLoadout.Fists);
        var before = gun.ToRecord(1);
        int mark = f.Recorder.Routed.Count;

        SkinReloadApply(f, player, gun, savedPreset);

        Assert.Equal(3720u, gun.DisplayDefinitionId);
        Assert.Same(gun, inventory.Items[gun.Guid]);
        Assert.Equal(before with { DefinitionId = 3720 }, gun.ToRecord(1));
        Assert.Equal(magazine, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal((ulong)counter, combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Equal(12, SkinReloadAmmo(player).Count(1511));
        byte[][] sent = SkinReloadSelf(f, player, mark);
        int deleted = Array.FindIndex(sent, p => SkinReloadIsItem(p, 4, gun.Guid));
        int added = Array.FindIndex(sent, p => SkinReloadIsItem(p, 2, gun.Guid));
        Assert.True(deleted >= 0 && added > deleted);
        var client = new AugustPumpReloadClient(magazine); // native ItemAdd constructs counters at zero
        if (counter == 0)
            Assert.DoesNotContain(sent, p => SkinReloadIsReply(p, gun.Guid));
        else
        {
            byte[] snapshot = Assert.Single(sent, p => SkinReloadIsReply(p, gun.Guid));
            Assert.True(Array.IndexOf(sent, snapshot) > added);
            Assert.Equal(0u, BitConverter.ToUInt32(snapshot, 14));
            Assert.Equal((uint)magazine, BitConverter.ToUInt32(snapshot, 18));
            Assert.Equal(12u, BitConverter.ToUInt32(snapshot, 22));
            client.Apply(snapshot);
        }
        Assert.Equal((ulong)counter, client.Acknowledged);

        if (!held) SkinReloadSelectSlot(f, player, gun.LoadoutSlotId);
        client.BeginReload();
        mark = f.Recorder.Routed.Count;
        CombatPacket(f, player, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            Environment.TickCount64 + 10_000);
        client.Apply(Assert.Single(SkinReloadSelf(f, player, mark), p => SkinReloadIsReply(p, gun.Guid)));
        Assert.Equal((ulong)counter + 1, client.Acknowledged);
        Assert.Equal(12, client.CachedReserve); // August 1414887e0 must enter its reserve-staging branch
        Assert.True(client.CompleteShell());
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        var completion = WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, inventory)!.Value;
        client.Apply(Assert.Single(completion.Replies!, p => SkinReloadIsReply(p, gun.Guid)));
        Assert.Equal(magazine + 1, client.Magazine);
        Assert.Equal(magazine + 1, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(11, SkinReloadAmmo(player).Count(1511));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void SkinRefresh_CancelsOnlyTheTargetReloadBeforeRebuildingAndNotifiesItsObserver(
        bool savedPreset, bool completeShell, bool removeReserve)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, new(5, 0, 0));
        var gun = SkinReloadGun(f, player, 2, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        for (int i = 0; i < 255; i++) combat.Shooter.Load(gun.Guid, 0);
        CombatInterest(f);
        CombatPacket(f, player, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            Environment.TickCount64 + 10_000);
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        var ammo = SkinReloadAmmo(player);
        if (completeShell)
            Assert.NotNull(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, inventory));
        if (removeReserve) Assert.Equal(12, ammo.Take(1511, 12, []));
        int magazine = combat.Shooter.AmmoOf(gun.Guid), reserve = ammo.Count(1511);
        ulong counter = combat.Shooter.ReloadCountOf(gun.Guid);
        int mark = f.Recorder.Routed.Count;

        SkinReloadApply(f, player, gun, savedPreset);

        Assert.Equal(3720u, gun.DisplayDefinitionId);
        Assert.Null(combat.Reload);
        Assert.Equal(counter + 1, combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, inventory));
        Assert.Equal(magazine, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(reserve, ammo.Count(1511));
        byte[][] sent = SkinReloadSelf(f, player, mark);
        int deleted = Array.FindIndex(sent, p => SkinReloadIsItem(p, 4, gun.Guid));
        int added = Array.FindIndex(sent, p => SkinReloadIsItem(p, 2, gun.Guid));
        int stopped = Array.FindIndex(sent, p => p.Length > 5 && p[0] == 0x82
            && p[5] == WeaponReplyPackets.SubReloadRejected);
        Assert.True(stopped >= 0 && stopped < deleted && deleted < added);
        byte[] snapshot = Assert.Single(sent.Skip(added + 1), p => SkinReloadIsReply(p, gun.Guid));
        var client = new AugustPumpReloadClient(magazine);
        client.Apply(snapshot);
        Assert.Equal(counter + 1, client.Acknowledged);
        Assert.Equal(magazine, client.Magazine);

        byte[] interrupt = RemoteWeaponPackets.ReloadInterrupt(CombatOwner(player, viewer), gun.Guid);
        Assert.Single(CombatRemote(f, viewer, mark), p => p.SequenceEqual(interrupt));
        var routed = f.Recorder.Routed.Skip(mark).ToArray();
        int observerStop = Array.FindIndex(routed, r => ReferenceEquals(r.Connection, viewer)
            && r.Packet[1..].SequenceEqual(interrupt));
        int selfDelete = Array.FindIndex(routed, r => ReferenceEquals(r.Connection, player)
            && SkinReloadIsItem(r.Packet[1..], 4, gun.Guid));
        Assert.True(observerStop >= 0 && observerStop < selfDelete);

        // A later accepted reload owns a different token. Re-running the old completion
        // must not consume ammunition or cancel this newer operation.
        if (!removeReserve)
        {
            client.BeginReload();
            mark = f.Recorder.Routed.Count;
            CombatPacket(f, player, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid), pending.DueAtMs + 10_000);
            var next = Assert.IsType<PendingWeaponReload>(combat.Reload);
            Assert.NotSame(pending, next);
            client.Apply(Assert.Single(SkinReloadSelf(f, player, mark), p => SkinReloadIsReply(p, gun.Guid)));
            Assert.Equal(reserve, client.CachedReserve);
            Assert.True(client.CompleteShell());
            Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, next.DueAtMs, gun.Guid, inventory));
            Assert.Same(next, combat.Reload);
            Assert.Equal(reserve, ammo.Count(1511));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkinRefresh_SkinningASpareKeepsTheHeldWeaponsReload(bool savedPreset)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, new(5, 0, 0));
        var held = SkinReloadGun(f, player, 2, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var spare = inventory.CreateInstance(1374, 1);
        inventory.BindLoadout(spare, SurvivorLoadout.Wheel2, 77);
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        combat.Shooter.DeclareWeapon(spare.Guid, 1374, 1);
        combat.Shooter.Load(spare.Guid, 0);
        CombatInterest(f);
        CombatPacket(f, player, held, ShootingPacketBuilder.ReloadRequest(held.Guid), Environment.TickCount64 + 10_000);
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        int mark = f.Recorder.Routed.Count;

        SkinReloadApply(f, player, spare, savedPreset);

        Assert.Equal(3720u, spare.DisplayDefinitionId);
        Assert.Same(pending, combat.Reload);
        Assert.Equal(held.Guid, inventory.WieldedItemGuid);
        Assert.DoesNotContain(SkinReloadSelf(f, player, mark), p => SkinReloadIsItem(p, 4, held.Guid));
        Assert.DoesNotContain(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.ReloadInterrupt(CombatOwner(player, viewer), held.Guid)));
        Assert.NotNull(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, held.Guid, inventory));
        Assert.Equal(3, combat.Shooter.AmmoOf(held.Guid));
        Assert.Equal(11, SkinReloadAmmo(player).Count(1511));
        Assert.Equal(1, combat.Shooter.AmmoOf(spare.Guid));
        Assert.Equal(1ul, combat.Shooter.ReloadCountOf(spare.Guid));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SkinRefresh_RefusedOrDuplicateRequestsDoNotInterruptReload(bool savedPreset, bool duplicate)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var gun = SkinReloadGun(f, player, 2, 12);
        if (duplicate) SkinReloadApply(f, player, gun, savedPreset);
        CombatPacket(f, player, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid), Environment.TickCount64 + 10_000);
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        ulong counter = combat.Shooter.ReloadCountOf(gun.Guid);
        int mark = f.Recorder.Routed.Count;

        SkinReloadApply(f, player, gun, savedPreset, account: duplicate ? 3752u : 4076u); // AR skin on a pump

        Assert.Same(pending, combat.Reload);
        Assert.Equal(counter, combat.Shooter.ReloadCountOf(gun.Guid));
        Assert.Equal(2, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(12, SkinReloadAmmo(player).Count(1511));
        byte[][] sent = SkinReloadSelf(f, player, mark);
        Assert.Contains(sent, p => p.Length > 1 && p[0] == 0xc8 && p[1] == 3);
        Assert.DoesNotContain(sent, p => SkinReloadIsItem(p, 4, gun.Guid) || SkinReloadIsReply(p, gun.Guid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkinRefresh_MenuOrDeathCannotReviveTheCancelledReload(bool death)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var player = f.Add(1, Vector3.Zero);
        var gun = SkinReloadGun(f, player, 2, 12);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var combat = Get<SessionCombat>(player.Tag!, "Combat");
        CombatPacket(f, player, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid), Environment.TickCount64 + 10_000);
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        SkinReloadApply(f, player, gun, savedPreset: true);
        Assert.Equal(12, SkinReloadAmmo(player).Count(1511));
        if (death) f.Service.ForTest(player).Damage(10_000, DamageCause.Bullet);
        else Call(f.Service, "AbandonMatch", player, player.Tag, "skin reload regression");
        // Death can transfer inventory into a body bag. Compare after that legitimate
        // transition, so the assertion isolates stale work rather than forbidding the drop.
        var oldAmmo = new PlayerAmmoContext(inventory, 1, AmmoOptions.Default);
        int remaining = oldAmmo.Count(1511);
        int mark = f.Recorder.Routed.Count;
        SkinReloadApply(f, player, gun, savedPreset: true);
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, inventory));
        Assert.Null(combat.Reload);
        Assert.Equal(remaining, oldAmmo.Count(1511));
        Assert.DoesNotContain(SkinReloadSelf(f, player, mark), p => SkinReloadIsItem(p, 2, gun.Guid));
    }

    private static InventoryItemInstance SkinReloadGun(Fixture f, SoeConnection player, int magazine, uint reserve)
    {
        var gun = CombatGun(f, player, 1374, magazine);
        var weapons = Get<WeaponSession>(player.Tag!, "Weapons");
        weapons.MarkProjectileDefinitionsSent();
        weapons.MarkWeaponDefinitionsSent();
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        Assert.True(inventory.TryStow(inventory.CreateInstance(1511, reserve)));
        return gun;
    }

    private static PlayerAmmoContext SkinReloadAmmo(SoeConnection player) =>
        new(Get<PlayerInventory>(player.Tag!, "Inventory"), Get<ulong>(player.Tag!, "Guid"), AmmoOptions.Default);

    private static void SkinReloadApply(Fixture f, SoeConnection player, InventoryItemInstance gun,
        bool savedPreset, uint account = 3752)
    {
        if (savedPreset)
        {
            Assert.True(AugustWardrobeCatalog.TryResolveClicked(account, out var selected));
            Assert.True(Get<AugustWardrobeState>(player.Tag!, "Wardrobe").TryApply(
                new(0x32, 1, 0, 2, selected.CategoryPrototypeId, account), out _, out _, out _));
            using var action = new PacketWriter();
            action.WriteByte(0x9a); action.WriteByte(5);
            action.WriteString("Cranberry.Inventory"); action.WriteString($"skin:{gun.Guid}:selected");
            action.WriteUInt32(0);
            CombatGatewayPacket(f, player, action.Written.ToArray(), channel: 0);
        }
        else
        {
            ulong owner = Get<ulong>(player.Tag!, "Guid");
            using var use = new PacketWriter();
            use.WriteByte(0xac); use.WriteByte(0x2c); use.WriteUInt64(1); use.WriteUInt32(88);
            use.WriteUInt64(owner); use.WriteUInt64(owner); use.WriteUInt64(owner);
            use.WriteUInt64(gun.Guid); use.WriteByte(1);
            CombatGatewayPacket(f, player, use.Written.ToArray(), channel: 0);
            using var selection = new PacketWriter();
            selection.WriteByte(0xac); selection.WriteByte(0x32);
            selection.WriteUInt32(1); selection.WriteUInt32(0); selection.WriteUInt32(2);
            selection.WriteUInt32(1374); selection.WriteUInt32(account);
            CombatGatewayPacket(f, player, selection.Written.ToArray(), channel: 0);
        }
    }

    private static void SkinReloadSelectSlot(Fixture f, SoeConnection player, uint slot)
    {
        using var packet = new PacketWriter();
        packet.WriteByte(0x86); packet.WriteByte(6);
        packet.WriteUInt32(0); packet.WriteUInt32(slot); packet.WriteUInt32(1234);
        CombatGatewayPacket(f, player, packet.Written.ToArray(), channel: 0);
    }

    private static byte[][] SkinReloadSelf(Fixture f, SoeConnection player, int mark) =>
        [.. f.Recorder.Routed.Skip(mark).Where(r => ReferenceEquals(r.Connection, player)).Select(r => r.Packet[1..])];

    private static bool SkinReloadIsReply(byte[] packet, ulong guid) =>
        packet.Length == 34 && packet[0] == 0x82 && packet[5] == 8 && BitConverter.ToUInt64(packet, 6) == guid;

    private static bool SkinReloadIsItem(byte[] packet, byte sub, ulong guid) =>
        packet.Length >= (sub == 2 ? 31 : 19) && packet[0] == 0x11 && packet[1] == sub && packet[2] == 0
        && BitConverter.ToUInt64(packet, sub == 2 ? 23 : 11) == guid;
}
