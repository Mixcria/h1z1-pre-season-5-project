using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private bool TryHandleNativeRunCommand(SoeConnection connection, GatewaySessionState state, byte[] payload)
    {
        if (!NativeRunCommand.Matches(payload)) return false;
        if (!TryAuthorizeNativeConsole(connection, state, "run")) return true;
        float speed;
        try { speed = NativeRunCommand.Parse(payload); }
        catch (PacketFormatException ex)
        {
            NativeConsoleReply(connection, state, ConsoleReply.Failed(ex.Message));
            return true;
        }

        // The original acknowledgement toggles an identical override off
        // (FUN_14129ad10, case 0x4c6). Mirror that transition in the server profile.
        bool reset = speed == 0f || speed == state.DevConsole.NativeRunMetresPerSecond;
        state.DevConsole.SpeedMultiplier = 1f;
        state.DevConsole.NativeRunMetresPerSecond = reset ? null : speed;
        PublishConsoleMovement(connection, state);
        // Echo the requested value so the client performs the same native toggle.
        SendTunnel(connection, writer => NativeRunCommand.WriteReply(writer, speed));
        NativeConsoleReply(connection, state, reset
            ? ConsoleReply.Did("run speed restored to normal")
            : ConsoleReply.Did(FormattableString.Invariant($"run speed {speed:0.###} m/s; /run default restores normal")));
        _log.Info(FormattableString.Invariant($"{connection} native console: /run {speed} m/s; override={(reset ? "default" : speed.ToString(System.Globalization.CultureInfo.InvariantCulture))}"));
        return true;
    }
}
