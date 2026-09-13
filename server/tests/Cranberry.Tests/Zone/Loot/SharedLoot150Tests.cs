using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.Loot;

public sealed class SharedLoot150Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsoleSpawnsFromDifferentPlayersCannotReuseAClaimedMatchIdentity(bool ring)
    {
        var recorder = new Recorder();
        var service = new ZoneService(new SilentLog(), recorder, new GatewayTicketRegistry(),
            new ZoneOptions { SendProximateItems = false });
        var connections = new List<SoeConnection>();
        for (int n = 0; n < 2; n++)
        {
            var request = new SessionRequest(3, (uint)n + 1, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 21000 + n), in request,
                new(), SessionDecision.Clear, service, new SilentLog(), (_, _) => { }, 0);
            service.OnConnected(connection);
            Set(connection.Tag!, "Guid", (ulong)(n + 1));
            Set(connection.Tag!, "BountyAdmission", new MatchAdmissionContext(1, MatchQueueKind.Public, MatchMode.Solo));
            Get<SessionMovementState>(connection.Tag!, "Movement").ApplyPlayer(ClientMovementUpdate.Parse(
                Cranberry.Tests.Zone.World.MovementRecord.Position(new Vector3(100, 20, 100))));
            Call(service, "JoinSharedLoot", connection, connection.Tag);
            connections.Add(connection);
        }
        GroundLootItem Spawn(SoeConnection connection)
        {
            if (ring) Call(service, "ConsoleSpawnLootRing", connection, connection.Tag);
            else Call(service, "ConsoleSpawnLoot", connection, connection.Tag, 1429u, 30u, false);
            return Get<LootWorld>(connection.Tag!, "Loot").Items.First();
        }
        var first = Spawn(connections[0]);
        Assert.True(Get<MatchLoot>(connections[0].Tag!, "StreamedLoot").TryGetClaimKey(first.WorldGuid, out var firstKey));
        object?[] claim = [connections[0].Tag, first.WorldGuid, null];
        Assert.True((bool)Call(service, "TryClaimSharedLoot", claim)!);
        var second = Spawn(connections[1]);
        Assert.Equal(first.WorldGuid, second.WorldGuid); // deliberately identical viewer-local guid
        Assert.True(Get<MatchLoot>(connections[1].Tag!, "StreamedLoot").TryGetClaimKey(second.WorldGuid, out var secondKey));
        Assert.NotEqual(firstKey, secondKey);
        claim = [connections[1].Tag, second.WorldGuid, null];
        Assert.True((bool)Call(service, "TryClaimSharedLoot", claim)!);
    }
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

    // Exercise the live gateway's ownership operations with its real session objects.
    // Reflection avoids making production mutation hooks public just for a fixture.
    private static object? Call(ZoneService service, string name, params object?[] args) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args);
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OneItemHasOneWinnerAcross150LiveSessionViews(int kind)
    {
        var recorder = new Recorder();
        var service = new ZoneService(new SilentLog(), recorder, new GatewayTicketRegistry(),
            new ZoneOptions { SendProximateItems = false });
        var connections = new List<SoeConnection>();
        var states = new List<object>();
        var guids = new List<ulong>();
        var key = kind switch
        {
            0 => LootStreamKey.ForMarker(42),
            1 => LootStreamKey.ForBox(42),
            2 => LootStreamKey.ForDropped(0x7000_0000_0000_0001),
            _ => LootStreamKey.ForDropped(0x6000_0000_0000_0001),
        };
        for (int n = 0; n < 150; n++)
        {
            var request = new SessionRequest(3, (uint)n + 1, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 10000 + n), in request,
                new(), SessionDecision.Clear, service, new SilentLog(), (_, _) => { }, 0);
            service.OnConnected(connection);
            object state = connection.Tag!;
            Set(state, "BountyAdmission", new MatchAdmissionContext(1, MatchQueueKind.Public, MatchMode.Solo));
            Call(service, "JoinSharedLoot", connection, state);
            var world = Get<LootWorld>(state, "Loot");
            // Deliberately give the same map marker DIFFERENT per-client guids.
            for (int extra = 0; extra < n; extra++) world.Spawn(1, 1, Vector3.One);
            var item = world.Spawn(2423, 1, Vector3.Zero);
            Get<MatchLoot>(state, "StreamedLoot").NoteSpawned(key, item.WorldGuid, item.Position);
            states.Add(state); connections.Add(connection); guids.Add(item.WorldGuid);
        }

        int winners = 0;
        Assert.Equal(150, states.Select(s => Get<LootWorld>(s, "Loot").NextItemGuid()).Distinct().Count());
        for (int n = 0; n < 150; n++)
        {
            object?[] args = [states[n], guids[n], null];
            if ((bool)Call(service, "TryClaimSharedLoot", args)!) winners++;
        }
        Assert.Equal(1, winners);
        for (int n = 0; n < 150; n++)
        {
            Assert.False(Get<LootWorld>(states[n], "Loot").TryGet(guids[n], out _));
            Assert.True(Get<MatchLoot>(states[n], "StreamedLoot").IsTaken(key));
        }
        Assert.Equal(149, recorder.Sent.Count);

        // A spawn already scheduled before the winning pickup cannot recreate it.
        bool spawned = false;
        Call(service, "SpawnSharedLoot", connections[1], states[1], key,
            (Func<GroundLootItem>)(() => { spawned = true; return null!; }));
        Assert.False(spawned);
        int oldGeneration = Get<int>(states[1], "WorldGeneration");
        Set(states[1], "WorldGeneration", oldGeneration + 1);
        var staleBurst = new List<Action> { () => spawned = true };
        Call(service, "DrainBurst", connections[1], states[1], staleBurst, 0, "stale test", false, oldGeneration);
        Assert.False(spawned);

        // Another round has its own ledger; disconnecting a member doesn't reset survivors.
        Call(service, "LeaveSharedLoot", states[0]);
        Assert.True(Get<MatchLoot>(states[1], "StreamedLoot").IsTaken(key));
        Set(states[0], "BountyAdmission", new MatchAdmissionContext(2, MatchQueueKind.Public, MatchMode.Solo));
        Get<MatchLoot>(states[0], "StreamedLoot").Clear();
        Call(service, "JoinSharedLoot", connections[0], states[0]);
        Assert.False(Get<MatchLoot>(states[0], "StreamedLoot").IsTaken(key));
        foreach (var c in connections) { c.Disconnect(); service.OnDisconnected(c, DisconnectCause.ServerRequested); }
    }

    [Fact]
    public void LateViewerCannotRestreamConsumedMarkersOrBoxes()
    {
        var shared = new SharedLootClaims();
        var harness = new LootStreamHarness();
        var initial = harness.Loot.PlanRestream(LootStreamHarness.Landing, harness.Layout, harness.Options);
        Assert.NotEmpty(initial.Spawns);
        foreach (var spawn in initial.Spawns) Assert.True(shared.TryClaim(spawn.Key));
        var late = new MatchLoot { SharedClaims = shared };
        var plan = late.PlanRestream(LootStreamHarness.Landing, harness.Layout, harness.Options);
        Assert.DoesNotContain(plan.Spawns, spawn => shared.IsTaken(spawn.Key));
        foreach (var spawn in initial.Spawns)
            Assert.False(late.NoteSpawned(spawn.Key, 100, spawn.Position));
    }

    [Fact]
    public void SharedDropsUseUniqueKeysAndRemainAvailableAfterViewerEviction()
    {
        var drops = new SharedDroppedLoot();
        // Two clients can mint the same local guid. Their drops must still be different objects.
        var item = new GroundLootItem(100, 1000, 2423, 1, 1, 1, new(-64, 0, -64));
        var a = drops.Add(item);
        var b = drops.Add(item with { ItemDefinitionId = 1429 });
        Assert.NotEqual(a, b);
        Assert.Equal(2, drops.Nearby(new(-65, 0, -65), 4).Count());
        Assert.Empty(drops.Nearby(new(1000, 0, 1000), 60));
        var view = new MatchLoot();
        Assert.True(view.NoteSpawned(a, 300, item.Position));
        Assert.True(view.NoteEvicted(300));
        Assert.Equal(2, drops.Nearby(item.Position, 4).Count());
        drops.Remove(a, item.Position);
        Assert.Equal(b, Assert.Single(drops.Nearby(item.Position, 4)).Key);
        var expiring = drops.Add(item, expiresAtMs: 100);
        Assert.Empty(drops.Expire(99));
        Assert.Equal(expiring, Assert.Single(drops.Expire(100)));
        Assert.Equal(b, Assert.Single(drops.Nearby(item.Position, 4)).Key);
    }

    [Fact]
    public void TakingAGunDoesNotRemoveItsUntouchedAmmoForALateViewer()
    {
        var harness = new LootStreamHarness();
        var first = harness.Loot.PlanRestream(LootStreamHarness.Landing, harness.Layout, harness.Options);
        Span<LootClusterItem> boxes = stackalloc LootClusterItem[2];
        foreach (var spawn in first.Spawns.Where(s => s.Key.Kind == LootStreamKeyKind.Marker))
        {
            if (!harness.Layout.TryGet(spawn.Key.MarkerIndex, out var roll)) continue;
            if (harness.Layout.ClusterFor(roll, boxes) != 2) continue;
            var shared = new SharedLootClaims();
            shared.TryClaim(spawn.Key);
            var late = new MatchLoot { SharedClaims = shared };
            var plan = late.PlanRestream(spawn.Position, harness.Layout, harness.Options);
            Assert.DoesNotContain(plan.Spawns, s => s.Key == spawn.Key);
            var a = LootStreamKey.ForBox(boxes[0]);
            var b = LootStreamKey.ForBox(boxes[1]);
            Assert.Contains(plan.Spawns, s => s.Key == a);
            Assert.Contains(plan.Spawns, s => s.Key == b);
            return;
        }
        Assert.Fail("The real loot fixture must contain a gun and ammo pair.");
    }

    [Fact]
    public void PickupReleasesUnsentReservationWithoutSpawningOrLeakingACapacitySlot()
    {
        var claims = new SharedLootClaims();
        var view = new MatchLoot { SharedClaims = claims };
        var key = LootStreamKey.ForDropped(1);
        Assert.True(view.TryReserveDrop(key, Vector3.Zero, 1));
        Assert.True(claims.TryClaim(key));
        view.NoteTaken(key);
        Assert.Equal(0, view.LiveCount);
        Assert.False(view.NoteSpawned(key, 100, Vector3.Zero));
        Assert.True(view.TryReserveDrop(LootStreamKey.ForDropped(2), Vector3.Zero, 1));
    }
}
