using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private PublicMatchQueue? _publicQueues;
    private bool _publicQueuePumpArmed;
    private readonly Dictionary<ulong, int> _publicQueuePrompted = [];
    private bool _cancellingPublicLobbyParty;
    public IReadOnlyList<PublicMatchSnapshot> PublicMatches => _publicQueues?.Snapshots ?? [];

    private PublicMatchQueue PublicQueues
    {
        get
        {
            if (_publicQueues is not null) return _publicQueues;
            _publicQueues = new(_options.PublicQueue!);
            _publicQueues.Transitioned += (id, phase) => _log.Info($"public match {id}: {phase}");
            return _publicQueues;
        }
    }

    private bool UsesPublicQueue(GatewaySessionState state) => _options.PublicQueue is not null
        && state.BountyAdmission.QueueKind == MatchQueueKind.Public;

    private void RunPublicQueue(SoeConnection connection, GatewaySessionState state)
    {
        SendTunnel(connection, new QueueUpdateGameMode((uint)Math.Max(1, PublicQueues.Position(state.Guid)),
            AdmittedGameMode(state)).WriteTo);
        ArmPublicQueuePump();
    }

    private void ArmPublicQueuePump()
    {
        if (_publicQueuePumpArmed || !(_publicQueues!.HasWaitingPlayers || _accountSessions.Any(p =>
            UsesPublicQueue(p.Value) && p.Value.Match is MatchStep.Queued or MatchStep.Transferring or MatchStep.Zoning or MatchStep.Lobby))) return;
        // The timer belongs to the queue, not its first connection, which can disconnect.
        _publicQueuePumpArmed = Later(null, 250, PumpPublicQueues);
    }

    private void PumpPublicQueues()
    {
        _publicQueuePumpArmed = false;
        long now = Environment.TickCount64;
        foreach (var round in PublicQueues.Snapshots.Where(r => r.Phase < PublicMatchPhase.ROSTER_FROZEN))
            PublicQueues.SetReadyPlayers(round.MatchId, ReadyPublicLobbyPlayers(round.MatchId), now);
        PublicQueues.Poll(now);
        // Queue time is now only an admission/availability prompt. The three-minute timer runs
        // in the physical pregame lobby after enough native clients have finished loading.
        foreach (var member in _accountSessions.Where(p => p.Key.State == ConnectionState.Open
            && p.Value.Match == MatchStep.Queued && UsesPublicQueue(p.Value)
            && PublicQueues.CanAccept(p.Value.Guid, p.Value.BountyAdmission.MatchId)).ToArray())
            PromptPublicAdmission(member.Key, member.Value);
        foreach (var round in PublicQueues.Snapshots)
        {
            if (round.Phase is >= PublicMatchPhase.MATCH_ALLOCATING and < PublicMatchPhase.ROSTER_FROZEN)
                PublishPublicLobbyCountdown(round.MatchId, round.CountdownDeadlineMs);
            else if (round.Phase is >= PublicMatchPhase.ROSTER_FROZEN and < PublicMatchPhase.ENDING)
            {
                PublishPublicLobbyCountdown(round.MatchId, round.CountdownDeadlineMs);
                StartReadyPublicPlayers(round.MatchId);
            }
        }
        ArmPublicQueuePump();
    }

    private void PromptPublicAdmission(SoeConnection connection, GatewaySessionState state)
    {
        if (state.AutoAcceptReplay)
        {
            if (AcceptPublicQueue(connection, state)) state.AutoAcceptReplay = false;
            return;
        }
        int generation = state.MatchAdmissionGeneration;
        if (_publicQueuePrompted.TryGetValue(state.Guid, out int prompted) && prompted == generation) return;
        _publicQueuePrompted[state.Guid] = generation;
        ulong matchId = state.BountyAdmission.MatchId;
        SendTunnel(connection, new QueueExit(_options.QueueExitSeconds, state.Guid).WriteTo);
        Later(connection, _options.PublicQueue!.AcceptTimeoutMs, () =>
        {
            if (state.Match != MatchStep.Queued || state.MatchAdmissionGeneration != generation
                || state.BountyAdmission.MatchId != matchId) return;
            SendTunnel(connection, new QueueExit(0, state.Guid, Cancel: true).WriteTo);
            AbandonMatch(connection, state, "public lobby acceptance expired");
        });
        // The native second EC is a one-shot after PLAY, not a repeatable QueueExit ack.
        Later(connection, checked((int)Math.Min(_options.QueueExitSeconds, 180u) * 1000), () =>
        {
            if (state.Match == MatchStep.Queued && state.MatchAdmissionGeneration == generation
                && state.BountyAdmission.MatchId == matchId) AcceptPublicQueue(connection, state);
        });
    }

    private void RefreshPublicLobby(ulong matchId)
    {
        long now = Environment.TickCount64;
        PublicQueues.SetReadyPlayers(matchId, ReadyPublicLobbyPlayers(matchId), now);
        PublicQueues.Poll(now);
        var round = PublicQueues.Snapshots.FirstOrDefault(r => r.MatchId == matchId);
        if (round is null) return;
        if (round.Phase < PublicMatchPhase.ENDING)
        {
            PublishPublicLobbyCountdown(matchId, round.CountdownDeadlineMs);
            if (round.Phase >= PublicMatchPhase.ROSTER_FROZEN) StartReadyPublicPlayers(matchId);
        }
        ArmPublicQueuePump();
    }

    private void StartReadyPublicPlayers(ulong matchId)
    {
        foreach (var player in _accountSessions.Where(p => p.Key.State == ConnectionState.Open
            && p.Value.BountyAdmission.MatchId == matchId && p.Value.Match == MatchStep.Lobby
            && p.Value.PregameClientReady && p.Value.PendingLogout is null).ToArray())
            BeginMatchDrop(player.Key, player.Value);
    }

    private bool AcceptPublicQueue(SoeConnection connection, GatewaySessionState state)
    {
        if (!PublicQueues.CanAccept(state.Guid, state.BountyAdmission.MatchId)) return false;
        var group = PublicQueues.GroupFor(state.Guid, state.BountyAdmission.MatchId);
        if (group.Count == 0 || group[0] != state.Guid) return false;
        var links = _accountSessions.Where(p => p.Key.State == ConnectionState.Open
            && p.Value.Match == MatchStep.Queued && p.Value.BountyAdmission == state.BountyAdmission
            && group.Contains(p.Value.Guid)).ToArray();
        if (links.Length != group.Count)
        {
            AbandonMatch(connection, state, "reserved party unavailable");
            return false;
        }
        // Mark every member before entering any one of them; no half-party admission is observable.
        foreach (var link in links) link.Value.Match = MatchStep.Transferring;
        foreach (var link in links)
        {
            ArmPublicLoadingDeadline(link.Key, link.Value);
            EnterMatch(link.Key, link.Value);
        }
        return true;
    }

    private void ArmPublicLoadingDeadline(SoeConnection connection, GatewaySessionState state)
    {
        int generation = state.MatchAdmissionGeneration;
        ulong matchId = state.BountyAdmission.MatchId;
        int timeoutMs = _options.PublicQueue!.LoadTimeoutMs;
        int pollMs = Math.Clamp(timeoutMs, 1, 1000);
        long worldDeadlineMs = Environment.TickCount64 + timeoutMs;
        long? dropDeadlineMs = null;

        void Poll()
        {
            if (!ReferenceEquals(connection.Tag, state) || state.MatchAdmissionGeneration != generation
                || state.BountyAdmission.MatchId != matchId) return;
            long now = Environment.TickCount64;
            bool expired;
            switch (state.Match)
            {
                case MatchStep.Transferring:
                case MatchStep.Zoning:
                    expired = now >= worldDeadlineMs;
                    break;
                case MatchStep.Lobby:
                    // A legitimate pregame countdown may exceed the loading timeout. Retire
                    // only a lobby that also missed its established drop deadline by that margin.
                    expired = _sharedLootMatches.TryGetValue(matchId, out var match)
                        && match.LobbyDeadlineMs is long lobbyDeadline && now >= lobbyDeadline + timeoutMs;
                    break;
                case MatchStep.Dropping when !state.Released:
                    // The transport can stay alive forever without e8/02. The progress watchdog
                    // reports that stall but does not release the participant's reserved slot.
                    dropDeadlineMs ??= now + timeoutMs;
                    expired = now >= dropDeadlineMs;
                    break;
                default:
                    // ReleaseTeleport enters InMatch before descent/landing. An accepted, active
                    // canopy is therefore never timed out as pending loading by this monitor.
                    return;
            }
            if (expired)
            {
                _log.Warn($"{connection} public match loading expired ({state.Match}); returning this reserved player to menu");
                CompleteLogout(connection, state);
                return;
            }
            Later(connection, pollMs, Poll);
        }

        Later(connection, pollMs, Poll);
    }

    private void NotePublicMatchPhase(GatewaySessionState state, PublicMatchPhase phase)
    {
        if (UsesPublicQueue(state)) PublicQueues.Advance(state.BountyAdmission.MatchId, phase);
    }

    private void LeavePublicQueue(GatewaySessionState state)
    {
        if (_publicQueues is null) return;
        ulong matchId = state.BountyAdmission.MatchId;
        var round = _publicQueues.Snapshots.FirstOrDefault(r => r.MatchId == matchId);
        var cancelledGroup = round is { Phase: < PublicMatchPhase.ROSTER_FROZEN }
            ? _publicQueues.GroupFor(state.Guid, matchId) : Array.Empty<ulong>();
        _publicQueues.Leave(state.Guid);
        _publicQueuePrompted.Remove(state.Guid);
        // Before freezing, losing any party member withdraws the complete reservation,
        // including members already in the physical lobby. Do not leave orphaned actors.
        if (!_cancellingPublicLobbyParty && cancelledGroup.Count > 1)
        {
            _cancellingPublicLobbyParty = true;
            try
            {
                foreach (var peer in _accountSessions.Where(p => !ReferenceEquals(p.Value, state)
                    && cancelledGroup.Contains(p.Value.Guid) && p.Value.BountyAdmission.MatchId == matchId
                    && p.Value.Match is MatchStep.Queued or MatchStep.Transferring or MatchStep.Zoning or MatchStep.Lobby).ToArray())
                    CompleteLogout(peer.Key, peer.Value);
            }
            finally { _cancellingPublicLobbyParty = false; }
        }
        ArmPublicQueuePump();
    }
}
