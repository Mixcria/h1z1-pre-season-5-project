using System.Numerics;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.Match;

/// <summary>
/// <b>Where this match drops.</b> docs/48 §5.3–§5.5.
///
/// <para>A pure function of (places, first circle, options, match seed). It sends no packet, holds
/// no session state and reads no clock, so a match replays exactly from its logged seed and the
/// whole policy is testable without a client.</para>
///
/// <para><b>The rule, and why it is the ring.</b> Retail's June-29-2017 change chose the spawn area
/// "relative to the initial safe-zone location, to ensure players don't start any game too far away
/// from where it will end" (docs/48 §3) — so the eligible places are the ones whose anchor is
/// inside the first safe circle. That is not a Cranberry invention, it is what the shipping game of
/// this build's era did, and it fixes the measured defect in
/// <c>logs/host-20260829-220829.log</c>, where one match put the player 4,316 m outside a 1,810 m
/// first circle. It is also free coordination: if the gas lane moves the ring, the drop follows and
/// no second policy has to be kept in sync.</para>
///
/// <para><b>Ordering matters for the caller.</b> The schedule has to be built <i>before</i> the drop
/// is computed, which is the one structural change docs/48 asks the Integrate lane for.
/// <see cref="GasSchedule.Create"/> is a pure allocation that writes no bytes, so moving it earlier
/// adds, removes, reorders and retimes nothing on the wire — only the payload of
/// <c>0f 05 UpdateLocation</c> changes (docs/48 §7, regression guard 4).</para>
/// </summary>
public static class DropPlanner
{
    /// <summary>
    /// Plans the drop, or returns null when there is nothing to plan from — no dataset, no place,
    /// or <see cref="DropOptions.Enabled"/> false. A null answer is the caller's cue to keep using
    /// its own fixed spawn, exactly as <c>SpawnRealGroundLoot</c> already degrades when the loot
    /// data is missing.
    /// </summary>
    /// <param name="pois">The map's named places; null when the loot data failed to load.</param>
    /// <param name="firstCircle">Phase 1's target circle, or null when the gas is disabled.</param>
    /// <param name="options">The knobs.</param>
    /// <param name="matchSeed">This match's seed; the drop draws from a salted sub-seed of it.</param>
    public static DropPlan? Plan(Z2DropPois? pois, GasCircle? firstCircle, DropOptions options, ulong matchSeed)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!options.Enabled || pois is null || pois.Count == 0)
        {
            return null;
        }

        ulong dropSeed = MatchSeeds.For(matchSeed, MatchSeeds.DropSalt);
        var random = new GasRandom(dropSeed);

        // 1. The edge invariant. Measured, not invented: no anchor on Z2 is within 200 m of the
        //    terrain edge, so this excludes nothing today (docs/48 §4.4). If a data change ever made
        //    it exclude everything, dropping nobody would be worse than dropping near the edge.
        List<DropPoi> candidates = [];
        foreach (DropPoi place in pois.Places)
        {
            if (place.EdgeMetres >= options.MinimumEdgeMetres)
            {
                candidates.Add(place);
            }
        }

        if (candidates.Count == 0)
        {
            candidates = [.. pois.Places];
        }

        // 2. The ladder (docs/48 §5.7). Under the shipped gas settings rung 0 always answers:
        //    minimum 4 eligible places, median 14, over 100,000 simulated matches.
        List<DropPoi> eligible;
        DropSelection selection;
        float factor = options.RingFactor;
        int widenSteps = 0;

        if (firstCircle is not GasCircle circle)
        {
            eligible = candidates;
            selection = DropSelection.WholeMap;
            factor = float.NaN;
        }
        else
        {
            eligible = Within(candidates, circle, factor);
            selection = DropSelection.InsideFirstCircle;

            while (eligible.Count == 0 && widenSteps < options.WidenSteps)
            {
                factor *= options.WidenFactor;
                widenSteps++;
                eligible = Within(candidates, circle, factor);
                selection = DropSelection.WidenedCircle;
            }

            if (eligible.Count == 0)
            {
                eligible = [Nearest(candidates, circle)];
                selection = DropSelection.NearestToCircle;
            }
        }

        // 3. The place: a weighted draw, exponent 0.5 (root-markers). Uniform makes a 151-marker
        //    campsite as likely as Cranberry; linear puts a third of all matches into five towns and
        //    rebuilds the monotony this lane exists to remove (docs/48 §6).
        DropPoi poi = DrawWeighted(eligible, options.PoiWeightExponent, ref random);

        // 4. The exact point. Retail scattered players *within* the spawn area, so the anchor alone
        //    would freeze a place's drop across matches. A marker is used rather than a jittered
        //    coordinate because a marker is on land, inside the place's real footprint, at a real
        //    placement height, and has loot around it by construction.
        Vector3 position = poi.Anchor;
        int markers = poi.AnchorNeighbours;
        int attemptsUsed = 0;
        bool usedAnchor = true;

        // D240 (AUDIT-parachute G5): the floor is RELATIVE to this place's own anchor. The 17:59
        // drop on 2026-09-03 accepted a marker with 74 neighbours inside a place whose anchor has
        // 472 — a 6.4x density loss bought purely by an absolute floor a rich place clears anywhere.
        // A poor place is unaffected: half of a 122-marker anchor is under the absolute 64.
        int floor = options.JitterFloorFor(poi.AnchorNeighbours);

        for (int attempt = 1; attempt <= options.JitterAttempts; attempt++)
        {
            int pick = (int)(random.NextDouble() * poi.MarkerCount);
            pick = Math.Clamp(pick, 0, poi.MarkerCount - 1);
            Vector3 candidate = pois.Spawns[poi.Markers[pick]].Position;

            int count = pois.CountWithin(candidate, options.LootRadius);
            if (count < floor)
            {
                continue;
            }

            position = candidate;
            markers = count;
            attemptsUsed = attempt;
            usedAnchor = false;
            break;
        }

        // 5. The air spawn. 850 m is the client's own KotK.SkySpawn slab centre and is absolute
        //    world Y, not an offset above the ground (docs/48 §2a) — and since D238 it is what the
        //    server ships, because retail's own release altitude is unknown and this is the only
        //    altitude the client states. CRANBERRY_DESCENT_PRESET raises it above the slab.
        var air = position with { Y = MathF.Max(options.SkySpawnAltitude, position.Y + options.MinimumClearanceMetres) };

        float distance = firstCircle is GasCircle first ? first.HorizontalDistanceTo(position) : float.NaN;
        int rank = firstCircle is GasCircle ranked ? RankOf(eligible, poi, ranked) : 0;

        return new DropPlan(
            Poi: poi,
            Position: position,
            AirPosition: air,
            Selection: selection,
            RingFactorUsed: factor,
            WidenSteps: widenSteps,
            EligibleCount: eligible.Count,
            EligibleRank: rank,
            DistanceToCircleCentre: distance,
            MarkersWithinLootRadius: markers,
            JitterAttemptsUsed: attemptsUsed,
            UsedAnchor: usedAnchor,
            MatchSeed: matchSeed,
            DropSeed: dropSeed)
        {
            LootRadiusUsed = options.LootRadius,
        };
    }

    private static List<DropPoi> Within(List<DropPoi> candidates, GasCircle circle, float factor)
    {
        float reach = circle.Radius * factor;
        List<DropPoi> inside = [];
        foreach (DropPoi place in candidates)
        {
            if (circle.HorizontalDistanceTo(place.Anchor) <= reach)
            {
                inside.Add(place);
            }
        }

        return inside;
    }

    private static DropPoi Nearest(List<DropPoi> candidates, GasCircle circle)
    {
        DropPoi best = candidates[0];
        float bestDistance = circle.HorizontalDistanceTo(best.Anchor);
        for (int i = 1; i < candidates.Count; i++)
        {
            float distance = circle.HorizontalDistanceTo(candidates[i].Anchor);
            if (distance < bestDistance)
            {
                best = candidates[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    private static int RankOf(List<DropPoi> eligible, DropPoi poi, GasCircle circle)
    {
        float distance = circle.HorizontalDistanceTo(poi.Anchor);
        int rank = 1;
        foreach (DropPoi other in eligible)
        {
            if (!ReferenceEquals(other, poi) && circle.HorizontalDistanceTo(other.Anchor) < distance)
            {
                rank++;
            }
        }

        return rank;
    }

    /// <summary>
    /// One draw over <c>markers ^ exponent</c>. The cumulative walk is over the caller's own list
    /// order, which is <see cref="Z2DropPois.Places"/>' ordinal-by-name order filtered in place, so
    /// the same seed and the same circle always pick the same place on any machine.
    /// </summary>
    private static DropPoi DrawWeighted(List<DropPoi> eligible, float exponent, ref GasRandom random)
    {
        if (eligible.Count == 1)
        {
            // Still consume a draw, so that adding or removing places cannot change how many values
            // the jitter loop below sees for an unrelated seed.
            random.NextDouble();
            return eligible[0];
        }

        double total = 0.0;
        Span<double> weights = eligible.Count <= 128 ? stackalloc double[eligible.Count] : new double[eligible.Count];
        for (int i = 0; i < eligible.Count; i++)
        {
            double weight = exponent == 0f ? 1.0 : Math.Pow(eligible[i].MarkerCount, exponent);
            if (!double.IsFinite(weight) || weight <= 0.0)
            {
                weight = double.Epsilon;
            }

            total += weight;
            weights[i] = total;
        }

        double draw = random.NextDouble() * total;
        for (int i = 0; i < weights.Length; i++)
        {
            if (draw < weights[i])
            {
                return eligible[i];
            }
        }

        return eligible[^1];
    }
}
