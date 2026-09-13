using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Loot;

public sealed class GroundLootTransferTests
{
    [Fact]
    public void ContextLootActionHonorsItsChosenQuantity()
    {
        using var world = new World();
        var player = world.AddPlayer();
        var ground = player.Loot.Spawn(1429, 1, Vector3.Zero, 30);
        uint option = ItemUseOptionTable.OptionsForItem(1429)
            .First(id => ItemUseOptionTable.KindOf(id) == ItemUseOptionKind.LootItem);
        using var w = new PacketWriter();
        w.WriteByte(0xac); w.WriteByte(0x2c); w.WriteUInt32(1); w.WriteUInt32(0);
        w.WriteUInt32(option); w.WriteUInt64(player.Guid); w.WriteUInt64(ground.WorldGuid);
        w.WriteUInt64(player.Guid); w.WriteUInt64(ground.WorldGuid); w.WriteByte(0);
        w.WriteUInt32(1); w.WriteUInt32(1); w.WriteUInt32(3);
        for (int i = 0; i < 4; i++) w.WriteUInt32(0);
        player.Send(w.Written.ToArray());
        Assert.Equal(3L, Count(player));
        Assert.Equal(27u, player.Loot.Items.Single().Count);
    }

    [Theory]
    [InlineData(0ul, -1)]
    [InlineData(1ul, 0)]
    [InlineData(1ul, 9)]
    public void RequestedPartialStackFitsWhenTheWholeStackDoesNot(ulong destination, int slot)
    {
        using var world = new World();
        var player = world.AddPlayer(100);
        player.Inventory.TryPickUp(1429, 49, out _); // 98/100 bulk.
        var ground = player.Loot.Spawn(1429, 1, Vector3.Zero, 30);
        player.Send(Move(player, ground, 30));
        Assert.Equal(30u, player.Loot.Items.Single().Count);
        world.Sent.Clear();

        player.Send(Move(player, ground, 1, destination == 0 ? 0 : player.Inventory.BaseBag!.Guid, slot));

        Assert.Equal(29u, player.Loot.Items.Single().Count);
        Assert.Equal(ground.WorldGuid, player.Loot.Items.Single().WorldGuid);
        Assert.Equal(50L, Count(player));
        if (slot == 9) Assert.Equal(1u, player.Inventory.BaseBag!.Slots[9].Count);
        Assert.DoesNotContain(world.Sent, p => Is(p.Bytes, 0x0f, 1));
        Assert.Single(world.Sent, p => Is(p.Bytes, 0x0f, 0x43));
        byte[] row = world.Sent.Last(p => Is(p.Bytes, 0xf8, 1)).Bytes;
        Assert.Equal(29u, BitConverter.ToUInt32(row, 26));
    }

    [Theory]
    [InlineData(0u, 0ul, -1)]
    [InlineData(31u, 0ul, -1)]
    [InlineData(3u, 999ul, -1)]
    [InlineData(3u, 1ul, 10000)]
    public void InvalidCountOrDestinationLeavesBothInventoriesUntouched(uint count, ulong destination, int slot)
    {
        using var world = new World();
        var player = world.AddPlayer();
        var ground = player.Loot.Spawn(1429, 1, Vector3.Zero, 30);
        player.Send(Move(player, ground, count, destination == 1 ? player.Inventory.BaseBag!.Guid : destination, slot));
        Assert.Equal(30u, player.Loot.Items.Single().Count);
        Assert.Equal(0L, Count(player));
        Assert.DoesNotContain(world.Sent, p => Is(p.Bytes, 0x0f, 0x43) || Is(p.Bytes, 0x0f, 1));
    }

    [Fact]
    public void ExplicitWeaponWheelDestinationIsHonored()
    {
        using var world = new World();
        var player = world.AddPlayer();
        var ground = player.Loot.Spawn(1373, 1, Vector3.Zero);
        player.Send(Move(player, ground, 1, PlayerInventory.EquippedContainerGuid, (int)SurvivorLoadout.Wheel3));
        Assert.Equal(1373u, player.Inventory.LoadoutSlots[SurvivorLoadout.Wheel3].DefinitionId);
        Assert.DoesNotContain(player.Inventory.LoadoutSlots, pair => pair.Key != SurvivorLoadout.Wheel3 && pair.Value.DefinitionId == 1373);
        Assert.Empty(player.Loot.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialPickupUpdatesEveryViewerAndRestreamsOnlyTheRemainder(bool dropped)
    {
        using var world = new World();
        var first = world.AddPlayer(shared: true);
        var second = world.AddPlayer(shared: true);
        var key = dropped ? LootStreamKey.ForDropped(91) : LootStreamKey.ForBox(91);
        var a = first.Spawn(key, 30);
        second.Loot.Spawn(1, 1, Vector3.One); // Prove local world GUIDs differ.
        var b = second.Spawn(key, 30);
        Assert.NotEqual(a.WorldGuid, b.WorldGuid);
        first.Send(Move(first, a, 3));
        Assert.True(first.Loot.Items.Single().Count == 27u, string.Join("\n", world.Logs));
        Assert.Equal(27u, second.Loot.Items.Single(i => i.ItemDefinitionId == 1429).Count);
        Assert.Equal(27u, second.Stream.RemainingCount(key, 30));
        Assert.Contains(world.Sent, p => p.Player == second.Connection && Is(p.Bytes, 0x11, 2));
        Assert.DoesNotContain(world.Sent, p => Is(p.Bytes, 0x0f, 1));

        // A later viewer and a queued spawn read the ledger at execution, not scheduling.
        var late = world.AddPlayer(shared: true);
        Assert.Equal(27u, late.Stream.RemainingCount(key, 30));
        second.Loot.TryEvict(b.WorldGuid, out _);
        second.Stream.NoteEvicted(b.WorldGuid);
        var restreamed = second.Spawn(key, 30);
        Assert.Equal(27u, restreamed.Count);
        second.Send(Move(second, restreamed, 27));
        second.Send(Move(second, restreamed, 27)); // A stale repeat cannot grant again.
        Assert.Equal(30L, Count(first) + Count(second));
        Assert.Empty(first.Loot.Items);
        Assert.DoesNotContain(second.Loot.Items, i => i.ItemDefinitionId == 1429);
        Assert.True(late.Stream.IsTaken(key));
        Assert.Equal(0u, late.Stream.RemainingCount(key, 30));
    }

    [Fact]
    public void LocalMarkerRemainderSurvivesEvictionButNotANewMatch()
    {
        using var world = new World();
        var player = world.AddPlayer();
        var key = LootStreamKey.ForMarker(123);
        var ground = player.Spawn(key, 30);
        player.Send(Move(player, ground, 2));
        player.Loot.TryEvict(ground.WorldGuid, out _);
        player.Stream.NoteEvicted(ground.WorldGuid);
        Assert.Equal(28u, player.Spawn(key, 30).Count);
        player.Stream.Clear();
        Assert.Equal(30u, player.Stream.RemainingCount(key, 30));
    }

    private static long Count(Player player) => player.Inventory.Items.Values.Where(i => i.DefinitionId == 1429).Sum(i => (long)i.Count);
    private static bool Is(byte[] p, byte a, byte b) => p.Length > 1 && p[0] == a && p[1] == b;
    private static byte[] Move(Player p, GroundLootItem item, uint count, ulong container = 0, int slot = -1)
    {
        using var w = new PacketWriter();
        w.WriteByte(0xc8); w.WriteUInt16(1); w.WriteUInt64(container);
        w.WriteUInt64(item.WorldGuid); w.WriteUInt64(item.WorldGuid); w.WriteUInt64(p.Guid);
        w.WriteUInt32(count); w.WriteInt32(slot);
        return w.Written.ToArray();
    }
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);

    private sealed class Player(World world, SoeConnection connection, ulong guid)
    {
        public SoeConnection Connection => connection;
        public object State => connection.Tag!;
        public ulong Guid => guid;
        public PlayerInventory Inventory => Get<PlayerInventory>(State, "Inventory");
        public LootWorld Loot => Get<LootWorld>(State, "Loot");
        public MatchLoot Stream => Get<MatchLoot>(State, "StreamedLoot");
        public void Send(byte[] packet) => world.Service.OnMessage(connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), .. packet]);
        public GroundLootItem Spawn(LootStreamKey key, uint original)
        {
            GroundLootItem? item = null;
            world.Call("SpawnSharedLoot", connection, State, key, (Func<GroundLootItem>)(() =>
                item = Loot.Spawn(1429, 1, Vector3.Zero, Stream.RemainingCount(key, original))));
            return item!;
        }
    }

    private sealed class World : ITransportLog, IPacketRecorder, IDisposable
    {
        public ZoneService Service { get; }
        public List<(SoeConnection Player, byte[] Bytes)> Sent { get; } = [];
        public List<string> Logs { get; } = [];
        private readonly List<SoeConnection> _connections = [];
        private ulong _nextItem = 0x3100_0000_0000_0001;
        public World() => Service = new(this, this, new GatewayTicketRegistry(), new ZoneOptions { SendProximateItems = true });
        public object? Call(string name, params object?[] args) => typeof(ZoneService)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Service, args);
        public Player AddPlayer(int bulk = 1000, bool shared = false)
        {
            uint index = (uint)_connections.Count;
            ulong guid = 0x1001 + index;
            var request = new SessionRequest(3, index + 1, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 12000 + (int)index), in request,
                new(), SessionDecision.Clear, Service, this, (_, _) => { }, 0);
            _connections.Add(connection); Service.OnConnected(connection);
            var state = connection.Tag!;
            Set(state, "Guid", guid); Set(state, "Authenticated", true);
            Set(state, "Visuals", CharacterVisuals.FromSelection(1, 1, 0, 0, 5));
            Set(state, "Gender", CharacterVisuals.Male); Set(state, "Wardrobe", new AugustWardrobeState());
            var match = state.GetType().GetProperty("Match")!;
            match.SetValue(state, Enum.Parse(match.PropertyType, "InMatch"));
            var weapons = Get<WeaponSession>(state, "Weapons");
            weapons.MarkProjectileDefinitionsSent(); weapons.MarkWeaponDefinitionsSent();
            var inventory = new PlayerInventory(guid, () => _nextItem++, new InventoryOptions { StarterOutfit = [], BaseCarryBulk = bulk });
            inventory.Bootstrap(); Set(state, "Inventory", inventory);
            var movement = Get<SessionMovementState>(state, "Movement");
            movement.ApplyPlayer(ClientMovementUpdate.Parse(new byte[7]));
            movement.PinPlayer(Vector3.Zero);
            if (shared)
            {
                Set(state, "BountyAdmission", new MatchAdmissionContext(1, MatchQueueKind.Public, MatchMode.Solo));
                Call("JoinSharedLoot", connection, state);
            }
            return new(this, connection, guid);
        }
        public bool IsEnabled(TransportLogLevel level) => true;
        public void Log(TransportLogLevel level, string message) => Logs.Add(message);
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add((connection, bytes[1..].ToArray())); }
        public void Dispose()
        {
            foreach (var connection in _connections)
            { connection.Disconnect(); Service.OnDisconnected(connection, DisconnectCause.ServerRequested); }
        }
    }
}
