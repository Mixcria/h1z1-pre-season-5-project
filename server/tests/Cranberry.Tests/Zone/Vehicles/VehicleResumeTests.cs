using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed class VehicleResumeTests
{
    [Fact]
    public void FullDataCarriesLiveResourcesEngineAndBothSeatedCharacters()
    {
        var roster = VehicleRoster.LoadDefault();
        var fleet = new VehicleFleet(roster);
        var car = new MatchVehicle(100, 1, roster.Require(1), Vector3.Zero, 0, 75_000, 2345);
        fleet.Add(car);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(100, 1001, 0, 0, out _, out _));
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(100, 1002, 3, 5000, out _, out _));
        car.EngineOn = true;
        using var writer = new PacketWriter();
        VehicleFullState.Create(car).WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();
        Assert.Equal(561, bytes.Length);
        Assert.Equal(192, BitConverter.ToInt32(bytes, 185));
        Assert.Equal(2, BitConverter.ToInt32(bytes, 189));
        Assert.Equal(50u, BitConverter.ToUInt32(bytes, 197));
        Assert.Equal(50u, BitConverter.ToUInt32(bytes, 201));
        Assert.Equal(2345u, BitConverter.ToUInt32(bytes, 209));
        Assert.Equal(561u, BitConverter.ToUInt32(bytes, 291));
        Assert.Equal(75_000u, BitConverter.ToUInt32(bytes, 303));
        Assert.Equal(1, bytes[401]);
        Assert.Equal(2, BitConverter.ToInt32(bytes, 447));
        Assert.Equal(1001UL, BitConverter.ToUInt64(bytes, 451));
        Assert.Equal(0, bytes[499]);
        Assert.Equal(1002UL, BitConverter.ToUInt64(bytes, 500));
        Assert.Equal(3, bytes[548]);
    }

    [Fact]
    public void SavedSkinRestoresForOnlyItsCharacterAndRejectsCrossVehicleItems()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-vehicle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = new VehicleSkinState();
            Assert.True(state.Set(1, 1, 3802));
            Assert.False(state.Set(1, 1, 4302));
            state.Save(root, 1001);
            var restored = VehicleSkinState.Load(root, 1001);
            Assert.Equal(840u, restored.ShaderFor(1));
            Assert.Single(restored.Snapshot());
            Assert.Empty(VehicleSkinState.Load(root, 1002).Snapshot());
            restored.Unset(1, 1);
            restored.Save(root, 1001);
            Assert.Empty(VehicleSkinState.Load(root, 1001).Snapshot());
            Assert.Equal(838u, restored.ShaderFor(1));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(2, 14)]
    [InlineData(3, 10)]
    [InlineData(4, 2)]
    [InlineData(6, 6)]
    public void VehicleSkinRequestRejectsTruncationAndTrailingData(byte sub, int length)
    {
        byte[] request = new byte[length];
        request[0] = 0xf2;
        request[1] = sub;
        Assert.Equal(sub, VehicleSkinRequest.Parse(request).SubOpcode);
        for (int end = 0; end < length; end++)
        {
            byte[] truncated = request[..end];
            Assert.ThrowsAny<Exception>(() => VehicleSkinRequest.Parse(truncated));
        }
        Assert.Throws<PacketFormatException>(() => VehicleSkinRequest.Parse([.. request, 0]));
    }
}
