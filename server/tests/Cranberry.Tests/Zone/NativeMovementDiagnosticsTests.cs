using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone;

public sealed class NativeMovementDiagnosticsTests
{
    // Existing captured August records, also covered by the movement codec tests.
    private static readonly byte[] Full = Convert.FromHexString("FF1F99F5B21C00850100000000000000000000000000000000000000000000000000000000");
    private static readonly byte[] PreciseOnly = Convert.FromHexString("00100000000000EA03D307BA0B0000002203");

    [Fact]
    public void ParsedProfilesDistinguishExactFullPreciseSparseVersionsAndStopFlagsByPhase()
    {
        using var f = new Fixture();
        var c = f.Add();
        SetPhase(c, "Lobby");
        f.Send(c, 2, At(Full, 100, 5));
        f.Send(c, 2, Header(101, 5));
        f.Send(c, 2, Flags(102, 0, 0x40));
        f.Send(c, 2, At(PreciseOnly, 103, 0));
        SetPhase(c, "InMatch");
        f.Send(c, 2, Header(104, 0));

        var snapshot = f.Snapshot();
        var wire = snapshot.GetProperty("MovementWire");
        var lobby = Phase(wire, "PlayerParsedByPhase", "Lobby");
        Assert.Equal(4, Value(lobby, "Records"));
        Assert.Equal(1, Value(lobby, "ExactFullRecords"));
        Assert.Equal(2, Value(lobby, "PreciseRecords"));
        Assert.Equal(1, Value(lobby, "HeaderOnlyRecords"));
        Assert.Equal(1, Value(lobby, "PositionRecords"));
        Assert.Equal(2, Value(lobby, "NonZeroVersionRecords"));
        Assert.Equal(1, Value(lobby, "VersionChanges"));
        Assert.Equal(2, Value(lobby, "MotionFlagsRecords"));
        Assert.Equal(2, Value(lobby, "StopFlagRecords"));
        Assert.Equal(0x1fff, Value(lobby, "FirstMask"));
        Assert.Equal(0x1000, Value(lobby, "LastMask"));
        Assert.Equal(5, Value(lobby, "FirstVersion"));
        Assert.Equal(0, Value(lobby, "LastVersion"));
        Assert.Equal(100, Value(lobby, "FirstClientTime"));
        Assert.Equal(103, Value(lobby, "LastClientTime"));
        Assert.Equal(0x61, Value(lobby, "FirstMotionFlags"));
        Assert.Equal(0x40, Value(lobby, "LastMotionFlags"));
        Assert.Equal(1, Value(Phase(wire, "PlayerParsedByPhase", "InMatch"), "Records"));
        var session = Assert.Single(snapshot.GetProperty("Sessions").EnumerateArray());
        Assert.Equal(lobby.GetRawText(), Phase(session.GetProperty("MovementWire"), "PlayerParsedByPhase", "Lobby").GetRawText());
        Assert.Empty(f.Snapshot().GetProperty("MovementWire").GetProperty("PlayerParsedByPhase").EnumerateArray());
    }

    [Fact]
    public void OfferedProfilesObserveActualBytesAndDoNotCallCoalescibleSnapshotsNativeFullRecords()
    {
        using var f = new Fixture();
        var c = f.Add();
        SetPhase(c, "Lobby");
        Invoke(f.Service, "RegisterPeerSession", c, c.Tag!);
        var sink = Get<PeerSession>(c, "Peer").Sink;
        sink.SendPose(10, 3, Header(1, 5), completeSnapshot: true);
        sink.SendPose(20, 4, At(Full, 2, 3), completeSnapshot: false);
        sink.SendPose(20, 4, Flags(3, 4, 0x20000040), completeSnapshot: false);

        var snapshot = f.Snapshot();
        var row = Phase(snapshot.GetProperty("MovementWire"), "PeerPoseOffersByReceiverPhase", "Lobby");
        Assert.Equal(3, Value(row, "Records"));
        Assert.Equal(1, Value(row, "ExactFullRecords"));
        Assert.Equal(1, Value(row, "PreciseRecords"));
        Assert.Equal(1, Value(row, "HeaderOnlyRecords"));
        Assert.Equal(1, Value(row, "VersionChanges")); // Different actors' versions are not compared.
        Assert.Equal(0x20000040, Value(row, "LastMotionFlags")); // Four-byte packed unsigned field.
        Assert.Equal(2, Value(row, "StopFlagRecords"));
        Assert.Equal(1, Value(snapshot.GetProperty("Replication"), "CompleteSnapshotOffers"));
        Assert.Equal(2, Value(snapshot.GetProperty("Replication"), "OrderedRecordOffers"));
        Assert.Empty(snapshot.GetProperty("MovementWire").GetProperty("PlayerParsedByPhase").EnumerateArray());
    }

    [Fact]
    public void VersionChangesDoNotCompareManagedEntitiesOrAdmissionGenerationsAndSurviveWindowReset()
    {
        using var f = new Fixture();
        var c = f.Add();
        SetPhase(c, "Dropping");
        f.Send(c, 3, Managed(7, Header(1, 5)));
        f.Send(c, 3, Managed(8, Header(2, 0)));
        f.Send(c, 3, Managed(8, Header(3, 1)));
        Assert.Equal(1, Value(Phase(f.Snapshot().GetProperty("MovementWire"), "ManagedParsedByPhase", "Dropping"), "VersionChanges"));
        f.Send(c, 3, Managed(8, Header(4, 2)));
        Assert.Equal(1, Value(Phase(f.Snapshot().GetProperty("MovementWire"), "ManagedParsedByPhase", "Dropping"), "VersionChanges"));
        Set(c, "MatchAdmissionGeneration", 100);
        f.Send(c, 3, Managed(8, Header(5, 3)));
        Assert.Equal(0, Value(Phase(f.Snapshot().GetProperty("MovementWire"), "ManagedParsedByPhase", "Dropping"), "VersionChanges"));
    }

    [Fact]
    public void MovementVersionRequestsAreCountedWithoutReplyAndBareTelemetryIsSeparate()
    {
        using var f = new Fixture();
        var c = f.Add();
        SetPhase(c, "Lobby");
        f.Send(c, 0, [0x0f, 0x57, 1, 0, 0, 0, 0, 0, 0, 0]);
        f.Send(c, 0, [0x0f, 0x57]);
        f.Send(c, 0, [0x57, .. new byte[20]]);
        f.Send(c, 2, [0x0f, 0x57]); // Invalid movement, not character-family dispatch.
        var snapshot = f.Snapshot();
        var wire = snapshot.GetProperty("MovementWire");
        Assert.Equal(1, Value(wire.GetProperty("MovementVersionRequestHeadersByPhase"), "Lobby"));
        Assert.Equal(1, Value(wire, "TruncatedMovementVersionRequestHeaders"));
        var session = Assert.Single(snapshot.GetProperty("Sessions").EnumerateArray()).GetProperty("MovementWire");
        Assert.Equal(1, Value(session, "MovementVersionRequestHeaders"));
        Assert.Equal(1, Value(session, "TruncatedMovementVersionRequestHeaders"));
        Assert.Empty(f.Recorded);
        Assert.Equal(0, Value(f.Snapshot().GetProperty("MovementWire").GetProperty("MovementVersionRequestHeadersByPhase"), "Lobby"));
    }

    [Fact]
    public void DisabledDiagnosticsPreserveMovementAndOfferedPacketBytesWithoutSessionCounters()
    {
        using var enabled = new Fixture();
        using var disabled = new Fixture(enabled: false);
        var a = enabled.Add(); var b = disabled.Add();
        byte[] precise = At(PreciseOnly, 100, 5);
        foreach (var (fixture, c) in new[] { (enabled, a), (disabled, b) })
        {
            fixture.Send(c, 2, precise);
            fixture.Send(c, 2, Header(101, 0));
            fixture.Send(c, 0, [0x0f, 0x57, 1, 0, 0, 0, 0, 0, 0, 0]);
            Invoke(fixture.Service, "RegisterPeerSession", c, c.Tag!);
            Get<PeerSession>(c, "Peer").Sink.SendPose(10, 3, precise, completeSnapshot: false);
        }
        var expected = Get<SessionMovementState>(a, "Movement").Player!;
        var actual = Get<SessionMovementState>(b, "Movement").Player!;
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.ClientTime, actual.ClientTime);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.LastUpdate.Payload.ToArray(), actual.LastUpdate.Payload.ToArray());
        Assert.Equal(enabled.Recorded.Count, disabled.Recorded.Count);
        Assert.NotEmpty(enabled.Recorded);
        for (int i = 0; i < enabled.Recorded.Count; i++) Assert.Equal(enabled.Recorded[i], disabled.Recorded[i]);
        Assert.False(disabled.Snapshot().GetProperty("Enabled").GetBoolean());
        Assert.Null(b.Tag!.GetType().GetField("ProductionMetrics")!.GetValue(b.Tag));
    }

    [Fact]
    public void UnsampledSessionProfilesResetAndNeverExportInternalStreamKeys()
    {
        using var f = new Fixture();
        var c = f.Add();
        f.Send(c, 2, Header(1, 5));
        f.Snapshot(0);
        var snapshot = f.Snapshot();
        var session = Assert.Single(snapshot.GetProperty("Sessions").EnumerateArray());
        Assert.Empty(session.GetProperty("MovementWire").GetProperty("PlayerParsedByPhase").EnumerateArray());
        Assert.DoesNotContain("StreamKey", snapshot.GetRawText());
        Assert.DoesNotContain("sensitive", snapshot.GetRawText());
        Assert.DoesNotContain("127.0.0.1", snapshot.GetRawText());
    }

    private static byte[] Header(uint time, byte version) => At(new byte[7], time, version);
    private static byte[] At(byte[] source, uint time, byte version)
    {
        var result = source.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), time);
        result[6] = version;
        return result;
    }
    private static byte[] Flags(uint time, byte version, uint flags)
    {
        using var writer = new PacketWriter();
        ClientVarInt.Write(writer, flags);
        var record = Header(time, version).Concat(writer.Written.ToArray()).ToArray();
        record[0] = 1;
        return record;
    }
    private static byte[] Managed(uint transient, byte[] movement)
    {
        using var writer = new PacketWriter();
        new ClientManagedMovementUpdate(transient, ClientMovementUpdate.Parse(movement)).WriteTo(writer);
        return writer.Written.ToArray();
    }
    private static JsonElement Phase(JsonElement root, string name, string phase) =>
        Assert.Single(root.GetProperty(name).EnumerateArray(), row => row.GetProperty("Phase").GetString() == phase);
    private static long Value(JsonElement element, string property) => element.GetProperty(property).GetInt64();
    private static void SetPhase(SoeConnection c, string name)
    {
        var property = c.Tag!.GetType().GetProperty("Match")!;
        property.SetValue(c.Tag, Enum.Parse(property.PropertyType, name));
    }
    private static void Set(SoeConnection c, string name, object value) => c.Tag!.GetType().GetProperty(name)!.SetValue(c.Tag, value);
    private static T Get<T>(SoeConnection c, string name) => (T)c.Tag!.GetType().GetProperty(name)!.GetValue(c.Tag)!;
    private static object? Invoke(ZoneService service, string name, params object[] arguments) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, arguments);

    private sealed class Fixture : IDisposable
    {
        private readonly List<SoeConnection> _connections = [];
        private readonly Recorder _recorder = new();
        public ZoneService Service { get; }
        public List<byte[]> Recorded => _recorder.Messages;
        public Fixture(bool enabled = true) => Service = new(new SilentLog(), _recorder) { DiagnosticsEnabled = enabled };
        public SoeConnection Add()
        {
            uint index = (uint)_connections.Count + 1;
            var request = new SessionRequest(3, index, 512, ZoneService.ProtocolName);
            var c = new SoeConnection(new(IPAddress.Loopback, 15000 + (int)index), in request,
                new(), SessionDecision.Clear, Service, new SilentLog(), (_, _) => { }, 0);
            Service.OnConnected(c);
            Set(c, "Authenticated", true); Set(c, "Guid", 100000000UL + index);
            Set(c, "AccountId", $"sensitive-account-{index}"); Set(c, "CharacterName", $"sensitive-name-{index}");
            Assert.True((bool)Invoke(Service, "InitializeAccountEconomy", c, c.Tag!)!);
            _connections.Add(c);
            return c;
        }
        public void Send(SoeConnection c, byte channel, byte[] payload) =>
            Service.OnMessage(c, [new GatewayHeader(GatewayTunnelFromClient.Opcode, channel).ToByte(), .. payload]);
        public JsonElement Snapshot(int maxSessions = 16) => JsonSerializer.SerializeToElement(Service.CaptureDiagnostics(maxSessions));
        public void Dispose() { foreach (var c in _connections) c.Disconnect(); }
    }
    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
    private sealed class Recorder : IPacketRecorder
    {
        public List<byte[]> Messages { get; } = [];
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c") Messages.Add(bytes.ToArray());
        }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> ciphertext) { }
    }
}
