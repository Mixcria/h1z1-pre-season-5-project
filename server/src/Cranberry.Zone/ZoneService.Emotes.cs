using Cranberry.Transport;
using Cranberry.Zone.Emotes;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void HandleEmote(SoeConnection connection, GatewaySessionState state, byte[] payload)
    {
        if (!EmotePackets.TryParseRequest(payload, out EmoteRequest request))
        {
            _log.Warn($"{connection} emote: malformed or unsupported request ({payload.Length} bytes)");
            return;
        }
        if (request.IsStart) StartEmote(connection, state, request.AnimationId);
        else if (state.ActiveEmoteAnimationId == request.AnimationId) StopEmote(connection, state);
    }

    private void StartEmote(SoeConnection connection, GatewaySessionState state, uint animationId)
    {
        // Only the default F-key animations are available until account emote equipment is
        // implemented. A request never grants control over another character or arbitrary clips.
        if (!AugustEmotes.TryGetAnimation(animationId, out AugustEmote emote)
            || state.Guid == 0 || !state.Authenticated
            || state.Match is not (MatchStep.Lobby or MatchStep.InMatch)
            || state.DeathSent || state.MountRequested
            || (state.Match == MatchStep.InMatch && state.Hitpoints == 0)
            || state.Fleet?.TryGetForOccupant(state.Guid, out _) == true)
            return;

        long now = Environment.TickCount64;
        // Bound key repeats without tying the gate to an assumed animation clip duration.
        if (state.LastEmoteStartAtMs is long last && now - last < 250)
            return;

        StopEmote(connection, state);
        state.ActiveEmoteAnimationId = animationId;
        state.LastEmoteStartAtMs = now;
        int viewers = SendEmote(connection, state, EmotePackets.Start(state.Guid, animationId));
        _log.Info($"{connection} emote: start {emote.Name} id={animationId} F{emote.SlotId}; locally predicted, {viewers} viewer(s)");
    }

    private void StopEmote(SoeConnection connection, GatewaySessionState state, bool includeSelf = false)
    {
        if (state.ActiveEmoteAnimationId is not uint animationId) return;
        state.ActiveEmoteAnimationId = null;
        SendEmote(connection, state, EmotePackets.Stop(state.Guid, animationId), includeSelf);
        _log.Info($"{connection} emote: stop id={animationId}");
    }

    private int SendEmote(SoeConnection connection, GatewaySessionState state, byte[] packet, bool includeSelf = false)
    {
        // Native 140cca610/140cca740 already apply the request locally. Echoing it would
        // retrigger Emote or stop a new predicted animation with the previous EmoteExit.
        if (includeSelf) SendTunnel(connection, writer => writer.WriteRaw(packet));
        if (!_options.Peers.Relay || state.Peer is not PeerSession subject) return 0;
        _peers.CollectViewers(subject, _peerViewers);
        foreach (PeerViewer viewer in _peerViewers)
            viewer.Viewer.Sink.Send(packet);
        return _peerViewers.Count;
    }
}
