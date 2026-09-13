using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Scenarios;
using Xunit.Abstractions;

namespace Cranberry.Harness.Tests;

/// <summary>
/// A fact that only runs when a host is up. The default suite must stay deterministic and
/// offline, so these are skipped unless <c>CRANBERRY_HARNESS_LIVE=1</c> is set.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CRANBERRY_HARNESS_LIVE") != "1")
        {
            Skip = "set CRANBERRY_HARNESS_LIVE=1 (and run a host on 127.0.0.1:20042) to run the live scenarios";
        }
    }
}

/// <summary>
/// The scenarios run against a real host. These are the readings the harness exists to take; run
/// them with:
/// <code>
/// set CRANBERRY_HARNESS_LIVE=1
/// dotnet test tests\Cranberry.Harness.Tests --filter LiveHostTests
/// </code>
/// </summary>
public sealed class LiveHostTests(ITestOutputHelper output)
{
    private HarnessOptions Options => new() { Log = output.WriteLine };

    [LiveFact]
    public async Task Login_and_the_gateway_handoff_complete()
    {
        ScenarioResult result = await StandardScenarios.Login().RunAsync(Options);
        output.WriteLine(result.Report());
        result.ThrowIfFailed();
    }

    [LiveFact]
    public async Task The_menu_bootstrap_reaches_the_loading_screen_close()
    {
        ScenarioResult result = await StandardScenarios.Menu().RunAsync(Options);
        output.WriteLine(result.Report());
        result.ThrowIfFailed();
    }

    [LiveFact]
    public async Task The_client_reaches_Z2_and_sends_ClientIsReady_within_four_seconds()
    {
        ScenarioResult result = await StandardScenarios.ZoneToZ2().RunAsync(Options);
        output.WriteLine(result.Report());
        result.ThrowIfFailed();
    }

    [LiveFact]
    public async Task The_drop_reaches_the_AutoMount_echo()
    {
        ScenarioResult result = await StandardScenarios.Drop().RunAsync(Options);
        output.WriteLine(result.Report());
        result.ThrowIfFailed();
    }

    [LiveFact]
    public async Task The_free_running_timers_keep_being_answered()
    {
        ScenarioResult result = await StandardScenarios.SteadyState().RunAsync(Options);
        output.WriteLine(result.Report());
        result.ThrowIfFailed();
    }

    [LiveFact]
    public async Task A_client_that_stops_acknowledging_is_dropped_after_about_eight_seconds()
    {
        // docs/71 §12.3 / K3: this server gives up after MaxResends 25 x ResendIntervalMs 300.
        // The number is the server's patience, not the client's — no capture shows the client's own.
        await using var client = new HarnessClient(Options);
        await client.ConnectAsync();
        await client.ExpectAsync(HarnessMilestone.MenuClientIsReadySent);

        client.GatewayLink!.AckingEnabled = false;
        TimeSpan startedAt = client.Clock.Now;

        while (client.Clock.Now - startedAt < TimeSpan.FromSeconds(20)
            && client.GatewayLink.CloseCause == Cranberry.Harness.Soe.LinkCloseCause.None)
        {
            await Task.Delay(100);
        }

        output.WriteLine($"link ended as {client.GatewayLink.CloseCause} after "
            + $"{(client.Clock.Now - startedAt).TotalSeconds:F2}s: {client.GatewayLink.Fault}");
        output.WriteLine(client.Journal.Tail());
    }
}
