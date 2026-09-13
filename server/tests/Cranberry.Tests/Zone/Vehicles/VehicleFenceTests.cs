using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.Destructibles;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed class VehicleFenceTests
{
    [Fact]
    public void ASharedFleetBurnsFuelOnlyOnItsDriversPump()
    {
        var (fleet, car, _) = Setup();
        car.EngineOn = true;
        float before = car.Fuel;
        fleet.BurnFuel(1f, driverGuid: 1002);
        Assert.Equal(before, car.Fuel);
        fleet.BurnFuel(1f, driverGuid: 1001);
        Assert.Equal(before - fleet.Options.FuelBurnPerSecond, car.Fuel);
    }

    private static (VehicleFleet Fleet, MatchVehicle Car, DestructibleCatalog Catalog) Setup()
    {
        var roster = VehicleRoster.LoadDefault();
        var fleet = new VehicleFleet(roster);
        var car = new MatchVehicle(2000, 3000, roster.Require(1), Vector3.Zero, 0, 100000, 10000);
        fleet.Add(car);
        fleet.TryEnter(car.Guid, 1001, 0, 1000, out _, out _);
        fleet.TryApplyOwnerPose(car.TransientId, 1001, Vector3.Zero, 0, 2000, out _);
        return (fleet, car, new([new(7, "Farm_Props_Fences_Fence01.adr", new(3, 0, 0), 5294, 5000)]));
    }

    [Fact]
    public void NativePolicyUsesModelIdsAndFloatPercentLoss()
    {
        using var writer = new PacketWriter();
        VehicleFencePolicy.WriteModels(writer);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(0xba, reader.ReadByte());
        Assert.Equal(6, reader.ReadUInt16());
        Assert.Equal(0u, reader.ReadUInt32());
        uint count = reader.ReadUInt32();
        Assert.Equal((uint)VehicleFencePolicy.ModelIds.Count, count);
        for (int i = 0; i < count; i++) Assert.Contains(reader.ReadUInt32(), VehicleFencePolicy.ModelIds.Values);
        Assert.Equal(count, reader.ReadUInt32());
        for (int i = 0; i < count; i++)
        {
            Assert.Contains(reader.ReadUInt32(), VehicleFencePolicy.ModelIds.Values);
            Assert.InRange(reader.ReadSingle(), 10f, 20f);
        }
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void OwnerImpactBreaksOnceAndPersistsForLateJoin()
    {
        var (fleet, car, catalog) = Setup();
        var world = new DestructibleWorld();
        var hit = new DestructibleHit(7, catalog.Props[7].Model, 0, car.Guid);
        Assert.True(world.HitByVehicle(catalog, hit, fleet, 1001, 2100)!.Value.Destroyed);
        Assert.Null(world.HitByVehicle(catalog, hit, fleet, 1001, 2200));
        Assert.Single(world.Destroyed);
        using var writer = new PacketWriter();
        DestructiblePackets.WriteInitial(writer, catalog, world.Destroyed);
        var reader = new PacketReader(writer.Written);
        reader.Skip(7);
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(7u, reader.ReadUInt32());
    }

    [Fact]
    public void WrongOwnerRemoteStaleAndNonFenceReportsAreRejected()
    {
        var (fleet, car, catalog) = Setup();
        var world = new DestructibleWorld();
        var hit = new DestructibleHit(7, catalog.Props[7].Model, 0, car.Guid);
        Assert.Null(world.HitByVehicle(catalog, hit, fleet, 1002, 2100));
        Assert.Null(world.HitByVehicle(catalog, hit, fleet, 1001, 5001));
        Assert.Null(world.HitByVehicle(catalog, hit with { ProjectileId = 42 }, fleet, 1001, 2100));
        var far = new DestructibleCatalog([catalog.Props[7] with { Position = new(100, 0, 0) }]);
        Assert.Null(world.HitByVehicle(far, hit, fleet, 1001, 2100));
        var wall = new DestructibleCatalog([catalog.Props[7] with { Model = "ConcreteWall.adr" }]);
        Assert.Null(world.HitByVehicle(wall, hit with { Model = "ConcreteWall.adr" }, fleet, 1001, 2100));
        Assert.Empty(world.Destroyed);
    }

    [Fact]
    public void CoastingCarCanStillBreakAFenceAfterBailingOut()
    {
        var (fleet, car, catalog) = Setup();
        fleet.TryExit(1001, 3000, 60, out _, out _);
        var hit = new DestructibleHit(7, catalog.Props[7].Model, 0, car.Guid);
        Assert.NotNull(new DestructibleWorld().HitByVehicle(catalog, hit, fleet, 1001, 3100));
    }

    [Fact]
    public void CatalogContainsAuthoredBreakablesButNoConcreteOrMilitaryBarriers()
    {
        Assert.True(VehicleFencePolicy.Catalog.Props.Count > 30000);
        Assert.All(VehicleFencePolicy.Catalog.Props.Values, p => Assert.True(VehicleFencePolicy.IsVehicleBreakable(p.Model)));
        Assert.False(VehicleFencePolicy.IsFence("Common_Props_MilitaryBase_HighFence1x2.adr"));
        Assert.False(VehicleFencePolicy.IsFence("City_Structures_Blockade_FenceWire01.adr"));
    }
}
