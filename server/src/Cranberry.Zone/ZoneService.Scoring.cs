using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private RankedScoreStore? _rankedScores;
    private RankedScoreStore RankedScores => LazyInitializer.EnsureInitialized(ref _rankedScores, () => new(_options.EconomyStoreRoot is null
        ? null : Path.Combine(_options.EconomyStoreRoot, "ranked-preseason5")));
    // Both the HTTPS and UDP paths read committed profiles from memory.
    public IReadOnlyDictionary<string, RankedProfile> LauncherRankedProfiles(IEnumerable<string> accounts, MatchMode mode)
        => RankedScores.ReadMany(accounts, mode);
    private static string ScoreAccount(GatewaySessionState state) => string.IsNullOrEmpty(state.AccountId)
        ? "character:" + state.Guid : state.AccountId;
    private static MatchMode ScoreMode(GatewaySessionState state) => state.BountyAdmission.Mode == MatchMode.Unknown
        ? MatchMode.Solo : state.BountyAdmission.Mode;
    private RankBadge BadgeFor(GatewaySessionState state) => state.RankPreview ??
        (state.DevConsole.Tier == ConsoleTier.Owner ? new(RankedTier.Staff)
        : RankedScores.Read(ScoreAccount(state), ScoreMode(state)).Badge(ScoreMode(state)));
    private KillFeedPlayer FeedPlayer(GatewaySessionState state) => new(state.Guid, state.CharacterName, BadgeFor(state));

    private void PublishMatchKillFeed(SoeConnection origin, GatewaySessionState state, RetailKillFeed feed)
    {
        SendTunnel(origin, feed.WriteTo);
        if (state.BountyAdmission.MatchId == 0) return;
        foreach (var pair in _accountSessions)
            if (!ReferenceEquals(pair.Key, origin) && pair.Key.State == ConnectionState.Open
                && pair.Value.BountyAdmission.MatchId == state.BountyAdmission.MatchId)
                SendTunnel(pair.Key, feed.WriteTo);
    }

    private void PublishScore(SoeConnection connection, GatewaySessionState state, uint placement = 0, bool finished = false)
    {
        uint playerCount = (uint)BountyPopulation(state);
        uint teamCount = playerCount;
        uint teamKills = (uint)state.Score.Kills;
        uint livingTeamMembers = finished ? 0u : state.DeathSent ? 0u : 1u;
        if (_sharedLootMembership.TryGetValue(state, out ulong id))
        {
            var match = _sharedLootMatches[id];
            // MatchResult's entrant totals are separate from ce/09's remaining counts.
            // Eliminated members remain on the roster until they leave the match.
            playerCount = (uint)match.Members.Count;
            teamCount = (uint)match.Teams.Values.Distinct().Count();
            if (IsTeamMode(state))
            {
                var members = TeamMembers(state);
                teamKills = checked((uint)members.Sum(member => member.Score.Kills));
                livingTeamMembers = finished ? 0u : (uint)members.Count(member =>
                    match.Members.TryGetValue(member, out var link) && IsSharedAlive(member, link));
            }
        }
        SendTunnel(connection, new MatchScoreUpdate(state.Guid, state.BountyAdmission.MatchId,
            state.BountyAdmission.Mode switch { MatchMode.Duos => 2u, MatchMode.Fives => 3u, _ => 1u },
            (uint)state.Score.Kills, placement, finished,
            playerCount, (uint)TeamSize(state), teamCount, teamKills, livingTeamMembers,
            PlayerFinished: finished || state.DeathSent,
            PlayerPlacement: IsTeamMode(state) ? state.Score.PlayerPlacement : null,
            GroupId: IsTeamMode(state) ? ClientTeamId(state) : 0,
            Experience: state.ExperienceEarned).WriteTo);
    }

    private void ScorePlayerDeath(SoeConnection victimConnection, GatewaySessionState victim, ulong killerGuid, DamageCause cause)
    {
        GatewaySessionState? killer = (_peers.Find(killerGuid)?.Sink as SessionPeerSink)?.Connection.Tag as GatewaySessionState;
        if (killer is not null && (killer.Guid == victim.Guid || killer.BountyAdmission.MatchId == 0
            || killer.BountyAdmission.MatchId != victim.BountyAdmission.MatchId)) killer = null;
        SessionPeerSink? creditedKiller = killer is not null && !AreTeammates(killer, victim)
            && killer.Score.Credit(victim.Guid, false)
            ? _peers.Find(killerGuid)?.Sink as SessionPeerSink : null;
        var killerBot = killer is null ? FindCombatBot(victim, killerGuid) : null;
        KillFeedPlayer? killerIdentity = killer is not null ? FeedPlayer(killer)
            : killerBot is not null ? new(killerBot.WorldGuid, killerBot.Name, killerBot.Badge) : null;
        var feed = new RetailKillFeed(FeedPlayer(victim), killerIdentity,
            cause == DamageCause.Bullet ? victim.LastDamageWeapon : 0, (uint)DeathCauseCodes.For(cause),
            cause == DamageCause.Bullet && victim.LastDamageHeadshot,
            killerIdentity is null ? FeedDeathKey(cause) : "BR.PlayerKilledPlayerWith",
            killerIdentity is null ? FeedSelfDeathKey(cause) : "");
        var sent = new HashSet<SoeConnection> { victimConnection };
        SendTunnel(victimConnection, feed.WriteTo);
        foreach (var pair in _accountSessions)
            if (pair.Key.State == ConnectionState.Open && victim.BountyAdmission.MatchId != 0
                && pair.Value.BountyAdmission.MatchId == victim.BountyAdmission.MatchId && sent.Add(pair.Key))
                SendTunnel(pair.Key, feed.WriteTo);
        if (victim.Peer is { } peer)
        {
            var viewers = new List<PeerViewer>();
            _peers.CollectViewers(peer, viewers);
            foreach (var viewer in viewers)
                if (viewer.Viewer.Sink is SessionPeerSink sink && sent.Add(sink.Connection)) SendTunnel(sink.Connection, feed.WriteTo);
        }
        // Receipt completion is emitted by SetExperience. Publish the victim's name/badge
        // first, then its XP award, so the stock HUD can assemble the whole kill receipt.
        if (creditedKiller is not null)
            PublishKillScore(creditedKiller.Connection, killer!, victim.Guid, victim.CharacterName);
    }

    // CodeStringMappings.txt: these are feed phrases, separate from the result-slide labels.
    private static string FeedDeathKey(DamageCause cause) => cause switch
    {
        DamageCause.ToxicGas => "BR.ChokedOnToxicGas",
        DamageCause.Falling => "BR.PlayerFellToTheirDeath",
        DamageCause.Fire => "BR.BurnedToDeath",
        DamageCause.Explosion => "BR.DiedDueToExplosion",
        DamageCause.BombingRun => "BR.GotBombed",
        DamageCause.Disconnected => "BR.DisconnectedFromTheMatch",
        _ => "BR.KilledByEnvironment",
    };
    private static string FeedSelfDeathKey(DamageCause cause) => cause switch
    {
        DamageCause.ToxicGas => "BR.KilledSelfToxicGas",
        DamageCause.Falling => "BR.KilledSelfFalling",
        DamageCause.Fire => "BR.KilledSelfBurned",
        DamageCause.Explosion => "BR.KilledSelfExplosion",
        DamageCause.BombingRun => "BR.KilledSelfBombed",
        DamageCause.Disconnected => "BR.KilledSelfDisconnected",
        _ => "BR.KilledSelfEnvironment",
    };

    private void CompleteRankedScore(SoeConnection connection, GatewaySessionState state, uint placement)
    {
        if (state.Score.Settled) return;
        if (state.Match is not (MatchStep.Dropping or MatchStep.InMatch or MatchStep.Ended)) return;
        state.Score.FinalPlacement ??= placement;
        placement = state.Score.FinalPlacement.Value;
        int points = checked(state.Score.KillPoints + RankedScoring.PlacementPoints(placement));
        // Public Solo/Duos/Fives have independent season records. Custom and training queues
        // remain outside ranked progression.
        if (state.BountyAdmission.QueueKind == MatchQueueKind.Public
            && state.BountyAdmission.Mode is MatchMode.Solo or MatchMode.Duos or MatchMode.Fives
            && state.BountyAdmission.MatchId != 0)
        {
            try
            {
                var committed = RankedScores.CompleteAsync(ScoreAccount(state), state.BountyAdmission.Mode,
                    new(EconomyRunId + ":" + state.BountyAdmission.MatchId, placement, state.Score.Kills, points,
                        Died: state.DeathSent || placement > 1));
                state.Score.Settled = true; // The store owns retries even if this connection leaves.
                if (committed.IsCompletedSuccessfully)
                    PublishFinishedRank(connection, state, placement, committed.Result);
                else
                    _ = PublishCommittedRank(committed, connection, state, state.BountyAdmission.MatchId, placement);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                _log.Warn($"ranking save failed for {state.Guid}: {error.Message}");
                return;
            }
        }
        state.Score.Settled = true;
        PublishFinishedRank(connection, state, placement, RankedScores.Read(ScoreAccount(state), state.BountyAdmission.Mode));
    }

    private async Task PublishCommittedRank(Task<RankedProfile> committed, SoeConnection connection,
        GatewaySessionState state, ulong matchId, uint placement)
    {
        try
        {
            var profile = await committed.ConfigureAwait(false);
            // Connection and session state may only be inspected on the listener thread.
            Post?.Invoke(() =>
            {
                if (connection.State == ConnectionState.Open && ReferenceEquals(connection.Tag, state)
                    && state.BountyAdmission.MatchId == matchId && state.Score.FinalPlacement == placement)
                    PublishFinishedRank(connection, state, placement, profile);
            });
        }
        catch (Exception error) { _log.Warn($"ranked completion could not be published: {error.Message}"); }
    }

    private void PublishFinishedRank(SoeConnection connection, GatewaySessionState state, uint placement, RankedProfile profile)
    {
        if (connection.State == ConnectionState.Open)
        {
            SendTeamResults(connection, state, placement);
            var badge = profile.Badge(state.BountyAdmission.Mode);
            SendTunnel(connection, new MatchRankUpdate(state.Guid, state.BountyAdmission.MatchId, badge).WriteTo);
            PublishScore(connection, state, placement, finished: true);
        }
        _log.Info($"{connection} score: {state.Score.Kills} kills, {state.Score.KillPoints + RankedScoring.PlacementPoints(placement)} points, placement {placement}");
    }

    private void SendPracticeTargetSpawn(SoeConnection connection, PracticeTargetSpawn spawn)
    {
        SendTunnel(connection, spawn.Add);
        SendTunnel(connection, spawn.Full);
        if (!spawn.Target.FullKit) return;
        SendTunnel(connection, w => w.WriteRaw(PracticeTargetKit.FullCharacter(spawn.Target)));
        uint transient = spawn.Target.TransientId;
        ulong weaponGuid = spawn.Target.WorldGuid + 0x1000_0000;
        SendTunnel(connection, w => w.WriteRaw(RemoteWeaponPackets.Reset(transient, [])));
        SendTunnel(connection, w => w.WriteRaw(RemoteWeaponPackets.AddWeapon(transient,
            weaponGuid, PracticeTargetKit.Weapon)));
        if (PracticeTargetKit.Weapon.FireGroups.Count > 0)
            SendTunnel(connection, w => w.WriteRaw(RemoteWeaponPackets.SwitchFireMode(transient, weaponGuid, 0, 0)));
    }

    private ConsoleReply ConsoleRank(GatewaySessionState state, string argument)
    {
        if (argument != "" && argument != "status")
        {
            if (state.DevConsole.Tier != ConsoleTier.Owner) return ConsoleReply.Failed("rank previews require owner/admin permission");
            if (argument == "reset") state.RankPreview = null;
            else if (RankBadge.TryParse(argument, out var tier)) state.RankPreview = new(tier);
            else return ConsoleReply.Failed("rank: bronze, silver, gold, platinum, diamond, master/emerald, royalty, staff, reset");
        }
        var profile = RankedScores.Read(ScoreAccount(state), ScoreMode(state));
        return ConsoleReply.Plain([$"* {BadgeFor(state)} | {state.Score.Kills} kills ({state.Score.PracticeKills} practice) | {state.Score.KillPoints:N0} kill points",
            $"* Season: {profile.Matches} matches | best ten {profile.Points:N0} points | {profile.Badge(ScoreMode(state))}"]);
    }
}
