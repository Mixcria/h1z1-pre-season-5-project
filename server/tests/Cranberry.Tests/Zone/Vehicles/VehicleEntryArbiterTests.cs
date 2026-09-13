using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// docs/61 §5 — docs/43 blocker 1, settled the cheap way.
///
/// <para>Nobody has ever pressed E on a parked car, so nobody knows whether the client sends
/// <c>70 01 MountRequest</c> or <c>09 07 InteractRequest</c>. <c>ZoneService</c> answers all three
/// candidate arms, which is right while the answer is unknown but means that a client sending
/// <b>two</b> of them for one press gets two full 239-byte mount bursts. These tests pin the
/// coalescing that makes both arms safe without choosing between them — and the observation that
/// makes the first live press <i>answer</i> the question.</para>
/// </summary>
public sealed class VehicleEntryArbiterTests
{
    private const ulong Car = 0x4242;
    private const ulong OtherCar = 0x4243;

    /// <summary>The client own <c>VehicleInteractionCooldownMs</c>, which is also what
    /// <c>VehicleFleet.TryEnter</c> gates on, so the two windows are the same by construction.</summary>
    private const int Window = 1_000;

    /// <summary>
    /// One whole successful entry, as <c>ZoneService.TryEnterVehicle</c> performs it: classify, and
    /// - only when the fleet then actually seats the character - record the press as answered.
    /// <see cref="VehicleEntryArbiter.Classify"/> deliberately records nothing itself, so that a
    /// press the fleet REFUSES leaves the coalescing memory untouched and can be retried.
    /// </summary>
    private static VehicleEntryVerdict Enter(
        VehicleEntryArbiter arbiter,
        VehicleEntrySource source,
        ulong vehicleGuid,
        bool alreadySeatedHere,
        long nowMs,
        int coalesceWindowMs)
    {
        VehicleEntryVerdict verdict = arbiter.Classify(
            source, vehicleGuid, alreadySeatedHere, nowMs, coalesceWindowMs);
        if (verdict.Proceed)
        {
            arbiter.NoteEntered(source, vehicleGuid, nowMs);
        }

        return verdict;
    }

    /// <summary>
    /// <b>Wave-6 verify fix.</b> A press the fleet refuses (<c>SeatOccupied</c>, <c>Cooldown</c>,
    /// <c>Destroyed</c>) sends nothing, so it must NOT be remembered as this press's answer. It used
    /// to be: <c>Classify</c> stamped every <c>Proceed</c> before <c>VehicleFleet.TryEnter</c> ever
    /// ran, so a player who pressed E on an occupied driver seat and pressed again 400 ms later -
    /// after the passenger got out - was answered with silence for the rest of the second. Wave 5
    /// had no such window at all: <c>VehicleFleet.TryEnter</c> arms its own cooldown only on success.
    /// </summary>
    [Fact]
    public void APressTheFleetRefusesDoesNotSwallowTheRetry()
    {
        var arbiter = new VehicleEntryArbiter();

        // The seat is taken: TryEnter refuses, so NoteEntered is never called.
        Assert.True(arbiter.Classify(VehicleEntrySource.InteractRequest, Car, false, 10_000, Window).Proceed);

        // 400 ms later the seat frees up and the player presses E again. It must reach the fleet.
        VehicleEntryVerdict retry = Enter(
            arbiter, VehicleEntrySource.InteractRequest, Car, false, 10_400, Window);
        Assert.True(retry.Proceed);
        Assert.Equal(0, arbiter.Coalesced);

        // And the successful entry DOES arm the window, so the second arm of that press is a duplicate.
        Assert.False(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 10_402, Window).Proceed);
        Assert.Equal(1, arbiter.Coalesced);
    }

    /// <summary>
    /// <b>The regression this exists for.</b> If one E-press produces both packets, only the first
    /// may be answered.
    /// </summary>
    [Fact]
    public void OnePressThatProducesBothPacketsIsAnsweredExactlyOnce()
    {
        var arbiter = new VehicleEntryArbiter();

        VehicleEntryVerdict first = Enter(arbiter, 
            VehicleEntrySource.InteractRequest, Car, alreadySeatedHere: false, 10_000, Window);
        Assert.True(first.Proceed);

        VehicleEntryVerdict second = Enter(arbiter, 
            VehicleEntrySource.MountRequest, Car, alreadySeatedHere: false, 10_020, Window);
        Assert.False(second.Proceed);
        Assert.Equal(VehicleEntryDecision.DuplicateOtherArm, second.Decision);
        Assert.Equal(VehicleEntrySource.InteractRequest, second.FirstSourceThisPress);
        Assert.Equal(1, arbiter.Coalesced);
    }

    /// <summary>The answer works in whichever order the two arrive; the lane does not pick a winner.</summary>
    [Fact]
    public void EitherArmMayBeTheOneThatAnswersAndTheOtherIsSuppressed()
    {
        var mountFirst = new VehicleEntryArbiter();
        Assert.True(Enter(mountFirst, VehicleEntrySource.MountRequest, Car, false, 0, Window).Proceed);
        Assert.False(Enter(mountFirst, VehicleEntrySource.InteractRequest, Car, false, 30, Window).Proceed);
        Assert.Equal(VehicleEntrySource.MountRequest, mountFirst.FirstObservedSource);

        var interactFirst = new VehicleEntryArbiter();
        Assert.True(Enter(interactFirst, VehicleEntrySource.InteractRequest, Car, false, 0, Window).Proceed);
        Assert.False(Enter(interactFirst, VehicleEntrySource.MountRequest, Car, false, 30, Window).Proceed);
        Assert.Equal(VehicleEntrySource.InteractRequest, interactFirst.FirstObservedSource);
    }

    [Fact]
    public void TheSameArmFiringTwiceIsAlsoCollapsed()
    {
        var arbiter = new VehicleEntryArbiter();
        Assert.True(Enter(arbiter, VehicleEntrySource.PlayerSelect, Car, false, 0, Window).Proceed);

        VehicleEntryVerdict repeat = Enter(arbiter, VehicleEntrySource.PlayerSelect, Car, false, 200, Window);
        Assert.Equal(VehicleEntryDecision.DuplicateSameArm, repeat.Decision);
    }

    /// <summary>
    /// A press on a <i>different</i> car is a different press however fast it follows — otherwise
    /// walking between two parked cars and pressing E twice would silently ignore the second.
    /// </summary>
    [Fact]
    public void ADifferentCarIsAlwaysANewPress()
    {
        var arbiter = new VehicleEntryArbiter();
        Assert.True(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 0, Window).Proceed);
        Assert.True(Enter(arbiter, VehicleEntrySource.MountRequest, OtherCar, false, 5, Window).Proceed);
    }

    [Fact]
    public void APressAfterTheWindowIsANewPress()
    {
        var arbiter = new VehicleEntryArbiter();
        Assert.True(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 0, Window).Proceed);
        Assert.False(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 999, Window).Proceed);
        Assert.True(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 1_000, Window).Proceed);
    }

    /// <summary>
    /// The <see cref="long.MinValue"/> sentinel is compared and never subtracted:
    /// <c>nowMs - long.MinValue</c> overflows and wraps negative, so a subtraction would make the very
    /// first press of a session read as a duplicate of a press that never happened — the same trap
    /// <c>VehicleFleet.IsCoolingDown</c> documents from the other direction.
    /// </summary>
    [Fact]
    public void TheVeryFirstPressOfASessionIsNeverADuplicate()
    {
        var arbiter = new VehicleEntryArbiter();
        Assert.True(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 0, Window).Proceed);

        var atZeroTick = new VehicleEntryArbiter();
        Assert.True(Enter(atZeroTick, VehicleEntrySource.InteractRequest, 0, false, 0, Window).Proceed);
    }

    /// <summary>
    /// Getting straight back into the car you just bailed out of must work. Without the exit reset,
    /// a re-entry inside one second would be swallowed as a duplicate of the entry before the exit.
    /// </summary>
    [Fact]
    public void ReEnteringAfterABailOutIsAFreshPress()
    {
        var arbiter = new VehicleEntryArbiter();
        Assert.True(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 0, Window).Proceed);

        arbiter.NoteExited();
        Assert.True(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 200, Window).Proceed);
    }

    /// <summary>
    /// An attempt from somebody already in that car is not an entry and — deliberately — not an exit
    /// either. The client has <c>70 03</c> and <c>88 18</c> for dismount and <c>ZoneService</c>
    /// already answers both; inventing an E-toggle here would be a guess, which is exactly what this
    /// lane is trying not to do.
    /// </summary>
    [Fact]
    public void AnAttemptFromSomebodyAlreadySeatedIsNeitherAnEntryNorAnExit()
    {
        var arbiter = new VehicleEntryArbiter();
        VehicleEntryVerdict verdict = Enter(arbiter, 
            VehicleEntrySource.InteractRequest, Car, alreadySeatedHere: true, 0, Window);

        Assert.Equal(VehicleEntryDecision.AlreadySeated, verdict.Decision);
        Assert.False(verdict.Proceed);

        // It is still an observation: it tells us which arm the client uses.
        Assert.Equal(VehicleEntrySource.InteractRequest, arbiter.FirstObservedSource);
    }

    /// <summary>
    /// <b>This is the deliverable for docs/43 blocker 1.</b> After one live E-press the host log line
    /// names the arm, so the question is answered with evidence instead of a guess.
    /// </summary>
    [Fact]
    public void TheFirstObservedArmIsWhatAnswersBlockerOne()
    {
        var arbiter = new VehicleEntryArbiter();
        Assert.Null(arbiter.FirstObservedSource);
        Assert.Contains("blocker 1 still open", arbiter.Describe());

        Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 0, Window);
        Enter(arbiter, VehicleEntrySource.InteractRequest, Car, false, 20, Window);

        Assert.Equal(VehicleEntrySource.MountRequest, arbiter.FirstObservedSource);
        Assert.Equal(2, arbiter.ObservedSources.Count);
        string description = arbiter.Describe();
        Assert.Contains("first entry arm was MountRequest", description);
        Assert.Contains("1 duplicate attempt(s) coalesced", description);
    }

    /// <summary>
    /// <b>The double press is not hypothetical — it is already in a host log.</b>
    /// <c>logs/host-20260829-201025.log</c> 20:14:02.179 / 02.181 records the client sending
    /// <c>09 15 Command.PlayerSelect</c> and then <c>09 07 Command.InteractRequest</c> <b>2 ms
    /// apart, carrying the same target guid</b>, for one F press on a world object — and the same
    /// pair again at 03.713/03.714 and at 06.377/06.378. <c>ZoneService</c> calls
    /// <c>TryEnterVehicle</c> from both arms, so pressing E on a car today runs the seat arbitration
    /// twice and logs the second as a refusal. This is the case the arbiter exists for, replayed at
    /// its real timing.
    /// </summary>
    [Fact]
    public void TheObservedTwoMillisecondSelectThenInteractPairIsOneEntry()
    {
        var arbiter = new VehicleEntryArbiter();

        Assert.True(Enter(arbiter, VehicleEntrySource.PlayerSelect, Car, false, 2_179, Window).Proceed);
        VehicleEntryVerdict second = Enter(arbiter, 
            VehicleEntrySource.InteractRequest, Car, false, 2_181, Window);

        Assert.Equal(VehicleEntryDecision.DuplicateOtherArm, second.Decision);
        Assert.Equal(VehicleEntrySource.PlayerSelect, second.FirstSourceThisPress);

        // The next press, 1.5 s later, is a real one (the log's 03.713 pair).
        Assert.True(Enter(arbiter, VehicleEntrySource.PlayerSelect, Car, false, 3_713, Window).Proceed);
        Assert.False(Enter(arbiter, VehicleEntrySource.InteractRequest, Car, false, 3_714, Window).Proceed);
        Assert.Equal(2, arbiter.Coalesced);
    }

    [Fact]
    public void ClearForgetsTheCoalescingWithoutForgettingWhatWasObserved()
    {
        var arbiter = new VehicleEntryArbiter();
        Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 0, Window);
        arbiter.Clear();

        Assert.Equal(VehicleEntrySource.MountRequest, arbiter.FirstObservedSource);
        Assert.True(Enter(arbiter, VehicleEntrySource.MountRequest, Car, false, 10, Window).Proceed);
    }
}
