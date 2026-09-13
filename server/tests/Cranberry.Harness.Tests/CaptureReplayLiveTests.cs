using Cranberry.Harness.Replay;
using Xunit.Abstractions;

namespace Cranberry.Harness.Tests;

/// <summary>
/// Replays a recorded session against a running host. Opt-in, like the rest of the live suite:
/// <code>
/// set CRANBERRY_HARNESS_LIVE=1
/// dotnet test tests\Cranberry.Harness.Tests --filter CaptureReplayLiveTests --logger "console;verbosity=detailed"
/// </code>
///
/// <para><b>What these assert, and what they only report.</b> A difference between the recording
/// and today's server is not automatically a fault — waves 3 to 6 changed the server on purpose —
/// so the tests fail on exactly two things: a packet shape a capture proves the client cannot
/// survive (<see cref="RegressionGuards"/>), and the disappearance of a bootstrap step the client
/// cannot proceed without (<see cref="CriticalOpcodes"/>). Everything else — new opcode families,
/// changed payload sizes, unmet causal edges — is printed for a human to classify, which is the
/// whole job the tool exists to make cheap.</para>
/// </summary>
public sealed class CaptureReplayLiveTests(ITestOutputHelper output)
{
    private const string KnownGood = "wire-20260829-184346.txt";
    private const string KnownGoodPreFix = "wire-20260829-150206.txt";
    private const string KnownBadG10 = "wire-20260829-173352.txt";

    private ReplayOptions Options => new()
    {
        Log = output.WriteLine,
        StopAfter = TimeSpan.FromSeconds(60),
        MaxMovementPackets = 400,
    };

    [LiveFact]
    public async Task The_docs32_fix_session_replays_without_a_client_fatal_packet()
    {
        ReplayResult result = await CaptureReplay.ReplayAsync(CaptureLocator.TryFind(KnownGood)!, Options);
        output.WriteLine(result.Report(journalTail: 12));

        Assert.DoesNotContain(result.LiveGuards, g => g.Severity == GuardSeverity.Fatal);
        Assert.Empty(result.MissingCritical);
    }

    /// <summary>
    /// The pre-regression session, replayed only as far as its menu bootstrap.
    ///
    /// <para><b>Its zoning half is not replayable and never will be.</b> In that build the server
    /// pushed <c>ClientBeginZoning</c> on its own timer at +35.4 s and the client's single
    /// <c>PlayerWorldTransferRequest</c> came <i>afterwards</i>, at +42.3 s. Today's server — after
    /// the docs/32 fix — answers the client's own retrying request pair with a queue
    /// (<c>LoginBase</c> QueueTick/QueueDone), then <c>PlayerWorldTransferReply</c>, then zoning.
    /// The recording's causal edges past +30 s therefore describe a server that no longer exists,
    /// and replaying them proves nothing. wire-20260829-184346 is the baseline for the zoning path;
    /// this one is still good evidence for the bootstrap that precedes it.</para>
    /// </summary>
    [LiveFact]
    public async Task The_pre_regression_menu_bootstrap_still_replays_unchanged()
    {
        ReplayResult result = await CaptureReplay.ReplayAsync(
            CaptureLocator.TryFind(KnownGoodPreFix)!,
            Options with { StopAfter = TimeSpan.FromSeconds(30) });
        output.WriteLine(result.Report());

        Assert.DoesNotContain(result.LiveGuards, g => g.Severity == GuardSeverity.Fatal);
        Assert.Empty(result.MissingCritical);
    }

    /// <summary>
    /// The control. Replaying the client half of a session that ended in G10 must <b>not</b>
    /// reproduce G10, because the fault was the server's: the recording's own guards fire and the
    /// live run's must not. A replay that came back dirty here would mean the docs/32 fix is not
    /// in this build.
    /// </summary>
    [LiveFact]
    public async Task Replaying_the_G10_session_no_longer_produces_the_G10_packets()
    {
        ReplayResult result = await CaptureReplay.ReplayAsync(CaptureLocator.TryFind(KnownBadG10)!, Options);
        output.WriteLine(result.Report());

        Assert.Contains(result.RecordedGuards, g => g.Id == "G1" && g.Severity == GuardSeverity.Fatal);
        Assert.DoesNotContain(result.LiveGuards, g => g.Severity == GuardSeverity.Fatal);
        Assert.Empty(result.NewFatalFindings);
    }
}
