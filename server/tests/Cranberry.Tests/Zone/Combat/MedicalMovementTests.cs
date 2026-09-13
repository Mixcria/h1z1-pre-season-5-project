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
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Combat;

public sealed class MedicalMovementTests
{
    private static string? HealingValue(IEnumerable<byte[]> packets)
    {
        string? value = null;
        foreach (var packet in packets)
        {
            if (packet[0] != ZoneOpcodes.UpdateStringHashToValueManager) continue;
            var reader = new PacketReader(packet);
            reader.ReadByte();
            if (reader.ReadString() == "Cranberry.Healing") value = reader.ReadString();
        }
        return value;
    }

    [Theory]
    [InlineData(2423u, "120583")]
    [InlineData(2424u, "120581")]
    public void HealingHudUsesTheConsumedItemAndClearsWhenItsHealingEnds(uint item, string icon)
    {
        using var world = new Fixture(item, initialHitpoints: 1000);
        world.Start();
        Assert.NotEqual(icon, HealingValue(world.Sent));
        world.TakeTimer()();
        Assert.Equal(icon, HealingValue(world.Sent));
        world.Set("Hitpoints", 9999u);
        world.TakeTimer()();
        Assert.Equal("0", HealingValue(world.Sent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiringFirstAidRevealsAStillActiveBandageAndDeathClearsTheIcon(bool bandageTicksFirst)
    {
        using var world = new Fixture(initialHitpoints: 1000);
        world.Invoke("BeginHeal", MedicalModel.For(2424)!.Value with { OverSeconds = 1, TotalHp = 1 });
        // Capture each timer before arming another: real continuations may enqueue in either order.
        Action firstAidTick = world.TakeTimer();
        world.Invoke("BeginHeal", MedicalModel.For(2423)!.Value);
        Action bandageTick = world.TakeTimer();
        Assert.Equal("120581", HealingValue(world.Sent));
        if (bandageTicksFirst)
        {
            bandageTick();
            Assert.Equal("120581", HealingValue(world.Sent));
        }
        firstAidTick();
        Assert.Equal("120583", HealingValue(world.Sent));
        world.Set("DeathSent", true);
        (bandageTicksFirst ? world.TakeTimer() : bandageTick)();
        Assert.Equal("0", HealingValue(world.Sent));
    }

    [Fact]
    public void ExpiringOneBandageKeepsAnotherBandagesIcon()
    {
        using var world = new Fixture(initialHitpoints: 1000);
        world.Invoke("BeginHeal", MedicalModel.For(2423)!.Value with { OverSeconds = 1, TotalHp = 1 });
        world.Invoke("BeginHeal", MedicalModel.For(2423)!.Value);
        world.TakeTimer()();
        Assert.Equal("120583", HealingValue(world.Sent));
        world.Invoke("ResetPlayerVitals");
        Assert.Equal("0", HealingValue(world.Sent));
    }

    [Fact]
    public void HealingHudClearsOnResetAndAnOldTickCannotRemoveTheNewIcon()
    {
        using var world = new Fixture();
        world.Start(); world.TakeTimer()();
        var old = world.TakeTimer();
        world.Invoke("ResetPlayerVitals");
        Assert.Equal("0", HealingValue(world.Sent));
        world.Invoke("BeginHeal", MedicalModel.For(2424)!.Value);
        Assert.Equal("120581", HealingValue(world.Sent));
        old();
        Assert.Equal("120581", HealingValue(world.Sent));
    }

    [Theory]
    [InlineData(2423u, 5100u)]
    [InlineData(3375u, 5500u)]
    public void CompletedMedicalTickUpdatesTheHealthResourceReadByTheHud(uint itemId, uint expected)
    {
        using var world = new Fixture(itemId);
        world.Start();
        world.TakeTimer()(); // application completes
        int mark = world.Sent.Count;
        world.TakeTimer()(); // first regeneration tick

        byte[] resource = Assert.Single(world.Sent.Skip(mark), p => p.Length == 101 && p[0] == 0x8d
            && p[5] == 3 && BitConverter.ToUInt32(p, 14) == 1);
        Assert.Equal(0x1001ul, BitConverter.ToUInt64(resource, 6));
        Assert.Equal(expected, BitConverter.ToUInt32(resource, 22));
        Assert.Equal(5000u, BitConverter.ToUInt32(resource, 26));
        Assert.Equal(expected, world.Get<uint>("Hitpoints"));
    }

    [Theory]
    [InlineData(2423u)]
    [InlineData(2424u)]
    public void FullHealthLobbyPlayerCanConsumeMedicalWithGasDisabled(uint itemId)
    {
        using var world = new Fixture(itemId, initialHitpoints: null, enableGas: false, matchStep: "Lobby");
        Assert.Equal(10000u, world.Get<uint>("Hitpoints"));
        world.Start();
        world.TakeTimer()();
        Assert.False(world.Inventory.Items.ContainsKey(world.Item.Guid));
        world.TakeTimer()();
        Assert.Equal(10000u, world.Get<uint>("Hitpoints"));
        Assert.False(world.Get<bool>("DeathSent"));
    }

    [Fact]
    public void NewMatchPhaseRestoresHealthAndClearsDeathWithoutDependingOnGas()
    {
        using var world = new Fixture(initialHitpoints: 0, enableGas: false, matchStep: "Lobby");
        world.Set("DeathSent", true);
        world.Invoke("ResetPlayerVitals");
        Assert.Equal(10000u, world.Get<uint>("Hitpoints"));
        Assert.False(world.Get<bool>("DeathSent"));
        world.Start();
        world.TakeTimer()();
        Assert.False(world.Inventory.Items.ContainsKey(world.Item.Guid));
    }

    [Fact]
    public void UsingLastQuickSlotMedicalClearsTheClientLoadoutAndRefreshesTheBag()
    {
        using var world = new Fixture(2424);
        Assert.Equal(SurvivorLoadout.QuickUse2, world.Item.LoadoutSlotId);
        world.Start();
        world.TakeTimer()();
        Assert.False(world.Inventory.LoadoutSlots.ContainsKey(SurvivorLoadout.QuickUse2));
        Assert.Contains(world.Sent, p => p.Length > 1 && p[0] == 0x86 && p[1] == 4);
        Assert.Contains(world.Sent, p => p.Length > 1 && p[0] == 0xc8 && p[1] == 6);
    }

    [Fact]
    public void LobbyRegenerationCannotHealTheNextMatchAfterVitalsAreReset()
    {
        using var world = new Fixture(matchStep: "Lobby");
        world.Start();
        world.TakeTimer()();
        world.Invoke("ResetPlayerVitals");
        world.Set("Hitpoints", 5000u);
        world.TakeTimer()();
        Assert.Equal(5000u, world.Get<uint>("Hitpoints"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartingGasDoesNotResetHealth(bool enabled)
    {
        using var world = new Fixture(enableGas: enabled);
        world.Invoke("StartGas");
        Assert.Equal(5000u, world.Get<uint>("Hitpoints"));
    }

    [Theory]
    [InlineData(.8f, 0f)]
    [InlineData(0f, .8f)]
    public void WalkingOrJumpingCancelsBeforeTheCallbackWithoutSpendingOrHealing(float dx, float dy)
    {
        using var world = new Fixture();
        world.Start();
        Action due = world.TakeTimer();
        world.Move(Fixture.StartPosition + new Vector3(dx, dy, 0));
        Assert.Null(world.Cast);
        Assert.Equal(0L, world.Get<long>("ConsumeBusyUntil"));
        Assert.Equal(2, world.Sent.Count(IsStop));
        int packets = world.Sent.Count;
        due();
        Assert.Equal(packets, world.Sent.Count);
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
        Assert.Equal(5000u, world.Get<uint>("Hitpoints"));
    }

    [Fact]
    public void SmallWalkingStepsAccumulateAgainstTheCastOrigin()
    {
        using var world = new Fixture();
        world.Start();
        world.Move(Fixture.StartPosition + new Vector3(.25f, 0, 0));
        world.Move(Fixture.StartPosition + new Vector3(.5f, 0, 0));
        Assert.NotNull(world.Cast);
        world.Move(Fixture.StartPosition + new Vector3(.8f, 0, 0));
        Assert.Null(world.Cast);
    }

    [Theory]
    [InlineData(2423u)]
    [InlineData(2424u)]
    public void ComingToAStopAndSmallAdjustmentsAllowBandageOrMedkitToFinish(uint itemId)
    {
        using var world = new Fixture(itemId);
        world.Start();
        // Several decelerating position updates, followed by a small adjustment, remain within
        // one buffer measured from the original position. No per-frame origin reset is needed.
        foreach (float offset in new[] { .3f, .45f, .5f, .51f, .48f, .6f })
            world.Move(Fixture.StartPosition + new Vector3(offset, 0, 0));
        Assert.NotNull(world.Cast);
        Assert.DoesNotContain(world.Sent, IsStop);
        world.TakeTimer()();
        Assert.Null(world.Cast);
        Assert.False(world.Inventory.Items.ContainsKey(world.Item.Guid));
        world.TakeTimer()();
        Assert.Equal(5100u, world.Get<uint>("Hitpoints"));
    }

    [Fact]
    public void DiagonalMovementUsesOneDistanceBufferRatherThanOnePerAxis()
    {
        using var world = new Fixture();
        world.Start();
        world.Move(Fixture.StartPosition + new Vector3(.55f, 0, .55f));
        Assert.Null(world.Cast);
        world.TakeTimer()();
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
    }

    [Fact]
    public void LookingAndStandingJitterAllowCompletionAndAppliedRegenerationSurvivesLaterMovement()
    {
        using var world = new Fixture();
        world.Start();
        world.Look();
        world.Move(Fixture.StartPosition + new Vector3(.01f, -.01f, .01f));
        world.Move(Fixture.StartPosition);
        Assert.NotNull(world.Cast);
        world.TakeTimer()();
        Assert.Null(world.Cast);
        Assert.False(world.Inventory.Items.ContainsKey(world.Item.Guid));
        Assert.Equal(5000u, world.Get<uint>("Hitpoints"));
        world.Move(Fixture.StartPosition + Vector3.UnitX);
        world.TakeTimer()();
        Assert.Equal(5100u, world.Get<uint>("Hitpoints"));
    }

    [Fact]
    public void CancelledTimerCannotFinishOrClearANewCastOfTheSameItem()
    {
        using var world = new Fixture();
        world.Start();
        Action old = world.TakeTimer();
        world.Move(Fixture.StartPosition + Vector3.UnitX);
        world.Start();
        PendingMedicalCast current = Assert.IsType<PendingMedicalCast>(world.Cast);
        Action next = world.TakeTimer();
        int packets = world.Sent.Count;
        old();
        Assert.Same(current, world.Cast);
        Assert.Equal(packets, world.Sent.Count);
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
        next();
        Assert.Null(world.Cast);
        Assert.False(world.Inventory.Items.ContainsKey(world.Item.Guid));
        Assert.Single(world.Sent, p => p.Length > 1 && p[0] == 0x11 && p[1] == 4);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("inventory")]
    [InlineData("death")]
    public void AbandonedWorldReplacedInventoryAndDeathInvalidateCompletion(string change)
    {
        using var world = new Fixture();
        world.Start();
        Action due = world.TakeTimer();
        if (change == "world") world.Set("WorldGeneration", 1);
        if (change == "inventory") world.Set("Inventory", new PlayerInventory(0x1001, () => 9000));
        if (change == "death") world.Set("DeathSent", true);
        due();
        Assert.Null(world.Cast);
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
        Assert.Equal(5000u, world.Get<uint>("Hitpoints"));
        if (change == "death") Assert.Equal(2, world.Sent.Count(IsStop));
        else Assert.DoesNotContain(world.Sent, IsStop);
    }

    [Fact]
    public void AlreadyAppliedHealCannotTickIntoANewWorld()
    {
        using var world = new Fixture();
        world.Start();
        world.TakeTimer()();
        world.Set("WorldGeneration", 1);
        world.TakeTimer()();
        Assert.Equal(5000u, world.Get<uint>("Hitpoints"));
    }

    [Theory]
    [InlineData("world")]
    [InlineData("inventory")]
    public void FirstMovementAfterLifecycleChangeSilentlyInvalidatesTheOldCast(string change)
    {
        using var world = new Fixture();
        world.Start();
        Action due = world.TakeTimer();
        if (change == "world") world.Set("WorldGeneration", 1);
        else world.Set("Inventory", new PlayerInventory(0x1001, () => 9000));
        world.Move(Fixture.StartPosition + Vector3.UnitX);
        Assert.Null(world.Cast);
        Assert.Equal(0L, world.Get<long>("ConsumeBusyUntil"));
        Assert.DoesNotContain(world.Sent, IsStop);
        int packets = world.Sent.Count;
        due();
        Assert.Equal(packets, world.Sent.Count);
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
        Assert.Equal(5000u, world.Get<uint>("Hitpoints"));
    }

    [Fact]
    public void ManagedDriverMovementCancelsAndUnownedMovementDoesNot()
    {
        using var world = new Fixture();
        world.Seat(driver: true);
        world.Start();
        world.MoveManaged(98, Fixture.StartPosition + Vector3.UnitX);
        Assert.NotNull(world.Cast);
        world.MoveManaged(99, Fixture.StartPosition + Vector3.UnitX);
        Assert.Null(world.Cast);
        world.TakeTimer()();
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
    }

    [Fact]
    public void PassengerDummyPoseDoesNotCancelUntilTheirVehicleActuallyMoves()
    {
        using var world = new Fixture();
        VehicleFleet fleet = world.Seat(driver: false);
        world.Start();
        world.Move(Vector3.Zero);
        Assert.NotNull(world.Cast);
        Assert.True(fleet.TryApplyOwnerPose(99, 0x2002, Fixture.StartPosition + Vector3.UnitX,
            0, Environment.TickCount64, out _));
        world.Move(Vector3.Zero);
        Assert.Null(world.Cast);
        world.TakeTimer()();
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
    }

    [Fact]
    public void CompletionChecksVehiclePositionEvenWithoutAPassengerMovementPacket()
    {
        using var world = new Fixture();
        VehicleFleet fleet = world.Seat(driver: false);
        world.Start();
        Assert.True(fleet.TryApplyOwnerPose(99, 0x2002, Fixture.StartPosition + Vector3.UnitX,
            0, Environment.TickCount64, out _));
        world.TakeTimer()();
        Assert.Null(world.Cast);
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
    }

    private static bool IsStop(byte[] p) => p.Length > 1 && p[0] == 0xcf && p[1] == 3;

    [Theory]
    [InlineData(73u, 5000f, 7500f)]
    [InlineData(1384u, 9000f, 10000f)]
    public void FuelCanAddsTwentyFivePercentClampsAndConsumesExactlyOne(uint item, float start, float expected)
    {
        using var world = new Fixture(item);
        var fleet = world.Seat(true);
        var vehicle = Assert.Single(fleet.Vehicles);
        vehicle.Fuel = start;
        world.Start();
        var completion = world.TakeTimer();
        completion();
        completion(); // A stale/duplicated timer cannot spend another can or add more fuel.
        Assert.Equal(expected, vehicle.Fuel);
        Assert.False(world.Inventory.Items.ContainsKey(world.Item.Guid));
        Assert.Contains(world.Sent, p => p.Length > 26 && p[0] == 0x8d
            && BitConverter.ToUInt32(p, 14) == 50 && BitConverter.ToUInt32(p, 22) == (uint)expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRefuelContextMenuWorksFromBagOrVehicleCargo(bool fromCargo)
    {
        using var world = new Fixture(73);
        var car = Assert.Single(world.Seat(true).Vehicles);
        ulong fuelGuid = fromCargo
            ? Assert.Single(car.Inventory.Items, item => item.DefinitionId == 73).ItemGuid
            : world.Item.Guid;
        world.UseFuelMenu(fuelGuid, fromCargo ? car.Guid : 0x1001);
        Assert.NotNull(world.Cast);
        var complete = world.TakeTimer();
        complete();
        complete();
        Assert.Equal(7500f, car.Fuel);
        if (fromCargo)
        {
            Assert.False(car.Inventory.TryGet(fuelGuid, out _));
            Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
        }
        else Assert.False(world.Inventory.Items.ContainsKey(fuelGuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefuelHonoursTheClickedVehicleInsteadOfChoosingADifferentNearbyCar(bool targetFull)
    {
        using var world = new Fixture(73);
        var fleet = new VehicleFleet(VehicleRoster.LoadDefault());
        var near = new MatchVehicle(500, 99, fleet.Roster.Require(1), Fixture.StartPosition + Vector3.UnitX,
            0, 100000, 5000);
        var clicked = new MatchVehicle(501, 100, fleet.Roster.Require(1), Fixture.StartPosition + Vector3.UnitX * 3,
            0, 100000, targetFull ? 10000 : 5000);
        fleet.Add(near); fleet.Add(clicked);
        world.Set("Fleet", fleet);
        world.UseFuelMenu(world.Item.Guid, 0x1001, clicked.Guid);
        if (targetFull)
        {
            Assert.Null(world.Cast);
            Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
            Assert.Equal(10000f, clicked.Fuel);
        }
        else
        {
            Assert.NotNull(world.Cast);
            world.TakeTimer()();
            Assert.False(world.Inventory.Items.ContainsKey(world.Item.Guid));
            Assert.Equal(7500f, clicked.Fuel);
        }
        Assert.Equal(5000f, near.Fuel);
    }

    [Fact]
    public void FuelCanIsKeptIfTheVehicleFillsDuringTheCast()
    {
        using var world = new Fixture(73);
        var fleet = world.Seat(true);
        var car = Assert.Single(fleet.Vehicles);
        world.Start();
        car.Fuel = fleet.Options.MaxFuel;
        world.TakeTimer()();
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
        Assert.Equal(10000f, car.Fuel);
    }

    [Fact]
    public void FuelUseWithoutVehicleOrWithAFullOrDestroyedVehicleDoesNotConsumeOrCast()
    {
        using var world = new Fixture(73);
        world.TryStart();
        Assert.Null(world.Cast);
        var fleet = world.Seat(true);
        var car = Assert.Single(fleet.Vehicles);
        car.Fuel = 10000;
        world.TryStart();
        Assert.Null(world.Cast);
        car.Fuel = 5000;
        car.Health = 0;
        world.TryStart();
        Assert.Null(world.Cast);
        Assert.True(world.Inventory.Items.ContainsKey(world.Item.Guid));
    }

    [Fact]
    public void StandingInMolotovAttachesOneFireLoopAndExpiryRemovesIt()
    {
        using var world = new Fixture(initialHitpoints: 10000);
        world.ApplyFire(Fixture.StartPosition);
        world.ApplyFire(Fixture.StartPosition);
        Assert.Single(world.Sent, p => p.Length >= 14 && p[0] == 0x0f && p[1] == 0x15
            && BitConverter.ToUInt32(p, 10) == 1212);
        world.Set("CharacterFireUntilMs", 0L);
        world.TakeTimer()();
        Assert.False(world.Get<bool>("CharacterFireActive"));
        Assert.Contains(world.Sent, p => p.Length >= 14 && p[0] == 0x0f && p[1] == 0x16
            && BitConverter.ToUInt32(p, 10) == 1212);
    }

    [Fact]
    public void OutsideMolotovDoesNotAttachFire()
    {
        using var world = new Fixture();
        world.ApplyFire(Fixture.StartPosition + new Vector3(100, 0, 0));
        Assert.False(world.Get<bool>("CharacterFireActive"));
    }

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        public static readonly Vector3 StartPosition = new(100, 20, 100);
        private readonly ConcurrentQueue<Action> _timers = new();
        private readonly ZoneService _service;
        private readonly SoeConnection _connection;
        private readonly object _state;
        public List<byte[]> Sent { get; } = [];
        public PlayerInventory Inventory { get; }
        public InventoryItemInstance Item { get; }
        public PendingMedicalCast? Cast => Get<PendingMedicalCast?>("PendingMedicalCast");

        public Fixture(uint itemId = 2423, uint? initialHitpoints = 5000, bool enableGas = true,
            string matchStep = "InMatch")
        {
            _service = new ZoneService(this, this, new GatewayTicketRegistry(), new ZoneOptions
            { SendProximateItems = false, EnableGas = enableGas }) { Post = _timers.Enqueue };
            var request = new SessionRequest(3, 123, 512, ZoneService.ProtocolName);
            _connection = new SoeConnection(new(IPAddress.Loopback, 12345), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(_connection);
            _state = _connection.Tag!;
            Set("Authenticated", true);
            Set("Guid", 0x1001ul);
            if (initialHitpoints is { } health) Set("Hitpoints", health);
            PropertyInfo match = _state.GetType().GetProperty("Match")!;
            match.SetValue(_state, Enum.Parse(match.PropertyType, matchStep));
            ulong next = 0x3100_0000_0000_0001;
            Inventory = new PlayerInventory(0x1001, () => next++);
            Inventory.Bootstrap();
            // This fixture exercises using the last unit, independently of starter Q supplies.
            Inventory.RemoveUnits(Inventory.LoadoutSlots[SurvivorLoadout.QuickUse1].Guid, 0);
            Inventory.TryPickUp(itemId, 1, out InventoryItemInstance? item);
            Item = Assert.IsType<InventoryItemInstance>(item);
            Set("Inventory", Inventory);
            Move(StartPosition);
        }

        public void Start()
        {
            TryStart();
            Assert.NotNull(Cast);
        }

        public void TryStart()
        {
            ItemActionResult plan = InventoryActions.Consume(Inventory, Item, ItemUseOptionKind.ConsumeItem, 0)
                with { BusyMilliseconds = 0 };
            typeof(ZoneService).GetMethod("RunConsume", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_service, [_connection, _state, Inventory, plan]);
        }

        public void ApplyFire(Vector3 position)
        {
            Assert.True(Cranberry.Zone.Weapons.AugustThrowables.TryGet(14, out var fact));
            var fire = new Detonation(fact, position, true, false, 1);
            typeof(ZoneService).GetMethod("ApplyBlast", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_service, [_connection, _state, fire, true]);
        }

        public void UseFuelMenu(ulong fuelGuid, ulong source, ulong target = 500)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(0xac); writer.WriteByte(0x2c);
            writer.WriteUInt32(1); writer.WriteUInt32(0); writer.WriteUInt32(17);
            writer.WriteUInt64(0x1001); writer.WriteUInt64(source); writer.WriteUInt64(target);
            writer.WriteUInt64(fuelGuid); writer.WriteByte(1);
            Send(writer.Written.ToArray(), 0);
        }

        public void Invoke(string name, params object[] arguments) => typeof(ZoneService)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_service, [ _connection, _state, .. arguments ]);

        public Action TakeTimer()
        {
            Assert.True(SpinWait.SpinUntil(() => !_timers.IsEmpty, 5000), "medical timer was not dispatched");
            Assert.True(_timers.TryDequeue(out Action? callback));
            return callback!;
        }

        public void Move(Vector3 position)
        {
            using var writer = new PacketWriter();
            PositionUpdateBlock.AtRest(position, 0).WriteTo(writer);
            Send(writer.Written.ToArray(), 2);
        }

        public void Look()
        {
            using var writer = new PacketWriter();
            writer.WriteUInt16((ushort)MovementFieldMask.Orientation);
            writer.WriteUInt32(50);
            writer.WriteByte(0);
            writer.WriteSingle(1.2f);
            Send(writer.Written.ToArray(), 2);
        }

        public void MoveManaged(uint transient, Vector3 position)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(0x90);
            ClientVarInt.Write(writer, transient);
            PositionUpdateBlock.AtRest(position, 0).WriteTo(writer);
            Send(writer.Written.ToArray(), 3);
        }

        public VehicleFleet Seat(bool driver)
        {
            var fleet = new VehicleFleet(VehicleRoster.LoadDefault());
            fleet.Add(new MatchVehicle(500, 99, fleet.Roster.Require(1), StartPosition, 0, 100000, 5000));
            if (!driver) Assert.Equal(VehicleActionResult.Ok,
                fleet.TryEnter(500, 0x2002, 0, 0, out _, out _));
            Assert.Equal(VehicleActionResult.Ok,
                fleet.TryEnter(500, 0x1001, driver ? 0 : 1, 2000, out _, out _));
            Set("Fleet", fleet);
            if (driver) Get<SessionMovementState>("Movement").RegisterManagedEntity(99, 500);
            return fleet;
        }

        private void Send(byte[] packet, byte channel) => _service.OnMessage(_connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, channel).ToByte(), .. packet]);
        public void Set(string name, object value) => _state.GetType().GetProperty(name)!.SetValue(_state, value);
        public T Get<T>(string name) => (T)_state.GetType().GetProperty(name)!.GetValue(_state)!;
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes[1..].ToArray()); }
        public void Dispose() => _connection.Disconnect();
    }
}
