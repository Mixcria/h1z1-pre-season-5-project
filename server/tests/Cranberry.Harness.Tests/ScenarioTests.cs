using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Scenarios;

namespace Cranberry.Harness.Tests;

/// <summary>
/// The runner itself, with no server. Steps are driven by noting milestones directly, so these
/// tests are about ordering, reporting and failure propagation rather than about the protocol.
/// </summary>
public sealed class ScenarioTests
{
    private static HarnessOptions Fast => new() { TimeScale = 0.001 };

    [Fact]
    public async Task Steps_run_in_order_and_are_all_recorded()
    {
        await using var client = new HarnessClient(Fast);
        var order = new List<string>();

        ScenarioResult result = await Scenario.Named("ordering")
            .Do("first", _ => order.Add("first"))
            .Do("second", _ => order.Add("second"))
            .Do("third", _ => order.Add("third"))
            .RunAsync(client);

        Assert.True(result.Passed);
        Assert.Equal(["first", "second", "third"], order);
        Assert.Equal(3, result.Steps.Count);
        Assert.All(result.Steps, step => Assert.Equal(StepOutcome.Passed, step.Outcome));
        Assert.Contains("passed", result.Report(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expectation_passes_when_the_client_reaches_the_milestone()
    {
        await using var client = new HarnessClient(Fast);

        ScenarioResult result = await Scenario.Named("reaches-it")
            .Do("the client becomes ready", c => c.Milestones.Note(HarnessMilestone.ZoningClientIsReadySent))
            .Expect("F1")
            .RunAsync(client);

        Assert.True(result.Passed);
        Assert.Contains(result.Steps, s => s.Description.StartsWith("[F1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missed_expectation_fails_the_scenario_and_leaves_the_rest_unreached()
    {
        await using var client = new HarnessClient(Fast);
        client.Milestones.Note(HarnessMilestone.ZoningBegun);

        ScenarioResult result = await Scenario.Named("z2-hang")
            .Expect("F1")
            .Do("this should never run", _ => throw new InvalidOperationException("ran anyway"))
            .RunAsync(client);

        Assert.False(result.Passed);
        Assert.Equal(StepOutcome.Failed, result.Steps[0].Outcome);
        Assert.Equal(StepOutcome.NotReached, result.Steps[1].Outcome);

        string report = result.Report();
        Assert.Contains("FAILED", report, StringComparison.Ordinal);
        Assert.Contains("[F1]", report, StringComparison.Ordinal);
        Assert.Contains("ClientBeginZoning", report, StringComparison.Ordinal);
        Assert.Contains("40 of 40 successful zonings", report, StringComparison.Ordinal);

        HarnessAssertionException thrown = Assert.Throws<HarnessAssertionException>(() => result.ThrowIfFailed());
        Assert.Contains("z2-hang", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_condition_fails_with_its_evidence_cited()
    {
        await using var client = new HarnessClient(Fast);

        ScenarioResult result = await Scenario.Named("world-type")
            .Require("SendZoneDetails carried world type 4", _ => false, "docs/06/07")
            .RunAsync(client);

        Assert.False(result.Passed);
        Assert.Contains("world type 4", result.Failure!, StringComparison.Ordinal);
        Assert.Contains("docs/06/07", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Occurrence_aware_expectations_distinguish_the_menu_ready_from_the_zoning_one()
    {
        await using var client = new HarnessClient(Fast);

        ScenarioResult first = await Scenario.Named("second-ready")
            .Do("menu ready", c => c.Milestones.Note(HarnessMilestone.MenuClientIsReadySent))
            .Expect(HarnessMilestone.MenuClientIsReadySent, TimeSpan.FromMilliseconds(50), occurrence: 2)
            .RunAsync(client);

        Assert.False(first.Passed);

        client.Milestones.Note(HarnessMilestone.MenuClientIsReadySent);
        ScenarioResult second = await Scenario.Named("second-ready-again")
            .Expect(HarnessMilestone.MenuClientIsReadySent, TimeSpan.FromMilliseconds(50), occurrence: 2)
            .RunAsync(client);

        Assert.True(second.Passed);
    }

    [Fact]
    public void Every_standard_scenario_has_a_name_and_can_be_built()
    {
        Scenario[] scenarios =
        [
            StandardScenarios.Login(),
            StandardScenarios.Menu(),
            StandardScenarios.ZoneToZ2(),
            StandardScenarios.Drop(),
            StandardScenarios.SteadyState(),
            StandardScenarios.Logout(),
        ];

        Assert.Equal(scenarios.Length, scenarios.Select(s => s.Name).Distinct().Count());
        Assert.All(scenarios, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
    }

    [Fact]
    public async Task Expecting_a_milestone_with_no_catalogued_budget_is_a_programming_error()
    {
        await using var client = new HarnessClient(Fast);
        Assert.Throws<ArgumentException>(() => Scenario.Named("x").Expect(HarnessMilestone.LinkClosed));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExpectAsync(HarnessMilestone.LinkClosed));
    }
}
