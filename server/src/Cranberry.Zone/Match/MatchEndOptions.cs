namespace Cranberry.Zone.Match;

/// <summary>
/// The switches over the death-and-victory orchestration (lane 1D-lite). Everything the lane can
/// put on the wire that the server did not send before is behind one of these, so a play-test that
/// goes wrong has a one-word revert and the boot banner says which arms ran.
///
/// <para>
/// <b>D151 / D152.</b> <see cref="LeaveOnEnd"/> is off because <c>ce 1b</c> logs the client out to
/// the title screen (<c>FUN_140bbbce0</c> → <c>EVENT_LEAVE_MATCH</c> → <c>UIBindingSystem.Logout()</c>)
/// and the re-login path has never been exercised; <see cref="PracticeTargetCountsAsOpponent"/> is
/// off because a dev-only prop deciding a match result is exactly the kind of default that quietly
/// becomes the spec.
/// </para>
/// </summary>
public sealed record MatchEndOptions
{
    /// <summary>
    /// Environment switch for <see cref="Enabled"/>. <c>CRANBERRY_MATCH_ENDGAME=0</c> is the
    /// one-word revert for the whole lane.
    /// </summary>
    public const string EnabledVariable = "CRANBERRY_MATCH_ENDGAME";

    /// <summary>Environment switch for <see cref="PracticeTargetCountsAsOpponent"/> (D152).</summary>
    public const string TargetCountsVariable = "CRANBERRY_MATCH_TARGET_COUNTS";

    /// <summary>
    /// The name D152 was written with, accepted as an alias of
    /// <see cref="TargetCountsVariable"/> so the decision row and the wiring plan can both be
    /// followed literally. Either variable set to <c>1</c> arms the behaviour.
    /// </summary>
    public const string TargetCountsLegacyVariable = "CRANBERRY_PRACTICE_TARGET_ENDS_MATCH";

    /// <summary>Environment switch for <see cref="LeaveOnEnd"/> (D151).</summary>
    public const string LeaveOnEndVariable = "CRANBERRY_MATCH_LEAVE_ON_END";

    /// <summary>Environment switch for <see cref="CollisionDamage"/>.</summary>
    public const string CollisionDamageVariable = "CRANBERRY_COLLISION_DAMAGE";

    /// <summary>
    /// The floor under the <c>Ended</c> hold, in seconds, and <b>not ours to shorten</b>: the
    /// client times its own wrap-up from its own settings — <c>UI.ShowVictory</c> 7 s +
    /// <c>UI.MatchWrapUpSlide1</c> 10 s + <c>UI.MatchWrapUpSlide2</c> 10 s = 27 s — so anything
    /// shorter tears the match down under the winner's results screen. 30 leaves three seconds of
    /// margin for the burst that opened them. Adopted as the owner's own value under D53 from
    /// <c>C:\Z1\Server\Zone\ZoneMatchFlow.cs:322</c> (<c>EndedHoldSeconds = 30</c>); the value
    /// crosses, the file does not.
    /// </summary>
    public const int DefaultEndedHoldSeconds = 30;

    /// <summary>
    /// Send the lane's packets at all. Off, a death sends exactly what it sent before this lane —
    /// one <c>ce 04</c> with the cause and one <c>ce 09</c> — and nothing dies, wins, holds or
    /// resets.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// D152: the last practice dummy's death is the last opponent's, so killing it wins the match
    /// for the shooter. <b>Off by default.</b> With it off the dummy still gets its own
    /// <c>0f 4f</c> and <c>0f 48</c> — a body that falls and a kill-feed line instead of a body
    /// that vanishes — and no match state moves.
    /// </summary>
    public bool PracticeTargetCountsAsOpponent { get; init; }

    /// <summary>
    /// D151: send <c>ce 1b LeaveMatch</c> at the end of the <see cref="EndedHoldSeconds"/> hold.
    /// <b>Off by default</b> — it logs the client out to the title screen, and the path back has
    /// never been tested. Off, results stay open until Play Again or Main Menu is selected.
    /// </summary>
    public bool LeaveOnEnd { get; init; }

    /// <summary>
    /// Let an <c>8e 01 Collision.Damage</c> report move health. <b>ON since 2026-09-03, because the
    /// body is now derived</b> — from the August client's own bytes rather than from the
    /// decompiler, which was never going to have them: the client's zone receive dispatcher
    /// <c>FUN_140af3950</c> has no <c>case 0x8e</c> at all, so the packet is send-only and there is
    /// nothing on the receive side to decompile. 47 records in <c>C:\Aug2017\logs\host-*.log</c>,
    /// every one exactly 44 bytes, all parsing cleanly against
    /// <see cref="Cranberry.Zone.Vehicles.CollisionDamageReport"/>. This closes D155.
    ///
    /// <para>Off restores exactly what this arm did before: one rate-limited structured line per
    /// session and no health movement anywhere.</para>
    /// </summary>
    public bool CollisionDamage { get; init; } = true;

    /// <summary>Minimum results hold before an optional legacy LeaveOnEnd departure. Default results wait for user input.</summary>
    public int EndedHoldSeconds { get; init; } = DefaultEndedHoldSeconds;

    /// <summary>The hold in milliseconds, clamped to the 30 s floor.</summary>
    public int EndedHoldMs => Math.Max(DefaultEndedHoldSeconds, EndedHoldSeconds) * 1000;

    /// <summary>The shipped defaults.</summary>
    public static MatchEndOptions Default { get; } = new();

    /// <summary>
    /// Reads the switches from the environment. Only the exact string <c>"1"</c> or <c>"0"</c>
    /// moves a switch, so a typo leaves the default rather than silently changing how a match ends.
    /// </summary>
    public static MatchEndOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return new MatchEndOptions
        {
            Enabled = Switch(read, EnabledVariable, @default: true),
            PracticeTargetCountsAsOpponent =
                Switch(read, TargetCountsVariable, @default: false)
                || Switch(read, TargetCountsLegacyVariable, @default: false),
            LeaveOnEnd = Switch(read, LeaveOnEndVariable, @default: false),
            CollisionDamage = Switch(read, CollisionDamageVariable, @default: true),
        };
    }

    /// <summary>A one-line boot-log description, so a play-test can never guess which arms ran.</summary>
    public string Describe() =>
        $"endgame: sends={On(Enabled)} practiceTargetEndsMatch={On(PracticeTargetCountsAsOpponent)} "
        + $"leaveMatch ce1b={On(LeaveOnEnd)} endedHold={EndedHoldMs / 1000}s "
        + $"collisionDamage 8e01={On(CollisionDamage)}";

    private static string On(bool value) => value ? "ON" : "off";

    private static bool Switch(Func<string, string?> read, string name, bool @default) =>
        read(name) switch
        {
            "1" => true,
            "0" => false,
            _ => @default,
        };
}
