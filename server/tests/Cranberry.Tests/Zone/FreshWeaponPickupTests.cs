using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;
using Cranberry.Tests.Zone.Combat;

namespace Cranberry.Tests.Zone;

public sealed partial class StarterWeaponDrawTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BinocularReturnRetainsLoadedSkinnedRifleAndUsesItsFinalDrawClock(bool reloading)
    {
        var combatOptions = CombatOptions.Default with { MagazineResync = false, ShippedRefireGate = true,
            ShippedReloadTime = true, RefireJitterMs = 16 };
        var (service, connection, recorder, work) = World(options => options with
            { GiveStarterWeapon = false, Combat = combatOptions });
        try
        {
            Land(service, connection, recorder, work);
            var inventory = StateProperty<PlayerInventory>(connection, "Inventory");
            var combat = StateProperty<SessionCombat>(connection, "Combat");
            var weapons = StateProperty<WeaponSession>(connection, "Weapons");
            var movement = StateProperty<SessionMovementState>(connection, "Movement");
            movement.ApplyPlayer(ClientMovementUpdate.Parse(new byte[7]));
            movement.PinPlayer(Vector3.Zero);
            var ground = StateProperty<LootWorld>(connection, "Loot")
                .Spawn(2425, 1, Vector3.Zero, skinRewardItemId: 2601);
            SendVehicle(service, connection, [0x09, 0x07, 0, .. BitConverter.GetBytes(ground.WorldGuid)]);
            var rifle = Assert.Single(inventory.Items.Values, item => item.DefinitionId == 2425);
            var binoculars = inventory.LoadoutSlots[SurvivorLoadout.Binoculars];
            Assert.Equal(1542u, binoculars.DefinitionId);
            combat.Shooter.DeclareWeapon(rifle.Guid, 2425, 5);
            combat.Shooter.Load(rifle.Guid, 0);
            Assert.True(inventory.TryStow(inventory.CreateInstance(1429, 6)));
            var ammo = new PlayerAmmoContext(inventory, Self, combatOptions.Ammo);
            if (reloading) SendVehicle(service, connection, ShootingPacketBuilder.ReloadRequest(rifle.Guid));
            PendingWeaponReload? pending = combat.Reload;
            Assert.Equal(reloading, pending is not null);

            int mark = recorder.Messages.Count;
            SelectReloadTestSlot(service, connection, SurvivorLoadout.Binoculars);
            Assert.Equal(binoculars.Guid, inventory.WieldedItemGuid);
            Assert.Null(combat.Reload);
            ulong counter = combat.Shooter.ReloadCountOf(rifle.Guid);
            long before = Environment.TickCount64;
            SelectReloadTestSlot(service, connection, rifle.LoadoutSlotId);
            long after = Environment.TickCount64;
            var opticsTiming = weapons.EquipmentTiming(RetailBalance.WeaponDefinitionIdFor(1542));
            var gunTiming = weapons.EquipmentTiming(RetailBalance.WeaponDefinitionIdFor(2425));
            Assert.InRange(combat.Draw.ReadyAtMs, before + opticsTiming.Unequip + gunTiming.Equip,
                after + opticsTiming.Unequip + gunTiming.Equip);
            byte[][] returned = recorder.Messages.Skip(mark).Where(m => m.Direction == "s2c").Select(m => m.Bytes).ToArray();
            Assert.DoesNotContain(returned, p => IsPickupItemAdd(p, rifle.Guid) || IsPickupItemDelete(p, rifle.Guid)
                || IsPickupItemAdd(p, binoculars.Guid) || IsPickupItemDelete(p, binoculars.Guid));
            Assert.Equal(rifle.Guid, inventory.WieldedItemGuid);
            Assert.Equal(2601u, rifle.DisplayDefinitionId);
            Assert.Equal(5, combat.Shooter.AmmoOf(rifle.Guid));
            Assert.Equal(counter, combat.Shooter.ReloadCountOf(rifle.Guid));
            if (pending is not null)
                Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, rifle.Guid, inventory));
            var results = new List<WeaponArmResult>();
            byte[] shot = ShootingPacketBuilder.Fire(rifle.Guid, 0, 0, 0, [1]);
            WeaponFireArm.Handle(combat, shot, combatOptions, 2425, rifle.Guid,
                Vector3.Zero, combat.Draw.ReadyAtMs - 1, results, ammo);
            Assert.False(Assert.Single(results).LaunchRelay);
            WeaponFireArm.Handle(combat, shot, combatOptions, 2425, rifle.Guid,
                Vector3.Zero, combat.Draw.ReadyAtMs, results, ammo);
            Assert.True(Assert.Single(results).LaunchRelay);
            Assert.Equal(4, combat.Shooter.AmmoOf(rifle.Guid));
            Assert.Equal(6, ammo.Count(1429));
            SelectReloadTestSlot(service, connection, SurvivorLoadout.Binoculars);
            Assert.Same(binoculars, inventory.EquipmentSlots[BodySlots.RightHand]);
        }
        finally
        {
            connection.Disconnect();
            service.OnDisconnected(connection, DisconnectCause.ServerRequested);
        }
    }

    [Theory]
    [InlineData(true, 2425u, "Weapon_M16A4_3P.adr")]
    [InlineData(false, 2425u, "Weapon_M16A4_3P.adr")]
    [InlineData(true, 1718u, "Weapons_Pistol_44Magnum01_3P.adr")]
    [InlineData(false, 1718u, "Weapons_Pistol_44Magnum01_3P.adr")]
    [InlineData(true, 1997u, "Weapons_M9Auto_3P.adr")]
    [InlineData(false, 1997u, "Weapons_M9Auto_3P.adr")]
    public void FreshWeaponPickupAndLaterHotbarDrawKeepTheSameClientItem(
        bool wieldFirstWeapon, uint definition, string model)
    {
        var (service, connection, recorder, pending) = World(options => options with
        {
            GiveStarterWeapon = false,
            Inventory = options.Inventory with { WieldFirstWeapon = wieldFirstWeapon },
        });
        try
        {
            Land(service, connection, recorder, pending);
            var inventory = StateProperty<PlayerInventory>(connection, "Inventory");
            var movement = StateProperty<SessionMovementState>(connection, "Movement");
            movement.ApplyPlayer(ClientMovementUpdate.Parse(new byte[7]));
            movement.PinPlayer(Vector3.Zero);
            var loot = StateProperty<LootWorld>(connection, "Loot").Spawn(definition, 1, Vector3.Zero);
            int before = recorder.Messages.Count;

            SendVehicle(service, connection,
                [0x09, 0x15, 0, .. BitConverter.GetBytes(Self), .. BitConverter.GetBytes(loot.WorldGuid)]);
            SendVehicle(service, connection, [0x09, 0x07, 0, .. BitConverter.GetBytes(loot.WorldGuid)]);

            var gun = Assert.Single(inventory.Items.Values, item => item.DefinitionId == definition);
            byte[][] pickup = [.. recorder.Messages.Skip(before).Where(m => m.Direction == "s2c").Select(m => m.Bytes)];
            byte[] grant = Assert.Single(pickup, p => IsPickupItemAdd(p, gun.Guid));
            Assert.DoesNotContain(pickup, p => IsPickupItemDelete(p, gun.Guid));
            Assert.False(StateProperty<LootWorld>(connection, "Loot").TryGet(loot.WorldGuid, out _));
            Assert.Equal(ulong.MaxValue, BitConverter.ToUInt64(grant, 37));
            Assert.Equal(gun.LoadoutSlotId, BitConverter.ToUInt32(grant, 49));

            int add = Array.IndexOf(pickup, grant);
            int loadout = Array.FindIndex(pickup, p => p.Length > 2 && p[1] == 0x86 && p[2] == 0x05);
            Assert.True(loadout > add, "The item must exist before the loadout names it.");
            if (wieldFirstWeapon)
            {
                int manager = Array.FindIndex(pickup, p => p.Length > 2 && p[1] == 0xa0 && p[2] == 0x05);
                int binding = Array.FindLastIndex(pickup, p => IsHandBinding(p, gun.Guid));
                Assert.True(manager > add && binding > manager);
                Assert.Equal(gun.Guid, inventory.WieldedItemGuid);
                SelectReloadTestSlot(service, connection, SurvivorLoadout.Fists);
            }

            before = recorder.Messages.Count;
            SelectReloadTestSlot(service, connection, gun.LoadoutSlotId);
            byte[][] draw = [.. recorder.Messages.Skip(before).Where(m => m.Direction == "s2c").Select(m => m.Bytes)];
            Assert.DoesNotContain(draw, p => IsPickupItemDelete(p, gun.Guid) || IsPickupItemAdd(p, gun.Guid));
            int managerAt = Array.FindIndex(draw, p => p.Length > 2 && p[1] == 0xa0 && p[2] == 5);
            Assert.True(managerAt >= 0 && Array.FindLastIndex(draw, p => IsHandBinding(p, gun.Guid)) > managerAt);
            Assert.Contains(draw, p => IsHandBinding(p, gun.Guid)
                && System.Text.Encoding.UTF8.GetString(p).Contains(model));
            Assert.Equal(gun.Guid, inventory.WieldedItemGuid);
        }
        finally
        {
            connection.Disconnect();
            service.OnDisconnected(connection, DisconnectCause.ServerRequested);
        }
    }

    private static bool IsPickupItemAdd(byte[] packet, ulong itemGuid) =>
        packet.Length > 32 && packet[1] == 0x11 && packet[2] == 0x02
        && BitConverter.ToUInt64(packet, 24) == itemGuid;

    private static bool IsPickupItemDelete(byte[] packet, ulong itemGuid) =>
        packet.Length == 20 && packet[1] == 0x11 && packet[2] == 0x04
        && BitConverter.ToUInt64(packet, 12) == itemGuid;
}
