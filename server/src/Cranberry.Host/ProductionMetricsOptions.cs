using System.Globalization;

namespace Cranberry.Host;

/// <summary>Operational telemetry only; no setting changes game timing or wire behavior.</summary>
public sealed record ProductionMetricsOptions
{
    public bool Enabled { get; init; }
    public int IntervalMs { get; init; } = 5_000;
    public int MaxSessions { get; init; } = 16;
    public int MaxFileMiB { get; init; } = 16;
    public int MaxFiles { get; init; } = 4;
    public bool Rolling { get; init; }
    public string NodeId { get; init; } = "local";

    public static ProductionMetricsOptions FromEnvironment(Func<string, string?> read) => new()
    {
        Enabled = read("CRANBERRY_METRICS") is "1" or "true" or "TRUE",
        IntervalMs = Number(read("CRANBERRY_METRICS_INTERVAL_MS"), 5_000, 1_000, 60_000),
        MaxSessions = Number(read("CRANBERRY_METRICS_MAX_SESSIONS"), 16, 0, 64),
        MaxFileMiB = Number(read("CRANBERRY_METRICS_MAX_FILE_MIB"), 16, 1, 64),
        MaxFiles = Number(read("CRANBERRY_METRICS_MAX_FILES"), 4, 1, 16),
        Rolling = read("CRANBERRY_METRICS_ROLLING") is "1" or "true" or "TRUE",
        NodeId = Label(read("CRANBERRY_METRICS_NODE_ID")),
    };

    private static int Number(string? text, int fallback, int minimum, int maximum) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string Label(string? text) => text is { Length: > 0 and <= 64 }
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? text : "local";
}
