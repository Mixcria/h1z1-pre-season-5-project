using Cranberry.Harness.Behaviour;

namespace Cranberry.Harness.Scenarios;

/// <summary>
/// Extra step builders for scenarios whose acceptance is not a single milestone deadline.
///
/// <para>They are extension methods rather than members of <see cref="Scenario"/> so the build
/// lane's runner and its tests stay exactly as they were: everything here is composed out of the
/// public <c>Do</c> the fluent API already offers.</para>
/// </summary>
public static class ScenarioSteps
{
    /// <summary>
    /// A condition the captures require that the server may take a moment to satisfy. Polls until
    /// the budget expires and then fails with the evidence and the journal tail, exactly as
    /// <see cref="Scenario.Require"/> does.
    /// </summary>
    public static Scenario RequireEventually(
        this Scenario scenario,
        string description,
        Func<HarnessClient, bool> predicate,
        TimeSpan budget,
        string evidence)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(predicate);
        return scenario.Do(
            $"require within {budget.TotalSeconds:F1}s: {description}",
            async (client, token) =>
            {
                TimeSpan deadline = client.Clock.Now + client.Clock.Scaled(budget);
                while (true)
                {
                    if (predicate(client))
                    {
                        return;
                    }

                    if (client.Clock.Now >= deadline)
                    {
                        throw HarnessAssertion.ServerFailed(
                            $"{description} — still not true {budget.TotalSeconds:F1} s later",
                            evidence, client.Milestones, client.Journal);
                    }

                    await client.Clock.DelayAsync(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
                }
            });
    }

    /// <summary>
    /// A measured check: the delegate returns <c>null</c> when the condition holds, or the reason it
    /// does not — including the number that was measured. A failure that says "the new loot is
    /// 41.2 m from the landing loot, not the 200 m the scenario demands" is worth ten that say false.
    /// </summary>
    public static Scenario Measure(
        this Scenario scenario,
        string description,
        Func<HarnessClient, string?> check,
        string evidence)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(check);
        return scenario.Do($"measure: {description}", (client, _) =>
        {
            string? failure = check(client);
            if (failure is not null)
            {
                throw HarnessAssertion.ServerFailed(
                    $"{description} — {failure}", evidence, client.Milestones, client.Journal);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>Takes a reading into <see cref="HarnessClient.Notes"/> without asserting anything.</summary>
    public static Scenario Read(this Scenario scenario, string description, Func<HarnessClient, string> line)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(line);
        return scenario.Do($"read: {description}", (client, _) =>
        {
            client.Notes.Add($"{description}: {line(client)}");
            return Task.CompletedTask;
        });
    }
}
