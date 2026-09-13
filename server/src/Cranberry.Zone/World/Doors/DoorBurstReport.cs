using System.Text;

namespace Cranberry.Zone.World.Doors;

/// <summary>
/// The one-line summaries the door burst log needs, kept out of <c>ZoneService</c> so the string
/// arithmetic is testable and so the hub file carries one call rather than a fold.
/// </summary>
public static class DoorBurstReport
{
    /// <summary>
    /// The families in <paramref name="doors"/> that went out with no kinematic actor, as a log
    /// fragment — <c>" — 1 walk-through (Camper)"</c> — or the empty string when every spawned door
    /// named one.
    /// <para>
    /// docs/79 §4 E11. Probe D6 FAILED for exactly one reason: 464 of Z2's 4,103 doors (463
    /// <c>Camper</c>, 1 <c>BathroomStall</c>) have no usable kinematic twin, for geometric reasons
    /// <c>gen-doors.py:95-119</c> spells out and which are not judgement calls. An <em>unnamed</em>
    /// shortfall makes a play-test misleading: the owner walks into a camper door, gets the wave-4
    /// answer and reasonably concludes the fix failed.
    /// </para>
    /// <para>
    /// Since docs/68 this line also carries the diagnosis. The collision flag
    /// (<see cref="LightweightEntityBody.CollidableFlag"/>, docs/85 §2a) is model-independent, so
    /// under it a camper door should block <em>anyway</em> — camper blocks ⇒ the flag was the cause;
    /// only the kinematic families block ⇒ the model was (docs/68 §5 F3 step 2).
    /// </para>
    /// </summary>
    public static string DescribeWalkThrough(MatchDoors doors)
    {
        ArgumentNullException.ThrowIfNull(doors);

        // Wave 10. Under VisibleMesh the collision claim does not rest on the model at all: every
        // door names its own correct-width Doors_* mesh, which carries its own .cdt collision file
        // (docs/91 §2), and the lever is the model-independent +0x1b1 bit 0x10. Counting "doors
        // whose model is a kinematic twin" would report every door as walk-through and would be
        // exactly the kind of log line that told the owner "3 kinematic" while he walked through.
        if (doors.Options.CollisionMode == DoorCollisionMode.VisibleMesh)
        {
            return string.Empty;
        }

        int walkThrough = doors.Count - doors.CollidableCount;
        if (walkThrough <= 0)
        {
            return string.Empty;
        }

        // A door burst is capped at a few dozen instances and this runs once per burst, so an
        // ordered set of names is cheap; it is a SortedSet rather than a LINQ Distinct().Order() so
        // the line is stable between runs without allocating an intermediate sequence.
        var families = new SortedSet<string>(StringComparer.Ordinal);
        foreach (DoorInstance door in doors.Instances)
        {
            if (!door.IsCollidable)
            {
                families.Add(door.KindName);
            }
        }

        var text = new StringBuilder(64);
        text.Append(" — ").Append(walkThrough).Append(" walk-through (");
        bool first = true;
        foreach (string family in families)
        {
            if (!first)
            {
                text.Append(", ");
            }

            text.Append(family);
            first = false;
        }

        return text.Append(", no kinematic twin — docs/74 §2 finding 4)").ToString();
    }

    /// <summary>
    /// The live doors broken down by family, as a log fragment — <c>" [ResidentialFront 5,
    /// Camper 2, Office 1]"</c>.
    /// <para>
    /// Wave 10 (docs/92). The owner reports "some spawn open, some swing into the house" and the
    /// server's own state says every door was spawned CLOSED, so the wrong thing is the pose the
    /// client draws, not the bit the server holds. That makes the FAMILY the one question his next
    /// report has to answer: <c>Camper</c>'s mesh is the only one of the eight that is not hinged
    /// on local <c>x ≈ 0</c> with its leaf running to <c>−x</c> and its base at <c>y = 0</c> — it
    /// runs along <c>+Z</c> from an origin 0.89 m above its base (measured from the
    /// <c>.dme</c> DMOD v4 headers, docs/92 §2) — so "the open ones were all campers" and "the open
    /// ones were residential" point at completely different fixes.
    /// </para>
    /// </summary>
    public static string DescribeFamilies(MatchDoors doors)
    {
        ArgumentNullException.ThrowIfNull(doors);

        if (doors.Count == 0)
        {
            return string.Empty;
        }

        // A burst is a few dozen doors and this runs once per burst, so an ordered walk is cheap.
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (DoorInstance door in doors.Instances)
        {
            counts.TryGetValue(door.KindName, out int n);
            counts[door.KindName] = n + 1;
        }

        var text = new StringBuilder(64);
        text.Append(" [");
        bool first = true;
        foreach (KeyValuePair<string, int> family in counts)
        {
            if (!first)
            {
                text.Append(", ");
            }

            text.Append(family.Key).Append(' ').Append(family.Value);
            first = false;
        }

        return text.Append(']').ToString();
    }
}
