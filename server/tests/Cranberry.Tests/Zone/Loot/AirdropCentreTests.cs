using System.Numerics;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed class AirdropCentreTests
{
    [Theory]
    [InlineData(1ul)]
    [InlineData(42ul)]
    [InlineData(123456ul)]
    public void EverySupplyDropUsesTheRevealedCentreAndThatPointsGroundHeight(ulong seed)
    {
        var options = AirdropOptions.Default with { FirstDropAtMs = 0, DropIntervalMs = 1000, MaxDropsPerMatch = 2, BombsEnabled = false };
        var drops = new MatchAirdrops(options, seed);
        var first = new GasCircle(new Vector3(2100, 0, -2700), 1600);
        var second = new GasCircle(new Vector3(2200, 0, -2500), 1000);
        float Ground(float x, float z) => 100 + x * .01f + z * .02f;
        var events = new List<AirdropEvent>();
        drops.Tick(1000, 1, second, Vector3.Zero, events, Ground, at => at == 0 ? first : second);
        Assert.Equal(2, drops.Flights.Count);
        for (int i = 0; i < 2; i++)
        {
            var centre = i == 0 ? first.Centre : second.Centre;
            var impact = Assert.Single(drops.Flights[i].Payloads).ImpactPosition;
            Assert.Equal(new Vector3(centre.X, Ground(centre.X, centre.Z), centre.Z), impact);
        }
    }
}
