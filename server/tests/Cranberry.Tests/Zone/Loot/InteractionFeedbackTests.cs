using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Weapons;
using Cranberry.Tests.Zone.Combat;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class InteractionFeedbackTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FirstAndSecondWeaponPickupsPlayOnceWithoutStartingAReload(bool wieldFirstWeapon, bool ammoFirst)
    {
        using var world = new Fixture(inventoryOptions: new() { WieldFirstWeapon = wieldFirstWeapon });
        ulong originalHand = world.Inventory.WieldedItemGuid;
        var ammo = new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default);
        if (ammoFirst) PickUp(1429, 15);
        ulong firstGun = 0;
        for (int index = 0; index < 2; index++)
        {
            var before = world.Inventory.Items.Keys.ToHashSet();
            PickUp(2425, 1);
            var gun = Assert.Single(world.Inventory.Items.Values,
                item => item.DefinitionId == 2425 && !before.Contains(item.Guid));
            if (index == 0) firstGun = gun.Guid;
            byte[] grant = Assert.Single(world.Sent,
                p => Is(p, 0x11, 2) && BitConverter.ToUInt64(p, 23) == gun.Guid);
            Assert.Equal(0u, BitConverter.ToUInt32(grant, 15 + InventoryItem.BaseLength + 5));
            Assert.Equal(wieldFirstWeapon ? firstGun : originalHand, world.Inventory.WieldedItemGuid);
            Assert.Equal(ammoFirst ? 15 : 0, ammo.Count(1429));
            Assert.Null(world.Combat.Reload);
        }
        if (!ammoFirst) PickUp(1429, 15);
        Assert.Equal(15, ammo.Count(1429));
        Assert.Null(world.Combat.Reload);

        void PickUp(uint definition, uint count)
        {
            var ground = world.Loot.Spawn(definition, 1, Vector3.Zero, count);
            world.Sent.Clear();
            world.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(0x1001ul), .. BitConverter.GetBytes(ground.WorldGuid)]);
            world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(ground.WorldGuid)]);
            Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
            Assert.True(Is(world.Sent[0], 0x0f, 0x43));
            Assert.DoesNotContain(world.Sent, IsReload);
            Assert.False(world.Loot.TryGet(ground.WorldGuid, out _));
        }
    }

    [Theory]
    [InlineData(1373u, 5157u)] // AR-15
    [InlineData(2325u, 5140u)] // ammunition
    [InlineData(2424u, 5149u)] // first aid kit
    [InlineData(2170u, 5153u)] // helmet
    [InlineData(2169u, 5153u)] // tactical helmet
    [InlineData(2158u, 5148u)] // ordinary hat
    [InlineData(2046u, 5148u)] // outback hat
    [InlineData(2058u, 5148u)] // aviator hat
    [InlineData(2060u, 5148u)] // cowboy hat
    [InlineData(uint.MaxValue, 5151u)]
    public void SoundsUseMatchingAugustAssets(uint item, uint expected)
    {
        Assert.Equal(expected, PickupEffects.ForItem(item));
        Assert.True(AugustEffectCatalog.Contains(expected));
    }

    [Fact]
    public void EveryHelmetSoundMappingBelongsToProtectiveAugustHeadgear()
    {
        foreach (var item in InventoryItemFacts.All)
            if (PickupEffects.ForItem(item.DefinitionId) == 5153)
                Assert.True(ArmourModel.IsHelmet(item.DefinitionId),
                    $"Non-armour item {item.DefinitionId} has a helmet pickup sound");
    }

    [Theory]
    [InlineData(2046u, 5148u)]
    [InlineData(2170u, 5153u)]
    public void HatAndHelmetPickupsSendDistinctSoundsExactlyOnce(uint itemId, uint expectedEffect)
    {
        using var world = new Fixture();
        var item = world.Loot.Spawn(itemId, 1, Vector3.Zero);
        world.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(0x1001ul), .. BitConverter.GetBytes(item.WorldGuid)]);
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(item.WorldGuid)]);
        byte[] sound = Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.Equal(expectedEffect, BitConverter.ToUInt32(sound, 10));
        Assert.False(world.Loot.TryGet(item.WorldGuid, out _));
    }

    [Fact]
    public void SuccessfulPickupsPlayOnceBeforeInventoryWorkAndRemovalIncludingStackMerges()
    {
        using var world = new Fixture();
        for (int pickup = 0; pickup < 2; pickup++)
        {
            var item = world.Loot.Spawn(2325, 1, Vector3.Zero, count: 5);
            world.Sent.Clear();
            // Real F press pair: select + interact for the same entity.
            world.Send([0x09, 0x15, 0, .. BitConverter.GetBytes(0x1001ul), .. BitConverter.GetBytes(item.WorldGuid)]);
            world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(item.WorldGuid)]);
            byte[] sound = Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
            Assert.Equal(PlayWorldCompositeEffect.Length, sound.Length);
            Assert.Equal(item.WorldGuid, BitConverter.ToUInt64(sound, 2));
            Assert.Equal(5140u, BitConverter.ToUInt32(sound, 10));
            int soundIndex = world.Sent.IndexOf(sound);
            Assert.Equal(0, soundIndex);
            Assert.True(soundIndex < world.Sent.FindIndex(p => Is(p, 0x11, pickup == 0 ? (byte)0x02 : (byte)0x03)));
            Assert.True(soundIndex < world.Sent.FindIndex(p => Is(p, 0x0f, 0x01)));
            Assert.False(world.Loot.TryGet(item.WorldGuid, out _));
        }
        Assert.Equal(10, new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default).Count(2325));
    }

    [Fact]
    public void RefusedPickupHasNoSoundAndLeavesTheObjectAvailable()
    {
        using var world = new Fixture();
        var item = world.Loot.Spawn(2325, 1, Vector3.Zero, count: 10000);
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(item.WorldGuid)]);
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.True(world.Loot.TryGet(item.WorldGuid, out _));
    }

    [Theory]
    [InlineData(2217u, true)] // footwear swap
    [InlineData(1373u, true)] // gun
    [InlineData(2423u, false)] // legacy grant path
    public void PickupSoundPrecedesGrantAndEquipmentWork(uint itemId, bool containers)
    {
        using var world = new Fixture(containers, proximity: true);
        var item = world.Loot.Spawn(itemId, 1, Vector3.Zero);
        world.Send([0x09, 0x07, 0, .. BitConverter.GetBytes(item.WorldGuid)]);
        Assert.True(Is(world.Sent[0], 0x0f, 0x43));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.Contains(world.Sent, p => Is(p, 0x11, 0x02));
        Assert.Contains(world.Sent, p => Is(p, 0x0f, 0x01));
        Assert.False(world.Loot.TryGet(item.WorldGuid, out _));
        if (containers)
        {
            int nearby = world.Sent.FindIndex(p => Is(p, ProximateItems.Opcode, ProximateItems.SubOpcode));
            int binding = world.Sent.FindLastIndex(p => p.Length > 0 && p[0] is 0x86 or 0x94 or 0xc8);
            Assert.True(binding >= 0 && nearby > binding,
                "Equipment and container bindings must arrive before the nearby-loot list.");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void NullaryInteractionCancelDoesNotRestartOrCancelReload(int completedShells)
    {
        using var world = new Fixture();
        var gun = world.Inventory.CreateInstance(1374, 1);
        world.Inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        world.Inventory.TryStow(world.Inventory.CreateInstance(1511, 6));
        var ammo = new PlayerAmmoContext(world.Inventory, 0x1001, AmmoOptions.Default);
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(world.Combat, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            CombatOptions.Default, 1374, gun.Guid, Vector3.Zero, 0, results, ammo);
        var pending = Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
        for (int shell = 1; shell <= completedShells; shell++)
            Assert.NotNull(WeaponFireArm.AdvanceReload(world.Combat, pending, shell * 800, gun.Guid, world.Inventory));

        world.Send([0x09, 0x08, 0]);
        Assert.Same(pending, world.Combat.Reload);
        Assert.DoesNotContain(world.Sent, IsReload);
        Assert.Equal(completedShells, world.Combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(6 - completedShells, ammo.Count(1511));
        // 09/08 can follow an already completed door interaction and precede an in-flight
        // ReloadRequest retry. Neither packet may reset the original due time.
        long deadline = pending.DueAtMs;
        WeaponFireArm.Handle(world.Combat, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            CombatOptions.Default, 1374, gun.Guid, Vector3.Zero, deadline - 1, results, ammo);
        Assert.Same(pending, world.Combat.Reload);
        Assert.Equal(deadline, pending.DueAtMs);
        Assert.NotNull(WeaponFireArm.AdvanceReload(world.Combat, pending, deadline, gun.Guid, world.Inventory));
        Assert.Equal(completedShells + 1, world.Combat.Shooter.AmmoOf(gun.Guid));
        world.Send([0x09, 0x08, 0]);
        Assert.DoesNotContain(world.Sent, IsReload);
    }

    [Fact]
    public void IdleInteractionCancelDoesNotSendAWeaponReplyOrTouchLoot()
    {
        using var world = new Fixture();
        var loot = world.Loot.Spawn(2325, 1, Vector3.Zero);
        world.Send([0x09, 0x08, 0]);
        Assert.Empty(world.Sent);
        Assert.True(world.Loot.TryGet(loot.WorldGuid, out _));
    }

    private static bool Is(byte[] p, byte opcode, byte sub) => p.Length >= 2 && p[0] == opcode && p[1] == sub;
    private static bool IsReload(byte[] p) => p.Length >= 6 && p[0] == 0x82 && p[5] == 0x08;

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly ZoneService _service;
        private readonly SoeConnection _connection;
        public List<byte[]> Sent { get; } = [];
        public PlayerInventory Inventory { get; }
        public LootWorld Loot { get; }
        public SessionCombat Combat { get; }
        public WeaponSession Weapons { get; }

        public Fixture(bool containers = true, bool proximity = false, InventoryOptions? inventoryOptions = null,
            CombatOptions? combatOptions = null)
        {
            _service = new ZoneService(this, this, new GatewayTicketRegistry(), new ZoneOptions
                { SendProximateItems = proximity, SendContainers = containers,
                    Inventory = inventoryOptions ?? new(), Combat = combatOptions ?? CombatOptions.Default });
            var request = new SessionRequest(3, 123, 512, ZoneService.ProtocolName);
            _connection = new SoeConnection(new(IPAddress.Loopback, 12345), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(_connection);
            object state = _connection.Tag!;
            Set(state, "Authenticated", true);
            Set(state, "Guid", 0x1001ul);
            Set(state, "Visuals", CharacterVisuals.FromSelection(1, 1, 0, 0, 5));
            Set(state, "Gender", CharacterVisuals.Male);
            Set(state, "Wardrobe", new AugustWardrobeState());
            // An established in-world actor already received these before any item grants.
            Weapons = (WeaponSession)state.GetType().GetProperty("Weapons")!.GetValue(state)!;
            Weapons.MarkProjectileDefinitionsSent();
            Weapons.MarkWeaponDefinitionsSent();
            PropertyInfo match = state.GetType().GetProperty("Match")!;
            match.SetValue(state, Enum.Parse(match.PropertyType, "InMatch"));
            ulong next = 0x3100_0000_0000_0001;
            Inventory = new PlayerInventory(0x1001, () => next++, inventoryOptions);
            Inventory.Bootstrap();
            Set(state, "Inventory", Inventory);
            Loot = (LootWorld)state.GetType().GetProperty("Loot")!.GetValue(state)!;
            Combat = (SessionCombat)state.GetType().GetProperty("Combat")!.GetValue(state)!;
        }

        public void Send(byte[] packet) => _service.OnMessage(_connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), .. packet]);
        public void StopReloadForVehicleEntry() => typeof(ZoneService)
            .GetMethod("StopReloadForVehicleEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_service, [_connection, _connection.Tag]);
        public void MoveTo(Vector3 position)
        {
            var movement = (SessionMovementState)_connection.Tag!.GetType()
                .GetProperty("Movement")!.GetValue(_connection.Tag)!;
            movement.ApplyPlayer(ClientMovementUpdate.Parse(new byte[7]));
            movement.PinPlayer(position);
        }
        private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes[1..].ToArray()); }
        public void Dispose() => _connection.Disconnect();
    }
}
