using System.Security.Cryptography;
using Cranberry.Transport;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private static readonly string EconomyRunId = System.Guid.NewGuid().ToString("N");
    private readonly BountyLedger? _bountyLedger;
    private readonly Dictionary<uint, MatchAdmissionContext> _formingBountyMatches = [];
    private readonly HashSet<ulong> _closedBountyMatches = [];
    private readonly HashSet<ulong> _startedBountyMatches = [];
    private sealed record PendingBountyCompletion(string AccountId, MatchAdmissionContext Admission,
        uint? Placement, bool BeforeStart);
    private readonly Dictionary<(string AccountId, ulong MatchId), PendingBountyCompletion> _pendingBountyCompletions = [];
    private bool _bountyCompletionRetryArmed;

    private bool PrepareMatchAdmission(GatewaySessionState state, PlayerWorldTransferRequest request)
    {
        if (DoorSwingClientReady is { } ready && !ready(state.AccountId)) return false;
        RetryPendingBountyCompletions(state.AccountId);
        ulong id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
        if (id == 0) id = 1;
        var resolved = ResolveMatchAdmission(state, request, id);
        if (resolved == MatchAdmissionContext.Unknown) return false;
        uint worldId = SelectMatchWorld(request);
        if (_options.PublicQueue is not null && resolved.QueueKind == MatchQueueKind.Public)
        {
            var reservingParty = _parties.Find(state.Guid);
            IReadOnlyList<ulong> group = reservingParty?.Members ?? new[] { state.Guid };
            long now = Environment.TickCount64;
            // A full lobby can freeze when an incoming whole party does not fit. Recheck
            // readiness here too: an opponent may have begun logout since the queue pump.
            var openLobby = PublicQueues.Snapshots.FirstOrDefault(r => r.WorldId == worldId
                && r.Phase < PublicMatchPhase.ROSTER_FROZEN);
            if (openLobby is not null)
                PublicQueues.SetReadyPlayers(openLobby.MatchId, ReadyPublicLobbyPlayers(openLobby.MatchId), now);
            if (!PublicQueues.TryReserve(worldId, resolved.Mode, group, now, out ulong reservedId))
                return false;
            resolved = resolved with { MatchId = reservedId };
        }
        else if (_formingBountyMatches.TryGetValue(worldId, out var forming)
            && forming.QueueKind == resolved.QueueKind && forming.Mode == resolved.Mode)
            resolved = forming;
        else
            _formingBountyMatches[worldId] = resolved;
        state.BountyAdmission = resolved;
        state.MatchAdmissionGeneration++;
        state.MatchTransferRequest = request;
        var party = _parties.Find(state.Guid);
        state.MatchPartyId = party?.Id ?? 0;
        state.MatchPartySize = party?.Members.Count ?? 1;
        state.Score.Reset();
        state.ExperienceAtMatchStart = null;
        state.ExperienceEarned = 0;
        state.Combat.Targets.Clear();
        state.Combat.TargetsArmed = false;
        state.LastDamageWeapon = 0;
        state.LastDamageHeadshot = false;
        state.BountyWorldId = worldId;
        state.HostedGameId = resolved.QueueKind == MatchQueueKind.Hosted ? _hostedGames.GetGame(worldId)?.Id : null;
        state.BountyPlacement = null;
        state.BountyResultSettled = false;
        state.Bounty = MatchBountyState.None;
        if (_bountyLedger?.Read(state.AccountId, resolved.MatchId) is { Status: "Backed" } backed)
            state.Bounty = new(backed.Amount, backed.OptionId);
        return true;
    }

    private uint AdmittedGameMode(GatewaySessionState state) =>
        TryGetMatchDefinition(state.BountyWorldId, out var definition) ? definition.GameModeId : 13;

    private uint BountyForfeitPlacement(GatewaySessionState state) => IsTeamMode(state)
        ? TeamForfeitPlacement(state) : (uint)(1 + _accountSessions.Values
        .Where(other => !ReferenceEquals(other, state) && other.BountyAdmission.MatchId == state.BountyAdmission.MatchId
            && !other.DeathSent && !other.VictorySent && other.Match is MatchStep.Dropping or MatchStep.InMatch)
        .Select(other => other.Guid).Distinct().Count());

    private int BountyPopulation(GatewaySessionState state) => Math.Max(1, _accountSessions.Values
        .Where(peer => peer.BountyAdmission.MatchId == state.BountyAdmission.MatchId
            && peer.Match is MatchStep.Queued or MatchStep.Transferring or MatchStep.Zoning or MatchStep.Lobby
                or MatchStep.Dropping or MatchStep.InMatch)
        .Select(peer => string.IsNullOrEmpty(peer.AccountId) ? peer.Guid.ToString() : peer.AccountId).Distinct().Count());

    private bool MayBack(GatewaySessionState state) => !_closedBountyMatches.Contains(state.BountyAdmission.MatchId)
        && state.PregameClientReady && BountyEligibility.CanBack(state.BountyAdmission,
        state.Match == MatchStep.Lobby ? BountyPhase.Lobby : BountyPhase.Menu,
        _options.Bounty.Enabled, _options.Bounty.AcceptAnte);

    private void PublishBountyOffer(SoeConnection connection, GatewaySessionState state)
    {
        bool eligible = MayBack(state);
        SendTunnel(connection, _options.Bounty.Costs(eligible, state.BountyAdmission.MatchId).WriteTo);
        if (eligible) SendBountyTables(connection, state, BountyPopulation(state));
    }

    private bool LockBackingForDrop(SoeConnection connection, GatewaySessionState state)
    {
        ulong matchId = state.BountyAdmission.MatchId;
        if (_closedBountyMatches.Add(matchId))
        {
            if (_formingBountyMatches.GetValueOrDefault(state.BountyWorldId)?.MatchId == matchId)
                _formingBountyMatches.Remove(state.BountyWorldId);
            foreach (var peer in _accountSessions)
                if (peer.Value.BountyAdmission.MatchId == matchId && peer.Key.State == ConnectionState.Open)
                    SendTunnel(peer.Key, MatchBountyCosts.Unavailable.WriteTo);
        }
        if (_bountyLedger is not null)
        {
            foreach (var peer in _accountSessions.Values.Where(peer => peer.BountyAdmission.MatchId == matchId
                && peer.Bounty.BountyType != 0).DistinctBy(peer => peer.AccountId))
            {
                var locked = _bountyLedger.Lock(peer.AccountId, matchId);
                if (!locked.Succeeded)
                {
                    _log.Warn($"{connection} backing could not be locked; drop deferred: {locked.Error}");
                    int generation = state.DevConsole.LobbyGeneration;
                    Later(connection, 1000, () =>
                    {
                        if (state.BountyAdmission.MatchId == matchId && state.DevConsole.LobbyGeneration == generation)
                            BeginMatchDrop(connection, state);
                    });
                    return false;
                }
            }
        }
        _startedBountyMatches.Add(matchId);
        if (_formingBountyMatches.GetValueOrDefault(state.BountyWorldId)?.MatchId == state.BountyAdmission.MatchId)
            _formingBountyMatches.Remove(state.BountyWorldId);
        return true;
    }

    private void AcceptBountySelection(SoeConnection connection, GatewaySessionState state, uint optionId)
    {
        if (!MayBack(state) || !_options.Bounty.TryGetAnte(optionId, out var ante))
        {
            if (BountyEligibility.IsEligible(state.BountyAdmission) && _options.Bounty.Enabled)
                SendTunnel(connection, state.Bounty.WriteTo);
            else
                SendTunnel(connection, MatchBountyCosts.Unavailable.WriteTo);
            _log.Warn($"{connection} refused bounty option {optionId}: unavailable mode, phase or option");
            return;
        }
        if (_bountyLedger is not null)
        {
            var result = _bountyLedger.Back(state.AccountId, state.BountyAdmission, BountyPhase.Lobby, optionId, _options.Bounty);
            if (!result.Succeeded)
            {
                SendTunnel(connection, state.Bounty.WriteTo);
                _log.Warn($"{connection} refused backing: {result.Error}");
                return;
            }
            var saved = _bountyLedger.Read(state.AccountId, state.BountyAdmission.MatchId)!;
            if (saved.Status != "Backed")
            {
                SendTunnel(connection, state.Bounty.WriteTo);
                return;
            }
            state.Bounty = new(saved.Amount, saved.OptionId);
            PublishAccountEconomy(state.AccountId, inventoryChanged: false);
        }
        else
        {
            // In-memory embedding fixture; the shipped host always supplies the durable store.
            if (state.Bounty.BountyType == 0)
            {
                uint held = state.Currency.GetValueOrDefault(ante.CurrencyId);
                if (held < ante.Amount) { SendTunnel(connection, state.Bounty.WriteTo); return; }
                state.Currency[ante.CurrencyId] = held - ante.Amount;
                state.Bounty = new(ante.Amount, ante.BountyType);
                SendTunnel(connection, new SetAccountCurrencyRecord(ante.CurrencyId, held - ante.Amount).WriteTo);
            }
        }
        SendTunnel(connection, state.Bounty.WriteTo);
        foreach (var peer in _accountSessions)
            if (peer.Key.State == ConnectionState.Open && peer.Value.BountyAdmission.MatchId == state.BountyAdmission.MatchId
                && MayBack(peer.Value))
                SendBountyTables(peer.Key, peer.Value, BountyPopulation(peer.Value));
    }

    private void CompleteBountyResult(SoeConnection connection, GatewaySessionState state, uint placement)
    {
        if (state.BountyResultSettled || !BountyEligibility.IsEligible(state.BountyAdmission)
            || !_options.Bounty.Enabled || _bountyLedger is null) return;
        state.BountyPlacement ??= placement;
        QueueBountyCompletion(new(state.AccountId, state.BountyAdmission, state.BountyPlacement, false));
    }

    private void CancelBountyOnDeparture(SoeConnection connection, GatewaySessionState state)
    {
        LeavePublicQueue(state);
        if (_bountyLedger is not null && state.Bounty.BountyType != 0
            && state.Match is MatchStep.Queued or MatchStep.Transferring or MatchStep.Zoning or MatchStep.Lobby)
        {
            if (!_startedBountyMatches.Contains(state.BountyAdmission.MatchId))
                QueueBountyCompletion(new(state.AccountId, state.BountyAdmission, null, true));
        }
        if (!_accountSessions.Values.Any(other => !ReferenceEquals(other, state)
            && other.BountyAdmission.MatchId == state.BountyAdmission.MatchId && other.Match != MatchStep.Menu)
            && _formingBountyMatches.GetValueOrDefault(state.BountyWorldId)?.MatchId == state.BountyAdmission.MatchId)
            _formingBountyMatches.Remove(state.BountyWorldId);
        if (!_accountSessions.Values.Any(other => !ReferenceEquals(other, state)
            && other.BountyAdmission.MatchId == state.BountyAdmission.MatchId && other.Match != MatchStep.Menu))
        {
            _closedBountyMatches.Remove(state.BountyAdmission.MatchId);
            _startedBountyMatches.Remove(state.BountyAdmission.MatchId);
        }
        if (connection.State == ConnectionState.Open && state.BountyAdmission != MatchAdmissionContext.Unknown)
        {
            SendTunnel(connection, new MatchBountyCosts(0, 0, 0).WriteTo);
            SendTunnel(connection, new BountyLobbyState(false).WriteTo);
        }
    }

    private void DepartBounty(SoeConnection connection, GatewaySessionState state)
    {
        if (!state.Score.Settled && (state.Score.FinalPlacement is not null || state.Match is MatchStep.Dropping or MatchStep.InMatch
            || _startedBountyMatches.Contains(state.BountyAdmission.MatchId)))
            CompleteRankedScore(connection, state, state.BountyPlacement ?? BountyForfeitPlacement(state));
        if (state.BountyPlacement is { } placement)
            CompleteBountyResult(connection, state, placement);
        else if (state.Match is MatchStep.Dropping or MatchStep.InMatch
            || _startedBountyMatches.Contains(state.BountyAdmission.MatchId))
            CompleteBountyResult(connection, state, BountyForfeitPlacement(state));
        CancelBountyOnDeparture(connection, state);
    }

    private void QueueBountyCompletion(PendingBountyCompletion completion)
    {
        _pendingBountyCompletions.TryAdd((completion.AccountId, completion.Admission.MatchId), completion);
        RetryPendingBountyCompletions(completion.AccountId);
    }

    // This queue deliberately outlives a gateway link and a menu/admission reset. The first
    // authoritative outcome is immutable. Once recorded, its intent also survives host restart.
    private void RetryPendingBountyCompletions(string? accountId = null)
    {
        if (_bountyLedger is null) return;
        foreach (var pair in _pendingBountyCompletions.ToArray())
        {
            var pending = pair.Value;
            if (accountId is not null && pending.AccountId != accountId) continue;
            try
            {
                var recorded = pending.Placement is { } placement
                    ? _bountyLedger.RecordResult(pending.AccountId, pending.Admission, placement, _options.Bounty)
                    : _bountyLedger.RecordRefund(pending.AccountId, pending.Admission.MatchId, pending.BeforeStart);
                if (!recorded.Succeeded) { _log.Warn($"bounty completion intent pending: {recorded.Error}"); continue; }
                var saved = _bountyLedger.Read(pending.AccountId, pending.Admission.MatchId)!;
                var completed = pending.Placement is not null
                    ? _bountyLedger.Settle(pending.AccountId, pending.Admission, saved.Placement, _options.Bounty)
                    : _bountyLedger.Cancel(pending.AccountId, pending.Admission.MatchId);
                if (!completed.Succeeded) { _log.Warn($"bounty completion pending: {completed.Error}"); continue; }
                _pendingBountyCompletions.Remove(pair.Key);
                foreach (var state in _accountSessions.Values.Where(state => state.AccountId == pending.AccountId
                    && state.BountyAdmission.MatchId == pending.Admission.MatchId))
                    if (pending.Placement is not null) state.BountyResultSettled = true;
                PublishAccountEconomy(pending.AccountId, inventoryChanged: false);
            }
            catch (AccountEconomyStoreException exception)
            {
                _log.Warn($"bounty completion remains pending: {exception.Message}");
            }
        }
        if (_pendingBountyCompletions.Count == 0 || _bountyCompletionRetryArmed || Post is not { } post) return;
        _bountyCompletionRetryArmed = true;
        _ = Task.Delay(1000).ContinueWith(_ => post(() =>
        {
            _bountyCompletionRetryArmed = false;
            RetryPendingBountyCompletions();
        }), TaskScheduler.Default);
    }
}
