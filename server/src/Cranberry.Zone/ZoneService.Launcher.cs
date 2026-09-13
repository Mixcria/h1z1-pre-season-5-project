using Cranberry.Transport;
using Cranberry.Zone.Match;

namespace Cranberry.Zone;

public sealed record LauncherGamePresence(string AccountId, string CharacterName, string Status);

public sealed partial class ZoneService
{
    /// <summary>When configured, a loopback relay never grants owner powers to another account.</summary>
    public string? LocalOwnerAccountId { get; set; }
    public Func<string, string>? CharacterSelectTicketFactory { get; set; }
    /// <summary>Production only: the launcher's verified native door helper gates match admission.</summary>
    public Func<string, bool>? DoorSwingClientReady { get; set; }

    // These entry points are called only by the authenticated launcher service on the listener thread.
    public IReadOnlyList<LauncherGamePresence> LauncherPresence() => _accountSessions
        .Where(pair => pair.Key.State == ConnectionState.Open && pair.Value.Authenticated)
        .Select(pair => new LauncherGamePresence(pair.Value.AccountId, pair.Value.CharacterName,
            pair.Value.Match == MatchStep.Menu && !pair.Value.AppearanceReadySent ? "Loading" : pair.Value.Match.ToString())).ToArray();

    public string? LauncherQueue(IReadOnlyList<string> accounts, string mode)
    {
        uint world = mode switch { "Solo" => 1, "Duos" => 6, "Fives" => 7, _ => 0 };
        int capacity = mode switch { "Solo" => 1, "Duos" => 2, "Fives" => 5, _ => 0 };
        if (accounts.Count < 1 || accounts.Count > capacity || accounts.Distinct().Count() != accounts.Count)
            return "Choose a mode with enough room for your party.";
        var members = new List<(SoeConnection Connection, GatewaySessionState State)>();
        foreach (string account in accounts)
        {
            if (DoorSwingClientReady is { } ready && !ready(account))
                return "A game update is still starting. Try Play again in a moment.";
            var candidates = _accountSessions.Where(pair => pair.Key.State == ConnectionState.Open
                && pair.Value.Authenticated && pair.Value.AccountId == account).Take(2).ToArray();
            if (candidates.Length != 1 || candidates[0].Value.Match != MatchStep.Menu || !candidates[0].Value.AppearanceReadySent)
                return "Every member must launch the game and reach the main menu first.";
            members.Add((candidates[0].Key, candidates[0].Value));
        }
        var leader = members[0];
        ulong[] guids = members.Select(m => m.State.Guid).ToArray();
        foreach (var member in members)
            if (_parties.Find(member.State.Guid) is { } existing
                && (existing.Leader != leader.State.Guid || !existing.Members.SequenceEqual(guids)))
                return "Leave your existing in-game party before queueing this launcher party.";
        if (members.Count > 1 && _parties.Find(leader.State.Guid) is null)
        {
            foreach (var member in members.Skip(1))
            {
                long now = Environment.TickCount64;
                var invite = _parties.Invite(leader.State.Guid, member.State.Guid, now)!;
                _parties.Respond(invite.Token, member.State.Guid, true, now);
            }
            PublishPartyRoster(_parties.Find(leader.State.Guid)!);
        }
        var request = new PlayerWorldTransferRequest(world, "", 0, 1, 1);
        if (!TryQueuePartyMatch(leader.Connection, leader.State, request))
        {
            if (!PrepareMatchAdmission(leader.State, request)) return "The selected match is unavailable.";
            if (leader.State.CrateOpening is not null) EndCrateOpening(leader.Connection, leader.State);
            leader.State.Match = MatchStep.Queued;
            RunQueue(leader.Connection, leader.State);
        }
        return members.All(m => m.State.Match == MatchStep.Queued) ? null : "The server refused this queue.";
    }

    public string? LauncherCancel(string account)
    {
        var member = _accountSessions.FirstOrDefault(p => p.Key.State == ConnectionState.Open
            && p.Value.Authenticated && p.Value.AccountId == account);
        if (member.Value is null) return null;
        if (member.Value.Match == MatchStep.Queued)
        {
            SendTunnel(member.Key, new QueueExit(0, member.Value.Guid, Cancel: true).WriteTo);
            AbandonMatch(member.Key, member.Value, "launcher queue cancelled");
        }
        else if (member.Value.Match != MatchStep.Menu) return "Return to the game menu before leaving the party.";
        PartyLinkClosed(member.Value);
        return null;
    }
}
