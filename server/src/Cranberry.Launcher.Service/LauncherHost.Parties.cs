using Cranberry.Launcher.Core;
using Microsoft.AspNetCore.Http;

namespace Cranberry.Launcher.Service;

public sealed partial class LauncherHost
{
    private async Task<LauncherState> SocialState(HttpContext context)
    {
        string actor = await Actor(context);
        var presence = Volatile.Read(ref _presence);
        var state = await Locked(() => _social.State(actor, id => presence.GetValueOrDefault(id) ?? "Not running"));
        var game = await OnZone(_zone, () => _zone.LauncherSocial(actor), context.RequestAborted);
        if (game is null) return state;
        var invites = state.Invites.ToList();
        if (game.Invitation is { } invite) invites.Add(new(invite.Id, invite.FromId, invite.FromName, "GameParty"));
        var lobby = game.Id is null ? state.Lobby : new LobbyView(game.Id, game.LeaderId!, game.Members.Count > 2 ? "Fives" : "Duos",
            game.Members.Select(m => new LobbyMember(m.AccountId, m.Name, m.Status == "Menu", true, m.Status)).ToArray(), true);
        return state with { Invites = invites, Lobby = lobby };
    }

    private async Task<object> InviteParty(HttpContext context, TargetRequest request)
    {
        string actor = await Actor(context);
        var result = await OnZone(_zone, () =>
        {
            bool inGame = _zone.LauncherHasGame(actor) || _zone.LauncherHasGame(request.Target);
            return (InGame: inGame, Error: inGame ? _zone.LauncherInviteToGame(actor, request.Target) : null);
        }, context.RequestAborted);
        if (result.Error is not null) throw new InvalidOperationException(result.Error);
        if (!result.InGame) await Act(context, a => _social.Invite(a, request.Target));
        return new { Ok = true };
    }

    private async Task<object> RespondParty(HttpContext context, RespondRequest request)
    {
        if (!request.Id.StartsWith("game:", StringComparison.Ordinal)) return await Act(context, a => _social.Respond(a, request));
        string actor = await Actor(context);
        string? error = await OnZone(_zone, () => _zone.LauncherRespondToGame(actor, request.Id, request.Accept), context.RequestAborted);
        if (error is not null) throw new InvalidOperationException(error);
        return new { Ok = true };
    }
}
