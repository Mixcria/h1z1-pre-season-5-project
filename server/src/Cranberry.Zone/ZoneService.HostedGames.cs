using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.HostedGames;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private readonly HostedGameStore _hostedGames;

    private static MatchMode HostedMode(uint mode) => mode switch
    {
        Login.GameWorldCatalog.SoloGameModeId => MatchMode.Solo,
        Login.GameWorldCatalog.DuosGameModeId => MatchMode.Duos,
        Login.GameWorldCatalog.FivesGameModeId => MatchMode.Fives,
        _ => MatchMode.Unknown,
    };

    private bool TryGetMatchDefinition(uint worldId, [NotNullWhen(true)] out MatchAdmissionDefinition? definition)
    {
        if (!_options.MatchAdmissions.TryGetDefinition(worldId, out definition)) return false;
        if (definition.QueueKind == MatchQueueKind.Hosted)
        {
            if (_hostedGames.GetGame(worldId) is not { IsActive: true } game) return false;
            definition = definition with { GameModeId = game.GameModeId, Mode = HostedMode(game.GameModeId) };
        }
        return true;
    }

    private MatchAdmissionContext ResolveMatchAdmission(GatewaySessionState state, PlayerWorldTransferRequest request, ulong matchId)
    {
        if (!_options.MatchAdmissions.TryGetDefinition(request.WorldId, out var definition))
            return MatchAdmissionContext.Unknown;
        if (definition.QueueKind != MatchQueueKind.Hosted)
            return _options.MatchAdmissions.Resolve(request, matchId);
        // Redeem through the authenticated command channel. Untrusted EC text and role never grant rights.
        if (matchId == 0 || request.Role != PlayerWorldTransferRequest.PlayerRole
            || request.AdmissionText.Length != 0 || request.UnknownValue != 0 || request.Flag != 1
            || !_hostedGames.CanEnter(state.AccountId, request.WorldId)
            || _hostedGames.GetGame(request.WorldId) is not { IsActive: true } game
            || HostedWorldRunning(request.WorldId) || !HostedHasRoom(request.WorldId, [state])
            || HostedQueueClosed(game)) return MatchAdmissionContext.Unknown;
        return new(matchId, MatchQueueKind.Hosted, HostedMode(game.GameModeId));
    }

    private bool HostedWorldRunning(uint worldId) => _accountSessions.Any(pair =>
        pair.Key.State == ConnectionState.Open && pair.Value.BountyWorldId == worldId
        && pair.Value.HostedGameId is not null && pair.Value.Match is MatchStep.Dropping or MatchStep.InMatch);

    private void RefuseHostedAdmission(SoeConnection connection, uint worldId)
    {
        SendPartyNotice(connection, !HostedHasRoom(worldId) && _hostedGames.GetGame(worldId) is { IsActive: true }
            ? "This hosted game is full. Wait for a space to open."
            : "This game is unavailable or your access has expired. Use /hostgame list or /hostgame redeem <key>.");
        SendTunnel(connection, new PlayerWorldTransferReply(worldId, Result: 1).WriteTo);
    }

    private bool ValidateHostedAdmission(SoeConnection connection, GatewaySessionState state)
    {
        if (state.HostedGameId is null) return true;
        var game = _hostedGames.GetGame(state.BountyWorldId);
        if (game is { IsActive: true } && game.Id == state.HostedGameId
            && HostedMode(game.GameModeId) == state.BountyAdmission.Mode
            && _hostedGames.CanEnter(state.AccountId, state.BountyWorldId)
            && (!state.HostedObserverActive || _hostedGames.CanManage(state.AccountId,
                ResolveConsoleTier(connection, state) >= ConsoleTier.Owner, state.BountyWorldId))) return true;
        bool inWorld = state.Match is MatchStep.Transferring or MatchStep.Zoning or MatchStep.Lobby
            or MatchStep.Dropping or MatchStep.InMatch or MatchStep.Ended;
        SendPartyNotice(connection, "Hosted game access expired or was revoked.");
        if (state.Match == MatchStep.Queued)
            SendTunnel(connection, new QueueExit(0, state.Guid, Cancel: true).WriteTo);
        AbandonMatch(connection, state, "hosted access ended");
        // The current menu-return ticket is a development shortcut. Disconnect an in-world
        // account cleanly rather than issue it a synthetic unauthenticated resume ticket.
        if (inWorld) connection.Disconnect();
        return false;
    }

    private void ArmHostedAccess(SoeConnection connection, GatewaySessionState state)
    {
        if (state.HostedGameId is null || state.HostedAccessPumping) return;
        state.HostedAccessPumping = Later(connection, 1000, () =>
        {
            state.HostedAccessPumping = false;
            if (ValidateHostedAdmission(connection, state))
            {
                SendHostedAdminWorld(connection, state);
                SendHostedSpectatorRoster(connection, state);
                RefreshHostedObserverTarget(connection, state);
                ArmHostedAccess(connection, state);
            }
        });
    }

    private void RefreshHostedAccess()
    {
        foreach (var pair in _accountSessions.ToArray())
        {
            if (pair.Key.State != ConnectionState.Open) continue;
            if (!ValidateHostedAdmission(pair.Key, pair.Value)) continue;
            SendHostedAdminWorld(pair.Key, pair.Value);
            if (pair.Value.Match == MatchStep.Menu) SendHostedSchedule(pair.Key, pair.Value);
        }
    }

    private void SendHostedSchedule(SoeConnection connection, GatewaySessionState state)
    {
        var games = _hostedGames.ListGames(state.AccountId).Where(game => game.IsActive).ToArray();
        string worldLabel = state.Match is MatchStep.Menu or MatchStep.Queued ? string.Empty
            : _hostedGames.GetGame(state.BountyWorldId)?.Name ?? string.Empty;
        var labels = WorldDisplayLabel.Values(worldLabel).ToList();
        foreach (var game in games)
        {
            labels.Add(new($"Cranberry.Hosted.{game.WorldId}.Name", game.Name));
            labels.Add(new($"Cranberry.Hosted.{game.WorldId}.Mode", HostedMode(game.GameModeId).ToString()));
        }
        SendTunnel(connection, new StringHashToValueManager(labels).WriteTo);
        // The complete table replaces the client's map, including prior authority deltas.
        RestoreHostedAdminWorldAfterTable(connection, state);
        state.InventoryActionUiState = null;
        SendInventoryActionState(connection, state);
        PublishHealingHud(connection, state);
        SendTunnel(connection, new MatchScheduleReply(games.Select(game => new HostedGameScheduleEntry(game.WorldId, game.GameModeId)
        {
            CanEnter = _hostedGames.CanEnter(state.AccountId, game.WorldId) && !HostedWorldRunning(game.WorldId)
                && HostedHasRoom(game.WorldId, [state]) && !HostedQueueClosed(game),
            UnlockTime = (ulong)game.CreatedAt.ToUnixTimeSeconds(),
            StartTime = (ulong)game.CreatedAt.ToUnixTimeSeconds(),
        }).ToArray()).WriteTo);
    }

    private ConsoleReply ConsoleHostedGame(SoeConnection connection, GatewaySessionState state, CommandCall call)
    {
        var args = call.Line.RawTokens;
        string verb = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
        string actor = state.AccountId;
        bool admin = call.Tier >= ConsoleTier.Owner;
        if (!state.Authenticated || state.LogoutPrepared || state.LogoutCompleted || string.IsNullOrWhiteSpace(actor))
            return ConsoleReply.Refused("an authenticated active account is required");
        HostedGameResult result;
        switch (verb)
        {
            case "panel":
                return ConsoleHostedPanel(connection, state, call);
            case "roster":
                if (args.Count != 2 || !uint.TryParse(args[1], out uint rosterWorld))
                    return ConsoleReply.Usage("/hostgame roster <worldId>");
                return HostedRoster(state, admin, rosterWorld);
            case "announce":
                if (args.Count < 3 || !uint.TryParse(args[1], out uint announcementWorld))
                    return ConsoleReply.Usage("/hostgame announce <worldId> <message>");
                return HostedAnnounce(state, admin, announcementWorld, string.Join(" ", args.Skip(2)));
            case "bring":
            case "goto":
                if (args.Count != 3 || !uint.TryParse(args[1], out uint teleportWorld)
                    || !TryHostedCharacterId(args[2], out ulong targetCharacter))
                    return ConsoleReply.Usage("/hostgame bring|goto <worldId> <characterId>; /hostgame roster <worldId>");
                return HostedTeleport(connection, state, admin, teleportWorld, targetCharacter, verb == "bring");
            case "kick":
                if (args.Count != 3 || !uint.TryParse(args[1], out uint kickWorld)
                    || !TryHostedCharacterId(args[2], out ulong kickTarget))
                    return ConsoleReply.Usage("/hostgame kick <worldId> <characterId>");
                return HostedKick(connection, state, admin, kickWorld, kickTarget);
            case "spectate":
                if (args.Count != 3 || !uint.TryParse(args[1], out uint spectateWorld)
                    || !TryHostedCharacterId(args[2], out ulong spectateTarget))
                    return ConsoleReply.Usage("/hostgame spectate <worldId> <characterId>");
                return HostedObserve(connection, state, admin, spectateWorld, spectateTarget, freeCamera: false);
            case "fly":
                if (args.Count is < 2 or > 3 || !uint.TryParse(args[1], out uint flyWorld)
                    || (args.Count == 3 && args[2].ToLowerInvariant() is not ("on" or "off")))
                    return ConsoleReply.Usage("/hostgame fly <worldId> [on|off]");
                bool freeCamera = args.Count == 3 ? args[2].Equals("on", StringComparison.OrdinalIgnoreCase)
                    : !state.HostedFreeCamera;
                return HostedObserve(connection, state, admin, flyWorld, 0, freeCamera);
            case "key":
                if (!admin) return ConsoleReply.Refused("Owner or admin permission is required to generate host keys");
                if (args.Count is < 3 or > 4 || !TryHostedDuration(args[2], out var hostLifetime))
                    return ConsoleReply.Usage("/hostgame key <region> <permanent|30m|2h|7d> [accountId]");
                result = _hostedGames.IssueHostKey(actor, admin, args[1], hostLifetime, args.Count == 4 ? args[3] : null);
                break;
            case "redeem":
                if (args.Count != 2) return ConsoleReply.Usage("/hostgame redeem <key>");
                result = _hostedGames.Redeem(actor, args[1]);
                break;
            case "redeemui":
                return RedeemHostedUi(connection, state, args);
            case "create":
                if (args.Count < 4 || !TryHostedMode(args[2], out uint mode))
                    return ConsoleReply.Usage("/hostgame create <region> <solo|duos|fives> <name>");
                result = _hostedGames.CreateGame(actor, args[1], mode, string.Join(" ", args.Skip(3)));
                if (result.Success && result.Game is { } created) _formingBountyMatches.Remove(created.WorldId);
                break;
            case "createform":
            case "createui":
                if (verb == "createui" && (args.Count < 2 || !ValidHostedUiNonce(args[1])))
                    return ConsoleReply.Usage("/hostgame createui <nonce:hex> <region> <mode> <queueMinutes> <maxPlayers> <name>");
                var createArgs = verb == "createui" ? args.Skip(1).ToArray() : args.ToArray();
                if (createArgs.Length < 6 || !TryHostedMode(createArgs[2], out uint formMode)
                    || !int.TryParse(createArgs[3], NumberStyles.None, CultureInfo.InvariantCulture, out int queueMinutes)
                    || queueMinutes is < 1 or > 60
                    || !int.TryParse(createArgs[4], NumberStyles.None, CultureInfo.InvariantCulture, out int maxPlayers)
                    || maxPlayers is < 1 or > 150)
                    return ConsoleReply.Usage("/hostgame createform <region> <solo|duos|fives> <queueMinutes:1-60> <maxPlayers:1-150> <name>");
                result = _hostedGames.CreateGame(actor, createArgs[1], formMode, string.Join(" ", createArgs.Skip(5)), queueMinutes, maxPlayers);
                if (result.Success && result.Game is { } formCreated) _formingBountyMatches.Remove(formCreated.WorldId);
                break;
            case "invite":
            case "admininvite":
                if (args.Count is < 3 or > 4 || !uint.TryParse(args[1], out uint inviteWorld)
                    || !TryHostedDuration(args[2], out var lifetime))
                    return ConsoleReply.Usage($"/hostgame {verb} <worldId> <permanent|30m|2h|7d> [accountId]");
                result = _hostedGames.IssuePlayerKey(actor, admin, inviteWorld, lifetime, args.Count == 4 ? args[3] : null,
                    moderator: verb == "admininvite");
                break;
            case "revoke":
                if (args.Count != 2) return ConsoleReply.Usage("/hostgame revoke <keyId> (see /hostgame keys)");
                result = _hostedGames.RevokeKey(actor, admin, args[1]);
                break;
            case "close":
                if (args.Count != 2 || !uint.TryParse(args[1], out uint closeWorld))
                    return ConsoleReply.Usage("/hostgame close <worldId>");
                result = _hostedGames.CloseGame(actor, admin, closeWorld);
                if (result.Success) _formingBountyMatches.Remove(closeWorld);
                break;
            case "mode":
                if (args.Count != 3 || !uint.TryParse(args[1], out uint modeWorld) || !TryHostedMode(args[2], out uint newMode))
                    return ConsoleReply.Usage("/hostgame mode <worldId> <solo|duos|fives>");
                if (!_hostedGames.CanManage(actor, admin, modeWorld)) return ConsoleReply.Refused("this is not your hosted game");
                if (_accountSessions.Any(pair => pair.Key.State == ConnectionState.Open && pair.Value.BountyWorldId == modeWorld
                    && pair.Value.HostedGameId is not null && pair.Value.Match != MatchStep.Menu))
                    return ConsoleReply.Refused("everyone must leave this game before changing its mode");
                result = _hostedGames.SetMode(actor, admin, modeWorld, newMode);
                if (result.Success) _formingBountyMatches.Remove(modeWorld);
                break;
            case "join":
                if (args.Count != 2 || !uint.TryParse(args[1], out uint joinWorld))
                    return ConsoleReply.Usage("/hostgame join <worldId>");
                if (state.Match != MatchStep.Menu) return ConsoleReply.Refused("join from the main menu");
                if (!_hostedGames.CanEnter(actor, joinWorld)) return ConsoleReply.Refused("redeem a valid invitation for this game first");
                if (!HostedHasRoom(joinWorld, [state])) return ConsoleReply.Refused("this hosted game is full; wait for a space to open");
                var request = new PlayerWorldTransferRequest(joinWorld, string.Empty, 0, 1, 1);
                if (TryQueuePartyMatch(connection, state, request)) return ConsoleReply.Plain(["* Hosted party queue request processed."]);
                if (!PrepareMatchAdmission(state, request)) return ConsoleReply.Refused("this hosted game is unavailable or already running");
                state.Match = MatchStep.Queued;
                RunQueue(connection, state);
                return ConsoleReply.Did($"joining hosted world {joinWorld}");
            case "start":
                if (args.Count != 2 || !uint.TryParse(args[1], out uint startWorld))
                    return ConsoleReply.Usage("/hostgame start <worldId>");
                return StartHostedGame(actor, admin, startWorld);
            case "list":
                if (args.Count > 1) return ConsoleReply.Usage("/hostgame list");
                var games = _hostedGames.ListGames(actor, admin).Where(game => game.IsActive).ToArray();
                return ConsoleReply.Plain(new[] { $"* Account: {actor}. {games.Length} hosted game(s); /hostgame join <worldId>.",
                    "  /help hostgame explains host keys, redeeming, invitations and game controls." }
                    .Concat(games.Select(game => $"  {game.WorldId}: {game.Name} [{game.Region} {HostedMode(game.GameModeId)}] owner={game.OwnerAccount}")));
            case "keys":
                if (args.Count > 2 || (args.Count == 2 && !uint.TryParse(args[1], out _)))
                    return ConsoleReply.Usage("/hostgame keys [worldId]");
                uint? filter = args.Count == 2 ? uint.Parse(args[1], CultureInfo.InvariantCulture) : null;
                var keys = _hostedGames.ListKeys(actor, admin).Where(key => filter is null || key.WorldId == filter).ToArray();
                return ConsoleReply.Plain(new[] { $"* {keys.Length} key(s). IDs below revoke access; secret keys are shown only on creation." }
                    .Concat(keys.Select(key => $"  {key.Id}: {key.Kind} {key.Region} world={key.WorldId?.ToString() ?? "-"} "
                        + $"account={key.Account ?? key.TargetAccount ?? "unclaimed"} {(key.IsActive ? "active" : "inactive")} "
                        + $"expires={key.ExpiresAt?.ToString("u") ?? "never"}")));
            default:
                return ConsoleReply.Usage("/hostgame panel|key|redeem|create|invite|admininvite|keys|revoke|list|join|mode|start|close|roster|announce|bring|goto|kick|spectate|fly; /help hostgame");
        }
        if (!result.Success) return ConsoleReply.Failed(result.Message);
        RefreshHostedAccess();
        var lines = new List<string> { "+ " + result.Message };
        if (result.Game is { } gameInfo) lines.Add($"  World {gameInfo.WorldId}: {gameInfo.Name} ({gameInfo.Region}, {HostedMode(gameInfo.GameModeId)}), "
            + $"maximum {gameInfo.MaxPlayers} players; " + (gameInfo.QueueDurationMinutes > 0
                ? $"queue {gameInfo.QueueDurationMinutes} minute(s) after the host loads into the lobby" : "manual start"));
        if (result.Key is { } keyInfo) lines.Add($"  Key ID: {keyInfo.Id}; expires {keyInfo.ExpiresAt?.ToString("u") ?? "never"}");
        if (result.Secret is { } secret) lines.Add($"  Key (save now): {secret}");
        return ConsoleReply.Plain(lines);
    }

    private ConsoleReply StartHostedGame(string actor, bool admin, uint worldId)
    {
        if (!_hostedGames.CanManage(actor, admin, worldId)) return ConsoleReply.Refused("this is not your active hosted game");
        var members = _accountSessions.Where(pair => pair.Key.State == ConnectionState.Open
            && pair.Value.BountyWorldId == worldId && pair.Value.HostedGameId is not null
            && pair.Value.Match != MatchStep.Menu).ToArray();
        if (members.Length == 0) return ConsoleReply.Failed("join the hosted game before starting it");
        if (members.Any(pair => !ValidateHostedAdmission(pair.Key, pair.Value))) return ConsoleReply.Refused("player access changed; check your roster");
        if (members.Any(pair => pair.Value.Match != MatchStep.Lobby || !pair.Value.PregameClientReady || pair.Value.Watchdog.IsStalled))
            return ConsoleReply.Refused("every player must finish loading into the lobby before starting");
        foreach (var pair in members) BeginMatchDrop(pair.Key, pair.Value);
        RefreshHostedAccess();
        return ConsoleReply.Did($"started hosted world {worldId} with {members.Length} player(s)");
    }

    private static bool TryHostedMode(string word, out uint mode)
    {
        mode = word.ToLowerInvariant() switch
        {
            "solo" => Login.GameWorldCatalog.SoloGameModeId,
            "duos" => Login.GameWorldCatalog.DuosGameModeId,
            "fives" => Login.GameWorldCatalog.FivesGameModeId,
            _ => 0,
        };
        return mode != 0;
    }

    private static bool TryHostedDuration(string text, out TimeSpan? duration)
    {
        duration = null;
        if (text.Equals("permanent", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Length < 2 || !int.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value <= 0) return false;
        double seconds = char.ToLowerInvariant(text[^1]) switch { 's' => value, 'm' => value * 60d, 'h' => value * 3600d, 'd' => value * 86400d, _ => 0 };
        if (seconds <= 0 || seconds > TimeSpan.FromDays(3650).TotalSeconds) return false;
        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
