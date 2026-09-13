using System.Numerics;

namespace Cranberry.Launcher.Core.Voice;

public sealed record VoiceParticipant(string AccountId, ulong CharacterId, ulong MatchId, Vector3 Position, float Heading);

public static class VoiceSpatial
{
    public static bool TryGains(VoiceParticipant speaker, VoiceParticipant listener, float range, out float left, out float right)
    {
        left = right = 0;
        if (speaker.AccountId == listener.AccountId || speaker.MatchId == 0 || speaker.MatchId != listener.MatchId
            || !float.IsFinite(range) || range <= 0 || !float.IsFinite(listener.Heading)) return false;
        Vector3 delta = speaker.Position - listener.Position;
        float distance = delta.Length();
        if (!float.IsFinite(distance) || distance >= range) return false;
        float fullVolumeRange = Math.Min(8, range * .2f);
        float gain = distance <= fullVolumeRange ? 1 : 1 - (distance - fullVolumeRange) / (range - fullVolumeRange);
        float horizontal = new Vector2(delta.X, delta.Z).Length();
        float pan = horizontal < .1f ? 0 : Math.Clamp(
            (delta.X * MathF.Cos(listener.Heading) - delta.Z * MathF.Sin(listener.Heading)) / horizontal, -1, 1);
        left = gain * MathF.Sqrt((1 - pan) * .5f);
        right = gain * MathF.Sqrt((1 + pan) * .5f);
        return true;
    }
}
