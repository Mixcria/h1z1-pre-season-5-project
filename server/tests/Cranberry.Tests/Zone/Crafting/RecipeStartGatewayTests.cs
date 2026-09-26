using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Crafting;

public sealed class RecipeStartGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedCountsCannotCraftOrStartACastAndTheNextNativeRequestIsAccepted(bool castBar)
    {
        using var gateway = new Gateway(castBar);
        byte[] valid = Convert.FromHexString("091A007709000001000000");
        var before = gateway.Inventory.Items.Values.OrderBy(item => item.Guid)
            .Select(item => (item.Guid, item.Count)).ToArray();

        // The former no-count fallback accepted the seven-byte prefix as one craft.
        foreach (int length in new[] { 6, 7, 8, 9, 10 })
        {
            gateway.Send(valid[..length]);
            Assert.Equal(before, gateway.Inventory.Items.Values.OrderBy(item => item.Guid)
                .Select(item => (item.Guid, item.Count)).ToArray());
            Assert.Null(gateway.PendingCraft);
            Assert.Equal(0L, gateway.CraftBusyUntil);
            Assert.Empty(gateway.Sent);
            Assert.Equal(ConnectionState.Open, gateway.Connection.State);
        }

        gateway.Send(valid);

        if (castBar)
        {
            Assert.NotNull(gateway.PendingCraft);
            Assert.Equal(before, gateway.Inventory.Items.Values.OrderBy(item => item.Guid)
                .Select(item => (item.Guid, item.Count)).ToArray());
            Assert.Contains(gateway.Sent, bytes => bytes.Length > 2 && bytes[1] == 0xcf && bytes[2] == 2);
        }
        else
        {
            Assert.Null(gateway.PendingCraft);
            Assert.DoesNotContain(gateway.Inventory.Items.Values, item => item.DefinitionId == 23);
            Assert.Equal(1u, Assert.Single(gateway.Inventory.Items.Values,
                item => item.DefinitionId == CraftingCatalog.FieldBandage).Count);
            Assert.Contains(gateway.Sent, bytes => bytes.Length > 3 && bytes[1] == 0x11 && bytes[2] == 2);
        }
    }

    private sealed class Gateway : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly ZoneService _service;
        private readonly object _state;
        public SoeConnection Connection { get; }
        public PlayerInventory Inventory { get; }
        public List<byte[]> Sent { get; } = [];
        public object? PendingCraft => _state.GetType().GetProperty("PendingCraft")!.GetValue(_state);
        public long CraftBusyUntil => (long)_state.GetType().GetProperty("CraftBusyUntil")!.GetValue(_state)!;

        public Gateway(bool castBar)
        {
            const ulong character = 0x1001;
            var tickets = new GatewayTicketRegistry();
            GatewayAdmission admission = tickets.Issue(character, "Recipe codec test");
            _service = new ZoneService(this, this, tickets, new ZoneOptions
            {
                BootstrapDelayMs = 1000, EnableGas = false, SendDoors = false,
                SendVehicles = false, AutoMatchMs = 0, DevGroundLootMs = 0,
                Crafting = new CraftingOptions { CraftCastBar = castBar, RetailRecipes = true },
            }) { Post = _ => { } }; // No world/timer callbacks are pumped in this codec regression.
            var endpoint = new IPEndPoint(IPAddress.Loopback, 5556);
            var session = new SessionRequest(3, 0x11223345, 512, ZoneService.ProtocolName);
            Connection = new SoeConnection(endpoint, in session, SessionSettings.WithSeed(1),
                _service.OnSessionRequest(endpoint, in session), _service, this, (_, _) => { }, now: 0);
            _service.OnConnected(Connection);
            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode);
            login.WriteUInt64(character);
            login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol);
            login.WriteString(GatewayLoginRequest.AugustVersion);
            _service.OnMessage(Connection, login.Written.ToArray());

            _state = Connection.Tag!;
            Assert.True((bool)_state.GetType().GetProperty("Authenticated")!.GetValue(_state)!);
            var loot = (LootWorld)_state.GetType().GetProperty("Loot")!.GetValue(_state)!;
            Inventory = new PlayerInventory(character, loot.NextItemGuid, new InventoryOptions { StarterOutfit = [] });
            Inventory.Bootstrap();
            Inventory.RemoveUnits(Inventory.LoadoutSlots[SurvivorLoadout.QuickUse1].Guid, 0);
            foreach (var ingredient in CraftingCatalog.RetailRecipes.Single(recipe =>
                recipe.RecipeId == CraftingCatalog.FieldBandage).Ingredients)
            {
                Inventory.TryPickUp(ingredient.ItemDefinitionId, ingredient.Quantity, out var granted);
                Assert.NotNull(granted);
            }
            _state.GetType().GetProperty("Inventory")!.SetValue(_state, Inventory);
            Sent.Clear();
        }

        public void Send(byte[] payload) => _service.OnMessage(Connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte(), .. payload]);

        public void Dispose()
        {
            Connection.Disconnect();
            _service.OnDisconnected(Connection, DisconnectCause.ServerRequested);
            ((WardrobeStore)typeof(ZoneService).GetField("_wardrobeStore",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!).Dispose();
        }

        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> ciphertext) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c") Sent.Add(bytes.ToArray());
        }
    }
}
