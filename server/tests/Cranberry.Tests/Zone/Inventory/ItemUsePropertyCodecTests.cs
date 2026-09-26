using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

// Synthetic layout cases derived from the exact August readers/writers documented in
// docs/item-use-properties-20260926.md. Captured client vectors remain in ItemUseRequestTests.
public sealed class ItemUsePropertyCodecTests
{
    private const ulong Character = 0x1001;
    private const ulong Item = 0x3100000000000009;

    [Fact]
    public void NativeGuidOrderSeparatesTheRequesterTargetAndContainerOwner()
    {
        byte[] packet = Packet(1);
        const ulong target = 0x4600000000000001;
        const ulong owner = 0x1002;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(22), target);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(30), owner);

        RequestUseItem parsed = RequestUseItem.Parse(packet);

        Assert.Equal(Character, parsed.CharacterGuid);
        Assert.Equal(target, parsed.TargetCharacterGuid);
        Assert.Equal(owner, parsed.SourceCharacterGuid);
        Assert.Equal(Item, parsed.ItemGuid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void EveryNonzeroFlagEndsThePropertyBlock(int flag)
    {
        byte[] packet = Packet((byte)flag);
        RequestUseItem parsed = RequestUseItem.Parse(packet);

        Assert.Equal(47, packet.Length);
        Assert.True(parsed.Simple);
        Assert.Equal(0u, parsed.Count);
        Assert.Empty(parsed.Parameters!);
        Assert.Equal(0, parsed.TrailingBytes);
    }

    [Fact]
    public void TheReaderAcceptsFiveEmptyListsEvenThoughTheWriterNormallyUsesSimple()
    {
        byte[] packet = EmptyLists();
        RequestUseItem parsed = RequestUseItem.Parse(packet);

        Assert.Equal(67, packet.Length);
        Assert.False(parsed.Simple);
        Assert.Equal(0u, parsed.Count);
        Assert.Empty(parsed.Parameters!);
        Assert.Equal(0, parsed.TrailingBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(120)]
    public void QuantityComesFromPropertyOneAndKeepsTheLegacyAliases(int quantity)
    {
        byte[] packet = Quantity((uint)quantity);
        RequestUseItem parsed = RequestUseItem.Parse(packet);

        Assert.Equal(75, packet.Length);
        Assert.Equal(1u, parsed.ItemCount); // Fixed header field, not the requested quantity.
        Assert.Equal(0u, parsed.ReservedA);
        Assert.Equal(1ul, parsed.UnknownA);
        Assert.Equal((uint)quantity, parsed.Count);
        Assert.Equal(parsed.Count, parsed.StackAtClick);
        Assert.Equal(quantity != 0, parsed.HasStackAtClick);
        Assert.Equal(new ItemUseParameter(1, (uint)quantity), Assert.Single(parsed.Parameters!));
        Assert.Equal(0, parsed.TrailingBytes);
    }

    [Fact]
    public void AllFivePropertyTypesAreConsumedWithoutChangingTheFirstListQuantity()
    {
        RequestUseItem parsed = RequestUseItem.Parse(AllPropertyTypes());

        Assert.Equal(new[] { new ItemUseParameter(7, 999), new ItemUseParameter(1, 3) }, parsed.Parameters);
        Assert.Equal(3u, parsed.Count);
        Assert.Equal(0, parsed.TrailingBytes);
    }

    [Fact]
    public void DuplicateQuantityPropertiesKeepTheExistingFirstMatchRuleIncludingZero()
    {
        byte[] packet = Packet(0, writer =>
        {
            writer.WriteInt32(3);
            writer.WriteUInt32(7); writer.WriteUInt32(999);
            writer.WriteUInt32(1); writer.WriteUInt32(0);
            writer.WriteUInt32(1); writer.WriteUInt32(13);
            WriteEmptyLists(writer, 4);
        });

        RequestUseItem parsed = RequestUseItem.Parse(packet);

        Assert.Equal(3, parsed.Parameters!.Count);
        Assert.Equal(0u, parsed.Count);
        Assert.Equal(0u, parsed.StackAtClick);
        Assert.False(parsed.HasStackAtClick);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativeListCountsUseNativeZeroIterationAndStillReadAllFiveCounts(int count)
    {
        byte[] packet = Packet(0, writer =>
        {
            for (int i = 0; i < 5; i++) writer.WriteInt32(count);
            writer.WriteByte(0xa5);
        });

        RequestUseItem parsed = RequestUseItem.Parse(packet);

        Assert.Empty(parsed.Parameters!);
        Assert.Equal(0u, parsed.Count);
        Assert.Equal(1, parsed.TrailingBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HugePositiveCountsAreRejectedFromTheAvailableLength(int list)
    {
        byte[] packet = EmptyLists();
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(47 + 4 * list), int.MaxValue);

        Assert.Throws<PacketFormatException>(() => RequestUseItem.Parse(packet));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void NegativeOrUnavailableStringLengthsAreRejected(int byteLength)
    {
        byte[] packet = Packet(0, writer =>
        {
            WriteEmptyLists(writer, 4);
            writer.WriteInt32(1);
            writer.WriteUInt32(9);
            writer.WriteInt32(byteLength);
        });

        Assert.Throws<PacketFormatException>(() => RequestUseItem.Parse(packet));
    }

    [Fact]
    public void EveryTruncatedPrefixOfTheCompleteFormsIsRejected()
    {
        byte[][] complete = [Packet(1), EmptyLists(), Quantity(3), AllPropertyTypes()];
        foreach (byte[] packet in complete)
        {
            for (int length = 0; length < packet.Length; length++)
            {
                byte[] truncated = packet[..length];
                Assert.Throws<PacketFormatException>(() => RequestUseItem.Parse(truncated));
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrailersAreReportedAfterTheActualPropertyBlock(bool simple)
    {
        byte[] packet = simple ? Packet(1) : AllPropertyTypes();
        byte[] withTrailer = [.. packet, 0xff, 0, 0xfe, 0x7f];

        RequestUseItem parsed = RequestUseItem.Parse(withTrailer);

        Assert.Equal(4, parsed.TrailingBytes);
        Assert.Equal(simple ? 0u : 3u, parsed.Count);
    }

    [Fact]
    public void MalformedPropertiesCannotDropInventoryAndTheSameGatewayAcceptsTheNextValidAction()
    {
        using var gateway = new Gateway();
        InventoryItemInstance ammunition = Assert.Single(gateway.Inventory.Items.Values,
            item => item.DefinitionId == 1429);
        byte[] valid = Quantity(3, ammunition.Guid);

        // Missing first count, missing quantity, and each absent/truncated later count used to
        // reach the action resolver. Some were interpreted as dropping the entire stack.
        foreach (int length in new[] { 47, 51, 59, 63, 67, 71, 74 })
        {
            gateway.Send(valid[..length]);
            Assert.Equal(30u, ammunition.Count);
            Assert.Same(ammunition, gateway.Inventory.Items[ammunition.Guid]);
            Assert.Empty(gateway.Loot.Items);
            Assert.Empty(gateway.Sent);
            Assert.Equal(ConnectionState.Open, gateway.Connection.State);
        }
        Assert.Equal(7, gateway.Messages.Count(message => message.Contains("malformed:", StringComparison.Ordinal)));

        gateway.Send(valid);

        Assert.Equal(27u, ammunition.Count);
        GroundLootItem dropped = Assert.Single(gateway.Loot.Items);
        Assert.Equal(1429u, dropped.ItemDefinitionId);
        Assert.Equal(3u, dropped.Count);
        Assert.Contains(gateway.Sent, bytes => bytes.Length > 3 && bytes[1] == 0x11 && bytes[2] == 0x02);
        Assert.Equal(ConnectionState.Open, gateway.Connection.State);
    }

    private static byte[] Packet(byte flag, Action<PacketWriter>? properties = null, ulong itemGuid = Item)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xac);
        writer.WriteByte(0x2c);
        writer.WriteUInt32(1);
        writer.WriteUInt32(0);
        writer.WriteUInt32(4); // DropItem
        writer.WriteUInt64(Character);
        writer.WriteUInt64(Character);
        writer.WriteUInt64(Character);
        writer.WriteUInt64(itemGuid);
        writer.WriteByte(flag);
        properties?.Invoke(writer);
        return writer.Written.ToArray();
    }

    private static byte[] EmptyLists() => Packet(0, writer => WriteEmptyLists(writer, 5));

    private static void WriteEmptyLists(PacketWriter writer, int count)
    {
        for (int i = 0; i < count; i++) writer.WriteInt32(0);
    }

    private static byte[] Quantity(uint quantity, ulong itemGuid = Item) => Packet(0, writer =>
    {
        writer.WriteInt32(1);
        writer.WriteUInt32(1);
        writer.WriteUInt32(quantity);
        WriteEmptyLists(writer, 4);
    }, itemGuid);

    private static byte[] AllPropertyTypes() => Packet(0, writer =>
    {
        writer.WriteInt32(2);
        writer.WriteUInt32(7); writer.WriteUInt32(999);
        writer.WriteUInt32(1); writer.WriteUInt32(3);
        writer.WriteInt32(2);
        writer.WriteUInt32(1); writer.WriteUInt32(0x7fc00000);
        writer.WriteUInt32(2); writer.WriteUInt32(0xffffffff);
        writer.WriteInt32(2);
        writer.WriteUInt32(1); writer.WriteUInt64(0x8877665544332211);
        writer.WriteUInt32(3); writer.WriteUInt64(0xfedcba9876543210);
        writer.WriteInt32(1);
        writer.WriteUInt32(1);
        writer.WriteUInt32(0x11223344); writer.WriteUInt32(0x55667788);
        writer.WriteUInt32(0x99aabbcc); writer.WriteUInt32(0xddeeff00);
        writer.WriteInt32(2);
        writer.WriteUInt32(1); writer.WriteInt32(3); writer.WriteRaw([0xff, 0, 0xc0]);
        writer.WriteUInt32(5); writer.WriteInt32(0);
    });

    private sealed class Gateway : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly ZoneService _service;
        public SoeConnection Connection { get; }
        public PlayerInventory Inventory { get; }
        public LootWorld Loot { get; }
        public List<byte[]> Sent { get; } = [];
        public List<string> Messages { get; } = [];

        public Gateway()
        {
            var tickets = new GatewayTicketRegistry();
            GatewayAdmission admission = tickets.Issue(Character, "Item property codec test");
            _service = new ZoneService(this, this, tickets, new ZoneOptions
            {
                BootstrapDelayMs = 1000, EnableGas = false, SendDoors = false,
                SendVehicles = false, AutoMatchMs = 0, DevGroundLootMs = 0,
            }) { Post = _ => { } };
            var endpoint = new IPEndPoint(IPAddress.Loopback, 5555);
            var session = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
            Connection = new SoeConnection(endpoint, in session, SessionSettings.WithSeed(1),
                _service.OnSessionRequest(endpoint, in session), _service, this, (_, _) => { }, now: 0);
            _service.OnConnected(Connection);
            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode);
            login.WriteUInt64(Character);
            login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol);
            login.WriteString(GatewayLoginRequest.AugustVersion);
            _service.OnMessage(Connection, login.Written.ToArray());

            object state = Connection.Tag!;
            Assert.True((bool)state.GetType().GetProperty("Authenticated")!.GetValue(state)!);
            Loot = (LootWorld)state.GetType().GetProperty("Loot")!.GetValue(state)!;
            Inventory = new PlayerInventory(Character, Loot.NextItemGuid,
                new InventoryOptions { StarterOutfit = [] });
            Inventory.Bootstrap();
            Inventory.TryPickUp(2112, 1, out _);
            Inventory.TryPickUp(1429, 30, out _);
            state.GetType().GetProperty("Inventory")!.SetValue(state, Inventory);
            Sent.Clear();
            Messages.Clear();
        }

        public void Send(byte[] payload)
        {
            using var tunnel = new PacketWriter();
            tunnel.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            tunnel.WriteRaw(payload);
            _service.OnMessage(Connection, tunnel.Written.ToArray());
        }

        public void Dispose()
        {
            Connection.Disconnect();
            _service.OnDisconnected(Connection, DisconnectCause.ServerRequested);
            ((WardrobeStore)typeof(ZoneService).GetField("_wardrobeStore",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!).Dispose();
        }

        public bool IsEnabled(TransportLogLevel level) => true;
        public void Log(TransportLogLevel level, string message) => Messages.Add(message);
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> ciphertext) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c") Sent.Add(bytes.ToArray());
        }
    }
}
