using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// The one producer that is real in the wave-1 scaffold. It parses the client's own channel-2 record
// (FUN_140a3ca40's read order), keeps the bytes verbatim for relay, and gates a pose that no honest
// client could have produced.
public sealed class MovementSystemTests
{
    private static readonly Vector3 First = new(100.5f, 506.25f, -200.25f);

    [Fact]
    public void AClientRecordMovesThePlayer()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        Assert.True(harness.PostMovement(player, First));
        harness.Step();

        Assert.Equal(First, player.Position);
        Assert.Equal(1, harness.Match.Movement.Applied);
        Assert.Equal(0, player.LastPoseTick);
    }

    [Fact]
    public void TheRecordIsKeptVerbatimForRelay()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        byte[] record = MovementRecord.Position(First, clientTime: 0x1234);

        harness.Match.Post(new Command(CommandKind.Movement, player.Slot, EntityId.None, 0, 0, 0), record);
        harness.Step();

        Assert.Equal(record, player.Pose.Span.ToArray());
        Assert.False(player.Pose.IsEmpty);
    }

    [Fact]
    public void PosesArePushedIntoTheRewindHistory()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.PostMovement(player, First);
        harness.Step();
        harness.PostMovement(player, First with { X = First.X + 5f });
        harness.Step();

        Assert.Equal(2, player.History.Count);
        Assert.Equal(First.X + 5f, player.History[0].Position.X);
        Assert.Equal(First.X, player.History[1].Position.X);

        Assert.True(player.History.TryRewind(0, out PoseSnapshot rewound));
        Assert.Equal(First.X, rewound.Position.X);
    }

    [Fact]
    public void TheHistoryRingHoldsOneSecondAndThenOverwrites()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        for (int i = 0; i < PoseHistory.Capacity + 5; i++)
        {
            harness.PostMovement(player, First with { X = First.X + (i * 0.25f) });
            harness.Step();
        }

        Assert.Equal(PoseHistory.Capacity, player.History.Count);
        Assert.Equal(First.X + ((PoseHistory.Capacity + 4) * 0.25f), player.History[0].Position.X);
    }

    [Fact]
    public void ATeleportIsRefusedAndThePreviousPoseIsKept()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.PostMovement(player, First);
        harness.Step();

        harness.PostMovement(player, new Vector3(5_000f, 506.25f, 5_000f));
        harness.Step();

        Assert.Equal(First, player.Position);
        Assert.Equal(1, harness.Match.Movement.Rejected);
        Assert.Equal(1, player.MovementViolations);
    }

    [Fact]
    public void AnHonestStepIsAccepted()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.PostMovement(player, First);
        harness.Step();

        // 5 m in one 50 ms tick is 100 m/s: fast, but under the ceiling.
        Vector3 next = First with { X = First.X + 5f };
        harness.PostMovement(player, next);
        harness.Step();

        Assert.Equal(next, player.Position);
        Assert.Equal(0, harness.Match.Movement.Rejected);
    }

    [Fact]
    public void AMalformedRecordIsCountedAndDropped()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        // Flag bit 0x8000 is outside MovementFieldMask.All; Parse refuses it.
        byte[] record = [0x00, 0x80, 0, 0, 0, 0, 0];
        harness.Match.Post(new Command(CommandKind.Movement, player.Slot, EntityId.None, 0, 0, 0), record);
        harness.Step();

        Assert.Equal(1, harness.Match.Movement.Malformed);
        Assert.Equal(0, harness.Match.Movement.Applied);
        Assert.True(player.Pose.IsEmpty);
    }

    [Fact]
    public void AnOversizedRecordIsRefusedRatherThanTruncated()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        var record = new byte[MovementSample.MaxBytes + 1];
        harness.Match.Post(new Command(CommandKind.Movement, player.Slot, EntityId.None, 0, 0, 0), record);
        harness.Step();

        Assert.Equal(1, harness.Match.Movement.Oversized);
        Assert.Equal(0, harness.Match.Movement.Applied);
    }

    [Fact]
    public void MovingRebucketsThePlayerInTheInterestGrid()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.PostMovement(player, First);
        harness.Step();
        ushort before = player.GridCell;

        harness.MoveTo(player, new Vector3(3_000f, 506f, 3_000f));

        Assert.NotEqual(before, player.GridCell);
        Assert.Equal(InterestGrid.CellOf(player.Position), player.GridCell);
    }

    [Fact]
    public void TheLastRecordOfATickWins()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.PostMovement(player, First);
        harness.PostMovement(player, First with { X = First.X + 1f });
        harness.Step();

        Assert.Equal(First.X + 1f, player.Position.X);
        Assert.Equal(1, harness.Match.Movement.Applied);
    }
}
