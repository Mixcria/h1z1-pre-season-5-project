using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Vehicles;

// Real gateway admission, channel-2/channel-3 parsing, mount/seat/dismiss dispatch and
// production interest/streaming. Only match admission, timer deadlines and fleet placement
// are shortcuts. The recorder proves offers and ordering, not native visibility.
public sealed class VehicleOccupantReplicationTests
{
    [Fact]
    public void DriverPassengerAndObserverReceiveAttachmentsButOnlyRidersReceivePossession()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll();
        foreach (var link in new[] { driver, passenger, observer }) f.Stream(link);
        f.Clear();
        f.Enter(driver, 0); f.Enter(passenger, 1);

        AssertMount(f.Sent(passenger), GuidOf(driver), f.Car.Guid, 0, 1);
        AssertMount(f.Sent(driver), GuidOf(passenger), f.Car.Guid, 1, 0);
        AssertMount(f.Sent(observer), GuidOf(driver), f.Car.Guid, 0, 1);
        AssertMount(f.Sent(observer), GuidOf(passenger), f.Car.Guid, 1, 0);
        AssertNoPossession(f.Sent(observer));
        Assert.DoesNotContain(f.Sent(passenger), p => Is(p, 0x88, 1) || Is(p, 0x0f, 0x3b));
        foreach (var p in f.Sent(driver).Where(p => Is(p, 0x88, 2))) Assert.Equal(GuidOf(driver), U64(p, 10));
        Assert.Empty(f.Sent(outsider));
        Assert.True(Movement(driver).TryGetManaged(f.Car.TransientId, out _));
        Assert.False(Movement(passenger).TryGetManaged(f.Car.TransientId, out _));
        f.Clear();
        f.Full(observer, f.Car.Guid);
        Assert.Equal(Bytes(VehicleFullState.Create(f.Car).WriteTo), Assert.Single(f.Sent(observer)));
        f.Full(outsider, f.Car.Guid);
        Assert.Empty(f.Sent(outsider));
    }

    [Fact]
    public void ManagedCarMotionMovesBothOccupantsInterestAndRelaysOnlyTheVehicle()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll();
        foreach (var link in new[] { driver, passenger, observer }) f.Stream(link);
        f.Enter(driver, 0); f.Enter(passenger, 1);
        var oldFoot = Peer(passenger).Pose.ToArray();
        f.Clear();
        var destination = new Vector3(700, 50, 100);
        f.Car.LastPoseMs = long.MinValue;
        f.Managed(driver, destination, time: 800, version: 5);
        Assert.Equal(destination, Peer(driver).Position);
        Assert.Equal(destination, Peer(passenger).Position);
        Assert.Equal(destination, Movement(passenger).Player!.Position);
        Assert.Equal(oldFoot, Peer(passenger).Pose.ToArray());
        f.Move(observer, destination);
        Assert.DoesNotContain(f.Sent(observer), p => Is(p, 0x0f, 1));
        f.Clear();
        f.Managed(driver, null, time: 801, version: 5);
        Assert.Equal(f.Car.TransientId, PoseTransient(Assert.Single(f.Sent(observer), p => p[0] == 0x78)));
        Assert.Equal(f.Car.TransientId, PoseTransient(Assert.Single(f.Sent(passenger), p => p[0] == 0x78)));
        Assert.Empty(f.Sent(driver));
        Assert.Empty(f.Sent(outsider));
        f.Managed(passenger, new(999, 50, 100), time: 802);
        Assert.Equal(destination, f.Car.Position);
        f.Deliver(passenger, Record(Vector3.Zero), channel: 2);
        Assert.Equal(destination, Peer(passenger).Position);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LateInterestAttachesEachRiderOnlyAfterBothActorsAndCurrentFullState(bool vehicleFirst)
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add();
        f.SeeAll(); f.Stream(driver); f.Stream(passenger);
        f.Enter(driver, 0); f.Enter(passenger, 1);
        var observer = f.Add(at: f.Car.Position + new Vector3(ObserverView.PlayerLeaveMetres + 100, 0, 0));
        f.Clear();
        if (vehicleFirst)
        {
            Movement(observer).PinPlayer(f.Car.Position);
            f.Stream(observer);
            f.Move(observer, f.Car.Position);
        }
        else
        {
            f.Move(observer, f.Car.Position);
            Assert.DoesNotContain(f.Sent(observer), p => Is(p, 0x70, 2));
            var deferred = f.Plan(observer);
            Assert.True(Get<MatchVehicleStream>(observer, "StreamedVehicles").IsStreamed(f.Car.Guid));
            Assert.False(Get<MatchVehicleStream>(observer, "StreamedVehicles").IsSpawned(f.Car.Guid));
            f.Full(observer, f.Car.Guid);
            f.Car.LastPoseMs = long.MinValue;
            f.Managed(driver, new(102, 50, 100), time: 900, version: 5);
            Call(f.Service, "SetVehicleHorn", driver, driver.Tag, f.Car, true);
            Assert.DoesNotContain(f.Sent(observer), p => p[0] is 0x78 or 0xdb);
            Assert.DoesNotContain(f.Sent(observer), p => p[0] == 0x0f);
            foreach (var action in deferred) action();
        }
        var sent = f.Sent(observer);
        int spawn = sent.FindIndex(p => p[0] == 0xd7);
        int full = sent.FindIndex(p => p[0] == 0xdb);
        Assert.True(spawn >= 0 && full > spawn);
        Assert.Equal(Bytes(VehicleFullState.Create(f.Car).WriteTo), sent[full]);
        foreach (var link in new[] { driver, passenger })
        {
            int pc = sent.FindIndex(p => p[0] == 0xd5 && U64(p, 1) == GuidOf(link));
            int mount = sent.FindIndex(p => Is(p, 0x70, 2) && U64(p, 2) == GuidOf(link));
            Assert.True(pc >= 0 && mount > pc && mount > full);
        }
        AssertNoPossession(sent);
        f.Full(observer, f.Car.Guid);
        Assert.Equal(2, f.Sent(observer).Count(p => Is(p, 0x70, 2)));
        if (!vehicleFirst)
        {
            var expected = (AddLightweightVehicle)Call(f.Service, "VehicleSpawn", f.Car)!;
            Assert.Equal(Bytes(expected.WriteTo), sent[spawn]);
            Assert.Equal(5, expected.PositionUpdate!.Byte6);
            Assert.Equal(900u, expected.PositionUpdate.SequenceTime);
            Assert.Equal(new Vector3(102, 50, 100), expected.Position);
        }
    }

    [Fact]
    public void SeatChangesDismountAndReentryUpdateObserversAndTransferOnlyDriverControl()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add();
        f.SeeAll(); foreach (var c in new[] { driver, passenger, observer }) f.Stream(c);
        f.Enter(driver, 0); f.Enter(passenger, 1); f.Clear();
        f.Seat(driver, 2);
        Assert.Equal(GuidOf(driver), f.Car.CoastingOwnerGuid);
        f.Seat(passenger, 0);
        Assert.Equal(GuidOf(passenger), f.Car.DriverGuid);
        Assert.False(Movement(driver).TryGetManaged(f.Car.TransientId, out _));
        Assert.True(Movement(passenger).TryGetManaged(f.Car.TransientId, out _));
        foreach (var c in new[] { driver, passenger })
        {
            var p = Assert.Single(f.Sent(observer), p => Is(p, 0x70, 0x0b) && U64(p, 2) == GuidOf(c));
            Assert.Equal(c == driver ? 2u : 0u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(54)));
            Assert.Equal(c == driver ? 0u : 1u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(58)));
        }
        AssertNoPossession(f.Sent(observer));
        f.Clear();
        f.Exit(driver);
        Assert.Single(f.Sent(observer), p => Is(p, 0x70, 4) && U64(p, 2) == GuidOf(driver));
        Assert.Single(f.Sent(passenger), p => Is(p, 0x88, 2) && U64(p, 10) == GuidOf(passenger));
        Assert.DoesNotContain(f.Sent(observer), p => Is(p, 0x0f, 1));
        f.Enter(driver, 1);
        AssertMount(f.Sent(observer), GuidOf(driver), f.Car.Guid, 1, 0);
    }

    [Fact]
    public void InterestLeaveAndDriverDisconnectDismountBeforeDespawnAndAllowPassengerToDrive()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll(); foreach (var c in new[] { driver, passenger, observer }) f.Stream(c);
        f.Enter(driver, 0); f.Enter(passenger, 1);
        uint oldTransient = Peer(observer).View.Transients.Acquire(Peer(driver).Key);
        f.Clear(); f.Move(observer, f.Car.Position + new Vector3(ObserverView.PlayerLeaveMetres + 100, 0, 0));
        AssertDismountBeforeRemoval(f.Sent(observer), GuidOf(driver));
        Assert.Empty(Peer(observer).VehicleAttachments);
        f.Clear(); f.Move(observer, f.Car.Position);
        AssertMount(f.Sent(observer), GuidOf(driver), f.Car.Guid, 0, 1);
        Assert.True(Peer(observer).View.Transients.TryResolve(oldTransient, out _));
        f.Clear(); f.Disconnect(driver);
        AssertDismountBeforeRemoval(f.Sent(observer), GuidOf(driver));
        Assert.Single(f.Sent(observer), p => p[0] == 0x78); // final parked pose even though the source link is closed
        Assert.Equal(0ul, f.Car.OwnerGuid);
        Assert.Equal(0ul, f.Car.CoastingOwnerGuid);
        Assert.False(Movement(driver).TryGetManaged(f.Car.TransientId, out _));
        Assert.Equal(1, f.Car.SeatOf(GuidOf(passenger)));
        f.Seat(passenger, 0);
        Assert.Equal(GuidOf(passenger), f.Car.OwnerGuid);
        Assert.Empty(f.Sent(outsider));
    }

    [Fact]
    public void RefusedOrFormerDriverMovementCannotPolluteManagedState()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add();
        f.Stream(driver); f.Stream(passenger); f.Enter(driver, 0); f.Enter(passenger, 1);
        f.Managed(driver, f.Car.Position, time: 1);
        Assert.True(Movement(driver).TryGetManaged(f.Car.TransientId, out var before));
        f.Managed(driver, new(100000, 50, 100), time: 2);
        Assert.True(Movement(driver).TryGetManaged(f.Car.TransientId, out var after));
        Assert.Same(before, after);
        Assert.Equal(1u, f.Car.LastClientTime);
        f.Exit(driver); f.Seat(passenger, 0);
        // Explicitly reproduce a stale managed-registration seam; fleet ownership must still win.
        Movement(driver).RegisterManagedEntity(f.Car.TransientId, f.Car.Guid);
        f.Clear(); f.Managed(driver, new(101, 50, 100), time: 3);
        Assert.True(Movement(driver).TryGetManaged(f.Car.TransientId, out var stale));
        Assert.Null(stale.Movement);
        Assert.Empty(f.Sent(passenger));
        Assert.Equal(GuidOf(passenger), f.Car.OwnerGuid);
    }

    [Fact]
    public void SparseNativeStopAllowsSeatChangeWithoutRestampingPosition()
    {
        using var f = new Fixture();
        var driver = f.Add(); f.Stream(driver); f.Enter(driver, 0);
        f.Managed(driver, f.Car.Position, time: 1);
        f.Car.LastSpeed = 20;
        long poseAt = f.Car.LastPoseMs;
        f.Managed(driver, null, time: 2, stopped: true);
        Assert.Equal(poseAt, f.Car.LastPoseMs);
        Assert.Equal(0f, f.Car.LastSpeed);
        f.Seat(driver, 1);
        Assert.Equal(1, f.Car.SeatOf(GuidOf(driver)));
    }

    [Fact]
    public void RealManagedDescentKeepsObserversOwnPossessionAndHandsLandingBackToFoot()
    {
        using var f = new Fixture();
        var rider = f.Add(); var viewer = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll();
        f.Drop(rider); f.Drop(viewer);
        ulong chute = Get<ulong>(rider, "ChuteGuid");
        ulong viewersChute = Get<ulong>(viewer, "ChuteGuid");
        Assert.Equal(0ul, Peer(rider).ParachuteGuid);
        f.Clear();
        f.Managed(rider, new(100, 400, 100), time: 200, version: 5, transient: 2);
        var sent = f.Sent(viewer);
        var spawn = Assert.Single(sent, p => p[0] == 0xd7);
        Assert.Equal(chute, U64(spawn, 1));
        uint canopyTransient = Peer(viewer).VisibleParachutes[GuidOf(rider)].TransientId;
        Assert.NotEqual(2u, canopyTransient);
        Assert.Equal(canopyTransient, PoseTransient(Assert.Single(sent, p => p[0] == 0x78)));
        AssertMount(sent, GuidOf(rider), chute, 0, 1);
        Assert.True(sent.FindIndex(p => p[0] == 0xdb) < sent.FindIndex(p => Is(p, 0x70, 2)));
        AssertNoPossession(sent);
        Assert.Equal(viewersChute, Get<ulong>(viewer, "ChuteGuid"));
        Assert.Equal(1, Movement(viewer).ManagedEntityCount);
        f.Full(viewer, chute);
        Assert.Equal(Bytes(new LightweightToFullVehicle(canopyTransient, chute,
            Occupants: [new(0, GuidOf(rider))]).WriteTo), f.Sent(viewer)[^1]);
        f.Full(outsider, chute); Assert.Empty(f.Sent(outsider));
        // Naming an observer transient on channel 3 cannot grant simulation authority.
        f.Managed(viewer, new(100, 1, 100), transient: canopyTransient);
        Assert.Equal(new Vector3(100, 400, 100), Peer(rider).Position);
        f.Managed(rider, new(100, 55, 100), time: 250, transient: 2);
        f.Clear();
        f.Deliver(rider, Bytes(w => { w.WriteByte(0x88); w.WriteByte(0x18); w.WriteUInt64(0); }));
        sent = f.Sent(viewer);
        int dismount = sent.FindIndex(p => Is(p, 0x70, 4));
        int remove = sent.FindIndex(p => Is(p, 0x0f, 1) && U64(p, 2) == chute);
        Assert.True(dismount >= 0 && remove > dismount);
        AssertNoPossession(sent);
        Assert.Equal(0ul, Peer(rider).ParachuteGuid);
        Assert.True(Movement(rider).AwaitingPostDismountPose);
        Assert.Equal(new Vector3(100, 55, 100), Movement(rider).Player!.Position);
        Assert.Empty(Peer(viewer).VisibleParachutes);
        Assert.False(Peer(viewer).View.Transients.TryResolve(canopyTransient, out _));
        f.Clear();
        f.Deliver(rider, Record(null, time: 251), channel: 2);
        Assert.Empty(f.Sent(viewer));
        f.Move(rider, new(102, 55, 100));
        var foot = Assert.Single(f.Sent(viewer), p => p[0] == 0x78);
        Assert.NotEqual(canopyTransient, PoseTransient(foot));
        Assert.False(Movement(rider).AwaitingPostDismountPose);
        f.Clear(); f.Drop(rider); f.Managed(rider, new(102, 400, 100), transient: 2);
        Assert.Equal(canopyTransient, Peer(viewer).VisibleParachutes[GuidOf(rider)].TransientId);
        Assert.Single(f.Sent(viewer), p => p[0] == 0xd7);
        Assert.Equal(viewersChute, Get<ulong>(viewer, "ChuteGuid"));
    }

    [Fact]
    public void WreckDismountsBothObservedRidersBeforeReplacingTheVehicleModel()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add();
        f.SeeAll(); foreach (var c in new[] { driver, passenger, observer }) f.Stream(c);
        f.Enter(driver, 0); f.Enter(passenger, 1); f.Clear();
        Call(f.Service, "ApplyVehicleDamage", driver, driver.Tag, f.Car, 100000u, "test wreck", false, null);
        var sent = f.Sent(observer);
        var wreck = Bytes(new VehicleDestroyedPacket(f.Car.Guid,
            VehicleCombatBalance.DestroyedModel(f.Car.Definition.VehicleId)).WriteTo);
        int model = sent.FindIndex(p => p.SequenceEqual(wreck));
        Assert.True(model >= 0);
        foreach (var c in new[] { driver, passenger })
        {
            Assert.Single(sent, p => Is(p, 0x70, 4) && U64(p, 2) == GuidOf(c));
            Assert.True(sent.FindIndex(p => Is(p, 0x70, 4) && U64(p, 2) == GuidOf(c)) < model);
        }
        Assert.Empty(Peer(observer).VehicleAttachments);
        AssertNoPossession(sent);
        Assert.Equal(0, Movement(driver).ManagedEntityCount);
    }

    private static byte[] Bytes(Action<PacketWriter> write)
    { using var w = new PacketWriter(); write(w); return w.Written.ToArray(); }
    private static ulong U64(byte[] p, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(offset));
    private static bool Is(byte[] p, byte family, byte sub) => p.Length > 1 && p[0] == family && p[1] == sub;
    private static void AssertNoPossession(IEnumerable<byte[]> packets) => Assert.DoesNotContain(packets,
        p => Is(p, 0x88, 1) || Is(p, 0x88, 2) || Is(p, 0x88, 0x19) || Is(p, 0x0f, 0x3b) || Is(p, 0x11, 0x39));
    private static void AssertMount(List<byte[]> sent, ulong rider, ulong car, uint seat, uint driver)
    {
        var packet = Assert.Single(sent, p => Is(p, 0x70, 2) && U64(p, 2) == rider);
        Assert.Equal(car, U64(packet, 10));
        Assert.Equal(seat, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(18)));
        Assert.Equal(driver, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(26)));
    }
    private static void AssertDismountBeforeRemoval(List<byte[]> sent, ulong rider)
    {
        int dismount = sent.FindIndex(p => Is(p, 0x70, 4) && U64(p, 2) == rider);
        int remove = sent.FindIndex(p => Is(p, 0x0f, 1) && U64(p, 2) == rider);
        Assert.True(dismount >= 0 && remove > dismount);
    }
    private static uint PoseTransient(byte[] packet) => ClientManagedMovementUpdate.Parse([0x90, .. packet[1..]]).TransientId;
    private static T Get<T>(SoeConnection c, string name) => (T)c.Tag!.GetType().GetProperty(name)!.GetValue(c.Tag)!;
    private static void Set(SoeConnection c, string name, object value) => c.Tag!.GetType().GetProperty(name)!.SetValue(c.Tag, value);
    private static ulong GuidOf(SoeConnection c) => Get<ulong>(c, "Guid");
    private static PeerSession Peer(SoeConnection c) => Get<PeerSession>(c, "Peer");
    private static SessionMovementState Movement(SoeConnection c) => Get<SessionMovementState>(c, "Movement");
    private static object? Call(ZoneService service, string name, params object?[] args) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args);

    private static byte[] Record(Vector3? at, uint time = 100, byte version = 0, bool stopped = false) => Bytes(w =>
    {
        w.WriteUInt16((ushort)((at.HasValue ? 0x22 : 0) | (stopped ? 0x19 : 0)));
        w.WriteUInt32(time); w.WriteByte(version);
        if (stopped) ClientVarInt.Write(w, 0x49);
        if (at is Vector3 p)
        {
            ClientPackedInt.Write(w, (int)MathF.Round(p.X * 100));
            ClientPackedInt.Write(w, (int)MathF.Round(p.Y * 100));
            ClientPackedInt.Write(w, (int)MathF.Round(p.Z * 100));
            w.WriteSingle(1.25f);
        }
        if (stopped) { ClientPackedInt.Write(w, 0); ClientPackedInt.Write(w, 0); }
    });

    private sealed class Fixture : IDisposable, ITransportLog, IPacketRecorder
    {
        private readonly GatewayTicketRegistry _tickets = new();
        private readonly List<SoeConnection> _links = [];
        private readonly List<(SoeConnection Link, byte[] Packet)> _sent = [];
        public ZoneService Service { get; }
        public VehicleFleet Fleet { get; } = new(VehicleRoster.LoadDefault());
        public MatchVehicle Car { get; }
        public Fixture()
        {
            Service = new(this, this, _tickets, new ZoneOptions
            {
                AutoMatchMs = 0, EnableGas = false, SendDoors = false, SendContainers = false,
                GroundLootRadius = 0, DevGroundLootMs = 0,
            }) { Post = _ => { } }; // Timers are explicitly driven; no background world mutations.
            Car = new(0x4600000000000001, 500000, Fleet.Roster.Require(1), new(100, 50, 100), 1.25f, 100000, 5000);
            Fleet.Add(Car);
        }
        public SoeConnection Add(Vector3? at = null, ulong match = 1)
        {
            int n = _links.Count + 1;
            var admission = _tickets.Issue((ulong)(0x1000 + n), $"Vehicle{n}", 2, 3, 2, 664, 270);
            var request = new SessionRequest(3, (uint)n, 512, ZoneService.ProtocolName);
            var endpoint = new IPEndPoint(IPAddress.Loopback, 18000 + n);
            var link = new SoeConnection(endpoint, in request, SessionSettings.WithSeed(1),
                Service.OnSessionRequest(endpoint, in request), Service, this, (_, _) => { }, 0);
            _links.Add(link); Service.OnConnected(link);
            Service.OnMessage(link, Bytes(w =>
            {
                w.WriteByte(GatewayLoginRequest.Opcode); w.WriteUInt64(admission.Guid); w.WriteString(admission.Ticket);
                w.WriteString(GatewayLoginRequest.AugustProtocol); w.WriteString(GatewayLoginRequest.AugustVersion);
            }));
            Assert.True(Get<bool>(link, "Authenticated"));
            Assert.NotNull(Peer(link));
            Service.ForTest(link).EnterMatch();
            Set(link, "BountyAdmission", new MatchAdmissionContext(match, MatchQueueKind.Public, MatchMode.Solo));
            Call(Service, "JoinSharedLoot", link, link.Tag);
            Set(link, "Fleet", match == 1 ? Fleet : new VehicleFleet(Fleet.Roster));
            Call(Service, "NotePeerInMatch", link.Tag, true);
            Move(link, at ?? Car.Position);
            return link;
        }
        public void Deliver(SoeConnection c, byte[] packet, byte channel = 0) => Service.OnMessage(c,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, channel).ToByte(), .. packet]);
        public void Move(SoeConnection c, Vector3 at)
        { Set(c, "NextPeerInterestMs", 0L); Deliver(c, Record(at), channel: 2); }
        public void SeeAll() { foreach (var c in _links) Move(c, Peer(c).Position); }
        public List<Action> Plan(SoeConnection c)
        {
            // Register the same observer the real initial vehicle burst registers, without
            // populating 300 unrelated parking pads. Restream actions are the production path.
            var observers = typeof(ZoneService).GetField("_vehiclePoses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Service) as VehiclePoseBroadcast;
            var type = typeof(ZoneService).GetNestedType("SessionVehicleObserver", BindingFlags.NonPublic)!;
            observers!.Register((IVehicleObserver)Activator.CreateInstance(type, Service, c, c.Tag)!);
            Set(c, "VehicleObserverRegistered", true);
            var actions = new List<Action>(); Call(Service, "PlanVehicleRestream", c, c.Tag, actions); return actions;
        }
        public void Stream(SoeConnection c) { foreach (var action in Plan(c)) action(); }
        public void Enter(SoeConnection c, uint seat)
        {
            Car.LastInteractionMs = long.MinValue;
            Deliver(c, Bytes(w => { w.WriteByte(0x70); w.WriteByte(1); w.WriteUInt64(Car.Guid); w.WriteUInt32(seat); w.WriteByte(0); w.WriteByte(0); }));
        }
        public void Seat(SoeConnection c, uint seat)
        {
            Car.LastSeatChangeMs = long.MinValue;
            Deliver(c, Bytes(w => { w.WriteByte(0x70); w.WriteByte(0x0a); w.WriteUInt64(Car.Guid); w.WriteUInt32(seat); }));
        }
        public void Exit(SoeConnection c) { Car.LastInteractionMs = long.MinValue; Deliver(c, [0x70, 3, 0]); }
        public void Full(SoeConnection c, ulong guid) => Deliver(c, Bytes(w => { w.WriteByte(0x0f); w.WriteByte(0x45); w.WriteUInt64(guid); }));
        public void Managed(SoeConnection c, Vector3? at, uint time = 100, byte version = 0, uint? transient = null, bool stopped = false) =>
            Deliver(c, Bytes(w => { w.WriteByte(0x90); ClientVarInt.Write(w, transient ?? Car.TransientId); w.WriteRaw(Record(at, time, version, stopped)); }), channel: 3);
        public void Drop(SoeConnection c)
        {
            Set(c, "Match", Enum.Parse(c.Tag!.GetType().GetProperty("Match")!.PropertyType, "Dropping"));
            Call(Service, "SendParachute", c, c.Tag, new Vector4(100, 500, 100, 1));
            Deliver(c, Bytes(new VehicleAutoMount(Get<ulong>(c, "ChuteGuid"), Flag: false).WriteTo));
            Deliver(c, [ZoneOpcodes.SynchronizedTeleportBase, (byte)SynchronizedTeleport.ClientReady, 0]);
            Assert.True(Get<bool>(c, "MountRequested"));
            Assert.True(Get<bool>(c, "Released"));
        }
        public List<byte[]> Sent(SoeConnection c) => _sent.Where(row => row.Link == c).Select(row => row.Packet).ToList();
        public void Clear() => _sent.Clear();
        public void Disconnect(SoeConnection c)
        { if (c.State != ConnectionState.Open) return; c.Disconnect(); Service.OnDisconnected(c, DisconnectCause.ServerRequested); }
        public void Dispose() { foreach (var c in _links) Disconnect(c); }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection c, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection c, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c" && bytes.Length > 1) _sent.Add((c, bytes[1..].ToArray())); }
    }
}
