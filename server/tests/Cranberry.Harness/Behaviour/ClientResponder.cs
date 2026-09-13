using Cranberry.Harness.Protocol;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Behaviour;

/// <summary>Where the modelled client is in the flow.</summary>
public enum ClientPhase
{
    Connecting,
    MenuBootstrap,
    Menu,
    Transferring,
    Zoning,
    InWorld,
    Mounted,
    LeavingWorld,
}

/// <summary>One message the client will send, and when.</summary>
public sealed record ScheduledMessage(
    TimeSpan DueAt,
    byte[] Bytes,
    string Description,
    HarnessMilestone? Milestone = null);

/// <summary>
/// The client's half of the protocol as a state machine, with no sockets in it.
///
/// It <b>parses the server's replies</b> and its transitions are guarded by what those replies
/// contained — that is the whole point. The client does not become ready because a timer fired; it
/// becomes ready because the server delivered a zone, a self record and a done-marker, in that
/// order. A server that omits one of those never gets a <see cref="HarnessMilestone.MenuClientIsReadySent"/>
/// or a <see cref="HarnessMilestone.ZoningClientIsReadySent"/>, and the missed-milestone report
/// names the precondition that was absent (<see cref="MissingPreconditions"/>).
///
/// Being socket-free makes the state machine unit-testable: feed it <see cref="ObservedPacket"/>s
/// and a clock and assert on what it would send.
///
/// <para><b>What this cannot do.</b> docs/71 §14.6 records that the cause of the Z2 hang is not
/// isolated from the wire — the failed and successful zoning bursts differ in four server-side
/// ways and the captures show correlation only. So this responder models a <i>healthy</i> client:
/// it will emit ClientIsReady whenever the server's burst is complete and well-formed. A missed F1
/// here therefore means "the server's zoning burst was incomplete", not "the real client would
/// have hung". It catches the whole class of bug where the server never sends the preconditions;
/// it cannot yet reproduce a hang whose trigger is unknown.</para>
/// </summary>
public sealed class ClientResponder
{
    private readonly ClientTimings _timings;
    private readonly ulong _selfGuid;
    private MovementReplay _playerMovement;
    private readonly MovementReplay _managedMovement;
    private readonly List<ScheduledMessage> _queue = [];
    private readonly Random _random;

    // What the server has delivered. These are the guards.
    private bool _zoneDetailsSeen;
    private uint _zoneType;
    private string _zoneName = string.Empty;
    private bool _selfSeen;
    private bool _zoneDoneSeen;
    private bool _menuBootstrapStarted;

    private TimeSpan? _beginZoningAt;
    private bool _zoningSelfSeen;
    private bool _zoningZoneDoneSeen;
    private bool _zoningBootstrapStarted;

    private byte[]? _pendingAutoMount;
    private bool _mountedLoadingSent;
    private bool _mountEchoScheduled;

    private bool _keepAliveRunning;
    private TimeSpan _nextKeepAlive;
    private TimeSpan _nextSynchronization;
    private TimeSpan _nextGameTimeSync;
    private TimeSpan _nextClientMetrics;

    private bool _playerMovementRunning;
    private TimeSpan _nextPlayerMovement;
    private bool _managedMovementRunning;
    private TimeSpan _nextManagedMovement;

    private uint _tickBase;
    private TimeSpan _startedAt;

    public ClientResponder(
        ClientTimings timings,
        ulong selfGuid,
        MovementReplay? playerMovement = null,
        MovementReplay? managedMovement = null,
        int? seed = null)
    {
        _timings = timings;
        _selfGuid = selfGuid;
        _playerMovement = playerMovement ?? MovementReplay.PlayerMovement();
        _managedMovement = managedMovement ?? MovementReplay.ManagedMovement();
        _random = seed is null ? new Random() : new Random(seed.Value);
        _tickBase = (uint)_random.Next(1 << 20, 1 << 28);
    }

    public ClientPhase Phase { get; private set; } = ClientPhase.Connecting;

    /// <summary>The zone the server named in SendZoneDetails.</summary>
    public string ZoneName => _zoneName;

    /// <summary>
    /// The zone type from SendZoneDetails. docs/06/07: the client registers a world implementation
    /// for type 4 (HeightfieldLod) only; every other value fails PostInitialize.
    /// </summary>
    public uint ZoneType => _zoneType;

    /// <summary>
    /// Replaces the channel-2 pool the modelled client replays from.
    ///
    /// <para>Recorded movement bytes carry the coordinates of the session they were recorded in,
    /// and the server believes them: it takes the ground-loot, door and vehicle centre from the
    /// position the client reports. A scenario that needs the modelled player to be standing
    /// somewhere real in Z2 — a landing, a walk — therefore has to choose a pool recorded there,
    /// and swap to it at the moment that becomes true. Nothing is synthesised either way; this only
    /// chooses which real client's bytes are replayed (docs/71 §9, §14.3).</para>
    /// </summary>
    public void UsePlayerMovement(MovementReplay replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        _playerMovement = replay;
    }

    /// <summary>
    /// Whether the modelled client answers a zoning burst at all.
    ///
    /// <para>Set to false it reproduces the shape of the four-hour outage from the <b>server's</b>
    /// point of view: a client that takes ClientBeginZoning, sends the WallOfData teardown, the
    /// 0x57 and the single 49-byte channel-2 packet a hung client also sends, and then never sends
    /// ClientIsReady (docs/71 §12.1). It does not reproduce the <i>cause</i> — docs/71 §14.6 leaves
    /// that unisolated — so it is only ever a probe of what the server does next, which is exactly
    /// what docs/32 fix 3 is about: the lobby HUD, StartMatch and the parachute must not be sent to
    /// a client that is still on its loading screen.</para>
    /// </summary>
    public bool AnswerZoning { get; set; } = true;

    /// <summary>The AutoMount packet the server sent, if one has arrived.</summary>
    public byte[]? PendingAutoMount => _pendingAutoMount;

    /// <summary>Messages waiting to be sent, for diagnostics.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Which of the preconditions for the next ClientIsReady the server has not delivered. Empty
    /// when the client is not blocked. This is what turns "milestone missed" into a diagnosis.
    /// </summary>
    public IReadOnlyList<string> MissingPreconditions()
    {
        var missing = new List<string>();
        if (_beginZoningAt is not null)
        {
            if (!_zoningSelfSeen)
            {
                missing.Add("SendSelfToClient after ClientBeginZoning (docs/09/10)");
            }

            if (!_zoningZoneDoneSeen)
            {
                missing.Add("ZoneDoneSendingInitialData after ClientBeginZoning (the client's starting gun)");
            }

            return missing;
        }

        if (!_zoneDetailsSeen)
        {
            missing.Add("SendZoneDetails (docs/06/07)");
        }
        else if (_zoneType != 4)
        {
            missing.Add($"SendZoneDetails carried world type {_zoneType}; the client only registers a world for type 4");
        }

        if (!_selfSeen)
        {
            missing.Add("SendSelfToClient (docs/09/10)");
        }

        if (!_zoneDoneSeen)
        {
            missing.Add("ZoneDoneSendingInitialData (the client's starting gun)");
        }

        return missing;
    }

    /// <summary>Called once the gateway link is up and the client is waiting for the bootstrap.</summary>
    public void EnterGateway(TimeSpan now)
    {
        _startedAt = now;
        Phase = ClientPhase.MenuBootstrap;
        _nextKeepAlive = now;
        _nextSynchronization = now;
        _nextGameTimeSync = now;
        _nextClientMetrics = now;
    }

    /// <summary>Feeds one server message to the state machine.</summary>
    public void OnServerMessage(ObservedPacket packet, TimeSpan now)
    {
        if (packet.Kind != ObservedKind.ZoneTunnel || packet.ZoneOpcode is not byte opcode)
        {
            return;
        }

        switch (opcode)
        {
            case ZoneWire.SendZoneDetails:
                ReadZoneDetails(packet);
                break;

            case ZoneWire.SendSelfToClient:
                if (_beginZoningAt is not null)
                {
                    _zoningSelfSeen = true;
                }
                else
                {
                    _selfSeen = true;
                }

                break;

            case ZoneWire.ClientBeginZoning:
                BeginZoning(now);
                break;

            case ZoneWire.ZoneDoneSendingInitialData:
                if (_beginZoningAt is not null)
                {
                    _zoningZoneDoneSeen = true;
                    TryStartZoningBootstrap(now);
                }
                else
                {
                    _zoneDoneSeen = true;
                    TryStartMenuBootstrap(now);
                }

                break;

            case ZoneWire.SynchronizedTeleportBase:
                if (packet.Payload.Length >= 2 && packet.Payload.Span[1] == 0x03)
                {
                    StartTeleportHandshake(now);
                }

                break;

            case ZoneWire.VehicleBase:
                if (packet.Payload.Length >= 2 && packet.Payload.Span[1] == 0x19)
                {
                    _pendingAutoMount = packet.Bytes;
                    TryScheduleMountEcho(now);
                }

                break;
        }
    }

    /// <summary>
    /// The PLAY click. The client sends the transfer request once now and once 4.542 s later on
    /// its own timer, whatever the server does in between (docs/71 §6).
    /// </summary>
    public void ClickPlay(TimeSpan now, uint worldId = 1)
    {
        Phase = ClientPhase.Transferring;
        Enqueue(now, ZoneClientMessages.PlayerWorldTransferRequest(worldId),
            "PlayerWorldTransferRequest (PLAY)", HarnessMilestone.TransferRequestSent);
        Enqueue(now + _timings.TransferRequestRetry, ZoneClientMessages.PlayerWorldTransferRequest(worldId),
            "PlayerWorldTransferRequest (client retry timer, +4.542 s)", HarnessMilestone.TransferRequestResent);
    }

    /// <summary>The logout burst of docs/71 §11.</summary>
    public void Quit(TimeSpan now, uint playSeconds)
    {
        Phase = ClientPhase.LeavingWorld;
        _playerMovementRunning = false;
        _managedMovementRunning = false;
        _keepAliveRunning = false;

        TimeSpan at = now;
        foreach (string window in new[] { "InventoryWindow", "TabNavigationWindow", "TabNavigationBackground", "HudWindow" })
        {
            Enqueue(at, ZoneClientMessages.WindowEvent(window, "close"), $"WindowEvent {window} close");
            at += TimeSpan.FromMilliseconds(2);
        }

        Enqueue(at, ZoneClientMessages.PlayLength(playSeconds), "PlayLength");
        Enqueue(at + TimeSpan.FromMilliseconds(8), ZoneClientMessages.PlayLength(playSeconds), "PlayLength (again)");
        Enqueue(at + TimeSpan.FromMilliseconds(15), ZoneClientMessages.ClientLogout(), "ClientLogout",
            HarnessMilestone.LogoutSent);
    }

    /// <summary>Everything due at <paramref name="now"/>, oldest first. Also advances the free-running timers.</summary>
    public IReadOnlyList<ScheduledMessage> Drain(TimeSpan now)
    {
        PumpTimers(now);

        var due = new List<ScheduledMessage>();
        for (int i = 0; i < _queue.Count;)
        {
            if (_queue[i].DueAt <= now)
            {
                due.Add(_queue[i]);
                _queue.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }

        due.Sort((a, b) => a.DueAt.CompareTo(b.DueAt));
        foreach (ScheduledMessage message in due)
        {
            OnDequeued(message, now);
        }

        return due;
    }

    private void OnDequeued(ScheduledMessage message, TimeSpan now)
    {
        switch (message.Milestone)
        {
            case HarnessMilestone.MenuClientFinishedLoadingSent:
                Phase = ClientPhase.Menu;
                // Start the 1 Hz pair from here rather than from EnterGateway, so turning the
                // timers on does not immediately flush every tick since the link opened.
                _keepAliveRunning = true;
                _nextKeepAlive = now;
                _nextSynchronization = now;
                _nextGameTimeSync = now;
                _nextClientMetrics = now;
                break;

            case HarnessMilestone.ZoningClientIsReadySent:
                Phase = ClientPhase.InWorld;
                break;

            case HarnessMilestone.MountedClientFinishedLoadingSent:
                _mountedLoadingSent = true;
                TryScheduleMountEcho(now);
                break;

            case HarnessMilestone.AutoMountEchoSent:
                Phase = ClientPhase.Mounted;
                if (_timings.ReplayMovement)
                {
                    _managedMovementRunning = true;
                    _nextManagedMovement = now + _timings.ManagedMovementInterval;
                }

                break;

            case HarnessMilestone.ZoningMovementResumed:
                if (_timings.ReplayMovement)
                {
                    _playerMovementRunning = true;
                    _nextPlayerMovement = now + _timings.PlayerMovementInterval;
                }

                break;
        }
    }

    private void PumpTimers(TimeSpan now)
    {
        if (_keepAliveRunning)
        {
            while (_nextKeepAlive <= now)
            {
                uint tick = Tick(_nextKeepAlive);
                Enqueue(_nextKeepAlive, ZoneClientMessages.KeepAlive(tick), "KeepAlive",
                    HarnessMilestone.KeepAlivePairSent);
                Enqueue(_nextKeepAlive, ZoneClientMessages.MonitorTimeDrift(), "MonitorTimeDrift");
                _nextKeepAlive += _timings.KeepAliveInterval;
            }

            while (_nextSynchronization <= now)
            {
                Enqueue(_nextSynchronization,
                    ZoneClientMessages.Synchronization(Tick(_nextSynchronization), UnixNow()),
                    "Synchronization", HarnessMilestone.SynchronizationSent);
                _nextSynchronization += _timings.SynchronizationInterval;
            }

            while (_nextGameTimeSync <= now)
            {
                Enqueue(_nextGameTimeSync, ZoneClientMessages.GameTimeSync(UnixNow()),
                    "GameTimeSync (free-running; NOT evidence of health)", HarnessMilestone.GameTimeSyncSent);
                _nextGameTimeSync += _timings.GameTimeSyncInterval;
            }

            while (_nextClientMetrics <= now)
            {
                Enqueue(_nextClientMetrics, ZoneClientMessages.ClientMetrics(), "ClientMetrics");
                _nextClientMetrics += _timings.ClientMetricsInterval;
            }
        }

        // The two movement streams are recorded bytes replayed verbatim (docs/71 §9, §14.3).
        // Cap each pump so a long stall cannot produce a burst of thousands.
        int budget = 64;
        while (_playerMovementRunning && _nextPlayerMovement <= now && budget-- > 0)
        {
            Enqueue(_nextPlayerMovement, _playerMovement.Next(), "ch2 PlayerMovement (replayed)");
            _nextPlayerMovement += _timings.PlayerMovementInterval;
        }

        budget = 32;
        while (_managedMovementRunning && _nextManagedMovement <= now && budget-- > 0)
        {
            Enqueue(_nextManagedMovement, _managedMovement.Next(), "ch3 ManagedMovement (replayed)");
            _nextManagedMovement += _timings.ManagedMovementInterval;
        }
    }

    private void ReadZoneDetails(ObservedPacket packet)
    {
        _zoneDetailsSeen = true;
        try
        {
            // 16 | str zoneName | u32 zoneType | …  (the rest is not needed here)
            var r = new WireReader(packet.Payload.Span);
            _ = r.U8();
            _zoneName = r.CountedString();
            _zoneType = r.LeU32();
        }
        catch (WireFormatException)
        {
            _zoneName = "(unparseable)";
            _zoneType = uint.MaxValue;
        }
    }

    private void TryStartMenuBootstrap(TimeSpan now)
    {
        if (_menuBootstrapStarted || !_zoneDetailsSeen || !_selfSeen || !_zoneDoneSeen)
        {
            return;
        }

        _menuBootstrapStarted = true;
        TimeSpan ping = now + _timings.PingInfoLog;
        Enqueue(ping, ZoneClientMessages.ClientLog("PingInfo.log", PingInfoBody()),
            "ClientLog PingInfo.log", HarnessMilestone.MenuPingInfoLogSent);

        TimeSpan locale = ping + _timings.SetLocale;
        Enqueue(locale, ZoneClientMessages.SetLocale(), "SetLocale (ch1)", HarnessMilestone.MenuSetLocaleSent);

        TimeSpan probe = locale + _timings.MenuProbeBurst;
        Enqueue(probe, ZoneClientMessages.ClientInitializationDetails(), "ClientInitializationDetails");
        Enqueue(probe, ZoneClientMessages.GetContinentBattleInfo(), "GetContinentBattleInfo (ch1)");
        Enqueue(probe, ZoneClientMessages.GetRewardBuffInfo(), "GetRewardBuffInfo");
        Enqueue(probe, ZoneClientMessages.Unregistered0x57(NextTag()), "unregistered 0x57",
            HarnessMilestone.Menu0x57Sent);

        TimeSpan ready = probe + _timings.MenuClientIsReady;
        Enqueue(ready, ZoneClientMessages.ClientIsReady(), "ClientIsReady (Menu)",
            HarnessMilestone.MenuClientIsReadySent);
        Enqueue(ready, ZoneClientMessages.LobbyGameDefinitionRequest(), "LobbyGameDefinitionBase request (ch1)",
            HarnessMilestone.MenuLobbyGameDefinitionRequestSent);

        TimeSpan loaded = ready + _timings.ClientFinishedLoading;
        Enqueue(loaded, ZoneClientMessages.ClientFinishedLoading(0), "ClientFinishedLoading 06 02 00",
            HarnessMilestone.MenuClientFinishedLoadingSent);
        Enqueue(loaded + TimeSpan.FromMilliseconds(1), ZoneClientMessages.UpdateBattlEyeRegistration(),
            "UpdateBattlEyeRegistration");
        Enqueue(loaded + TimeSpan.FromMilliseconds(7),
            ZoneClientMessages.ClientLog("ClientStartTime.log", "11257 ms"), "ClientLog ClientStartTime.log");

        ScheduleMenuBurst(loaded);
    }

    private void ScheduleMenuBurst(TimeSpan loaded)
    {
        Enqueue(loaded + TimeSpan.FromMilliseconds(6),
            ZoneClientMessages.WallOfDataBlob("{\"DriverVersion\":\"00000000-00000000\"}"),
            "WallOfDataBase driver blob (ch1)");
        Enqueue(loaded + TimeSpan.FromMilliseconds(7), ZoneClientMessages.WallOfDataCounter(),
            "WallOfDataBase counter (ch1)");

        TimeSpan close = loaded + _timings.LoadingScreenClose;
        Enqueue(close, ZoneClientMessages.WindowEvent("LoadingScreenWindow", "close"),
            "WindowEvent LoadingScreenWindow close", HarnessMilestone.LoadingScreenClosed);
        Enqueue(close, ZoneClientMessages.Unregistered0xF3(), "unregistered 0xF3 (ch1)");

        TimeSpan burst = close + TimeSpan.FromMilliseconds(1);
        Enqueue(burst, ZoneClientMessages.MatchHistoryRequest(1), "MatchHistoryBase 1 (ch1)");
        Enqueue(burst, ZoneClientMessages.MatchHistoryRequest(2), "MatchHistoryBase 2 (ch1)");
        Enqueue(burst, ZoneClientMessages.MatchHistoryRequest(3), "MatchHistoryBase 3 (ch1)");
        Enqueue(burst, ZoneClientMessages.StaticViewRequest("kotkdefault"), "StaticViewBase kotkdefault");

        TimeSpan purchases = burst + TimeSpan.FromMilliseconds(3);
        foreach (byte[] request in ZoneClientMessages.InGamePurchaseBurst())
        {
            Enqueue(purchases, request, "InGamePurchase request (ch1)");
        }

        Enqueue(purchases + TimeSpan.FromMilliseconds(5),
            ZoneClientMessages.ClientLog("ClientValues.log", "Total = 4, Fail = 0, Small = 0\r\n"),
            "ClientLog ClientValues.log");

        TimeSpan damage = purchases + TimeSpan.FromMilliseconds(36);
        for (uint i = 0; i < 24; i++)
        {
            Enqueue(damage + TimeSpan.FromTicks(i), ZoneClientMessages.CollisionDamage(_selfGuid, 0x40 + (i * 0x58), -31.53f, 316.43f, 279.73f),
                "CollisionBase Damage");
        }

        Enqueue(damage + TimeSpan.FromMilliseconds(1), ZoneClientMessages.FreeInteractionNpc(),
            "cCommandPacketFreeInteractionNpc", HarnessMilestone.MenuBurstSent);

        // One channel-2 packet accompanies the menu burst; the sustained stream only starts after
        // zoning (docs/71 §7).
        Enqueue(damage, MovementReplay.FirstZoningPacket(), "ch2 PlayerMovement (single, menu)");
    }

    private void BeginZoning(TimeSpan now)
    {
        Phase = ClientPhase.Zoning;
        _beginZoningAt = now;
        _zoningSelfSeen = false;
        _zoningZoneDoneSeen = false;
        _zoningBootstrapStarted = false;
        _playerMovementRunning = false;
        _managedMovementRunning = false;
        // These gates belong to a drop, not to the lifetime of the gateway link.
        // A second PLAY must acknowledge its new canopy after its own teleport handshake.
        _pendingAutoMount = null;
        _mountedLoadingSent = false;
        _mountEchoScheduled = false;

        // The HUD tearing itself down and back up: seven channel-1 WallOfData events at +0.033 s,
        // then 0x57 and exactly one 49-byte channel-2 packet at +0.067 s. Both a healthy and a
        // hung client send all of this (docs/71 §12.1), so none of it is evidence of health.
        TimeSpan wall = now + _timings.ZoningWallOfData;
        for (int i = 0; i < 7; i++)
        {
            Enqueue(wall + TimeSpan.FromTicks(i), ZoneClientMessages.WallOfDataCounter(),
                "WallOfDataBase (HUD teardown, ch1)");
        }

        // The client re-opens its own loading screen ~0.4 s in. A HUNG client sends this too
        // (wire-20260829-173352 17:36:05.041, +0.37 s), so it is modelled for fidelity and asserted
        // on by nothing; its matching close is the discriminator (F4).
        Enqueue(now + _timings.ZoningLoadingScreenOpen,
            ZoneClientMessages.WindowEvent("LoadingScreenWindow", "open"),
            "WindowEvent LoadingScreenWindow open (zoning, ch1)");

        TimeSpan tag = now + _timings.Zoning0x57;
        Enqueue(tag, ZoneClientMessages.Unregistered0x57(NextTag()), "unregistered 0x57 (zoning)",
            HarnessMilestone.Zoning0x57Sent);
        Enqueue(tag, MovementReplay.FirstZoningPacket(),
            "ch2 PlayerMovement (the single 49 B packet a hung client also sends)",
            HarnessMilestone.ZoningFirstMovementSent);
        Enqueue(now + TimeSpan.FromMilliseconds(420), ZoneClientMessages.WallOfDataCounter(),
            "WallOfDataBase (ch1)");
    }

    private void TryStartZoningBootstrap(TimeSpan now)
    {
        if (!AnswerZoning || _zoningBootstrapStarted || _beginZoningAt is not TimeSpan begun
            || !_zoningSelfSeen || !_zoningZoneDoneSeen)
        {
            return;
        }

        _zoningBootstrapStarted = true;

        // Anchored on ClientBeginZoning, not on "now": the 1.970–2.926 s window is measured from
        // the zoning packet, and a slow server burst must eat into that budget rather than push
        // the deadline out.
        TimeSpan ready = Later(begun + _timings.ZoningClientIsReady, now);
        Enqueue(ready, ZoneClientMessages.ClientIsReady(), "ClientIsReady (Zoning)",
            HarnessMilestone.ZoningClientIsReadySent);
        Enqueue(ready, ZoneClientMessages.LobbyGameDefinitionRequest(), "LobbyGameDefinitionBase request (ch1)");

        TimeSpan loaded = Later(begun + TimeSpan.FromMilliseconds(3050), ready + TimeSpan.FromMilliseconds(20));
        Enqueue(loaded, ZoneClientMessages.ClientLog("PingInfo.log", PingInfoBody()),
            "ClientLog PingInfo.log (second upload)");
        Enqueue(loaded, ZoneClientMessages.ClientFinishedLoading(0), "ClientFinishedLoading 06 02 00 (zoning)",
            HarnessMilestone.ZoningClientFinishedLoadingSent);
        Enqueue(loaded + TimeSpan.FromMilliseconds(1), ZoneClientMessages.UpdateBattlEyeRegistration(),
            "UpdateBattlEyeRegistration");

        // docs/32's own acceptance check, and a second Z2-hang discriminator as strong as F1: the
        // healthy client closes the loading screen ~3.1 s after ClientBeginZoning (2 of 2 good
        // sessions at +3.055 s and +3.891 s), while in 6 of 6 hung zonings the next close does not
        // arrive until the client gives up and logs out, 27-54 s later.
        Enqueue(loaded + _timings.LoadingScreenCloseAfterLoad,
            ZoneClientMessages.WindowEvent("LoadingScreenWindow", "close"),
            "WindowEvent LoadingScreenWindow close (zoning, ch1)",
            HarnessMilestone.LoadingScreenClosed);

        _keepAliveRunning = true;
        _nextKeepAlive = Later(begun + TimeSpan.FromMilliseconds(3160), now);
        _nextSynchronization = _nextKeepAlive;

        TimeSpan resume = Later(begun + _timings.ZoningMovementResume, loaded + TimeSpan.FromMilliseconds(20));
        Enqueue(resume, _playerMovement.Next(),
            "ch2 PlayerMovement (the SECOND packet — 40 of 40 good zonings, 0 of 12 hung)",
            HarnessMilestone.ZoningMovementResumed);
    }

    private void StartTeleportHandshake(TimeSpan now)
    {
        // The third loading screen of a match: open on the teleport start, close just after the ack
        // (wire-20260829-150206 15:03:22.665 / 15:03:26.158; -184346 18:45:04.869 / 18:45:06.378).
        Enqueue(now + _timings.TeleportLoadingScreenOpen,
            ZoneClientMessages.WindowEvent("LoadingScreenWindow", "open"),
            "WindowEvent LoadingScreenWindow open (teleport, ch1)");

        Enqueue(now + TimeSpan.FromMilliseconds(13), ZoneClientMessages.VoiceBase(), "VoiceBase (ch1)");
        Enqueue(now + TimeSpan.FromMilliseconds(13), ZoneClientMessages.FreeInteractionNpc(),
            "cCommandPacketFreeInteractionNpc");
        Enqueue(now + TimeSpan.FromMilliseconds(19),
            ZoneClientMessages.ClientLog("ClientSynchronizedTeleport.log",
                "BaseClient::StartWaitForTeleport - Player entering wait for teleport state"),
            "ClientLog ClientSynchronizedTeleport.log");

        TimeSpan ack = now + _timings.TeleportAck;
        Enqueue(ack, ZoneClientMessages.SynchronizedTeleportAck(), "SynchronizedTeleportBase ack 06 E8 02 00",
            HarnessMilestone.TeleportAckSent);
        Enqueue(ack + TimeSpan.FromMilliseconds(10),
            ZoneClientMessages.ClientLog("ClientSynchronizedTeleport.log",
                "BaseClient::WaitForTeleport - releasing local player, mount guid is 0"),
            "ClientLog ClientSynchronizedTeleport.log");
        Enqueue(ack + TimeSpan.FromMilliseconds(10), ZoneClientMessages.ClientFinishedLoading(1),
            "ClientFinishedLoading 06 02 01 (tail 01, mounted)",
            HarnessMilestone.MountedClientFinishedLoadingSent);
        Enqueue(ack + TimeSpan.FromMilliseconds(20),
            ZoneClientMessages.WindowEvent("LoadingScreenWindow", "close"),
            "WindowEvent LoadingScreenWindow close (teleport, ch1)",
            HarnessMilestone.LoadingScreenClosed);
        Enqueue(ack + TimeSpan.FromMilliseconds(24), ZoneClientMessages.InGamePurchaseRequest(0x000A),
            "WalletInfoRequest (ch1)");
    }

    private void TryScheduleMountEcho(TimeSpan now)
    {
        // Ordered, not immediate: the echo always follows the teleport ack and the 06 02 01
        // ClientFinishedLoading, 1.545–11.796 s after the server's AutoMount (docs/71 §8).
        if (_mountEchoScheduled || _pendingAutoMount is null || !_mountedLoadingSent)
        {
            return;
        }

        _mountEchoScheduled = true;
        Enqueue(now + _timings.MountEcho, ZoneClientMessages.VehicleAutoMountEcho(_pendingAutoMount),
            "VehicleAutoMount echo (server bytes, header 05→06, byte 11 01→00)",
            HarnessMilestone.AutoMountEchoSent);
        Enqueue(now + _timings.MountEcho + TimeSpan.FromMilliseconds(28),
            ZoneClientMessages.VehicleCurrentMoveMode(MountGuid(_pendingAutoMount), 5), "VehicleCurrentMoveMode");
        Enqueue(now + _timings.MountEcho + TimeSpan.FromMilliseconds(29),
            ZoneClientMessages.VehicleCurrentMoveMode(MountGuid(_pendingAutoMount), 1), "VehicleCurrentMoveMode");
    }

    private static ulong MountGuid(byte[] autoMount)
    {
        // 06 88 19 | u64 guid | …
        if (autoMount.Length < 11)
        {
            return 0;
        }

        var r = new WireReader(autoMount.AsSpan(3));
        return r.Remaining >= 8 ? r.LeU64() : 0;
    }

    private void Enqueue(TimeSpan dueAt, byte[] bytes, string description, HarnessMilestone? milestone = null) =>
        _queue.Add(new ScheduledMessage(dueAt, bytes, description, milestone));

    private static TimeSpan Later(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private uint Tick(TimeSpan at) => _tickBase + (uint)Math.Max(0, (at - _startedAt).TotalMilliseconds);

    private uint NextTag() => (uint)_random.Next(int.MinValue, int.MaxValue);

    private static ulong UnixNow() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string PingInfoBody() =>
        "{ \"sessionCountry\":\"GB\", \"steamCountry\":\"<nullptr>\", \"currencyCode\":\"\", "
        + "\"homeDataCenter\":\"AMS\", \"source\":\"cranberry-harness\" }";
}

/// <summary>Base opcodes the responder branches on. Names are the client's own (ZoneOpcodes.g.cs).</summary>
public static class ZoneWire
{
    public const byte ClientFinishedLoading = 0x02;
    public const byte SendSelfToClient = 0x03;
    public const byte ClientIsReady = 0x04;
    public const byte ZoneDoneSendingInitialData = 0x05;
    public const byte ClientLogout = 0x07;
    public const byte CommandBase = 0x09;
    public const byte ClientBeginZoning = 0x0B;
    public const byte CharacterBase = 0x0F;
    public const byte ClientUpdateBase = 0x11;
    public const byte SendZoneDetails = 0x16;
    public const byte ReferenceData = 0x17;
    public const byte GameTimeSync = 0x1D;
    public const byte SetLocale = 0x33;
    public const byte KeepAlive = 0x3B;
    public const byte LobbyGameDefinitionBase = 0x41;
    public const byte ClientMetrics = 0x44;
    public const byte ClientLog = 0x47;
    public const byte ClientGameSettings = 0x60;
    public const byte InitializationParameters = 0x6E;
    public const byte MountBase = 0x70;
    public const byte ClientInitializationDetails = 0x71;
    public const byte VoiceBase = 0x81;
    public const byte VehicleBase = 0x88;
    public const byte Synchronization = 0x8C;
    public const byte CollisionBase = 0x8E;
    public const byte EquipmentBase = 0x94;
    public const byte WallOfDataBase = 0x9A;
    public const byte LoginBase = 0xA6;
    public const byte ItemsBase = 0xAC;
    public const byte GetRewardBuffInfo = 0xB1;
    public const byte UpdateWeatherData = 0xCA;
    public const byte AddLightweightNpc = 0xD6;
    public const byte AddLightweightVehicle = 0xD7;
    public const byte SynchronizedTeleportBase = 0xE8;
    public const byte StaticViewBase = 0xE9;
    public const byte PlayerWorldTransferRequest = 0xEC;
    public const byte PlayerWorldTransferReply = 0xED;
    public const byte ProximateItemBase = 0xF8;
    public const byte PlayLength = 0xFA;
}
