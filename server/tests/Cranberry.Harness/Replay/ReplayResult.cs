using System.Text;
using Cranberry.Harness.Runtime;
using Cranberry.Zone;

namespace Cranberry.Harness.Replay;

/// <summary>
/// What one replay found. The report is deliberately ordered the way a reader needs it: the
/// fidelity caveats first (so nothing below is over-read), then the guard findings, then the
/// causal edges that did not hold, then the opcode diff — structural differences before cadence
/// noise.
/// </summary>
public sealed record ReplayResult(
    ReplayScript Script,
    StreamDiff Diff,
    IReadOnlyList<GuardFinding> RecordedGuards,
    IReadOnlyList<GuardFinding> LiveGuards,
    IReadOnlyList<AnchorOutcome> Anchors,
    IReadOnlyList<string> Problems,
    PacketJournal Journal)
{
    public IEnumerable<AnchorOutcome> BrokenAnchors => Anchors.Where(a => !a.Satisfied);

    public IEnumerable<GuardFinding> NewFatalFindings
    {
        get
        {
            var recorded = RecordedGuards.Where(f => f.Severity == GuardSeverity.Fatal).Select(f => f.Id).ToHashSet();
            return LiveGuards.Where(f => f.Severity == GuardSeverity.Fatal && !recorded.Contains(f.Id));
        }
    }

    /// <summary>
    /// The families from <see cref="CriticalOpcodes"/> the recording carried and this run did not.
    /// This is the subset of <see cref="StreamDiff.Structural"/> worth failing a build over: a
    /// difference here is not a wave-3-to-6 feature change, it is a step of the bootstrap that
    /// stopped happening — the shape of the Z2 hang.
    /// </summary>
    public IEnumerable<StreamDifference> MissingCritical =>
        Diff.Structural.Where(d => d.Kind == DifferenceKind.Missing && CriticalOpcodes.Contains(d.Signature));

    /// <summary>
    /// True when the live server did nothing the recording did not, at guard and structural
    /// resolution. Cadence differences are expected in every replay and do not fail it.
    /// </summary>
    public bool Matches =>
        LiveGuards.All(f => f.Severity != GuardSeverity.Fatal)
        && !BrokenAnchors.Any()
        && !Diff.Structural.Any()
        && !Diff.Payload.Any();

    public string Report(int journalTail = 0)
    {
        var text = new StringBuilder();
        text.AppendLine($"=== capture replay: {Path.GetFileName(Script.Run.Path)} run #{Script.Run.Index} ({Script.Run.Gateway.Remote})");
        text.AppendLine($"    {Script.GatewaySteps.Count} client message(s) over {Script.Duration.TotalSeconds:F1}s of recording");
        text.AppendLine();

        text.AppendLine("-- fidelity");
        foreach (string note in Script.Notes)
        {
            text.AppendLine($"   * {note}");
        }

        foreach (IGrouping<ReplayFidelity, ReplayStep> group in Script.GatewaySteps.GroupBy(s => s.Fidelity))
        {
            text.AppendLine($"   {group.Count(),6} gateway step(s) {group.Key}");
        }

        foreach (string problem in Problems)
        {
            text.AppendLine($"   ! {problem}");
        }

        text.AppendLine();
        text.AppendLine("-- regression guards");
        text.Append(RegressionGuards.Render("   recorded session", RecordedGuards));
        text.Append(RegressionGuards.Render("   live server    ", LiveGuards));

        text.AppendLine();
        text.AppendLine($"-- causal edges ({Anchors.Count} anchored step(s), {BrokenAnchors.Count()} unmet)");
        foreach (AnchorOutcome broken in BrokenAnchors)
        {
            text.AppendLine($"   ! {broken}");
        }

        text.AppendLine();
        text.AppendLine("-- server stream, by opcode");
        text.Append(Diff.Render());

        if (journalTail > 0)
        {
            text.AppendLine();
            text.AppendLine(Journal.Tail(journalTail));
        }

        return text.ToString();
    }
}

/// <summary>
/// The families without which the recorded session could not have happened, so a replay that does
/// not see them has found a regression rather than a feature change.
///
/// Every entry is a step the client's own behaviour depends on: it cannot become ready without a
/// zone, a self record and the done-marker (docs/06/07/09, docs/71 §13 C-rows); it cannot leave the
/// menu without a transfer reply; it cannot enter Z2 without ClientBeginZoning followed by a second
/// self record and done-marker (docs/32's acceptance check); and it cannot mount the parachute
/// without the teleport handshake and the vehicle burst (docs/12, docs/71 §8).
///
/// Deliberately excluded: everything optional or cosmetic. Loot, weather, purchases, match history
/// and the lobby all changed legitimately across waves 3 to 6 and belong in the diff, not here.
/// </summary>
public static class CriticalOpcodes
{
    private static readonly byte[] Opcodes =
    [
        ZoneOpcodes.SendZoneDetails,
        ZoneOpcodes.SendSelfToClient,
        ZoneOpcodes.ZoneDoneSendingInitialData,
        ZoneOpcodes.ClientBeginZoning,
        ZoneOpcodes.PlayerWorldTransferReply,
        ZoneOpcodes.SynchronizedTeleportBase,
        ZoneOpcodes.AddLightweightVehicle,
        ZoneOpcodes.MountBase,
        ZoneOpcodes.EquipmentBase,
    ];

    public static bool Contains(PacketSignature signature) =>
        signature.Kind == SignatureKind.Zone && Opcodes.Contains((byte)signature.Opcode);
}

/// <summary>
/// The offline half of the tool: everything that can be learned from a capture with no server
/// running. It is worth having on its own — the guards over all 96 captures are the history of
/// this project's three known client-fatal packet shapes, and they cost milliseconds.
/// </summary>
public sealed record CaptureAnalysis(
    string Path,
    int Index,
    string Remote,
    TimeSpan Duration,
    StreamSummary ServerStream,
    StreamSummary ClientStream,
    IReadOnlyList<GuardFinding> Guards)
{
    /// <summary>Gateway application records unavailable to this analysis due to privacy redaction.</summary>
    public int RedactedMessages { get; init; }

    public bool Clean => Guards.All(g => g.Severity != GuardSeverity.Fatal);

    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine($"=== {System.IO.Path.GetFileName(Path)} run #{Index} ({Remote}, {Duration.TotalSeconds:F1}s)");
        if (RedactedMessages != 0)
            text.AppendLine($"  {RedactedMessages} redacted gateway packet(s) omitted; guard coverage is incomplete.");
        text.Append(RegressionGuards.Render("  guards", Guards));
        text.AppendLine();
        text.Append(ServerStream.Render());
        return text.ToString();
    }
}

/// <summary>The entry points.</summary>
public static class CaptureReplay
{
    /// <summary>Every run in a capture, summarised and guarded. No server needed.</summary>
    public static IReadOnlyList<CaptureAnalysis> Analyse(string path) =>
    [
        .. CaptureSessionReader.ReadRuns(path).Select(run => new CaptureAnalysis(
            path,
            run.Index,
            run.Gateway.Remote,
            run.Gateway.Duration,
            StreamSummary.Build($"{System.IO.Path.GetFileName(path)}#{run.Index} s2c", run.Gateway.ServerToClient),
            StreamSummary.Build($"{System.IO.Path.GetFileName(path)}#{run.Index} c2s", run.Gateway.ClientToServer),
            RegressionGuards.Run(run.Gateway)) { RedactedMessages = run.Gateway.RedactedMessages })
    ];

    /// <summary>Replays the longest run of a capture against a live server.</summary>
    public static Task<ReplayResult> ReplayAsync(string capturePath, ReplayOptions? options = null, CancellationToken cancellationToken = default) =>
        ReplayAsync(CaptureSessionReader.LongestRun(capturePath), options, cancellationToken);

    public static async Task<ReplayResult> ReplayAsync(CaptureRun run, ReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        ReplayOptions settings = options ?? new ReplayOptions();
        ReplayScript script = ReplayScript.Build(run, settings.AnchorWindow, settings.MaxMovementPackets);
        await using var client = new CaptureReplayClient(settings);
        return await client.RunAsync(script, cancellationToken).ConfigureAwait(false);
    }
}
