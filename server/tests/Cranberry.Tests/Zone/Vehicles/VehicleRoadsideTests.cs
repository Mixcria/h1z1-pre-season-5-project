using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Destructibles;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed class VehicleRoadsideTests
{
    [Theory]
    [InlineData("City_Props_TrafficSigns_StopSigns.adr", 9927u, 5587u, 188)]
    [InlineData("City_Props_TrafficSigns_Town.adr", 9918u, 5589u, 541)]
    [InlineData("City_Props_TrafficSigns_HighWay.adr", 9992u, 5585u, 204)]
    [InlineData("City_Props_TrafficSigns_HighWay_LargeSigns_Road_TwoPost01.adr", 9948u, 5586u, 73)]
    [InlineData("City_Props_TrafficSigns_HighWay_LargeSigns_Wood_TwoPost01.adr", 9982u, 5598u, 89)]
    [InlineData("City_Props_StreetSigns_StreetNames_Sign.adr", 9943u, 5597u, 374)]
    [InlineData("City_Props_StreetSigns_StreetNames_SignPost.adr", 9950u, 5588u, 131)]
    [InlineData("City_Props_PostalBox01.adr", 9917u, 5579u, 136)]
    [InlineData("Residential_Props_CommunalMailbox.adr", 9924u, 5578u, 91)]
    [InlineData("City_Props_GarbageCan01.adr", 9919u, 5581u, 1594)]
    [InlineData("Residential_Props_MunicipalGarbageCans.adr", 9925u, 5582u, 1240)]
    [InlineData("Industrial_Props_Barriers_CrowdBarrier01.adr", 10010u, 5617u, 627)]
    [InlineData("Farm_Props_CorralFence_Fence01.adr", 10395u, 6003u, 205)]
    public void AugustRoadsideModelsUseTheirOwnIdsEffectsAndExactPlacementCensus(
        string model, uint modelId, uint effectId, int count)
    {
        Assert.Equal(modelId, VehicleFencePolicy.ModelIds[model]);
        var props = VehicleFencePolicy.Catalog.Props.Values.Where(p => p.Model == model).ToArray();
        Assert.Equal(count, props.Length);
        Assert.All(props, p => Assert.Equal(effectId, p.EffectId));
    }

    [Theory]
    [InlineData("City_Props_ConcreteBarriers.adr")]
    [InlineData("City_Props_GarbageCan02.adr")] // Models.txt explicitly says concrete.
    [InlineData("City_Props_UtilityBox.adr")]
    [InlineData("City_Props_FireHydrant.adr")]
    [InlineData("Common_Props_MilitaryBase_HescoBarrier.adr")]
    [InlineData("Common_Props_MilitaryBase_HighFence1x2.adr")]
    [InlineData("City_Structures_Blockade_FenceWire01.adr")]
    [InlineData("Commercial_Props_ShoppingCenterSign01.adr")]
    [InlineData("City_Props_TrafficSigns_HighWay_LargeSigns_Rustic_NoPosts01.adr")]
    public void StructuralAndUnmappedPropsAreNotEnabledByBroadNameMatching(string model)
    {
        Assert.False(VehicleFencePolicy.IsVehicleBreakable(model));
        Assert.DoesNotContain(VehicleFencePolicy.Catalog.Props.Values, p => p.Model == model);
    }

    [Fact]
    public void EveryEnabledFamilyCanReportAndEveryPlacedFamilyCanBreakFromACoastingCar()
    {
        using var initial = new PacketWriter();
        DestructiblePackets.WriteInitial(initial, DestructibleCatalog.Default, []);
        var reader = new PacketReader(initial.Written);
        reader.Skip(7);
        Assert.Equal(0u, reader.ReadUInt32());
        uint count = reader.ReadUInt32();
        var reportHashes = new HashSet<uint>();
        for (int i = 0; i < count; i++)
        {
            reportHashes.Add(reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
        }
        Assert.True(reader.AtEnd);
        foreach (string model in VehicleFencePolicy.ModelIds.Keys)
        {
            Assert.Contains(StringHashValue.HashName(model), reportHashes);
            Assert.Contains(StringHashValue.HashName(model.ToLowerInvariant()), reportHashes);
            Assert.Contains(StringHashValue.HashName(model.ToUpperInvariant()), reportHashes);
        }

        var roster = VehicleRoster.LoadDefault();
        foreach (var family in VehicleFencePolicy.Catalog.Props.Values.GroupBy(p => p.Model))
        {
            var prop = family.First();
            var fleet = new VehicleFleet(roster);
            var car = new MatchVehicle(2000, 3000, roster.Require(1), prop.Position, 0, 100000, 10000);
            fleet.Add(car);
            Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, 1001, 0, 1000, out _, out _));
            Assert.True(fleet.TryApplyOwnerPose(car.TransientId, 1001, prop.Position, 0, 2000, out _));
            Assert.Equal(VehicleActionResult.Ok, fleet.TryExit(1001, 3000, 60, out _, out _));
            var world = new DestructibleWorld();
            var report = new DestructibleHit(prop.ObjectId, prop.Model, 0, car.Guid);
            Assert.Null(world.HitByVehicle(VehicleFencePolicy.Catalog, report, fleet, 9999, 3100));
            Assert.Null(world.HitByVehicle(VehicleFencePolicy.Catalog,
                report with { Model = "City_Props_ConcreteBarriers.adr" }, fleet, 1001, 3100));
            Assert.True(world.HitByVehicle(VehicleFencePolicy.Catalog, report, fleet, 1001, 3100)!.Value.Destroyed);
            Assert.Null(world.HitByVehicle(VehicleFencePolicy.Catalog, report, fleet, 1001, 3100));
            Assert.Equal(prop, Assert.Single(world.Destroyed));
        }
    }
}
