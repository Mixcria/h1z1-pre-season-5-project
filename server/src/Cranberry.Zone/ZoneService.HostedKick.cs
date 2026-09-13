using Cranberry.Transport;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private ConsoleReply HostedKick(SoeConnection connection, GatewaySessionState actor, bool admin,
        uint worldId, ulong characterId)
    {
        if (_hostedGames.GetGame(worldId) is not { IsActive: true } game
            || !_hostedGames.CanManage(actor.AccountId, admin, worldId)
            || !IsHostedMember(connection, actor, game))
            return ConsoleReply.Refused("join the active hosted game you administer before removing players");
        var target = HostedMembers(game).FirstOrDefault(pair => pair.Value.Guid == characterId);
        if (target.Key is null || target.Value.BountyAdmission.MatchId != actor.BountyAdmission.MatchId)
            return ConsoleReply.Refused("select a player in your current hosted round");
        if (target.Value == actor) return ConsoleReply.Refused("select another player");
        if (!admin && (target.Value.AccountId == game.OwnerAccount
            || ResolveConsoleTier(target.Key, target.Value) >= ConsoleTier.Owner
            || actor.AccountId != game.OwnerAccount && _hostedGames.CanManage(target.Value.AccountId, false, worldId)))
            return ConsoleReply.Refused("only the game owner or a server administrator can remove another game administrator");

        string name = target.Value.CharacterName;
        SendPartyNotice(target.Key, $"You were removed from {game.Name} by a hosted game administrator.");
        // Use the normal departure cleanup for reservations, vehicles, observers and
        // shared-world ownership. Do not revoke unrelated invitations or ban accounts.
        AbandonMatch(target.Key, target.Value, "hosted administrator kick");
        target.Key.Disconnect();
        _log.Info($"hosted world {worldId}: {actor.Guid} removed {characterId} from the current round");
        return ConsoleReply.Did($"removed {name} from this game; revoke their invitation to prevent rejoining");
    }
}
