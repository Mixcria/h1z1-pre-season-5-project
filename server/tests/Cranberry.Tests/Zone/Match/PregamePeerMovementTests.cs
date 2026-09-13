using System.Buffers.Binary;
using System.Diagnostics;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class BountyGatewayTests
{
    [Fact]
    public void NativeLookOnlyRelayDoesNotRestampAnOlderPositionOrVelocity()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var (first, second) = EnterNativePregamePeers(fixture, 5);
        byte[] look = [0, 2, 0x20, 0x19, 0xb4, 0x0e, 5, 0, 0, 0, 0];
        SendNativePeerMovement(fixture, first, look, 5);
        byte[] pose = fixture.Sent(second).Last(packet => packet.Length > 1
            && packet[1] == ZoneOpcodes.PlayerUpdatePosition);
        int start = 2 + 1 + (pose[2] & 3);
        Assert.Equal(look, pose[start..]);
        var movement = ClientMovementUpdate.Parse(pose.AsSpan(start));
        Assert.Null(movement.Position);
        Assert.Null(movement.HorizontalSpeed);
        Assert.Equal(MovementFieldMask.Rotation, movement.Fields);
        Assert.Equal(0, second.PendingLatestMessages);
    }

    [Fact]
    public void NativeLookInfoDoesNotBecomeThePeerSpawnQuaternion()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var connection = fixture.Connect("a"); fixture.Zone(connection);
        SendNativePeerMovement(fixture, connection, NativePregameFull, 5);
        var peer = fixture.Service.PeerRegistry.Find(0x1001)!;
        var before = peer.Rotation;
        Assert.InRange(before.Length(), .999f, 1.001f);
        // Captured ordinary lookInfo is all zero; it cannot be a valid spawn quaternion.
        Assert.NotEqual(default, before);
        // A look-only report has no body Euler fields. Four packed zero values at 0x200.
        SendNativePeerMovement(fixture, connection, [0, 2, 1, 0, 0, 0, 5, 0, 0, 0, 0], 5);
        Assert.Equal(before, peer.Rotation);
    }
    // Native client capture wire-20260909-182558.txt, channel 2 records at lines 429 and 693.
    // The full version-5 record arrives while Zoning, before peers become visible. Later lobby
    // movement is partial. Payloads contain movement only; no identity, endpoint or ticket.
    private static readonly byte[] NativePregameFull = Convert.FromHexString("FF1F59F8B30E050511C50003D42D0645E33B3333B3BF000000000000000063040000000000000000DCF5016500010000");
    private static readonly byte[] NativePregamePartial = Convert.FromHexString("FE010119B40E05C50003442D0645E33BCD11B3BF0000000000000000");

    private static void SendNativePeerMovement(Fixture fixture, SoeConnection connection,
        byte[] captured, byte version)
    {
        var packet = new byte[captured.Length + 1];
        packet[0] = new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 2).ToByte();
        captured.CopyTo(packet, 1);
        packet[7] = version;
        fixture.Service.OnMessage(connection, packet);
    }

    private static (SoeConnection First, SoeConnection Second) EnterNativePregamePeers(Fixture fixture,
        byte version)
    {
        var first = fixture.Connect("a");
        var second = fixture.Connect("b");
        foreach (var connection in new[] { first, second })
        {
            fixture.Zone(connection);
            SendNativePeerMovement(fixture, connection, NativePregameFull, version);
            fixture.Ready(connection);
        }
        // A loading callback may have already run the viewer's 250 ms interest sweep.
        // Wait for the next real sweep instead of assuming every input bypasses its throttle.
        fixture.Pump(() =>
        {
            RelayNativePregamePeers(fixture, first, second, version);
            return fixture.Service.PeerRegistry.Find(0x1001)!.View.KnownCount == 1
                && fixture.Service.PeerRegistry.Find(0x1002)!.View.KnownCount == 1;
        });
        return (first, second);
    }

    private static void RelayNativePregamePeers(Fixture fixture, SoeConnection first,
        SoeConnection second, byte version)
    {
        // Every viewer drives its own interest pass. One more first-player pose follows the
        // second viewer's enter, then commit latest poses through the actual transport sink.
        SendNativePeerMovement(fixture, first, NativePregamePartial, version);
        SendNativePeerMovement(fixture, second, NativePregamePartial, version);
        SendNativePeerMovement(fixture, first, NativePregamePartial, version);
        first.FlushLatest(0x1002);
        second.FlushLatest(0x1001);
    }

    [Theory]
    [InlineData((byte)5)] // Captured August pregame version, not a server-selected constant.
    [InlineData((byte)0)] // Released match version must retain its existing zero byte.
    [InlineData((byte)17)] // The packet carries a version byte; do not hard-code known phases.
    public void PregamePeerSpawnSeedsTheVersionOfItsFirstRelayedNativeMovement(byte version)
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var (first, second) = EnterNativePregamePeers(fixture, version);
        foreach (var viewer in new[] { first, second })
        {
            var sent = fixture.Sent(viewer);
            byte[] spawn = Assert.Single(sent, packet => packet.Length > 1
                && packet[1] == ZoneOpcodes.AddLightweightPc);
            byte[] pose = sent.Last(packet => packet.Length > 1
                && packet[1] == ZoneOpcodes.PlayerUpdatePosition);
            int transientLength = 1 + (pose[2] & 3);
            var movement = ClientMovementUpdate.Parse(pose.AsSpan(2 + transientLength));
            Assert.NotEqual((ushort)0x1fff, (ushort)movement.Fields);
            Assert.Equal(version, movement.State);
            // d5's fixed tail starts at rec+0x178: 1 + 4 + 4 + 8 + 4 + 1 bytes.
            // FUN_140af2ba0 sets actor+0x5a0 from this byte and constructs a 0x1fff baseline;
            // FUN_140b14d30 rejects partial motion whose version differs from that baseline.
            Assert.Equal(movement.State, spawn[^22]);
            Assert.True(Array.IndexOf(sent, spawn) < Array.IndexOf(sent, pose));

        }
    }

    [Fact]
    public void DepartedWorldClearsViewerIdsBeforeTheSamePeersEnterAnotherPregame()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var (first, second) = EnterNativePregamePeers(fixture, 5);
        var firstPeer = fixture.Service.PeerRegistry.Find(0x1001)!;
        var secondPeer = fixture.Service.PeerRegistry.Find(0x1002)!;
        Assert.Equal(1, firstPeer.View.KnownCount);
        Assert.Equal(1, secondPeer.View.KnownCount);
        // Simulate the existing actors' later version-0 match stream before leaving.
        SendNativePeerMovement(fixture, first, NativePregameFull, 0);
        SendNativePeerMovement(fixture, second, NativePregameFull, 0);
        fixture.Cancel(first);
        Assert.Equal(0, firstPeer.View.KnownCount);
        Assert.False(firstPeer.HasPose);
        Assert.Equal(1, secondPeer.View.KnownCount); // Other viewers retain ordered sweep/despawn.
        fixture.Cancel(second);
        Assert.Equal(0, secondPeer.View.KnownCount);
        Assert.False(secondPeer.HasPose);
        Assert.False(firstPeer.View.Transients.TryGet(secondPeer.Key, out _));
        Assert.False(secondPeer.View.Transients.TryGet(firstPeer.Key, out _));

        // LoginZone can report its own version-0 pose after departure. That menu actor's
        // position must not establish the next Z2 actor's baseline or visibility readiness.
        SendNativePeerMovement(fixture, first, NativePregameFull, 0);
        SendNativePeerMovement(fixture, second, NativePregameFull, 0);
        fixture.Zone(first); fixture.Ready(first);
        fixture.Zone(second); fixture.Ready(second);
        Assert.False(firstPeer.HasPose);
        Assert.False(secondPeer.HasPose);
        SendNativePeerMovement(fixture, first, NativePregamePartial, 5);
        // A sparse/header-only packet supplies a fresh version but no new-world position.
        SendNativePeerMovement(fixture, second, new byte[7], 5);
        Assert.False(secondPeer.HasPose);
        foreach (var viewer in new[] { first, second })
            Assert.Single(fixture.Sent(viewer), packet => packet.Length > 1
                && packet[1] == ZoneOpcodes.AddLightweightPc);
        var interval = Stopwatch.StartNew();
        fixture.Pump(() => interval.ElapsedMilliseconds >= 300);
        RelayNativePregamePeers(fixture, first, second, 5);
        // Let the first viewer run its next interest pass after the second has a fresh position.
        interval.Restart();
        fixture.Pump(() => interval.ElapsedMilliseconds >= 300);
        RelayNativePregamePeers(fixture, first, second, 5);
        foreach (var viewer in new[] { first, second })
        {
            var spawns = fixture.Sent(viewer).Where(packet => packet.Length > 1
                && packet[1] == ZoneOpcodes.AddLightweightPc).ToArray();
            Assert.Equal(2, spawns.Length);
            Assert.Equal(5, spawns[^1][^22]);
        }
    }

    [Fact]
    public void RemainingViewerDespawnsDepartedPeerAndCancelsItsPendingPose()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var (first, second) = EnterNativePregamePeers(fixture, 5);
        SendNativePeerMovement(fixture, first, NativePregamePartial, 5);
        fixture.Cancel(first);
        var interval = Stopwatch.StartNew();
        fixture.Pump(() => interval.ElapsedMilliseconds >= 300);
        SendNativePeerMovement(fixture, second, NativePregamePartial, 5);
        byte[] remove = Assert.Single(fixture.Sent(second), packet => packet.Length == 13
            && packet[1] == 0x0f && packet[2] == 0x01
            && BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(3)) == 0x1001);
        Assert.Equal(0, fixture.Service.PeerRegistry.Find(0x1002)!.View.KnownCount);
        int offersBeforeFlush = fixture.Sent(second).Count(packet => packet.Length > 1
            && packet[1] == ZoneOpcodes.PlayerUpdatePosition);
        second.FlushLatest(0x1001);
        Assert.Equal(offersBeforeFlush, fixture.Sent(second).Count(packet => packet.Length > 1
            && packet[1] == ZoneOpcodes.PlayerUpdatePosition));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(remove.AsSpan(11)));
    }
}
