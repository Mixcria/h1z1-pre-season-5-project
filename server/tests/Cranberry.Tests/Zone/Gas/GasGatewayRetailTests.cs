using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Gas;

/// <summary>The production gateway pump and its real packet writer, with a synthetic admitted state.</summary>
public sealed class GasGatewayRetailTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CarOccupantsTakeGasDamageAtTheCarInsteadOfTheirOldOnFootPosition(int seat)
    {
        using var gateway = new Gateway(new GasSettings
        {
            PreMoveRing = GasPreMoveRing.Boundary, ToxicityMaxValue = 1000,
        });
        gateway.Movement.PinPlayer(Vector3.Zero);
        var roster = VehicleRoster.LoadDefault();
        var fleet = new VehicleFleet(roster);
        var car = new MatchVehicle(100, 1, roster.Require(1), new Vector3(9000, 500, 0), 0, 100_000, 10_000);
        fleet.Add(car);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(100, 1001, seat, 5000, out _, out _));
        gateway.Set("Fleet", fleet);
        gateway.Gas.Start(Environment.TickCount64 - 1001, 42);

        gateway.Invoke("PumpGas", gateway.Connection, gateway.State);

        Assert.Equal(car.Position, gateway.Samples[0].Position);
        Assert.True(gateway.Samples[0].Alive);
        Assert.Equal(9870u, gateway.Get<uint>("Hitpoints"));
        Assert.Equal(1000u, gateway.Gas.ToxicityForPlayer(0));
        Assert.Equal(1000u, gateway.LastToxicity());
    }

    [Fact]
    public void HudCopiesTheAuthoritativeMeterImmediatelyIncludingItsFirstSend()
    {
        using var gateway = new Gateway(new GasSettings { PreMoveRing = GasPreMoveRing.Boundary });
        gateway.Gas.Start(0, 42);
        gateway.Samples[0] = new(0, new Vector3(9000, 500, 0), true);
        gateway.Gas.Tick(1000, gateway.Samples);

        gateway.Invoke("PumpToxicity", gateway.Connection, gateway.State, gateway.Gas, 1000L, 1000L);
        Assert.Equal(1000u, gateway.LastToxicity());
        gateway.Gas.Tick(2000, gateway.Samples);
        // The render call's own wall clock has moved only one millisecond. It must still show
        // the controller's new state rather than apply a second independent one-second gate.
        gateway.Invoke("PumpToxicity", gateway.Connection, gateway.State, gateway.Gas, 2000L, 1001L);
        Assert.Equal(2000u, gateway.LastToxicity());
        int sent = gateway.Packets.Count;
        gateway.Invoke("PumpToxicity", gateway.Connection, gateway.State, gateway.Gas, 2000L, 1002L);
        Assert.Equal(sent, gateway.Packets.Count);
    }

    [Fact]
    public void DisablingToxicityHudPacketsKeepsProductionDamageAndHealthHudUpdates()
    {
        using var gateway = new Gateway(new GasSettings
        {
            PreMoveRing = GasPreMoveRing.Boundary, ToxicityMaxValue = 1000, SendToxicity = false,
        });
        gateway.Movement.PinPlayer(new Vector3(9000, 500, 0));
        gateway.Gas.Start(Environment.TickCount64 - 1001, 42);
        gateway.Invoke("PumpGas", gateway.Connection, gateway.State);
        Assert.Equal(9870u, gateway.Get<uint>("Hitpoints"));
        Assert.Equal(1000u, gateway.Gas.ToxicityForPlayer(0));
        Assert.DoesNotContain(gateway.Packets, p => p.Length == 102 && p[1] == 0x8d
            && BitConverter.ToUInt32(p, 15) == 611);
        var health = Assert.Single(gateway.Packets, p => p.Length == 102 && p[1] == 0x8d
            && BitConverter.ToUInt32(p, 15) == 1);
        Assert.Equal(9870u, BitConverter.ToUInt32(health, 23));
    }

    private sealed class Gateway : IPacketRecorder, ITransportLog, IDisposable
    {
        public List<byte[]> Packets { get; } = [];
        public ZoneService Service { get; }
        public SoeConnection Connection { get; }
        public object State => Connection.Tag!;
        public GasController Gas { get; }
        public SessionMovementState Movement => Get<SessionMovementState>("Movement");
        public PlayerSample[] Samples => Get<PlayerSample[]>("GasSamples");

        public Gateway(GasSettings gas)
        {
            gas = gas with { HudHealIntervalMs = 0, SafeZoneHealIntervalMs = 0 };
            Service = new(this, this, new GatewayTicketRegistry(), new ZoneOptions { Gas = gas });
            var request = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
            var remote = new IPEndPoint(IPAddress.Loopback, 5699);
            Connection = new(remote, in request, SessionSettings.WithSeed(1), SessionDecision.Clear,
                Service, this, (_, _) => { }, now: 0);
            Service.OnConnected(Connection);
            Set("Guid", 1001UL);
            Set("Hitpoints", 10_000u);
            PropertyInfo match = State.GetType().GetProperty("Match")!;
            match.SetValue(State, Enum.Parse(match.PropertyType, "InMatch"));
            Gas = new(gas);
            Set("Gas", Gas);
            Movement.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString("020018F6B21C00000000")));
        }

        public T Get<T>(string name) => (T)State.GetType().GetProperty(name)!.GetValue(State)!;
        public void Set(string name, object value) => State.GetType().GetProperty(name)!.SetValue(State, value);
        public void Invoke(string method, params object[] arguments) =>
            typeof(ZoneService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Service, arguments);
        public uint LastToxicity() => BitConverter.ToUInt32(Packets.Last(p =>
            p.Length == CharacterResourceUpdate.WireLength + 1 && p[1] == 0x8d
            && BitConverter.ToUInt32(p, 15) == 611), 23);
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c") Packets.Add(bytes.ToArray());
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void Dispose() => Connection.Disconnect();
    }
}
