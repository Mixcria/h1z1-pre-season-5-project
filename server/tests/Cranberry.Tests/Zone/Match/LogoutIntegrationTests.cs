using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.MatchLogout;

public sealed class LogoutIntegrationTests
{
    [Theory]
    [InlineData("Lobby")]
    [InlineData("Dropping")]
    [InlineData("InMatch")]
    public void InWorldExitShowsTenSecondBarAndKeepsWorldUntilCompletion(string step)
    {
        using var f = new Fixture(step);
        PlayerInventory inventory = f.Get<PlayerInventory>("Inventory");
        f.RequestExit();
        Assert.NotNull(f.Pending);
        object pending = f.Pending;
        byte[] start = Assert.Single(f.Sent);
        Assert.Equal(67, start.Length);
        Assert.Equal(new byte[] { 0xcf, 2 }, start[..2]);
        Assert.Equal(0x1001ul, BinaryPrimitives.ReadUInt64LittleEndian(start.AsSpan(2)));
        Assert.Equal(10_000u, BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(10)));
        Assert.Equal(14991u, BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(38)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(42)));
        Assert.Equal(step, f.Step);
        Assert.Same(inventory, f.Get<PlayerInventory>("Inventory"));

        f.RequestExit();
        f.Send([0xc3]); // A premature relogin request cannot bypass the countdown.
        Assert.Same(pending, f.Pending);
        Assert.Single(f.Sent);

        f.Complete(pending);
        Assert.Null(f.Pending);
        Assert.Equal("Menu", f.Step);
        Assert.Null(f.Get<object?>("Inventory"));
        int stopIndex = f.Sent.FindIndex(p => p.Length >= 2 && p[0] == 0xcf && p[1] == 3);
        Assert.InRange(stopIndex, 1, f.Sent.Count - 2);
        Assert.Equal(new byte[] { 0x11, 0x30, 0 }, f.Sent[^1]);
        Assert.Equal(ConnectionState.Open, f.Connection.State);
        int count = f.Sent.Count;
        f.Complete(pending);
        Assert.Equal(count, f.Sent.Count);
        f.Send([0xc3]);
        Assert.Equal(0xc4, f.Sent[^1][0]);
        Assert.Equal(1, f.Sent[^1][1]);
    }

    [Fact]
    public void RealDispatcherDoesNotCompleteBeforeTenSeconds()
    {
        using var f = new Fixture("InMatch");
        long started = Environment.TickCount64;
        f.RequestExit();
        Assert.False(f.Timers.TryTake(out _, 100));
        Assert.NotNull(f.Pending);
        Assert.True(f.Timers.TryTake(out Action? due, 12_000));
        Assert.True(Environment.TickCount64 - started >= 9_950);
        due!();
        Assert.Equal("Menu", f.Step);
        Assert.Equal(new byte[] { 0x11, 0x30, 0 }, f.Sent[^1]);
    }

    [Fact]
    public void LobbyCountdownCannotStartAnotherMatchDuringExit()
    {
        using var f = new Fixture("Lobby");
        f.RequestExit();
        object pending = f.Pending!;
        int packets = f.Sent.Count;
        f.Invoke("BeginMatchDrop");
        Assert.Equal("Lobby", f.Step);
        Assert.Same(pending, f.Pending);
        Assert.Equal(packets, f.Sent.Count);
        f.Complete(pending);
        Assert.Equal("Menu", f.Step);
        Assert.Equal(new byte[] { 0x11, 0x30, 0 }, f.Sent[^1]);
    }

    [Fact]
    public void ResultsWaitForChoiceAndNativeExitIsImmediate()
    {
        using var f = new Fixture("Ended");
        int packets = f.Sent.Count;
        f.Invoke("CompleteEndedHold");
        Assert.Equal("Ended", f.Step);
        Assert.Equal(packets, f.Sent.Count);
        f.RequestExit();
        Assert.Null(f.Pending);
        Assert.Equal("Menu", f.Step);
        Assert.Equal(new byte[] { 0x11, 0x30, 0 }, f.Sent[^1]);
    }

    [Fact]
    public void LegacyResultsTimerCannotInterruptAPreparedUserExit()
    {
        using var f = new Fixture("Ended", leaveOnEnd: true);
        f.Action("menu");
        int packets = f.Sent.Count;
        f.Invoke("CompleteEndedHold");
        Assert.Equal(packets, f.Sent.Count);
        Assert.True(f.Get<bool>("LogoutPrepared"));
        f.RequestExit();
        Assert.Single(f.Sent, IsLogout);
    }

    [Theory]
    [InlineData(.8f, 0f, 0f)]
    [InlineData(0f, .8f, 0f)]
    [InlineData(0f, 0f, -.8f)]
    public void MovementCancelsExitAndAnOldCallbackCannotExitANewAttempt(float x, float y, float z)
    {
        using var f = new Fixture("InMatch");
        f.Action("exit");
        object old = f.Pending!;
        f.Move(Fixture.Origin + new Vector3(x, y, z));
        Assert.Null(f.Pending);
        Assert.Equal("InMatch", f.Step);
        Assert.Equal(new byte[] { 0xcf, 3 }, f.Sent[^1][..2]);
        f.Action("exit");
        object current = f.Pending!;
        int count = f.Sent.Count;
        f.Complete(old);
        Assert.Same(current, f.Pending);
        Assert.Equal(count, f.Sent.Count);
        Assert.True((bool)f.Invoke("RefuseInteractionDuringLogout")!);
    }

    [Fact]
    public void LookingAndSmallPoseJitterKeepCountdownButAccumulatedWalkingCancels()
    {
        using var f = new Fixture("InMatch");
        f.Action("exit");
        object pending = f.Pending!;
        f.Look();
        f.Move(Fixture.Origin + new Vector3(.01f, -.01f, .01f));
        f.Move(Fixture.Origin + new Vector3(.25f, 0, 0));
        f.Move(Fixture.Origin + new Vector3(.5f, 0, 0));
        Assert.Same(pending, f.Pending);
        f.Move(Fixture.Origin + new Vector3(.8f, 0, 0));
        Assert.Null(f.Pending);
        Assert.False((bool)f.Invoke("RefuseInteractionDuringLogout")!);
    }

    [Fact]
    public void UpdatedUiStartsNativeLogoutOnlyAfterCountdownAndCompletesExactlyOnce()
    {
        using var f = new Fixture("InMatch");
        f.Action("exit");
        Assert.False(f.Get<bool>("LogoutPrepared"));
        f.Complete(f.Pending!);
        Assert.Equal("InMatch", f.Step);
        Assert.True(f.Get<bool>("LogoutPrepared"));
        Assert.DoesNotContain(f.Sent, IsLogout);
        Assert.Contains(f.Sent, p => System.Text.Encoding.UTF8.GetString(p).Contains("@cranberry/match-exit/1;logout"));
        f.RequestExit();
        Assert.Equal("Menu", f.Step);
        Assert.Single(f.Sent, IsLogout);
        f.RequestExit(); f.Action("exit");
        Assert.Single(f.Sent, IsLogout);
        f.Send([0xc3]);
        Assert.Equal(0xc4, f.Sent[^1][0]);
    }

    [Theory]
    [InlineData("menu", "Ended", "DeathSent")]
    [InlineData("play", "Ended", "DeathSent")]
    [InlineData("menu", "InMatch", "DeathSent")]
    [InlineData("play", "InMatch", "DeathSent")]
    [InlineData("menu", "InMatch", "VictorySent")]
    [InlineData("play", "InMatch", "VictorySent")]
    public void ResultChoicesSkipTheCountdownAndOnlyPlayRequeuesAfterFreshMenu(string choice, string phase, string flag)
    {
        using var f = new Fixture(phase);
        f.Set(flag, true);
        f.Set("BountyAdmission", new MatchAdmissionContext(123, MatchQueueKind.Public, MatchMode.Solo));
        f.Set("MatchTransferRequest", new PlayerWorldTransferRequest(1, "", 0, 1, 1));
        f.Action(choice);
        Assert.Null(f.Pending);
        Assert.True(f.Get<bool>("LogoutPrepared"));
        Assert.DoesNotContain(f.Sent, p => p.Length > 1 && p[0] == 0xcf && p[1] == 2);
        f.RequestExit();
        f.Invoke("ReplayAfterMenuReady");
        Assert.Equal("Menu", f.Step); // The outgoing session cannot consume replay intent.
        f.FreshMenu();
        f.Invoke("ReplayAfterMenuReady");
        Assert.Equal(choice == "play" ? "Transferring" : "Menu", f.Step);
        int count = f.Sent.Count;
        f.Invoke("ReplayAfterMenuReady");
        Assert.Equal(count, f.Sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoloReplayWaitsForClientInitializationAndPendingExitCancelsIt(bool exitWhileWaiting)
    {
        using var f = new Fixture("Ended");
        bool ready = false;
        f.ClientReady(() => ready);
        f.Set("BountyAdmission", new MatchAdmissionContext(123, MatchQueueKind.Public, MatchMode.Solo));
        f.Set("MatchTransferRequest", new PlayerWorldTransferRequest(1, "", 0, 1, 1));
        f.Action("play");
        f.RequestExit();
        f.FreshMenu();
        f.Invoke("ReplayAfterMenuReady");
        f.Invoke("ReplayAfterMenuReady");
        Assert.Equal("Menu", f.Step);
        Assert.False(f.Get<bool>("AutoAcceptReplay"));
        Assert.True(f.Timers.TryTake(out Action? next, 2_000));
        Assert.Empty(f.Timers);
        if (exitWhileWaiting) f.Action("exit");
        ready = true;
        next!();
        Assert.Equal(exitWhileWaiting ? "Menu" : "Transferring", f.Step);
        int packets = f.Sent.Count;
        f.Invoke("ReplayAfterMenuReady");
        Assert.Equal(packets, f.Sent.Count);
    }

    [Theory]
    [InlineData(MatchMode.Duos, MatchQueueKind.Public, 6u)]
    [InlineData(MatchMode.Fives, MatchQueueKind.Public, 7u)]
    [InlineData(MatchMode.Solo, MatchQueueKind.Hosted, 1u)]
    [InlineData(MatchMode.Training, MatchQueueKind.Public, 1u)]
    [InlineData(MatchMode.Unknown, MatchQueueKind.Unknown, 1u)]
    [InlineData(MatchMode.Solo, MatchQueueKind.Public, 6u)]
    public void NonSoloOrInconsistentReplayCannotLeaveResultsOrQueue(MatchMode mode, MatchQueueKind kind, uint world)
    {
        using var f = new Fixture("Ended");
        f.Set("BountyAdmission", new MatchAdmissionContext(123, kind, mode));
        f.Set("MatchTransferRequest", new PlayerWorldTransferRequest(world, "", 0, 1, 1));
        f.Action("play");
        Assert.Equal("Ended", f.Step);
        Assert.False(f.Get<bool>("LogoutPrepared"));
        Assert.DoesNotContain(f.Sent, IsLogout);
        f.Action("menu");
        Assert.True(f.Get<bool>("LogoutPrepared"));
        f.RequestExit();
        f.FreshMenu();
        f.Invoke("ReplayAfterMenuReady");
        Assert.Equal("Menu", f.Step);
    }

    [Theory]
    [InlineData("Lobby", "DeathSent")]
    [InlineData("Lobby", "VictorySent")]
    [InlineData("Dropping", "DeathSent")]
    [InlineData("Dropping", "VictorySent")]
    public void PreviousResultFlagsCannotSkipANewPregameOrDropCountdown(string phase, string flag)
    {
        using var f = new Fixture(phase);
        f.Set(flag, true);
        f.Action("exit");
        Assert.NotNull(f.Pending);
        Assert.False(f.Get<bool>("LogoutPrepared"));
        Assert.DoesNotContain(f.Sent, IsLogout);
    }

    [Theory]
    [InlineData("menu")]
    [InlineData("play")]
    public void ResultActionsCannotBypassALiveMatchCountdown(string choice)
    {
        using var f = new Fixture("InMatch");
        f.Action(choice);
        Assert.Empty(f.Sent);
        Assert.False(f.Get<bool>("LogoutPrepared"));
        Assert.Equal("InMatch", f.Step);
    }

    [Fact]
    public void DyingCancelsAnEarlierCountdownWithoutLeavingTheResultScreen()
    {
        using var f = new Fixture("InMatch");
        f.Action("exit");
        object pending = f.Pending!;
        f.Invoke("BeginEndedHold", "test death");
        Assert.Null(f.Pending);
        int count = f.Sent.Count;
        f.Complete(pending);
        Assert.Equal(count, f.Sent.Count);
        Assert.Equal("Ended", f.Step);
    }

    private static bool IsLogout(byte[] packet) => packet.SequenceEqual(new byte[] { 0x11, 0x30, 0 });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovingVehicleCancelsDriverAndPassengerExit(bool driver)
    {
        using var f = new Fixture("InMatch");
        var fleet = f.Seat(driver);
        f.Action("exit");
        object pending = f.Pending!;
        if (driver) f.MoveManaged(99, Fixture.Origin + Vector3.UnitX);
        else
        {
            Assert.True(fleet.TryApplyOwnerPose(99, 0x2002, Fixture.Origin + Vector3.UnitX, 0,
                Environment.TickCount64, out _));
            f.Move(Vector3.Zero); // Native passenger channel 2 uses a placeholder pose.
        }
        Assert.Null(f.Pending);
        f.Complete(pending);
        Assert.Equal("InMatch", f.Step);
        Assert.DoesNotContain(f.Sent, IsLogout);
    }

    [Fact]
    public void CompletionRechecksVehiclePoseEvenWithoutAPassengerPacket()
    {
        using var f = new Fixture("InMatch");
        var fleet = f.Seat(false);
        f.Action("exit");
        object pending = f.Pending!;
        Assert.True(fleet.TryApplyOwnerPose(99, 0x2002, Fixture.Origin + Vector3.UnitX, 0,
            Environment.TickCount64, out _));
        f.Complete(pending);
        Assert.Null(f.Pending);
        Assert.False(f.Get<bool>("LogoutPrepared"));
        Assert.DoesNotContain(f.Sent, IsLogout);
    }

    [Fact]
    public void PregameMenuExitUsesTheSameImmediateNativeHandshake()
    {
        using var f = new Fixture("Menu");
        f.Action("exit");
        Assert.True(f.Get<bool>("LogoutPrepared"));
        Assert.Null(f.Pending);
        f.RequestExit();
        Assert.Single(f.Sent, IsLogout);
    }

    [Theory]
    [InlineData("Menu")]
    [InlineData("Queued")]
    [InlineData("Transferring")]
    public void MenuBackDoesNotAcquireAnInWorldDelay(string step)
    {
        using var f = new Fixture(step);
        f.RequestExit();
        Assert.Null(f.Pending);
        Assert.Equal(new byte[] { 0x11, 0x30, 0 }, Assert.Single(f.Sent));
    }

    [Theory]
    [InlineData("abandon")]
    [InlineData("world")]
    [InlineData("disconnect")]
    public void OldCountdownCannotLogoutAnotherWorldOrAClosedConnection(string change)
    {
        using var f = new Fixture("InMatch");
        f.RequestExit();
        object pending = f.Pending!;
        if (change == "abandon") f.Invoke("AbandonMatch", "test leave");
        if (change == "world") f.Set("WorldGeneration", f.Get<int>("WorldGeneration") + 1);
        if (change == "disconnect") f.Connection.Disconnect();
        int count = f.Sent.Count;
        f.Complete(pending);
        Assert.Equal(count, f.Sent.Count);
        Assert.DoesNotContain(f.Sent, p => p.SequenceEqual(new byte[] { 0x11, 0x30, 0 }));
    }

    [Fact]
    public void LogoutReplacesMedicalAndRejectsNewCraftBars()
    {
        using var f = new Fixture("InMatch");
        PlayerInventory inventory = f.Get<PlayerInventory>("Inventory");
        f.Set("PendingMedicalCast", new Cranberry.Zone.Combat.PendingMedicalCast(inventory, 0, null));
        f.Set("ConsumeBusyUntil", Environment.TickCount64 + 20_000);
        f.Set("CraftBusyUntil", Environment.TickCount64 + 20_000);
        f.Set("ShredBusyUntil", Environment.TickCount64 + 20_000);
        f.RequestExit();
        Assert.Null(f.Get<object?>("PendingMedicalCast"));
        Assert.Equal(0, f.Get<long>("ConsumeBusyUntil"));
        Assert.Equal(0, f.Get<long>("CraftBusyUntil"));
        Assert.Equal(0, f.Get<long>("ShredBusyUntil"));
        Assert.True((bool)f.Invoke("RefuseInteractionDuringLogout")!);
        Assert.Single(f.Sent, p => p[0] == 0xcf && p[1] == 2);
        Assert.Equal(new byte[] { 0xc8, 3 }, f.Sent[^1][..2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecipeRequestCannotReplaceLogoutOrConsumeIngredients(bool castBar)
    {
        using var f = new Fixture("InMatch", castBar);
        var inventory = f.Get<PlayerInventory>("Inventory");
        RecipeDefinition recipe = CraftingCatalog.ByIdFor(true)[CraftingCatalog.FieldBandage];
        foreach (var ingredient in recipe.Ingredients)
            inventory.TryPickUp(ingredient.ItemDefinitionId, ingredient.Quantity, out _);
        uint before = CraftingService.CraftableCount(inventory, recipe);
        Assert.True(before > 0);
        f.RequestExit();
        using var request = new PacketWriter();
        request.WriteByte(0x09);
        request.WriteUInt16(0x1a);
        request.WriteUInt32(recipe.RecipeId);
        request.WriteUInt32(1);
        f.Send(request.Written.ToArray());
        Assert.Equal(before, CraftingService.CraftableCount(inventory, recipe));
        Assert.Single(f.Sent, p => p[0] == 0xcf && p[1] == 2);
        Assert.Equal(new byte[] { 0xc8, 3 }, f.Sent[^1][..2]);
    }

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly ZoneService _service;
        private object _state;
        public SoeConnection Connection { get; private set; }
        public static readonly Vector3 Origin = new(100, 20, 100);
        public List<byte[]> Sent { get; } = [];
        public BlockingCollection<Action> Timers { get; } = new();
        public object? Pending => Get<object?>("PendingLogout");
        public string Step => Get<object>("Match").ToString()!;

        public Fixture(string step, bool craftCastBar = true, bool leaveOnEnd = false)
        {
            _service = new ZoneService(this, this, new GatewayTicketRegistry(), new ZoneOptions
            { SendProximateItems = false, EnableGas = false,
                MatchEnd = new Cranberry.Zone.Match.MatchEndOptions { LeaveOnEnd = leaveOnEnd },
                Crafting = new CraftingOptions { CraftCastBar = craftCastBar } }) { Post = action => Timers.Add(action) };
            var request = new SessionRequest(3, 123, 512, ZoneService.ProtocolName);
            Connection = new SoeConnection(new(IPAddress.Loopback, 12345), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(Connection);
            _state = Connection.Tag!;
            Set("Authenticated", true);
            Set("Guid", 0x1001ul);
            PropertyInfo match = _state.GetType().GetProperty("Match")!;
            match.SetValue(_state, Enum.Parse(match.PropertyType, step));
            ulong next = 0x3100;
            var inventory = new PlayerInventory(0x1001, () => next++);
            inventory.Bootstrap();
            Set("Inventory", inventory);
            Move(Origin);
            Sent.Clear();
        }

        public void FreshMenu()
        {
            Connection.Disconnect();
            var request = new SessionRequest(3, 124, 512, ZoneService.ProtocolName);
            Connection = new SoeConnection(new(IPAddress.Loopback, 12346), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(Connection);
            _state = Connection.Tag!;
            Set("Authenticated", true); Set("Guid", 0x1001ul); Set("AppearanceReadySent", true);
        }

        public void Action(string action)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(ZoneOpcodes.WallOfDataBase); writer.WriteByte(5);
            writer.WriteString("CRANBERRY_MATCH_ACTION_V1"); writer.WriteString(action); writer.WriteUInt32(0);
            Send(writer.Written.ToArray());
        }

        public void ClientReady(Func<bool> ready) => _service.DoorSwingClientReady = _ => ready();

        public void Move(Vector3 position)
        {
            using var writer = new PacketWriter();
            PositionUpdateBlock.AtRest(position, 0).WriteTo(writer);
            _service.OnMessage(Connection, [new GatewayHeader(GatewayTunnelFromClient.Opcode, 2).ToByte(), .. writer.Written]);
        }

        public void Look()
        {
            using var writer = new PacketWriter();
            writer.WriteUInt16((ushort)MovementFieldMask.Orientation);
            writer.WriteUInt32(50); writer.WriteByte(0); writer.WriteSingle(1.2f);
            _service.OnMessage(Connection, [new GatewayHeader(GatewayTunnelFromClient.Opcode, 2).ToByte(), .. writer.Written]);
        }

        public void MoveManaged(uint transient, Vector3 position)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(0x90); ClientVarInt.Write(writer, transient);
            PositionUpdateBlock.AtRest(position, 0).WriteTo(writer);
            _service.OnMessage(Connection, [new GatewayHeader(GatewayTunnelFromClient.Opcode, 3).ToByte(), .. writer.Written]);
        }

        public VehicleFleet Seat(bool driver)
        {
            var fleet = new VehicleFleet(VehicleRoster.LoadDefault());
            fleet.Add(new MatchVehicle(500, 99, fleet.Roster.Require(1), Origin, 0, 100000, 5000));
            if (!driver) Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(500, 0x2002, 0, 0, out _, out _));
            Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(500, 0x1001, driver ? 0 : 1, 2000, out _, out _));
            Set("Fleet", fleet);
            if (driver) Get<SessionMovementState>("Movement").RegisterManagedEntity(99, 500);
            return fleet;
        }

        public void RequestExit() => Send([0x09, 0x4e, 0, 0]);
        public void Send(byte[] packet) => _service.OnMessage(Connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), .. packet]);
        public void Complete(object pending) => Invoke("CompletePendingLogout", pending);
        public object? Invoke(string name, params object[] tail) => typeof(ZoneService)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_service, [Connection, _state, .. tail]);
        public void Set(string name, object value) => _state.GetType().GetProperty(name)!.SetValue(_state, value);
        public T Get<T>(string name) => (T)_state.GetType().GetProperty(name)!.GetValue(_state)!;
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes[1..].ToArray()); }
        public void Dispose() => Connection.Disconnect();
    }
}
