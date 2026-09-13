using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>Exercise the actual zone scheduler, native routes and per-viewer event cursors.</summary>
public sealed partial class AirdropGatewayTests
{
    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private sealed class Recorder : IPacketRecorder
    {
        public List<(SoeConnection Connection, byte[] Bytes)> Sent { get; } = [];
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add((connection, bytes.ToArray())); }
    }

    private static object? Call(ZoneService service, string name, params object?[] args) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args);
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);

    private sealed class Fixture : IDisposable
    {
        public Recorder Recorder { get; } = new();
        public ZoneService Service { get; }
        public long StartedAtMs { get; }
        public List<SoeConnection> Connections { get; } = [];
        public AirdropOptions Options { get; }
        public Fixture(bool bombs = false, bool oldMatch = false, int droppedLifetimeMs = 0, int maxDrops = 1)
        {
            StartedAtMs = Environment.TickCount64 - (oldMatch ? 300_000 : 0);
            Options = AirdropOptions.Default with
            {
                FirstDropAtMs = 0, MaxDropsPerMatch = maxDrops, BombRunDelayMs = 0,
                BombRunChance = 1, BombsEnabled = bombs,
            };
            Service = new ZoneService(new SilentLog(), Recorder, new GatewayTicketRegistry(),
                new ZoneOptions { SendProximateItems = false, Airdrop = Options,
                    LootStream = new LootStreamOptions { DroppedItemLifetimeMs = droppedLifetimeMs } });
        }

        public SoeConnection Add(ulong matchId = 555)
        {
            int n = Connections.Count;
            var request = new SessionRequest(3, (uint)n + 1, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 16000 + n), in request,
                new(), SessionDecision.Clear, Service, new SilentLog(), (_, _) => { }, 0);
            Service.OnConnected(connection);
            object state = connection.Tag!;
            var gas = new GasController(new GasSettings());
            gas.Start(StartedAtMs, 42);
            Set(state, "Gas", gas);
            Set(state, "Hitpoints", 10_000u);
            var matchProperty = state.GetType().GetProperty("Match")!;
            matchProperty.SetValue(state, Enum.Parse(matchProperty.PropertyType, "InMatch"));
            Set(state, "BountyAdmission", new MatchAdmissionContext(matchId, MatchQueueKind.Public, MatchMode.Solo));
            Call(Service, "JoinSharedLoot", connection, state);
            Call(Service, "StartAirdrops", state, 42UL, StartedAtMs);
            Connections.Add(connection);
            return connection;
        }

        public void Pump(SoeConnection connection, long clock) => Call(Service, "PumpAirdrops",
            connection, connection.Tag, Get<GasController>(connection.Tag!, "Gas"), clock, StartedAtMs + clock);

        public MatchAirdrops Controller(SoeConnection connection) => Get<MatchAirdrops>(connection.Tag!, "Airdrops");
        public IEnumerable<byte[]> Routes(SoeConnection connection) => Recorder.Sent
            .Where(p => ReferenceEquals(p.Connection, connection) && p.Bytes.Length > 12
                && p.Bytes[1] == 0x09 && p.Bytes[2] == 0x50 && p.Bytes[3] == 0)
            .Select(p => p.Bytes);
        public void Dispose()
        {
            foreach (SoeConnection c in Connections)
            { c.Disconnect(); Service.OnDisconnected(c, DisconnectCause.ServerRequested); }
        }
    }

    [Fact]
    public void ViewersShareOneControllerAndReceiveOneCompleteNativeRouteEach()
    {
        using var f = new Fixture();
        SoeConnection a = f.Add();
        SoeConnection b = f.Add();
        Assert.Same(f.Controller(a), f.Controller(b));
        f.Pump(a, 0);
        f.Pump(b, 0);
        f.Pump(a, 1);
        f.Pump(b, 1);
        Assert.Equal(1, f.Controller(a).Announced);
        byte[] first = Assert.Single(f.Routes(a));
        byte[] second = Assert.Single(f.Routes(b));
        Assert.Equal(new uint[] { 9215, 9218, 9219 }, Models(first));
        Assert.Equal(first.AsSpan(8).ToArray(), second.AsSpan(8).ToArray());
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(21, 1)]
    public void BomberCutoffCountsActualRunningMembers(int players, int bombers)
    {
        using var f = new Fixture(bombs: true);
        for (int i = 0; i < players; i++) f.Add();
        foreach (var c in f.Connections) f.Pump(c, 0);
        MatchAirdrops controller = f.Controller(f.Connections[0]);
        Assert.Equal(bombers, controller.BombRuns);
        Assert.Equal(1, controller.Announced);
        Assert.All(f.Connections, c => Assert.Same(controller, f.Controller(c)));
        Assert.All(f.Connections, c => Assert.Equal(1 + bombers, f.Routes(c).Count()));
        if (bombers > 0)
        {
            uint[] bombModels = Models(f.Routes(f.Connections[0]).Last());
            Assert.Equal(9215u, bombModels[0]);
            Assert.Equal(f.Options.BombsPerRun, bombModels.Count(m => m == 9372));
            Assert.DoesNotContain(9218u, bombModels);
        }
    }

    [Fact]
    public void LandedCratesShareIdentityAndUnlockFromTheScheduledImpactTime()
    {
        using var f = new Fixture();
        SoeConnection a = f.Add();
        SoeConnection b = f.Add();
        f.Pump(a, 0);
        long landsAt = Assert.Single(Assert.Single(f.Controller(a).Flights).Payloads).ImpactAtMs;
        f.Pump(a, landsAt + 30_000);
        f.Pump(b, landsAt + 60_000);
        var crateA = Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(a.Tag!, "AirdropCrates")).Value;
        var crateB = Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(b.Tag!, "AirdropCrates")).Value;
        Assert.Same(crateA, crateB);
        Assert.Equal(f.StartedAtMs + landsAt + f.Options.UnlockMs, crateA.UnlockAtMs);
        f.Pump(a, landsAt + 90_000);
        Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(a.Tag!, "AirdropCrates"));
    }

    [Fact]
    public void LateViewerDoesNotReplayOldBombDamageOrExpiredVisualRoutes()
    {
        using var f = new Fixture(bombs: true, oldMatch: true);
        for (int i = 0; i < 21; i++) f.Add();
        f.Pump(f.Connections[0], 0);
        AirdropPayload bomb = f.Controller(f.Connections[0]).Flights
            .Single(flight => flight.Kind == AirdropFlightKind.Bomber).Payloads[0];
        f.Pump(f.Connections[0], 300_000);
        SoeConnection late = f.Add();
        SessionMovementState movement = Get<SessionMovementState>(late.Tag!, "Movement");
        movement.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString("020018F6B21C00000000")));
        movement.PinPlayer(bomb.ImpactPosition + new Vector3(6.5f, 0, 0));
        f.Pump(late, 300_000);
        Assert.Equal(10_000u, Get<uint>(late.Tag!, "Hitpoints"));
        Assert.Empty(f.Routes(late));
        // Establish that the same position really is vulnerable if a new strike occurs now.
        Call(f.Service, "ApplyAirdropBomb", late, late.Tag, bomb.ImpactPosition);
        Assert.Equal(5_000u, Get<uint>(late.Tag!, "Hitpoints"));
    }

    [Fact]
    public void LethalBomberImpactUsesTheAugustBombingRunResultsAndOneDeathFlow()
    {
        using var f = new Fixture(bombs: true);
        for (int i = 0; i < 21; i++) f.Add();
        SoeConnection victim = f.Connections[0];
        f.Pump(victim, 0);
        AirdropPayload bomb = f.Controller(victim).Flights
            .Single(flight => flight.Kind == AirdropFlightKind.Bomber).Payloads[0];
        SessionMovementState movement = Get<SessionMovementState>(victim.Tag!, "Movement");
        movement.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString("020018F6B21C00000000")));
        movement.PinPlayer(bomb.ImpactPosition);
        f.Recorder.Sent.Clear();
        f.Pump(victim, bomb.ImpactAtMs);

        Assert.Equal(0u, Get<uint>(victim.Tag!, "Hitpoints"));
        Assert.True(Get<bool>(victim.Tag!, "DeathSent"));
        Assert.False(Get<GasController>(victim.Tag!, "Gas").Running);
        Assert.Equal("Ended", Get<object>(victim.Tag!, "Match").ToString());
        Assert.True(Get<long>(victim.Tag!, "EndedAtMs") > 0);
        var packets = f.Recorder.Sent.Where(p => ReferenceEquals(p.Connection, victim))
            .Select(p => p.Bytes).ToArray();
        byte[] deathInfo = Assert.Single(packets, p => p.Length > 3 && p[1] == 0xce && p[2] == 0x04);
        Assert.Equal(25, deathInfo.Length);
        Assert.Equal(0x43u, BitConverter.ToUInt32(deathInfo, 21));
        Assert.Equal(0u, BitConverter.ToUInt32(deathInfo, 9)); // no player killer name
        Assert.Equal(0u, BitConverter.ToUInt32(deathInfo, 17)); // no source substitution
        Assert.Equal("UI.Results.Rank.BombingRun",
            DeathCauseCodes.RankKey((DeathCauseCode)BitConverter.ToUInt32(deathInfo, 21)));
        Assert.Single(packets, p => p.Length > 3 && p[1] == 0x0f && p[2] == 0x4f); // ragdoll
        Assert.Single(packets, p => p.Length > 3 && p[1] == 0x0f && p[2] == 0x48); // death screen
        Assert.Contains(packets, p => p.Length > 3 && p[1] == 0xce && p[2] == 0x09); // survivors

        int sent = f.Recorder.Sent.Count;
        Call(f.Service, "ApplyAirdropBomb", victim, victim.Tag, bomb.ImpactPosition);
        Assert.Equal(sent, f.Recorder.Sent.Count);
    }

    [Fact]
    public void GasFastForwardDoesNotAdvanceAnAlreadyDeliveredFlight()
    {
        using var f = new Fixture();
        SoeConnection viewer = f.Add();
        f.Pump(viewer, 0);
        AirdropFlight flight = Assert.Single(f.Controller(viewer).Flights);
        long impactAt = Assert.Single(flight.Payloads).ImpactAtMs;
        GasController gas = Get<GasController>(viewer.Tag!, "Gas");
        Assert.True(gas.AdvanceToNextEvent(f.StartedAtMs));
        Assert.True(gas.MatchClockAt(f.StartedAtMs) > impactAt);

        Call(f.Service, "PumpAirdrops", viewer, viewer.Tag, gas,
            gas.MatchClockAt(f.StartedAtMs + 1), f.StartedAtMs + 1);
        Assert.Equal(0, f.Controller(viewer).Landed);
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
        Assert.Single(f.Routes(viewer));

        Call(f.Service, "PumpAirdrops", viewer, viewer.Tag, gas,
            gas.MatchClockAt(f.StartedAtMs + impactAt), f.StartedAtMs + impactAt);
        Assert.Equal(1, f.Controller(viewer).Landed);
        Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
    }

    [Fact]
    public void BombDamageUsesTheManagedParachutePoseAndNeverItsStaleGroundPose()
    {
        using var f = new Fixture();
        SoeConnection viewer = f.Add();
        object state = viewer.Tag!;
        SessionMovementState movement = Get<SessionMovementState>(state, "Movement");
        movement.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString("020018F6B21C00000000")));
        Set(state, "ChuteGuid", 0x2001UL);
        Set(state, "MountRequested", true);
        // Until channel 3 resolves the chute, the old ground position cannot be used for a blast.
        Call(f.Service, "ApplyAirdropBomb", viewer, state, Vector3.Zero);
        Assert.Equal(10_000u, Get<uint>(state, "Hitpoints"));

        // Real August managed-movement capture, wire-20260829-085701 at 08:57:58.939.
        ClientManagedMovementUpdate update = ClientManagedMovementUpdate.Parse(Convert.FromHexString(
            "9008FF1F2F2F1F00002511BDDA021C7E189DB73B0000000000000000000000002203220322032203000000000000000000"));
        movement.RegisterManagedEntity(new ZoneOptions().ParachuteTransientId, 0x2001);
        Assert.True(movement.TryApplyManaged(update, out var chute));
        Call(f.Service, "ApplyAirdropBomb", viewer, state, Vector3.Zero);
        Assert.Equal(10_000u, Get<uint>(state, "Hitpoints"));

        Vector3 realPosition = chute.Movement!.Position!.Value;
        Call(f.Service, "ApplyAirdropBomb", viewer, state, realPosition + new Vector3(6.5f, 0, 0));
        Assert.Equal(5_000u, Get<uint>(state, "Hitpoints"));
    }

    private static uint[] Models(byte[] tunnel)
    {
        ReadOnlySpan<byte> packet = tunnel.AsSpan(1);
        int count = (int)BitConverter.ToUInt32(packet.Slice(7, 4));
        var models = new uint[count];
        int offset = 11;
        for (int i = 0; i < count; i++)
        {
            models[i] = BitConverter.ToUInt32(packet.Slice(offset, 4));
            int knots = (int)BitConverter.ToUInt32(packet.Slice(offset + 40, 4));
            offset += 44 + knots * 20;
        }
        Assert.Equal(packet.Length, offset);
        return models;
    }
}
