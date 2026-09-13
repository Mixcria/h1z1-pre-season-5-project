using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    // Exact previously unanswered native /vehicle 1 request, host log 2026-09-06 18:46:23.759.
    private static byte[] CapturedNativeVehicle() => Convert.FromHexString(
        "099B04010000000008EFD344000082419B4EE54436C38F4000000000000000000000000000");

    private static void SendNativeVehicle(ZoneService service, SoeConnection connection, byte[] payload)
    {
        var packet = new byte[payload.Length + 1];
        packet[0] = new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte();
        payload.CopyTo(packet, 1);
        service.OnMessage(connection, packet);
    }

    [Fact]
    public void ActualNativeVehicleCaptureSpawnsAtTheClientsRaycastWithItsHeading()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        byte[] packet = CapturedNativeVehicle();
        NativeVehicleCommand request = NativeVehicleCommand.Parse(packet);
        Assert.Equal(1u, request.VehicleId);
        Assert.Equal(new Vector3(1695.4697265625f, 16.25f, 1834.4564208984375f), request.Position);
        Assert.Equal(4.492579460144043f, request.Yaw);
        int before = SentCount(recorder);

        SendNativeVehicle(service, connection, packet);

        var car = Assert.Single(Member<VehicleFleet>(connection, "Fleet").Vehicles, v => v.AnchorInstanceId == 0);
        Assert.Equal(request.Position, car.Position);
        Assert.Equal(request.Yaw, car.Yaw);
        Assert.Equal(request.VehicleId, car.Definition.VehicleId);
        Assert.Single(Sent(recorder, before), p => p[1] == ZoneOpcodes.AddLightweightVehicle);
        Assert.Single(Sent(recorder, before), p => p[1] == ZoneOpcodes.LightweightToFullVehicle);
        Assert.Contains(ConsoleLines(recorder, before), line => line.StartsWith("+ spawned"));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(5u)]
    public void NativeVehicleSupportsEveryDrivableFamilyAndItsAutoMountFlag(uint vehicleId)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        byte[] packet = CapturedNativeVehicle();
        packet[0] = ConsoleOpcodes.AdminBase;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), vehicleId);
        packet[28] = 1;
        int before = SentCount(recorder);
        SendNativeVehicle(service, connection, packet);
        var fleet = Member<VehicleFleet>(connection, "Fleet");
        Assert.True(fleet.TryGetForOccupant(Member<ulong>(connection, "Guid"), out var occupied));
        Assert.Equal(vehicleId, occupied!.Definition.VehicleId);
        Assert.Equal(0, occupied.SeatOf(Member<ulong>(connection, "Guid")));
        Assert.Contains(Sent(recorder, before), p => p[1] == 0x70 && p[2] == 0x02);
    }

    [Theory]
    [InlineData("bad-id")]
    [InlineData("reward-set")]
    [InlineData("faction")]
    [InlineData("nan-position")]
    [InlineData("infinite-heading")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("bad-mount")]
    [InlineData("player")]
    [InlineData("menu")]
    [InlineData("parachute")]
    public void NativeVehicleRejectsUnsupportedMalformedOrUnauthorizedRequestsWithoutSpawning(string variant)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        byte[] packet = CapturedNativeVehicle();
        switch (variant)
        {
            case "bad-id": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), 0); break;
            case "reward-set": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(29), 1); break;
            case "faction": packet[7] = 1; break;
            case "nan-position": BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8), float.NaN); break;
            case "infinite-heading": BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(20), float.PositiveInfinity); break;
            case "short": packet = packet[..^1]; break;
            case "long": packet = [.. packet, 0]; break;
            case "bad-mount": packet[28] = 2; break;
            case "player": Session(connection).TierOverride = ConsoleTier.Player; break;
            case "menu": EnterStep(connection, "Menu"); break;
            case "parachute": SetSessionMember(connection, "ChuteGuid", 99UL); break;
        }
        int before = SentCount(recorder);
        SendNativeVehicle(service, connection, packet);
        Assert.Null(Fleet(connection));
        if (variant == "player") Assert.Empty(Sent(recorder, before));
        else Assert.NotEmpty(ConsoleLines(recorder, before));
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
    }

    [Fact]
    public void CarListIsReadOnlyAndGivesNativeIds()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "car", "list");
        Assert.Null(Fleet(connection));
        Assert.Contains(ConsoleLines(recorder, before), line => line.Contains("1 offroader") && line.Contains("5 atv"));
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
    }
}
