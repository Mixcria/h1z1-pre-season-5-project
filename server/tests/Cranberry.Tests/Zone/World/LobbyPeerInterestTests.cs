using System.Numerics;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

public sealed class LobbyPeerInterestTests
{
    private sealed class Sink : IPeerSink
    {
        public bool IsOpen { get; set; } = true;
        public List<byte[]> Sent { get; } = [];
        public void Send(byte[] packet) => Sent.Add(packet);
    }

    private static PeerSession Join(SessionRegistry registry, ulong guid, float distance, ulong match = 1)
    {
        PeerSession peer = registry.Register(guid, new Sink());
        peer.MatchId = match;
        peer.InMatch = true;
        peer.ModelId = 9469;
        peer.Position = new Vector3(distance, 0, 0);
        peer.SetPose(MovementRecord.Position(peer.Position));
        return peer;
    }

    private static (List<PeerEnter> Enters, List<PeerLeave> Leaves) Sweep(SessionRegistry registry,
        PeerSession viewer, LobbyInterestSettings? settings = null)
    {
        List<PeerEnter> enters = []; List<PeerLeave> leaves = [];
        registry.Sweep(viewer, enters, leaves, settings ?? LobbyInterestSettings.Default);
        return (enters, leaves);
    }

    [Fact]
    public void LiveInterestHandlerUsesConfiguredLobbyPolicyAndRestoresMatchVisibility()
    {
        var recorder = new NullRecorder();
        var service = new ZoneService(recorder, recorder, new GatewayTicketRegistry(), new ZoneOptions
        { Peers = PeerOptions.Default with { LobbyInterest = new(2, 30) } });
        var viewer = Join(service.PeerRegistry, 1, 0);
        Join(service.PeerRegistry, 2, 10);
        Join(service.PeerRegistry, 3, 20);
        Join(service.PeerRegistry, 4, 25);
        Join(service.PeerRegistry, 5, 90);
        Type stateType = typeof(ZoneService).GetNestedType("GatewaySessionState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(stateType, nonPublic: true)!;
        stateType.GetProperty("Guid")!.SetValue(state, viewer.CharacterGuid);
        stateType.GetProperty("Peer")!.SetValue(state, viewer);
        var match = stateType.GetProperty("Match")!;
        void Run(string phase)
        {
            match.SetValue(state, Enum.Parse(match.PropertyType, phase));
            stateType.GetProperty("NextPeerInterestMs")!.SetValue(state, 0L);
            typeof(ZoneService).GetMethod("RunPeerInterest", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, state, viewer]);
        }
        Run("Lobby");
        Assert.Equal(2, viewer.View.KnownCount);
        Assert.Equal(2, ((Sink)viewer.Sink).Sent.Count(p => p[0] == 0xd5));
        Assert.False(viewer.View.Knows(new EntityId(4)));
        Run("InMatch");
        Assert.Equal(4, viewer.View.KnownCount);
        Assert.True(viewer.View.Knows(new EntityId(5)));
        Run("Lobby");
        Assert.Equal(2, viewer.View.KnownCount);
        Assert.Equal(2, ((Sink)viewer.Sink).Sent.Count(p => p[0] == 0x0f && p[1] == 1));
    }

    private sealed class NullRecorder : ITransportLog, IPacketRecorder
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
    }

    [Fact]
    public void TwoHundredLobbyPlayersHaveBoundedViewsAndOnlyKnownPeersReceiveRelays()
    {
        var registry = new SessionRegistry();
        PeerSession[] players = Enumerable.Range(1, 200).Select(i => Join(registry, (ulong)i, i * .2f)).ToArray();
        foreach (PeerSession viewer in players)
        {
            Assert.Equal(8, Sweep(registry, viewer).Enters.Count);
            Assert.Equal(8, Sweep(registry, viewer).Enters.Count);
            Assert.Empty(Sweep(registry, viewer).Enters);
            Assert.Equal(16, viewer.View.KnownCount);
            Assert.False(viewer.View.Knows(viewer.Key));
        }
        Assert.Equal(3200, players.Sum(p => p.View.KnownCount));
        List<PeerViewer> viewers = [];
        int edges = 0;
        foreach (PeerSession subject in players)
        {
            registry.CollectViewers(subject, viewers);
            Assert.All(viewers, v => Assert.True(v.Viewer.View.Knows(subject.Key)));
            edges += viewers.Count;
        }
        Assert.Equal(3200, edges);
    }

    [Fact]
    public void SelectionUsesDistanceAndReleasesTheOldReverseViewBeforeReplacement()
    {
        var registry = new SessionRegistry();
        PeerSession viewer = Join(registry, 1, 0);
        PeerSession far = Join(registry, 2, 20);
        var settings = new LobbyInterestSettings(1);
        Assert.Same(far, Assert.Single(Sweep(registry, viewer, settings).Enters).Subject);
        PeerSession near = Join(registry, 3, 19);
        Assert.Empty(Sweep(registry, viewer, settings).Enters); // one metre does not churn
        near.Position = new(16, 0, 0);
        var change = Sweep(registry, viewer, settings);
        Assert.Equal(far.CharacterGuid, Assert.Single(change.Leaves).CharacterGuid);
        Assert.Same(near, Assert.Single(change.Enters).Subject);
        List<PeerViewer> viewers = [];
        registry.CollectViewers(far, viewers); Assert.Empty(viewers);
        registry.CollectViewers(near, viewers); Assert.Same(viewer, Assert.Single(viewers).Viewer);
    }

    [Fact]
    public void RadiusHasAnExitMarginAndAFormerPeerMustReenterTheInnerBand()
    {
        var registry = new SessionRegistry();
        PeerSession viewer = Join(registry, 1, 0);
        PeerSession peer = Join(registry, 2, 59);
        Assert.Single(Sweep(registry, viewer).Enters);
        peer.Position = new(65, 0, 0);
        Assert.Empty(Sweep(registry, viewer).Leaves);
        peer.Position = new(67, 0, 0);
        Assert.Single(Sweep(registry, viewer).Leaves);
        peer.Position = new(61, 0, 0);
        Assert.Empty(Sweep(registry, viewer).Enters);
        peer.Position = new(59, 0, 0);
        Assert.Single(Sweep(registry, viewer).Enters);
    }

    [Fact]
    public void MatchSweepRestoresTwoKilometreVisibilityAndDoesNotKeepTheLobbyCap()
    {
        var registry = new SessionRegistry();
        PeerSession viewer = Join(registry, 1, 0);
        for (ulong i = 2; i <= 40; i++) Join(registry, i, i * 30);
        Sweep(registry, viewer);
        Assert.Equal(1, viewer.View.KnownCount);
        for (int i = 0; i < 8; i++) registry.Sweep(viewer, [], []);
        Assert.Equal(39, viewer.View.KnownCount);
        for (int i = 0; i < 8; i++) Sweep(registry, viewer);
        Assert.Equal(1, viewer.View.KnownCount);
        for (int i = 0; i < 8; i++) Sweep(registry, viewer, new LobbyInterestSettings(0));
        Assert.Equal(39, viewer.View.KnownCount);
    }

    [Fact]
    public void ClosedMenuForeignMatchAndInvalidPosesCannotConsumeSlots()
    {
        var registry = new SessionRegistry();
        PeerSession viewer = Join(registry, 1, 0);
        Join(registry, 2, 1, match: 2);
        Join(registry, 3, 1).InMatch = false;
        ((Sink)Join(registry, 4, 1).Sink).IsOpen = false;
        Join(registry, 5, float.NaN);
        var valid = Join(registry, 6, 30);
        Assert.Same(valid, Assert.Single(Sweep(registry, viewer).Enters).Subject);
        viewer.InMatch = false;
        Assert.Single(Sweep(registry, viewer).Leaves);
        Assert.Equal(0, viewer.View.KnownCount);
    }

    [Fact]
    public void EqualDistancesHaveAStableIdentityTieBreak()
    {
        var registry = new SessionRegistry();
        PeerSession viewer = Join(registry, 1, 0);
        Join(registry, 99, 10);
        var selected = Join(registry, 2, 10);
        Assert.Same(selected, Assert.Single(Sweep(registry, viewer, new(1)).Enters).Subject);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("99999")]
    public void InvalidLobbySettingsFallBackToTheReferencePolicy(string invalid)
    {
        PeerOptions options = PeerOptions.FromEnvironment(_ => invalid);
        Assert.Equal(16, options.LobbyInterest.MaxPlayers);
        Assert.Equal(60f, options.LobbyInterest.RadiusMetres);
    }
}
