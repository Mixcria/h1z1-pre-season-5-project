using Cranberry.Harness.Scenarios;
using Xunit.Abstractions;

namespace Cranberry.Harness.Tests;

/// <summary>
/// Scenario lane A against a running host. Opt-in, like the rest of the live suite:
/// <code>
/// set CRANBERRY_HARNESS_LIVE=1
/// dotnet test tests\Cranberry.Harness.Tests --filter LaneAScenarioTests
/// </code>
///
/// <para>Each test prints the step list, every reading the scenario took, and — on a failure — the
/// milestone timeline and the last packets both ways. A failure here is the finding; it is never
/// something to relax the budget for without a capture that justifies it.</para>
/// </summary>
public sealed class LaneAScenarioTests(ITestOutputHelper output)
{
    private HarnessOptions Options => new() { Log = output.WriteLine, JournalCapacity = 200 };

    [LiveFact]
    public Task S1_login_character_list_character_login_and_the_gateway_handoff() =>
        RunAsync(LaneAScenarios.S1Login());

    [LiveFact]
    public Task S2_zone_bootstrap_menu_ready_finished_loading_and_the_menu_answered() =>
        RunAsync(LaneAScenarios.S2Menu());

    [LiveFact]
    public Task S3_play_queue_transfer_reply_zoning_and_ClientIsReady_for_Z2() =>
        RunAsync(LaneAScenarios.S3ZoneToZ2());

    [LiveFact]
    public Task S4_start_match_air_spawn_parachute_mount_descent_and_landing() =>
        RunAsync(LaneAScenarios.S4Drop());

    [LiveFact]
    public Task S5_ground_loot_full_character_data_and_a_pickup() =>
        RunAsync(LaneAScenarios.S5Loot());

    [LiveFact]
    public Task S6_loot_streaming_after_a_two_hundred_metre_move() =>
        RunAsync(LaneAScenarios.S6LootStreaming());

    [LiveFact]
    public Task S7_the_regression_guards_as_live_assertions() =>
        RunAsync(LaneAScenarios.S7RegressionGuards());

    [LiveFact]
    public Task S8_a_silent_client_is_not_dropped_out_of_an_aeroplane() =>
        RunAsync(LaneAScenarios.S8SilentClientIsNotTalkedAt());

    /// <summary>
    /// docs/98, lane 0B. Needs the host started with <c>CRANBERRY_WIELD_FIRST_PICKUP=1</c> (which is
    /// what <c>live-run.ps1</c> does), or the pickup is stowed and nothing is ever drawn. Written
    /// before the draw was finished, and expected to fail until it is: the point of it is that
    /// "wield works" has an oracle that is not a person saying so.
    /// </summary>
    [LiveFact]
    public Task S9_the_draw_definitions_the_slot_seven_binding_the_stance_and_the_trailer() =>
        RunAsync(LaneAScenarios.S9WieldDraw());

    /// <summary>
    /// docs/97, lane 1D-lite. Needs the host started with <c>CRANBERRY_GAS_PRESET=Sprint</c> — on
    /// the retail ladder the death is twenty minutes away and the scenario's own budget fails it
    /// honestly rather than hanging. Skipped like the rest without <c>CRANBERRY_HARNESS_LIVE=1</c>.
    /// </summary>
    [LiveFact]
    public Task S10_standing_in_the_gas_kills_the_player_with_a_ragdoll_a_cause_and_a_count() =>
        RunAsync(LaneAScenarios.S10DeathAndVictory());

    private async Task RunAsync(Scenario scenario)
    {
        await using var client = new HarnessClient(Options);
        ScenarioResult result = await scenario.RunAsync(client);
        output.WriteLine(result.Report());
        foreach (string note in client.Notes)
        {
            output.WriteLine($"  reading | {note}");
        }

        result.ThrowIfFailed();
    }
}
