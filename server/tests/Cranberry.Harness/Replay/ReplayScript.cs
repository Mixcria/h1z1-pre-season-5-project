using Cranberry.Harness.Protocol;

namespace Cranberry.Harness.Replay;

/// <summary>How faithfully one recorded message can be put back on the wire.</summary>
public enum ReplayFidelity
{
    /// <summary>The recorded bytes, unchanged.</summary>
    Verbatim,

    /// <summary>The recorded bytes with named fields substituted from this run.</summary>
    Rewritten,

    /// <summary>Rebuilt from this run's own values; the recorded bytes cannot be reused at all.</summary>
    Rebuilt,

    /// <summary>Not sent, with a reason.</summary>
    Skipped,
}

/// <summary>
/// A causal edge read out of the recording: the server message that came immediately before this
/// client message. Waiting for it before replaying keeps a replay meaningful when the server's
/// timing has drifted — and turns each edge into an assertion in its own right, because "in the
/// recorded session this message followed <c>X</c>, and today <c>X</c> never came" is exactly the
/// difference the tool exists to surface.
/// </summary>
public sealed record ReplayAnchor(PacketSignature Signature, int Occurrence)
{
    public override string ToString() => Occurrence == 1 ? Signature.Name : $"{Signature.Name} #{Occurrence}";
}

/// <summary>One message to put back on the wire.</summary>
public sealed record ReplayStep(
    int Index,
    TimeSpan At,
    CaptureLink Link,
    byte[] Bytes,
    PacketSignature Signature,
    ReplayFidelity Fidelity,
    ReplayAnchor? Anchor,
    string? Note)
{
    public override string ToString() =>
        $"#{Index} {At.TotalSeconds,8:F3}s {Signature.Name} ({Bytes.Length} B, {Fidelity})"
        + (Anchor is null ? string.Empty : $" after {Anchor}")
        + (Note is null ? string.Empty : $" — {Note}");
}

/// <summary>
/// The client half of a recorded session, turned into something that can be said again.
///
/// <para><b>What cannot be replayed, and why.</b></para>
/// <list type="bullet">
/// <item><b>Nothing below the application message.</b> Session ids, sequence numbers, fragments,
/// acks and the RC4 keystream are all re-derived by the harness's own transport. This is not a
/// limitation but the reason the approach works: the recorder writes decrypted, reassembled
/// application messages, so a replay never has to reproduce a keystream position.</item>
/// <item><b>The gateway LoginRequest is rebuilt, not replayed.</b> Its 24-character ticket is
/// minted per session by the login server (<c>32D5949AF1C0D4BE8F6D78DD</c> in one recording,
/// <c>32642B7688ED7FD9430BCAED</c> in another). The guid beside it is <b>not</b> a per-run value —
/// it is 0x1003 in every capture — so only the ticket actually moves.</item>
/// <item><b>CharacterLoginRequest's entity key is rewritten</b> from this run's roster when the
/// recorded key is not on it; the rest of the message, including the client's real 967-byte
/// SystemFingerprint, goes out verbatim.</item>
/// <item><b>Timing is reproduced, not preserved.</b> Steps are sent at their recorded offsets from
/// the session start, which reproduces the client's cadence but not the server's. Where the two
/// interact, the anchor does the work.</item>
/// <item><b>Channel 2 and 3 are opaque.</b> Movement bytes are replayed exactly as recorded and
/// nothing decodes them (docs/71 §1, §9). A replay proves the server accepts and acks that stream;
/// it proves nothing about whether the positions in it mean anything.</item>
/// </list>
/// </summary>
public sealed class ReplayScript
{
    private ReplayScript(CaptureRun run, IReadOnlyList<ReplayStep> login, IReadOnlyList<ReplayStep> gateway, IReadOnlyList<string> notes)
    {
        Run = run;
        LoginSteps = login;
        GatewaySteps = gateway;
        Notes = notes;
    }

    public CaptureRun Run { get; }

    /// <summary>The LoginUdp_14 client messages, in order. Empty when the capture has no login link.</summary>
    public IReadOnlyList<ReplayStep> LoginSteps { get; }

    /// <summary>
    /// The ExternalGatewayApi_3 client messages after the clear LoginRequest, in order. The
    /// LoginRequest itself is not a step: arming RC4 has to happen in the same operation that
    /// sends it (docs/71 §3), so <see cref="CaptureReplayClient"/> owns it.
    /// </summary>
    public IReadOnlyList<ReplayStep> GatewaySteps { get; }

    /// <summary>Everything the script had to change or could not reproduce, in plain words.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// How far apart two messages may sit and still be treated as one causal group. This is the
    /// script's anchor window — the rule that a client message sent 30 s after the last server
    /// message was not caused by it — and it is the only definition of "the same burst" this
    /// repository has. <see cref="RegressionGuards"/> reuses it rather than inventing a second one.
    /// </summary>
    public static readonly TimeSpan DefaultAnchorWindow = TimeSpan.FromSeconds(3);

    public TimeSpan Duration => GatewaySteps.Count == 0 ? TimeSpan.Zero : GatewaySteps[^1].At;

    /// <summary>
    /// Reads a run into a script. <paramref name="anchorWindow"/> bounds how far back an anchor may
    /// look: a client message sent 30 s after the last server message was not caused by it, and
    /// pretending otherwise would make the replay wait for something irrelevant.
    /// </summary>
    public static ReplayScript Build(CaptureRun run, TimeSpan? anchorWindow = null, int maxMovementPackets = int.MaxValue)
    {
        TimeSpan window = anchorWindow ?? DefaultAnchorWindow;
        var notes = new List<string>();
        int redacted = run.Gateway.RedactedMessages + (run.Login?.RedactedMessages ?? 0);
        if (redacted != 0)
            notes.Add($"{redacted} explicitly redacted packet(s) are unavailable; replay and guard coverage are incomplete");

        IReadOnlyList<ReplayStep> login = run.Login is null
            ? BuildNoLogin(notes)
            : BuildLoginSteps(run.Login, notes);

        IReadOnlyList<ReplayStep> gateway = BuildGatewaySteps(run.Gateway, window, maxMovementPackets, notes);

        return new ReplayScript(run, login, gateway, notes);
    }

    private static IReadOnlyList<ReplayStep> BuildNoLogin(List<string> notes)
    {
        notes.Add("the capture has no LoginUdp_14 session before this gateway session, so the login "
            + "flow is the harness's own; only the gateway stream is replayed");
        return [];
    }

    private static IReadOnlyList<ReplayStep> BuildLoginSteps(CaptureSession session, List<string> notes)
    {
        var steps = new List<ReplayStep>();

        foreach (CaptureMessage message in session.ClientToServer)
        {
            if (message.Truncated)
            {
                steps.Add(new ReplayStep(steps.Count, message.At, CaptureLink.Login, [], message.Signature,
                    ReplayFidelity.Skipped, null, $"the recorded message is {message.Length} B, beyond the materialisation cap"));
                continue;
            }

            byte opcode = message.Bytes.Length == 0 ? (byte)0 : message.Bytes[0];
            (ReplayFidelity fidelity, string? note) = opcode switch
            {
                LoginOpcodes.CharacterLoginRequest =>
                    (ReplayFidelity.Rewritten, "entity key and server id come from this run's roster"),
                LoginOpcodes.LoginRequest =>
                    (ReplayFidelity.Verbatim, "carries the client's own SystemFingerprint, replayed unchanged"),
                _ => (ReplayFidelity.Verbatim, (string?)null),
            };

            steps.Add(new ReplayStep(steps.Count, message.At, CaptureLink.Login, message.Bytes, message.Signature, fidelity, null, note));
        }

        if (steps.Count == 0)
        {
            notes.Add("the capture's login session recorded no client messages");
        }

        return steps;
    }

    private static IReadOnlyList<ReplayStep> BuildGatewaySteps(
        CaptureSession session,
        TimeSpan anchorWindow,
        int maxMovementPackets,
        List<string> notes)
    {
        var steps = new List<ReplayStep>();
        var serverCounts = new Dictionary<PacketSignature, int>();
        CaptureMessage? lastServer = null;
        bool seenLoginRequest = false;
        int movement = 0;
        int droppedMovement = 0;

        foreach (CaptureMessage message in session.Messages)
        {
            if (message.Direction == CaptureDirection.ServerToClient)
            {
                serverCounts[message.Signature] = serverCounts.GetValueOrDefault(message.Signature) + 1;
                lastServer = message;
                continue;
            }

            if (message.Direction != CaptureDirection.ClientToServer)
            {
                continue;
            }

            if (message.Signature.Kind == SignatureKind.GatewayControl
                && message.Signature.Opcode == GatewayWire.OpcodeLoginRequest)
            {
                seenLoginRequest = true;
                notes.Add($"the gateway LoginRequest ({message.Length} B at {message.At.TotalSeconds:F3}s) is rebuilt from "
                    + "this run's handoff: the ticket is minted per session");
                continue;
            }

            if (!seenLoginRequest)
            {
                notes.Add($"a client message ({message.Signature.Name}) preceded the gateway LoginRequest and was dropped");
                continue;
            }

            if (message.Truncated)
            {
                steps.Add(new ReplayStep(steps.Count, message.At, CaptureLink.Gateway, [], message.Signature,
                    ReplayFidelity.Skipped, null, $"the recorded message is {message.Length} B, beyond the materialisation cap"));
                continue;
            }

            if (message.Signature.Kind == SignatureKind.Movement)
            {
                if (++movement > maxMovementPackets)
                {
                    droppedMovement++;
                    continue;
                }

                steps.Add(new ReplayStep(steps.Count, message.At, CaptureLink.Gateway, message.Bytes, message.Signature,
                    ReplayFidelity.Verbatim, null, null));
                continue;
            }

            ReplayAnchor? anchor = lastServer is not null && message.At - lastServer.At <= anchorWindow
                ? new ReplayAnchor(lastServer.Signature, serverCounts[lastServer.Signature])
                : null;

            steps.Add(new ReplayStep(steps.Count, message.At, CaptureLink.Gateway, message.Bytes, message.Signature,
                ReplayFidelity.Verbatim, anchor, null));
        }

        if (droppedMovement > 0)
        {
            notes.Add($"{droppedMovement} channel-2/3 packet(s) beyond the {maxMovementPackets} cap were not replayed");
        }

        if (!seenLoginRequest)
        {
            notes.Add("the capture's gateway session has no clear LoginRequest, so nothing before the "
                + "harness's own handoff could be anchored");
        }

        return steps;
    }
}
