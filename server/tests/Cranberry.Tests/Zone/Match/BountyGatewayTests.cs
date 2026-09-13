using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Match;
using Cranberry.Zone.HostedGames;

namespace Cranberry.Tests.Zone.MatchLobby;

/// <summary>Real gateway dispatcher and durable store; these are send-side tests, not UI screenshots.</summary>
public sealed partial class BountyGatewayTests
{
    private sealed class Recorder : IPacketRecorder
    {
        public List<(SoeConnection Connection, byte[] Bytes)> Sent { get; } = [];
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add((connection, bytes.ToArray())); }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cranberry-bounty-gateway", Guid.NewGuid().ToString("N"));
        public ConcurrentQueue<Action> Pending { get; } = new();
        public Recorder Recorder { get; } = new();
        public GatewayTicketRegistry Tickets { get; } = new();
        public ZoneService Service { get; }
        public AccountEconomyStore Store => new(Root);
        private readonly List<SoeConnection> _connections = [];

        public Fixture(MatchQueueKind queue = MatchQueueKind.Public, MatchMode mode = MatchMode.Solo,
            uint countdown = 10000, uint crowns = 1000, LobbyOptions? lobby = null,
            MatchAdmissionRegistry? admissions = null, PublicQueueOptions? publicQueue = null,
            Cranberry.Zone.DevConsole.ConsoleOptions? console = null)
        {
            HostedGameStore? hosted = null;
            if (queue == MatchQueueKind.Hosted)
            {
                hosted = new(worlds: [new GameWorldDefinition(1, 13, "Solo", IsHosted: true)]);
                var key = hosted.IssueHostKey("admin", true, "EU");
                Assert.True(hosted.Redeem("a", key.Secret!).Success);
                Assert.True(hosted.CreateGame("a", "EU", 13, "Private bounty test").Success);
            }
            Service = new(new SilentLog(), Recorder, Tickets, new ZoneOptions
            {
                EconomyStoreRoot = Root,
                HostedGames = hosted,
                MatchAdmissions = admissions ?? new([new(1, 13, queue, mode)]),
                PublicQueue = publicQueue,
                Console = console ?? Cranberry.Zone.DevConsole.ConsoleOptions.Default,
                AutoMatchMs = 0, LobbyCountdownMs = countdown,
                Lobby = lobby ?? LobbyOptions.Default with { FallbackArmMs = 1 },
                MenuTopBar = MenuTopBarOptions.Default with { Crowns = crowns, Skulls = 1000, Credits = 300 },
                EnableGas = false, SendDoors = false, SendVehicles = false, SendContainers = false,
                GroundLootRadius = 0, DevGroundLootMs = 0,
            }) { Post = Pending.Enqueue };
        }

        public SoeConnection Connect(string account = "a", ulong? character = null)
        {
            int number = _connections.Count + 1;
            GatewayAdmission admission = Tickets.Issue(character ?? (ulong)(0x1000 + number), "BountyTest", gender: 2,
                headId: 3, hairId: 2, skinToneId: 664, profileId: 270, accountId: account);
            var request = new SessionRequest(3, (uint)(0x11223300 + number), 512, ZoneService.ProtocolName);
            var address = new IPEndPoint(IPAddress.Loopback, 5500 + number);
            var connection = new SoeConnection(address, in request, SessionSettings.WithSeed(1),
                Service.OnSessionRequest(address, in request), Service, new SilentLog(), (_, _) => { }, now: 0);
            _connections.Add(connection);
            Service.OnConnected(connection);
            using var writer = new PacketWriter();
            writer.WriteByte(GatewayLoginRequest.Opcode);
            writer.WriteUInt64(admission.Guid);
            writer.WriteString(admission.Ticket);
            writer.WriteString(GatewayLoginRequest.AugustProtocol);
            writer.WriteString(GatewayLoginRequest.AugustVersion);
            Service.OnMessage(connection, writer.Written.ToArray());
            Send(connection, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
            return connection;
        }

        public void Send(SoeConnection connection, Action<PacketWriter> body)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            body(writer);
            Service.OnMessage(connection, writer.Written.ToArray());
        }

        public byte[][] Sent(SoeConnection connection) => [.. Recorder.Sent.Where(row => row.Connection == connection).Select(row => row.Bytes)];
        public void Select(SoeConnection connection, uint option) => Send(connection, w => new SelectBountyRequest(option).WriteTo(w));
        public void Transfer(SoeConnection connection, uint world = 1, string text = "") => Send(connection, w =>
        {
            w.WriteByte(0xec); w.WriteUInt32(world); w.WriteString(text);
            w.WriteUInt32(0); w.WriteByte(1); w.WriteInt32(1);
        });
        public void Zone(SoeConnection connection, uint world = 1)
        {
            int before = Sent(connection).Count(packet => packet.Length > 1 && packet[1] == ZoneOpcodes.ClientBeginZoning);
            Transfer(connection, world); Transfer(connection, world);
            Pump(() => Sent(connection).Count(packet => packet.Length > 1 && packet[1] == ZoneOpcodes.ClientBeginZoning) > before);
            Send(connection, w => w.WriteByte(ZoneOpcodes.ClientIsReady));
        }
        public void Ready(SoeConnection connection) => Send(connection, w => { w.WriteByte(ZoneOpcodes.ClientFinishedLoading); w.WriteByte(0); });
        public void Cancel(SoeConnection connection) => Send(connection, w => w.WriteByte(ZoneOpcodes.CancelQueueOnWorld));
        public void Pump(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.ElapsedMilliseconds < 5000)
            {
                if (Pending.TryDequeue(out Action? action)) action();
                else Thread.Sleep(1);
            }
            Assert.True(done(), "Expected gateway transition did not occur within five seconds.");
        }
        public BountyBackingRecord Record(string account = "a") => JsonSerializer.Deserialize<BountyBackingRecord>(
            Assert.Single(Store.GetOrCreate(account).States, row => row.Key.StartsWith(BountyLedger.RecordPrefix)).Value)!;
        public FileStream BlockStore(string account = "a") => new(Path.Combine(Root, AccountEconomyStore.FileNameFor(account)) + ".lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        public void Disconnect(SoeConnection connection)
        {
            if (connection.State != ConnectionState.Open) return;
            connection.Disconnect();
            Service.OnDisconnected(connection, DisconnectCause.ServerRequested);
        }
        public void Dispose()
        {
            foreach (var connection in _connections) Disconnect(connection);
            Service.FlushRankedScores();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static bool Is(byte[] packet, byte opcode, byte sub) => packet.Length >= 3 && packet[1] == opcode && packet[2] == sub;
    private static bool Hud(byte[] packet, ushort sub) => packet.Length >= 4 && packet[1] == 0xce && BitConverter.ToUInt16(packet, 2) == sub;
    private static bool InBox(byte[] packet, bool value) => Hud(packet, 0x15) && packet.Length == 5 && packet[4] == (value ? 1 : 0);
    private static uint[] Costs(byte[] packet)
    {
        var reader = new PacketReader(packet.AsSpan(1));
        reader.Skip(2); reader.ReadUInt64();
        uint count = reader.ReadUInt32();
        var result = new uint[count];
        for (int i = 0; i < count; i++) { reader.ReadUInt32(); reader.ReadString(); result[i] = reader.ReadUInt32(); }
        Assert.True(reader.AtEnd);
        return result;
    }

    [Fact]
    public void PublicSoloOffersAtWorldReadyAndPersistsOneChargeBeforeDropping()
    {
        using var fixture = new Fixture(countdown: 200);
        var connection = fixture.Connect();
        fixture.Zone(connection);
        fixture.Select(connection, 1);
        Assert.Equal(1000u, fixture.Store.GetOrCreate("a").Balance(4));
        Assert.DoesNotContain(fixture.Sent(connection), p => InBox(p, true));
        fixture.Ready(connection);
        byte[][] lobby = fixture.Sent(connection);
        int inBox = Array.FindIndex(lobby, p => InBox(p, true));
        int countdown = Array.FindIndex(lobby, p => Hud(p, 0x0f));
        Assert.True(inBox >= 0 && countdown > inBox);
        Assert.Contains(lobby.Take(countdown), p => Is(p, 0x67, 0x10) && Costs(p).SequenceEqual(new uint[] { 500, 500, 100, 100 }));
        fixture.Select(connection, 1);
        fixture.Select(connection, 2);
        Assert.Equal(500u, fixture.Store.GetOrCreate("a").Balance(4));
        Assert.Equal(1000u, fixture.Store.GetOrCreate("a").Balance(5));
        Assert.Equal(1u, fixture.Record().OptionId);
        fixture.Pump(() => fixture.Sent(connection).Any(p => Hud(p, 0x16)));
        byte[][] sent = fixture.Sent(connection);
        int start = Array.FindIndex(sent, p => Hud(p, 0x16));
        Assert.Contains(sent.Take(start), p => InBox(p, false));
        Assert.All(sent.Skip(start), p => Assert.False(InBox(p, true)));
        Assert.All(Costs(sent.Take(start).Last(p => Is(p, 0x67, 0x10))), amount => Assert.Equal(0u, amount));
        Assert.Equal("Locked", fixture.Record().Status);
        fixture.Select(connection, 3);
        Assert.Equal(300u, fixture.Store.GetOrCreate("a").Balance(6));
        Assert.Equal(1u, fixture.Record().OptionId);
    }

    [Theory]
    [InlineData(MatchQueueKind.Public, MatchMode.Duos)]
    [InlineData(MatchQueueKind.Public, MatchMode.Fives)]
    [InlineData(MatchQueueKind.Hosted, MatchMode.Solo)]
    [InlineData(MatchQueueKind.Custom, MatchMode.Solo)]
    public void OtherModesStillEnterPregameButReceiveNoBountyOfferOrBacking(MatchQueueKind queue, MatchMode mode)
    {
        using var fixture = new Fixture(queue, mode);
        var connection = fixture.Connect();
        fixture.Zone(connection); fixture.Ready(connection);
        byte[][] lobby = fixture.Sent(connection);
        Assert.Contains(lobby, p => InBox(p, true));
        Assert.All(lobby.Where(p => Is(p, 0x67, 0x10)), p => Assert.All(Costs(p), amount => Assert.Equal(0u, amount)));
        Assert.DoesNotContain(lobby, p => Is(p, 0x67, 0x0d) || Is(p, 0x67, 0x0e));
        fixture.Select(connection, 1);
        Assert.Equal(1000u, fixture.Store.GetOrCreate("a").Balance(4));
        Assert.DoesNotContain(fixture.Sent(connection), p => Is(p, 0x67, 0x0d) || Is(p, 0x67, 0x0e));
        Assert.DoesNotContain(fixture.Store.GetOrCreate("a").States, row => row.Key.StartsWith(BountyLedger.RecordPrefix));
    }

    [Fact]
    public void UnknownWorldAndCustomTextCannotFallBackToPublicSolo()
    {
        using var fixture = new Fixture();
        var connection = fixture.Connect();
        fixture.Transfer(connection, world: 99);
        fixture.Transfer(connection, text: "hosted-solo-code");
        fixture.Select(connection, 1);
        Assert.DoesNotContain(fixture.Sent(connection), p => InBox(p, true) || Is(p, 0x67, 0x0e));
        Assert.Equal(1000u, fixture.Store.GetOrCreate("a").Balance(4));
    }

    [Fact]
    public void InvalidOptionAndInsufficientBalanceDoNotCreateBacking()
    {
        using var fixture = new Fixture(crowns: 499);
        var connection = fixture.Connect();
        fixture.Zone(connection); fixture.Ready(connection);
        fixture.Select(connection, 0); fixture.Select(connection, 4); fixture.Select(connection, 1);
        Assert.Equal(499u, fixture.Store.GetOrCreate("a").Balance(4));
        Assert.DoesNotContain(fixture.Store.GetOrCreate("a").States, row => row.Key.StartsWith(BountyLedger.RecordPrefix));
    }

    [Fact]
    public void FailedLobbyDisconnectRefundSurvivesTheClosedConnectionAndReleasesFreeBounty()
    {
        using var fixture = new Fixture();
        var connection = fixture.Connect();
        fixture.Zone(connection); fixture.Ready(connection); fixture.Select(connection, 3);
        using (fixture.BlockStore()) fixture.Disconnect(connection);
        Assert.Equal("Backed", fixture.Record().Status);
        Assert.Equal(200u, fixture.Store.GetOrCreate("a").Balance(6));
        fixture.Pump(() => fixture.Record().Status == "Cancelled");
        Assert.Equal(300u, fixture.Store.GetOrCreate("a").Balance(6));
        var reconnected = fixture.Connect();
        fixture.Zone(reconnected); fixture.Ready(reconnected); fixture.Select(reconnected, 3);
        Assert.Equal(200u, fixture.Store.GetOrCreate("a").Balance(6));
    }

    [Fact]
    public void FailedLiveDeparturePaymentSurvivesResettingAdmissionToTheMenu()
    {
        using var fixture = new Fixture(countdown: 100);
        var connection = fixture.Connect();
        fixture.Zone(connection); fixture.Ready(connection); fixture.Select(connection, 1);
        fixture.Pump(() => fixture.Sent(connection).Any(p => Hud(p, 0x16)));
        using (fixture.BlockStore()) fixture.Cancel(connection);
        Assert.Equal("Locked", fixture.Record().Status);
        fixture.Pump(() => fixture.Record().Status == "Settled");
        Assert.Equal(1500u, fixture.Store.GetOrCreate("a").Balance(5));
        Assert.Equal(400u, fixture.Store.GetOrCreate("a").Balance(6));
        fixture.Cancel(connection);
        Assert.Equal(1500u, fixture.Store.GetOrCreate("a").Balance(5));
    }

    [Fact]
    public void FirstDropClosesTheWholeActualMatchAndLocksEveryBackedMember()
    {
        using var fixture = new Fixture(countdown: 200);
        var first = fixture.Connect("a");
        var second = fixture.Connect("b");
        fixture.Zone(first); fixture.Zone(second);
        fixture.Ready(first);
        Thread.Sleep(80);
        fixture.Ready(second);
        fixture.Select(first, 1); fixture.Select(second, 2);
        Assert.Equal(fixture.Record("a").MatchId, fixture.Record("b").MatchId);
        byte[] table = fixture.Sent(first).Last(p => Is(p, 0x67, 0x0e));
        Assert.Equal(2u, BitConverter.ToUInt32(table, 3));
        Assert.Equal(2u, BitConverter.ToUInt32(table, 7));
        fixture.Pump(() => fixture.Sent(first).Any(p => Hud(p, 0x16)));
        // Members now share one deadline, so either callback may run first. Both must start,
        // and closing that shared match must still lock every member's backing exactly once.
        fixture.Pump(() => fixture.Sent(second).Any(p => Hud(p, 0x16)));
        Assert.Equal("Locked", fixture.Record("a").Status);
        Assert.Equal("Locked", fixture.Record("b").Status);
        Assert.All(Costs(fixture.Sent(second).Last(p => Is(p, 0x67, 0x10))), amount => Assert.Equal(0u, amount));
        fixture.Select(second, 3);
        Assert.Equal(300u, fixture.Store.GetOrCreate("b").Balance(6));
        Assert.Equal(2u, fixture.Record("b").OptionId);
    }

    [Fact]
    public void AnOldFailedLockRetryCannotStartANewLobbyOnTheSameGateway()
    {
        using var fixture = new Fixture(countdown: 1500);
        var connection = fixture.Connect();
        fixture.Zone(connection); fixture.Ready(connection); fixture.Select(connection, 1);
        int costsBefore = fixture.Sent(connection).Count(p => Is(p, 0x67, 0x10));
        using (fixture.BlockStore())
        {
            fixture.Pump(() => fixture.Sent(connection).Count(p => Is(p, 0x67, 0x10)) > costsBefore);
            fixture.Cancel(connection);
        }
        fixture.Zone(connection); fixture.Ready(connection);
        int before = fixture.Sent(connection).Length;
        // Old lock retry is due 1s after the failed lock; the new countdown is due later.
        var elapsed = Stopwatch.StartNew();
        fixture.Pump(() => elapsed.ElapsedMilliseconds >= 750);
        Assert.DoesNotContain(fixture.Sent(connection).Skip(before), p => Hud(p, 0x16));
        Assert.Equal(1000u, fixture.Store.GetOrCreate("a").Balance(4));
    }
}
