using Cranberry.Harness.Scenarios;
using Cranberry.Harness.Verification;
using Xunit.Abstractions;

namespace Cranberry.Harness.Tests;

/// <summary>
/// The run lane: one live reading of every feature waves 3–6 shipped without a client ever seeing
/// it. Opt-in like the rest of the live suite:
/// <code>
/// set CRANBERRY_HARNESS_LIVE=1
/// dotnet test tests\Cranberry.Harness.Tests --filter VerificationMatrixTests
/// </code>
///
/// <para>Unlike Lane A's scenarios these do not stop at the first failure. Each probe records
/// VERIFIED / FAILED / NOT-TESTABLE / INCONCLUSIVE and the run carries on, so one session yields a
/// whole matrix — a failed door probe must not cost the crafting, inventory and weapon readings
/// behind it. The test itself fails only if the session could not be driven at all.</para>
/// </summary>
public sealed class VerificationMatrixTests(ITestOutputHelper output)
{
    private HarnessOptions Options => new() { Log = output.WriteLine, JournalCapacity = 300 };

    [LiveFact(Timeout = 900_000)]
    public Task V1_in_match_sweep() => RunAsync(VerificationScenarios.V1InMatchSweep);

    [LiveFact(Timeout = 900_000)]
    public Task V2_gas_pacing() => RunAsync(VerificationScenarios.V2GasPacing);

    [LiveFact(Timeout = 900_000)]
    public Task V3_second_match() => RunAsync(VerificationScenarios.V3SecondMatch);

    /// <summary>
    /// docs/103 §5.6: the developer console's server half. It needs a bigger journal than the other
    /// three - the name burst alone is fifty packets - and it proves only what the host SENT: the
    /// pane is probe C4, and it is NOT-TESTABLE from a wire harness by construction.
    /// </summary>
    [LiveFact(Timeout = 900_000)]
    public Task V4_developer_console() =>
        RunAsync(
            ConsoleProbeScenario.V4ConsoleProbe,
            Options with { JournalCapacity = ConsoleProbeScenario.RecommendedJournalCapacity });

    private Task RunAsync(Func<ProbeSheet, Scenario> build) => RunAsync(build, Options);

    private async Task RunAsync(Func<ProbeSheet, Scenario> build, HarnessOptions options)
    {
        var sheet = new ProbeSheet();
        Scenario scenario = build(sheet);
        await using var client = new HarnessClient(options);
        ScenarioResult result = await scenario.RunAsync(client);

        output.WriteLine(result.Report());
        foreach (string note in client.Notes)
        {
            output.WriteLine($"  reading | {note}");
        }

        output.WriteLine(sheet.Report());

        // A probe verdict is the deliverable, so a FAILED probe does not fail the test — it is a row
        // in the matrix. What fails the test is the session itself not being drivable, because then
        // every row below the break is meaningless.
        result.ThrowIfFailed();
    }
}
