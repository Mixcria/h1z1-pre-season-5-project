using System.Globalization;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Gas;
using Cranberry.Zone.HostedGames;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Hosted authority is account-bound and never promotes the global developer console tier.
    // Always use the current game incarnation as well as its world slot: closed slots are reusable.
    private bool IsHostedMember(SoeConnection connection, GatewaySessionState state, HostedGameInfo game) =>
        connection.State == ConnectionState.Open && state.Authenticated && !state.LogoutCompleted
        && state.HostedGameId == game.Id && state.BountyWorldId == game.WorldId
        && state.BountyAdmission is { MatchId: > 0, QueueKind: MatchQueueKind.Hosted }
        && state.Match != MatchStep.Menu
        && _hostedGames.CanEnter(state.AccountId, game.WorldId);

    private KeyValuePair<SoeConnection, GatewaySessionState>[] HostedMembers(HostedGameInfo game) =>
        _accountSessions.Where(pair => IsHostedMember(pair.Key, pair.Value, game)).ToArray();

    private ConsoleReply HostedRoster(GatewaySessionState actor, bool admin, uint worldId)
    {
        if (_hostedGames.GetGame(worldId) is not { IsActive: true } game
            || (!_hostedGames.CanManage(actor.AccountId, admin, worldId)
                && !_hostedGames.CanEnter(actor.AccountId, worldId)))
            return ConsoleReply.Refused("this hosted game is unavailable to your account");
        var members = HostedMembers(game);
        return ConsoleReply.Plain(new[] { $"* {game.Name}: {members.Length} player(s). Use character IDs for hosted controls." }
            .Concat(members.OrderBy(pair => pair.Value.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Value.Guid).Select(pair =>
                    $"  {pair.Value.Guid}: {pair.Value.CharacterName} [{pair.Value.Match}, "
                    + $"{(pair.Value.DeathSent ? "eliminated" : "alive")}]"
                    + (_hostedGames.CanManage(pair.Value.AccountId, false, worldId) ? " host admin" : ""))));
    }

    private ConsoleReply HostedAnnounce(GatewaySessionState actor, bool admin, uint worldId, string text)
    {
        if (_hostedGames.GetGame(worldId) is not { IsActive: true } game
            || !_hostedGames.CanManage(actor.AccountId, admin, worldId))
            return ConsoleReply.Refused("this is not your active hosted game");
        text = text.Trim();
        if (text.Length is < 1 or > 240 || text.Any(char.IsControl))
            return ConsoleReply.Usage("announcements must contain 1 to 240 characters on one line");
        var members = HostedMembers(game);
        if (members.Length == 0) return ConsoleReply.Failed("there are no players in this hosted game");
        foreach (var member in members)
            SendTunnel(member.Key, writer => GasAlerts.Write(writer, $"[{game.Name}] {text}"));
        _log.Info($"hosted world {worldId}: {actor.Guid} announced to {members.Length} member(s)");
        return ConsoleReply.Did($"announced to {members.Length} player(s)");
    }

    private ConsoleReply HostedTeleport(SoeConnection connection, GatewaySessionState actor, bool admin,
        uint worldId, ulong characterId, bool bring)
    {
        if (_hostedGames.GetGame(worldId) is not { IsActive: true } game
            || !_hostedGames.CanManage(actor.AccountId, admin, worldId))
            return ConsoleReply.Refused("this is not your active hosted game");
        if (!IsHostedMember(connection, actor, game))
            return ConsoleReply.Refused("join this hosted game before moving its players");
        var target = HostedMembers(game).FirstOrDefault(pair => pair.Value.Guid == characterId);
        if (target.Key is null || target.Value.BountyAdmission.MatchId != actor.BountyAdmission.MatchId)
            return ConsoleReply.Refused("select a player in your current hosted round");
        if (target.Value == actor) return ConsoleReply.Refused("select another player");
        if (!CanHostedTeleport(actor) || !CanHostedTeleport(target.Value))
            return ConsoleReply.Refused("both players must be alive in a loaded lobby or match; use spectate after elimination");
        var destination = bring ? actor : target.Value;
        var moved = bring ? target.Value : actor;
        var movedConnection = bring ? target.Key : connection;
        if (destination.Movement.Player?.Position is not Vector3 position)
            return ConsoleReply.Failed("destination has no player position yet");
        if (moved.Movement.Player?.Position is null)
            return ConsoleReply.Failed("the selected player has no position yet");
        // Exit the real vehicle first so its managed movement cannot immediately undo the teleport.
        TryExitVehicle(movedConnection, moved, "hosted teleport");
        if (moved.Fleet?.TryGetForOccupant(moved.Guid, out _) == true)
            return ConsoleReply.Refused("the player could not leave their vehicle");
        var reply = ConsoleTeleport(movedConnection, moved, position + new Vector3(2, 0, 0));
        if (!reply.Ok) return reply;
        SendPartyNotice(movedConnection, $"Hosted admin {actor.CharacterName} moved you to {destination.CharacterName}.");
        return ConsoleReply.Did($"moved {moved.CharacterName} to {destination.CharacterName}");
    }

    private static bool CanHostedTeleport(GatewaySessionState state) =>
        state.Match is MatchStep.Lobby or MatchStep.InMatch && !state.DeathSent && state.Hitpoints > 0
        && !state.MountRequested && !state.VictorySent && !state.LogoutPrepared && !state.Watchdog.IsStalled
        && (state.Match != MatchStep.Lobby || state.PregameClientReady);

    private bool CanHostedObserve(SoeConnection connection, GatewaySessionState state, bool admin, uint worldId) =>
        _hostedGames.GetGame(worldId) is { IsActive: true } game && IsHostedMember(connection, state, game)
        && _hostedGames.CanManage(state.AccountId, admin, worldId)
        && state.DeathSent && !state.VictorySent && !state.LogoutPrepared
        && state.Match is MatchStep.InMatch or MatchStep.Ended;

    private ConsoleReply HostedObserve(SoeConnection connection, GatewaySessionState state, bool admin,
        uint worldId, ulong targetGuid, bool freeCamera)
    {
        if (!CanHostedObserve(connection, state, admin, worldId))
            return ConsoleReply.Refused("hosted spectating is available to this game's admins after elimination");
        var game = _hostedGames.GetGame(worldId)!;
        var targets = HostedMembers(game).Where(pair => pair.Value != state
            && pair.Value.BountyAdmission.MatchId == state.BountyAdmission.MatchId
            && pair.Value.Match == MatchStep.InMatch && !pair.Value.DeathSent
            && pair.Value.Hitpoints > 0 && !pair.Value.VictorySent).ToArray();
        if (!freeCamera && targetGuid == 0)
            targetGuid = targets.FirstOrDefault(pair => pair.Value.Guid == state.HostedSpectateTarget).Value?.Guid
                ?? targets.FirstOrDefault().Value?.Guid ?? 0;
        var target = targets.FirstOrDefault(pair => pair.Value.Guid == targetGuid);
        if (!freeCamera && target.Key is null)
            return ConsoleReply.Refused("select a living player in this round to spectate; use Main Menu to leave");
        Vector3? position = freeCamera ? state.HostedCameraPosition ?? state.Movement.Player?.Position
            : WorldStreamPosition(target.Value);
        if (position is null) return ConsoleReply.Failed("the camera destination has no position yet");
        if (!state.HostedObserverActive)
        {
            // August FUN_140af3950 case E2/1: controller 0x2a, spectator flag, Enabled=1.
            // It is an enable operation, never a toggle or a grant of global admin privileges.
            SendTunnel(connection, writer => { writer.WriteByte(ZoneOpcodes.SpectatorBase); writer.WriteUInt16(1); });
            state.HostedObserverActive = true;
        }
        state.HostedFreeCamera = freeCamera;
        if (!freeCamera) state.HostedSpectateTarget = targetGuid;
        state.HostedObserverSequence++;
        SendHostedSpectatorRoster(connection, state, force: true);
        SetHostedCameraPosition(connection, state, position.Value, teleport: true);
        if (!state.WorldPumping && WorldPumpIntervalMs > 0)
        {
            int generation = state.WorldGeneration;
            state.WorldPumping = Later(connection, WorldPumpIntervalMs, () => PumpWorld(connection, state, generation));
        }
        return ConsoleReply.Did(freeCamera ? "free camera enabled; select a player to follow them"
            : $"spectating {target.Value.CharacterName}");
    }

    private void SetHostedCameraPosition(SoeConnection connection, GatewaySessionState state, Vector3 position, bool teleport)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return;
        state.HostedCameraPosition = position;
        if (state.Peer is { } peer)
        {
            peer.ObserverPosition = position;
            if (teleport) state.NextPeerInterestMs = 0;
            RunPeerInterest(connection, state, peer);
        }
        if (teleport)
        {
            // Native GotoPlayer enters Awaiting Teleport. Move the client's observer origin through
            // the existing location response; the server corpse/interaction pose remains untouched.
            SendTunnel(connection, new UpdateLocation(new Vector4(position, 1), new Vector4(0, 0, 0, 1), Apply: true).WriteTo);
        }
    }

    private void RefreshHostedObserverTarget(SoeConnection connection, GatewaySessionState state)
    {
        if (!state.HostedObserverActive || state.HostedFreeCamera
            || _hostedGames.GetGame(state.BountyWorldId) is not { IsActive: true } game) return;
        var target = HostedMembers(game).FirstOrDefault(pair => pair.Value.Guid == state.HostedSpectateTarget
            && pair.Value.BountyAdmission.MatchId == state.BountyAdmission.MatchId
            && pair.Value.Match == MatchStep.InMatch && !pair.Value.DeathSent
            && pair.Value.Hitpoints > 0 && !pair.Value.VictorySent);
        if (target.Key is not null && WorldStreamPosition(target.Value) is { } position)
            SetHostedCameraPosition(connection, state, position, teleport: false);
        else
        {
            // A disconnected/eliminated target cannot strand the camera or become another match's
            // reused character. The panel applies the native freeflight transition once.
            state.HostedFreeCamera = true;
            state.HostedObserverSequence++;
            SendHostedPanel(connection, state, state.BountyWorldId);
        }
    }

    private void HandleHostedSpectatorRequest(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        if (!state.HostedObserverActive || !CanHostedObserve(connection, state,
                ResolveConsoleTier(connection, state) >= ConsoleTier.Owner, state.BountyWorldId)) return;
        var reader = new PacketReader(payload);
        if (reader.ReadByte() != ZoneOpcodes.SpectatorBase) return;
        ushort action = reader.ReadUInt16();
        if (action == 3)
        {
            ulong guid = reader.ReadUInt64();
            if (!reader.AtEnd) return;
            var game = _hostedGames.GetGame(state.BountyWorldId)!;
            var target = HostedMembers(game).FirstOrDefault(pair => pair.Value.Guid == guid && pair.Value != state
                && pair.Value.BountyAdmission.MatchId == state.BountyAdmission.MatchId
                && pair.Value.Match == MatchStep.InMatch && !pair.Value.DeathSent
                && pair.Value.Hitpoints > 0 && !pair.Value.VictorySent);
            if (target.Key is null || WorldStreamPosition(target.Value) is not Vector3 position) return;
            state.HostedSpectateTarget = guid;
            state.HostedFreeCamera = false;
            SetHostedCameraPosition(connection, state, position, teleport: true);
        }
        else if (action == 4)
        {
            float x = reader.ReadSingle();
            float z = reader.ReadSingle();
            if (!reader.AtEnd || !float.IsFinite(x) || !float.IsFinite(z)
                || Math.Abs(x) > 10_000 || Math.Abs(z) > 10_000) return;
            state.HostedFreeCamera = true;
            float y = state.HostedCameraPosition?.Y ?? state.Movement.Player?.Position?.Y ?? 500;
            SetHostedCameraPosition(connection, state, new Vector3(x, y, z), teleport: true);
        }
    }

    private static bool TryHostedCharacterId(string text, out ulong characterId) =>
        (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out characterId)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out characterId)) && characterId != 0;
}
