using System.Globalization;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;
using Cranberry.Zone.DevConsole.Surfaces;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Zone;

/// <summary>
/// docs/103: the developer console and its Jiggy-style menu, on the server side of the facade.
/// <para>
/// This file is the <b>only</b> place where the console's delegates are bound to
/// <see cref="ZoneService"/>'s private members. Everything under <c>DevConsole/</c> - the parser,
/// the registry, the gates, the menu state machine, the renderer, the command bodies - knows
/// nothing about connections, sessions or packets: it sees only <see cref="ConsoleContext"/>. That
/// is what makes the whole feature testable without a client and what keeps the diff against the
/// contended <c>ZoneService.cs</c> down to single lines: the <c>using</c>, the dispatch clause, the
/// burst call, the god-mode guard, the session property, the login-time <c>ResolveConsoleTier</c>
/// and the self record's <c>FlagI</c> (design §4.6 plus Door A, review-1 M1).
/// </para>
/// <para>
/// <b>Nothing here is live-verified.</b> No console line has ever been drawn by this client:
/// twenty captures hold zero <c>09 42</c> (R3 §2.2). Every packet this file sends is one the server
/// already sends on another path - <c>11 0a</c>, <c>11 01</c>, <c>d7</c>/<c>db</c>, <c>0f 0a</c>,
/// <c>11 31</c> - and the two console-only carriers (<c>06 03</c>, <c>06 05</c>) are read from the
/// client's own dispatcher but have never been on a wire. Which of them the open pane draws is
/// design open question 1 and <c>/surface probe</c> is the thing that answers it.
/// </para>
/// </summary>
public sealed partial class ZoneService
{
    /// <summary>Where a console-spawned vehicle is parked: three units east of the player (D176).</summary>
    private const float ConsoleVehicleOffset = 3f;

    /// <summary>How far ahead of the player a console-spawned ground item lands (D176).</summary>
    private const float ConsoleLootAhead = 1.5f;

    /// <summary>Guards the one-time build of the console engine.</summary>
    private readonly object _devConsoleGate = new();

    private ConsoleEngine? _devConsoleEngine;
    private bool _devConsoleBuilt;

    /// <summary>
    /// The console engine, built once per service on first use, or null when
    /// <c>CRANBERRY_CONSOLE=0</c>.
    /// <para>
    /// It is lazy rather than a constructor line on purpose: <c>ZoneService.cs</c>'s constructor is
    /// inside the wave-10 / inventory lane's working tree, and a lazy getter in this file buys the
    /// same behaviour with one fewer edit to a contended file. The build is not free - it registers
    /// fifty names and hashes each against the client's 1,025-entry registry - so a unit test that
    /// constructs a service and never speaks to it never pays for it.
    /// </para>
    /// </summary>
    private ConsoleEngine? DevConsoleEngine
    {
        get
        {
            if (_devConsoleBuilt)
            {
                return _devConsoleEngine;
            }

            lock (_devConsoleGate)
            {
                if (!_devConsoleBuilt)
                {
                    _devConsoleEngine = BuildDevConsole();
                    _devConsoleBuilt = true;
                }
            }

            return _devConsoleEngine;
        }
    }

    /// <summary>
    /// The registry half of the boot banner, for <c>Program.cs</c>:
    /// <c>43 commands (34 live, 9 not-yet), 50 names, 0 collide with the client registry
    /// (1,025 hashes), 0 refused last run</c>. Empty when the console is off.
    /// </summary>
    public string ConsoleSummary => DevConsoleEngine?.Summary ?? string.Empty;

    private ConsoleEngine? BuildDevConsole()
    {
        ConsoleOptions options = _options.Console;
        if (!options.Enabled)
        {
            return null;
        }

        try
        {
            ConsoleEngine engine = ConsoleEngine.Create(options, CommandCatalog.Build(options), _log);

            // The runtime backstop for what the start-up census cannot see (design §4.3). The file
            // belongs to the client and may be absent, which is the healthy case; the reader is
            // mtime-cached and is re-read only on an inbound 09 42, never on a timer.
            ClientCollisionLog log = new(options.CollisionLogPath, _log.Warn);
            engine.RefusalSource = log.Read;

            IReadOnlyList<string> refused = log.Read();
            foreach (string name in refused)
            {
                if (engine.Registry.MarkRefused(name))
                {
                    _log.Warn($"console: RENAME '{name}' - the client refused it last run "
                        + $"({options.CollisionLogPath}); its menu leaf is greyed (docs/103 §7)");
                }
            }

            return engine;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A name that collides with the client's registry throws at Register, by design: it
            // would be an un-typeable menu row otherwise. Turning the whole console off is the
            // right answer, and the message names the offender - but it must never stop the host
            // from booting a match.
            _log.Warn($"console: DISABLED - the catalogue would not build ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// One inbound <c>Command.ExecuteCommand</c> (or the toggle's <c>Command.Spectate</c>), from the
    /// single guard clause in <c>HandleClientTunnel</c>.
    /// <para>
    /// <b>It catches everything a backend can throw.</b> <c>HandleClientTunnel</c> catches only
    /// <see cref="PacketFormatException"/>; anything else would be swallowed at
    /// <c>SoeListener.cs:116</c> and the console would simply go quiet - the round-21 defect. A
    /// throwing command answers one <c>- error:</c> line and writes the stack to the host log.
    /// </para>
    /// </summary>
    private void HandleConsoleCommand(SoeConnection connection, GatewaySessionState state, byte[] payload)
    {
        if (DevConsoleEngine is not ConsoleEngine engine)
        {
            return;
        }

        if (SpectateNotice.Matches(payload))
        {
            // The console toggle command sends this on every press (FUN_141291d50:100-104). On
            // retail it flipped the player to spectator; Cranberry never answers it, and naming it
            // here keeps the first click's log readable.
            _log.Log(
                TransportLogLevel.Debug,
                $"{connection} console: client sent Command.Spectate "
                + $"\"{SpectateNotice.TryReadTarget(payload) ?? "?"}\" (console toggle) - ignored");
            return;
        }

        ExecuteCommandRequest request = ExecuteCommandRequest.Parse(payload);
        if (request.Truncated)
        {
            _log.Warn($"{connection} console: 09 {request.Sub:x2} declared more argument bytes than "
                + "arrived");
        }

        ConsoleContext context = BuildConsoleContext(connection, state);
        engine.RefreshRefusals(context);

        try
        {
            engine.Execute(context, state.DevConsole, request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            context.Surface.Line($"- error: {ex.GetType().Name}: {ex.Message}");
            _log.Warn($"{connection} console: 0x{request.Hash:x8} threw {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// One navigation step from the client-side script, arriving as
    /// <c>WallOfData 9a 05 | String8 "CRANBERRY" | String8 "&lt;token&gt;" | u32 0</c> - the c2s
    /// half of R6 recommendation (b') (<c>out\devconsole-20260901\R6-graphical-menu.md</c> §4.2
    /// part 3).
    /// <para>
    /// <b>One state machine, three front doors.</b> The token is reduced by
    /// <see cref="LuaMenuSurface.Route"/> to exactly the command line the owner would have typed,
    /// and then goes through <see cref="ConsoleEngine.ExecuteLine"/> - the same entry point the
    /// menu's own leaves use. So <c>/d</c> typed in the chat box, <c>/m d</c> typed in the console
    /// and an arrow key captured by the script all end in <c>MenuInput.Parse</c> and none of them
    /// can drift from the others.
    /// </para>
    /// <para>
    /// <b>Nothing is trusted.</b> The token is attacker-controlled in exactly the sense any typed
    /// console line is, so it runs under the same tier gate, the same match gate and the same rate
    /// limit; an unknown token becomes a <c>/m &lt;word&gt;</c> the menu parser rejects with a hint,
    /// not an error. The whole call is wrapped, because <c>HandleClientTunnel</c> catches only
    /// <see cref="PacketFormatException"/> and a throwing backend would otherwise silence the link.
    /// </para>
    /// </summary>
    private void HandleCranberryMenuInput(SoeConnection connection, string action)
    {
        if (DevConsoleEngine is not ConsoleEngine engine)
        {
            _log.Log(
                TransportLogLevel.Debug,
                $"{connection} console: CranberryMenu sent '{action}' but the console is off");
            return;
        }

        if (connection.Tag is not GatewaySessionState state)
        {
            return;
        }

        if (LuaMenuSurface.Route(action) is not (string name, string arguments))
        {
            _log.Info($"{connection} console: CranberryMenu sent an empty or over-long token - ignored");
            return;
        }

        ConsoleContext context = BuildConsoleContext(connection, state);
        engine.RefreshRefusals(context);

        try
        {
            engine.ExecuteLine(context, state.DevConsole, name, arguments);
            _log.Log(
                TransportLogLevel.Debug,
                $"{connection} console: CranberryMenu 9a 05 -> /{name}".TrimEnd());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            context.Surface.Line($"- error: {ex.GetType().Name}: {ex.Message}");
            _log.Warn($"{connection} console: CranberryMenu threw {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Sends the <c>AddWorldCommand</c> burst for one <c>ClientIsReady</c>, synchronously.
    /// <para>
    /// <b>Synchronous is the contract</b> (D178, refute-2 F3/F4): the bootstrap tests pin the label
    /// list of everything sent after the menu <c>ClientIsReady</c> returns, so a burst on
    /// <c>Later</c> or <c>Post</c> would land inside the pinned window and would leak into every
    /// test pump. The burst goes out after the reply the arm was already sending, which is why the
    /// call site sits at the very end of the <c>AppearanceReadySent</c> block.
    /// </para>
    /// </summary>
    private void SendConsoleBurst(SoeConnection connection, GatewaySessionState state, bool zoning)
    {
        if (DevConsoleEngine is not ConsoleEngine engine)
        {
            return;
        }

        engine.OnClientIsReady(BuildConsoleContext(connection, state), state.DevConsole, zoning);
    }

    /// <summary>
    /// Resolves this link's console tier from the remote, the character name and
    /// <c>CRANBERRY_CONSOLE_TIERS</c>, and stores it on the session.
    /// <para>
    /// Called once from <c>HandleLogin</c> - which is what makes the tier known <b>before</b> the
    /// first <c>SendSelfToClient</c>, the record that carries Door A's flag (review-1 M1) - and
    /// again on every inbound <c>09 42</c> through <see cref="BuildConsoleContext"/>.
    /// </para>
    /// </summary>
    private ConsoleTier ResolveConsoleTier(SoeConnection connection, GatewaySessionState state)
    {
        var options = LocalOwnerAccountId is not null && state.AccountId != LocalOwnerAccountId
            ? _options.Console with { LocalIsOwner = false } : _options.Console;
        ConsoleTier tier = state.DevConsole.TierOverride ?? ConsolePermission.Resolve(
            connection.RemoteEndPoint, state.CharacterName, state.Guid, options);
        state.DevConsole.Tier = tier;
        return tier;
    }

    /// <summary>
    /// Door A (design §3.5, docs/103 §3): whether this session's <c>SendSelfToClient</c> carries
    /// <c>SelfRecord.FlagI</c> - the client-side flag at player+0x106b9 whose own Tilde binding is
    /// believed to open the debug console with no tool at all.
    /// <para>
    /// Off unless <c>CRANBERRY_CONSOLE_SELF_FLAG=1</c> <b>and</b> this link resolved to Owner. The
    /// flag rides the login-critical self record, so the switch is opt-in; it is one bool inside a
    /// fixed layout, so the record's length is unchanged either way and a login cannot fail on it.
    /// Whether the client really opens the pane on it is [U] until the first click - that is what
    /// the tool in <c>tools\ConsoleOpener</c> (Door B) exists to fall back on.
    /// </para>
    /// </summary>
    private bool ConsoleSelfFlag(GatewaySessionState state)
    {
        if (!_options.Console.Enabled
            || !_options.Console.SelfFlagOpensConsole
            || state.DevConsole.Tier != ConsoleTier.Owner)
        {
            return false;
        }

        _log.Info($"console: Door A armed - the self record for guid {state.Guid} carries "
            + $"FlagI=true (+0x106b9); press Tilde in the client (docs/103 §3)");
        return true;
    }

    /// <summary>
    /// Binds every delegate the console can reach. Read it as the console's whole contract with the
    /// rest of the server: a delegate that is present is a row that works, a delegate that is null
    /// is a row that says "not available yet", and every nullable session member is read through an
    /// accessor so a backend answers <c>- no vehicles streamed</c> instead of dereferencing null
    /// inside a packet handler.
    /// </summary>
    private ConsoleContext BuildConsoleContext(SoeConnection connection, GatewaySessionState state)
    {
        ConsoleOptions options = _options.Console;
        ConsoleSession session = state.DevConsole;

        // Re-resolved on every inbound line rather than trusted from login: it is a dictionary
        // lookup, and it keeps the tier honest if the option list is ever made reloadable. The
        // login-time resolve is the one Door A reads (ResolveConsoleTier, below).
        ResolveConsoleTier(connection, state);

        void Send(Action<PacketWriter> write) => SendTunnel(connection, write);

        // Hosted-game invitations use the same slash-command transport as development
        // commands. Ordinary accounts receive those replies in chat, with no console flag.
        bool development = ConsoleAccessPolicy.HasDevelopmentAccess(session.Tier);
        IConsoleSurface surface = SurfaceFactory.For(
            development ? session.SurfaceKind(options) : ConsoleSurfaceKind.Chat0, Send);

        return new ConsoleContext
        {
            Surface = surface,
            SurfaceFor = kind => development ? SurfaceFactory.For(kind, Send) : surface,
            Send = Send,
            Log = message => _log.Info($"{connection} {message}"),
            Warn = message => _log.Warn($"{connection} {message}"),
            Remote = connection.RemoteEndPoint.ToString(),
            Name = state.CharacterName,
            Guid = state.Guid,
            Tier = session.Tier,
            ServerVersion = ConsoleBuildStamp,
            ClientBuild = "1148 / 0.0.118.208059",
            MatchStep = () => state.Match.ToString(),
            Position = () => state.Movement.Player?.Position is Vector3 p ? new Vector4(p, 1f) : null,
            Yaw = () => state.Movement.Player?.Orientation is float yaw
                ? yaw * (180f / MathF.PI)
                : null,
            OnlinePlayers = () => _throwableSessions.Values.Count(s => s.Connection.State == ConnectionState.Open),
            ResolveItem = ItemNames.Resolve,
            ResolvePlace = place => ConsolePlace(state, place),
            RowNote = id => ConsoleRowNote(state, id),

            Teleport = target => ConsoleTeleport(connection, state, target),
            Place = place => ConsolePlace(state, place),
            Heal = hp => ConsoleHeal(connection, state, hp),
            Hurt = amount => ConsoleHurt(connection, state, amount),
            Kill = () => ConsoleKill(connection, state),
            Inventory = () => ConsoleInventory(state),
            GiveItem = (id, count) => ConsoleGive(connection, state, id, count),
            GiveKit = kit => ConsoleKit(connection, state, kit),
            GiveAmmo = count => ConsoleAmmo(connection, state, count),
            DropItem = slot => ConsoleDrop(connection, state, slot),
            Parachute = height => ConsoleParachute(connection, state, height),
            Speed = multiplier => ConsoleSpeed(connection, state, multiplier),
            Players = ConsolePlayers,
            Evict = (name, reason) => ConsoleEvict(name, reason),
            SetTier = (name, tier) => ConsoleTierChange(name, tier),
            HostedGame = call => RunHostedPanelCommand(connection, state, call),
            TeleportHere = name => ConsoleTeleportHere(state, name),
            CombatStatus = () => ConsoleCombatStatus(state),
            Places = () => ConsolePlaces("", 1),
            FindPlaces = ConsolePlaces,
            TeleportPlace = name => ConsoleTeleportPlace(connection, state, name),
            SpawnVehicle = word => ConsoleSpawnVehicle(connection, state, word),
            EnterVehicle = () => ConsoleEnterVehicle(connection, state),
            ExitVehicle = () => ConsoleExitVehicle(connection, state),
            Refuel = fraction => ConsoleRefuel(connection, state, fraction),
            VehicleCensus = () => ConsoleVehicleCensus(state),
            SpawnLoot = (id, count) => ConsoleSpawnLoot(connection, state, id, count),
            SpawnLootRing = () => ConsoleSpawnLootRing(connection, state),
            FindLoot = (item, radius) => ConsoleFindLoot(state, item, radius),
            LootStats = () => ConsoleLootStats(state),
            AirdropStats = () => ConsoleAirdropStats(state),
            StartMatch = () => ConsoleStartMatch(connection, state),
            AbandonMatch = ended => ConsoleAbandonMatch(connection, state, ended),
            MatchStatus = () => ConsoleMatchStatus(state),
            Gas = verb => ConsoleGas(connection, state, verb),
            Doors = (verb, radius) => ConsoleDoors(connection, state, verb, radius),
            PracticeTarget = verb => ConsolePracticeTarget(connection, state, verb),
            Bots = (verb, count, difficulty, distance) => ConsoleBots(connection, state, verb, count, difficulty, distance),
            Rank = argument => ConsoleRank(state, argument),
            Announce = text => ConsoleAnnounce(connection, text),
            Dump = (what, rows) => ConsoleDump(state, what, rows),
            Watchdog = () => ConsoleWatchdog(state),
            WireLog = () => ConsoleWireLog(),
            Raw = bytes => ConsoleRaw(connection, bytes),
            UiScript = (script, ints) => ConsoleUiScript(connection, script, ints),
            Later = (ms, work) => Later(connection, ms, work),
            InfoLines = () => ConsoleInfo(connection, state),
        };
    }

    /// <summary>The server build shown in the frame title: the assembly's informational version.</summary>
    private static string ConsoleBuildStamp { get; } =
        typeof(ZoneService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(a => a.InformationalVersion)
            .FirstOrDefault()
        ?? typeof(ZoneService).Assembly.GetName().Version?.ToString()
        ?? "dev";

    // --- player ---------------------------------------------------------------------------------

    private ConsoleReply ConsoleTeleport(SoeConnection connection, GatewaySessionState state, Vector3 target)
    {
        // The same 11 0a the menu mark and the air spawn already send (R3 #2, :717 / :2227). In a
        // match the door, loot and vehicle pumps re-stream around the new pose by themselves.
        if (!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
            return ConsoleReply.Failed("coordinates must be finite");
        if (state.Match is MatchStep.Dropping or MatchStep.Transferring or MatchStep.Zoning or MatchStep.Ended
            || state.DeathSent || state.ChuteGuid != 0 || state.MountRequested)
            return ConsoleReply.Failed("land and finish loading before teleporting; start a fresh match if dead");
        if (state.Fleet?.TryGetForOccupant(state.Guid, out _) == true)
            return ConsoleReply.Failed("exit your vehicle before teleporting (/exit)");
        var previous = state.Movement.Player?.Position;
        state.Movement.PinPlayer(target);
        var mark = new Vector4(target, 1f);
        SendTunnel(connection, writer =>
            new UpdateLocation(mark, new Vector4(0, 0, 0, 1), Apply: true).WriteTo(writer));
        state.DevConsole.LastPosition = previous;
        _log.Info($"{connection} console: teleport to {FormatPosition(target)}");
        return ConsoleReply.Did($"teleported to {Coordinates(target)}");
    }

    private Vector3? ConsolePlace(GatewaySessionState state, string place)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return null;
        }

        switch (place.Trim().ToLowerInvariant())
        {
            case "drop":
                Vector4 drop = state.Drop != default ? state.Drop : _options.MatchDropSpawn;
                return new Vector3(drop.X, drop.Y, drop.Z);

            case "staging":
                Vector4 staging = StagingPosition(state);
                return new Vector3(staging.X, staging.Y, staging.Z);

            case "spawn":
                Vector4 spawn = _options.SpawnPosition;
                return new Vector3(spawn.X, spawn.Y, spawn.Z);

            default:
                var places = _drop.Places(out _);
                if (places is null) return null;
                var matches = TeleportPlaces.Find(places, place);
                return matches.Count == 1 ? matches[0].Anchor + new Vector3(0, 0.5f, 0) : null;
        }
    }

    private ConsoleReply ConsoleHeal(SoeConnection connection, GatewaySessionState state, uint? hitpoints)
    {
        uint max = _options.Gas.MaxHitpoints;
        uint target = Math.Min(hitpoints ?? max, max);
        uint previous = state.Hitpoints;
        state.Hitpoints = target;
        PublishPlayerHealth(connection, state, previous);
        _log.Info($"{connection} console: health set to {target}/{max}");
        return ConsoleReply.Did($"health {target}/{max}");
    }

    private ConsoleReply ConsoleHurt(SoeConnection connection, GatewaySessionState state, uint amount)
    {
        if (state.Hitpoints == 0)
        {
            return ConsoleReply.Failed("already at zero health");
        }

        uint before = state.Hitpoints;

        // Deliberately the real damage entry (R3 #16): it sends the 11 01 bar, it honours the god
        // guard this lane added at its top, and at zero it goes on to the one death path.
        ApplyGasDamage(connection, state, amount);

        return state.Hitpoints == before && state.DevConsole.Invulnerable
            ? ConsoleReply.Did($"god mode absorbed {amount} - health {state.Hitpoints}/{_options.Gas.MaxHitpoints}")
            : ConsoleReply.Did($"-{amount} hp - health {state.Hitpoints}/{_options.Gas.MaxHitpoints}");
    }

    private ConsoleReply ConsoleKill(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Hitpoints == 0)
        {
            return ConsoleReply.Failed("already dead");
        }

        SendGasDeath(connection, state);
        return ConsoleReply.Did("killed you through the gas death path");
    }

    // --- items ----------------------------------------------------------------------------------

    private static ConsoleReply ConsoleInventory(GatewaySessionState state)
    {
        if (state.Inventory is not PlayerInventory inventory)
        {
            return ConsoleReply.Failed("no inventory yet -- it is built at the in-match ClientIsReady");
        }

        (int used, int max) = inventory.Capacity;
        List<string> lines =
        [
            $"* bag {inventory.Items.Count} item(s), {inventory.Containers.Count} container(s), bulk {used}/{max}",
        ];

        foreach (KeyValuePair<uint, InventoryItemInstance> slot in inventory.LoadoutSlots.OrderBy(s => s.Key))
        {
            lines.Add(
                $"  slot {slot.Key}  {ItemNames.Label(slot.Value.DefinitionId)} x{slot.Value.Count} "
                + $"id={slot.Value.DefinitionId} guid={slot.Value.Guid}");
        }

        foreach (KeyValuePair<uint, InventoryItemInstance> worn in inventory.EquipmentSlots.OrderBy(s => s.Key))
        {
            lines.Add($"  worn {worn.Key}  {ItemNames.Label(worn.Value.DefinitionId)} "
                + $"id={worn.Value.DefinitionId} guid={worn.Value.Guid}");
        }

        foreach (var item in inventory.Items.Values.Where(i => i.LoadoutSlotId == 0).OrderBy(i => i.DefinitionId))
            lines.Add($"  bag {ItemNames.Label(item.DefinitionId)} x{item.Count} id={item.DefinitionId} guid={item.Guid}");
        return ConsoleReply.Plain(lines);
    }

    // --- vehicles -------------------------------------------------------------------------------

    private ConsoleReply ConsoleSpawnVehicle(SoeConnection connection, GatewaySessionState state, string word)
    {
        if (VehicleCommands.ResolveVehicle(word) is not uint vehicleId)
        {
            return ConsoleReply.Failed($"not a name: '{word}'");
        }

        // InMatch begins at chute release, before landing. The retained player record still
        // describes the staging area during descent, so it cannot position a console vehicle.
        if (state.ChuteGuid != 0)
        {
            return ConsoleReply.Failed("land before spawning a vehicle -- /car atv after landing");
        }

        MatchVehicle? occupied = state.Fleet?.TryGetForOccupant(state.Guid, out MatchVehicle? seated) == true
            ? seated : null;
        if ((occupied?.Position ?? state.Movement.Player?.Position) is not Vector3 here)
        {
            return ConsoleReply.Failed("no position yet -- move once so a channel-2 record arrives");
        }

        return SpawnConsoleVehicleAt(connection, state, vehicleId,
            new Vector3(here.X + ConsoleVehicleOffset, here.Y, here.Z),
            occupied?.Yaw ?? state.Movement.Player?.Orientation ?? 0f);
    }

    private ConsoleReply SpawnConsoleVehicleAt(SoeConnection connection, GatewaySessionState state,
        uint vehicleId, Vector3 position, float yaw, bool autoMount = false)
    {

        VehicleFleet fleet;
        try
        {
            fleet = EnsureVehicleFleet(connection, state,
                populate: _options.SendVehicles && _options.VehicleRadius > 0f && _options.VehicleMaxPerBurst > 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"{connection} console: vehicle fleet unavailable ({ex.GetType().Name}: {ex.Message})");
            return ConsoleReply.Failed($"the vehicle roster or parking plan is unavailable ({ex.GetType().Name})");
        }

        if (!fleet.Roster.TryGet(vehicleId, out VehicleDefinition? definition))
        {
            return ConsoleReply.Failed($"the roster has no vehicle {vehicleId}");
        }

        if (!state.VehicleObserverRegistered)
        {
            _vehiclePoses.Register(new SessionVehicleObserver(this, connection, state));
            state.VehicleObserverRegistered = true;
        }

        // Retiring a wreck must not let a later console spawn reuse another vehicle's ids.
        int ordinal = fleet.CreatedCount + 1;
        ulong guid = VehicleWorldGuidBase + 0x0001_0000UL + (ulong)ordinal;
        uint transient = VehicleTransientIdBase + 500_000u + (uint)ordinal;

        MatchVehicle car = new(
            guid,
            transient,
            definition,
            position,
            yaw,
            _options.VehicleFleet.MaxHealth,
            fleet.SpawnFuel(state.MatchSeed, anchorInstanceId: 0));
        fleet.Add(car);

        // A stationary peer may have exhausted its disc before this car existed. Let its normal
        // paced stream discover the new entry without requiring the peer to move first.
        if (_sharedLootMembership.TryGetValue(state, out ulong matchId))
        {
            foreach (GatewaySessionState viewer in _sharedLootMatches[matchId].Members.Keys)
                viewer.StreamedVehicles.InvalidateCandidates();
        }
        else state.StreamedVehicles.InvalidateCandidates();

        // NoteSpawned first, exactly as the parking burst does (:3789): without it the first
        // re-stream tick would send a SECOND actor for the same guid that nothing ever destroys.
        if (!state.StreamedVehicles.NoteSpawned(car.Guid, car.Position))
        {
            return ConsoleReply.Failed($"vehicle guid {guid} was already streamed");
        }

        var lightweight = new AddLightweightVehicle(
            car.Guid,
            car.TransientId,
            car.Definition.ModelId,
            car.Position,
            car.Rotation,
            car.Definition.VehicleId,
            OwnerGuid: 0,
            PositionUpdate: PositionUpdateBlock.AtRest(car.Position, car.Yaw, car.LastRotation),
            SpawnFlags1: _options.VehicleSpawnFlags1,
            BodyShaderGroupId: _options.VehicleShader ? car.SkinShaderGroup ?? VehicleShaderGroups.For(car.Definition.VehicleId) : 0,
            RenderDistance: _options.VehicleRenderDistance);
        SendTunnel(connection, writer => lightweight.WriteTo(writer));
        SendTunnel(connection, writer =>
            VehicleFullState.Create(car).WriteTo(writer));

        if (autoMount)
        {
            TryEnterVehicle(connection, state, car.Guid, 0, "native /vehicle auto mount", VehicleEntrySource.MountRequest);
            if (car.SeatOf(state.Guid) < 0)
                return ConsoleReply.Did($"spawned {definition.Name} at {Coordinates(position)}; auto mount was refused");
        }

        _log.Info($"{connection} console: spawned {definition.Name} guid {guid} at {FormatPosition(position)}");
        return ConsoleReply.Did($"spawned {definition.Name} (id {vehicleId}) at {Coordinates(position)}");
    }

    private ConsoleReply ConsoleEnterVehicle(SoeConnection connection, GatewaySessionState state)
    {
        if (NearestVehicle(state) is not MatchVehicle car)
        {
            return ConsoleReply.Failed("no vehicles streamed -- /car spawns one");
        }

        return TryEnterVehicle(connection, state, car.Guid, 0, "console /enter", VehicleEntrySource.MountRequest)
            ? ConsoleReply.Did($"took the wheel of {car.Definition.Name}")
            : ConsoleReply.Failed($"{car.Definition.Name} refused the seat");
    }

    private ConsoleReply ConsoleExitVehicle(SoeConnection connection, GatewaySessionState state) =>
        TryExitVehicle(connection, state, "console /exit")
            ? ConsoleReply.Did("out of the vehicle")
            : ConsoleReply.Failed("you are not in a vehicle");

    private ConsoleReply ConsoleRefuel(SoeConnection connection, GatewaySessionState state, float fraction)
    {
        if (state.Fleet is not VehicleFleet fleet)
        {
            return ConsoleReply.Failed("no vehicles streamed -- /car spawns one");
        }

        MatchVehicle? car = fleet.TryGetForOccupant(state.Guid, out MatchVehicle? mine)
            ? mine
            : NearestVehicle(state);
        if (car is null)
        {
            return ConsoleReply.Failed("no vehicles streamed -- /car spawns one");
        }

        float wanted = (fleet.Options.MaxFuel * fraction) - car.Fuel;
        if (wanted <= 0f)
        {
            return ConsoleReply.Did($"{car.Definition.Name} already holds {car.Fuel:0} of {fleet.Options.MaxFuel:0}");
        }

        uint previous = (uint)Math.Max(0f, car.Fuel);
        float added = fleet.Refuel(car, wanted);
        SendTunnel(connection, writer => new CharacterResourceUpdate(car.Guid, AugustFuelFacts.ResourceId,
            AugustFuelFacts.ResourceType, (uint)Math.Max(0f, car.Fuel), previous).WriteTo(writer));
        return ConsoleReply.Did(
            $"{car.Definition.Name} +{added:0} fuel - {car.Fuel:0}/{fleet.Options.MaxFuel:0}");
    }

    private static ConsoleReply ConsoleVehicleCensus(GatewaySessionState state)
    {
        if (state.Fleet is not VehicleFleet fleet)
        {
            return ConsoleReply.Failed("no fleet yet -- it is planned at the landing");
        }

        Vector3? here = fleet.TryGetForOccupant(state.Guid, out MatchVehicle? occupied)
            ? occupied.Position : state.Movement.Player?.Position;
        int consoleCars = fleet.Vehicles.Count(v => v.AnchorInstanceId == 0);
        List<string> lines =
        [
            $"* fleet {fleet.Count} total -- {fleet.Count - consoleCars} from map pads, {consoleCars} console",
            $"  {state.StreamedVehicles.LiveCount} streamed to you; "
                + $"{fleet.Vehicles.Count(v => v.OccupantCount > 0)} occupied; "
                + $"{fleet.Vehicles.Count(v => v.Health == 0)} destroyed",
        ];
        IEnumerable<MatchVehicle> nearest = here is Vector3 centre
            ? fleet.Vehicles.OrderBy(v => Vector3.DistanceSquared(v.Position, centre)).Take(8)
            : fleet.Vehicles.Take(8);

        foreach (MatchVehicle car in nearest)
        {
            string distance = here is Vector3 centre2
                ? $"{Vector3.Distance(car.Position, centre2):0} m"
                : "?";
            string seat = car.Health == 0 ? "destroyed" : car.OccupantCount == 0 ? "parked" : "occupied";
            lines.Add($"  {car.Definition.Name,-12} {distance,7}  {seat}  fuel {car.Fuel:0}");
        }

        return ConsoleReply.Plain(lines);
    }

    private static MatchVehicle? NearestVehicle(GatewaySessionState state)
    {
        if (state.Fleet is not VehicleFleet fleet)
        {
            return null;
        }

        if (state.Movement.Player?.Position is not Vector3 centre)
        {
            return fleet.Vehicles.FirstOrDefault();
        }

        return fleet.Vehicles
            .OrderBy(candidate => Vector3.DistanceSquared(candidate.Position, centre))
            .FirstOrDefault();
    }

    // --- loot -----------------------------------------------------------------------------------

    private ConsoleReply ConsoleSpawnLoot(
        SoeConnection connection,
        GatewaySessionState state,
        uint definitionId,
        uint count,
        bool pickup = false)
    {
        if (!InventoryItemFacts.TryGet(definitionId, out var fact) || count == 0 || count > 100)
            return ConsoleReply.Failed("unknown item or invalid count (1..100)");
        if (count > Math.Max(1, fact.MaxStackSize))
            return ConsoleReply.Failed($"this item allows at most {Math.Max(1, fact.MaxStackSize)} per grant");
        if (state.Movement.Player?.Position is not Vector3 here)
        {
            return ConsoleReply.Failed("no position yet -- move once so a channel-2 record arrives");
        }

        uint groundModelId = 0;
        uint nameId = 0;
        try
        {
            state.Droppable ??= DroppableItems.Value;
            state.Droppable.TryGet(definitionId, out groundModelId, out nameId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ConsoleReply.Failed($"the drop catalogue is unavailable ({ex.GetType().Name})");
        }

        if (groundModelId == 0)
        {
            groundModelId = _options.GroundLootModelId;
        }

        float yaw = state.Movement.Player?.Orientation ?? 0f;
        var position = new Vector3(
            here.X + (ConsoleLootAhead * MathF.Sin(yaw)),
            here.Y,
            here.Z + (ConsoleLootAhead * MathF.Cos(yaw)));

        GroundLootItem item = SpawnGroundLoot(
            connection, state, definitionId, groundModelId, position, count, nameId);

        // World guids are local to a viewer. Allocate the same match-owned identity as a player
        // drop, or a prior grant with this guid on another client can retire this new object.
        SharePlayerDrop(connection, state, item);
        if (pickup)
        {
            TryPickUpGroundLoot(connection, state, item.WorldGuid, "console give");
            return state.Loot.TryGet(item.WorldGuid, out _)
                ? ConsoleReply.Failed("pickup refused; item left at your feet")
                : ConsoleReply.Did($"gave {ItemNames.Label(definitionId)} x{count}");
        }
        if (_options.SendProximateItems)
        {
            SendProximateItems(connection, state);
        }

        return ConsoleReply.Did(
            $"{ItemNames.Label(definitionId)} x{count} on the ground at {Coordinates(position)}");
    }

    private ConsoleReply ConsoleSpawnLootRing(SoeConnection connection, GatewaySessionState state)
    {
        var before = state.Loot.Items.Select(item => item.WorldGuid).ToHashSet();
        SpawnDevGroundLoot(connection, state);
        var spawned = state.Loot.Items.Where(item => !before.Contains(item.WorldGuid)).ToArray();
        foreach (var item in spawned)
            SharePlayerDrop(connection, state, item);
        if (_options.SendProximateItems) SendProximateItems(connection, state);
        return spawned.Length == 0 ? ConsoleReply.Failed("developer ring contains no configured items")
            : ConsoleReply.Did($"developer ring: {spawned.Length} item(s), {state.Loot.Count} on the ground");
    }

    private static ConsoleReply ConsoleFindLoot(GatewaySessionState state, string item, float radius)
    {
        if (state.Movement.Player?.Position is not Vector3 here)
        {
            return ConsoleReply.Failed("no position yet -- move once so a channel-2 record arrives");
        }

        int? wanted = ItemNames.Resolve(item)
            ?? (int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : null);
        if (wanted is null)
        {
            string? nearest = ItemNames.Nearest(item);
            return ConsoleReply.Failed(
                nearest is null ? $"unknown item '{item}'" : $"unknown item '{item}' -- did you mean {nearest}?");
        }

        List<GroundLootItem> hits =
        [
            .. state.Loot.ItemsWithin(here, radius)
                .Where(candidate => candidate.ItemDefinitionId == (uint)wanted.Value)
                .OrderBy(candidate => Vector3.DistanceSquared(candidate.Position, here))
                .Take(20),
        ];

        if (hits.Count == 0)
        {
            return ConsoleReply.Failed(
                $"no {ItemNames.Label((uint)wanted.Value)} within {radius:0} m of you");
        }

        List<string> lines = [$"* {hits.Count} x {ItemNames.Label((uint)wanted.Value)} within {radius:0} m"];
        foreach (GroundLootItem hit in hits)
        {
            lines.Add($"  {Vector3.Distance(hit.Position, here),6:0.0} m  /tp {Coordinates(hit.Position)}");
        }

        return ConsoleReply.Plain(lines);
    }

    /// <summary>
    /// D274, <c>/loot airdrop</c>. Read-only: it reports the schedule and never advances it, which
    /// is what keeps a console verb out of the match's own determinism.
    /// </summary>
    private ConsoleReply ConsoleAirdropStats(GatewaySessionState state)
    {
        if (state.Airdrops is not MatchAirdrops airdrops)
        {
            return ConsoleReply.Failed(
                "no airdrops this match -- CRANBERRY_AIRDROPS=0, or the match has not started");
        }

        long clock = state.Gas is GasController gas && gas.Running
            ? gas.MatchClockAt(Environment.TickCount64)
            : 0;

        List<string> lines =
        [
            $"* airdrops: {airdrops.Announced} announced, {airdrops.Landed} landed, "
                + $"{airdrops.InFlight} in flight, {state.AirdropCrates.Count} standing",
            $"  match clock {clock / 1000}s; next crate at {airdrops.NextDropAtMs / 1000}s "
                + $"(supply cap {airdrops.Options.MaxDropsPerMatch}; bombs stop at "
                + $"{airdrops.Options.StopAtPlayersAlive} alive)",
            $"  bombers: {airdrops.BombRuns} launched, {airdrops.BombsExploded} bombs impacted",
        ];

        foreach ((ulong guid, AirdropCrateState crate) in state.AirdropCrates)
        {
            long left = crate.UnlockAtMs - Environment.TickCount64;
            lines.Add($"  crate #{crate.Index + 1} {guid} at {Coordinates(crate.Position)} -- "
                + (left > 0 ? $"locked for {left} ms" : "open")
                + $", {crate.Contents.Count} item(s)");
        }

        return ConsoleReply.Plain(lines);
    }

    private static ConsoleReply ConsoleLootStats(GatewaySessionState state) =>
        ConsoleReply.Plain(
        [
            $"* ground objects {state.Loot.Count}, streamed {state.StreamedLoot.LiveCount}, "
                + $"on wire {state.StreamedLoot.OnWireCount}",
            $"  bursts {state.StreamedLoot.BurstCount}, streamed total {state.StreamedLoot.StreamedCount}, "
                + $"evicted {state.StreamedLoot.EvictedCount}",
            $"  markers taken {state.StreamedLoot.TakenMarkerCount}, boxes taken {state.StreamedLoot.TakenBoxCount}",
        ]);

    // --- match ----------------------------------------------------------------------------------

    private ConsoleReply ConsoleStartMatch(SoeConnection connection, GatewaySessionState state)
    {
        if (state.DevConsole.Tier < ConsoleTier.Owner)
            return ConsoleReply.Refused($"Owner only (you are {state.DevConsole.Tier})");
        if (state.HostedGameId is not null)
            return StartHostedGame(state.AccountId, state.DevConsole.Tier >= ConsoleTier.Owner, state.BountyWorldId);
        if (state.Match == MatchStep.Lobby)
        {
            if (UsesPublicQueue(state))
            {
                if (connection.State != ConnectionState.Open || !state.PregameClientReady
                    || state.PendingLogout is not null || state.Watchdog.IsStalled)
                    return ConsoleReply.Refused("finish loading and cancel any exit before starting; check /watchdog");
                ulong matchId = state.BountyAdmission.MatchId;
                long now = Environment.TickCount64;
                PublicQueues.SetReadyPlayers(matchId, ReadyPublicLobbyPlayers(matchId), now);
                if (!PublicQueues.Snapshots.Any(r => r.MatchId == matchId
                        && r.Phase is >= PublicMatchPhase.ROSTER_FROZEN and < PublicMatchPhase.ENDING)
                    && !PublicQueues.TryForceFreeze(matchId, now))
                    return ConsoleReply.Refused("this public lobby is not available to start");
                // Freeze the complete reserved cohort, including late loaders. The shared path
                // starts every ready participant and opens the next same-mode generation.
                RefreshPublicLobby(matchId);
            }
            else BeginMatchDrop(connection, state);
            return state.Match == MatchStep.Dropping
                ? ConsoleReply.Did("match started; dropping from the pregame lobby")
                : ConsoleReply.Failed($"drop did not start ({state.Match}); check /watchdog");
        }
        if (state.Match != MatchStep.Menu)
            return ConsoleReply.Refused($"not now: {state.Match} (needs Menu or Lobby)");

        // The dev auto-match path (:1201-1211), run by hand.
        state.Match = MatchStep.Transferring;
        EnterMatch(connection, state, sendTransferReply: false);
        return ConsoleReply.Did($"match starting - zoning into {_options.MatchZoneName}");
    }

    private ConsoleReply ConsoleAbandonMatch(SoeConnection connection, GatewaySessionState state, bool ended)
    {
        if (ended)
        {
            SendVictory(connection, state);
            return state.VictorySent ? ConsoleReply.Did("match ended; victory and wrap-up started")
                : ConsoleReply.Failed("endgame is disabled or already complete");
        }
        if (state.Match is not (MatchStep.InMatch or MatchStep.Ended or MatchStep.Lobby))
            return ConsoleReply.Refused($"not now: {state.Match} (needs Lobby, InMatch or Ended)");
        AbandonMatch(connection, state, "console /lobby");
        state.Match = MatchStep.Transferring;
        EnterMatch(connection, state, sendTransferReply: false);
        return ConsoleReply.Did("returning to a fresh pregame lobby");
    }

    private ConsoleReply ConsoleMatchStatus(GatewaySessionState state)
    {
        List<string> lines =
        [
            $"* {state.Match}  hp {state.Hitpoints}/{_options.Gas.MaxHitpoints}  "
                + $"seed {MatchSeeds.Format(state.MatchSeed)}",
        ];

        if (state.Drop != default)
        {
            lines.Add($"  drop {Coordinates(new Vector3(state.Drop.X, state.Drop.Y, state.Drop.Z))}");
        }

        if (state.Gas is GasController gas && gas.Running)
        {
            long clock = gas.MatchClockAt(Environment.TickCount64);
            lines.Add($"  gas {(gas.Paused ? "PAUSED" : "running")}, match clock {Clock(clock)}, "
                + $"phase {state.Schedule?.PhaseIndexAt(clock).ToString(CultureInfo.InvariantCulture) ?? "?"}");
        }
        else
        {
            lines.Add("  gas not running");
        }

        lines.Add($"  loot {state.Loot.Count}  vehicles {state.Fleet?.Count ?? 0}  "
            + $"doors {state.Doors?.Count ?? 0} ({state.Doors?.OpenCount ?? 0} open)");
        return ConsoleReply.Plain(lines);
    }

    private ConsoleReply ConsoleGas(SoeConnection connection, GatewaySessionState state, string verb)
    {
        if (verb is "pause" or "resume" or "next")
            return ConsoleControlGas(connection, state, verb);
        switch (verb)
        {
            case "start":
                if (state.Gas is GasController running && running.Running)
                {
                    if (running.Paused) return ConsoleControlGas(connection, state, "resume");
                    return ConsoleReply.Failed("the gas is already running -- /gas status");
                }

                StartGas(connection, state);
                return state.Gas is { Running: true }
                    ? ConsoleReply.Did("gas started")
                    : ConsoleReply.Failed("the gas refused to start -- see the host log");

            case "stop":
                if (state.Gas is not GasController live || !live.Running)
                {
                    return ConsoleReply.Failed("the gas is not running");
                }

                live.Stop();
                LeaveSharedGas(state);
                SendTunnel(connection, w => GameModeHud.WriteCountdown(w, 0, labelId: 0));
                return ConsoleReply.Did("gas stopped for this player; /gas start rejoins it. Use /gas pause to freeze the match instead.");

            default:
                if (state.Gas is not GasController controller || !controller.Running)
                {
                    return ConsoleReply.Plain(["* gas not running"]);
                }

                long clock = controller.MatchClockAt(Environment.TickCount64);
                GasCircle circle = controller.ActiveCircleAt(Environment.TickCount64);
                List<string> lines =
                [
                    $"* gas {(controller.Paused ? "PAUSED" : "running")} clock {Clock(clock)}  phase "
                        + $"{state.Schedule?.PhaseIndexAt(clock).ToString(CultureInfo.InvariantCulture) ?? "?"}",
                    $"  circle centre {circle.Centre.X:0} {circle.Centre.Y:0} radius {circle.Radius:0} m",
                ];

                if (state.Schedule is GasSchedule schedule)
                {
                    long next = schedule.NextEventAtMs(clock);
                    lines.Add(next == long.MaxValue ? "  final circle reached; no further events"
                        : $"  next event in {Clock(Math.Max(0, next - clock))}{(controller.Paused ? " (frozen; /gas resume)" : "")}");
                }

                return ConsoleReply.Plain(lines);
        }
    }

    // --- world ----------------------------------------------------------------------------------

    private ConsoleReply ConsoleDoors(
        SoeConnection connection,
        GatewaySessionState state,
        string verb,
        float radius)
    {
        if (state.Doors is not MatchDoors doors || doors.Count == 0)
        {
            return ConsoleReply.Failed("no doors streamed -- they arrive with the landing burst");
        }

        if (state.Movement.Player?.Position is not Vector3 here)
        {
            return ConsoleReply.Failed("no position yet -- move once so a channel-2 record arrives");
        }

        long now = Environment.TickCount64;
        List<DoorInstance> targets;

        if (verb == "toggle")
        {
            if (!doors.TryResolveNearest(here, radius, out DoorInstance? nearest))
            {
                return ConsoleReply.Failed($"no door within {radius:0} m of you");
            }

            targets = [nearest];
        }
        else
        {
            bool wantOpen = verb == "open";
            targets =
            [
                .. doors.Instances
                    .Where(door => door.IsOpen != wantOpen
                        && Vector3.DistanceSquared(door.Position, here) <= radius * radius)
                    .OrderBy(door => Vector3.DistanceSquared(door.Position, here)),
            ];

            if (targets.Count == 0)
            {
                return ConsoleReply.Failed($"no door to {verb} within {radius:0} m of you");
            }
        }

        int moved = 0;
        foreach (DoorInstance door in targets)
        {
            // The same two lines the real interact path runs (TryToggleDoor :3936-3972); the
            // 09 2d prompt refresh it also sends is deliberately NOT sent here, because the
            // client drops a prompt reply whose guid is not its CURRENT interaction target
            // (docs/47 §4d) and a console sweep names doors the UI has never looked at.
            if (doors.TryToggle(door.WorldGuid, now, out DoorInstance? toggled) != DoorToggleOutcome.Toggled
                || toggled is null)
            {
                continue;
            }

            DoorStateUpdate update = toggled.StateUpdate();
            SendTunnel(connection, writer => update.WriteTo(writer));
            moved++;
        }

        return moved == 0
            ? ConsoleReply.Failed("every door in range was inside its own press window -- try again")
            : ConsoleReply.Did($"{verb} {moved} door(s) within {radius:0} m ({doors.OpenCount} open)");
    }

    private ConsoleReply ConsolePracticeTarget(
        SoeConnection connection,
        GatewaySessionState state,
        string verb)
    {
        if (verb == "status")
            return ConsoleReply.Plain([$"* practice targets live={state.Combat.Targets.All.Count(t => t.IsAlive && !t.IsCombatBot)}; kills={state.Score.Kills}, points={state.Score.KillPoints}; /bots status lists shared combat bots"]);
        var targetWords = verb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var badge = new RankBadge(RankedTier.Bronze);
        if (targetWords.Length > 1)
        {
            if (!RankBadge.TryParse(targetWords[1], out var tier)) return ConsoleReply.Failed("unknown target rank");
            badge = new(tier);
        }
        if (verb != "clear" && (state.DeathSent || state.VictorySent || state.Score.Settled || state.ChuteGuid != 0 || state.MountRequested))
            return ConsoleReply.Failed("enter a fresh match and land before spawning practice targets");
        foreach (var target in state.Combat.Targets.All.Where(t => !t.IsCombatBot).ToArray())
        {
            SendTunnel(connection, w => new RemovePlayer(target.WorldGuid).WriteTo(w));
            state.FullNpcSent.Remove(target.WorldGuid);
            state.Combat.Targets.Remove(target);
        }
        state.Combat.TargetsArmed = false;
        if (verb == "clear") return ConsoleReply.Did("practice targets removed");
        state.Score.PracticeSession = true;
        var fallback = state.Drop != default ? state.Drop : _options.MatchDropSpawn;
        var origin = state.Movement.Player?.Position ?? new Vector3(fallback.X, fallback.Y, fallback.Z);
        var options = _options.Combat with { PracticeTarget = true, PracticeTargetFullKit = true, PracticeTargetCount = Math.Clamp(_options.Combat.PracticeTargetCount, 1, 20) };
        var burst = Combat.PracticeTargetSpawner.Build(state.Combat.Targets, origin, state.Movement.Player?.Orientation ?? 0f, options);
        foreach (var spawn in burst)
        {
            spawn.Target.Badge = badge;
            spawn.Target.Name = $"{badge.Tier} Practice Dummy";
            SendPracticeTargetSpawn(connection, spawn);
            state.FullNpcSent.Add(spawn.Target.WorldGuid);
        }
        state.Combat.TargetsArmed = true;
        return ConsoleReply.Did($"spawned {burst.Count} full-kit {badge.Tier} player dummy(s): helmet, laminated armour, military backpack, AR-15; kills award 1,000 points");
    }

    // --- players, debug, info -------------------------------------------------------------------

    private ConsoleReply ConsoleAnnounce(SoeConnection connection, string text)
    {
        int count = 0;
        foreach (var session in _throwableSessions.Values.Where(s => s.Connection.State == ConnectionState.Open))
        {
            SendTunnel(session.Connection, writer => GasAlerts.Write(writer, text));
            count++;
        }
        return ConsoleReply.Did($"announced to {count} player(s): {text}");
    }

    private ConsoleReply ConsoleDump(GatewaySessionState state, string what, int rows)
    {
        switch (what)
        {
            case "inv":
                return ConsoleInventory(state);

            case "self":
                return ConsoleReply.Plain(
                [
                    $"* self guid {state.Guid:x16} '{state.CharacterName}' gender {state.Gender}",
                    $"  head {state.Visuals?.HeadModel ?? "?"}  hair {state.Visuals?.HairModel ?? "?"}  "
                        + $"skin {state.Visuals?.SkinToneId ?? 0}",
                    $"  step {state.Match}  appearance-resync-sent {state.AppearanceReadySent}  "
                        + $"stances {state.WeaponStancesSent}",
                ]);

            default:
                if (state.Movement.Player is null)
                {
                    return ConsoleReply.Failed("no position stream yet");
                }

                List<string> lines =
                [
                    $"* movement: managed entities {state.Movement.ManagedEntityCount}, "
                        + $"positionless chute records {state.PositionlessChuteRecords}",
                ];

                if (state.Movement.Player.Position is Vector3 position)
                {
                    lines.Add($"  position {Coordinates(position)}");
                }

                lines.Add($"  clientTime {state.Movement.Player.ClientTime}  state 0x{state.Movement.Player.State:x2}"
                    + $"  posture {state.Movement.Player.Posture?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                lines.Add($"  horizontal {state.Movement.Player.HorizontalSpeed?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-"}"
                    + $"  vertical {state.Movement.Player.VerticalSpeed?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-"}");
                _ = rows;
                return ConsoleReply.Plain(lines);
        }
    }

    private static ConsoleReply ConsoleWatchdog(GatewaySessionState state) =>
        ConsoleReply.Plain(
        [
            $"* watchdog armed {state.Watchdog.ArmedCount}  stalled {state.Watchdog.IsStalled}  "
                + $"silent {state.Watchdog.SilentForMs} ms",
            $"  last sent: {(state.Watchdog.LastSent.Length == 0 ? "-" : state.Watchdog.LastSent)}",
        ]);

    private ConsoleReply ConsoleWireLog()
    {
        // The host's recorder knows its own file; a test recorder does not, and says so rather
        // than pretending.
        string? path = _recorder.GetType().GetProperty("FilePath")?.GetValue(_recorder) as string;
        return path is null
            ? ConsoleReply.Plain([$"* recorder {_recorder.GetType().Name} (no file)"])
            : ConsoleReply.Plain([$"* wire {path}"]);
    }

    private ConsoleReply ConsoleRaw(SoeConnection connection, byte[] bytes)
    {
        SendTunnel(connection, writer => writer.WriteRaw(bytes));
        _log.Warn($"{connection} console: /raw sent {bytes.Length} byte(s) {Convert.ToHexString(bytes)}");
        return ConsoleReply.Did($"sent {bytes.Length} byte(s): {Convert.ToHexString(bytes)}");
    }

    /// <summary>
    /// One <c>Ui.ExecuteScript</c> for <c>/win</c> (docs/103 §11, R6 §1.5): <c>1a 07</c>, the
    /// <c>Object.Method</c> as a String8, then the counted run of <c>u32</c> arguments.
    /// <para>
    /// <b>The reply is about the send, not the effect.</b> The client's invoker
    /// (<c>FUN_140ba89e0</c>) returns silently when the table or the method does not resolve, and
    /// <c>Ui.ExecuteScript</c> passes <c>nresults = 0</c>, so nothing comes back on any path. The
    /// host log carries the same line, because a probe judged on the screen still wants a
    /// timestamped record of what was on the wire when the screen changed.
    /// </para>
    /// </summary>
    private ConsoleReply ConsoleUiScript(
        SoeConnection connection,
        string script,
        IReadOnlyList<uint> ints)
    {
        uint[] arguments = [.. ints];
        int length = WindowScripts.ByteLength(script, arguments.Length);
        SendTunnel(connection, writer => new UiExecuteScript(script, arguments).WriteTo(writer));

        string tail = arguments.Length == 0 ? string.Empty : $" [{string.Join(' ', arguments)}]";
        _log.Info($"{connection} console: Ui.ExecuteScript \"{script}\"{tail} ({length} bytes) - "
            + "the client does not acknowledge it");
        return ConsoleReply.Did(
            $"sent Ui.ExecuteScript {script}{tail} ({length} bytes) - "
            + "the client does not acknowledge; watch the screen");
    }

    private IReadOnlyList<string> ConsoleInfo(SoeConnection connection, GatewaySessionState state)
    {
        ConsoleOptions options = _options.Console;
        return
        [
            $"* CRANBERRY {ConsoleBuildStamp}   client 1148 / 0.0.118.208059",
            $"  session {connection.RemoteEndPoint}  '{state.CharacterName}' {state.Guid:x16}",
            $"  tier {state.DevConsole.Tier}  step {state.Match}  "
                + $"surface {ConsoleOptions.SurfaceWord(state.DevConsole.SurfaceKind(options))}",
            $"  {ConsoleSummary}",
            $"  register {ConsoleOptions.RegisterWord(options.Register)}  bursts {state.DevConsole.BurstsSent}"
                + $"  self-flag {(options.SelfFlagOpensConsole ? "on" : "off")}",
            $"  refusal log {options.CollisionLogPath}",
        ];
    }

    /// <summary>The live right-hand note of a root menu row (design §2.6): the two that have one.</summary>
    private string? ConsoleRowNote(GatewaySessionState state, string nodeId) => nodeId switch
    {
        "match" => state.Gas is GasController gas && gas.Running
            ? $"{state.Match} {Clock(gas.MatchClockAt(Environment.TickCount64))}"
            : state.Match.ToString(),
        "players" => "1 online",
        _ => null,
    };

    private static string Coordinates(Vector3 position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.X:0.0} {position.Y:0.0} {position.Z:0.0}");

    private static string Clock(long milliseconds)
    {
        long seconds = Math.Max(0, milliseconds) / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
