using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private sealed record ClientAdmissionWait(PlayerWorldTransferRequest Request, long ExpiresAtMs);

    private void BeginClientAdmission(SoeConnection connection, GatewaySessionState state, PlayerWorldTransferRequest request)
    {
        if (state.LogoutPrepared || state.LogoutCompleted || state.PendingLogout is not null) return;
        if (state.PendingClientAdmission?.Request == request) return;
        state.PendingClientAdmission = null;
        if (DoorSwingClientReady is { } ready && !ready(state.AccountId))
        {
            var pending = new ClientAdmissionWait(request, Environment.TickCount64 + 60_000);
            state.PendingClientAdmission = pending;
            _log.Info($"{connection} match: waiting for client initialization before admitting world {request.WorldId}");
            PumpClientAdmission(connection, state, pending, Environment.TickCount64);
            return;
        }
        CompleteClientAdmission(connection, state, request);
    }

    private void PumpClientAdmission(SoeConnection connection, GatewaySessionState state, ClientAdmissionWait pending, long now)
    {
        if (!ReferenceEquals(state.PendingClientAdmission, pending)) return;
        if (!ReferenceEquals(connection.Tag, state) || connection.State != ConnectionState.Open
            || state.Match != MatchStep.Menu || !state.Authenticated
            || state.LogoutPrepared || state.LogoutCompleted || state.PendingLogout is not null)
        {
            state.PendingClientAdmission = null;
            return;
        }
        if (DoorSwingClientReady is not { } ready || ready(state.AccountId))
        {
            state.PendingClientAdmission = null;
            _log.Info($"{connection} match: client initialized; continuing requested world {pending.Request.WorldId}");
            CompleteClientAdmission(connection, state, pending.Request);
            return;
        }
        if (now < pending.ExpiresAtMs && Later(connection, 250,
                () => PumpClientAdmission(connection, state, pending, Environment.TickCount64))) return;

        state.PendingClientAdmission = null;
        _log.Warn($"{connection} match: client initialization did not complete; no match was reserved");
        SendPartyNotice(connection, "The launcher could not finish initializing the game. Close the game and reopen the launcher.");
        SendTunnel(connection, new PlayerWorldTransferReply(pending.Request.WorldId, Result: 1).WriteTo);
    }

    private void CompleteClientAdmission(SoeConnection connection, GatewaySessionState state, PlayerWorldTransferRequest request)
    {
        if (state.CrateOpening is not null) EndCrateOpening(connection, state);
        if (TryQueuePartyMatch(connection, state, request)) return;
        if (!PrepareMatchAdmission(state, request))
        {
            RefuseHostedAdmission(connection, request.WorldId);
            return;
        }
        state.Match = MatchStep.Queued;
        RunQueue(connection, state);
    }
}
