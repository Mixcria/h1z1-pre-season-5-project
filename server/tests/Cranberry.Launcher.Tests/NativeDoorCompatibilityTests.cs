using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Launcher.Tests;

public sealed class NativeDoorCompatibilityTests
{
    private static readonly Lazy<Z2Doors> Data = new(Z2Doors.LoadDefault);
    private sealed class Sink : IPeerSink { public bool IsOpen => true; public void Send(byte[] packet) { } }
    private static byte[] State(DoorInstance door)
    {
        using var writer = new PacketWriter();
        door.StateUpdate().WriteTo(writer);
        return writer.Written.ToArray();
    }
    private static void AssertNativeState(DoorInstance door, bool open)
    {
        byte[] packet = State(door);
        Assert.Equal(22, packet.Length);
        Assert.Equal(new byte[] { 0x0f, 0x0a }, packet[..2]);
        Assert.Equal(door.WorldGuid, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(2)));
        Assert.Equal(open ? 1UL << 48 : 0, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(10)));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(18)));
        Assert.Equal(0, door.SwingDirection); // No custom-angle marker; original controller chooses +pi/2.
    }
    private static Vector3 Side(DoorInstance door, float side) => door.Position
        + new Vector3(MathF.Sin(door.Yaw) * side, 0, MathF.Cos(door.Yaw) * side);

    [Fact]
    public void ProtocolZeroIsAdmittedWithoutAnyHelperReadyCall()
    {
        var readiness = new DoorClientReadiness();
        Assert.False(readiness.IsReady("native"));
        readiness.Begin("native", "synthetic-native-ticket", 0);
        Assert.True(readiness.IsReady("native"));
        readiness.Remove("native");
        Assert.False(readiness.IsReady("native"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    public void PatchedOrUnknownLaunchProtocolsAreRejectedBeforeAdmission(int protocol)
    {
        var readiness = new DoorClientReadiness();
        var error = Assert.Throws<InvalidOperationException>(() => readiness.Begin("client", "synthetic-ticket", protocol));
        Assert.Contains("reopen the launcher", error.Message);
        Assert.False(readiness.IsReady("client"));
    }

    [Fact]
    public void OptionalCompatibilityConfirmationCannotCrossAccountsTicketsOrProtocols()
    {
        var readiness = new DoorClientReadiness();
        readiness.Begin("native", "first", 0);
        Assert.False(readiness.Confirm("other", "first", 0));
        Assert.False(readiness.Confirm("native", "wrong", 0));
        Assert.True(readiness.Confirm("native", "first", 0));
        Assert.Throws<InvalidOperationException>(() => readiness.Confirm("native", "first", 1));
        readiness.Begin("native", "replacement", 0);
        Assert.False(readiness.Confirm("native", "first", 0));
        Assert.True(readiness.Confirm("native", "replacement", 0));
        readiness.Remove("native");
        Assert.False(readiness.Confirm("native", "replacement", 0));
    }

    [Theory]
    [InlineData(-2f)]
    [InlineData(2f)]
    public void EveryDoorFamilyUsesOriginalStateRegardlessOfOpenerSide(float side)
    {
        int[] kinds = Enumerable.Range(0, Data.Value.Count)
            .GroupBy(i => Data.Value.KindOf(Data.Value[i]).Name).Select(group => group.First()).ToArray();
        Assert.True(kinds.Length >= 10);
        var doors = new MatchDoors(Data.Value);
        foreach (int index in kinds)
        {
            DoorInstance door = doors.Register(index);
            Assert.Equal(DoorToggleOutcome.Toggled, doors.TryToggle(door.WorldGuid, 1000, out _, Side(door, side)));
            AssertNativeState(door, true);
            Assert.Equal(DoorToggleOutcome.Toggled, doors.TryToggle(door.WorldGuid, 1800, out _, Side(door, -side)));
            AssertNativeState(door, false);
            Assert.Equal(DoorToggleOutcome.Toggled, doors.TryToggle(door.WorldGuid, 2600, out _, Side(door, -side)));
            AssertNativeState(door, true);
        }
    }

    [Fact]
    public void SharedDoorCooldownLateJoinRestreamAndResetKeepOneOriginalSwing()
    {
        var shared = new SharedMatchDoors();
        var a = new MatchDoors(Data.Value, shared: shared);
        var b = new MatchDoors(Data.Value, shared: shared);
        shared.Join(a, new Sink()); shared.Join(b, new Sink());
        DoorInstance da = a.Register(0), db = b.Register(0);
        Assert.Equal(DoorToggleOutcome.Toggled, a.TryToggle(da.WorldGuid, 1000, out _, Side(da, 2)));
        Assert.True(db.IsOpen); Assert.Equal(State(da), State(db)); AssertNativeState(db, true);
        Assert.Equal(DoorToggleOutcome.Absorbed, b.TryToggle(db.WorldGuid, 1002, out _, Side(db, -2)));
        b.Unregister(db.WorldGuid); db = b.Register(0);
        Assert.Equal(1000, db.LastToggleMs); AssertNativeState(db, true);
        var c = new MatchDoors(Data.Value, shared: shared); shared.Join(c, new Sink());
        DoorInstance dc = c.Register(0);
        AssertNativeState(dc, true); Assert.Equal(State(da), State(dc));
        Assert.Equal(DoorToggleOutcome.Absorbed, c.TryToggle(dc.WorldGuid, 1799, out _, Side(dc, -2)));
        Assert.Equal(DoorToggleOutcome.Toggled, c.TryToggle(dc.WorldGuid, 1800, out _, Side(dc, -2)));
        AssertNativeState(da, false); AssertNativeState(db, false); AssertNativeState(dc, false);
        Assert.Equal(0, shared.OpenCount);
        shared.Leave(a); shared.Leave(b); shared.Leave(c);
        var fresh = new MatchDoors(Data.Value, shared: shared); shared.Join(fresh, new Sink());
        DoorInstance reset = fresh.Register(0);
        AssertNativeState(reset, false); Assert.Equal(long.MinValue, reset.LastToggleMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RealHttpsLaunchAcceptsNativeWithoutHandshakeAndRejectsPatchedLauncher(int protocol)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "native-door-fixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "launcher-release"));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var options = new LauncherHostOptions { Port = ((IPEndPoint)listener.LocalEndpoint).Port }; listener.Stop();
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        File.WriteAllText(Path.Combine(root, "launcher-release", "manifest.json"),
            JsonSerializer.Serialize(new GameManifest(1, "native-door-fixture", "0.0.118.208059", [new("H1Z1.exe", 1, new string('0', 64))])));
        var log = new QuietLog();
        var zone = new ZoneService(log, log, new GatewayTicketRegistry(), new ZoneOptions()) { Post = action => action() };
        var accounts = new LocalAccountDirectory("owner");
        using var login = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var gateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var host = new LauncherHost(root, accounts, zone,
            ((IPEndPoint)login.Client.LocalEndPoint!).Port, ((IPEndPoint)gateway.Client.LocalEndPoint!).Port);
        await host.StartAsync();
        var settings = new LauncherSettings { ServerUrl = $"https://127.0.0.1:{options.Port}/", CertificateSha256 = host.CertificateSha256 };
        using var http = LauncherConnection.CreateHttp(settings);
        using var registration = await http.PostAsJsonAsync("api/register", new Credentials("NativeDoor", "synthetic-test-password", options.JoinCode));
        registration.EnsureSuccessStatusCode();
        var auth = (await registration.Content.ReadFromJsonAsync<AuthSession>())!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token);
        await using var tunnel = new GameTunnel(); await tunnel.Connect(settings, auth.Token);
        using var response = await http.PostAsJsonAsync("api/launch", new LaunchRequest(tunnel.GatewayPort, protocol));
        if (protocol == 0)
        {
            response.EnsureSuccessStatusCode();
            var launch = (await response.Content.ReadFromJsonAsync<GameLaunch>())!;
            Assert.Equal("native-door-fixture", launch.BuildId);
            Assert.False(string.IsNullOrEmpty(launch.Ticket));
            Assert.True(zone.DoorSwingClientReady!(auth.AccountId));
            // No call to api/client/doors-ready occurs: native admission needs no patch proof.
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("reopen the launcher", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);
            Assert.False(zone.DoorSwingClientReady!(auth.AccountId));
        }
    }

    private sealed class QuietLog : ITransportLog, IPacketRecorder
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }
}
