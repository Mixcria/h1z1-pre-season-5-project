using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    public static IEnumerable<object[]> RemovableParts()
    {
        foreach (uint family in new uint[] { 1, 2, 3, 5 })
            foreach (uint slot in new uint[] { 17, 18, 19, 33, 34, 36 })
                foreach (bool menu in new[] { false, true }) yield return [family, slot, menu];
        yield return [3u, 35u, false];
    }

    [Theory]
    [MemberData(nameof(RemovableParts))]
    public void InstalledComponentsWaitTenSecondsForBothDragAndRemoveMenu(uint family, uint slot, bool menu)
    {
        var (service, connection, recorder) = Admit();
        service.Post = _ => { }; // The seam advances the production completion clock explicitly.
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family);
        var player = InstallRemovalInventory(connection.Tag!, session.Guid);
        var part = Assert.Single(car.Inventory.Items, i => i.SlotId == slot);
        int mark = recorder.Sent.Count;
        if (menu)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(0xac); writer.WriteByte(0x2c);
            writer.WriteUInt32(1); writer.WriteUInt32(0); writer.WriteUInt32(12);
            writer.WriteUInt64(session.Guid); writer.WriteUInt64(car.Guid); writer.WriteUInt64(session.Guid);
            writer.WriteUInt64(part.ItemGuid); writer.WriteByte(1);
            session.Deliver(writer.Written.ToArray());
        }
        else session.Deliver(ComponentMove(car.Guid, part.ItemGuid, session.Guid, player.BaseBag!.Guid));
        long due = Assert.IsType<long>(session.ComponentRemovalDueMs);
        byte[] start = Assert.Single(Sub8(From(recorder, mark), 0xcf, 2));
        Assert.Equal(10_000, BitConverter.ToInt32(start, 11));
        Assert.True(car.Inventory.HasSlot(slot));
        Assert.True(car.EngineOn);
        session.Deliver(ComponentMove(car.Guid, part.ItemGuid, session.Guid, player.BaseBag!.Guid));
        Assert.Equal(due, session.ComponentRemovalDueMs); // duplicate request cannot restart/bypass the timer
        session.CompleteComponentRemoval(due - 1);
        Assert.True(car.Inventory.HasSlot(slot));
        session.CompleteComponentRemoval(due);
        Assert.Null(session.ComponentRemovalDueMs);
        Assert.False(car.Inventory.HasSlot(slot));
        Assert.Single(player.Items.Values, i => i.DefinitionId == part.DefinitionId);
        Assert.Equal(slot is not (19 or 33 or 34), car.EngineOn);
        session.CompleteComponentRemoval(due + 1);
        Assert.Single(player.Items.Values, i => i.DefinitionId == part.DefinitionId);
        connection.Disconnect();
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("source-moved")]
    [InlineData("full-bag")]
    [InlineData("world")]
    [InlineData("dead")]
    [InlineData("wreck")]
    public void RemovalRechecksAccessItemCapacityAndLifeAtCompletion(string change)
    {
        var (service, connection, _) = Admit();
        service.Post = _ => { };
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        var player = InstallRemovalInventory(connection.Tag!, session.Guid);
        var battery = Assert.Single(car.Inventory.Items, i => i.SlotId == 33);
        session.Deliver(ComponentMove(car.Guid, battery.ItemGuid, session.Guid, player.BaseBag!.Guid));
        long due = Assert.IsType<long>(session.ComponentRemovalDueMs);
        void Set(string name, object value) => connection.Tag!.GetType().GetProperty(name)!.SetValue(connection.Tag, value);
        switch (change)
        {
            case "exit": Assert.True(session.Exit()); break;
            case "source-moved": Assert.True(car.Inventory.MoveWithin(battery.ItemGuid, 1, car.Inventory.CargoGuid, -1)); break;
            case "full-bag": player.TryPickUp(1429, (uint)((player.Capacity.Max - player.Capacity.Used) / 2), out _); break;
            case "world": Set("WorldGeneration", 900); break;
            case "dead": Set("DeathSent", true); break;
            case "wreck": car.Health = 0; break;
        }
        session.CompleteComponentRemoval(due);
        Assert.True(car.Inventory.TryGet(battery.ItemGuid, out _));
        Assert.DoesNotContain(player.Items.Values, i => i.DefinitionId == battery.DefinitionId);
        connection.Disconnect();
    }

    [Fact]
    public void MovingAnInstalledPartIntoVehicleCargoIsTimedButLooseCargoAndReinstallationAreImmediate()
    {
        var (service, connection, _) = Admit();
        service.Post = _ => { };
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        var player = InstallRemovalInventory(connection.Tag!, session.Guid);
        var plug = Assert.Single(car.Inventory.Items, i => i.SlotId == 34);
        session.Deliver(ComponentMove(car.Guid, plug.ItemGuid, car.Guid, car.Inventory.CargoGuid));
        Assert.True(car.Inventory.HasSlot(34));
        session.CompleteComponentRemoval(Assert.IsType<long>(session.ComponentRemovalDueMs));
        Assert.False(car.Inventory.HasSlot(34));
        Assert.False(car.EngineOn);
        session.Deliver(ComponentMove(car.Guid, plug.ItemGuid, car.Guid, PlayerInventory.EquippedContainerGuid, 34));
        Assert.Null(session.ComponentRemovalDueMs);
        Assert.True(car.Inventory.HasSlot(34));
        Assert.False(car.EngineOn); // replacing parts does not start the engine
        var fuel = Assert.Single(car.Inventory.Items, i => i.DefinitionId == 73);
        session.Deliver(ComponentMove(car.Guid, fuel.ItemGuid, session.Guid, player.BaseBag!.Guid));
        Assert.Null(session.ComponentRemovalDueMs);
        Assert.False(car.Inventory.TryGet(fuel.ItemGuid, out _));
        connection.Disconnect();
    }

    [Theory]
    [InlineData(1u, 90001u, 100042u)]
    [InlineData(2u, 90062u, 110237u)]
    [InlineData(3u, 90063u, 110260u)]
    [InlineData(5u, 90187u, 120649u)]
    public void RealEntryWaitsForMotorInputAndEnablesTheDriverOnceWithAServerOrigin(uint family, uint client, uint server)
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(family);
        Assert.True(session.Exit());
        car.LastInteractionMs = long.MinValue;
        int mark = recorder.Sent.Count;
        Assert.True(session.Enter(car.Guid));
        session.RefreshInventory(car);
        Assert.False(car.EngineOn);
        Assert.Empty(Sub8(From(recorder, mark), 0xa0, 1));
        Assert.DoesNotContain(Sub8(From(recorder, mark), 0x88, 0x1b), p => p[^1] == 1);
        var add = MotorAdd(session.Guid, car.Guid, client, server);
        session.Deliver(add);
        session.Deliver(add);
        Assert.True(car.EngineOn);
        var engine = Assert.Single(Sub8(From(recorder, mark), 0x88, 0x1b));
        // FUN_140c95340 skips a local-player origin, which also prevents driving.
        Assert.Equal(0ul, BitConverter.ToUInt64(engine, 3));
        Assert.NotEqual(session.Guid, BitConverter.ToUInt64(engine, 3));
        Assert.Empty(Sub8(From(recorder, mark), 0xa0, 1));
        connection.Disconnect();
    }

    [Fact]
    public void ExitingAnAlreadyStoppedCarSeedsTheRemoteControllerEvenWithoutAnotherClientReport()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        var position = new Vector3(100, 20, 30);
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid, position, 0, Environment.TickCount64, out _));
        using var writer = new PacketWriter();
        writer.WriteByte(0x90); ClientVarInt.Write(writer, car.TransientId);
        writer.WriteRaw(VehiclePoseRelay.Parked(car).MovementPayload.Span);
        session.Deliver(writer.Written.ToArray(), 3);
        int mark = recorder.Sent.Count;
        Assert.True(session.Exit());
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        var packets = From(recorder, mark).ToArray();
        var release = Assert.Single(Sub8(packets, 0x0f, 0x3b));
        AssertNativeRestingHandoff(packets, position);
        var rest = packets.First(p => p[1] == 0x78);
        Assert.True(Array.IndexOf(packets, release) < Array.IndexOf(packets, rest));
        Assert.Equal(position, car.Position);
        connection.Disconnect();
    }

    private static PlayerInventory InstallRemovalInventory(object state, ulong guid)
    {
        ulong next = 0x3100_0000_0000_0000;
        var inventory = new PlayerInventory(guid, () => ++next);
        inventory.Bootstrap();
        inventory.TryPickUp(2124, 1, out _);
        state.GetType().GetProperty("Inventory")!.SetValue(state, inventory);
        return inventory;
    }

    private static byte[] ComponentMove(ulong source, ulong item, ulong target, ulong container, int slot = -1)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xc8); writer.WriteUInt16(1); writer.WriteUInt64(container);
        writer.WriteUInt64(source); writer.WriteUInt64(item); writer.WriteUInt64(target);
        writer.WriteUInt32(1); writer.WriteInt32(slot);
        return writer.Written.ToArray();
    }

    private static byte[] MotorAdd(ulong driver, ulong vehicle, uint clientEffect, uint serverEffect)
    {
        byte[] bytes = Convert.FromHexString("9E0101000000905F0100B7860100000000002110000000000000010000000000000000000000110000000000004600000000000000000000000000000000000000000000000001");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), clientEffect);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), serverEffect);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18), driver);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(38), vehicle);
        return bytes;
    }
}
