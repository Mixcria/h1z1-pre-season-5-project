using System.Net;
using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Destructibles;
using Cranberry.Zone.Match;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;
using Cranberry.Tests.Zone.World;

namespace Cranberry.Tests.Zone.Destructibles;

public sealed class DestructibleIntegrationTests
{
    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
    private sealed class Recorder : IPacketRecorder
    {
        public List<(SoeConnection Connection, byte[] Bytes)> Sent { get; } = [];
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add((connection, bytes.ToArray())); }
    }
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Call(ZoneService service, string name, params object[] args) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args);

    [Theory]
    [InlineData(null)] // Existing shooting path.
    [InlineData("City_Structures_Buildings_Int_WallHoleBlocked01.adr")]
    [InlineData("City_Props_TrafficSigns_StopSigns.adr")]
    [InlineData("Residential_Props_MunicipalGarbageCans.adr")]
    [InlineData("Farm_Props_CorralFence_Fence01.adr")]
    public void GatewayEnablesReportsSharesDestructionOnlyWithinMatchAndRestoresLateJoiner(string? vehicleModel)
    {
        var log = new SilentLog();
        var recorder = new Recorder();
        var service = new ZoneService(log, recorder, new GatewayTicketRegistry());
        var connections = new List<SoeConnection>();
        bool vehicleImpact = vehicleModel is not null && !vehicleModel.Contains("WallHoleBlocked");
        var prop = vehicleModel is null
            ? DestructibleCatalog.Default.Props.Values.First(p => p.Health == 5000)
            : (vehicleImpact ? VehicleFencePolicy.Catalog : DestructibleCatalog.Default)
                .Props.Values.First(p => p.Model == vehicleModel);
        uint requiredBullets = prop.IsWallHoleBlocker ? 2u : 5u;
        ulong sourceGuid = 0;
        SoeConnection Add(ulong guid, ulong match)
        {
            var request = new SessionRequest(3, (uint)guid, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 15000 + (int)guid), in request,
                new(), SessionDecision.Clear, service, log, (_, _) => { }, 0);
            service.OnConnected(connection);
            connections.Add(connection);
            Set(connection.Tag!, "Guid", guid);
            Set(connection.Tag!, "Authenticated", true);
            Set(connection.Tag!, "BountyAdmission", new MatchAdmissionContext(match, MatchQueueKind.Public, MatchMode.Solo));
            service.ForTest(connection).EnterMatch();
            Call(service, "JoinSharedLoot", connection, connection.Tag!);
            Get<SessionMovementState>(connection.Tag!, "Movement").ApplyPlayer(
                ClientMovementUpdate.Parse(MovementRecord.Position(prop.Position)));
            Deliver(connection, [ZoneOpcodes.ClientFinishedLoading]);
            return connection;
        }
        void Deliver(SoeConnection connection, byte[] packet)
        {
            byte header = new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte();
            service.OnMessage(connection, [header, .. packet]);
        }
        byte[] Report(uint id)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(0xba); writer.WriteUInt16(1); writer.WriteUInt32(prop.ObjectId);
            writer.WriteString(prop.Model); writer.WriteUInt32(id); writer.WriteUInt64(sourceGuid);
            return writer.Written.ToArray();
        }
        List<(SoeConnection Connection, byte[] Bytes)> Dto(ushort sub) => recorder.Sent.Where(p =>
            p.Bytes.Length >= 4 && p.Bytes[1] == 0xba && BitConverter.ToUInt16(p.Bytes, 2) == sub).ToList();

        try
        {
            var shooter = Add(1, 10);
            var peer = Add(2, 10);
            var unrelated = Add(3, 20);
            Assert.Equal(3, Dto(3).Count);
            Deliver(shooter, [ZoneOpcodes.ClientFinishedLoading]);
            Assert.Equal(3, Dto(3).Count); // repeated milestone does not reset DTO registration
            if (vehicleImpact)
            {
                // Both client entry paths must be armed: streamed model id and already-loaded actor id.
                var policy = Assert.Single(Dto(6), p => p.Connection == shooter).Bytes;
                Assert.Contains(VehicleFencePolicy.ModelIds[prop.Model], FirstDtoArray(policy));
                Assert.Contains(prop.ObjectId, Dto(5).Where(p => p.Connection == shooter)
                    .SelectMany(p => FirstDtoArray(p.Bytes)));
                var roster = VehicleRoster.LoadDefault();
                var fleet = new VehicleFleet(roster);
                var car = new MatchVehicle(2000, 3000, roster.Require(1), prop.Position, 0, 100000, 10000);
                fleet.Add(car);
                long now = Environment.TickCount64;
                Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, 1, 0, now - 2000, out _, out _));
                Assert.True(fleet.TryApplyOwnerPose(car.TransientId, 1, prop.Position, 0, now, out _));
                Set(shooter.Tag!, "Fleet", fleet);
                Set(peer.Tag!, "Fleet", fleet);
                sourceGuid = car.Guid;
                Deliver(peer, Report(0)); // A nearby passenger/viewer cannot report another driver's impact.
                Assert.Empty(Dto(2));
                Deliver(shooter, Report(0));
            }
            else
            {
                var combat = Get<SessionCombat>(shooter.Tag!, "Combat").Shooter;
                var point = prop.Position;
                long now = Environment.TickCount64;
                Assert.Equal(FireVerdict.Accepted, combat.Fire(new(2425, point.X, point.Y, point.Z, [1]),
                    2425, now - 2000, CombatOptions.Default).Verdict);
                Deliver(unrelated, Report(1)); // Another match has no accepted shot to spend.
                Deliver(shooter, Report(2)); // A report before its accepted shot cannot pay damage.
                Assert.Empty(Dto(2));
                Deliver(shooter, Report(1));
                Assert.Empty(Dto(2));
                Deliver(shooter, Report(1)); // duplicate must not break the fence
                Assert.Empty(Dto(2));
                for (uint bullet = 2; bullet <= requiredBullets; bullet++)
                {
                    Assert.Equal(FireVerdict.Accepted, combat.Fire(new(2425, point.X, point.Y, point.Z, [bullet]),
                        2425, now - 2500 + bullet * 500, CombatOptions.Default).Verdict);
                    Deliver(shooter, Report(bullet));
                    if (bullet < requiredBullets) Assert.Empty(Dto(2));
                }
            }
            Assert.Equal(2, Dto(2).Count);
            Assert.Contains(Dto(2), p => p.Connection == shooter);
            Assert.Contains(Dto(2), p => p.Connection == peer);
            Assert.DoesNotContain(Dto(2), p => p.Connection == unrelated);
            Deliver(shooter, Report(vehicleImpact ? 0u : requiredBullets));
            Assert.Equal(2, Dto(2).Count);
            var late = Add(4, 10);
            var snapshot = Assert.Single(Dto(3), p => p.Connection == late).Bytes;
            // gateway(1), ba(1), sub(2), header object(4), replacement count(4)
            Assert.Equal(1u, BitConverter.ToUInt32(snapshot, 8));
            Assert.Equal(prop.ObjectId, BitConverter.ToUInt32(snapshot, 12));
            var fresh = Add(5, 30);
            Assert.Equal(0u, BitConverter.ToUInt32(Assert.Single(Dto(3), p => p.Connection == fresh).Bytes, 8));
        }
        finally
        {
            foreach (var connection in connections)
            { connection.Disconnect(); service.OnDisconnected(connection, DisconnectCause.ServerRequested); }
        }
    }

    [Theory]
    [InlineData(2u, 110755u, 200, false, "")]
    [InlineData(2u, 110755u, 200, true, "")]
    [InlineData(3u, 110756u, 400, false, "")]
    [InlineData(3u, 110756u, 400, true, "")]
    [InlineData(4u, 110757u, 450, false, "")]
    [InlineData(4u, 110757u, 450, true, "")]
    [InlineData(5u, 110758u, 400, false, "")]
    [InlineData(90124u, 110759u, 350, false, "")]
    [InlineData(2u, 110755u, 200, false, "world")]
    [InlineData(2u, 110755u, 200, false, "dead")]
    [InlineData(2u, 110755u, 200, false, "moved-away")]
    [InlineData(2u, 110755u, 200, false, "wrong-source")]
    [InlineData(2u, 110755u, 200, false, "no-animation")]
    [InlineData(2u, 110755u, 200, false, "", true)]
    [InlineData(3u, 110756u, 400, true, "", true)]
    [InlineData(90124u, 110759u, 350, false, "", true)]
    [InlineData(2u, 110755u, 200, false, "look-away", true)]
    [InlineData(2u, 110755u, 200, false, "world", true)]
    [InlineData(2u, 110755u, 200, false, "dead", true)]
    public void NativePunchWaitsForAnimationContactThenSharesThePaneWithTheMatchAndLateJoiners(
        uint effectId, uint serverEffectId, int windupMs, bool animationFirst, string invalidation, bool roof = false)
    {
        using var data = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Data/Destructibles/z2-glass-panes.json")));
        var row = data.RootElement.GetProperty("panes").EnumerateArray().First(p => roof
            ? MathF.Abs(p[5].GetSingle()) > 0.99f
            : MathF.Abs(p[5].GetSingle()) < 0.0001f && p[8].GetSingle() > 0.99f);
        uint objectId = row[0].GetUInt32();
        var normal = new Vector3(row[4].GetSingle(), row[5].GetSingle(), row[6].GetSingle());
        var center = new Vector3(row[1].GetSingle(), row[2].GetSingle(), row[3].GetSingle());
        var position = roof ? center + Vector3.UnitY * 0.02f
            : center + normal - Vector3.UnitY * GlassMeleeCatalog.StrikeHeight;
        float yaw = MathF.Atan2(-normal.X, -normal.Z);
        var log = new SilentLog();
        var recorder = new Recorder();
        var service = new ZoneService(log, recorder, new GatewayTicketRegistry());
        service.Post = _ => { }; // Advance the production contact callback explicitly.
        var connections = new List<SoeConnection>();
        void Deliver(SoeConnection connection, byte[] bytes) => service.OnMessage(connection,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte(), .. bytes]);
        SoeConnection Add(ulong guid, ulong match)
        {
            var request = new SessionRequest(3, (uint)guid, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(new(IPAddress.Loopback, 17000 + (int)guid), in request,
                new(), SessionDecision.Clear, service, log, (_, _) => { }, 0);
            service.OnConnected(connection);
            connections.Add(connection);
            Set(connection.Tag!, "Guid", guid);
            Set(connection.Tag!, "Authenticated", true);
            Set(connection.Tag!, "BountyAdmission", new MatchAdmissionContext(match, MatchQueueKind.Public, MatchMode.Solo));
            service.ForTest(connection).EnterMatch();
            Call(service, "JoinSharedLoot", connection, connection.Tag!);
            using var pose = new PacketWriter();
            PositionUpdateBlock.AtRest(position, yaw).WriteTo(pose);
            Get<SessionMovementState>(connection.Tag!, "Movement").ApplyPlayer(ClientMovementUpdate.Parse(pose.Written));
            if (roof) Aim(connection, -1.57f);
            Deliver(connection, [ZoneOpcodes.ClientFinishedLoading]);
            return connection;
        }
        void Aim(SoeConnection connection, float pitch)
        {
            using var aim = new PacketWriter();
            aim.WriteUInt16((ushort)MovementFieldMask.Rotation);
            aim.WriteUInt32(101);
            aim.WriteByte(0);
            ClientPackedInt.Write(aim, (int)(yaw * 100));
            ClientPackedInt.Write(aim, (int)(pitch * 100));
            ClientPackedInt.Write(aim, 0);
            ClientPackedInt.Write(aim, 0);
            Get<SessionMovementState>(connection.Tag!, "Movement").ApplyPlayer(ClientMovementUpdate.Parse(aim.Written));
        }
        try
        {
            var puncher = Add(1, 10);
            var viewer = Add(2, 10);
            var other = Add(3, 20);
            using var punch = new PacketWriter();
            punch.WriteByte(0x82); punch.WriteUInt32(100); punch.WriteByte(1);
            punch.WriteUInt64(1); punch.WriteByte(1); punch.WriteByte(0);
            // Actual August 9e/01 RequestAnimation shape; the same packet family as MotorRun.
            byte[] animation = Convert.FromHexString("9E0101000000905F0100B7860100000000002110000000000000010000000000000000000000110000000000004600000000000000000000000000000000000000000000000001");
            BinaryPrimitives.WriteUInt32LittleEndian(animation.AsSpan(6), effectId);
            BinaryPrimitives.WriteUInt32LittleEndian(animation.AsSpan(10), serverEffectId);
            BinaryPrimitives.WriteUInt64LittleEndian(animation.AsSpan(18), invalidation == "wrong-source" ? 999ul : 1ul);
            BinaryPrimitives.WriteUInt64LittleEndian(animation.AsSpan(38), 1);
            int DestructionCount() => recorder.Sent.Count(p => p.Bytes.Length > 8 && p.Bytes[1] == 0xba
                && BitConverter.ToUInt16(p.Bytes, 2) == 2);
            if (animationFirst) Deliver(puncher, animation);
            Deliver(puncher, punch.Written.ToArray());
            object pending = Get<object>(puncher.Tag!, "PendingGlassSwing");
            Assert.Equal(0, DestructionCount()); // Trigger-down never breaks glass immediately.
            if (!animationFirst && invalidation != "no-animation") Deliver(puncher, animation);
            if (invalidation is "wrong-source" or "no-animation")
            {
                Assert.Null(Get<long?>(pending, "DueMs"));
                Call(service, "CompleteMeleeGlassHit", puncher, puncher.Tag!, pending, long.MaxValue);
                Assert.Equal(0, DestructionCount());
                return;
            }
            long due = Assert.IsType<long>(Get<long?>(pending, "DueMs"));
            object observedAnimation = Get<object>(puncher.Tag!, "LastPunchAnimation");
            Assert.Equal(windupMs, due - Get<long>(observedAnimation, "StartedMs"));
            Deliver(puncher, animation);
            Deliver(puncher, punch.Written.ToArray());
            Assert.Same(pending, Get<object>(puncher.Tag!, "PendingGlassSwing"));
            Assert.Equal(due, Get<long?>(pending, "DueMs"));
            Call(service, "CompleteMeleeGlassHit", puncher, puncher.Tag!, pending, due - 1);
            Assert.Equal(0, DestructionCount());
            if (invalidation == "world") Set(puncher.Tag!, "WorldGeneration", 999);
            if (invalidation == "dead") Set(puncher.Tag!, "DeathSent", true);
            if (invalidation == "look-away") Aim(puncher, 0);
            if (invalidation == "moved-away")
            {
                using var away = new PacketWriter();
                PositionUpdateBlock.AtRest(position + Vector3.UnitX * 20, yaw).WriteTo(away);
                Get<SessionMovementState>(puncher.Tag!, "Movement").ApplyPlayer(ClientMovementUpdate.Parse(away.Written));
            }
            Call(service, "CompleteMeleeGlassHit", puncher, puncher.Tag!, pending, due);
            if (invalidation.Length != 0)
            {
                Assert.Equal(0, DestructionCount());
                return;
            }
            var destroyed = recorder.Sent.Where(p => p.Bytes.Length > 8 && p.Bytes[1] == 0xba
                && BitConverter.ToUInt16(p.Bytes, 2) == 2).ToArray();
            Assert.Equal(2, destroyed.Length);
            Assert.Contains(destroyed, p => p.Connection == puncher);
            Assert.Contains(destroyed, p => p.Connection == viewer);
            Assert.DoesNotContain(destroyed, p => p.Connection == other);
            Assert.All(destroyed, p => Assert.Equal(objectId, BitConverter.ToUInt32(p.Bytes, 4)));
            Call(service, "CompleteMeleeGlassHit", puncher, puncher.Tag!, pending, due + 1);
            Deliver(puncher, punch.Written.ToArray());
            Assert.Equal(2, recorder.Sent.Count(p => p.Bytes.Length > 8 && p.Bytes[1] == 0xba
                && BitConverter.ToUInt16(p.Bytes, 2) == 2));
            var late = Add(4, 10);
            var snapshot = Assert.Single(recorder.Sent, p => p.Connection == late && p.Bytes.Length > 12
                && p.Bytes[1] == 0xba && BitConverter.ToUInt16(p.Bytes, 2) == 3).Bytes;
            Assert.Equal(1u, BitConverter.ToUInt32(snapshot, 8));
            Assert.Equal(objectId, BitConverter.ToUInt32(snapshot, 12));
        }
        finally
        {
            foreach (var connection in connections)
            { connection.Disconnect(); service.OnDisconnected(connection, DisconnectCause.ServerRequested); }
        }
    }

    private static uint[] FirstDtoArray(byte[] gatewayPacket)
    {
        var reader = new PacketReader(gatewayPacket);
        reader.Skip(8); // gateway, BA, sub-opcode, object header
        var ids = new uint[reader.ReadUInt32()];
        for (int i = 0; i < ids.Length; i++) ids[i] = reader.ReadUInt32();
        return ids;
    }
}
