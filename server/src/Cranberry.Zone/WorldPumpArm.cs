namespace Cranberry.Zone;

/// <summary>
/// The <b>per-arm rate limit</b> of the shared world pump (docs/52 §Integration step 3, as corrected
/// by the wave-5 verify pass).
/// <para>
/// <c>ZoneService.WorldPumpIntervalMs</c> ticks the one timer chain at
/// <c>Math.Min(doorRestreamMs, lootRestreamMs)</c>, so that lowering one arm's interval cannot slow
/// the other one down. But <c>WorldStream.NextStep</c> and <c>MatchDoors.NextPumpStep</c> read their
/// own interval <b>only</b> as a <c>Stop</c> test, never as a rate — so the min also <b>sped the
/// other arm up</b>. <c>CRANBERRY_DOOR_RESTREAM_MS=500</c> ran the ground-loot streamer at 500 ms:
/// six times its configured 3,000, ~50 KB/s, with successive <c>DrainBurst</c> chains (up to 11
/// slices, ~440 ms) overlapping the next tick. That is precisely the "two pumps drifting into phase
/// and interleaving two drains into a single gapless blast" that
/// <c>LootStreamOptions.RestreamIntervalMs</c>'s own doc comment says the shared pump exists to
/// prevent, arrived at from the other direction. The code comment on the pump stated only the safe
/// half of the trade.
/// </para>
/// <para>
/// <b>Why it lives here and not in <c>Cranberry.Zone.World</c>.</b> docs/22 §9.4 and
/// <c>WorldSeamTests.NoTypeUnderWorldReadsAWallClock</c>: the simulation namespace holds no
/// transport type and no clock. This type does not read a clock either — the caller passes the
/// instant in — but scheduling is a host concern, so it sits on the host side of the seam with
/// <c>ZoneService</c>, which is where <c>Environment.TickCount64</c> is actually read.
/// </para>
/// </summary>
public static class WorldPumpArm
{
    /// <summary>
    /// Whether this arm may fire at <paramref name="nowMs"/>, advancing <paramref name="nextDueMs"/>
    /// when it may.
    /// <para>
    /// Pure and monotonic-clock-agnostic: it compares and assigns, and never asks what time it is. A
    /// <paramref name="nextDueMs"/> of 0 is "never fired", so the first tick after arming always
    /// fires. A non-positive <paramref name="intervalMs"/> means "every tick", which is what the
    /// shared pump already takes a non-positive interval to mean.
    /// </para>
    /// </summary>
    /// <param name="nowMs">The current monotonic millisecond instant, supplied by the caller.</param>
    /// <param name="nextDueMs">This arm's own next-fire stamp; advanced only when the arm fires.</param>
    /// <param name="intervalMs">This arm's own configured period, not the shared pump's.</param>
    /// <param name="toleranceMs">
    /// How early a tick may still count as due. <b>Not slop — it is what stops the rate limit
    /// HALVING an arm.</b> The shared pump ticks on a timer, so when an arm's interval equals the
    /// pump's period (the shipped case: both 3,000 ms) a tick arriving one millisecond early would
    /// miss its own deadline and the arm would wait a whole further period, running at 6 s instead
    /// of 3. Half the pump's period is the natural value — it makes the arm fire on the <i>nearest</i>
    /// tick to its deadline — and it can never make an arm faster than the pump itself, which is the
    /// behaviour that existed before the limit.
    /// </param>
    public static bool IsDue(long nowMs, ref long nextDueMs, int intervalMs, int toleranceMs = 0)
    {
        if (nowMs < nextDueMs - Math.Max(0, toleranceMs))
        {
            return false;
        }

        nextDueMs = nowMs + Math.Max(1, intervalMs);
        return true;
    }
}
