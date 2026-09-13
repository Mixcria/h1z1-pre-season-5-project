using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Tests.Zone.Combat;

namespace Cranberry.Tests.Zone;

public sealed partial class StarterWeaponDrawTests
{
    [Fact]
    public void DrawingAndUnloadingAPumpSynchronizesItsCounterBeforeTheNextReload()
    {
        var (service, connection, recorder, work) = World();
        try
        {
            Land(service, connection, recorder, work);
            var inventory = StateProperty<PlayerInventory>(connection, "Inventory");
            var combat = StateProperty<SessionCombat>(connection, "Combat");
            var pump = inventory.CreateInstance(1374, 1);
            inventory.BindLoadout(pump, SurvivorLoadout.Wheel2, 77);
            Assert.True(inventory.TryStow(inventory.CreateInstance(1511, 4)));
            combat.Shooter.DeclareWeapon(pump.Guid, 1374, 2);
            for (int count = 0; count < 11; count++) combat.Shooter.Load(pump.Guid, 0);
            combat.Shooter.SpendDurability(pump.Guid, 5, 2000);

            int firstDraw = recorder.Messages.Count;
            SelectReloadTestSlot(service, connection, SurvivorLoadout.Wheel2);
            // This fixture weapon has never been granted to the client. Its first draw must
            // still deliver the item/fire groups; subsequent draws preserve that component.
            Assert.Contains(recorder.Messages.Skip(firstDraw), m => m.Direction == "s2c"
                && m.Bytes.Length > 20 && m.Bytes[1] == 0x11 && m.Bytes[2] == 2);
            SelectReloadTestSlot(service, connection, SurvivorLoadout.Fists);
            int before = recorder.Messages.Count;
            SelectReloadTestSlot(service, connection, SurvivorLoadout.Wheel2);
            byte[][] drawn = [.. recorder.Messages.Skip(before)
                .Where(m => m.Direction == "s2c").Select(m => m.Bytes)];
            int add = Array.FindIndex(drawn, p => p.Length > 20 && p[1] == 0x11 && p[2] == 2);
            int binding = Array.FindIndex(drawn, p => IsHandBinding(p, pump.Guid));
            int sync = Array.FindIndex(drawn, p => IsReloadFor(p, pump.Guid));
            Assert.Equal(-1, add); // drawing preserves the client weapon and its magazine
            Assert.True(binding >= 0 && sync > binding);
            byte[] snapshot = drawn[sync][1..];
            Assert.Equal(11ul, BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(26)));
            Assert.Equal(0u, BitConverter.ToUInt32(snapshot, 14));
            Assert.Equal(2, combat.Shooter.AmmoOf(pump.Guid));
            Assert.Equal(1995, combat.Shooter.DurabilityOf(pump.Guid));
            Assert.Equal(11ul, combat.Shooter.ReloadCountOf(pump.Guid));

            var client = new AugustPumpReloadClient(2); // Verify synchronization even from a stale counter.
            client.Apply(snapshot);
            client.BeginReload();
            before = recorder.Messages.Count;
            SendVehicle(service, connection, ShootingPacketBuilder.ReloadRequest(pump.Guid));
            byte[] acknowledgement = Assert.Single(recorder.Messages.Skip(before),
                m => m.Direction == "s2c" && IsReloadFor(m.Bytes, pump.Guid)).Bytes[1..];
            client.Apply(acknowledgement);
            Assert.Equal(12ul, client.Acknowledged);
            Assert.Equal(4, client.CachedReserve);
            Assert.True(client.CompleteShell());
            Assert.Equal(2, combat.Shooter.AmmoOf(pump.Guid)); // prediction has not spent server ammo
            Assert.NotNull(combat.Reload);

            var priorReload = combat.Reload;
            before = recorder.Messages.Count;
            using (var unload = new PacketWriter())
            {
                unload.WriteByte(ItemUseOpcodes.ItemsBase);
                unload.WriteByte(ItemUseOpcodes.RequestUseItemSub);
                unload.WriteUInt32(1);
                unload.WriteUInt32(0);
                unload.WriteUInt32(7); // UnloadWeapon, a native item-use option
                unload.WriteUInt64(Self);
                unload.WriteUInt64(Self);
                unload.WriteUInt64(Self);
                unload.WriteUInt64(pump.Guid);
                unload.WriteByte(1);
                SendVehicle(service, connection, unload.Written.ToArray());
            }
            byte[][] unloadPackets = [.. recorder.Messages.Skip(before)
                .Where(m => m.Direction == "s2c").Select(m => m.Bytes)];
            int unloadAdd = Array.FindIndex(unloadPackets,
                p => p.Length > 20 && p[1] == 0x11 && p[2] == 2);
            int unloadSync = Array.FindLastIndex(unloadPackets, p => IsReloadFor(p, pump.Guid));
            Assert.True(unloadAdd >= 0 && unloadSync > unloadAdd);
            Assert.Equal(0, combat.Shooter.AmmoOf(pump.Guid));
            Assert.Null(combat.Reload);
            var ammo = new PlayerAmmoContext(inventory, Self, AmmoOptions.Default);
            Assert.Equal(6, ammo.Count(1511));
            Assert.Null(WeaponFireArm.AdvanceReload(combat, priorReload!, priorReload!.DueAtMs,
                pump.Guid, inventory));
            var afterUnload = new AugustPumpReloadClient(0);
            afterUnload.Apply(unloadPackets[unloadSync][1..]);
            afterUnload.BeginReload();
            before = recorder.Messages.Count;
            SendVehicle(service, connection, ShootingPacketBuilder.ReloadRequest(pump.Guid));
            byte[] reloadAgain = Assert.Single(recorder.Messages.Skip(before),
                m => m.Direction == "s2c" && IsReloadFor(m.Bytes, pump.Guid)).Bytes[1..];
            afterUnload.Apply(reloadAgain);
            Assert.Equal(14ul, afterUnload.Acknowledged);
            Assert.Equal(6, afterUnload.CachedReserve);
            Assert.True(afterUnload.CompleteShell());
        }
        finally
        {
            connection.Disconnect();
            service.OnDisconnected(connection, DisconnectCause.ServerRequested);
        }
    }

    private static bool IsReloadFor(byte[] packet, ulong gun) => packet.Length == 35
        && packet[1] == 0x82 && packet[6] == 0x08 && BitConverter.ToUInt64(packet, 7) == gun;

    private static void SelectReloadTestSlot(ZoneService service, SoeConnection connection, uint slot)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(SelectLoadoutSlotRequest.Opcode);
        writer.WriteByte(SelectLoadoutSlotRequest.SubOpcode);
        writer.WriteUInt32(0);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(1234);
        SendVehicle(service, connection, writer.Written.ToArray());
    }
}
