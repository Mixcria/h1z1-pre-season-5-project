using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class BodyBagGatewayTests
{
    [Fact]
    public void DummyDeathDropsOneLootableBagAndFDoesNotCollectTheContainer()
    {
        using var world = new World();
        var player = world.AddPlayer();
        var dummy = new PracticeTarget(123, 45, Vector3.Zero, Vector4.UnitW, 100);
        PracticeTargetKit.Equip(dummy);
        dummy.Damage(1000, 1);
        world.Call("KillPracticeTarget", player.Connection, player.State, dummy);
        world.Call("KillPracticeTarget", player.Connection, player.State, dummy);
        var (guid, bag) = Assert.Single(player.Bags);
        Assert.Equal(BodyBag.ModelId, Assert.Single(player.Loot.Items).GroundModelId);
        Assert.Contains(bag.Items.Values, i => i.DefinitionId == PracticeTargetKit.Backpack);
        Assert.Contains(bag.Items.Values, i => i.DefinitionId == PracticeTargetKit.Rifle);
        player.Sent.Clear();
        player.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(guid)]);
        player.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(player.Guid), .. BitConverter.GetBytes(guid)]);
        Assert.Equal(guid, Get<ulong>(player.State, "AccessedBodyBag"));
        Assert.Single(player.Sent, p => Is(p, 0xf0, 1) && BitConverter.ToUInt64(p, 3) == guid);
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 2) && BitConverter.ToUInt64(p, 11) == guid);
        Assert.DoesNotContain(player.Inventory.Items.Values, i => i.DefinitionId == BodyBag.ItemDefinitionId);
    }

    [Fact]
    public void PlayerDeathTransfersEveryCarriedStackAndLoadedRoundsExactlyOnce()
    {
        using var world = new World();
        var player = world.AddPlayer();
        player.Inventory.TryPickUp(2425, 1, out var rifle);
        player.Inventory.TryPickUp(1429, 12, out _);
        Get<SessionCombat>(player.State, "Combat").Shooter.DeclareWeapon(rifle!.Guid, rifle.DefinitionId, 7);
        var expected = player.Inventory.Items.Values.Where(i => i.DefinitionId is not (85 or 3156))
            .GroupBy(i => i.DefinitionId).ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Count));
        expected[1429] += 7;
        world.Call("KillPlayer", player.Connection, player.State, DamageCause.Bullet, 0ul, null, 0u);
        world.Call("KillPlayer", player.Connection, player.State, DamageCause.Bullet, 0ul, null, 0u);
        var bag = Assert.Single(player.Bags).Value;
        Assert.Equal(expected.OrderBy(i => i.Key), bag.Items.Values.GroupBy(i => i.DefinitionId)
            .ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Count)).OrderBy(i => i.Key));
        Assert.All(player.Inventory.Items.Values, i => Assert.Contains(i.DefinitionId, new uint[] { 85, 3156 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPracticeBandageCanBeLootedByItsAdvertisedCount(bool fullKit)
    {
        using var world = new World();
        var player = world.AddPlayer();
        var dummy = new PracticeTarget(123, 45, Vector3.Zero, Vector4.UnitW, 100);
        if (fullKit) PracticeTargetKit.Equip(dummy);
        dummy.Damage(1000, 1);
        world.Call("KillPracticeTarget", player.Connection, player.State, dummy);
        var (owner, bag) = Assert.Single(player.Bags);
        var bandages = bag.Records(owner).Where(i => i.DefinitionId == 2423).ToArray();
        Assert.Single(bandages);
        Assert.Equal(5u, bandages.Aggregate(0u, (count, item) => count + item.Count));
        long before = player.Inventory.Items.Values.Where(i => i.DefinitionId == 2423).Sum(i => (long)i.Count);

        foreach (var item in bandages)
        {
            player.Sent.Clear();
            // The ordinary left-click path sends the row's complete advertised count.
            player.Send(Move(owner, item.ItemGuid, player.Guid, item.Count));
            Assert.DoesNotContain(player.Sent, p => Is(p, 0xc8, 3));
            Assert.False(bag.Items.ContainsKey(item.ItemGuid));
            long after = player.Inventory.Items.Values.Where(i => i.DefinitionId == 2423).Sum(i => (long)i.Count);
            player.Send(Move(owner, item.ItemGuid, player.Guid, item.Count));
            Assert.Equal(after, player.Inventory.Items.Values.Where(i => i.DefinitionId == 2423).Sum(i => (long)i.Count));
        }
        Assert.Equal(before + 5, player.Inventory.Items.Values.Where(i => i.DefinitionId == 2423).Sum(i => (long)i.Count));
        Assert.Equal(before + 5, player.Inventory.LoadoutSlots[SurvivorLoadout.QuickUse1].Count);
        Assert.DoesNotContain(bag.Items.Values, i => i.DefinitionId == 2423);
    }

    [Fact]
    public void ClickAndPartialDragWorkAfterMovingAndLastItemRemovesBag()
    {
        using var world = new World();
        var player = world.AddPlayer();
        var bag = world.SpawnBag(player, (1429u, 10u), (2158u, 1u));
        ulong guid = Assert.Single(player.Bags).Key;
        var ammo = bag.Items.Values.Single(i => i.DefinitionId == 1429);
        var hat = bag.Items.Values.Single(i => i.DefinitionId == 2158);
        player.Move(new(1, 0, 0));
        player.Send(Move(guid, ammo.ItemGuid, player.Guid, 3, player.Inventory.BaseBag!.Guid, 9));
        Assert.Equal(7u, bag.Items[ammo.ItemGuid].Count);
        Assert.Equal(3u, player.Inventory.BaseBag.Slots[9].Count);
        player.Move(new(0, 0, 1));
        player.Send(Move(guid, hat.ItemGuid, player.Guid, 1, PlayerInventory.EquippedContainerGuid, (int)SurvivorLoadout.Head));
        Assert.Equal(2158u, player.Inventory.LoadoutSlots[SurvivorLoadout.Head].DefinitionId);
        player.Send(Move(guid, ammo.ItemGuid, player.Guid, 7));
        player.Send(Move(guid, ammo.ItemGuid, player.Guid, 7));
        Assert.Empty(player.Bags);
        Assert.Empty(player.Loot.Items);
        Assert.Equal(10u, player.Inventory.Items.Values.Single(i => i.DefinitionId == 1429).Count);
    }

    [Theory]
    [InlineData("distance")]
    [InlineData("dead")]
    [InlineData("count")]
    [InlineData("target")]
    [InlineData("capacity")]
    [InlineData("slot")]
    public void RefusedTransfersLeaveBagAndInventoryUnchanged(string reason)
    {
        using var world = new World();
        var player = world.AddPlayer();
        var bag = world.SpawnBag(player, (reason == "capacity" ? 1373u : 1429u, 1u));
        ulong guid = Assert.Single(player.Bags).Key;
        var item = Assert.Single(bag.Items.Values);
        if (reason == "distance") player.Move(new(20, 0, 0));
        if (reason == "dead") Set(player.State, "DeathSent", true);
        int before = player.Inventory.Items.Count;
        player.Send(Move(guid, item.ItemGuid, reason == "target" ? 999 : player.Guid,
            reason == "count" ? 2u : 1u,
            reason is "capacity" or "slot" ? player.Inventory.BaseBag!.Guid : 0,
            reason == "slot" ? 10000 : -1));
        Assert.Single(bag.Items);
        Assert.Equal(before, player.Inventory.Items.Count);
        Assert.Contains(player.Sent, p => Is(p, 0xc8, 3));
    }

    [Fact]
    public void SharedBagSurvivesOwnerLeavingAndCannotBeTakenTwiceOrRestreamedEmpty()
    {
        using var world = new World();
        var owner = world.AddPlayer(shared: true);
        var first = world.AddPlayer(shared: true);
        var second = world.AddPlayer(shared: true);
        var bag = world.SpawnBag(owner, (1429u, 5u));
        ulong firstGuid = Assert.Single(first.Bags).Key;
        ulong secondGuid = Assert.Single(second.Bags).Key;
        var item = Assert.Single(bag.Items.Values);
        world.Call("LeaveSharedLoot", owner.State);
        first.Send(Move(firstGuid, item.ItemGuid, first.Guid, 5));
        second.Send(Move(secondGuid, item.ItemGuid, second.Guid, 5));
        Assert.Equal(5u, first.Inventory.Items.Values.Single(i => i.DefinitionId == 1429).Count);
        Assert.DoesNotContain(second.Inventory.Items.Values, i => i.DefinitionId == 1429);
        Assert.Empty(first.Bags);
        Assert.Empty(second.Bags);
        var burst = new List<Action>();
        world.Call("PlanSharedDrops", second.Connection, second.State, burst);
        Assert.Empty(burst);
    }

    [Fact]
    public void LeavingReachClosesForeignAccessAndRetainsSelfAccess()
    {
        using var world = new World();
        var player = world.AddPlayer();
        world.SpawnBag(player, (1429u, 5u));
        ulong guid = Assert.Single(player.Bags).Key;
        player.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(guid)]);
        player.Move(new(20, 0, 0));
        world.Call("RefreshBodyBagMovement", player.Connection, player.State);
        Assert.Equal(0ul, Get<ulong>(player.State, "AccessedBodyBag"));
        Assert.True(Get<bool>(player.State, "CharacterAccessGranted"));
        Assert.Contains(player.Sent, p => Is(p, 0xf0, 2));
        var panel = player.Sent.Last(p => Is(p, 0xf8, 1));
        Assert.Equal(0, BitConverter.ToInt32(panel, 2));
    }

    [Fact]
    public void MovingWithinSameLootDoesNotRebuildTheListUnderTheMouse()
    {
        using var world = new World();
        var player = world.AddPlayer();
        world.SpawnBag(player, (1429u, 5u));
        player.Sent.Clear();
        player.Move(new(0.5f, 0, 0));
        world.Call("RefreshBodyBagMovement", player.Connection, player.State);
        Assert.DoesNotContain(player.Sent, p => Is(p, 0xf8, 1));
        Set(player.State, "NextBodyBagPanelMs", 0L);
        player.Move(new(10, 0, 0));
        world.Call("RefreshBodyBagMovement", player.Connection, player.State);
        Assert.Equal(0, BitConverter.ToInt32(Assert.Single(player.Sent, p => Is(p, 0xf8, 1)), 2));
    }

    [Fact]
    public void TwoBagsPublishDistinctRowKeysAndRemoteOwners()
    {
        using var world = new World();
        var player = world.AddPlayer();
        world.SpawnBag(player, (1429u, 5u));
        world.SpawnBag(player, (1429u, 5u));
        var panel = player.Sent.Last(p => Is(p, 0xf8, 1));
        Assert.Equal(2, BitConverter.ToInt32(panel, 2));
        Assert.NotEqual(BitConverter.ToUInt32(panel, 6), BitConverter.ToUInt32(panel, 80));
        Assert.NotEqual(BitConverter.ToUInt64(panel, 60), BitConverter.ToUInt64(panel, 134));
        Assert.Contains(BitConverter.ToUInt64(panel, 60), player.Bags.Keys);
        Assert.Contains(BitConverter.ToUInt64(panel, 134), player.Bags.Keys);
    }

    [Fact]
    public void EvictionAndRestreamRetainPartialBagContents()
    {
        using var world = new World();
        var first = world.AddPlayer(shared: true);
        var second = world.AddPlayer(shared: true);
        var bag = world.SpawnBag(first, (1429u, 10u));
        ulong oldGuid = Assert.Single(second.Bags).Key;
        ulong source = Assert.Single(bag.Items).Key;
        first.Send(Move(Assert.Single(first.Bags).Key, source, first.Guid, 3));
        Get<MatchLoot>(second.State, "StreamedLoot").NoteEvicted(oldGuid);
        world.Call("EvictGroundLoot", second.Connection, second.State, oldGuid);
        Assert.Empty(second.Bags);
        var burst = new List<Action>();
        world.Call("PlanSharedDrops", second.Connection, second.State, burst);
        Assert.Single(burst)();
        var (newGuid, restreamed) = Assert.Single(second.Bags);
        Assert.NotEqual(oldGuid, newGuid);
        Assert.Same(bag, restreamed);
        Assert.Equal(7u, Assert.Single(restreamed.Items.Values).Count);
        second.Send(Move(newGuid, source, second.Guid, 7));
        Assert.Empty(first.Bags);
        Assert.Empty(second.Bags);
    }

    private static bool Is(byte[] p, byte code, byte sub) => p.Length >= 2 && p[0] == code && p[1] == sub;
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);
    private static byte[] Move(ulong owner, ulong item, ulong target, uint count, ulong container = 0, int slot = -1)
    {
        using var w = new PacketWriter();
        w.WriteByte(0xc8); w.WriteUInt16(1); w.WriteUInt64(container); w.WriteUInt64(owner);
        w.WriteUInt64(item); w.WriteUInt64(target); w.WriteUInt32(count); w.WriteInt32(slot);
        return w.Written.ToArray();
    }

    private sealed class World : ITransportLog, IPacketRecorder, IDisposable
    {
        public ZoneService Service { get; }
        public List<Player> Players { get; } = [];
        public World(ZoneOptions? options = null) => Service = new(this, this, new GatewayTicketRegistry(), options ?? new ZoneOptions
            { SendProximateItems = true, MatchEnd = new() { Enabled = false } });
        public Player AddPlayer(bool shared = false)
        {
            var player = new Player(this, Players.Count + 1);
            Players.Add(player);
            if (shared)
            {
                Set(player.State, "BountyAdmission", new MatchAdmissionContext(1, MatchQueueKind.Public, MatchMode.Solo));
                Call("JoinSharedLoot", player.Connection, player.State);
            }
            return player;
        }
        public BodyBag SpawnBag(Player player, params (uint Definition, uint Count)[] items)
        {
            var bag = new BodyBag();
            foreach (var (definition, count) in items) bag.Add(player.Loot.NextItemGuid(), definition, count);
            Call("SpawnBodyBag", player.Connection, player.State, bag, Vector3.Zero);
            return bag;
        }
        public object? Call(string method, params object?[] args) => typeof(ZoneService)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Service, args);
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Players.Find(p => p.Connection == connection)?.Sent.Add(bytes[1..].ToArray()); }
        public void Dispose() { foreach (var p in Players) p.Connection.Disconnect(); }
    }
    private sealed class Player
    {
        private readonly World _world;
        public SoeConnection Connection { get; }
        public object State => Connection.Tag!;
        public ulong Guid { get; }
        public List<byte[]> Sent { get; } = [];
        public PlayerInventory Inventory => Get<PlayerInventory>(State, "Inventory");
        public LootWorld Loot => Get<LootWorld>(State, "Loot");
        public Dictionary<ulong, BodyBag> Bags => Get<Dictionary<ulong, BodyBag>>(State, "BodyBags");
        public Player(World world, int index)
        {
            _world = world; Guid = 0x1000ul + (ulong)index;
            var request = new SessionRequest(3, (uint)index, 512, ZoneService.ProtocolName);
            Connection = new(new(IPAddress.Loopback, 12000 + index), in request, new(), SessionDecision.Clear,
                world.Service, world, (_, _) => { }, 0);
            world.Service.OnConnected(Connection);
            Set(State, "Authenticated", true); Set(State, "Guid", Guid);
            Set(State, "Visuals", CharacterVisuals.FromSelection(1, 1, 0, 0, 5));
            Set(State, "Gender", CharacterVisuals.Male); Set(State, "Wardrobe", new AugustWardrobeState());
            var weapons = Get<WeaponSession>(State, "Weapons");
            weapons.MarkProjectileDefinitionsSent(); weapons.MarkWeaponDefinitionsSent();
            var match = State.GetType().GetProperty("Match")!;
            match.SetValue(State, Enum.Parse(match.PropertyType, "InMatch"));
            Set(State, "LandingLootDrained", true);
            var inventory = new PlayerInventory(Guid, Loot.NextItemGuid);
            inventory.Bootstrap(); Set(State, "Inventory", inventory); Move(Vector3.Zero);
        }
        public void Send(byte[] packet) => _world.Service.OnMessage(Connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), .. packet]);
        public void Move(Vector3 position)
        {
            var movement = Get<SessionMovementState>(State, "Movement");
            movement.ApplyPlayer(ClientMovementUpdate.Parse(new byte[7])); movement.PinPlayer(position);
        }
    }
}
