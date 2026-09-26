using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.World;
using Cranberry.Tests.Zone.World;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>Real service fanout through independent encrypted reliable send/receive channels.</summary>
public sealed class BleedingFanoutTests(ITestOutputHelper output)
{
    private const ulong Subject = 0x0123456789abcdef;

    [Fact]
    public void VisibleOpenPeersReceiveExactEffectsWhileVictimResourceAndEffectRemainImmediate()
    {
        using var f = new Fixture();
        var victim = f.Add(Subject);
        var first = f.Add(2);
        var second = f.Add(3);
        var hidden = f.Add(4, position: new(10000, 0, 0));
        var otherMatch = f.Add(5, matchId: 2);
        var closed = f.Add(6);
        f.Sweep();
        Assert.False(hidden.Peer.View.Knows(victim.Peer.Key));
        Assert.False(otherMatch.Peer.View.Knows(victim.Peer.Key));
        Assert.True(closed.Peer.View.Knows(victim.Peer.Key));
        closed.Server.Close(DisconnectCause.PeerRequested); // Still registered/known until owner cleanup.

        f.Wound(victim);

        victim.Receive(flush: false);
        Assert.Equal(3, victim.Messages.Count);
        AssertResource(victim.Messages[0], Subject, current: 1, previous: 0);
        AssertNativeTag(victim.Messages[1], Subject, 120107, 9034);
        Assert.Equal(AddMinor(Subject), victim.Messages[2]);
        foreach (var viewer in new[] { first, second })
        {
            viewer.Receive();
            Assert.Equal(AddMinor(Subject), Assert.Single(viewer.Messages));
            Assert.Equal(0, viewer.Server.PendingDatagrams);
        }
        foreach (var excluded in new[] { hidden, otherMatch, closed })
        {
            excluded.Receive();
            Assert.Empty(excluded.Messages);
            Assert.Empty(excluded.Datagrams);
        }
        Assert.Equal(0, victim.Server.PendingDatagrams);
    }

    [Fact]
    public void SeverityChangeKeepsRemoveBeforeAddAndLaterDespawnAcrossReorderingAndDuplicates()
    {
        using var f = new Fixture();
        var victim = f.Add(Subject);
        var viewer = f.Add(2);
        f.Sweep();
        f.Wound(victim);
        f.Wound(victim); // Severity two retains the minor loop.
        viewer.Receive();
        victim.Receive();
        viewer.Messages.Clear();
        victim.Messages.Clear();

        ulong oldTag = Get<ulong>(victim.Server.Tag!, "BleedHudInstance");
        f.Wound(victim); // Severity three replaces the loop.
        victim.Receive(flush: false);
        Assert.Equal(5, victim.Messages.Count);
        AssertResource(victim.Messages[0], Subject, current: 3, previous: 2);
        AssertNativeRemove(victim.Messages[1], Subject, oldTag);
        AssertNativeTag(victim.Messages[2], Subject, 120112, 14116);
        Assert.Equal(RemoveMinor(Subject), victim.Messages[3]);
        Assert.Equal(AddModerate(Subject), victim.Messages[4]);
        viewer.Server.FlushBufferedOutput(Environment.TickCount64);
        Call(f.Service, "PeerLinkClosed", victim.Server, victim.Server.Tag);
        Assert.False(viewer.Peer.View.Knows(victim.Peer.Key));

        // The client receives the newest datagram first, twice, before the missing predecessor.
        // Its real SOE receiver must recover order and consume each RC4 range only once.
        viewer.Receive(reverse: true, duplicate: true);
        Assert.Equal(3, viewer.Messages.Count);
        Assert.Equal(RemoveMinor(Subject), viewer.Messages[0]);
        Assert.Equal(AddModerate(Subject), viewer.Messages[1]);
        Assert.Equal(RemovePlayer(Subject), viewer.Messages[2]);
        Assert.Equal(0, viewer.Server.PendingDatagrams);
        Assert.Equal(ConnectionState.Open, viewer.Client.State);
    }

    [Fact]
    public void SeverityWithinAParticleBandUpdatesVictimResourceAndNativeTagAndMaxSeverityAddsNothing()
    {
        using var f = new Fixture();
        var victim = f.Add(Subject);
        var viewer = f.Add(2);
        f.Sweep();
        f.Wound(victim);
        ulong oldTag = Get<ulong>(victim.Server.Tag!, "BleedHudInstance");
        victim.Receive();
        viewer.Receive();
        victim.Messages.Clear();
        viewer.Messages.Clear();

        f.Wound(victim);
        victim.Receive(flush: false);
        viewer.Receive();
        Assert.Equal(3, victim.Messages.Count);
        AssertResource(victim.Messages[0], Subject, current: 2, previous: 1);
        AssertNativeRemove(victim.Messages[1], Subject, oldTag);
        AssertNativeTag(victim.Messages[2], Subject, 120111, 1110);
        Assert.Empty(viewer.Messages);

        f.Wound(victim); // Moderate loop.
        f.Wound(victim); // Same moderate loop.
        f.Wound(victim); // Severe loop.
        victim.Receive();
        viewer.Receive();
        Assert.Equal(new[] { 0x16, 0x15, 0x16, 0x15 }, viewer.Messages.Select(p => (int)p[2]));
        Assert.Equal(new[] { 5106u, 5042u, 5042u, 5105u }, viewer.Messages.Select(p => U32(p, 11)));
        victim.Messages.Clear();
        viewer.Messages.Clear();
        f.Wound(victim); // Already at the maximum: no repeated resource or effect publication.
        victim.Receive();
        viewer.Receive();
        Assert.Empty(victim.Messages);
        Assert.Empty(viewer.Messages);
        Assert.Equal((byte)5, Medical(victim).Bleed);
    }

    [Fact]
    public void ResetRemovesQueuedEffectBeforeANewWoundAndClearsItsDamageAttribution()
    {
        using var f = new Fixture();
        var victim = f.Add(Subject);
        var viewer = f.Add(2);
        f.Sweep();
        f.Wound(victim);
        int generation = Get<int>(victim.Server.Tag!, "BleedGeneration");
        Call(f.Service, "ResetPlayerVitals", victim.Server, victim.Server.Tag);
        Assert.True(Get<int>(victim.Server.Tag!, "BleedGeneration") > generation);
        Assert.Equal((byte)0, Medical(victim).Bleed);
        Assert.False(Medical(victim).Bleeding);
        Assert.Equal(0u, Get<uint>(victim.Server.Tag!, "BleedEffect"));
        Assert.Equal(0UL, Get<ulong>(victim.Server.Tag!, "WoundAttacker"));
        Assert.Null(Get<string?>(victim.Server.Tag!, "WoundAttackerName"));
        Assert.Equal(0u, Get<uint>(victim.Server.Tag!, "WoundWeapon"));
        Assert.Equal(10000u, f.Service.ForTest(victim.Server).Hitpoints);
        viewer.Server.FlushBufferedOutput(Environment.TickCount64);
        f.Wound(victim);
        viewer.Receive(reverse: true, duplicate: true);
        Assert.Equal(3, viewer.Messages.Count);
        Assert.Equal(AddMinor(Subject), viewer.Messages[0]);
        Assert.Equal(RemoveMinor(Subject), viewer.Messages[1]);
        Assert.Equal(AddMinor(Subject), viewer.Messages[2]);
        Assert.Equal((byte)1, Medical(victim).Bleed);
        Assert.Equal(0, viewer.Server.PendingDatagrams);
    }

    [Fact]
    public void OwnerFlushBundlesSeverityReplacementIntoOneReliableDatagramWithTheSameWireMessages()
    {
        using var f = new Fixture();
        var victim = f.Add(Subject);
        var viewer = f.Add(2);
        f.Sweep();
        f.Wound(victim);
        f.Wound(victim);
        victim.Receive();
        viewer.Receive();
        viewer.Messages.Clear();
        int mark = viewer.Datagrams.Count;

        f.Wound(victim);
        viewer.Receive(); // Explicit end-of-owner-pass flush, followed by genuine transport ACK.

        Assert.Equal(2, viewer.Messages.Count);
        Assert.Equal(RemoveMinor(Subject), viewer.Messages[0]);
        Assert.Equal(AddModerate(Subject), viewer.Messages[1]);
        byte[] packet = Assert.Single(viewer.Datagrams.Skip(mark));
        Assert.Equal(new byte[] { 0, 9 }, packet.Take(2));
        Assert.Equal(new byte[] { 0, 0x19 }, packet.Skip(4).Take(2));
        Assert.Equal(0, viewer.Server.PendingDatagrams);
    }

    [Fact]
    public void Dense175PlayerWoundBurstRetainsEveryEffectAndUsesFewerThan50DatagramsPerViewer()
    {
        using var f = new Fixture();
        var players = Enumerable.Range(1, 175).Select(guid => f.Add((ulong)guid)).ToArray();
        for (int pass = 0; pass < players.Length && players.Any(p => p.Peer.View.KnownCount != 174); pass++)
            f.Sweep();
        Assert.All(players, player => Assert.Equal(174, player.Peer.View.KnownCount));

        // All wounds arrive in one owner work pass, as in the synchronized combat load case.
        foreach (var player in players) f.Wound(player);
        long pendingBeforeAck = players.Sum(player => (long)player.Server.PendingDatagrams);
        foreach (var viewer in players)
        {
            viewer.Receive();
            Assert.Equal(177, viewer.Messages.Count); // 174 remote particles, own resource, native HUD tag, particle.
            int message = 0;
            foreach (var subject in players)
            {
                ulong guid = subject.Peer.CharacterGuid;
                if (ReferenceEquals(subject, viewer))
                {
                    AssertResource(viewer.Messages[message++], guid, current: 1, previous: 0);
                    AssertNativeTag(viewer.Messages[message++], guid, 120107, 9034);
                }
                Assert.Equal(AddMinor(guid), viewer.Messages[message++]);
            }
            Assert.Equal(0, viewer.Server.PendingDatagrams);
            Assert.Equal(ConnectionState.Open, viewer.Server.State);
            Assert.Equal(ConnectionState.Open, viewer.Client.State);
        }

        int datagrams = players.Sum(player => player.Datagrams.Count);
        int minimum = players.Min(player => player.Datagrams.Count);
        int maximum = players.Max(player => player.Datagrams.Count);
        output.WriteLine($"Dense175 encrypted wound fanout: sessions=175; messages={players.Sum(p => p.Messages.Count)}; "
            + $"datagrams={datagrams}; minPerViewer={minimum}; maxPerViewer={maximum}; "
            + $"pendingBeforeAck={pendingBeforeAck}; pendingAfterAck={players.Sum(p => p.Server.PendingDatagrams)}.");
        Assert.InRange(maximum, 1, 49);
    }

    // These expected packets come from the native wire layout, not the production writers.
    private static byte[] AddMinor(ulong guid) => Effect(guid, 0x15, 5106);
    private static byte[] AddModerate(ulong guid) => Effect(guid, 0x15, 5042);
    private static byte[] RemoveMinor(ulong guid) => Effect(guid, 0x16, 5106);

    private static void AssertNativeTag(byte[] packet, ulong subject, uint effect, uint name)
    {
        Assert.Equal(116, packet.Length);
        Assert.Equal(new byte[] { 5, 0x9e, 6 }, packet.Take(3));
        Assert.Equal(subject, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(3)));
        Assert.NotEqual(0ul, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(11)));
        Assert.Equal(effect, U32(packet, 23));
        Assert.Equal(uint.MaxValue, U32(packet, 31)); // no client ability stage
        Assert.Equal(name, U32(packet, 35));
    }

    private static void AssertNativeRemove(byte[] packet, ulong subject, ulong instance)
    {
        Assert.Equal(19, packet.Length);
        Assert.Equal(new byte[] { 5, 0x9e, 8 }, packet.Take(3));
        Assert.Equal(subject, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(3)));
        Assert.Equal(instance, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(11)));
    }

    private static byte[] Effect(ulong guid, byte kind, uint effect)
    {
        var packet = new byte[kind == 0x15 ? 39 : 19];
        packet[0] = 5;
        packet[1] = 0x0f;
        packet[2] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(3), guid);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11), effect);
        if (kind == 0x15)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(15), effect);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(35), effect);
        }
        return packet;
    }

    private static byte[] RemovePlayer(ulong guid)
    {
        var packet = new byte[13];
        packet[0] = 5;
        packet[1] = 0x0f;
        packet[2] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(3), guid);
        return packet;
    }

    private static void AssertResource(byte[] packet, ulong guid, uint current, uint previous)
    {
        Assert.Equal(102, packet.Length);
        Assert.Equal(new byte[] { 5, 0x8d, 0, 0, 0, 0, 3 }, packet.Take(7));
        Assert.Equal(guid, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(7)));
        Assert.Equal(21u, U32(packet, 15));
        Assert.Equal(21u, U32(packet, 19));
        Assert.Equal(current, U32(packet, 23));
        Assert.Equal(previous, U32(packet, 27));
        Assert.All(packet.Skip(31), value => Assert.Equal(0, value));
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);
    private static MedicalState Medical(Link link) => (MedicalState)link.Server.Tag!.GetType().GetField("PlayerMedical")!.GetValue(link.Server.Tag)!;
    private static object? Call(ZoneService service, string name, params object?[] args) =>
        typeof(ZoneService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(service, args);

    private sealed class Fixture : IDisposable
    {
        public ZoneService Service { get; } = new(new SilentLog(), new Recorder(), new GatewayTicketRegistry());
        private readonly List<Link> _links = [];
        public Fixture() => Service.Post = _ => { }; // Only this test owns service state; delayed timers never run.

        public Link Add(ulong guid, ulong matchId = 1, Vector3 position = default)
        {
            var link = new Link(Service, _links.Count + 1);
            _links.Add(link);
            Service.OnConnected(link.Server);
            Set(link.Server.Tag!, "Guid", guid);
            Set(link.Server.Tag!, "CharacterName", $"BleedTest{guid}");
            Service.ForTest(link.Server).EnterMatch();
            Call(Service, "RegisterPeerSession", link.Server, link.Server.Tag);
            link.Peer = Get<PeerSession>(link.Server.Tag!, "Peer");
            link.Peer.MatchId = matchId;
            link.Peer.InMatch = true;
            link.Peer.Position = position;
            link.Peer.SetPose(MovementRecord.Position(position));
            return link;
        }

        public void Sweep()
        {
            var enters = new List<PeerEnter>();
            var leaves = new List<PeerLeave>();
            foreach (var link in _links) Service.PeerRegistry.Sweep(link.Peer, enters, leaves);
        }

        public void Wound(Link victim)
        {
            MedicalState medical = Medical(victim);
            medical.LastWoundAtMs = Environment.TickCount64 - MedicalModel.BleedIncrementCooldownMs;
            victim.Server.Tag!.GetType().GetField("PlayerMedical")!.SetValue(victim.Server.Tag, medical);
            Call(Service, "WoundPlayer", victim.Server, victim.Server.Tag, 900UL, "Attacker", 10000u, 6u);
        }

        public void Dispose()
        {
            foreach (var link in _links)
            {
                link.Server.Close(DisconnectCause.ServerRequested);
                link.Client.Close(DisconnectCause.ServerRequested);
            }
            foreach (var link in _links) Service.OnDisconnected(link.Server, DisconnectCause.ServerRequested);
        }
    }

    private sealed class Link
    {
        private readonly Receiver _receiver = new();
        private readonly List<byte[]> _signals = [];
        private int _received;
        public SoeConnection Server { get; }
        public SoeConnection Client { get; }
        public PeerSession Peer { get; set; } = null!;
        public List<byte[]> Datagrams { get; } = [];
        public List<byte[]> Messages => _receiver.Messages;

        public Link(ZoneService service, int index)
        {
            byte[] key = [1, 9, (byte)index, 3, 0x71, 0x2c]; // Distinct stream per viewer.
            var request = new SessionRequest(3, (uint)index, 512, ZoneService.ProtocolName);
            Server = new(new(IPAddress.Loopback, 21000 + index), in request, new(),
                SessionDecision.Encrypted(key), service, new SilentLog(),
                (_, bytes) => Datagrams.Add(bytes.ToArray()), Environment.TickCount64);
            Client = new(new(IPAddress.Loopback, 22000 + index), in request, new(),
                SessionDecision.Encrypted(key), _receiver, new SilentLog(),
                (_, bytes) => _signals.Add(bytes.ToArray()), Environment.TickCount64);
        }

        public void Receive(bool flush = true, bool reverse = false, bool duplicate = false)
        {
            if (flush) Server.FlushBufferedOutput(Environment.TickCount64);
            while (_received < Datagrams.Count)
            {
                var pending = Datagrams.Skip(_received).ToArray();
                _received = Datagrams.Count;
                if (reverse) Array.Reverse(pending);
                foreach (byte[] datagram in pending)
                {
                    Client.HandleDatagram(datagram.ToArray(), Environment.TickCount64, flushSignals: false);
                    if (duplicate) Client.HandleDatagram(datagram.ToArray(), Environment.TickCount64, flushSignals: false);
                }
                Client.FlushSignals();
                foreach (byte[] signal in _signals)
                    Server.HandleDatagram(signal.ToArray(), Environment.TickCount64, flushSignals: false);
                _signals.Clear();
            }
        }
    }

    private sealed class Receiver : ISoeService
    {
        public List<byte[]> Messages { get; } = [];
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) => Messages.Add(message.ToArray());
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private sealed class Recorder : IPacketRecorder
    {
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }
}
