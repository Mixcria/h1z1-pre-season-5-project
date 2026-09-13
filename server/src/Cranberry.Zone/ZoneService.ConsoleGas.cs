using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private ConsoleReply ConsoleControlGas(SoeConnection connection, GatewaySessionState state, string verb)
    {
        if (state.Gas is not { Running: true } gas)
            return ConsoleReply.Failed("gas is stopped; /gas start opens it");
        if (verb == "pause" && gas.Paused)
            return ConsoleReply.Plain(["* gas already paused; /gas resume continues from this circle"]);
        if (verb == "resume" && !gas.Paused)
            return ConsoleReply.Plain(["* gas is already running; /gas pause freezes it"]);
        if (verb == "next" && gas.Paused)
            return ConsoleReply.Failed("gas is paused; /gas resume before /gas next");
        long now = Environment.TickCount64;
        if (verb == "next" && gas.Schedule!.NextEventAtMs(gas.MatchClockAt(now)) == long.MaxValue)
            return ConsoleReply.Failed("final circle reached; no next gas event");

        // Sessions have individual damage controllers but share their plan and clock. Update
        // every member on the listener thread, and the join template for future members too.
        var members = new List<(SoeConnection Connection, GatewaySessionState State)> { (connection, state) };
        if (_sharedGasMembership.TryGetValue(state, out ulong matchId))
            members.AddRange(_throwableSessions.Values.Where(s => !ReferenceEquals(s.State, state)
                && s.Connection.State == ConnectionState.Open && s.State.Gas is { Running: true }
                && _sharedGasMembership.TryGetValue(s.State, out ulong otherId) && otherId == matchId));

        foreach (var member in members)
        {
            var controller = member.State.Gas!;
            switch (verb)
            {
                case "pause": controller.Pause(now); break;
                case "resume": controller.Resume(now); break;
                case "next": controller.AdvanceToNextEvent(now); break;
            }
            long clock = controller.MatchClockAt(now);
            if (!controller.Paused) ApplyGasTick(member.Connection, member.State, controller.Tick(now, []), clock);
            if (controller.Schedule!.IsRingVisibleAt(clock))
                SendTunnel(member.Connection, w => GasPackets.WriteRing(w, _options.Gas, controller.ActiveCircleAt(now), advancing: true));
            member.State.GasHudHealAtMs = 0;
            member.State.GasSafeZoneHealAtMs = 0;
            HealGasHud(member.Connection, member.State, controller, clock, now);
            SendTunnel(member.Connection, w => GasAlerts.Write(w, verb switch
            {
                "pause" => "Gas paused by the owner. The ring and gas damage are frozen.",
                "resume" => "Gas resumed by the owner.",
                _ => "Gas advanced to its next event by the owner.",
            }));
        }
        if (_sharedGasMembership.TryGetValue(state, out ulong id) && _sharedGasMatches.TryGetValue(id, out var plan))
            plan.SynchronizeClock(gas.StartMs, gas.PausedAtMs);
        return ConsoleReply.Did($"gas {(verb == "pause" ? "paused" : verb == "resume" ? "resumed" : "advanced")} at {Clock(gas.MatchClockAt(now))} for {members.Count} player(s){(verb == "pause" ? "; /gas resume continues" : "")}");
    }
}
