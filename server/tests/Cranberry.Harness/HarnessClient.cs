using System.Net;
using System.Threading.Channels;
using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Runtime;
using Cranberry.Harness.Soe;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness;

public sealed record HarnessOptions
{
    /// <summary>The login listener. The gateway address comes from the CharacterLoginReply.</summary>
    public IPEndPoint LoginEndPoint { get; init; } = new(IPAddress.Loopback, 20042);

    /// <summary>
    /// The fixed RC4 key the login link is encrypted with from its first byte. It is the host's
    /// own constant (Cranberry.Host/Program.cs), not a client secret; the client is shipped with
    /// the matching key.
    /// </summary>
    public byte[] LoginKey { get; init; } = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");

    public ClientTimings Timings { get; init; } = new();

    /// <summary>1.0 = the observed client timings, the only value valid against a live server.</summary>
    public double TimeScale { get; init; } = 1.0;

    public int JournalCapacity { get; init; } = 120;
    /// <summary>Opt-in for idle-menu population tests; active-match clients retain their original cadence.</summary>
    public bool LowFrequencyIdlePolling { get; init; }

    /// <summary>
    /// Which roster row to log in as, by index. The row's name lives inside its appearance payload
    /// (docs/05), which the harness deliberately does not decode, so selection is positional: the
    /// default takes the first row the server marked available, exactly as the character-select
    /// screen does with one character on the roster.
    /// </summary>
    public int CharacterIndex { get; init; }

    /// <summary>Overrides the gateway address the server hands back. Useful when it advertises 0.0.0.0.</summary>
    public IPEndPoint? GatewayEndPointOverride { get; init; }

    /// <summary>Seeds the responder's random values (the 0x57 tag, the tick base) for a repeatable run.</summary>
    public int? Seed { get; init; }

    public MovementReplay? PlayerMovement { get; init; }

    public MovementReplay? ManagedMovement { get; init; }

    /// <summary>Where a scenario writes its progress. Defaults to nowhere.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Launcher-issued login ticket. Null keeps the recorded local development context.</summary>
    public string? LoginTicket { get; init; }

    /// <summary>Explicit QA character creation for an otherwise empty launcher account.</summary>
    public string? CreateCharacterIfEmpty { get; init; }

    /// <summary>Long population runs can aggregate packets instead of retaining the entire stream.</summary>
    public bool RetainLedger { get; init; } = true;

    /// <summary>Called on the gateway pump; observers must be short and thread-safe.</summary>
    public Action<ObservedPacket, TimeSpan>? ObservePacket { get; init; }
}

/// <summary>
/// One modelled client: the login link, the gateway link, the responder that plays the client's
/// role, and the milestone log everything is asserted against.
///
/// The class deliberately draws a line. Phases A and B (login and the gateway handoff) are written
/// here as a linear <c>await</c> sequence, because they are a linear request/reply exchange and
/// each step needs the previous reply parsed. Everything from the zone bootstrap onwards is driven
/// by <see cref="ClientResponder"/>, because from there the client is a state machine with
/// free-running timers and the sequence depends on what the server delivers.
/// </summary>
public sealed class HarnessClient : IAsyncDisposable
{
    private readonly HarnessOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private SoeClientSession? _login;
    private SoeClientSession? _gateway;
    private Task? _gatewayPump;
    private ulong _selfGuid;

    public HarnessClient(HarnessOptions? options = null)
    {
        _options = options ?? new HarnessOptions();
        Clock = new HarnessClock { Scale = _options.TimeScale };
        Journal = new PacketJournal(_options.JournalCapacity);
        Milestones = new MilestoneLog(Clock);
    }

    public HarnessClock Clock { get; }

    public PacketJournal Journal { get; }

    public MilestoneLog Milestones { get; }

    /// <summary>
    /// Everything the server said, kept for the whole session. The journal is a bounded tail for a
    /// failure report; assertions about an <i>absence</i> (docs/32 guard 2) or about a whole-session
    /// property (loot 200 m from the landing) need a record that does not forget.
    /// </summary>
    public ServerLedger Ledger { get; } = new();

    /// <summary>
    /// Free-text readings a scenario took along the way — the descent it measured, the distance
    /// between two loot bursts. A passing scenario that measured nothing tells the reader nothing,
    /// and the numbers are what the next capture gets compared against.
    /// </summary>
    public List<string> Notes { get; } = [];

    public ClientResponder? Responder { get; private set; }

    public SoeClientSession? LoginLink => _login;

    public SoeClientSession? GatewayLink => _gateway;

    /// <summary>The character guid the server admitted, once the handoff has happened.</summary>
    public ulong SelfGuid => _selfGuid;

    public GatewayHandoff? Handoff { get; private set; }

    public CharacterSelectInfo? Roster { get; private set; }

    // ---- Phase A: the login link (docs/71 §2) -------------------------------------------------

    /// <summary>Opens the login link and walks it to a CharacterLoginReply carrying a gateway handoff.</summary>
    public async Task<GatewayHandoff> LoginAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = linked.Token;

        _login = await SoeClientSession.OpenAsync(
            _options.LoginEndPoint,
            new SoeClientOptions
            {
                ProtocolName = AugustClient.LoginProtocolName,
                LinkName = "LoginUdp_14",
                Key = _options.LoginKey,
                IdleTickIntervalMs = _options.LowFrequencyIdlePolling ? 100 : 10,
            },
            Clock, Journal, m => ObservedPacket.ParseLogin(m).Name, token).ConfigureAwait(false);
        Milestones.Note(HarnessMilestone.LoginSessionOpen, $"session {_login.SessionId:x8}");

        _login.Send(LoginWire.LoginRequest(_options.LoginTicket ?? AugustLoginContext.SessionId, AugustLoginContext.Fingerprint));
        Milestones.Note(HarnessMilestone.LoginRequestSent);

        await ExpectLoginAsync(LoginOpcodes.LoginReply, TimeSpan.FromSeconds(5), "LoginRequest", token)
            .ConfigureAwait(false);
        Milestones.Note(HarnessMilestone.LoginReplyReceived);

        await Clock.DelayAsync(_options.Timings.ServerListRequest, token).ConfigureAwait(false);
        _login.Send(LoginWire.ServerListRequest());
        Milestones.Note(HarnessMilestone.ServerListRequestSent);

        await ExpectLoginAsync(LoginOpcodes.ServerListReply, TimeSpan.FromSeconds(5), "ServerListRequest", token)
            .ConfigureAwait(false);
        Milestones.Note(HarnessMilestone.ServerListReplyReceived);

        await Clock.DelayAsync(_options.Timings.CharacterSelectInfoRequest, token).ConfigureAwait(false);
        _login.Send(LoginWire.CharacterSelectInfoRequest());
        Milestones.Note(HarnessMilestone.CharacterSelectInfoRequestSent);

        ObservedPacket rosterReply = await ExpectLoginAsync(
            LoginOpcodes.CharacterSelectInfoReply, TimeSpan.FromSeconds(5), "CharacterSelectInfoRequest", token)
            .ConfigureAwait(false);
        Roster = LoginWire.ParseCharacterSelectInfoReply(rosterReply.Bytes);
        Milestones.Note(HarnessMilestone.CharacterSelectInfoReplyReceived, $"{Roster.Characters.Count} character(s)");

        if (Roster.Characters.Count == 0 && _options.CreateCharacterIfEmpty is { } characterName)
        {
            _login.Send(LoginWire.CreateCharacter(characterName));
            ObservedPacket created = await ExpectLoginAsync(0x06, TimeSpan.FromSeconds(15), "CharacterCreateRequest", token)
                .ConfigureAwait(false);
            if (created.Bytes.Length < 13 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(created.Bytes.AsSpan(1)) != 1)
                throw new InvalidOperationException("QA character creation was refused.");
            _login.Send(LoginWire.CharacterSelectInfoRequest());
            rosterReply = await ExpectLoginAsync(LoginOpcodes.CharacterSelectInfoReply, TimeSpan.FromSeconds(15), "refreshed QA roster", token)
                .ConfigureAwait(false);
            Roster = LoginWire.ParseCharacterSelectInfoReply(rosterReply.Bytes);
        }

        RosterCharacter character = SelectCharacter(Roster);

        // The 1.3 s here is a person clicking PLAY on character select, not a protocol delay.
        await Clock.DelayAsync(_options.Timings.CharacterLoginClick, token).ConfigureAwait(false);
        _login.Send(LoginWire.CharacterLoginRequest(
            character.EntityKey, character.ServerId, AugustLoginContext.Bytes));
        Milestones.Note(HarnessMilestone.CharacterLoginRequestSent, $"entity {character.EntityKey:x}");

        ObservedPacket loginReply = await ExpectLoginAsync(
            LoginOpcodes.CharacterLoginReply, TimeSpan.FromSeconds(10), "CharacterLoginRequest", token)
            .ConfigureAwait(false);
        CharacterLoginOutcome outcome = LoginWire.ParseCharacterLoginReply(loginReply.Bytes);
        if (!outcome.Succeeded)
        {
            throw HarnessAssertion.ServerFailed(
                $"CharacterLoginReply came back with status {outcome.Status} and "
                + $"{(outcome.Gateway is null ? "no" : "a")} gateway handoff",
                "docs/71 §2 step 9, docs/05", Milestones, Journal);
        }

        Handoff = outcome.Gateway!;
        _selfGuid = Handoff.Guid;
        Milestones.Note(HarnessMilestone.CharacterLoginReplyReceived,
            $"gateway '{Handoff.Address}' cipher={Handoff.CipherMode} key={Handoff.Key.Length} B");

        if (!Handoff.UsesRc4)
        {
            throw HarnessAssertion.ServerFailed(
                $"the gateway handoff asked for cipher mode {Handoff.CipherMode}; the August client arms RC4 (mode 3)",
                "docs/05, docs/71 §3", Milestones, Journal);
        }

        return Handoff;
    }

    // ---- Phase B: the gateway handoff and the RC4 arming point (docs/71 §3) --------------------

    /// <summary>
    /// Opens the gateway link, sends the one clear LoginRequest and arms RC4 immediately after it,
    /// then checks that the server's reply decrypts at inbound keystream position 0. That check is
    /// the arming test: if the server encrypted its reply too late or too early, the reply would
    /// not decrypt to <c>02 01</c> here.
    /// </summary>
    public async Task ConnectGatewayAsync(CancellationToken cancellationToken = default)
    {
        GatewayHandoff handoff = Handoff
            ?? throw new InvalidOperationException("LoginAsync must succeed before ConnectGatewayAsync.");

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = linked.Token;

        await Clock.DelayAsync(_options.Timings.GatewaySessionRequest, token).ConfigureAwait(false);

        IPEndPoint gatewayEndPoint = _options.GatewayEndPointOverride ?? ParseEndPoint(handoff.Address);
        _gateway = await SoeClientSession.OpenAsync(
            gatewayEndPoint,
            new SoeClientOptions
            {
                ProtocolName = AugustClient.GatewayProtocolName,
                LinkName = "ExternalGatewayApi_3",
                IdleTickIntervalMs = _options.LowFrequencyIdlePolling ? 100 : 10,
            },
            Clock, Journal, m => ObservedPacket.ParseGateway(m).Name, token).ConfigureAwait(false);
        Milestones.Note(HarnessMilestone.GatewaySessionOpen, $"session {_gateway.SessionId:x8} to {gatewayEndPoint}");

        Responder = new ClientResponder(
            _options.Timings, handoff.Guid, _options.PlayerMovement, _options.ManagedMovement, _options.Seed);
        Responder.EnterGateway(Clock.Now);

        // The pump must outlive this method, so it gets the client's own shutdown token. Handing
        // it `token` — which comes from the linked source disposed at the end of this scope —
        // leaves it spinning on a token that can never be cancelled, and DisposeAsync then waits
        // for it forever.
        _gatewayPump = Task.Run(() => PumpGatewayAsync(_shutdown.Token), CancellationToken.None);

        await Clock.DelayAsync(_options.Timings.GatewayLoginRequest, token).ConfigureAwait(false);
        _gateway.SendThenArmEncryption(
            GatewayWire.LoginRequest(handoff.Guid, handoff.Ticket, AugustClient.Protocol, AugustClient.Version),
            handoff.Key,
            "Gateway.LoginRequest");
        Milestones.Note(HarnessMilestone.GatewayLoginRequestSentClear);
        Milestones.Note(HarnessMilestone.GatewayRc4Armed);

        MilestoneRecord? reply = await Milestones
            .WaitAsync(HarnessMilestone.GatewayLoginReplyDecrypted, 1,
                MilestoneCatalogue.For(HarnessMilestone.GatewayLoginReplyDecrypted)!.Budget, token)
            .ConfigureAwait(false);
        if (reply is null)
        {
            throw Missed(HarnessMilestone.GatewayLoginReplyDecrypted, "the clear Gateway.LoginRequest",
                MilestoneCatalogue.For(HarnessMilestone.GatewayLoginReplyDecrypted)!.Budget,
                "The gateway reply either never arrived or did not decrypt to 02 01, which means the "
                + "server armed RC4 at a different point than the client does (docs/71 §3).");
        }
    }

    /// <summary>Login then gateway, in one call.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await LoginAsync(cancellationToken).ConfigureAwait(false);
        await ConnectGatewayAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Driving the modelled client ----------------------------------------------------------

    /// <summary>The PLAY click that starts a world transfer (docs/71 §6).</summary>
    public void ClickPlay(uint worldId = 1)
    {
        RequireResponder().ClickPlay(Clock.Now, worldId);
        _options.Log?.Invoke("PLAY clicked");
    }

    /// <summary>The logout burst (docs/71 §11), followed by the SOE Disconnect 2.00 s later.</summary>
    public async Task QuitAsync(uint playSeconds = 192, CancellationToken cancellationToken = default)
    {
        RequireResponder().Quit(Clock.Now, playSeconds);
        await Milestones.WaitAsync(HarnessMilestone.LogoutSent, 1, TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        await Clock.DelayAsync(_options.Timings.LogoutSilence, cancellationToken).ConfigureAwait(false);
        _gateway?.Disconnect();
        _login?.Send(LoginWire.Logout());
        Milestones.Note(HarnessMilestone.LoginLogoutSent);
    }

    /// <summary>
    /// Waits for a client milestone with a deadline, exactly as the server-side
    /// ClientProgressWatchdog does. Throws a report naming what was expected, how far the client
    /// got, what the server last said, and the last packets both ways.
    /// </summary>
    public async Task<MilestoneRecord> ExpectAsync(
        HarnessMilestone milestone,
        TimeSpan? budget = null,
        int occurrence = 1,
        string? after = null,
        CancellationToken cancellationToken = default)
    {
        MilestoneDefinition? definition = MilestoneCatalogue.For(milestone);
        TimeSpan window = budget ?? definition?.Budget
            ?? throw new ArgumentException(
                $"{milestone} has no docs/71 §13 budget, so a scenario must state one.", nameof(budget));

        TimeSpan startedAt = Clock.Now;
        MilestoneRecord? record = await Milestones
            .WaitAsync(milestone, occurrence, window, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            return record;
        }

        IReadOnlyList<string> missing = Responder?.MissingPreconditions() ?? [];
        string? note = missing.Count == 0
            ? LinkFault()
            : "the server had not delivered: " + string.Join("; ", missing);

        throw HarnessAssertion.Missed(
            milestone,
            after ?? definition?.After ?? "the previous step",
            window,
            Clock.Now - startedAt,
            Milestones,
            Journal,
            definition,
            note);
    }

    /// <summary>The §13 row by id: <c>ExpectAsync("F1")</c> reads as the contract does.</summary>
    public Task<MilestoneRecord> ExpectAsync(string catalogueId, int occurrence = 1, CancellationToken cancellationToken = default)
    {
        MilestoneDefinition definition = MilestoneCatalogue.ById(catalogueId);
        return ExpectAsync(definition.Milestone, definition.Budget, occurrence, definition.After, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_gatewayPump is not null)
        {
            try
            {
                // Bounded: a harness that cannot be disposed would hang the test runner, which is
                // a worse failure than the one it was measuring.
                await _gatewayPump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Expected on shutdown, or the pump is wedged; the links are closed below anyway.
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

    private ClientResponder RequireResponder() => Responder
        ?? throw new InvalidOperationException("ConnectGatewayAsync must run before the client can act.");

    /// <summary>
    /// The gateway loop: parse each server message, feed the state machine, send whatever the
    /// modelled client owes, and note the milestone it represents.
    /// </summary>
    private async Task PumpGatewayAsync(CancellationToken cancellationToken)
    {
        SoeClientSession gateway = _gateway!;
        ClientResponder responder = Responder!;
        var reader = gateway.Messages;
        Task<bool>? ready = null;
        Task? timer = null;
        int timerInterval = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int handled = 0;
                while (handled < 256 && reader.TryRead(out InboundMessage? inbound))
                {
                    HandleServerMessage(gateway, responder, inbound);
                    handled++;
                }

                Flush(gateway, responder);

                // A continuously busy crowd must not monopolize a worker or starve responses.
                if (handled == 256)
                {
                    await Task.Yield();
                    continue;
                }

                // One read and one timer at a time; normal idle polling must not throw thousands
                // of cancellation exceptions per second or accumulate abandoned channel waits.
                ready ??= reader.WaitToReadAsync(cancellationToken).AsTask();
                int interval = _options.LowFrequencyIdlePolling && responder.Phase == ClientPhase.Menu ? 100 : 5;
                if (timer is null || timerInterval != interval)
                {
                    timerInterval = interval;
                    timer = Task.Delay(interval, cancellationToken);
                }
                await Task.WhenAny(ready, timer).ConfigureAwait(false);
                if (ready.IsCompleted)
                {
                    if (!await ready.ConfigureAwait(false))
                    {
                        Milestones.Note(HarnessMilestone.LinkClosed, gateway.Fault ?? gateway.CloseCause.ToString());
                        return;
                    }
                    ready = null;
                }
                if (timer.IsCompleted) timer = null;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (ChannelClosedException)
            {
                Milestones.Note(HarnessMilestone.LinkClosed, gateway.Fault ?? gateway.CloseCause.ToString());
                return;
            }
            catch (Exception ex)
            {
                // A pump that dies silently would look exactly like a hung client, which is the
                // one failure this harness must never fake. Record it as a milestone and stop.
                Milestones.Note(HarnessMilestone.LinkClosed, $"harness pump failed: {ex}");
                return;
            }
        }
    }

    private void HandleServerMessage(SoeClientSession gateway, ClientResponder responder, InboundMessage inbound)
    {
        ObservedPacket packet = ObservedPacket.ParseGateway(inbound.Bytes);
        if (_options.RetainLedger) Ledger.Observe(packet, Clock.Now);
        _options.ObservePacket?.Invoke(packet, Clock.Now);

        if (packet.Kind == ObservedKind.GatewayControl
            && packet.GatewayOpcode == GatewayWire.OpcodeLoginReply
            && GatewayWire.TryParseLoginReply(inbound.Bytes, out bool loggedIn)
            && loggedIn)
        {
            Milestones.Note(HarnessMilestone.GatewayLoginReplyDecrypted,
                $"decrypted at inbound keystream position {inbound.KeystreamPosition}");
            return;
        }

        if (packet.Kind == ObservedKind.ZoneTunnel && packet.ZoneOpcode is byte opcode)
        {
            switch (opcode)
            {
                case ZoneWire.ZoneDoneSendingInitialData:
                    Milestones.Note(
                        responder.Phase == ClientPhase.Zoning
                            ? HarnessMilestone.ZoningZoneDataComplete
                            : HarnessMilestone.MenuZoneDataComplete);
                    break;

                case ZoneWire.ClientBeginZoning:
                    Milestones.Note(HarnessMilestone.ZoningBegun, $"zone '{responder.ZoneName}'");
                    break;

                case ZoneWire.PlayerWorldTransferReply:
                    Milestones.Note(HarnessMilestone.TransferReplyReceived);
                    break;

                case ZoneWire.SynchronizedTeleportBase when packet.Payload.Length >= 2 && packet.Payload.Span[1] == 0x03:
                    Milestones.Note(HarnessMilestone.TeleportStartReceived);
                    break;

                case ZoneWire.Synchronization:
                    Milestones.Note(HarnessMilestone.SynchronizationEchoed);
                    break;

                case ZoneWire.ProximateItemBase:
                    Milestones.Note(HarnessMilestone.ProximateItemsReceived, $"{packet.Bytes.Length} B");
                    break;

                case ZoneWire.MountBase:
                    if (Milestones.Has(HarnessMilestone.AutoMountEchoSent))
                    {
                        Milestones.Note(HarnessMilestone.MountConfirmed);
                    }

                    break;
            }
        }

        responder.OnServerMessage(packet, Clock.Now);
        Flush(gateway, responder);
    }

    private void Flush(SoeClientSession gateway, ClientResponder responder)
    {
        foreach (ScheduledMessage message in responder.Drain(Clock.Now))
        {
            gateway.Send(message.Bytes, message.Description);
            if (message.Milestone is HarnessMilestone milestone)
            {
                Milestones.Note(milestone, message.Description);
                _options.Log?.Invoke($"{Clock.Now.TotalSeconds,8:F3}s  {milestone}");
            }
        }
    }

    private async Task<ObservedPacket> ExpectLoginAsync(
        byte opcode,
        TimeSpan budget,
        string after,
        CancellationToken cancellationToken)
    {
        SoeClientSession login = _login!;
        TimeSpan startedAt = Clock.Now;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Clock.Scaled(budget));

        try
        {
            while (await login.Messages.WaitToReadAsync(deadline.Token).ConfigureAwait(false))
            {
                while (login.Messages.TryRead(out InboundMessage? inbound))
                {
                    ObservedPacket packet = ObservedPacket.ParseLogin(inbound.Bytes);
                    if (packet.ZoneOpcode == opcode)
                    {
                        return packet;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Deadline; fall through to the report.
        }
        catch (ChannelClosedException)
        {
            // Link closed; fall through to the report.
        }

        throw HarnessAssertion.ServerFailed(
            $"no {LoginOpcodes.Name(opcode)} arrived within {budget.TotalSeconds:F1} s of the client's {after}"
            + (login.Fault is null ? string.Empty : $" (link: {login.Fault})"),
            "docs/71 §2",
            Milestones,
            Journal);
    }

    private HarnessAssertionException Missed(
        HarnessMilestone milestone, string after, TimeSpan budget, string? note) =>
        HarnessAssertion.Missed(milestone, after, budget, budget, Milestones, Journal,
            MilestoneCatalogue.For(milestone), note);

    private string? LinkFault()
    {
        string? gateway = _gateway?.Fault;
        if (gateway is not null)
        {
            return $"the gateway link is gone: {gateway}";
        }

        if (_gateway?.CloseCause is LinkCloseCause cause && cause != LinkCloseCause.None)
        {
            return $"the gateway link closed ({cause})";
        }

        return null;
    }

    private RosterCharacter SelectCharacter(CharacterSelectInfo roster)
    {
        RosterCharacter[] available =
            [.. roster.Characters.Where(c => c.Status == RosterCharacter.StatusAvailable)];
        if (available.Length == 0)
        {
            throw HarnessAssertion.ServerFailed(
                $"CharacterSelectInfoReply advertised {roster.Characters.Count} character(s), none of them available",
                "docs/71 §2 step 7", Milestones, Journal);
        }

        if (_options.CharacterIndex < 0 || _options.CharacterIndex >= available.Length)
        {
            throw HarnessAssertion.ServerFailed(
                $"CharacterIndex {_options.CharacterIndex} is outside the {available.Length} available character(s)",
                "docs/71 §2 step 7", Milestones, Journal);
        }

        return available[_options.CharacterIndex];
    }

    private static IPEndPoint ParseEndPoint(string address)
    {
        if (!IPEndPoint.TryParse(address, out IPEndPoint? endPoint))
        {
            throw new WireFormatException($"The gateway handoff address '{address}' is not an IP endpoint.");
        }

        if (endPoint.Address.Equals(IPAddress.Any))
        {
            endPoint = new IPEndPoint(IPAddress.Loopback, endPoint.Port);
        }

        return endPoint;
    }
}
