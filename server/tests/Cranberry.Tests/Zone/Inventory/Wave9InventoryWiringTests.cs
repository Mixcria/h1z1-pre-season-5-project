using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

/// <summary>
/// Wave 9, INVENTORY: what the wiring actually puts on the wire. <c>Wave9InventoryPortTests</c> pins
/// the model; this pins <c>ZoneService.HandleItemUseRequest</c>, which is where the risk is - the
/// packet order, the ground spawn and the shred busy window all live there and none of them is
/// reachable from a model-only test.
/// <para>
/// <b>D29, and it matters more here than anywhere else in the lane.</b> These assertions are over
/// the bytes the SERVER EMITTED. They prove what was sent and nothing whatever about what the client
/// did with it. Wave 8's door fix is the standing warning: the log said "3 kinematic, flags1 0x20"
/// and the owner walked through the door anyway.
/// </para>
/// </summary>
public sealed class Wave9InventoryWiringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedHelmetClickAndDragReachEquipAndPublishTheNewHeadSlot(bool drag)
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            Inventory = new InventoryOptions { StarterOutfit = [] },
        });
        SendClientIsReady(service, connection);
        SendInventoryWindowOpen(service, connection);
        var inventory = service.ForVehicleTest(connection).Inventory!;
        inventory.TryPickUp(2112, 1, out _);
        inventory.TryPickUp(2484, 1, out var hat);
        var helmet = inventory.CreateInstance(2172, 1);
        inventory.TryStow(helmet);
        int mark = recorder.Messages.Count;
        using var move = new PacketWriter();
        move.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        move.WriteByte(ContainerOpcodes.ContainerBase);
        move.WriteUInt16(ContainerOpcodes.MoveItemSub);
        move.WriteUInt64(drag ? PlayerInventory.EquippedContainerGuid : 0);
        move.WriteUInt64(inventory.CharacterGuid);
        move.WriteUInt64(helmet.Guid);
        move.WriteUInt64(inventory.CharacterGuid);
        move.WriteUInt32(1);
        move.WriteInt32(drag ? (int)SurvivorLoadout.Head : -1);

        service.OnMessage(connection, move.Written.ToArray());

        Assert.Same(helmet, inventory.EquipmentSlots[BodySlots.Head]);
        Assert.Equal(inventory.BaseBag!.Guid, hat!.ContainerGuid);
        byte[][] sent = [.. recorder.Messages.Skip(mark).Where(m => m.Direction == "s2c").Select(m => m.Bytes)];
        Assert.DoesNotContain(sent, p => p.Length > 3 && p[1] == ContainerOpcodes.ContainerBase
            && BitConverter.ToUInt16(p, 2) == ContainerError.SubOpcode);
        Assert.Contains(sent, p => p.Length > 2 && p[1] == 0x86 && p[2] == 4);
        Assert.Contains(sent, p => p.Length > 2 && p[1] == 0x94 && p[2] == 1);
        Assert.Contains(sent, p => p.Length > 28 && p[1] == 0x11 && p[2] == 2
            && BitConverter.ToUInt64(p, 24) == helmet.Guid);
        Assert.Contains(sent, p => p.Length > 28 && p[1] == 0x11 && p[2] == 2
            && BitConverter.ToUInt64(p, 24) == hat.Guid);
    }

    /// <summary>Item 2144, his refused shirt. <c>ITEM_CLASS</c> 25002, and NOT a loot-table row.</summary>
    private const uint HisRefusedShirt = 2144;

    /// <summary><c>Models.txt</c> row 9249, <c>Common_Props_Clothes_FoldedShirt.adr</c>.</summary>
    private const uint FoldedShirtModel = 9249;

    /// <summary>Its <c>NAME_ID</c>.</summary>
    private const uint HisRefusedShirtNameId = 8947;

    private const uint DropItemOption = 4;
    private const uint SalvageItemOption = 6;
    private const uint RemoveItemOption = 12;
    private const uint LootItemOption = 59;
    private const uint MotorcycleHelmet = 2168;
    private const uint MotorcycleHelmetGroundModel = 68;
    private const uint MotorcycleHelmetNameId = 1499;

    private sealed class RecordingRecorder : IPacketRecorder
    {
        public List<(string Direction, byte[] Bytes)> Messages { get; } = [];

        public void RecordSession(IPEndPoint remote, in SessionRequest request)
        {
        }

        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) =>
            Messages.Add((direction, bytes.ToArray()));

        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext)
        {
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;

        public void Log(TransportLogLevel level, string message)
        {
        }
    }

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder)
        Admit(ZoneOptions options, Action<Action>? post = null)
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options) { Post = post };
        var request = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
        var connection = new SoeConnection(
            new IPEndPoint(IPAddress.Loopback, 5555),
            in request,
            SessionSettings.WithSeed(1),
            service.OnSessionRequest(new IPEndPoint(IPAddress.Loopback, 5555), in request),
            service,
            new SilentLog(),
            (_, _) => { },
            now: 0);
        service.OnConnected(connection);

        using var writer = new PacketWriter();
        writer.WriteByte(GatewayLoginRequest.Opcode);
        writer.WriteUInt64(admission.Guid);
        writer.WriteString(admission.Ticket);
        writer.WriteString(GatewayLoginRequest.AugustProtocol);
        writer.WriteString(GatewayLoginRequest.AugustVersion);
        service.OnMessage(connection, writer.Written.ToArray());
        return (service, connection, recorder);
    }

    private static void SendClientIsReady(ZoneService service, SoeConnection connection)
    {
        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        service.OnMessage(connection, ready.Written.ToArray());
    }

    private static void SendInventoryWindowOpen(ZoneService service, SoeConnection connection)
    {
        using var open = new PacketWriter();
        open.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 1).ToByte());
        open.WriteByte(ZoneOpcodes.WallOfDataBase);
        open.WriteByte(0x05);
        open.WriteString("InventoryWindow");
        open.WriteString("open");
        open.WriteInt32(0);
        service.OnMessage(connection, open.Written.ToArray());
    }

    private static void SendInteractRequest(ZoneService service, SoeConnection connection, ulong targetGuid)
    {
        using var interact = new PacketWriter();
        interact.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        interact.WriteByte(ZoneOpcodes.CommandBase);
        interact.WriteUInt16(InteractRequest.SubOpcode);
        interact.WriteUInt64(targetGuid);
        service.OnMessage(connection, interact.Written.ToArray());
    }

    /// <summary>The 47-byte short form, which is what every one of the 21 captured packets but two is.</summary>
    private static void SendRequestUseItem(
        ZoneService service,
        SoeConnection connection,
        uint optionId,
        ulong characterGuid,
        ulong itemGuid)
    {
        using var use = new PacketWriter();
        use.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        use.WriteByte(ItemUseOpcodes.ItemsBase);
        use.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        use.WriteUInt32(1);                 // itemCount
        use.WriteUInt32(0);                 // reservedA
        use.WriteUInt32(optionId);
        use.WriteUInt64(characterGuid);
        use.WriteUInt64(characterGuid);     // source
        use.WriteUInt64(characterGuid);     // target
        use.WriteUInt64(itemGuid);
        use.WriteByte(1);                   // noParams
        service.OnMessage(connection, use.Written.ToArray());
    }

    private static void SendProximityClick(
        ZoneService service,
        SoeConnection connection,
        ulong characterGuid,
        ulong worldGuid)
    {
        using var use = new PacketWriter();
        use.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        use.WriteByte(ItemUseOpcodes.ItemsBase);
        use.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        use.WriteUInt32(1);
        use.WriteUInt32(0);
        use.WriteUInt32(LootItemOption);
        use.WriteUInt64(characterGuid);
        use.WriteUInt64(worldGuid);       // ItemObject/source represented by the proximity row
        use.WriteUInt64(characterGuid);
        use.WriteUInt64(worldGuid);       // nested item guid (the same guid in Cranberry's row)
        use.WriteByte(1);
        service.OnMessage(connection, use.Written.ToArray());
    }

    private static void SendProximityDrag(
        ZoneService service,
        SoeConnection connection,
        ulong characterGuid,
        ulong worldGuid)
    {
        using var move = new PacketWriter();
        move.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        move.WriteByte(ContainerOpcodes.ContainerBase);
        move.WriteUInt16(ContainerOpcodes.MoveItemSub);
        move.WriteUInt64(PlayerInventory.EquippedContainerGuid);
        move.WriteUInt64(worldGuid);
        move.WriteUInt64(worldGuid);
        move.WriteUInt64(characterGuid);
        move.WriteUInt32(1);
        move.WriteInt32((int)SurvivorLoadout.Head);
        service.OnMessage(connection, move.Written.ToArray());
    }

    private static void WaitForDevelopmentDrop(ConcurrentQueue<Action> pending, RecordingRecorder recorder)
    {
        // Watchdog callbacks share the queue with the sample loot timer. One posted
        // callback is not evidence that the item exists yet; under full-suite load
        // the old helper interacted before the spawn and then read the menu dress.
        long deadline = Environment.TickCount64 + 30_000;
        while (!Sent(recorder).Any(packet => packet.Length >= 10
            && packet[1] == ZoneOpcodes.AddLightweightNpc
            && BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(2)) == LootWorld.DefaultWorldGuidBase))
        {
            int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            Assert.True(remaining > 0 && SpinWait.SpinUntil(() => !pending.IsEmpty, remaining),
                "the development item's spawn was never emitted");
            Assert.True(pending.TryDequeue(out Action? work));
            work!();
        }
    }

    private static byte[][] Sent(RecordingRecorder recorder, int skip = 0) =>
        [.. recorder.Messages.Where(m => m.Direction == "s2c").Select(m => m.Bytes).Skip(skip)];

    private static int SentCount(RecordingRecorder recorder) =>
        recorder.Messages.Count(m => m.Direction == "s2c");

    private static string Label(byte[] packet) => packet[1] switch
    {
        ZoneOpcodes.EquipmentBase or ZoneOpcodes.ItemsBase or ZoneOpcodes.LoadoutsBase
            or ZoneOpcodes.CharacterBase or ZoneOpcodes.RecipeBase
            => $"{packet[1]:x2}.{packet[2]:x2}",
        ZoneOpcodes.ClientUpdateBase or ContainerOpcodes.ContainerBase
            => $"{packet[1]:x2}.{BitConverter.ToUInt16(packet, 2):x4}",
        _ => $"{packet[1]:x2}",
    };

    private static void AssertNoEquipmentSlotClears(IEnumerable<byte[]> packets) =>
        Assert.DoesNotContain(
            packets,
            packet => packet.Length > 2
                && packet[1] == ZoneOpcodes.EquipmentBase
                && packet[2] == UnsetCharacterEquipmentSlot.SubOpcode);

    private static (uint[] EquipmentSlots, uint[] AttachmentSlots) ReadDress(byte[] packet)
    {
        var reader = new PacketReader(packet.AsSpan(1));
        Assert.Equal(ZoneOpcodes.EquipmentBase, reader.ReadByte());
        Assert.Equal(SetCharacterEquipment.SubOpcode, reader.ReadByte());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt64();
        _ = reader.ReadUInt32();
        _ = reader.ReadString();
        _ = reader.ReadString();

        int equipmentCount = reader.ReadInt32();
        var equipment = new uint[equipmentCount];
        for (int index = 0; index < equipmentCount; index++)
        {
            uint key = reader.ReadUInt32();
            equipment[index] = reader.ReadUInt32();
            Assert.Equal(key, equipment[index]);
            _ = reader.ReadUInt64();
            _ = reader.ReadString();
            _ = reader.ReadString();
        }

        int attachmentCount = reader.ReadInt32();
        var attachments = new uint[attachmentCount];
        for (int index = 0; index < attachmentCount; index++)
        {
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            attachments[index] = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            int appearances = reader.ReadInt32();
            for (int appearance = 0; appearance < appearances; appearance++)
            {
                _ = reader.ReadUInt32();
            }

            _ = reader.ReadBool();
        }

        _ = reader.ReadBool();
        Assert.Equal(0, reader.Remaining);
        return (equipment, attachments);
    }

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder,
        ConcurrentQueue<Action> Pending, ulong CharacterGuid, ulong WorldGuid) GroundOne(
            uint itemDefinitionId, uint modelId, uint nameId)
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = itemDefinitionId,
                GroundLootModelId = modelId,
                GroundLootNameId = nameId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        WaitForDevelopmentDrop(pending, recorder);
        SendInventoryWindowOpen(service, connection);
        return (service, connection, recorder, pending, 0x1001, LootWorld.DefaultWorldGuidBase);
    }

    private static void AssertPickedUpIntoHead(byte[][] packets)
    {
        byte[] slot = Assert.Single(packets, packet =>
            packet.Length > 2
            && packet[1] == ZoneOpcodes.LoadoutsBase
            && packet[2] == SetLoadoutSlot.SubOpcode);

        Assert.Equal(SurvivorLoadout.Id,
            BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(11)));
        Assert.Equal(SurvivorLoadout.Head,
            BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(15)));
        Assert.Equal(MotorcycleHelmet,
            BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(19)));
        Assert.Contains(packets, packet => packet.Length > 2
            && packet[1] == RemovePlayer.Opcode
            && packet[2] == RemovePlayer.SubOpcode);
        Assert.DoesNotContain(packets, packet => packet.Length > 3
            && packet[1] == ContainerOpcodes.ContainerBase
            && BitConverter.ToUInt16(packet, 2) == ContainerError.SubOpcode);
    }

    /// <summary>
    /// Pick up one dev-spawned item and return the character guid and the instance guid the pickup
    /// bound, read off the re-dress - the one place on the wire that binds a body slot to an
    /// inventory instance guid.
    /// </summary>
    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder,
        ConcurrentQueue<Action> Pending, ulong CharacterGuid, ulong ItemGuid) HoldingOne(
            uint itemDefinitionId, uint modelId, uint nameId)
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = itemDefinitionId,
                GroundLootModelId = modelId,
                GroundLootNameId = nameId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        WaitForDevelopmentDrop(pending, recorder);
        // This fixture tests worn garments. Make room for the pickup instead of replacing a shirt.
        typeof(ZoneService).GetMethod("EnsureInventory", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(service, [connection, connection.Tag, "worn garment fixture"]);
        var inventory = Assert.IsType<PlayerInventory>(connection.Tag!.GetType()
            .GetProperty("Inventory")!.GetValue(connection.Tag));
        inventory.RemoveUnits(inventory.LoadoutSlots[SurvivorLoadout.Chest].Guid, 0);
        int beforePickup = SentCount(recorder);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);

        // Read the pickup's own binding, never a stale menu/bootstrap appearance.
        byte[] dress = Assert.Single(Sent(recorder, beforePickup), p =>
            p.Length > 2 && p[1] == ZoneOpcodes.EquipmentBase && p[2] == SetCharacterEquipment.SubOpcode);
        var reader = new PacketReader(dress.AsSpan(1));
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadUInt32();
        ulong characterGuid = reader.ReadUInt64();
        _ = reader.ReadUInt32();
        _ = reader.ReadString();
        _ = reader.ReadString();
        int slotCount = reader.ReadInt32();
        Assert.True(slotCount >= 1);
        Assert.True(InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact fact));
        ulong itemGuid = 0;
        for (int index = 0; index < slotCount; index++)
        {
            _ = reader.ReadUInt32();
            uint bodySlot = reader.ReadUInt32();
            ulong candidate = reader.ReadUInt64();
            _ = reader.ReadString();
            _ = reader.ReadString();
            if (bodySlot == fact.PassiveEquipSlotId)
            {
                itemGuid = candidate;
            }
        }
        Assert.NotEqual(0ul, itemGuid);

        return (service, connection, recorder, pending, characterGuid, itemGuid);
    }

    // =================================================================================

    /// <summary>
    /// <b>The owner's own defect, end to end.</b> Item 2144 is not a row of the loot tables, so wave
    /// 8 answered his drop with <c>Container.Error</c> and the log line <c>DropItem refused for item
    /// 2613 — this build has no ground actor for it</c>, three times in five minutes. It now becomes
    /// a folded shirt on the floor: <c>ItemDelete</c>, the loadout, the bag, and an
    /// <c>AddLightweightNpc</c> - never a <c>Container.Error</c>.
    /// </summary>
    [Fact]
    public void DroppingAGarmentTheLootTablesNeverCarriedNowReachesTheFloor()
    {
        var (service, connection, recorder, _, characterGuid, shirtGuid) =
            HoldingOne(HisRefusedShirt, FoldedShirtModel, HisRefusedShirtNameId);

        int before = SentCount(recorder);
        SendRequestUseItem(service, connection, DropItemOption, characterGuid, shirtGuid);
        byte[][] drop = Sent(recorder, before);
        string[] labels = [.. drop.Select(Label)];

        Assert.Contains($"{ZoneOpcodes.ClientUpdateBase:x2}.0004", labels);          // ItemDelete
        Assert.Contains($"{ContainerOpcodes.ContainerBase:x2}.0002", labels);        // full set: provider left
        Assert.Contains($"{ZoneOpcodes.AddLightweightNpc:x2}", labels);              // on the floor
        Assert.DoesNotContain($"{ContainerOpcodes.ContainerBase:x2}.0003", labels);  // NOT refused
        AssertNoEquipmentSlotClears(drop);

        byte[] redress = Assert.Single(drop, packet =>
            packet.Length > 2
            && packet[1] == ZoneOpcodes.EquipmentBase
            && packet[2] == SetCharacterEquipment.SubOpcode);
        (uint[] equipmentSlots, uint[] attachmentSlots) = ReadDress(redress);
        Assert.DoesNotContain(BodySlots.Chest, equipmentSlots);
        Assert.DoesNotContain(BodySlots.Chest, attachmentSlots);
    }

    [Fact]
    public void ClickingAProximityRowPicksTheHelmetUpIntoItsHeadSlot()
    {
        var (service, connection, recorder, _, characterGuid, worldGuid) = GroundOne(
            MotorcycleHelmet, MotorcycleHelmetGroundModel, MotorcycleHelmetNameId);

        int before = SentCount(recorder);
        SendProximityClick(service, connection, characterGuid, worldGuid);

        AssertPickedUpIntoHead(Sent(recorder, before));
    }

    [Fact]
    public void DraggingAProximityRowUsesTheAugustMovePacketAndCorrectEquipmentSlot()
    {
        var (service, connection, recorder, _, characterGuid, worldGuid) = GroundOne(
            MotorcycleHelmet, MotorcycleHelmetGroundModel, MotorcycleHelmetNameId);

        int before = SentCount(recorder);
        SendProximityDrag(service, connection, characterGuid, worldGuid);

        AssertPickedUpIntoHead(Sent(recorder, before));
    }

    /// <summary>
    /// A shred, end to end. <c>ShredTable.Shred</c> has been complete and green since wave 6 and had
    /// no caller: the owner right-clicked Shred four times on 30 Aug and got
    /// <c>SalvageItem ... no server behaviour is derived</c> each time.
    /// </summary>
    [Fact]
    public void ShreddingIsAnsweredAndGrantsTheYield()
    {
        var (service, connection, recorder, pending, characterGuid, shirtGuid) =
            HoldingOne(HisRefusedShirt, FoldedShirtModel, HisRefusedShirtNameId);

        int before = SentCount(recorder);
        SendRequestUseItem(service, connection, SalvageItemOption, characterGuid, shirtGuid);
        byte[][] armed = Sent(recorder, before);
        byte[] interaction = Assert.Single(armed, packet =>
            packet.Length > 2
            && packet[1] == ZoneOpcodes.CharacterStateBase
            && packet[2] == InteractionStart.SubOpcode);
        Assert.DoesNotContain(
            armed,
            packet => packet.Length > 3
                && packet[1] == ZoneOpcodes.ClientUpdateBase
                && BitConverter.ToUInt16(packet, 2) == ItemDelete.SubOpcode);
        Assert.Equal(1000u, BitConverter.ToUInt32(interaction, 11));
        Assert.Equal(10u, BitConverter.ToUInt32(interaction, 43));

        Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000));
        while (!Sent(recorder, before).Any(packet => packet.Length > 3
            && packet[1] == ZoneOpcodes.ClientUpdateBase
            && BitConverter.ToUInt16(packet, 2) == ItemDelete.SubOpcode))
        {
            Assert.True(pending.TryDequeue(out Action? work));
            work!();
            if (pending.IsEmpty)
            {
                Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000));
            }
        }

        byte[][] shred = Sent(recorder, before);
        string[] labels = [.. shred.Select(Label)];

        Assert.Contains($"{ZoneOpcodes.ClientUpdateBase:x2}.0004", labels);          // the shirt goes
        Assert.Contains($"{ZoneOpcodes.ClientUpdateBase:x2}.0002", labels);          // the scrap arrives
        Assert.Contains($"{ZoneOpcodes.LoadoutsBase:x2}.04", labels);                // it was WORN
        AssertNoEquipmentSlotClears(shred);
    }

    /// <summary>
    /// The client locks the character out for the option's own <c>BUSY_MSEC</c>, so a second
    /// <c>SalvageItem</c> arriving inside that window did not come from the context menu. It is
    /// refused in the client's own vocabulary rather than shredding a second item.
    /// </summary>
    [Fact]
    public void ASecondShredInsideTheBusyWindowIsRefused()
    {
        var (service, connection, recorder, _, characterGuid, shirtGuid) =
            HoldingOne(HisRefusedShirt, FoldedShirtModel, HisRefusedShirtNameId);

        SendRequestUseItem(service, connection, SalvageItemOption, characterGuid, shirtGuid);

        int before = SentCount(recorder);
        SendRequestUseItem(service, connection, SalvageItemOption, characterGuid, shirtGuid);
        string[] labels = [.. Sent(recorder, before).Select(Label)];

        Assert.Contains($"{ContainerOpcodes.ContainerBase:x2}.0003", labels);        // Container.Error
        Assert.DoesNotContain($"{ZoneOpcodes.ClientUpdateBase:x2}.0004", labels);    // nothing deleted
    }

    /// <summary>
    /// <c>RemoveItem</c> (12) is the most-offered option in the August build and used to send nothing
    /// at all. A worn item comes off into the bag: the tile is re-announced where it now is (his own
    /// delete-then-add order), the loadout is rebuilt and the bag is repainted.
    /// </summary>
    [Fact]
    public void RemoveItemTakesAWornGarmentOffIntoTheBag()
    {
        var (service, connection, recorder, _, characterGuid, shirtGuid) =
            HoldingOne(HisRefusedShirt, FoldedShirtModel, HisRefusedShirtNameId);

        int before = SentCount(recorder);
        SendRequestUseItem(service, connection, RemoveItemOption, characterGuid, shirtGuid);
        byte[][] removed = Sent(recorder, before);
        string[] labels = [.. removed.Select(Label)];

        Assert.NotEmpty(removed);
        Assert.Contains($"{ZoneOpcodes.ClientUpdateBase:x2}.0004", labels);          // delete
        Assert.Contains($"{ZoneOpcodes.ClientUpdateBase:x2}.0002", labels);          // then re-add
        Assert.Contains($"{ZoneOpcodes.LoadoutsBase:x2}.04", labels);                // SetLoadoutSlots
        Assert.Contains($"{ContainerOpcodes.ContainerBase:x2}.0002", labels);        // full set: provider left

        // Guard 2: never 94 03. The re-dress is the full 94 01 list rebuilt from the model.
        AssertNoEquipmentSlotClears(removed);
    }

    /// <summary>
    /// A verb this server will not perform answers with a NAMED <c>Container.Error</c> rather than
    /// with silence - his rule, and the difference between a menu entry that explains itself and one
    /// that looks broken. <c>UnloadWeapon</c> on a shirt is the cheapest case to drive.
    /// </summary>
    [Fact]
    public void AVerbThisServerWillNotPerformStillAnswers()
    {
        var (service, connection, recorder, _, characterGuid, shirtGuid) =
            HoldingOne(HisRefusedShirt, FoldedShirtModel, HisRefusedShirtNameId);

        // Option 5, PlaceItem - a real ItemUseOptions row, and one this server refuses by name.
        int before = SentCount(recorder);
        SendRequestUseItem(service, connection, 5, characterGuid, shirtGuid);
        string[] labels = [.. Sent(recorder, before).Select(Label)];

        Assert.Contains($"{ContainerOpcodes.ContainerBase:x2}.0003", labels);
        Assert.DoesNotContain($"{ZoneOpcodes.ClientUpdateBase:x2}.0004", labels);
    }
}
