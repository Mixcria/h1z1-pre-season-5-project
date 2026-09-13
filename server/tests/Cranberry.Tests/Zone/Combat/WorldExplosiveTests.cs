using System.Net;
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

namespace Cranberry.Tests.Zone.Combat;

public sealed class WorldExplosiveTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShotExplosiveDamagesOnlyItsMatchOnceAndRemovesTheObject(bool barrel)
    {
        using var world = new Fixture();
        var prop = DestructibleCatalog.Default.Props.Values.First(p => p.IsExplosive);
        Vector3 point = prop.Position;
        var shooter = world.Add(1, 10, point + new Vector3(10, 0, 0));
        var nearby = world.Add(2, 10, point + new Vector3(2, 0, 0));
        var otherMatch = world.Add(3, 20, point + new Vector3(2, 0, 0));
        GroundLootItem? can = null;
        if (!barrel)
        {
            can = (GroundLootItem)world.Call("SpawnGroundLoot", shooter, shooter.Tag!, 73u, 1u,
                point, 1u, 0u, null!, 0u, null!)!;
            world.Call("SharePlayerDrop", shooter, shooter.Tag!, can);
            world.DrainDrops(nearby);
            Assert.Single(Get<LootWorld>(nearby, "Loot").Items);
            var spawn = world.Sent.First(p => p.Link == shooter && p.Packet[0] == 0xd6).Packet;
            var shape = new AddLightweightItem(can.WorldGuid, can.TransientId, 1, point);
            Assert.Equal(LightweightEntityBody.CollidableFlag, spawn[shape.Body.SpawnFlags1Offset]);
        }
        else
        {
            world.Call("InitializeDestructibles", shooter, shooter.Tag!);
            world.Call("InitializeDestructibles", nearby, nearby.Tag!);
            world.Call("InitializeDestructibles", otherMatch, otherMatch.Tag!);
        }

        void Shoot(uint projectile)
        {
            var combat = Get<SessionCombat>(shooter, "Combat");
            Vector3 muzzle = point + new Vector3(10, 0, 0);
            Assert.Equal(FireVerdict.Accepted, combat.Shooter.Fire(
                new WeaponFire(42, muzzle.X, muzzle.Y, muzzle.Z, [projectile]),
                2425, Environment.TickCount64 - 500 + projectile * 130, CombatOptions.Default).Verdict);
            byte[] report;
            if (barrel)
            {
                using var writer = new PacketWriter();
                writer.WriteByte(0xba); writer.WriteUInt16(1); writer.WriteUInt32(prop.ObjectId);
                writer.WriteString(prop.Model); writer.WriteUInt32(projectile); writer.WriteUInt64(0);
                report = writer.Written.ToArray();
            }
            else report = ShootingPacketBuilder.HitReport(projectile, can!.WorldGuid, "Body", point.X, point.Y, point.Z);
            world.Send(shooter, report);
        }

        Shoot(1);
        if (!barrel)
        {
            Assert.Equal(10000u, Get<uint>(nearby, "Hitpoints"));
            Shoot(2);
            Assert.Equal(10000u, Get<uint>(nearby, "Hitpoints"));
            Assert.Single(Get<LootWorld>(nearby, "Loot").Items);
            Shoot(3);
        }
        Assert.Equal(10000u, Get<uint>(shooter, "Hitpoints"));
        // The native movement record quantizes the map position before the distance check.
        uint afterBlast = Get<uint>(nearby, "Hitpoints");
        Assert.InRange(afterBlast, 4990u, 5010u);
        Assert.Equal(10000u, Get<uint>(otherMatch, "Hitpoints"));
        Shoot(barrel ? 2u : 4u);
        Assert.Equal(afterBlast, Get<uint>(nearby, "Hitpoints"));
        if (!barrel)
        {
            Assert.Empty(Get<LootWorld>(shooter, "Loot").Items);
            Assert.Empty(Get<LootWorld>(nearby, "Loot").Items);
            world.DrainDrops(nearby);
            Assert.Empty(Get<LootWorld>(nearby, "Loot").Items);
        }
        else Assert.Contains(world.Sent, p => p.Link == nearby && p.Packet[0] == 0xba && p.Packet[1] == 2);
    }

    [Fact]
    public void FuelCanCountsTriggerPullsAcrossShootersRatherThanShotgunPellets()
    {
        var can = new FuelCanDurability();
        for (uint pellet = 1; pellet <= 8; pellet++)
            Assert.False(can.Hit(10, new(pellet, 1374, 0, 0, 0, 100)));
        Assert.Equal(1, can.Hits);
        Assert.False(can.Hit(11, new(1, 1374, 0, 0, 0, 100)));
        Assert.True(can.Hit(10, new(1, 1374, 0, 0, 0, 900)));
        Assert.Equal(3, can.Hits);
    }

    [Fact]
    public void AtvStartsWithoutAKeyAndAllMapSpawnTanksAreSeventyFivePercent()
    {
        var fleet = new VehicleFleet(VehicleRoster.LoadDefault());
        for (uint seed = 0; seed < 20; seed++) Assert.Equal(7500f, fleet.SpawnFuel(seed, seed * 17));
        var atv = new MatchVehicle(100, 101, fleet.Roster.Require(5), Vector3.Zero, 0,
            fleet.Options.MaxHealth, fleet.SpawnFuel(1, 0));
        fleet.Add(atv);
        Assert.Equal(100000u, atv.Health);
        Assert.DoesNotContain(atv.Inventory.Items, item => item.DefinitionId is 3460 or 73);
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(atv.Guid, 123, 0, 0, out _, out _));
        var ignition = new VehicleIgnition();
        var result = ignition.TryStart(atv, 123, false, 0, 1, fleet.Options,
            new VehicleIgnitionOptions { Required = true, KeyedFraction = 0 });
        Assert.True(result.EngineOn);
        Assert.True(atv.EngineOn);
    }

    private static T Get<T>(SoeConnection link, string name) =>
        (T)link.Tag!.GetType().GetProperty(name)!.GetValue(link.Tag)!;

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly ZoneService _service;
        private readonly List<SoeConnection> _connections = [];
        public List<(SoeConnection Link, byte[] Packet)> Sent { get; } = [];
        public Fixture() => _service = new ZoneService(this, this, new GatewayTicketRegistry(),
            new ZoneOptions { SendProximateItems = false });
        public object? Call(string method, params object[] args) => typeof(ZoneService)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_service, args);
        private static void Set(SoeConnection link, string key, object value) =>
            link.Tag!.GetType().GetProperty(key)!.SetValue(link.Tag, value);
        public SoeConnection Add(ulong guid, ulong match, Vector3 position)
        {
            var request = new SessionRequest(3, (uint)guid, 512, ZoneService.ProtocolName);
            var link = new SoeConnection(new(IPAddress.Loopback, 16000 + (int)guid), in request,
                new(), SessionDecision.Clear, _service, this, (_, _) => { }, 0);
            _service.OnConnected(link);
            _connections.Add(link);
            Set(link, "Guid", guid); Set(link, "Authenticated", true);
            Set(link, "BountyAdmission", new MatchAdmissionContext(match, MatchQueueKind.Public, MatchMode.Solo));
            _service.ForTest(link).EnterMatch();
            Call("JoinSharedLoot", link, link.Tag!);
            Get<SessionMovementState>(link, "Movement").ApplyPlayer(
                ClientMovementUpdate.Parse(MovementRecord.Position(position)));
            return link;
        }
        public void DrainDrops(SoeConnection link)
        {
            Set(link, "LandingLootDrained", true);
            var burst = new List<Action>();
            Call("PlanSharedDrops", link, link.Tag!, burst);
            foreach (var action in burst) action();
        }
        public void Send(SoeConnection link, byte[] packet) => _service.OnMessage(link,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), .. packet]);
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add((connection, bytes[1..].ToArray())); }
        public void Dispose()
        {
            foreach (var link in _connections)
            { link.Disconnect(); _service.OnDisconnected(link, DisconnectCause.ServerRequested); }
        }
    }
}
