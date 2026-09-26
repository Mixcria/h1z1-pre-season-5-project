using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Loot;
using Cranberry.Zone.World.Doors;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone;

public sealed class DeferredWorldCallbacksTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("doors")]
    [InlineData("devLoot")]
    public void MenuReadyReallySchedulesOneBurstAndCurrentWorldStillReceivesIt(string kind)
    {
        using var f = new Fixture(kind);
        f.MenuReady();
        Assert.True(f.Armed);
        Assert.Empty(f.Sent);
        Action callback = f.TakeTimer();
        f.MenuReady(); // Once-only latch must not arm a second burst.
        Assert.Empty(f.Sent);
        callback();
        Assert.True(f.Population > 0, string.Join(Environment.NewLine, f.Logs));
        Assert.Contains(f.Sent, p => p[0] == ZoneOpcodes.AddLightweightNpc);
        Assert.False(f.Timers.TryTake(out _, 50));
        output.WriteLine($"{kind}: current-world population={f.Population}; packets={f.Sent.Count}");
    }

    [Theory]
    [InlineData("doors", "Queued")]
    [InlineData("doors", "Transferring")]
    [InlineData("devLoot", "Queued")]
    [InlineData("devLoot", "Transferring")]
    public void SameLoginWorldBurstRemainsValidWhileQueueOrTransferHasNotZoned(string kind, string nextStep)
    {
        using var f = new Fixture(kind);
        f.MenuReady();
        Action callback = f.TakeTimer();
        int generation = f.Get<int>("WorldGeneration");
        f.Step(nextStep);
        callback();
        Assert.Equal(generation, f.Get<int>("WorldGeneration"));
        Assert.True(f.Population > 0);
        Assert.Contains(f.Sent, p => p[0] == ZoneOpcodes.AddLightweightNpc);
    }

    [Fact]
    public void ActualDefaultTwoSecondDoorTimerCannotRepopulateAfterNativeMenuExit()
    {
        using var f = new Fixture("doors", useDefaultDoorDelay: true);
        long started = Environment.TickCount64;
        f.MenuReady();
        f.Send([0x09, 0x4e, 0, 0]);
        Assert.True(f.Get<bool>("LogoutCompleted"));
        Assert.Equal(ConnectionState.Open, f.Connection.State);
        int sent = f.Sent.Count;
        Action callback = f.TakeTimer();
        long elapsed = Environment.TickCount64 - started;
        Assert.True(elapsed >= 1950, $"Default 2000 ms timer dispatched too early at {elapsed} ms.");
        callback();
        output.WriteLine($"default2s/nativeExit: elapsedMs={elapsed}; extraPackets={f.Sent.Count - sent}; stalePopulation={f.Population}");
        Assert.True(f.Sent.Count == sent && f.Population == 0,
            $"The default 2000 ms door callback sent {f.Sent.Count - sent} packet(s) and populated {f.Population} object(s) after native logout.");
    }

    [Theory]
    [InlineData("doors", "world")]
    [InlineData("doors", "nativeExit")]
    [InlineData("doors", "session")]
    [InlineData("devLoot", "world")]
    [InlineData("devLoot", "nativeExit")]
    [InlineData("devLoot", "session")]
    public void OldMenuBurstCannotSendOrPopulateAfterItsWorldOrSessionEnds(string kind, string invalidation)
    {
        using var f = new Fixture(kind);
        f.MenuReady();
        Action callback = f.TakeTimer();
        Assert.Equal(0, f.Population);
        if (invalidation == "world")
        {
            f.Set("WorldGeneration", f.Get<int>("WorldGeneration") + 1);
            f.Step("Zoning");
        }
        if (invalidation == "nativeExit")
        {
            // The real packet dispatcher calls StartLogout -> CompleteLogout -> AbandonMatch.
            // Menu exit deliberately retains the open connection for c3/c4.
            f.Send([0x09, 0x4e, 0, 0]);
            Assert.True(f.Get<bool>("LogoutCompleted"));
            Assert.Equal(ConnectionState.Open, f.Connection.State);
            Assert.Equal("Menu", f.Get<object>("Match").ToString());
            Assert.Contains(f.Sent, p => p.SequenceEqual(new byte[] { 0x11, 0x30, 0 }));
        }
        if (invalidation == "session") f.Connection.Tag = new object();
        int sent = f.Sent.Count;
        callback();
        output.WriteLine($"{kind}/{invalidation}: extraPackets={f.Sent.Count - sent}; stalePopulation={f.Population}");
        Assert.True(f.Sent.Count == sent && f.Population == 0,
            $"An obsolete {kind} callback sent {f.Sent.Count - sent} packet(s) and populated {f.Population} object(s) after {invalidation}.");
    }

    [Theory]
    [InlineData("doors")]
    [InlineData("devLoot")]
    public void ClosedConnectionAlreadyMakesPostedBurstInert(string kind)
    {
        using var f = new Fixture(kind);
        f.MenuReady();
        Action callback = f.TakeTimer();
        f.Connection.Disconnect();
        int sent = f.Sent.Count;
        callback();
        Assert.Equal(sent, f.Sent.Count);
        Assert.Equal(0, f.Population);
    }

    [Theory]
    [InlineData("Zoning")]
    [InlineData("Lobby")]
    [InlineData("Dropping")]
    [InlineData("InMatch")]
    public void NonMenuReadyDoesNotArmMenuObjectTimers(string step)
    {
        using var f = new Fixture("doors");
        f.Step(step);
        f.MenuReady();
        Assert.False(f.Get<bool>("LobbyDoorsArmed"));
        Assert.False(f.Get<bool>("DevGroundLootArmed"));
        Assert.False(f.Timers.TryTake(out _, 50));
    }

    [Fact]
    public void DoorSlicesAlreadyStopWhenGenerationChangesAfterTheFirstSlice()
    {
        using var f = new Fixture("doors", sliceSize: 1);
        f.MenuReady();
        f.TakeTimer()();
        Assert.Single(f.Sent, p => p[0] == ZoneOpcodes.AddLightweightNpc);
        Action nextSlice = f.TakeTimer();
        f.Set("WorldGeneration", f.Get<int>("WorldGeneration") + 1);
        int sent = f.Sent.Count;
        nextSlice();
        Assert.Equal(sent, f.Sent.Count);
    }

    [Fact]
    public void DoorSlicesCannotContinueIntoAReplacementSessionTag()
    {
        using var f = new Fixture("doors", sliceSize: 1);
        f.MenuReady();
        f.TakeTimer()();
        Assert.Single(f.Sent, p => p[0] == ZoneOpcodes.AddLightweightNpc);
        Action nextSlice = f.TakeTimer();
        f.Connection.Tag = new object();
        int sent = f.Sent.Count;
        nextSlice();
        output.WriteLine($"doorSlice/session: extraPackets={f.Sent.Count - sent}");
        Assert.Equal(sent, f.Sent.Count);
    }

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly string _kind;
        private readonly ZoneService _service;
        private readonly object _state;
        public SoeConnection Connection { get; }
        public BlockingCollection<Action> Timers { get; } = new();
        public List<byte[]> Sent { get; } = [];
        public List<string> Logs { get; } = [];
        public bool Armed => Get<bool>(_kind == "doors" ? "LobbyDoorsArmed" : "DevGroundLootArmed");
        public int Population => _kind == "doors" ? Get<MatchDoors?>("Doors")?.Count ?? 0 : Get<LootWorld>("Loot").Count;

        public Fixture(string kind, int sliceSize = 100, bool useDefaultDoorDelay = false)
        {
            _kind = kind;
            // Only the timer delay is accelerated. Default map, staging, door radius, data,
            // and packet writers are retained. Every invocation is the real posted callback.
            var options = new ZoneOptions
            {
                SendDoors = kind == "doors", SendLobbyDoors = true,
                LobbyDoorDelayMs = useDefaultDoorDelay ? new ZoneOptions().LobbyDoorDelayMs : 0,
                DevGroundLootMs = kind == "devLoot" ? 1 : 0, DevGroundLootCount = 1,
                BurstSliceSize = sliceSize, BurstSliceDelayMs = 0,
                EnableGas = false, AutoMatchMs = 0, SendProximateItems = true,
            };
            _service = new ZoneService(this, this, new GatewayTicketRegistry(), options)
            { Post = action => Timers.Add(action) };
            var request = new SessionRequest(3, 123, 512, ZoneService.ProtocolName);
            Connection = new SoeConnection(new(IPAddress.Loopback, 12345), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(Connection);
            _state = Connection.Tag!;
            Set("Authenticated", true);
            Set("Guid", 0x1001ul);
            Set("AppearanceReadySent", true); // Object timer path does not depend on dress bootstrap.
            Step("Menu");
        }

        public void MenuReady() => Send([ZoneOpcodes.ClientIsReady]);
        public void Send(byte[] packet) => _service.OnMessage(Connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), .. packet]);
        public Action TakeTimer()
        {
            Assert.True(Timers.TryTake(out Action? callback, 5000), string.Join(Environment.NewLine, Logs));
            return callback!;
        }
        public void Set(string name, object value) => _state.GetType().GetProperty(name)!.SetValue(_state, value);
        public T Get<T>(string name) => (T)_state.GetType().GetProperty(name)!.GetValue(_state)!;
        public void Step(string step)
        {
            PropertyInfo property = _state.GetType().GetProperty("Match")!;
            property.SetValue(_state, Enum.Parse(property.PropertyType, step));
        }
        public bool IsEnabled(TransportLogLevel level) => true;
        public void Log(TransportLogLevel level, string message) => Logs.Add(message);
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes[1..].ToArray()); }
        public void Dispose()
        {
            Connection.Tag = _state;
            Connection.Disconnect();
        }
    }
}
