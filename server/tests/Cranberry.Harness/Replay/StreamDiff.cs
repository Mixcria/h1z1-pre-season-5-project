using System.Text;

namespace Cranberry.Harness.Replay;

/// <summary>How two streams differ at one signature.</summary>
public enum DifferenceKind
{
    /// <summary>The reference session had it; this run never sent it.</summary>
    Missing,

    /// <summary>This run sent it; the reference session never did.</summary>
    Added,

    /// <summary>Both sent it, different numbers of times.</summary>
    CountChanged,

    /// <summary>Both sent it the same number of times, at different payload sizes.</summary>
    SizeChanged,
}

/// <summary>
/// How much a difference matters. This exists because the point of the tool is telling an intended
/// change from a regression, and the two are not distinguishable by machine — but their <i>shapes</i>
/// are. A family that vanished is nearly always a regression; a repeat count that moved by one is
/// nearly always a timer landing differently.
/// </summary>
public enum DifferenceWeight
{
    /// <summary>A whole opcode family appeared or vanished. Read every one of these.</summary>
    Structural,

    /// <summary>The payload size of an existing family changed. docs/45 lived here.</summary>
    Payload,

    /// <summary>Counts moved. Usually a timer or a session-length difference.</summary>
    Cadence,
}

public sealed record StreamDifference(
    PacketSignature Signature,
    DifferenceKind Kind,
    DifferenceWeight Weight,
    SignatureStats? Reference,
    SignatureStats? Live,
    string Detail)
{
    public override string ToString() => $"[{Weight}] {Kind} {Signature.Name} — {Detail}";
}

/// <summary>
/// Compares a recorded stream against a live one, per opcode family.
///
/// <para><b>Why not a byte diff.</b> Every session differs in its guids, its match seed, its
/// timestamps and its item instance ids, so a byte-level comparison of two sessions is 100%
/// different and says nothing. What survives across runs is <i>which</i> opcodes the server sends,
/// <i>how many</i> of each, in <i>what order</i>, and at <i>what payload sizes</i> — and all four
/// of the regressions this repository has actually suffered are visible at exactly that
/// resolution: docs/32's UnsetCharacterEquipmentSlot burst is a family that appears, docs/45's
/// slot-7 row is a 24-byte size change, and the Z2 hang is a family that stops arriving.</para>
///
/// <para><b>Cadence tolerance.</b> Counts of the free-running periodic families (GameTimeSync,
/// UpdateWeatherData, the 1 Hz ClientUpdateBase pair, Synchronization) scale with how long the
/// session ran, which a replay never reproduces exactly. They are reported as
/// <see cref="DifferenceWeight.Cadence"/> and a proportional tolerance is applied, so a 30 s
/// replay of a 195 s recording does not bury the structural findings under noise.</para>
/// </summary>
public sealed class StreamDiff
{
    private StreamDiff(StreamSummary reference, StreamSummary live, IReadOnlyList<StreamDifference> differences)
    {
        Reference = reference;
        Live = live;
        Differences = differences;
    }

    public StreamSummary Reference { get; }

    public StreamSummary Live { get; }

    public IReadOnlyList<StreamDifference> Differences { get; }

    public IEnumerable<StreamDifference> Structural =>
        Differences.Where(d => d.Weight == DifferenceWeight.Structural);

    public IEnumerable<StreamDifference> Payload =>
        Differences.Where(d => d.Weight == DifferenceWeight.Payload);

    public bool IsClean => Differences.All(d => d.Weight == DifferenceWeight.Cadence);

    /// <summary>
    /// Compares. <paramref name="countTolerance"/> is the fraction by which a repeated family's
    /// count may differ before it is worth a row at all; the default lets a shorter replay through
    /// without hiding a family that stopped entirely (a count of zero is always structural).
    /// </summary>
    public static StreamDiff Compare(StreamSummary reference, StreamSummary live, double countTolerance = 0.5)
    {
        var differences = new List<StreamDifference>();

        foreach (PacketSignature signature in reference.Rows.Keys.Union(live.Rows.Keys))
        {
            reference.Rows.TryGetValue(signature, out SignatureStats? refRow);
            live.Rows.TryGetValue(signature, out SignatureStats? liveRow);

            if (liveRow is null)
            {
                differences.Add(new StreamDifference(
                    signature, DifferenceKind.Missing, DifferenceWeight.Structural, refRow, null,
                    $"the recording had {refRow!.Count} (first at {refRow.FirstAt.TotalSeconds:F3}s, len {refRow.LengthText}); this run sent none"));
                continue;
            }

            if (refRow is null)
            {
                differences.Add(new StreamDifference(
                    signature, DifferenceKind.Added, DifferenceWeight.Structural, null, liveRow,
                    $"this run sent {liveRow.Count} (first at {liveRow.FirstAt.TotalSeconds:F3}s, len {liveRow.LengthText}); the recording had none"));
                continue;
            }

            if (!refRow.DistinctLengths.SequenceEqual(liveRow.DistinctLengths))
            {
                differences.Add(new StreamDifference(
                    signature, DifferenceKind.SizeChanged, DifferenceWeight.Payload, refRow, liveRow,
                    $"payload lengths {refRow.LengthText} -> {liveRow.LengthText}"));
            }

            if (refRow.Count != liveRow.Count)
            {
                int allowed = (int)Math.Ceiling(refRow.Count * countTolerance);
                if (Math.Abs(refRow.Count - liveRow.Count) > allowed)
                {
                    differences.Add(new StreamDifference(
                        signature, DifferenceKind.CountChanged, DifferenceWeight.Cadence, refRow, liveRow,
                        $"count {refRow.Count} -> {liveRow.Count}"));
                }
            }
        }

        return new StreamDiff(reference, live,
            [.. differences.OrderBy(d => d.Weight).ThenBy(d => d.Kind).ThenBy(d => d.Signature.Name, StringComparer.Ordinal)]);
    }

    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine($"{Reference.Label}  ->  {Live.Label}");
        text.AppendLine($"  {Reference.Total} vs {Live.Total} message(s); {Reference.Rows.Count} vs {Live.Rows.Count} signature(s)");

        if (Differences.Count == 0)
        {
            text.AppendLine("  no differences at opcode resolution");
            return text.ToString();
        }

        foreach (IGrouping<DifferenceWeight, StreamDifference> group in Differences.GroupBy(d => d.Weight))
        {
            text.AppendLine($"  {group.Key.ToString().ToUpperInvariant()} ({group.Count()})");
            foreach (StreamDifference difference in group)
            {
                text.AppendLine($"    {Symbol(difference.Kind)} {difference.Signature.Name,-46} {difference.Detail}");
            }
        }

        return text.ToString();
    }

    private static string Symbol(DifferenceKind kind) => kind switch
    {
        DifferenceKind.Missing => "-",
        DifferenceKind.Added => "+",
        DifferenceKind.SizeChanged => "~",
        _ => "#",
    };
}
