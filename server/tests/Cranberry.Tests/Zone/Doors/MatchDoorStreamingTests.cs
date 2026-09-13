using System.Numerics;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// docs/47 §5a / §I3 — wave 3 armed the doors exactly once per match, at the parachute landing
/// point, and latched it for the rest of the match. Walk one street and there were no doors left in
/// the world. <see cref="MatchDoors.ShouldRestream"/> replaces that latch.
/// </summary>
public sealed class MatchDoorStreamingTests
{
    private static readonly Lazy<Z2Doors> Dataset = new(Z2Doors.LoadDefault);

    /// <summary>The 22:25 parachute landing point, from <c>logs/host-20260829-220829.log:1099</c>.</summary>
    private static readonly Vector3 Landing = new(-111.91f, 33.40f, 258.82f);

    private static MatchDoors NewMatch(MatchDoorOptions? options = null) =>
        new(Dataset.Value, options);

    [Fact]
    public void TheFirstBurstIsAlwaysDue()
    {
        MatchDoors match = NewMatch();

        Assert.False(match.HasStreamed);
        Assert.True(match.ShouldRestream(Landing, 60f));

        var spawned = new List<DoorInstance>();
        match.RegisterNear(Landing, 60f, 48, spawned);

        Assert.True(match.HasStreamed);
        Assert.Equal(1, match.BurstCount);
        Assert.Equal(Landing, match.LastBurstCentre);
        Assert.NotEmpty(spawned);
    }

    /// <summary>
    /// Standing still — the owner's 22:25 session, where he never went more than 14 m from where he
    /// landed — must not re-query the grid on every movement packet.
    /// </summary>
    [Fact]
    public void StandingStillDoesNotAskForAnotherBurst()
    {
        MatchDoors match = NewMatch();
        match.RegisterNear(Landing, 60f, 48, []);

        Assert.False(match.ShouldRestream(Landing, 60f));
        Assert.False(match.ShouldRestream(Landing + new Vector3(14f, 0f, 0f), 60f));   // his real range
        Assert.False(match.ShouldRestream(Landing + new Vector3(0f, 200f, 0f), 60f));  // height is ignored
    }

    /// <summary>Half the radius — 30 m at the shipped 60 m — is what makes another burst due.</summary>
    [Fact]
    public void MovingHalfTheRadiusAsksForAnotherBurst()
    {
        MatchDoors match = NewMatch();
        match.RegisterNear(Landing, 60f, 48, []);

        Assert.False(match.ShouldRestream(Landing + new Vector3(29f, 0f, 0f), 60f));
        Assert.True(match.ShouldRestream(Landing + new Vector3(31f, 0f, 0f), 60f));
        Assert.True(match.ShouldRestream(Landing + new Vector3(0f, 0f, -31f), 60f));
    }

    /// <summary>
    /// A re-stream must only ever <b>add</b> doors: registration is keyed by dataset index, so a
    /// door already on the client keeps its guid, its transient id and its open bit. Two doors in one
    /// doorway is the failure mode this rules out.
    /// </summary>
    [Fact]
    public void ARestreamAddsDoorsWithoutRespawningTheOnesAlreadyThere()
    {
        MatchDoors match = NewMatch();

        var first = new List<DoorInstance>();
        match.RegisterNear(Landing, 60f, 48, first);
        Assert.NotEmpty(first);

        DoorInstance kept = first[0];
        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(kept.WorldGuid, 0, out DoorInstance? _));
        Assert.True(kept.IsOpen);

        int liveBefore = match.Count;
        var second = new List<DoorInstance>();
        match.RegisterNear(Landing + new Vector3(40f, 0f, 0f), 60f, 48, second);

        Assert.Equal(2, match.BurstCount);
        Assert.DoesNotContain(kept, second);                  // not re-minted
        Assert.True(match.TryGet(kept.WorldGuid, out DoorInstance? still));
        Assert.Same(kept, still);
        Assert.True(still!.IsOpen);                           // and still open
        Assert.Equal(liveBefore + second.Count, match.Count);
    }

    /// <summary>
    /// A non-positive fraction is the explicit "stream once per match" setting — wave 3's behaviour,
    /// kept reachable so a regression can be A/B'd against it.
    /// </summary>
    [Fact]
    public void AZeroFractionRestoresTheStreamOncePerMatchBehaviour()
    {
        MatchDoors match = NewMatch(new MatchDoorOptions { RestreamFraction = 0f });
        match.RegisterNear(Landing, 60f, 48, []);

        Assert.False(match.ShouldRestream(Landing + new Vector3(500f, 0f, 500f), 60f));
    }

    /// <summary>Leaving the world resets the streaming anchor, or the next match would start latched.</summary>
    [Fact]
    public void ClearingTheMatchMakesTheNextBurstDueAgain()
    {
        MatchDoors match = NewMatch();
        match.RegisterNear(Landing, 60f, 48, []);
        Assert.False(match.ShouldRestream(Landing, 60f));

        match.Clear();

        Assert.False(match.HasStreamed);
        Assert.Equal(0, match.BurstCount);
        Assert.Equal(0, match.Count);
        Assert.True(match.ShouldRestream(Landing, 60f));
    }
}
