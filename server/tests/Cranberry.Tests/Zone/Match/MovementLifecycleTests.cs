using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;
using Cranberry.Tests.Zone.World;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class BountyGatewayTests
{
    [Fact]
    public void MovementLaneWorldDepartureClearsThePeerTransformAndFlags()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var connection = fixture.Connect();
        fixture.Zone(connection);
        SendNativePeerMovement(fixture, connection, NativePregameFull, 5);
        fixture.Ready(connection);
        var peer = fixture.Service.PeerRegistry.Find(0x1001)!;
        Assert.NotEqual(Vector3.Zero, peer.Position);
        Assert.NotEqual(0u, peer.Posture);
        Assert.NotEqual(new Vector4(0, 0, 0, 1), peer.Rotation);

        fixture.Cancel(connection);

        Assert.False(peer.IsReplicable);
        Assert.True(peer.Pose.IsEmpty);
        Assert.False(peer.HasCompleteRelaySnapshot);
        Assert.Equal(Vector3.Zero, peer.Position);
        Assert.Equal(new Vector4(0, 0, 0, 1), peer.Rotation);
        Assert.Equal(0f, peer.Heading);
        Assert.Equal(0u, peer.Posture);
    }

    [Fact]
    public void MovementLaneZoningDoesNotMergeMenuBodyOrFlagsIntoAFreshSparsePosition()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var connection = fixture.Connect();
        fixture.Zone(connection);
        SendNativePeerMovement(fixture, connection, NativePregameFull, 5);
        fixture.Ready(connection);
        fixture.Cancel(connection);
        // LoginZone has a separate actor and can send movement after world departure.
        SendNativePeerMovement(fixture, connection, NativePregameFull, 0);

        fixture.Zone(connection);
        var peer = fixture.Service.PeerRegistry.Find(0x1001)!;
        byte[] look = [0, 2, 10, 0, 0, 0, 5, 0, 0, 0, 0];
        SendNativePeerMovement(fixture, connection, look, 5);
        fixture.Ready(connection);
        Assert.False(peer.HasPose);

        var position = new Vector3(125, 250, -375);
        byte[] fresh = MovementRecord.Position(position, clientTime: 20, state: 5);
        SendNativePeerMovement(fixture, connection, fresh, 5);

        Assert.True(peer.IsReplicable);
        Assert.Equal(position, peer.Position);
        Assert.Equal(fresh, peer.Pose.ToArray());
        Assert.Equal(new Vector4(0, 0, 0, 1), peer.Rotation);
        Assert.Equal(0f, peer.Heading);
        Assert.Equal(0u, peer.Posture);
        List<byte[]> burst = [];
        PeerBurst.Enter(peer, 16, burst);
        Assert.Equal(5, burst[0][^22]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovementLaneSparseStartStopTurnAndJumpPreserveBothViews(bool reverse)
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var (first, second) = EnterNativePregamePeers(fixture, 5);
        var sender = reverse ? second : first;
        var receiver = reverse ? first : second;
        var peer = fixture.Service.PeerRegistry.Find(reverse ? 0x1002ul : 0x1001ul)!;
        var start = peer.Position;
        var bodyBefore = peer.Rotation;
        // Synthetic input sequence using August fields/observed flags, not a native animation test.
        // The jump is represented by airborne flags and rising/falling positions, not an invented
        // jump-animation bit. Start/stop also exercise native inputHeading/inputSpeed (0x400/800).
        byte[][] samples =
        [
            MovementLaneDelta(0x0c11, 100, w =>
            {
                ClientVarInt.Write(w, 0x0401);
                ClientPackedInt.Write(w, 55); // velocity magnitude 5.5
                ClientPackedInt.Write(w, 7);  // input heading 0.7
                ClientPackedInt.Write(w, 10); // input speed 1.0
            }),
            MovementRecord.Position(start + new Vector3(1, 0, 0), 110, 5),
            MovementLaneDelta(0x0811, 120, w =>
            {
                ClientVarInt.Write(w, 0x0441); // STOP, grounded
                ClientPackedInt.Write(w, 0);
                ClientPackedInt.Write(w, 0);
            }),
            MovementLaneDelta(0x0200, 130, w =>
            {
                foreach (int component in new[] { 25, -50, 75, 100 }) ClientPackedInt.Write(w, component);
            }),
            MovementLaneDelta(0x00e0, 140, w =>
            {
                w.WriteSingle(1.25f);
                ClientPackedInt.Write(w, 20);
                ClientPackedInt.Write(w, -30);
            }),
            MovementLaneDelta(0x0001, 150, w => ClientVarInt.Write(w, 0x0021)), // airborne
            MovementRecord.Position(start + new Vector3(1, 1, 0), 160, 5),
            MovementRecord.Position(start + new Vector3(1, 0, 0), 170, 5),
            MovementLaneDelta(0x0001, 180, w => ClientVarInt.Write(w, 0x0441)), // grounded stop
        ];
        int before = MovementLanePoses(fixture, receiver).Length;
        int selfBefore = MovementLanePoses(fixture, sender).Length;
        for (int i = 0; i < samples.Length; i++)
        {
            SendNativePeerMovement(fixture, sender, samples[i], 5);
            Assert.Equal(samples[i], MovementLanePoses(fixture, receiver)[^1]);
            if (i == 3) Assert.Equal(bodyBefore, peer.Rotation); // look is independent of body
            if (i == 4)
            {
                var rotation = Quaternion.CreateFromYawPitchRoll(1.25f, .2f, -.3f);
                Assert.Equal(new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W), peer.Rotation);
                Assert.Equal(start + new Vector3(1, 0, 0), peer.Position);
            }
            if (i == 5) Assert.Equal(0x0021u, peer.Posture);
        }
        Assert.Equal(before + samples.Length, MovementLanePoses(fixture, receiver).Length);
        Assert.Equal(selfBefore, MovementLanePoses(fixture, sender).Length);
        Assert.Equal(0x0441u, peer.Posture);
        Assert.Equal(0, receiver.PendingLatestMessages);
    }

    [Fact]
    public void MovementLaneTimestampWrapAndPreciseBarriersKeepExactRecords()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var (first, second) = EnterNativePregamePeers(fixture, 5);
        var peer = fixture.Service.PeerRegistry.Find(0x1001)!;
        var at = peer.Position;
        byte[] precise = MovementLaneDelta(0x1000, uint.MaxValue - 1, w =>
        {
            foreach (float component in new[] { at.X, at.Y, at.Z, 0, 0, 0, 1 })
                ClientPackedInt.Write(w, (int)MathF.Round(component * 100));
        });
        byte[][] samples =
        [
            MovementRecord.Position(at, uint.MaxValue - 2, 5),
            precise,
            MovementLaneDelta(0x0200, uint.MaxValue, w => w.WriteRaw(new byte[4])),
            MovementLaneDelta(0, 0, _ => { }),
            MovementRecord.Position(at + new Vector3(1, 0, 0), 1, 5),
        ];
        foreach (byte[] sample in samples)
        {
            SendNativePeerMovement(fixture, first, sample, 5);
            Assert.Equal(sample, MovementLanePoses(fixture, second)[^1]);
            if (ReferenceEquals(sample, precise)) Assert.False(peer.HasCompleteRelaySnapshot);
        }
        Assert.Equal(1u, ClientMovementUpdate.Parse(peer.Pose).ClientTime);
        Assert.Equal(at + new Vector3(1, 0, 0), peer.Position);
    }

    [Fact]
    public void MovementLaneVersionRepliesUseObservedVersionsAndRespectActorScope()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var (first, second) = EnterNativePregamePeers(fixture, 5);
        var subject = fixture.Service.PeerRegistry.Find(0x1001)!;
        var viewer = fixture.Service.PeerRegistry.Find(0x1002)!;
        void Request(ulong guid) => fixture.Send(second, w =>
        {
            w.WriteByte(0x0f); w.WriteByte(0x57); w.WriteUInt64(guid);
        });
        byte[][] Replies() => fixture.Sent(second).Where(p => Is(p, 0x0f, 0x56)).ToArray();

        // Native version changes normally cause full records. Also exercise a sparse version
        // mismatch: recovery changes only actor+0x5a0 through 0f56, never the movement payload.
        SendNativePeerMovement(fixture, first, NativePregameFull, 0);
        Request(subject.CharacterGuid);
        byte[] changed = MovementLaneDelta(0, 42, _ => { }, version: 17);
        SendNativePeerMovement(fixture, first, changed, 17);
        Request(subject.CharacterGuid);
        Request(subject.CharacterGuid); // same-version immediate duplicate is rate limited
        Assert.Equal(new byte[] { 0, 17 }, Replies().Select(p => p[^1]).ToArray());
        Assert.All(Replies(), p =>
        {
            Assert.Equal(12, p.Length);
            Assert.Equal(subject.CharacterGuid, BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(3)));
        });
        Assert.Equal(changed, MovementLanePoses(fixture, second)[^1]);
        Request(viewer.CharacterGuid);
        Request(ulong.MaxValue);
        viewer.MovementVersionReplies.Clear(); // scope checks must not pass only through throttling
        subject.MatchId++;
        Request(subject.CharacterGuid);
        Assert.Equal(2, Replies().Length);
        subject.MatchId = viewer.MatchId;
        subject.ParachuteGuid = 0x2001;
        Request(subject.CharacterGuid);
        Assert.Equal(2, Replies().Length);
        subject.ParachuteGuid = 0;
        viewer.View.MarkForgotten(subject.Key);
        Request(subject.CharacterGuid);
        Assert.Equal(2, Replies().Length);
        Assert.DoesNotContain(fixture.Sent(first), p => Is(p, 0x0f, 0x56));
    }

    [Fact]
    public void MovementLaneInterestReentrySeedsCurrentBodyAndVersionBeforeSparseRelay()
    {
        using var fixture = new Fixture(countdown: 60000, lobby: ArrivalLobby);
        var (first, second) = EnterNativePregamePeers(fixture, 5);
        var subject = fixture.Service.PeerRegistry.Find(0x1001)!;
        var viewer = fixture.Service.PeerRegistry.Find(0x1002)!;
        var origin = viewer.Position;
        SendNativePeerMovement(fixture, first,
            MovementRecord.Position(origin + new Vector3(ObserverView.PlayerLeaveMetres + 100, 0, 0), 100, 5), 5);
        // Idle interest callbacks must remove the mover from the stationary viewer too.
        fixture.Pump(() => !viewer.View.Knows(subject.Key) && !subject.View.Knows(viewer.Key));
        Assert.Contains(fixture.Sent(second), p => Is(p, 0x0f, 0x01));
        Assert.False(viewer.View.Transients.TryGet(subject.Key, out _));
        int before = MovementLanePoses(fixture, second).Length;
        SendNativePeerMovement(fixture, first,
            MovementLaneDelta(0x20, 200, w => w.WriteSingle(.75f), version: 17), 17);
        Assert.Equal(before, MovementLanePoses(fixture, second).Length);
        SendNativePeerMovement(fixture, first, MovementRecord.Position(origin, 210, 17), 17);
        fixture.Pump(() => viewer.View.Knows(subject.Key) && subject.View.Knows(viewer.Key));

        byte[][] sent = fixture.Sent(second);
        var spawns = sent.Where(p => p.Length > 1 && p[1] == ZoneOpcodes.AddLightweightPc).ToArray();
        Assert.Equal(2, spawns.Length);
        byte[] latestSpawn = spawns[^1];
        Assert.Equal(17, latestSpawn[^22]);
        // d5's fixed tail: position xyz, rotation xyzw, then 42 bytes (FUN_140af2ba0).
        var pose = new PacketReader(latestSpawn.AsSpan(latestSpawn.Length - 70));
        Assert.Equal(origin, new Vector3(pose.ReadSingle(), pose.ReadSingle(), pose.ReadSingle()));
        var rotation = new Vector4(pose.ReadSingle(), pose.ReadSingle(), pose.ReadSingle(), pose.ReadSingle());
        Assert.Equal(subject.Rotation, rotation);
        byte[] sparse = MovementLaneDelta(0x0200, 220, w => w.WriteRaw(new byte[4]), version: 17);
        SendNativePeerMovement(fixture, first, sparse, 17);
        Assert.Equal(sparse, MovementLanePoses(fixture, second)[^1]);
        Assert.Equal(0, second.PendingLatestMessages);
    }

    private static byte[] MovementLaneDelta(ushort mask, uint time, Action<PacketWriter> fields, byte version = 5)
    {
        using var writer = new PacketWriter();
        writer.WriteUInt16(mask); writer.WriteUInt32(time); writer.WriteByte(version);
        fields(writer);
        return writer.Written.ToArray();
    }

    private static byte[][] MovementLanePoses(Fixture fixture, SoeConnection viewer) =>
        fixture.Sent(viewer).Where(p => p.Length > 2 && p[1] == ZoneOpcodes.PlayerUpdatePosition)
            .Select(p => p[(3 + (p[2] & 3))..]).ToArray();
}
