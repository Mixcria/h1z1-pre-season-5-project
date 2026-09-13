using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Appearance;

public sealed class IncrementalAppearanceTests
{
    [Theory]
    [InlineData(2170u, 1u)] // helmet
    [InlineData(2114u, 10u)] // backpack
    [InlineData(1803u, 11u)] // belt, binding without a resolved mesh
    [InlineData(1374u, 76u)] // stowed shotgun
    public void ARealPickupUpdatesOnlyTheChangedEquipmentSlot(uint definition, uint slot)
    {
        using var world = new Fixture();
        world.Dress("ClientIsReady");
        Assert.Single(world.Sent, p => Is(p, 0x94, 1));
        world.Sent.Clear();

        world.Pickup(definition);

        Assert.DoesNotContain(world.Sent, p => Is(p, 0x94, 1));
        var packet = Assert.Single(world.Sent, p => Is(p, 0x94, 2));
        Assert.Equal(slot, BitConverter.ToUInt32(packet, 14));
        Assert.Equal(world.Inventory.EquipmentSlots[slot].Guid, BitConverter.ToUInt64(packet, 22));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x94, 3));
        world.Sent.Clear();
        world.Dress("repeat equipment refresh");
        Assert.DoesNotContain(world.Sent, p => p[0] == 0x94);
    }

    [Fact]
    public void ReplacingAndRemovingApparelPreservesEveryOtherClothingSlotAndFistBinding()
    {
        using var world = new Fixture();
        world.Dress("ClientIsReady");
        world.Pickup(2114);
        var original = world.Inventory.EquipmentSlots[BodySlots.Backpack];
        var replacement = world.Inventory.CreateInstance(2124, 1);
        world.Inventory.BindLoadout(replacement, original.LoadoutSlotId, BodySlots.Backpack);
        world.Sent.Clear();
        world.Dress("EquipItem 2124");
        var replace = Assert.Single(world.Sent, p => p[0] == 0x94);
        Assert.True(Is(replace, 0x94, 2));
        Assert.Equal(BodySlots.Backpack, BitConverter.ToUInt32(replace, 14));

        world.Inventory.Unbind(replacement);
        world.Sent.Clear();
        world.Dress("RemoveItem 2124");
        var remove = Assert.Single(world.Sent, p => p[0] == 0x94);
        Assert.True(Is(remove, 0x94, 3));
        Assert.Equal(BodySlots.Backpack, BitConverter.ToUInt32(remove, 18));
    }

    [Fact]
    public void RecreatedActorStillGetsCompleteBaselineWithoutAnActiveHandRow()
    {
        using var world = new Fixture();
        world.Dress("ClientIsReady");
        world.Pickup(2170);
        world.Sent.Clear();
        world.Suppressor.Forget();
        world.Dress("parachute landing");
        byte[] packet = Assert.Single(world.Sent, p => Is(p, 0x94, 1));
        var reader = new PacketReader(packet);
        reader.ReadByte(); reader.ReadByte(); reader.ReadUInt32(); reader.ReadUInt64();
        reader.ReadUInt32(); reader.ReadString(); reader.ReadString();
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            reader.ReadUInt32();
            Assert.NotEqual(BodySlots.RightHand, reader.ReadUInt32());
            reader.ReadUInt64(); reader.ReadString(); reader.ReadString();
        }
    }

    [Fact]
    public void WorldSkinApplyAndUnsetReassertTheOwnedClothesWithoutAWholeBodyDress()
    {
        using var world = new Fixture();
        world.Dress("ClientIsReady");
        var hoodie = AugustSkinCatalog.Apparel.Single(s => s.RewardItemId == 4266);
        world.Sent.Clear();
        world.Skin(hoodie, unset: false);
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x94, 1));
        var changedChest = Assert.Single(world.Sent, p => Is(p, 0x94, 2)
            && BitConverter.ToUInt32(p, 14) == BodySlots.Chest);
        world.Sent.Clear();
        world.Skin(hoodie, unset: true);
        Assert.Contains(world.Sent, p => Is(p, 0xac, 0x23)); // real skin-manager correction
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x94, 1));
        var restoredChest = Assert.Single(world.Sent, p => Is(p, 0x94, 2)
            && BitConverter.ToUInt32(p, 14) == BodySlots.Chest);
        Assert.Equal(BitConverter.ToUInt64(changedChest, 22), BitConverter.ToUInt64(restoredChest, 22));
        Assert.NotEqual(Convert.ToHexString(changedChest[38..]), Convert.ToHexString(restoredChest[38..]));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x94, 3));
    }

    [Fact]
    public void ManagerReassertsIdenticalOwnedSlotsButZoningStillForcesCompleteBaseline()
    {
        using var world = new Fixture();
        world.Dress("ClientIsReady");
        world.Sent.Clear();
        world.SkinManager();
        world.Dress("skin manager correction");
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x94, 1));
        foreach (uint slot in world.Inventory.EquipmentSlots.Keys.Where(s => s != BodySlots.RightHand))
            Assert.Contains(world.Sent, p => Is(p, 0x94, 2) && BitConverter.ToUInt32(p, 14) == slot);
        Assert.False(world.Suppressor.RequiresSlotReassert);
        world.Sent.Clear();
        world.SkinManager();
        world.Suppressor.Forget();
        world.Dress("match zoning");
        Assert.Single(world.Sent, p => Is(p, 0x94, 1));
    }

    private static bool Is(byte[] p, byte opcode, byte sub) => p.Length >= 2 && p[0] == opcode && p[1] == sub;

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly ZoneService _service;
        private readonly SoeConnection _connection;
        private readonly object _state;
        public List<byte[]> Sent { get; } = [];
        public PlayerInventory Inventory { get; }
        public AugustDressSuppressor Suppressor => (AugustDressSuppressor)Get("Dress");

        public Fixture()
        {
            _service = new ZoneService(this, this, new GatewayTicketRegistry(), new ZoneOptions { SendProximateItems = false });
            var request = new SessionRequest(3, 123, 512, ZoneService.ProtocolName);
            _connection = new SoeConnection(new(IPAddress.Loopback, 12345), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(_connection);
            _state = _connection.Tag!;
            Set("Authenticated", true);
            Set("Guid", 0x1001ul);
            Set("Gender", CharacterVisuals.Male);
            Set("Visuals", CharacterVisuals.FromSelection(CharacterVisuals.Male, 1, 1, 665, 0));
            Set("Wardrobe", new AugustWardrobeState());
            PropertyInfo match = _state.GetType().GetProperty("Match")!;
            match.SetValue(_state, Enum.Parse(match.PropertyType, "InMatch"));
            ulong next = 0x3100_0000_0000_0001;
            Inventory = new PlayerInventory(0x1001, () => next++);
            Inventory.Bootstrap();
            Set("Inventory", Inventory);
        }

        public void Dress(string reason) => typeof(ZoneService).GetMethod("SendCharacterAppearance",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_service, [_connection, _state, reason]);

        public void SkinManager() => typeof(ZoneService).GetMethod("SendSkinManagerState",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_service, [_connection, _state]);

        public void Skin(AugustSkinCatalogEntry skin, bool unset)
        {
            using var packet = new PacketWriter();
            packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
            packet.WriteByte(ZoneOpcodes.ItemsBase);
            packet.WriteByte(unset ? SkinItemSelectionRequest.RequestUnsetSkinItem
                : SkinItemSelectionRequest.RequestSetSkinItemByItemId);
            packet.WriteUInt32(1); packet.WriteUInt32(0);
            packet.WriteUInt32(SetSkinItemManager.ApparelCollectionId);
            packet.WriteUInt32(skin.CategoryPrototypeId); packet.WriteUInt32(skin.AccountItemId);
            _service.OnMessage(_connection, packet.Written.ToArray());
        }

        public void Pickup(uint definition)
        {
            var loot = ((LootWorld)Get("Loot")).Spawn(definition, 1, Vector3.Zero);
            _service.OnMessage(_connection,
                [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), 0x09, 0x07, 0, .. BitConverter.GetBytes(loot.WorldGuid)]);
        }

        private object Get(string name) => _state.GetType().GetProperty(name)!.GetValue(_state)!;
        private void Set(string name, object value) => _state.GetType().GetProperty(name)!.SetValue(_state, value);
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes[1..].ToArray()); }
        public void Dispose() => _connection.Disconnect();
    }
}
