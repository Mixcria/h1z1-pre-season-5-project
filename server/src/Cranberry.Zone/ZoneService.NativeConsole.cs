using Cranberry.Transport;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Original client commands have dedicated packets. They must not be registered
    // as AddWorldCommand aliases: the client's native registry already owns them.
    private bool TryHandleNativeConsoleCommand(
        SoeConnection connection, GatewaySessionState state, byte[] payload)
    {
        if (DevConsoleEngine is null) return false;
        try
        {
            return TryHandleNativeVehicleCommand(connection, state, payload)
                || TryHandleNativeItemCommand(connection, state, payload)
                || TryHandleNativeGotoCommand(connection, state, payload)
                || TryHandleNativeRunCommand(connection, state, payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NativeConsoleReply(connection, state,
                ConsoleReply.Failed($"native command failed ({ex.GetType().Name}); see the host log"));
            _log.Warn($"{connection} native console command failed: {ex}");
            return true;
        }
    }

    private bool TryAuthorizeNativeConsole(
        SoeConnection connection, GatewaySessionState state, string name,
        ConsoleTier minimum = ConsoleTier.Tester, MatchGate gate = MatchGate.InMatch)
    {
        if (DevConsoleEngine is null) return false;
        ConsoleContext context = BuildConsoleContext(connection, state);
        if (!ConsoleAccessPolicy.HasDevelopmentAccess(state.DevConsole.Tier)) return false;
        if (state.DevConsole.Tier < minimum)
        {
            context.Surface.Line($"! {minimum} or above required for /{name} (you are {state.DevConsole.Tier})");
            _log.Info($"{connection} native console: REFUSED /{name}, tier={state.DevConsole.Tier}");
            return false;
        }
        if (!MatchGates.Allows(gate, context.Step()))
        {
            context.Surface.Line($"! {MatchGates.Refusal(gate, context.Step())}");
            return false;
        }
        return true;
    }

    private void NativeConsoleReply(
        SoeConnection connection, GatewaySessionState state, ConsoleReply reply)
    {
        if (DevConsoleEngine is null) return;
        ConsoleContext context = BuildConsoleContext(connection, state);
        if (!ConsoleAccessPolicy.HasDevelopmentAccess(state.DevConsole.Tier)) return;
        foreach (string line in reply.Lines)
            context.Surface.Line(ConsoleReply.NonEmpty(line));
    }
}
