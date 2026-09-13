using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Descent;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>Native catalog coverage and real account ownership through the menu gateway.</summary>
public sealed class VehicleSkinMenuTests
{
    [Theory]
    [InlineData(1u, 9)]
    [InlineData(2u, 4)]
    [InlineData(5u, 4)]
    [InlineData(13u, 3)]
    public void EveryAugustVehicleSkinHasItsDirectAccountItemAndClientShader(uint vehicle, int count)
    {
        // VehicleSkinVehicles has exactly these four categories; PoliceCar has no skin rows.
        Assert.Equal(new uint[] { 1, 2, 5, 13 }, VehicleSkinCatalog.All.Select(s => s.VehicleId).Distinct().Order());
        var skins = VehicleSkinCatalog.All.Where(s => s.VehicleId == vehicle).ToArray();
        Assert.Equal(count, skins.Length);
        Assert.Equal(count, skins.Select(s => s.ItemId).Distinct().Count());
        foreach (var choice in skins)
        {
            Assert.Equal(1u, choice.ModPoint);
            Assert.Contains(choice.ItemId, SetAccountItemManager.CatalogAccountItemIds);
            var cosmetic = EconomyCatalog.Default.Skins[choice.ItemId];
            Assert.Equal(choice.ItemId, cosmetic.RewardItemId);
            Assert.NotEqual(0u, cosmetic.NameLocaleId);
            Assert.NotEqual(0u, cosmetic.ImageSetId);
            Assert.True(InventoryItemFacts.TryGet(choice.ItemId, out var item));
            Assert.Equal(ItemCodeFactory.VehicleSkinShaderParameterGroupId, item.CodeFactory);
            Assert.Equal(item.Param1, choice.ShaderGroupId);
        }
        if (vehicle == ParachuteSkin.VehicleId)
        {
            Assert.Equal(ParachuteSkin.ItemIds, skins.Select(s => s.ItemId));
            Assert.Equal(new uint[] { 484, 492, 491 }, skins.Select(s => s.ShaderGroupId));
        }
    }

    [Fact]
    public void FreshAccountKeepsAllTwentySkinsLockedAndRefusesTheirEquipRequestsAcrossReconnect()
    {
        using var menu = new Menu();
        menu.Admit();
        Assert.Empty(OwnedRows(menu.Sent));
        long revision = menu.Store.GetOrCreate(Menu.Account).Revision;
        foreach (var choice in VehicleSkinCatalog.All)
            menu.Request(2, choice.VehicleId, choice.ModPoint, choice.ItemId);
        menu.Request(4);
        Assert.Empty(SelectedRows(menu.Sent));
        Assert.Empty(menu.Store.GetOrCreate(Menu.Account).Items);
        Assert.Equal(revision, menu.Store.GetOrCreate(Menu.Account).Revision);
        menu.Admit();
        Assert.Empty(OwnedRows(menu.Sent));
        menu.Request(4);
        Assert.Empty(SelectedRows(menu.Sent));
    }

    [Theory]
    [InlineData(1u, 3802u, 840u)]
    [InlineData(2u, 4296u, 1129u)]
    [InlineData(5u, 4303u, 667u)]
    [InlineData(13u, 4056u, 492u)]
    public void OnlyTheOwnedVehicleSkinIsPublishedAndItsSelectionSurvivesReconnect(
        uint vehicle, uint item, uint shader)
    {
        using var menu = new Menu();
        menu.Store.GetOrCreate(Menu.Account, new(new Dictionary<uint, uint>(),
            [new(123, item, item, 1, "explicit-test-seed")]));
        menu.Admit();
        Assert.Equal((123ul, item, 1u), Assert.Single(OwnedRows(menu.Sent)));
        menu.Request(2, vehicle, 1, item);
        Assert.Equal((vehicle, 1u, item), Assert.Single(SelectedRows(menu.Sent)));
        foreach (var locked in VehicleSkinCatalog.All.Where(s => s.ItemId != item))
            menu.Request(2, locked.VehicleId, locked.ModPoint, locked.ItemId);
        menu.Request(4);
        Assert.Equal((vehicle, 1u, item), Assert.Single(SelectedRows(menu.Sent)));
        {
            menu.Request(6, vehicle);
            byte[] reply = menu.Sent.Last(p => Is(p, 0xf2, 7));
            Assert.NotEqual(0ul, BitConverter.ToUInt64(reply, 2));
            Assert.Contains(menu.Sent, p => p[0] == ZoneOpcodes.AddLightweightVehicle);
            Assert.Contains(menu.Sent, p => p[0] == ZoneOpcodes.LightweightToFullVehicle);
            menu.Request(2, vehicle, 1, item);
            Assert.Equal(shader, BitConverter.ToUInt32(menu.Sent.Last(p => Is(p, 0xf2, 1)), 18));
        }
        menu.Admit();
        menu.Request(4);
        Assert.Equal((123ul, item, 1u), Assert.Single(OwnedRows(menu.Sent)));
        Assert.Equal((vehicle, 1u, item), Assert.Single(SelectedRows(menu.Sent)));
    }

    [Fact]
    public void RepeatedCategoryChangesPublishNewActorsBeforeSelectingThemAndClearOnClose()
    {
        using var menu = new Menu();
        menu.Admit();
        var guids = new HashSet<ulong>();
        ulong previous = 0;
        foreach (uint category in new uint[] { 1, 5, 13, 2, 1, 13 })
        {
            menu.Sent.Clear();
            menu.Request(6, category);
            int spawn = menu.Sent.FindIndex(p => p[0] == ZoneOpcodes.AddLightweightVehicle);
            int full = menu.Sent.FindIndex(p => p[0] == ZoneOpcodes.LightweightToFullVehicle);
            int select = menu.Sent.FindIndex(p => Is(p, 0xf2, 7) && BitConverter.ToUInt64(p, 2) != 0);
            Assert.True(spawn >= 0 && full > spawn && select > full);
            ulong guid = BitConverter.ToUInt64(menu.Sent[select], 2);
            Assert.True(guids.Add(guid));
            if (previous != 0)
            {
                byte[] removed = Assert.Single(menu.Sent, p => Is(p, 0x0f, 1));
                Assert.Equal(previous, BitConverter.ToUInt64(removed, 2));
            }
            Assert.DoesNotContain(menu.Sent, p => p[0] == ZoneOpcodes.VehicleBase); // no possession grant
            previous = guid;
        }
        menu.Sent.Clear();
        menu.Request(6, 0);
        Assert.Contains(menu.Sent, p => Is(p, 0x0f, 1) && BitConverter.ToUInt64(p, 2) == previous);
        Assert.All(menu.Sent.Where(p => Is(p, 0xf2, 7)), p => Assert.Equal(0ul, BitConverter.ToUInt64(p, 2)));
        Assert.DoesNotContain(menu.Sent, p => p[0] == ZoneOpcodes.AddLightweightVehicle);
    }

    [Fact]
    public void UnsettingAtvPaintRestoresDefaultPaintAndClearsTheSavedSelection()
    {
        using var menu = new Menu();
        menu.Store.GetOrCreate(Menu.Account, new(new Dictionary<uint, uint>(),
            [new(123, 4303, 4303, 1, "explicit-test-seed")]));
        menu.Admit();
        menu.Request(2, 5, 1, 4303);
        menu.Request(6, 5);
        ulong painted = BitConverter.ToUInt64(menu.Sent.Last(p => Is(p, 0xf2, 7)), 2);
        menu.Sent.Clear();
        menu.Request(3, 5, 1);
        Assert.Empty(SelectedRows(menu.Sent));
        Assert.DoesNotContain(menu.Sent, p => Is(p, 0x0f, 1));
        Assert.Contains(menu.Sent, p => Is(p, 0xf2, 1) && BitConverter.ToUInt64(p, 2) == painted
            && BitConverter.ToUInt32(p, p.Length - 4) == 229u);
    }

    private static bool Is(byte[] packet, byte opcode, byte sub) =>
        packet.Length >= 2 && packet[0] == opcode && packet[1] == sub;

    private static IReadOnlyList<(ulong Instance, uint Item, uint Count)> OwnedRows(List<byte[]> sent)
    {
        var reader = new PacketReader(sent.Last(p => Is(p, 0xac, 0x11)).AsSpan(2));
        var rows = new List<(ulong, uint, uint)>();
        for (int n = reader.ReadInt32(); n > 0; n--)
        {
            ulong instance = reader.ReadUInt64();
            Assert.Equal(instance, reader.ReadUInt64());
            uint item = reader.ReadUInt32();
            reader.ReadUInt32();
            rows.Add((instance, item, reader.ReadUInt32()));
        }
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.True(reader.AtEnd);
        return rows;
    }

    private static IReadOnlyList<(uint Vehicle, uint Mod, uint Item)> SelectedRows(List<byte[]> sent)
    {
        var reader = new PacketReader(sent.Last(p => Is(p, 0xf2, 8)).AsSpan(2));
        var rows = new List<(uint, uint, uint)>();
        for (int vehicles = reader.ReadInt32(); vehicles > 0; vehicles--)
        {
            uint vehicle = reader.ReadUInt32();
            for (int mods = reader.ReadInt32(); mods > 0; mods--)
                rows.Add((vehicle, reader.ReadUInt32(), reader.ReadUInt32()));
        }
        Assert.True(reader.AtEnd);
        return rows;
    }

    private sealed class Menu : IPacketRecorder, ITransportLog, IDisposable
    {
        public const string Account = "vehicle-menu-test";
        private const ulong Character = 4097;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-vehicle-menu", Guid.NewGuid().ToString("N"));
        private readonly GatewayTicketRegistry _tickets = new();
        private readonly ZoneService _service;
        private SoeConnection? _connection;
        public AccountEconomyStore Store { get; }
        public List<byte[]> Sent { get; } = [];

        public Menu()
        {
            Store = new(_root);
            _service = new(this, this, _tickets, new ZoneOptions
            {
                EconomyStoreRoot = _root, Skins = new SkinOptions { WardrobeStoreRoot = Path.Combine(_root, "wardrobe") },
                EnableGas = false, SendDoors = false, SendVehicles = false, SendContainers = false,
                GroundLootRadius = 0, DevGroundLootMs = 0, AutoMatchMs = 0,
            }) { Post = _ => { } };
        }

        public void Admit()
        {
            if (_connection is not null) _service.OnDisconnected(_connection, DisconnectCause.PeerRequested);
            Sent.Clear();
            var admission = _tickets.Issue(Character, "Vehicle test", accountId: Account);
            var request = new SessionRequest(3, 42, 512, ZoneService.ProtocolName);
            var endpoint = new IPEndPoint(IPAddress.Loopback, 5356);
            _connection = new(endpoint, in request, SessionSettings.WithSeed(1),
                _service.OnSessionRequest(endpoint, in request), _service, this, (_, _) => { }, now: 0);
            _service.OnConnected(_connection);
            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode); login.WriteUInt64(Character); login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol); login.WriteString(GatewayLoginRequest.AugustVersion);
            _service.OnMessage(_connection, login.Written.ToArray());
        }

        public void Request(byte sub, params uint[] words)
        {
            using var packet = new PacketWriter();
            packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            packet.WriteByte(ZoneOpcodes.VehicleSkinBase); packet.WriteByte(sub);
            foreach (uint word in words) packet.WriteUInt32(word);
            _service.OnMessage(_connection!, packet.Written.ToArray());
        }

        public void Dispose()
        {
            if (_connection is not null) _service.OnDisconnected(_connection, DisconnectCause.PeerRequested);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c" && bytes.Length > 1 && bytes[0] == 0x05) Sent.Add(bytes[1..].ToArray());
        }
    }
}
