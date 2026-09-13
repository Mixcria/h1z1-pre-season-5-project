using Cranberry.Zone.World;

namespace Cranberry.Zone.Combat;

/// <summary>
/// The <b>wire</b> cause codes of <c>GameMode.DeathInfo</c> (<c>ce 04</c>)'s trailing
/// <c>u32 Cause</c> field — the value that decides which sentence the client's death and wrap-up
/// slides print.
///
/// <para>
/// <b>This is not <see cref="DamageCause"/>.</b> That enum
/// (<c>World/DamageQueue.cs:7</c>) is the server's own answer to "why did this player lose
/// health" and is what the damage queue carries; this one is a client-side value space with no
/// entry for a bullet at all. <see cref="DeathCauseCodes.For(DamageCause)"/> is the one place the
/// two meet.
/// </para>
///
/// <para>
/// <b>Every value here is [P] at 1148</b>, read out of the handler's own localisation switch in
/// <c>FUN_140bbb120</c> — the function <c>FUN_140bba510</c> case 4 routes <c>ce 04</c> to. The
/// switch is at
/// <c>out\gas-safezone-research\ce-dispatch\_140bba510\FUN_140bbb120_140bbb120.c:200-248</c>
/// (each case assigns a <c>UI.Results.Rank.*</c> key, resolved at <c>:249-250</c> through
/// <c>(**(code**)(*DAT_143f696c0 + 0x20))(mgr, key)</c> and published to <c>DAT_143f6e9a8</c>),
/// and the two source-name substitutions are at <c>:102-152</c> of the same file. <b>The switch
/// is exhaustive</b>: anything not listed falls to its <c>default:</c> at <c>:205-208</c> and
/// reads <c>UI.Results.Rank.Environment</c>, so no code here is invented and no missing one can
/// be guessed at.
/// </para>
///
/// <para>
/// <b>It is also not docs/15 §5's table</b> (<c>1</c> Disconnected, <c>6</c> Vehicle, <c>7</c>
/// Explosion, <c>8</c> Fire, <c>9</c> Toxic Gas, <c>10</c> Falling, <c>0xd</c> Spectate). That
/// belongs to the client's local <c>MatchResults-&lt;matchId&gt;.txt</c> writer
/// <c>FUN_1413f0ad0</c>, switching on <c>*(u32*)(record+0x70)</c> of a client-side results
/// record; the two disagree on every shared concept, so they cannot be the same value space
/// (docs/18 §3b). <see cref="Cranberry.Zone.Gas.GasPackets.ResultsFileCause"/> keeps that one.
/// </para>
///
/// <para>
/// <b>Relationship to <see cref="Cranberry.Zone.Gas.GasPackets.DeathCause"/>.</b> That class
/// holds the same numbers as loose <c>uint</c> constants, reached by the gas lane before the
/// death lane existed. This enum is the typed form the death and victory paths use; the values
/// are identical by construction and <c>DeathCauseCodeTests</c> pins them against each other, so
/// the two can never drift. Nothing in the gas lane was edited to add this.
/// </para>
/// </summary>
public enum DeathCauseCode : uint
{
    /// <summary>
    /// Not a value the switch names. Present so a mapping can say "no code" without inventing
    /// one; a <c>ce 04</c> carrying it lands on the switch's <c>default:</c> and reads
    /// <c>UI.Results.Rank.Environment</c> — "You died from the environment." (12603).
    /// </summary>
    Environment = 0,

    /// <summary>
    /// <b>[P]</b> <c>0x0d</c> — <b>vehicle</b>. Not a case of the rank switch, so the slide's
    /// rank line reads <c>UI.Results.Rank.Environment</c>; its real effect is the source-name
    /// substitution at <c>FUN_140bbb120:128-152</c>, which publishes the literal
    /// <c>"vehicle"</c> as the source name when <c>SourceId == 0</c>.
    /// </summary>
    Vehicle = 0x0d,

    /// <summary>
    /// <b>[P]</b> <c>0x11</c> — <b>falling</b>. <c>FUN_140bbb120:201-204</c>,
    /// <c>UI.Results.Rank.Falling</c>.
    /// </summary>
    Falling = 0x11,

    /// <summary>
    /// <b>[P]</b> <c>0x23</c> — <b>explosion</b>. Not a case of the rank switch (→
    /// <c>Environment</c>); the substitution at <c>FUN_140bbb120:103-127</c> publishes the literal
    /// <c>"explosion"</c> when <c>SourceId == 0</c>.
    /// </summary>
    Explosion = 0x23,

    /// <summary>
    /// <b>[P]</b> <c>0x3e</c> — <b>fire</b>. <c>FUN_140bbb120:209-212</c>,
    /// <c>UI.Results.Rank.Fire</c>.
    /// </summary>
    Fire = 0x3e,

    /// <summary>
    /// <b>[P]</b> <c>0x42</c> — <b>toxic gas</b>. <c>FUN_140bbb120:213-216</c>,
    /// <c>UI.Results.Rank.Gas</c>. The value the shipped gas death already sends (docs/18
    /// §3b/§5); docs/15 §5's "cause = 9" was the results-file enum and is wrong here.
    /// </summary>
    Gas = 0x42,

    /// <summary>
    /// <b>[P]</b> <c>0x43</c> — <b>bombing run</b>. <c>FUN_140bbb120:217-220</c>,
    /// <c>UI.Results.Rank.BombingRun</c>. Used by lethal airdrop bomber impacts.
    /// </summary>
    BombingRun = 0x43,

    /// <summary>
    /// <b>[P]</b> <c>0x48</c> — <b>the winner</b>. <c>FUN_140bbb120:221-224</c>,
    /// <c>UI.Results.Rank.EndOfMatchWinner</c>, which resolves locale <b>12600 "You won!"</b>.
    /// It rides on the winner's own <c>ce 04 {rank 0}</c> so the wrap-up slides say "You won!"
    /// instead of whatever the last <c>ce 04</c> left in that field — for a player who never
    /// died, an empty string.
    /// </summary>
    EndOfMatchWinner = 0x48,

    /// <summary>
    /// <b>[P]</b> <c>0x49</c> — <b>disconnected</b>. <c>FUN_140bbb120:225-228</c>,
    /// <c>UI.Results.Rank.PlayerDisconnected</c> (locale 12601, "You died when disconnected.").
    /// </summary>
    PlayerDisconnected = 0x49,

    /// <summary>
    /// <b>[P]</b> <c>0x4a</c> — <b>still alive when the match ended</b>.
    /// <c>FUN_140bbb120:229-232</c>, <c>UI.Results.Rank.EndOfMatchRemaining</c> (locale 12595).
    /// A survivor of a forced end, not a kill.
    /// </summary>
    EndOfMatchRemaining = 0x4a,

    /// <summary>
    /// <b>[P]</b> <c>0x4b</c> — <b>match clock expired</b>. <c>FUN_140bbb120:233-236</c>,
    /// <c>UI.Results.Rank.EndOfMatchExpired</c> (locale 12594).
    /// </summary>
    EndOfMatchExpired = 0x4b,

    /// <summary>
    /// <b>[P]</b> <c>0x4c</c> — <b>starting-area violation</b>. <c>FUN_140bbb120:237-240</c>,
    /// <c>UI.Results.Rank.StartingAreaViolation</c> (locale 12602, out of bounds).
    /// </summary>
    StartingAreaViolation = 0x4c,

    /// <summary>
    /// <b>[P] code / [I] mapping</b> <c>0x4d</c> — <c>UI.Results.Rank.GameModeKillCondition</c>,
    /// <c>FUN_140bbb120:241-244</c>. <b>The code and its key are proven; that this is the one a
    /// gunshot or a melee kill should send is inferred</b> (S4 row E1): the switch has no weapon,
    /// bullet or player-kill case at all, and this is the only key whose sentence ("You have
    /// died!") fits an ordinary kill — every other case names a specific environmental or
    /// end-of-match circumstance. The owner's own server reaches the same conclusion for its
    /// <c>PlayerKillCause</c> (Z1 <c>ZoneEndgame.Classify</c>: "there IS no player-weapon case").
    /// The killer's <i>name</i> travels in <c>ce 04</c>'s <c>Killer</c> string, not in the cause.
    /// </summary>
    GameModeKillCondition = 0x4d,

    /// <summary>
    /// <b>[P]</b> <c>0x52</c> — <c>UI.Results.Rank.Ignition.Detonation</c>,
    /// <c>FUN_140bbb120:245-247</c>. The last case of the switch.
    /// </summary>
    IgnitionDetonation = 0x52,
}

/// <summary>
/// The bridge between the server's own <see cref="DamageCause"/> and the client's
/// <see cref="DeathCauseCode"/>, plus the two read-only facts about a code that a host log wants.
/// Nothing here is on the wire.
/// </summary>
public static class DeathCauseCodes
{
    /// <summary>
    /// Which wire code a server-side damage cause reports on <c>ce 04</c>.
    ///
    /// <para>
    /// <see cref="DamageCause.Bullet"/> and <see cref="DamageCause.Melee"/> both map to
    /// <see cref="DeathCauseCode.GameModeKillCondition"/> — <b>[I]</b>, see that member: the
    /// client's switch has no weapon case, and this is the only key whose sentence fits a kill.
    /// <see cref="DamageCause.Unknown"/> maps to <see cref="DeathCauseCode.Environment"/>, which
    /// is the switch's own <c>default:</c> and therefore not a guess.
    /// </para>
    /// <para>
    /// <c>World/Match.cs</c>'s private <c>ClientDeathCause</c> is the same table minus the two
    /// weapon rows (it answers <c>0</c> for a bullet); the wiring lane replaces it with a call to
    /// this method, which is why this lives here and not there.
    /// </para>
    /// </summary>
    public static DeathCauseCode For(DamageCause cause) => cause switch
    {
        DamageCause.ToxicGas => DeathCauseCode.Gas,
        DamageCause.Bullet => DeathCauseCode.GameModeKillCondition,
        DamageCause.Melee => DeathCauseCode.GameModeKillCondition,
        DamageCause.Falling => DeathCauseCode.Falling,
        DamageCause.Vehicle => DeathCauseCode.Vehicle,
        DamageCause.Explosion => DeathCauseCode.Explosion,
        DamageCause.Fire => DeathCauseCode.Fire,
        DamageCause.Disconnected => DeathCauseCode.PlayerDisconnected,
        DamageCause.BombingRun => DeathCauseCode.BombingRun,
        _ => DeathCauseCode.Environment,
    };

    /// <summary>
    /// The <c>UI.Results.Rank.*</c> key <c>FUN_140bbb120:200-248</c> resolves for this code, or
    /// <c>UI.Results.Rank.Environment</c> for anything the switch does not name (its
    /// <c>default:</c> at <c>:205-208</c>) — which includes <see cref="DeathCauseCode.Vehicle"/>
    /// and <see cref="DeathCauseCode.Explosion"/>, whose effect is the source-name substitution.
    /// </summary>
    public static string RankKey(DeathCauseCode code) => code switch
    {
        DeathCauseCode.Falling => "UI.Results.Rank.Falling",
        DeathCauseCode.Fire => "UI.Results.Rank.Fire",
        DeathCauseCode.Gas => "UI.Results.Rank.Gas",
        DeathCauseCode.BombingRun => "UI.Results.Rank.BombingRun",
        DeathCauseCode.EndOfMatchWinner => "UI.Results.Rank.EndOfMatchWinner",
        DeathCauseCode.PlayerDisconnected => "UI.Results.Rank.PlayerDisconnected",
        DeathCauseCode.EndOfMatchRemaining => "UI.Results.Rank.EndOfMatchRemaining",
        DeathCauseCode.EndOfMatchExpired => "UI.Results.Rank.EndOfMatchExpired",
        DeathCauseCode.StartingAreaViolation => "UI.Results.Rank.StartingAreaViolation",
        DeathCauseCode.GameModeKillCondition => "UI.Results.Rank.GameModeKillCondition",
        DeathCauseCode.IgnitionDetonation => "UI.Results.Rank.Ignition.Detonation",
        _ => "UI.Results.Rank.Environment",
    };

    /// <summary>
    /// The literal source name the client substitutes when <c>SourceId == 0</c>
    /// (<c>FUN_140bbb120:102-152</c>), or <c>null</c> when the code has no substitution and the
    /// previously published name is left standing.
    /// </summary>
    public static string? SourceName(DeathCauseCode code) => code switch
    {
        DeathCauseCode.Explosion => "explosion",
        DeathCauseCode.Vehicle => "vehicle",
        _ => null,
    };
}
