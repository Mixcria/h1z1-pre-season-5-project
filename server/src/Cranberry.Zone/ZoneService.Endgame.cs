using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

/// <summary>
/// Lane 1D-lite — <b>death and victory, the orchestration</b>. The writers
/// (<see cref="DeathPackets"/>, <see cref="DeathCauseCodes"/>, <see cref="EndgamePackets"/>,
/// <see cref="MatchAlerts"/>) shipped tested and inert in the previous lane; this file is the half
/// that calls them, and it lives in its own partial so <c>ZoneService.cs</c> gains only the call
/// sites (<c>out\overhaul-20260901\1D-lite-wiring-plan.md</c>).
///
/// <para>
/// <b>Send order</b> is the owner's, adopted under D53 from <c>C:\Z1\Server\Zone\ZoneCombat.cs</c>
/// <c>Kill</c> (<c>:2398-2470</c>) and <c>ZoneEndgame.cs</c> (<c>SendDeathInfoOnce :363</c>,
/// <c>VictoryPacketsFor :419</c>, <c>LeaveMatch :483</c>), re-expressed against
/// <c>ClientProtocol_1148</c>: health, then <c>0f 4f</c>, then <c>0f 48</c>, then one
/// <c>ce 04</c>, then <c>ce 09</c> on change, then the alert.
/// </para>
///
/// <para>
/// <b>Everything here is driven from the per-connection <c>GatewaySessionState</c>, not from
/// <c>World/Match</c>.</b> Cranberry is still one session per match, so "every viewer" is today
/// "this connection"; the loops are written over the recipients they will have rather than
/// collapsed to a single send, so the second player costs a session list and nothing else.
/// </para>
/// </summary>
public sealed partial class ZoneService
{
    // Every damage source shares the same world/life policy. Lobby invulnerability is a
    // phase rule, not zero health (consumables remain usable there).
    private static bool CanTakeGameplayDamage(GatewaySessionState state) =>
        state.Match == MatchStep.InMatch && !state.DeathSent && state.Hitpoints > 0
        && !state.DevConsole.Invulnerable;

    /// <summary>
    /// Applies damage and publishes both the legacy health percentage and the current
    /// HUD's health resource, then returns the new value. It does <b>not</b> kill:
    /// the callers decide what else the tick owes before the death burst, because the gas has a
    /// diagnostic <c>DamageInfo</c> send that must keep its place in the byte order.
    /// </summary>
    private uint DecrementHealth(SoeConnection connection, GatewaySessionState state, uint amount)
    {
        if (!CanTakeGameplayDamage(state) || amount == 0) return state.Hitpoints;
        uint previous = state.Hitpoints;
        state.Hitpoints = amount >= state.Hitpoints ? 0 : state.Hitpoints - amount;
        uint current = state.Hitpoints;
        PublishPlayerHealth(connection, state, previous);
        return current;
    }

    /// <summary>
    /// Damage from anything that is not the gas, through the same health path the gas uses:
    /// <see cref="DecrementHealth"/>, then <see cref="KillPlayer"/> when the bar reaches zero.
    /// Returns true when this was the killing hit.
    ///
    /// <para>
    /// <b>This is the entry <c>8e 01 Collision.Damage</c> uses</b> — see
    /// <see cref="HandleCollisionReport"/>, which decodes the client's own 44-byte report and
    /// arrives here with <see cref="DamageCause.Falling"/> or <see cref="DamageCause.Vehicle"/> —
    /// and it is what a second player's bullet will use when there is a second player.
    /// <c>Cranberry.Tests</c> reaches it through <see cref="TestSession"/>, which is how a death
    /// is driven without waiting out 35 s of real match-flow timers.
    /// </para>
    /// </summary>
    private bool ApplyDamage(
        SoeConnection connection,
        GatewaySessionState state,
        uint amount,
        DamageCause cause,
        ulong killerGuid = 0,
        string? killerName = null,
        uint killerHealth = 0)
    {
        if (!CanTakeGameplayDamage(state) || amount == 0)
        {
            return false;
        }

        uint current = DecrementHealth(connection, state, amount);
        _log.Info($"{connection} damage: −{amount} hp ({cause}) → {current}/{_options.Gas.MaxHitpoints}");

        if (current != 0)
        {
            return false;
        }

        KillPlayer(connection, state, cause, killerGuid, killerName, killerHealth);
        return true;
    }

    /// <summary>
    /// <b>The one death path.</b> Gas reaches it from <c>ApplyGasDamage</c>, a bullet or a swing
    /// from the practice-target arbitration, and a fall or a vehicle crash from <c>8e 01</c>.
    /// Idempotent through <c>state.DeathSent</c>, which is already reset by <c>AbandonMatch</c> and
    /// <c>StartGas</c>.
    ///
    /// <para>
    /// With <see cref="MatchEndOptions.Enabled"/> off this sends exactly what the gas death sent
    /// before the lane existed — one <c>ce 04</c> with the cause and one <c>ce 09 0</c> — and
    /// <c>EndgamePacketTests.EnvironmentalDeathIsByteIdenticalToTheShippedGasDeath</c> is what
    /// makes that claim byte-for-byte rather than by inspection.
    /// </para>
    /// </summary>
    /// <param name="killerGuid">The killer's character guid, or 0 for gas, a fall or the clock.</param>
    /// <param name="killerName">
    /// The killer's name. Non-empty selects <see cref="EndgamePackets.KilledByPlayer"/> (cause
    /// <c>0x4d</c>) over <see cref="EndgamePackets.Environmental"/>; the client needs the string to
    /// render locale 12604 at all.
    /// </param>
    /// <param name="killerHealth"><c>ce 04</c>'s <c>Field4</c> — the killer's remaining health. <b>[I]</b>.</param>
    private void KillPlayer(
        SoeConnection connection,
        GatewaySessionState state,
        DamageCause cause,
        ulong killerGuid = 0,
        string? killerName = null,
        uint killerHealth = 0)
    {
        if (state.DeathSent)
        {
            return;
        }

        StopEmote(connection, state, includeSelf: true);
        StopCharacterFire(connection, state);
        StopPlayerBleeding(connection, state);
        ClearHealingHud(connection, state);
        StopCombatPresentationOnDeath(state);
        state.DeathSent = true;
        CancelVehicleComponentRemoval(connection, state);
        DropPlayerBodyBag(connection, state);
        state.Score.PlayerPlacement ??= checked((uint)AliveCount(state) + 1);
        ScorePlayerDeath(connection, state, killerGuid, cause);
        uint placement = BountyForfeitPlacement(state);
        if (!IsTeamMode(state)) CompleteBountyResult(connection, state, placement);
        state.Gas?.Stop();
        // docs/109 §5: a dead player releases its hold on the shared gas plan, so the last one out
        // of a match forgets it and the next match draws a fresh circle. Idempotent by the
        // SharedGasJoined latch, which matters because a match can also end through an abandon or a
        // link close.
        LeaveSharedGas(state);

        // Step 1 of the plan's §2a. ApplyGasDamage has already sent the bar for a gas death, so
        // this only fires for a cause that killed the player without walking the health packet
        // itself — otherwise the client is told 0/10000 twice on one tick.
        bool healthAlreadySent = state.Hitpoints == 0;
        uint previousHealth = state.Hitpoints;
        state.Hitpoints = 0;
        DeathCauseCode code = DeathCauseCodes.For(cause);

        if (!healthAlreadySent)
        {
            PublishPlayerHealth(connection, state, previousHealth);
        }

        if (!_options.MatchEnd.Enabled)
        {
            // The pre-lane behaviour, exactly: one ce 04 and one ce 09, no ragdoll, no kill feed,
            // no hold and no reset. CRANBERRY_MATCH_ENDGAME=0.
            SendTunnel(connection, w => EndgamePackets.Environmental(rankIndex: 0, code).WriteTo(w));
            SendTunnel(connection, w => EndgamePackets.WriteAliveCount(w, 0));
            CompleteRankedScore(connection, state, placement);
            _log.Info($"{connection} death: {cause} (ce 04 cause=0x{(uint)code:x2}) — endgame sends disabled "
                + $"({MatchEndOptions.EnabledVariable}=0)");
            return;
        }

        // Step 2. The victim's own copy names the victim as the ragdoll owner; a viewer's copy would
        // name that viewer (Z1 ZoneCombat.cs:2402-2427). There are no viewers yet.
        ulong victim = state.Guid;
        SendTunnel(connection, w => StartMultiStateDeath.RagdollFor(victim, victim).WriteTo(w));

        // Step 3. Envelope guid is the KILLER. The victim always gets it; the killer would get the
        // same bytes on their own link, which for one session is the same send.
        SendTunnel(connection, w => new KilledBy(killerGuid, victim).WriteTo(w));

        // Step 4. Exactly one ce 04, guarded by DeathSent above.
        int aliveAfter = AliveCount(state);
        bool byPlayer = killerGuid != 0 && !string.IsNullOrEmpty(killerName);
        GasPackets.DeathInfo info = byPlayer
            ? EndgamePackets.KilledByPlayer(aliveAfter, killerName!, killerHealth)
            : EndgamePackets.Environmental(aliveAfter, code);
        if (DeferTeamDeath(state, info))
        {
            // Individual death does not determine a squad's placement. Publish the corpse and
            // population now; the last surviving teammate determines every member's result.
            PublishSharedPlayerDeath(state, killerGuid);
            return;
        }
        PublishSharedPlayerDeath(state, killerGuid);
        SendTunnel(connection, w => info.WriteTo(w));
        CompleteRankedScore(connection, state, placement);

        // Steps 5 and 6.
        PublishAliveCount(connection, state, aliveAfter);

        _log.Info($"{connection} death: {cause} → 0f 4f victim={victim} ragdollOwner={victim}, "
            + $"0f 48 killer={killerGuid}, ce 04 rank0={aliveAfter} cause=0x{info.Cause:x2}"
            + (byPlayer ? $" killer='{killerName}'" : string.Empty)
            + $", ce 09={aliveAfter}");

        // The player's own match is over: the client is already in run state 0x21 from the 0f 4f
        // and has taken the death screen from the 0f 48 (DeathPackets, docs/21 §2h/§2i). Nothing
        // further is owed to open it — ce 04 only fills the boxes on the slides (S4 row E1).
        BeginEndedHold(connection, state, "death");
    }

    /// <summary>
    /// A practice dummy's death (D152). <b>Always</b> the dummy's own <c>0f 4f</c> and
    /// <c>0f 48</c>, so the shooter watches a body fall and reads "&lt;player&gt; has killed
    /// &lt;dummy&gt;" instead of watching it blink out of existence 10 s later; <b>never</b> a
    /// <c>ce 04</c>, which is per-player-HUD and not per-character (<c>PracticeTarget.cs:92</c>).
    /// The corpse still goes through the existing <c>DespawnPracticeTargetLater</c>.
    ///
    /// <para>
    /// Behind <see cref="MatchEndOptions.PracticeTargetCountsAsOpponent"/> the dummy also counts
    /// as an opponent, so its death drops the alive count and — when it was the last one — wins
    /// the match for the shooter.
    /// </para>
    /// </summary>
    private void KillPracticeTarget(
        SoeConnection connection, GatewaySessionState state, PracticeTarget dead)
    {
        if (dead.IsCombatBot) { KillCombatBot(connection, state, dead); return; }
        if (dead.IsAlive || dead.DeathPublished) return;
        dead.DeathPublished = true;
        DropPracticeBodyBag(connection, state, dead);
        if (state.Score.Credit(dead.WorldGuid, true))
        {
            PublishMatchKillFeed(connection, state, new RetailKillFeed(new(dead.WorldGuid, dead.Name, dead.Badge),
                FeedPlayer(state), dead.LastWeaponItemId, Headshot: dead.LastHeadshot));
            PublishKillScore(connection, state, dead.WorldGuid, dead.Name);
        }
        if (_options.MatchEnd.Enabled)
        {
            // The viewer owns the ragdoll, and the only viewer is the shooter.
            SendTunnel(connection, w => StartMultiStateDeath.RagdollFor(dead.WorldGuid, state.Guid).WriteTo(w));
            if (!dead.FullKit) SendTunnel(connection, w => new KilledBy(state.Guid, dead.WorldGuid).WriteTo(w));
            _log.Info($"{connection} combat: practice target {dead.WorldGuid} died — 0f 4f "
                + $"(ragdollOwner={state.Guid}) + 0f 48 (killer={state.Guid}); no ce 04 for a dummy");
        }

        DespawnPracticeTargetLater(connection, state, dead);

        if (dead.FullKit || !_options.MatchEnd.Enabled || !_options.MatchEnd.PracticeTargetCountsAsOpponent)
        {
            return;
        }

        int alive = AliveCount(state);
        PublishAliveCount(connection, state, alive);

        if (alive <= 1 && !state.DeathSent)
        {
            SendVictory(connection, state);
        }
    }

    /// <summary>
    /// The victory burst, to the winner and nobody else — Z1 <c>ZoneEndgame.VictoryPacketsFor</c>
    /// (<c>:419-445</c>) plus its alert. <c>ce 18</c> first, then the <c>ce 04</c> that makes the
    /// wrap-up slides say "You won!" rather than printing whatever the last <c>ce 04</c> left in
    /// the cause field, then the 11124 announcement.
    /// </summary>
    private void SendVictory(SoeConnection connection, GatewaySessionState state)
    {
        if (!_options.MatchEnd.Enabled || state.Match == MatchStep.Ended && !_pendingTeamDeaths.ContainsKey(state)
            || state.VictorySent)
        {
            return;
        }

        state.VictorySent = true;
        NotePublicMatchPhase(state, PublicMatchPhase.ENDING);
        StopPlayerBleeding(connection, state);
        CompleteBountyResult(connection, state, 1);
        // ce/18 prepares the solo result panels but returns early for a native group.
        // Team results prepare them with ce/1a before 67/08 emits EVENT_GROUP_TEAM_WIN.
        if (!IsTeamMode(state)) SendTunnel(connection, w => new ShowVictoryScreen().WriteTo(w));
        SendTunnel(connection, w => EndgamePackets.Winner().WriteTo(w));
        CompleteRankedScore(connection, state, 1);

        string name = string.IsNullOrEmpty(state.CharacterName) ? "Survivor" : state.CharacterName;
        string announcement = MatchAlerts.WinnerAnnounced(name);
        SendTunnel(connection, w => MatchAlerts.Write(w, announcement));

        string trigger = IsTeamMode(state) ? "67 08 team result" : "ce 18 to the winner only";
        _log.Info($"{connection} match: VICTORY — {trigger}, ce 04 {{rank 0, cause "
            + $"0x{(uint)DeathCauseCode.EndOfMatchWinner:x2}}}, 11 31 \"{announcement}\"");

        BeginEndedHold(connection, state, "victory");
        NotePublicMatchPhase(state, PublicMatchPhase.RESULTS);
    }

    /// <summary>
    /// Enter <c>Ended</c> and let the client's wrap-up slides finish. Results remain open
    /// until the player chooses Play Again or Main Menu, unless legacy LeaveOnEnd is enabled.
    ///
    /// <para>
    /// <b>The hold is not ours to shorten.</b> The client times its own slides from its own
    /// settings (7 + 10 + 10 = 27 s) and tearing the match down under them is a real bug the owner
    /// has already had and fixed once.
    /// </para>
    /// <para>
    /// The explicit exit handshake calls <c>AbandonMatch</c> to release the old world.
    /// Play Again waits for a fresh menu connection before admitting the next match.
    /// </para>
    /// <para>
    /// Every deferred match closure in this service re-checks <c>state.Match</c> before acting, so
    /// the new <c>Ended</c> step makes all of them inert for the duration of the hold without one
    /// of them being edited.
    /// </para>
    /// </summary>
    private void BeginEndedHold(SoeConnection connection, GatewaySessionState state, string trigger)
    {
        if (state.Match == MatchStep.Ended && state.EndedAtMs != 0)
        {
            return;
        }

        state.Match = MatchStep.Ended;
        CancelLogout(connection, state, "match ended");
        state.EndedAtMs = Environment.TickCount64;
        long endedAt = state.EndedAtMs;
        int admissionGeneration = state.MatchAdmissionGeneration;
        int holdMs = _options.MatchEnd.EndedHoldMs;
        _log.Info($"{connection} match: Ended ({trigger}) — holding {holdMs / 1000} s for the client's own "
            + "victory and wrap-up slides; default results wait for the player's exit choice");

        Later(connection, holdMs, () =>
        {
            if (state.MatchAdmissionGeneration == admissionGeneration && state.EndedAtMs == endedAt)
                CompleteEndedHold(connection, state);
        });
    }

    /// <summary>
    /// Finish the results hold. Only explicit legacy LeaveOnEnd sends a departure packet;
    /// a pending user exit owns its own handshake and must not be interrupted by this timer.
    /// </summary>
    private void CompleteEndedHold(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Match != MatchStep.Ended || state.PendingLogout is not null || state.LogoutPrepared
            || state.LogoutCompleted || _pendingTeamDeaths.ContainsKey(state))
        {
            _log.Info($"{connection} match: Ended hold cancelled ({state.Match})");
            return;
        }

        if (_options.MatchEnd.LeaveOnEnd)
        {
            // D151. The client logs ITSELF out to the title screen on this packet, so nothing may
            // follow it on this link — a lobby sent afterwards would be drawn on a login screen.
            // Off by default; the path back in has never been tested.
            SendTunnel(connection, w => new LeaveMatch().WriteTo(w));
            AbandonMatch(connection, state, "ended (leaveOnEnd)");
            _log.Info($"{connection} match: ce 1b LeaveMatch sent (configured exit) — "
                + "the client will log itself out and must log in again");
            return;
        }

        // The native wrap-up ends in a button screen. A timer is not a user choice:
        // unsolicited CompleteLogoutProcess skips the client's cleanup/Logout call,
        // and a same-link lobby reset can draw underneath its still-active results.
        _log.Info($"{connection} match: results ready; waiting for Play Again or Main Menu");
    }

    /// <summary>
    /// How many participants are still alive on this session. The local player counts until
    /// <c>DeathSent</c>; a practice dummy counts only under
    /// <see cref="MatchEndOptions.PracticeTargetCountsAsOpponent"/>, which is what keeps a dev-only
    /// prop out of the match result by default.
    /// </summary>
    private int AliveCount(GatewaySessionState state)
    {
        int alive = _sharedLootMembership.TryGetValue(state, out ulong matchId)
            ? CountSharedAlive(_sharedLootMatches[matchId])
            : (state.DeathSent ? 0 : 1) + CountCombatBots(state);

        if (!_options.MatchEnd.PracticeTargetCountsAsOpponent)
        {
            return alive;
        }

        foreach (PracticeTarget target in state.Combat.Targets.All)
        {
            if (target.IsAlive && !target.FullKit)
            {
                alive++;
            }
        }

        return alive;
    }

    /// <summary>
    /// <c>ce 09</c> and the 11113 "Only N remain." alert, <b>on change only</b> (S4 row E7): the
    /// owner's server sends the counter when it moves and not on a timer, and the client starts
    /// <c>KOTK_MX_COMBAT_FINAL</c> by itself the moment the value is exactly 2.
    /// <para>
    /// The alert is suppressed at zero — "Only 0 remain." is not a sentence the client should ever
    /// print, and at zero the recipient has already been handed a death screen.
    /// </para>
    /// </summary>
    private void PublishAliveCount(SoeConnection connection, GatewaySessionState state, int alive)
    {
        if (PublishTeamPopulation(connection, state, alive)) return;
        if (state.AliveSent == alive)
        {
            return;
        }

        bool dropped = state.AliveSent is int previous && previous > alive;
        state.AliveSent = alive;
        SendTunnel(connection, w => EndgamePackets.WriteAliveCount(w, alive));
        if (!state.Score.Settled) PublishScore(connection, state);

        if (alive <= 0)
        {
            return;
        }

        string alert = MatchAlerts.Remaining(alive);
        SendTunnel(connection, w => MatchAlerts.Write(w, alert));
        _log.Info($"{connection} match: ce 09 = {alive}{(dropped ? " (dropped)" : string.Empty)}, "
            + $"11 31 \"{alert}\"");
    }

    /// <summary>
    /// <b><c>8e 01 Collision.Damage</c> — the client's own crash, fall and gas report, decoded.</b>
    ///
    /// <para>
    /// This method used to say the body was underived and drop every report. It is derived now, and
    /// not from the decompiler: the client's zone receive dispatcher <c>FUN_140af3950</c> has 164
    /// cases and <b>no <c>case 0x8e</c></b>, and <c>cCollisionPacketIdDamage</c> is referenced by
    /// exactly one function in the whole 1148 image — the name registrar <c>FUN_1413d1490</c>,
    /// which attaches no reader and no writer factory. The packet is send-only from the client, so
    /// there was never anything to decompile; only the wire could answer, and it has. 47 records in
    /// <c>C:\Aug2017\logs\host-*.log</c>, every one exactly 44 bytes, all parsing cleanly against
    /// <see cref="CollisionDamageReport"/>. <b>D155 is closed.</b>
    /// </para>
    /// <para>
    /// <b>The amount is the client's own number and nothing scales it</b> (grade CLIENT). What the
    /// server adds is three gates and a burst rule, all four adopted from the owner's own Z1 under
    /// D53 — and all four are load-bearing rather than tuning:
    /// </para>
    /// <list type="number">
    /// <item>Nothing before the world release counts (<c>ZoneCombat.cs:2004</c>) — his round-20 fix.</item>
    /// <item>Nothing inside <see cref="VehicleDamageOptions.PostArrivalGraceMs"/> of it counts
    /// (<c>:196</c>): the client settles its actor onto the terrain on arrival and reports the
    /// settle as an impact.</item>
    /// <item>A <see cref="CollisionDamageCause.FallDamage"/> report under the canopy is suppressed
    /// (<c>:2033</c>). Without it the first thing decoding this packet would do is kill every player
    /// on the drop: one of the recovered samples is a fall reporting <b>42,637</b> against a 10,000
    /// bar.</item>
    /// <item>Within <see cref="VehicleDamageOptions.CollisionBurstWindowMs"/> an impact costs its
    /// <i>peak</i>, once (<c>ZoneCombat.cs:189</c>) — his round-25 fix, and the recovered samples
    /// are exactly why: they are two interleaved ramps (7, 14, 22, 30 … and 262, 272, 283, 293 …),
    /// so the client re-reports one continuing event with a running total.</item>
    /// </list>
    /// <para>
    /// <b>Gas reports are counted, never charged.</b> 44 of the 47 samples carry cause 2, and
    /// Cranberry runs its own gas ladder — applying the client's copy as well would bill the same
    /// damage twice. The line cross-checks the two instead. Causes 0 and 3 have never been seen on
    /// this wire, so they are logged with their hex and dropped until one is.
    /// </para>
    /// </summary>
    // Owner-requested dismount protection, not a recovered retail timing. This gates
    // physical reports only: ordinary combat, gas and vehicle condition still apply.
    internal const int VehicleExitCollisionGraceMs = 2_000;

    // Owner policy: allow leaving a roof safely for ten seconds after parachute landing.
    internal const int ParachuteLandingFallGraceMs = 10_000;

    private void HandleCollisionReport(
        SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload, long? nowMs = null)
    {
        long now = nowMs ?? Environment.TickCount64;
        state.CollisionReports++;

        if (!CollisionDamageReport.TryParse(payload, out CollisionDamageReport? parsed)
            || parsed is null)
        {
            // A shape this server has never seen. Rate-limited, with untruncated hex, because it is
            // the only client-originated evidence that the 44-byte form is not the only one.
            if (state.LastCollisionLogMs == 0 || now - state.LastCollisionLogMs >= CollisionLogIntervalMs)
            {
                state.LastCollisionLogMs = now;
                _log.Warn($"{connection} collision 8e {(payload.Length > 1 ? payload[1] : 0):x2} "
                    + $"({payload.Length} bytes, expected {CollisionDamageReport.Length}): "
                    + $"{Convert.ToHexString(payload)} — NOT the derived 44-byte body, dropped");
            }

            return;
        }

        CollisionDamageReport report = parsed;
        VehicleDamageOptions damage = _options.VehicleDamage;
        bool armed = _options.MatchEnd.CollisionDamage;

        // Gate 1 (D53, ZoneCombat.cs:2004). Before the release the client is still building the
        // world and its reports are about a character nobody is playing yet.
        string? refused =
            !armed ? $"{MatchEndOptions.CollisionDamageVariable}=0"
            : state.Match != MatchStep.InMatch || !state.Released ? "before the world release"
            : state.ReleasedAtMs != 0 && now - state.ReleasedAtMs < damage.PostArrivalGraceMs
                ? $"inside the {damage.PostArrivalGraceMs} ms post-arrival grace"
            : report.Cause == CollisionDamageCause.FallDamage
                && damage.SuppressFallDamageUnderCanopy
                && state.ChuteGuid != 0 && state.MountRequested
                ? "a fall under the canopy"
            : null;

        // CharacterId is the damaged actor; ObjectCharacterId is its collision counterpart.
        // Retail managed-vehicle fall reports frequently put the car's GUID in both fields.
        MatchVehicle? struck = null;
        // Managed physics reports name the vehicle as BOTH character and object.
        // IsSelfReport means the reporting actor hit itself, not that actor is the player.
        if (state.Fleet is VehicleFleet fleet)
            fleet.TryGet(report.CharacterId, out struck);
        if (struck is null && report.CharacterId != state.Guid)
            refused ??= "report belongs to an unknown actor";
        if (struck is null && report.CharacterId == state.Guid
            && report.Cause == CollisionDamageCause.FallDamage
            && now < state.ParachuteFallProtectedUntilMs)
            refused ??= "post-parachute landing fall protection";
        if (struck is null && report.CharacterId == state.Guid
            && now < state.VehicleExitProtectedUntilMs
            && report.Cause is CollisionDamageCause.VehicleCollision or CollisionDamageCause.FallDamage)
            refused ??= "vehicle exit protection";
        if (struck is null && state.Fleet?.TryGetForOccupant(state.Guid, out _) == true
            && report.Cause == CollisionDamageCause.FallDamage)
            refused ??= "seated player cannot take pedestrian fall damage";

        if (refused is not null)
        {
            if (state.LastCollisionLogMs == 0 || now - state.LastCollisionLogMs >= CollisionLogIntervalMs)
            {
                state.LastCollisionLogMs = now;
                _log.Info($"{connection} collision {report} — no damage: {refused} "
                    + $"({state.CollisionReports} report(s) this session)");
            }

            return;
        }

        switch (report.Cause)
        {
            case CollisionDamageCause.ToxicGas:
                // Counted, never charged: Cranberry owns the gas ladder and this is the client's
                // own second opinion on the same damage.
                state.CollisionGasReports++;
                if (state.LastCollisionLogMs == 0
                    || now - state.LastCollisionLogMs >= CollisionLogIntervalMs)
                {
                    state.LastCollisionLogMs = now;
                    _log.Info($"{connection} collision {report} — gas, counted only "
                        + $"({state.CollisionGasReports} gas report(s); the server's own ladder is "
                        + "what moves the bar)");
                }

                return;

            case CollisionDamageCause.FallDamage:
            case CollisionDamageCause.VehicleCollision:
                break;

            default:
                _log.Info($"{connection} collision {report} — cause 0x{report.CauseValue:x2} has "
                    + $"never been seen on this wire; logged and dropped. hex="
                    + $"{Convert.ToHexString(payload)}");
                return;
        }

        if (struck is not null)
        {
            // Only the current physics reporter can report impacts for this car.
            if (struck.OwnerGuid != state.Guid
                && (struck.OwnerGuid != 0 || struck.CoastingOwnerGuid != state.Guid)) return;
            // Captured August landings can report more than an entire condition bar. Bound the
            // cumulative event before billing its increase, so repeated reports cannot bypass it.
            uint bounded = damage.CollisionDamageFor(report.Damage);
            uint charged = struck.Collision.Charge(bounded, now, damage.CollisionBurstWindowMs);
            if (charged == 0)
            {
                return;
            }

            ApplyVehicleDamage(connection, state, struck, charged, "8e 01 crash", collision: true);
            return;
        }

        // A fall, or a crash whose object this server does not hold as a vehicle. The player takes
        // it, on the player's own burst window.
        uint amount = state.PlayerCollision.Charge(report.Damage, now, damage.CollisionBurstWindowMs);
        if (amount == 0)
        {
            return;
        }

        DamageCause cause = report.Cause == CollisionDamageCause.FallDamage
            ? DamageCause.Falling
            : DamageCause.Vehicle;
        _log.Info($"{connection} collision {report} → {cause} −{amount} hp "
            + $"(raw {report.Damage}, burst peak {state.PlayerCollision.Peak})");
        ApplyDamage(connection, state, amount, cause);
    }

    /// <summary>One structured collision line per session per 10 s; the client reports continuously.</summary>
    private const int CollisionLogIntervalMs = 10_000;

    /// <summary>
    /// The <b>only</b> seam <c>Cranberry.Tests</c> has into this service's private per-connection
    /// match state, and the reason it exists: the real route to a death is the 15 s lobby timer,
    /// the 20 s countdown, the drop, and then a gas ladder whose first damage phase opens at 4:30
    /// — all on <c>Task.Delay</c> against the wall clock, because <c>Later</c> has no injectable
    /// clock. A test that drove that honestly would take minutes per case, so it would not exist,
    /// and the death-and-victory burst would be pinned by unit tests over the writers only.
    ///
    /// <para>
    /// Everything here is a <i>shortcut into</i> the real code, never a reimplementation of it:
    /// <see cref="Damage"/> is the production <see cref="ApplyDamage"/>, <see cref="KillTarget"/>
    /// is the production <see cref="KillPracticeTarget"/>, and the hold and the reset are the
    /// production <see cref="BeginEndedHold"/>. Only the <i>entry</i> is faked.
    /// </para>
    /// </summary>
    internal sealed class TestSession(ZoneService service, SoeConnection connection)
    {
        private readonly GatewaySessionState _state = (GatewaySessionState)connection.Tag!;

        /// <summary>Puts the session in a live match at full health, with <paramref name="dummies"/> practice targets.</summary>
        public IReadOnlyList<ulong> EnterMatch(uint hitpoints = 10_000, int dummies = 0)
        {
            _state.Match = MatchStep.InMatch;
            _state.Hitpoints = hitpoints;
            _state.DeathSent = false;
            _state.VictorySent = false;
            _state.AliveSent = null;
            _state.EndedAtMs = 0;

            return dummies <= 0
                ? []
                : [.. _state.Combat.Targets
                    .Spawn(System.Numerics.Vector3.Zero, 0f, dummies, 4f, 10_000)
                    .Select(t => t.WorldGuid)];
        }

        /// <inheritdoc cref="ZoneService.ApplyDamage"/>
        public bool Damage(
            uint amount,
            DamageCause cause,
            ulong killerGuid = 0,
            string? killerName = null,
            uint killerHealth = 0) =>
            service.ApplyDamage(connection, _state, amount, cause, killerGuid, killerName, killerHealth);

        /// <summary>Kills one dummy through the real arbitration outcome <c>DrainCombatArm</c> produces.</summary>
        public void KillTarget(ulong worldGuid)
        {
            PracticeTarget target = _state.Combat.Targets.Find(worldGuid)
                ?? throw new InvalidOperationException($"no practice target {worldGuid}");
            target.Damage(target.Health, Environment.TickCount64);
            service.KillPracticeTarget(connection, _state, target);
        }

        /// <summary>
        /// Runs the production <see cref="CompleteEndedHold"/> now instead of after the real 30 s.
        /// <c>Later</c> is wall-clock only, so this is the whole of the hold that a test can drive:
        /// what is asserted is the <i>state transition</i> either side of it, not the duration.
        /// </summary>
        public void ExpireEndedHold() => service.CompleteEndedHold(connection, _state);

        /// <summary>The private <c>MatchStep</c>, by name.</summary>
        public string Step => _state.Match.ToString();

        /// <summary>The last <c>ce 09</c> the endgame path sent, or null.</summary>
        public int? AliveSent => _state.AliveSent;

        /// <summary>Server-side health.</summary>
        public uint Hitpoints => _state.Hitpoints;

        /// <summary>The local character guid.</summary>
        public ulong Guid => _state.Guid;
    }

    /// <summary>Opens the <see cref="TestSession"/> seam for <c>Cranberry.Tests</c>.</summary>
    internal TestSession ForTest(SoeConnection connection) => new(this, connection);
}
