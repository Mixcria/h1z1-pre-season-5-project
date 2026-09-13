using System.Numerics;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Loot;

public enum AirdropEventKind
{
    Inbound,
    Landed,
    PlaneSpawned,
    PlaneDeparted,
    CrateReleased,
    BombReleased,
    BombExploded,
}

/// <summary>One transition, with its scheduled match-clock time and stable flight/payload identity.</summary>
public readonly record struct AirdropEvent(
    AirdropEventKind Kind, int Index, Vector3 Position, long MatchClockMs)
{
    public int FlightIndex { get; init; } = -1;
    public int PayloadIndex { get; init; }
}

public sealed record AirdropCrateState(
    int Index, Vector3 Position, long UnlockAtMs, IReadOnlyList<AirdropItem> Contents);

/// <summary>
/// Deterministic supply and bomber flights. This owns no session or packets and must be advanced
/// once per shared match. Flight plans remain available for clients joining an ongoing flight.
/// The June 2017 top-20 rule applies to bombs, not supply crates. Already launched bombers finish.
/// Flight tuning is a reconstruction; docs/airdrop-retail-20260906.md records the evidence boundary.
/// </summary>
public sealed class MatchAirdrops
{
    private const ulong SeedSalt = 0x41_49_52_44_52_4F_50UL;
    private readonly AirdropOptions _options;
    private readonly ulong _matchSeed;
    private readonly List<AirdropFlight> _flights = [];
    private IReadOnlyList<AirdropFlight> _flightSnapshot = Array.Empty<AirdropFlight>();
    private readonly List<AirdropEvent> _pending = [];
    private int _bombAttempt;
    private long _lastClockMs = -1;

    public MatchAirdrops(AirdropOptions options, ulong matchSeed)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(MatchAirdrops));
        _options = options;
        _matchSeed = matchSeed;
        NextDropAtMs = options.FirstDropAtMs;
    }

    public AirdropOptions Options => _options;
    public long NextDropAtMs { get; private set; }
    public int Announced { get; private set; }
    public int Landed { get; private set; }
    public int BombRuns { get; private set; }
    public int BombsExploded { get; private set; }
    public int InFlight => Announced - Landed;
    public int PeakAlive { get; private set; }
    /// <summary>Immutable snapshot, including completed flights (bounded by the match drop cap).</summary>
    public IReadOnlyList<AirdropFlight> Flights => _flightSnapshot;

    public bool Exhausted(int playersAlive) =>
        Announced >= _options.MaxDropsPerMatch || playersAlive < _options.MinPlayersAlive;

    public bool BombsAllowed(int playersAlive) => _options.BombsEnabled
        && _options.BombsPerRun > 0 && playersAlive > _options.StopAtPlayersAlive
        && playersAlive >= _options.MinPlayersAlive;

    /// <summary>
    /// Appends every due transition in chronological order, once only, even after a delayed tick.
    /// Ground height is resolved once at scheduling and retained in each trajectory. Without a
    /// resolver the supplied circle/fallback Y is used. Backwards/repeated clocks produce no replay.
    /// </summary>
    public int Tick(long matchClockMs, int playersAlive, GasCircle? circle,
        in Vector3 fallbackCentre, List<AirdropEvent> into,
        Func<float, float, float>? groundHeight = null,
        Func<long, GasCircle?>? circleAtClock = null,
        Func<float, float, bool>? landingAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!_options.Enabled || matchClockMs < 0 || matchClockMs <= _lastClockMs) return 0;
        _lastClockMs = matchClockMs;
        PeakAlive = Math.Max(PeakAlive, playersAlive);
        while (matchClockMs >= NextDropAtMs && !Exhausted(playersAlive))
        {
            ScheduleFlight(Announced, AirdropFlightKind.Supply, NextDropAtMs,
                circleAtClock is null ? circle : circleAtClock(NextDropAtMs), fallbackCentre, groundHeight, landingAllowed);
            Announced++;
            NextDropAtMs = checked(NextDropAtMs + _options.DropIntervalMs);
        }
        // Each due bomber opportunity is consumed, even below 20, so a later count cannot backfill
        // suppressed bombers. Bomb opportunities do not reduce the number of supply crates.
        while (_bombAttempt < _options.MaxDropsPerMatch)
        {
            long due = checked(_options.FirstDropAtMs + _options.BombRunDelayMs
                + (_options.DropIntervalMs * _bombAttempt));
            if (due > matchClockMs) break;
            int attempt = _bombAttempt++;
            if (BombsAllowed(playersAlive)
                && new GasRandom(SeedFor(attempt, 100)).NextDouble() < _options.BombRunChance)
            {
                ScheduleFlight(attempt, AirdropFlightKind.Bomber, due,
                    circleAtClock is null ? circle : circleAtClock(due), fallbackCentre, groundHeight, landingAllowed);
                BombRuns++;
            }
        }
        _pending.Sort(CompareEvents);
        int count = 0;
        while (count < _pending.Count && _pending[count].MatchClockMs <= matchClockMs)
        {
            AirdropEvent e = _pending[count++];
            into.Add(e);
            if (e.Kind == AirdropEventKind.Landed) Landed++;
            if (e.Kind == AirdropEventKind.BombExploded) BombsExploded++;
        }
        if (count > 0) _pending.RemoveRange(0, count);
        return count;
    }

    private static int CompareEvents(AirdropEvent a, AirdropEvent b)
    {
        int comparison = a.MatchClockMs.CompareTo(b.MatchClockMs);
        if (comparison != 0) return comparison;
        comparison = a.FlightIndex.CompareTo(b.FlightIndex);
        if (comparison != 0) return comparison;
        // Spawn precedes release, which precedes impact, even with an instant-descent test setting.
        static int Order(AirdropEventKind kind) => kind switch
        {
            AirdropEventKind.Inbound => 0, AirdropEventKind.PlaneSpawned => 1,
            AirdropEventKind.CrateReleased or AirdropEventKind.BombReleased => 2,
            AirdropEventKind.Landed or AirdropEventKind.BombExploded => 3, _ => 4,
        };
        comparison = Order(a.Kind).CompareTo(Order(b.Kind));
        return comparison != 0 ? comparison : a.PayloadIndex.CompareTo(b.PayloadIndex);
    }

    private void ScheduleFlight(int drop, AirdropFlightKind kind, long spawnAt,
        GasCircle? circle, in Vector3 fallback, Func<float, float, float>? groundHeight,
        Func<float, float, bool>? landingAllowed)
    {
        bool bomb = kind == AirdropFlightKind.Bomber;
        int flightIndex = checked((drop * 2) + (bomb ? 1 : 0));
        Vector3 centre = circle?.Centre ?? fallback;
        float radius = (float)(circle?.Radius ?? 0);
        if (!float.IsFinite(radius) || radius < 0 || !Finite(centre))
            throw new ArgumentException("Airdrop safe-zone geometry must be finite and nonnegative.");
        var random = new GasRandom(SeedFor(drop, bomb ? 101u : 0u));
        Vector3 direction = Vector3.UnitX;
        float maxOffset = radius * (float)_options.SafeZoneFraction;
        int payloadCount = bomb ? _options.BombsPerRun : 1;
        var impacts = new Vector3[payloadCount];
        bool Allowed(Vector3 p) => Math.Abs(p.X) < _options.PayloadMapHalfExtentMetres
            && Math.Abs(p.Z) < _options.PayloadMapHalfExtentMetres
            && (landingAllowed?.Invoke(p.X, p.Z) ?? true);
        bool fitted = false;
        for (int attempt = 0; attempt < 256 && !fitted; attempt++)
        {
            double angle = random.NextDouble() * Math.Tau;
            direction = new Vector3((float)Math.Cos(angle), 0, (float)Math.Sin(angle));
            // Bombers retain their spread along the flight route. Supply payloads use
            // its exact centre, with terrain height resolved at that same coordinate.
            float offset = bomb ? (float)((random.NextDouble() * 2 - 1) * maxOffset * 0.5)
                : 0f; // Supply crates land at the revealed safe-zone centre.
            float spacing = payloadCount <= 1 ? 0 : Math.Min(_options.BombSpacingMetres,
                (2 * (maxOffset - Math.Abs(offset))) / (payloadCount - 1));
            fitted = true;
            for (int i = 0; i < payloadCount; i++)
            {
                impacts[i] = centre + direction * (offset + (i - ((payloadCount - 1) * 0.5f)) * spacing);
                fitted &= Allowed(impacts[i]);
            }
        }
        if (!fitted)
        {
            if (!Allowed(centre))
                throw new InvalidDataException("Airdrop circle has no supported landing point in the playable terrain.");
            Array.Fill(impacts, centre);
        }
        float altitude = _options.PlaneAltitudeMetres;
        float Ground(float x, float z)
        {
            float height = groundHeight?.Invoke(x, z) ?? centre.Y;
            if (!float.IsFinite(height)) throw new InvalidDataException("Nonfinite airdrop ground height.");
            return height;
        }
        for (int i = 0; i < payloadCount; i++)
        {
            Vector3 p = impacts[i];
            p.Y = Ground(p.X, p.Z);
            impacts[i] = p;
            altitude = Math.Max(altitude, p.Y + _options.MinimumTerrainClearanceMetres);
        }
        float reach = radius + _options.FlightMarginMetres;
        // Keep the route over terrain; the resolver is also used at every actual impact coordinate.
        for (int i = 0; i <= 32; i++)
        {
            Vector3 p = centre + direction * (reach * ((i / 16f) - 1));
            altitude = Math.Max(altitude, Ground(p.X, p.Z) + _options.MinimumTerrainClearanceMetres);
        }
        if (bomb)
        {
            float longestFall = impacts.Max(p => MathF.Sqrt(2 * (altitude - p.Y)
                / _options.BombGravityMetresPerSecondSquared));
            reach += longestFall * _options.PlaneSpeedMetresPerSecond;
        }
        Vector3 start = centre - direction * reach;
        Vector3 end = centre + direction * reach;
        start.Y = end.Y = altitude;
        long duration = Math.Max(1, (long)Math.Ceiling(2 * (double)reach / _options.PlaneSpeedMetresPerSecond * 1000));
        long departAt = checked(spawnAt + duration);
        float actualSpeed = 2 * reach * 1000f / duration;
        var payloads = new AirdropPayload[payloadCount];
        for (int i = 0; i < payloadCount; i++)
        {
            Vector3 release = impacts[i];
            long fallMs = bomb ? Math.Max(1, (long)Math.Round(Math.Sqrt(2 * (altitude - release.Y)
                / _options.BombGravityMetresPerSecondSquared) * 1000)) : _options.DescentMs;
            if (bomb) release -= direction * (actualSpeed * fallMs / 1000f);
            release.Y = altitude;
            float distance = Vector3.Dot(release - start, direction);
            long releaseAt = checked(spawnAt + (long)Math.Round(distance / actualSpeed * 1000));
            // Derive the release point from the same route/time used by visual snapshots.
            release = Vector3.Lerp(start, end, (float)((double)(releaseAt - spawnAt) / duration));
            if (!bomb) release = new Vector3(impacts[i].X, altitude, impacts[i].Z);
            payloads[i] = new AirdropPayload(i, releaseAt, checked(releaseAt + fallMs), release, impacts[i], bomb);
        }
        end.Y += _options.DepartureClimbMetres;
        var flight = new AirdropFlight(flightIndex, drop, kind, spawnAt, departAt,
            start, end, centre, Array.AsReadOnly(payloads))
        { ClimbStartAtMs = payloads.Max(p => p.ReleaseAtMs) };
        _flights.Add(flight);
        _flightSnapshot = Array.AsReadOnly(_flights.OrderBy(f => f.Index).ToArray());
        void Queue(AirdropEventKind eventKind, Vector3 position, long due, int payload = 0) =>
            _pending.Add(new AirdropEvent(eventKind, drop, position, due)
            { FlightIndex = flightIndex, PayloadIndex = payload });
        if (!bomb) Queue(AirdropEventKind.Inbound, impacts[0], spawnAt);
        Queue(AirdropEventKind.PlaneSpawned, start, spawnAt);
        foreach (AirdropPayload payload in payloads)
        {
            Queue(bomb ? AirdropEventKind.BombReleased : AirdropEventKind.CrateReleased,
                payload.ReleasePosition, payload.ReleaseAtMs, payload.Index);
            Queue(bomb ? AirdropEventKind.BombExploded : AirdropEventKind.Landed,
                payload.ImpactPosition, payload.ImpactAtMs, payload.Index);
        }
        Queue(AirdropEventKind.PlaneDeparted, end, departAt);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X)
        && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// Rolls one crate's contents: the guaranteed bundles, then the rifle bundles on a
    /// <c>RifleChance</c> hit, then <c>PoolDraws</c> weighted bundles. Pure in
    /// <c>(matchSeed, dropIndex)</c>.
    /// </summary>
    public List<AirdropItem> RollContents(AirdropTables tables, int dropIndex)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var items = new List<AirdropItem>(8);

        foreach (AirdropBundle bundle in tables.Guaranteed)
        {
            items.AddRange(bundle.Items);
        }

        var rifleRandom = new GasRandom(SeedFor(dropIndex, stream: 1));
        if (rifleRandom.NextDouble() < _options.RifleChance)
        {
            foreach (AirdropBundle bundle in tables.Rifle)
            {
                items.AddRange(bundle.Items);
            }
        }

        for (int draw = 0; draw < _options.PoolDraws; draw++)
        {
            // Each draw gets its own stream so PoolDraws can be retuned without re-rolling the
            // draws that were already there.
            if (tables.RollPool(SeedFor(dropIndex, stream: 2 + (uint)draw), out AirdropBundle bundle))
            {
                items.AddRange(bundle.Items);
            }
        }

        return items;
    }

    /// <summary>
    /// Where a crate's contents are laid when it is opened: a deterministic ring of
    /// <c>SpillRadiusMetres</c> around the crate, evenly spaced so no two items occupy one point.
    /// A single item lands on the crate itself.
    /// </summary>
    public Vector3 SpillPositionFor(in Vector3 crate, int index, int count)
    {
        if (count <= 1 || _options.SpillRadiusMetres <= 0f)
        {
            return crate;
        }

        float angle = MathF.Tau * index / count;
        return new Vector3(
            crate.X + (MathF.Cos(angle) * _options.SpillRadiusMetres),
            crate.Y,
            crate.Z + (MathF.Sin(angle) * _options.SpillRadiusMetres));
    }

    private ulong SeedFor(int dropIndex, uint stream)
    {
        unchecked
        {
            ulong seed = (_matchSeed ^ SeedSalt) * 0x9E37_79B9_7F4A_7C15UL;
            return seed ^ ((ulong)(uint)dropIndex << 32) ^ (stream + 0x1656_67B1_9E37_79F9UL);
        }
    }
}
