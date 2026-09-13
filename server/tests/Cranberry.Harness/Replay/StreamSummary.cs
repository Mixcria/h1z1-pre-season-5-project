using System.Text;

namespace Cranberry.Harness.Replay;

/// <summary>What one opcode family did in one direction of one session.</summary>
public sealed record SignatureStats(
    PacketSignature Signature,
    int Count,
    int FirstIndex,
    TimeSpan FirstAt,
    TimeSpan LastAt,
    int MinLength,
    int MaxLength,
    IReadOnlyList<int> DistinctLengths)
{
    public string LengthText => DistinctLengths.Count == 1
        ? DistinctLengths[0].ToString()
        : $"{MinLength}..{MaxLength} ({DistinctLengths.Count} sizes)";
}

/// <summary>
/// A whole stream reduced to one row per opcode family. This is the shape a difference is read in:
/// counts, first appearance, and the set of payload lengths.
/// </summary>
public sealed class StreamSummary
{
    /// <summary>Above this many distinct lengths the set is summarised by its range only.</summary>
    public const int MaxTrackedLengths = 12;

    private StreamSummary(string label, IReadOnlyDictionary<PacketSignature, SignatureStats> rows, int total, TimeSpan span)
    {
        Label = label;
        Rows = rows;
        Total = total;
        Span = span;
    }

    public string Label { get; }

    public IReadOnlyDictionary<PacketSignature, SignatureStats> Rows { get; }

    public int Total { get; }

    public TimeSpan Span { get; }

    /// <summary>Rows in the order the stream first produced them — how a bootstrap reads.</summary>
    public IEnumerable<SignatureStats> InFirstSeenOrder => Rows.Values.OrderBy(r => r.FirstIndex);

    public static StreamSummary Build(string label, IEnumerable<CaptureMessage> messages) =>
        Build(label, messages.Select(m => (m.At, m.Signature, m.Length)));

    public static StreamSummary Build(string label, IEnumerable<(TimeSpan At, PacketSignature Signature, int Length)> messages)
    {
        var counts = new Dictionary<PacketSignature, int>();
        var firstIndex = new Dictionary<PacketSignature, int>();
        var firstAt = new Dictionary<PacketSignature, TimeSpan>();
        var lastAt = new Dictionary<PacketSignature, TimeSpan>();
        var min = new Dictionary<PacketSignature, int>();
        var max = new Dictionary<PacketSignature, int>();
        var lengths = new Dictionary<PacketSignature, SortedSet<int>>();

        int index = 0;
        TimeSpan span = TimeSpan.Zero;

        foreach ((TimeSpan at, PacketSignature signature, int length) in messages)
        {
            if (!counts.TryAdd(signature, 1))
            {
                counts[signature]++;
            }
            else
            {
                firstIndex[signature] = index;
                firstAt[signature] = at;
                min[signature] = length;
                max[signature] = length;
                lengths[signature] = [];
            }

            lastAt[signature] = at;
            min[signature] = Math.Min(min[signature], length);
            max[signature] = Math.Max(max[signature], length);
            if (lengths[signature].Count <= MaxTrackedLengths)
            {
                lengths[signature].Add(length);
            }

            span = at > span ? at : span;
            index++;
        }

        var rows = counts.ToDictionary(
            pair => pair.Key,
            pair => new SignatureStats(
                pair.Key,
                pair.Value,
                firstIndex[pair.Key],
                firstAt[pair.Key],
                lastAt[pair.Key],
                min[pair.Key],
                max[pair.Key],
                [.. lengths[pair.Key]]));

        return new StreamSummary(label, rows, index, span);
    }

    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine($"{Label}: {Total} message(s), {Rows.Count} opcode signature(s), {Span.TotalSeconds:F1}s");
        foreach (SignatureStats row in InFirstSeenOrder)
        {
            text.AppendLine($"  {row.Count,6}  {row.FirstAt.TotalSeconds,8:F3}s  {row.Signature.Name,-46} len={row.LengthText}");
        }

        return text.ToString();
    }
}
