using System.Collections.Concurrent;
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
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Descent;

public sealed class DescentWorldStreamingTests
{
    [Fact]
    public void DefaultVehicleVisibilityIsFilledDuringDescentAndRetainedAfterTouchdown()
    {
        var defaults = new ZoneOptions();
        using var world = new Fixture(Fixture.DefaultOptions with
        {
            GroundLootRadius = 0, SendDoors = false,
            VehicleRadius = defaults.VehicleRadius,
            VehicleAirborneRadius = defaults.VehicleAirborneRadius,
            VehicleMaxPerBurst = defaults.VehicleMaxPerBurst,
            VehicleStream = defaults.VehicleStream with { RestreamIntervalMs = 1_000_000 },
        });
        // A dense approach spans the actual 1,500 m native draw range. The former
        // 750 m/12-car descent burst and 250 m ground disc silently hid these cars.
        for (uint index = 0; index < 64; index++)
            world.AddCar(600 + index, 200 + index, Fixture.Drop + new Vector3(400 + 15 * index, 0, 0));
        world.Release();
        world.DrainStartup();
        Assert.Equal(65, world.VehicleSpawns.Distinct().Count());

        world.MoveChute(Fixture.Drop with { Y = Fixture.Drop.Y + 1 });
        world.Invoke("SendParachuteDismountBurst", "vehicle visibility regression");
        world.DrainStartup();
        world.Set("NextVehiclePumpMs", 0L);
        world.Invoke("PumpWorld", (int?)null);

        Assert.Equal(65, world.Get<MatchVehicleStream>("StreamedVehicles").LiveCount);
        Assert.Equal(65, world.VehicleSpawns.Count()); // No remove/recreate at landing.
        Assert.DoesNotContain(world.Sent, p => p.Length >= 10 && p[0] == RemovePlayer.Opcode
            && p[1] == RemovePlayer.SubOpcode && BitConverter.ToUInt64(p, 2) is >= 600 and < 664);
    }

    [Fact]
    public void AirbornePreviewIncludesDistantCarsWithAnExplicitNativeDrawDistance()
    {
        using var world = new Fixture(Fixture.DefaultOptions with
        {
            VehicleAirborneRadius = 750, VehicleMaxPerBurst = 12,
        });
        world.AddCar(501, 100, Fixture.Drop + new Vector3(700, 0, 0));
        world.AddCar(502, 101, Fixture.Drop + new Vector3(800, 0, 0));
        world.Release();
        world.DrainStartup();
        Assert.Contains(501ul, world.VehicleSpawns);
        Assert.DoesNotContain(502ul, world.VehicleSpawns);
        foreach (var packet in world.Sent.Where(p => p[0] == ZoneOpcodes.AddLightweightVehicle))
        {
            int varintLength = (packet[9] & 3) + 1;
            int bodyLength = LightweightEntityBody.MinimalLength + varintLength - 1;
            Assert.Equal(1500f, BitConverter.ToSingle(packet, bodyLength - 20));
        }
    }

    [Fact]
    public void ReleaseStreamsDoorsLootAndCarsBeforeLandingAtTheDropRatherThanTheOldPlayerPose()
    {
        using var world = new Fixture();
        world.Release();
        world.DrainStartup();

        Assert.Equal(0x2001ul, world.Get<ulong>("ChuteGuid"));
        Assert.False(world.Get<bool>("MountRequested")); // Release need not wait for the auto-mount echo.
        Assert.Equal(Fixture.Staging, world.Get<SessionMovementState>("Movement").Player!.Position);
        Assert.InRange(world.Get<MatchDoors>("Doors").Count, 1, 2);
        Assert.Contains(world.Sent, p => p[0] == ZoneOpcodes.AddLightweightNpc);
        Assert.InRange(world.Get<MatchLoot>("StreamedLoot").LiveCount, 1, 3);
        Assert.Equal(500ul, Assert.Single(world.VehicleSpawns));
        Assert.False(world.Get<bool>("DevGroundLootArmed"));
        Assert.DoesNotContain(world.Sent, p => p[0] == ZoneOpcodes.AddLightweightNpc
            && BitConverter.ToUInt64(p, 1) == PracticeTargetPack.DefaultWorldGuidBase);
    }

    [Fact]
    public void AcceptedManagedSteeringMovesInitialAndRecurringInterestWithoutPromotingThePlayer()
    {
        using var world = new Fixture(Fixture.DefaultOptions with
        {
            GroundLootRadius = 0, SendDoors = false,
            VehicleStream = new VehicleStreamOptions
            {
                Enabled = true, RestreamIntervalMs = 1_000_000,
                StreamRadiusMetres = 50, DespawnRadiusMetres = 75, MaxLive = 2, MaxPerRestream = 1,
            },
        });
        Vector3 away = Fixture.Drop + new Vector3(1000, 0, 0);
        world.AddCar(600, 101, away);
        world.Release();
        world.MoveChute(away with { Y = 800 });
        world.DrainStartup();
        Assert.Equal(600ul, Assert.Single(world.VehicleSpawns));

        world.MoveChute(Fixture.Drop with { Y = 700 });
        world.Invoke("PumpWorld", (int?)null);
        Assert.Equal(new ulong[] { 600, 500 }, world.VehicleSpawns);
        Assert.Contains(world.Sent, p => p.Length >= 10 && p[0] == RemovePlayer.Opcode
            && p[1] == RemovePlayer.SubOpcode && BitConverter.ToUInt64(p, 2) == 600);
        Assert.Equal(Fixture.Staging, world.Get<SessionMovementState>("Movement").Player!.Position);
    }

    [Fact]
    public void HighAltitudeVehicleBudgetChoosesHorizontalNearestInsteadOfTheHighestHill()
    {
        using var world = new Fixture(Fixture.DefaultOptions with { GroundLootRadius = 0, SendDoors = false });
        world.AddCar(501, 100, Fixture.Drop + new Vector3(20, 650, 0));
        world.Release();
        world.DrainStartup();
        Assert.Equal(500ul, Assert.Single(world.VehicleSpawns));
    }

    [Fact]
    public void MovementCannotStartWorldBeforeReleaseOrArmRepeatedBurstsDuringDescent()
    {
        using var world = new Fixture();
        world.MoveChute(Fixture.Drop with { Y = 840 });
        Assert.False(world.Get<bool>("RealGroundLootArmed"));
        Assert.False(world.Get<bool>("VehiclesArmed"));
        Assert.Empty(world.VehicleSpawns);
        world.Release();
        for (int index = 0; index < 20; index++)
            world.MoveChute(Fixture.Drop with { Y = 830 - index });
        world.DrainStartup();
        Assert.Single(world.Logs, line => line.Contains("ground loot armed by parachute descent", StringComparison.Ordinal));
        Assert.Single(world.VehicleSpawns);
    }

    [Fact]
    public void AbandonedWorldMakesQueuedDescentSpawnsInert()
    {
        using var world = new Fixture();
        world.Release();
        Action due = world.TakeTimer();
        int sent = world.Sent.Count;
        world.Set("WorldGeneration", 1);
        due();
        Assert.Equal(sent, world.Sent.Count);
        Assert.Empty(world.VehicleSpawns);
        Assert.Null(world.Get<MatchDoors?>("Doors"));
    }

    [Fact]
    public void PacedDescentBurstKeepsThePumpBarrierAndCannotDuplicateCarsAtLanding()
    {
        using var world = new Fixture(Fixture.DefaultOptions with { BurstSliceSize = 1 });
        world.Release();
        world.TakeTimer()();
        Assert.False(world.Get<bool>("LandingLootDrained"));
        Assert.Empty(world.VehicleSpawns); // Loot is first; the car is still owed a later slice.
        world.DrainStartup();
        Assert.Single(world.VehicleSpawns);

        world.MoveChute(Fixture.Drop with { Y = Fixture.Drop.Y + 1 });
        world.Invoke("SendParachuteDismountBurst", "descent streaming regression");
        world.DrainStartup();
        Assert.Equal(0ul, world.Get<ulong>("ChuteGuid"));
        Assert.Single(world.VehicleSpawns);
        Assert.True(world.Get<bool>("DevGroundLootArmed"));
        Assert.Contains(world.Sent, p => p[0] == ZoneOpcodes.AddLightweightNpc
            && BitConverter.ToUInt64(p, 1) == PracticeTargetPack.DefaultWorldGuidBase);
    }

    [Fact]
    public void LandingBurstCannotReleaseTheBarrierWhileDescentSlicesAreStillPending()
    {
        var startup = new WorldStreamStartup();
        Assert.True(startup.TryArmDescent(5));
        Assert.False(startup.TryArmDescent(5));
        startup.BeginBurst(5); // Deferred descent burst.
        startup.BeginBurst(5); // Landing and its dev drop happen before descent drains.
        Assert.False(startup.CompleteBurst(5));
        Assert.True(startup.CompleteBurst(5));
        Assert.False(startup.CompleteBurst(5));
    }

    [Fact]
    public void StartupGenerationDiscardsOldPendingWorkAndAllowsTheNextDescent()
    {
        var startup = new WorldStreamStartup();
        Assert.True(startup.TryArmDescent(5));
        startup.BeginBurst(5);
        Assert.True(startup.TryArmDescent(6));
        startup.BeginBurst(6);
        Assert.False(startup.CompleteBurst(5));
        Assert.True(startup.CompleteBurst(6));
    }

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        public static readonly Vector3 Drop = new(1624.96f, 41.18f, -2175.05f);
        public static readonly Vector3 Staging = new(100, 20, 100);
        public static readonly ZoneOptions DefaultOptions = new()
        {
            GroundLootDelayMs = 0, GroundLootMaxPerBurst = 3,
            SendLootClusters = false, SendProximateItems = false,
            DoorMaxPerBurst = 2, DoorRestreamIntervalMs = 0,
            VehicleRadius = 50, VehicleAirborneRadius = 50, VehicleMaxPerBurst = 1,
            LootStream = new LootStreamOptions { Enabled = false },
            VehicleStream = new VehicleStreamOptions { Enabled = false },
            BurstSliceSize = 100, BurstSliceDelayMs = 0,
            DevGroundLootMs = 1, DevGroundLootCount = 1,
            Combat = CombatOptions.Default with { PracticeTarget = true },
        };
        private readonly ConcurrentQueue<Action> _timers = new();
        private readonly ZoneService _service;
        private readonly SoeConnection _connection;
        private readonly object _state;
        private readonly ZoneOptions _options;
        public List<byte[]> Sent { get; } = [];
        public List<string> Logs { get; } = [];
        public IEnumerable<ulong> VehicleSpawns => Sent
            .Where(p => p[0] == ZoneOpcodes.AddLightweightVehicle).Select(p => BitConverter.ToUInt64(p, 1));

        public Fixture(ZoneOptions? options = null)
        {
            _options = options ?? DefaultOptions;
            _service = new ZoneService(this, this, new GatewayTicketRegistry(), _options) { Post = _timers.Enqueue };
            var request = new SessionRequest(3, 123, 512, ZoneService.ProtocolName);
            _connection = new SoeConnection(new(IPAddress.Loopback, 12345), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(_connection);
            _state = _connection.Tag!;
            Set("Authenticated", true);
            Set("Guid", 0x1001ul);
            Set("Visuals", CharacterVisuals.FromSelection(1, 1, 0, 0, 5));
            Set("Gender", CharacterVisuals.Male);
            Set("Wardrobe", new AugustWardrobeState());
            PropertyInfo match = _state.GetType().GetProperty("Match")!;
            match.SetValue(_state, Enum.Parse(match.PropertyType, "InMatch"));
            Set("Drop", new Vector4(Drop, 1));
            Set("ChuteAirY", 850f);
            using var writer = new PacketWriter();
            PositionUpdateBlock.AtRest(Staging, 0).WriteTo(writer);
            Send(writer.Written.ToArray(), 2);
            Set("ChuteGuid", 0x2001ul);
            Get<SessionMovementState>("Movement").RegisterManagedEntity(_options.ParachuteTransientId, 0x2001);
            ulong nextItem = 0x3100_0000_0000_0001;
            var inventory = new PlayerInventory(0x1001, () => nextItem++);
            inventory.Bootstrap();
            Set("Inventory", inventory);
            Set("Fleet", new VehicleFleet(VehicleRoster.LoadDefault()));
            AddCar(500, 99, Drop + new Vector3(10, 0, 0));
        }

        public void Release()
        {
            PropertyInfo match = _state.GetType().GetProperty("Match")!;
            match.SetValue(_state, Enum.Parse(match.PropertyType, "Dropping"));
            Send([ZoneOpcodes.SynchronizedTeleportBase, (byte)SynchronizedTeleport.ClientReady, 0], 0);
        }
        public void AddCar(ulong guid, uint transient, Vector3 position)
        {
            var fleet = Get<VehicleFleet>("Fleet");
            fleet.Add(new MatchVehicle(guid, transient, fleet.Roster.Require(1), position, 0, 100000, 5000));
        }

        public void MoveChute(Vector3 position)
        {
            Set("MountRequested", true);
            using var writer = new PacketWriter();
            writer.WriteByte(0x90);
            ClientVarInt.Write(writer, _options.ParachuteTransientId);
            PositionUpdateBlock.AtRest(position, 0).WriteTo(writer);
            Send(writer.Written.ToArray(), 3);
        }

        public void DrainStartup()
        {
            for (int index = 0; !Get<bool>("LandingLootDrained") && index < 100; index++) TakeTimer()();
            Assert.True(Get<bool>("LandingLootDrained"));
        }

        public Action TakeTimer()
        {
            Assert.True(SpinWait.SpinUntil(() => !_timers.IsEmpty, 5000), "world streaming timer was not dispatched");
            Assert.True(_timers.TryDequeue(out Action? callback));
            return callback!;
        }

        public void Invoke(string name, params object?[] extra) => typeof(ZoneService)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_service, new object?[] { _connection, _state }.Concat(extra).ToArray());
        private void Send(byte[] packet, byte channel) => _service.OnMessage(_connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, channel).ToByte(), .. packet]);
        public void Set(string name, object value) => _state.GetType().GetProperty(name)!.SetValue(_state, value);
        public T Get<T>(string name) => (T)_state.GetType().GetProperty(name)!.GetValue(_state)!;
        public bool IsEnabled(TransportLogLevel level) => true;
        public void Log(TransportLogLevel level, string message) => Logs.Add(message);
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes[1..].ToArray()); }
        public void Dispose() => _connection.Disconnect();
    }
}
