using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Movement;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Owner request, 2026-09-06. UI.ExitMatch in August CodeStringMappings.txt is 14991.
    private const int LogoutDurationMilliseconds = 10_000;
    private const uint ExitMatchStringId = 14991;

    private sealed record PendingLogout(int WorldGeneration, StationaryCastPosition Position, bool ClientManaged);

    private void StartLogout(SoeConnection connection, GatewaySessionState state, bool clientManaged = false)
    {
        if (state.LogoutCompleted) return;
        if (state.LogoutPrepared)
        {
            if (!clientManaged) CompleteLogout(connection, state);
            return;
        }
        // Repeated clicks cannot reset the deadline or schedule a second completion.
        if (state.PendingLogout is not null) return;

        if (state.Match == MatchStep.Ended
            || state.Match == MatchStep.InMatch && (state.DeathSent || state.VictorySent)
            || state.Match is not (MatchStep.Lobby or MatchStep.Dropping or MatchStep.InMatch))
        {
            FinishLogoutCountdown(connection, state, clientManaged);
            return;
        }

        var pending = new PendingLogout(state.WorldGeneration,
            new StationaryCastPosition(MedicalCastPosition(state), MedicalModel.CastCancelRadiusUnits), clientManaged);
        state.PendingLogout = pending;
        if (!Later(connection, LogoutDurationMilliseconds, () => CompletePendingLogout(connection, state, pending)))
        {
            state.PendingLogout = null;
            return;
        }

        CancelMedicalCast(connection, state, "exit match requested");
        CancelVehicleComponentRemoval(connection, state);
        state.InteractionGeneration++;
        state.CraftBusyUntil = 0;
        state.ShredBusyUntil = 0;
        // Use the same native cast bar as bandages, with no medical character animation.
        SendTunnel(connection, new InteractionStart(state.Guid, LogoutDurationMilliseconds,
            ExitMatchStringId, AnimationId: 0).WriteTo);
        _log.Info($"{connection} logout: EXIT MATCH countdown started, {LogoutDurationMilliseconds} ms; player remains in world");
    }

    private void CompletePendingLogout(SoeConnection connection, GatewaySessionState state, PendingLogout pending)
    {
        if (connection.State != ConnectionState.Open || !ReferenceEquals(connection.Tag, state)
            || !ReferenceEquals(state.PendingLogout, pending)) return;
        if (state.WorldGeneration != pending.WorldGeneration)
        {
            state.PendingLogout = null;
            return;
        }
        CancelLogoutIfMoved(connection, state, MedicalCastPosition(state));
        if (!ReferenceEquals(state.PendingLogout, pending)) return;
        state.PendingLogout = null;

        SendTunnel(connection, new InteractionStop(state.Guid).WriteTo);
        FinishLogoutCountdown(connection, state, pending.ClientManaged);
    }

    private void CancelLogoutIfMoved(SoeConnection connection, GatewaySessionState state, Vector3? position)
    {
        if (state.PendingLogout is not { } pending) return;
        if (pending.WorldGeneration != state.WorldGeneration)
        {
            state.PendingLogout = null;
            return;
        }
        if (pending.Position.HasMoved(position)) CancelLogout(connection, state, "player moved");
    }

    private void CancelLogout(SoeConnection connection, GatewaySessionState state, string reason)
    {
        if (state.PendingLogout is null) return;
        state.PendingLogout = null;
        SendTunnel(connection, new InteractionStop(state.Guid).WriteTo);
        _log.Info($"{connection} logout: countdown cancelled - {reason}");
    }

    private void FinishLogoutCountdown(SoeConnection connection, GatewaySessionState state, bool clientManaged)
    {
        if (!clientManaged)
        {
            CompleteLogout(connection, state);
            return;
        }
        // The UI starts native Logout only after the cancellable action has finished.
        // Native Logout changes global login state and must never be armed while the
        // player can cancel and continue using the old world. Its ensuing 09/4e is immediate.
        state.LogoutPrepared = true;
        SendTunnel(connection, new ConsolePrint("@cranberry/match-exit/1;logout").WriteTo);
    }

    private void CompleteLogout(SoeConnection connection, GatewaySessionState state)
    {
        if (state.LogoutCompleted) return;
        AbandonMatch(connection, state, "Command.StartLogoutRequest");
        state.LogoutCompleted = true;
        // Keep the link open for c3/c4. The client's in-world Logout() automatically logs
        // back into LoginZone after this exchange; closing transport here interrupts that flow.
        SendTunnel(connection, new CompleteLogoutProcess().WriteTo);
        _log.Info($"{connection} zone Command.StartLogoutRequest → CompleteLogoutProcess");
    }

    private bool RefuseInteractionDuringLogout(SoeConnection connection, GatewaySessionState state)
    {
        if (state.PendingLogout is null && !state.LogoutPrepared) return false;
        SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.ContainerInUse).WriteTo);
        return true;
    }
}
