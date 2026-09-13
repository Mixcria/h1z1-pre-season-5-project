using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Fact]
    public void ConsoleVehicleReachesAStationaryPeerWhoseStreamExhaustedItsDisc()
    {
        using var f = new Fixture();
        var caller = f.Add(1, Vector3.Zero);
        var peer = f.Add(2, Vector3.Zero);
        var separate = f.Add(3, Vector3.Zero, matchId: 2);
        foreach (var c in new[] { caller, peer, separate })
        {
            Call(f.Service, "SpawnNearbyVehicles", c, c.Tag, "test", new List<Action>());
            var fleet = Get<VehicleFleet>(c.Tag!, "Fleet");
            var stream = Get<MatchVehicleStream>(c.Tag!, "StreamedVehicles");
            while (stream.ShouldRestream(Vector3.Zero, VehicleStreamOptions.Default))
                stream.PlanRestream(Vector3.Zero, fleet, VehicleStreamOptions.Default);
            Assert.True(stream.DiscExhausted);
        }

        // This test enters through OnMessage; the combat fixture otherwise invokes private arms.
        Set(caller.Tag!, "Authenticated", true);
        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        packet.WriteByte(ZoneOpcodes.CommandBase);
        packet.WriteUInt16(0x42);
        packet.WriteUInt32(CommandHash.Compute("spawnvehicle"));
        packet.WriteString("atv");
        f.Service.OnMessage(caller, packet.Written.ToArray());

        var car = Assert.Single(Get<VehicleFleet>(caller.Tag!, "Fleet").Vehicles, v => v.AnchorInstanceId == 0);
        var peerStream = Get<MatchVehicleStream>(peer.Tag!, "StreamedVehicles");
        Assert.True(peerStream.ShouldRestream(Vector3.Zero, VehicleStreamOptions.Default));
        Assert.False(Get<MatchVehicleStream>(separate.Tag!, "StreamedVehicles")
            .ShouldRestream(Vector3.Zero, VehicleStreamOptions.Default));
        int before = f.Recorder.Routed.Count;
        var burst = new List<Action>();
        Call(f.Service, "PlanVehicleRestream", peer, peer.Tag, burst);
        foreach (Action action in burst) action();
        Assert.True(peerStream.IsStreamed(car.Guid));
        Assert.Contains(f.Recorder.Routed.Skip(before), sent => ReferenceEquals(peer, sent.Connection)
            && sent.Packet[1] == ZoneOpcodes.AddLightweightVehicle
            && BitConverter.ToUInt64(sent.Packet, 2) == car.Guid);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(before), sent => ReferenceEquals(separate, sent.Connection));
    }
}
