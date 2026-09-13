using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>Real gateway transitions and recipients; these assertions do not prove native rendering.</summary>
public sealed class VehicleSkinTransitionTests
{
    [Theory]
    [InlineData(VehicleEntrySource.MountRequest)]
    [InlineData(VehicleEntrySource.InteractRequest)]
    [InlineData(VehicleEntrySource.PlayerSelect)]
    public void OrdinaryEntryUsesTheOwnedSelectionOnceAcrossAllEntryArms(VehicleEntrySource arm)
    {
        using var f = new Fixture();
        var driver = f.Add(3857);
        var observer = f.Add();
        f.Clear();
        f.Enter(driver, arm: arm);
        f.AssertPaint(861, driver, driver, observer);
        f.AssertDriver(driver);
        Assert.Single(f.Packets(driver, 0x0f, 0x3b));
        Assert.Empty(f.Packets(observer, 0x0f, 0x3b));
        Assert.True(f.IndexOf(driver, 0xf2, 1) < f.IndexOf(driver, 0x70, 2));

        f.Clear();
        foreach (var duplicate in new[] { VehicleEntrySource.MountRequest,
                     VehicleEntrySource.InteractRequest, VehicleEntrySource.PlayerSelect })
            f.Enter(driver, arm: duplicate);
        Assert.Empty(f.Notifications);
        Assert.Empty(f.Packets(driver, 0x0f, 0x3b));
        f.Ready();
        f.Exit(driver);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        Assert.Equal(0ul, f.Car.DriverGuid);
        Assert.Empty(f.Notifications);
    }

    [Fact]
    public void PassengerTakeoverAppliesTheNewDriversPaintToOnlyTheMatchAudienceAndAReturnRestoresA()
    {
        using var f = new Fixture();
        var a = f.Add(3857);
        var b = f.Add(3802);
        var observer = f.Add();
        var outOfRange = f.Add(streamed: false);
        var otherMatch = f.Add(match: 556);
        var closed = f.Add();
        closed.Disconnect();
        f.Enter(a);
        f.Ready();
        f.Exit(a);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        f.Clear();
        f.Ready();
        f.Enter(b, seat: 1);
        Assert.Empty(f.Notifications);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        f.ForgetVehicle(b); // seated recipients are eligible independently of the stream set
        f.ChangeSeat(b, 0);
        f.AssertPaint(840, b, a, b, observer);
        f.AssertDriver(b);
        Assert.Single(f.Packets(b, 0x0f, 0x3b));
        Assert.Empty(f.Packets(outOfRange, 0xf2, 1));
        Assert.Empty(f.Packets(otherMatch, 0xf2, 1));
        Assert.Empty(f.Packets(closed, 0xf2, 1));
        Assert.True(f.IndexOf(b, 0xf2, 1) < f.IndexOf(b, 0x70, 0x0b));

        f.Clear();
        f.Ready();
        f.ChangeSeat(b, 2);
        Assert.Empty(f.Notifications);
        Assert.Equal(840u, f.Car.SkinShaderGroup);
        f.Enter(a);
        f.AssertPaint(861, a, a, b, observer);
        f.AssertDriver(a);
        Assert.Equal(2, f.Car.SeatOf(f.GuidOf(b)));
        Assert.Equal(Vector3.Zero, f.Car.Position);
        Assert.DoesNotContain(f.Sent, p => p.Bytes[0] == 0xd7 || Is(p.Bytes, 0x11, 0x23));
    }

    [Theory]
    [InlineData("occupied")]
    [InlineData("moving")]
    [InlineData("cooldown")]
    [InlineData("invalid")]
    [InlineData("not-mounted")]
    public void RefusedSeatChangesDoNotRepaintOrGrantControl(string refusal)
    {
        using var f = new Fixture();
        var a = f.Add(3857);
        var b = f.Add(3802);
        f.Enter(a);
        f.Ready();
        if (refusal != "not-mounted") f.Enter(b, 1);
        if (refusal != "occupied")
        {
            f.Ready();
            f.Exit(a);
        }
        if (refusal == "moving") f.Car.LastSpeed = 1;
        if (refusal == "cooldown") f.Car.LastSeatChangeMs = Environment.TickCount64;
        ulong owner = f.Car.OwnerGuid;
        f.Clear();
        f.ChangeSeat(b, refusal == "invalid" ? uint.MaxValue : 0);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        Assert.Equal(owner, f.Car.OwnerGuid);
        Assert.Empty(f.Notifications);
        Assert.Empty(f.Packets(b, 0x0f, 0x3b));
        Assert.Empty(f.Packets(b, 0x70, 0x0b));
    }

    [Fact]
    public void AFormerDriverStillSeatedAsPassengerReceivesTheTakeoverPaint()
    {
        using var f = new Fixture();
        var a = f.Add(3857);
        var b = f.Add(3802);
        var observer = f.Add();
        f.Enter(a);
        f.Ready();
        f.Enter(b, 1);
        f.ChangeSeat(a, 2);
        f.ForgetVehicle(a);
        f.ForgetVehicle(b);
        f.Ready();
        f.Clear();
        f.ChangeSeat(b, 0);
        f.AssertPaint(840, b, a, b, observer);
        f.AssertDriver(b);
        Assert.Equal(2, f.Car.SeatOf(f.GuidOf(a)));
        Assert.Single(f.Packets(b, 0x0f, 0x3b));
        var handoff = f.Packets(a, 0x0f, 0x3b).Select(p => CharacterManagedObject.Parse(p)).ToArray();
        Assert.Equal(new[] { CharacterManagedObject.Release(f.Car.Guid),
            new CharacterManagedObject(f.Car.Guid, 0, f.GuidOf(b)) }, handoff);
        Assert.DoesNotContain(f.Sent, p => p.Bytes[0] == 0xd7 || Is(p.Bytes, 0x11, 0x23));
    }

    [Fact]
    public void RejectedEntryAndAnIdenticalVehicleGuidInAnotherMatchCannotRepaintThisCar()
    {
        using var f = new Fixture();
        var a = f.Add(3857);
        var b = f.Add(3802);
        var otherMatch = f.Add(3802, match: 556);
        f.Enter(a);
        f.Ready();
        f.Clear();
        f.Enter(b); // occupied driver seat
        Assert.Empty(f.Notifications);
        Assert.Empty(f.Packets(b, 0x0f, 0x3b));
        f.Enter(otherMatch); // same numeric actor GUID, separately owned fleet
        f.AssertDriver(a);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        Assert.Equal(otherMatch, Assert.Single(f.Notifications).Connection);
        Assert.Empty(f.Packets(a, 0xf2, 1));
        Assert.Empty(f.Packets(b, 0xf2, 1));
    }

    [Fact]
    public void RepeatedSeatReportsCannotApplyASelectionChangedWhileAlreadyDriving()
    {
        using var f = new Fixture();
        var driver = f.Add(3857, 3802);
        f.Enter(driver, 1);
        f.ChangeSeat(driver, 0);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        f.Select(driver, 3802);
        f.Clear();
        f.Ready();
        f.ChangeSeat(driver, 0); // already occupied by this driver
        f.Enter(driver);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        Assert.Empty(f.Notifications);
        Assert.Empty(f.Packets(driver, 0x0f, 0x3b));
        f.ChangeSeat(driver, 1);
        f.Ready();
        f.ChangeSeat(driver, 0);
        f.AssertPaint(840, driver, driver);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewDriverWithTheSameShaderDoesNotPublishADuplicateRepaint(bool seatTakeover)
    {
        using var f = new Fixture();
        var a = f.Add(3857);
        var b = f.Add(3857);
        f.Enter(a);
        f.Ready();
        f.Exit(a);
        f.Ready();
        if (seatTakeover) f.Enter(b, 1);
        f.Clear();
        if (seatTakeover) f.ChangeSeat(b, 0);
        else f.Enter(b);
        f.AssertDriver(b);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        Assert.Empty(f.Notifications);
        Assert.Single(f.Packets(b, 0x0f, 0x3b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnownedAndRemovedSelectionsCannotRepaintTheNextOffroader(bool revoked)
    {
        using var f = new Fixture();
        var driver = f.Add(3857);
        f.Select(driver, 3802); // unowned: the saved owned choice remains
        Assert.Equal(861u, f.Skins(driver).ShaderFor(1));
        if (revoked) f.RevokeSelections(driver);
        else f.Unset(driver);
        Assert.Empty(f.Skins(driver).Snapshot());
        Assert.Empty(VehicleSkinState.Load(f.WardrobeRoot, f.GuidOf(driver)).Snapshot());
        f.Car.SkinShaderGroup = 840;
        f.Enter(driver, 1);
        f.Clear();
        f.ChangeSeat(driver, 0);
        f.AssertPaint(838, driver, driver);
        Assert.Equal(!revoked, f.Store.GetOrCreate(f.AccountOf(driver)).Owns(3857));
        Assert.False(f.Store.GetOrCreate(f.AccountOf(driver)).Owns(3802));
    }

    [Fact]
    public void LateAndReturningViewersSpawnTheLastDriversPaintWithoutAnotherTransition()
    {
        using var f = new Fixture();
        var driver = f.Add(3857);
        var observer = f.Add();
        f.Enter(driver, 1);
        f.ChangeSeat(driver, 0);
        f.Ready();
        f.Exit(driver);
        Assert.Equal(861u, f.Car.SkinShaderGroup);
        f.Session(observer).RestreamVehiclesAt(new Vector3(9000, 0, 9000));
        Assert.False(f.Session(observer).IsVehicleStreamed(f.Car.Guid));
        var late = f.Add(streamed: false);
        f.Clear();
        foreach (var viewer in new[] { observer, late })
        {
            f.Session(viewer).RestreamVehiclesAt(f.Car.Position);
            f.AssertSpawn(viewer, 861);
            f.Session(viewer).RestreamVehiclesAt(f.Car.Position);
            Assert.Single(f.Packets(viewer, 0xd7));
        }
        Assert.Empty(f.Notifications);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void ASpawnPlannedBeforeTakeoverUsesTheShaderCurrentWhenItIsSent(bool initialSpawn, bool shaderEnabled)
    {
        using var f = new Fixture(shaderEnabled);
        var a = f.Add(3857);
        var b = f.Add(3802);
        f.Enter(a);
        f.Ready();
        f.Exit(a);
        f.Ready();
        f.Enter(b, 1);
        var late = f.Add(streamed: false);
        var pending = initialSpawn ? f.PlanInitialSpawn(late) : f.Session(late).PlanRestreamVehiclesAt(f.Car.Position);
        Assert.NotEmpty(pending);
        f.ChangeSeat(b, 0);
        Assert.Equal(840u, f.Car.SkinShaderGroup);
        f.Clear();
        foreach (var send in pending) send();
        f.AssertSpawn(late, shaderEnabled ? 840u : 0u);
    }

    private static bool Is(byte[] bytes, byte opcode, byte? sub = null) =>
        bytes.Length > 1 && bytes[0] == opcode && (!sub.HasValue || bytes[1] == sub);

    private sealed class Fixture : IPacketRecorder, ITransportLog, IDisposable
    {
        private readonly GatewayTicketRegistry _tickets = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-vehicle-transitions", Guid.NewGuid().ToString("N"));
        private readonly List<SoeConnection> _connections = [];
        public string WardrobeRoot => Path.Combine(_root, "wardrobe");
        public ZoneService Service { get; }
        public AccountEconomyStore Store { get; }
        public VehicleFleet Fleet { get; } = new(VehicleRoster.LoadDefault());
        public MatchVehicle Car { get; }
        public List<(SoeConnection Connection, byte[] Bytes)> Sent { get; } = [];
        public IEnumerable<(SoeConnection Connection, byte[] Bytes)> Notifications => Sent.Where(p => Is(p.Bytes, 0xf2, 1));

        public Fixture(bool shaderEnabled = true)
        {
            Store = new(_root);
            Service = new(this, this, _tickets, new ZoneOptions
            {
                EconomyStoreRoot = _root, Skins = new SkinOptions { WardrobeStoreRoot = WardrobeRoot },
                EnableGas = false, SendDoors = false, SendContainers = false, SendProximateItems = false,
                GroundLootRadius = 0, DevGroundLootMs = 0, AutoMatchMs = 0,
                VehicleShader = shaderEnabled, SendVehicles = true,
            }) { Post = _ => { } };
            Car = new(0x4600_0000_0000_0001, 2_000_000, Fleet.Roster.Require(1), Vector3.Zero, 0, 100_000, 7_500);
            Fleet.Add(Car);
        }

        public SoeConnection Add(uint item = 0, uint secondItem = 0, bool streamed = true, ulong match = 555)
        {
            ulong character = 0x1001ul + (ulong)_connections.Count;
            string account = $"vehicle-transition-{character}";
            Store.GetOrCreate(account, new(new Dictionary<uint, uint>(),
                new[] { item, secondItem }.Where(id => id != 0)
                    .Select(id => new OwnedAccountItem(id, id, id, 1, "explicit-test-seed")).ToArray()));
            var admission = _tickets.Issue(character, "Vehicle test", accountId: account);
            var request = new SessionRequest(3, (uint)character, 512, ZoneService.ProtocolName);
            var endpoint = new IPEndPoint(IPAddress.Loopback, 18000 + _connections.Count);
            var connection = new SoeConnection(endpoint, in request, SessionSettings.WithSeed(1),
                Service.OnSessionRequest(endpoint, in request), Service, this, (_, _) => { }, now: 0);
            Service.OnConnected(connection);
            _connections.Add(connection);
            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode); login.WriteUInt64(character); login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol); login.WriteString(GatewayLoginRequest.AugustVersion);
            Service.OnMessage(connection, login.Written.ToArray());
            if (item != 0) Select(connection, item);
            // Existing seam skips only the timed lobby/drop; all actions below use real gateway handlers.
            Session(connection).EnterMatchWithCar(asDriver: false);
            if (match == 555) Set(connection.Tag!, "Fleet", Fleet);
            Set(connection.Tag!, "BountyAdmission", new MatchAdmissionContext(match, MatchQueueKind.Public, MatchMode.Solo));
            Call("JoinSharedLoot", connection, connection.Tag);
            if (!streamed) ForgetVehicle(connection);
            return connection;
        }

        public ZoneService.VehicleTestSession Session(SoeConnection c) => Service.ForVehicleTest(c);
        public IReadOnlyList<Action> PlanInitialSpawn(SoeConnection c)
        {
            // Seed the player's position through the existing movement seam, then plan a fresh initial burst.
            Session(c).PlanRestreamVehiclesAt(Car.Position);
            ForgetVehicle(c);
            var burst = new List<Action>();
            Call("SpawnNearbyVehicles", c, c.Tag, "skin transition test", burst);
            return burst;
        }
        public ulong GuidOf(SoeConnection c) => Session(c).Guid;
        public string AccountOf(SoeConnection c) => Get<string>(c.Tag!, "AccountId");
        public VehicleSkinState Skins(SoeConnection c) => Get<VehicleSkinState>(c.Tag!, "VehicleSkins");
        public void ForgetVehicle(SoeConnection c) => Get<MatchVehicleStream>(c.Tag!, "StreamedVehicles").Clear();
        public void Clear() => Sent.Clear();
        // Advance only fixture cooldown stamps; rejection cases exercise the production guards directly.
        public void Ready() { Car.LastInteractionMs = long.MinValue; Car.LastSeatChangeMs = long.MinValue; }
        public void Exit(SoeConnection c) => Deliver(c, w => { w.WriteByte(0x70); w.WriteByte(3); w.WriteByte(0); });
        public void ChangeSeat(SoeConnection c, uint seat) => Deliver(c, w =>
        { w.WriteByte(0x70); w.WriteByte(0x0a); w.WriteUInt64(Car.Guid); w.WriteUInt32(seat); });
        public void Select(SoeConnection c, uint item) => Deliver(c, w =>
        { w.WriteByte(0xf2); w.WriteByte(2); w.WriteUInt32(1); w.WriteUInt32(1); w.WriteUInt32(item); });
        public void Unset(SoeConnection c) => Deliver(c, w =>
        { w.WriteByte(0xf2); w.WriteByte(3); w.WriteUInt32(1); w.WriteUInt32(1); });
        public void Enter(SoeConnection c, uint seat = 0, VehicleEntrySource arm = VehicleEntrySource.MountRequest) => Deliver(c, w =>
        {
            if (arm == VehicleEntrySource.MountRequest)
            { w.WriteByte(0x70); w.WriteByte(1); w.WriteUInt64(Car.Guid); w.WriteUInt32(seat); w.WriteByte(0); w.WriteByte(0); }
            else
            {
                w.WriteByte(0x09); w.WriteUInt16(arm == VehicleEntrySource.PlayerSelect ? (ushort)0x15 : (ushort)7);
                if (arm == VehicleEntrySource.PlayerSelect) w.WriteUInt64(GuidOf(c));
                w.WriteUInt64(Car.Guid);
            }
        });
        private void Deliver(SoeConnection c, Action<PacketWriter> write)
        { using var w = new PacketWriter(); write(w); Session(c).Deliver(w.Written); }

        public void RevokeSelections(SoeConnection c)
        {
            var result = Store.Execute(AccountOf(c), "revoke-test", "test", draft =>
            { foreach (var item in draft.Items) draft.Consume(item.InstanceId, item.Count); return null; });
            Assert.True(result.Succeeded);
            Call("PublishAccountEconomy", AccountOf(c), true);
        }

        public IEnumerable<byte[]> Packets(SoeConnection c, byte opcode, byte? sub = null) =>
            Sent.Where(p => p.Connection == c && Is(p.Bytes, opcode, sub)).Select(p => p.Bytes);
        public int IndexOf(SoeConnection c, byte opcode, byte sub) => Sent.FindIndex(p => p.Connection == c && Is(p.Bytes, opcode, sub));
        public void AssertDriver(SoeConnection c)
        { Assert.Equal(GuidOf(c), Car.DriverGuid); Assert.Equal(GuidOf(c), Car.OwnerGuid); Assert.Equal(0, Car.SeatOf(GuidOf(c))); }
        public void AssertPaint(uint shader, SoeConnection driver, params SoeConnection[] audience)
        {
            Assert.Equal(shader, Car.SkinShaderGroup);
            Assert.Equal(audience.Length, Notifications.Count());
            foreach (var recipient in audience)
            {
                var packet = Assert.Single(Packets(recipient, 0xf2, 1));
                var reader = new PacketReader(packet.AsSpan(2));
                Assert.Equal(Car.Guid, reader.ReadUInt64());
                Assert.Equal(GuidOf(driver), reader.ReadUInt64());
                Assert.Equal(shader, reader.ReadUInt32());
                Assert.True(reader.AtEnd);
            }
        }
        public void AssertSpawn(SoeConnection c, uint shader)
        {
            byte[] packet = Assert.Single(Packets(c, 0xd7));
            Assert.Equal(Car.Guid, BitConverter.ToUInt64(packet, 1));
            // Native 140a2d040: +0x1a4 shader is followed by one u32 and one u64.
            int bodyLength = LightweightEntityBody.MinimalLength + ClientVarInt.Length(Car.TransientId) - 1;
            Assert.Equal(shader, BitConverter.ToUInt32(packet, bodyLength - 16));
            Assert.Single(Packets(c, 0xdb));
        }
        private object? Call(string name, params object?[] args) => typeof(ZoneService)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Service, args);
        private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
        private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);
        public void Dispose()
        {
            foreach (var connection in _connections)
            { connection.Disconnect(); Service.OnDisconnected(connection, DisconnectCause.ServerRequested); }
            ((RankedScoreStore)typeof(ZoneService).GetField("_rankedScores",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!).FlushPending(TimeSpan.FromSeconds(5));
            // Disconnect queues wardrobe persistence too. Join that writer before deleting
            // its temporary directory, just as we already drain ranked persistence above.
            ((WardrobeStore)typeof(ZoneService).GetField("_wardrobeStore",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!).Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c" && bytes.Length > 1 && bytes[0] == 5) Sent.Add((connection, bytes[1..].ToArray())); }
    }
}
