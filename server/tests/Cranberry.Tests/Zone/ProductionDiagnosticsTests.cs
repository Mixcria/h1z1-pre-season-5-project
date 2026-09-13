using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone;

public sealed class ProductionDiagnosticsTests
{
    [Fact]
    public void MovementCountersDistinguishAppliedSuppressedAndMalformedWithoutChangingState()
    {
        using var f = new Fixture();
        var c = f.Add();
        f.Send(c, 2, Convert.FromHexString("00100000000000EA03D307BA0B0000002203"));
        var movement = Get<SessionMovementState>(c, "Movement");
        var applied = movement.Player;
        f.Send(c, 2, Convert.FromHexString("020018F6B21C00000000")); // Dummy origin.
        f.Send(c, 2, [1]); // Truncated mask.
        Set(c, "ChuteGuid", 55UL); Set(c, "MountRequested", true);
        f.Send(c, 2, Sparse(1));
        Set(c, "ChuteGuid", 0UL); Set(c, "MountRequested", false);
        Assert.Same(applied, movement.Player);

        byte[] managed = Managed(7, Sparse(10));
        f.Send(c, 3, managed); // An unowned entity must not become authoritative.
        Assert.Equal(0, movement.ManagedEntityCount);
        movement.RegisterManagedEntity(7, 12345);
        Set(c, "ChuteGuid", 12345UL); // The registration must still name this session's owned actor.
        f.Send(c, 3, managed);
        f.Send(c, 3, [0x90]);

        var snapshot = f.Snapshot();
        var foot = snapshot.GetProperty("PlayerMovement").GetProperty("Counts");
        Assert.Equal(4, Value(foot, "Received"));
        Assert.Equal(3, Value(foot, "Parsed"));
        Assert.Equal(1, Value(foot, "Applied"));
        Assert.Equal(1, Value(foot, "ZeroPoseSuppressed"));
        Assert.Equal(1, Value(foot, "MountedSuppressed"));
        Assert.Equal(1, Value(foot, "Malformed"));
        var vehicle = snapshot.GetProperty("ManagedMovement").GetProperty("Counts");
        Assert.Equal(3, Value(vehicle, "Received"));
        Assert.Equal(1, Value(vehicle, "Applied"));
        Assert.Equal(1, Value(vehicle, "UnownedSuppressed"));
        Assert.Equal(1, Value(vehicle, "Malformed"));
        Assert.Equal(4, Value(snapshot.GetProperty("PlayerMovement").GetProperty("HandlerDuration"), "Count"));
        Assert.Equal(0, Value(f.Snapshot().GetProperty("PlayerMovement").GetProperty("Counts"), "Received"));
    }

    [Fact]
    public void ClientClockWrapDuplicateAndResetAreObservationsNotServerLatency()
    {
        using var f = new Fixture();
        var c = f.Add();
        f.Send(c, 2, Sparse(uint.MaxValue - 10));
        f.Send(c, 2, Sparse(5)); // Forward 16 ms across uint wrap.
        f.Send(c, 2, Sparse(5));
        f.Send(c, 2, Sparse(4)); // Backward/reset, still accepted by existing protocol behavior.
        Assert.Equal(4u, Get<SessionMovementState>(c, "Movement").Player!.ClientTime);
        var movement = f.Snapshot().GetProperty("PlayerMovement");
        Assert.Equal(1, Value(movement, "ClientTimestampForwardWraps"));
        Assert.Equal(1, Value(movement, "ClientTimestampDuplicates"));
        Assert.Equal(1, Value(movement, "ClientTimestampBackwardOrReset"));
        Assert.Equal(3, Value(movement.GetProperty("ArrivalIntervals"), "Count"));
        Assert.Equal(1, Value(movement.GetProperty("ClientForwardDeltaMs"), "Count"));
        Assert.InRange(movement.GetProperty("ClientForwardDeltaMs").GetProperty("SumMs").GetDouble(), 15.99, 16.01);
    }

    [Fact]
    public void SamplesAreBoundedAnonymousAndResetEvenWhenNotExported()
    {
        using var f = new Fixture();
        var connections = Enumerable.Range(0, 70).Select(_ => f.Add()).ToArray();
        f.Send(connections[0], 2, Sparse(10));
        var zero = f.Snapshot(-1);
        Assert.Empty(zero.GetProperty("Sessions").EnumerateArray());
        var snapshot = f.Snapshot(int.MaxValue);
        var sessions = snapshot.GetProperty("Sessions").EnumerateArray().ToArray();
        Assert.Equal(64, sessions.Length);
        Assert.Equal(64, sessions.Select(s => Value(s, "Session")).Distinct().Count());
        Assert.All(sessions, s => Assert.Equal(0, Value(s.GetProperty("PlayerMovement"), "Received")));
        Assert.Equal(70, Value(snapshot.GetProperty("Population"), "AuthenticatedOpenSessions"));
        string json = snapshot.GetRawText();
        Assert.DoesNotContain("sensitive-account", json);
        Assert.DoesNotContain("sensitive-name", json);
        Assert.DoesNotContain("127.0.0.1", json);
        Assert.DoesNotContain("AccountId", json);
        Assert.DoesNotContain("CharacterGuid", json);
        Assert.DoesNotContain("Ticket", json);
    }

    [Fact]
    public void PeerReplicationCountsRecipientOffersAndMatchesAreNotCountedPerPlayer()
    {
        using var f = new Fixture();
        var first = f.Add(); var second = f.Add(); var third = f.Add();
        foreach (var c in new[] { first, second, third })
        {
            Set(c, "BountyAdmission", new MatchAdmissionContext(777, MatchQueueKind.Public, MatchMode.Solo));
            f.Service.ForTest(c).EnterMatch();
            Invoke(f.Service, "RegisterPeerSession", c, c.Tag!);
        }
        byte[] pose = Sparse(100);
        Get<PeerSession>(second, "Peer").Sink.SendPose(99999, 2, pose, completeSnapshot: true);
        Get<PeerSession>(third, "Peer").Sink.SendPose(99999, 2, pose, completeSnapshot: false);
        var snapshot = f.Snapshot();
        var replication = snapshot.GetProperty("Replication");
        Assert.Equal(2, Value(replication, "PeerPoseOffers"));
        Assert.Equal(1, Value(replication, "CompleteSnapshotOffers"));
        Assert.Equal(1, Value(replication, "OrderedRecordOffers"));
        Assert.Equal(2, Value(replication.GetProperty("PeerPoseEnqueueWork"), "Count"));
        Assert.Equal(1, Value(snapshot.GetProperty("Population"), "AdmittedMatches"));
        Assert.Equal(1, Value(snapshot.GetProperty("Population"), "ActiveMatches"));
        Assert.Equal(2, snapshot.GetProperty("Sessions").EnumerateArray().Sum(s => Value(s, "PeerPoseOffers")));
        // A finished member must not relabel the entire still-active round as ending.
        var phase = first.Tag!.GetType().GetProperty("Match")!;
        phase.SetValue(first.Tag, Enum.Parse(phase.PropertyType, "Ended"));
        var population = f.Snapshot().GetProperty("Population");
        Assert.Equal(1, Value(population, "ActiveMatches"));
        Assert.Equal(0, Value(population, "EndingMatches"));
    }

    [Fact]
    public void DeferredWorkMeasuresQueueWaitSeparatelyAndClosedLinksDoNotRun()
    {
        using var f = new Fixture();
        var c = f.Add();
        var callbacks = new ConcurrentQueue<Action>();
        f.Service.Post = callbacks.Enqueue;
        int calls = 0;
        Assert.True((bool)Invoke(f.Service, "Later", c, 0, (Action)(() => calls++))!);
        Assert.True(SpinWait.SpinUntil(() => !callbacks.IsEmpty, 3000));
        Thread.Sleep(25); // Deliberately hold the owner queue after continuation dispatch.
        Assert.True(callbacks.TryDequeue(out var callback));
        callback!();
        Assert.Equal(1, calls);
        var first = f.Snapshot().GetProperty("Timers");
        Assert.Equal(1, Value(first, "Scheduled"));
        Assert.Equal(1, Value(first, "Executed"));
        Assert.True(first.GetProperty("ContinuationToCallback").GetProperty("MaxMs").GetDouble() >= 10);
        Assert.Equal(1, Value(first.GetProperty("ScheduleToContinuation"), "Count"));

        Assert.True((bool)Invoke(f.Service, "Later", c, 0, (Action)(() => calls++))!);
        Assert.True(SpinWait.SpinUntil(() => !callbacks.IsEmpty, 3000));
        c.Disconnect();
        Assert.True(callbacks.TryDequeue(out callback)); callback!();
        var closed = f.Snapshot().GetProperty("Timers");
        Assert.Equal(1, calls);
        Assert.Equal(1, Value(closed, "ClosedLinkSkipped"));
        Assert.Equal(0, Value(closed, "Executed"));
    }

    [Fact]
    public void DisabledDiagnosticsDoNotAllocateSessionCounters()
    {
        using var f = new Fixture(enabled: false);
        var c = f.Add(); f.Send(c, 2, Sparse(50));
        Assert.False(f.Snapshot().GetProperty("Enabled").GetBoolean());
        Assert.Null(c.Tag!.GetType().GetField("ProductionMetrics")!.GetValue(c.Tag));
        Assert.Equal(50u, Get<SessionMovementState>(c, "Movement").Player!.ClientTime);
    }

    [Fact]
    public void DisconnectedWorkStaysInAggregateButLeavesNoSampledSession()
    {
        using var f = new Fixture();
        var c = f.Add(); f.Send(c, 2, Sparse(50));
        c.Disconnect();
        f.Service.OnDisconnected(c, DisconnectCause.ServerRequested);
        var snapshot = f.Snapshot();
        Assert.Equal(1, Value(snapshot.GetProperty("PlayerMovement").GetProperty("Counts"), "Received"));
        Assert.Equal(0, Value(snapshot.GetProperty("Population"), "AuthenticatedOpenSessions"));
        Assert.Empty(snapshot.GetProperty("Sessions").EnumerateArray());
    }

    private static byte[] Sparse(uint clientTime)
    {
        byte[] packet = new byte[7];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), clientTime);
        return packet;
    }

    private static byte[] Managed(uint transient, byte[] movement)
    {
        using var writer = new PacketWriter();
        new ClientManagedMovementUpdate(transient, ClientMovementUpdate.Parse(movement)).WriteTo(writer);
        return writer.Written.ToArray();
    }

    private static long Value(JsonElement element, string property) => element.GetProperty(property).GetInt64();
    private static void Set(SoeConnection c, string name, object value) => c.Tag!.GetType().GetProperty(name)!.SetValue(c.Tag, value);
    private static T Get<T>(SoeConnection c, string name) => (T)c.Tag!.GetType().GetProperty(name)!.GetValue(c.Tag)!;
    private static object? Invoke(ZoneService service, string name, params object[] arguments) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, arguments);

    private sealed class Fixture(bool enabled = true) : IDisposable
    {
        private readonly List<SoeConnection> _connections = [];
        private readonly SilentLog _log = new();
        public ZoneService Service { get; } = new(new SilentLog(), new Recorder()) { DiagnosticsEnabled = enabled };
        public SoeConnection Add()
        {
            uint index = (uint)_connections.Count + 1;
            var request = new SessionRequest(3, index, 512, ZoneService.ProtocolName);
            var c = new SoeConnection(new(IPAddress.Loopback, 15000 + (int)index), in request,
                new(), SessionDecision.Clear, Service, _log, (_, _) => { }, 0);
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
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> ciphertext) { }
    }
}
