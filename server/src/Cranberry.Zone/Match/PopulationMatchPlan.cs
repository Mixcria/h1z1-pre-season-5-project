using System.Numerics;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Match;

/// <summary>
/// One frozen roster chooses both the drop footprint and gas. Population thresholds and
/// intermediate sizes are server tuning; see docs/population-gas-20260913.md.
/// </summary>
public sealed record PopulationMatchPlan(GasSchedule Schedule, PopulationDropPlanner Spawns,
    int Population, bool Dynamic)
{
    public const int FullMatchPlayers = 100;
    public const float FullSpawnHalfExtent = 2000f; // August KotK.SkySpawn X/Z bounds.

    public static PopulationMatchPlan Create(Z2DropPois places, DropOptions drop, GasSettings gas,
        bool gasEnabled, ulong seed, IEnumerable<DropParticipant> participants, int teamSize)
    {
        var roster = participants.Distinct().ToArray();
        if (roster.Length == 0) throw new ArgumentException("A match needs a roster.", nameof(participants));
        ulong gasSeed = MatchSeeds.For(seed, MatchSeeds.GasSalt);
        GasSchedule normal = GasSchedule.Create(gas, gasSeed);
        bool dynamic = gasEnabled && CanAdapt(gas) && roster.Length < FullMatchPlayers;
        if (!dynamic)
        {
            var spawns = new PopulationDropPlanner(places, drop,
                gasEnabled ? normal.Phase(1).Target : null, seed,
                spawnHalfExtent: roster.Length >= FullMatchPlayers ? FullSpawnHalfExtent : null);
            spawns.Assign(roster, teamSize);
            return new(normal, spawns, roster.Length, false);
        }

        // Retry a bounded number of real looting neighbourhoods if a rural area lacks seats.
        // A capacity fallback may ease spacing, but can never widen beyond the visible gas.
        PopulationMatchPlan? best = null;
        for (ulong attempt = 0; attempt < 12; attempt++)
        {
            ulong locationSeed = attempt == 0 ? seed : MatchSeeds.For(seed, 0x504F50554C415400UL + attempt);
            DropPlan origin = DropPlanner.Plan(places, null, drop, locationSeed)!;
            float mapEdge = drop.MapHalfExtentMetres - drop.MinimumEdgeMetres - 24f;
            if (MathF.Abs(origin.Position.X) > mapEdge || MathF.Abs(origin.Position.Z) > mapEdge) continue;
            GasSettings local = LocalSettings(gas, roster.Length, origin.Position with { Y = 0 });
            GasSchedule schedule = GasSchedule.Create(local, gasSeed);
            var spawns = new PopulationDropPlanner(places, drop, schedule.Phase(1).Target, seed,
                spawnBoundary: schedule.InitialCircle with { Radius = Math.Min(schedule.InitialCircle.Radius, FullSpawnHalfExtent) },
                origin: origin);
            spawns.Assign(roster, teamSize);
            var candidate = new PopulationMatchPlan(schedule, spawns, roster.Length, true);
            if (best is null || spawns.ReducedSpacingPlacements < best.Spawns.ReducedSpacingPlacements)
                best = candidate;
            if (spawns.ReducedSpacingPlacements == 0) return candidate;
        }
        return best ?? throw new InvalidOperationException("No looting neighbourhood fits the configured map bounds.");
    }

    private static bool CanAdapt(GasSettings settings)
    {
        // Explicit operator geometry/pacing remains authoritative. Damage, packet and clock
        // overrides still apply to population matches using the ordinary August geometry.
        var normal = new GasSettings();
        if (settings.AdvanceDurationsMs.Count == 0) return false;
        float scale = settings.AdvanceDurationsMs[0] / (float)normal.AdvanceDurationsMs[0];
        var clock = normal.ScaledBy(scale);
        return settings.PopulationAdaptive && settings.Pacing == GasPacing.PhaseTable
            && settings.PhaseCount == normal.PhaseCount
            && settings.InitialRadius == normal.InitialRadius && settings.FinalRadius == normal.FinalRadius
            && settings.PlayAreaCentre == normal.PlayAreaCentre && settings.CentrePlan == normal.CentrePlan
            && settings.FirstRevealDelayMs == clock.FirstRevealDelayMs
            && settings.FirstMoveDelayMs == clock.FirstMoveDelayMs && settings.PhaseHoldMs == clock.PhaseHoldMs
            && settings.HoldDurationsMs.SequenceEqual(clock.HoldDurationsMs)
            && settings.AdvanceDurationsMs.SequenceEqual(clock.AdvanceDurationsMs)
            && Enumerable.Range(1, normal.PhaseCount).All(i => settings.RadiusForPhase(i) == normal.RadiusForPhase(i));
    }

    private static GasSettings LocalSettings(GasSettings gas, int population, Vector3 centre)
    {
        float first = population switch { <= 2 => 300f, <= 10 => 625f, <= 20 => 900f, <= 50 => 1400f, _ => 2000f };
        int skip = Enumerable.Range(1, gas.PhaseCount).First(i => gas.RadiusForPhase(i) <= first) - 1;
        var radii = Enumerable.Range(skip + 1, gas.PhaseCount - skip).Select(gas.RadiusForPhase).ToArray();
        uint[] holds = gas.HoldDurationsMs.Skip(skip).ToArray();
        uint[] advances = gas.AdvanceDurationsMs.Skip(skip).ToArray();
        // Reference: 15 s reveal, 120 s first hold, 90 s first advance in the captured
        // two-player match. Scale these with the configured opening clock for test presets.
        holds[0] = (uint)Math.Min(uint.MaxValue, Math.Round(gas.HoldDurationsMs[0] * (120d / 250d)));
        advances[0] = (uint)Math.Clamp(Math.Round(gas.AdvanceDurationsMs[0] * (90d / 300d)), 1d, uint.MaxValue);
        uint reveal = gas.FirstRevealDelayMs / 8;
        var settings = gas with
        {
            PhaseCount = radii.Length, RadiusLadder = radii,
            InitialRadius = first * 1.6f, PlayAreaCentre = centre,
            CentrePlan = GasCentrePlan.Drift, CentreDriftFraction = Math.Min(gas.CentreDriftFraction, 0.25f),
            PlayAreaLeadMetres = 0, PreMoveRing = GasPreMoveRing.Boundary,
            FirstRevealDelayMs = reveal, FirstMoveDelayMs = reveal + holds[0],
            HoldDurationsMs = holds, AdvanceDurationsMs = advances,
        };
        settings.Validate();
        return settings;
    }
}
