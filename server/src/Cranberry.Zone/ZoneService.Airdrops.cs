using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private sealed class SharedAirdropMatch(MatchAirdrops controller, long startedAtMs)
    {
        public MatchAirdrops Controller { get; } = controller;
        public long StartedAtMs { get; } = startedAtMs;
        public List<AirdropEvent> Events { get; } = [];
        public HashSet<GatewaySessionState> Members { get; } = [];
        public bool Faulted { get; set; }
    }

    private sealed class AirdropViewer(SharedAirdropMatch match, long joinedAtMs)
    {
        public SharedAirdropMatch Match { get; } = match;
        public long JoinedAtMs { get; } = joinedAtMs;
        public int Cursor { get; set; }
        public HashSet<int> DeliveredFlights { get; } = [];
    }

    private readonly Dictionary<(ulong MatchId, ulong Seed, long Start), SharedAirdropMatch> _airdropMatches = [];
    private readonly Dictionary<GatewaySessionState, AirdropViewer> _airdropViewers = [];
    private uint _nextAirdropDisplayId = 1;
    private static readonly Lazy<AirdropTerrain> AirdropTerrainData = new(AirdropTerrain.LoadDefault);

    private void StartAirdrops(GatewaySessionState state, ulong seed, long startedAtMs)
    {
        LeaveAirdrops(state);
        state.AirdropCrates.Clear();
        state.Airdrops = null;
        if (!_options.Airdrop.Enabled) return;
        var key = (state.BountyAdmission.MatchId, seed, startedAtMs);
        if (!_airdropMatches.TryGetValue(key, out var match))
        {
            match = new(new MatchAirdrops(_options.Airdrop, seed), startedAtMs);
            _airdropMatches.Add(key, match);
        }
        match.Members.Add(state);
        _airdropViewers.Add(state, new(match, Math.Max(0, Environment.TickCount64 - startedAtMs)));
        state.Airdrops = match.Controller;
    }

    private void LeaveAirdrops(GatewaySessionState state)
    {
        state.Airdrops = null;
        if (!_airdropViewers.Remove(state, out var viewer)) return;
        viewer.Match.Members.Remove(state);
        if (viewer.Match.Members.Count != 0) return;
        foreach (var pair in _airdropMatches)
        {
            if (!ReferenceEquals(pair.Value, viewer.Match)) continue;
            _airdropMatches.Remove(pair.Key);
            break;
        }
    }

    private void PumpAirdrops(SoeConnection connection, GatewaySessionState state,
        GasController gas, long gasClockMs, long nowMs)
    {
        PumpAirdropFlights(connection, state, gas, gasClockMs, nowMs);
        // A future flight's data failure cannot suspend already-landed shared loot or its marker.
        SyncAirdropCrateViews(connection, state, nowMs);
        SyncAirdropMarkers(state);
    }

    private void PumpAirdropFlights(SoeConnection connection, GatewaySessionState state,
        GasController gas, long gasClockMs, long nowMs)
    {
        if (!_airdropViewers.TryGetValue(state, out var viewer) || gas.Schedule is not { } schedule) return;
        SharedAirdropMatch match = viewer.Match;
        if (match.Faulted) return;
        MatchAirdrops airdrops = match.Controller;
        // Flights and native activation timestamps share real elapsed time. The developer's
        // gas-next command can advance the gas independently without fast-forwarding a plane
        // whose immutable route has already been delivered to clients.
        long matchClockMs = Math.Max(0, nowMs - match.StartedAtMs);
        GasCircle? circle = schedule.PhaseAt(gasClockMs)?.Target;
        Vector3 fallback = schedule.InitialCircle.Centre;
        // One scheduler per shared gas plan. Session pump offsets cannot reroll a landing point
        // or produce different bomb decisions on either side of a survivor-count change.
        int alive = match.Members.Count(member => !member.DeathSent && member.Hitpoints > 0
            && member.Gas is { Running: true });
        try
        {
            airdrops.Tick(matchClockMs, alive, circle, fallback, match.Events, AirdropGroundHeight,
                at => schedule.PhaseAt(gas.MatchClockAt(match.StartedAtMs + at))?.Target);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            // A damaged deployment must not stop the gas pump or fabricate a landing at Y=0.
            match.Faulted = true;
            _log.Warn($"{connection} airdrop: disabled for this match because terrain/flight data failed: {ex.Message}");
            return;
        }
        foreach (AirdropFlight flight in airdrops.Flights)
        {
            if (viewer.DeliveredFlights.Contains(flight.Index)) continue;
            IReadOnlyList<AirdropDisplaySegment> segments =
                AirdropDisplayPlan.Create(flight, _options.Airdrop, match.StartedAtMs, matchClockMs);
            viewer.DeliveredFlights.Add(flight.Index);
            if (segments.Count == 0) continue;
            byte[] packet = AirdropPackets.DeliveryDisplayInfo(_nextAirdropDisplayId++, segments);
            SendTunnel(connection, writer => writer.WriteRaw(packet));
            _log.Info($"{connection} airdrop: {flight.Kind} flight {flight.Index}, native 09 50 "
                + $"with {segments.Count} actor rails through {FormatPosition(flight.Centre)}");
        }
        while (viewer.Cursor < match.Events.Count)
        {
            AirdropEvent drop = match.Events[viewer.Cursor++];
            switch (drop.Kind)
            {
                case AirdropEventKind.Landed:
                    if (AirdropCrateExpired(match.StartedAtMs + drop.MatchClockMs, nowMs))
                    {
                        ExpireAirdropCrate(state, SharedCrateKey(drop.Index, drop.PayloadIndex));
                        break;
                    }
                    SpawnAirdropCrate(connection, state, airdrops, drop,
                        match.StartedAtMs + drop.MatchClockMs);
                    break;
                case AirdropEventKind.BombExploded when drop.MatchClockMs >= viewer.JoinedAtMs:
                    ApplyAirdropBomb(connection, state, drop.Position);
                    break;
            }
        }
    }

    private static float AirdropGroundHeight(float x, float z) => AirdropTerrainData.Value.HeightAt(x, z);

    private void ApplyAirdropBomb(SoeConnection connection, GatewaySessionState state, Vector3 impact)
    {
        if (state.Match != MatchStep.InMatch || state.DeathSent) return;
        if (!state.DevConsole.Invulnerable && EnvironmentalDamagePosition(state) is { } playerPosition)
        {
            int damage = AirdropBombDamage.At(_options.Airdrop, impact, playerPosition);
            if (damage > 0) ApplyDamage(connection, state, (uint)damage, DamageCause.BombingRun);
        }
        if (state.Fleet is VehicleFleet fleet)
        {
            foreach (MatchVehicle vehicle in fleet.Vehicles)
            {
                int damage = AirdropBombDamage.At(_options.Airdrop, impact, vehicle.Position, vehicle: true);
                if (damage > 0) ApplyVehicleDamage(connection, state, vehicle, (uint)damage, "airdrop bomb");
            }
        }
    }
}
