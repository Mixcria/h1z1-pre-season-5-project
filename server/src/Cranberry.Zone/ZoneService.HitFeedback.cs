using Cranberry.Transport;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void SendHitFeedback(SoeConnection shooter, WeaponHitFeedback marker)
    {
        if (marker.IsMelee || marker.IsVehicle)
        {
            // The vehicle flag selects the white reticle and PLAY_SHOT_ENEMY_VEHICLE.
            SendTunnel(shooter, marker.WriteTo);
            return;
        }

        // The modern icon's audio ignores armour. Use the native surface cue instead,
        // including on absorbed or lethal hits, without a duplicate generic hit sound.
        SendTunnel(shooter, (marker with { SuppressAudio = true }).WriteTo);
        SendTunnel(shooter, HitFeedbackSound.For(marker).WriteTo);
    }
}
