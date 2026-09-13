using System.Text.Json;
using Cranberry.Host.Config;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Host;

public sealed class VehicleSpawnConfigTests
{
    [Fact]
    public void DefaultChanceRandomizesOccupancyAndIsVisibleInEffectiveConfig()
    {
        var config = CranberryConfig.Defaults();
        Assert.Equal(0.3, config.Vehicles.Plan.SpawnChance);
        Assert.True(config.Vehicles.Plan.LimitPoliceStationPopulation);
        using var effective = JsonDocument.Parse(config.EffectiveJson());
        Assert.Equal(0.3, effective.RootElement.GetProperty("vehicles").GetProperty("spawnChance").GetDouble());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void FileChanceControlsActualMapPopulation(double configured, double expected)
    {
        string json = JsonSerializer.Serialize(new { vehicles = new { spawnChance = configured } });
        var config = CranberryConfig.Load([], _ => null, json);
        Assert.Null(config.FileNote);
        Assert.Equal(expected, config.Vehicles.Plan.SpawnChance);
        var anchors = VehicleAnchorSet.LoadDefault();
        var plan = VehicleSpawnPlanner.Plan(anchors, VehicleRoster.LoadDefault(), 42, config.Vehicles.Plan);
        Assert.Equal(expected == 0 ? 0 : anchors.Count, plan.Count);
    }

    [Theory]
    [InlineData("CRANBERRY_VEHICLE_SPAWN_CHANCE")]
    [InlineData("CRANBERRY_VEHICLES_SPAWN_CHANCE")]
    public void EnvironmentOverridesTheFile(string variable)
    {
        var config = CranberryConfig.Load([], name => name == variable ? "0.8" : null,
            """{"vehicles":{"spawnChance":0.2}}""");
        Assert.Equal(0.8, config.Vehicles.Plan.SpawnChance);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("invalid")]
    public void InvalidChanceFallsBackToTheDefault(string value)
    {
        var config = CranberryConfig.Load([], name => name == "CRANBERRY_VEHICLE_SPAWN_CHANCE" ? value : null,
            "{}");
        Assert.Equal(0.3, config.Vehicles.Plan.SpawnChance);
    }
}
