using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone.Descent;

public sealed class TeleportReadinessTests
{
    [Theory]
    [InlineData("Menu")]
    [InlineData("Queued")]
    [InlineData("Transferring")]
    [InlineData("Zoning")]
    [InlineData("Lobby")]
    [InlineData("InMatch")]
    [InlineData("Ended")]
    public void ReadinessOutsidePendingDropCannotEnterMatchOrConsumeTheLoadGate(string phase)
    {
        using var world = new Fixture();
        world.ArmDrop();
        world.Phase = phase;

        world.Ready();

        Assert.Equal(phase, world.Phase);
        Assert.False(world.Get<bool>("TeleportReady"));
        Assert.False(world.Get<bool>("Released"));
        Assert.True(world.Watchdog.IsArmed(ClientMilestone.TeleportClientReady));
        Assert.Empty(world.Sent);
    }

    [Fact]
    public void ReadinessWithoutCurrentParachuteCannotRelease()
    {
        using var world = new Fixture();
        world.ArmDrop();
        world.Set("ChuteGuid", 0ul);

        world.Ready();

        Assert.Equal("Dropping", world.Phase);
        Assert.False(world.Get<bool>("TeleportReady"));
        Assert.False(world.Get<bool>("Released"));
        Assert.True(world.Watchdog.IsArmed(ClientMilestone.TeleportClientReady));
        Assert.Empty(world.Sent);
    }

    [Fact]
    public void CurrentClientReadinessReleasesOnceWithoutWaitingForAutoMount()
    {
        using var world = new Fixture();
        world.ArmDrop();

        world.Ready();

        Assert.Equal("InMatch", world.Phase);
        Assert.True(world.Get<bool>("TeleportReady"));
        Assert.True(world.Get<bool>("Released"));
        Assert.False(world.Get<bool>("MountRequested"));
        Assert.False(world.Watchdog.IsArmed(ClientMilestone.TeleportClientReady));
        Assert.Single(world.Sent, Fixture.IsRelease);
        long releasedAt = world.Get<long>("ReleasedAtMs");
        int sent = world.Sent.Count;
        int expected = world.Watchdog.ArmedCount;

        world.Ready();

        Assert.Equal(sent, world.Sent.Count);
        Assert.Equal(releasedAt, world.Get<long>("ReleasedAtMs"));
        Assert.Equal(expected, world.Watchdog.ArmedCount);
    }

    [Fact]
    public void EarlyAutoMountCannotBypassClientReadinessAndStillMountsNormally()
    {
        using var world = new Fixture();
        world.ArmDrop();

        world.AutoMount();

        Assert.True(world.Get<bool>("MountRequested"));
        Assert.False(world.Get<bool>("Released"));
        Assert.Equal("Dropping", world.Phase);
        Assert.DoesNotContain(world.Sent, Fixture.IsRelease);

        world.Ready();

        Assert.Equal("InMatch", world.Phase);
        Assert.Single(world.Sent, Fixture.IsRelease);
    }

    [Fact]
    public void AutoMountRetryCanCompleteAnAlreadyReadyPendingDropOnlyOnce()
    {
        using var world = new Fixture();
        world.ArmDrop();
        world.Set("TeleportReady", true);

        world.AutoMount();

        Assert.True(world.Get<bool>("MountRequested"));
        Assert.Equal("InMatch", world.Phase);
        Assert.Single(world.Sent, Fixture.IsRelease);
        int sent = world.Sent.Count;

        world.AutoMount();
        Assert.Equal(sent, world.Sent.Count);
    }

    [Theory]
    [InlineData("Dropping", true, false, false)]
    [InlineData("Dropping", false, true, false)]
    [InlineData("Dropping", true, true, true)]
    [InlineData("Lobby", true, true, false)]
    public void ReleaseHelperCannotBypassAnyPendingDropGate(
        string phase, bool parachute, bool ready, bool released)
    {
        using var world = new Fixture();
        world.ArmDrop();
        world.Phase = phase;
        world.Set("ChuteGuid", parachute ? Fixture.Chute : 0ul);
        world.Set("TeleportReady", ready);
        world.Set("Released", released);

        world.InvokeRelease();

        Assert.Equal(phase, world.Phase);
        Assert.Equal(released, world.Get<bool>("Released"));
        Assert.Equal(0, world.Get<long>("ReleasedAtMs"));
        Assert.Empty(world.Sent);
    }

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        public const ulong Chute = 0x2001;
        private readonly ZoneService _service;
        private readonly SoeConnection _connection;
        private readonly object _state;
        public List<byte[]> Sent { get; } = [];
        public ClientProgressWatchdog Watchdog => Get<ClientProgressWatchdog>("Watchdog");
        public string Phase
        {
            get => Get<object>("Match").ToString()!;
            set
            {
                PropertyInfo property = _state.GetType().GetProperty("Match")!;
                property.SetValue(_state, Enum.Parse(property.PropertyType, value));
            }
        }

        public Fixture()
        {
            // No scheduling callback: this fixture isolates the release wire/state transition.
            _service = new ZoneService(this, this, new GatewayTicketRegistry());
            var request = new SessionRequest(3, 123, 512, ZoneService.ProtocolName);
            _connection = new SoeConnection(new(IPAddress.Loopback, 12345), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(_connection);
            _state = _connection.Tag!;
            Set("Authenticated", true);
            Set("Guid", 0x1001ul);
        }

        public void ArmDrop()
        {
            Phase = "Dropping";
            Set("ChuteGuid", Chute);
            Watchdog.Expect(ClientMilestone.TeleportClientReady, "test pending teleport");
        }

        public void Ready() => Send([ZoneOpcodes.SynchronizedTeleportBase, (byte)SynchronizedTeleport.ClientReady, 0]);
        public void AutoMount()
        {
            using var writer = new PacketWriter();
            new VehicleAutoMount(Chute, Flag: false).WriteTo(writer);
            Send(writer.Written.ToArray());
        }
        public void InvokeRelease() => typeof(ZoneService)
            .GetMethod("ReleaseTeleport", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_service, [_connection, _state]);
        private void Send(byte[] packet) => _service.OnMessage(_connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), .. packet]);
        public static bool IsRelease(byte[] packet) => packet.AsSpan().SequenceEqual(new byte[] { 0xe8, 4, 0 });
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
