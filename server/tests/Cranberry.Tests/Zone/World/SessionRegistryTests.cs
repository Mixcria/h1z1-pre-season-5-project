using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

/// <summary>
/// docs/109 lane 3C: the enter/leave contract two sessions on one host meet, with no host, no
/// client and no socket. The spatial rules are <c>InterestSystem</c>'s (310 m enter, x1.2 leave,
/// the self-guard, the per-viewer budgets and the release-after-despawn ordering) and these tests
/// pin them on the registry that now applies them.
/// </summary>
public sealed class SessionRegistryTests
{
    private static readonly Vector3 Origin = new(-233.83f, 506.36f, -4892.03f);

    /// <summary>A sink that keeps every packet instead of sending it.</summary>
    private sealed class FakeSink : IPeerSink
    {
        public bool IsOpen { get; set; } = true;

        public List<byte[]> Sent { get; } = [];

        public void Send(byte[] zonePacket) => Sent.Add(zonePacket);
    }

    private static PeerSession Join(
        SessionRegistry registry,
        ulong guid,
        Vector3 position,
        string name = "Player",
        bool inMatch = true)
    {
        PeerSession session = registry.Register(guid, new FakeSink());
        session.CharacterName = name;
        session.ModelId = 9469;
        session.InMatch = inMatch;
        session.SetPose(MovementRecord.Position(position));
        session.Position = position;
        return session;
    }

    private static (List<PeerEnter> Enters, List<PeerLeave> Leaves) Sweep(
        SessionRegistry registry,
        PeerSession viewer)
    {
        List<PeerEnter> enters = [];
        List<PeerLeave> leaves = [];
        registry.Sweep(viewer, enters, leaves);
        return (enters, leaves);
    }

    [Fact]
    public void AnotherPlayerInsideTheEnterRadiusIsSpawnedWithAMintedTransientId()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        PeerSession bob = Join(registry, 0x1002, Origin with { X = Origin.X + 300f }, "Bob");

        (List<PeerEnter> enters, List<PeerLeave> leaves) = Sweep(registry, alice);

        PeerEnter entered = Assert.Single(enters);
        Assert.Empty(leaves);
        Assert.Same(bob, entered.Subject);

        // TransientIdTable reserves 0-15 and allocates from 16, so a peer can never be handed the
        // viewer's own id (docs/100 §8) - the second half of the self-guard.
        Assert.Equal(TransientIdTable.FirstAllocated, entered.TransientId);
        Assert.NotEqual(TransientIdTable.LocalPlayer, entered.TransientId);
        Assert.True(alice.View.Knows(bob.Key));
    }

    [Fact]
    public void NothingIsSpawnedBeyondTheTwoKilometreVisibilityBand()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        _ = Join(registry, 0x1002, Origin with { X = Origin.X + 2300f }, "Bob");

        (List<PeerEnter> enters, List<PeerLeave> leaves) = Sweep(registry, alice);

        // 2300 m is outside both the entry radius and its hysteresis margin.
        Assert.Empty(enters);
        Assert.Empty(leaves);
        Assert.Equal(0, alice.View.KnownCount);
        Assert.True(ObserverView.PlayerLeaveMetres < 2300f);
    }

    [Fact]
    public void AViewerNeverSeesItself()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");

        (List<PeerEnter> enters, _) = Sweep(registry, alice);

        Assert.Empty(enters);
        Assert.Equal(0, alice.View.KnownCount);
    }

    [Fact]
    public void TheHysteresisBandKeepsAPeerBetweenTheTwoRadii()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        PeerSession bob = Join(registry, 0x1002, Origin with { X = Origin.X + 1900f }, "Bob");

        Assert.Single(Sweep(registry, alice).Enters);

        // 2100 m: outside the 2000 m enter radius, inside the 2200 m leave radius. An entity sitting
        // on one boundary would otherwise be spawned and despawned every tick, and each churn cycle
        // costs a 125-byte minimum d5 and a 12-byte 0f 01.
        bob.Position = Origin with { X = Origin.X + 2100f };
        (List<PeerEnter> enters, List<PeerLeave> leaves) = Sweep(registry, alice);

        Assert.Empty(enters);
        Assert.Empty(leaves);
        Assert.True(alice.View.Knows(bob.Key));
    }

    [Fact]
    public void LeavingTheOuterRadiusReportsTheIdBeforeItIsReleased()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        PeerSession bob = Join(registry, 0x1002, Origin with { X = Origin.X + 300f }, "Bob");

        uint given = Assert.Single(Sweep(registry, alice).Enters).TransientId;

        bob.Position = Origin with { X = Origin.X + 2300f };
        (List<PeerEnter> enters, List<PeerLeave> leaves) = Sweep(registry, alice);

        Assert.Empty(enters);
        PeerLeave left = Assert.Single(leaves);
        Assert.Equal(bob.CharacterGuid, left.CharacterGuid);

        // The despawn names the id the client still has bound - reading it after the release would
        // report 0, and the caller writes 0f 01 from this list.
        Assert.Equal(given, left.TransientId);
        Assert.False(alice.View.Knows(bob.Key));

        // Released, and therefore reusable: the next character to enter gets the same id back off
        // the free list, which is what keeps the varint one byte over a long match.
        PeerSession carol = Join(registry, 0x1003, Origin with { X = Origin.X + 5f }, "Carol");
        Assert.Equal(given, Assert.Single(Sweep(registry, alice).Enters).TransientId);
        Assert.True(alice.View.Knows(carol.Key));
    }

    [Fact]
    public void AMenuSessionIsNeitherShownAnybodyNorShownToAnybody()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice", inMatch: false);
        PeerSession bob = Join(registry, 0x1002, Origin with { X = Origin.X + 10f }, "Bob");

        Assert.Empty(Sweep(registry, alice).Enters);      // no world to draw a peer in
        Assert.Empty(Sweep(registry, bob).Enters);        // and nothing to draw

        alice.InMatch = true;
        Assert.Single(Sweep(registry, alice).Enters);
        Assert.Single(Sweep(registry, bob).Enters);
    }

    [Fact]
    public void ACharacterThatHasNotReportedAPositionIsNotSpawnedAtTheOrigin()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        PeerSession mute = registry.Register(0x1002, new FakeSink());
        mute.InMatch = true;

        // Position defaults to (0,0,0), which is 4.9 km from the staging spawn - but the gate is
        // HasPose, not the distance, or a peer would be spawned at the map origin and then teleport
        // in on its first relay.
        Assert.False(mute.HasPose);
        Assert.Empty(Sweep(registry, alice).Enters);

        mute.SetPose(MovementRecord.Position(Origin));
        mute.Position = Origin;
        Assert.Single(Sweep(registry, alice).Enters);
    }

    [Fact]
    public void TheSpawnBudgetDefersTheRestToTheNextPassRatherThanDroppingThem()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        for (ulong i = 0; i < 12; i++)
        {
            Join(registry, 0x2000 + i, Origin with { X = Origin.X + 5f + i }, $"P{i}");
        }

        int budget = alice.View.SpawnBudgetPerTick;
        Assert.Equal(budget, Sweep(registry, alice).Enters.Count);
        Assert.True(registry.SpawnBudgetStops > 0);

        Assert.Equal(12 - budget, Sweep(registry, alice).Enters.Count);
        Assert.Equal(12, alice.View.KnownCount);
    }

    [Fact]
    public void ReconnectingOnTheSameCharacterReplacesTheSessionRatherThanAddingASecond()
    {
        var registry = new SessionRegistry();
        _ = Join(registry, 0x1001, Origin, "Alice");
        PeerSession again = Join(registry, 0x1001, Origin, "Alice");

        Assert.Equal(1, registry.Count);
        Assert.Same(again, registry.Find(0x1001));
    }

    [Fact]
    public void CollectViewersReportsEverybodyHoldingTheSubjectAndNeverTheSubject()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        PeerSession bob = Join(registry, 0x1002, Origin with { X = Origin.X + 10f }, "Bob");
        _ = Join(registry, 0x1003, Origin with { X = Origin.X + 900f }, "Far");

        Sweep(registry, alice);
        Sweep(registry, bob);

        List<PeerViewer> viewers = [];
        registry.CollectViewers(bob, viewers);

        PeerViewer only = Assert.Single(viewers);
        Assert.Same(alice, only.Viewer);
        Assert.True(alice.View.Transients.TryGet(bob.Key, out uint expected));
        Assert.Equal(expected, only.TransientId);
    }

    [Fact]
    public void ForgetEverywhereReportsEveryViewerSoTheLinkCloseCanDespawnBeforeItReleases()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        PeerSession bob = Join(registry, 0x1002, Origin with { X = Origin.X + 10f }, "Bob");
        Sweep(registry, alice);
        Assert.True(alice.View.Transients.TryGet(bob.Key, out uint held));

        List<PeerViewer> owed = [];
        registry.ForgetEverywhere(bob.CharacterGuid, owed);

        PeerViewer told = Assert.Single(owed);
        Assert.Same(alice, told.Viewer);
        Assert.Equal(held, told.TransientId);
        Assert.False(alice.View.Knows(bob.Key));
    }

    [Fact]
    public void AClosedLinkIsSweptAndStopsBeingACandidate()
    {
        var registry = new SessionRegistry();
        PeerSession alice = Join(registry, 0x1001, Origin, "Alice");
        PeerSession bob = Join(registry, 0x1002, Origin with { X = Origin.X + 10f }, "Bob");
        ((FakeSink)bob.Sink).IsOpen = false;

        registry.SweepClosed();

        Assert.Equal(1, registry.Count);
        Assert.Null(registry.Find(bob.CharacterGuid));
        Assert.Empty(Sweep(registry, alice).Enters);
    }

    [Fact]
    public void TheClientsExactBytesAreKeptForRelayAndAreNeverReEncoded()
    {
        var registry = new SessionRegistry();
        PeerSession bob = Join(registry, 0x1002, Origin, "Bob");
        byte[] record = MovementRecord.Position(Origin with { X = 1.005f });

        bob.SetPose(record);
        Assert.Equal(record, bob.Pose.ToArray());

        // A shorter record after a longer one must not leave the tail of the old one behind: the
        // buffer is reused and only grows, so the length has to be tracked separately from it.
        byte[] shorter = record[..(record.Length - 3)];
        bob.SetPose(shorter);
        Assert.Equal(shorter, bob.Pose.ToArray());
    }

    [Fact]
    public void ASessionWithoutAGuidCannotBeRegistered() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PeerSession(0, new FakeSink()));

    [Fact]
    public void SpectatorInterestMovesIndependentlyOfItsReplicatedBodyAndStaysInItsMatch()
    {
        var registry = new SessionRegistry();
        var corpse = Join(registry, 0x1001, Origin, "Eliminated host");
        var nearbyBody = Join(registry, 0x1002, Origin + new Vector3(10, 0, 0), "Body viewer");
        var remote = Join(registry, 0x1003, Origin + new Vector3(5000, 0, 0), "Spectated player");
        var otherMatch = Join(registry, 0x1004, remote.Position, "Other match");
        corpse.MatchId = nearbyBody.MatchId = remote.MatchId = 1;
        otherMatch.MatchId = 2;
        byte[] bodyPose = corpse.Pose.ToArray();
        corpse.ObserverPosition = remote.Position;

        Assert.Same(remote, Assert.Single(Sweep(registry, corpse).Enters).Subject);
        Assert.Same(corpse, Assert.Single(Sweep(registry, nearbyBody).Enters).Subject);
        Assert.Equal(Origin, corpse.Position);
        Assert.Equal(bodyPose, corpse.Pose.ToArray());

        corpse.ObserverPosition = Origin;
        Assert.Contains(Sweep(registry, corpse).Leaves, left => left.CharacterGuid == remote.CharacterGuid);
        corpse.ResetWorldPose();
        Assert.Null(corpse.ObserverPosition);
    }
}
