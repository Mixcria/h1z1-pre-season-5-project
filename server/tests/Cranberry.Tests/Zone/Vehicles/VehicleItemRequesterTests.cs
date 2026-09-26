using Cranberry.Protocol;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Theory]
    [InlineData("remove")]
    [InlineData("loot")]
    [InlineData("equip")]
    public void VehicleItemUse_MismatchedRequesterCannotMutateAnExternalInventory(string verb)
    {
        var (service, connection, recorder) = Admit();
        try
        {
            service.Post = _ => { }; // Component completion uses the existing explicit clock seam.
            var session = service.ForVehicleTest(connection);
            var car = session.EnterMatchWithCar();
            var player = InstallRemovalInventory(connection.Tag!, session.Guid);
            uint option, definition;
            ulong itemGuid, source, target;
            if (verb == "equip")
            {
                var installed = Assert.Single(car.Inventory.Items, i => i.SlotId == 33);
                Assert.True(car.Inventory.TakeTo(player, installed.ItemGuid, 1, out var carried));
                Assert.NotNull(carried);
                definition = carried.DefinitionId;
                itemGuid = carried.Guid;
                source = session.Guid;
                target = car.Guid;
                option = 60;
            }
            else
            {
                var vehicleItem = verb == "remove"
                    ? Assert.Single(car.Inventory.Items, i => i.SlotId == 33)
                    : Assert.Single(car.Inventory.Items, i => i.DefinitionId == 73);
                definition = vehicleItem.DefinitionId;
                itemGuid = vehicleItem.ItemGuid;
                source = car.Guid;
                target = session.Guid;
                option = verb == "remove" ? 12u : 59u;
            }

            var beforeVehicle = car.Inventory.Items.OrderBy(i => i.ItemGuid).ToArray();
            var beforePlayer = RequesterInventorySnapshot(player);
            long playerUnits = player.Items.Values.Where(i => i.DefinitionId == definition).Sum(i => (long)i.Count);
            int mark = recorder.Sent.Count;
            session.Deliver(VehicleItemRequest(session.Guid + 1, target, source, itemGuid, option));

            Assert.Equal(beforeVehicle, car.Inventory.Items.OrderBy(i => i.ItemGuid).ToArray());
            Assert.Equal(beforePlayer, RequesterInventorySnapshot(player));
            Assert.Null(session.ComponentRemovalDueMs);
            byte[] refusal = Assert.Single(From(recorder, mark));
            Assert.Equal(ContainerError.Length + 1, refusal.Length); // Gateway header retained by this recorder.
            Assert.Equal(0xc8, refusal[1]);
            Assert.Equal(3, refusal[2]);
            Assert.Equal((uint)ContainerErrorCode.InteractionValidationFailed, BitConverter.ToUInt32(refusal, 12));

            // The same target/source/item operation still works for the authenticated
            // requester. Rejection must not poison inventory or cast ownership.
            session.Deliver(VehicleItemRequest(session.Guid, target, source, itemGuid, option));
            if (verb == "remove")
            {
                long due = Assert.IsType<long>(session.ComponentRemovalDueMs);
                Assert.True(car.Inventory.HasSlot(33));
                session.Deliver(VehicleItemRequest(session.Guid + 1, target, source, itemGuid, option));
                Assert.Equal(due, session.ComponentRemovalDueMs);
                session.CompleteComponentRemoval(due);
                Assert.Null(session.ComponentRemovalDueMs);
                Assert.False(car.Inventory.HasSlot(33));
                Assert.Equal(playerUnits + 1, player.Items.Values.Where(i => i.DefinitionId == definition).Sum(i => (long)i.Count));
            }
            else if (verb == "loot")
            {
                Assert.Null(session.ComponentRemovalDueMs);
                Assert.False(car.Inventory.TryGet(itemGuid, out _));
                Assert.Equal(playerUnits + 1, player.Items.Values.Where(i => i.DefinitionId == definition).Sum(i => (long)i.Count));
            }
            else
            {
                Assert.Null(session.ComponentRemovalDueMs);
                Assert.True(car.Inventory.HasSlot(33));
                Assert.False(player.Items.ContainsKey(itemGuid));
            }
        }
        finally { connection.Disconnect(); }
    }

    private static (ulong Guid, uint Definition, uint Count, uint Slot, ulong Container)[] RequesterInventorySnapshot(
        PlayerInventory inventory) => inventory.Items.Values
        .Select(i => (i.Guid, i.DefinitionId, i.Count, i.LoadoutSlotId, i.ContainerGuid)).OrderBy(i => i.Guid).ToArray();

    private static byte[] VehicleItemRequest(ulong requester, ulong target, ulong source, ulong item, uint option)
    {
        using var packet = new PacketWriter();
        packet.WriteByte(0xac); packet.WriteByte(0x2c);
        packet.WriteUInt32(1); packet.WriteUInt32(0); packet.WriteUInt32(option);
        packet.WriteUInt64(requester); packet.WriteUInt64(target); packet.WriteUInt64(source);
        packet.WriteUInt64(item); packet.WriteByte(1);
        return packet.Written.ToArray();
    }
}
