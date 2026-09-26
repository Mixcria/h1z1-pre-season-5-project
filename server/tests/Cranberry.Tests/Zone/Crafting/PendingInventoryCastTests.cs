using System.Net;
using System.Numerics;
using System.Reflection;
using System.Threading.Channels;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Crafting;

// Exercise the live gateway handler and its real delayed dispatcher. An expired
// animation deadline does not mean the listener has committed the inventory work.
public sealed partial class PendingInventoryCastTests
{
    [Fact]
    public async Task ElapsedCraftAwaitingDispatcherStillOwnsTheCast()
    {
        using var player = new Player();
        player.Inventory.TryPickUp(CraftingCatalog.ScrapOfCloth, 4, out _);
        long bandagesBefore = player.Count(CraftingCatalog.FieldBandage);
        player.CraftBandage();
        Action completion = await player.NextCompletion();

        // Hold the first callback after its actual due time, then deliver another
        // request as can happen when network work beats the posted timer to dispatch.
        player.CraftBandage();
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        completion();
        Assert.Equal(bandagesBefore + 1, player.Count(CraftingCatalog.FieldBandage));
        Assert.Equal(2, player.Count(CraftingCatalog.ScrapOfCloth));

        // Once the first callback commits, the next request is allowed normally.
        player.CraftBandage();
        (await player.NextCompletion())();
        Assert.Equal(bandagesBefore + 2, player.Count(CraftingCatalog.FieldBandage));
        Assert.Equal(0, player.Count(CraftingCatalog.ScrapOfCloth));
    }

    [Fact]
    public async Task ElapsedShredAwaitingDispatcherStillOwnsTheCast()
    {
        using var player = new Player();
        player.Inventory.TryPickUp(2144, 1, out var first);
        player.Inventory.TryPickUp(2144, 1, out var second);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Guid, second.Guid);
        player.Shred(first.Guid);
        Action completion = await player.NextCompletion();

        player.Shred(second.Guid);
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        completion();
        Assert.False(player.Inventory.Items.ContainsKey(first.Guid));
        Assert.True(player.Inventory.Items.ContainsKey(second.Guid));

        player.Shred(second.Guid);
        (await player.NextCompletion())();
        Assert.False(player.Inventory.Items.ContainsKey(second.Guid));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GroundAndCarriedShredsSharePendingOwnership(bool groundFirst)
    {
        using var player = new Player();
        player.Inventory.TryPickUp(2144, 1, out var carried);
        var ground = player.Loot.Spawn(2144, 9249, Vector3.Zero);
        Assert.NotNull(carried);
        ulong first = groundFirst ? ground.WorldGuid : carried.Guid;
        ulong second = groundFirst ? carried.Guid : ground.WorldGuid;
        player.Shred(first);
        Action completion = await player.NextCompletion();
        player.Shred(second);
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        completion();
        player.Shred(second);
        (await player.NextCompletion())();
        Assert.False(player.Inventory.Items.ContainsKey(carried.Guid));
        Assert.False(player.Loot.TryGet(ground.WorldGuid, out _));
    }

    [Fact]
    public async Task MedicalUseWaitsForQueuedShredToCommit()
    {
        using var player = new Player();
        player.Start(craft: false);
        Action completion = await player.NextCompletion();
        var bandage = player.Inventory.Items.Values.First(i => i.DefinitionId == CraftingCatalog.FieldBandage);
        uint consume = ItemUseOptionTable.OptionsForItem(bandage.DefinitionId)
            .First(id => ItemUseOptionTable.KindOf(id) == ItemUseOptionKind.ConsumeItem);
        player.Use(bandage.Guid, consume);
        Assert.Null(player.Get<object?>("PendingMedicalCast"));
        Assert.Single(player.Sent, p => Is(p, 0xcf, 2));
        completion();
        player.Use(bandage.Guid, consume);
        Assert.NotNull(player.Get<object?>("PendingMedicalCast"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingDispatcherLeavesNoPendingCastOrInventoryMutation(bool craft)
    {
        using var player = new Player();
        player.DisableDispatcher();
        player.Start(craft);
        Assert.Null(player.Get<object?>(craft ? "PendingCraft" : "PendingShred"));
        Assert.Equal(0, player.Get<long>(craft ? "CraftBusyUntil" : "ShredBusyUntil"));
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xcf, 2));
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
        if (craft) Assert.Equal(2, player.Count(CraftingCatalog.ScrapOfCloth));
        else Assert.Equal(1, player.Count(2144));
    }

    [Theory]
    [InlineData(true, "death")]
    [InlineData(false, "death")]
    [InlineData(true, "zoning")]
    [InlineData(false, "zoning")]
    [InlineData(true, "abandon")]
    [InlineData(false, "abandon")]
    [InlineData(true, "inventory")]
    [InlineData(false, "inventory")]
    [InlineData(true, "world")]
    [InlineData(false, "world")]
    [InlineData(true, "interaction")]
    [InlineData(false, "interaction")]
    public async Task StaleCallbackCannotMutateInventoryOrStopANewCast(bool craft, string transition)
    {
        using var player = new Player();
        player.Start(craft);
        Action stale = await player.NextCompletion();
        PlayerInventory previous = player.Inventory;

        switch (transition)
        {
            case "death":
                player.Invoke("KillPlayer", DamageCause.Bullet, 0ul, null, 0u);
                Assert.Null(player.Get<object?>(craft ? "PendingCraft" : "PendingShred"));
                break;
            case "abandon":
                player.Invoke("AbandonMatch", "inventory cast test");
                Assert.Null(player.Get<object?>(craft ? "PendingCraft" : "PendingShred"));
                break;
            case "zoning":
                player.Set("BountyAdmission", new MatchAdmissionContext(1, MatchQueueKind.Public, MatchMode.Solo));
                player.Match("Transferring");
                int previousWorld = player.Get<int>("WorldGeneration");
                player.Invoke("EnterMatch", false);
                (await player.NextCompletion())();
                Assert.NotSame(previous, player.Get<object?>("Inventory"));
                Assert.Equal(previousWorld + 1, player.Get<int>("WorldGeneration"));
                Assert.Null(player.Get<object?>(craft ? "PendingCraft" : "PendingShred"));
                break;
            case "world":
                player.Set("WorldGeneration", player.Get<int>("WorldGeneration") + 1);
                break;
            case "interaction":
                player.Set("InteractionGeneration", player.Get<int>("InteractionGeneration") + 1);
                break;
        }

        // Replace the actor's inventory after real death/zoning/abandon; direct
        // context replacements independently exercise the defensive epoch checks.
        if (transition is not ("world" or "interaction")) player.ReplaceInventory();
        player.Set("DeathSent", false);
        player.Set("Hitpoints", 10000u);
        player.InMatch();
        player.Start(craft);
        object? current = player.Get<object?>(craft ? "PendingCraft" : "PendingShred");
        Assert.NotNull(current);
        long deadline = player.Get<long>(craft ? "CraftBusyUntil" : "ShredBusyUntil");
        var oldItems = Snapshot(previous);
        var newItems = Snapshot(player.Inventory);
        player.Sent.Clear();

        stale();

        Assert.Same(current, player.Get<object?>(craft ? "PendingCraft" : "PendingShred"));
        Assert.Equal(deadline, player.Get<long>(craft ? "CraftBusyUntil" : "ShredBusyUntil"));
        Assert.Equal(oldItems, Snapshot(previous));
        Assert.Equal(newItems, Snapshot(player.Inventory));
        Assert.Empty(player.Sent);
        while (player.Get<object?>(craft ? "PendingCraft" : "PendingShred") is not null)
            (await player.NextCompletion())();
        Assert.Contains(player.Sent, p => Is(p, 0xcf, 3));
    }

    private static (ulong Guid, uint Definition, uint Count)[] Snapshot(PlayerInventory inventory) =>
        inventory.Items.Values.Select(i => (i.Guid, i.DefinitionId, i.Count)).OrderBy(i => i.Guid).ToArray();

    private static bool Is(byte[] packet, byte family, byte message) =>
        packet.Length > 1 && packet[0] == family && packet[1] == message;

    private sealed class Player : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly ZoneService _service;
        private readonly SoeConnection _connection;
        private readonly Channel<Action> _completed = Channel.CreateUnbounded<Action>();
        public PlayerInventory Inventory { get; private set; } = null!;
        public LootWorld Loot => Get<LootWorld>("Loot");
        public List<byte[]> Sent { get; } = [];
        private const ulong Guid = 0x1001;

        public Player()
        {
            _service = new(this, this, new GatewayTicketRegistry(), new ZoneOptions
            {
                SendDoors = false, SendVehicles = false,
                Crafting = new CraftingOptions { CraftCastBar = true, RetailRecipes = true },
            }) { Post = action => _completed.Writer.TryWrite(action) };
            var request = new SessionRequest(3, 123, 512, ZoneService.ProtocolName);
            _connection = new(new(IPAddress.Loopback, 12345), in request, new(),
                SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(_connection);
            object state = _connection.Tag!;
            Set(state, "Authenticated", true);
            Set(state, "Guid", Guid);
            Set(state, "Visuals", CharacterVisuals.FromSelection(1, 1, 0, 0, 5));
            Set(state, "Gender", CharacterVisuals.Male);
            Set(state, "Wardrobe", new AugustWardrobeState());
            PropertyInfo match = state.GetType().GetProperty("Match")!;
            match.SetValue(state, Enum.Parse(match.PropertyType, "InMatch"));
            var weapons = (WeaponSession)state.GetType().GetProperty("Weapons")!.GetValue(state)!;
            weapons.MarkProjectileDefinitionsSent();
            weapons.MarkWeaponDefinitionsSent();
            ReplaceInventory();
        }

        public void ReplaceInventory()
        {
            Inventory = new(Guid, Loot.NextItemGuid, new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1000 });
            Inventory.Bootstrap();
            Set("Inventory", Inventory);
        }
        public void Start(bool craft)
        {
            if (craft)
            {
                Inventory.TryPickUp(CraftingCatalog.ScrapOfCloth, 2, out _);
                CraftBandage();
            }
            else
            {
                Inventory.TryPickUp(2144, 1, out var shirt);
                Assert.NotNull(shirt);
                Shred(shirt.Guid);
            }
        }
        public void InMatch() => Match("InMatch");
        public void Match(string value)
        {
            var match = _connection.Tag!.GetType().GetProperty("Match")!;
            match.SetValue(_connection.Tag, Enum.Parse(match.PropertyType, value));
        }
        public void Set(string name, object value) => Set(_connection.Tag!, name, value);
        public T Get<T>(string name) => (T)_connection.Tag!.GetType().GetProperty(name)!.GetValue(_connection.Tag)!;
        public void Invoke(string name, params object?[] args) => typeof(ZoneService)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_service, [_connection, _connection.Tag, .. args]);
        public long Count(uint definition) => Inventory.Items.Values.Where(i => i.DefinitionId == definition)
            .Sum(i => (long)i.Count);
        public void DisableDispatcher() => _service.Post = null;
        public void EnableDispatcher() => _service.Post = action => _completed.Writer.TryWrite(action);
        public ZoneService.VehicleTestSession VehicleSession => _service.ForVehicleTest(_connection);
        public void CraftBandage()
        {
            using var packet = new PacketWriter();
            packet.WriteByte(0x09); packet.WriteUInt16(0x1a);
            packet.WriteUInt32(CraftingCatalog.FieldBandage); packet.WriteUInt32(1);
            Send(packet.Written.ToArray());
        }
        public void Shred(ulong itemGuid) => Use(itemGuid, 6);
        public void Use(ulong itemGuid, uint option)
        {
            using var packet = new PacketWriter();
            packet.WriteByte(0xac); packet.WriteByte(0x2c);
            packet.WriteUInt32(1); packet.WriteUInt32(0); packet.WriteUInt32(option);
            packet.WriteUInt64(Guid); packet.WriteUInt64(Guid); packet.WriteUInt64(Guid);
            packet.WriteUInt64(itemGuid); packet.WriteByte(1);
            Send(packet.Written.ToArray());
        }
        private void Send(byte[] packet) => _service.OnMessage(_connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), .. packet]);
        public async Task<Action> NextCompletion() =>
            await _completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        private static void Set(object state, string name, object value) =>
            state.GetType().GetProperty(name)!.SetValue(state, value);
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes[1..].ToArray()); }
        public void Dispose() => _connection.Disconnect();
    }
}
