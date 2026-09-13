using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Cranberry.Launcher.Core;
using Cranberry.Zone.Match;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cranberry.Launcher.Service;

public sealed partial class LauncherHost
{
    private readonly Channel<SocialOverlayRequest> _overlayRequests = Channel.CreateBounded<SocialOverlayRequest>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private Task? _overlayPump;
    private static string Field(string value) => Uri.EscapeDataString(value);

    private void MapSocialOverlay()
    {
        _zone.SocialOverlaySubmit = r => _overlayRequests.Writer.TryWrite(r);
        _app.MapPost("/api/profile/avatar", async (HttpContext c, AvatarRequest r) =>
            await Locked(() => _social.SetAvatar(_social.Authenticate(Token(c)), r.Pixels)));
        _app.MapGet("/api/profile/{target}/avatar", async (HttpContext c, string target) =>
            await Locked(() => _social.Avatar(_social.Authenticate(Token(c)), target)));
        _app.MapGet("/api/messages/unread", async (HttpContext c) =>
        {
            return await Locked(() => _social.Unread(_social.Authenticate(Token(c))));
        });
        _app.MapGet("/api/messages/{target}", async (HttpContext c, string target) =>
            await Locked(() => _social.History(_social.Authenticate(Token(c)), target)));
        _app.MapPost("/api/messages", async (HttpContext c, MessageRequest r) =>
            await Locked(() => _social.SendMessage(_social.Authenticate(Token(c)), r)));
        _app.MapPost("/api/messages/read", async (HttpContext c, ReadMessagesRequest r) =>
            await Act(c, actor => _social.MarkRead(actor, r.Target, r.ThroughId)));
        _app.MapPost("/api/overlay/toggle", async (HttpContext c, TargetRequest r) =>
        {
            string actor = await Actor(c);
            string? error = await OnZone(_zone, () => _zone.ToggleSocialOverlay(actor, r.Target), c.RequestAborted);
            if (error is not null) throw new InvalidOperationException(error);
            return new { Ok = true };
        });
    }

    private string OverlayState(string actor)
    {
        var presence = Volatile.Read(ref _presence);
        var state = _social.State(actor, id => presence.GetValueOrDefault(id) ?? "Not running");
        var unread = _social.Unread(actor).ToDictionary(r => r.AccountId, r => r.Count);
        var result = new StringBuilder("S|" + Field(actor) + "|" + Field(state.Me.Name) + "|" + state.Me.AvatarVersion);
        foreach (var friend in state.Friends)
            result.Append(";F|").Append(Field(friend.AccountId)).Append('|').Append(Field(friend.Name)).Append('|')
                .Append(Field(friend.Online || friend.GameStatus != "Not running" ? friend.GameStatus : "Offline"))
                .Append('|').Append(friend.AvatarVersion).Append('|').Append(unread.GetValueOrDefault(friend.AccountId));
        return result.ToString();
    }

    private string OverlayHistory(string actor, string target)
    {
        var history = _social.History(actor, target);
        var result = new StringBuilder("H|" + Field(target) + "|" + history.ReadThrough);
        // Bound each console envelope independently of the retained history size.
        foreach (var m in history.Messages.TakeLast(40))
            result.Append(";M|").Append(m.Id).Append('|').Append(Field(m.From)).Append('|')
                .Append(m.SentAt.ToUnixTimeSeconds()).Append('|').Append(Field(m.Text)).Append('|').Append(m.ClientId);
        return result.ToString();
    }

    private string ExecuteOverlay(SocialOverlayRequest request)
    {
        switch (request.Action)
        {
            case "state": return OverlayState(request.Actor);
            case "history": return OverlayHistory(request.Actor, request.Target);
            case "send":
                var message = _social.SendMessage(request.Actor, new(request.Target, request.Text, request.ClientId));
                return "D|" + request.ClientId + "|" + message.Id;
            case "read":
                if (!long.TryParse(request.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long through))
                    throw new InvalidOperationException("Invalid read position.");
                _social.MarkRead(request.Actor, request.Target, through);
                return "R|" + Field(request.Target);
            default: throw new InvalidOperationException("Unknown friends action.");
        }
    }

    private async Task ProcessOverlay()
    {
        try
        {
            await foreach (var request in _overlayRequests.Reader.ReadAllAsync(_stop.Token))
            {
                string reply;
                try { reply = await Locked(() => ExecuteOverlay(request)); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                { reply = "E|" + Field(ex.Message); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { reply = "E|" + Field("Your message could not be saved. Please try again."); }
                try { await OnZone(_zone, () => { request.Reply(reply); return true; }, _stop.Token); }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
}
