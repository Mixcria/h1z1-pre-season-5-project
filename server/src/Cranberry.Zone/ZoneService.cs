using System.Numerics;
using Cranberry.Zone.HostedGames;
using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Zone.Appearance;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Descent;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Equipment;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Emotes;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Zone;

/// <summary>
/// Owns the August client's gateway transport. Its first application packet is clear; after
/// validating the login-issued guid and ticket, both directions switch to the issued RC4 key
/// before the two-byte gateway LoginReply is sent.
/// </summary>
public sealed partial class ZoneService : ISoeService
{
    public const string ProtocolName = "ExternalGatewayApi_3";

    private readonly ITransportLog _log;
    private readonly IPacketRecorder _recorder;
    private readonly GatewayTicketRegistry _tickets;
    private readonly ZoneOptions _options;
    private readonly AugustDynamicAppearanceTable? _dynamicAppearance;
    private readonly ConcurrentDictionary<ulong, AugustWardrobeState> _wardrobes = new();

    /// <summary>
    /// docs/80 edits 8-9: the durable half of <see cref="_wardrobes"/>. Disabled (a no-op) unless
    /// <c>ZoneOptions.Skins.WardrobeStoreRoot</c> names a directory, which is what keeps every
    /// existing test and embedder writing nothing to disk.
    /// </summary>
    private readonly WardrobeStore _wardrobeStore;

    /// <summary>
    /// docs/106 §13 (D283): one log line per (looted item, outcome), not one per dress. A dress can
    /// run twenty times a minute in a match and the interesting event is the first one - the same
    /// reason <c>AugustWorldEquipmentVisuals</c> counts its declines by pair.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _wornSkinNotes = new(StringComparer.Ordinal);

    // Every apparel-owned attachment slot in the August EquipmentSlotDefinitions table. A full
    // equipment dress does not necessarily evict a native skin-manager attachment from an
    // omitted slot, so SendCharacterAppearance explicitly unsets the absent members of this set.
    private static readonly uint[] WardrobeEquipmentSlots = [1, 2, 3, 4, 5, 10, 28, 29, 100];

    /// <summary>
    /// The real Z2 loot placement data (docs/33), loaded once per process and shared by every
    /// session — both objects are immutable after construction. Lazy so a host that never enters a
    /// match (and a unit test that never lands) pays neither the 4.1 MB nor the 60 ms.
    /// </summary>
    private static readonly Lazy<LootTables> LootTablesData = new(LootTables.LoadDefault);

    private static readonly Lazy<Z2LootSpawns> LootSpawnData = new(Z2LootSpawns.LoadDefault);

    /// <summary>
    /// D274: what an airdrop crate holds, and the crate's own client ids. Its own file rather than
    /// another category of <see cref="LootTablesData"/>, because the crate is the one legitimate
    /// place two ids the ground roster explicitly EXCLUDES — the .308 Hunting Rifle and its round —
    /// are supposed to appear (<c>AUDIT-loot.md</c> F10).
    /// </summary>
    private static readonly Lazy<AirdropTables> AirdropData = new(AirdropTables.LoadDefault);

    /// <summary>
    /// docs/63 §5.4: item definition → (<c>Models.txt</c> ground actor, <c>NAME_ID</c>), indexed off
    /// the same loot tables the world was seeded from. Process-scoped because
    /// <see cref="LootTablesData"/> is: an item the floor cannot produce cannot be dropped, and that
    /// is a property of the build, not of a session.
    /// </summary>
    private static readonly Lazy<DroppedItemCatalogue> DroppableItems =
        new(() => new DroppedItemCatalogue(LootTablesData.Value));

    /// <summary>
    /// docs/61 §1, D25: the <c>0x78</c> bystander relay. One per service, because its whole point is
    /// to reach the OTHER sessions on this host — a driven car was invisible to everyone but its
    /// driver before this. Provably a no-op with one player: <see cref="VehiclePoseBroadcast.Relay"/>
    /// never relays to the reporter.
    /// </summary>
    private readonly VehiclePoseBroadcast _vehiclePoses = new();

    /// <summary>
    /// docs/39 §I.3: the whole match's floor, decided once. The gate plus the symmetric room-cap
    /// pass takes ~114 ms over all 168,322 markers, so it belongs at boot (<see cref="PreloadLootData"/>)
    /// rather than inside a posted continuation on the single listener thread.
    /// <para>
    /// One layout per process, not per match, because the seed is <see cref="ZoneOptions.LootSeed"/>
    /// and that is a host setting today (docs/33 §4.3 — the gas seed is per match, the loot seed is
    /// not yet). Building it lazily off the first session's options keeps that honest: the seed is
    /// read once and logged.
    /// </para>
    /// </summary>
    private readonly Lazy<Z2LootLayout> _lootLayout;

    /// <summary>
    /// Boot-time validation/preview using LootSeed. Real rounds roll with their shared MatchSeed
    /// so a pad missing in one round can be populated in the next.
    /// </summary>
    private readonly Lazy<IReadOnlyList<PlannedVehicle>> _vehiclePlan;

    /// <summary>
    /// docs/42 §10.2 step 2: the map's own 4,103 door proxies, loaded once per process and shared —
    /// <see cref="Z2Doors"/> is immutable after construction and every session gets its own
    /// <see cref="MatchDoors"/> on top of it.
    /// </summary>
    private static readonly Lazy<Z2Doors> DoorData = new(Z2Doors.LoadDefault);

    /// <summary>
    /// docs/48: this match's drop location. One chooser per service, sharing the loot spawn set the
    /// layout already loads, because the anchor pass over 144,365 area-tagged markers costs ~720 ms
    /// in Debug and is a pure function of the shipped data — it belongs at boot
    /// (<see cref="PreloadWorldData"/>), not inside a countdown closure on the listener thread.
    /// </summary>
    private readonly MatchDropChooser _drop;

    /// <summary>docs/43 §I.2 hook 1: the four drivable vehicles and the map's 3,519 parking anchors.</summary>
    private static readonly Lazy<VehicleRoster> VehicleRosterData = new(VehicleRoster.LoadDefault);

    private static readonly Lazy<VehicleAnchorSet> VehicleAnchorData = new(VehicleAnchorSet.LoadDefault);

    /// <summary>
    /// Guid base for this session's parked vehicles. Disjoint from ground loot
    /// (<c>0x2000…</c>/<c>0x3100…</c>) and doors (<c>0x4400…</c>) so a c2s guid identifies which
    /// subsystem owns the object without any positional guesswork.
    /// </summary>
    private const ulong VehicleWorldGuidBase = 0x4600_0000_0000_0001;

    /// <summary>Transient-id base, above <c>LootWorld</c>'s 1,000 and <c>MatchDoors</c>' 1,000,000.</summary>
    private const uint VehicleTransientIdBase = 2_000_000;

    /// <summary>
    /// Watchdog poll interval (docs/35 §5b). One second is fine: the tightest deadline is 3 s and
    /// the poll only bounds how late a warning is, never whether it happens.
    /// </summary>
    private const int WatchdogPollMs = 1000;

    /// <summary>
    /// Runs deferred work on the transport thread. Sessions are not thread-safe, so a delayed
    /// bootstrap must be marshalled onto the listener thread through this — set it to the gateway
    /// <see cref="SoeListener.Post"/>. Null (the default) means "no dispatcher configured": a
    /// delayed bootstrap is then refused rather than run on a thread-pool continuation thread.
    /// </summary>
    public Action<Action>? Post { get; set; }

    public ZoneService(
        ITransportLog log,
        IPacketRecorder recorder,
        GatewayTicketRegistry? tickets = null,
        ZoneOptions? options = null)
    {
        _log = log;
        _recorder = recorder;
        _tickets = tickets ?? new GatewayTicketRegistry();
        _options = options ?? new ZoneOptions();
        // Restore ranked data before accepting packets; runtime reads must never open files.
        if (!string.IsNullOrWhiteSpace(_options.EconomyStoreRoot))
            _rankedScores = new RankedScoreStore(Path.Combine(_options.EconomyStoreRoot, "ranked-preseason5"),
                backgroundWrites: true, log: _log.Warn);
        _hostedGames = _options.HostedGames ?? new HostedGames.HostedGameStore();
        // Lazy, and instance-scoped because it depends on _options: a unit test that constructs a
        // service but never lands must not pay the 4.1 MB read and the 114 ms layout pass. The host
        // forces it at boot through PreloadWorldData().
        _lootLayout = new Lazy<Z2LootLayout>(() => Z2LootLayout.Build(
            LootSpawnData.Value,
            LootTablesData.Value,
            _options.LootSeed,
            _options.LootDensity));
        _drop = new MatchDropChooser(_options.Drop, () => LootSpawnData.Value);
        _vehiclePlan = new Lazy<IReadOnlyList<PlannedVehicle>>(() => VehicleSpawnPlanner.Plan(
            VehicleAnchorData.Value,
            VehicleRosterData.Value,
            _options.LootSeed,
            _options.VehiclePlan));
        _wardrobeStore = new WardrobeStore(_options.Skins.WardrobeStoreRoot, _log.Info);
        _economy = string.IsNullOrWhiteSpace(_options.EconomyStoreRoot) ? null
            : new AccountEconomyStore(_options.EconomyStoreRoot, _log.Warn);
        _bountyLedger = _economy is null ? null : new BountyLedger(_economy, EconomyRunId);
        _log.Info("match worlds: " + string.Join(", ", _options.MatchAdmissions.Definitions
            .Select(world => $"{world.WorldId}={world.DisplayName} (mode {world.GameModeId})")));
        // docs/80: two policy switches that live on the type that enforces them rather than being
        // threaded through every call site. Both are set from the options record at construction,
        // both default to the behaviour the owner asked for, and neither is read anywhere else.
        AugustWorldEquipmentVisuals.RetintOnly = _options.Skins.SkinRetintOnly;
        AugustWorldEquipmentPolicy.GateBackpacksAndPerformanceFootwear =
            _options.Skins.GateBackpacksAndPerformanceFootwear;
        if (AugustDynamicAppearanceTable.TryLoad(
            _options.DynamicAppearanceSourcePath,
            out AugustDynamicAppearanceTable? dynamicAppearance,
            out string appearanceStatus))
        {
            _dynamicAppearance = dynamicAppearance;
            _log.Info(appearanceStatus);
            // Prepare once before the listeners start, so the first player does
            // not perform table compression on the gameplay thread.
            ReferenceData compressedAppearance = dynamicAppearance!.CreateCompressedReferenceData();
            _log.Info($"appearance transfer prepared: {dynamicAppearance.PayloadLength:N0} raw bytes, "
                + $"{compressedAppearance.WirePayloadLength:N0} wire payload bytes (client LZ4 envelope)");
        }
        else
        {
            _log.Warn(appearanceStatus);
        }
    }

    /// <summary>
    /// The skin lane's boot lines: which of the docs/80 switches are live, and - unless
    /// <c>CRANBERRY_SKIN_CENSUS=0</c> - the grey census.
    /// <para>
    /// <b>The census changes no byte.</b> It resolves every wearable on both bodies exactly the way
    /// the client resolves it and says, before the owner launches, which ids will render grey,
    /// which name the other body's mesh, and which resolve to no appearance row at all. It is the
    /// diagnostic that would have predicted docs/69's white AK-47 without costing a session, and it
    /// is the owner's own idea (D53, <c>ZoneSkinCensus.cs</c>).
    /// </para>
    /// <para>
    /// Deliberately NOT folded into <see cref="PreloadWorldData"/>: that method's four lines are
    /// pinned by <c>ZoneIntegrationTests.EveryWorldDataFileLoadsAndReportsItsDerivedCensus</c>, and
    /// a diagnostic must not move a regression guard. Never throws.
    /// </para>
    /// </summary>
    public string[] PreloadSkinData()
    {
        var lines = new List<string> { _options.Skins.Describe() };
        if (!_options.Skins.RunSkinCensus)
        {
            return [.. lines];
        }

        try
        {
            lines.AddRange(AugustSkinCensus.Describe(
                _dynamicAppearance,
                _options.Skins.OrderAppearanceRowsByGender,
                // D323: the census resolves item 10 on a male body the way the client does -
                // no row, then the packet's own group - and that group is D226's.
                _options.Skins.CrossGenderShaderGroup));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lines.Add($"skin census unavailable ({exception.GetType().Name}: "
                + $"{exception.Message}) - it is a diagnostic, nothing on the wire changes");
        }

        return [.. lines];
    }

    /// <summary>
    /// Forces the shared Z2 loot data into memory and returns the line to log for it. The host calls
    /// this once at start-up: without it the first parachute landing reads 4.1 MB from disk and
    /// parses 25 KB of JSON <em>inside</em> a posted continuation, stalling the single listener
    /// thread — and every other session's acknowledgements with it. It also turns a missing or
    /// hand-broken data file into one line at boot instead of a surprise at the first landing.
    /// Never throws.
    /// </summary>
    public static string PreloadLootData()
    {
        try
        {
            Z2LootSpawns spawns = LootSpawnData.Value;
            LootTables tables = LootTablesData.Value;
            return $"loot data: {spawns.Count:N0} Z2 spawn markers over {spawns.Dimension}×{spawns.Dimension} "
                + $"cells of {spawns.CellMetres} m, {tables.Count} category table(s) for zone {tables.Zone}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"loot data unavailable ({ex.GetType().Name}: {ex.Message}) — matches will spawn "
                + "no real ground loot; only the development drop will work";
        }
    }

    /// <summary>
    /// Forces everything a match reads from disk that <see cref="PreloadLootData"/> does not: the
    /// per-match loot layout (docs/39 §I.3 — the gate and the room caps over all 168,322 markers)
    /// and the Z2 door dataset (docs/42). The host calls this once at start-up so the first landing
    /// does not do 4 MB of I/O and a full-map pass inside a posted continuation on the single
    /// listener thread. Never throws: a missing or hand-edited data file becomes one line at boot
    /// and a degraded match, not an unhandled exception later.
    /// </summary>
    public string[] PreloadWorldData()
    {
        var lines = new List<string>(4);

        try
        {
            var destructibles = Destructibles.DestructibleCatalog.Default;
            lines.Add($"destructible data: {destructibles.Props.Count:N0} Z2 glass/wood objects in {destructibles.Models.Count} families");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"destructible data unavailable ({ex.GetType().Name}: {ex.Message})");
        }

        try
        {
            Z2LootLayout layout = _lootLayout.Value;
            lines.Add($"loot layout: seed {layout.MatchSeed}, {layout.MarkerCount:N0} markers → "
                + $"{layout.GatedCount:N0} gated − {layout.SuppressedCount:N0} room-capped − "
                + $"{layout.Armour.Refused:N0} laminated-armour (D275: {layout.Armour.Kept} vest(s) "
                + $"kept, {layout.Armour.RefusedByChance} by the 5 % chance, "
                + $"{layout.Armour.RefusedBySpacing} by the 250 m radius, "
                + $"{layout.Armour.RefusedBySquare} by the per-square cap) = "
                + $"{layout.LiveCount:N0} items on the Z2 floor");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"loot layout unavailable ({ex.GetType().Name}: {ex.Message}) — the match will "
                + "spawn no real ground loot");
        }

        if (_options.Drop.Enabled)
        {
            // docs/48: force the 92-place view and its anchor pass here. Never throws — a missing or
            // hand-edited placement file degrades the match to the fixed MatchDropSpawn with the
            // reason on the boot log, exactly like the loot layout above.
            Z2DropPois? places = _drop.Places(out string? dropReason);
            lines.Add(places is not null
                ? $"drop data: {places.Count} named Z2 place(s), anchors chosen for "
                    + $"{places.LootRadius:F0} m; every match drops on one of them from "
                    + $"{_options.Drop.SkySpawnAltitude:F0} m"
                : $"drop data unavailable ({dropReason}) — every match will drop on the fixed "
                    + $"MatchDropSpawn {_options.MatchDropSpawn}");
        }

        if (_options.SendDoors)
        {
            try
            {
                Z2Doors doors = DoorData.Value;
                lines.Add($"door data: {doors.Count:N0} Z2 door proxies over {doors.Dimension}×{doors.Dimension} cells");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"door data unavailable ({ex.GetType().Name}: {ex.Message}) — no doors will spawn");
            }
        }

        if (_options.SendVehicles)
        {
            try
            {
                VehicleRoster roster = VehicleRosterData.Value;
                VehicleAnchorSet anchors = VehicleAnchorData.Value;
                // Force the plan here too: it is the same argument as the loot layout — a full pass
                // over 3,519 anchors belongs at boot, not inside the landing continuation.
                IReadOnlyList<PlannedVehicle> plan = _vehiclePlan.Value;
                lines.Add($"vehicle data: {roster.Count} drivable vehicle(s), {anchors.Count:N0} spawn "
                    + $"anchors / {anchors.TotalSpaces:N0} bays over {anchors.AreaCount} named place(s); "
                    + $"{plan.Count:N0} car(s) in boot preview (seed {_options.LootSeed}); each match rolls its own plan");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"vehicle data unavailable ({ex.GetType().Name}: {ex.Message}) — no vehicles will spawn");
            }
        }

        return [.. lines];
    }

    public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request)
    {
        _recorder.RecordSession(remote, in request);
        if (!string.Equals(request.ProtocolName, ProtocolName, StringComparison.Ordinal))
        {
            _log.Warn($"{remote} asked for '{request.ProtocolName}' on the gateway port; refused");
            return SessionDecision.Refuse;
        }

        // FUN_142112b30 sends the gateway LoginRequest while the crypto flag is still zero, then
        // arms RC4. We therefore accept clear and install the matching key in OnMessage.
        return SessionDecision.Clear;
    }

    public void OnConnected(SoeConnection connection)
    {
        connection.Tag = NewSessionState();
        connection.RawInboundSink = (c, cipher, position) =>
            _recorder.RecordRaw(c, position, cipher);
        _log.Info($"{connection} gateway link open; awaiting clear LoginRequest");
    }

    public void OnMessage(SoeConnection connection, Span<byte> message)
    {
        _recorder.RecordMessage(connection, "c2s", message);
        var state = connection.Tag as GatewaySessionState ?? NewSessionState();
        connection.Tag = state;

        if (!state.Authenticated)
        {
            HandleLogin(connection, state, message);
            return;
        }

        if (!ValidateHostedAdmission(connection, state)) return;

        GatewayHeader header = GatewayHeader.Parse(message[0]);
        if (header.Opcode == GatewayTunnelFromClient.Opcode)
        {
            HandleClientTunnel(connection, GatewayTunnelFromClient.Parse(message));
            return;
        }

        _log.Info($"{connection} gateway message opcode={header.Opcode} channel={header.Channel} "
            + $"({message.Length} bytes): {Convert.ToHexString(message.Slice(0, Math.Min(64, message.Length)))}");
    }

    public void OnDisconnected(SoeConnection connection, DisconnectCause cause)
    {
        if (connection.Tag is GatewaySessionState departing)
        {
            CancelPartyQueuePeers(departing, "party member disconnected");
            DepartBounty(connection, departing);
            PartyLinkClosed(departing);
        }
        _accountSessions.TryRemove(connection, out _);
        PublishInGameSocial();
        if (connection.Tag is GatewaySessionState state)
        {
            state.PendingClientAdmission = null;
            state.PendingLogout = null;
            state.InteractionGeneration++;
            LeaveAirdrops(state);
            LeaveSharedLoot(state);
            state.BootstrapCancellation?.Cancel();
            // The Later chain that pumps the gas already drops its work once the link is closed;
            // stopping the controller ends the chain on its next pass instead of one tick later.
            state.Gas?.Stop();
            state.GasPumping = false;
            // Same reasoning for the watchdog poll chain (docs/35 §5c): Reset() disarms every
            // milestone, so NeedsPolling goes false and PumpWatchdog stops rescheduling itself.
            // A closed link would drop the work anyway; this stops it one pass earlier and, more
            // importantly, means a link close is never reported as a missed milestone.
            state.Watchdog.Reset();
            // docs/61 §1: stop relaying this session's view of other people's cars the moment the
            // link is gone. VehiclePoseBroadcast also sweeps closed observers itself, so this is the
            // early half of a two-part guarantee rather than the only one.
            _vehiclePoses.Unregister(state.Guid);
            // docs/109 lane 3C, the one removal: 0f 01 RemovePlayer to every viewer that could see
            // this character, THEN the registry drop. The other way round and the transient ids are
            // released while the clients still have them bound.
            PeerLinkClosed(connection, state);
            // docs/80 edit 9: one last save, then drop the in-memory entry. Before wave 8 the map
            // was the only copy, so it could never be evicted and every guid that had ever
            // connected was retained for the life of the host; with a store behind it the map is a
            // cache again.
            if (state.Guid != 0 && _wardrobeStore.Enabled)
            {
                _wardrobeStore.Save(state.Guid, state.Wardrobe);
                _wardrobes.TryRemove(state.Guid, out _);
            }
        }

        _log.Info($"{connection} gateway link closed ({cause})");
    }

    private void HandleLogin(
        SoeConnection connection,
        GatewaySessionState state,
        ReadOnlySpan<byte> message)
    {
        GatewayLoginRequest request;
        try
        {
            request = GatewayLoginRequest.Parse(message);
        }
        catch (PacketFormatException ex)
        {
            _log.Warn($"{connection} invalid clear gateway LoginRequest: {ex.Message}");
            return;
        }

        if (!string.Equals(request.ClientProtocol, GatewayLoginRequest.AugustProtocol, StringComparison.Ordinal)
            || !string.Equals(request.ClientVersion, GatewayLoginRequest.AugustVersion, StringComparison.Ordinal))
        {
            _log.Warn($"{connection} gateway client identity rejected: "
                + $"protocol='{request.ClientProtocol}' version='{request.ClientVersion}'");
            return;
        }

        if (!_tickets.TryValidate(request.Ticket, request.Guid, out GatewayAdmission admission))
        {
            _log.Warn($"{connection} gateway admission rejected: guid={request.Guid} "
                + $"ticket='{request.Ticket}' was not issued by this login host");
            return;
        }

        // The clear LoginRequest has already been delivered. The August client enables RC4
        // immediately after sending it, so the response must consume s2c keystream position 0.
        connection.EnableEncryption(admission.Key);
        state.Authenticated = true;
        state.Guid = request.Guid;
        state.CharacterName = admission.CharacterName;
        state.Visuals = CharacterVisuals.FromSelection(
            admission.Gender,
            admission.HeadId,
            admission.HairId,
            admission.SkinToneId,
            admission.ProfileId);
        state.Gender = state.Visuals.Gender;
        // docs/80 edit 9 (the owner RestoreOnLogin): this runs BEFORE the bootstrap dress at
        // SendBootstrap, which is his "order matters both ways" satisfied by construction. With no
        // store configured Load returns a fresh state and this is exactly the old GetOrAdd.
        // docs/106 §14 (D284): the menu outfit is NOT a default - it is this file. Every garment
        // over the starter five is a row some earlier session committed and no session clears, so
        // CRANBERRY_WARDROBE_RESTORE=0 logs in with none of them (the retail starter outfit) and
        // leaves the file untouched; renaming the file clears it for good.
        state.Wardrobe = _wardrobes.GetOrAdd(
            request.Guid,
            guid => _options.Skins.RestoreWardrobeSelections
                ? _wardrobeStore.Load(guid)
                : new AugustWardrobeState());
        LogMenuOutfitOrigin(connection, state);
        state.VehicleSkins = VehicleSkinState.Load(_options.Skins.WardrobeStoreRoot, state.Guid, _log.Warn);
        state.AccountId = admission.AccountId;
        if (!InitializeAccountEconomy(connection, state))
        {
            state.Authenticated = false;
            return;
        }
        RankedScores.RegisterIdentity(ScoreAccount(state), state.Guid, state.CharacterName);
        // docs/103 §3: the console tier depends on the remote, the name and the guid, all three of
        // which are known here — and it has to be known before the self record, which carries Door
        // A's FlagI. ZoneService.Console.cs owns both.
        ResolveConsoleTier(connection, state);
        // docs/109 lane 3C, the one registration: from here this character is somebody another
        // client on this host could be told about. It does not make anyone visible — PeerSession.
        // InMatch does that — so a menu session registers, is seen by nobody, and is removed by the
        // same link-close path as an in-match one.
        RegisterPeerSession(connection, state);

        using var writer = new PacketWriter();
        new GatewayLoginReply(LoggedIn: true).WriteTo(writer);
        _recorder.RecordMessage(connection, "s2c", writer.Written);
        connection.Send(writer.Written);
        _log.Info($"{connection} gateway LoginRequest accepted: guid={request.Guid} "
            + $"ticket='{request.Ticket}' protocol='{request.ClientProtocol}' "
            + $"version='{request.ClientVersion}'; encrypted LoginReply sent");

        if (_options.BootstrapDelayMs <= 0)
        {
            SendBootstrap(connection, state);
            return;
        }

        // A delayed bootstrap fires from a timer thread, so it must be marshalled onto the
        // transport thread; without a dispatcher, touching the non-thread-safe SoeConnection off
        // the listener thread would race. Refuse the delay rather than run it unsafely.
        Action<Action>? post = Post;
        if (post is null)
        {
            _log.Warn($"{connection} BootstrapDelayMs={_options.BootstrapDelayMs} but no listener-thread "
                + "dispatcher was configured (ZoneService.Post); sending the bootstrap immediately instead");
            SendBootstrap(connection, state);
            return;
        }

        _log.Info($"{connection} zone bootstrap scheduled in {_options.BootstrapDelayMs} ms");
        state.BootstrapCancellation = new CancellationTokenSource();
        CancellationToken token = state.BootstrapCancellation.Token;
        _ = Task.Delay(_options.BootstrapDelayMs, token).ContinueWith(task =>
        {
            if (task.IsCanceledOrFaulted())
            {
                return;
            }

            // The reads of connection/state and every send run on the listener thread via Post.
            post(() =>
            {
                if (!token.IsCancellationRequested
                    && connection.State == ConnectionState.Open
                    && state.Authenticated)
                {
                    SendBootstrap(connection, state);
                }
            });
        }, CancellationToken.None);
    }

    /// <summary>
    /// The packets that take the client from <c>Connecting</c> to its local player: zone details
    /// first (PostInitialize needs the name and the only registered world type), then the complete
    /// self record, then the gates consumed by the later run states.
    /// </summary>
    private void SendBootstrap(SoeConnection connection, GatewaySessionState state)
    {
        // Establish the KOTK SKU in DEFAULT before InitializationParameters switches away
        // from the locally seeded INIT group. The native Crown getter requires Sku=2.
        SendTunnel(connection, writer => new EnvironmentSettingsUpdate().WriteTo(writer));
        _log.Info($"{connection} sent August EnvironmentSettings DEFAULT Sku=2");

        // The August InitializationParameters handler (FUN_140b019d0) calls
        // FUN_140ac4760(client, true), rebuilding the resource-backed managers and loading
        // ClientItemDefinitions.txt from the shipped packs.  Without this packet the online item
        // cache stays empty and the native wardrobe builder drops every otherwise-valid skin row.
        SendTunnel(connection, writer => new InitializationParameters().WriteTo(writer));
        _log.Info($"{connection} sent InitializationParameters LIVE_KOTK (loaded native client item definitions)");

        // PostInitialize (FUN_140b36780) initialises the world from the zone name and type stored
        // by SendZoneDetails; FUN_140b37510 registers exactly one world implementation, type 4
        // (HeightfieldLod), so any other type exits the client. The zone must exist in the
        // client's own pack files (LoginZone or Z2, docs/07 §8.1).
        // The trailing list is the client's StringHashToValue table; FUN_140a4dc30 clears the map
        // before reading it, so the complete table goes out (an empty list left the model loader
        // without Model.DescriptorReplaceString and crashed it in WaitForFirstZone).
        state.InventoryActionUiState = null;
        SendTunnel(connection, new SendZoneDetails(
            _options.ZoneName,
            ZoneType: SendZoneDetails.HeightfieldLod,
            Values: WorldDisplayLabel.Values(string.Empty),
            Weather: _options.Weather,
            LightingFile: _options.LightingFile));
        RestoreHostedAdminWorldAfterTable(connection, state);
        _log.Info($"{connection} sent SendZoneDetails for {_options.ZoneName} type=4 (HeightfieldLod) "
            + $"with lighting '{_options.LightingFile}' and {StringHashValues.Entries.Count} StringHashToValue entries");

        SendTunnel(connection, writer => new ClientGameSettings().WriteTo(writer));
        _log.Info($"{connection} sent August ClientGameSettings");

        // Actor construction resolves the self record's +0xb9f0 skin group and equipment palette
        // immediately. Load the project's complete appearance table before creating that actor.
        SendTunnel(connection, writer => CreateDynamicAppearanceReference().WriteTo(writer));
        _log.Info(_dynamicAppearance is null
            ? $"{connection} sent project-authored starter DynamicAppearance table before SendSelfToClient"
            : $"{connection} sent "
                + (_dynamicAppearance.WholeTable ? "WHOLE friend" : "filtered August wardrobe")
                + " DynamicAppearance table before SendSelfToClient "
                + $"({_dynamicAppearance.AppearanceCount:N0} appearances, {_dynamicAppearance.SemanticCount:N0} item maps, "
                + $"{_dynamicAppearance.ParameterCount:N0} shader parameters, "
                + $"{_dynamicAppearance.PayloadLength:N0} payload bytes)");

        // The loader FUN_140a31140 aborts the client unless the record is consumed exactly, so the
        // complete minimal layout goes out, carrying the gateway guid and the name.
        SelfRecord self = CreateSelfRecord(state, _options.SpawnPosition);
        SendSelfToClient selfPacket = SendSelfToClient.FromRecord(self);
        SendTunnel(connection, selfPacket);
        _log.Info($"{connection} sent SendSelfToClient guid={state.Guid} name='{state.CharacterName}' "
            + $"gender={state.Gender} model={self.ActorDefinitionId} head={state.Visuals.HeadId} "
            + $"'{state.Visuals.HeadModel}' hair={state.Visuals.HairId} '{state.Visuals.HairModel}' "
            + $"skinTone={state.Visuals.SkinToneId} ({selfPacket.PlayerData.Length} record bytes)");

        // The actor creator hides the local player; Equipment.SetCharacterEquipment for its guid is
        // the only thing that un-hides it (FUN_1411af6c0 ← self+0xdd18 ← FUN_140cd85e0).
        // The front end treats a missing account/skin manager as a service outage and disables
        // every wardrobe category. Saved ownership controls selectable tiles, while the manager's
        // worn rows restore this character's reconciled saved choices.
        SendSkinCatalogue(connection, state);
        _log.Info($"{connection} sent ready account-item and skin-item managers: "
            + $"{AugustSkinCatalog.Apparel.Count} apparel + {AugustSkinCatalog.Weapons.Count} weapon tiles, "
            + (_economy is null ? "legacy in-memory ownership fixture" : "saved account ownership"));

        // Equipment.SetCharacterEquipment is the packet that unhides the actor. Keep it after
        // the manager burst so the native wardrobe recomposite cannot become the last writer of
        // the visible menu outfit.
        SendCharacterAppearance(connection, state, "initial bootstrap after wardrobe managers");

        // ReferenceData "ProfileDefinitions" (FUN_140b055c0): creates the PreLoadPcModels tracker
        // (client+0x32340) that WaitForConfirmationPacket polls through FUN_14124cc30; an empty
        // table reports done immediately.
        SendTunnel(connection, writer => ReferenceData.EmptyProfileDefinitions.WriteTo(writer));
        _log.Info($"{connection} sent ReferenceData ProfileDefinitions (empty table)");

        // docs/60 stage 1 (docs/58 §11, D33). The client builds a weapon's fire groups ITSELF at
        // ItemAdd time out of this table, and a weapon whose definition is missing returns early and
        // leaves an empty array the per-frame active-hand update then null-derefs (docs/45) — the
        // crash that has blocked wielding, shooting and even the fists. Its own
        // Client\WeaponErrors.log says "weapon definition not found for weapon ID 6".
        // MUST precede every weapon ItemAdd: CreateItem's self-init (FUN_142291180) runs once, at
        // construction, and no re-initialisation path is known (docs/58 U8) — hence the session
        // bootstrap and not the match burst. This is NOT the match-zoning burst, so regression
        // guard 4 is untouched.
        // docs/89 §4 A4 (wave 9). The blob is NO LONGER SENT FROM HERE. Stage 1 is on by default
        // now, and this is the login burst that is otherwise byte-identical to
        // captures\wire-20260829-150206.txt — the owner's only confirmed path to PLAY. A blob
        // shorter than the true layout is not ignored: FUN_140a20570 returns the cursor's error
        // byte and FUN_140b055c0 answers false with _DAT_00000000 = 1, a store to address 0, which
        // would land here, BEFORE character select, on the one path that works. The invariant the
        // send has to keep is "before every weapon ItemAdd", not "in the login burst": the first
        // weapon ItemAdd of any session is in the in-match bootstrap (host-20260830-203615.log:173),
        // so EnsureInventory is both early enough and off the proven path. See
        // SendWeaponDefinitionsOnce.

        if (_options.SendLaterGates)
        {
            // Sets InitialZoneDataComplete (client+0x32122) and, with the player present, calls
            // FUN_140dc0ab0 — live, the client answers this with its GameTimeSync request as it
            // enters WaitForFirstZone (the 20:17 run had it; the 20:24 run without it did not).
            SendTunnel(connection, writer => new ZoneDoneSendingInitialData().WriteTo(writer));
            _log.Info($"{connection} sent ZoneDoneSendingInitialData");
            ExpectClient(
                connection,
                state,
                ClientMilestone.MenuClientIsReady,
                "zone bootstrap: self record + zone-done");
        }
    }

    /// <summary>
    /// The packets consumed by <c>WaitForFirstZone</c> / <c>WaitForConfirmationPacket</c>. They must
    /// arrive after the client has entered <c>WaitForFirstZone</c>: its entry routine
    /// <c>FUN_140b864c0</c> clears <c>WeatherDataSynced</c> (client+0x31634) and
    /// <c>ReceivedPreloadDonePacket</c> (client+0x32123), so anything sent with the self record is
    /// wiped. The client's own <c>GameTimeSync</c> request marks that moment.
    /// </summary>
    private void SendWorldGates(SoeConnection connection)
    {
        SendTunnel(connection, writer => new UpdateWeatherData(_options.Weather).WriteTo(writer));
        SendTunnel(connection, writer => new DoneSendingPreloadCharacters().WriteTo(writer));
        // WaitForConfirmationPacket (FUN_140b8dc30) additionally needs the time stamp that
        // ClientUpdate.NetworkProximityUpdatesComplete stores into client+0x32128.
        SendTunnel(connection, writer => new NetworkProximityUpdatesComplete().WriteTo(writer));
        _log.Info($"{connection} sent UpdateWeatherData, DoneSendingPreloadCharacters, NetworkProximityUpdatesComplete");
    }

    private void HandleClientTunnel(SoeConnection connection, GatewayTunnelFromClient tunnel)
    {
        byte[] payload = tunnel.Payload;
        if (payload.Length == 0)
        {
            _log.Warn($"{connection} empty client tunnel packet on channel {tunnel.Channel}");
            return;
        }

        // FUN_140dd1400 diverts the two high-rate movement channels before base-opcode dispatch.
        // Keep their sparse deltas as server-owned state: later proximity checks consume the local
        // player position, while landing/driving consume only explicitly registered managed ids.
        var movementState = (GatewaySessionState)connection.Tag!;
        // Before the channel-2/3 diversion so the high-rate streams count as traffic (docs/35 §5c):
        // the idle watch is the backstop for a failure nobody wrote a milestone for.
        movementState.Watchdog.NoteClientPacket();
        if (tunnel.Channel == 2)
        {
            HandlePlayerMovement(connection, movementState, payload);
            return;
        }

        // Channel 3 is NOT movement-only. The August client sends its c2s 0x82 weapon traffic
        // (82 1f MultiWeapon wrapping FireStateUpdate / SwitchFireModeRequest / ...) on channel 3
        // as well - 431 packets across the three 2026-09-02 sessions, every one of which this
        // branch used to hand to the managed-movement parser, which threw on the 0x82 lead byte and
        // logged it as "malformed managed movement" (out\overhaul-20260901\FIRE-PATH-DIAGNOSIS.md
        // §0, wire-20260902-212215.txt et al.). Only a 0x90 lead byte is managed movement; anything
        // else on channel 3 falls through to the base-opcode switch below, where the WeaponBase arm
        // already knows how to decode it.
        if (tunnel.Channel == 3
            && payload.Length > 0
            && payload[0] == ClientManagedMovementUpdate.Opcode)
        {
            HandleManagedMovement(connection, movementState, payload);
            return;
        }

        RecordMovementVersionRequest(movementState, payload);
        if (HandleMovementVersionRequest(connection, movementState, payload)) return;
        byte opcode = payload[0];
        string name = ZoneOpcodes.Name(opcode) ?? $"unregistered 0x{opcode:x2}";
        string hex = HostedGameStore.ContainsSecret(payload) ? "[hosted key redacted]"
            : Convert.ToHexString(payload.AsSpan(0, Math.Min(48, payload.Length)));
        try
        {
            switch (opcode)
            {
                case ZoneOpcodes.SpectatorBase:
                    HandleHostedSpectatorRequest(connection, movementState, payload);
                    return;
                case ZoneOpcodes.AnimationBase:
                    HandleEmote(connection, movementState, payload);
                    return;
                case ZoneOpcodes.GroupsBase:
                    HandlePartyPacket(connection, movementState, payload);
                    PublishInGameSocial();
                    return;
                case ZoneOpcodes.SetLocale:
                {
                    // FUN_140a7a2b0: u8 0x33; str locale — the first packet after the local player exists.
                    var reader = new PacketReader(payload.AsSpan(1));
                    _log.Info($"{connection} zone {name} locale='{reader.ReadString()}' (channel {tunnel.Channel}) — client left Connecting");
                    return;
                }

                case ZoneOpcodes.ClientInitializationDetails:
                {
                    // FUN_140a22d80: u8 0x71; u32 (client+0x3210c)
                    var reader = new PacketReader(payload.AsSpan(1));
                    _log.Info($"{connection} zone {name} value={reader.ReadUInt32()} (channel {tunnel.Channel})");
                    return;
                }

                case ZoneOpcodes.GameTimeSync:
                {
                    GameTimeSync request = GameTimeSync.Parse(payload);
                    _log.Info($"{connection} zone {name} clientTime={request.Time} value={request.Value} flag={request.Flag} (channel {tunnel.Channel})");
                    // D16: fixed 14:00 UTC-of-day, cycle scalar 0.0 (u32 float bits) and the
                    // freeze flag — FUN_140ff2250 applies the time on the first reply, stores the
                    // scalar into clock+0x94 and sets the freeze byte +0xa0 from the flag.
                    SendTunnel(connection, writer => new GameTimeSync(
                        Time: _options.FixedUnixTime, Value: 0, Flag: _options.FreezeClock).WriteTo(writer));
                    if (_options.SendLaterGates)
                    {
                        SendWorldGates(connection);
                    }

                    return;
                }

                case ZoneOpcodes.LobbyGameDefinitionBase when payload.Length >= 3 && BitConverter.ToUInt16(payload, 1) == 1:
                    // 41 01 00: the client asks for the LobbyGameDefinition tables once after ClientIsReady.
                    SendTunnel(connection, writer => new LobbyGameDefinitions().WriteTo(writer));
                    _log.Info($"{connection} zone {name} request → LobbyGameDefinitions (five empty tables)");
                    return;

                case ZoneOpcodes.StaticViewBase when payload.Length >= 3 && BitConverter.ToUInt16(payload, 1) == 1:
                {
                    // E9 01 00 str name: the menu UI's SetStaticView(name) — a named camera viewpoint.
                    var reader = new PacketReader(payload.AsSpan(3));
                    string view = reader.ReadString();

                    var menuState = (GatewaySessionState)connection.Tag!;
                    if (view.StartsWith(WeaponCategoryPreviewPrefix, StringComparison.Ordinal))
                    {
                        PreviewWeaponCategory(connection, menuState, view);
                        return;
                    }
                    bool changedView = menuState.MenuView != view;
                    if (menuState.CrateOpening is not null && view != "kotkcrates")
                        EndCrateOpening(connection, menuState);
                    menuState.MenuView = view;
                    if (menuState.Match == MatchStep.Menu
                        && (changedView || menuState.MenuWeaponPreview is not null
                            || menuState.MenuWeaponPreviewCategoryId != 0 || menuState.MenuApparelPreview is not null))
                    {
                        menuState.MenuWeaponPreview = null;
                        menuState.MenuWeaponPreviewCategoryId = 0;
                        menuState.MenuApparelPreview = null;
                        SendCharacterAppearance(connection, menuState, "appearance tab changed");
                    }

                    // docs/105: the name is a pure server-side key — nothing in the client defines
                    // the kotk* viewpoints (StaticViewLocations.txt ships only `mesa` and `hill`).
                    if (!MenuViewTable.TryResolve(
                            view, _options.MenuViews.Coverage, out StaticViewReply camera, out Vector4 mark))
                    {
                        _log.Info($"{connection} zone {name} '{view}' → inherited current period camera");
                        return;
                    }

                    // StaticViewReply applies its Heading to the subject, replacing this rotation.
                    // MenuViewTable therefore puts the backpack turn in the reply itself.
                    SendTunnel(connection, writer => new UpdateLocation(mark, MenuViewTable.SubjectRotation(view), Apply: true).WriteTo(writer));
                    SendTunnel(connection, writer => camera.WriteTo(writer));

                    // docs/105 §9 (MENU-RETAIL-GAP U-1). The friend server follows every answered
                    // view with Character.WeaponStance = 0 — 35 requests, 35 replies, 35 stance-0
                    // packets in the 2026-08-22 admin capture's menu session, each within 12 ms of
                    // its reply and always after the paired UpdateLocation. It is the only thing
                    // that poses the lobby actor: every PlayAnimation in that capture is a melee
                    // swing in the MATCH session. Reuses the Weapons writer; sends nothing new.
                    if (_options.MenuActor.SendWeaponStanceOnViewChange
                        && connection.Tag is GatewaySessionState viewState)
                    {
                        SendTunnel(
                            connection,
                            writer => new WeaponStance(viewState.Guid, viewState.MenuWeaponPreview is null ? 0u : 1u).WriteTo(writer));
                    }

                    _log.Info($"{connection} zone {name} '{view}' → StaticViewReply target=({camera.TargetX},{camera.TargetY},{camera.TargetZ}) heading={camera.Heading} distance={camera.Distance}, mark=({mark.X},{mark.Y},{mark.Z})");
                    return;
                }

                case ZoneOpcodes.WallOfDataBase when payload.Length >= 2 && payload[1] == 0x05:
                {
                    // WallOfData.WindowEvent: u8 base, u8 sub, string window, string action,
                    // trailing argument count. OpenGearSkinEditor has set the client's editor
                    // active flag by this boundary. Publishing sooner parses cleanly but skips
                    // the native catalogue-filter refresh (FUN_140d2f640), leaving SKIN empty.
                    var reader = new PacketReader(payload.AsSpan(2));
                    string window = reader.ReadString();
                    string action = reader.ReadString();
                    if (window == MatchActionWindow)
                    {
                        if (reader.ReadUInt32() == 0 && reader.Remaining == 0)
                            HandleMatchAction(connection, (GatewaySessionState)connection.Tag!, action);
                        return;
                    }
                    if (window == OverlayWindow)
                    {
                        if (reader.ReadUInt32() == 0 && reader.Remaining == 0)
                            HandleSocialOverlay(connection, (GatewaySessionState)connection.Tag!, action);
                        return;
                    }
                    if (window == InventoryActionWindow)
                    {
                        if (reader.ReadUInt32() == 0 && reader.Remaining == 0)
                            HandleInventoryAction(connection, (GatewaySessionState)connection.Tag!, action);
                        return;
                    }
                    if (window == SocialWindow)
                    {
                        if (reader.ReadUInt32() == 0 && reader.Remaining == 0)
                            HandleInGameSocial(connection, (GatewaySessionState)connection.Tag!, action);
                        return;
                    }
                    if (window == VoiceHudWindow)
                    {
                        if (reader.ReadUInt32() == 0 && reader.Remaining == 0)
                            HandleVoiceHud(connection, (GatewaySessionState)connection.Tag!, action);
                        return;
                    }
                    if (window == "CUSTOMIZATION_WINDOW" && action == "open")
                    {
                        var state = (GatewaySessionState)connection.Tag!;
                        SendSkinCatalogue(connection, state);
                        SendCharacterAppearance(connection, state, "Gear editor catalogue refresh");
                        _log.Info($"{connection} refreshed the unlocked August skin catalogue at Gear editor open");
                    }
                    else if (window == "CUSTOMIZATION_WINDOW" && action == "close")
                    {
                        var state = (GatewaySessionState)connection.Tag!;
                        state.MenuWeaponPreview = null;
                        state.MenuWeaponPreviewCategoryId = 0;
                        state.MenuApparelPreview = null;
                        state.MenuView = "kotkappearancegear";
                        SendSkinManagerState(connection, state);
                        SendCharacterAppearance(connection, state, "Gear editor close");
                        _log.Info($"{connection} committed {state.Wardrobe.Snapshot().Count} wardrobe selections at Gear editor close");
                    }
                    else if (window == "LoadingScreenWindow" && action == "close")
                    {
                        // The client closing its own loading screen is the strongest "I am in the
                        // world" evidence short of a screenshot (docs/32's evidence standard).
                        var state = (GatewaySessionState)connection.Tag!;
                        _log.Info($"{connection} zone {name} window='{window}' action='{action}' "
                            + $"({state.Match}) — the client closed its own loading screen");
                        NoteMilestone(connection, state, ClientMilestone.LoadingScreenClosed);
                        ReplayAfterMenuReady(connection, state);
                    }
                    else if (window == "InventoryWindow")
                    {
                        HandleInventoryWindow(connection, movementState, action);
                    }
                    else if (window == BountyScreenWindowEvent)
                    {
                        // docs/113 §6, the U-B1 evidence recorder. This line is TELEMETRY, not a
                        // request: FUN_14120c590 builds a WallOfData 9a 05 with
                        // "UI_Binding::ToggleBountyScreen" and "OpenScreen"/"CloseScreen" and does
                        // nothing else, so the show/hide happened entirely client-side. Recording
                        // the match phase beside it is what turns the owner's next click into an
                        // answer: an open in Lobby is the fix working; an open in Dropping is the
                        // residual artefact D254 could not reach.
                        var bountyState = (GatewaySessionState)connection.Tag!;
                        bool inMatchOpen = false;
                        if (action == "OpenScreen")
                        {
                            bountyState.BountyScreenOpens++;
                            if (bountyState.Match == MatchStep.Lobby)
                            {
                                bountyState.BountyScreenSeenInLobby = true;
                            }
                            else
                            {
                                // docs/113 addendum (2026-09-03): an open outside the lobby IS the
                                // owner's reported defect — the client force-opening its own Bounty
                                // page over the parachute. This counter is the pass/fail number the
                                // next play-test greps for: SUPPRESS_DROP_OPEN worked iff it stays 0.
                                bountyState.BountyScreenInMatchOpens++;
                                inMatchOpen = true;
                            }
                        }

                        // Client-originated telemetry verifies the UI timing, separately from
                        // the server's cost and IsInBox sends. Any post-drop open is a regression.
                        string verdict = inMatchOpen
                            ? " — DROP-OPEN NOT suppressed: the client force-opened its Bounty page in "
                                + "match; inspect pregame flag, eligibility and UI arrival guards"
                            : string.Empty;
                        _log.Info($"{connection} bounty: client {action} on its own Bounty screen "
                            + $"({bountyState.Match}, open #{bountyState.BountyScreenOpens}, "
                            + $"in-match auto-opens {bountyState.BountyScreenInMatchOpens}, "
                            + $"seen in the lobby: {bountyState.BountyScreenSeenInLobby}){verdict}");
                    }
                    else if (window == DevConsole.Surfaces.LuaMenuSurface.WallOfDataTable)
                    {
                        // R6 recommendation (b'): the client-side CranberryMenu.lua calls
                        // Ui.SetWallOfData("CRANBERRY", "<token>") and the token is a menu step.
                        // Both front doors feed one state machine (ZoneService.Console.cs).
                        HandleCranberryMenuInput(connection, action);
                    }
                    else
                    {
                        _log.Info($"{connection} zone {name} window='{window}' action='{action}'");
                    }

                    return;
                }

                case ZoneOpcodes.AccessedCharacterBase:
                {
                    if (EndCharacterAccess.Matches(payload))
                    {
                        EndCharacterAccess ended = EndCharacterAccess.Parse(payload);
                        if (ended.CharacterGuid == movementState.AccessedBodyBag)
                            movementState.AccessedBodyBag = 0;
                        bool self = ended.CharacterGuid == movementState.Guid;
                        if (self)
                        {
                            movementState.CharacterAccessGranted = false;
                            GrantSelfInventoryAccess(connection, movementState);
                        }

                        _log.Info($"{connection} zone AccessedCharacter.EndCharacterAccess "
                            + $"character={ended.CharacterGuid} ({(self ? "self" : "external")})");
                    }
                    else
                    {
                        _log.Info($"{connection} zone AccessedCharacter {payload.Length} bytes: "
                            + $"{hex} (unanswered)");
                    }

                    return;
                }

                case ContainerOpcodes.ContainerBase:
                {
                    if (MoveItemRequest.Matches(payload))
                    {
                        HandleContainerMove(connection, movementState, payload);
                    }
                    else
                    {
                        ushort sub = payload.Length >= 3 ? BitConverter.ToUInt16(payload, 1) : (ushort)0;
                        _log.Info($"{connection} zone Container sub=0x{sub:x4} "
                            + $"{payload.Length} bytes: {hex} (unanswered)");
                    }

                    return;
                }

                case ZoneOpcodes.GrinderBase when payload.Length >= 3:
                    HandleGrinderExchange(connection, movementState, payload);
                    return;

                case ZoneOpcodes.ItemsBase when payload.Length >= 2:
                {
                    if (HandleEconomyPurchase(connection, movementState, payload)) return;
                    byte subOpcode = payload[1];
                    if (subOpcode is RequestUseAccountItem.SubOpcode or RequestOpenAccountCrate.SubOpcode
                        or RequestPreviewAccountCrateRewards.SubOpcode)
                    {
                        HandleAccountEconomyItem(connection, movementState, payload);
                    }
                    else if (subOpcode is SkinItemSelectionRequest.RequestSetSkinItem
                        or SkinItemSelectionRequest.RequestSetSkinItemByItemId
                        or SkinItemSelectionRequest.RequestUnsetSkinItem)
                    {
                        HandleSkinItemRequest(connection, movementState, payload);
                    }
                    else if (RequestUseItem.Matches(payload))
                    {
                        // docs/63 §1. Items::cItemPacketIdRequestUseItem — the client-to-server half
                        // of the inventory, and the ONLY packet the August client uses for it (no
                        // 0x86 and no 0xc8 has ever arrived inbound). Its layout is LIVE-VERIFIED:
                        // read off 21 real client packets in captures\wire-20260829-201025.txt,
                        // wire-20260829-220829.txt, wire-20260830-085410.txt and
                        // wire-20260830-090725.txt — every one of which this server logged as
                        // "(unanswered)" and threw away, for four waves.
                        HandleItemUseRequest(connection, (GatewaySessionState)connection.Tag!, payload);
                    }
                    else
                    {
                        _log.Info($"{connection} zone Items sub=0x{subOpcode:x2} "
                            + $"{payload.Length} bytes: {hex} (unanswered)");
                    }

                    return;
                }

                case ZoneOpcodes.GetContinentBattleInfo:
                    SendTunnel(connection, writer => new ContinentBattleInfo().WriteTo(writer));
                    _log.Info($"{connection} zone {name} → ContinentBattleInfo (empty)");
                    return;

                case ZoneOpcodes.GetRewardBuffInfo:
                    SendTunnel(connection, writer => new RewardBuffInfo().WriteTo(writer));
                    _log.Info($"{connection} zone {name} → RewardBuffInfo (no buffs)");
                    return;

                case 0xf5:
                    HandleCrateOpening(connection, movementState, payload);
                    return;

                case ZoneOpcodes.InGamePurchaseBase when payload.Length >= 3:
                {
                    if (HandleEconomyPurchase(connection, movementState, payload)) return;
                    // u16 sub-opcode (FUN_1410533c0): 0x0a WalletInfoRequest, 0x1a AccountInfoRequest (+ str
                    // locale), 0x15 CountryCodesRequest (+ str locale).
                    ushort sub = BitConverter.ToUInt16(payload, 1);
                    switch (sub)
                    {
                        case 0x000a:
                            SendTunnel(connection, writer => new WalletInfoResponse().WriteTo(writer));
                            _log.Info($"{connection} zone InGamePurchase.WalletInfoRequest → WalletInfoResponse (0 KH$)");
                            return;
                        case 0x001a:
                        {
                            string locale = payload.Length > 3 ? new PacketReader(payload.AsSpan(3)).ReadString() : "en_US";
                            SendTunnel(connection, writer => new AccountInfoResponse(locale).WriteTo(writer));
                            _log.Info($"{connection} zone InGamePurchase.AccountInfoRequest '{locale}' → AccountInfoResponse");
                            return;
                        }
                        case 0x0015:
                            SendTunnel(connection, writer => new CountryCodesResponse().WriteTo(writer));
                            _log.Info($"{connection} zone InGamePurchase.CountryCodesRequest → CountryCodesResponse (built-ins)");
                            return;
                        case 0x000e:
                            SendTunnel(connection, writer => new StoreProductsResponse(StoreProductsResponse.StationCashProducts).WriteTo(writer));
                            _log.Info($"{connection} zone InGamePurchase.StationCashProductsRequest → StationCashProductsResponse (no skus)");
                            return;
                        case 0x000f:
                            SendTunnel(connection, writer => new StoreProductsResponse(StoreProductsResponse.TargetedPromoProducts).WriteTo(writer));
                            _log.Info($"{connection} zone InGamePurchase.TargetedPromoProductsRequest → TargetedPromoProductsResponse (no skus)");
                            return;
                        case 0x001e:
                            // cIdClientStatistics: store telemetry, one-way.
                            return;
                        default:
                            _log.Info($"{connection} zone InGamePurchase sub=0x{sub:x4} {payload.Length} bytes: {hex} (unanswered)");
                            return;
                    }
                }

                case ZoneOpcodes.MatchHistoryBase when payload.Length >= 2:
                {
                    // u8 sub-opcode (FUN_1413f1210): 0x06 RequestRanking {u64; u64; u32 gameMode; u32}, 0x11 schedule window (u8 open).
                    switch (payload[1])
                    {
                        case 0x06:
                        case 0x0a:
                        case 0x1d:
                            HandleRankingRequest(connection, (GatewaySessionState)connection.Tag!, payload);
                            return;
                        case 0x11:
                            if (payload.Length >= 3 && payload[2] != 0)
                            {
                                SendHostedSchedule(connection, (GatewaySessionState)connection.Tag!);
                                _log.Info($"{connection} zone MatchHistory events window opened → ScheduleReply (no events)");
                            }

                            return;
                        case SelectBountyRequest.SubOpcode
                            when SelectBountyRequest.TryParse(payload, out SelectBountyRequest? bounty)
                                && bounty is not null:
                            // docs/115 §5: the Bounty screen's Confirm button. AUDIT-bounty U-1
                            // named 67 13 and 67 1d as the candidates; the needle it asked for
                            // (refs:1431f4340 → FUN_1404b07a0 → stub 0x1412267a0 → FUN_1413f0940)
                            // settles it as 67 0c with one u32, and both candidates keep falling to
                            // the log arm below.
                            HandleSelectBounty(
                                connection,
                                (GatewaySessionState)connection.Tag!,
                                bounty.BountyType);
                            return;
                        default:
                            _log.Info($"{connection} zone MatchHistory sub=0x{payload[1]:x2} {payload.Length} bytes: {hex} (unanswered)");
                            return;
                    }
                }

                case ZoneOpcodes.CommandBase or ZoneOpcodes.AdminBase or ZoneOpcodes.InventoryBase
                    when TryHandleNativeConsoleCommand(connection, (GatewaySessionState)connection.Tag!, payload):
                    return;

                case ZoneOpcodes.CommandBase or ZoneOpcodes.AdminBase
                    when ConsoleInbound.Matches(payload) && DevConsoleEngine is not null:
                {
                    // docs/103: the developer console. 09 42 Command.ExecuteCommand is a typed
                    // /name, and 09 10 05 Command.Spectate "ObserverCamera" is the noise the
                    // client's own console-toggle command makes on every press. With
                    // CRANBERRY_CONSOLE=0 the engine is never built, HandleConsoleCommand returns
                    // at once, and the bytes fall to the default log arm exactly as they do today.
                    HandleConsoleCommand(connection, (GatewaySessionState)connection.Tag!, payload);
                    return;
                }

                case ZoneOpcodes.CommandBase when RecipeStartRequest.Matches(payload):
                {
                    // 09 1a Command.RecipeStart (docs/62 §6, docs/115 §5). The framing is CLOSED and
                    // has been since 2026-08-30: four live requests in host-20260830-131819.log
                    // (15:40:36.141, 15:48:21.896, 16:04:23.020, 16:19:04.196) all parsed as
                    // 09 1A 00 | u32 recipeId | u32 count, 11 bytes, shape=U16SubWithCount,
                    // trailing=0 — which is also the framing the owner's own Z1 reads
                    // (ZoneCrafting.cs:126-134). docs/62 §6's [BLOCKED] is retired. The reader still
                    // tries all four framings and accepts only the one whose id names a recipe THIS
                    // session delivered; anything else is logged whole.
                    var state = (GatewaySessionState)connection.Tag!;
                    if (RefuseInteractionDuringLogout(connection, state)) return;
                    CraftingOptions craftOptions = _options.Crafting.Effective;
                    string fullHex = Convert.ToHexString(payload);
                    if (!RecipeStartRequest.TryParse(
                            payload,
                            id => CraftingCatalog.IsKnownIn(id, craftOptions.RetailRecipes),
                            out RecipeStartRequest request))
                    {
                        // NEVER truncated: the catch-all above stops at 48 bytes, which would make
                        // docs/59 §1.7 needle 2 unreadable in the one log line that answers it.
                        _log.Warn($"{connection} crafting: UNPARSED Command.RecipeStart, "
                            + $"{payload.Length} bytes: {fullHex}");
                        return;
                    }

                    _log.Info($"{connection} zone Command.RecipeStart recipe={request.RecipeId} "
                        + $"count={request.Count} shape={request.Shape} "
                        + $"trailing={request.TrailingBytes} ({payload.Length} bytes): {fullHex}");
                    if (state.Inventory is not PlayerInventory craftingInventory)
                    {
                        _log.Warn($"{connection} crafting: Command.RecipeStart arrived before the "
                            + "container bootstrap — nothing to craft into");
                        return;
                    }

                    if (craftOptions.CraftCastBar)
                    {
                        RunCraft(connection, state, craftingInventory, request, craftOptions);
                        return;
                    }

                    // CRANBERRY_CRAFT_CAST_BAR=0 — the wave-6 path, byte for byte: the craft
                    // completes inside the request, with no bar, no animation and no busy window.
                    CraftOutcome outcome = CraftingService.Craft(
                        craftingInventory, request.RecipeId, request.Count, craftOptions);
                    ApplyCraft(connection, state, craftingInventory, outcome);
                    return;
                }

                case ZoneOpcodes.CommandBase
                    when payload.Length >= InteractRequest.MinimumLength
                        && BitConverter.ToUInt16(payload, 1) == InteractRequest.SubOpcode:
                {
                    // 09 07 00 <u64 guid>: Command.InteractRequest — the F key on a world object
                    // (docs/13 §4a). The guid offset was a lead; the raw bytes are logged so one
                    // live press confirms it.
                    var state = (GatewaySessionState)connection.Tag!;
                    InteractRequest request = InteractRequest.Parse(payload);
                    _log.Info($"{connection} zone Command.InteractRequest guid={request.TargetGuid} "
                        + $"({payload.Length} bytes, {request.TrailingBytes} trailing): {hex}");
                    // docs/43 blocker 1: an E-press on a car may arrive here rather than as 70 01.
                    // Vehicles resolve by exact guid only, so this can never steal a loot pickup.
                    if (TryEnterVehicle(
                            connection, state, request.TargetGuid, -1, "Command.InteractRequest",
                            VehicleEntrySource.InteractRequest)
                        || TryToggleDoor(connection, state, payload, "Command.InteractRequest"))
                    {
                        return;
                    }

                    TryPickUpGroundLoot(connection, state, request.TargetGuid, "Command.InteractRequest");
                    return;
                }

                case ZoneOpcodes.CommandBase
                    when payload.Length >= PlayerSelect.MinimumLength
                        && BitConverter.ToUInt16(payload, 1) == PlayerSelect.SubOpcode:
                {
                    // 09 15 00: Command.PlayerSelect — the August client emits this for the same F
                    // press as the InteractRequest above, so the pickup must be idempotent
                    // (docs/13 §8). Whichever arrives first claims the object; the other no-ops.
                    var state = (GatewaySessionState)connection.Tag!;
                    PlayerSelect select = PlayerSelect.Parse(payload);
                    _log.Info($"{connection} zone Command.PlayerSelect selecting={select.SelectingCharacterGuid} "
                        + $"target={select.TargetGuid} ({payload.Length} bytes): {hex}");
                    if (TryEnterVehicle(
                            connection, state, select.TargetGuid, -1, "Command.PlayerSelect",
                            VehicleEntrySource.PlayerSelect)
                        || TryToggleDoor(connection, state, payload, "Command.PlayerSelect"))
                    {
                        return;
                    }

                    TryPickUpGroundLoot(connection, state, select.TargetGuid, "Command.PlayerSelect");
                    return;
                }

                case ZoneOpcodes.CommandBase when InteractionStringRequest.Matches(payload):
                {
                    // 09 2d 00 <u64 guid> <u32> <u32>: Command.InteractionString — the client asking
                    // what the [F] prompt should SAY (docs/47 §4c-§4d). Cranberry has never answered
                    // it, which is why no prompt has ever carried text, for doors OR for ground loot.
                    // Its driver FUN_14140bcd0 asks at most once a second, only while a target is
                    // bound and inside the ea 04's 3 m range, and FUN_1412bae90 BLANKS the label on
                    // every target change — so the reply goes out on this tick, and it must echo the
                    // guid the request named or FUN_14129e7c0 drops it.
                    var state = (GatewaySessionState)connection.Tag!;
                    InteractionStringRequest request = InteractionStringRequest.Parse(payload);

                    // The same resolution order as the interact dispatch above: vehicle, door, loot.
                    InteractionTargetKind kind;
                    if (state.Fleet is VehicleFleet promptFleet
                        && promptFleet.TryGet(request.TargetGuid, out MatchVehicle? _))
                    {
                        kind = InteractionTargetKind.Vehicle;
                    }
                    else if (state.Doors is MatchDoors promptDoors
                        && promptDoors.TryGet(request.TargetGuid, out DoorInstance? promptDoor))
                    {
                        kind = InteractionPromptStrings.KindOf(promptDoor);
                    }
                    else if (state.Loot.IsLootGuid(request.TargetGuid))
                    {
                        kind = InteractionTargetKind.GroundLoot;
                    }
                    else
                    {
                        // Reply anyway: FUN_14140c480 is called either way, and an answered request
                        // is one the client stops re-asking every second for the rest of the match.
                        kind = InteractionTargetKind.Unknown;
                    }

                    InteractionStringReply reply = InteractionStringReply.For(request.TargetGuid, kind);
                    SendTunnel(connection, writer => reply.WriteTo(writer));
                    _log.Info($"{connection} prompt: Command.InteractionString guid={request.TargetGuid} → "
                        + $"{kind}, string id {reply.StringId} ({InteractionStringReply.MinimalLength} B) "
                        + "— NOT live-verified (docs/47 §I5 step B)");
                    return;
                }

                case ZoneOpcodes.CommandBase when payload.Length >= 3 && BitConverter.ToUInt16(payload, 1) == 0x0008:
                {
                    // 09 08 00 cCommandPacketIdInteractCancel — exactly 3 bytes, no body [P-live].
                    // docs/47 §4b CORRECTS docs/36 §3's reading of this packet: its writer
                    // FUN_140b78110 has one body field and it is a literal zero, so there is no
                    // target in it and it is NOT evidence that the interaction system found nothing.
                    // It fires with nothing under the cursor AND one millisecond after every
                    // successful pickup, when the object was just removed. The positive proof that
                    // the target binds is 09 15 + 09 07 naming the correct guid 3 ms apart, which
                    // ground loot has been doing since wave 2.
                    var state = (GatewaySessionState)connection.Tag!;
                    _log.Info($"{connection} loot: Command.InteractCancel (09 08) in {state.Match} with "
                        + $"{state.Loot.Count} ground item(s) registered — the client released its "
                        + "interaction target. FUN_140b78110 is nullary: this packet carries no target "
                        + "and says NOTHING about whether one was bound (docs/47 §4b)");
                    return;
                }

                case ZoneOpcodes.CommandBase when payload.Length >= 3 && BitConverter.ToUInt16(payload, 1) == 0x0016:
                    // 09 16 00 cCommandPacketFreeInteractionNpc — 3 bytes, 475 occurrences across the
                    // capture corpus. The client releasing an interaction candidate; far too
                    // frequent to log per packet.
                    //
                    // docs/105 §9 (MENU-RETAIL-GAP U-1, second half): it is NOT one-way. The
                    // 2026-08-22 admin capture answers every single one — 35 c2s / 35 s2c in the
                    // menu session 1118:62892 and 4 c2s / 4 s2c in the match session 1119:53544,
                    // 30–50 ms behind the request, identical three bytes both ways. The match
                    // session has zero view changes, which is what proves the echo is bound to this
                    // packet rather than to a menu screen change (MENU-RETAIL-GAP guessed the
                    // latter).
                    if (_options.MenuActor.EchoFreeInteractionNpc)
                    {
                        SendTunnel(connection, writer => new FreeInteractionNpcEcho().WriteTo(writer));
                    }

                    return;

                case ZoneOpcodes.CharacterBase
                    when payload.Length == WeaponStance.Length
                        && payload[1] == WeaponStance.SubOpcode:
                {
                    // 0f 20 Character.WeaponStance, c2s. The client reports its own stance as it
                    // aims and hip-fires, using the identical 14-byte layout the server asserts
                    // with. The FIRST one of these is the proof S6 §7.3 wants: it says the stance
                    // machine is running, and it is the signal to stop re-asserting (the owner's
                    // Z1 AbilityState.ClientToldUsStance). No answer is owed.
                    var stanceState = (GatewaySessionState)connection.Tag!;
                    if (!WeaponStance.TryParse(payload, out WeaponStance? reported)
                        || reported is null)
                    {
                        _log.Info($"{connection} zone CharacterBase {payload.Length} bytes: {hex} (unanswered)");
                        return;
                    }

                    bool first = !stanceState.ClientSentWeaponStance;
                    stanceState.ClientSentWeaponStance = true;
                    _log.Info($"{connection} WEAPONSTANCE c2s guid={reported.CharacterGuid} "
                        + $"stance={reported.Stance}"
                        + (first
                            ? $" — FIRST of this session after {stanceState.WeaponStancesSent} "
                                + "server assert(s); the re-assert stops here"
                            : string.Empty));
                    // docs/100 / docs/109 lane 3C: forward it to this character's viewers. The
                    // packet names the character by guid, so a peer's aim and hip-fire read the
                    // same on every screen instead of only on his own.
                    RelayPeerStance(connection, stanceState, reported);
                    return;
                }

                case ZoneOpcodes.CharacterBase
                    when payload.Length >= FullCharacterDataRequest.Length
                        && payload[1] == FullCharacterDataRequest.SubOpcode:
                {
                    // 0F 45 <u64 guid>: Character.FullCharacterDataRequest — 10 bytes, live-proven
                    // (docs/19 §2). The client sends exactly one per AddLightweightNpc, ~150 ms
                    // after the spawn burst, and never retries. The answer is LightweightToFullNpc
                    // 0xda, which the client matches by TRANSIENT id (FUN_140b02060), so the guid
                    // has to be resolved through this session's LootWorld first.
                    var state = (GatewaySessionState)connection.Tag!;
                    FullCharacterDataRequest request = FullCharacterDataRequest.Parse(payload);
                    if (state.CrateOpening is { } opening && opening.ActorGuid == request.CharacterGuid)
                    {
                        SendTunnel(connection, new LightweightToFullNpc(CrateOpeningRun.TransientId, opening.ActorGuid).WriteTo);
                        return;
                    }
                    NoteMilestone(
                        connection,
                        state,
                        ClientMilestone.FullCharacterDataRequest,
                        request.CharacterGuid);
                    if (state.VehiclePreview is { } preview && preview.Guid == request.CharacterGuid)
                    {
                        SendTunnel(connection, preview.FullState.WriteTo);
                        return;
                    }
                    if (state.Fleet is { } requestedFleet && requestedFleet.TryGet(request.CharacterGuid, out var requestedCar))
                    {
                        if (!state.StreamedVehicles.IsSpawned(requestedCar.Guid)) return;
                        SendTunnel(connection, VehicleFullState.Create(requestedCar).WriteTo);
                        SyncVehicleAttachments(connection, state, requestedCar);
                        if (requestedCar.Health == 0) SendVehicleWreckState(connection, requestedCar);
                        return;
                    }
                    if (state.Loot.TryGet(request.CharacterGuid, out GroundLootItem? requested))
                    {
                        SendLightweightToFullNpc(connection, state, requested, "Character.FullCharacterDataRequest");
                        return;
                    }

                    // Doors are introduced with the SAME 0xd6, so the client asks for full data on
                    // every one of them too (docs/19 §2, docs/35 §3 row 7) — up to DoorMaxPerBurst
                    // requests per landing. Resolving only through LootWorld made each of those a
                    // WARN on the one diagnostic docs/35 uses to detect a broken spawn, which is how
                    // a real broken spawn would get lost in the noise. SendDoor already sent the
                    // 0xda proactively; answering again is the same no-op the loot path documents.
                    if (state.Doors is MatchDoors known
                        && known.TryGet(request.CharacterGuid, out DoorInstance? door))
                    {
                        var full = new LightweightToFullNpc(door.TransientId, door.WorldGuid);
                        SendTunnel(connection, writer => full.WriteTo(writer));
                        _log.Info($"{connection} doors: Character.FullCharacterDataRequest for {door} → "
                            + $"LightweightToFullNpc ({full.Length} B) — repeat of the proactive send");
                        return;
                    }

                    // docs/109 lane 3C: the guid may name another PLAYER rather than a world
                    // object — the client asks for full data on a peer the same way it asks for one
                    // on a loot pile. The answer is d9 LightweightToFullPc, whose blob is not
                    // derived (docs/100 §3), so this is logged once per pair and left unanswered
                    // rather than reported as a broken spawn.
                    if (NotePeerFullCharacterDataRequest(connection, state, request.CharacterGuid))
                    {
                        return;
                    }

                    _log.Warn($"{connection} loot: Character.FullCharacterDataRequest guid={request.CharacterGuid} "
                        + $"is NOT registered ground loot ({state.Loot.Count} on the ground) and names no "
                        + $"spawned door ({state.Doors?.Count ?? 0} live) — unanswered, so that object stays "
                        + $"stuck in the client's 'full data pending' state: {hex}");
                    return;
                }

                case ZoneOpcodes.CommandBase when payload.Length >= 3 && BitConverter.ToUInt16(payload, 1) == 0x004e:
                {
                    var state = (GatewaySessionState)connection.Tag!;
                    StartLogout(connection, state);
                    return;
                }

                case ZoneOpcodes.CharacterSelectSessionRequest:
                {
                    // The session id becomes the ticket of the client's new LoginUdp_14 LoginRequest.
                    var state = (GatewaySessionState)connection.Tag!;
                    if (state.PendingLogout is not null) return;
                    AbandonMatch(connection, state, name);
                    string sessionId = CharacterSelectTicketFactory?.Invoke(state.AccountId)
                        ?? $"lp2.{Guid.NewGuid():N}.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.cranberry";
                    SendTunnel(connection, writer => new CharacterSelectSessionResponse(sessionId).WriteTo(writer));
                    _log.Info($"{connection} zone {name} → CharacterSelectSessionResponse (client returns to character select)");
                    return;
                }

                case ZoneOpcodes.ClientIsReady:
                {
                    var state = (GatewaySessionState)connection.Tag!;
                    _log.Info($"{connection} zone {name} ({state.Match})");
                    bool zoning = state.Match == MatchStep.Zoning;
                    NoteMilestone(
                        connection,
                        state,
                        zoning ? ClientMilestone.ZoningClientIsReady : ClientMilestone.MenuClientIsReady);
                    if (zoning)
                    {
                        ExpectClient(
                            connection,
                            state,
                            ClientMilestone.ClientFinishedLoading,
                            "answered ClientIsReady (Zoning)");
                    }

                    if (!state.AppearanceReadySent)
                    {
                        // SendSelf creates the actor asynchronously. ClientIsReady is the first
                        // client-owned proof that it exists, so repeat the same full dress once.
                        // ORDER IS LOAD-BEARING (docs/32, third regression): the dress
                        // (SetCharacterEquipment 94/01) precedes the SetSkinItem rows (ac/24).
                        // The only capture with wardrobe selections that the client accepted,
                        // wire-20260829-150206.txt rows 261-267 and logs/host-20260829-150206.log
                        // :136-137, has 94/01 first on this very resync; the refused 17:33 session
                        // inverts it. Skins-before-dress has never been accepted by a client.
                        // docs/80 edit 5 does NOT reach this one. The repeat is not navigation: it
                        // is the first client-owned proof that the actor exists, and the actor
                        // creator hides the local player until a SetCharacterEquipment for its own
                        // guid arrives. Suppressing it because its bytes match the bootstrap dress
                        // would be exactly the class of "optimisation" docs/32 was written about.
                        state.Dress.Forget();
                        SendCharacterAppearance(connection, state, "ClientIsReady");

                        // D221 (docs/111), the owner's defects 1 and 3 of 2026-09-03. In the LOBBY
                        // the ac/24 re-announce is the packet that makes the main menu disagree
                        // with the Gear editor. Every other menu burst in
                        // captures\wire-20260903-174857.txt ends with the dress - bootstrap :23-34,
                        // Gear-editor open :331-342, Gear-editor close :447-458 - and all four
                        // 94/01 packets are byte-identical (742 bytes, the same seven attachments).
                        // This one site alone ends with ac/24 x7 (:53-60), so the menu shows the
                        // client's OWN skin-manager recomposite (D86's purple suit) while every
                        // wardrobe screen shows the server's dress. Two sources, one character.
                        //
                        // D211 rules out the other repair - appending a second dress after the
                        // rows is the packet that wedged the next world load - so the rows go
                        // instead of the dress moving. That leaves a bare 94/01, which is exactly
                        // what the 2026-08-29 18:43 zoning-proof session sent: that character had
                        // no selections, so SendWornSkinItems emitted nothing there either. The
                        // rows are not lost - the bootstrap sent them 2 s earlier (:27-33) and the
                        // Gear editor re-sends them. The MATCH ready reply is untouched: docs/32's
                        // third regression and regression guard 4 pin that one.
                        // CRANBERRY_MENU_LOOK_DRESS=0 restores the lobby re-announce.
                        if (zoning || !_options.Skins.MenuLookFromDressOnly)
                        {
                            SendWornSkinItems(connection, state, "ClientIsReady", reassertDress: true);
                        }

                        SendInitialCharacterResources(connection, state, "ClientIsReady");
                        // Both of this wave's new packet families go here, and IN THE MATCH ONLY.
                        // The LoginZone menu is the critical path to PLAY and its burst is
                        // byte-identical to the known-good capture; docs/32 is the cautionary tale
                        // about new packets on a proven-safe path, and neither lane's acceptance
                        // check needs the menu (docs/40 acceptance, docs/41 §I3 — both in-match).
                        if (zoning)
                        {
                            // docs/40 step 2: the stat burst goes out once the character exists
                            // client-side and NEVER before — a 0x0f sub against an unknown guid is
                            // silently dropped (docs/21 §1b), which would look exactly like "the
                            // speeds did nothing". ClientIsReady is the client's own proof that the
                            // actor exists, so this is the first legal moment.
                            // docs/41 §I1(b): the container model, ordered after the dress and the
                            // resources — never above them (docs/32, third regression).
                            EnsureInventory(connection, state, "ClientIsReady (Zoning)");
                            SendMovementStats(connection, state, "ClientIsReady (Zoning)");
                            // docs/62 stage 1: the crafting tab's contents. AFTER the zoning burst,
                            // never inside it (regression guard 4 pins that opcode order against
                            // captures\wire-20260829-150206.txt), and after the inventory so a seed
                            // grant has containers to land in. The client ships ZERO recipe data —
                            // an empty tab is entirely the server's doing.
                            SendRecipes(connection, state, "ClientIsReady (Zoning)");
                        }

                        state.AppearanceReadySent = true;
                        if (!zoning)
                            SendTunnel(connection, new ConsolePrint("@cranberry/match-exit/1;ready").WriteTo);

                        // docs/103 / D178: the developer console's AddWorldCommand burst, the one
                        // packet family this wave puts on the MENU path as well as the zoning one.
                        // It is sent SYNCHRONOUSLY, never through Later/Post: the bootstrap tests
                        // pin the label list of everything sent after this arm returns, and a
                        // deferred burst would land inside that window. Re-sending a name the
                        // client already holds is a proven silent early return
                        // (FUN_141280280:36-46), which is what makes each-zone safe as the default.
                        SendConsoleBurst(connection, state, zoning);

                        if (!zoning)
                        {
                            // docs/105 §10 (MENU-RETAIL-GAP U-3 / U-4): the LoginZone top bar and
                            // the MOTD panel. MENU ONLY and after the console burst, so the burst
                            // the bootstrap tests pin is untouched and nothing here can reorder the
                            // dress. Both arms are separately revertible.
                            SendMenuTopBar(connection);
                            SendMenuMotd(connection);
                            SendEconomyPurchaseCatalog(connection, state);
                        }
                        else
                        {
                            SendAccountExperience(connection, state);
                        }
                    }

                    if (_options.AutoMatchMs > 0 && state.Match == MatchStep.Menu && !state.AutoMatchArmed)
                    {
                        // Development aid (ZoneOptions.AutoMatchMs): start the match without a PLAY click.
                        state.AutoMatchArmed = true;
                        _log.Info($"{connection} match: dev auto-match armed — zoning into {_options.MatchZoneName} in {_options.AutoMatchMs} ms");
                        Later(connection, _options.AutoMatchMs, () =>
                        {
                            if (state.Match == MatchStep.Menu)
                            {
                                state.Match = MatchStep.Transferring;
                                EnterMatch(connection, state, sendTransferReply: false);
                            }
                        });
                    }

                    // Development aid (ZoneOptions.DevGroundLootMs, docs/13 §9 step 1): drop the
                    // sample items where the player stands so the pickup flow can be exercised
                    // without a world loot table. In a match the drop waits for the landing
                    // instead — the staging spawn is not where the player comes down.
                    if (state.Match == MatchStep.Menu)
                    {
                        ArmDevGroundLoot(connection, state, $"{name} ({state.Match})");
                        // docs/114 §1: and the LOBBY's own five doors, which this server has
                        // never shipped — the dataset dropped them as an off-map set piece and no
                        // burst was ever armed here. They are the only doors the friend's live
                        // server is on record spawning (his 2026-08-22 admin capture, matched to
                        // 0.070 m on all five), and two of them stand 48 m and 57 m from where
                        // this server's own player is put. Deferred and paced, never inside the
                        // menu burst docs/32 pins.
                        ArmLobbyDoors(connection, state, $"{name} ({state.Match})");
                    }

                    return;
                }

                case ZoneOpcodes.PlayerWorldTransferRequest:
                {
                    // 0xec: PLAY pressed (first) or the queue-exit prompt accepted (second). The client's
                    // serializer is not decompiled; the bytes are logged for the derivation log.
                    var state = (GatewaySessionState)connection.Tag!;
                    _log.Info($"{connection} zone {name} ({state.Match}) {payload.Length} bytes: {hex}");
                    switch (state.Match)
                    {
                        case MatchStep.Menu:
                            if (!PlayerWorldTransferRequest.TryParse(payload, out var admissionRequest)) return;
                            BeginClientAdmission(connection, state, admissionRequest);
                            return;
                        case MatchStep.Queued:
                            if (!PlayerWorldTransferRequest.TryParse(payload, out var queuedRequest)
                                || queuedRequest != state.MatchTransferRequest)
                            {
                                _log.Warn($"{connection} refused changed/malformed queued admission");
                                return;
                            }
                            AcceptQueuedMatch(connection, state);
                            return;
                        default:
                            return;
                    }
                }

                case ZoneOpcodes.CancelQueueOnWorld:
                case ZoneOpcodes.DeclineEnterGameOnWorld:
                {
                    var state = (GatewaySessionState)connection.Tag!;
                    _log.Info($"{connection} zone {name} ({state.Match}) — back to the menu");
                    AbandonMatch(connection, state, name);
                    return;
                }

                case ZoneOpcodes.SynchronizedTeleportBase when payload.Length >= 3 && BitConverter.ToUInt16(payload, 1) == SynchronizedTeleport.ClientReady:
                {
                    // e8 02 00: the client is ready for the drop (world + actor loaded, countdown
                    // elapsed). Release immediately: the live runs (2026-08-29) show the client sends
                    // its Vehicle.AutoMount echo only AFTER the release un-freezes it (WaitForTeleport
                    // → Running, ~0.4 s later), so the chute is auto-deployed just after the drop, not
                    // before it. The mount burst then answers that echo.
                    var state = (GatewaySessionState)connection.Tag!;
                    if (state.Match != MatchStep.Dropping || state.ChuteGuid == 0 || state.Released)
                    {
                        _log.Info($"{connection} zone SynchronizedTeleport.ClientReady ({state.Match}) ignored: no pending parachute teleport");
                        return;
                    }

                    state.TeleportReady = true;
                    NoteMilestone(connection, state, ClientMilestone.TeleportClientReady);
                    _log.Info($"{connection} zone SynchronizedTeleport.ClientReady ({state.Match}) → release");
                    ReleaseTeleport(connection, state);
                    return;
                }

                case ZoneOpcodes.MountBase when payload.Length >= 2:
                {
                    var state = (GatewaySessionState)connection.Tag!;
                    switch (payload[1])
                    {
                        case MountRequest.SubOpcode when payload.Length >= MountRequest.Length:
                        {
                            // 70 01 is retained as the generic mount-request path. Live parachute
                            // drops use the Vehicle.AutoMount echo below instead (docs/12 §3).
                            MountRequest request = MountRequest.Parse(payload);
                            _log.Info($"{connection} zone Mount.MountRequest guid={request.VehicleGuid} seat={request.Seat} flag={request.Flag} ({state.Match})");
                            if (state.ChuteGuid != 0 && request.VehicleGuid == state.ChuteGuid)
                            {
                                SendMountBurst(connection, state, request.Seat);
                                if (state.TeleportReady && !state.Released)
                                {
                                    ReleaseTeleport(connection, state);
                                }
                            }
                            else if (!TryEnterVehicle(
                                connection, state, request.VehicleGuid, (int)request.Seat,
                                $"Mount.MountRequest seat={request.Seat}",
                                VehicleEntrySource.MountRequest))
                            {
                                _log.Warn($"{connection} zone Mount.MountRequest for unknown vehicle {request.VehicleGuid} (chute is {state.ChuteGuid}) — unanswered");
                            }

                            return;
                        }

                        case DismountRequest.SubOpcode:
                        {
                            // 70 03: the rider wants off (landing, or the exit key).
                            DismountRequest request = DismountRequest.Parse(payload);
                            if (state.ChuteGuid != 0 && state.MountRequested)
                            {
                                SendParachuteDismountBurst(
                                    connection,
                                    state,
                                    $"Mount.DismountRequest flag={request.Flag}");
                            }
                            else if (!TryExitVehicle(
                                connection, state, $"Mount.DismountRequest flag={request.Flag}"))
                            {
                                _log.Info($"{connection} zone Mount.DismountRequest flag={request.Flag} ({state.Match}) — not mounted, unanswered");
                            }

                            return;
                        }

                        case SeatChangeRequest.SubOpcode:
                            // docs/118 §4 (AUDIT-vehicles gap 14): unhandled until this lane, so
                            // there was no seat change and no seat swap. The body is a CANDIDATE -
                            // the Mount send path has no per-packet serializer - so every request
                            // is logged with its full hex and a seat that does not resolve is
                            // refused rather than acted on.
                            HandleSeatChangeRequest(connection, state, payload, hex);
                            return;

                        default:
                            _log.Info($"{connection} zone Mount sub=0x{payload[1]:x2} {payload.Length} bytes: {hex} (unanswered)");
                            return;
                    }
                }

                case ZoneOpcodes.VehicleSkinBase when payload.Length >= 2:
                    HandleVehicleSkinRequest(connection, (GatewaySessionState)connection.Tag!, payload);
                    return;

                case ZoneOpcodes.VehicleBase when payload.Length >= 2:
                {
                    var state = (GatewaySessionState)connection.Tag!;
                    // 88 19 Vehicle.AutoMount ECHOED by the client is its mount-completion request on
                    // the parachute path (live run 2026-08-29 08:44: the client auto-mounts the ready
                    // chute and echoes 88 19 with flag byte 0, versus our outbound 1 — it never sends
                    // 70 01 for the chute). Answer with the possession burst (Owner, MountResponse,
                    // Occupy) — never with another 88 19, which echo-loops (PS3-era law). 88 27
                    // CurrentMoveMode (5 = parachuting) is one-way.
                    if (payload[1] == VehicleAutoMount.SubOpcode)
                    {
                        VehicleAutoMount echo = VehicleAutoMount.Parse(payload);
                        NoteMilestone(connection, state, ClientMilestone.ParachuteAutoMountEcho, echo.VehicleGuid);
                        _log.Info($"{connection} zone Vehicle.AutoMount echo guid={echo.VehicleGuid} flag={echo.Flag} value={echo.Value} ({state.Match})");
                        if (state.ChuteGuid != 0 && echo.VehicleGuid == state.ChuteGuid)
                        {
                            if (!state.MountRequested)
                            {
                                SendMountBurst(connection, state, seat: 0);
                            }

                            if (state.TeleportReady && !state.Released)
                            {
                                ReleaseTeleport(connection, state);
                            }
                        }
                        else
                        {
                            _log.Warn($"{connection} zone Vehicle.AutoMount echo for {echo.VehicleGuid} (chute is {state.ChuteGuid}) — unanswered");
                        }

                        return;
                    }

                    if (payload[1] == VehicleDismiss.SubOpcode)
                    {
                        // The August client has two exit signals. An open-ground touchdown sent
                        // Mount.DismountRequest (70 03); a live landing under geometry instead sent
                        // this exact 10-byte, null-guid Vehicle.Dismiss first. Treat either as the
                        // same idempotent transition while the chute is still possessed. A later
                        // null-guid Dismiss after the clear burst is only an acknowledgement.
                        VehicleDismiss dismiss = VehicleDismiss.Parse(payload);
                        if (state.ChuteGuid != 0
                            && state.MountRequested
                            && (dismiss.VehicleGuid == 0 || dismiss.VehicleGuid == state.ChuteGuid))
                        {
                            _log.Info($"{connection} zone Vehicle.Dismiss guid={dismiss.VehicleGuid} ({state.Match}) → parachute landing");
                            SendParachuteDismountBurst(
                                connection,
                                state,
                                $"Vehicle.Dismiss guid={dismiss.VehicleGuid}");
                        }
                        else
                        {
                            _log.Info($"{connection} zone Vehicle.Dismiss guid={dismiss.VehicleGuid} ({state.Match}) — ignored (chute={state.ChuteGuid}, mounted={state.MountRequested})");
                        }

                        return;
                    }

                    if (payload[1] == VehicleCurrentMoveMode.SubOpcode
                        && payload.Length == VehicleCurrentMoveMode.Length)
                    {
                        // docs/43 §I.2 hook 6 / blocker 3: 88 27 Vehicle.CurrentMoveMode is one-way,
                        // and its enum is NOT interpreted — the byte is stored verbatim so that one
                        // capture from a car at speed is the derivation. (5 = parachuting is the only
                        // value seen so far, from the chute.)
                        VehicleCurrentMoveMode mode = VehicleCurrentMoveMode.Parse(payload);
                        if (state.Fleet is VehicleFleet moving
                            && moving.TryGet(mode.VehicleGuid, out MatchVehicle? driven))
                        {
                            driven.ReportedMoveMode = mode.MoveMode;
                        }

                        return;
                    }

                    if (payload[1] == 0x03)
                    {
                        // Vehicle.StateData: per-part state the driver streams at ~2 Hz. One-way.
                        return;
                    }

                    _log.Info($"{connection} zone Vehicle sub=0x{payload[1]:x2} {payload.Length} bytes: {hex} (unanswered)");
                    return;
                }

                case ZoneOpcodes.Synchronization when payload.Length >= 25:
                {
                    // 8C + 6×u64, sent every 5 s as a raw unreliable datagram; the reply sets the
                    // client's server-clock offset (reliable delivery is accepted by the dispatcher).
                    ulong serverMs = (ulong)Environment.TickCount64;
                    SynchronizationReply reply = SynchronizationReply.For(payload, serverMs);
                    SendTunnel(connection, writer => reply.WriteTo(writer));
                    return;
                }

                case ZoneOpcodes.KeepAlive:
                case ZoneOpcodes.ClientMetrics:
                case ZoneOpcodes.PlayLength:
                case ZoneOpcodes.ClientLog:
                case 0xf3:   // ClientTwitchManager traffic (TwitchConnectionUrlRequest every second)
                    // One-way client telemetry; the client's inbound dispatcher has no case for a reply.
                    return;

                case ZoneOpcodes.CollisionBase:
                    // 8e 01 cCollisionPacketIdDamage — the client's own fall / impact report, and
                    // the only wire signal that could carry one. Lane 1D-lite split it out of the
                    // telemetry group above so it is at least VISIBLE; the body has no recovered
                    // 1148 reader, so nothing is decoded out of it and no health moves. See
                    // HandleCollisionReport (ZoneService.Endgame.cs) and docs/99.
                    HandleCollisionReport(connection, (GatewaySessionState)connection.Tag!, payload);
                    return;

                case ZoneOpcodes.ClientUpdateBase when payload.Length >= 3 && BitConverter.ToUInt16(payload, 1) is 0x0044 or 0x0050:
                    // MonitorTimeDrift (1 Hz) and UpdateBattlEyeRegistration: fire-and-forget.
                    return;

                case ZoneOpcodes.ClientFinishedLoading:
                {
                    // 0x02, one-way. It used to fall through to the default: hex log, which made the
                    // single strongest "the world is built" signal on the wire indistinguishable
                    // from any other unhandled opcode. Live latency 969 ms after ClientIsReady
                    // (logs/host-20260829-184346.log 18:44:31.823 → 18:44:32.792).
                    var state = (GatewaySessionState)connection.Tag!;
                    _log.Info($"{connection} zone {name} ({state.Match}) — the client has built the world");
                    if (state.Match == MatchStep.Zoning) state.PregameClientReady = true;
                    NoteMilestone(connection, state, ClientMilestone.ClientFinishedLoading);
                    InitializeDestructibles(connection, state);
                    ExpectClient(
                        connection,
                        state,
                        ClientMilestone.LoadingScreenClosed,
                        "client sent ClientFinishedLoading");

                    // D249 (docs/113): arrival at Fort Destiny. This packet is the client's own
                    // proof that the lobby world is built, so it — not a blind 15,000 ms timer —
                    // is when the lobby HUD, the currency rows and the bounty tables go out. The
                    // player then gets the whole 120 s (D248) to back his match instead of ~27 s.
                    // CRANBERRY_LOBBY_ARM_MS=15000 restores the old blind arm.
                    if (_options.Lobby.ArmMs <= 0 || state.LobbyArmPending)
                    {
                        ArmLobbyHud(connection, state, "the client's own ClientFinishedLoading (Zoning)");
                    }

                    return;
                }

                case Destructibles.DestructiblePackets.Opcode:
                    HandleDestructibleHit(connection, (GatewaySessionState)connection.Tag!, payload);
                    return;

                case ZoneOpcodes.LoadoutsBase:
                {
                    // August hotbar input is c2s 86/06. The client queues this request before its
                    // own local selection callbacks.  Treat receipt as the first proof that the
                    // input/loadout gate passed; promotion to RHand is a separate, crash-sensitive
                    // phase and must not be inferred from the request alone.
                    var loadoutState = (GatewaySessionState)connection.Tag!;
                    if (!SelectLoadoutSlotRequest.TryParse(payload, out SelectLoadoutSlotRequest? request)
                        || request is null)
                    {
                        _log.Info($"{connection} zone LoadoutsBase {payload.Length} bytes: {hex} (unanswered)");
                        return;
                    }

                    if (loadoutState.Inventory is not PlayerInventory inventory)
                    {
                        _log.Info($"{connection} hotbar: SelectSlot {request.SlotId} before inventory bootstrap — ignored");
                        return;
                    }

                    // A c2s 86/06 is the useful proof that the client's *own* key path accepted
                    // this loadout entry.  Do not answer it by re-sending 86/07: the August client
                    // applies that packet through the lower-level selection routine with its c2s
                    // echo disabled, which is how the unconditional bootstrap Fists selection
                    // suppressed both mouse-look and movement (docs/89 / live A-B 2026-08-31).
                    //
                    // WHICH KEY IS WHICH, from the client's OWN two files (docs/102 §5):
                    // InputProfile_Default.xml binds 1..5 to the actions Slot1..Slot5, and
                    // LoadoutSlots.txt loadout 17 puts SLOT_INPUT_ACTION Slot1 on slot 1, Slot2 on
                    // slot 2, Slot3 on slot 4, Slot4 on slot 7 (ITEM_ID 85, the FISTS) and Slot5 on
                    // slot 5 (class 25081, which ItemClassMappings row 337 maps the Binoculars 1542
                    // onto). So key 4 = fists and key 5 = binoculars, and neither is a special case
                    // here: they are wheelable slots whose occupants ACTIVE_EQUIP_SLOT_ID is 7.
                    // D341 (research inventory.md §8 gap 2): Q and E are loadout slots 40 and 41
                    // (ConsumeItem1/2). They are not wheelable - nothing goes to the hand - the key
                    // consumes the bound medical through the same cast-and-heal as the right-click
                    // option (Z1 ZoneItemUse.cs:655-680).
                    if (request.SlotId is SurvivorLoadout.QuickUseOne or SurvivorLoadout.QuickUseTwo
                        && inventory.LoadoutSlots.TryGetValue(request.SlotId, out InventoryItemInstance? quick)
                        && MedicalModel.For(quick.DefinitionId) is not null)
                    {
                        ItemActionResult quickPlan = InventoryActions.Consume(
                            inventory, quick, ItemUseOptionKind.ConsumeItem, optionId: 0);
                        if (quickPlan.Kind == ItemActionKind.Consume)
                        {
                            _log.Info($"{connection} hotbar: SelectSlot {request.SlotId} = quick-use "
                                + $"{(request.SlotId == SurvivorLoadout.QuickUseOne ? "Q" : "E")} → consume");
                            RunConsume(connection, loadoutState, inventory, quickPlan);
                        }

                        return;
                    }

                    if (!LoadoutSlotTable.TryGet(
                            inventory.LoadoutId,
                            request.SlotId,
                            out LoadoutSlotDefinition slot)
                        || !slot.Wheelable
                        || !inventory.LoadoutSlots.TryGetValue(
                            request.SlotId,
                            out InventoryItemInstance? selected)
                        || selected.Fact.ActiveEquipSlotId != BodySlots.RightHand)
                    {
                        _log.Info($"{connection} hotbar: SelectSlot {request.SlotId} refused — the slot is empty, non-wheelable, or cannot be held");
                        return;
                    }

                    // A repeated key press must not tear down the active ability or restart
                    // a draw already in progress. In particular, do not uninit/rebind Fists.
                    if (inventory.CurrentLoadoutSlotId == request.SlotId)
                        return;

                    // Read the two things the draw needs BEFORE the model moves anything: the
                    // ability of what is leaving the hand (WieldSequence step 1) and the back peg
                    // the drawn item is hanging on right now (step 2's 94 03). Z1 reads that peg
                    // off the outfit and never re-derives it — S6 §3.2 step 1.
                    uint hotbarPreviousAbilityId = ActiveHandAbilityId(inventory);
                    uint hotbarVacatedBodySlotId =
                        selected.EquipmentSlotId != BodySlots.RightHand ? selected.EquipmentSlotId : 0;

                    if (!inventory.TrySelectLoadoutSlot(request.SlotId, out LoadoutSelection? selection)
                        || selection is null)
                    {
                        _log.Info($"{connection} hotbar: SelectSlot {request.SlotId} refused by the inventory model");
                        return;
                    }

                    bool hotbarHeld =
                        inventory.EquipmentSlots.TryGetValue(
                            BodySlots.RightHand, out InventoryItemInstance? hotbarHand)
                        && hotbarHand.Guid == selected.Guid;

                    // Selecting a wheel slot changes equipment bindings, not the item's
                    // container/loadout placement. Retain an established client's item and
                    // selected skin: deleting it unloads the just-picked-up AR-15 while the
                    // hand is trying to draw it. A weapon withheld at bootstrap still needs
                    // its first grant, as recorded by the fire-group delivery ledger.
                    if (hotbarHeld
                        && inventory.Options.UseWieldSequence
                        && TrySendWieldSequence(
                            connection,
                            loadoutState,
                            inventory,
                            selected,
                            hotbarPreviousAbilityId,
                            hotbarVacatedBodySlotId,
                            itemAlreadyGranted: loadoutState.Weapons.Clearance?.IsClearedForActiveHand(selected.Guid) == true))
                    {
                        // The sequence refreshes equipment and abilities, retaining the item.
                    }
                    else if (inventory.Options.UseWieldSequence && selected.DefinitionId == PlayerInventory.SurvivorFistsItemDefinitionId)
                    {
                        if (hotbarPreviousAbilityId != 0)
                            SendTunnel(connection, writer => writer.WriteRaw(AbilityPackets.UninitAbility(hotbarPreviousAbilityId)));
                        SendLoadoutSlots(connection, inventory);
                        SendCharacterAppearance(connection, loadoutState, "weapon slot change");
                        BindSelectedFists(connection, loadoutState, force: true);
                    }
                    else
                    {
                        // The fallback draw, and the ONLY answer for key 4: the fists are the
                        // empty-hand sentinel, and a 94/01 body-slot-7 row for item 85 is the
                        // regression that locked movement and mouse-look after the bootstrap.
                        SendLoadoutSlots(connection, inventory);
                        SendCharacterAppearance(
                            connection, loadoutState, $"hotbar slot {request.SlotId}");

                        if (hotbarHeld)
                        {
                            SendWeaponStance(
                                connection, $"fallback draw, hotbar slot {request.SlotId}");
                        }
                    }

                    _log.Info($"{connection} hotbar: SelectSlot {request.SlotId} drew item "
                        + $"{selected.DefinitionId} guid {selected.Guid} (clientTime={request.ClientGameTime}, "
                        + $"unknown={request.Unknown}); previous slot {selection.PreviousLoadoutSlotId}, "
                        + $"freed peg {hotbarVacatedBodySlotId}, stowed the old hand at "
                        + $"{selection.PreviousHandStowedBodySlot}, "
                        + $"hand={(hotbarHeld ? "the selected item" : "unchanged (fists/empty)")}");
                    return;
                }

                case ZoneOpcodes.WeaponBase:
                {
                    if (HandleCrateOpeningWeapon(connection, movementState, payload)) return;
                    // docs/81. D52's hole: the c2s Fire (82 03) and ProjectileHitReport (82 06)
                    // used to arrive and fall on the floor. WeaponFireArm now unwraps 82 1f
                    // MultiWeapon, decodes 82 01 / 03 / 06 / 07 behind a plausibility gate and
                    // answers a resolved hit with the hit marker. EVERY arm still logs, and a
                    // decode failure keeps exactly the old behaviour — one WEAPONFIRE line with
                    // untruncated hex and no reply — because docs/56 §1.3 counted ZERO 0x82
                    // packets in 96 captured sessions, so the trace is the strongest signal the
                    // attack path came alive.
                    var weaponState = (GatewaySessionState)connection.Tag!;

                    if (weaponState.DeathSent || weaponState.Hitpoints == 0 || weaponState.Match == MatchStep.Ended)
                        return;

                    if (!_options.Combat.Enabled)
                    {
                        _log.Info($"{connection} {WeaponBasePacketTrace.Format(payload)}");
                        return;
                    }

                    // Lane 1F: the shooter's own bag, so a reload spends carried rounds instead of
                    // conjuring them (S3 row 6, D118). Null before the inventory bootstrap and
                    // under CRANBERRY_AMMO_FROM_BAG=0, and the arm's null path is D118's behaviour
                    // exactly.
                    PlayerAmmoContext? weaponAmmo =
                        _options.Combat.Ammo.AmmoFromBag
                        && weaponState.Inventory is PlayerInventory weaponBag
                            ? new PlayerAmmoContext(
                                weaponBag, weaponState.Guid, _options.Combat.Ammo)
                            : null;

                    PrepareMeleeContext(weaponState);
                    WeaponFireArm.Handle(
                        weaponState.Combat,
                        payload,
                        Post is null ? _options.Combat with { TimedReload = false } : _options.Combat,
                        HeldItemDefinitionId(weaponState),
                        HeldItemGuid(weaponState),
                        weaponState.Movement.Player?.Position ?? default,
                        Environment.TickCount64,
                        weaponState.WeaponArmResults,
                        weaponAmmo,
                        HeldItemDisplayDefinitionId(weaponState));

                    DrainCombatArm(connection, weaponState);
                    return;
                }

                case ZoneOpcodes.EffectsBase:
                {
                    // docs/118 §3 (AUDIT-vehicles gap 6): THE BOOST. Not 0x9f - at 1148 that is
                    // cPacketIdRewardBuffsBase and has nothing to do with vehicles; 0x9e00 is
                    // cPacketIdEffectsBase and the client's receive dispatcher has case 0x9e.
                    // 9e 01 AddEffect on the press, 9e 03 RemoveEffect on the release, and the
                    // release MUST be echoed or the client's own effect manager sticks after
                    // press #1 (ClientEffects: EXPIRE_MSEC 0, FLAG_CAN_STACK 0 on all four turbo
                    // rows).
                    HandleEffects(connection, (GatewaySessionState)connection.Tag!, payload, hex);
                    return;
                }

                case ZoneOpcodes.AbilitiesBase
                    when payload.Length >= 2 && payload[1] is 0x0d or 0x0f:
                {
                    // Native 140cb5f40 owns these before the 140cc44b0 fallback.
                    // Effects 9e/01 and 9e/03 own boost state and the required release echo.
                    var controlState = (GatewaySessionState)connection.Tag!;
                    if (!HandleVehicleDriverAbility(connection, controlState, payload))
                        LogVehicleAbility(connection, controlState, payload, hex);
                    return;
                }

                case ZoneOpcodes.AbilitiesBase:
                {
                    // docs/89 §3. MELEE rides 0xa0, not 0x82: a0 01 InitAbility on the click and
                    // a0 02 UpdateAbility on the landing. Ported from the owner's ZoneAbilities.cs
                    // and ZoneCombat.MeleeSwing under D53, with the family's clean base -1.
                    var meleeState = (GatewaySessionState)connection.Tag!;
                    if (HandleVehicleDriverAbility(connection, meleeState, payload)) return;

                    if (!_options.Combat.Enabled)
                    {
                        _log.Info($"{connection} zone {name} 0xa0 abilities, {payload.Length} bytes: {hex}");
                        return;
                    }

                    PrepareMeleeContext(meleeState);
                    MeleeArm.Handle(
                        meleeState.Combat,
                        payload,
                        _options.Combat,
                        HeldItemDefinitionId(meleeState),
                        meleeState.Movement.Player?.Position ?? default,
                        Environment.TickCount64,
                        meleeState.WeaponArmResults);

                    DrainCombatArm(connection, meleeState);
                    return;
                }

                case ZoneOpcodes.ClientLogout:
                    _log.Info($"{connection} zone {name}: client is leaving");
                    return;

                default:
                    _log.Info($"{connection} zone {name} channel={tunnel.Channel} {payload.Length} bytes: {hex}");
                    return;
            }
        }
        catch (PacketFormatException ex)
        {
            _log.Warn($"{connection} zone {name} malformed: {ex.Message} ({hex})");
        }
        catch (AccountEconomyStoreException ex)
        {
            _log.Warn($"{connection} zone account economy unavailable; action refused: {ex.Message}");
        }
    }

    private void HandlePlayerMovement(
        SoeConnection connection,
        GatewaySessionState state,
        ReadOnlySpan<byte> payload)
    {
        long diagnosticStarted = BeginMovementDiagnostics(state, managed: false);
        MovementOutcome diagnosticOutcome = MovementOutcome.UnexpectedFailure;
        state.StreamPackets++;
        // Observe on an unarmed slot is a no-op returning default, which is what makes this safe on
        // the 20 Hz path (docs/35 §5e). Only the first packet after a drop release clears anything.
        state.Watchdog.Observe(ClientMilestone.DescentMovement);
        try
        {
            ClientMovementUpdate update = ClientMovementUpdate.Parse(payload);
            ParsedMovementDiagnostics(state, managed: false, update);

            if (state.HostedObserverActive)
            {
                // Spectator input only moves interest. It never moves or republishes the dead body.
                if (update.EffectivePosition is { } camera)
                    SetHostedCameraPosition(connection, state, camera, teleport: false);
                diagnosticOutcome = MovementOutcome.Applied;
                return;
            }

            // The mounted player stream is a dummy (0,0,0) pose; the real chute/rider pose is on
            // channel 3. Preserve the last on-foot state until landing promotes that managed pose.
            if (state.ChuteGuid != 0 && state.MountRequested)
            {
                diagnosticOutcome = MovementOutcome.MountedSuppressed;
                return;
            }

            // docs/49 §I4: the client reports its own stance and its own horizontal speed on this
            // channel, so a movement stat is directly checkable against what the player actually
            // moved at — the one thing docs/40 §10 said was impossible. SetReportedPosture replaces
            // the tracker's nearest-speed guess with the client's own word; Observe judges only a
            // SETTLED plateau, only the peak one per mode, and never a ramp, a free fall or a
            // stop-flag sample, because every confound the server cannot see lowers the reported
            // speed and never raises it.
            //
            // A SEATED player is excluded (verify wave 4). docs/43's cars report ordinary on-ground
            // postures at car speeds, and RecordSpeedCheck would settle a plateau well above any
            // stat this server sent, latch _checked for that mode for the whole match and inflate
            // SpeedViolations — poisoning exactly the measurement docs/49 §I4 exists to take. The
            // parachute is already excluded above; the fleet is the other mount the server knows.
            bool seated = state.Fleet is VehicleFleet mounted
                && mounted.TryGetForOccupant(state.Guid, out MatchVehicle? _);
            if (!seated)
            {
                if (update.Posture is uint reportedPosture)
                {
                    state.MovementStats.SetReportedPosture(reportedPosture);
                }

                if (update.HorizontalSpeed is float reportedSpeed)
                {
                    state.MovementStats.Observe(reportedSpeed);
                }
            }

            if (state.MovementStats.TakeSpeedCheckLine() is string checkLine)
            {
                _log.Info($"{connection} {checkLine}");
            }

            // docs/98: the owner-free grade. One line per verdict change, never per record, so the
            // 20 Hz path pays a list append. It is read before TryApplyPlayer because a rotation-only
            // record carries no position and would not reach the tracker, and the freeze is exactly
            // the state where those are most of the stream (S5a §3.2).
            if (LocomotionLockWatch.Observe(connection, update, Environment.TickCount64) is string lockLine)
            {
                _log.Info($"{connection} {lockLine}");
            }

            // D340 (research vehicles.md §7 gap 1): a SEATED client's channel-2 record carries a dummy
            // (0,0,0) pose - the rider's real pose is the car's, which the vehicle relay tracks.
            // Applying it re-centred the world pump on the origin 250 ms after seating and evicted
            // every other car, door and prop within reach (host-20260903-213229.log 21:37:38; again
            // 19:10:54 on 09-04). The same guard refuses an exact zero pose from anyone: no player
            // stands at the map origin, and a zero is the client saying "nothing to report".
            if (seated
                || update.EffectivePosition is { X: 0f, Y: 0f, Z: 0f })
            {
                diagnosticOutcome = seated ? MovementOutcome.MountedSuppressed : MovementOutcome.ZeroPoseSuppressed;
                if (state.Fleet is VehicleFleet ridden
                    && ridden.TryGetForOccupant(state.Guid, out MatchVehicle? car)
                    && car is not null)
                {
                    state.Movement.PinPlayer(car.Position);
                    CancelMedicalCastIfMoved(connection, state, car.Position);
                    CancelLogoutIfMoved(connection, state, car.Position);
                }

                return;
            }

            if (!state.Movement.TryApplyPlayer(update, out EntityMovementState? movement))
            {
                diagnosticOutcome = MovementOutcome.DismountHandoffSuppressed;
                return;
            }
            diagnosticOutcome = MovementOutcome.Applied;
            RefreshBodyBagMovement(connection, state);

            CancelMedicalCastIfMoved(connection, state, movement.Position);
            CancelLogoutIfMoved(connection, state, movement.Position);

            // docs/109 lane 3C, the one line in this handler: refresh the registry's copy of this
            // character's pose, run this viewer's 4 Hz interest pass, and re-frame this record as
            // 0x78 for everyone who holds it. Provably a no-op with one session on the host.
            NotePeerMovement(connection, state, update, movement);
            PublishTeamPose(state);

            DateTime now = DateTime.UtcNow;
            if (now - state.LastStreamLog >= TimeSpan.FromSeconds(5))
            {
                state.LastStreamLog = now;
                _log.Info($"{connection} zone player movement (channel 2): "
                    + $"received={state.StreamPackets} malformed={state.MalformedStreamPackets} "
                    + $"clientTime={movement.ClientTime} position={FormatPosition(movement.Position)}");
            }
        }
        catch (PacketFormatException ex)
        {
            if (diagnosticOutcome != MovementOutcome.Applied) diagnosticOutcome = MovementOutcome.Malformed;
            RecordMovementFormatFailure(managed: false, diagnosticOutcome == MovementOutcome.Applied);
            state.MalformedStreamPackets++;
            DateTime now = DateTime.UtcNow;
            if (now - state.LastStreamLog >= TimeSpan.FromSeconds(5))
            {
                state.LastStreamLog = now;
                _log.Warn($"{connection} malformed player movement (channel 2): {ex.Message}; "
                    + $"received={state.StreamPackets} malformed={state.MalformedStreamPackets}");
            }
        }
        finally
        {
            EndMovementDiagnostics(state, managed: false, diagnosticStarted, diagnosticOutcome);
        }
    }

    private void HandleManagedMovement(
        SoeConnection connection,
        GatewaySessionState state,
        ReadOnlySpan<byte> payload)
    {
        long diagnosticStarted = BeginMovementDiagnostics(state, managed: true);
        MovementOutcome diagnosticOutcome = MovementOutcome.UnexpectedFailure;
        state.ManagedStreamPackets++;
        try
        {
            ClientManagedMovementUpdate update = ClientManagedMovementUpdate.Parse(payload);
            ParsedMovementDiagnostics(state, managed: true, update.Movement, update.TransientId);
            // Channel-3 poses sent before the reliable righting correction may arrive later.
            // Do not let them restore the overturned body while its new physics state starts.
            if (state.Fleet is VehicleFleet rightingFleet
                && rightingFleet.TryGetForSimulator(update.TransientId, state.Guid, out var righted)
                && righted.LastRightingMs != long.MinValue
                && Environment.TickCount64 - righted.LastRightingMs < 1500
                && update.Movement.State != righted.MovementVersion)
            {
                state.RejectedManagedStreamPackets++;
                return;
            }
            state.Movement.TryGetManaged(update.TransientId, out var previousManaged);
            MatchVehicle? driven = null;
            bool authorized = previousManaged is null || previousManaged.Guid == state.ChuteGuid;
            if (previousManaged is not null && state.Fleet is { } authorityFleet
                && authorityFleet.TryGet(previousManaged.Guid, out _))
            {
                // A stale registration must not authorize a former driver, or retain a refused
                // position for the next sparse delta. Validate before merging managed state.
                authorized = authorityFleet.TryGetForSimulator(update.TransientId, state.Guid, out driven)
                    && (update.Movement.Orientation is not float heading || float.IsFinite(heading))
                    && (update.Movement.EffectivePosition is not Vector3 drivenAt
                        || authorityFleet.TryApplyOwnerPose(update.TransientId, state.Guid, drivenAt,
                            update.Movement.Orientation ?? previousManaged.Movement?.Orientation ?? driven.Yaw,
                            Environment.TickCount64, out driven));
            }
            if (!authorized || !state.Movement.TryApplyManaged(update, out ManagedEntityMovementState? entity))
            {
                diagnosticOutcome = MovementOutcome.UnownedSuppressed;
                state.RejectedManagedStreamPackets++;
                DateTime rejectedAt = DateTime.UtcNow;
                if (rejectedAt - state.LastManagedLog >= TimeSpan.FromSeconds(5))
                {
                    state.LastManagedLog = rejectedAt;
                    _log.Warn($"{connection} rejected managed movement for unauthorized or invalid transient id "
                        + $"{update.TransientId}; received={state.ManagedStreamPackets} "
                        + $"rejected={state.RejectedManagedStreamPackets}");
                }

                return;
            }
            diagnosticOutcome = MovementOutcome.Applied;

            // docs/61 §1, THE D25 LINE: the owner's own bytes, re-emitted under 0x78 to everyone
            // else who holds this car. Before it, a driven vehicle was invisible to bystanders.
            // ClientMovementUpdate has no Yaw — the heading field is Orientation — and the position
            // may arrive only inside the precise-pose block, which is what EffectivePosition merges;
            // a sparse channel-3 record with neither must not move the car to the origin or
            // stamp its retained position as a fresh physics pose.
            // Speed-only stop records must still reach observers. August omits unchanged
            // position fields; dropping that final delta leaves the previous wheel/dust/audio
            // motion active indefinitely. Do not turn a retained position into a fresh pose.
            if (driven is not null)
            {
                driven.LastClientTime = update.Movement.ClientTime;
                driven.MovementVersion = update.Movement.State;
                if (update.Movement.EffectivePosition is not null)
                    NoteVehicleOccupantPositions(connection, state, driven);
                if (driven.SeatOf(state.Guid) >= 0)
                {
                    CancelMedicalCastIfMoved(connection, state, driven.Position);
                    CancelLogoutIfMoved(connection, state, driven.Position);
                }
                bool stopped = entity.Movement is { } motion
                    && (motion.HorizontalSpeed ?? 0f) == 0f && (motion.VerticalSpeed ?? 0f) == 0f
                    && (previousManaged?.Movement?.HorizontalSpeed is float oldHorizontal && oldHorizontal != 0f
                        || previousManaged?.Movement?.VerticalSpeed is float oldVertical && oldVertical != 0f);
                if (update.Movement.Posture is uint rest && (rest & 0x40) != 0
                    && entity.Movement is { HorizontalSpeed: 0f, VerticalSpeed: 0f })
                    driven.LastSpeed = 0;
                var relay = VehiclePoseRelay.From(update);
                bool coastStopped = driven.OwnerGuid == 0 && driven.CoastingOwnerGuid == state.Guid
                    && update.Movement.Posture is uint posture && (posture & 0x40) != 0
                    && (update.Movement.HorizontalSpeed is not float freshH || freshH == 0f)
                    && (update.Movement.VerticalSpeed is not float freshV || freshV == 0f)
                    && (previousManaged?.Movement?.Posture is not uint priorPosture || (priorPosture & 0x40) == 0
                        || previousManaged.Movement.HorizontalSpeed is float coastH && coastH != 0f
                        || previousManaged.Movement.VerticalSpeed is float coastV && coastV != 0f);
                if (coastStopped)
                {
                    relay = VehiclePoseRelay.MotionStopped(update);
                    state.Movement.TryApplyManaged(new(update.TransientId,
                        ClientMovementUpdate.Parse(relay.MovementPayload.Span)), out _);
                    // Native 140b14d30 refuses every 0x78 while this GUID remains locally
                    // managed. A zero-speed echo cannot silence the former driver's car.
                    // Hand off only on the client's explicit rest flag, then seed a complete
                    // remote baseline at the final transform (never the spawn transform).
                    driven.LastSpeed = 0;
                    StopVehicleEngineForViewers(connection, state, driven);
                    SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.RemoveMotorEffect(driven, state.Guid)));
                    SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.StopEngineRuntime(driven.Definition.VehicleId)));
                    ReleaseVehicleSimulation(connection, state, driven, broadcastParked: false);
                    relay = VehiclePoseRelay.Parked(driven);
                }
                _vehiclePoses.Relay(
                    driven,
                    relay,
                    state.Guid,
                    Environment.TickCount64,
                    _options.VehicleRelay,
                    state.BountyAdmission.MatchId,
                    flushMotionStop: stopped || coastStopped);

                // docs/118 §2 (AUDIT-vehicles gap 5): the ONLY thing the server can know about a
                // car's attitude is the rotation the owning client puts in its own record - it owns
                // the physics for every vehicle id and Cranberry integrates nothing. The pulse
                // itself is charged on the world pump, never here: a 20 Hz stream must not be a
                // 20 Hz damage source.
                // 142339720 builds the vehicle's orientation from these three Euler fields.
                // The misleadingly named Rotation (0x200) is (1,1,1,1) in the actual native
                // full-car report, and the 0x1000 block is zero. Neither is its body attitude.
                if ((update.Movement.Fields & (MovementFieldMask.Orientation
                    | MovementFieldMask.Scalar14C | MovementFieldMask.Scalar150)) != 0)
                {
                    if (update.Movement.Orientation is float yaw && float.IsFinite(yaw))
                        driven.SetPose(driven.Position, yaw);
                    NoteVehicleAttitude(connection, state, driven, Quaternion.CreateFromYawPitchRoll(
                        driven.Yaw, entity.Movement?.Scalar14C ?? 0f, entity.Movement?.Scalar150 ?? 0f));
                }
            }

            // docs/88 §4a, the E5 experiment made observable instead of guessed. The touchdown
            // fallback below is gated on the RAW per-record position, and a sparse channel-3 record
            // carries none — which is the standing hypothesis for why that fallback has never fired
            // in 318 host logs. Counting the positionless records of a mounted chute turns that
            // hypothesis into a number the owner's next session prints, at the cost of one branch.
            // It is deliberately NOT acted on: widening the gate to the merged position would feed
            // a settle detector a retained, unchanging sample, and three seconds of that at 300 m
            // would dismount the player in mid-air. Measure first.
            if (state.ChuteGuid != 0
                && state.MountRequested
                && entity.Guid == state.ChuteGuid
                && update.Movement.EffectivePosition is null)
            {
                state.PositionlessChuteRecords++;
            }

            // docs/115 §3 (D239): the two facts the rebuilt descent deadline reasons with. The
            // timestamp is taken for EVERY record of this chute, positionless ones included — the
            // silence gate asks "is anybody still flying this canopy", which a sparse record answers
            // as well as a dense one. The altitude is taken only from a record that carries its own
            // raw position, for the reason docs/88 §4a gives: the merged position is retained by
            // SessionMovementState, so feeding it to an altitude gate would hand the guard a stale
            // sample. Reading, not acting — the decision is EnforceDescentDeadline's.
            if (state.ChuteGuid != 0 && state.MountRequested && entity.Guid == state.ChuteGuid)
            {
                state.ChuteLastPoseMs = Environment.TickCount64;
                // The rider's channel-2 stream is a dummy while mounted. The accepted, owned
                // channel-3 pose drives both interest and the remote canopy instead.
                if (entity.Movement?.Position is Vector3 && state.Peer is PeerSession mountedPeer)
                {
                    if (mountedPeer.ParachuteGuid != state.ChuteGuid) mountedPeer.ResetRelaySnapshot();
                    mountedPeer.ParachuteGuid = state.ChuteGuid;
                    NotePeerMovement(connection, state, update.Movement, entity.Movement);
                }
                if (update.Movement.EffectivePosition is Vector3 chuteAltitude
                    && float.IsFinite(chuteAltitude.Y))
                {
                    state.ChuteLastY = chuteAltitude.Y;
                    CancelMedicalCastIfMoved(connection, state, chuteAltitude);
                    CancelLogoutIfMoved(connection, state, chuteAltitude);
                    ArmDescentWorld(connection, state);
                }
            }

            // Vehicle.Dismiss is the normal live touchdown signal. Keep a movement-based fallback
            // for missed exits, feeding only fresh position-bearing samples: channel 3 also has
            // sparse records whose merged position is retained by SessionMovementState.
            if (state.ChuteGuid != 0
                && state.MountRequested
                && entity.Guid == state.ChuteGuid
                && update.Movement.EffectivePosition is Vector3 chutePosition
                && state.Touchdown.Observe(update.Movement.ClientTime, chutePosition))
            {
                _log.Info($"{connection} match: parachute touchdown detected at "
                    + $"{FormatPosition(chutePosition)} after client-owned descent");
                SendParachuteDismountBurst(connection, state, "managed touchdown detector");
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now - state.LastManagedLog >= TimeSpan.FromSeconds(5))
            {
                state.LastManagedLog = now;
                _log.Info($"{connection} zone managed movement (channel 3): "
                    + $"received={state.ManagedStreamPackets} malformed={state.MalformedManagedStreamPackets} "
                    + $"rejected={state.RejectedManagedStreamPackets} transient={entity.TransientId} "
                    + $"guid={entity.Guid} clientTime={entity.Movement!.ClientTime} "
                    + $"position={FormatPosition(entity.Movement.Position)} "
                    + $"positionless-chute={state.PositionlessChuteRecords}"
                    + (state.Fleet?.TryGet(entity.Guid, out var loggedVehicle) == true
                        ? $" accepted-vehicle-position={FormatPosition(loggedVehicle.Position)} vehicle-pose-rejects={loggedVehicle.RejectedPoses}"
                        : string.Empty));
            }
        }
        catch (PacketFormatException ex)
        {
            if (diagnosticOutcome != MovementOutcome.Applied) diagnosticOutcome = MovementOutcome.Malformed;
            RecordMovementFormatFailure(managed: true, diagnosticOutcome == MovementOutcome.Applied);
            state.MalformedManagedStreamPackets++;
            DateTime now = DateTime.UtcNow;
            if (now - state.LastManagedLog >= TimeSpan.FromSeconds(5))
            {
                state.LastManagedLog = now;
                _log.Warn($"{connection} malformed managed movement (channel 3): {ex.Message}; "
                    + $"received={state.ManagedStreamPackets} malformed={state.MalformedManagedStreamPackets}");
            }
        }
        finally
        {
            EndMovementDiagnostics(state, managed: true, diagnosticStarted, diagnosticOutcome);
        }
    }

    private static string FormatPosition(Vector3? position) => position is Vector3 value
        ? FormattableString.Invariant($"({value.X:F2}, {value.Y:F2}, {value.Z:F2})")
        : "unknown";

    /// <summary>
    /// Runs <paramref name="work"/> on the transport thread after <paramref name="ms"/>, if the link
    /// is still open. Returns false when there is no dispatcher and the work was refused, so a
    /// caller that keeps "is my chain running" state can tell the difference.
    /// </summary>
    private bool Later(SoeConnection? connection, int ms, Action work)
    {
        // The continuation runs on a thread-pool thread, so it MUST be marshalled onto the listener
        // thread: SoeConnection is single-threaded by construction ("every method runs on the
        // listener thread"), and a send off it races the listener's own Tick/Acknowledge over the
        // pending queue and the RC4 keystream. Without a dispatcher the only safe answer is to
        // refuse the work, exactly as the delayed bootstrap does (HandleLogin, D14) — never the
        // old `run => run()` fallback, which ran every queue, match-flow and gas timer off-thread.
        Action<Action>? post = Post;
        if (post is null)
        {
            _log.Warn($"{connection} deferred work ({ms} ms) skipped: no listener-thread dispatcher "
                + "is configured (ZoneService.Post); running it here would race the transport");
            return false;
        }

        if (_productionDiagnostics is { } diagnostics)
        {
            diagnostics.TimersScheduled++;
            long scheduled = Stopwatch.GetTimestamp();
            long deadline = scheduled + (long)(Math.Max(0, ms) * (Stopwatch.Frequency / 1000d));
            _ = Task.Delay(ms).ContinueWith(_ =>
            {
                long continued = Stopwatch.GetTimestamp();
                diagnostics.ScheduleToContinuation.RecordTicks(continued - scheduled);
                diagnostics.ContinuationLateness.RecordTicks(Math.Max(0, continued - deadline));
                post(() =>
                {
                    long callback = Stopwatch.GetTimestamp();
                    diagnostics.ContinuationToCallback.RecordTicks(callback - continued);
                    if (connection is null || connection.State == ConnectionState.Open)
                    {
                        diagnostics.TimersExecuted++;
                        try { work(); }
                        finally { diagnostics.CallbackDuration.RecordTicks(Stopwatch.GetTimestamp() - callback); }
                    }
                    else diagnostics.TimersClosedLinkSkipped++;
                });
            });
            return true;
        }
        _ = Task.Delay(ms).ContinueWith(_ => post(() =>
        {
            if (connection is null || connection.State == ConnectionState.Open)
            {
                work();
            }
        }));
        return true;
    }

    /// <summary>
    /// Match flow phase 1 (docs/11): three queue updates one second apart, then the queue-exit
    /// prompt ("Joining match in N seconds"); the client accepts with a second
    /// PlayerWorldTransferRequest, handled in <see cref="EnterMatch"/>.
    /// </summary>
    private void RunQueue(SoeConnection connection, GatewaySessionState state)
    {
        ArmHostedAccess(connection, state);
        if (UsesPublicQueue(state)) { RunPublicQueue(connection, state); return; }
        ulong queuedMatchId = state.BountyAdmission.MatchId;
        int queueGeneration = state.MatchAdmissionGeneration;
        if (PublicMatchQueueBlocked(state))
        {
            if (state.QueueWaitGeneration == queueGeneration) return;
            state.QueueWaitGeneration = queueGeneration;
            SendTunnel(connection, new QueueUpdateGameMode(Position: 1, GameMode: AdmittedGameMode(state)).WriteTo);
            Later(connection, 1000, () =>
            {
                if (state.Match == MatchStep.Queued && state.BountyAdmission.MatchId == queuedMatchId
                    && state.MatchAdmissionGeneration == queueGeneration)
                {
                    state.QueueWaitGeneration = -1;
                    RunQueue(connection, state);
                }
            });
            return;
        }
        for (int tick = 0; tick < 3; tick++)
        {
            uint position = (uint)(3 - tick);
            Later(connection, 1000 * tick, () =>
            {
                if (state.Match != MatchStep.Queued || state.BountyAdmission.MatchId != queuedMatchId
                    || state.MatchAdmissionGeneration != queueGeneration)
                {
                    return;
                }

                SendTunnel(connection, writer => new QueueUpdateGameMode(Position: position, GameMode: AdmittedGameMode(state)).WriteTo(writer));
                _log.Info($"{connection} match: queue update position={position}");
            });
        }

        Later(connection, 3000, () =>
        {
            if (state.Match != MatchStep.Queued || state.BountyAdmission.MatchId != queuedMatchId
                || state.MatchAdmissionGeneration != queueGeneration)
            {
                return;
            }

            if (!RefreshClosedQueueAdmission(connection, state)) return;
            if (PublicMatchQueueBlocked(state)) { RunQueue(connection, state); return; }
            SendTunnel(connection, writer => new QueueExit(_options.QueueExitSeconds, state.Guid).WriteTo(writer));
            _log.Info($"{connection} match: queue exit — joining in {_options.QueueExitSeconds} s, waiting for the client's accept");
        });
    }

    /// <summary>
    /// Match flow phases 2-3 (docs/11, in-place shape): transfer reply, then zone into Z2 on this
    /// connection — ClientBeginZoning (clears InitialZoneDataComplete), the self record at the
    /// staging spawn, ZoneDoneSendingInitialData; the later gates ride the client's GameTimeSync
    /// as in the first bootstrap. The lobby HUD and the drop follow on a timer.
    /// </summary>
    private void EnterMatch(SoeConnection connection, GatewaySessionState state, bool sendTransferReply = true)
    {
        if (!ValidateHostedAdmission(connection, state)) return;
        if (state.BountyAdmission.MatchId == 0
            && !PrepareMatchAdmission(state, new PlayerWorldTransferRequest(1, string.Empty, 0, 1, 1)))
        {
            _log.Warn($"{connection} no configured development match admission");
            return;
        }
        JoinSharedLoot(connection, state);
        if (sendTransferReply)
        {
            SendTunnel(connection, writer => new PlayerWorldTransferReply(ServerId: state.BountyWorldId).WriteTo(writer));
            _log.Info($"{connection} match: PlayerWorldTransferReply success → zoning into {_options.MatchZoneName} in place");
        }
        else
        {
            _log.Info($"{connection} match: dev auto-match → zoning into {_options.MatchZoneName} in place (no transfer request, no reply)");
        }

        ulong transferringMatchId = state.BountyAdmission.MatchId;
        int transferGeneration = state.MatchAdmissionGeneration;
        Later(connection, 500, () =>
        {
            // Same guard RunQueue carries: the client can press BACK (Command.StartLogoutRequest,
            // CancelQueueOnWorld, DeclineEnterGameOnWorld) between the arm and the fire, and the
            // gateway link stays open through it. Without this the server zones a session that is
            // sitting in character select.
            if (state.Match != MatchStep.Transferring || state.BountyAdmission.MatchId != transferringMatchId
                || state.MatchAdmissionGeneration != transferGeneration)
            {
                _log.Info($"{connection} match: zoning cancelled ({state.Match})");
                return;
            }

            state.Match = MatchStep.Zoning;
            if (!ValidateHostedAdmission(connection, state)) return;
            if (TryGetMatchDefinition(state.BountyWorldId, out var admittedWorld))
            {
                SendTunnel(connection, new StringHashToValueManager(WorldDisplayLabel.Values(admittedWorld.DisplayName)).WriteTo);
                RestoreHostedAdminWorldAfterTable(connection, state);
                state.InventoryActionUiState = null;
                SendInventoryActionState(connection, state);
            }
            state.PregameClientReady = false;
            // LoginZone may have reported movement after the previous match ended. This new
            // world's pose/version starts with its own input, including full records before ready.
            state.Movement.ResetPlayerWorldPose();
            state.Peer?.ResetWorldPose();
            state.LobbyArmPending = false;
            state.AppearanceReadySent = false;
            // Zoning destroys every world object the previous zone had, so the ground loot goes
            // with it and the development drop re-arms for the new world (it fires at the landing).
            state.Loot.Clear();
            state.FuelCans.Clear();
            state.BodyBags.Clear();
            state.AccessedBodyBag = 0;
            // docs/52: the working set and the taken sets belong to the world that is going away.
            // Leaving them behind would make the new match's first re-stream tick believe the
            // previous world's objects were still on the client and skip them.
            state.StreamedLoot.Clear();
            StopCharacterFire(connection, state);
            state.WorldGeneration++;
            state.Combat.Grenades.Clear();
            state.Combat.Draw.Clear();
            state.ActiveEmoteAnimationId = null;
            state.LastEmoteStartAtMs = null;
            state.PlayerArmour.Clear();
            JoinSharedLoot(connection, state);
            // docs/52 §Integration step 3: the landing burst of the world that is going away has
            // drained, but the new world's has not. The loot arm of the pump must wait for it again.
            state.LandingLootDrained = false;
            state.NextDoorPumpMs = 0;
            state.NextLootPumpMs = 0;
            state.FullNpcSent.Clear();
            // A new match starts with clothing only. Pickup-only cosmetic presets stay saved,
            // but no helmet, backpack, performance footwear or armour exists on the body yet.
            state.WorldEquipment.Clear();
            state.DevGroundLootArmed = false;
            state.RealGroundLootArmed = false;
            state.HeldWeaponItemGuid = 0;
            // docs/41 §I1(a): the container model belongs to the match actor, and InitContainers is
            // destructive on the client. A new world starts with a new (unsent) inventory.
            state.Inventory = null;
            state.FistsBound = false;
            state.EquipmentDress = null;
            state.VehiclePreview = null;
            state.MenuWeaponPreview = null;
            state.MenuWeaponPreviewCategoryId = 0;
            state.MenuApparelPreview = null;
            state.CharacterAccessGranted = false;
            // docs/42: the previous world's doors went with it. The open set is keyed by ZONE
            // instance id, so clearing it is what makes a new match start with every door shut.
            // MatchDoors.Clear() resets the streaming anchor too (docs/47 §I3), so the next match's
            // first burst is due again without a separate latch.
            // docs/114 §3: give up the member slot BEFORE the local set is cleared — the last
            // session out is what forgets the match's guid space and its open doors, and
            // LeaveSharedDoors reads state.Doors to know which member is leaving.
            LeaveSharedDoors(state);
            state.Doors?.Clear();
            state.Doors = null;
            state.LobbyDoorsArmed = false;
            // The pump latch belongs to the world too (verify wave 4). It self-heals — PumpDoors
            // ends itself when Match != InMatch and a new match cannot land inside one interval —
            // but every neighbouring field is reset here and leaving one out is how the next
            // divergence starts.
            state.WorldPumping = false;
            // docs/115: the lobby belongs to the world going away too. Without these three a second
            // match would find LobbyHudSent already latched (no lobby HUD at all) and would inherit
            // the previous match's backed bounty.
            state.LobbyHudSent = false;
            state.Bounty = MatchBountyState.None;
            state.BountyScreenSeenInLobby = false;
            state.BountyScreenInMatchOpens = 0;
            // docs/43: the car park belongs to the world too. Leaving VehiclesArmed set would make
            // ArmGroundLoot skip SpawnNearbyVehicles for the new world (no cars at all) while the
            // stale fleet still answered TryEnterVehicle for guids the client no longer holds.
            // AbandonMatch already clears both; this reset must match it or the two paths diverge.
            state.Fleet = null;
            state.VehiclesArmed = false;
            // docs/61 §2: the streamed car park, the fuel bill, a half-finished hotwire and the
            // E-press arbitration all belong to the world that is going away.
            state.StreamedVehicles.Clear();
            state.Fuel.Clear();
            state.Ignition.Clear();
            state.VehicleEntry.Clear();
            state.VehicleExitProtectedUntilMs = 0;
            state.ParachuteFallProtectedUntilMs = 0;
            state.NextVehiclePumpMs = 0;
            _vehiclePoses.Unregister(state.Guid);
            state.VehicleObserverRegistered = false;
            state.MovementStats.Reset();
            state.FootwearAudio = null;
            // docs/80 edit 5: the client rebuilds the actor across a zone transition, so the
            // post-zoning dress must always go out even when its bytes match the lobby dress.
            state.Dress.Forget();
            ChooseStagingPosition(state);
            SendTunnel(connection, writer => new ClientBeginZoning(
                _options.MatchZoneName, StagingPosition(state), new Vector4(0, 0, 0, 1), Weather: _options.Weather).WriteTo(writer));
            SendTunnel(connection, writer => new UpdateWeatherData(_options.Weather).WriteTo(writer));

            // ClientBeginZoning can rebuild the actor immediately after the self record. Re-send
            // the same unified table first; loading ReferenceData replaces rather than appends.
            // REQUIRED: the last known-good zoning (capture wire-20260829-150206.txt) carries this
            // 1,268,872-byte ReferenceData blob here. Removing it (2026-08-29) made the client die
            // even earlier — before it opened LoadingScreenWindow. Do not "optimise" it away.
            SendTunnel(connection, writer => CreateDynamicAppearanceReference().WriteTo(writer));

            // HudGameModeWindow wires its BR-only fields when it enters. Declare the mode before
            // the self/zone-done gates; the same four-dword store is repeated at lobby start.
            SendTunnel(connection, writer => GameModeHud.WriteModeTrio(writer, gameMode: AdmittedGameMode(state)));

            SelfRecord self = CreateSelfRecord(state, StagingPosition(state));
            SendTunnel(connection, SendSelfToClient.FromRecord(self));
            // REQUIRED here: the last known-good zoning capture (wire-20260829-150206.txt) carries
            // the 726-byte SetCharacterEquipment and six 24-byte skin-item rows between the self
            // record and ProfileDefinitions. Removing them (2026-08-29) killed the client sooner.
            // AND IN THIS ORDER (docs/32, third regression, found by Lane A this wave): that capture
            // sends 94/01 SetCharacterEquipment BEFORE the six ac/24 SetSkinItem rows (rows 261-267,
            // logs/host-20260829-150206.log:123-124). The build that inverted it is the one the
            // client refused. The 18:43 session that proves zoning works had zero wardrobe
            // selections, so SendWornSkinItems emitted nothing and never exercised the inversion.
            SendCharacterAppearance(connection, state, "match zoning");
            SendWornSkinItems(connection, state, "match zoning");
            SendTunnel(connection, writer => ReferenceData.EmptyProfileDefinitions.WriteTo(writer));
            SendTunnel(connection, writer => new ZoneDoneSendingInitialData().WriteTo(writer));
            _log.Info($"{connection} match: sent ClientBeginZoning {_options.MatchZoneName} @ {StagingPosition(state)}, weather, self record, profile definitions, zone-done");
            // The milestone the 2026-08-29 outage lost (docs/35 §3 row 2). Its 12,000 ms deadline is
            // held deliberately under the 15,000 ms lobby-HUD arm below, so the suppression lands
            // before the first blind send of the match flow.
            ExpectClient(
                connection,
                state,
                ClientMilestone.ZoningClientIsReady,
                "ClientBeginZoning Z2 + ReferenceData + self record + equipment + zone-done");

            // D249 (docs/113): the lobby HUD arms at the client's OWN arrival — the
            // ClientFinishedLoading it sends once the Fort Destiny world is built — rather than on
            // a blind 15,000 ms timer that ran while it was still on a loading screen. The blind
            // timer is one number away: CRANBERRY_LOBBY_ARM_MS=15000 restores exactly this arm.
            int armMs = _options.Lobby.ArmMs > 0 ? _options.Lobby.ArmMs : _options.Lobby.FallbackArmMs;
            Later(connection, armMs, () => ArmLobbyHud(connection, state, $"{armMs} ms after zoning"));
        });
    }

    /// <summary>
    /// The one gate every lobby-HUD arm goes through (D249). Both callers — the legacy blind timer
    /// and the client's own <c>ClientFinishedLoading</c> — land here, so the phase check, the
    /// watchdog suppression and the once-only latch are stated once.
    /// </summary>
    private void ArmLobbyHud(SoeConnection connection, GatewaySessionState state, string because)
    {
        if (state.Match != MatchStep.Zoning)
        {
            _log.Info($"{connection} match: lobby HUD cancelled ({state.Match})");
            return;
        }

        if (state.LobbyHudSent)
        {
            return;
        }

        if (!state.PregameClientReady)
        {
            state.LobbyArmPending = true;
            _log.Info($"{connection} lobby awaits the client's world-ready signal");
            return;
        }

        // docs/35 §5f: decided at fire time, not arm time. Without this the server keeps
        // talking to a client that is still on its loading screen and every send logs as a
        // success — the exact shape of the four-hour outage (docs/32).
        if (state.Watchdog.IsStalled)
        {
            _log.Warn($"{connection} match: lobby HUD suppressed — {state.Watchdog.Stall}");
            return;
        }

        _log.Info($"{connection} match: lobby HUD armed — {because}");
        SendLobbyHud(connection, state);
    }

    /// <summary>
    /// Match flow phase 3-4: BR HUD gate, "Waiting for players", countdown, StartMatch, then the
    /// air spawn with WaitForTeleport — the client answers with SynchronizedTeleport.ClientReady
    /// once it has loaded, and the release is sent from the dispatcher.
    /// </summary>
    private void SendLobbyHud(SoeConnection connection, GatewaySessionState state)
    {
        if (!ValidateHostedAdmission(connection, state)) return;
        if (state.BountyAdmission.MatchId == 0
            && !PrepareMatchAdmission(state, new PlayerWorldTransferRequest(
                state.BountyWorldId == 0 ? 1u : state.BountyWorldId, string.Empty, 0, 1, 1))) return;
        int lobbyGeneration = ++state.DevConsole.LobbyGeneration;
        state.Match = MatchStep.Lobby;
        NotePublicMatchPhase(state, PublicMatchPhase.PRE_GAME);
        ResetPlayerVitals(connection, state);
        JoinSharedLoot(connection, state);
        if (TryGetMatchDefinition(state.BountyWorldId, out var lobbyWorld))
        {
            SendTunnel(connection, new StringHashToValueManager(WorldDisplayLabel.Values(lobbyWorld.DisplayName)).WriteTo);
            RestoreHostedAdminWorldAfterTable(connection, state);
            state.InventoryActionUiState = null;
            SendInventoryActionState(connection, state);
        }
        state.LobbyHudSent = true;
        // ClientIsReady has created the Z2 actor. Pregame players participate in the
        // same visibility and voice projection as released players.
        NotePeerInMatch(state, inMatch: true);
        state.NextPeerInterestMs = 0;
        if (_bountyLedger?.Read(state.AccountId, state.BountyAdmission.MatchId) is { Status: "Backed" } backing)
            state.Bounty = new(backing.Amount, backing.OptionId);
        SendTunnel(connection, new BountyLobbyState(true).WriteTo);
        SendTunnel(connection, writer => GameModeHud.WriteModeTrio(writer, gameMode: AdmittedGameMode(state)));
        SendTeamRoster(connection, state);
        SendLobbyCurrency(connection, state);
        PublishBountyOffer(connection, state);
        SendTunnel(connection, writer => GameModeHud.WritePlayersRemaining(writer, -1));
        SendTunnel(connection, writer => new SynchronizedTeleport(SynchronizedTeleport.Start).WriteTo(writer));
        if (UsesPublicQueue(state))
        {
            BindSelectedFists(connection, state, force: true);
            RefreshPublicLobby(state.BountyAdmission.MatchId);
            return;
        }
        if (state.HostedGameId is not null)
        {
            BindSelectedFists(connection, state, force: true);
            SendHostedAdminWorld(connection, state);
            RefreshHostedLobby(state.BountyAdmission.MatchId, Environment.TickCount64);
            return;
        }
        // D250 (docs/113): below the minimum population the widget holds the client's own
        // "Waiting for players..." (13198) and the clock is not promised; at or above it, it counts
        // down with "Starting game ..." (13356). Both ids come from AugustStrings, resolved out of
        // the client's own CodeStringMappings.txt — this file types neither number.
        int population = BountyPopulation(state);
        bool counting = state.HostedGameId is null && population >= _options.Lobby.MinPlayers;
        // The first arrival starts this match's clock. Later arrivals inherit its remaining
        // time, even if the original player leaves; hosted games still start manually.
        long nowMs = Environment.TickCount64;
        long? lobbyDeadlineMs = state.HostedGameId is null
            ? _sharedLootMatches[state.BountyAdmission.MatchId].LobbyDeadlineMs ??= nowMs + _options.LobbyCountdownMs
            : null;
        uint countdownMs = lobbyDeadlineMs is { } deadline ? (uint)Math.Max(0, deadline - nowMs) : 0;
        uint labelId = _options.Lobby.Labels
            ? counting ? Generated.AugustStrings.HudLabels.StartingMatch : Generated.AugustStrings.HudLabels.WaitingForPlayers
            : GameModeHud.StartingMatchLabelId;
        SendTunnel(connection, writer => GameModeHud.WriteCountdown(
            writer,
            countdownMs,
            labelId: labelId));
        _log.Info($"{connection} match: lobby HUD — mode trio, pre-game state, players hidden, "
            + $"teleport start, labelled countdown {countdownMs} ms, match {state.BountyAdmission.MatchId} "
            + $"(label {labelId} \"{(counting ? Generated.AugustStrings.HudLabels.StartingMatchText : Generated.AugustStrings.HudLabels.WaitingForPlayersText)}\", "
            + $"population {population}/{_options.Lobby.MinPlayers})");

        // Fort Destiny is where the Bounty is backed (client locale 4162258810), so the balances
        // and the payout tables it needs go out with the lobby HUD and nowhere later.
        if (lobbyDeadlineMs is { } bannerDeadline) ArmLobbyBanners(connection, state, bannerDeadline);

        // Inventory arrived during Zoning, when an active-hand binding is deliberately withheld.
        // World-ready has now admitted infantry input; finish that bootstrap without requiring a
        // hotbar switch or the unsafe server-driven 86/07 selection.
        BindSelectedFists(connection, state, force: true);

        if (lobbyDeadlineMs is { } dropDeadline) Later(connection, LobbyDelayMs(dropDeadline), () =>
        {
            if (state.DevConsole.LobbyGeneration == lobbyGeneration)
                BeginMatchDrop(connection, state);
        });
    }

    /// <summary>Shared pregame drop transition for the countdown and development commands.</summary>
    private void BeginMatchDrop(SoeConnection connection, GatewaySessionState state)
    {
        // Public lobbies may exist indefinitely before the ready-player minimum is met.
        // Only the shared coordinator may freeze their roster and authorize a BR start.
        if (UsesPublicQueue(state) && !PublicQueues.Snapshots.Any(r => r.MatchId == state.BountyAdmission.MatchId
            && r.Phase is >= PublicMatchPhase.ROSTER_FROZEN and < PublicMatchPhase.ENDING)) return;
        // EXIT MATCH keeps the player in the lobby for its ten-second cast. A shorter
        // lobby timer must not admit that departing player to another round meanwhile.
        if (state.PendingLogout is not null || state.LogoutPrepared) return;
        if (!ValidateHostedAdmission(connection, state)) return;
        if (state.Match != MatchStep.Lobby)
        {
            _log.Info($"{connection} match: start cancelled ({state.Match}) — no StartMatch, parachute or gas");
            return;
        }

        // docs/35 §5f, the second suppression guard: this closure drops a player out of an
        // aeroplane and opens the gas. Neither is worth doing to a client that has stopped
        // answering — the gas would simply grind toward a fabricated death (docs/34 §5 D2).
        if (state.Watchdog.IsStalled)
        {
            _log.Warn($"{connection} match: StartMatch/parachute/gas suppressed — {state.Watchdog.Stall}");
            return;
        }

        if (!LockBackingForDrop(connection, state)) return;
        NotePublicMatchPhase(state, PublicMatchPhase.STARTING);
        state.Match = MatchStep.Dropping;
        // Freeze the roster's spawn and gas plan before any countdown is sent. Later members
        // adopt its settings even if somebody disconnects while the roster is dropping.
        PreparePopulationPlan(state);
        GasSettings matchGas = state.Schedule!.Settings;
        ResetPlayerVitals(connection, state);
        SendTunnel(connection, new MatchBountyCosts(0, 0, 0).WriteTo);
        SendTunnel(connection, new BountyLobbyState(false).WriteTo);
        // ce15 is IsInBox: clear pregame eligibility before StartMatch rebuilds the HUD.
        // Older experiments inverted it and incorrectly concluded no server lever existed.
        // Keep the optional legacy restate within the public Solo contract as well.
        if (_options.Bounty.Enabled && !_options.Bounty.SuppressDropOpen
            && BountyEligibility.IsEligible(state.BountyAdmission))
        {
            MatchBountyState backed = state.Bounty;
            SendTunnel(connection, writer => backed.WriteTo(writer));
            _log.Info($"{connection} bounty: 67 0d amount={backed.BountyAmount} type={backed.BountyType} "
                + "re-stated before ce 16 StartMatch; pregame offer is closed");
        }

        SendTunnel(connection, writer => new SynchronizedTeleport(SynchronizedTeleport.StartingMatch).WriteTo(writer));
        SendTunnel(connection, writer => GameModeHud.WriteStartMatch(writer));
        // HudGameModeWindow never hides an already-visible countdown when labelId is zero.
        // Move it directly from the lobby phase to the next retail phase.
        SendTunnel(connection, writer => GameModeHud.WriteModeTrio(writer, gameMode: AdmittedGameMode(state)));
        // The green widget counts down to the moment the first circle is revealed, so it has to
        // promise what the gas schedule will actually do: with the gas running that is
        // GasSettings.FirstRevealDelayMs, otherwise the static SafeZoneRevealMs placeholder.
        uint revealInMs = _options.EnableGas ? matchGas.FirstRevealDelayMs : _options.SafeZoneRevealMs;
        SendTunnel(connection, writer => GameModeHud.WriteCountdown(
            writer,
            revealInMs,
            labelId: GameModeHud.RevealingSafeZoneLabelId));
        // Wave 9, docs/87 §4.1-4.2 and §7 edit 5. The three BR banners the owner's own server
        // broadcasts at the drop, on the capture-proven TextAlert channel (11 31, byte-identical
        // in 1087 and 1148), in his order: Proceed follows the safe-zone sentence because that
        // is the moment the circle exists on the map (Z1 ZoneMatchFlow.cs:1416-1419). Every
        // sentence is the August client's own locale text.
        if (_options.EnableGas && _options.Gas.SendBanners)
        {
            uint releasedInSeconds = GasAlerts.SecondsOf(
                (long)matchGas.FirstRevealDelayMs + matchGas.HoldMsForPhase(1));
            SendTunnel(connection, writer => GasAlerts.Write(writer, GasAlerts.MatchBegun));
            SendTunnel(connection, writer => GasAlerts.Write(writer, GasAlerts.SafeZoneMarked(releasedInSeconds)));
            SendTunnel(connection, writer => GasAlerts.Write(writer, GasAlerts.Proceed));
            _log.Info($"{connection} gas: drop banners (11 31) — \"{GasAlerts.MatchBegun}\" / "
                + $"\"{GasAlerts.SafeZoneMarked(releasedInSeconds)}\" / \"{GasAlerts.Proceed}\"");
        }

        SendTunnel(connection, writer => WriteMatchPopulation(writer, state));
        SendTeamRoster(connection, state);
        SendTunnel(connection, new BountyLobbyState(false).WriteTo);
        // The air spawn is above MatchDropSpawn, not above the lobby compound. A vertical fall
        // from StagingSpawn lands 1,127.5 m from the nearest of the client's own 168,322 Z2
        // spawn markers, which made the whole ground-loot feature a silent no-op.
        // docs/48: one seed per match, salted per subsystem, so the drop and the gas agree and
        // one logged hex value replays both. GasSchedule.Create is a pure allocation — it writes
        // no bytes — so building it here costs the WaitForTeleport quiet window nothing, and it
        // is what lets the drop be constrained to phase 1's circle.
        Vector4 drop = _options.MatchDropSpawn;
        Vector4 air = drop with { Y = drop.Y + _options.DropAltitude };
        if (TryPopulationDrop(state, out DropPlan plan, out string? why))
        {
            drop = plan.PositionVector4;
            air = plan.AirVector4;
            _log.Info($"{connection} match: drop — {plan.Describe()}");
        }
        else if (why is not null)
        {
            _log.Warn($"{connection} match: random drop unavailable ({why}); using the fixed "
                + $"spawn {drop} (seed {MatchSeeds.Format(state.MatchSeed)})");
        }
        else
        {
            _log.Info($"{connection} match: random drop disabled (ZoneOptions.Drop.Enabled = "
                + $"false) — fixed spawn {drop} (seed {MatchSeeds.Format(state.MatchSeed)})");
        }

        // Every burst centre falls back to this, not to the option, until the client's own pose
        // arrives (docs/48 §Integration step 6).
        state.Drop = drop;
        SendTunnel(connection, writer => new UpdateLocation(air, new Vector4(0, 0, 0, 1), Apply: true, WaitForTeleport: true).WriteTo(writer));
        _log.Info($"{connection} match: starting — StartMatch, 1 remaining, in-match HUD, "
            + $"safe-zone reveal countdown {revealInMs} ms, "
            + $"UpdateLocation to the air spawn {air} (WaitForTeleport)");
        ExpectClient(
            connection,
            state,
            ClientMilestone.TeleportClientReady,
            "StartMatch + UpdateLocation to the air spawn (WaitForTeleport)");
        // NOTHING new goes on the wire between here and SynchronizedTeleport.ClientReady. This
        // is the one window where the client is mid-teleport and rebuilding the actor, and the
        // milestone armed two lines above is the only evidence that it survived: a packet it
        // refuses here costs the drop and leaves a TeleportClientReady stall that names no
        // cause. The starter weapon used to be granted right here; it now rides the
        // parachute-landing burst instead (docs/32's two regressions were both equipment-shaped
        // packets injected into a transition).
        SendParachute(connection, state, air);
        StartGas(connection, state);
    }

    /// <summary>
    /// How many players this lobby holds. The 3C lane's <see cref="World.SessionRegistry"/> already
    /// counts every live gateway session on this host, so the number is read from it rather than
    /// invented; with the registry empty (a bootstrap that never admitted anybody) it is 1, because
    /// the session being served is by definition present.
    /// </summary>
    private int LobbyPopulation() => Math.Max(1, _peers.Count);

    /// <summary>
    /// The <c>WallOfData.WindowEvent</c> window name the client uses for its own Bounty screen —
    /// the telemetry name <c>FUN_14120c590</c> builds, at file offset 52,340,416 of
    /// <c>H1Z1.exe</c>. It is not a packet we send and not a window we can close.
    /// </summary>
    private const string BountyScreenWindowEvent = "UI_Binding::ToggleBountyScreen";

    /// <summary>
    /// docs/113 §3 — the four <c>Currency.SetAccountCurrencyRecord</c> (<c>ab 03</c>, 10 B) rows at
    /// Fort Destiny arrival: Scrap 1, Crowns 4, Skulls 5 and <b>Credits 6</b>.
    /// <para>
    /// Before this lane <c>ab 03</c> was sent only on the main menu, three rows, all zero, and
    /// Credits never at all (AUDIT-bounty G3) — so every ante button on the Bounty screen was
    /// unusable by construction. The writer is the same one docs/105 §10 built; only the moment is
    /// new. <c>CRANBERRY_BOUNTY_CURRENCY=0</c> reverts.
    /// </para>
    /// </summary>
    private void SendLobbyCurrency(SoeConnection connection, GatewaySessionState state)
    {
        if (!_options.Bounty.Currency)
        {
            return;
        }

        int rows = 0;
        foreach (SetAccountCurrencyRecord record in AccountCurrencyRecords(state))
        {
            state.Currency[record.CurrencyId] = record.Amount;
            SendTunnel(connection, writer => record.WriteTo(writer));
            rows++;
        }

        _log.Info($"{connection} bounty: lobby currency — {rows} authoritative balance rows");
    }

    /// <summary>
    /// docs/113 §4 — <c>67 0e</c> (player count, bounty count, the two PLACE→BONUS payout tables)
    /// then <c>67 0d</c> (the still-empty backed state), both with the lobby HUD.
    /// <para>
    /// Order is deliberate: the tables first so the window has rows to draw, the state second so
    /// the "BOUNTY BACKED" line is the last thing written into <c>MatchBounty</c>. Both layouts are
    /// the client's own parsers (<c>FUN_1413eafe0</c>, <c>FUN_1413e9db0</c>); the numbers inside the
    /// tables are Cranberry's design, D253.
    /// </para>
    /// </summary>
    private void SendBountyTables(SoeConnection connection, GatewaySessionState state, int population)
    {
        if (!_options.Bounty.Enabled || !BountyEligibility.IsEligible(state.BountyAdmission))
        {
            return;
        }

        var tables = new MatchBountyTables(
            (uint)Math.Max(0, population),
            BountyCount: (uint)_accountSessions.Values.Where(peer => peer.BountyAdmission.MatchId == state.BountyAdmission.MatchId
                && peer.Bounty.BountyType != 0).Select(peer => string.IsNullOrEmpty(peer.AccountId)
                    ? peer.Guid.ToString() : peer.AccountId).Distinct().Count(),
            _options.Bounty.SkullTable(),
            _options.Bounty.CreditTable());
        SendTunnel(connection, writer => tables.WriteTo(writer));
        MatchBountyState bounty = state.Bounty;
        SendTunnel(connection, writer => bounty.WriteTo(writer));
        _log.Info($"{connection} bounty: 67 0e players={tables.PlayerCount} backed={tables.BountyCount} "
            + $"skulls[{string.Join("/", _options.Bounty.SkullPayouts)}] "
            + $"credits[{string.Join("/", _options.Bounty.CreditPayouts)}] ({tables.Length} B) "
            + $"+ 67 0d amount={bounty.BountyAmount} type={bounty.BountyType}");
    }

    /// <summary>
    /// docs/113 §2 — the <c>ce 14</c> "Match starts in N seconds." banners at 60 / 30 / 10 s
    /// (D251, Z1's <c>BannerSeconds</c> under D53).
    /// <para>
    /// One <see cref="Later"/> per step rather than a tick loop: the lobby has no pump, the steps
    /// are three fixed offsets from a known start, and every closure re-checks
    /// <see cref="MatchStep.Lobby"/> so an abandoned match fires none of them. A step that does not
    /// fit inside the lobby is never armed at all (<c>ApplicableBannerSeconds</c>).
    /// </para>
    /// </summary>
    private static int LobbyDelayMs(long deadlineMs) =>
        (int)Math.Clamp(deadlineMs - Environment.TickCount64, 0, int.MaxValue);

    private void ArmLobbyBanners(SoeConnection connection, GatewaySessionState state, long deadlineMs)
    {
        int lobbyGeneration = state.DevConsole.LobbyGeneration;
        uint lobbyMs = (uint)LobbyDelayMs(deadlineMs);
        var armed = new List<uint>();
        foreach (uint at in _options.Lobby.ApplicableBannerSeconds(lobbyMs))
        {
            uint seconds = at;
            int afterMs = LobbyDelayMs(deadlineMs - seconds * 1000L);
            if (afterMs <= 0)
            {
                continue;
            }

            armed.Add(seconds);
            Later(connection, afterMs, () =>
            {
                if (state.Match != MatchStep.Lobby || state.Watchdog.IsStalled
                    || state.DevConsole.LobbyGeneration != lobbyGeneration)
                {
                    return;
                }

                SendTunnel(connection, writer => GameModeHud.WriteMatchStartBanner(writer, seconds));
                _log.Info($"{connection} match: ce 14 banner — match starts in {seconds} s "
                    + $"(\"{GameModeHud.MatchStartBannerKey}\", \"{GameModeHud.SecondBannerKey}\")");
            });
        }

        if (armed.Count > 0)
        {
            _log.Info($"{connection} match: lobby banners armed at {string.Join("/", armed)} s "
                + "(CRANBERRY_LOBBY_BANNERS=0 reverts)");
        }
    }

    /// <summary>
    /// docs/113 §5 — the Confirm click on the Bounty screen: <c>67 0c SelectBounty(u32)</c>.
    /// <para>
    /// Debit the ante from the balance this session is holding, re-send that currency's
    /// <c>ab 03</c> row, and echo <c>67 0d</c> with the amount and the type the client asked for,
    /// so the window can draw BOUNTY BACKED. An ante the balance cannot cover is refused by
    /// re-stating the unchanged state rather than by silence — the click must always produce an
    /// answer, which is the whole point of AUDIT-bounty G6.
    /// </para>
    /// </summary>
    private void HandleSelectBounty(SoeConnection connection, GatewaySessionState state, uint bountyType)
    {
        AcceptBountySelection(connection, state, bountyType);
    }

    /// <summary>
    /// The parachute (docs/12 §5): the lightweight vehicle record at the air spawn owned by the
    /// rider (the owner tail makes the client manage the chute's movement itself), the full
    /// record, then Vehicle.AutoMount — once released, the client echoes AutoMount to request the
    /// possession burst ("Mountable npc ready" in its ClientMountLog.txt).
    /// </summary>
    private void SendParachute(SoeConnection connection, GatewaySessionState state, Vector4 air)
    {
        StopEmote(connection, state, includeSelf: true);
        ulong chute = state.Guid + _options.ParachuteGuidOffset;
        state.ChuteGuid = chute;
        state.ParachuteFallProtectedUntilMs = 0;
        state.Movement.RegisterManagedEntity(_options.ParachuteTransientId, chute);
        state.MountRequested = false;
        state.TeleportReady = false;
        state.Released = false;
        // docs/88 §4b: the two numbers the descent deadline needs. The ground is state.Drop.
        state.ChuteAirY = air.Y;
        state.MountedAtMs = 0;
        state.PositionlessChuteRecords = 0;
        // docs/115 §3: the descent guard's two inputs start empty for every ride, so a previous
        // match's last known altitude can never satisfy this one's near-ground gate.
        state.ChuteLastY = float.NaN;
        state.ChuteLastPoseMs = 0;
        state.Touchdown.Reset(air.Y);
        var position = new Vector3(air.X, air.Y, air.Z);
        PositionUpdateBlock? update = _options.RetailPositionBlock ? PositionUpdateBlock.Retail(position) : null;
        var lightweight = new AddLightweightVehicle(
            chute,
            _options.ParachuteTransientId,
            _options.ParachuteModelId,
            position,
            new Vector4(0, 0, 0, 1),
            _options.ParachuteVehicleId,
            OwnerGuid: state.Guid,
            PositionUpdate: update,
            ShaderParameterGroupId: _options.ParachuteShaderParameterGroupId);
        SendTunnel(connection, writer => lightweight.WriteTo(writer));
        if (_options.SendFullVehicleRecord)
        {
            SendTunnel(connection, writer => new LightweightToFullVehicle(_options.ParachuteTransientId, chute).WriteTo(writer));
        }

        SendTunnel(connection, writer => new VehicleAutoMount(chute).WriteTo(writer));
        _log.Info($"{connection} match: parachute guid={chute} (vehicle {_options.ParachuteVehicleId}, model {_options.ParachuteModelId}) at {position} — "
            + $"{(_options.ParachuteShaderParameterGroupId == 0 ? "default canopy" : $"shader group {_options.ParachuteShaderParameterGroupId} (D241 experiment)")} — "
            + $"AddLightweightVehicle ({lightweight.Length} B)"
            + $"{(_options.SendFullVehicleRecord ? " + LightweightToFullVehicle" : string.Empty)} + Vehicle.AutoMount; waiting for the client's AutoMount echo");
    }

    /// <summary>
    /// Puts the session back in the menu and tears down everything a match armed. The gateway link
    /// outlives the BACK button — the client asks for a character-select session (0xc3) on this
    /// same connection — so the deferred chains have to be stopped by hand: every match-flow
    /// closure re-checks <c>state.Match</c>, and the gas pump ends on its next pass once the
    /// controller is stopped. Idempotent.
    /// </summary>
    private void AbandonMatch(SoeConnection connection, GatewaySessionState state, string trigger)
    {
        state.PendingClientAdmission = null;
        state.PendingLogout = null;
        state.LogoutPrepared = false;
        state.AutoAcceptReplay = false;
        state.InteractionGeneration++;
        state.Combat.Grenades.Clear();
        state.CraftBusyUntil = 0;
        state.ShredBusyUntil = 0;
        CancelVehicleComponentRemoval(connection, state, notify: false);
        CancelMedicalCast(connection, state, "leaving the world", notify: false);
        ClearHealingHud(connection, state);
        StopEmote(connection, state, includeSelf: true);
        state.LastEmoteStartAtMs = null;
        CancelPartyQueuePeers(state, trigger);
        StopCharacterFire(connection, state);
        state.Combat.Draw.Clear();
        state.WorldGeneration++;
        LeaveAirdrops(state);
        DepartBounty(connection, state);
        LeaveSharedLoot(state);
        state.BountyAdmission = MatchAdmissionContext.Unknown;
        state.MatchTransferRequest = null;
        state.HostedGameId = null;
        ClearHostedSpectatorRoster(connection, state);
        SendHostedAdminWorld(connection, state);
        state.HostedObserverActive = false;
        state.HostedFreeCamera = false;
        state.HostedSpectateTarget = 0;
        state.HostedCameraPosition = null;
        state.HostedObserverSequence++;
        state.MatchPartyId = 0;
        state.MatchPartySize = 0;
        state.Bounty = MatchBountyState.None;
        state.Combat.Reload = null;
        state.Combat.Shooter.Reset();
        MatchStep from = state.Match;
        state.Match = MatchStep.Menu;
        // The match was cancelled, so its milestones were not missed (docs/35 §5c) — reporting them
        // would blame the client for the player's own BACK press, and IsStalled would then suppress
        // the next match the player starts on this same link.
        state.Watchdog.Reset();
        state.AutoMatchArmed = false;
        state.HeldWeaponItemGuid = 0;
        // The match actor is gone, and with it its containers, its loadout bindings and its doors
        // (docs/41 §I1(a), docs/42). Leaving them behind would let the next match inherit stale item
        // guids, loadout bindings and access state from the previous world.
        state.Inventory = null;
        state.FistsBound = false;
        state.EquipmentDress = null;
        state.VehiclePreview = null;
        state.MenuWeaponPreview = null;
        state.MenuWeaponPreviewCategoryId = 0;
        state.MenuApparelPreview = null;
        state.CharacterAccessGranted = false;
        LeaveSharedDoors(state);
        state.Doors?.Clear();
        state.Doors = null;
        state.LobbyDoorsArmed = false;
        // docs/52: same reason as the doors — the streamed loot belonged to the abandoned match.
        state.StreamedLoot.Clear();
        state.LandingLootDrained = false;
        state.NextDoorPumpMs = 0;
        state.NextLootPumpMs = 0;
        state.WorldPumping = false;
        state.Fleet = null;
        state.VehiclesArmed = false;
        state.StreamedVehicles.Clear();
        state.Fuel.Clear();
        state.Ignition.Clear();
        state.VehicleEntry.Clear();
        state.VehicleExitProtectedUntilMs = 0;
        state.ParachuteFallProtectedUntilMs = 0;
        state.NextVehiclePumpMs = 0;
        _vehiclePoses.Unregister(state.Guid);
        state.VehicleObserverRegistered = false;
        // docs/109 lane 3C: the match this session was in is over, so it is neither shown to nor
        // shown anybody until the next drop. The sweep does the rest — every viewer that held this
        // character drops it on its next pass, which is what sends the 0f 01.
        NotePeerInMatch(state, inMatch: false);
        StopPlayerBleeding(connection, state);
        LeaveSharedGas(state);
        state.MovementStats.Reset();
        state.FootwearAudio = null;
        // docs/48: the drop, the seed and the schedule belong to the match that was abandoned. The
        // next countdown draws its own; clearing them means a burst that somehow fires in between
        // falls back to the fixed spawn rather than to the previous match's landing point.
        state.Drop = default;
        state.MatchSeed = 0;
        state.Schedule = null;
        state.ChuteGuid = 0;
        state.MountRequested = false;
        state.MountedAtMs = 0;
        state.ChuteLastY = float.NaN;
        state.ChuteLastPoseMs = 0;
        state.TeleportReady = false;
        state.Released = false;
        state.DeathSent = false;
        // Lane 1D-lite: the endgame state belongs to the match that just ended. AbandonMatch is
        // also the reset the Ended hold calls, so clearing them here is what makes a second match
        // on the same link start from a clean death/victory/alive-count slate.
        state.VictorySent = false;
        state.EndedAtMs = 0;
        state.AliveSent = null;

        if (_parties.Find(state.Guid) is { } menuParty)
            PublishPartyRoster(menuParty);

        if (state.Gas is GasController gas && gas.Running)
        {
            gas.Stop();
            _log.Info($"{connection} match: abandoned from {from} ({trigger}) — gas stopped, timers will no-op");
            return;
        }

        if (from != MatchStep.Menu)
        {
            _log.Info($"{connection} match: abandoned from {from} ({trigger}) — timers will no-op");
        }
    }

    /// <summary>
    /// Opens this session's gas match (docs/23 §3). The controller is started on the same tick as
    /// the <c>ce 16</c> StartMatch send, so its match clock and the client's own "Revealing safe
    /// zone" countdown agree. The seed is derived from the character guid and the wall clock so two
    /// matches differ while one match replays from the seed logged here.
    /// </summary>
    private void StartGas(SoeConnection connection, GatewaySessionState state)
    {
        if (!_options.EnableGas)
        {
            _log.Info($"{connection} gas: disabled (ZoneOptions.EnableGas = false) — no ce 01/ce 02 traffic this match");
            return;
        }

        // docs/34 §5 D2. Belt and braces with the SendLobbyHud suppression above, and kept anyway:
        // the gas pump is the one blind timer whose side effect is DAMAGE. A stalled client never
        // moves, so an unguarded pump would grind a motionless player to a fabricated gas death and
        // log every tick of it as a success.
        if (state.Watchdog.IsStalled)
        {
            _log.Warn($"{connection} gas: not started — {state.Watchdog.Stall}");
            return;
        }

        GasSettings matchGas = state.Schedule?.Settings ?? _options.Gas;
        state.Gas = new GasController(matchGas);
        // Wave 9: rearm both heal beats for this match, so the first pump after the drop re-sends
        // the countdown rather than inheriting a previous match's clock.
        state.GasHudHealAtMs = 0;
        state.GasSafeZoneHealAtMs = 0;
        state.GasHudLabelSent = 0;
        // D279: an empty, unarmed meter. The arming 8d goes out on the first pump, not here, so the
        // match-open burst stays exactly as it was for a host running CRANBERRY_GAS_TOXICITY=0.
        state.Toxicity.Reset();
        state.GasToxicityAtMs = 0;

        // docs/48 §Integration step 5: the schedule was built at the countdown from this match's
        // salted gas sub-seed, because the drop had to be constrained to phase 1's circle before the
        // gas could be opened. Adopt it rather than drawing a second, unrelated one from the wall
        // clock — otherwise the circle the drop was chosen against is not the circle the player
        // gets. Everything this method SENDS is unchanged, so the opening ce 01 keeps its position.
        // GasSchedule.Create is a pure function of (settings, seed), so opening the controller with
        // the SAME salted sub-seed reproduces the schedule byte for byte — the drop was chosen
        // against phase 1 of exactly this plan. No new GasController API, and the wave-3 behaviour
        // when a match somehow reaches here without a countdown is preserved by the fallback draw.
        ulong seed = state.MatchSeed != 0
            ? MatchSeeds.For(state.MatchSeed, MatchSeeds.GasSalt)
            : state.Guid ^ (ulong)DateTime.UtcNow.Ticks;
        long startedAtMs = Environment.TickCount64;
        // docs/109 §5, the D67 minimum: a session joining a match that is already running adopts
        // the running plan's (seed, matchClockMs) instead of drawing its own. GasController is a
        // pure function of exactly those two, so the second player then sees the SAME phase,
        // centre, radius and timers as the first — the one piece of shared match state this lane
        // fixes. CRANBERRY_SHARED_GAS_SEED=0 restores two private circles.
        bool joinedSharedGas = JoinSharedGas(state, ref seed, ref startedAtMs);
        GasSchedule schedule = state.Gas.Start(startedAtMs, seed);
        if (state.SharedGasJoined && _sharedGasMatches.TryGetValue(state.BountyAdmission.MatchId, out var sharedPlan)
            && sharedPlan.PausedAtMs is long pausedAt)
            state.Gas.Pause(pausedAt);
        state.Schedule = schedule;
        StartAirdrops(state, seed, startedAtMs);
        if (joinedSharedGas)
        {
            _log.Info($"{connection} gas: JOINED the match already in progress — seed {seed:x16} and "
                + $"match clock zero {startedAtMs} ms adopted from the first session "
                + $"for match {state.BountyAdmission.MatchId}; members of other worlds keep their own circle.");
        }

        _log.Info($"{connection} gas: match opened, seed {seed:x16}, {schedule.Phases.Count} phases, "
            + $"first reveal at {schedule.Phase(1).RevealAtMs} ms, last circle closed at {schedule.FinishedAtMs} ms, "
            + $"initial circle {FormatPosition(schedule.InitialCircle.Centre)} r={schedule.InitialCircle.Radius:F1}");
        foreach (GasPhase phase in schedule.Phases)
        {
            _log.Info($"{connection} gas: phase {phase.Index} reveal={phase.RevealAtMs} shrink={phase.ShrinkStartAtMs} "
                + $"closed={phase.ClosedAtMs} centre={FormatPosition(phase.Target.Centre)} "
                + $"r={phase.Target.Radius:F1} dmg={phase.DamagePerTick}");
        }

        _log.Info($"{connection} gas: leading edge <= {matchGas.LeadingEdgeSpeedCeiling():F2} m/s "
            + $"(pacing {matchGas.Pacing}, drift cap {matchGas.CentreDriftFraction:F2}, "
            + $"cone {matchGas.DriftConeDegrees:F0} deg), pre-move ring {matchGas.PreMoveRing}, "
            + $"lethal from {schedule.RingLiveFromMs} ms");

        // D277/D278, docs/118 §3: which of the client's own nine GasWeightArea volumes this match is
        // aimed at, and how close containment let it get. This is the line to read when the owner
        // asks "where did that circle come from".
        if (_options.Gas.CentrePlan == GasCentrePlan.PoiDestination && schedule.DestinationAreaIndex >= 0)
        {
            GasCircle last = schedule.FinalCircle;
            float missed = MathF.Sqrt(
                ((last.Centre.X - schedule.Destination.X) * (last.Centre.X - schedule.Destination.X))
                + ((last.Centre.Z - schedule.Destination.Y) * (last.Centre.Z - schedule.Destination.Y)));
            _log.Info($"{connection} gas: destination {GasWeightAreas.NameOf(schedule.DestinationAreaIndex)} "
                + $"at ({schedule.Destination.X:F1}, {schedule.Destination.Y:F1}) — final circle "
                + $"{FormatPosition(last.Centre)} r={last.Radius:F1}, "
                + (missed <= 1f ? "landed in it" : $"{missed:F0} m short (containment budget spent)")
                + $"; play area led to {FormatPosition(schedule.InitialCircle.Centre)} "
                + $"(lead cap {_options.Gas.PlayAreaLeadMetres:F0} m)");
        }

        // docs/77 section 6, the owner's own click-test ruling: nothing gas-shaped goes on the map
        // — and nothing is lethal — until the ring first moves. GasSchedule.IsLethalAt carries the
        // matching damage gate, so the drawn wall and the burning wall cannot come apart. Only ce 01
        // is at stake here: ce 02 is the "next safe zone" widget and revealing phase 1 early is
        // exactly what FirstRevealDelayMs prevents.
        GasCircle opening = schedule.InitialCircle;
        switch (matchGas.PreMoveRing)
        {
            case GasPreMoveRing.Boundary:
                SendTunnel(connection, w => GasPackets.WriteRing(w, _options.Gas, opening));
                _log.Info($"{connection} gas: opening ce 01 — play-area boundary {FormatPosition(opening.Centre)} "
                    + $"r={opening.Radius:F1} (visible from the drop; phase 1 is not revealed until {schedule.Phase(1).RevealAtMs} ms)");
                break;
            case GasPreMoveRing.ZeroRadius:
                SendTunnel(connection, w => GasPackets.WriteRing(
                    w, _options.Gas, opening with { Radius = GasPackets.RingTerminalRadius }));
                _log.Info($"{connection} gas: opening ce 01 at radius 0 (the client's own \"no gas\" value) "
                    + $"— the wall appears when it starts moving at {schedule.Phase(1).ShrinkStartAtMs} ms");
                break;
            default:
                _log.Info($"{connection} gas: no ce 01 before the gas moves — the wall appears at "
                    + $"{schedule.Phase(1).ShrinkStartAtMs} ms and nothing is lethal until then");
                break;
        }

        if (!state.GasPumping)
        {
            state.GasPumping = true;
            // One cached delegate for the life of the match instead of a fresh closure every
            // 250 ms per session (docs/77 section 9.2).
            state.GasPump ??= () => PumpGas(connection, state);
            Later(connection, _options.Gas.HostTickIntervalMs, state.GasPump);
        }
    }

    /// <summary>
    /// The gas pump (docs/23 §4): one <see cref="GasController.Tick"/> per
    /// <c>GasSettings.HostTickIntervalMs</c> on the listener thread, then whatever the controller
    /// asked for goes out on this connection. The controller is clock-driven and rate-limits itself,
    /// so pump drift changes only the latency of an event, never the match.
    /// </summary>
    private void PumpGas(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Gas is not GasController gas || !gas.Running)
        {
            state.GasPumping = false;
            return;
        }

        // Feed the latest authoritative pose. While the player is on the chute the real pose lives
        // on the managed (channel-3) stream and the rider is in the air, so nothing is damaged yet.
        Vector3 position = EnvironmentalDamagePosition(state)
            ?? new Vector3(StagingPosition(state).X, StagingPosition(state).Y, StagingPosition(state).Z);
        // A rider under the canopy is not damaged: the on-foot pose is stale while the real one is
        // on channel 3. The gate is MountRequested, not ChuteGuid — ChuteGuid is cleared only by
        // SendParachuteDismountBurst, which every caller gates on MountRequested, so a dropped
        // Vehicle.AutoMount echo would leave ChuteGuid set for the rest of the session and make
        // the player permanently immune to gas (and the match unendable).
        bool alive = state.Hitpoints > 0 && !state.MountRequested && !state.DeathSent;
        state.GasSamples[0] = new PlayerSample(0, position, alive);

        // docs/88 §4b, one call: force the landing handover on a ride that has outlived twice its
        // own expected length. It rides this pump because the pump is the only thing that ticks for
        // the whole of a match and it is already gated on exactly the state that goes wrong — five
        // sessions in the archive ended with the client silent under canopy and `alive` false above
        // for the full 120 s idle timeout. BUILT, not LIVE-VERIFIED: it is designed never to fire.
        //
        // It deliberately does NOT return early: this pump re-arms itself at the bottom of the
        // method, so an early exit here would silently stop the gas for the rest of the match while
        // leaving GasPumping latched true. The sample above is one tick stale on the tick that
        // forces the handover, which costs at most one 250 ms tick of gas damage on a session that
        // was already broken.
        EnforceDescentDeadline(connection, state);

        long now = Environment.TickCount64;
        long matchClockMs = gas.MatchClockAt(now);
        ApplyGasTick(connection, state, gas.Tick(now, state.GasSamples), matchClockMs);
        HealGasHud(connection, state, gas, matchClockMs, now);
        PumpToxicity(connection, state, gas, matchClockMs, now);
        // D274: the airdrop rides this beat for the same reason the toxicity meter does — it is the
        // only thing that ticks for the whole of a match, and it already carries the match clock and
        // the schedule the crate's landing point is measured against.
        PumpAirdrops(connection, state, gas, matchClockMs, now);

        Later(connection, _options.Gas.HostTickIntervalMs, state.GasPump ??= () => PumpGas(connection, state));
    }

    /// <summary>
    /// Wave 9's fix, and the only edit that addresses the reported defect (docs/87 §3.2, §7 edit 3).
    /// The <c>ce 0f</c> countdown is re-sent once a second and the green <c>ce 02</c> circle every
    /// fifteen, so neither can be lost to a HUD window rebuild — which is what the owner's own
    /// client reported doing 2.6 s into his 20:36 match, 117.6 s before the next countdown was due.
    /// The <c>(labelId, ms)</c> mapping is wave 8's, unchanged; only the cadence moved.
    /// Both beats are switchable to 0 for an A/B (<c>CRANBERRY_GAS_HUD_HEAL_MS</c>,
    /// <c>CRANBERRY_GAS_SAFEZONE_HEAL_MS</c>) and cost 19 B/s and 1.5 B/s.
    /// </summary>
    private void HealGasHud(
        SoeConnection connection,
        GatewaySessionState state,
        GasController gas,
        long matchClockMs,
        long nowMs)
    {
        if (GasHud.DueAt(nowMs, ref state.GasHudHealAtMs, _options.Gas.HudHealIntervalMs))
        {
            (uint label, uint milliseconds) = GasHud.Countdown(
                gas.Settings, gas.Schedule, matchClockMs, state.MountRequested);
            // The client counts down locally. Suppress that countdown during an owner pause.
            if (gas.Paused) (label, milliseconds) = (GameModeHud.GasAdvancesInLabelId, 0u);
            SendTunnel(connection, w => GameModeHud.WriteCountdown(w, milliseconds, labelId: label));

            // One line per label CHANGE, never per beat: at 1 Hz per session the alternative fills
            // the console with 1,490 lines a match.
            if (state.GasHudLabelSent != label)
            {
                state.GasHudLabelSent = label;
                _log.Info($"{connection} gas: hud heal — label {label}, {milliseconds} ms remaining "
                    + $"(re-sent every {_options.Gas.HudHealIntervalMs} ms)");
            }
        }

        if (gas.Schedule is GasSchedule plan
            && plan.PhaseAt(matchClockMs) is GasPhase revealed
            && GasHud.DueAt(nowMs, ref state.GasSafeZoneHealAtMs, _options.Gas.SafeZoneHealIntervalMs))
        {
            GasCircle green = revealed.Target;
            SendTunnel(connection, w => GasPackets.WriteSafeZone(w, green));
        }
    }

    /// <summary>
    /// D279, docs/118 §4 — the toxicity meter. One <c>8d ResourceEvent</c> type 3 for resource
    /// <b>611</b> when the meter is first armed, and one more on every
    /// <c>GasSettings.TickPeriodMs</c> on which its value actually changed. A player who never
    /// enters the gas costs exactly one packet a match; a player standing in it costs 101 B/s while
    /// the bar is filling and nothing once it is full.
    /// <para>
    /// <b>Why the value and not just the flags.</b> <c>Resources.txt:145</c> ships the row with
    /// <c>FLAG_INIT_WITH_DISABLED_REGEN</c> and <c>FLAG_INIT_WITH_DISABLED_BURN</c> both set, so a
    /// server has to arm it either way — but the burn it would arm is <c>BURN_PER_MSEC = 0</c>, so
    /// arming alone can fill the meter and can never empty it, and the owner's ruling (d) asks for
    /// both. Sending the value is also what makes the meter agree with the server's own idea of who
    /// is in the gas, which is the thing the bar is for.
    /// </para>
    /// <para>
    /// The arming send is what <i>creates</i> the bar: 611 is not in
    /// <c>CharacterResource.Starter</c>, and a complete <c>8d</c> row creates the client's missing
    /// <c>Resources.PlayerResourceDataSource</c> entry (docs/02, <c>FUN_140ce52c0</c>).
    /// </para>
    /// </summary>
    private void PumpToxicity(
        SoeConnection connection,
        GatewaySessionState state,
        GasController gas,
        long matchClockMs,
        long nowMs)
    {
        if (!_options.Gas.SendToxicity || gas.Schedule is not GasSchedule plan)
        {
            return;
        }

        if (!state.Toxicity.Armed)
        {
            uint initial = gas.ToxicityForPlayer(state.GasSamples[0].PlayerIndex);
            state.Toxicity.Synchronize(initial);
            SendTunnel(connection, w => new CharacterResourceUpdate(
                state.Guid,
                _options.Gas.ToxicityResourceId,
                _options.Gas.ToxicityResourceType,
                Value: initial,
                PreviousValue: 0).WriteTo(w));
            state.Toxicity.MarkArmed();
            state.GasToxicityAtMs = nowMs;
            _log.Info($"{connection} gas: toxicity armed — resource {_options.Gas.ToxicityResourceId} "
                + $"(type {_options.Gas.ToxicityResourceType}) at {initial}/{_options.Gas.ToxicityMaxValue}; "
                + $"+{_options.Gas.ToxicityFillPerSecond}/s in the gas "
                + $"({_options.Gas.ToxicityMaxValue / Math.Max(1u, _options.Gas.ToxicityFillPerSecond)} s to fill), "
                + $"-{_options.Gas.ToxicityDrainPerSecond}/s outside it");
            return;
        }

        // The controller owns toxicity even when this HUD is disabled. Rendering samples the
        // same authoritative value that selected this tick's base/full-toxicity damage.
        if (!state.Toxicity.Synchronize(gas.ToxicityForPlayer(state.GasSamples[0].PlayerIndex)))
        {
            return;
        }

        uint value = state.Toxicity.Value;
        uint previous = state.Toxicity.Sent;
        SendTunnel(connection, w => new CharacterResourceUpdate(
            state.Guid,
            _options.Gas.ToxicityResourceId,
            _options.Gas.ToxicityResourceType,
            Value: value,
            PreviousValue: previous).WriteTo(w));
        state.Toxicity.MarkSent();

        // One line per endpoint, not one per tick: a full fill is 180 of these.
        if (value == 0 || value == _options.Gas.ToxicityMaxValue)
        {
            _log.Info($"{connection} gas: toxicity {(value == 0 ? "clear" : "full")} — "
                + $"{value}/{_options.Gas.ToxicityMaxValue}");
        }
    }

    /// <summary>
    /// Maps one controller tick onto sends (docs/23 §5). <see cref="GasTickResult"/> is a
    /// <c>ref struct</c>, so it is consumed here and never captured.
    /// </summary>
    private void ApplyGasTick(
        SoeConnection connection,
        GatewaySessionState state,
        in GasTickResult result,
        long matchClockMs)
    {
        if (result.Has(GasTickEvents.RevealSafeZone))
        {
            GasCircle next = result.Revealed;
            GasCircle boundary = result.Active;

            // ce 01 — the drawn gas wall, and the ONLY packet that reaches the volume renderer and
            // the minimap ring (docs/18 §1b: FUN_140bbed00 → FUN_1423ce5b0, plus FUN_141209960).
            // Its trailing u32 is a smoothing time constant, not a deadline, so it has to carry the
            // circle currently in force — at a reveal that is the phase's origin, not its target —
            // and be re-sent as that circle closes (the SafeZoneUpdate branch below). Sending the
            // target here instead would draw the finished wall the moment the phase is revealed,
            // minutes before anything outside it is lethal.
            //
            // It is withheld entirely while GasPreMoveRing.None says the gas is not on the
            // map yet: at the 2:00 reveal only the green ce 02 circle appears, and the wall itself
            // arrives with the ShrinkStarted branch below (docs/77 section 6).
            if (state.Gas?.Schedule is not GasSchedule plan || plan.IsRingVisibleAt(matchClockMs))
            {
                SendTunnel(connection, w => GasPackets.WriteRing(w, _options.Gas, boundary));
            }

            // ce 02 — the NEXT safe zone: two Flash data providers only, no renderer path at all
            // (docs/18 §2), and no timing field. It is the phase's destination and stays pinned
            // there for the whole phase.
            SendTunnel(connection, w => GasPackets.WriteSafeZone(w, next));

            // The widget has three labels, not one (docs/66 section 6, docs/77 section 5): the
            // August client's own CodeStringMappings.txt carries 14153 "Revealing safe zone in",
            // 14151 "Gas advances in" and 14152 "Gas is spreading!" as three consecutive ids, and
            // the owner's own server drives them off the phase state. A circle has just been
            // revealed and its ring is still holding, so this is 14151 counting down to the moment
            // it starts moving — not, as this branch used to send, 14153 promising a reveal seven
            // minutes away while a wall is closing on the player.
            if (state.Gas?.Schedule is GasSchedule schedule)
            {
                uint advancesInMs = (uint)Math.Max(0, schedule.Phase(result.PhaseIndex).ShrinkStartAtMs - matchClockMs);
                SendTunnel(connection, w => GameModeHud.WriteCountdown(
                    w, advancesInMs, labelId: GameModeHud.GasAdvancesInLabelId));

                // Wave 9, docs/87 §7 edit 4: the owner's server announces every reveal in words as
                // well as in the widget (Z1 ZoneMatch.cs:1121-1131), with the seconds to the
                // advance — the same number the label above carries.
                if (_options.Gas.SendBanners)
                {
                    string marked = GasAlerts.SafeZoneMarked(GasAlerts.SecondsOf(advancesInMs));
                    SendTunnel(connection, w => GasAlerts.Write(w, marked));
                    _log.Info($"{connection} gas: banner (11 31) — \"{marked}\"");
                }
            }

            _log.Info($"{connection} gas: phase {result.PhaseIndex} revealed — next zone centre "
                + $"{FormatPosition(next.Centre)} r={next.Radius:F1} (ce 02), wall still at "
                + $"{FormatPosition(boundary.Centre)} r={boundary.Radius:F1} (ce 01), "
                + $"closes in {result.RevealClosesInMs} ms");
        }

        if (result.Has(GasTickEvents.ShrinkStarted))
        {
            // The ring has started to travel. Under GasPreMoveRing.None this is the FIRST ce 01 of
            // the match — the wall appearing exactly as it begins to close, which is the owner's
            // ruling — and for every later phase it is a free extra update at the moment the
            // circle starts moving.
            GasCircle moving = result.Active;
            // D280: another ce 01 is coming in SafeZoneUpdateIntervalMs, so the blend constant is
            // that interval and the client's ease finishes exactly as the next one lands.
            SendTunnel(connection, w => GasPackets.WriteRing(w, _options.Gas, moving, advancing: true));

            // 14152 "Gas is spreading!", carrying the REMAINING ADVANCE (docs/77 section 5): the
            // owner's own server sends the time until the ring has finished closing, which is what
            // the player needs, and this corrects docs/66 section 6c's proposed ms = 0.
            if (state.Gas?.Schedule is GasSchedule advancing && result.PhaseIndex > 0)
            {
                uint closesInMs = (uint)Math.Max(0, advancing.Phase(result.PhaseIndex).ClosedAtMs - matchClockMs);
                SendTunnel(connection, w => GameModeHud.WriteCountdown(
                    w, closesInMs, labelId: GameModeHud.GasIsSpreadingLabelId));
                _log.Info($"{connection} gas: phase {result.PhaseIndex} ring is spreading — "
                    + $"ce 01 {FormatPosition(moving.Centre)} r={moving.Radius:F1}, closes in {closesInMs} ms (label 14152)");

                // docs/87 §7 edit 4, the second edge: BR.ReleasingGas 11120, the August client's own
                // sentence, on every advance (Z1 ZoneMatch.cs:1122-1126).
                if (_options.Gas.SendBanners)
                {
                    SendTunnel(connection, w => GasAlerts.Write(w, GasAlerts.ReleasingGas));
                    _log.Info($"{connection} gas: banner (11 31) — \"{GasAlerts.ReleasingGas}\"");
                }
            }
        }
        else if (result.Has(GasTickEvents.SafeZoneUpdate))
        {
            // ce 01 re-sent at GasSettings.SafeZoneUpdateIntervalMs (500 ms) with the
            // server-interpolated circle — docs/18 §1b's second supported pattern, and the only way
            // the drawn wall can track the circle the server actually damages against. ce 02 does
            // not move: the next zone is fixed for the phase.
            GasCircle active = result.Active;
            SendTunnel(connection, w => GasPackets.WriteRing(w, _options.Gas, active, advancing: true));
        }

        foreach (GasDamageTick tick in result.Damage)
        {
            ApplyGasDamage(connection, state, tick.Amount);
        }

        if (result.Has(GasTickEvents.Finished))
        {
            // The parked terminal state: 14152 with a zero timer, which FUN_140bb67b0 renders as
            // the label with an empty countdown (docs/77 section 5).
            SendTunnel(connection, w => GameModeHud.WriteCountdown(
                w, 0, labelId: GameModeHud.GasIsSpreadingLabelId));
            _log.Info($"{connection} gas: last circle closed at r={result.Active.Radius:F1}; the final circle stays lethal");
        }
    }

    /// <summary>
    /// One gas tick's worth of damage on the server's own health bar, then the client-facing update
    /// (docs/23 §6). <c>ClientUpdate.Hitpoints (11 01)</c> is the proven carrier: the client's own
    /// handler computes <c>(current*100)/max</c> from these two fields (docs/16 §4d, docs/18 §4c).
    /// </summary>
    private void ApplyGasDamage(SoeConnection connection, GatewaySessionState state, uint amount)
    {
        if (!CanTakeGameplayDamage(state))
        {
            return;
        }

        // Lane 1D-lite: the health write and its 11 01 carrier are shared with every other cause
        // (ZoneService.Endgame.cs, DecrementHealth). The byte order below is unchanged — the
        // diagnostic DamageInfo still follows the bar and precedes the death burst.
        uint current = DecrementHealth(connection, state, amount);

        // Unverified (docs/15 §4, docs/18 §4c, research-gaps G-08): DamageInfo's field semantics are
        // BLOCKED, so this stays off until one live tick says which field is the amount.
        if (_options.Gas.SendDamageInfo)
        {
            SendTunnel(connection, w => GasPackets.DamageInfo.GasTick(amount).WriteTo(w, _options.Gas));
        }

        _log.Info($"{connection} gas: −{amount} hp → {current}/{_options.Gas.MaxHitpoints}");

        if (current == 0)
        {
            SendGasDeath(connection, state);
        }
    }

    /// <summary>
    /// The gas death hand-off. <b>Lane 1D-lite moved the packets into
    /// <see cref="KillPlayer"/></b> (ZoneService.Endgame.cs), the one death path every cause now
    /// uses: <c>0f 4f</c> ragdoll, <c>0f 48</c> kill feed, exactly one <c>ce 04</c> with
    /// <c>DeathCauseCodes.For(DamageCause.ToxicGas)</c> = <c>0x42</c>
    /// (<c>UI.Results.Rank.Gas</c>), <c>ce 09</c> on change, then the <c>Ended</c> hold and the
    /// reset. This method stays as the gas lane's own name for the event, and as the only place
    /// the gas cause is chosen.
    /// <para>
    /// <c>CRANBERRY_MATCH_ENDGAME=0</c> makes <see cref="KillPlayer"/> send exactly what this
    /// method sent before the lane — the single <c>ce 04</c> and <c>ce 09 0</c>.
    /// </para>
    /// </summary>
    private void SendGasDeath(SoeConnection connection, GatewaySessionState state) =>
        KillPlayer(connection, state, DamageCause.ToxicGas);

    /// <summary>
    /// docs/88 §4b, <b>rebuilt by D239 / docs/115 §3</b>: the descent deadline. Returns true when it
    /// forced the landing handover, purely so a caller or a test can see that it fired; the gas pump
    /// deliberately ignores the result and finishes its own tick, because it is the thing that
    /// re-arms itself.
    /// <para>
    /// <b>The rule this now obeys: a live chute in the air is never dismounted.</b> The first
    /// version reasoned about a real elapsed time with the DIVED mean and had no altitude gate at
    /// all, so at the 1,454 m release wave 9 shipped it expired at 87.0 s against a hands-off ride
    /// that honestly lasts ~148 s — and it fired three times on 2026-09-03
    /// (<c>host-20260903-082114</c>, <c>-082424</c>, <c>-083422</c>), each time dismounting a
    /// working client about 600 m in the air. <see cref="DescentDeadline.Evaluate"/> now computes the
    /// clock at the client's hands-off <c>MIN_TERM_VELOCITY</c> and, past it, still requires either
    /// a chute near the ground or a pose stream that has gone silent.
    /// </para>
    /// <para>
    /// <c>CRANBERRY_DESCENT_LANDING_GUARD=0</c> restores the pre-D239 behaviour exactly.
    /// </para>
    /// </summary>
    private bool EnforceDescentDeadline(SoeConnection connection, GatewaySessionState state)
    {
        if (state.ChuteGuid == 0 || !state.MountRequested || state.MountedAtMs == 0)
        {
            return false;
        }

        long now = Environment.TickCount64;
        long elapsedMs = now - state.MountedAtMs;
        float airY = state.ChuteAirY;
        float groundY = state.Drop != default ? state.Drop.Y : _options.MatchDropSpawn.Y;
        long silentMs = state.ChuteLastPoseMs == 0 ? -1L : now - state.ChuteLastPoseMs;
        float? chuteY = float.IsFinite(state.ChuteLastY) ? state.ChuteLastY : null;

        DescentHandover verdict = DescentDeadline.Evaluate(
            _options.DescentLandingGuard, elapsedMs, silentMs, airY, groundY, chuteY);
        if (verdict == DescentHandover.None)
        {
            return false;
        }

        string where = chuteY is float reported
            ? $"chute last seen at y {reported:F1}, {reported - groundY:F1} m above the drop"
            : "the chute's pose stream has never carried a position";
        string why = verdict switch
        {
            DescentHandover.Landed =>
                $"it is within {DescentDeadline.GroundProximityMetres:F0} m of the ground, so the "
                + "client has landed and its Vehicle.Dismiss was lost",
            DescentHandover.ClientSilent =>
                $"its pose stream has been silent for {silentMs / 1000d:F1} s, so nobody is flying it",
            DescentHandover.StuckClient =>
                $"it has outlived the absolute {DescentDeadline.StuckClientSeconds(airY, groundY):F0} s "
                + "backstop without ever descending",
            _ =>
                "CRANBERRY_DESCENT_LANDING_GUARD=0 — the pre-D239 deadline, which CAN dismount a live "
                + "rider in mid-air",
        };

        _log.Warn($"{connection} match: descent deadline ({verdict}) — chute {state.ChuteGuid} has been "
            + $"mounted for {elapsedMs / 1000d:F1} s against a hands-off ride of "
            + $"{DescentSettings.HandsOffSecondsFor(airY, groundY):F1} s and a deadline of "
            + $"{DescentDeadline.DeadlineSeconds(airY, groundY):F1} s "
            + $"(air {airY:F1} → ground {groundY:F1}; {where}); {why}; forcing the landing handover so "
            + "the rider stops being immune to gas and the match can end");
        SendParachuteDismountBurst(connection, state, $"descent deadline ({verdict})");
        return true;
    }

    /// <summary>docs/12 §5 step 4: Owner, MountResponse (queue position 1 = mount, driver), Occupy — idempotent.</summary>
    private void SendMountBurst(SoeConnection connection, GatewaySessionState state, uint seat)
    {
        SendTunnel(connection, writer => new VehicleOwner(state.ChuteGuid, state.Guid, _options.ParachuteVehicleId).WriteTo(writer));
        SendTunnel(connection, writer => new MountResponse(Rider: state.Guid, Mount: state.ChuteGuid, Seat: seat).WriteTo(writer));
        SendTunnel(connection, writer => new VehicleOccupy(state.ChuteGuid, state.Guid, _options.ParachuteVehicleId, Seat: seat).WriteTo(writer));
        state.MountRequested = true;
        state.MountedAtMs = Environment.TickCount64;
        _log.Info($"{connection} match: mount burst — Vehicle.Owner, Mount.MountResponse (seat {seat}), Vehicle.Occupy for chute {state.ChuteGuid}");
    }

    /// <summary>
    /// Completes the client-owned parachute transition. <c>70 04</c> collapses the chute, the
    /// null-guid <c>88 02</c> clears possession, <c>88 01</c> clears ownership, and <c>0f 01</c>
    /// removes the landed canopy. Reasserting equipment last returns rendering to the infantry
    /// actor after possession changes.
    /// </summary>
    private void SendParachuteDismountBurst(
        SoeConnection connection,
        GatewaySessionState state,
        string trigger)
    {
        ulong chute = state.ChuteGuid;
        if (chute == 0) return;

        // Start at the actual canopy-to-infantry transition, before sending the clear burst.
        // Duplicate exit signals see ChuteGuid == 0 and cannot extend this grace period.
        state.ParachuteFallProtectedUntilMs = Environment.TickCount64 + ParachuteLandingFallGraceMs;
        state.PlayerCollision.Reset();

        // Promote the managed landing pose before any queued channel-2 packet can be handled,
        // then retain it until the client resumes a finite, non-zero player position.
        state.Movement.BeginPostDismountPoseHandoff(_options.ParachuteTransientId);

        EndPeerParachute(state);

        SendTunnel(connection, writer => new DismountResponse(state.Guid, chute).WriteTo(writer));
        SendTunnel(connection, writer => new VehicleOccupyCleared(state.Guid).WriteTo(writer));
        SendTunnel(connection, writer => new VehicleOwner(chute, OwnerGuid: 0, VehicleId: 0).WriteTo(writer));
        SendTunnel(connection, writer => new ManagedObjectResponseControl(Control: false, ObjectGuid: chute).WriteTo(writer));
        // Release the managed actor before deleting its world entity, as car dismount does.
        SendTunnel(connection, writer => CharacterManagedObject.Release(chute).WriteTo(writer));
        SendTunnel(connection, writer => new RemovePlayer(chute).WriteTo(writer));
        // The rifle goes into the hand here, not at StartMatch (docs/36 §W3c only asks for "at match
        // start"; the teleport hold is the worst possible place for an UNVERIFIED equipment packet).
        // Granted BEFORE the dress on purpose: the ItemAdd must precede the equipment-slot row that
        // names it, and the dress below then carries the row, so the whole grant costs one extra
        // packet rather than a second 94 01.
        // docs/98 §5.5: the grant sends the ItemAdd and the containers here, but the BINDING is
        // handed back and drawn after the dress below — a slot-7 row on the whole-character 94 01
        // is guard G2, and a 94 01 sent after the 94 02 would undo it.
        InventoryItemInstance? starterDraw =
            GiveHeldWeapon(connection, state, "parachute landing", dress: false);
        // Same dress-before-skins order as the two zoning sites (docs/32, third regression).
        // Dismount replaces the locally possessed actor.  This reassert is therefore mandatory
        // even when it has identical appearance bytes to the lobby snapshot; otherwise the
        // suppressor leaves the client in its vehicle/input state after landing.
        state.Dress.Forget();
        SendCharacterAppearance(connection, state, "parachute landing");
        SendWornSkinItems(connection, state, "parachute landing", reassertDress: true);

        if (starterDraw is not null)
        {
            // Z1's eight-packet draw, ending in the 94 02 that binds body slot 7 — the last
            // equipment packet of the landing burst, which is the whole point of the ordering.
            DrawStarterWeapon(connection, state, starterDraw, "parachute landing");
        }

        state.MountRequested = false;
        state.ChuteGuid = 0;
        state.MountedAtMs = 0;

        _log.Info($"{connection} zone {trigger} ({state.Match}) → "
            + $"landing burst for chute {chute}: DismountResponse, cleared Occupy/Owner/control, RemovePlayer, equipment reassert");

        // Normal world actors already stream around the parachute during descent. Landing adds
        // any configured development drops and practice targets, or starts the world if no
        // descent startup occurred. The shared startup barrier keeps overlapping bursts safe.
        ArmGroundLoot(connection, state, "parachute landing");
    }

    /// <summary>
    /// Sends one planned world burst in slices of <see cref="ZoneOptions.BurstSliceSize"/> objects,
    /// <see cref="ZoneOptions.BurstSliceDelayMs"/> apart, then republishes <c>ProximateItems</c> once
    /// the last slice has gone out.
    /// <para>
    /// The single <c>ProximateItems</c> at the end is load-bearing: the client's reader clears and
    /// rebuilds its whole collection from the packet (docs/13 §3c), so publishing it mid-burst would
    /// name a world that is still half-spawned.
    /// </para>
    /// </summary>
    private void DrainBurst(
        SoeConnection connection,
        GatewaySessionState state,
        List<Action> burst,
        int from,
        string trigger,
        bool republishProximateItems = true,
        int? worldGeneration = null)
    {
        worldGeneration ??= state.WorldGeneration;
        if (worldGeneration != state.WorldGeneration || connection.State != ConnectionState.Open) return;
        // Clamped so that "no pacing" (BurstSliceSize = int.MaxValue, the host's rollback switch)
        // cannot overflow the addition below.
        int slice = Math.Clamp(_options.BurstSliceSize, 1, Math.Max(1, burst.Count));
        int end = Math.Min(burst.Count, from + slice);
        for (int index = from; index < end; index++)
        {
            burst[index]();
        }

        if (end < burst.Count)
        {
            if (Later(
                connection,
                Math.Max(0, _options.BurstSliceDelayMs),
                () => DrainBurst(connection, state, burst, end, trigger, republishProximateItems, worldGeneration)))
            {
                return;
            }

            // No listener-thread dispatcher: Later has already warned. Half a spawned world is worse
            // than an unpaced one, so finish here rather than drop the remainder.
            for (int index = end; index < burst.Count; index++)
            {
                burst[index]();
            }
        }

        if (from > 0 || end < burst.Count)
        {
            _log.Info($"{connection} loot: burst ({trigger}) complete — {burst.Count} world object(s) "
                + $"in slices of {slice}, {_options.BurstSliceDelayMs} ms apart");
        }

        // A door-only re-stream deliberately does NOT republish: ProximateItems is 128 rows / ~9.4 KB
        // on a 512-byte MTU, its reader clears and rebuilds the whole collection, and no ground item
        // changed. Only a burst that touched the loot world republishes.
        // NOT "&& state.Loot.Count > 0" (wave-5 verify fix). The gate is "did this burst touch the
        // ground world", never "is anything left standing in it": a tick that evicts the last
        // objects and spawns nothing — a player leaving a POI for open terrain, measured 2–5 times
        // per map crossing — sent the 0f 01 destroys and then skipped the republish, and the
        // client's ProximateItems collection is rebuilt ONLY from that packet (docs/13 §3c), so the
        // pickup panel went on listing rows whose world objects were already destroyed until some
        // later tick happened to spawn something. An empty republish is 7 bytes and is exactly what
        // clears the panel.
        if (republishProximateItems && _options.SendProximateItems)
        {
            SendProximateItems(connection, state);
        }
    }

    /// <summary>
    /// Keeps the world around the player rather than around the point they landed on. <b>One tick,
    /// two arms</b>: the doors (docs/47 §I3) and the map's own ground loot (docs/52).
    /// <para>
    /// <b>Why the loot arm exists.</b> Wave 4 gave the doors this pump and gave ground loot nothing:
    /// <c>RealGroundLootArmed</c> was a one-way latch, so the landing burst was the only loot the
    /// whole match ever saw and the other 39,708 items on the Z2 floor were never mentioned again.
    /// The owner's play-test is the proof — one <c>real Z2 ground loot</c> line against twenty
    /// <c>door re-stream</c> bursts over the same 400 m of walking — and <i>"no matter what building
    /// I went into there was no loot"</i> is what that reads like from inside the game.
    /// </para>
    /// <para>
    /// Both arms ask their own <c>ShouldRestream</c> once per tick, and both are true only after the
    /// player has moved half a radius — so a stationary player costs two distance tests per tick and
    /// no packets. Both plan into <b>one</b> burst, which is drained by the one slicer, so a tick
    /// that re-streams doors and loot together still cannot put an unpaced blast on a link that has
    /// no send window. It runs on the same listener-thread <c>Later</c> chain as the gas pump and
    /// ends itself the moment the session leaves the match.
    /// </para>
    /// </summary>
    private void PumpWorld(SoeConnection connection, GatewaySessionState state, int? worldGeneration = null)
    {
        worldGeneration ??= state.WorldGeneration;
        if (worldGeneration != state.WorldGeneration) return;
        RefreshHostedObserverTarget(connection, state);
        // Stop / Wait / Restream, decided per arm by a pure function so the arms can be pinned by a
        // test. Only Stop ends the chain (verify wave 4): "no per-match state yet" and "no pose yet"
        // are WAITS. state.Doors is created lazily inside SpawnNearbyDoors on ArmGroundLoot's
        // Later(GroundLootDelayMs), while this pump is armed in the same method at
        // WorldPumpIntervalMs — so folding the null case into the terminal arm made the whole
        // feature depend on 3000 > 2000 holding, and CRANBERRY_DOOR_RESTREAM_MS=1000 silently
        // disabled the re-stream for the rest of the match.
        //
        // The chain ends only when BOTH arms say Stop. One arm being switched off must not be able
        // to take the other one down with it.
        DoorPumpStep doorStep = MatchDoors.NextPumpStep(
            state.Match == MatchStep.InMatch || state.HostedObserverActive,
            _options.SendDoors,
            _options.DoorRestreamIntervalMs,
            state.Doors,
            WorldStreamPosition(state),
            _options.DoorRadius);

        WorldStreamStep lootStep = MatchLoot.NextPumpStep(
            state.Match == MatchStep.InMatch || state.HostedObserverActive,
            RealGroundLootEnabled,
            state.StreamedLoot,
            WorldStreamPosition(state),
            _options.LootStream,
            // NOT "state.StreamedLoot is not null" — that is a non-null initialiser and the guard
            // the door arm's shape was copied from is a compile-time true here. The landing burst
            // adopts itself into the working set only as DrainBurst pays it out, so a tick that
            // beats the drain plans against a half-empty set and duplicates it (docs/52 §I3).
            state.LandingLootDrained);

        // docs/61 §2: the third arm. Wave 5 spawned the map's 300 cars once, at the landing, and
        // never again — structurally the same defect docs/52 fixed for ground loot. Same shared
        // decision function, same burst, no third timer chain.
        WorldStreamStep vehicleStep = MatchVehicleStream.NextPumpStep(
            state.Match == MatchStep.InMatch || state.HostedObserverActive,
            _options.SendVehicles,
            state.StreamedVehicles,
            WorldStreamPosition(state),
            VehicleStreamingOptions(state),
            // NOT "the fleet exists": the fleet is built SYNCHRONOUSLY inside SpawnNearbyVehicles
            // while its spawns are paid out over DrainBurst's slices, so a tick between two slices
            // would plan against a half-adopted working set and re-offer every car the drain is
            // about to send — the identical trap docs/52 §I3 found for ground loot. LandingLootDrained
            // is the last action of that same burst list and is therefore the right latch for both.
            fleetPlanned: state.Fleet is not null && state.LandingLootDrained);

        if (doorStep == DoorPumpStep.Stop
            && lootStep == WorldStreamStep.Stop
            && vehicleStep == WorldStreamStep.Stop)
        {
            state.WorldPumping = false;
            return;
        }

        // PER-ARM RATE LIMIT (WorldPumpArm.IsDue). The chain ticks at Math.Min(doorMs, lootMs) so
        // that lowering one arm's interval cannot slow the other down; without these two stamps the
        // min also SPED the other arm up, because WorldStream.NextStep reads its interval only as a
        // Stop test. CRANBERRY_DOOR_RESTREAM_MS=500 ran the LOOT streamer at 500 ms — 6× its
        // configured 3,000, ~50 KB/s, with successive DrainBurst chains overlapping the next tick.
        long nowMs = Environment.TickCount64;
        // Half the shared period: a tick that arrives a millisecond early still counts as due, or an
        // arm whose interval EQUALS the pump's (the shipped case, both 3,000) would miss its own
        // deadline on timer jitter and run at half rate. It can never make an arm faster than the
        // pump, which is exactly the behaviour that existed before this limit.
        int toleranceMs = Math.Max(0, WorldPumpIntervalMs) / 2;
        if (doorStep == DoorPumpStep.Restream)
        {
            long doorDue = state.NextDoorPumpMs;
            if (!WorldPumpArm.IsDue(nowMs, ref doorDue, _options.DoorRestreamIntervalMs, toleranceMs))
            {
                doorStep = DoorPumpStep.Wait;
            }

            state.NextDoorPumpMs = doorDue;
        }

        if (lootStep == WorldStreamStep.Restream)
        {
            long lootDue = state.NextLootPumpMs;
            if (!WorldPumpArm.IsDue(nowMs, ref lootDue, _options.LootStream.RestreamIntervalMs, toleranceMs))
            {
                lootStep = WorldStreamStep.Wait;
            }

            state.NextLootPumpMs = lootDue;
        }

        if (vehicleStep == WorldStreamStep.Restream)
        {
            long vehicleDue = state.NextVehiclePumpMs;
            if (!WorldPumpArm.IsDue(
                nowMs, ref vehicleDue, _options.VehicleStream.RestreamIntervalMs, toleranceMs))
            {
                vehicleStep = WorldStreamStep.Wait;
            }

            state.NextVehiclePumpMs = vehicleDue;
        }

        var burst = new List<Action>();
        bool lootChanged = false;

        if (lootStep == WorldStreamStep.Restream)
        {
            lootChanged = PlanLootRestream(connection, state, burst);
        }
        lootChanged |= PlanSharedDrops(connection, state, burst);

        if (vehicleStep == WorldStreamStep.Restream)
        {
            // NOT a reason to republish ProximateItems: a car is not a ground item and that packet
            // is ~9.4 KB. lootChanged stays the only gate (docs/52 §3f rule 4).
            PlanVehicleRestream(connection, state, burst);
        }

        if (doorStep == DoorPumpStep.Restream)
        {
            SpawnNearbyDoors(connection, state, "world re-stream", burst);
        }

        if (burst.Count > 0)
        {
            // docs/52 §3f rule 4: a door-only tick pays NOTHING. ProximateItems is 6 + 74 × N bytes
            // — 9,552 at the 129 rows measured live — its reader clears and rebuilds the client's
            // whole collection, and it triggers a crafting-recipe recompute; republishing it because
            // a door moved would be the most expensive no-op on this wire.
            DrainBurst(
                connection,
                state,
                burst,
                from: 0,
                "world re-stream",
                republishProximateItems: lootChanged);
        }

        if (state.Fleet is VehicleFleet fuelled)
        {
            ReleaseSettledVehicles(connection, state, nowMs);
            // docs/61 §3: the caller VehicleFleet.BurnFuel never had. Burning is OFF by default —
            // an engine cutting out mid-drive has never been play-tested — but the GAUGE is on,
            // because a resource row the client already parses cannot cost anything.
            VehicleFuelTick fuel = state.Fuel.Step(fuelled, nowMs, _options.VehicleFuel, state.Guid);
            foreach (VehicleFuelGauge gauge in fuel.Gauges)
            {
                VehicleFuelGauge row = gauge;
                SendTunnel(connection, writer => new CharacterResourceUpdate(
                    row.Vehicle.Guid,
                    AugustFuelFacts.ResourceId,
                    AugustFuelFacts.ResourceType,
                    row.Value,
                    row.PreviousValue).WriteTo(writer));
            }

            foreach (MatchVehicle stalled in fuel.Stalled)
            {
                MatchVehicle dry = stalled;
                StopVehicleEngineForViewers(connection, state, dry);
                _log.Info($"{connection} vehicles: {dry} ran out of fuel — engine off");
            }

            // docs/118 §2/§3: the flip pulse and the extra fuel a held boost costs. On the world
            // pump rather than on the 20 Hz pose stream, so the period is the client's own
            // UPSIDE_DOWN_DAMAGE_PULSE amount over a period this server chose, and not one pulse
            // per movement packet.
            PumpVehicleDamage(connection, state, nowMs, fuel.BilledSeconds);
        }

        if (!Later(connection, WorldPumpIntervalMs, () => PumpWorld(connection, state, worldGeneration)))
        {
            state.WorldPumping = false;
        }
    }

    /// <summary>
    /// docs/52 §3a: plans one ground-loot re-stream tick into <paramref name="burst"/> — the
    /// evictions first, then the spawns. Returns whether the loot world actually changed, which is
    /// what decides whether the tick pays for a <c>ProximateItems</c> republish.
    /// <para>
    /// <b>The order is load-bearing.</b> Evicting first keeps the peak client-side object count at
    /// <see cref="LootStreamOptions.MaxLive"/> rather than <c>MaxLive + delta</c>, and it front-loads
    /// the 13-byte destroys so the working set is back under the cap before the 477-byte spawns land.
    /// </para>
    /// <para>
    /// <see cref="MatchLoot.PlanRestream"/> <b>commits</b>: it removes the evicted entries and
    /// reserves the spawned keys before a single packet goes out, because <see cref="DrainBurst"/>
    /// spreads the burst over ~160 ms of slices and a second plan must not be able to re-offer what
    /// the first one already owns. An abandoned reservation is self-healing — the first later tick
    /// that finds it out of range reaps it without a packet.
    /// </para>
    /// </summary>
    private bool PlanLootRestream(
        SoeConnection connection,
        GatewaySessionState state,
        List<Action> burst)
    {
        Vector3 centre = WorldStreamCentre(state);

        Z2LootLayout layout;
        try
        {
            layout = _lootLayout.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Same rule as the landing burst: missing or hand-edited data must not take the match
            // down. A Lazy<T> caches whatever it throws, and the landing burst has already warned
            // once by the time a tick reaches this, so this cannot become a new per-tick log flood.
            _log.Warn($"{connection} loot: re-stream unavailable ({ex.GetType().Name}: {ex.Message})");
            return false;
        }

        LootStreamPlan plan = state.StreamedLoot.PlanRestream(
            centre,
            layout,
            _options.LootStream,
            // docs/52 §4f: LootWorld mints transient ids from 1,000 upwards and never recycles them
            // (a recycled id would be matched to the wrong object by the 0xda apply, which keys on
            // the transient id and not the guid). A 25-minute match at 32 spawns per 3 s cannot
            // reach the 1,000,000 ceiling MatchDoors starts at, but the streamer is the first thing
            // in this server that could, so it is told the headroom rather than trusted with it.
            state.Loot.TransientIdHeadroom,
            // The clock, read only by LootStreamOptions.DroppedItemLifetimeMs, which is off by
            // default (docs/78 §5.1).
            Environment.TickCount64);

        foreach (ulong guid in plan.Evictions)
        {
            ulong evicted = guid;
            burst.Add(() => EvictGroundLoot(connection, state, evicted));
        }

        foreach (LootStreamSpawn spawn in plan.Spawns)
        {
            LootStreamSpawn planned = spawn;
            burst.Add(() => SpawnSharedLoot(connection, state, planned.Key, () => SpawnGroundLoot(
                connection,
                state,
                planned.ItemDefinitionId,
                planned.GroundModelId,
                planned.Position,
                count: state.StreamedLoot.RemainingCount(planned.Key, planned.Count),
                // Identity rotation, exactly as the landing burst sends: nothing derived so far says
                // how the client reads AddLightweightNpc's own f32×4 at +0xa0 (docs/33 §6c).
                nameId: planned.NameId)));
        }

        if (!plan.ChangedTheWorld)
        {
            return false;
        }

        // D29 / docs/32: this line is written BEFORE DrainBurst sends anything, so it says
        // "planned" and never "sent". The server log records what the server decided; the burst
        // completion line at the end of DrainBurst is the one that names what actually went out.
        _log.Info($"{connection} loot: re-stream PLANNED at {FormatPosition(centre)} — "
            + $"{plan.LiveMarkersInRadius} live marker(s) within {_options.LootStream.StreamRadiusMetres:F0} m, "
            + $"spawning {plan.Spawns.Count} (cap {_options.LootStream.MaxPerRestream}/tick), "
            + $"evicting {plan.Evictions.Count} past {_options.LootStream.EffectiveDespawnRadiusMetres:F0} m, "
            + $"{plan.LiveAfter} live of {_options.LootStream.MaxLive}"
            + (plan.Spawns.Count == 0
                ? string.Empty
                : FormattableString.Invariant($", {plan.NearestMetres:F1}–{plan.FarthestMetres:F1} m out"))
            + (string.IsNullOrEmpty(plan.AreaName) ? string.Empty : $" in {plan.AreaName}")
            + (plan.DiscExhausted ? ", disc exhausted" : string.Empty)
            + (plan.ReapedReservations > 0
                ? $", {plan.ReapedReservations} abandoned reservation(s) reaped"
                : string.Empty)
            + $"; ~{plan.EstimatedBytes:N0} B this tick, {state.Loot.Count} already on the ground");
        return true;
    }

    /// <summary>
    /// docs/52 §5a: destroy one streamed-out ground object. <c>Character.RemovePlayer (0f 01)</c>
    /// with <c>effectFlag = 0</c> is a full, silent destroy — the entity-destroy call in
    /// <c>FUN_140af9ca0</c> case 1 sits <b>outside</b> the <c>effectFlag == 1</c> ragdoll branch, and
    /// the lookup is by world guid alone, which is what makes a 13-byte packet a legitimate
    /// streaming primitive.
    /// <para>
    /// Destroying the object also unlinks its <c>[F]</c> binding: <c>ClientInteractComponent</c>'s
    /// destructor virtual-destructs its <c>ProximityTrackableTemplate</c>, which splices itself out
    /// of the interaction manager's two lists. So an evicted item leaves no stale interact target,
    /// and the client's own <c>ProximityManager.log</c> staying free of "Cache count … is not zero!"
    /// is the client-originated proof of it (docs/52 §5b-c, and under D29 the only kind that counts).
    /// </para>
    /// </summary>
    private void EvictGroundLoot(SoeConnection connection, GatewaySessionState state, ulong worldGuid)
    {
        if (!state.Loot.TryEvict(worldGuid, out GroundLootItem? item))
        {
            // Claimed by a pickup between the plan and this slice. PlanRestream has already dropped
            // it from the working set, so there is nothing to repair and nothing worth a log line.
            return;
        }

        // The world object is gone, so a later re-use of the guid may be promoted again — the same
        // bookkeeping the pickup path does, and an eviction owes it for the same reason (docs/52 §5d).
        state.FullNpcSent.Remove(worldGuid);
        state.BodyBags.Remove(worldGuid);
        if (state.AccessedBodyBag == worldGuid) CloseBodyBag(connection, state);
        SendTunnel(connection, writer => new RemovePlayer(worldGuid).WriteTo(writer));
        _log.Info($"{connection} loot: streamed out world object {worldGuid} (item "
            + $"{item.ItemDefinitionId} ×{item.Count}, transient {item.TransientId}) at "
            + $"{FormatPosition(item.Position)} — RemovePlayer, effectFlag 0; "
            + $"{state.Loot.Count} left on the ground");
    }

    /// <summary>
    /// Adopts one just-spawned object into the streaming working set (docs/52 §Integration step 2).
    /// Returns the item so the adoption can wrap a <see cref="SpawnGroundLoot"/> call in one
    /// expression, which is what keeps the landing burst's planner a single statement per object.
    /// </summary>
    private GroundLootItem NoteSpawned(
        SoeConnection connection,
        GatewaySessionState state,
        in LootStreamKey key,
        GroundLootItem item)
    {
        // The stamp is read by exactly one rule, LootStreamOptions.DroppedItemLifetimeMs, and only
        // for LootStreamKeyKind.Dropped keys. It is recorded for every object regardless, because a
        // stamp that only exists when a switch is on is a stamp that is always missing the first
        // time the switch is turned on.
        if (state.StreamedLoot.NoteSpawned(key, item.WorldGuid, item.Position, Environment.TickCount64))
        {
            return item;
        }

        // The key was ALREADY on the client under an earlier guid, so the SpawnGroundLoot in this
        // call's own argument expression has just minted a duplicate: it is registered in LootWorld
        // and its d6/da/ea 04 have already gone out. MatchLoot can only hold one guid per key, so
        // dropping this one on the floor left an object the client kept for the rest of the match —
        // in every ProximateItems republish, visually doubled on top of its twin, and unevictable
        // because nothing was left holding its identity. Destroy it instead (wave-5 verify fix).
        //
        // LandingLootDrained now makes the reachable path unreachable; this is the belt to its
        // braces, and it is the half that makes the leak IMPOSSIBLE rather than merely unlikely.
        state.StreamedLoot.TryGetGuid(key, out ulong kept);
        state.Loot.TryEvict(item.WorldGuid, out _);
        state.FullNpcSent.Remove(item.WorldGuid);
        SendTunnel(connection, writer => new RemovePlayer(item.WorldGuid).WriteTo(writer));
        _log.Warn($"{connection} loot: world object {item.WorldGuid} (item {item.ItemDefinitionId}) "
            + $"duplicated a ground marker already streamed as {kept} — destroyed the duplicate "
            + "(RemovePlayer, effectFlag 0)");
        return item;
    }

    /// <summary>
    /// docs/62 stage 1: <c>0x26 09 Recipe.List</c>, plus the optional ingredient seed. The client
    /// ships no recipe data at all — <c>ClientRecipes</c> and <c>ClientRecipeComponents</c> are
    /// header-only — so an empty crafting tab is the server's doing and nothing else's.
    /// <para>
    /// Sent once per match, <b>after</b> the zoning burst and after the container bootstrap. The
    /// same records also fill the self record's <c>0x11a</c> field, but only with
    /// <c>CRANBERRY_CRAFTING_SELFRECORD=1</c>: a length error there fails the login outright
    /// (<c>FUN_1409e1080</c> writes <c>0xbadbeef</c> to address 0), whereas a bad list here costs
    /// one empty tab.
    /// </para>
    /// </summary>
    private void SendRecipes(SoeConnection connection, GatewaySessionState state, string trigger)
    {
        CraftingOptions crafting = _options.Crafting.Effective;
        if (crafting.SendRecipeList)
        {
            var list = new RecipeList(
                CraftingCatalog.ToRecords(crafting.RetailRecipes, crafting.SentinelFields));
            SendTunnel(connection, writer => list.WriteTo(writer));
            _log.Info($"{connection} crafting: 0x26 09 Recipe.List, {crafting.Catalogue.Count} "
                + $"recipe(s) ({list.Length} B, {trigger}) — "
                + (crafting.RetailRecipes
                    ? "the retail six, byte for byte the friend server's 936 B packet (D276)"
                    : "the wave-6 four, every quantity DESIGN (D47)")
                + ", and NOT live-verified (docs/62 §6 acceptance step 2)");
        }

        if (!crafting.SeedIngredients || state.Inventory is not PlayerInventory inventory)
        {
            return;
        }

        // CRANBERRY_CRAFT_SEED=1, default off. Three of the four recipes have no loot-reachable
        // inputs today because shred is deliberately unwired (docs/62 §8), so this exists purely so
        // all four can be play-tested at all.
        foreach (RecipeIngredient seed in CraftingService.SeedGrants())
        {
            InventoryPlacement placed = inventory.TryPickUp(
                seed.ItemDefinitionId, seed.Quantity, out InventoryItemInstance? granted);
            if (granted is null)
            {
                _log.Warn($"{connection} crafting: seed of item {seed.ItemDefinitionId} ×"
                    + $"{seed.Quantity} refused — {placed.Rule}");
                continue;
            }

            SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, granted.ToRecord(state.Guid)));
            if (placed.Kind == InventoryPlacementKind.LoadoutSlot)
            {
                SendTunnel(connection, writer => inventory.ToLoadoutSlot(granted).WriteTo(writer));
            }

            _log.Info($"{connection} crafting: seeded item {seed.ItemDefinitionId} ×{seed.Quantity} "
                + $"as instance {granted.Guid} — {placed.Rule} (CRANBERRY_CRAFT_SEED)");
        }

        if (inventory.BaseBag is InventoryContainer bag)
        {
            SendTunnel(connection, writer => inventory.ToUpdate(bag).WriteTo(writer));
        }
    }

    /// <summary>
    /// docs/61 §2, step 4: plans one vehicle re-stream tick into <paramref name="burst"/> — the
    /// evictions first, then the spawns, exactly as the ground-loot arm does.
    /// <para>
    /// Two rules the loot arm does not need: an <b>occupied</b> car is never evicted at any distance
    /// (destroying the actor a player is sitting in unparents their character and orphans the
    /// managed object the client is simulating), and a car is judged on its current pose rather than
    /// on the parking anchor it spawned at, because somebody may have driven it.
    /// </para>
    /// </summary>
    private void PlanVehicleRestream(
        SoeConnection connection,
        GatewaySessionState state,
        List<Action> burst)
    {
        if (state.Fleet is not VehicleFleet fleet || WorldStreamPosition(state) is not Vector3 centre)
        {
            return;
        }

        VehicleStreamPlan plan = state.StreamedVehicles.PlanRestream(
            centre, fleet, VehicleStreamingOptions(state), state.Guid);
        if (!plan.ChangedTheWorld)
        {
            return;
        }

        foreach (ulong guid in plan.Evictions)
        {
            ulong evicted = guid;
            burst.Add(() =>
            {
                // An unoccupied sleeping car may still be simulated by this viewer. Release it
                // immediately before destroying the actor, never on a silence/settling timer.
                if (fleet.TryGet(evicted, out MatchVehicle? car))
                {
                    // Planning commits the stream set before this paced action runs. A rider may
                    // have mounted in between; retain the actual actor and restore its membership.
                    if (car.OccupantCount > 0 || car.OwnerGuid == state.Guid)
                    {
                        state.StreamedVehicles.NoteSpawned(car.Guid, car.Position);
                        return;
                    }
                    if (car.CoastingOwnerGuid == state.Guid)
                        ReleaseVehicleSimulation(connection, state, car);
                }
                SendTunnel(connection, writer => new RemovePlayer(evicted).WriteTo(writer));
                state.StreamedVehicles.ConfirmEviction(evicted);
            });
        }

        foreach (VehicleStreamSpawn spawn in plan.Spawns)
        {
            MatchVehicle car = spawn.Vehicle;
            burst.Add(() =>
            {
                if (!CanSendVehicleSpawn(fleet, car))
                {
                    state.StreamedVehicles.NoteEvicted(car.Guid);
                    return;
                }
                SendTunnel(connection, VehicleSpawn(car).WriteTo);
                SendTunnel(connection, writer =>
                    VehicleFullState.Create(car).WriteTo(writer));
                state.StreamedVehicles.ConfirmSpawn(car.Guid);
                SyncVehicleAttachments(connection, state, car);
                SendVehicleActiveEffects(connection, car);
            });
        }

        _log.Info($"{connection} vehicles: re-stream at {FormatPosition(centre)} — "
            + $"{plan.Spawns.Count} spawned, {plan.Evictions.Count} evicted, "
            + $"{plan.ProtectedFromEviction} occupied-and-kept, {plan.LiveAfter} live of "
            + $"{plan.InRadius} in radius, ~{plan.EstimatedBytes} B");
    }

    /// <summary>
    /// docs/61 §1: this session as a bystander of other people's cars. <c>Holds</c> is exact rather
    /// than approximate — it asks this session's own streamer whether the actor was ever spawned
    /// here — because a <c>0x78</c> naming a transient the receiving client has never been given is
    /// at best discarded and at worst <c>ClientBadData.log</c> traffic.
    /// </summary>
    private sealed class SessionVehicleObserver(ZoneService service, SoeConnection connection, GatewaySessionState state)
        : IVehicleObserver
    {
        public ulong CharacterGuid => state.Guid;

        public ulong MatchId => state.BountyAdmission.MatchId;

        public bool IsOpen => connection.State == ConnectionState.Open;

        public Vector3? Position => service.WorldStreamPosition(state);

        public bool Holds(ulong vehicleGuid) => state.StreamedVehicles.IsSpawned(vehicleGuid);

        public void Relay(VehiclePoseRelay pose)
        {
            var diagnostics = service._productionDiagnostics;
            long started = diagnostics is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            service.RecordVehiclePoseOffer(state);
            service.SendTunnel(connection, writer => pose.WriteTo(writer));
            diagnostics?.VehiclePoseEnqueueWork.RecordTicks(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    /// <summary>
    /// The world re-stream pump's period: the shorter of the two arms' intervals, or 0 when neither
    /// arm is switched on. <c>Math.Min</c> and not the door interval, so overriding one arm's period
    /// downwards cannot slow the other arm down (docs/52 §Integration step 3).
    /// </summary>
    private int WorldPumpIntervalMs
    {
        get
        {
            int doorMs = _options.SendDoors ? Math.Max(0, _options.DoorRestreamIntervalMs) : 0;
            int lootMs = RealGroundLootEnabled && _options.LootStream.Enabled
                ? Math.Max(0, _options.LootStream.RestreamIntervalMs)
                : 0;
            int vehicleMs = _options.SendVehicles && _options.VehicleStream.Enabled
                ? Math.Max(0, _options.VehicleStream.RestreamIntervalMs)
                : 0;
            int shortest = 0;
            foreach (int arm in (ReadOnlySpan<int>)[doorMs, lootMs, vehicleMs])
            {
                if (arm > 0 && (shortest == 0 || arm < shortest))
                {
                    shortest = arm;
                }
            }

            return shortest;
        }
    }

    /// <summary>
    /// Is the map's own ground loot switched on at all. There is no single <c>SendGroundLoot</c>
    /// flag — the landing burst gates on the radius and the cap, so the streamer gates on the same
    /// pair, or <c>CRANBERRY_GROUND_LOOT_RADIUS_M=0</c> would turn the burst off and leave the
    /// streamer running behind it.
    /// </summary>
    private bool RealGroundLootEnabled =>
        _options.GroundLootRadius > 0f && _options.GroundLootMaxPerBurst > 0;

    /// <summary>
    /// Development aid (<c>ZoneOptions.DevGroundLootMs</c>): once per session, drop the sample
    /// items around the player after the configured delay. Nothing here is retail behaviour — a
    /// real world loot table is a later lane; this is the hook that makes docs/13's pickup flow
    /// observable, mirroring how <c>AutoMatchMs</c> makes the match flow observable.
    /// </summary>
    private void ArmDevGroundLoot(SoeConnection connection, GatewaySessionState state, string trigger)
    {
        if (_options.DevGroundLootMs <= 0 || _options.DevGroundLootCount <= 0 || state.DevGroundLootArmed)
        {
            return;
        }

        state.DevGroundLootArmed = true;
        _log.Info($"{connection} loot: dev ground loot armed by {trigger} — "
            + $"{_options.DevGroundLootCount} × item {_options.GroundLootItemDefinitionId} in {_options.DevGroundLootMs} ms");
        Later(connection, _options.DevGroundLootMs, () =>
        {
            SpawnDevGroundLoot(connection, state);
            if (_options.SendProximateItems && state.Loot.Count > 0)
            {
                SendProximateItems(connection, state);
            }
        });
    }

    /// <summary>
    /// Spawns the sample ring. The <c>ProximateItems</c> republish belongs to the caller: in a match
    /// this runs in the same burst as the real Z2 roll and the two must share one 0xf8.
    /// </summary>
    private void SpawnDevGroundLoot(SoeConnection connection, GatewaySessionState state)
    {
        // docs/48 §Integration step 6: this match's own drop, never the fixed option, or a burst
        // sent before the client's first pose lands kilometres from the player.
        Vector4 fallback = state.Match >= MatchStep.Zoning
            ? (state.Drop != default ? state.Drop : _options.MatchDropSpawn)
            : _options.SpawnPosition;
        Vector3 centre = state.Movement.Player?.Position
            ?? new Vector3(fallback.X, fallback.Y, fallback.Z);

        for (int index = 0; index < _options.DevGroundLootCount; index++)
        {
            // A small ring around the player: close enough for the client's own range check on the
            // [F] prompt (docs/13 §8: ~4 m), far enough apart to be told apart on screen.
            double angle = 2 * Math.PI * index / _options.DevGroundLootCount;
            var position = new Vector3(
                centre.X + _options.DevGroundLootRadius * (float)Math.Cos(angle),
                centre.Y,
                centre.Z + _options.DevGroundLootRadius * (float)Math.Sin(angle));
            SpawnGroundLoot(
                connection,
                state,
                _options.GroundLootItemDefinitionId,
                _options.GroundLootModelId,
                position,
                nameId: _options.GroundLootNameId);
        }
    }

    /// <summary>
    /// Largest nearest-marker window one burst may ask for. The window is bigger than the burst cap
    /// so that markers whose category rolls nothing (FireExtinguisher) are backfilled by the next
    /// nearest rather than costing the burst an item.
    /// </summary>
    private const int GroundLootQueryWindow = 512;

    /// <summary>
    /// <summary>
    /// Puts a landed crate in the world. It is an ordinary ground object - the same
    /// <c>AddLightweightNpc</c> + <c>LightweightToFullNpc</c> + interact component every item on the
    /// floor gets - carrying the client's own <c>Common_Props_MilitaryCrate.adr</c> (9218,
    /// DESCRIPTION <i>"Crate.Military for air drops."</i>) and the name id of item 1501 "Military
    /// Crate". No new packet, no new pickup path: the crate is reached by the interact dispatch that
    /// already works, and <see cref="TryOpenAirdropCrate"/> intercepts it there.
    /// <para>
    /// The contents are rolled HERE and not at the press, so two packets from one <c>[F]</c> cannot
    /// produce two different crates.
    /// </para>
    /// </summary>
    private void SpawnAirdropCrate(
        SoeConnection connection,
        GatewaySessionState state,
        MatchAirdrops airdrops,
        in AirdropEvent drop,
        long nowMs)
    {
        AirdropTables tables;
        try
        {
            tables = AirdropData.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Same rule as the ground tables: missing or hand-edited data makes the feature
            // unavailable and says so, it never takes the match down.
            _log.Warn($"{connection} airdrop: crate table unavailable ({ex.GetType().Name}: "
                + $"{ex.Message}); drop #{drop.Index + 1} is cancelled");
            return;
        }

        List<AirdropItem> contents = airdrops.RollContents(tables, drop.Index);
        LootStreamKey crateKey = SharedCrateKey(drop.Index, drop.PayloadIndex);
        if (state.StreamedLoot.IsTaken(crateKey)) return;
        AirdropCrateState sharedCrate = ResolveSharedCrate(state, crateKey,
            new AirdropCrateState(drop.Index, drop.Position, nowMs + _options.Airdrop.UnlockMs, contents));
        GroundLootItem crate = SpawnGroundLoot(
            connection,
            state,
            tables.Crate.ItemDefinitionId,
            tables.Crate.GroundModelId,
            sharedCrate.Position,
            count: 1,
            nameId: tables.Crate.NameId);

        state.AirdropCrates[crate.WorldGuid] = sharedCrate;
        // A crate is a server-placed object with no marker behind it, so it takes a Dropped stream
        // key exactly as a player's own drop does - that is the kind the streamer has for "this
        // exists, it counts against MaxLive, and no marker owns it".
        NoteSpawned(connection, state, crateKey, crate);
        EnsureAirdropMarker(state, crateKey, sharedCrate);

        SendTunnel(connection, w => GasAlerts.Write(w, _options.Airdrop.LandedText));
        if (_options.SendProximateItems)
        {
            SendProximateItems(connection, state);
        }

        _log.Info($"{connection} airdrop: drop #{drop.Index + 1} LANDED as world object "
            + $"{crate.WorldGuid} (model {tables.Crate.GroundModelId}, item "
            + $"{tables.Crate.ItemDefinitionId} \"{tables.Crate.Name}\") at "
            + $"{FormatPosition(drop.Position)} - {contents.Count} item(s) inside "
            + $"[{string.Join(", ", contents.Select(i => $"{i.ItemDefinitionId}x{i.Count}"))}], "
            + $"unlocks in {_options.Airdrop.UnlockMs} ms; banner (11 31) "
            + $"\"{_options.Airdrop.LandedText}\"");
    }

    /// <summary>
    /// <b>The crate's open path, and the reason it has to run before every other pickup rule.</b> A
    /// crate is registered in <see cref="GatewaySessionState.Loot"/> like any other ground object -
    /// which is exactly what makes <c>[F]</c> reach it - so an uncaught press would hand the player
    /// the <i>crate</i> (item 1501, a World Container) instead of what is in it.
    ///
    /// <para>Opening lays the contents on the ground in a ring around the crate and destroys the
    /// crate. Every spilled item is then an ordinary ground pickup through the path that already
    /// works, which is why this feature needs no container packet, no withdrawal flow and no new
    /// wire layout - and why <see cref="AirdropOptions.SpillRadiusMetres"/> is 1.5 m: the whole
    /// spill stays inside the client's own 2 m proximity ball, so the <c>[F]</c> panel lists a
    /// crate's contents as one set.</para>
    ///
    /// <para>Returns true when the guid WAS a crate, whatever happened to it - a caller must then
    /// stop, because the object is either gone or deliberately still standing (locked).</para>
    /// </summary>
    private bool TryOpenAirdropCrate(
        SoeConnection connection,
        GatewaySessionState state,
        ulong worldGuid,
        string trigger)
    {
        if (!state.AirdropCrates.TryGetValue(worldGuid, out AirdropCrateState? crate))
        {
            return false;
        }

        // The same reach gate every other ground object gets, on the same non-destructive read:
        // a crate is a world object like any other and must not be openable from across the map.
        if (!WithinPickupReach(state, crate.Position, out float reach))
        {
            _log.Info($"{connection} airdrop: {trigger} on crate {worldGuid} REFUSED — out of reach "
                + $"({reach:0.00} m > {_options.LootStream.PickupReachMetres:0.#} m); the crate stays "
                + "standing");
            return true;
        }

        long now = Environment.TickCount64;
        if (now < crate.UnlockAtMs)
        {
            // Retail's ~8 s unlock. Deliberately silent on the wire: the client's own prompt
            // vocabulary has no "unlocking" line, and inventing a second unproven string for an
            // eight-second window is a worse trade than a log line.
            _log.Info($"{connection} airdrop: {trigger} on crate {worldGuid} REFUSED - "
                + $"{crate.UnlockAtMs - now} ms of its {_options.Airdrop.UnlockMs} ms unlock left; "
                + "the crate stays shut");
            return true;
        }

        if (!TryClaimSharedLoot(state, worldGuid, out GroundLootItem? _))
        {
            // The other half of the same [F] press already opened it. Exactly the idempotency case
            // docs/47 s4b describes for ordinary ground loot.
            _log.Info($"{connection} airdrop: {trigger} on crate {worldGuid} - already opened by the "
                + "other half of the same press");
            state.AirdropCrates.Remove(worldGuid);
            return true;
        }

        state.AirdropCrates.Remove(worldGuid);
        state.FullNpcSent.Remove(worldGuid);
        state.StreamedLoot.NoteTaken(worldGuid);
        SendTunnel(connection, w => new RemovePlayer(worldGuid).WriteTo(w));

        MatchAirdrops? airdrops = state.Airdrops;
        int spilled = 0;
        for (int i = 0; i < crate.Contents.Count; i++)
        {
            AirdropItem item = crate.Contents[i];
            Vector3 at = airdrops?.SpillPositionFor(crate.Position, i, crate.Contents.Count)
                ?? crate.Position;
            GroundLootItem spawned = SpawnGroundLoot(
                connection,
                state,
                item.ItemDefinitionId,
                item.GroundModelId,
                at,
                count: item.Count,
                nameId: item.NameId);
            SharePlayerDrop(connection, state, spawned);
            spilled++;
        }

        if (_options.SendProximateItems)
        {
            SendProximateItems(connection, state);
        }

        _log.Info($"{connection} airdrop: {trigger} OPENED crate #{crate.Index + 1} ({worldGuid}) - "
            + $"{spilled} item(s) laid within {_options.Airdrop.SpillRadiusMetres:0.#} m; "
            + $"{state.Loot.Count} on the ground");
        return true;
    }

    /// docs/33: roll the map's own spawn markers around the landing point. 168,322 placements were
    /// parsed out of the client's own <c>Z2.zone</c> and each is rolled against a Cranberry loot
    /// table whose item ids, name ids, stack clamps and ground models all come from
    /// <c>ClientItemDefinitions.txt</c> / <c>Models.txt</c>.
    /// <para>
    /// The burst takes the <b>nearest</b> markers to where the player actually stands
    /// (<see cref="Z2LootSpawns.QueryNearest(in Vector3, float, Span{int})"/>), and the log records
    /// the nearest and farthest metres it spawned: a burst whose closest item is 75 m away is a
    /// failure that "spawned 64 (cap 64)" on its own would report as a success, and the client's own
    /// interact range is 3 m.
    /// </para>
    /// </summary>
    private void SpawnRealGroundLoot(
        SoeConnection connection,
        GatewaySessionState state,
        string trigger,
        List<Action> burst)
    {
        // docs/48 §Integration step 6: this match's own drop, never the fixed option.
        Vector3 centre = WorldStreamCentre(state);

        Z2LootLayout layout;
        try
        {
            layout = _lootLayout.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Missing or corrupt data must not take the match down — the dev drop still works and
            // the reason is on the log rather than in an unhandled exception on the listener thread.
            // Deliberately broad: the JSON table is shipped as loose content so the odds can be
            // retuned without a rebuild, so a hand-edit's JsonException is the likeliest failure of
            // all, and a Lazy<T> caches whatever it throws for the life of the process.
            _log.Warn($"{connection} loot: real Z2 ground loot unavailable ({ex.GetType().Name}: "
                + $"{ex.Message}); only the development drop will spawn");
            return;
        }

        Z2LootSpawns spawns = layout.Spawns;
        int cap = Math.Min(_options.GroundLootMaxPerBurst, GroundLootQueryWindow);
        int window = Math.Min(GroundLootQueryWindow, Math.Max(cap, cap * 2));
        Span<int> found = stackalloc int[window];
        // docs/39 §I.3: QueryLive, not QueryNearest — the nearest markers CARRYING AN ITEM this
        // match. With the density gate on, three quarters of the markers are empty, so a plain
        // nearest-query would fill the burst's budget with holes and then truncate.
        int matched = layout.QueryLive(centre, _options.GroundLootRadius, found);
        int usable = Math.Min(matched, window);
        int spawned = 0;
        int boxes = 0;
        string? area = null;
        float nearest = float.MaxValue;
        float farthest = 0f;
        Span<LootClusterItem> cluster = stackalloc LootClusterItem[2];

        // spawned + boxes, not spawned: the ammunition boxes stay EXEMPT in the sense that a gun
        // already taken always gets its own pair (a gun without ammunition is an inert prop, docs/39
        // §5), but they must still count against the burst's budget or the cap bounds nothing — at
        // ~25 % firearms a cap of 128 produced up to 384 world objects, three times the largest
        // burst this client has ever accepted.
        for (int index = 0; index < usable && spawned + boxes < cap; index++)
        {
            if (!layout.TryGet(found[index], out LootSpawnRoll roll))
            {
                // Belt and braces: QueryLive already filtered these out. A marker is empty when it
                // lost the density gate, when its room was full, or when its category rolls nothing
                // at all (FireExtinguisher — the August client has no such item, docs/33 §3).
                continue;
            }

            // Not every marker sits inside a named area (LootSpawnPoint.NoArea); take the first one
            // that does, because "spawned 41 items in Loot.PVResidential.01" is what makes a live
            // capture readable and "spawned 41 items in " is noise.
            if (string.IsNullOrEmpty(area))
            {
                area = spawns.AreaNameOf(spawns[found[index]]);
            }

            float dx = roll.Position.X - centre.X;
            float dz = roll.Position.Z - centre.Z;
            float distance = MathF.Sqrt((dx * dx) + (dz * dz));
            nearest = MathF.Min(nearest, distance);
            farthest = MathF.Max(farthest, distance);

            LootSpawnRoll planned = roll;
            // docs/52 §Integration step 2 — THE load-bearing line. The streamer's 60 m disc lies
            // inside this burst's 80 m one, so unless the landing burst is adopted into the working
            // set the first re-stream tick finds it empty and spawns all 128 objects a second time.
            // AdoptingTheLandingBurstIsWhatStopsTheFirstTickDuplicatingIt pins both directions.
            LootStreamKey markerKey = LootStreamKey.ForMarker(found[index]);
            burst.Add(() => SpawnSharedLoot(connection, state, markerKey, () => SpawnGroundLoot(
                connection,
                state,
                planned.ItemDefinitionId,
                planned.GroundModelId,
                planned.Position,
                count: state.StreamedLoot.RemainingCount(markerKey, planned.Count),
                // No rotation: the marker's own yaw is NOT sent. zoneread.py documents the ZONE
                // file's field as Euler (yaw/pitch/roll, radians), but nothing derived so far says
                // how the client reads AddLightweightNpc's own f32×4 at +0xa0 — FUN_140a2d040 only
                // copies the four floats, and the sole live-proven value, (0,0,0,1), is the identity
                // in BOTH the Euler and the quaternion reading, so it cannot tell them apart. Feeding
                // a yaw into x of a quaternion would tip every prop on its side. Identity until the
                // consumer of +0xa0 is read (docs/33 §6c refinement, deferred).
                nameId: planned.NameId)));
            spawned++;

            // docs/39 §5: every firearm is a set — the gun plus two ammunition boxes of its own
            // calibre. They are EXEMPT from the burst cap because they belong to their gun, not to
            // the budget: §3 deliberately removed ammunition from Gear01/Weapons01, so a gun without
            // its pair is an inert prop and loose rounds in an empty room are the failure signature.
            if (!_options.SendLootClusters)
            {
                continue;
            }

            int pair = layout.ClusterFor(roll, cluster);
            for (int box = 0; box < pair; box++)
            {
                // Copy out of the stackalloc span before the closure: a Span<T> cannot be captured,
                // and the buffer is reused by the next marker's ClusterFor.
                LootClusterItem ammunition = cluster[box];
                LootStreamKey boxKey = LootStreamKey.ForBox(ammunition);
                burst.Add(() => SpawnSharedLoot(connection, state, boxKey, () => SpawnGroundLoot(
                    connection,
                    state,
                    ammunition.ItemDefinitionId,
                    ammunition.GroundModelId,
                    ammunition.Position,
                    count: state.StreamedLoot.RemainingCount(boxKey, ammunition.Count),
                    nameId: ammunition.NameId)));
                boxes++;
            }
        }

        string reach = spawned == 0
            ? string.Empty
            : FormattableString.Invariant($", {nearest:F1}–{farthest:F1} m out");
        string line = $"{connection} loot: real Z2 ground loot at {FormatPosition(centre)} ({trigger}) — "
            + $"{matched} live marker(s) within {_options.GroundLootRadius} m of "
            + $"{layout.LiveCount:N0} on the map, planned {spawned} (cap {cap}, boxes included) "
            + $"+ {boxes} clustered ammunition box(es){reach}"
            + $"{(string.IsNullOrEmpty(area) ? string.Empty : $" in {area}")}; "
            + $"{state.Loot.Count} already on the ground";

        if (spawned == 0)
        {
            // A silent "0 markers" is how a drop point outside the ±4,096 m marker field looks, and
            // it is exactly the failure that hid this feature being a no-op. It is a warning.
            _log.Warn(line + " — NOTHING SPAWNED: the landing point has no spawn markers near it");
            return;
        }

        _log.Info(line);
    }

    /// <summary>
    /// docs/43 §I.2 hooks 1-2: plan the match's car park and stream the nearest of it in.
    /// <para>
    /// The plan is the whole map (<see cref="ZoneOptions.VehiclePlan"/>, 300 cars over the derived
    /// parking anchors, seeded so the same match seed parks the same cars in the same driveways);
    /// only the ones near the landing point go on the wire, because a 300-car burst is not a burst.
    /// The two packets are the parachute's own, live-proven pair — <c>d7 AddLightweightVehicle</c>
    /// with <c>OwnerGuid = 0</c> and no position block, then <c>db LightweightToFullVehicle</c>.
    /// </para>
    /// <para>
    /// Deliberately NOT registered with <c>state.Movement.RegisterManagedEntity</c> here: a parked
    /// car has no owner, so it streams no pose. Registration happens at the moment a rider takes the
    /// driver's seat, which is when the client starts simulating it (docs/43 §3).
    /// </para>
    /// </summary>
    private void SpawnNearbyVehicles(
        SoeConnection connection,
        GatewaySessionState state,
        string trigger,
        List<Action> burst)
    {
        if (!_options.SendVehicles || _options.VehicleRadius <= 0f || _options.VehicleMaxPerBurst <= 0)
        {
            return;
        }

        // docs/48 §Integration step 6: this match's own drop, never the fixed option.
        Vector3 centre = WorldStreamCentre(state);

        VehicleFleet fleet;
        IReadOnlyList<MatchVehicle> parked;
        try
        {
            fleet = EnsureVehicleFleet(connection, state, populate: true);
            parked = [.. fleet.Vehicles];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Same rule as the loot and door bursts: missing or hand-edited data degrades the match,
            // it never takes the listener thread down.
            _log.Warn($"{connection} vehicles: roster or anchor data unavailable "
                + $"({ex.GetType().Name}: {ex.Message}) — no vehicles this match");
            return;
        }

        if (!state.VehicleObserverRegistered)
        {
            // docs/61 §1: from the moment this session holds cars it is also a possible BYSTANDER of
            // somebody else's. Registering by character guid rather than appending is what makes a
            // reconnect not double every relay.
            _vehiclePoses.Register(new SessionVehicleObserver(this, connection, state));
            state.VehicleObserverRegistered = true;
        }

        float radius = VehicleInitialRadius(state);
        float radiusSquared = radius * radius;
        int sent = 0;
        foreach (MatchVehicle vehicle in parked
            .Where(candidate =>
            {
                float dx = candidate.Position.X - centre.X;
                float dz = candidate.Position.Z - centre.Z;
                return ((dx * dx) + (dz * dz)) <= radiusSquared;
            })
            .OrderBy(candidate =>
                (candidate.Position.X - centre.X) * (candidate.Position.X - centre.X)
                + (candidate.Position.Z - centre.Z) * (candidate.Position.Z - centre.Z))
            .Take(_options.VehicleStream.Enabled
                ? Math.Min(_options.VehicleMaxPerBurst, VehicleStreamingOptions(state).MaxLive)
                : _options.VehicleMaxPerBurst))
        {
            MatchVehicle planned = vehicle;
            burst.Add(() =>
            {
                if (!CanSendVehicleSpawn(fleet, planned)) return;
                // docs/61 §2.4, NOT optional: a guid the client already holds would get a SECOND
                // actor at the same spot that nothing ever destroys. NoteSpawned is the only thing
                // that stops the first re-stream tick duplicating this whole burst.
                if (!state.StreamedVehicles.NoteSpawned(planned.Guid, planned.Position))
                {
                    return;
                }

                var lightweight = VehicleSpawn(planned);
                SendTunnel(connection, writer => lightweight.WriteTo(writer));
                SendTunnel(connection, writer =>
                    VehicleFullState.Create(planned).WriteTo(writer));
                SyncVehicleAttachments(connection, state, planned);
                SendVehicleActiveEffects(connection, planned);
                _log.Info($"{connection} vehicles: parked {planned} at {FormatPosition(planned.Position)} "
                    + $"— AddLightweightVehicle ({lightweight.Length} B) + LightweightToFullVehicle");
            });
            sent++;
        }

        state.StreamedVehicles.NoteStreamed(centre);
        _log.Info($"{connection} vehicles: planned {sent} of {parked.Count} within "
            + $"{radius} m of {FormatPosition(centre)} ({trigger}); the re-stream arm "
            + $"is {(_options.SendVehicles && _options.VehicleStream.Enabled ? "ON" : "OFF")} from here "
            + "— NOT live-verified");
    }

    /// <summary>
    /// docs/43 §I.2 hook 3: seat a rider. Called from both candidate c2s triggers — <c>70 01
    /// MountRequest</c> and the <c>09 07</c>/<c>09 15</c> interact pair — because which one an E-press
    /// actually produces is docs/43's blocker 1 and one live press settles it. Returns true when the
    /// guid named a vehicle, so an interact falls through to doors and ground loot unchanged.
    /// </summary>
    private bool TryEnterVehicle(
        SoeConnection connection,
        GatewaySessionState state,
        ulong vehicleGuid,
        int seatIndex,
        string source,
        VehicleEntrySource arm)
    {
        if (state.Fleet is not VehicleFleet fleet || !fleet.TryGet(vehicleGuid, out MatchVehicle? known))
        {
            return false;
        }

        if (!state.StreamedVehicles.IsSpawned(vehicleGuid) || state.ChuteGuid != 0) return true;
        if (TryRightVehicle(connection, state, fleet, known, arm)) return true;

        // docs/61 §5: one E/F press produces 09 15 PlayerSelect AND 09 07 InteractRequest 1-2 ms
        // apart with the same guid (logs/host-20260829-201025.log, three times out of three), and
        // this method is called from both arms and from 70 01 — so the seat arbitration ran twice
        // per press. Coalescing uses the client's own VehicleInteractionCooldownMs, which is also
        // what VehicleFleet.TryEnter gates on, so the two windows cannot drift apart.
        // FirstObservedSource is what finally answers docs/43 blocker 1, in one log line.
        VehicleEntryVerdict verdict = state.VehicleEntry.Classify(
            arm,
            vehicleGuid,
            fleet.TryGetForOccupant(state.Guid, out MatchVehicle? seated) && ReferenceEquals(seated, known),
            Environment.TickCount64,
            VehicleRosterData.Value.Constants.InteractionCooldownMs);
        if (!verdict.Proceed)
        {
            _log.Info($"{connection} vehicles: {source} on {known} — {verdict.Decision}; "
                + state.VehicleEntry.Describe());
            return true;
        }

        bool alreadySimulating = known.OwnerGuid == state.Guid || known.CoastingOwnerGuid == state.Guid;
        ulong formerSimulator = known.CoastingOwnerGuid;
        long enteredMs = Environment.TickCount64;
        VehicleActionResult result = fleet.TryEnter(
            vehicleGuid, state.Guid, seatIndex, enteredMs,
            out MatchVehicle? vehicle, out int seat);
        if (result != VehicleActionResult.Ok || vehicle is null)
        {
            // NOT recorded as this press's answer: the fleet refused and nothing went out, so a
            // retry a few hundred ms later (the seat frees up) must reach the fleet again rather
            // than be coalesced into a press that was never answered.
            _log.Info($"{connection} vehicles: {source} refused for {known} — {result}");
            return true;
        }

        // Seated. NOW the press is answered, so the other arm of the same E-press is a duplicate.
        state.VehicleEntry.NoteEntered(arm, vehicleGuid, enteredMs);
        ResetVehicleRiderPose(state, vehicle);
        StopReloadForVehicleEntry(connection, state);
        StopEmote(connection, state, includeSelf: true);

        bool driver = vehicle.Definition.Seats[seat].IsDriver;
        if (driver)
        {
            if (formerSimulator != 0 && formerSimulator != state.Guid)
                TransferVehicleSimulation(state, vehicle, formerSimulator);
            ApplyDriverVehicleSkin(connection, state, vehicle);
            // docs/43 §3: the owner guid alone decides who simulates the vehicle; there is no branch
            // on vehicle id anywhere on that path, which is why a car reaches the same client-side
            // physics the parachute already proved. The managed registration is what makes the
            // client's own channel-3 poses for it acceptable rather than "unowned".
            state.Movement.RegisterManagedEntity(vehicle.TransientId, vehicle.Guid);
            // Grant also calls the client's release virtual first. Regranting a coasting car
            // unnecessarily swaps back to its remote controller and can restore the spawn pose.
            if (!alreadySimulating)
            {
                // Retain the client's body transform. The movement position is a different
                // physics reference point; using it as an 11/23 body-origin reset lifts the
                // car on reentry. Grant clears the remote queue itself.
                SendTunnel(connection, writer =>
                    CharacterManagedObject.Grant(vehicle.Guid, state.Guid).WriteTo(writer));
            }
        }

        foreach (var resource in VehicleFullState.ResourcesFor(vehicle))
            SendTunnel(connection, CharacterResourceUpdate.Initial(vehicle.Guid, resource).WriteTo);
        SendTunnel(connection, new VehicleHealthUpdateOwner(state.Guid, vehicle.Health).WriteTo);
        IReadOnlyList<VehicleOccupantSlot> occupants = vehicle.Occupants();
        if (driver)
            SendTunnel(connection, writer => new VehicleOwnerState(
                vehicle.Guid, vehicle.OwnerGuid, vehicle.Definition.VehicleId, occupants).WriteTo(writer));
        // docs/43 §I.2 step 3: MountResponse carries the seat AND isDriver. The record defaults
        // IsDriver to 1 (the parachute is a one-seat driver-only mount), so leaving it out told
        // every passenger in seat 1+ that it was the driver.
        SendTunnel(connection, writer => new MountResponse(
            Rider: state.Guid,
            Mount: vehicle.Guid,
            Seat: (uint)seat,
            IsDriver: driver ? 1u : 0u).WriteTo(writer));
        SendTunnel(connection, writer => new VehicleOccupantState(
            vehicle.Guid,
            state.Guid,
            vehicle.Definition.VehicleId,
            vehicle.Definition.SeatCount,
            occupants).WriteTo(writer));
        // Mounting supplies the ability manager, but does not activate the motor.
        // August starts its client-run MotorRun ability on movement input. Forcing it
        // here plays an extra start and can fail native initialization (status 11).
        SendVehicleInventory(connection, state, vehicle);

        PublishVehicleOccupants(connection, state, vehicle);

        _log.Info($"{connection} vehicles: {source} → seated in {vehicle} seat {seat} "
            + $"({(driver ? "driver — managed object granted" : "passenger")}), "
            + $"fuel {vehicle.Fuel:F0}/{_options.VehicleFleet.MaxFuel:F0}"
            + $"; {state.VehicleEntry.Describe()}");
        return true;
    }

    /// <summary>
    /// docs/43 §I.2 hook 4: take the rider out. Unlike the parachute there is <b>no
    /// <c>0f 01 RemovePlayer</c></b> — the car stays in the world.
    /// </summary>
    private bool TryExitVehicle(SoeConnection connection, GatewaySessionState state, string source)
    {
        if (state.Fleet is not VehicleFleet fleet
            || !fleet.TryGetForOccupant(state.Guid, out MatchVehicle? seatedIn))
        {
            return false;
        }

        // The owner permits bailing out at full speed; client physics continues the coast.
        float exitSpeed = seatedIn.LastSpeed;
        bool wasDriver = seatedIn.DriverGuid == state.Guid;
        VehicleActionResult result = fleet.TryExit(
            state.Guid, Environment.TickCount64, exitSpeed, out MatchVehicle? vehicle, out int seat);
        if (result != VehicleActionResult.Ok || vehicle is null)
        {
            _log.Info($"{connection} vehicles: {source} refused ? {result}");
            return true;
        }

        CancelVehicleComponentRemoval(connection, state);
        ResetVehicleRiderPose(state, vehicle);
        // Owner policy, September 7: bailing out must not damage the rider. Arm before
        // the response can produce native exit/terrain contact reports, for any seat/speed.
        state.VehicleExitProtectedUntilMs = Environment.TickCount64 + VehicleExitCollisionGraceMs;
        state.PlayerCollision.Reset();

        // A rider who leaves is no longer boosting, and the client must be told before the car
        // stops being theirs — a resident turbo effect on a car they no longer own can never be
        // cleared.
        ReleaseBoost(connection, state, vehicle, $"the driver left ({source})");
        if (wasDriver) StopVehicleEngineForViewers(connection, state, vehicle);

        SendTunnel(connection, writer => new DismountResponse(state.Guid, vehicle.Guid).WriteTo(writer));
        SendTunnel(connection, writer => new VehicleOccupyCleared(state.Guid).WriteTo(writer));
        if (wasDriver)
        {
            SetVehicleHorn(connection, state, vehicle, false);
            SendTunnel(connection, writer =>
                new VehicleOwner(vehicle.Guid, OwnerGuid: 0, VehicleId: 0).WriteTo(writer));
            // Engine/effect shutdown was published to every viewer before dismount.
        }
        SendTunnel(connection, new EndCharacterAccess(vehicle.Guid).WriteTo);
        SendTunnel(connection, writer => writer.WriteRaw(vehicle.Inventory.AbilityManager(driver: false)));
        // docs/61 §5: getting straight back into the same car is a fresh press, not a duplicate.
        state.VehicleEntry.NoteExited();
        state.Ignition.Cancel(state.Guid);

        PublishVehicleOccupants(connection, state, vehicle);

        // A car already asleep when the driver exits may send no subsequent pose at all.
        if (wasDriver && state.Movement.TryGetManaged(vehicle.TransientId, out var parked)
            && parked.Movement is { Posture: uint stoppedPosture } stoppedMotion && (stoppedPosture & 0x40) != 0
            && MathF.Abs(stoppedMotion.HorizontalSpeed ?? 0) <= 0.2f
            && MathF.Abs(stoppedMotion.VerticalSpeed ?? 0) <= 0.02f)
        {
            vehicle.LastSpeed = 0;
            ReleaseVehicleSimulation(connection, state, vehicle);
        }

        _log.Info($"{connection} vehicles: {source} → left {vehicle} from seat {seat}; the car stays "
            + "in the world (no RemovePlayer)");
        return true;
    }

    /// <summary>
    /// docs/42 §10.2 step 6: a door press and a loot press are <b>the same two packets</b> — only the
    /// guid tells them apart — so the dispatcher asks the doors first and falls through to loot
    /// unchanged. Returns true when the request was a door and has been answered.
    /// <para>
    /// <b>Ground loot wins a tie, always.</b> <see cref="MatchDoors.TryResolve"/> falls back to
    /// resolving by world position within 6 m when the guid names nothing it minted, and a bandage
    /// lying in a doorway is well inside 6 m of the leaf. Loot pickup is the one thing in this file
    /// the owner has confirmed working live, so a guid that names ground loot never reaches the door
    /// resolver at all.
    /// </para>
    /// <para>
    /// <b>The guard must be <see cref="LootWorld.IsLootGuid"/>, not <c>TryGet</c>.</b> One press is
    /// two packets (<c>09 15</c> then <c>09 07</c> ~2 ms later, both carrying the same guid — see
    /// <c>logs/host-20260829-201025.log</c> 02.179/02.181). The first claims the item and
    /// <see cref="LootWorld.TryClaim"/> <i>removes</i> it, so a <c>TryGet</c> guard passes on the
    /// second packet, and <c>09 07</c> is the one that carries the float4 the positional fallback
    /// needs — so every indoor pickup would swing the nearest door within 6 m and swallow the
    /// fall-through. The range test still answers true for a just-claimed guid.
    /// </para>
    /// </summary>
    private bool TryToggleDoor(
        SoeConnection connection,
        GatewaySessionState state,
        ReadOnlySpan<byte> payload,
        string source)
    {
        if (!_options.SendDoors
            || state.Doors is not MatchDoors doors
            || doors.Count == 0
            || !DoorToggleRequest.TryParse(payload, out DoorToggleRequest? request))
        {
            return false;
        }

        if (state.Loot.IsLootGuid(request!.TargetGuid))
        {
            return false;
        }

        DoorToggleOutcome outcome = doors.TryToggle(request, Environment.TickCount64, out DoorInstance? door,
            WorldStreamCentre(state));
        switch (outcome)
        {
            case DoorToggleOutcome.Toggled:
            {
                DoorStateUpdate update = door!.StateUpdate();
                SendTunnel(connection, writer => update.WriteTo(writer));

                // docs/114 §3. The same 22 bytes to every OTHER session that has this door
                // spawned. Before this the send above was the whole answer, so a door opened by
                // one player stayed shut for everybody else and the second player's [F] then
                // closed a door that was, for him, already closed (AUDIT-doors §6). It is
                // addressable at all only because SharedMatchDoors mints one guid per door per
                // MATCH; with CRANBERRY_DOOR_SHARED=0 there is no shared set and this is zero.
                int alsoTold = BroadcastDoorState(state, door);

                // docs/79 §4 E10. The client's prompt driver FUN_14140bcd0 polls at most once a
                // second, so without this the [F] caption still reads "Open" on a door that is now
                // open, for up to a second after the press. This is the owner's Z1 ProactivePrompts
                // intent expressed for 1148: his mechanism — a 4.5 m proximity sweep — must NOT be
                // ported, because FUN_14129e7c0 drops any 09 2d reply whose guid is not the UI's
                // CURRENT interaction target (docs/47 §4d), so most of a sweep's packets would be
                // discarded. At the instant of a toggle the target is provably bound (the client
                // just named this guid to us, twice), so this one 19-byte reply is the only push in
                // the design that is guaranteed to land.
                InteractionStringReply prompt = InteractionStringReply.ForDoor(door);
                SendTunnel(connection, writer => prompt.WriteTo(writer));

                _log.Info($"{connection} doors: {source} {door} → {(door.IsOpen ? "OPEN" : "CLOSE")} "
                    + $"(0f 0a, {doors.TotalToggles} toggle(s) this match), prompt 09 2d string id "
                    + $"{prompt.StringId}"
                    + (alsoTold > 0
                        ? $", and the same 0f 0a fanned out to {alsoTold} other session(s) "
                            + $"({doors.Shared})"
                        : string.Empty)
                    + " — NOT live-verified (docs/79 §4 E10)");
                return true;
            }

            case DoorToggleOutcome.Absorbed:
                // The second packet of one press. Toggling here would open then immediately re-close.
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// docs/42 §10.2 step 4: stream the map's own door proxies around the player. A door is
    /// introduced with the very packets ground loot uses — <c>0xd6 AddLightweightNpc</c> with the one
    /// <c>+0x19c</c> door-id field non-zero, then <c>0xda LightweightToFullNpc</c>, then the
    /// <c>ea 04</c> component with one <c>InteractReplicationData</c> entry that binds <c>[F]</c>.
    /// </summary>
    private void SpawnNearbyDoors(
        SoeConnection connection,
        GatewaySessionState state,
        string trigger,
        List<Action> burst,
        Vector3? centreOverride = null)
    {
        if (!_options.SendDoors || _options.DoorRadius <= 0f || _options.DoorMaxPerBurst <= 0)
        {
            return;
        }

        // docs/48 §Integration step 6: this match's own drop, never the fixed option.
        // docs/114 §1: except in the LOBBY, where neither the drop nor MatchDropSpawn is where
        // the player is standing — the caller passes the staging spawn instead.
        Vector3 centre = centreOverride ?? WorldStreamCentre(state);

        var spawned = new List<DoorInstance>();
        MatchDoors doors;
        int matched;
        int leaving;
        try
        {
            // docs/114 §3: one construction site, in ZoneService.Doors.cs, because the lobby
            // burst needs exactly the same options record and it is also where the session joins
            // the match's shared door state.
            doors = EnsureMatchDoors(connection, state);
            matched = doors.RegisterNear(centre, _options.DoorRadius, _options.DoorMaxPerBurst, spawned);
            // docs/114 §4: and give back the ones that have left the band, which nothing used to
            // do — the live set grew monotonically for the whole match.
            leaving = DespawnDistantDoors(connection, state, doors, centre, burst);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Same rule as the loot burst: missing or hand-edited data must not take the match down.
            _log.Warn($"{connection} doors: dataset unavailable ({ex.GetType().Name}: {ex.Message}) "
                + "— no doors this match");
            return;
        }

        foreach (DoorInstance door in spawned)
        {
            DoorInstance planned = door;
            burst.Add(() => SendDoor(connection, state, planned));
        }

        _log.Info($"{connection} doors: {matched} within {_options.DoorRadius} m of "
            + $"{FormatPosition(centre)} ({trigger}), spawned {spawned.Count} (cap "
            + $"{_options.DoorMaxPerBurst}"
            // docs/114 §4: say WHICH cap ran. "48 of the disc" and "48 new" are different
            // numbers in a dense town and the log has to be able to tell the next report which.
            + (doors.Options.CapCountsNewDoorsOnly ? " new" : " of the disc")
            + $"), streamed out {leaving} past "
            + $"{doors.Options.EffectiveDespawnRadiusMetres(_options.DoorRadius):F0} m, "
            + $"{doors.Count} live, {doors.CollidableCount} kinematic"
            // docs/79 §4 E11: probe D6 failed only because 464 of Z2's doors have no kinematic twin.
            // An unnamed shortfall makes a play-test misleading — the owner walks into a camper door,
            // gets the wave-4 answer and reasonably concludes the fix failed. Under the physics flag
            // a camper SHOULD block anyway, so it is also the discriminator (docs/68 §5 F3 step 2).
            + DoorBurstReport.DescribeWalkThrough(doors)
            // docs/92 §4: name the families live in this burst. The owner's "some spawn open"
            // is a pose the client draws, not a bit we hold, so which FAMILY was open is the
            // one thing his next report must be able to say.
            + DoorBurstReport.DescribeFamilies(doors)
            + $", {doors.OpenCount} open, burst "
            + $"{doors.BurstCount}, rotation {DoorRotationPacking.ForWire(_options.DoorRotation)}"
            + $", collision {_options.DoorCollision}"
            + $", flags1 0x{_options.DoorSpawnFlags1:x2}"
            // docs/85 §5 E4: say which experiment ran, so the next session's log answers the
            // question without anyone re-parsing a capture. Still a SEND claim (D29).
            + ((_options.DoorSpawnFlags1 & LightweightEntityBody.CollidableFlag) != 0
                ? " (collidable bit SET)"
                : " (collidable bit CLEAR — docs/85 §2a)")
            + (_options.DoorSetCollidable ? ", + 0f 1e SetCollidable false/true pair" : string.Empty)
            + (_options.DoorPositionUpdateType != DoorCollision.CollidablePositionUpdateType
                ? $", positionUpdateType {_options.DoorPositionUpdateType} (NEGATIVE CONTROL — "
                    + "docs/85 §2e says non-zero RELEASES collision on this build)"
                : string.Empty)
            + $", press window {_options.DoorPressWindowMs} ms"
            + (DoorRotationPacking.IsProvenImpossible(_options.DoorRotation)
                ? $" (asked for {_options.DoorRotation}, which docs/47 §3b proves arithmetically "
                    + "impossible — substituted at the writer)"
                : string.Empty)
            + " — NOT live-verified (docs/47 §I5 step A)");
    }

    /// <summary>
    /// One door, in the order docs/42 §8 rules 1 and 3 make load-bearing. The <c>0xda</c> must follow
    /// its own <c>0xd6</c> and must keep its <c>+0x198</c> at zero, or the full-npc apply replaces the
    /// door controller at <c>entity+0xd10</c> with a movement rail.
    /// </summary>
    private void SendDoor(SoeConnection connection, GatewaySessionState state, DoorInstance door)
    {
        // docs/79 §3d: the order lives in DoorSpawnSequence rather than in this method's statement
        // order, because two of the four steps are load-bearing for reasons a reader of the sends
        // cannot see — above all that the 0xda is what takes the door OFF the client's lightweight
        // update throttle, which is the difference between a 785 ms sweep and a staircase.
        foreach (DoorSpawnStep step in DoorSpawnSequence.For(
            door.IsOpen,
            _options.SendInteractComponent,
            _options.DoorSetCollidable))
        {
            switch (step)
            {
                case DoorSpawnStep.Spawn:
                {
                    AddLightweightDoor spawn = door.Spawn(_options.DoorRotation);
                    SendTunnel(connection, writer => spawn.WriteTo(writer));
                    break;
                }

                case DoorSpawnStep.FullNpc:
                {
                    var full = new LightweightToFullNpc(door.TransientId, door.WorldGuid);
                    SendTunnel(connection, writer => full.WriteTo(writer));
                    state.FullNpcSent.Add(door.WorldGuid);
                    break;
                }

                case DoorSpawnStep.SetCollidable:
                {
                    // docs/85 §5 E5: a PAIR, and the false is not optional. FUN_140c75ef0 returns at
                    // its first branch when entity+0x37e6 bit 3 already holds the requested value,
                    // and the spawn record's collision bit has just set it — so a lone true is a
                    // no-op. Ported from the owner's ZoneDoors.SetCollidable (D53) with his lone-true
                    // send corrected.
                    var off = new SetCollidablePacket(door.WorldGuid, Collidable: false);
                    SendTunnel(connection, writer => off.WriteTo(writer));
                    var on = new SetCollidablePacket(door.WorldGuid, Collidable: true);
                    SendTunnel(connection, writer => on.WriteTo(writer));
                    break;
                }

                case DoorSpawnStep.InteractComponent:
                {
                    // Identical to the ground-item component: the one InteractReplicationData entry
                    // is what enrols the object in the client's interaction manager and binds [F]
                    // (docs/19 §4b). repId is unique per session because MatchDoors mints transient
                    // ids from 1,000,000 and LootWorld from 1,000.
                    // docs/114 §5: the range is the DOOR's, not the ground item's. The friend's
                    // server writes 2.0f in this field of this class hash and Cranberry wrote its
                    // own 3.0f; adopted under D53, with CRANBERRY_DOOR_RETAIL_INTERACT=0 as the
                    // revert. Ground loot is untouched — its reach is its own tuned number.
                    var component = CreateComponentWithRepData.ForGroundItem(
                        door.TransientId,
                        door.TransientId,
                        _options.DoorInteractRangeMetres);
                    SendTunnel(connection, writer => component.WriteTo(writer));
                    break;
                }

                default:
                {
                    // docs/42 §9 q3: the client's door controller constructs itself CLOSED, so a door
                    // this match has already opened has to be re-opened after the component.
                    DoorStateUpdate update = door.StateUpdate();
                    SendTunnel(connection, writer => update.WriteTo(writer));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// docs/13 §5 spawn step: register the object in this session's <see cref="LootWorld"/> and
    /// send the lightweight world record that renders it. The [F] prompt binding
    /// (<c>Replication.CreateComponent 0xea/04</c>) and the nearby-item list
    /// (<c>ProximateItems 0xf8/01</c>) are still blocked on their payload layouts (docs/13 §3b,
    /// §3c), so this sends the smallest-first spawn the doc's §9 step 2 asks to try.
    /// </summary>
    private GroundLootItem SpawnGroundLoot(
        SoeConnection connection,
        GatewaySessionState state,
        uint itemDefinitionId,
        uint groundModelId,
        Vector3 position,
        uint count = 1,
        uint nameId = 0,
        Vector4? rotation = null,
        uint skinRewardItemId = 0,
        int? magazineRounds = null)
    {
        GroundLootItem item = state.Loot.Spawn(itemDefinitionId, groundModelId, position, count, nameId,
            skinRewardItemId, magazineRounds);
        // D328: the item's base appearance group at +0x1a4, so a tintable gun mesh on the floor
        // composites its colourway instead of white. 0 when the table has no group for the item.
        uint groundShaderGroup = _options.GroundLootShader && _dynamicAppearance is AugustDynamicAppearanceTable groundTable
            ? groundTable.ShaderGroupForAnyBody(item.SkinRewardItemId != 0 ? item.SkinRewardItemId : itemDefinitionId)
            : 0;
        // These August ground ADR/DMAT files already contain their C/N/S textures and have no
        // paint mask. The held-weapon groups request absent Weapons_M9_PM / Weapons_Magnum_PM
        // textures (AssetFailure.log, September 8), replacing their working authored materials.
        if (groundModelId is 9423 or 9483) groundShaderGroup = 0;
        var spawn = new AddLightweightItem(
            item.WorldGuid,
            item.TransientId,
            item.GroundModelId,
            item.Position,
            item.NameId,
            rotation,
            groundShaderGroup,
            Collidable: AugustFuelFacts.IsRefuelItem(item.ItemDefinitionId));
        SendTunnel(connection, writer => spawn.WriteTo(writer));
        // docs/47 §6 RETIRES this deadline. docs/35 §3 row 7 armed a FullCharacterDataRequest
        // watchdog here on the reading that the client answers every 0xd6 with a 0F 45 ~150 ms
        // later. It cannot: the client's 0F 45 sender FUN_140c71600 requires the "full data pending"
        // bit entity+0x37ec & 0x40, and the 0xda applier FUN_140b02060 CLEARS it — and the 0xda goes
        // out in this same burst, three lines below, before the client's next tick. The request is
        // therefore never due and the warning fired in all four sessions of host-20260829-220829.log
        // for an object that was perfectly healthy. A permanently-false alarm masks the genuinely
        // broken spawn docs/35 built it to catch, so it is not armed for a spawn whose 0xda this
        // server sends proactively. Re-arm it only where the 0xda is deliberately withheld.
        // docs/19 §7 rule 1: the 0xda apply FUN_140b02060 requires the "full data pending" bit the
        // 0xd6 apply FUN_140af2900 set (entity+0x37ec |= 0x40) and matches the record to the object
        // by TRANSIENT id, not by guid — so this must follow its own 0xd6 and carry the same
        // transient id. Sending it proactively is the shape SendParachute already uses for d7 + db;
        // the client's own 0F 45 is then either never sent or answered a second time as a no-op.
        var full = new LightweightToFullNpc(item.TransientId, item.WorldGuid);
        SendTunnel(connection, writer => full.WriteTo(writer));
        state.FullNpcSent.Add(item.WorldGuid);
        // The native proximity datasource resolves the world GUID to ClientNpcComponent and
        // reads IsWorldItem. Missing/false means a grey, unclickable row despite a valid ItemAdd.
        // Promotion must precede this component so it cannot overwrite the loot state.
        var npc = CreateComponentWithRepData.ForWorldItemNpc(item.TransientId, item.NameId);
        SendTunnel(connection, writer => npc.WriteTo(writer));
        SendGroundInventoryItem(connection, state, item);

        if (_options.SendInteractComponent)
        {
            // docs/19 §4b — the actual [F] bug: an ea 04 with an EMPTY rep-data list creates the
            // component but never runs ClientInteractComponent's per-entry callback FUN_14146d0a0
            // → FUN_14146d230, so the object is never enrolled in the interaction manager
            // (DAT_143f696a0 + 0x322b8) and the prompt has no target. One InteractReplicationData
            // entry (0x50d51c9d, 11 bytes) is what makes that callback run. The owner transient id
            // must name an existing object ("… owner object (transientId=%u) doesn't exist!") and
            // repId must be unique per rep-data instance for the session — the transient id is both.
            var component = CreateComponentWithRepData.ForGroundItem(item.TransientId, item.TransientId);
            SendTunnel(connection, writer => component.WriteTo(writer));
        }

        _log.Info($"{connection} loot: spawned item {item.ItemDefinitionId} ×{item.Count} as world object "
            + $"{item.WorldGuid} (transient {item.TransientId}, model {item.GroundModelId}) at "
            + $"{FormatPosition(item.Position)} — AddLightweightNpc ({spawn.Length} B)"
            + $" + LightweightToFullNpc ({full.Length} B, guid at +{full.GuidOffset})"
            + " + CreateComponent(ClientNpcComponent, IsWorldItem=true)"
            + " + ItemAdd (ground object's inventory)"
            + $"{(_options.SendInteractComponent ? " + CreateComponent(ClientInteractComponent, 1 × InteractReplicationData)" : string.Empty)}"
            + $"; {state.Loot.Count} on the ground");
        return item;
    }

    /// <summary>
    /// docs/81 §7: puts the practice dummies in the world, once per match, at the player's own
    /// landing point. Off unless <c>CRANBERRY_PRACTICE_TARGET=1</c>. It exists because a single
    /// player has nothing to shoot: without a body in the world no <c>ProjectileHitReport</c> can
    /// name anything, so the whole fire-to-damage path is unverifiable by the one person who can
    /// play-test it.
    /// </summary>
    private void ArmPracticeTargets(SoeConnection connection, GatewaySessionState state)
    {
        if (!_options.Combat.PracticeTarget || state.Combat.TargetsArmed)
        {
            return;
        }

        Vector4 fallback = state.Drop != default ? state.Drop : _options.MatchDropSpawn;
        Vector3 origin = state.Movement.Player?.Position
            ?? new Vector3(fallback.X, fallback.Y, fallback.Z);

        IReadOnlyList<PracticeTargetSpawn> burst = PracticeTargetSpawner.Build(
            state.Combat.Targets, origin, state.Movement.Player?.Orientation ?? 0f, _options.Combat);

        if (burst.Count == 0)
        {
            return;
        }

        state.Combat.TargetsArmed = true;
        state.Score.PracticeSession = true;

        foreach (PracticeTargetSpawn spawn in burst)
        {
            SendPracticeTargetSpawn(connection, spawn);
            state.FullNpcSent.Add(spawn.Target.WorldGuid);
            _log.Info($"{connection} {spawn.Line}");
        }
    }

    /// <summary>
    /// The corpse lies where it fell for <c>PracticeTargetDespawnAfterDeathMs</c> and is then
    /// removed with <c>Character.RemovePlayer 0f 01</c>, effect flag 0 — the same eviction a
    /// streamed-out ground object gets, and the only removal packet Cranberry has proven.
    /// </summary>
    private void DespawnPracticeTargetLater(
        SoeConnection connection, GatewaySessionState state, PracticeTarget target)
    {
        int delay = (int)Math.Clamp(_options.Combat.PracticeTargetDespawnAfterDeathMs, 0, int.MaxValue);

        Later(connection, delay, () =>
        {
            if (!state.Combat.Targets.Remove(target))
            {
                return;
            }

            state.FullNpcSent.Remove(target.WorldGuid);
            SendTunnel(connection, writer => new RemovePlayer(target.WorldGuid).WriteTo(writer));
            _log.Info($"{connection} combat: practice target {target.WorldGuid} removed "
                + $"{_options.Combat.PracticeTargetDespawnAfterDeathMs} ms after it went down");
        });
    }

    /// <summary>
    /// docs/19 §1: promote one ground object out of the "full data pending" state the
    /// <c>AddLightweightNpc</c> apply left it in. <c>FUN_140b02060</c> looks the object up by
    /// transient id, refuses the record unless <c>+0x37ec &amp; 0x40</c> is still set and clears the
    /// bit, so the record is accepted exactly once; a repeat is a silent no-op, which is why
    /// answering the client's own request after the proactive send is harmless.
    /// </summary>
    private void SendLightweightToFullNpc(
        SoeConnection connection,
        GatewaySessionState state,
        GroundLootItem item,
        string trigger)
    {
        var full = new LightweightToFullNpc(item.TransientId, item.WorldGuid);
        SendTunnel(connection, writer => full.WriteTo(writer));
        bool first = state.FullNpcSent.Add(item.WorldGuid);
        SendGroundInventoryItem(connection, state, item);
        _log.Info($"{connection} loot: {trigger} → LightweightToFullNpc for world object {item.WorldGuid} "
            + $"(transient {item.TransientId}, {full.Length} B, guid at +{full.GuidOffset})"
            + $"{(first ? string.Empty : " — repeat of the proactive send, the client's apply drops it")}");
    }

    private void SendGroundInventoryItem(SoeConnection connection, GatewaySessionState state, GroundLootItem item)
    {
        // f8 populates the proximity UI only. MoveItem2 also looks for this item in its
        // owning entity's inventory (FUN_140d24980, entity+0xaf8). Remote ItemAdd installs
        // that entry through FUN_140c50ba0 -> FUN_140c35400 before a click can dispatch.
        InventoryItem record = ToProximateItem(item).Item;
        WeaponItemAddTail? tail = state.Weapons.CreateTail(item.ItemDefinitionId, magazine: item.MagazineRounds ?? 0);
        if (tail is null)
            SendTunnel(connection, new ItemAdd(item.WorldGuid, record).WriteTo);
        else
        {
            // Remote CreateItem resolves the same weapon definitions as the local inventory.
            // A development or streamed drop can arrive before EnsureInventory has run.
            SendWeaponDefinitionsOnce(connection, state);
            SendTunnel(connection, new WeaponItemAdd(item.WorldGuid, record, tail).WriteTo);
        }
    }

    /// <summary>
    /// How many bytes the <c>ItemAdd</c> for <paramref name="record"/> will be, for the log line
    /// only. <see cref="WeaponSession.CreateTail"/> is pure, so asking twice records nothing twice —
    /// the ledger is written by <see cref="WeaponSession.CreateItemAdd"/> and by nothing else.
    /// </summary>
    private static int ItemAddLength(GatewaySessionState state, InventoryItem record) =>
        state.Weapons.CreateTail(record.DefinitionId) is WeaponItemAddTail tail
            ? new WeaponItemAdd(state.Guid, record, tail).Length
            : new ItemAdd(state.Guid, record).Length;

    /// <summary>
    /// docs/63 §2 capability 7: one inventory action — parse, resolve against the model, and send
    /// the packets the outcome names. Every writer used here is already proven: <c>ItemDelete</c>
    /// (11 04), <c>ItemAdd</c> (11 02), <c>SetLoadoutSlots</c> (86 04), <c>UpdateContainer</c>
    /// (c8 06), <c>Container.Error</c> (c8 03) and the ground-spawn burst every looted world object
    /// already uses.
    /// <para>
    /// The REQUEST is client-originated and therefore LIVE-VERIFIED in the D29 sense; the REPLY is
    /// not — nothing here has been seen to land.
    /// </para>
    /// </summary>
    private void HandleItemUseRequest(
        SoeConnection connection,
        GatewaySessionState state,
        byte[] payload)
    {
        HandleItemUseAction(connection, state, RequestUseItem.Parse(payload));
    }

    private void HandleItemUseAction(
        SoeConnection connection, GatewaySessionState state, RequestUseItem request)
    {
        if (request.Kind == ItemUseOptionKind.SkinItem)
            state.InventorySkinTargetItemGuid = 0;
        if (state.DeathSent && request.Kind is (ItemUseOptionKind.SkinItem or ItemUseOptionKind.HoodieUp or ItemUseOptionKind.HoodieDown))
        {
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
            return;
        }
        if (state.Inventory is not PlayerInventory inventory)
        {
            _log.Info($"{connection} inventory: {request.Kind} for item {request.ItemGuid} arrived "
                + "before the container bootstrap — ignored");
            return;
        }

        if (HandleProximityShred(connection, state, inventory, request)) return;
        if (HandleBodyBagItemUse(connection, state, request)) return;
        if (HandleVehicleItemUse(connection, state, request)) return;

        // A Proximity-panel click refers to an item that is still owned by the ground world, not
        // by this PlayerInventory. Route it before the carried-item resolver performs its ownership
        // check, otherwise every legitimate LootItem click is misreported as
        // SlotDoesNotContainItem and can never reach the pickup path.
        if (request.Kind == ItemUseOptionKind.LootItem)
        {
            HandleProximityLootItem(connection, state, inventory, request);
            return;
        }

        // PLAN BEFORE CLAIM, the rule the pickup path already follows: nothing leaves the inventory
        // until the ground object it becomes is known to be placeable.
        // docs/102 §6: the session's magazines are what turn UnloadWeapon from D118's named refusal
        // into a real unload. Passing them is the whole of the wiring — with CRANBERRY_AMMO_FROM_BAG
        // on, every round in a magazine got there out of this same bag, so putting it back is
        // conservative by construction. A caller with no magazines still gets D118's refusal.
        // CRANBERRY_AMMO_FROM_BAG=0 is D118's world exactly - Reload refills the magazine out of
        // nothing - so unload/reload/unload would mint ammunition and the magazines are withheld,
        // which restores the named refusal verbatim (AmmoOptions.AmmoFromBag).
        if (request.Kind == ItemUseOptionKind.UnloadWeapon
            && WeaponFireArm.CancelReload(state.Combat, request.ItemGuid, "unload requested") is { } stopped
            && stopped.Reply is { } reloadReply)
        {
            SendTunnel(connection, writer => writer.WriteRaw(reloadReply));
        }

        ItemActionResult plan = InventoryActions.Resolve(
            inventory,
            request,
            _options.Combat.Ammo.AmmoFromBag ? state.Combat.Shooter : null);

        ApplyInventoryAction(connection, state, inventory, plan);
    }

    private void ApplyInventoryAction(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        ItemActionResult plan)
    {
        // WAVE 9. The item that has to reach the floor is the dropped one - or, on an equip whose
        // displaced occupant will not fit in the bag, that occupant (docs/86 edit 8).
        uint toGround = plan.Kind == ItemActionKind.Drop ? plan.DefinitionId
            : plan.DisplacedToGround ? plan.DisplacedDefinitionId
            : 0;
        ulong droppedGuid = plan.Kind == ItemActionKind.Drop ? plan.ItemGuid : plan.DisplacedItemGuid;
        uint droppedSkin = inventory.Items.TryGetValue(droppedGuid, out var droppedInstance)
            ? droppedInstance.DisplayDefinitionId : toGround;
        uint groundModelId = 0;
        uint nameId = 0;
        GroundActorSource groundSource = default;
        if (toGround != 0
            && !TryResolveGroundActor(state, inventory, toGround, out groundModelId, out nameId, out groundSource))
        {
            // Only reachable with InventoryOptions.UniversalGroundActor off, or when the catalogue
            // itself failed to load. WAVE 9 replaced the blanket refusal this used to be: the owner
            // was told "this build has no ground actor for it" three times in five minutes for two
            // SurvivorStarterOutfit garments, and docs/63 §5.4's "in practice that is the four
            // starter garments and nothing else" was wrong by a factor of two and a half - see
            // DroppedItemCatalogue and docs/86 §2.1.
            SendTunnel(connection, writer =>
                new ContainerError(state.Guid, ContainerErrorCode.WrongItemType).WriteTo(writer));
            _log.Info($"{connection} inventory: {plan.Option} refused for item {toGround} — no "
                + "ground actor, and InventoryOptions.UniversalGroundActor is off");
            return;
        }

        if (plan.Kind == ItemActionKind.Refused)
        {
            SendTunnel(connection, writer => new ContainerError(state.Guid, plan.Error).WriteTo(writer));
            _log.Info($"{connection} inventory: {plan.Option} REFUSED — {plan.Rule} "
                + $"(Container.Error {plan.Error})");
            return;
        }

        if (plan.Kind == ItemActionKind.NotImplemented)
        {
            // Named, not hex. Nothing is sent: an unanswered request leaves the item exactly where
            // the client already draws it. WAVE 9: only a switched-off verb reaches this now - every
            // real ItemUseOptions row resolves either to an action or to a NAMED refusal above.
            _log.Info($"{connection} inventory: {plan.Rule} — no packet sent (docs/63 §5)");
            return;
        }

        // WAVE 9, docs/86 §3.2 and edit 6. Crafting\ShredTable.Shred has been complete and green
        // since wave 6; its own header recorded the blocker as "the packet the client sends when a
        // player right-clicks Shred", and docs/63 §1 had already proved from 21 client-originated
        // packets that it is this one, ac 2c with ITEM_USE_OPTION_ID 6 or 63. The owner shredded
        // four times on 30 Aug and got silence from code that was already written.
        if (plan.Kind == ItemActionKind.Shred)
        {
            RunShred(connection, state, inventory, plan);
            return;
        }

        // D341: a medical runs on a cast bar and heals afterwards; the removal is applied at the
        // end of the bar, not here.
        if (plan.Kind == ItemActionKind.Consume)
        {
            RunConsume(connection, state, inventory, plan);
            return;
        }

        if (plan.Kind == ItemActionKind.Unload)
        {
            RunUnload(connection, state, inventory, plan);
            return;
        }

        if (plan.Kind == ItemActionKind.Skin)
        {
            state.InventorySkinTargetItemGuid = plan.ItemGuid;
            state.InventorySkinTargetExpiresAtMs = Environment.TickCount64 + 5000;
            return;
        }

        if (plan.Kind == ItemActionKind.Hood)
        {
            InventoryActions.Apply(inventory, plan);
            SendCharacterAppearance(connection, state, $"{plan.Option} {plan.DefinitionId}");
            _log.Info($"{connection} inventory: {plan.Rule}; hood is now "
                + (inventory.HoodUp ? "up" : "down"));
            return;
        }

        bool containerSetChanged =
            (plan.ClearedLoadoutSlotId != 0
                || plan.BoundLoadoutSlotId != 0
                || plan.DisplacedItemGuid != 0)
            && (PlayerInventory.ProvidesContainer(plan.DefinitionId)
                || PlayerInventory.ProvidesContainer(plan.DisplacedDefinitionId));

        // Preserve rounds before item removal forgets combat identity. A dropped reload
        // cannot later spend reserve or refill the now unowned instance.
        int? droppedMagazine = null;
        if (toGround != 0)
        {
            StopReloadForGroundDrop(connection, state, droppedGuid);
            droppedMagazine = CaptureGroundWeaponMagazine(state, droppedGuid, toGround);
        }
        InventoryActions.Apply(inventory, plan);

        // 1. The item collection first. The panel resolves (definitionId, slotId) against it
        //    (docs/46 §3), so removing the item from it is what empties both the box and the cell; a
        //    partial stack is a re-sent ItemAdd for the SAME guid, which FUN_140c35400 applies as a
        //    change rather than an add (docs/46 §6a).
        bool moved = plan.Kind is ItemActionKind.Equip or ItemActionKind.Unequip;
        if (moved || plan.Kind == ItemActionKind.Repaint)
        {
            // WAVE 9. The tile MOVED rather than vanished, so it must be re-announced where it is
            // now. Delete-then-add is his own order (ZoneInventoryActions.TryEquip / .TryUnequip
            // both send ItemDeleteFor then ItemAddCarried/ItemAddEquipped); a Repaint sends only the
            // add, because nothing about the record changed and FUN_140c35400 treats a re-sent
            // ItemAdd for a live guid as a change rather than an add (docs/46 §6a).
            if (moved && !plan.SwapLoadoutSlots)
            {
                ulong same = plan.ItemGuid;
                SendTunnel(connection, writer => new ItemDelete(state.Guid, same).WriteTo(writer));
            }

            if (inventory.Items.TryGetValue(plan.ItemGuid, out InventoryItemInstance? settled))
            {
                InventoryItem row = settled.ToRecord(state.Guid);
                SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, row));
                SyncDrawnWeaponReloadCounter(connection, state, settled);
            }
        }
        else if (plan.RemainingCount == 0)
        {
            ulong gone = plan.ItemGuid;
            // The instance no longer exists, so its active-hand clearance must go with it. Inert
            // while stage 3 is off (nothing consults the ledger), and today LootWorld's item guids
            // are monotonic so a stale entry could never be inherited by a different item — but the
            // narrowed guard 5 should not rest on a monotonic counter it does not own.
            state.Weapons.Ledger.Forget(gone);
            state.Combat.Shooter.ForgetWeapon(gone);
            SendTunnel(connection, writer => new ItemDelete(state.Guid, gone).WriteTo(writer));
        }
        else
        {
            InventoryItem updated = inventory.Items[plan.ItemGuid].ToRecord(state.Guid);
            SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, updated));
        }

        // The item an equip pushed out of the slot it took. Into the bag it is a re-announce; onto
        // the ground it is a delete here and a world object below (InventoryOptions.
        // SwapNeverRefusedForBulk - the owner's helmet swap, host log 20:38:30.900).
        if (plan.DisplacedItemGuid != 0)
        {
            if (plan.DisplacedToGround)
            {
                ulong evicted = plan.DisplacedItemGuid;
                state.Weapons.Ledger.Forget(evicted);
                state.Combat.Shooter.ForgetWeapon(evicted);
                SendTunnel(connection, writer => new ItemDelete(state.Guid, evicted).WriteTo(writer));
            }
            else if (inventory.Items.TryGetValue(
                plan.DisplacedItemGuid, out InventoryItemInstance? bagged))
            {
                InventoryItem row = bagged.ToRecord(state.Guid);
                if (!plan.SwapLoadoutSlots)
                    SendTunnel(connection, writer => new ItemDelete(state.Guid, row.ItemGuid).WriteTo(writer));
                SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, row));
            }
        }

        // 2. The loadout, when a bound slot was vacated. The whole list, not 86/05: a SetLoadoutSlot
        //    carrying ItemDefinitionId 0 is inert at the client's own gate (docs/46 §3a), so there is
        //    no "clear one slot" packet — the rebuilt list IS the clear.
        if (plan.ClearedLoadoutSlotId != 0 || plan.BoundLoadoutSlotId != 0 || plan.DisplacedItemGuid != 0)
        {
            SendLoadoutSlots(connection, inventory);
        }

        // 3. Container state. c8/06 can update an existing record but cannot create or remove one,
        // so an equipped container entering/leaving the loadout needs the destructive full list.
        // The 86/04 above has already installed the new loadout, letting c8/02 re-index against it.
        if (containerSetChanged)
        {
            SendTunnel(connection, writer => inventory.ToInitContainers().WriteTo(writer));
        }
        else if (inventory.BaseBag is InventoryContainer bag)
        {
            SendTunnel(connection, writer => inventory.ToUpdate(bag).WriteTo(writer));
        }

        // 4. Re-dress when a BODY slot was vacated. Regression guard 5: ClearedEquipmentSlotId can
        //    never be 7 — ADropNeverVacatesTheActiveHand pins it, and this path never binds one.
        if (plan.ClearedEquipmentSlotId != 0 || plan.BoundEquipmentSlotId != 0)
        {
            // 94 01, the full attachment list rebuilt from inventory.EquipmentSlots - never 94 03
            // UnsetCharacterEquipmentSlot, which regression guard 2 forbids and which no Cranberry
            // path sends. BoundEquipmentSlotId can never be 7 (ItemVerbs drops the active hand to
            // 0), so guard 5 holds across the new verbs too.
            SendCharacterAppearance(connection, state, $"{plan.Option} {plan.DefinitionId}");
        }

        // 5. And put it on the floor, at the player's feet, with the burst every looted object uses.
        //    It becomes an ordinary ground object from that moment — it answers InteractRequest and
        //    it appears in ProximateItems. What it must NOT do is spend a spawn MARKER: a dropped
        //    item never had one (docs/63 §I3).
        //
        //    It must still be ADOPTED into the streaming working set, and that is the wave-6 verify
        //    fix. MatchLoot's evictions come only from its own key-indexed working set, which is
        //    populated solely by NoteSpawned, so an unadopted drop was permanently unevictable: it
        //    lived in LootWorld (which has no cap) and in nothing else, never counted against
        //    LootStreamOptions.MaxLive, never destroyed client-side, and added 74 B to EVERY later
        //    ProximateItems republish. Twenty-five routine drops turned a 9,479 B panel rebuild into
        //    11,329 B forever. LootStreamKeyKind.Dropped is a key space of its own, minted from the
        //    world guid LootWorld just handed out (monotonic, never reused), so the object is
        //    evicted by the ordinary distance rule and re-offered by nothing.
        if (toGround != 0)
        {
            Vector4 fallback = state.Drop != default ? state.Drop : _options.SpawnPosition;
            Vector3 where = state.Movement.Player?.Position
                ?? new Vector3(fallback.X, fallback.Y, fallback.Z);

            // D259 / audit G7 — the dropped object faces the way the player is facing. The captured
            // drop's own d7 carries the four floats (0, -3.0808, 0, 1) at +0xa0
            // (packets_1119_53544.log:5290's neighbour, cPacketIdAddLightweightNpc 123.016 s), and
            // -3.0808 rad is a yaw: it sits in component Y with w left at 1, which is not a
            // normalised quaternion. That settles the Euler-vs-quaternion question docs/33 §6c left
            // open FOR THIS FIELD, and only for it - the world-seeded markers keep their identity
            // rotation, because a marker's yaw was never observed on the wire. Off restores
            // (0, 0, 0, 1), which is the identity in either reading.
            Vector4? facing = _options.Inventory.DropCarriesFacing
                && state.Movement.Player?.Orientation is float yaw
                ? new Vector4(0f, yaw, 0f, 1f)
                : null;
            uint droppedCount = plan.Kind == ItemActionKind.Drop ? plan.Count : plan.DisplacedCount;
            GroundLootItem dropped = SpawnGroundLoot(
                connection, state, toGround, groundModelId, where,
                count: droppedCount,
                nameId: nameId,
                rotation: facing,
                skinRewardItemId: droppedSkin,
                magazineRounds: droppedMagazine);
            SharePlayerDrop(connection, state, dropped);
            if (_options.SendProximateItems)
            {
                // Same shape as the pickup path (docs/13 §9 step 5), which has answered inline in
                // every live session since wave 2. With the adoption above, the row count this
                // rebuilds is bounded by MaxLive again.
                SendProximateItems(connection, state);
            }

            // D259 / audit G4 — LAST in the chain, exactly where the friend's server puts it
            // (packets_1119_53544.log:5288 and :5553, both after the whole item/loadout/spawn
            // burst). Without it a drop looks like an item vanishing. Only a real DropItem raises
            // the toast: an equip whose displaced occupant overflowed to the floor is not something
            // the player dropped, and telling them they dropped it would be a lie.
            if (plan.Kind == ItemActionKind.Drop && _options.Inventory.SendDroppedItemNotification)
            {
                SendTunnel(connection, writer =>
                    new DroppedItemNotification(state.Guid, toGround, droppedCount).WriteTo(writer));
            }
        }

        (int used, int max) = inventory.Capacity;
        _log.Info($"{connection} inventory: {plan.Rule}; bulk {used}/{max}"
            + (toGround == 0 ? string.Empty : $" — ground model {groundModelId} ({groundSource})")
            + " — NOT live-verified that the client accepted the reply, though the REQUEST is "
            + "client-originated (docs/63 §1)");
    }

    /// <summary>
    /// docs/102 §6 — one <c>UnloadWeapon</c>, the closing of D118. <c>ItemVerbs.Unload</c> has
    /// already emptied the magazine and put the rounds in the bag (it is the one verb whose Resolve
    /// mutates, because the grant and the emptying are one operation or neither), so this only puts
    /// the outcome on the wire: <b>two</b> items and the container.
    /// <list type="number">
    /// <item>the weapon, <c>11 02 ItemAdd</c> for its own guid — <c>FUN_140c35400</c> treats a
    /// re-sent add for a live guid as a change (docs/46 §6a), and it is what makes the gun's tile
    /// stop claiming a loaded magazine;</item>
    /// <item>the ammunition, <c>11 02</c> when the stack is new and <c>11 03 ItemUpdate</c> when it
    /// merged into one the client already holds — an update for an unknown guid is a no-op in
    /// <c>FUN_140dbfcb0</c>, so a genuinely new stack has to be an add (the same rule
    /// <c>PlayerAmmoContext.Announce</c> follows);</item>
    /// <item>the bag, <c>c8 06 UpdateContainer</c>.</item>
    /// </list>
    /// <para>
    /// Nothing goes to the ground and no loadout binding moves, so none of the drop path applies.
    /// </para>
    /// </summary>
    private void RunUnload(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        ItemActionResult plan)
    {
        if (inventory.Items.TryGetValue(plan.ItemGuid, out InventoryItemInstance? weapon))
        {
            SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, weapon.ToRecord(state.Guid)));
            SyncDrawnWeaponReloadCounter(connection, state, weapon);
        }

        bool sentUpdate = false;
        if (plan.DisplacedItemGuid != 0
            && inventory.Items.TryGetValue(plan.DisplacedItemGuid, out InventoryItemInstance? stack))
        {
            if (plan.DisplacedCreated)
            {
                SendTunnel(connection, writer =>
                    new ItemAdd(state.Guid, stack.ToRecord(state.Guid)).WriteTo(writer));
            }
            else if (_options.Combat.Ammo.SendItemUpdate)
            {
                SendTunnel(connection, writer =>
                    ItemUpdate.ForStack(state.Guid, stack).WriteTo(writer));
                sentUpdate = true;
            }
        }

        if (inventory.BaseBag is InventoryContainer bag)
        {
            SendTunnel(connection, writer => inventory.ToUpdate(bag).WriteTo(writer));
        }

        (int used, int max) = inventory.Capacity;
        _log.Info($"{connection} inventory: {plan.Rule}; bulk {used}/{max} — announced the weapon "
            + $"(11 02) and the stack ({(plan.DisplacedCreated ? "11 02, new" : sentUpdate
                ? "11 03 ItemUpdate" : "not announced, CRANBERRY_ITEM_UPDATE=0")})");
    }

    /// <summary>
    /// One shred, on the client's own <c>BUSY_MSEC</c> clock (docs/86 edit 6). Everything it sends
    /// is <see cref="ApplyCraft"/>'s, which was already written: a shred is a craft whose ingredient
    /// is the item itself.
    /// <para>
    /// A second shred inside the busy window is refused rather than queued - the client has locked
    /// the character out for that long, so a request arriving inside it did not come from the menu.
    /// </para>
    /// </summary>
    private void RunShred(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        ItemActionResult plan)
    {
        if (RefuseInteractionDuringLogout(connection, state)) return;
        long now = Environment.TickCount64;
        if (state.PendingVehicleRemoval is not null || state.ShredBusyUntil > now)
        {
            SendTunnel(connection, writer =>
                new ContainerError(state.Guid, ContainerErrorCode.ContainerInUse).WriteTo(writer));
            _log.Info($"{connection} inventory: {plan.Option} REFUSED — a shred is already running "
                + $"for another {state.ShredBusyUntil - now} ms (ItemUseOptions BUSY_MSEC "
                + $"{plan.BusyMilliseconds})");
            return;
        }

        int duration = Math.Max(plan.BusyMilliseconds, 0);
        state.ShredBusyUntil = now + duration;
        int interactionGeneration = state.InteractionGeneration;
        bool armed = Later(connection, duration, () =>
        {
            if (state.InteractionGeneration != interactionGeneration
                || !ReferenceEquals(inventory, state.Inventory)) return;
            // The item can be moved during the cast by a forged/late request. ShredTable performs
            // ownership and placement validation again at completion and leaves it untouched on a
            // refusal, exactly as the reference does.
            CraftOutcome outcome = ShredTable.Shred(inventory, plan.ItemGuid);
            state.ShredBusyUntil = 0;
            ApplyCraft(
                connection,
                state,
                inventory,
                outcome,
                forceContainerRedeclaration: outcome.ClearedLoadoutSlotId != 0
                    && PlayerInventory.ProvidesContainer(plan.DefinitionId));

            if (outcome.Succeeded && outcome.ClearedLoadoutSlotId != 0)
            {
                SendCharacterAppearance(connection, state, $"shredded {plan.DefinitionId}");
            }

            // D258 / audit G3. The bar ran for the option's own BUSY_MSEC (1,000 ms on rows 6 and
            // 63) but animation 10 is authored to run 2,000 (InteractionAnimations row 10:
            // Action / ActionEnd / EXPIRE_MSEC 2000), so without this the character keeps shredding
            // for a second after the item has already changed. Sent whether or not the shred
            // succeeded: the bar is on screen either way and something has to take it off.
            int stops = SendInteractionStops(connection, state);
            _log.Info($"{connection} inventory: {plan.Rule} — timer completed"
                + (outcome.Succeeded ? string.Empty : " (nothing was consumed)")
                + (stops == 0 ? string.Empty : $", cf/03 InteractionStop ×{stops}"));
        });

        if (!armed)
        {
            state.ShredBusyUntil = 0;
            SendTunnel(connection, writer => new ContainerError(
                state.Guid, ContainerErrorCode.ContainerInUse).WriteTo(writer));
            return;
        }

        InventoryItemFacts.TryGet(plan.DefinitionId, out InventoryItemFact fact);
        SendTunnel(connection, writer => new InteractionStart(
            state.Guid,
            duration,
            fact.NameId,
            plan.InteractionAnimationId).WriteTo(writer));
        _log.Info($"{connection} inventory: {plan.Rule} — InteractionStart cf/02, "
            + $"{duration} ms, string {fact.NameId}, animation {plan.InteractionAnimationId}");
    }

    /// <summary>
    /// <b>D341 — a medical on a cast bar, then the heal.</b> Retail (capture P:L7539-L8010, Z1
    /// <c>ZoneMedical.cs</c>): <c>cf 02 InteractionStart</c> for the option's <c>BUSY_MSEC</c> with
    /// animation 18, then at the bar's end one unit leaves the stack (<c>11 03</c> / <c>11 04</c>),
    /// the heal starts, and <c>cf 03</c> ×2 take the bar off. The heal is
    /// <see cref="MedicalModel"/>'s row - Field Bandage 10 HP over 10 s, kit 60 over 60 - ticked at
    /// 1 Hz through the same <c>11 01</c> health bar the gas and every damage cause use. A second
    /// consume inside the window is refused like a second shred. A bleed HUD is still owed
    /// (<see cref="MedicalModel"/>'s stated subtraction).
    /// </summary>
    private void RunConsume(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        ItemActionResult plan)
    {
        if (RefuseInteractionDuringLogout(connection, state)) return;
        long now = Environment.TickCount64;
        if (state.PendingMedicalCast is { } previous
            && (previous.WorldGeneration != state.WorldGeneration || !ReferenceEquals(previous.Inventory, state.Inventory)))
            CancelMedicalCast(connection, state, "world or inventory changed", notify: false);
        if (state.PendingVehicleRemoval is not null || state.PendingMedicalCast is not null || state.ConsumeBusyUntil > now || state.ShredBusyUntil > now)
        {
            SendTunnel(connection, writer =>
                new ContainerError(state.Guid, ContainerErrorCode.ContainerInUse).WriteTo(writer));
            _log.Info($"{connection} inventory: {plan.Option} REFUSED — a cast is already running "
                + $"for another {Math.Max(state.ConsumeBusyUntil, state.ShredBusyUntil) - now} ms");
            return;
        }

        if (state.Hitpoints == 0 || state.DeathSent)
        {
            _log.Info($"{connection} inventory: {plan.Option} REFUSED — the player is dead");
            return;
        }

        Medical? medical = MedicalModel.For(plan.DefinitionId);
        MatchVehicle? refuelTarget = null;
        MatchVehicle? fuelSource = null;
        if (AugustFuelFacts.IsRefuelItem(plan.DefinitionId))
        {
            refuelTarget = FindRefuelVehicle(state, plan.TargetCharacterGuid);
            if (refuelTarget is null)
            {
                SendTunnel(connection, writer => new ContainerError(
                    state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo(writer));
                return;
            }
            if (refuelTarget.Inventory.TryGet(plan.ItemGuid, out var cargo)
                && cargo?.DefinitionId == plan.DefinitionId)
                fuelSource = refuelTarget;
        }
        int duration = Math.Max(plan.BusyMilliseconds, 0);
        var cast = new PendingMedicalCast(inventory, state.WorldGeneration, MedicalCastPosition(state));
        state.PendingMedicalCast = cast;
        state.ConsumeBusyUntil = now + duration;
        bool armed = Later(connection, duration, () =>
        {
            if (!ReferenceEquals(state.PendingMedicalCast, cast)) return;
            if (cast.WorldGeneration != state.WorldGeneration || !ReferenceEquals(inventory, state.Inventory))
            {
                CancelMedicalCast(connection, state, "world or inventory no longer active", notify: false);
                return;
            }
            if (state.Hitpoints == 0 || state.DeathSent)
            {
                CancelMedicalCast(connection, state, "player is dead");
                return;
            }

            CancelMedicalCastIfMoved(connection, state, MedicalCastPosition(state));
            if (!ReferenceEquals(state.PendingMedicalCast, cast)) return;
            state.PendingMedicalCast = null;
            state.ConsumeBusyUntil = 0;
            if (fuelSource is not null)
            {
                if (CanRefuelVehicle(state, fuelSource)
                    && fuelSource.Inventory.ConsumeFuelCan(plan.ItemGuid, out var remaining))
                {
                    RefuelWithCan(connection, state, fuelSource);
                    PublishConsumedVehicleFuel(connection, state, fuelSource, plan.ItemGuid, remaining);
                }
                SendInteractionStops(connection, state);
                return;
            }
            if (!inventory.Items.TryGetValue(plan.ItemGuid, out InventoryItemInstance? still)
                || still.DefinitionId != plan.DefinitionId || still.Count < plan.Count)
            {
                SendInteractionStops(connection, state);
                _log.Info($"{connection} inventory: {plan.Rule} — timer completed but the item is "
                    + "gone (moved or dropped during the cast); nothing consumed");
                return;
            }

            if (refuelTarget is not null && !CanRefuelVehicle(state, refuelTarget))
            {
                SendInteractionStops(connection, state);
                SendTunnel(connection, writer => new ContainerError(
                    state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo(writer));
                return;
            }

            uint remainingCount = still.Count - plan.Count;
            uint consumedLoadoutSlot = still.LoadoutSlotId;
            InventoryActions.Apply(inventory, plan);
            if (refuelTarget is not null)
                RefuelWithCan(connection, state, refuelTarget);
            if (remainingCount == 0)
            {
                ulong gone = plan.ItemGuid;
                state.Weapons.Ledger.Forget(gone);
                state.Combat.Shooter.ForgetWeapon(gone);
                SendTunnel(connection, writer => new ItemDelete(state.Guid, gone).WriteTo(writer));
            }
            else
            {
                InventoryItem updated = inventory.Items[plan.ItemGuid].ToRecord(state.Guid);
                SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, updated));
            }

            if (remainingCount == 0 && consumedLoadoutSlot != 0)
                SendLoadoutSlots(connection, inventory);
            if (inventory.BaseBag is InventoryContainer medicalBag)
                SendTunnel(connection, inventory.ToUpdate(medicalBag).WriteTo);

            int stops = SendInteractionStops(connection, state);
            _log.Info($"{connection} inventory: {plan.Rule} — timer completed, one unit consumed"
                + (remainingCount == 0 ? " (11 04, the instance is gone)" : $" (11 02, {remainingCount} left)")
                + (stops == 0 ? string.Empty : $", cf/03 InteractionStop ×{stops}"));

            if (medical is { } row)
            {
                BeginHeal(connection, state, row);
            }
        });

        if (!armed)
        {
            state.PendingMedicalCast = null;
            state.ConsumeBusyUntil = 0;
            SendTunnel(connection, writer => new ContainerError(
                state.Guid, ContainerErrorCode.ContainerInUse).WriteTo(writer));
            return;
        }

        InventoryItemFacts.TryGet(plan.DefinitionId, out InventoryItemFact fact);
        SendTunnel(connection, writer => new InteractionStart(
            state.Guid,
            duration,
            fact.NameId,
            plan.InteractionAnimationId).WriteTo(writer));
        _log.Info($"{connection} inventory: {plan.Rule} — InteractionStart cf/02, "
            + $"{duration} ms, string {fact.NameId}, animation {plan.InteractionAnimationId}");
    }

    /// <summary>
    /// D341: the heal ticks. <see cref="MedicalModel.HealUnitsPerTick"/> per second for the row's
    /// <c>OverSeconds</c>, clamped at the bar's maximum, publishing both client health paths.
    /// Stops on death. Two medicals stack their ticks; retail's Z1 model does the same.
    /// </summary>
    private void BeginHeal(SoeConnection connection, GatewaySessionState state, Medical row)
    {
        if (row.StopsBleed) StopPlayerBleeding(connection, state);
        int ticks = Math.Max(1, row.OverSeconds);
        uint perTick = (uint)MedicalModel.HealUnitsPerTick(in row);
        uint max = _options.Gas.MaxHitpoints;
        uint startedAt = state.Hitpoints;
        int healWorldGeneration = state.WorldGeneration;
        int healVitalsGeneration = state.PlayerVitalsGeneration;
        PlayerInventory? healInventory = state.Inventory;
        int healHudGeneration = state.HealingHudGeneration;
        uint healEffect = AddHealingHud(connection, state, row);

        void Tick(int remaining)
        {
            if (healHudGeneration != state.HealingHudGeneration) return;
            if (state.Hitpoints == 0 || state.DeathSent || healWorldGeneration != state.WorldGeneration
                || healVitalsGeneration != state.PlayerVitalsGeneration
                || !ReferenceEquals(healInventory, state.Inventory))
            {
                RemoveHealingHud(connection, state, healEffect);
                return;
            }

            uint before = state.Hitpoints;
            uint after = Math.Min(max, before + perTick);
            state.Hitpoints = after;
            PublishPlayerHealth(connection, state, before);

            if (remaining > 1 && after < max)
            {
                Later(connection, (int)MedicalModel.TickIntervalMs, () => Tick(remaining - 1));
            }
            else
            {
                RemoveHealingHud(connection, state, healEffect);
                _log.Info($"{connection} medical: {row.Name} healed {startedAt} → {after}/{max} over "
                    + $"{ticks - remaining + 1} tick(s)");
            }
        }

        _log.Info($"{connection} medical: {row.Name} heals {row.TotalHp} HP over {row.OverSeconds} s "
            + $"({perTick} units/tick) from {startedAt}/{max}");
        Later(connection, (int)MedicalModel.TickIntervalMs, () => Tick(ticks));
    }

    /// <summary>
    /// <b>D257 / audit G2 — one craft, on a cast bar and a timer.</b> Until now
    /// <c>Command.RecipeStart</c> called <see cref="CraftingService.Craft"/> and
    /// <see cref="ApplyCraft"/> synchronously: the craft was instantaneous, there was no bar, no
    /// animation and no busy state, which is the exact complaint that produced the owner's round-37
    /// fix for shred. The mechanism this needs already existed and is proven —
    /// <see cref="RunShred"/>'s <c>Later</c> + <see cref="InteractionStart"/> — so this is that
    /// shape applied to the other verb.
    /// <list type="number">
    /// <item><b>Validate before arming.</b> A craft with missing ingredients is answered at once,
    /// exactly as it is today, and never puts a bar on screen it is going to take back. This is Z1's
    /// round-34 lesson, which <c>ShredTable.Shred</c> already learned.</item>
    /// <item><b>Arm the bar</b> for <see cref="Rulings.Crafting.CraftMillisecondsPerUnit"/> per
    /// requested unit (D257, the owner's own value under D53 — the client carries no craft
    /// duration anywhere), carrying the OUTPUT item's <c>NAME_ID</c> so the bar names what is being
    /// made.</item>
    /// <item><b>Do the work when it elapses</b>, re-validating: <see cref="CraftingService.Craft"/>
    /// checks ingredients and room again at completion, so an ingredient that moved during the cast
    /// costs the player nothing.</item>
    /// <item><b>Stop the animation</b> with <see cref="SendInteractionStops"/>.</item>
    /// </list>
    /// <para>
    /// A second request inside the window is refused rather than queued, for
    /// <see cref="RunShred"/>'s reason: the bar has locked the character out for that long, so a
    /// request arriving inside it did not come from the crafting window.
    /// </para>
    /// </summary>
    private void RunCraft(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        RecipeStartRequest request,
        CraftingOptions crafting)
    {
        if (RefuseInteractionDuringLogout(connection, state)) return;
        long now = Environment.TickCount64;
        if (state.PendingVehicleRemoval is not null || state.CraftBusyUntil > now)
        {
            SendTunnel(connection, writer =>
                new ContainerError(state.Guid, ContainerErrorCode.ContainerInUse).WriteTo(writer));
            _log.Info($"{connection} crafting: recipe {request.RecipeId} REFUSED — a craft is "
                + $"already running for another {state.CraftBusyUntil - now} ms");
            return;
        }

        // PLAN BEFORE ARM. CraftingService.Craft is pure with respect to a refusal - it mutates
        // nothing unless it succeeds - so the cheapest correct preflight is to ask it how many units
        // it could make right now and refuse here if the answer is none.
        if (!CraftingCatalog.ByIdFor(crafting.RetailRecipes)
                .TryGetValue(request.RecipeId, out RecipeDefinition? recipe))
        {
            CraftOutcome unknown = CraftingService.Craft(
                inventory, request.RecipeId, request.Count, crafting);
            ApplyCraft(connection, state, inventory, unknown);
            return;
        }

        uint available = CraftingService.CraftableCount(inventory, recipe);
        uint requested = Math.Min(Math.Max(1u, request.Count), available);
        if (available == 0)
        {
            CraftOutcome refused = CraftingService.Craft(inventory, recipe, requested, crafting);
            ApplyCraft(connection, state, inventory, refused);
            _log.Info($"{connection} crafting: recipe {recipe.RecipeId} refused before the cast bar "
                + "was armed — nothing was consumed and no bar was shown");
            return;
        }

        int duration = (int)Math.Min((long)Math.Max(recipe.BusyMilliseconds, 0) * requested, int.MaxValue);
        state.CraftBusyUntil = now + duration;
        int interactionGeneration = state.InteractionGeneration;
        bool armed = Later(connection, duration, () =>
        {
            if (state.InteractionGeneration != interactionGeneration
                || !ReferenceEquals(inventory, state.Inventory)) return;
            CraftOutcome outcome = CraftingService.Craft(inventory, recipe, requested, crafting);
            state.CraftBusyUntil = 0;
            ApplyCraft(
                connection,
                state,
                inventory,
                outcome,
                forceContainerRedeclaration: PlayerInventory.ProvidesContainer(
                    recipe.OutputItemDefinitionId));

            int stopped = SendInteractionStops(connection, state);
            _log.Info($"{connection} crafting: recipe {recipe.RecipeId} timer completed"
                + (stopped == 0 ? string.Empty : $", cf/03 InteractionStop ×{stopped}"));
        });

        if (!armed)
        {
            state.CraftBusyUntil = 0;
            SendTunnel(connection, writer => new RecipeCraftingStatus(recipe.RecipeId, RecipeCraftingState.Ready).WriteTo(writer));
            SendTunnel(connection, writer => new ContainerError(
                state.Guid, ContainerErrorCode.ContainerInUse).WriteTo(writer));
            return;
        }

        InventoryItemFacts.TryGet(recipe.OutputItemDefinitionId, out InventoryItemFact output);
        SendTunnel(connection, writer => new RecipeCraftingStatus(recipe.RecipeId, RecipeCraftingState.Crafting).WriteTo(writer));
        SendTunnel(connection, writer => new InteractionStart(
            state.Guid,
            duration,
            output.NameId,
            Rulings.Crafting.CraftInteractionAnimationId).WriteTo(writer));
        _log.Info($"{connection} crafting: recipe {recipe.RecipeId} ×{requested} — InteractionStart "
            + $"cf/02, {duration} ms, string {output.NameId}, animation "
            + $"{Rulings.Crafting.CraftInteractionAnimationId}");
    }

    /// <summary>
    /// Close a cast with <c>cf 03 CharacterState.InteractionStop</c>, as many times as retail sends
    /// it (<see cref="Rulings.Crafting.InteractionStopRepeat"/> — the friend's server sends two,
    /// 8-9 ms apart, at every one of the three completions in the capture).
    /// </summary>
    /// <returns>How many were sent; 0 when <c>CRANBERRY_INTERACTION_STOP=0</c>.</returns>
    private int SendInteractionStops(SoeConnection connection, GatewaySessionState state)
    {
        if (!_options.Crafting.Effective.SendInteractionStop)
        {
            return 0;
        }

        for (int index = 0; index < Rulings.Crafting.InteractionStopRepeat; index++)
        {
            SendTunnel(connection, writer => new InteractionStop(state.Guid).WriteTo(writer));
        }

        return Rulings.Crafting.InteractionStopRepeat;
    }

    /// <summary>
    /// The ground actor an item becomes when it is dropped, from the same loot tables the world was
    /// seeded from. Never throws: missing or hand-edited data makes a drop refusable, not fatal.
    /// </summary>
    private bool TryResolveGroundActor(
        GatewaySessionState state,
        PlayerInventory inventory,
        uint itemDefinitionId,
        out uint groundModelId,
        out uint nameId,
        out GroundActorSource source)
    {
        groundModelId = 0;
        nameId = 0;
        source = default;
        try
        {
            state.Droppable ??= DroppableItems.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"inventory: the drop catalogue is unavailable ({ex.GetType().Name}: "
                + $"{ex.Message}) — nothing can be dropped this session");
            return false;
        }

        return state.Droppable.TryGet(
            itemDefinitionId,
            inventory.Options.UniversalGroundActor,
            out groundModelId,
            out nameId,
            out source);
    }

    /// <summary>
    /// docs/62 §Integration 5: put one craft outcome on the wire. <c>Changes</c> is already in send
    /// order. The grant goes through <c>PlayerInventory.TryPickUp</c>, so regression guard 5 applies
    /// to a crafted bandage exactly as it does to a looted one.
    /// </summary>
    private void ApplyCraft(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        CraftOutcome outcome,
        bool forceContainerRedeclaration = false)
    {
        foreach (CraftItemChange change in outcome.Changes)
        {
            if (change.NeedsDelete)
            {
                ulong gone = change.ItemGuid;
                SendTunnel(connection, writer => new ItemDelete(state.Guid, gone).WriteTo(writer));
            }

            if (change.NeedsAdd && change.Item is InventoryItemInstance instance)
            {
                SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, instance.ToRecord(state.Guid)));
            }
        }

        if (outcome.ContainerError != ContainerErrorCode.None)
        {
            SendTunnel(connection, writer =>
                new ContainerError(state.Guid, outcome.ContainerError).WriteTo(writer));
        }

        SendTunnel(connection, writer =>
            new RecipeCraftingStatus(outcome.RecipeId, outcome.Status).WriteTo(writer));

        if (_options.Crafting.Effective.SendComponentCounts && outcome.Recipe is not null)
        {
            foreach (RecipeComponentUpdate update in
                CraftingService.ComponentCountUpdates(inventory, outcome.Recipe))
            {
                RecipeComponentUpdate row = update;
                SendTunnel(connection, writer => row.WriteTo(writer));
            }
        }

        if (outcome.Succeeded)
        {
            // A craft changes the bag's bulk exactly as a pickup does, and a crafted item that
            // auto-assigns into a wheel box needs its binding sent or the panel box stays empty.
            bool containerSetChanged = forceContainerRedeclaration
                || outcome.Changes.Any(change =>
                    change.Item is InventoryItemInstance { LoadoutSlotId: not 0 } granted
                    && PlayerInventory.ProvidesContainer(granted));

            if (containerSetChanged)
            {
                SendLoadoutSlots(connection, inventory);
                SendTunnel(connection, writer => inventory.ToInitContainers().WriteTo(writer));
            }
            else
            {
                if (inventory.BaseBag is InventoryContainer craftedInto)
                {
                    SendTunnel(connection, writer => inventory.ToUpdate(craftedInto).WriteTo(writer));
                }

                SendLoadoutSlots(connection, inventory);
            }
        }

        if (outcome.Succeeded && outcome.Changes.Any(change =>
                change.Item is { EquipmentSlotId: not 0 } || change.NeedsDelete))
            SendCharacterAppearance(connection, state, "crafting equipment changed");

        _log.Info($"{connection} crafting: {outcome.Describe()}");
    }

    private void HandleProximityLootItem(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        RequestUseItem request)
    {
        if (request.CharacterGuid != state.Guid)
        {
            SendTunnel(connection, writer => new ContainerError(
                state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo(writer));
            return;
        }

        ulong[] candidates =
            [request.ItemGuid, request.SourceCharacterGuid, request.TargetCharacterGuid];
        foreach (ulong candidate in candidates.Distinct())
        {
            if (!state.Loot.TryGet(candidate, out GroundLootItem? ground))
            {
                continue;
            }

            if (!ItemUseOptionTable.Allows(ground.ItemDefinitionId, request.ItemUseOptionId)
                && ItemUseOptionTable.OptionsForItem(ground.ItemDefinitionId).Count > 0)
            {
                SendTunnel(connection, writer => new ContainerError(
                    state.Guid, ContainerErrorCode.WrongItemType).WriteTo(writer));
                return;
            }

            TransferGroundLoot(connection, state, inventory, ground, new MoveItemRequest(
                0, ground.WorldGuid, ground.WorldGuid, state.Guid,
                request.Count == 0 ? ground.Count : request.Count, -1));
            return;
        }

        SendTunnel(connection, writer => new ContainerError(
            state.Guid, ContainerErrorCode.SlotDoesNotContainItem).WriteTo(writer));
        _log.Info($"{connection} inventory: Proximity LootItem could not resolve item/source/target "
            + $"{request.ItemGuid}/{request.SourceCharacterGuid}/{request.TargetCharacterGuid}");
    }

    private void HandleContainerMove(
        SoeConnection connection,
        GatewaySessionState state,
        byte[] payload)
    {
        MoveItemRequest move = MoveItemRequest.Parse(payload);
        if (state.Inventory is not PlayerInventory inventory)
        {
            SendTunnel(connection, writer => new ContainerError(
                state.Guid, ContainerErrorCode.UnknownContainer).WriteTo(writer));
            return;
        }

        if (HandleBodyBagMove(connection, state, move)) return;
        if (HandleVehicleContainerMove(connection, state, move)) return;

        if (move.SourceCharacterGuid == state.Guid && move.TargetCharacterGuid == state.Guid)
        {
            ApplyInventoryAction(connection, state, inventory, InventoryMoves.Resolve(inventory, move));
            return;
        }

        ulong worldGuid = state.Loot.TryGet(move.SourceCharacterGuid, out GroundLootItem? source)
            ? source.WorldGuid
            : state.Loot.TryGet(move.ItemGuid, out source)
                ? source.WorldGuid
                : 0;

        bool valid = source is not null
            && worldGuid != 0
            && move.TargetCharacterGuid == state.Guid
            && move.ItemGuid == source.WorldGuid
            && move.Count > 0
            && move.Count <= source.Count
            && WithinPickupReach(state, source.Position, out _);
        if (!valid)
        {
            SendTunnel(connection, writer => new ContainerError(
                state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo(writer));
            _log.Info($"{connection} inventory: proximity drag REFUSED source "
                + $"{move.SourceCharacterGuid}, item {move.ItemGuid}, target "
                + $"{move.TargetCharacterGuid}, count {move.Count}, slot {move.NewSlotId}");
            return;
        }

        TransferGroundLoot(connection, state, inventory, source!, move);
    }

    /// <summary>
    /// Rebuild the four pieces the August client consumes when its inventory window opens. The
    /// order matches the working reference flow after translating the build-shifted opcodes:
    /// ProximateItems, InitContainers, SetLoadoutSlots, then the self-access grant.
    /// </summary>
    private void HandleInventoryWindow(
        SoeConnection connection,
        GatewaySessionState state,
        string action)
    {
        if (action == "open")
        {
            if (!EnsureInventory(connection, state, "InventoryWindow open")
                || state.Inventory is not PlayerInventory inventory)
            {
                _log.Info($"{connection} inventory: InventoryWindow open while containers are disabled");
                return;
            }

            if (_options.SendProximateItems)
            {
                SendProximateItems(connection, state);
            }

            InitContainers containers = inventory.ToInitContainers();
            SendTunnel(connection, writer => containers.WriteTo(writer));

            // SetLoadoutSlots rebuilds the client-side hotbar and its ability manager. This window
            // open only republishes state; it must not emit 86/07 or invent a hotbar transition
            // the player did not request.
            int loadoutBindings = inventory.LoadoutSlots.Count;
            SendLoadoutSlots(connection, inventory);
            SendInventoryActionState(connection, state);

            var access = new BeginCharacterAccess(state.Guid);
            SendTunnel(connection, writer => access.WriteTo(writer));
            state.CharacterAccessGranted = true;
            if (state.Fleet?.TryGetForOccupant(state.Guid, out var accessedVehicle) == true)
                SendVehicleInventory(connection, state, accessedVehicle);
            if (state.AccessedBodyBag != 0 && state.BodyBags.TryGetValue(state.AccessedBodyBag, out var accessedBag))
                SendBodyBagInventory(connection, state, state.AccessedBodyBag, accessedBag);

            _log.Info($"{connection} inventory: InventoryWindow open -> "
                + $"{(_options.SendProximateItems ? "ProximateItems, " : string.Empty)}"
                + $"InitContainers ({containers.Containers.Count} records), SetLoadoutSlots "
                + $"({loadoutBindings} bindings) + ability-manager refresh, "
                + $"BeginCharacterAccess f0/01 ({BeginCharacterAccess.Length} B)");
            return;
        }

        if (action == "close")
        {
            CloseBodyBag(connection, state);
            // Native Q/E -> 140d239e0 -> 140d24980 checks self mutable access through
            // 140d833a0 even with the inventory window closed. Never revoke that permission.
            GrantSelfInventoryAccess(connection, state);
            _log.Info($"{connection} inventory: window closed; self access retained for quick-use");
            return;
        }

        _log.Info($"{connection} inventory: InventoryWindow action='{action}' (unhandled)");
    }

    private void GrantSelfInventoryAccess(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Inventory is null || state.CharacterAccessGranted) return;
        SendTunnel(connection, writer => new BeginCharacterAccess(state.Guid).WriteTo(writer));
        state.CharacterAccessGranted = true;
    }

    /// <summary>
    /// Republishes the nearby-pickable list (docs/13 §3c). The client's reader clears and rebuilds
    /// its collection from the packet, so a refresh after a pickup is what drops the claimed row from
    /// the proximity panel (docs/13 §9 step 5).
    /// <para>
    /// <b>WAVE 8: it is the owner's 2 m ball, not the whole registry.</b> This method published every
    /// registered ground item with no distance test of any kind, which was survivable only while the
    /// working set was capped at 128 — and was the single constraint that pinned it there, at a
    /// measured 9,552 B per republish. His <c>ZoneProximity</c> is
    /// <c>ProximityRadius = 2</c> / <c>ProximityYDistance = 1</c> / <c>MaximumEntries = 32</c>, and
    /// at that filter the packet is 155 B p90 / 673 B max (docs/78 §3.3).
    /// </para>
    /// <para>
    /// <b>This cannot break pickup.</b> <c>f8 01</c> is the QuickLoot panel list, not the <c>[F]</c>
    /// binding — that is bound by <c>ea 04 CreateComponent</c> — and the reader frees its whole
    /// collection before parsing, so a full replace with fewer rows is what the packet is for. The
    /// filter is skipped when the session has no pose yet, so a panel is never emptied by a missing
    /// position.
    /// </para>
    /// </summary>
    private void SendProximateItems(SoeConnection connection, GatewaySessionState state)
    {
        if (state.BodyBags.Count > 0) state.BodyBagPanelSignature = BodyBagPanelSignature(state);
        LootStreamOptions stream = _options.LootStream;
        Vector3? centre = state.Movement.Player?.Position;
        var rows = new List<ProximateItem>(
            Math.Min(state.Loot.Count, stream.PanelMaxRows > 0 ? stream.PanelMaxRows : state.Loot.Count));

        if (centre is Vector3 at && stream.PanelRadiusMetres > 0f)
        {
            List<GroundLootItem> near = _panelScratch;
            near.Clear();
            state.Loot.SelectPanelRows(
                at, stream.PanelRadiusMetres, stream.PanelHeightMetres, stream.PanelMaxRows, near);
            foreach (GroundLootItem ground in near)
            {
                AddBodyBagProximityRows(connection, state, rows, ground);
            }

            near.Clear();
        }
        else
        {
            // No pose, or the filter switched off: the pre-wave-8 behaviour, the whole registry.
            foreach (GroundLootItem ground in state.Loot.Items)
            {
                AddBodyBagProximityRows(connection, state, rows, ground);
            }
        }

        if (stream.PanelMaxRows > 0 && rows.Count > stream.PanelMaxRows)
            rows.RemoveRange(stream.PanelMaxRows, rows.Count - stream.PanelMaxRows);
        var list = new ProximateItems(rows);
        SendTunnel(connection, writer => list.WriteTo(writer));
        _log.Info($"{connection} loot: ProximateItems list refreshed with {rows.Count} row(s) "
            + $"({list.Length} B) of {state.Loot.Count} registered"
            + (centre is not null && stream.PanelRadiusMetres > 0f
                ? $" — within {stream.PanelRadiusMetres:0.#} m / ±{stream.PanelHeightMetres:0.#} m"
                : " — unfiltered (no pose yet)"));
    }

    /// <summary>
    /// One reused buffer for the panel selection. The republish runs on the listener thread, one
    /// session at a time, and the rows are copied into the packet's own list before it is handed to
    /// the writer, so nothing outlives the call.
    /// </summary>
    private readonly List<GroundLootItem> _panelScratch = [];

    private static ProximateItem ToProximateItem(GroundLootItem ground) => new(
        Key: ground.TransientId,
        Item: new InventoryItem(
            DefinitionId: ground.SkinRewardItemId != 0 ? ground.SkinRewardItemId : ground.ItemDefinitionId,
            ItemGuid: ground.WorldGuid,
            Count: ground.Count,
            // InventoryWindow passes ContainerOwnerGuid to MoveItem2 when a proximity row
            // is clicked. The loose item's owner is its world object; zero leaves the
            // native inventory resolver without a source and the click never reaches us.
            OwnerGuid: ground.WorldGuid,
            ContainerGuid: 0,
            ContainerDefinitionId: 0,
            SlotId: 0),
        WorldObjectGuid: ground.WorldGuid);

    /// <summary>
    /// <b>The server's own answer gate for a ground pickup</b> — his round-24 ruling, one reach on
    /// every path (<c>ZoneProximity.InteractReach = 4</c>). Cranberry had none at all.
    /// <para>
    /// <b>Fail-open on purpose.</b> A session with no pose yet, or a disabled
    /// <see cref="LootStreamOptions.PickupReachMetres"/>, grants exactly as before: the gate can only
    /// refuse a press whose distance the server actually knows. Ground pickup is the one loot
    /// behaviour this build has proven live, and a reach check is not allowed to be the thing that
    /// takes it away.
    /// </para>
    /// </summary>
    private bool WithinPickupReach(GatewaySessionState state, in Vector3 target, out float metres)
    {
        metres = 0f;
        float reach = _options.LootStream.PickupReachMetres;
        if (!(reach > 0f))
        {
            return true;
        }
        if (state.Movement.Player?.Position is not Vector3 player)
        {
            metres = float.PositiveInfinity;
            return !_sharedLootMembership.ContainsKey(state);
        }

        metres = Vector3.Distance(player, target);
        return metres <= reach;
    }

    /// <summary>
    /// docs/40 §3: the movement stat burst for one character — <c>Character.UpdateStat (0f 40)</c>
    /// with the 18 speed/blend entries, then the base-speed <c>ClientUpdate (11 05)</c>.
    /// <para>
    /// <b>Every number in the burst is <c>[DESIGN]</c></b>: the client ships no speed table at all
    /// (docs/40 §4.1), so this is Cranberry's answer to "walking, sprinting, crouching, strafing and
    /// walking backwards should all be different speeds". What is derived is the delivery — both
    /// packet layouts, the 13-byte entry, the stat ordinals (resolved from the client's own
    /// <c>StringHashToValue</c> table rather than re-typed) and the stance/axis scalar chain.
    /// </para>
    /// <para>
    /// Order is load-bearing: this must follow the character record. A <c>0x0f</c> sub against a
    /// guid the client has never seen is dropped without a word (docs/21 §1b).
    /// </para>
    /// </summary>
    private void SendMovementStats(SoeConnection connection, GatewaySessionState state, string reason)
    {
        PlayerMovementTracker tracker = state.MovementStats;
        FootwearTier footwear = Footwear.Equipped(state.Inventory);
        MovementProfile profileForShoes = ApplyConsoleSpeed(state, Footwear.Apply(_options.Movement, footwear));
        if (state.FootwearAudio != footwear)
        {
            SendTunnel(connection, w => w.WriteRaw(Footwear.AudioPacket(state.Guid, footwear)));
            state.FootwearAudio = footwear;
        }
        if (!_options.SendMovementStats) return;
        if (tracker.Profile != profileForShoes)
        {
            // SetProfile clears StatsDelivered, so this is guarded: an unconditional call would
            // re-send the burst on every resync.
            tracker.SetProfile(profileForShoes);
        }

        if (tracker.StatsDelivered)
        {
            return;
        }

        CharacterStatPackets.UpdateStat burst = tracker.BuildStatBurst(state.Guid);
        CharacterStatPackets.ClientUpdateStat baseSpeed = tracker.BuildBaseSpeedUpdate();
        SendTunnel(connection, writer => burst.WriteTo(writer));
        SendTunnel(connection, writer => baseSpeed.WriteTo(writer));
        tracker.MarkStatsDelivered();

        MovementProfile profile = tracker.Profile;
        _log.Info($"{connection} movement: stat burst sent ({reason}) — Character.UpdateStat 0f 40 "
            + $"with {burst.Stats.Count} entries ({burst.Length} B) + ClientUpdate 11 05 "
            + $"({baseSpeed.Length} B); run {profile.RunSpeed:F2}, sprint {profile.SprintSpeed:F2}, "
            + $"walk {profile.WalkSpeed:F2}, crouch {profile.CrouchSpeed:F2}, "
            + $"strafe {profile.StrafeSpeed:F2}, backpedal {profile.BackpedalSpeed:F2} m/s "
            + "— NOT live-verified: nothing client-originated acknowledges a stat (docs/40)");
    }

    /// <summary>
    /// docs/41 §I1(b): create this match actor's container model and tell the client about it, once.
    /// <para>
    /// <c>Container.InitContainers</c> is <b>destructive</b> — <c>FUN_140d7ed90</c> clears the whole
    /// client-side list — so every publication is a complete snapshot. Bootstrap sends the first
    /// snapshot after the equipped <c>ItemAdd</c> rows; inventory-window opens and container-provider
    /// loadout changes can safely replace it later. That ordering is the whole of docs/41 §0's
    /// diagnosis: wave 2 granted items with <c>ContainerGuid = 0</c>, a guid that names no container,
    /// which is why a picked-up item appeared in the count and in no panel.
    /// </para>
    /// Returns false when the container model is switched off, in which case the caller keeps the
    /// wave-2 grant.
    /// </summary>
    private bool EnsureInventory(SoeConnection connection, GatewaySessionState state, string trigger)
    {
        // docs/89 §4 A4: the weapon-definition table, once per session, before the first weapon
        // ItemAdd - and above the container gate, because CRANBERRY_CONTAINERS=0 still lets a
        // weapon reach the client by the wave-2 grant path.
        SendWeaponDefinitionsOnce(connection, state);

        if (!_options.SendContainers)
        {
            return false;
        }

        if (state.Inventory is not null)
        {
            return true;
        }

        InventoryOptions inventoryOptions = InventoryOptionsFor(state);
        var inventory = new PlayerInventory(state.Guid, state.Loot.NextItemGuid, inventoryOptions)
        {
            SkinDefinition = id => _options.Skins.SendWornSkinsInWorld && AugustWornSkins.TryGetCategory(id, out uint category)
                && state.Wardrobe.Snapshot().FirstOrDefault(e => e.CategoryPrototypeId == category) is { RewardItemId: not 0 } selected
                    ? selected.RewardItemId : id,
        };
        inventory.Bootstrap();
        state.Inventory = inventory;
        state.InventorySkinTargetItemGuid = 0;
        state.InventoryActionUiState = null;

        SendTunnel(connection, writer => new SetCurrentLoadout(state.Guid, inventory.LoadoutId).WriteTo(writer));

        // docs/46 §7a condition 1: the inventory panel resolves (definition id, slot id) against the
        // client's own item collection, so every worn item needs an ItemAdd BEFORE the binding that
        // names it — the same ordering rule docs/36 §W3 proved for the RHand row. Without this burst
        // the character is dressed in the server's model and the panel is empty, which is exactly
        // the owner's wave-3 report. Each bootstrap record carries the equipped sentinel
        // (0xFFFFFFFFFFFFFFFF) rather than a container guid, so the burst is safe before the
        // destructive InitContainers, and putting it first lets c8/02's re-index (FUN_140d7bcb0)
        // run with the items already present.
        IReadOnlyList<InventoryItem> grants = inventory.ToBootstrapGrants();
        foreach (InventoryItem grant in grants)
        {
            InventoryItem granted = grant;
            SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, granted));
        }

        SendTunnel(connection, writer => inventory.ToInitContainers().WriteTo(writer));
        SendLoadoutSlots(connection, inventory);
        GrantSelfInventoryAccess(connection, state);

        (int used, int max) = inventory.Capacity;
        int withheld = inventory.Items.Count - grants.Count;
        _log.Info($"{connection} inventory: bootstrapped loadout {inventory.LoadoutId} with "
            + $"{inventory.LoadoutSlots.Count} binding(s), {grants.Count} item grant(s) and "
            + $"bag {inventory.BaseBag?.Guid} (definition {inventory.BaseBag?.DefinitionId}, "
            + $"bulk {used}/{max}) — SetCurrentLoadout 86/03, {grants.Count} × ItemAdd "
            + $"11/02, InitContainers c8/02, SetLoadoutSlots 86/04 ({trigger})"
            + (withheld > 0
                ? $"; {withheld} CODE_FACTORY_NAME = Weapon instance(s) held back from the zoning "
                    + "burst by InventoryOptions.GrantWeaponItemsAtBootstrap rollback"
                : string.Empty)
            // D29: the wave-3 build DID put 86/03, c8/02, 86/04 and 86/05 on this wire — they are in
            // captures\wire-20260829-220829.txt (lines 292-294, 6100, 12678, 12813). What was never
            // true is that anything acknowledged them, and docs/46 §3a proves the 86/04|86/05 that
            // went out carried ItemDefinitionId = 0 and were inert at the client's own gate.
            + ". NOT live-verified: nothing client-originated acknowledges an 0xc8 or 0x86, and the "
            + "wave-3 86/04|86/05 that were sent carried ItemDefinitionId = 0 and were inert "
            + "(docs/41 §I2, docs/46 §3a, §I1d)");
        return true;
    }

    /// <summary>
    /// Turn the visible pre-game wardrobe into the actual starter item instances. The previous
    /// implementation always granted four hard-coded garments even when the character was visibly
    /// wearing a selected hoodie, hat or mask; consequently the visible garment could neither be
    /// dropped nor used for hoodie actions.
    /// </summary>
    private InventoryOptions InventoryOptionsFor(GatewaySessionState state)
    {
        IReadOnlyList<uint> configured = _options.Inventory.StarterOutfit;
        var byBodySlot = new Dictionary<uint, uint>();
        var orderedSlots = new List<uint>();

        void Put(uint definitionId)
        {
            uint loadoutSlot = InventoryAutoAssign.AutoEquipLoadoutSlot(
                definitionId,
                SurvivorLoadout.Id,
                _options.Inventory.FoldItemClassMappings);
            if (loadoutSlot == 0
                || !LoadoutSlotTable.TryGet(
                    SurvivorLoadout.Id, loadoutSlot, out LoadoutSlotDefinition slot)
                || slot.EquipSlotId == 0)
            {
                return;
            }

            if (!byBodySlot.ContainsKey(slot.EquipSlotId))
            {
                orderedSlots.Add(slot.EquipSlotId);
            }

            byBodySlot[slot.EquipSlotId] = definitionId;
        }

        foreach (uint definitionId in configured)
        {
            Put(definitionId);
        }

        // WornSnapshot excludes pickup-only helmet/backpack/armour presets until the matching
        // world item exists, so this cannot manufacture equipment the player has not picked up.
        foreach (AugustSkinCatalogEntry selected in state.Wardrobe.WornSnapshot())
        {
            // A cosmetic choice must not grant a faster gameplay tier at match bootstrap.
            if (selected.EquipmentSlotId == BodySlots.Feet
                && Footwear.TierFor(selected.RewardItemId) != FootwearTier.Stealth) continue;
            Put(selected.RewardItemId);
        }

        uint[] starter = [.. orderedSlots.Select(slot => byBodySlot[slot])];
        return _options.Inventory with { StarterOutfit = starter };
    }

    /// <summary>
    /// docs/13 §5 grant step. The claim is destructive, so the duplicate request of one F press
    /// finds nothing; on success the grant goes out <em>before</em> the removal, so a refused
    /// pickup can never vanish the object. No container packet follows: the ItemAdd apply core
    /// <c>FUN_140c35400</c> repaints the bag itself (docs/13 §7).
    /// </summary>
    private void TryPickUpGroundLoot(
        SoeConnection connection,
        GatewaySessionState state,
        ulong worldGuid,
        string trigger)
    {
        // D274 FIRST, before anything else looks at the guid: an airdrop crate is registered in
        // state.Loot exactly like an item — that is what makes [F] reach it at all — so an uncaught
        // press would hand the player item 1501, a World Container, instead of what is inside it.
        if (TryOpenBodyBag(connection, state, worldGuid)) return;
        if (TryOpenAirdropCrate(connection, state, worldGuid, trigger))
        {
            return;
        }

        // docs/41 §I1(c): with the container model on, the pickup resolves a real destination slot
        // first. Without it (CRANBERRY_CONTAINERS=0) the wave-2 grant below is kept byte for byte.
        if (EnsureInventory(connection, state, trigger) && state.Inventory is PlayerInventory inventory)
        {
            PickUpIntoInventory(connection, state, inventory, worldGuid, trigger);
            return;
        }

        // The reach gate runs on the non-destructive TryGet, BEFORE the claim, for the same reason
        // the container path plans before it claims: a refused pickup has to leave the object
        // standing on the ground, and TryClaim has no undo (docs/78 §7 E9).
        if (state.Loot.TryGet(worldGuid, out GroundLootItem? standing)
            && !WithinPickupReach(state, standing.Position, out float wave2Reach))
        {
            _log.Info($"{connection} loot: {trigger} REFUSED world object {worldGuid} — out of reach "
                + $"({wave2Reach:0.00} m > {_options.LootStream.PickupReachMetres:0.#} m); the object "
                + "stays on the ground");
            return;
        }

        if (!TryClaimSharedLoot(state, worldGuid, out GroundLootItem? item))
        {
            // docs/47 §4b: this is almost always the idempotency guard working, not a failure. One
            // [F] press produces BOTH 09 15 PlayerSelect and 09 07 InteractRequest ~3 ms apart; the
            // first claims the object destructively and the second necessarily finds nothing. Only a
            // guid that was never ground loot at all is interesting, and that is now said plainly.
            _log.Info($"{connection} loot: {trigger} named world object {worldGuid}, which is not "
                + $"claimable ground loot ({state.Loot.Count} item(s) registered) — no grant "
                + (state.Loot.WasEvicted(worldGuid)
                    // docs/52 §Integration step 6: since the streamer there are TWO ways to reach
                    // this line, and the pre-streaming wording asserted the wrong one half the time.
                    ? "(streamed out — the object was destroyed before the press landed)"
                    : state.Loot.IsLootGuid(worldGuid)
                        ? "(already claimed by the other half of the same press — expected)"
                        : "(the guid is outside this session's ground-loot range)"));
            return;
        }

        var record = new InventoryItem(
            DefinitionId: item.ItemDefinitionId,
            ItemGuid: state.Loot.NextItemGuid(),
            Count: item.Count,
            OwnerGuid: state.Guid,
            ContainerGuid: _options.GroundLootContainerGuid,
            ContainerDefinitionId: _options.GroundLootContainerDefinitionId,
            SlotId: state.NextInventorySlot++);
        RestoreGroundWeaponMagazine(state, item, record.ItemGuid, record.DefinitionId);
        Action<PacketWriter> grant = state.Weapons.CreateItemAdd(state.Guid, record);
        int grantLength = ItemAddLength(state, record);

        // The world object is gone, so a later re-use of the guid may be promoted again.
        state.FullNpcSent.Remove(item.WorldGuid);
        // docs/52 §3e: and the MARKER it came from is spent for the rest of the match. Without this
        // a looted house restocks itself the moment the player walks 90 m away and comes back,
        // which is a worse bug than the empty buildings the streamer was built to fix.
        state.StreamedLoot.NoteTaken(item.WorldGuid);
        SendPickupSound(connection, item);
        SendTunnel(connection, grant);
        SendTunnel(connection, writer => new RemovePlayer(item.WorldGuid).WriteTo(writer));
        if (_options.SendProximateItems)
        {
            // docs/13 §9 step 5: the panel drops the claimed row only when the list is republished.
            SendProximateItems(connection, state);
        }

        _log.Info($"{connection} loot: {trigger} → granted item {item.ItemDefinitionId} ×{item.Count} "
            + $"as instance {record.ItemGuid} in slot {record.SlotId} (ItemAdd {grantLength} B) and "
            + $"removed world object {item.WorldGuid}; {state.Loot.Count} left on the ground");
    }

    /// <summary>
    /// docs/41 §I1(c): the pickup with a real destination. The item's home is chosen by the client's
    /// own rule — <c>FUN_140d35510</c>: a loadout slot accepts an item iff the item's
    /// <c>ITEM_CLASS</c> is in that slot's <c>LoadoutSlotItemClasses</c> set — so the server and the
    /// client's own UI cannot disagree about where the thing went.
    /// <para>
    /// <b>Plan before claim.</b> <see cref="LootWorld.TryClaim"/> is destructive and has no restore,
    /// so the refusal test runs against the non-destructive <c>TryGet</c> first: a refused pickup has
    /// to leave the object standing on the ground. The claim then keeps its original role of losing
    /// the race between the two packets one <c>[F]</c> press produces (docs/13 §8).
    /// </para>
    /// </summary>
    private void PickUpIntoInventory(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        ulong worldGuid,
        string trigger)
    {
        // D274: the crate guard again, because this method has callers of its own (the quick-loot
        // and panel paths) that do not come through TryPickUpGroundLoot.
        if (TryOpenBodyBag(connection, state, worldGuid)) return;
        if (TryOpenAirdropCrate(connection, state, worldGuid, trigger))
        {
            return;
        }

        if (!state.Loot.TryGet(worldGuid, out GroundLootItem? peek))
        {
            // docs/47 §4b: this is almost always the idempotency guard working, not a failure. One
            // [F] press produces BOTH 09 15 PlayerSelect and 09 07 InteractRequest ~3 ms apart; the
            // first claims the object destructively and the second necessarily finds nothing. Only a
            // guid that was never ground loot at all is interesting, and that is now said plainly.
            _log.Info($"{connection} loot: {trigger} named world object {worldGuid}, which is not "
                + $"claimable ground loot ({state.Loot.Count} item(s) registered) — no grant "
                + (state.Loot.WasEvicted(worldGuid)
                    // docs/52 §Integration step 6: since the streamer there are TWO ways to reach
                    // this line, and the pre-streaming wording asserted the wrong one half the time.
                    ? "(streamed out — the object was destroyed before the press landed)"
                    : state.Loot.IsLootGuid(worldGuid)
                        ? "(already claimed by the other half of the same press — expected)"
                        : "(the guid is outside this session's ground-loot range)"));
            return;
        }

        if (!WithinPickupReach(state, peek.Position, out float reachMetres))
        {
            // His round-24 ruling: ONE reach for every pickup path. Nothing is claimed and nothing
            // is sent — an unanswered press leaves the object exactly where the client draws it,
            // which is the same shape as the NotImplemented arm of the item-use path.
            _log.Info($"{connection} loot: {trigger} REFUSED item {peek.ItemDefinitionId} — out of "
                + $"reach ({reachMetres:0.00} m > {_options.LootStream.PickupReachMetres:0.#} m); the "
                + "object stays on the ground");
            return;
        }

        InventoryPlacement plan = inventory.Plan(peek.ItemDefinitionId, peek.Count);
        if (plan.Kind == InventoryPlacementKind.Refused)
        {
            // docs/41 §4c: the server is the only authority for bulk — a ground pickup never passes
            // through client validation at all. Answer in the client's own vocabulary
            // (Container.Error, whose handler FUN_140d7eb60 prints the code) and leave the object be.
            SendTunnel(connection, writer => new ContainerError(state.Guid, plan.Error).WriteTo(writer));
            _log.Info($"{connection} loot: {trigger} REFUSED item {peek.ItemDefinitionId} "
                + $"×{peek.Count} — {plan.Rule} (Container.Error {plan.Error}); the object stays on "
                + "the ground");
            return;
        }

        if (!TryClaimSharedLoot(state, worldGuid, out GroundLootItem? item))
        {
            // The duplicate InteractRequest/PlayerSelect of one press lost the race, as designed.
            return;
        }

        // docs/95 step 1 needs the ability of the weapon LEAVING the hand, and TryPickUp is what
        // replaces it - so read it here, before the inventory moves. Zero means the hand was empty
        // (or held the fists), and Z1 sends no UninitAbility in that case either.
        uint previousAbilityId = ActiveHandAbilityId(inventory);

        plan = inventory.TryPickUp(item.ItemDefinitionId, item.Count, out InventoryItemInstance? instance);
        if (instance is null)
        {
            _log.Warn($"{connection} loot: {trigger} claimed world object {item.WorldGuid} but the "
                + $"inventory then refused item {item.ItemDefinitionId} ({plan.Kind}, {plan.Rule}) — "
                + "the object is gone and nothing was granted; this is a bug in the plan/apply pair");
            return;
        }

        PublishLootPickup(connection, state, inventory, item, instance, plan, previousAbilityId, trigger);
    }

    private void PublishLootPickup(SoeConnection connection, GatewaySessionState state,
        PlayerInventory inventory, GroundLootItem item, InventoryItemInstance instance,
        InventoryPlacement plan, uint previousAbilityId, string trigger, bool removeWorldObject = true,
        GroundLootItem? remainingGround = null, ulong previousHandGuid = 0)
    {
        bool containerSetChanged = plan.Kind == InventoryPlacementKind.LoadoutSlot
            && (PlayerInventory.ProvidesContainer(instance)
                || (plan.DisplacedItemGuid != 0
                    && inventory.Items.TryGetValue(
                        plan.DisplacedItemGuid, out InventoryItemInstance? displacedProvider)
                    && PlayerInventory.ProvidesContainer(displacedProvider)));

        // The world object is gone, so a later re-use of the guid may be promoted again.
        if (removeWorldObject) state.FullNpcSent.Remove(item.WorldGuid);
        // docs/52 §3e: and the MARKER it came from is spent for the rest of the match. Without this
        // a looted house restocks itself the moment the player walks 90 m away and comes back,
        // which is a worse bug than the empty buildings the streamer was built to fix.
        if (removeWorldObject) state.StreamedLoot.NoteTaken(item.WorldGuid);

        // A dropped/death-looted item retains its own appearance, even when the recipient's
        // category preset differs. This includes an explicitly unskinned base definition.
        if (item.SkinRewardItemId != 0 && plan.Kind != InventoryPlacementKind.Stack)
            inventory.TrySetItemSkin(instance.Guid, item.SkinRewardItemId);

        RestoreGroundWeaponMagazine(state, item, instance);

        // The authoritative grant has succeeded. Queue its sound before skin selection and
        // ItemAdd, whose client handlers can load assets/rebuild inventory and hold up feedback.
        // Duplicate and refused requests still never reach this point.
        SendPickupSound(connection, item);

        bool updateStack = plan.Kind == InventoryPlacementKind.Stack && _options.Combat.Ammo.SendItemUpdate;
        if (updateStack)
        {
            // Keep the existing client item (and any loadout/reload references) alive.
            SendTunnel(connection, ItemUpdate.ForStack(state.Guid, instance).WriteTo);
        }
        else
        {
            if (plan.Kind == InventoryPlacementKind.Stack)
                SendTunnel(connection, writer => new ItemDelete(state.Guid, instance.Guid).WriteTo(writer));

            // New instances must exist before any equipment or container binding names them.
            if (instance.EquipmentSlotId != 0 && instance.SkinOverrideDefinitionId is null)
                SendSelectedItemSkin(connection, state, instance.DefinitionId);
            SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, instance.ToRecord(state.Guid)));
        }
        if (removeWorldObject) SendTunnel(connection, writer => new RemovePlayer(item.WorldGuid).WriteTo(writer));

        if (plan.Kind == InventoryPlacementKind.LoadoutSlot)
        {
            if (plan.GroundDrop is { } oldShoes)
            {
                SendTunnel(connection, new ItemDelete(state.Guid, plan.DisplacedItemGuid).WriteTo);
                InventoryItemFacts.TryGet(oldShoes.DefinitionId, out var oldFact);
                Vector3 dropAt = state.Movement.Player?.Position ?? item.Position;
                var dropped = SpawnGroundLoot(connection, state, oldShoes.DefinitionId,
                    DroppedItemCatalogue.ApparelGroundModels[oldFact.ItemClass], dropAt,
                    oldShoes.Count, oldFact.NameId, skinRewardItemId: oldShoes.DisplayDefinitionId);
                SharePlayerDrop(connection, state, dropped);
            }
            // docs/46 §6a: a swap MOVES the displaced item from its loadout slot into the bag, and
            // re-sending ClientUpdate.ItemAdd (11 02) for that same guid is the only packet that
            // refreshes the item object's own ContainerGuid/SlotId — FUN_140c35400 removes the old
            // node and fires *changed* rather than *added*. c8/06 updates the container's copy only,
            // so without this the client's copy of the swapped-out backpack still claims to be worn.
            if (plan.DisplacedItemGuid != 0
                && inventory.Items.TryGetValue(plan.DisplacedItemGuid, out InventoryItemInstance? moved))
            {
                InventoryItem movedRecord = moved.ToRecord(state.Guid);
                SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, movedRecord));
            }

            // This is what puts the helmet in the head box and the rifle in the first weapon box
            // (docs/41 §2b) — the binding, not the mesh, decides which panel box an item lands in.
            SendTunnel(connection, writer => inventory.ToLoadoutSlot(instance).WriteTo(writer));

            // docs/95: a draw is Z1's EIGHT-packet order, not one whole-character dress that
            // happens to carry a slot-7 row. When the sequence is on it REPLACES both the
            // 86/04-86/07-a0/05 refresh and the re-dress below, because it issues its own 86/04 and
            // a0/05 and ends with the 94/02 binding - sending the dress as well would re-state the
            // whole character after the targeted bind and undo the ordering the sequence exists for.
            if (previousHandGuid != 0 && plan.DisplacedItemGuid == previousHandGuid
                && inventory.CurrentLoadoutSlotId == SurvivorLoadout.Fists && !plan.Wielded)
            {
                // An explicit loot drag can replace the drawn weapon. Unbind has already
                // returned the server hand to fists; publish the same transition as key4,
                // including the ability manager, before restoring the native fist binding.
                StopReloadIfWeaponChanged(connection, state);
                if (previousAbilityId != 0)
                    SendTunnel(connection, writer => writer.WriteRaw(AbilityPackets.UninitAbility(previousAbilityId)));
                SendLoadoutSlots(connection, inventory);
                SendCharacterAppearance(connection, state, "weapon slot change");
                BindSelectedFists(connection, state, force: true);
            }
            else if (plan.Wielded
                && inventory.Options.UseWieldSequence
                && TrySendWieldSequence(connection, state, inventory, instance, previousAbilityId,
                    itemAlreadyGranted: true))
            {
                // The sequence carried the binding, the wheel and the ability manager.
            }
            else
            {
                if (plan.Wielded)
                {
                    // A first picked-up weapon replaces the empty hand under the enabled weapon
                    // stages. The refreshed 86 04 carries the new current slot in its trailer
                    // (S6 §7.1); no 86 07 follows it - that packet froze the owner's input (§7.4).
                    SendLoadoutSlots(connection, inventory);
                }

                // Re-dress: clothing keeps its normal equipment rows and a selected weapon replaces
                // the RHand visual attachment.
                SendCharacterAppearance(connection, state, $"picked up {item.ItemDefinitionId}");

                if (plan.Wielded)
                {
                    // (b) of S6 §7.3: the dress carried the 94 01 slot-7 row, so this is the same
                    // "after the final equipment packet of a draw" point the sequence path uses.
                    SendWeaponStance(connection, $"fallback draw, item {item.ItemDefinitionId}");
                }
            }
        }

        // c8/06 only replaces an already-declared record. Equipping or swapping a container item
        // changes the protocol set, so publish a fresh c8/02 after the new loadout binding; ordinary
        // cargo pickups retain the cheaper update-in-place path.
        if (containerSetChanged)
        {
            SendTunnel(connection, writer => inventory.ToInitContainers().WriteTo(writer));
        }
        else
        {
            foreach (InventoryContainer container in inventory.Containers.Values)
            {
                SendTunnel(connection, writer => inventory.ToUpdate(container).WriteTo(writer));
            }
        }

        if (remainingGround is not null) SendGroundInventoryItem(connection, state, remainingGround);

        // Nearby loot can be a fragmented list. Finish the authoritative inventory/equipment
        // feedback first so that list cannot hold the pickup's bindings behind it on reliable UDP.
        if (_options.SendProximateItems) SendProximateItems(connection, state);

        (int used, int max) = inventory.Capacity;
        _log.Info($"{connection} loot: {trigger} → {plan.Kind} for item {item.ItemDefinitionId} "
            + $"×{item.Count} as instance {instance.Guid} — {plan.Rule}"
            + (plan.Kind == InventoryPlacementKind.LoadoutSlot
                ? $" (loadout slot {plan.LoadoutSlotId}, body slot {plan.EquipmentSlotId}"
                    + $"{(plan.Wielded ? ", WIELDED" : string.Empty)})"
                : $" (container {plan.ContainerGuid} slot {plan.ContainerSlotId})")
            + $"; bulk {used}/{max}; source world object {item.WorldGuid}, "
            + $"{state.Loot.Count} left on the ground");
    }

    private void ReleaseTeleport(SoeConnection connection, GatewaySessionState state)
    {
        // Readiness belongs to the current pending drop. A stale/duplicate acknowledgement or
        // a mount retry must neither skip the client's load gate nor restart an existing release.
        if (state.Match != MatchStep.Dropping || state.ChuteGuid == 0
            || !state.TeleportReady || state.Released) return;

        state.Released = true;
        // docs/115 §1 gate 2: the collision path measures its post-arrival grace from this instant.
        // The client settles its actor onto the terrain as it arrives and reports the settle as an
        // impact, so the first few seconds of every match would otherwise be a fall.
        state.ReleasedAtMs = Environment.TickCount64;
        state.PlayerCollision.Reset();
        SendTunnel(connection, writer => new SynchronizedTeleport(SynchronizedTeleport.Release).WriteTo(writer));
        // WaitForTeleport rebuilds the HUD. Repeat the current count so it cannot retain the
        // lobby's explicit -1 hide sentinel.
        SendTunnel(connection, writer => WriteMatchPopulation(writer, state));
        SendTeamRoster(connection, state);
        state.Match = MatchStep.InMatch;
        NotePublicMatchPhase(state, PublicMatchPhase.ACTIVE);
        // docs/109 lane 3C: from the release this character is in a world another player could see
        // it in, and can see one itself. Before it, a menu/lobby session has no Z2 actor to attach
        // a peer to — which is why the registry gates on this and not on the registration.
        NotePeerInMatch(state, inMatch: true);
        ArmDescentWorld(connection, state);
        _log.Info($"{connection} match: SynchronizedTeleport.Release sent (chute mount {(state.MountRequested ? "confirmed" : "not requested")})");
        // Two client-originated proofs the drop is real (docs/35 §3 rows 6 and 8): the AutoMount
        // echo, whose absence would leave MountRequested false and the player permanently immune to
        // gas; and the first channel-2 packet, whose absence means the client is frozen in the air —
        // the failure that otherwise looks exactly like a successful drop.
        ExpectClient(
            connection,
            state,
            ClientMilestone.ParachuteAutoMountEcho,
            "SynchronizedTeleport.Release",
            key: state.ChuteGuid);
        ExpectClient(
            connection,
            state,
            ClientMilestone.DescentMovement,
            "SynchronizedTeleport.Release");
    }

    private SelfRecord CreateSelfRecord(GatewaySessionState state, Vector4 position) =>
        new()
        {
            Guid = state.Guid,
            // docs/100 §7, behind CRANBERRY_SELF_TRANSIENT_ID (default ON). The client copies this
            // varint (+0xe0) to world+0x324f8 at FUN_140af3950 L545 and uses it as its answer to
            // "is this packet about me?" for the rest of the session. It carried 0 until this lane,
            // and 0 is wrong three ways: it makes the id 0 unusable for a peer only by accident
            // (TransientIdTable allocates from 16 anyway), it makes any 11 01 1e damage record with
            // a zero field B look like self-damage, and world+0x324f8's own "no network id"
            // sentinel DAT_143f6842c may or may not equal 0. Writing 1 removes all three questions.
            // The record's LENGTH does not move — both 0 and 1 are one-byte client varints, 00 and
            // 04 — so the wave-5 login burst keeps its packet sizes; only that one byte differs.
            TransientId = _options.Peers.SelfTransientId ? TransientIdTable.LocalPlayer : 0,
            ActorDefinitionId = _options.ModelIdFor(state.Gender),
            TextA = state.Visuals.HeadModel,
            TextB = state.Visuals.HairModel,
            // FUN_140acccd0 copies +0xb9f0 into the actor descriptor; first-person arms then
            // resolve this value through DynamicAppearance as the selected skin shader group.
            ValueE = state.Visuals.SkinToneId,
            // The first repeated gender field in the self schema (player+0x250).
            Field8 = state.Gender,
            Position = position,
            Identity = new SelfIdentity { Name = state.CharacterName },
            Resources = CharacterResource.Starter,
            // SendSelf clears the native skin manager, including its current F-key map.
            // Carry defaults on both admission and zoning, not just in the menu's ac23 burst.
            Emotes = AugustEmotes.DefaultSlots,
            // docs/62 §3 stage 2, ON by default (D289). Blob offset 0x11a is the recipe list, and
            // per DIAG-recipes-vehicles §A this is the ONLY delivery that publishes a UI row: the
            // August crafting window reads its rows exclusively from this list (FUN_141513580 <-
            // FUN_140db5680), while 0x26 09 Recipe.List only fills the container. It rides on EVERY
            // self record CreateSelfRecord builds — the menu one and the ClientBeginZoning one —
            // because FUN_141513580 clears the datasources first, so a later empty list would wipe a
            // populated window. Effective still refuses this without the 0x26 09 packet.
            Recipes = _options.Crafting.Effective.SendRecipesInSelfRecord
                ? CraftingCatalog.ToRecords(
                    _options.Crafting.Effective.RetailRecipes,
                    _options.Crafting.Effective.SentinelFields)
                : [],
            // docs/103 §3 Door A, off unless CRANBERRY_CONSOLE_SELF_FLAG=1 and this link is Owner:
            // one bool at +0x106b9, believed to be what the client's own Tilde binding checks
            // before it opens the debug console. The record's length is the same either way.
            FlagI = ConsoleSelfFlag(state),
        };

    private void SendCharacterAppearance(
        SoeConnection connection,
        GatewaySessionState state,
        string reason)
    {
        StopReloadIfWeaponChanged(connection, state);
        TrackWeaponDraw(state);
        SendInventoryActionState(connection, state);
        if (state.Match is MatchStep.Lobby or MatchStep.InMatch && state.Inventory is not null)
            SendMovementStats(connection, state, reason);
        IReadOnlyList<CharacterEquipmentAttachment> attachments =
            AugustWorldEquipmentVisuals.BuildAttachments(
                state.Visuals,
                state.Wardrobe,
                _dynamicAppearance,
                state.WorldEquipment,
                _options.Skins.OrderAppearanceRowsByGender,
                _options.Skins.SendWardrobeShaderGroups);

        if (state.Match == MatchStep.Menu)
        {
            if (state.MenuView != "kotkappearancegearfeet"
                && !state.Wardrobe.WornSnapshot().Any(entry => entry.EquipmentSlotId == BodySlots.Feet))
            {
                var stealth = state.Visuals.StarterOutfit.First(a => a.SlotId == BodySlots.Feet);
                attachments = attachments.Select(a => a.SlotId == BodySlots.Feet ? stealth : a).ToArray();
            }
            uint previewSlot = state.MenuView switch
            {
                "kotkappearancegearhead" => BodySlots.Head,
                "kotkappearancegearback" => BodySlots.Backpack,
                "kotkappearancegearfeet" => BodySlots.Feet,
                _ => 0,
            };
            var selectedPreview = state.MenuApparelPreview is { } clicked && clicked.EquipmentSlotId == previewSlot
                ? clicked
                : state.Wardrobe.Snapshot().LastOrDefault(e => e.EquipmentSlotId == previewSlot
                    && (previewSlot != BodySlots.Head || AugustWardrobeCatalog.IsPickupOnly(e)));
            if (previewSlot != 0 && selectedPreview.RewardItemId != 0)
            {
                string model = selectedPreview.ModelNameFor(state.Gender);
                if (!string.IsNullOrWhiteSpace(model))
                {
                    var preview = new CharacterEquipmentAttachment(model, previewSlot,
                        TextureAlias: selectedPreview.TextureAlias,
                        ShaderParameterGroupId: _dynamicAppearance?.ShaderGroupFor(selectedPreview.RewardItemId, state.Gender) ?? 0,
                        AppearanceIds: _dynamicAppearance?.AppearanceRowsFor(selectedPreview.RewardItemId, state.Gender));
                    attachments = [.. attachments.Where(a => a.SlotId != previewSlot), preview];
                }
            }
        }

        if (state.Match == MatchStep.Menu && state.Inventory is null
            && (state.MenuWeaponPreview is not null || state.MenuWeaponPreviewCategoryId != 0))
        {
            uint prototype = state.MenuWeaponPreview?.CategoryPrototypeId ?? state.MenuWeaponPreviewCategoryId;
            TryResolveMenuWeaponMesh(prototype, state.Gender, out string baseModel, out uint baseShader);
            string model = state.MenuWeaponPreview?.ModelNameFor(state.Gender)
                ?? baseModel;
            if (!string.IsNullOrWhiteSpace(model))
            {
                var preview = BuildActiveHandAttachment(prototype, state, model, baseShader);
                attachments = [.. attachments.Where(a => a.SlotId != BodySlots.RightHand), preview];
            }
        }

        // docs/41 §I1(d): with the container model on, the equipment-slot list is the projection of
        // the inventory's own body slots — one row per equipped item, and the item's own MODEL_NAME
        // substituted into that slot's attachment. Before inventory bootstrap this branch is
        // skipped. Afterwards the equipped body slots become authoritative, allowing a dropped
        // garment to disappear from the full dress.
        if (state.Inventory is PlayerInventory inventory
            && (inventory.EquipmentSlots.Count > 0 || inventory.ManagedEquipmentSlots.Count > 0))
        {
            var rows = new List<EquipmentSlotRow>(inventory.EquipmentSlots.Count);
            // Once inventory owns an apparel slot, an empty slot means bare/default — it must not
            // be repopulated from the immutable starter/wardrobe baseline after Drop or Remove.
            // Keep a baseline attachment while the slot is occupied: selected wardrobe items may
            // have no loot-roster mesh row, and their already-correct baseline is then the resolver.
            var dressed = new List<CharacterEquipmentAttachment>(attachments.Where(attachment =>
                attachment.SlotId == BodySlots.RightHand
                || !inventory.ManagedEquipmentSlots.Contains(attachment.SlotId)
                || inventory.EquipmentSlots.ContainsKey(attachment.SlotId)));
            var worn = new List<AugustWornItem>(inventory.EquipmentSlots.Count);
            foreach (KeyValuePair<uint, InventoryItemInstance> equipped in inventory.EquipmentSlots)
            {
                // docs/98 §5.5, guard G2: with the draw sequence on, body slot 7 is bound by
                // 94 02 SetCharacterEquipmentSlot and by nothing else. A slot-7 row on the
                // whole-character 94 01 is the exact shape that froze the owner on 2026-08-31, and
                // the starter-weapon grant put one there in both harness sessions of 2026-09-02.
                // The ATTACHMENT half is untouched — ApplyActiveHandVisual still puts the mesh in
                // the hand, which is what docs/95 §0 calls harmless and both reference sessions
                // carry. With the sequence off nothing else would ever bind the slot, so the row
                // stays and ActiveHandRowGuard keeps its old say over it.
                if (equipped.Key != BodySlots.RightHand || !inventory.Options.UseWieldSequence)
                {
                    rows.Add(new EquipmentSlotRow(equipped.Key, equipped.Value.Guid));
                }

                worn.Add(new AugustWornItem(
                    equipped.Key,
                    equipped.Value.DefinitionId,
                    equipped.Value.Fact.ModelName,
                    equipped.Value.SkinOverrideDefinitionId));
            }

            // docs/54 §3. The equipment-slot row binds the item into the inventory panel and already
            // worked (docs/46); this is what hangs a mesh on the character. The loop that used to be
            // here resolved the third-person mesh only from ClientItemDefinitions.MODEL_NAME, which
            // is blank for all 35 wearables in the loot roster — so every wearable took the
            // `continue` and the packet carried a constant "5 meshes" through five pickups. That is
            // the owner's "some go into the slots but that's it. They don't show on me."
            // AugustWornVisuals consults the appearance table's own ModelId column first and the
            // sheet second; it dresses body slots 1, 10 and 100 only, and it can never touch slot 7
            // (docs/45, regression guard 5 — it writes attachments, never equipment-slot rows, and
            // refuses the active hand twice over).
            AugustWornVisuals.Dress(
                dressed,
                worn,
                state.Gender,
                RowsForThisBody(state, inventory),
                // docs/80 edit 1: every IS_EQUIPMENT body slot except the active hand, so the
                // stowed guns on 76/77/80 finally get a mesh. CRANBERRY_STOWED_MESHES=0 puts the
                // allow-list back to {1, 10, 100} - the wire docs/74 W1 measured.
                _options.Skins.DressStowedWeapons
                    ? AugustWornVisuals.AttachableBodySlots
                    : AugustWornVisuals.AdditiveBodySlots,
                // docs/80 edit 3: the item own colourway, per group and only when the table this
                // session transmits actually defines it. Zero extra payload bytes for the nine
                // roster weapons; still 0 for the 26 wearables the filter discards (docs/54 I4).
                _options.Skins.SendWornShaderGroups && _dynamicAppearance is not null
                    ? _dynamicAppearance.DefinesShaderGroup
                    : null,
                // docs/106 §13 (D283): the selected skin for a picked-up item, on every worn slot
                // and every stow peg. D225 reached the hand and nothing else, so the owner's
                // backpack, helmets and the three guns on his back all went out in their base
                // colourway on 2026-09-03 19:5x while eighteen selections sat unused.
                // CRANBERRY_WORN_SKINS_IN_WORLD=0 puts the base rows back.
                WornSkinRewardResolver(state),
                WornSkinShaderGroupResolver(state));

            ApplyActiveHandVisual(dressed, inventory, state);
            ApplyHoodState(dressed, inventory, state);

            // HONEST LABEL (rewritten by the wave-5 verify pass — the wave-4 text below it
            // described the deleted MeshFor/ReplaceAttachmentSlot loop and had become the OPPOSITE
            // of what this code does, three lines above the log line that reads it).
            //
            // With WieldFirstWeapon off a stowed weapon still does not vanish from the EQUIPMENT-SLOT
            // rows: it moves to its PASSIVE_EQUIP_SLOT_ID (106 for the machete, 76/78 for long guns
            // and pistols). What changed is the ATTACHMENT half. AugustWornVisuals.AttachableBodySlots
            // is {1, 10, 100} and Dress skips every other slot, so a passive slot is no longer
            // dressed at all: nothing appends a 7th attachment any more, and the count stays at the
            // constant 5 or 6 that every packet this client accepted carried. The one roster item
            // that loses an attachment it had at 249e129 is item 83 Machete (slot 106,
            // Weapons_Machete01_3P.adr, which does ship) — deferred deliberately by docs/54 §I3
            // together with 76/77/78, and recorded there as a consequence rather than left implicit.
            //
            // The slot-7 argument is unchanged and is the one that matters: docs/45 §3's fault reads
            // the ACTIVE HAND slot id (*(u32*)(DAT_143f69ba0+0x228)) and only that slot, so a passive
            // slot cannot enter it. If a gun pickup still kills the client, read the capture's row
            // list before blaming the fire group.

            if (!SendDressTunnel(
                    connection,
                    state,
                    writer => new SetCharacterEquipmentWithSlots(
                        state.Guid,
                        Slots: rows,
                        Attachments: dressed,
                        // WAVE 10: the ledger, not null. ActiveHandRowGuard still drops every
                        // slot-7 row it is not shown positive evidence for - the clearance can
                        // only ever permit ONE item guid, and only one this session has already
                        // written a resolvable fire group for (docs/58 11 stage 3). With stage 3
                        // off, WeaponSession.Clearance IS null and this is byte-for-byte the
                        // wave-5 behaviour.
                        Clearance: state.Weapons.Clearance).WriteTo(writer),
                    reason,
                    dress: dressed,
                    equipmentRows: ActiveHandRowGuard.Filter(rows, state.Weapons.Clearance)))
            {
                return;
            }

            // docs/45: SetCharacterEquipmentWithSlots.WriteTo runs every row through
            // ActiveHandRowGuard, which drops body slot 7 (RHand) — an equipment-slot row for the
            // active hand hard-crashes the August client (5 of 5 accepted without one, 4 of 4 killed
            // with one). Log the FILTERED list, so the line is the wire and not the intent.
            // The ledger remains useful for ItemAdd/WeaponDefinitions ordering, but it is not yet
            // live proof that an August 94/01 RHand row is safe. Filter the log with the SAME
            // clearance the writer just used, or the log resumes lying in the other direction the
            // moment stage 3 clears a row.
            IReadOnlyList<EquipmentSlotRow> onWire =
                ActiveHandRowGuard.Filter(rows, state.Weapons.Clearance);
            _log.Info($"{connection} sent SetCharacterEquipmentWithSlots guid={state.Guid} "
                + $"({dressed.Count} meshes, {onWire.Count} equipment-slot row(s) → body slot(s) "
                + $"{string.Join(',', onWire.Select(row => row.SlotId))}, "
                + $"{state.Wardrobe.Snapshot().Count} wardrobe selections, {reason}; "
                + $"dresses {state.Dress.Counters()})"
                + (onWire.Count == rows.Count
                    ? string.Empty
                    : $" — {rows.Count - onWire.Count} row(s) for body slot "
                        + $"{ActiveHandRowGuard.ActiveHandSlotId} dropped by the docs/45 guard"));
            return;
        }

        if (state.HeldWeaponItemGuid != 0)
        {
            // docs/36 §W3: swap the "Fists" mesh out of slot 7 for the weapon's own _3P attachment
            // and carry the equipment-slot row that binds the granted inventory instance to RHand.
            // SetCharacterEquipmentWithSlots is byte-identical to SetCharacterEquipment when the
            // slot list is empty, so the only new bytes on the wire are the 24-byte row — which is
            // deliberately the smallest possible delta from the live-proven 726-byte packet.
            // The substitution is NOT done through AugustWorldEquipmentState: that type refuses
            // anything outside the pickup-only wardrobe categories, and a rifle is not a skin.
            var withWeapon = new List<CharacterEquipmentAttachment>(attachments.Count + 1);
            foreach (CharacterEquipmentAttachment attachment in attachments)
            {
                if (attachment.SlotId != AugustHeldWeapon.RightHandSlotId)
                {
                    withWeapon.Add(attachment);
                }
            }

            withWeapon.Add(AugustHeldWeapon.Attachment());
            ulong heldGuid = state.HeldWeaponItemGuid;
            if (!SendDressTunnel(
                    connection,
                    state,
                    writer => new SetCharacterEquipmentWithSlots(
                        state.Guid,
                        Slots: [AugustHeldWeapon.SlotRow(heldGuid)],
                        Attachments: withWeapon,
                        // WAVE 10: see the container-model branch above. The guard is narrowed by
                        // the ledger, not switched off.
                        Clearance: state.Weapons.Clearance).WriteTo(writer),
                    reason,
                    dress: withWeapon))
            {
                return;
            }

            // D29 / docs/45: the row this branch builds is a BODY SLOT 7 row, and
            // SetCharacterEquipmentWithSlots.WriteTo runs every row through ActiveHandRowGuard — so
            // what actually goes out carries ZERO rows. Log the FILTERED list, exactly as the
            // container-model branch above does: a log line that claims a slot-7 row the capture
            // does not contain is the log-versus-wire divergence that hid docs/32 for hours.
            IReadOnlyList<EquipmentSlotRow> heldOnWire = ActiveHandRowGuard.Filter(
                [AugustHeldWeapon.SlotRow(heldGuid)],
                state.Weapons.Clearance);
            _log.Info($"{connection} sent SetCharacterEquipmentWithSlots guid={state.Guid} "
                + $"({withWeapon.Count} meshes incl. {AugustHeldWeapon.AttachmentModelName} in slot "
                + $"{AugustHeldWeapon.RightHandSlotId}, {heldOnWire.Count} equipment-slot row(s) "
                + $"for item {heldGuid}, "
                + $"{state.Wardrobe.Snapshot().Count} wardrobe selections, {reason})"
                + (heldOnWire.Count == 0
                    ? $" — the body-slot-{ActiveHandRowGuard.ActiveHandSlotId} row was dropped by the "
                        + "docs/45 guard and is NOT on the wire"
                    : $" — the body-slot-{ActiveHandRowGuard.ActiveHandSlotId} row IS on the wire, "
                        + "cleared by fire group "
                        + $"{state.Weapons.Ledger.DeliveredFireGroupId(heldGuid)?.ToString() ?? "?"}"));
            return;
        }

        if (!SendDressTunnel(
                connection,
                state,
                writer => new SetCharacterEquipment(
                    state.Guid,
                    Attachments: attachments).WriteTo(writer),
                reason,
                dress: attachments))
        {
            return;
        }

        // The UnsetCharacterEquipmentSlot burst that used to follow is REMOVED (regression
        // 2026-08-29, docs/32). It does not appear anywhere in the last known-good capture
        // (wire-20260829-150206.txt), which carries only SetCharacterEquipment plus the worn
        // skin-item rows. It was added after 15:02 and is the only genuinely new packet on this
        // path; with it the client stops answering after ClientBeginZoning — hanging on
        // LoadingScreenWindow, or exiting with error G10 when the cleared set includes slot 1
        // (which happens when no wardrobe selections are active). SetCharacterEquipment already
        // carries the full attachment list, so absent slots are implied by omission and do not
        // need an explicit clear.
        _log.Info($"{connection} sent SetCharacterEquipment guid={state.Guid} "
            + $"({attachments.Count} meshes, "
            + $"{state.Wardrobe.Snapshot().Count} wardrobe selections, {reason}; "
            + $"dresses {state.Dress.Counters()})");
    }

    /// <summary>
    /// docs/36 §W3c: put a real weapon in the player's hand at match start, so the client will
    /// finally speak its own combat packets. In 96 captured sessions it has never emitted a single
    /// <c>0x82 WeaponBase</c> packet, because slot 7 has always carried <c>Weapon_Empty.adr</c>
    /// ("Fists"); the c2s fire layout is send-only in the binary and cannot be ported from anywhere
    /// (docs/16 §5, docs/20, docs/00), so making the client emit it is the derivation.
    /// <para>
    /// Order matters: the <c>ItemAdd</c> goes first, because the equipment-slot row names an
    /// inventory item guid and an item the client has never been told about is at best ignored
    /// (<c>FUN_140dbf5b0</c>). Idempotent — a second call while a weapon is already held is a no-op.
    /// </para>
    /// <para>
    /// <paramref name="dress"/> false leaves the <c>SetCharacterEquipmentWithSlots</c> to the
    /// caller: the landing burst sends its own dress one line later, and two of them would put a
    /// redundant 750-byte equipment packet on a proven-safe path for nothing.
    /// </para>
    /// <b>UNVERIFIED</b> that the slot row is <em>sufficient</em> for firing as opposed to merely
    /// identifying the weapon; if the rifle renders but the idle stays unarmed,
    /// <c>Character.UpdateActiveWieldType (0f 28)</c> is the packet to recover next (docs/36 §W4).
    /// </summary>
    /// <returns>
    /// The granted instance when a WIELDED starter weapon still owes its draw — the caller must send
    /// its own dress first and then hand this back to <see cref="DrawStarterWeapon"/>, so the
    /// <c>94 02</c> binding is the LAST equipment packet of the burst (docs/95 §0, docs/98 §5.5).
    /// Null when nothing was granted, when the grant did not wield, or when the draw has already
    /// been sent here.
    /// </returns>
    private InventoryItemInstance? GiveHeldWeapon(
        SoeConnection connection,
        GatewaySessionState state,
        string reason,
        bool dress = true)
    {
        if (!_options.GiveStarterWeapon || state.HeldWeaponItemGuid != 0)
        {
            return null;
        }

        // With the container model on, the starter weapon is granted the same way a picked-up one is,
        // so it lands in a real weapon-wheel slot and in RHand by the client's own rule rather than
        // by a hard-coded slot 7 (docs/41 §5b).
        if (EnsureInventory(connection, state, reason) && state.Inventory is PlayerInventory inventory)
        {
            InventoryPlacement plan = inventory.TryPickUp(
                _options.StarterWeaponItemDefinitionId, 1, out InventoryItemInstance? granted);
            if (granted is null)
            {
                _log.Warn($"{connection} weapon: the inventory refused starter item "
                    + $"{_options.StarterWeaponItemDefinitionId} ({plan.Kind}, {plan.Rule}) — no grant");
                return null;
            }

            // docs/98 §5.5: when the draw sequence is on, the grant is a DRAW, not a dress. The
            // 86 04 wheel, the ability manager and the slot-7 binding all ride WieldSequence, so
            // none of them is sent here — exactly as the ordinary pickup path does it (§5).
            bool drawsSequence = plan.Wielded && inventory.Options.UseWieldSequence;

            SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, granted.ToRecord(state.Guid)));
            if (plan.Kind == InventoryPlacementKind.LoadoutSlot)
            {
                SendTunnel(connection, writer => inventory.ToLoadoutSlot(granted).WriteTo(writer));
                if (plan.Wielded && !drawsSequence)
                {
                    // Match the ordinary pickup path: the rifle has replaced Fists, so the HUD
                    // needs the authoritative current slot and the refreshed ability manager
                    // before the landing dress exposes the active-hand visual. The current slot
                    // rides in the 86 04 trailer (S6 §7.1), not in an 86 07 (§7.4).
                    SendLoadoutSlots(connection, inventory);
                }
            }

            foreach (InventoryContainer container in inventory.Containers.Values)
            {
                SendTunnel(connection, writer => inventory.ToUpdate(container).WriteTo(writer));
            }

            state.HeldWeaponItemGuid = granted.Guid;
            _log.Info($"{connection} weapon: granted item {_options.StarterWeaponItemDefinitionId} "
                + $"as instance {granted.Guid} — {plan.Rule} (loadout slot {plan.LoadoutSlotId}, body "
                + $"slot {plan.EquipmentSlotId}{(plan.Wielded ? ", WIELDED" : string.Empty)}, {reason})");

            if (dress)
            {
                SendCharacterAppearance(connection, state, $"starter weapon ({reason})");
            }

            if (drawsSequence)
            {
                // The draw has to be the LAST equipment packet of the burst: a whole-character
                // 94 01 after the 94 02 would re-state the character's slot list and undo the very
                // binding the sequence exists to make. When this call sends its own dress the draw
                // can go out here; when the caller sends one (dress: false, the landing burst) the
                // instance goes back to it and DrawStarterWeapon runs after that dress.
                if (dress)
                {
                    DrawStarterWeapon(connection, state, granted, reason);
                    return null;
                }

                return granted;
            }

            if (plan.Wielded)
            {
                // (b) of S6 §7.3 - the starter grant is a draw like any other.
                SendWeaponStance(connection, $"starter weapon ({reason})");
            }

            return null;
        }

        ulong itemGuid = state.Loot.NextItemGuid();
        var record = new InventoryItem(
            DefinitionId: _options.StarterWeaponItemDefinitionId,
            ItemGuid: itemGuid,
            Count: 1,                       // MAX_STACK_SIZE = 1 on row 2425
            OwnerGuid: state.Guid,
            ContainerGuid: _options.GroundLootContainerGuid,
            ContainerDefinitionId: _options.GroundLootContainerDefinitionId,
            SlotId: state.NextInventorySlot++);
        int starterGrantLength = ItemAddLength(state, record);
        SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, record));
        state.HeldWeaponItemGuid = itemGuid;

        _log.Info($"{connection} weapon: granted item {_options.StarterWeaponItemDefinitionId} "
            + $"('{AugustHeldWeapon.AttachmentModelName}') as instance {itemGuid} in slot "
            + $"{record.SlotId} (ItemAdd {starterGrantLength} B, {reason}) — reasserting equipment; the "
            + $"body-slot-{ActiveHandRowGuard.ActiveHandSlotId} row is WITHHELD by the docs/45 guard, "
            + "so the mesh goes in the hand but nothing binds the item to it");

        if (dress)
        {
            // The dress carries the substitution; sending it is what puts the mesh in hand.
            SendCharacterAppearance(connection, state, $"starter weapon in RHand ({reason})");
        }

        return null;
    }

    /// <summary>
    /// docs/98 §5.5 — <b>the starter weapon is DRAWN, never dressed.</b> Both harness sessions of
    /// 2026-09-02 carried a <c>94 01 SetCharacterEquipment</c> whose row set was
    /// <c>[2, 3, 4, 5, 7, 28, 29]</c>: a body-slot-7 row on the whole-character packet, guard
    /// <b>G2</b>, Fatal, and the exact shape that froze the owner on 2026-08-31. It came from the
    /// starter grant, whose <c>plan.Wielded</c> put the item in <c>EquipmentSlots[7]</c> and let the
    /// landing dress project a row out of it.
    /// <para>
    /// The cure is the one docs/95 already ships for a pickup: the dress goes out with no slot-7
    /// row, and the binding is the last of <c>WieldSequence</c>'s eight packets, a
    /// <c>94 02 SetCharacterEquipmentSlot</c> after the ability manager. This is the same
    /// <see cref="TrySendWieldSequence"/> call the pickup path makes, with the same fallback.
    /// </para>
    /// <param name="granted">The instance <see cref="GiveHeldWeapon"/> handed back.</param>
    /// </summary>
    private void DrawStarterWeapon(
        SoeConnection connection,
        GatewaySessionState state,
        InventoryItemInstance granted,
        string reason)
    {
        if (state.Inventory is not PlayerInventory inventory)
        {
            return;
        }

        // previousAbilityId 0: the starter grant is the first thing this session ever puts in the
        // hand, so there is no ability to uninitialise and no body slot to vacate — Z1 sends
        // neither step in that case (docs/95 §1, WieldSequence steps 1 and 2).
        if (TrySendWieldSequence(connection, state, inventory, granted, previousAbilityId: 0))
        {
            return;
        }

        // The sequence refused (no third-person mesh, or ActiveHandRowGuard would not clear the
        // binding). Fall back to what the grant used to do — the wheel and the stance — but NOT to
        // a dress carrying a slot-7 row: that packet is G2 whatever produced it.
        SendLoadoutSlots(connection, inventory);
        SendWeaponStance(connection, $"starter weapon, undrawn ({reason})");
        _log.Warn($"{connection} weapon: the starter grant could not be DRAWN (item "
            + $"{granted.DefinitionId} instance {granted.Guid}, {reason}) — the mesh is in the hand "
            + "but no 94 02 binds it, and no slot-7 row is put on the 94 01 to compensate (docs/98 "
            + "§5.5, guard G2)");
    }

    /// <summary>
    /// Arms a client-originated milestone and makes sure the poll chain is running (docs/35 §5b).
    /// Everything the server sends that the client must answer goes through here, so no arm site can
    /// forget to start the pump.
    /// </summary>
    private void ExpectClient(
        SoeConnection connection,
        GatewaySessionState state,
        ClientMilestone milestone,
        string lastSent,
        ulong key = 0,
        bool first = false)
    {
        if (first)
        {
            state.Watchdog.ExpectFirst(milestone, lastSent, key);
        }
        else
        {
            state.Watchdog.Expect(milestone, lastSent, key);
        }

        if (!state.WatchdogPumping)
        {
            // Only claim the pump is running if the deferral was actually accepted. Setting the flag
            // first meant that a service with no dispatcher (Post is null) latched it true forever
            // and silently disabled the watchdog for the whole session.
            state.WatchdogPumping = Later(connection, WatchdogPollMs, () => PumpWatchdog(connection, state));
        }
    }

    /// <summary>
    /// Records a client-originated milestone arrival (docs/35 §5e) and logs the two cases worth
    /// seeing: a late arrival (the client recovered, and <c>IsStalled</c> is now cleared) and an
    /// arrival nobody was waiting for. Everything else is silent — the packet's own handler already
    /// logs it.
    /// </summary>
    private void NoteMilestone(
        SoeConnection connection,
        GatewaySessionState state,
        ClientMilestone milestone,
        ulong key = 0)
    {
        MilestoneArrival arrival = state.Watchdog.Observe(milestone, key);
        if (arrival.WasExpected && arrival.WasLate)
        {
            _log.Warn($"{connection} client-progress watchdog: {milestone} arrived LATE after "
                + $"{arrival.LatencyMs} ms — the client recovered; the session is no longer stalled");
        }
    }

    /// <summary>
    /// The watchdog poll (docs/35 §5b): one pass per second on the listener thread, one loud WRN per
    /// missed milestone, then re-arm only while something is still outstanding. Like the gas pump it
    /// rides <see cref="Later"/>, so it is dropped the moment the link closes and can never outlive
    /// the session — and <c>ClientProgressWatchdog</c> itself owns no timer, so there is nothing
    /// else that could leak.
    /// </summary>
    private void PumpWatchdog(SoeConnection connection, GatewaySessionState state)
    {
        while (state.Watchdog.TryTakeExpired(out ClientProgressStall stall))
        {
            _log.Warn($"{connection} {stall}");
        }

        if (!state.Watchdog.NeedsPolling)
        {
            state.WatchdogPumping = false;
            return;
        }

        Later(connection, WatchdogPollMs, () => PumpWatchdog(connection, state));
    }

    private void HandleSkinItemRequest(
        SoeConnection connection,
        GatewaySessionState state,
        ReadOnlySpan<byte> payload)
    {
        SkinItemSelectionRequest request = SkinItemSelectionRequest.Parse(payload);
        if (state.Match != MatchStep.Menu
            && (state.InventorySkinTargetItemGuid != 0
                || AugustSkinCatalog.Weapons.Any(s => s.CategoryPrototypeId == request.CategoryPrototypeId)))
        {
            ApplyInventoryWeaponSkin(connection, state, request);
            return;
        }
        if (request.SubOpcode != SkinItemSelectionRequest.RequestUnsetSkinItem
            && _economy is not null
            && (!AugustWardrobeCatalog.TryResolveClicked(request.ClickedId, out var ownedSkin)
                || !ReadAccountEconomy(state).Owns(ownedSkin.AccountItemId)))
        {
            _log.Warn($"{connection} refused unowned skin selection {request.ClickedId}");
            return;
        }
        var previousSelections = state.Wardrobe.Snapshot();
        if (!state.Wardrobe.TryApply(
            request,
            out AugustSkinCatalogEntry selected,
            out bool removed,
            out string reason))
        {
            _log.Warn($"{connection} refused Items skin request 0x{request.SubOpcode:x2}: {reason}");
            return;
        }

        // docs/80 edit 9 (the owner SaveSkins): after EVERY accepted apply, set and unset alike,
        // and with the WHOLE snapshot - saving one pick would let a cleared category leave a stale
        // row on disk. The write itself is coalesced onto a background thread, so the receive
        // thread never takes a disk barrier here.
        _wardrobeStore.Save(state.Guid, state.Wardrobe);

        if (!removed && request.SubOpcode != SkinItemSelectionRequest.RequestUnsetSkinItem)
        {
            // The retail click answer, and it is exactly one row: the friend server replies to
            // ad/31 with a single ad/23 and nothing else - twelve clicks between 16:49:00.061 and
            // 16:49:13.353 in C:\Project\out\ingest-admin-20260822-part1\packets_1118_62892.log
            // and not one 95/01 among them.
            SendTunnel(connection, writer => new SetSkinItem(
                state.Guid,
                selected.CategoryPrototypeId,
                selected.AccountItemId,
                CollectionId: SkinCollectionFor(selected)).WriteTo(writer));

            if (!previousSelections.Any(e => e.CategoryPrototypeId == selected.CategoryPrototypeId
                    && e.AccountItemId == selected.AccountItemId))
                RefreshCarriedWeaponSkin(connection, state, selected.CategoryPrototypeId);

            bool replacing = previousSelections.Any(e => e.AccountItemId != selected.AccountItemId
                && (e.CategoryPrototypeId == selected.CategoryPrototypeId
                    || (AugustWardrobeCatalog.ProjectsOntoStarterBody(e)
                        && AugustWardrobeCatalog.ProjectsOntoStarterBody(selected)
                        && e.EquipmentSlotId == selected.EquipmentSlotId)));
            if (replacing && state.Match == MatchStep.Menu)
                SendSkinManagerState(connection, state);
            if (state.Match == MatchStep.Menu && !AugustWardrobeCatalog.IsApparel(selected))
                state.MenuWeaponPreview = selected;
            else if (state.Match == MatchStep.Menu)
                state.MenuApparelPreview = selected;

            // D222 (docs/111), the owner's defect 2: "switching between skins in the
            // appearance/gear screens does NOT change the character in real time". The row above
            // is enough for the FRIEND server because its 95/01 never carries a wardrobe garment -
            // all six of its menu dresses hold the same five starter meshes and differ only in the
            // escrow account-item guids on their equipment-slot rows (packets_1118_62892.log lines
            // 153, 341, 379, 456, 468, 540). Cranberry bakes the chosen garment into the dress
            // instead, so here the dress IS the look, and a click that does not re-send it cannot
            // be seen until the Gear editor closes - which is precisely what the owner measured
            // (17:53:40.677 click -> 17:53:42.954 "committed 14 wardrobe selections at Gear editor
            // close", the first packet whose bytes changed).
            //
            // This sends the same dress the close path sends, and only when the pick actually
            // reaches the lobby body: a weapon preset or a pickup-only helmet changes no menu
            // mesh, WornSnapshot omits it, and the suppressor would drop the packet anyway.
            // Ordering is the Gear-editor shape (ac/24 then 94/01), never D211's forbidden
            // 94/01 -> ac/24 -> 94/01. CRANBERRY_SKIN_LIVE_PREVIEW=0 restores the bare echo.
            if ((replacing && state.Match == MatchStep.Menu)
                || (_options.Skins.LiveSkinPreview
                    && (AugustWardrobeCatalog.ProjectsOntoStarterBody(selected)
                        || state.Match == MatchStep.Menu)))
            {
                SendCharacterAppearance(connection, state, "skin click");
                if (state.Match == MatchStep.Menu && state.MenuWeaponPreview is not null)
                    SendTunnel(connection, writer => new WeaponStance(state.Guid, 1).WriteTo(writer));
            }

            _log.Info($"{connection} equipped testing skin in real time: {reason}");
            return;
        }

        if (state.MenuWeaponPreview is { } preview && preview.CategoryPrototypeId == request.CategoryPrototypeId)
        {
            state.MenuWeaponPreview = null;
            state.MenuWeaponPreviewCategoryId = request.CategoryPrototypeId;
        }

        if (state.MenuApparelPreview is { } apparel && apparel.CategoryPrototypeId == request.CategoryPrototypeId)
            state.MenuApparelPreview = null;

        // Unset has no useful single-row echo. Rebuild the manager state, then make the
        // server-authored outfit the final visual writer so the slot returns to its starter mesh.
        SendSkinManagerState(connection, state);
        RefreshCarriedWeaponSkin(connection, state, request.CategoryPrototypeId);
        SendCharacterAppearance(connection, state, "skin unset");
        _log.Info($"{connection} applied skin unset: {reason}");
    }

    private ReferenceData CreateDynamicAppearanceReference() =>
        _dynamicAppearance?.CreateCompressedReferenceData()
        ?? DynamicAppearanceReference.CreateStarterAppearance();

    private void SendSkinCatalogue(SoeConnection connection, GatewaySessionState state)
    {
        SendTunnel(connection, writer =>
            new SetAccountItemManager(IncludeCatalogRecords: _economy is null,
                OwnedItems: _economy is null ? null : EconomyMenuInventory.VisibleItems(
                    ReadAccountEconomy(state).Items, new EconomyPurchaseService(_economy).Offers)).WriteTo(writer));
        SendTunnel(connection, writer => new AccountItemManagerStateChanged().WriteTo(writer));
        SendSkinManagerState(connection, state);
    }

    private void SendSkinManagerState(SoeConnection connection, GatewaySessionState state)
    {
        // THE LAST-WRITER LAW, and the one place identical-dress suppression must not reach.
        //
        // The owner measured the purple suit: a skin-manager burst with no equipment packet behind
        // it, and 8 ms later his client had composited a Chest_Jacket_Suit and Legs_Pants_Slacks
        // the server never sent - the lilac slacks in his own recording at t=22.5 s. His cure is
        // that ANY manager burst ends with an equipment writer. Cranberry already obeys it at all
        // three of its manager sites (docs/80 s3.1), and every one of those sites routes through
        // this method. Invalidate suppression while retaining the existing actor's slot baseline:
        // after a manager burst its safe occupied slots must be reasserted even when unchanged.
        // Lifecycle resets still Forget the baseline and require the complete actor dress.
        state.Dress.InvalidateAfterSkinManager();

        IReadOnlyDictionary<uint, ulong>? ownedInstances = null;
        if (_economy is not null)
        {
            var account = ReadAccountEconomy(state);
            ReconcileAccountSelections(state, account);
            ownedInstances = account.Items.GroupBy(item => item.AccountItemId)
                .ToDictionary(group => group.Key, group => group.Min(item => item.InstanceId));
        }

        IReadOnlyList<AugustSkinCatalogEntry> worn = ActualWornSkins(state);
        SendTunnel(connection, writer =>
            new SetSkinItemManager(IncludeCatalog: true, WornItems: worn,
                SelectedItems: state.Wardrobe.Snapshot(), OwnedInstanceIds: ownedInstances,
                Emotes: AugustEmotes.DefaultSlots).WriteTo(writer));
        SendTunnel(connection, writer => new SetCurrentSkinItemCollection(
            SelectedItems: state.Wardrobe.Snapshot().Where(AugustWardrobeCatalog.IsApparel).ToArray(),
            OwnedInstanceIds: ownedInstances, Emotes: AugustEmotes.DefaultSlots).WriteTo(writer));
        SendWornSkinItems(connection, state, "skin-manager state");
    }

    /// <param name="reassertDress">
    /// Make the equipment packet the LAST writer of this burst (the owner's law, D86). The menu
    /// bootstrap already ends that way — <c>ac 11 → ac 19 → ac 23 → ac 28 → ac 24×N → 94 01</c>,
    /// captures\wire-20260902-212215.txt:23-33 — but every burst the world adds is the other way
    /// round: <c>94 01 → ac 24×N</c> at both <c>ClientIsReady</c>s (:52-58, :370-376) and at the
    /// parachute landing (:6209-6215), so in Z2 the client's own skin-manager recomposite is the
    /// last thing to touch the outfit. That is the owner's Z1 round-29 purple suit
    /// (<c>ZoneWardrobe.DressAfterManagers</c>). Passing <c>true</c> APPENDS one dress; it never
    /// reorders, so the docs/32 order that regression guard 4 pins — <c>94 01</c> before the six
    /// <c>ac 24</c> — is unchanged, and the ClientBeginZoning burst itself never sets it.
    /// Rollback: <c>CRANBERRY_DRESS_LAST=0</c>.
    /// </param>
    private void SendWornSkinItems(
        SoeConnection connection,
        GatewaySessionState state,
        string reason,
        bool reassertDress = false)
    {
        // AC24 also carries future weapon mappings. Pickup-only apparel is deliberately omitted
        // until its real item exists: announcing a helmet/backpack/armour preset against an empty
        // body slot makes the native client composite gear the player does not possess.
        IReadOnlyList<AugustSkinCatalogEntry> body = ActualWornSkins(state);
        IReadOnlyList<AugustSkinCatalogEntry> weapons = state.Wardrobe.WeaponSnapshot();
        foreach (AugustSkinCatalogEntry entry in body.Concat(weapons))
        {
            SendTunnel(connection, writer => new SetSkinItem(
                state.Guid,
                entry.CategoryPrototypeId,
                entry.AccountItemId,
                CollectionId: SkinCollectionFor(entry)).WriteTo(writer));
        }

        int announced = body.Count + weapons.Count;
        if (announced > 0)
        {
            _log.Info($"{connection} re-announced {announced} active skin selection(s) "
                + $"({body.Count} body, {weapons.Count} weapon; {reason})");
        }

        // D211 (docs/108): OFF by default since 2026-09-03. This arm is the packet that stopped
        // the owner entering two matches with the client's "G10": a lobby ClientIsReady that ends
        // 94 01 -> ac 24xN -> 94 01 leaves the client unable to finish the NEXT ClientBeginZoning
        // world load ("WFWR: Waiting for load : Zoning = true, Loading = true", then silence), and
        // seven owner-free live runs isolate this one field. docs/32's third regression says the
        // same thing about the zoning burst: the dress goes before the skin rows, and nothing
        // re-states it afterwards.
        if (announced > 0 && reassertDress && _options.Skins.DressLastAfterSkinRows)
        {
            // The same coupling SendSkinManagerState documents: suppression (D86) must not be
            // allowed to swallow the writer whose whole job is to be last.
            state.Dress.Forget();
            SendCharacterAppearance(connection, state, $"{reason}, dress last after skin rows");
        }
    }

    private static IReadOnlyList<AugustSkinCatalogEntry> ActualWornSkins(
        GatewaySessionState state)
    {
        HashSet<uint> equippedPickupCategories = state.WorldEquipment.Snapshot()
            .Select(item => item.CategoryPrototypeId)
            .ToHashSet();
        if (state.Inventory is { } inventory)
            foreach (var item in inventory.EquipmentSlots.Values)
                if (AugustWornSkins.TryGetCategory(item.DefinitionId, out uint category))
                    equippedPickupCategories.Add(category);
        return state.Wardrobe.WornSnapshot(equippedPickupCategories);
    }

    private void SendSelectedItemSkin(SoeConnection connection, GatewaySessionState state, uint itemId)
    {
        if (!AugustWornSkins.TryGetCategory(itemId, out uint category)) return;
        foreach (var entry in state.Wardrobe.Snapshot())
            if (entry.CategoryPrototypeId == category)
            {
                SendTunnel(connection, writer => new SetSkinItem(state.Guid, category,
                    entry.AccountItemId, CollectionId: SkinCollectionFor(entry)).WriteTo(writer));
                break;
            }
    }

    private uint SelectedSkinRewardFor(GatewaySessionState state, uint itemId)
    {
        if (!AugustWornVisuals.TryResolveMesh(itemId, state.Gender, null, out string mesh, out _)) return 0;
        return WornSkinRewardResolver(state)?.Invoke(itemId, mesh) ?? 0;
    }

    private static uint SkinCollectionFor(AugustSkinCatalogEntry entry) =>
        AugustWardrobeCatalog.IsApparel(entry)
            ? SetSkinItemManager.ApparelCollectionId
            : SetSkinItemManager.WeaponCollectionId;

    private void SendInitialCharacterResources(
        SoeConnection connection,
        GatewaySessionState state,
        string reason)
    {
        foreach (CharacterResource resource in CharacterResource.Starter)
        {
            CharacterResourceUpdate update = resource.ResourceId == 1 && resource.ResourceType == 1
                ? CharacterResourceUpdate.Health(state.Guid, state.Hitpoints, state.Hitpoints, _options.Gas.MaxHitpoints)
                : CharacterResourceUpdate.Initial(state.Guid, resource);
            SendTunnel(connection, update.WriteTo);
        }

        _log.Info($"{connection} sent current health/stamina ResourceEvents ({reason}); hp={state.Hitpoints}/{_options.Gas.MaxHitpoints}");
    }

    private void SendTunnel(SoeConnection connection, SendZoneDetails packet) =>
        SendTunnel(connection, packet.WriteTo);

    private void SendTunnel(SoeConnection connection, SendSelfToClient packet) =>
        SendTunnel(connection, packet.WriteTo);

    /// <summary>
    /// The appearance-row resolver a dress hands to <c>AugustWornVisuals.Dress</c>, already bound
    /// to this character body so the row naming its own mesh is first (docs/80 §Built).
    /// </summary>
    private Func<uint, IReadOnlyList<uint>>? RowsForThisBody(
        GatewaySessionState state,
        PlayerInventory? inventory = null)
    {
        if (_dynamicAppearance is not AugustDynamicAppearanceTable table)
        {
            return null;
        }

        uint gender = _options.Skins.OrderAppearanceRowsByGender ? state.Gender : 0;
        return item => inventory is not null
            && ItemUseOptionTable.Allows(item, 96)
            && ItemUseOptionTable.Allows(item, 97)
                ? table.AppearanceRowsForHood(item, inventory.HoodUp, gender)
                : table.AppearanceRowsFor(item, gender);
    }

    /// <summary>
    /// <b>WHERE THE MENU OUTFIT COMES FROM</b> (docs/106 §14, D284). One line per login naming, per
    /// body slot, the mesh the menu character will wear and whether it is the retail starter outfit
    /// or a stored wardrobe selection.
    /// <para>
    /// The owner's *"I'm still wearing a hoodie in the main menu"* could not be answered from a log
    /// at all: the restore line said <i>"restored 15 selection(s)"</i> and named no garment, and
    /// the only way to find the hoodie was to decode a 94 01 out of an 18 MB capture. It is
    /// <c>state\wardrobe\w-&lt;guid&gt;.json</c> every time - <c>w-0000000000001004.json</c> holds
    /// <c>3250 -&gt; reward 4266</c>, <c>Survivor*_Chest_Hoodie_Down_Tintable.adr</c> - and nothing
    /// in the tree ever said so out loud.
    /// </para>
    /// </summary>
    private void LogMenuOutfitOrigin(SoeConnection connection, GatewaySessionState state)
    {
        IReadOnlyList<AugustSkinCatalogEntry> worn = state.Wardrobe.WornSnapshot();
        var chosen = new Dictionary<uint, AugustSkinCatalogEntry>();
        foreach (AugustSkinCatalogEntry entry in worn)
        {
            chosen[entry.EquipmentSlotId] = entry;
        }

        var bySlot = new SortedDictionary<uint, string>();
        foreach (CharacterEquipmentAttachment starter in state.Visuals.StarterOutfit)
        {
            bySlot[starter.SlotId] = $"slot {starter.SlotId} {starter.ModelName} (starter)";
        }

        foreach (KeyValuePair<uint, AugustSkinCatalogEntry> pick in chosen)
        {
            bySlot[pick.Key] = $"slot {pick.Key} {pick.Value.ModelNameFor(state.Gender)} "
                + $"(SELECTED, category {pick.Value.CategoryPrototypeId} -> "
                + $"reward {pick.Value.RewardItemId})";
        }

        var parts = new List<string>(bySlot.Values);
        _log.Info($"{connection} menu outfit for character {state.Guid}: {string.Join("; ", parts)}"
            + (chosen.Count == 0
                ? " - every slot is the retail starter outfit"
                : $" - {chosen.Count} slot(s) come from "
                    + $"{WardrobeStore.FileNameFor(state.Guid)}, not from a default; rename that "
                    + "file or set CRANBERRY_WARDROBE_RESTORE=0 to go back to the starter outfit"));
    }

    /// <summary>
    /// <b>THE SKIN ON A PICKUP</b> (docs/106 §13, D283). Given a looted item and the mesh
    /// <c>AugustWornVisuals</c> just resolved for it, the reward item whose appearance rows and
    /// shader group go out instead of the base item's - or 0.
    /// <para>
    /// Two guards beyond <see cref="AugustWornSkins.TryResolve"/>'s own: the transmitted table must
    /// actually carry rows for that reward (the same test <c>BuildActiveHandAttachment</c> applies
    /// on body slot 7), because substituting a reward with no rows would replace a real colourway
    /// with an empty list; and each outcome is logged once per (item, reason) for the life of the
    /// process, so a decline is visible without a line per dress.
    /// </para>
    /// </summary>
    private Func<uint, string, uint>? WornSkinRewardResolver(GatewaySessionState state)
    {
        if (!_options.Skins.SendWornSkinsInWorld)
        {
            return null;
        }

        AugustDynamicAppearanceTable? table = _dynamicAppearance;
        uint gender = state.Gender;
        AugustWardrobeState wardrobe = state.Wardrobe;
        return (itemDefinitionId, modelName) =>
        {
            // With no appearance table, retain the selected starter outfit's valid mesh
            // just as the menu does. Other looted items keep their normal fallback.
            if (table is null && !SurvivorStarterOutfit.DefaultItemDefinitionIds.Contains(itemDefinitionId))
                return 0;
            if (!AugustWornSkins.TryResolve(
                    itemDefinitionId,
                    gender,
                    modelName,
                    wardrobe.Snapshot(),
                    _options.Skins.SkinRetintOnly,
                    out AugustSkinCatalogEntry pick,
                    out string reason))
            {
                NoteWornSkin(itemDefinitionId, reason);
                return 0;
            }

            if (table is not null && table.AppearanceRowsFor(pick.RewardItemId).Count == 0)
            {
                NoteWornSkin(
                    itemDefinitionId,
                    $"reward {pick.RewardItemId} has no row in the transmitted table");
                return 0;
            }

            NoteWornSkin(itemDefinitionId, reason);
            return pick.RewardItemId;
        };
    }

    /// <summary>
    /// The shader group a worn attachment should carry for whichever item ends up naming its rows,
    /// resolved the way the client's own selector resolves it, with D226's cross-gender fallback.
    /// Returns 0 for "no answer", which leaves <c>AugustWornVisuals.Dress</c> on the mesh
    /// catalogue's own group exactly as before D283.
    /// </summary>
    private Func<uint, uint>? WornSkinShaderGroupResolver(GatewaySessionState state)
    {
        if (!_options.Skins.SendWornSkinsInWorld
            || !_options.Skins.SendWornShaderGroups
            || _dynamicAppearance is null)
        {
            return null;
        }

        AugustDynamicAppearanceTable table = _dynamicAppearance;
        uint gender = state.Gender;
        bool crossGender = _options.Skins.CrossGenderShaderGroup;
        return rowItem =>
        {
            uint candidate = table.ShaderGroupFor(rowItem, gender);
            if (candidate == 0 && crossGender)
            {
                candidate = table.ShaderGroupForAnyBody(rowItem);
            }

            return candidate;
        };
    }

    /// <summary>One line per (looted item, outcome) for the life of the host, never per dress.</summary>
    private void NoteWornSkin(uint itemDefinitionId, string reason)
    {
        if (_wornSkinNotes.TryAdd(itemDefinitionId + "|" + reason, 0))
        {
            _log.Info($"worn skin: item {itemDefinitionId} - {reason}");
        }
    }

    /// <summary>
    /// The active hand is intentionally excluded from <see cref="AugustWornVisuals.Dress"/>: that
    /// generic path must never create a slot-7 attachment as a side effect of a clothing pickup.
    /// A confirmed hotbar selection reaches this narrow, inventory-authoritative replacement path
    /// instead. The matching equipment row still goes through <see cref="ActiveHandRowGuard"/>.
    /// </summary>
    /// <summary>
    /// The activatable ability of whatever is in body slot 7 right now, or 0 when the hand is empty
    /// or holds the fists. This is <c>WieldSequence</c> step 1's argument, and it must be read
    /// BEFORE the inventory moves the new item in.
    /// <para>
    /// The fists are excluded deliberately: <c>AbilityPackets.AbilityIdOf</c> gives them their own
    /// id, but Z1 sends no <c>UninitAbility</c> when drawing from an empty hand, and an uninit for
    /// an ability the client never activated is a packet with no state behind it.
    /// </para>
    /// </summary>
    private static uint ActiveHandAbilityId(PlayerInventory inventory)
    {
        if (!inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out InventoryItemInstance? held)
            || held.DefinitionId == 0
            || held.DefinitionId == AbilityPackets.FistsItemDefinitionId)
        {
            return 0;
        }

        return AbilityPackets.AbilityIdOf(held.DefinitionId);
    }

    /// <summary>
    /// Send Z1's eight-packet draw (docs/95) for <paramref name="instance"/>. Returns false when the
    /// sequence cannot be built — no third-person mesh, or <c>ActiveHandRowGuard</c> refusing the
    /// binding — in which case the caller falls back to the one-packet dress and NOTHING has been
    /// written, so the two paths can never both run.
    /// </summary>
    private bool TrySendWieldSequence(
        SoeConnection connection,
        GatewaySessionState state,
        PlayerInventory inventory,
        InventoryItemInstance instance,
        uint previousAbilityId,
        uint vacatedBodySlotId = 0,
        bool itemAlreadyGranted = false)
    {
        StopReloadIfWeaponChanged(connection, state);
        if (!AugustWornVisuals.TryResolveMesh(
                instance.DefinitionId,
                state.Gender,
                instance.Fact.ModelName,
                out string modelName,
                out uint wieldMeshShaderGroup))
        {
            _log.Info($"{connection} wield: NO sequence for item {instance.DefinitionId} — no "
                + "third-person mesh resolves, so there is nothing to put in the hand; falling back "
                + "to the dress");
            return false;
        }

        // THE WHITE GUN, second half (docs/106 §12, D224). D190 gave slot 7 its appearance ids
        // and its shader group on the 94 01 dress - and then the wield sequence, which is what
        // actually binds body slot 7 on every draw, built its own attachment with the TWO-ARGUMENT
        // constructor: grp 0, no appearance ids. Worse, the sequence deliberately REPLACES the dress
        // (see the comments at the hotbar and pickup call sites), so the corrected attachment never
        // went out at all and the last writer of slot 7 was always the bare one. Every gun the owner
        // drew composited at the neutral Default tint, which on the AK-47 and pump-shotgun colour
        // maps - docs/69 measured them at 30.4 % and 30.1 % near-white, ranks 1 and 2 of thirty - is
        // a WHITE gun. This is D190's fix, applied to the packet that wins.
        CharacterEquipmentAttachment wieldAttachment = BuildActiveHandAttachment(
            instance.DefinitionId, state, modelName, wieldMeshShaderGroup, instance.SkinOverrideDefinitionId);

        IReadOnlyList<WieldSequence.Step> steps = WieldSequence.Build(
            state.Guid,
            instance.ToRecord(state.Guid),
            wieldAttachment,
            BodySlots.RightHand,
            // A ground pickup vacates nothing: the item comes from the world, not from a peg. A
            // HOTBAR switch does - the drawn gun is coming off its back peg (76/77/80), and Z1's
            // §3.2 step 1 frees that peg with a 95 03 before anything else (docs/102 §5).
            vacatedBodySlotId: 0,
            previousAbilityId,
            inventory.ToLoadoutSlots(),
            _options.Combat.Enabled && _options.Combat.SendAbilityManager,
            state.Weapons,
            itemAlreadyGranted);

        if (steps.Count == 0)
        {
            _log.Info($"{connection} wield: NO sequence for item {instance.DefinitionId} instance "
                + $"{instance.Guid} — ActiveHandRowGuard refused the 94/02 binding (no resolvable "
                + "fire group delivered this session); falling back to the dress");
            return false;
        }

        // Update only changed stow pegs before binding the new hand. This also updates peer
        // appearance; clearing a peg later would erase the old weapon just stowed onto it.
        SendCharacterAppearance(connection, state, "weapon slot change");

        foreach (WieldSequence.Step step in steps)
        {
            SendTunnel(connection, step.Write);
        }

        SyncDrawnWeaponReloadCounter(connection, state, instance);

        _log.Info($"{connection} wield: sent Z1's draw order for item {instance.DefinitionId} "
            + $"instance {instance.Guid} into body slot {BodySlots.RightHand} — "
            + $"{steps.Count} packet(s): {string.Join(" -> ", steps.Select(s => s.Label))}"
            + $"; {WieldSequence.WeaponArmStepIsUnderived}");

        // (b) of S6 §7.3: after the draw's final 94 02, the new weapon's stance.
        SendWeaponStance(connection, $"draw sequence, item {instance.DefinitionId}");
        return true;
    }

    private void ApplyActiveHandVisual(
        List<CharacterEquipmentAttachment> attachments,
        PlayerInventory inventory,
        GatewaySessionState state)
    {
        if (!inventory.EquipmentSlots.TryGetValue(
                BodySlots.RightHand, out InventoryItemInstance? held)
            || !AugustWornVisuals.TryResolveMesh(
                held.DefinitionId,
                state.Gender,
                held.Fact.ModelName,
                out string modelName,
                out uint meshShaderGroup))
        {
            return;
        }

        CharacterEquipmentAttachment attachment = BuildActiveHandAttachment(
            held.DefinitionId, state, modelName, meshShaderGroup, held.SkinOverrideDefinitionId);
        int existing = attachments.FindIndex(row => row.SlotId == BodySlots.RightHand);
        if (existing >= 0)
        {
            attachments[existing] = attachment;
        }
        else
        {
            attachments.Add(attachment);
        }
    }

    /// <summary>
    /// <b>THE GUN IN YOUR HANDS</b> (docs/106, D190 and its 2026-09-03 correction D224). The one
    /// place body slot 7's attachment is built, so the <c>94 01</c> dress and the <c>94 02</c> wield
    /// binding can never disagree about it again - they did for a whole lane, and the disagreement
    /// was a white gun.
    /// <para>
    /// It was built with the two-argument constructor - no appearance ids, no shader group - while
    /// the SAME mesh on the stow peg carried both. Proof, in one packet:
    /// <c>captures/wire-20260902-212215.txt:8142</c> (21:24:32.814, in Z2):
    /// <c>slot=7 grp=0 app=[]</c> against <c>slot=76 grp=168 app=[181,182]</c>, both
    /// <c>Weapon_M16A4_3P.adr</c>. The client falls back to the packet's own group when the
    /// appearance list is empty (<c>FUN_140c70d60</c> tail), and group 0 is the neutral
    /// <c>Default</c> tint; docs/69 measured the AK-47 and pump-shotgun colour maps at 30.4 % and
    /// 30.1 % near-white, ranks 1 and 2 of thirty. A luminance mask at a neutral tint IS a white
    /// gun.
    /// </para>
    /// <para>
    /// <b>A selected weapon skin is applied here and nowhere else.</b> A weapon selection reaches
    /// this server only as <c>ac 24 SetSkinItem</c> in collection 2, and
    /// <c>AugustWorldEquipmentPolicy</c> - which knows only apparel categories - refuses every
    /// weapon, so before this the owner's three weapon skins changed nothing in the world at all.
    /// When the wardrobe holds a selection for the held item's category AND the transmitted table
    /// has rows for the reward item, the reward's rows and group are sent for the base item's mesh:
    /// the same "the pick's rows over the base rows" shape
    /// <c>AugustWorldEquipmentVisuals.ApplySkin</c> already uses for clothing.
    /// <c>CRANBERRY_WEAPON_SKINS_IN_WORLD=0</c> is the revert.
    /// </para>
    /// </summary>
    private CharacterEquipmentAttachment BuildActiveHandAttachment(
        uint itemDefinitionId,
        GatewaySessionState state,
        string modelName,
        uint meshShaderGroup,
        uint? skinOverrideDefinitionId = null)
    {
        IReadOnlyList<uint> handRows = [];
        uint handShaderGroup = 0;
        uint handEffectId = 0;

        if (_options.Skins.SendActiveHandAppearance && _dynamicAppearance is not null)
        {
            uint rowGender = _options.Skins.OrderAppearanceRowsByGender ? state.Gender : 0;
            uint rowItem = skinOverrideDefinitionId ?? itemDefinitionId;

            if (skinOverrideDefinitionId is null && _options.Skins.SendWeaponSkinsInWorld)
            {
                foreach (AugustSkinCatalogEntry pick in state.Wardrobe.WeaponSnapshot())
                {
                    if (AugustWornSkins.TryGetCategory(itemDefinitionId, out uint category)
                        && pick.CategoryPrototypeId == category
                        && _dynamicAppearance.AppearanceRowsFor(pick.RewardItemId).Count > 0)
                    {
                        rowItem = pick.RewardItemId;
                        break;
                    }
                }
            }

            handRows = _dynamicAppearance.AppearanceRowsFor(rowItem, rowGender);
            // D338: the reward's material effect (Infernal fire, Showdown shimmer) rides the
            // attachment's effectId; a plain tint skin leaves it 0.
            handEffectId = Appearance.AugustMaterialEffects.EffectIdFor(rowItem);

            // Prefer the group the client's own selector would land on; fall back to the mesh
            // catalogue's group for an item with no appearance row at all. Both go through
            // DefinesShaderGroup, so a group the transmitted table carries no parameters for is
            // still never sent (D84, docs/54 §4).
            uint candidate = _dynamicAppearance.ShaderGroupFor(rowItem, state.Gender);

            // D226: an item whose rows are all for the OTHER body resolves nothing, and 0 is the
            // neutral tint rather than "no opinion". Item 10, the AR-15, is that case on every male
            // body - two rows, both GENDER_ID 2 - and the boot census has been naming it as
            // NoRowForThisBody since the census existed.
            if (candidate == 0
                && _options.Skins.CrossGenderShaderGroup
                && _dynamicAppearance is AugustDynamicAppearanceTable crossBody)
            {
                candidate = crossBody.ShaderGroupForAnyBody(rowItem);
            }

            if (candidate == 0)
            {
                candidate = meshShaderGroup;
            }

            if (_dynamicAppearance.DefinesShaderGroup(candidate))
            {
                handShaderGroup = candidate;
            }
        }

        return new CharacterEquipmentAttachment(
            modelName,
            BodySlots.RightHand,
            TextureAlias: "Default",
            TintAlias: "Default",
            EffectId: handEffectId,
            ShaderParameterGroupId: handShaderGroup,
            AppearanceIds: handRows.Count == 0 ? null : handRows);
    }

    /// <summary>Apply the inventory's hood posture to the chest attachment assembled above.</summary>
    private void ApplyHoodState(
        List<CharacterEquipmentAttachment> attachments,
        PlayerInventory inventory,
        GatewaySessionState state)
    {
        if (!inventory.EquipmentSlots.TryGetValue(
                BodySlots.Chest, out InventoryItemInstance? chest)
            || !ItemUseOptionTable.Allows(chest.DisplayDefinitionId, 96)
            || !ItemUseOptionTable.Allows(chest.DisplayDefinitionId, 97))
        {
            return;
        }

        int index = attachments.FindIndex(row => row.SlotId == BodySlots.Chest);
        if (index < 0)
        {
            return;
        }

        CharacterEquipmentAttachment current = attachments[index];
        string hoodModel = AugustWornSkins.ModelForReward(chest.DisplayDefinitionId, state.Gender)
            ?? current.ModelName;
        string model = inventory.HoodUp
            ? hoodModel.Replace("Hoodie_Down", "Hoodie_Up", StringComparison.OrdinalIgnoreCase)
            : hoodModel.Replace("Hoodie_Up", "Hoodie_Down", StringComparison.OrdinalIgnoreCase);
        uint gender = _options.Skins.OrderAppearanceRowsByGender ? state.Gender : 0;
        IReadOnlyList<uint> rows = _dynamicAppearance?.AppearanceRowsForHood(
            chest.DisplayDefinitionId, inventory.HoodUp, gender) ?? [];
        attachments[index] = current with
        {
            ModelName = model,
            AppearanceIds = rows.Count == 0 ? null : rows,
        };
    }

    /// <summary>
    /// One <c>SetCharacterEquipment*</c>, dropped when it is byte-identical to the last one this
    /// session was sent (docs/80 edit 5, D53).
    /// <para>
    /// The owner measured that a full dress makes this client tear the whole attachment group down
    /// and rebuild it - 76 to 534 ms of bare body in his own <c>AttachmentProcessor.log</c> - and
    /// that <b>11 of 25</b> dresses in one session changed nothing at all. Cranberry sends a full
    /// dress on bootstrap, on Gear-editor open and on Gear-editor close, which is the same shape.
    /// Established in-world actors use the derived per-slot writers for safe equipment changes.
    /// A complete baseline remains necessary when an actor is created or a change lacks a safe
    /// slot binding; byte-identical updates remain suppressed.
    /// </para>
    /// </summary>
    /// <returns>True when the full dress was sent; false suppresses the caller's full-dress log.</returns>
    private bool SendDressTunnel(
        SoeConnection connection,
        GatewaySessionState state,
        Action<PacketWriter> writePacket,
        string reason,
        IReadOnlyList<CharacterEquipmentAttachment>? dress = null,
        IReadOnlyList<EquipmentSlotRow>? equipmentRows = null)
    {
        // docs/109 lane 3C: the rows this dress carries are the rows a peer is dressed with, so
        // they are recorded here — the one choke point all three dress branches go through —
        // whether or not the packet is suppressed. A suppressed dress is an IDENTICAL dress, so
        // the registry is already holding it.
        bool rearm = false;
        if (dress is not null)
        {
            rearm = NotePeerDress(state, dress);
            // Team rows live outside peer interest and their pose updates are throttled.
            // Publish equipment now, including explicit clears, even if meshes are identical.
            PublishTeamHudStatus(state);
        }

        var snapshot = dress is not null && equipmentRows is not null
            ? new EquipmentDressSnapshot(dress, equipmentRows) : null;
        var previous = state.EquipmentDress;
        bool reassertHand = state.Dress.RequiresSlotReassert;
        IReadOnlyList<byte[]>? deltas = InWorldEquipmentDelta(state, snapshot);
        state.EquipmentDress = snapshot;

        using var inner = new PacketWriter();
        writePacket(inner);
        ReadOnlySpan<byte> payload = inner.Written;
        if (!state.Dress.ShouldSend(payload, _options.Skins.SuppressIdenticalDress))
        {
            _log.Info($"{connection} suppressed an identical re-dress ({reason}; "
                + $"{payload.Length} bytes, dresses {state.Dress.Counters()}) - the client would "
                + "have torn down and rebuilt the whole attachment group for no change");
            BindSelectedFists(connection, state, force: false);
            return false;
        }

        if (deltas is not null)
        {
            foreach (var delta in deltas)
                SendTunnel(connection, writer => writer.WriteRaw(delta));
            _log.Info($"{connection} sent {deltas.Count} equipment slot update(s) ({reason}); clothing outside changed slots retained");
        }
        else
        {
            using var outer = new PacketWriter();
            new GatewayTunnelToClient(Channel: 0, Payload: payload.ToArray()).WriteTo(outer);
            _recorder.RecordMessage(connection, "s2c", outer.Written);
            connection.Send(outer.Written);
        }

        BindSelectedFists(connection, state, force: deltas is null || reassertHand);
        if (reason != "weapon slot change"
            && (deltas is null || reassertHand || snapshot?.HandDiffersFrom(previous) == true))
            RestoreDrawnHandAfterDress(connection, state, dress);
        // D322: the dress that just changed on this client's screen changes on every viewer's.
        if (dress is not null)
        {
            CharacterEquipmentAttachment? peerHand = null;
            ulong peerHandItem = 0;
            var peerDeltas = deltas;
            if (deltas is not null && state.Inventory is { Options.UseWieldSequence: true } inventory
                && snapshot?.HandDiffersFrom(previous) == true)
            {
                peerHand = dress.FirstOrDefault(a => a.SlotId == BodySlots.RightHand);
                if (peerHand is not null && inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out var held))
                    peerHandItem = held.Guid;
                else peerDeltas = null; // An unbound / removed hand still needs the complete baseline.
            }
            RelayPeerDress(connection, state, rearm, peerDeltas, peerHandItem, peerHand);
        }

        return deltas is null;
    }

    private void RestoreDrawnHandAfterDress(SoeConnection connection, GatewaySessionState state,
        IReadOnlyList<CharacterEquipmentAttachment>? dress)
    {
        // Full 94/01 replaces the equipment map, including the separately bound hand. Its slot-7
        // row must remain absent (G2), so restore the existing item through the same 94/02 used by
        // a draw. Recreating the item here would reset a loaded/reloading gun on clothing changes.
        if (state.Match is not (MatchStep.Lobby or MatchStep.InMatch)
            || state.Inventory is not { Options.UseWieldSequence: true } inventory
            || !inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out var held)
            || dress?.FirstOrDefault(attachment => attachment.SlotId == BodySlots.RightHand) is not { } hand)
            return;

        var binding = new SetCharacterEquipmentSlot(state.Guid,
            new(BodySlots.RightHand, held.Guid), hand, Clearance: state.Weapons.Clearance);
        if (!binding.IsPermitted) return;
        SendTunnel(connection, binding.WriteTo);
        _log.Info($"{connection} restored active hand item {held.Guid} after full equipment dress (94/02)");
    }

    private void BindSelectedFists(SoeConnection connection, GatewaySessionState state, bool force)
    {
        if (state.Inventory?.CurrentLoadoutSlotId != SurvivorLoadout.Fists)
            state.FistsBound = false;
        if (state.FistsBound && !force) return;
        state.FistsBound = false;
        if (state.Match is MatchStep.InMatch or MatchStep.Lobby
            && state.Inventory is { } fistsInventory
            && FistsEquipmentBinding.Create(state.Guid, fistsInventory, state.Weapons.Clearance) is { } fistsBinding)
        {
            SendTunnel(connection, writer => fistsBinding.WriteTo(writer));
            state.FistsBound = true;
            _log.Info($"{connection} bound selected fists item {fistsBinding.Row.ItemGuid} to the active hand (94/02)");
        }
    }

    /// <summary>Wraps one zone packet in gateway tunnel opcode 5 on channel 0 and sends it.</summary>

    /// <summary>
    /// <c>ReferenceData "WeaponDefinitions"</c>, once per session, before the first weapon
    /// <c>ItemAdd</c> - docs/89 §2b, §4 A4.
    /// <para>
    /// <b>The ordering invariant, and it is not negotiable.</b>
    /// <c>ClientPlayerItemManager::CreateItem</c>'s self-init <c>FUN_142291180</c> runs once, at
    /// construction, and no re-initialisation path is known (docs/58 U8) - so a weapon that arrives
    /// before this table is permanently a weapon the client could not build. It has to be early.
    /// It does <b>not</b> have to be in the login burst, which is the one path to PLAY this project
    /// has ever proven, so wave 9 moved it out of <c>SendBootstrap</c> and into here. If the
    /// client ever dies before character select after this wave, this packet is the first suspect
    /// and <c>CRANBERRY_WEAPON_DEFINITIONS=0</c> is the one-word revert.
    /// </para>
    /// </summary>
    private void SendWeaponDefinitionsOnce(SoeConnection connection, GatewaySessionState state)
    {
        // ReferenceData "ProjectileDefinitions", immediately BEFORE the weapon table and only when
        // CRANBERRY_PROJECTILE_DEFINITIONS=1 (default off - docs/99 §4.3 says exactly what is still
        // missing). A fire mode that names a projectile id can only resolve it against a table the
        // client already holds, so the order is not free.
        if (state.Weapons.TryCreateProjectileDefinitions(out ReferenceData projectileTable))
        {
            SendTunnel(connection, writer => projectileTable.WriteTo(writer));
            state.Weapons.MarkProjectileDefinitionsSent();
            _log.Info($"{connection} sent ReferenceData ProjectileDefinitions");
        }

        if (!state.Weapons.TryCreateWeaponDefinitions(out ReferenceData weaponTable))
        {
            return;
        }

        SendTunnel(connection, writer => weaponTable.WriteTo(writer));
        state.Weapons.MarkWeaponDefinitionsSent();
        _log.Info($"{connection} sent ReferenceData WeaponDefinitions — {state.Weapons.Describe()}");
    }

    /// <summary>
    /// <c>SetLoadoutSlots</c> (<c>86 04</c>) and, immediately after it,
    /// <c>Abilities.SetActivatableAbilityManager</c> (<c>a0 05</c>) - docs/89 §3a.
    /// <para>
    /// <b>No <c>86 07 SelectLoadoutSlot</c> is ever sent from here, or anywhere</b> (2026-09-02,
    /// S6 §7.4). It was the one packet that did set a current slot, and the owner's live A/B froze
    /// mouse-look and movement with it (docs/95); Z1 never sends its <c>87 07</c> at all. Since
    /// <see cref="SetLoadoutSlots"/> now carries the trailing <c>currentSlotId</c> the selection
    /// travels with the table, so the send is redundant as well as harmful. The c2s <c>86 06</c>
    /// observer stays.
    /// </para>
    /// <para>
    /// The client's own <c>updateLoadout</c> sends the two together every single time, and the
    /// manager is what tells it which abilities this loadout may activate: without it a left-click
    /// with a melee weapon in hand produces no <c>a0 01</c> at all. The owner has thirteen send
    /// sites and enforces the pairing at an outbound chokepoint; this build has three, so one
    /// helper is enough. The manager carries no character id, so it can only ever mean "mine" -
    /// which is why it is sent here, on the owner's own connection, and nowhere else.
    /// </para>
    /// </summary>
    private void SendLoadoutSlots(
        SoeConnection connection,
        PlayerInventory inventory)
    {
        SetLoadoutSlots slots = inventory.ToLoadoutSlots();
        SendTunnel(connection, writer => slots.WriteTo(writer));

        if (_options.Combat.Enabled && _options.Combat.SendAbilityManager)
        {
            byte[] manager = AbilityPackets.SetActivatableAbilityManager(slots);
            SendTunnel(connection, writer => writer.WriteRaw(manager));

            if (connection.Tag is GatewaySessionState managerState
                && !managerState.WeaponStanceWorldAsserted)
            {
                // (a) of S6 §7.3: once, after the world session's FIRST a0 05. The flag is set
                // whether or not the packet goes out, so a session that starts with the switch off
                // never turns this into a per-refresh packet later on.
                managerState.WeaponStanceWorldAsserted = true;
                SendWeaponStance(connection, "world entry, after a0 05");
            }
        }
    }

    /// <summary>
    /// <c>0f 20 Character.WeaponStance {selfGuid, 1}</c> - the packet that starts the client's own
    /// weapon stance machine (<see cref="WeaponStance"/>).
    /// <para>
    /// S6 §7.3 and the owner's round-26 finding on his own 1087 server
    /// (<c>ZoneAbilities.cs:770-856</c>, adopted under D53): a client that is never given a stance
    /// never leaves stance 0, never enters a fire state, and reports "cannot shoot / reload". The
    /// August client has never been sent one and has never sent one. It is asserted once at world
    /// entry and after every draw, and <b>stops for good</b> at the session's first c2s
    /// <c>0f 20</c> - so if the hypothesis is wrong this costs 14 bytes per draw and the log says
    /// so, and if it is right the client speaks once and the re-assert ends itself.
    /// </para>
    /// </summary>
    private void SendWeaponStance(SoeConnection connection, string reason)
    {
        if (connection.Tag is not GatewaySessionState state
            || state.Guid == 0
            || !state.Weapons.Options.Effective.SendWeaponStance
            || state.ClientSentWeaponStance)
        {
            return;
        }

        var stance = new WeaponStance(state.Guid, WeaponStance.Initial);
        SendTunnel(connection, writer => stance.WriteTo(writer));
        state.WeaponStancesSent++;
        _log.Info($"{connection} weapon stance: 0f 20 {{self, {WeaponStance.Initial}}} "
            + $"({reason}) — assert #{state.WeaponStancesSent}; stops at the client's own 0f 20");
    }

    /// <summary>
    /// What is in the active hand (body slot 7) right now. The
    /// <c>0x82</c> arm used to be handed <c>StarterWeaponItemDefinitionId</c>, a fixed option that
    /// is not what the player is holding; melee needs the real item because the damage table is
    /// keyed on it.
    /// </summary>
    private static uint HeldItemDefinitionId(GatewaySessionState state) =>
        state.Inventory is { } inventory
        && inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out InventoryItemInstance? held)
            ? held.DefinitionId
            : state.FistsBound && state.Inventory?.CurrentLoadoutSlotId == SurvivorLoadout.Fists
                ? PlayerInventory.SurvivorFistsItemDefinitionId : 0u;

    /// <summary>The same displayed item sent in inventory packets, including the equipped weapon skin.</summary>
    private static uint HeldItemDisplayDefinitionId(GatewaySessionState state) =>
        state.Inventory is { } inventory
        && inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out InventoryItemInstance? held)
            ? held.DisplayDefinitionId
            : HeldItemDefinitionId(state);

    /// <summary>The active-hand instance guid, or zero before inventory bootstrap.</summary>
    private static ulong HeldItemGuid(GatewaySessionState state) =>
        state.Inventory?.WieldedItemGuid is > 0 and var held ? held
            : state.FistsBound && state.Inventory?.CurrentLoadoutSlotId == SurvivorLoadout.Fists
                && state.Inventory.LoadoutSlots.TryGetValue(SurvivorLoadout.Fists, out var fists)
                    ? fists.Guid : 0;

    /// <summary>Logs, answers and despawns for one combat arm's results, then clears them.</summary>
    /// <summary>
    /// Sends an arm's extra replies, honouring per-reply delays (docs/121 §6). Consecutive replies
    /// with the same delay are grouped so one <see cref="Later"/> sends them in order.
    /// </summary>
    private void SendCombatReplies(
        SoeConnection connection, IReadOnlyList<byte[]> replies, IReadOnlyList<int>? delaysMs)
    {
        foreach ((int delayMs, byte[][] group) in WeaponReplySchedule.Group(replies, delaysMs))
        {
            if (delayMs <= 0
                || !Later(connection, delayMs, () =>
                {
                    foreach (byte[] packet in group)
                    {
                        SendTunnel(connection, writer => writer.WriteRaw(packet));
                    }
                }))
            {
                foreach (byte[] packet in group)
                {
                    SendTunnel(connection, writer => writer.WriteRaw(packet));
                }
            }
        }
    }

    private void DrainCombatArm(SoeConnection connection, GatewaySessionState state)
    {
        foreach (WeaponArmResult armed in state.WeaponArmResults)
        {
            _log.Info($"{connection} {armed.Line}");
            RelayPeerWeaponUpdate(state, armed);
            if (armed.MeleeGlassObjectId is uint glassObjectId)
                ScheduleMeleeGlassHit(connection, state, glassObjectId);

            if (armed.ReloadWork is { } pendingReload)
            {
                ScheduleReload(connection, state, pendingReload);
            }

            if (armed.Marker is { } marker)
            {
                SendHitFeedback(connection, marker);
            }

            if (armed.Reply is { } reply)
            {
                SendTunnel(connection, writer => writer.WriteRaw(reply));
            }

            // Lane 1F: an arm that owes more than one packet - a bag-fed reload sends its stack
            // updates and one 82 08 per shell, in order. docs/121 §6 (D334): each entry may carry a
            // delay, and entries with the same delay go out together on one deferred continuation
            // so their order is kept; a refused deferral (no dispatcher) sends immediately rather
            // than dropping a reload reply.
            if (armed.Replies is { Count: > 0 } replies)
            {
                SendCombatReplies(connection, replies, armed.ReplyDelaysMs);
            }

            // docs/121 §7 (D335): the bystanders' 82 15 04 01.
            if (armed.Relay != PeerFireRelay.None)
            {
                RelayPeerFire(connection, state, armed.Relay, armed.RelayAimPoint, armed.RelayWeaponGuid);
                if (armed.StopAfterRelay)
                    RelayPeerFire(connection, state, PeerFireRelay.Stop, armed.RelayAimPoint, armed.RelayWeaponGuid);
            }

            // docs/125 §6 (D315): the bystanders' 82 15 04 0b, on every accepted trigger - a shot
            // and a throw take the same path, which is why the throw's own relay is gone.
            if (armed.LaunchRelay)
            {
                RelayProjectileLaunch(
                    connection, state, armed.Thrown is null ? "shot" : "throw",
                    armed.Thrown?.Grenade.ItemGuid ?? armed.RelayWeaponGuid);
            }

            // docs/118 §2 (AUDIT-vehicles gap 3): a hit report whose guid resolved to neither a
            // practice target nor a player is offered to the vehicle fleet here. Before this lane
            // the arm ended at "no damage model for it yet" and shooting a car did nothing at all.
            if (armed.MeleeHit is { } meleeHit)
                ApplyPeerMeleeHit(connection, state, meleeHit);

            if (armed.TargetGuid != 0 && armed.TargetDamageUnits > 0
                && !TryDamagePeerWithBullet(connection, state, armed))
            {
                if (!TryDamageVehicleWithBullet(connection, state, armed))
                    TryDamageFuelCanWithBullet(connection, state, armed);
            }

            if (armed.HitTarget is { FullKit: true } dressedTarget)
            {
                if (dressedTarget.IsCombatBot) PublishBotDress(state, dressedTarget);
                else SendTunnel(connection, w => PracticeTargetKit.WriteDress(w, dressedTarget));
            }

            if (armed.Killed && armed.HitTarget is { } dead)
            {
                // Lane 1D-lite: the dummy DIES before it is despawned — 0f 4f + 0f 48, so the
                // shooter sees a body fall and a kill-feed line instead of a body vanishing.
                // KillPracticeTarget still calls DespawnPracticeTargetLater for the corpse timer.
                KillPracticeTarget(connection, state, dead);
            }

            // docs/120: a throwable's 82 03 put a grenade in the air, or its 82 19 said where it
            // went off. ZoneService.Throwables.cs owns the stack, the relay, the timer and the blast.
            if (armed.Thrown is { } thrown)
            {
                OnGrenadeThrown(connection, state, in thrown);
            }

            if (armed.Detonation is { } detonation)
            {
                Detonate(connection, state, detonation);
            }
        }
    }

    /// <summary>
    /// docs/105 §10, MENU-RETAIL-GAP U-3 - the menu top bar: one
    /// <c>Experience.SetExperience</c> (<c>87 01</c>, 55 B) and one
    /// <c>Currency.SetAccountCurrencyRecord</c> (<c>ab 03</c>, 10 B) per currency the bar draws.
    /// Menu only; <c>CRANBERRY_MENU_TOPBAR=0</c> silences it.
    /// </summary>
    private void SendMenuTopBar(SoeConnection connection)
    {
        MenuTopBarOptions options = _options.MenuTopBar;
        if (!options.SendTopBar)
        {
            return;
        }

        SetExperience experience;
        if (connection.Tag is GatewaySessionState experienceState)
            experience = SendAccountExperience(connection, experienceState);
        else
        {
            experience = options.ExperiencePacket();
            SendTunnel(connection, experience.WriteTo);
        }
        int rows = 0;
        foreach (SetAccountCurrencyRecord record in connection.Tag is GatewaySessionState state
            ? AccountCurrencyRecords(state) : options.CurrencyRecords())
        {
            SendTunnel(connection, writer => record.WriteTo(writer));
            rows++;
        }

        _log.Info(
            $"{connection} menu top bar: SetExperience xp {experience.Experience} rank "
                + $"{experience.Rank}, {rows} SetAccountCurrencyRecord rows");
    }

    /// <summary>
    /// docs/105 §10, MENU-RETAIL-GAP U-4 - the MOTD panel (<c>0x32</c>, two counted strings).
    /// Menu only; <c>CRANBERRY_MENU_MOTD=0</c> silences it and <c>CRANBERRY_MENU_MOTD_TEXT</c>
    /// replaces the body. An empty body would REMOVE the row client-side, so it is never sent.
    /// </summary>
    private void SendMenuMotd(SoeConnection connection)
    {
        MenuTopBarOptions options = _options.MenuTopBar;
        if (!options.SendMotd || options.MotdText.Length == 0)
        {
            return;
        }

        MessageOfTheDay motd = options.MotdPacket();
        SendTunnel(connection, writer => motd.WriteTo(writer));
        _log.Info($"{connection} menu MOTD: \"{motd.Message}\"");
    }

    private void SendTunnel(SoeConnection connection, Action<PacketWriter> writePacket)
    {
        using var inner = new PacketWriter();
        writePacket(inner);

        using var outer = new PacketWriter();
        new GatewayTunnelToClient(Channel: 0, Payload: inner.Written.ToArray()).WriteTo(outer);
        _recorder.RecordMessage(connection, "s2c", outer.Written);
        connection.Send(outer.Written);
    }

    /// <summary>
    /// A fresh per-link state, carrying the options-derived pieces the nested type cannot reach.
    /// </summary>
    private GatewaySessionState NewSessionState()
    {
        var state = new GatewaySessionState
        {
            Weapons = new WeaponSession(_options.Weapons, crateOpeningWeapon: true),
            Hitpoints = _options.Gas.MaxHitpoints,
        };
        // Inventory and remotely held weapons share the host's instance namespace.
        state.Loot.ItemGuidAllocator = () => _nextInventoryItemGuid++;

        // docs/107 §1: the Weapon ItemAdd tail's ammo-slot array is the ONLY thing that ever tells
        // this client how many rounds are in a gun, so it is filled from the session's own combat
        // state. A guid combat has not met yet has no count of its own - that is a fresh pickup, and
        // DefaultMagazineFor is the same expression the trigger and the reload use for it (S6 §4.5),
        // which under GunsSpawnEmpty is 0 for a firearm and 1 for a throwable.
        state.Weapons.MagazineSource = (itemGuid, itemDefinitionId) =>
        {
            int live = state.Combat.Shooter.AmmoOf(itemGuid);
            return live >= 0
                ? live
                : ShooterCombatState.DefaultMagazineFor(itemDefinitionId, _options.Combat.Ammo);
        };

        return state;
    }

    private sealed class GatewaySessionState
    {
        public SessionDiagnostics? ProductionMetrics;
        public int WorldGeneration { get; set; }
        public PendingMeleeGlass? PendingGlassSwing { get; set; }
        public PunchAnimation? LastPunchAnimation { get; set; }
        public uint? ActiveEmoteAnimationId { get; set; }
        public long? LastEmoteStartAtMs { get; set; }
        public WorldStreamStartup WorldStreamStartup { get; } = new();
        public Dictionary<ulong, Armour> PlayerArmour { get; } = [];
        public bool Authenticated { get; set; }
        public string AccountId { get; set; } = string.Empty;
        public bool SocialLinked { get; set; }
        public bool VoiceHudLinked { get; set; }
        public string? VoiceHudLastView { get; set; }
        public long VoiceHudSentAt { get; set; }
        public ulong SocialInviteAcknowledgedToken { get; set; }
        public bool OverlayPending { get; set; }
        public long OverlayLastActionMs { get; set; } = long.MinValue / 2;
        public long OverlayLastToggleMs { get; set; } = long.MinValue / 2;
        public string? OverlayToggleId { get; set; }
        public long SocialLastPollMs { get; set; } = long.MinValue / 2;
        public long SocialLastActionMs { get; set; } = long.MinValue / 2;
        public long SocialLastInviteMs { get; set; } = long.MinValue / 2;
        public string? SocialLastView { get; set; }
        public string EconomyLinkId { get; } = System.Guid.NewGuid().ToString("N");
        public long EconomyRequestSequence { get; set; }
        public Progression.ExperienceProgress? ExperienceAtMatchStart { get; set; }
        public uint ExperienceEarned { get; set; }
        public CrateOpeningRun? CrateOpening { get; set; }
        public MatchAdmissionContext BountyAdmission { get; set; } = MatchAdmissionContext.Unknown;
        public PlayerWorldTransferRequest? MatchTransferRequest { get; set; }
        public ClientAdmissionWait? PendingClientAdmission { get; set; }
        public string? HostedGameId { get; set; }
        public uint HostedAdminWorldSent { get; set; }
        public uint HostedCreateSequence { get; set; }
        public bool HostedSpectatorRosterSent { get; set; }
        public long NextHostedSpectatorRosterMs { get; set; }
        public bool HostedAccessPumping { get; set; }
        public bool HostedObserverActive { get; set; }
        public bool HostedFreeCamera { get; set; }
        public ulong HostedSpectateTarget { get; set; }
        public Vector3? HostedCameraPosition { get; set; }
        public uint HostedObserverSequence { get; set; }
        public uint HostedPanelFeedbackSequence { get; set; }
        public int MatchAdmissionGeneration { get; set; }
        public uint MatchPartyId { get; set; }
        public int MatchPartySize { get; set; }
        public uint BountyWorldId { get; set; }
        public uint? BountyPlacement { get; set; }
        public bool BountyResultSettled { get; set; }
        public bool PregameClientReady { get; set; }
        public Vector4 StagingPosition { get; set; }
        public bool LobbyArmPending { get; set; }

        /// <summary>
        /// docs/80 edit 5: the last <c>SetCharacterEquipment*</c> this session was sent, so an
        /// identical one is never sent twice. Forgotten at <c>ClientBeginZoning</c>, because the
        /// client rebuilds the actor across a zone transition.
        /// </summary>
        public AugustDressSuppressor Dress { get; } = new();

        public EquipmentDressSnapshot? EquipmentDress { get; set; }
        public bool FistsBound { get; set; }

        /// <summary>Cancels a pending delayed bootstrap when the link closes first.</summary>
        public CancellationTokenSource? BootstrapCancellation { get; set; }

        /// <summary>Channel-2 stream packets received (20 Hz once the client is Running).</summary>
        public long StreamPackets { get; set; }

        /// <summary>Channel-3 PlayerUpdateManagedPosition packets (the client streaming an object it owns).</summary>
        public long ManagedStreamPackets { get; set; }

        public long MalformedStreamPackets { get; set; }
        public long MalformedManagedStreamPackets { get; set; }
        public long RejectedManagedStreamPackets { get; set; }

        /// <summary>Merged authoritative movement for this player and its registered objects.</summary>
        public SessionMovementState Movement { get; } = new();

        /// <summary>Settled-motion detector used because August sends no landing dismount request.</summary>
        public ParachuteTouchdownDetector Touchdown { get; } = new();

        /// <summary>The ground loot this session can see and claim (docs/13 §9 step 1).</summary>
        public LootWorld Loot { get; } = new();
        public Dictionary<ulong, BodyBag> BodyBags { get; } = [];
        public ulong AccessedBodyBag { get; set; }
        public long NextBodyBagPanelMs { get; set; }
        public int? BodyBagPanelSignature { get; set; }

        /// <summary>
        /// docs/52: which of the map's 39,708 ground objects are on the client right now, and which
        /// this match has already taken. <see cref="Loot"/> is the wire state (what exists, by
        /// guid); this is the streaming state (which MARKER each object came from), and the two are
        /// different because a marker that is streamed out and back gets a new guid while a marker
        /// that was picked up must never come back at all.
        /// </summary>
        public MatchLoot StreamedLoot { get; } = new();

        /// <summary>
        /// D274: this match's airdrop schedule, created with the gas controller because both run off
        /// the same match clock. Null until the match starts.
        /// </summary>
        public MatchAirdrops? Airdrops { get; set; }

        /// <summary>
        /// The crates standing in the world, by world guid: when each may be opened, and what it
        /// holds. A crate is registered in <see cref="Loot"/> like any other ground object — that is
        /// what makes <c>[F]</c> reach it at all — so the pickup path has to check this FIRST, or
        /// the press would hand the player the crate itself instead of its contents.
        /// </summary>
        public Dictionary<ulong, AirdropCrateState> AirdropCrates { get; } = [];

        /// <summary>Scratch list for the airdrop pump, so a 4 Hz beat allocates nothing.</summary>
        public List<AirdropEvent> AirdropEvents { get; } = [];

        /// <summary>
        /// World-object guids whose <c>LightweightToFullNpc 0xda</c> has already gone out. The apply
        /// <c>FUN_140b02060</c> requires the pending bit the <c>0xd6</c> apply set and clears it, so
        /// a second <c>0xda</c> for the same object is a silent no-op (docs/19 §1a) — this set only
        /// keeps the log honest about which send was the load-bearing one.
        /// </summary>
        public HashSet<ulong> FullNpcSent { get; } = [];

        /// <summary>This session's gas runtime (docs/23); started at the drop, stopped at the death or the link close.</summary>
        public GasController? Gas { get; set; }

        /// <summary>Reused one-element sample buffer so the gas pump allocates nothing per tick.</summary>
        public PlayerSample[] GasSamples { get; } = new PlayerSample[1];

        /// <summary>Server-side health, initialized on session creation and reset at lobby/drop entry independently of gas.</summary>
        public uint Hitpoints { get; set; }
        public int PlayerVitalsGeneration { get; set; }

        /// <summary>The gas pump has been scheduled for this drop.</summary>
        public bool GasPumping { get; set; }

        /// <summary>
        /// Wave 9 (docs/87 §3.2): wall clock of the last <c>ce 0f</c> heal, the last <c>ce 02</c>
        /// heal, and the label the widget was last told to show. The third exists only so the
        /// console gets one line per label change instead of one per second per session.
        /// </summary>
        public long GasHudHealAtMs;

        /// <inheritdoc cref="GasHudHealAtMs"/>
        public long GasSafeZoneHealAtMs;

        /// <inheritdoc cref="GasHudHealAtMs"/>
        public uint GasHudLabelSent { get; set; }

        /// <summary>
        /// D279 (docs/118 §4): this session's toxicity meter — resource 611, the bar the client
        /// already draws. A value, not a service; the pump maps it onto one <c>8d</c> per changed
        /// tick.
        /// </summary>
        public GasToxicity Toxicity;

        /// <summary>Wall clock of the last toxicity tick, on the same beat as the damage tick.</summary>
        public long GasToxicityAtMs;

        /// <summary>
        /// The pump's own re-arm delegate, cached so the 4 Hz chain allocates one closure per match
        /// rather than one per tick per session (docs/77 section 9.2).
        /// </summary>
        public Action? GasPump { get; set; }

        /// <summary>The death hand-off has already gone out for this match.</summary>
        public bool DeathSent { get; set; }

        /// <summary>
        /// Lane 1D-lite: the victory burst has already gone out for this match. Separate from
        /// <see cref="DeathSent"/> because a winner never dies, and a dead player never wins.
        /// </summary>
        public bool VictorySent { get; set; }

        /// <summary>Lane 1D-lite: when <c>MatchStep.Ended</c> was entered — the start of the hold.</summary>
        public long EndedAtMs { get; set; }

        /// <summary>
        /// Lane 1D-lite: the last <c>ce 09</c> value this session was sent by the endgame path, or
        /// null when it has never sent one. The counter goes out on change only (S4 row E7).
        /// </summary>
        public int? AliveSent { get; set; }

        /// <summary>Lane 1D-lite: how many <c>8e 01</c> collision reports this session has sent.</summary>
        public long CollisionReports { get; set; }

        /// <summary>Lane 1D-lite: wall clock of the last collision line, so the log is rate-limited.</summary>
        public long LastCollisionLogMs { get; set; }

        /// <summary>
        /// docs/115 §1: how many of those carried <c>causeOfDamage 2 ToxicGas</c>. Counted and never
        /// charged — Cranberry runs its own gas ladder, so the client's copy of the same damage is a
        /// cross-check, not a second bill.
        /// </summary>
        public long CollisionGasReports { get; set; }

        /// <summary>
        /// docs/115 §1: this player's own burst window over <c>8e 01</c>, so a ramp of reports about
        /// one fall costs its peak once. The car has its own on <see cref="MatchVehicle.Collision"/>.
        /// </summary>
        public CollisionBurst PlayerCollision = CollisionBurst.Fresh;

        /// <summary>Successful dismount protects the rider from exit collision/fall reports.</summary>
        public long VehicleExitProtectedUntilMs { get; set; }

        /// <summary>Monotonic deadline for player fall damage only, armed by parachute landing.</summary>
        public long ParachuteFallProtectedUntilMs { get; set; }

        /// <summary>
        /// <c>Environment.TickCount64</c> at the <c>SynchronizedTeleport.Release</c> that put this
        /// session in the world, or 0. Gate 2 of the collision path measures its grace from here.
        /// </summary>
        public long ReleasedAtMs { get; set; }

        /// <summary>docs/115 §3: which of this session's cars are holding the boost.</summary>
        public VehicleBoostState Boost { get; } = new();

        /// <summary>
        /// Real pickup-only equipment on this match actor. Wardrobe presets remain independent
        /// and project here only after the matching gameplay item has actually been equipped.
        /// </summary>
        public AugustWorldEquipmentState WorldEquipment { get; } = new();

        /// <summary>The development ground-loot drop has been scheduled once for this session.</summary>
        public bool DevGroundLootArmed { get; set; }
        public long CharacterFireUntilMs { get; set; }
        public bool CharacterFireActive { get; set; }

        /// <summary>The map's real ground loot has been scheduled once for this session's match (docs/33).</summary>
        public bool RealGroundLootArmed { get; set; }

        /// <summary>
        /// The landing burst has finished draining — every one of its spawns has run and been adopted
        /// into <see cref="StreamedLoot"/>.
        /// <para>
        /// docs/52 §Integration step 3, as corrected by the wave-5 verify pass. This is the loot
        /// arm's real "has the per-match state arrived yet" test. <see cref="StreamedLoot"/> is a
        /// non-null initialiser, so the <c>loot is not null</c> guard the door arm's shape was copied
        /// from is a compile-time <c>true</c> here and guards nothing. Without this flag a pump tick
        /// that beat the landing drain re-offered markers the drain was about to spawn, and each
        /// duplicate was a world object nothing could ever evict.
        /// </para>
        /// </summary>
        public bool LandingLootDrained { get; set; }

        /// <summary>
        /// Next <c>Environment.TickCount64</c> at which the door arm / the loot arm of the shared
        /// world pump may fire. The pump itself ticks at the SHORTER of the two intervals so that
        /// lowering one cannot slow the other down; these two stamps are what stop the min also
        /// SPEEDING the other one up (<c>WorldStream.IsDue</c>).
        /// </summary>
        public long NextDoorPumpMs { get; set; }

        /// <inheritdoc cref="NextDoorPumpMs"/>
        public long NextLootPumpMs { get; set; }

        /// <inheritdoc cref="NextDoorPumpMs"/>
        public long NextVehiclePumpMs { get; set; }

        /// <summary>
        /// docs/60: this session's weapon staging — the fire-group table it has sent, the ledger of
        /// which item guids have had real fire-group data delivered, and therefore the ONLY thing
        /// that may narrow regression guard 5. Null clearance unless <c>CRANBERRY_WIELD=1</c>.
        /// </summary>
        public WeaponSession Weapons { get; init; } = new();

        /// <summary>
        /// docs/81: this session's shooter book-keeping (fire hints, magazines, the refire clock)
        /// and its practice dummies. One field, because <c>WeaponFireArm</c> owns the logic.
        /// </summary>
        public SessionCombat Combat { get; } = new();

        public MatchScore Score { get; } = new();
        public LeaderboardRequestBudget RankingBudget { get; } = new();
        public RankBadge? RankPreview { get; set; }
        /// <summary>Kill-feed item, including the skin captured by the damaging shot.</summary>
        public uint LastDamageWeapon { get; set; }
        public bool LastDamageHeadshot { get; set; }

        /// <summary>Reused result buffer so answering a <c>0x82</c> allocates nothing per shot.</summary>
        public List<WeaponArmResult> WeaponArmResults { get; } = [];
        public int DestructiblesGeneration { get; set; } = -1;

        /// <summary>docs/61 §2: which of the fleet this client currently holds.</summary>
        public MatchVehicleStream StreamedVehicles { get; } = new();

        /// <summary>docs/61 §3: the caller <c>VehicleFleet.BurnFuel</c> never had.</summary>
        public VehicleFuelPump Fuel { get; } = new();

        /// <summary>docs/61 §4: hotwire timers and the keyed roll.</summary>
        public VehicleIgnition Ignition { get; } = new();
        public ulong VehicleEngineRuntimeGuid { get; set; }

        /// <summary>docs/61 §5: collapses the two-packet E-press into one mount.</summary>
        public VehicleEntryArbiter VehicleEntry { get; } = new();

        /// <summary>docs/61 §1: this session is registered with the bystander relay.</summary>
        public bool VehicleObserverRegistered { get; set; }

        /// <summary>
        /// docs/113 (D249): the lobby HUD has gone out for this match. One latch, because two arms
        /// can reach it — the client's own <c>ClientFinishedLoading</c> and the legacy blind timer
        /// — and the second must be a no-op rather than a second countdown.
        /// </summary>
        public bool LobbyHudSent { get; set; }

        /// <summary>
        /// docs/113 §3: the balances this session is holding, by <c>Currency.txt</c> id. Seeded
        /// from <see cref="MenuTopBarOptions"/> when the lobby's <c>ab 03</c> rows go out and
        /// debited by an accepted ante, so the number the client draws and the number the server
        /// checks an ante against are the same number.
        /// </summary>
        public Dictionary<uint, uint> Currency { get; } = [];

        /// <summary>
        /// docs/113 §4: what this session has staked on the current match — the body of the next
        /// <c>67 0d</c>. Reset to <see cref="MatchBountyState.None"/> by every new lobby.
        /// </summary>
        public MatchBountyState Bounty { get; set; } = MatchBountyState.None;

        /// <summary>
        /// docs/113 §6 (U-B1): how many times the client has opened its own Bounty screen this
        /// session, counted off the <c>9a 05</c> telemetry.
        /// </summary>
        public int BountyScreenOpens { get; set; }

        /// <summary>
        /// docs/113 §6 (U-B1): the client opened the Bounty screen while still in the lobby. This
        /// is the fact the owner's click has to produce, and the fact the post-teleport re-open
        /// has to be judged against.
        /// </summary>
        public bool BountyScreenSeenInLobby { get; set; }

        /// <summary>
        /// docs/113 §6 addendum (2026-09-03): how many times the client force-opened its own Bounty
        /// screen OUTSIDE the lobby this match — i.e. the owner's reported drop-open defect. This is
        /// the pass/fail number for <c>CRANBERRY_BOUNTY_SUPPRESS_DROP_OPEN</c>: it should read 0.
        /// A non-zero count is the 1148 client artefact (third branch) the server cannot reach.
        /// Reset per new lobby with the rest of the match's bounty state.
        /// </summary>
        public int BountyScreenInMatchOpens { get; set; }

        /// <summary>
        /// docs/109 lane 3C: this session's entry in <see cref="SessionRegistry"/> — the projection
        /// of this state that OTHER sessions replicate. Null before admission and after the link
        /// closes, which is what makes every peer hook a single null test.
        /// </summary>
        public PeerSession? Peer { get; set; }

        /// <summary>
        /// <c>Environment.TickCount64</c> at which this viewer may run its next peer interest pass.
        /// The channel-2 stream is 20 Hz and the sweep is 4 Hz; without this stamp the sweep would
        /// run on every record.
        /// </summary>
        public long NextPeerInterestMs { get; set; }

        /// <summary>
        /// Characters this session has asked for full data on with <c>0f 45</c>. Only the FIRST
        /// request per character is logged: <c>d9</c>'s blob is not derived (docs/100 §3) so the
        /// answer never comes, and the client retries.
        /// </summary>
        public HashSet<ulong> PeerFullDataRequests { get; } = [];

        /// <summary>
        /// docs/109 §5: this session holds a member slot in <see cref="SharedMatchGas"/>. One bool
        /// so the release is idempotent — a match can end through a death, an abandon or a link
        /// close, and only the first of them may decrement.
        /// </summary>
        public bool SharedGasJoined { get; set; }

        /// <summary>
        /// docs/63 §5.4: item → (<c>Models.txt</c> ground actor, <c>NAME_ID</c>), resolved lazily
        /// from the shared loot tables. An item the floor cannot produce cannot be dropped.
        /// </summary>
        public DroppedItemCatalogue? Droppable { get; set; }

        /// <summary>
        /// <c>Environment.TickCount64</c> at which the running shred's <c>BUSY_MSEC</c> window ends.
        /// A second <c>SalvageItem</c> arriving before it is refused (docs/86 edit 6a): the client
        /// has locked the character out for that long, so a request inside the window did not come
        /// from the context menu.
        /// </summary>
        public long ShredBusyUntil { get; set; }
        public PendingVehicleRemoval? PendingVehicleRemoval { get; set; }
        public long ProximityShredSequence { get; set; }
        public Dictionary<ulong, FuelCanDurability> FuelCans { get; } = [];

        /// <summary>D341: the running medical cast's window end, like <see cref="ShredBusyUntil"/>.</summary>
        public long ConsumeBusyUntil { get; set; }

        /// <summary>The unspent medical cast whose identity makes cancelled callbacks inert.</summary>
        public PendingMedicalCast? PendingMedicalCast { get; set; }
        public MedicalState PlayerMedical;
        public Dictionary<uint, int> HealingHudCounts { get; } = [];
        public int HealingHudGeneration { get; set; }
        public Armour BleedArmour;
        public int BleedGeneration { get; set; }
        public int QueueWaitGeneration { get; set; } = -1;
        public uint BleedEffect { get; set; }
        public ulong WoundAttacker { get; set; }
        public string? WoundAttackerName { get; set; }
        public uint WoundAttackerHealth { get; set; }
        public uint WoundWeapon { get; set; }

        /// <summary>
        /// <c>Environment.TickCount64</c> at which the running craft's cast bar ends (D277). A
        /// second <c>Command.RecipeStart</c> arriving before it is refused, for the same reason as
        /// <see cref="ShredBusyUntil"/>: the bar has locked the character out for that long.
        /// </summary>
        public long CraftBusyUntil { get; set; }

        /// <summary>
        /// Inventory guid of the weapon in the player's RHand, or 0 when the hand is empty
        /// (docs/36 §W). Non-zero makes every <see cref="SendCharacterAppearance"/> substitute the
        /// slot-7 attachment and carry the equipment-slot row that names this instance.
        /// </summary>
        public ulong HeldWeaponItemGuid { get; set; }

        public AugustSkinCatalogEntry? MenuWeaponPreview { get; set; }
        public uint MenuWeaponPreviewCategoryId { get; set; }
        public AugustSkinCatalogEntry? MenuApparelPreview { get; set; }

        /// <summary>
        /// Client-progress milestones for this session (docs/35). Passive: it owns no timer and is
        /// polled by <c>PumpWatchdog</c>, which rides <c>Later</c> and therefore dies with the link.
        /// </summary>
        public ClientProgressWatchdog Watchdog { get; } = new() { IdleTimeoutMs = 20_000 };

        /// <summary>
        /// docs/103: this link's developer console - tier, menu cursor, toggles and how many
        /// <c>AddWorldCommand</c> bursts its client has had. It dies with the link, so nothing has
        /// to be cleaned up. Named <c>DevConsole</c> and not <c>Console</c> so that no member of
        /// this file can ever shadow <c>System.Console</c>.
        /// </summary>
        public ConsoleSession DevConsole { get; } = new();

        /// <summary>The watchdog poll chain has been scheduled for this session.</summary>
        public bool WatchdogPumping { get; set; }

        /// <summary>
        /// Next 1-based inventory slot handed to a granted item. <b>Legacy path only</b>
        /// (<c>ZoneOptions.SendContainers = false</c>): with the container model on, a slot id is an
        /// index within a container and <c>InventoryContainer.NextFreeSlot</c> owns it (docs/41 §5d).
        /// </summary>
        public uint NextInventorySlot { get; set; } = 1;

        /// <summary>
        /// docs/41 §I1(a): this match actor's containers, loadout bindings and bulk. Null until the
        /// bootstrap has sent <c>SetCurrentLoadout</c> + <c>InitContainers</c> + <c>SetLoadoutSlots</c>,
        /// and cleared at every zoning/abandon so a new world starts with a new inventory. Later
        /// <c>InitContainers</c> publications are complete replacement snapshots for window opens or
        /// equipped-container set changes.
        /// The item guid space stays <see cref="LootWorld.NextItemGuid"/>'s, so instance guids remain
        /// unique across ground loot and inventory exactly as they are today.
        /// </summary>
        public PlayerInventory? Inventory { get; set; }
        public ulong InventorySkinTargetItemGuid { get; set; }
        public long InventorySkinTargetExpiresAtMs { get; set; }
        public string? InventoryActionUiState { get; set; }

        /// <summary>
        /// Whether this session has an outstanding self <c>AccessedCharacter.BeginCharacterAccess</c>
        /// grant. Keep self access across window close so the native Q/E item-use path remains valid.
        /// </summary>
        public bool CharacterAccessGranted { get; set; }

        /// <summary>
        /// docs/40: this player's movement profile and stat-burst bookkeeping. Nothing
        /// client-originated ever acknowledges a stat, so <c>StatsDelivered</c> records only what the
        /// server sent — the owner's own hands are the acceptance check.
        /// </summary>
        public PlayerMovementTracker MovementStats { get; set; } = new();
        public FootwearTier? FootwearAudio { get; set; }
        public string MenuView { get; set; } = "kotkdefault";

        /// <summary>
        /// docs/42: the doors this session has streamed in, keyed so that a door left open survives a
        /// stream-out. Created lazily by <see cref="ZoneService.SpawnNearbyDoors"/> so a session that
        /// never enters a match never touches the dataset.
        /// </summary>
        public MatchDoors? Doors { get; set; }

        /// <summary>
        /// docs/114 §1: the pre-match LOBBY's own door burst has been armed for this session.
        /// A latch of its own rather than <c>Doors.HasStreamed</c>, because the lobby burst and
        /// the landing burst are different worlds and the second must not be suppressed by the
        /// first — <c>MatchDoors.Clear()</c> on the world change resets the streaming anchor, and
        /// this latch is what stops the MENU arming twice on a re-entered lobby.
        /// </summary>
        public bool LobbyDoorsArmed { get; set; }

        /// <summary>
        /// docs/43: this match's parked vehicles. Created lazily with the first spawn burst, so a
        /// session that never lands never reads the roster or the anchor set.
        /// </summary>
        public VehicleFleet? Fleet { get; set; }

        /// <summary>The car park has been planned and streamed once for this session's match.</summary>
        public bool VehiclesArmed { get; set; }

        /// <summary>
        /// The world re-stream pump (docs/47 §I3 doors, docs/52 ground loot) is running on this
        /// session's <c>Later</c> chain. One latch for one timer chain: <see cref="ZoneService.PumpWorld"/>
        /// has two arms and stops only when BOTH have nothing left to do.
        /// </summary>
        public bool WorldPumping { get; set; }

        /// <summary>
        /// docs/48 §5.1: this match's seed. Drawn once at the countdown, salted per subsystem, and
        /// logged as hex so <c>CRANBERRY_MATCH_SEED</c> replays the whole match.
        /// </summary>
        public ulong MatchSeed { get; set; }

        /// <summary>
        /// docs/48: where this match dropped. <c>default</c> before the countdown has run, which is
        /// what every burst-centre fallback tests before it reaches for
        /// <see cref="ZoneOptions.MatchDropSpawn"/>. Reading the option instead would burst loot,
        /// vehicles and doors around Pleasant Valley while the player descends over Ruby Lake.
        /// </summary>
        public Vector4 Drop { get; set; }

        /// <summary>
        /// docs/48 §Integration step 5: the gas schedule built from this match's seed at the
        /// countdown, so the drop can be constrained to phase 1's circle before the gas starts.
        /// <see cref="StartGas"/> adopts it rather than building a second one from the wall clock.
        /// </summary>
        public GasSchedule? Schedule { get; set; }

        public ulong Guid { get; set; }
        public string CharacterName { get; set; } = string.Empty;
        public uint Gender { get; set; }

        /// <summary>The validated roster appearance used by every self/equipment resend.</summary>
        public CharacterVisuals Visuals { get; set; } = null!;

        /// <summary>Server-authoritative cosmetic picks shared by menu and match zoning.</summary>
        public AugustWardrobeState Wardrobe { get; set; } = null!;
        public VehicleSkinState VehicleSkins { get; set; } = new();
        public MenuVehiclePreview? VehiclePreview { get; set; }
        public uint VehiclePreviewSequence { get; set; }

        /// <summary>The full outfit has been repeated once after the current zone's actor became ready.</summary>
        public bool AppearanceReadySent { get; set; }

        /// <summary>
        /// The client has sent a <c>0f 20 Character.WeaponStance</c> of its own, which is the proof
        /// that its stance machine is running and the signal to stop re-asserting one
        /// (S6 §7.3; the owner's Z1 <c>AbilityState.ClientToldUsStance</c>).
        /// </summary>
        public bool ClientSentWeaponStance { get; set; }

        /// <summary>The world session's one post-<c>a0 05</c> stance assert has gone out.</summary>
        public bool WeaponStanceWorldAsserted { get; set; }

        /// <summary>How many <c>0f 20</c>s this server has asserted, for the session log.</summary>
        public int WeaponStancesSent { get; set; }

        /// <summary>Where this session is in the match flow (docs/11).</summary>
        public MatchStep Match { get; set; } = MatchStep.Menu;

        public PendingLogout? PendingLogout { get; set; }
        public bool LogoutPrepared { get; set; }
        public bool LogoutCompleted { get; set; }
        public bool AutoAcceptReplay { get; set; }

        /// <summary>Invalidates deferred interactions when logout replaces the cast bar.</summary>
        public int InteractionGeneration { get; set; }

        /// <summary>The parachute entity's guid once it has been sent; 0 before the drop.</summary>
        public ulong ChuteGuid { get; set; }

        /// <summary>The client asked to mount the chute and the mount burst went out.</summary>
        public bool MountRequested { get; set; }

        /// <summary>
        /// <c>Environment.TickCount64</c> at the mount burst, or 0 when no ride is under way — the
        /// start of the <see cref="DescentDeadline"/> window (docs/88 §4b).
        /// </summary>
        public long MountedAtMs { get; set; }

        /// <summary>The air spawn this drop released from, for the descent deadline's expected ride.</summary>
        public float ChuteAirY { get; set; }

        /// <summary>
        /// The chute's last reported altitude off the channel-3 pose stream, or <c>NaN</c> before
        /// the first position-bearing record. D239's near-ground gate: a chute more than
        /// <see cref="DescentDeadline.GroundProximityMetres"/> above the drop is flying and is never
        /// dismounted by the server, whatever the ride clock says (docs/115 §3).
        /// </summary>
        public float ChuteLastY { get; set; } = float.NaN;

        /// <summary>
        /// <c>Environment.TickCount64</c> at the last channel-3 record for this chute, or 0 when
        /// none has arrived since the mount burst. D239's silence gate: the five archived rides the
        /// deadline exists for are the ones where the client went quiet under canopy.
        /// </summary>
        public long ChuteLastPoseMs { get; set; }

        /// <summary>
        /// Channel-3 records for the mounted chute that carried no raw position of their own —
        /// docs/88 §4a's E5 measurement. A non-zero count in a live session is the evidence that the
        /// touchdown fallback's <c>EffectivePosition</c> gate is what has kept it from ever firing.
        /// </summary>
        public long PositionlessChuteRecords { get; set; }

        /// <summary>The client's SynchronizedTeleport.ClientReady has arrived for this drop.</summary>
        public bool TeleportReady { get; set; }

        /// <summary>SynchronizedTeleport.Release has been sent for this drop.</summary>
        public bool Released { get; set; }

        /// <summary>The development auto-match has been scheduled once for this session.</summary>
        public bool AutoMatchArmed { get; set; }

        public DateTime LastStreamLog { get; set; } = DateTime.MinValue;

        public DateTime LastManagedLog { get; set; } = DateTime.MinValue;
    }

    private enum MatchStep
    {
        Menu,
        Queued,
        Transferring,
        Zoning,
        Lobby,
        Dropping,
        InMatch,

        /// <summary>
        /// Lane 1D-lite: the match is over and the client is playing its own victory / wrap-up
        /// slides. Held here for at least <c>MatchEndOptions.DefaultEndedHoldSeconds</c> before
        /// <c>AbandonMatch</c> resets to a fresh lobby. Every deferred match closure already
        /// re-checks <c>state.Match</c>, so adding this step makes all of them inert for the hold
        /// without one of them being edited.
        /// </summary>
        Ended,
    }
}

internal static class TaskExtensions
{
    /// <summary>True when a continuation's antecedent did not run to completion.</summary>
    public static bool IsCanceledOrFaulted(this Task task) => task.IsCanceled || task.IsFaulted;
}
