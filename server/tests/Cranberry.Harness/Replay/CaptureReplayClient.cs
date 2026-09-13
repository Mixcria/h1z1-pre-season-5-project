using System.Net;
using System.Threading.Channels;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Runtime;
using Cranberry.Harness.Soe;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Replay;

public sealed record ReplayOptions
{
    public IPEndPoint LoginEndPoint { get; init; } = new(IPAddress.Loopback, 20042);

    public byte[] LoginKey { get; init; } = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");

    public IPEndPoint? GatewayEndPointOverride { get; init; }

    /// <summary>Stops the replay once the recorded clock passes this offset. Null replays it all.</summary>
    public TimeSpan? StopAfter { get; init; }

    /// <summary>How long to wait for an anchor before sending anyway and recording the timeout.</summary>
    public TimeSpan AnchorTimeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>How far back a client message may look for the server message that caused it.</summary>
    public TimeSpan AnchorWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Caps the opaque channel-2/3 stream; a full session carries several thousand.</summary>
    public int MaxMovementPackets { get; init; } = 1500;

    /// <summary>How long to keep listening after the last step, for the server's trailing burst.</summary>
    public TimeSpan Drain { get; init; } = TimeSpan.FromSeconds(3);

    public int JournalCapacity { get; init; } = 160;

    public Action<string>? Log { get; init; }
}

/// <summary>What happened to one anchor.</summary>
public sealed record AnchorOutcome(ReplayStep Step, ReplayAnchor Anchor, bool Satisfied, TimeSpan Waited)
{
    public override string ToString() => Satisfied
        ? $"{Anchor} -> {Step.Signature.Name} after {Waited.TotalSeconds:F3}s"
        : $"{Anchor} NEVER ARRIVED; {Step.Signature.Name} sent anyway after {Waited.TotalSeconds:F3}s";
}

/// <summary>
/// Replays a recorded client session against a live server and records what comes back.
///
/// <para><b>Live socket, not in-process.</b> The replay goes over UDP to a running
/// <c>Cranberry.Host</c> rather than driving <c>ZoneService</c> in-process, for three reasons. The
/// host's wiring lives in <c>Program.cs</c>, so an in-process rig would have to duplicate it and
/// would then be measuring a server the owner never runs. The regressions this exists to catch
/// (docs/32, docs/45) were all in that assembled whole, not in one class. And the harness's own
/// transport is deliberately independent of <c>Cranberry.Transport</c> (docs/72 §2), which is only
/// worth anything if the two actually meet on a wire.</para>
///
/// <para>The login link is walked message by message from the recording, so the server sees the
/// client's real 967-byte SystemFingerprint rather than the harness's stand-in. Only the gateway
/// LoginRequest is rebuilt, because its ticket is minted per session.</para>
/// </summary>
public sealed class CaptureReplayClient : IAsyncDisposable
{
    private readonly ReplayOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly List<(TimeSpan At, PacketSignature Signature, int Length)> _liveServer = [];
    private readonly List<GuardMessage> _liveGuard = [];
    private readonly Dictionary<PacketSignature, int> _liveCounts = [];
    private SoeClientSession? _login;
    private SoeClientSession? _gateway;
    private Task? _pump;
    private TimeSpan _gatewayEpoch;
    private TimeSpan _slip;

    public CaptureReplayClient(ReplayOptions? options = null)
    {
        _options = options ?? new ReplayOptions();
        Clock = new HarnessClock();
        Journal = new PacketJournal(_options.JournalCapacity);
    }

    public HarnessClock Clock { get; }

    public PacketJournal Journal { get; }

    public GatewayHandoff? Handoff { get; private set; }

    public IReadOnlyList<AnchorOutcome> Anchors => _anchors;

    private readonly List<AnchorOutcome> _anchors = [];

    /// <summary>Everything the server said on the gateway link, offset from the gateway epoch.</summary>
    public IReadOnlyList<(TimeSpan At, PacketSignature Signature, int Length)> LiveServerStream
    {
        get { lock (_gate) { return [.. _liveServer]; } }
    }

    /// <summary>The same stream in the shape <see cref="RegressionGuards"/> consumes.</summary>
    public IReadOnlyList<GuardMessage> LiveGuardStream
    {
        get { lock (_gate) { return [.. _liveGuard]; } }
    }

    public async Task<ReplayResult> RunAsync(ReplayScript script, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = linked.Token;
        var problems = new List<string>();

        GatewayHandoff handoff = await LoginAsync(script, problems, token).ConfigureAwait(false);
        await ConnectGatewayAsync(handoff, token).ConfigureAwait(false);
        await DriveAsync(script, problems, token).ConfigureAwait(false);
        await Clock.DelayAsync(_options.Drain, token).ConfigureAwait(false);

        StreamSummary reference = StreamSummary.Build(
            $"recorded {Path.GetFileName(script.Run.Path)} run #{script.Run.Index}",
            script.Run.Gateway.ServerToClient.Where(m => m.At <= EffectiveEnd(script)));

        StreamSummary live = StreamSummary.Build("live server", LiveServerStream);

        return new ReplayResult(
            script,
            StreamDiff.Compare(reference, live),
            RegressionGuards.Run(script.Run.Gateway.ServerToClient.Select(GuardMessage.From)),
            RegressionGuards.Run(LiveGuardStream),
            [.. _anchors],
            problems,
            Journal);
    }

    private TimeSpan EffectiveEnd(ReplayScript script) =>
        _options.StopAfter is TimeSpan stop && stop < script.Run.Gateway.Duration
            ? stop + _options.Drain
            : TimeSpan.MaxValue;

    // ---- the login link -----------------------------------------------------------------------

    private async Task<GatewayHandoff> LoginAsync(ReplayScript script, List<string> problems, CancellationToken token)
    {
        _login = await SoeClientSession.OpenAsync(
            _options.LoginEndPoint,
            new SoeClientOptions
            {
                ProtocolName = AugustClient.LoginProtocolName,
                LinkName = "LoginUdp_14",
                Key = _options.LoginKey,
            },
            Clock, Journal, m => ObservedPacket.ParseLogin(m).Name, token).ConfigureAwait(false);

        CharacterSelectInfo? roster = null;
        CharacterLoginOutcome? outcome = null;
        TimeSpan epoch = Clock.Now;

        IReadOnlyList<ReplayStep> steps = script.LoginSteps.Count > 0
            ? script.LoginSteps
            : DefaultLoginSteps();

        foreach (ReplayStep step in steps)
        {
            if (step.Fidelity == ReplayFidelity.Skipped)
            {
                problems.Add($"login step {step.Signature.Name} skipped: {step.Note}");
                continue;
            }

            byte opcode = step.Bytes[0];
            byte[] bytes = step.Bytes;

            if (opcode == LoginOpcodes.CharacterLoginRequest)
            {
                if (roster is null)
                {
                    problems.Add("the recording sent CharacterLoginRequest without a roster reply; using it verbatim");
                }
                else
                {
                    bytes = RewriteCharacterLogin(step.Bytes, roster, problems);
                }
            }

            if (opcode == LoginOpcodes.Logout)
            {
                // The recorded Logout closes the recorded session; the replay's own link has to
                // stay open until the gateway work is done, so it is deliberately not replayed.
                continue;
            }

            await DelayUntil(epoch, step.At, token).ConfigureAwait(false);
            _login.Send(bytes, ObservedPacket.ParseLogin(bytes).Name);
            _options.Log?.Invoke($"{Clock.Now.TotalSeconds,8:F3}s  login c2s {step.Signature.Name} ({bytes.Length} B, {step.Fidelity})");

            byte? expected = ExpectedReply(opcode);
            if (expected is null)
            {
                continue;
            }

            ObservedPacket reply = await AwaitLoginReplyAsync(expected.Value, token).ConfigureAwait(false);
            if (expected == LoginOpcodes.CharacterSelectInfoReply)
            {
                roster = LoginWire.ParseCharacterSelectInfoReply(reply.Bytes);
            }
            else if (expected == LoginOpcodes.CharacterLoginReply)
            {
                outcome = LoginWire.ParseCharacterLoginReply(reply.Bytes);
            }
        }

        if (outcome is null || !outcome.Succeeded)
        {
            throw new InvalidOperationException(
                $"the replay never reached a successful CharacterLoginReply (status {outcome?.Status.ToString() ?? "none"}).");
        }

        Handoff = outcome.Gateway!;
        return Handoff;
    }

    /// <summary>The five-message login script, for a capture whose login link was not recorded.</summary>
    private static IReadOnlyList<ReplayStep> DefaultLoginSteps()
    {
        byte[][] messages =
        [
            LoginWire.LoginRequest(AugustLoginContext.SessionId, AugustLoginContext.Fingerprint),
            LoginWire.ServerListRequest(),
            LoginWire.CharacterSelectInfoRequest(),
            LoginWire.CharacterLoginRequest(0, 0, AugustLoginContext.Bytes),
        ];

        TimeSpan[] at =
        [
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(240),
            TimeSpan.FromMilliseconds(1540),
        ];

        return [.. messages.Select((m, i) => new ReplayStep(
            i, at[i], CaptureLink.Login, m, PacketSignature.ForLogin(m),
            i == 3 ? ReplayFidelity.Rewritten : ReplayFidelity.Rebuilt, null,
            "the capture had no login link; this is the harness's own login flow"))];
    }

    private static byte? ExpectedReply(byte request) => request switch
    {
        LoginOpcodes.LoginRequest => LoginOpcodes.LoginReply,
        LoginOpcodes.ServerListRequest => LoginOpcodes.ServerListReply,
        LoginOpcodes.CharacterSelectInfoRequest => LoginOpcodes.CharacterSelectInfoReply,
        LoginOpcodes.CharacterLoginRequest => LoginOpcodes.CharacterLoginReply,
        _ => null,
    };

    /// <summary>
    /// Substitutes this run's entity key and server id into the recorded CharacterLoginRequest,
    /// leaving the 97-byte context — the part the client built and nothing here understands — alone.
    /// </summary>
    private static byte[] RewriteCharacterLogin(byte[] recorded, CharacterSelectInfo roster, List<string> problems)
    {
        var reader = new WireReader(recorded);
        reader.U8();
        ulong recordedKey = reader.LeU64();
        reader.LeU64();
        byte[] context = reader.CountedBytes().ToArray();

        RosterCharacter[] available = [.. roster.Characters.Where(c => c.Status == RosterCharacter.StatusAvailable)];
        if (available.Length == 0)
        {
            problems.Add("this run's roster has no available character; replaying the recorded entity key");
            return recorded;
        }

        RosterCharacter chosen = available.FirstOrDefault(c => c.EntityKey == recordedKey) ?? available[0];
        if (chosen.EntityKey != recordedKey)
        {
            problems.Add($"the recorded entity key 0x{recordedKey:x} is not on this run's roster; "
                + $"replaying against 0x{chosen.EntityKey:x} instead");
        }

        return LoginWire.CharacterLoginRequest(chosen.EntityKey, chosen.ServerId, context);
    }

    private async Task<ObservedPacket> AwaitLoginReplyAsync(byte opcode, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            while (await _login!.Messages.WaitToReadAsync(deadline.Token).ConfigureAwait(false))
            {
                while (_login.Messages.TryRead(out InboundMessage? inbound))
                {
                    ObservedPacket packet = ObservedPacket.ParseLogin(inbound.Bytes);
                    if (packet.ZoneOpcode == opcode)
                    {
                        return packet;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Falls through to the throw below.
        }
        catch (ChannelClosedException)
        {
            // Falls through to the throw below.
        }

        throw new InvalidOperationException(
            $"no {LoginOpcodes.Name(opcode)} arrived within 10 s"
            + (_login?.Fault is null ? string.Empty : $" (link: {_login.Fault})"));
    }

    // ---- the gateway link ---------------------------------------------------------------------

    private async Task ConnectGatewayAsync(GatewayHandoff handoff, CancellationToken token)
    {
        IPEndPoint endPoint = _options.GatewayEndPointOverride ?? ParseEndPoint(handoff.Address);
        _gateway = await SoeClientSession.OpenAsync(
            endPoint,
            new SoeClientOptions
            {
                ProtocolName = AugustClient.GatewayProtocolName,
                LinkName = "ExternalGatewayApi_3",
            },
            Clock, Journal, m => ObservedPacket.ParseGateway(m).Name, token).ConfigureAwait(false);

        _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);

        _gatewayEpoch = Clock.Now;
        _gateway.SendThenArmEncryption(
            GatewayWire.LoginRequest(handoff.Guid, handoff.Ticket, AugustClient.Protocol, AugustClient.Version),
            handoff.Key,
            "Gateway.LoginRequest (rebuilt)");
        _options.Log?.Invoke($"{Clock.Now.TotalSeconds,8:F3}s  gateway LoginRequest rebuilt, RC4 armed");
    }

    private async Task DriveAsync(ReplayScript script, List<string> problems, CancellationToken token)
    {
        foreach (ReplayStep step in script.GatewaySteps)
        {
            if (_options.StopAfter is TimeSpan stop && step.At > stop)
            {
                problems.Add($"replay stopped at {stop.TotalSeconds:F1}s of the recording by request; "
                    + $"{script.GatewaySteps.Count - step.Index} step(s) not sent");
                break;
            }

            if (step.Fidelity == ReplayFidelity.Skipped)
            {
                problems.Add($"gateway step #{step.Index} {step.Signature.Name} skipped: {step.Note}");
                continue;
            }

            if (step.Anchor is ReplayAnchor anchor)
            {
                TimeSpan started = Clock.Now;
                bool satisfied = await WaitForAnchorAsync(anchor, token).ConfigureAwait(false);
                _anchors.Add(new AnchorOutcome(step, anchor, satisfied, Clock.Now - started));
                if (!satisfied)
                {
                    problems.Add($"the server never sent {anchor}, which preceded {step.Signature.Name} in the recording");
                }

                // Waiting on an anchor puts the live clock behind the recorded one. Without this,
                // every later step is already overdue and the whole rest of the session goes out in
                // one burst — the client's cadence, which is half of what a replay is reproducing,
                // is destroyed by the first slow anchor. Absorbing the overrun into the epoch keeps
                // every subsequent gap exactly as recorded.
                TimeSpan overdue = Clock.Now - (_gatewayEpoch + _slip + step.At);
                if (overdue > TimeSpan.Zero)
                {
                    _slip += overdue;
                }
            }

            await DelayUntil(_gatewayEpoch + _slip, step.At, token).ConfigureAwait(false);

            if (_gateway!.CloseCause != LinkCloseCause.None)
            {
                problems.Add($"the gateway link closed ({_gateway.CloseCause}: {_gateway.Fault}) "
                    + $"after {step.Index} of {script.GatewaySteps.Count} step(s)");
                return;
            }

            _gateway.Send(step.Bytes, step.Signature.Name);
        }
    }

    private async Task<bool> WaitForAnchorAsync(ReplayAnchor anchor, CancellationToken token)
    {
        TimeSpan deadline = Clock.Now + _options.AnchorTimeout;
        while (Clock.Now < deadline)
        {
            lock (_gate)
            {
                if (_liveCounts.GetValueOrDefault(anchor.Signature) >= anchor.Occurrence)
                {
                    return true;
                }
            }

            if (_gateway!.CloseCause != LinkCloseCause.None)
            {
                return false;
            }

            await Task.Delay(10, token).ConfigureAwait(false);
        }

        return false;
    }

    private async Task DelayUntil(TimeSpan epoch, TimeSpan offset, CancellationToken token)
    {
        TimeSpan due = epoch + offset;
        TimeSpan wait = due - Clock.Now;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, token).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        ChannelReader<InboundMessage> reader = _gateway!.Messages;
        while (!token.IsCancellationRequested)
        {
            try
            {
                while (reader.TryRead(out InboundMessage? inbound))
                {
                    PacketSignature signature = PacketSignature.ForGateway(inbound.Bytes);
                    TimeSpan at = Clock.Now - _gatewayEpoch;
                    lock (_gate)
                    {
                        _liveServer.Add((at, signature, inbound.Bytes.Length));
                        _liveGuard.Add(new GuardMessage(at, signature, inbound.Bytes, inbound.Bytes.Length, false));
                        _liveCounts[signature] = _liveCounts.GetValueOrDefault(signature) + 1;
                    }
                }

                await reader.WaitToReadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }
        }
    }

    private static IPEndPoint ParseEndPoint(string address)
    {
        if (!IPEndPoint.TryParse(address, out IPEndPoint? endPoint))
        {
            throw new WireFormatException($"The gateway handoff address '{address}' is not an IP endpoint.");
        }

        return endPoint.Address.Equals(IPAddress.Any) ? new IPEndPoint(IPAddress.Loopback, endPoint.Port) : endPoint;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Expected on shutdown.
            }
        }

        if (_gateway is not null)
        {
            await _gateway.DisposeAsync().ConfigureAwait(false);
        }

        if (_login is not null)
        {
            await _login.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }
}
