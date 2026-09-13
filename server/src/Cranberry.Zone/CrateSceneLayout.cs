using System.Numerics;

namespace Cranberry.Zone;

/// <summary>
/// Authored shooting-gallery placement around the captured kotkcrates camera eye. The capture
/// supplies the eye, FOV and westward view bearing, but contains no market-crate spawn.
/// Player and crate positions are server design, with a crate aim point 0.75m above its root.
/// Native 140ea8d30 derives camera yaw/pitch from player-minus-eye, then adds the aim offsets;
/// recompute those offsets for this nearby player instead of reusing the distant yard mark's.
/// </summary>
public static class CrateSceneLayout
{
    private const float GroundY = 505.965f;
    private static readonly Vector3 Eye = new(-67.95f, 507.751f, 307.163f);
    private static readonly Vector3 CapturedPlayer = new(18.75f, GroundY, 280.55f);
    private static readonly float CapturedViewYaw = Yaw(CapturedPlayer - Eye) + 2.8098f;
    private static readonly Vector3 Forward = new(MathF.Sin(CapturedViewYaw), 0, MathF.Cos(CapturedViewYaw));
    private static readonly Vector3 Right = new(Forward.Z, 0, -Forward.X);

    public static Vector3 CratePosition { get; } = OnGround(Eye + 5f * Forward);
    public static Vector4 PlayerPosition { get; } = new(OnGround(Eye + 1.25f * Forward + 0.75f * Right), 1f);
    public static Vector4 CrateRotation { get; } = CreateCrateRotation();
    public static StaticViewReply Camera { get; } = CreateCamera();

    private static Vector4 CreateCrateRotation()
    {
        // Model 10029's native DME puts Hinge (hash C2FFAE95) at Z=-0.475239
        // and the four ClaspSpring_*Front bones at Z=+0.463229: its front is +Z.
        // Face that broad front toward the camera; the nearby player is 11.31 degrees off it.
        float yaw = Yaw(Eye - CratePosition);
        return new(0, MathF.Sin(yaw / 2f), 0, MathF.Cos(yaw / 2f));
    }

    private static StaticViewReply CreateCamera()
    {
        Vector3 player = new(PlayerPosition.X, PlayerPosition.Y, PlayerPosition.Z);
        Vector3 target = CratePosition + new Vector3(0, 0.75f, 0);
        Vector3 playerFromEye = player - Eye;
        Vector3 targetFromEye = target - Eye;
        return new(TargetX: Eye.X, TargetY: Eye.Y, TargetZ: Eye.Z,
            Heading: Yaw(target - player), YawOffset: 0, Pitch: 0, Distance: 0,
            AimYawOffset: MathF.IEEERemainder(Yaw(targetFromEye) - Yaw(playerFromEye), MathF.Tau),
            AimPitchOffset: Pitch(targetFromEye) - Pitch(playerFromEye), Fov: 49.7f, FocusArea: 1);
    }

    private static Vector3 OnGround(Vector3 position) => new(position.X, GroundY, position.Z);
    private static float Yaw(Vector3 direction) => MathF.Atan2(direction.X, direction.Z);
    private static float Pitch(Vector3 direction) =>
        MathF.Atan2(direction.Y, MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z));
}
