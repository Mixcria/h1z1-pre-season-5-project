using System.Diagnostics;
using System.Text;
using Cranberry.Harness.Behaviour;

namespace Cranberry.Harness.Scenarios;

/// <summary>How one step of a scenario ended.</summary>
public enum StepOutcome
{
    Passed,
    Failed,
    NotReached,
}

/// <summary>What one step did and how long it took.</summary>
public sealed record StepResult(int Index, string Description, StepOutcome Outcome, TimeSpan At, TimeSpan Took, string? Failure);

/// <summary>The whole run.</summary>
public sealed record ScenarioResult(
    string Name,
    bool Passed,
    IReadOnlyList<StepResult> Steps,
    IReadOnlyList<MilestoneRecord> Milestones,
    string? Failure,
    string JournalTail)
{
    /// <summary>Throws the failure report if the scenario did not pass. Use this from a test.</summary>
    public ScenarioResult ThrowIfFailed()
    {
        if (Passed)
        {
            return this;
        }

        throw new HarnessAssertionException(Report());
    }

    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"scenario '{Name}': {(Passed ? "passed" : "FAILED")}");
        foreach (StepResult step in Steps)
        {
            string mark = step.Outcome switch
            {
                StepOutcome.Passed => "ok  ",
                StepOutcome.Failed => "FAIL",
                _ => "----",
            };
            sb.AppendLine($"  {mark} {step.Index,2}. {step.Description}  ({step.At.TotalSeconds:F3}s, +{step.Took.TotalSeconds:F3}s)");
        }

        if (Failure is not null)
        {
            sb.AppendLine();
            sb.AppendLine(Failure);
        }

        return sb.ToString();
    }
}

/// <summary>
/// A scenario is a sequence of steps and expectations that reads like the docs/71 contract it
/// enforces:
///
/// <code>
/// await Scenario.Named("z2-zoning")
///     .Connect()
///     .Expect("C4")                       // ClientIsReady (Menu) 1.59-1.63 s after 0x57
///     .Expect("D1")                       // the loading screen closes
///     .Do("PLAY", c => c.ClickPlay())
///     .Expect("E2")                       // the client's own 4.542 s retry
///     .Expect("F1")                       // ClientIsReady within 4 s of ClientBeginZoning
///     .Expect("F3")                       // the SECOND channel-2 packet
///     .RunAsync();
/// </code>
///
/// Every step is timed and recorded, and a failure carries the step list plus the last packets
/// both ways with their client names.
/// </summary>
public sealed class Scenario
{
    private readonly List<Step> _steps = [];

    private Scenario(string name) => Name = name;

    public string Name { get; }

    public static Scenario Named(string name) => new(name);

    /// <summary>Login, gateway handoff and RC4 arming, as one step.</summary>
    public Scenario Connect() =>
        Do("connect: login, gateway handoff, RC4 armed", (client, token) => client.ConnectAsync(token));

    /// <summary>
    /// Appends every step of <paramref name="other"/>. A later phase is written as "the earlier
    /// scenario, then …" so the two cannot drift apart: if the drop scenario gains an assertion,
    /// the loot scenario that builds on it gains it too.
    /// </summary>
    public Scenario Then(Scenario other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _steps.AddRange(other._steps);
        return this;
    }

    /// <summary>An action the player or the client takes.</summary>
    public Scenario Do(string description, Func<HarnessClient, CancellationToken, Task> action)
    {
        _steps.Add(new Step(description, action));
        return this;
    }

    public Scenario Do(string description, Action<HarnessClient> action) =>
        Do(description, (client, _) =>
        {
            action(client);
            return Task.CompletedTask;
        });

    /// <summary>Waits for a docs/71 §13 row by id, with that row's budget.</summary>
    public Scenario Expect(string catalogueId, int occurrence = 1)
    {
        MilestoneDefinition definition = MilestoneCatalogue.ById(catalogueId);
        string label = occurrence == 1 ? string.Empty : $" (#{occurrence})";
        return Do($"[{definition.Id}] {definition.Expect}{label}",
            (client, token) => client.ExpectAsync(definition.Milestone, definition.Budget, occurrence, definition.After, token));
    }

    /// <summary>Waits for a milestone with an explicit budget.</summary>
    public Scenario Expect(HarnessMilestone milestone, TimeSpan budget, int occurrence = 1, string? after = null)
    {
        string label = occurrence == 1 ? string.Empty : $" (#{occurrence})";
        return Do($"{milestone}{label} within {budget.TotalSeconds:F2}s",
            (client, token) => client.ExpectAsync(milestone, budget, occurrence, after, token));
    }

    /// <summary>Waits for a milestone that has a catalogued budget.</summary>
    public Scenario Expect(HarnessMilestone milestone, int occurrence = 1) =>
        Expect(milestone, MilestoneCatalogue.For(milestone)?.Budget
            ?? throw new ArgumentException($"{milestone} has no docs/71 §13 budget.", nameof(milestone)), occurrence);

    /// <summary>Client think-time: an observed pause, not a poll.</summary>
    public Scenario Wait(TimeSpan interval) =>
        Do($"wait {interval.TotalSeconds:F2}s", (client, token) => client.Clock.DelayAsync(interval, token));

    /// <summary>A condition the captures say must hold. Fails with the evidence cited.</summary>
    public Scenario Require(string description, Func<HarnessClient, bool> predicate, string evidence) =>
        Do($"require: {description}", (client, _) =>
        {
            if (!predicate(client))
            {
                throw HarnessAssertion.ServerFailed(description, evidence, client.Milestones, client.Journal);
            }

            return Task.CompletedTask;
        });

    /// <summary>The logout burst and the SOE Disconnect (docs/71 §11).</summary>
    public Scenario Quit(uint playSeconds = 192) =>
        Do("quit: WallOfData close burst, PlayLength x2, ClientLogout, Disconnect",
            (client, token) => client.QuitAsync(playSeconds, token));

    /// <summary>Runs against a client the caller owns (so it can inspect it afterwards).</summary>
    public async Task<ScenarioResult> RunAsync(HarnessClient client, CancellationToken cancellationToken = default)
    {
        var results = new List<StepResult>();
        string? failure = null;
        var watch = Stopwatch.StartNew();

        for (int i = 0; i < _steps.Count; i++)
        {
            Step step = _steps[i];
            TimeSpan at = client.Clock.Now;
            TimeSpan before = watch.Elapsed;
            if (failure is not null)
            {
                results.Add(new StepResult(i + 1, step.Description, StepOutcome.NotReached, at, TimeSpan.Zero, null));
                continue;
            }

            try
            {
                await step.Action(client, cancellationToken).ConfigureAwait(false);
                results.Add(new StepResult(i + 1, step.Description, StepOutcome.Passed, at, watch.Elapsed - before, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex.Message;
                results.Add(new StepResult(i + 1, step.Description, StepOutcome.Failed, at, watch.Elapsed - before, ex.Message));
            }
        }

        return new ScenarioResult(
            Name,
            failure is null,
            results,
            client.Milestones.Timeline,
            failure,
            client.Journal.Tail(HarnessAssertion.DefaultJournalTail));
    }

    /// <summary>Runs against a client this call creates and disposes.</summary>
    public async Task<ScenarioResult> RunAsync(HarnessOptions? options = null, CancellationToken cancellationToken = default)
    {
        await using var client = new HarnessClient(options);
        return await RunAsync(client, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Step(string Description, Func<HarnessClient, CancellationToken, Task> Action);
}
