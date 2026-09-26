using System.Collections;
using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

/// <summary>
/// Exercises actual gateway admission and disconnect cleanup with an in-memory transport.
/// Keeping replacement registrations belongs to this server's session ownership contract;
/// these tests do not establish original retail reconnect policy or client presentation.
/// </summary>
public sealed class GatewayReplacementSessionTests
{
    [Fact]
    public void ReplacementAdmissionRetiresTheOldTransportAndOwnsTheCharacterAlone()
    {
        using var world = new Fixture();
        var (old, replacement, _) = world.ReconnectWithObserver();

        Assert.Equal(ConnectionState.Closed, old.State);
        Assert.Equal(ConnectionState.Open, replacement.State);
        Assert.Same(world.Peer(replacement), world.Service.PeerRegistry.Find(Fixture.Character));
        var sessions = world.Field<IDictionary>("_accountSessions");
        Assert.False(sessions.Contains(old));
        Assert.True(sessions.Contains(replacement));
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("guid")]
    [InlineData("protocol")]
    [InlineData("version")]
    public void RejectedReplacementAdmissionLeavesTheCurrentOwnerConnected(string invalid)
    {
        using var world = new Fixture();
        var current = world.Admit(Fixture.Character);
        PeerSession peer = world.Peer(current);

        var rejected = world.AttemptAdmission(Fixture.Character, invalid);

        Assert.Equal(ConnectionState.Open, current.State);
        Assert.Same(peer, world.Service.PeerRegistry.Find(Fixture.Character));
        Assert.False((bool)rejected.Tag!.GetType().GetProperty("Authenticated")!.GetValue(rejected.Tag)!);
        Assert.Single(world.Service.PeerRegistry.Sessions);
    }

    [Fact]
    public void DelayedOldLinkCloseKeepsTheReplacementPeerRegistered()
    {
        using var world = new Fixture();
        var (old, replacement, _) = world.ReconnectWithObserver();

        world.Close(old);

        Assert.Same(world.Peer(replacement), world.Service.PeerRegistry.Find(Fixture.Character));
        Assert.Equal(2, world.Service.PeerRegistry.Count);
        Assert.Equal(ConnectionState.Open, replacement.State);
    }

    [Fact]
    public void DelayedOldLinkCloseDoesNotDespawnTheReplacementFromItsObserver()
    {
        using var world = new Fixture();
        var (old, replacement, observer) = world.ReconnectWithObserver();
        PeerSession viewer = world.Peer(observer);
        Assert.True(viewer.View.Transients.TryGet(new(Fixture.Character), out uint transient));

        world.Close(old);

        Assert.DoesNotContain(world.Sent(observer), packet => packet.SequenceEqual(PeerBurst.Leave(Fixture.Character)));
        Assert.True(viewer.View.Knows(new(Fixture.Character)));
        Assert.True(viewer.View.Transients.TryGet(new(Fixture.Character), out uint retained));
        Assert.Equal(transient, retained);
        var viewers = new List<PeerViewer>();
        world.Service.PeerRegistry.CollectViewers(world.Peer(replacement), viewers);
        Assert.Same(viewer, Assert.Single(viewers).Viewer);
    }

    [Fact]
    public void DelayedOldLinkCloseKeepsTheReplacementAsAThrowableTarget()
    {
        using var world = new Fixture();
        var (old, replacement, _) = world.ReconnectWithObserver();

        world.Close(old);

        var registry = (IDictionary)typeof(ZoneService).GetField("_throwableSessions",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world.Service)!;
        Assert.True(registry.Contains(Fixture.Character));
        object entry = registry[Fixture.Character]!;
        Assert.Same(replacement, entry.GetType().GetField("Item1")!.GetValue(entry));
        Assert.Same(replacement.Tag, entry.GetType().GetField("Item2")!.GetValue(entry));
    }

    [Fact]
    public void ClosingTheCurrentReplacementStillRemovesItsPeerAndTellsTheObserver()
    {
        using var world = new Fixture();
        var (_, replacement, observer) = world.ReconnectWithObserver();

        world.Close(replacement);

        Assert.Null(world.Service.PeerRegistry.Find(Fixture.Character));
        Assert.False(world.Peer(observer).View.Knows(new(Fixture.Character)));
        Assert.Single(world.Sent(observer), packet => packet.SequenceEqual(PeerBurst.Leave(Fixture.Character)));
    }

    [Fact]
    public void DelayedOldLinkCloseKeepsTheReplacementVehicleObserver()
    {
        using var world = new Fixture();
        var (old, replacement, _) = world.ReconnectWithObserver();
        var vehicles = world.Field<VehiclePoseBroadcast>("_vehiclePoses");
        vehicles.Register(new VehicleObserver(replacement));

        world.Close(old);

        Assert.Equal(1, vehicles.ObserverCount);
        world.Close(replacement);
        Assert.Equal(0, vehicles.ObserverCount);
    }

    [Fact]
    public void DelayedOldLinkCloseKeepsThePartyJoinedByTheReplacement()
    {
        using var world = new Fixture();
        var (old, _, _) = world.ReconnectWithObserver();
        PartySnapshot party = world.JoinNewParty();

        world.Close(old);

        Assert.Equal(party.Members, world.Field<PartyRegistry>("_parties").Find(Fixture.Character)!.Members);
    }

    [Fact]
    public void RetiredLinkCannotProcessALatePartyLeaveForTheReplacement()
    {
        using var world = new Fixture();
        var (old, _, _) = world.ReconnectWithObserver();
        PartySnapshot party = world.JoinNewParty();

        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        new PartyPacket(PartyPacket.LeaveSub, Execute: 1, Error: 0, Context: 0).WriteTo(packet);
        world.Service.OnMessage(old, packet.Written.ToArray());

        Assert.Equal(party.Members, world.Field<PartyRegistry>("_parties").Find(Fixture.Character)!.Members);
    }

    [Fact]
    public void RepeatedRetiredCallbacksAcrossThreeAdmissionsDoNotRemoveTheLatestOwner()
    {
        using var world = new Fixture();
        var (first, second, observer) = world.ReconnectWithObserver();
        var third = world.Admit(Fixture.Character);
        world.SeedPeer(third);
        world.Service.PeerRegistry.Sweep(world.Peer(observer), [], []);
        world.ClearSent();

        foreach (var retired in new[] { first, second, first, second })
            world.Service.OnDisconnected(retired, DisconnectCause.Replaced);

        Assert.Equal(ConnectionState.Closed, first.State);
        Assert.Equal(ConnectionState.Closed, second.State);
        Assert.Equal(ConnectionState.Open, third.State);
        Assert.Same(world.Peer(third), world.Service.PeerRegistry.Find(Fixture.Character));
        Assert.True(world.Peer(observer).View.Knows(new(Fixture.Character)));
        Assert.DoesNotContain(world.Sent(observer), packet => packet.SequenceEqual(PeerBurst.Leave(Fixture.Character)));
    }

    [Fact]
    public void ImmediateReplacementKeepsTheLatestWardrobeBeforeTheQueuedDiskWrite()
    {
        using var world = new Fixture(durableWardrobe: true);
        var old = world.Admit(Fixture.Character);
        AugustWardrobeState selection = world.Wardrobe(old);
        AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2158 && entry.AccountItemId != 0);
        Assert.True(selection.TryApply(new SkinItemSelectionRequest(SkinItemSelectionRequest.RequestSetSkinItem,
            Field1: 0, Field2: 0, SlotType: 0, hat.CategoryPrototypeId, hat.AccountItemId),
            out _, out _, out _));
        var store = world.Field<WardrobeStore>("_wardrobeStore");
        var writerGate = (SemaphoreSlim)typeof(WardrobeStore).GetField("_writerGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        // Hold the writer rather than racing its 200 ms coalescing window. Replacement must
        // use the most recent in-memory selection even while no saved file is available.
        writerGate.Wait();
        try
        {
            var replacement = world.Admit(Fixture.Character);
            Assert.Equal(hat.AccountItemId, Assert.Single(world.Wardrobe(replacement).Snapshot()).AccountItemId);
            world.Close(old);
            var cache = world.Field<IDictionary>("_wardrobes");
            Assert.Same(world.Wardrobe(replacement), cache[Fixture.Character]);
            world.Close(replacement);
            Assert.False(cache.Contains(Fixture.Character));
        }
        finally { writerGate.Release(); }
    }

    private sealed class VehicleObserver(SoeConnection connection) : IVehicleObserver
    {
        public ulong CharacterGuid => Fixture.Character;
        public bool IsOpen => connection.State == ConnectionState.Open;
        public Vector3? Position => Vector3.Zero;
        public bool Holds(ulong guid) => true;
        public void Relay(VehiclePoseRelay pose) { }
    }

    private sealed class Fixture : IPacketRecorder, ITransportLog, IDisposable
    {
        public const ulong Character = 0x1001;
        private readonly GatewayTicketRegistry _tickets = new();
        private readonly List<SoeConnection> _connections = [];
        private readonly List<(SoeConnection Connection, byte[] Body)> _sent = [];
        private readonly string? _wardrobeRoot;
        public ZoneService Service { get; }

        public Fixture(bool durableWardrobe = false)
        {
            if (durableWardrobe)
                _wardrobeRoot = Path.Combine(Path.GetTempPath(), "cranberry-replacement-wardrobe", Guid.NewGuid().ToString("N"));
            Service = new(this, this, _tickets, new ZoneOptions
            {
                BootstrapDelayMs = 1000, EnableGas = false, SendDoors = false, SendVehicles = false,
                SendContainers = false, GroundLootRadius = 0, DevGroundLootMs = 0, AutoMatchMs = 0,
                Skins = new SkinOptions { WardrobeStoreRoot = _wardrobeRoot },
            }) { Post = _ => { } };
            // Admission installs the registries before delayed world bootstrap. No timers are
            // pumped, sockets opened or native clients started. The optional wardrobe store
            // uses a unique temporary test directory, never the user's player data.
        }

        public (SoeConnection Old, SoeConnection Replacement, SoeConnection Observer) ReconnectWithObserver()
        {
            var old = Admit(Character);
            var observer = Admit(Character + 1);
            SeedPeer(old);
            SeedPeer(observer);
            Service.PeerRegistry.Sweep(Peer(observer), [], []);
            Assert.True(Peer(observer).View.Knows(new(Character)));

            var replacement = Admit(Character);
            SeedPeer(replacement);
            Service.PeerRegistry.Sweep(Peer(observer), [], []);
            Assert.Same(Peer(replacement), Service.PeerRegistry.Find(Character));
            Assert.True(Peer(observer).View.Knows(new(Character)));
            _sent.Clear();
            return (old, replacement, observer);
        }

        public SoeConnection Admit(ulong character)
        {
            var connection = AttemptAdmission(character);
            Assert.True((bool)connection.Tag!.GetType().GetProperty("Authenticated")!.GetValue(connection.Tag)!);
            Assert.NotNull(Peer(connection));
            return connection;
        }

        public SoeConnection AttemptAdmission(ulong character, string? invalid = null)
        {
            GatewayAdmission admission = _tickets.Issue(invalid == "guid" ? character + 99 : character,
                "Reconnect test", accountId: character.ToString());
            var endpoint = new IPEndPoint(IPAddress.Loopback, 5500 + _connections.Count);
            var request = new SessionRequest(3, (uint)(0x102000 + _connections.Count), 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(endpoint, in request, SessionSettings.WithSeed(1),
                Service.OnSessionRequest(endpoint, in request), Service, this, (_, _) => { }, now: 0);
            _connections.Add(connection);
            Service.OnConnected(connection);
            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode);
            login.WriteUInt64(character);
            login.WriteString(invalid == "ticket" ? "not-issued" : admission.Ticket);
            login.WriteString(invalid == "protocol" ? "not-the-target" : GatewayLoginRequest.AugustProtocol);
            login.WriteString(invalid == "version" ? "not-the-target" : GatewayLoginRequest.AugustVersion);
            Service.OnMessage(connection, login.Written.ToArray());
            return connection;
        }

        public void SeedPeer(SoeConnection connection)
        {
            PeerSession peer = Peer(connection);
            peer.MatchId = 7;
            peer.InMatch = true;
            peer.Position = new Vector3(100, 500, 100);
            peer.SetPose([0, 0, 1, 0, 0, 0, 0]);
        }

        public PeerSession Peer(SoeConnection connection) =>
            (PeerSession)connection.Tag!.GetType().GetProperty("Peer")!.GetValue(connection.Tag)!;

        public AugustWardrobeState Wardrobe(SoeConnection connection) =>
            (AugustWardrobeState)connection.Tag!.GetType().GetProperty("Wardrobe")!.GetValue(connection.Tag)!;

        public T Field<T>(string name) => (T)typeof(ZoneService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!;

        public PartySnapshot JoinNewParty()
        {
            var registry = Field<PartyRegistry>("_parties");
            PartyInvitation invite = Assert.IsType<PartyInvitation>(registry.Invite(Character, Character + 1, 0));
            return Assert.IsType<PartySnapshot>(registry.Respond(invite.Token, Character + 1, true, 1));
        }

        public void ClearSent() => _sent.Clear();

        public byte[][] Sent(SoeConnection connection) => _sent
            .Where(item => ReferenceEquals(item.Connection, connection)).Select(item => item.Body).ToArray();

        public void Close(SoeConnection connection)
        {
            if (!_connections.Remove(connection)) return;
            connection.Disconnect();
            Service.OnDisconnected(connection, DisconnectCause.ServerRequested);
        }

        public void Dispose()
        {
            foreach (SoeConnection connection in _connections.ToArray()) Close(connection);
            Field<WardrobeStore>("_wardrobeStore").Dispose();
            if (_wardrobeRoot is not null && Directory.Exists(_wardrobeRoot))
            {
                string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cranberry-replacement-wardrobe"));
                string target = Path.GetFullPath(_wardrobeRoot);
                if (Path.GetDirectoryName(target) != expectedParent) throw new IOException("Unexpected test cleanup directory.");
                Directory.Delete(target, recursive: true);
            }
        }

        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c" && bytes.Length > 1 && bytes[0] == 0x05)
                _sent.Add((connection, bytes[1..].ToArray()));
        }
    }
}
