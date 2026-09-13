using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Zone;

public sealed record ProximityVoicePresence(string AccountId, ulong CharacterId, ulong MatchId, Vector3 Position, float Heading);
public sealed record ProximityVoiceHud(string AccountId, ulong CharacterId, ulong MatchId, ulong[] Speakers);

public sealed partial class ZoneService
{
    public const string VoiceHudWindow = "CRANBERRY_VOICE_HUD_V1";
    public const string VoiceHudPrefix = "@cranberry/voice/1;";

    private void HandleVoiceHud(SoeConnection connection, GatewaySessionState state, string action)
    {
        if (!state.Authenticated || action is not ("open" or "close")) return;
        state.VoiceHudLinked = action == "open";
        state.VoiceHudLastView = null;
    }

    /// <summary>Called on the game listener. Recheck identity and range before publishing names to the native HUD.</summary>
    public void PublishProximityVoiceHud(IReadOnlyList<ProximityVoiceHud> views, float rangeMetres, long now)
    {
        var players = ProximityVoicePlayers();
        var byAccount = players.GroupBy(p => p.AccountId).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byCharacter = players.GroupBy(p => p.CharacterId).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());
        var hud = views.GroupBy(v => v.AccountId).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var names = _accountSessions.Values.Where(s => s.Authenticated).GroupBy(s => s.Guid)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().CharacterName);
        foreach (var (connection, state) in _accountSessions)
        {
            if (!state.VoiceHudLinked || !state.Authenticated || connection.State != ConnectionState.Open) continue;
            var rows = new List<string>();
            if (float.IsFinite(rangeMetres) && rangeMetres > 0
                && byAccount.TryGetValue(state.AccountId, out var listener) && listener.CharacterId == state.Guid
                && hud.TryGetValue(state.AccountId, out var view)
                && view.CharacterId == listener.CharacterId && view.MatchId == listener.MatchId)
            {
                foreach (ulong id in view.Speakers.Distinct().Take(10))
                {
                    if (!byCharacter.TryGetValue(id, out var speaker) || speaker.MatchId != listener.MatchId
                        || !byAccount.ContainsKey(speaker.AccountId) || !names.TryGetValue(id, out string? name)
                        || !float.IsFinite(Vector3.DistanceSquared(listener.Position, speaker.Position))
                        || (id != listener.CharacterId && Vector3.DistanceSquared(listener.Position, speaker.Position) >= rangeMetres * rangeMetres)) continue;
                    rows.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Uri.EscapeDataString(name));
                }
            }
            string text = VoiceHudPrefix + string.Join(';', rows);
            // Refresh visible rows so an interrupted connection cannot leave a stuck talking icon.
            if (state.VoiceHudLastView == text && (rows.Count == 0 || now - state.VoiceHudSentAt < 500)) continue;
            state.VoiceHudLastView = text;
            state.VoiceHudSentAt = now;
            SendTunnel(connection, new ConsolePrint(text).WriteTo);
        }
    }

    /// <summary>Only the gateway listener calls this. Voice clients never supply world identity or position.</summary>
    public IReadOnlyList<ProximityVoicePresence> ProximityVoicePlayers()
    {
        List<ProximityVoicePresence> players = [];
        foreach (var (connection, state) in _accountSessions)
        {
            if (connection.State != ConnectionState.Open || !state.Authenticated || string.IsNullOrWhiteSpace(state.AccountId)
                || state.Hitpoints == 0 || state.DeathSent || state.Match is not (MatchStep.Lobby or MatchStep.InMatch)
                || state.Peer is not { InMatch: true, HasPose: true, MatchId: not 0 } peer) continue;
            players.Add(new(state.AccountId, state.Guid, peer.MatchId, peer.Position, peer.Heading));
        }
        return players;
    }
}
