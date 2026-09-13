using System.Numerics;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class CrateSceneLayoutTests
{
    [Fact]
    public void GalleryKeepsCapturedEyeWithCrateFiveMetresWestAndPlayerBesideCamera()
    {
        var camera = CrateSceneLayout.Camera;
        Assert.Equal(new Vector3(-67.95f, 507.751f, 307.163f), new(camera.TargetX, camera.TargetY, camera.TargetZ));
        Assert.Equal(49.7f, camera.Fov);
        Assert.Equal(0f, camera.Distance);
        Assert.Equal(505.965f, CrateSceneLayout.CratePosition.Y);
        Assert.Equal(505.965f, CrateSceneLayout.PlayerPosition.Y);
        Vector2 eye = new(camera.TargetX, camera.TargetZ);
        Vector2 crate = new(CrateSceneLayout.CratePosition.X, CrateSceneLayout.CratePosition.Z);
        Vector2 player = new(CrateSceneLayout.PlayerPosition.X, CrateSceneLayout.PlayerPosition.Z);
        Assert.InRange(Vector2.Distance(eye, crate), 4.999f, 5.001f);
        Assert.True(crate.X < eye.X - 4.9f);
        Assert.InRange(Vector2.Distance(eye, player), 1.4f, 1.5f);
        Vector2 forward = Vector2.Normalize(crate - eye);
        Vector2 right = new(forward.Y, -forward.X);
        Assert.InRange(Vector2.Dot(player - eye, right), 0.749f, 0.751f);
    }

    [Fact]
    public void NativeCrateFrontFacesCameraWithHingeBehindForBothObservers()
    {
        Vector4 rotation = CrateSceneLayout.CrateRotation;
        Quaternion quaternion = new(rotation.X, rotation.Y, rotation.Z, rotation.W);
        Assert.InRange(quaternion.LengthSquared(), 0.999999f, 1.000001f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, quaternion)), 0f, 0.000001f);

        // Native Common_DPO_MarketCrate_Crate01_LOD0.dme bind-pose measurements:
        // Hinge is at local -Z; the front clasp springs are at +Z, across the wide X edge.
        Vector3 front = CrateSceneLayout.CratePosition + Vector3.Transform(new Vector3(0, 0.785836f, 0.463229f), quaternion);
        Vector3 hinge = CrateSceneLayout.CratePosition + Vector3.Transform(new Vector3(0, 0.799743f, -0.475239f), quaternion);
        Vector3 frontNormal = Vector3.Normalize(new(front.X - hinge.X, 0, front.Z - hinge.Z));
        var camera = CrateSceneLayout.Camera;
        Vector3 eye = new(camera.TargetX, camera.TargetY, camera.TargetZ);
        Vector3 towardEye = Vector3.Normalize(new(eye.X - CrateSceneLayout.CratePosition.X, 0,
            eye.Z - CrateSceneLayout.CratePosition.Z));
        Assert.InRange(Vector3.Dot(frontNormal, towardEye), 0.99999f, 1.00001f);
        Assert.InRange(MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitX, quaternion), towardEye)), 0f, 0.00001f);

        Vector3 player = new(CrateSceneLayout.PlayerPosition.X, CrateSceneLayout.PlayerPosition.Y,
            CrateSceneLayout.PlayerPosition.Z);
        foreach (Vector3 observer in new[] { eye, player })
        {
            Assert.True(Vector3.DistanceSquared(front, observer) < Vector3.DistanceSquared(hinge, observer));
            Vector3 towardObserver = Vector3.Normalize(new(observer.X - CrateSceneLayout.CratePosition.X, 0,
                observer.Z - CrateSceneLayout.CratePosition.Z));
            Assert.True(Vector3.Dot(frontNormal, towardObserver) > 0.98f);
        }
    }

    [Fact]
    public void NativePlayerRelativeAimOffsetsPointAtCrateCentreAfterPlayerRelocation()
    {
        var camera = CrateSceneLayout.Camera;
        Vector3 eye = new(camera.TargetX, camera.TargetY, camera.TargetZ);
        Vector3 player = new(CrateSceneLayout.PlayerPosition.X, CrateSceneLayout.PlayerPosition.Y,
            CrateSceneLayout.PlayerPosition.Z);
        Vector3 delta = player - eye;
        float yaw = MathF.Atan2(delta.X, delta.Z) + camera.AimYawOffset;
        float pitch = MathF.Atan2(delta.Y, new Vector2(delta.X, delta.Z).Length()) + camera.AimPitchOffset;
        Vector3 nativeDirection = new(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch));
        Vector3 target = CrateSceneLayout.CratePosition + new Vector3(0, 0.75f, 0);
        Assert.InRange(Vector3.Distance(eye + nativeDirection * Vector3.Distance(eye, target), target), 0f, 0.001f);
        Vector2 towardCrate = Vector2.Normalize(new(target.X - player.X, target.Z - player.Z));
        Vector2 playerFacing = new(MathF.Sin(camera.Heading), MathF.Cos(camera.Heading));
        Assert.InRange(Vector2.Distance(towardCrate, playerFacing), 0f, 0.001f);
    }
}
