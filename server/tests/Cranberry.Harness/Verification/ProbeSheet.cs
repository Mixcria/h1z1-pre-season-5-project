using Cranberry.Harness.Scenarios;

namespace Cranberry.Harness.Verification;

/// <summary>What one probe concluded about one feature.</summary>
public enum ProbeStatus
{
    /// <summary>A client-visible fact the captures or the client's own data say must hold, and it held.</summary>
    Verified,

    /// <summary>It did not hold. This is a finding for the next wave, never something to relax.</summary>
    Failed,

    /// <summary>
    /// The question cannot be answered by a wire-level harness at all — anything purely visual, or
    /// anything that needs a second client, a rebuild, or the owner's own eyes. Saying so is the
    /// point: it tells the owner exactly which items still need a play-test.
    /// </summary>
    NotTestable,

    /// <summary>
    /// The probe ran but the session never reached the state it needed (no firearm in the burst, no
    /// door within reach). Distinguished from <see cref="Failed"/> on purpose: it says nothing
    /// about the server.
    /// </summary>
    Inconclusive,
}

/// <summary>What a probe returns.</summary>
public readonly record struct ProbeOutcome(ProbeStatus Status, string Evidence)
{
    public static ProbeOutcome Pass(string evidence) => new(ProbeStatus.Verified, evidence);

    public static ProbeOutcome Fail(string evidence) => new(ProbeStatus.Failed, evidence);

    public static ProbeOutcome NotTestable(string evidence) => new(ProbeStatus.NotTestable, evidence);

    public static ProbeOutcome Unknown(string evidence) => new(ProbeStatus.Inconclusive, evidence);
}

/// <summary>One row of the verification matrix.</summary>
public sealed record ProbeResult(string Id, string Feature, ProbeStatus Status, string Evidence, string Citation)
{
    public override string ToString() =>
        $"PROBE | {Id,-4} | {Status,-12} | {Feature} | {Evidence} | {Citation}";
}

/// <summary>
/// The matrix a run fills in.
///
/// <para><b>Why probes are not assertions.</b> Lane A's scenarios stop at the first failure, which
/// is right for a contract: a client that never became ready has nothing more to say. This lane is
/// taking a <i>reading</i> of eleven independent features in one session, and a failed door probe
/// must not cost the crafting, inventory and weapon readings behind it. So a probe records its
/// verdict and the scenario carries on — the run ends with a full matrix whatever happened, and a
/// failure is a row rather than a stack trace.</para>
/// </summary>
public sealed class ProbeSheet
{
    private readonly List<ProbeResult> _results = [];

    public IReadOnlyList<ProbeResult> Results
    {
        get { lock (_results) { return [.. _results]; } }
    }

    public void Add(ProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_results)
        {
            _results.Add(result);
        }
    }

    public int Count(ProbeStatus status) => Results.Count(r => r.Status == status);

    public string Report()
    {
        IReadOnlyList<ProbeResult> rows = Results;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"verification matrix: {rows.Count} probe(s) — "
            + $"{Count(ProbeStatus.Verified)} verified, {Count(ProbeStatus.Failed)} FAILED, "
            + $"{Count(ProbeStatus.NotTestable)} not-testable, {Count(ProbeStatus.Inconclusive)} inconclusive");
        foreach (ProbeResult row in rows)
        {
            sb.AppendLine(row.ToString());
        }

        return sb.ToString();
    }
}

/// <summary>The probe step builder.</summary>
public static class ProbeSteps
{
    /// <summary>
    /// Runs <paramref name="check"/> and records its verdict. Never throws: a probe that threw is
    /// recorded as inconclusive with the exception text, because a harness bug must not be reported
    /// as a server failure (and must not cost the rest of the matrix either).
    /// </summary>
    public static Scenario Probe(
        this Scenario scenario,
        ProbeSheet sheet,
        string id,
        string feature,
        string citation,
        Func<HarnessClient, ProbeOutcome> check)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(check);
        return scenario.Do($"probe {id}: {feature}", client =>
        {
            ProbeOutcome outcome;
            try
            {
                outcome = check(client);
            }
            catch (Exception ex)
            {
                outcome = ProbeOutcome.Unknown($"the probe itself threw: {ex.GetType().Name}: {ex.Message}");
            }

            sheet.Add(new ProbeResult(id, feature, outcome.Status, outcome.Evidence, citation));
        });
    }

    /// <summary>An action that must not abort the run either — a press that finds nothing to press.</summary>
    public static Scenario Try(
        this Scenario scenario,
        string description,
        Func<HarnessClient, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(action);
        return scenario.Do($"try: {description}", async (client, token) =>
        {
            try
            {
                await action(client, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                client.Notes.Add($"step '{description}' failed but the run continued: {ex.Message}");
            }
        });
    }
}
