using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Destructibles;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    private static MatchVehicle AddFeedbackVehicle(Fixture f, SoeConnection owner,
        uint vehicleId = 1, Vector3 position = default)
    {
        var fleet = (VehicleFleet)Call(f.Service, "EnsureVehicleFleet", owner, owner.Tag, false)!;
        var car = new MatchVehicle(0x4600000000020000UL + vehicleId, 3000 + vehicleId,
            fleet.Roster.Require(vehicleId), position, 0, fleet.Options.MaxHealth, 5000);
        fleet.Add(car);
        Get<MatchVehicleStream>(owner.Tag!, "StreamedVehicles").NoteSpawned(car.Guid, car.Position);
        return car;
    }

    private static bool IsVehicleDestroyed(byte[] packet) =>
        packet.Length >= 3 && packet[1] == 0x0f && packet[2] == 0x25;

    [Theory]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(5u, false)]
    [InlineData(1u, true)]
    public void VehicleBulletHitSendsOneWhiteMarkerWithNativeVehicleAudio(uint vehicleId, bool lethal)
    {
        using var f = new Fixture(new ZoneOptions { VehicleDamage = new() { ExplosionDamage = 0 } });
        var shooter = f.Add(1, Vector3.Zero);
        f.Add(2, Vector3.Zero);
        var car = AddFeedbackVehicle(f, shooter, vehicleId);
        if (lethal) car.Health = 250;
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, car.Guid, 42);

        var marker = Assert.Single(f.Recorder.Routed.Skip(mark), r => IsHitFeedback(r.Packet));
        Assert.Same(shooter, marker.Connection);
        Assert.Equal(12, marker.Packet.Length);
        Assert.Equal(0x03, marker.Packet[7]); // Enemy+vehicle; no flesh/armour/headshot/kill or audio suppression.
        Assert.Equal(lethal ? 250u : 1000u, WireUInt32(marker.Packet, 3));
        Assert.Equal(uint.MaxValue, WireUInt32(marker.Packet, 8));
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => IsHitFeedbackAudio(r.Packet));
        Assert.Equal(lethal ? 0u : 99000u, car.Health);

        mark = f.Recorder.Routed.Count;
        f.Weapon(shooter, ShootingPacketBuilder.HitReport(42, car.Guid, "SPINE"), 42001);
        AssertNoHitFeedback(f, mark);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => IsVehicleDestroyed(r.Packet));
        if (lethal)
        {
            FeedbackShot(f, shooter, car.Guid, 43);
            AssertNoHitFeedback(f, mark);
        }
    }

    [Theory]
    [InlineData("unfired")]
    [InlineData("range")]
    [InlineData("impact")]
    [InlineData("damage disabled")]
    [InlineData("markers disabled")]
    public void VehicleFeedbackHonoursCombatAndHitValidation(string scenario)
    {
        using var f = new Fixture(new ZoneOptions
        {
            Combat = new CombatOptions
            {
                EnableCombatDamage = scenario != "damage disabled",
                SendHitMarker = scenario != "markers disabled",
            },
        });
        var shooter = f.Add(1, Vector3.Zero);
        var car = AddFeedbackVehicle(f, shooter, position: scenario switch
        {
            "range" => new(10000, 0, 0), "impact" => new(20, 0, 0), _ => Vector3.Zero,
        });
        int mark = f.Recorder.Routed.Count;
        if (scenario == "unfired")
            f.Weapon(shooter, ShootingPacketBuilder.HitReport(42, car.Guid, "SPINE"), 42000);
        else FeedbackShot(f, shooter, car.Guid, 42);
        AssertNoHitFeedback(f, mark);
        Assert.Equal(scenario == "markers disabled" ? 99000u : 100000u, car.Health);
    }

    [Theory]
    [InlineData(1u, 7226u, 5207u)]
    [InlineData(2u, 9315u, 5208u)]
    [InlineData(3u, 9316u, 5209u)]
    [InlineData(5u, 9593u, 5206u)]
    public void VehicleWreckUsesNativeModelThenExpiresForAllViewersAfterShooterLeaves(
        uint vehicleId, uint model, uint corpseEffect)
    {
        using var f = new Fixture(new ZoneOptions { VehicleDamage = new() { ExplosionDamage = 0 } });
        var shooter = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, Vector3.Zero);
        var separate = f.Add(3, Vector3.Zero, matchId: 2);
        var car = AddFeedbackVehicle(f, shooter, vehicleId);
        var fleet = Get<VehicleFleet>(shooter.Tag!, "Fleet");
        Call(f.Service, "EnsureVehicleFleet", viewer, viewer.Tag, false);
        Get<MatchVehicleStream>(viewer.Tag!, "StreamedVehicles").NoteSpawned(car.Guid, car.Position);
        int mark = f.Recorder.Routed.Count;
        long beforeExplosion = Environment.TickCount64;
        Call(f.Service, "ApplyVehicleDamage", shooter, shooter.Tag, car, 100000u, "test", false, null);
        Assert.InRange(car.WreckExpiresAtMs!.Value, beforeExplosion + 7_000, Environment.TickCount64 + 7_000);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => r.Packet.Length > 2
            && ((r.Packet[1] == 0x11 && r.Packet[2] == 0x31)
                || (r.Packet[1] == 0xce && r.Packet[2] == 9)));

        foreach (var peer in new[] { shooter, viewer })
        {
            var sent = f.Recorder.Routed.Skip(mark).Where(r => r.Connection == peer).Select(r => r.Packet).ToList();
            byte[] destroyed = Assert.Single(sent, IsVehicleDestroyed);
            Assert.Equal(29, destroyed.Length);
            Assert.Equal(car.Guid, BinaryPrimitives.ReadUInt64LittleEndian(destroyed.AsSpan(3)));
            Assert.Equal(0u, WireUInt32(destroyed, 11)); // No repeated explosion in the model-swap packet.
            Assert.Equal(model, WireUInt32(destroyed, 15));
            Assert.All(destroyed.Skip(19), b => Assert.Equal(0, b));
            int corpse = sent.FindIndex(p => p.Length == 16 && p[1] == 9 && p[2] == 0x1d
                && WireUInt32(p, 12) == corpseEffect);
            Assert.True(corpse > sent.FindIndex(IsVehicleDestroyed));
        }
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => r.Connection == separate && IsVehicleDestroyed(r.Packet));
        Assert.NotNull(car.WreckExpiresAtMs);
        long deadline = car.WreckExpiresAtMs.Value;
        Call(f.Service, "ApplyVehicleDamage", shooter, shooter.Tag, car, 100000u, "replay", false, null);
        Assert.Equal(deadline, car.WreckExpiresAtMs);

        // A newly arriving viewer receives the burnt model and corpse loop, with no blast replay.
        var late = f.Add(4, Vector3.Zero);
        mark = f.Recorder.Routed.Count;
        var burst = new List<Action>();
        Call(f.Service, "SpawnNearbyVehicles", late, late.Tag, "late join", burst);
        foreach (var send in burst) send();
        Assert.Single(f.Recorder.Routed.Skip(mark), r => r.Connection == late && IsVehicleDestroyed(r.Packet));
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => r.Packet.Length > 2
            && r.Packet[1] == 0x0f && r.Packet[2] == 0x43);

        Call(f.Service, "LeaveSharedLoot", shooter.Tag);
        f.Service.ForVehicleTest(viewer).PumpDamage(deadline - 1);
        Assert.True(fleet.TryGet(car.Guid, out _));
        mark = f.Recorder.Routed.Count;
        f.Service.ForVehicleTest(viewer).PumpDamage(deadline);
        Assert.False(fleet.TryGet(car.Guid, out _));
        Assert.False(fleet.TryGetByTransient(car.TransientId, out _));
        foreach (var peer in new[] { viewer, late })
        {
            Assert.False(Get<MatchVehicleStream>(peer.Tag!, "StreamedVehicles").IsStreamed(car.Guid));
            Assert.Single(f.Recorder.Routed.Skip(mark), r => r.Connection == peer && r.Packet.Length >= 11
                && r.Packet[1] == 0x0f && r.Packet[2] == 1
                && BinaryPrimitives.ReadUInt64LittleEndian(r.Packet.AsSpan(3)) == car.Guid);
        }
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => r.Connection == separate || r.Connection == shooter);
        mark = f.Recorder.Routed.Count;
        f.Service.ForVehicleTest(late).PumpDamage(deadline + 1000);
        burst.Clear();
        Call(f.Service, "PlanVehicleRestream", viewer, viewer.Tag, burst);
        foreach (var send in burst) send();
        Assert.Empty(f.Recorder.Routed.Skip(mark));
    }

    [Theory]
    [InlineData(1u, false, 100000u)]
    [InlineData(2u, false, 100000u)]
    [InlineData(3u, false, 100000u)]
    [InlineData(5u, false, 100000u)]
    [InlineData(1u, true, 100000u)]
    [InlineData(1u, false, 250u)]
    public void BreakingAPropCostsHalfAPercentOnceAndCanWreckACar(uint vehicleId, bool coasting, uint health)
    {
        using var f = new Fixture(new ZoneOptions { VehicleDamage = new() { ExplosionDamage = 0 } });
        var prop = VehicleFencePolicy.Catalog.Props.Values.First(p => VehicleFencePolicy.IsFence(p.Model));
        var driver = f.Add(1, prop.Position);
        var stranger = f.Add(2, prop.Position);
        var car = AddFeedbackVehicle(f, driver, vehicleId, prop.Position);
        var fleet = Get<VehicleFleet>(driver.Tag!, "Fleet");
        Call(f.Service, "EnsureVehicleFleet", stranger, stranger.Tag, false);
        foreach (var peer in new[] { driver, stranger })
        {
            Set(peer.Tag!, "Authenticated", true); // This combat fixture skips the gateway login handshake.
            Set(peer.Tag!, "DestructiblesGeneration", Get<object>(peer.Tag!, "WorldGeneration"));
        }
        long now = Environment.TickCount64;
        Assert.Equal(VehicleActionResult.Ok, fleet.TryEnter(car.Guid, 1, 0, now, out _, out _));
        Assert.True(fleet.TryApplyOwnerPose(car.TransientId, 1, prop.Position, 0, now, out _));
        if (coasting)
        {
            car.LastInteractionMs = long.MinValue;
            Assert.Equal(VehicleActionResult.Ok, fleet.TryExit(1, now, 60, out _, out _));
        }
        car.Health = health;
        using var writer = new PacketWriter();
        writer.WriteByte(0xba); writer.WriteUInt16(1); writer.WriteUInt32(prop.ObjectId);
        writer.WriteString(prop.Model); writer.WriteUInt32(0); writer.WriteUInt64(car.Guid);

        f.Service.ForVehicleTest(stranger).Deliver(writer.Written);
        Assert.Equal(health, car.Health); // A non-owner cannot break it or charge the car.
        int mark = f.Recorder.Routed.Count;
        f.Service.ForVehicleTest(driver).Deliver(writer.Written);
        Assert.Equal(health > 500 ? health - 500 : 0, car.Health);
        var destroyedProps = f.Recorder.Routed.Skip(mark).Where(r => r.Packet.Length > 3
            && r.Packet[1] == 0xba && r.Packet[2] == 2 && r.Packet[3] == 0).ToArray();
        Assert.Equal(2, destroyedProps.Length);
        AssertNoHitFeedback(f, mark); // Driving through a prop is not a weapon hit.
        if (health <= 500)
        {
            Assert.True(car.IsEmpty);
            Assert.NotNull(car.WreckExpiresAtMs);
            Assert.Single(f.Recorder.Routed.Skip(mark), r => IsVehicleDestroyed(r.Packet));
        }
        mark = f.Recorder.Routed.Count;
        f.Service.ForVehicleTest(driver).Deliver(writer.Written);
        f.Service.ForVehicleTest(stranger).Deliver(writer.Written);
        Assert.Equal(health > 500 ? health - 500 : 0, car.Health);
        Assert.Empty(f.Recorder.Routed.Skip(mark));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQueuedVehicleSpawnCannotResurrectAnExpiredWreck(bool initialSpawn)
    {
        using var f = new Fixture(new ZoneOptions { VehicleDamage = new() { ExplosionDamage = 0 } });
        var first = f.Add(1, Vector3.Zero);
        var late = f.Add(2, Vector3.Zero);
        var car = AddFeedbackVehicle(f, first);
        var fleet = Get<VehicleFleet>(first.Tag!, "Fleet");
        Call(f.Service, "EnsureVehicleFleet", late, late.Tag, false);
        var burst = new List<Action>();
        if (initialSpawn) Call(f.Service, "SpawnNearbyVehicles", late, late.Tag, "test", burst);
        else Call(f.Service, "PlanVehicleRestream", late, late.Tag, burst);
        Assert.NotEmpty(burst);
        Call(f.Service, "ApplyVehicleDamage", first, first.Tag, car, 100000u, "test", false, null);
        f.Service.ForVehicleTest(first).PumpDamage(car.WreckExpiresAtMs!.Value);
        int mark = f.Recorder.Routed.Count;
        foreach (var send in burst) send();
        Assert.Empty(f.Recorder.Routed.Skip(mark));
        Assert.Equal(0, fleet.Count);
        Assert.Equal(1, fleet.CreatedCount); // Retirement cannot rewind console id allocation.
    }
}
