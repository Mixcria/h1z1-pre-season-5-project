using System.Numerics;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed class AirdropDisplayPlanTests
{
    private static AirdropFlight Supply() => new(0, 0, AirdropFlightKind.Supply,
        1000, 21000, new(-1500, 800, 0), new(1500, 800, 0), Vector3.Zero,
        [new(0, 11000, 31000, new(0, 800, 0), new(0, 200, 0), false)]);

    [Fact]
    public void NativeSupplyIncludesPlaneCrateAndCanopyOnTheSynchronizedClock()
    {
        var rails = AirdropDisplayPlan.Create(Supply(), AirdropOptions.Default, 4_000_000, 1000);
        Assert.Equal(new uint[] { 9215, 9218, 9219 }, rails.Select(r => r.ModelId));
        Assert.Equal(4_001_000u, rails[0].ActivationTimeMs);
        Assert.Equal(4_011_000u, rails[1].ActivationTimeMs);
        Assert.Equal(0.5f, rails[0].Rotation);
        Assert.Equal(5038u, rails[1].EndEffectId);
        Assert.Equal(0u, rails[2].EndEffectId);
        Assert.Equal(rails[1].Waypoints, rails[2].Waypoints);
        Assert.All(rails, r => Assert.All(r.Waypoints, k => Assert.Equal(0f, k.Position.W)));
    }

    [Fact]
    public void LateViewerGetsOnlySurvivingRailsWithoutReplayingLandingEffects()
    {
        Assert.Equal(new uint[] { 9218, 9219 },
            AirdropDisplayPlan.Create(Supply(), AirdropOptions.Default, 10000, 22000).Select(r => r.ModelId));
        Assert.Empty(AirdropDisplayPlan.Create(Supply(), AirdropOptions.Default, 10000, 31000));
    }

    [Fact]
    public void BombVisualFollowsAcceleratedFallAndKeepsTheImpactCoordinate()
    {
        var payload = new AirdropPayload(0, 2000, 12000, new(0, 800, 0), new(1500, 200, 0), true);
        var flight = Supply() with { Kind = AirdropFlightKind.Bomber, Payloads = [payload] };
        var bomb = Assert.Single(AirdropDisplayPlan.Create(flight, AirdropOptions.Default, 1000, 1000),
            r => r.ModelId == 9372);
        Assert.Equal(5328u, bomb.EndEffectId);
        Assert.Equal(new Vector4(750, 650, 0, 0), bomb.Waypoints[32].Position);
        Assert.Equal(new Vector4(payload.ImpactPosition, 0), bomb.EndPosition);
        // Max linearization error for the actual 600 m quadratic fall is <4 cm.
        for (int i = 0; i < bomb.Waypoints.Count - 1; i++)
        {
            var a = bomb.Waypoints[i]; var b = bomb.Waypoints[i + 1];
            float t = (a.Progress + b.Progress) * 0.5f;
            float expectedY = 800 - 600 * t * t;
            Assert.InRange(Math.Abs((a.Position.Y + b.Position.Y) * 0.5f - expectedY), 0f, 0.04f);
        }
    }

    [Fact]
    public void PlaneKeepsCruiseAltitudeUntilReleaseThenClimbsAway()
    {
        var flight = Supply() with { End = new(1500, 1050, 0), ClimbStartAtMs = 11000 };
        var plane = AirdropDisplayPlan.Create(flight, AirdropOptions.Default, 0, 1000)[0];
        var release = Assert.Single(plane.Waypoints, k => k.Progress == 0.5f);
        Assert.Equal(800f, release.Position.Y);
        Assert.Equal(1050f, plane.Waypoints[^1].Position.Y);
        Assert.All(plane.Waypoints.Where(k => k.Progress < 0.5f), k => Assert.Equal(800f, k.Position.Y));
    }

    [Fact]
    public void ActivationUsesTheSameUintWrapAsSynchronization()
    {
        var rail = AirdropDisplayPlan.Create(Supply(), AirdropOptions.Default, uint.MaxValue - 499L, 1000)[0];
        Assert.Equal(500u, rail.ActivationTimeMs);
    }
}
