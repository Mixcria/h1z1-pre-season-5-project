using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleOccupantReplicationTests
{
    [Fact]
    public void AnimationReplication_OwnerRelaysOnlyTheNormalizedDeltaWithoutChangingGameplay()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add();
        var reserved = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll();
        foreach (var link in new[] { driver, passenger, observer }) f.Stream(link);
        f.Plan(reserved);
        Assert.False(Get<MatchVehicleStream>(reserved, "StreamedVehicles").IsSpawned(f.Car.Guid));
        f.Enter(driver, 0); f.Enter(passenger, 1);
        f.Car.EngineOn = true;
        var before = (f.Car.Health, f.Car.Fuel, f.Car.EngineOn, f.Car.OwnerGuid, f.Car.Position);
        var items = f.Car.Inventory.Items.OrderBy(item => item.ItemGuid).ToArray();
        var seats = f.Car.Seats.ToArray();
        var delta = new VehicleStateData(f.Car.Guid, ListA: [new(0, 1), new(29, 0)],
            ListB: [new(0, unchecked((byte)-50)), new(14, 100)]);
        byte[] normalized = Bytes(delta.WriteTo);
        f.Clear();

        f.Deliver(driver, [.. normalized, 0xde, 0xad]); // Native reader permits a trailer; relay omits it.

        Assert.Empty(f.Sent(driver));
        Assert.Equal(normalized, Assert.Single(f.Sent(passenger)));
        Assert.Equal(normalized, Assert.Single(f.Sent(observer)));
        Assert.Empty(f.Sent(reserved)); Assert.Empty(f.Sent(outsider));
        Assert.Equal(normalized, Bytes(f.Car.Animation.ToPacket(f.Car.Guid).WriteTo));
        Assert.Equal(before, (f.Car.Health, f.Car.Fuel, f.Car.EngineOn, f.Car.OwnerGuid, f.Car.Position));
        Assert.Equal(items, f.Car.Inventory.Items.OrderBy(item => item.ItemGuid).ToArray());
        Assert.Equal(seats, f.Car.Seats.ToArray());
    }

    [Theory]
    [InlineData("passenger")]
    [InlineData("other-match")]
    [InlineData("missing-registration")]
    [InlineData("wrong-registration")]
    [InlineData("unknown-vehicle")]
    [InlineData("wreck")]
    [InlineData("unknown-value")]
    public void AnimationReplication_InvalidAuthorityOrUnsupportedProducerCannotChangeOrRelayState(string reason)
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll();
        foreach (var link in new[] { driver, passenger, observer }) f.Stream(link);
        f.Enter(driver, 0); f.Enter(passenger, 1);
        var sender = driver;
        ulong guid = f.Car.Guid;
        uint value = 0;
        switch (reason)
        {
            case "passenger": sender = passenger; break;
            case "other-match": sender = outsider; break;
            case "missing-registration": Movement(driver).RemoveManagedEntity(f.Car.TransientId); break;
            case "wrong-registration":
                Movement(driver).RemoveManagedEntity(f.Car.TransientId);
                Movement(driver).RegisterManagedEntity(f.Car.TransientId, f.Car.Guid + 1);
                break;
            case "unknown-vehicle": guid++; break;
            case "wreck": f.Car.Health = 0; break;
            case "unknown-value": value = 1; break;
        }
        var retained = f.Car.Animation;
        f.Clear();

        f.Deliver(sender, Bytes(new VehicleStateData(guid, value, [new(0, 1)], [new(1, 50)]).WriteTo));

        Assert.Same(retained, f.Car.Animation);
        foreach (var link in new[] { driver, passenger, observer, outsider }) Assert.Empty(f.Sent(link));
        Assert.Equal(GuidOf(driver), f.Car.OwnerGuid);
        Assert.Equal(5000f, f.Car.Fuel);
    }

    [Fact]
    public void AnimationReplication_TruncatedUpdatesAreAtomicAndDoNotPreventTheNextValidMessage()
    {
        using var f = new Fixture();
        var driver = f.Add(); var observer = f.Add();
        f.SeeAll(); f.Stream(driver); f.Stream(observer); f.Enter(driver, 0);
        f.Deliver(driver, Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 1)], ListB: [new(1, 10)]).WriteTo));
        var retained = f.Car.Animation;
        byte[] update = Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 0)], ListB: [new(1, 75)]).WriteTo);
        f.Clear();

        for (int length = 2; length < update.Length; length++)
        {
            f.Deliver(driver, update[..length]);
            Assert.Same(retained, f.Car.Animation);
            Assert.Empty(f.Sent(driver)); Assert.Empty(f.Sent(observer));
        }
        f.Deliver(driver, update);

        Assert.Equal(update, Assert.Single(f.Sent(observer)));
        Assert.Empty(f.Sent(driver));
        Assert.Equal(update, Bytes(f.Car.Animation.ToPacket(f.Car.Guid).WriteTo));
    }

    [Fact]
    public void AnimationReplication_MergesSparseKeysAndSendsTheLatestFullStateAfterLateSpawn()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add();
        f.SeeAll(); f.Stream(driver); f.Stream(passenger); f.Enter(driver, 0); f.Enter(passenger, 1);
        f.Deliver(driver, Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 1), new(29, 1)],
            ListB: [new(0, 25), new(14, 80)]).WriteTo));
        var observer = f.Add(at: f.Car.Position + new Vector3(ObserverView.PlayerLeaveMetres + 100, 0, 0));
        f.Move(observer, f.Car.Position); // Players become known before the vehicle actor.
        var deferred = f.Plan(observer);
        Assert.False(Get<MatchVehicleStream>(observer, "StreamedVehicles").IsSpawned(f.Car.Guid));
        f.Clear();
        var delta = new VehicleStateData(f.Car.Guid, ListA: [new(0, 0)], ListB: [new(0, unchecked((byte)-40))]);

        f.Deliver(driver, Bytes(delta.WriteTo));
        f.Full(observer, f.Car.Guid);

        Assert.Empty(f.Sent(observer));
        Assert.Equal(Bytes(delta.WriteTo), Assert.Single(f.Sent(passenger))); // Relay a delta, not the merged map.
        Assert.Equal(new[] { new VehicleStateEntry(0, 0), new VehicleStateEntry(29, 1) }, f.Car.Animation.ListA);
        Assert.Equal(new[] { new VehicleStateEntry(0, unchecked((byte)-40)), new VehicleStateEntry(14, 80) }, f.Car.Animation.ListB);
        f.Car.EngineOn = true;
        f.Car.Fuel = 4321;
        foreach (var action in deferred) action();

        var sent = f.Sent(observer);
        int spawn = sent.FindIndex(p => p[0] == 0xd7);
        int full = sent.FindIndex(p => p[0] == 0xdb);
        Assert.True(spawn >= 0 && full > spawn);
        Assert.Equal(Bytes(VehicleFullState.Create(f.Car).WriteTo), sent[full]);
        Assert.Equal(2, sent.Count(p => Is(p, 0x70, 2)));
        Assert.All(sent.Select((packet, index) => (packet, index)).Where(row => Is(row.packet, 0x70, 2)),
            row => Assert.True(row.index > full));
        Assert.DoesNotContain(sent, p => Is(p, 0x88, 3)); // Full state already embeds the retained maps.
        AssertNoPossession(sent);
        f.Clear(); f.Full(observer, f.Car.Guid);
        Assert.Equal(Bytes(VehicleFullState.Create(f.Car).WriteTo), Assert.Single(f.Sent(observer)));
    }

    [Fact]
    public void AnimationReplication_CoastingOwnerStillReportsAndReceivesRetainedBaselineAfterRelease()
    {
        using var f = new Fixture();
        var driver = f.Add(); var observer = f.Add();
        f.SeeAll(); f.Stream(driver); f.Stream(observer); f.Enter(driver, 0);
        f.Deliver(driver, Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 1)], ListB: [new(1, 50)]).WriteTo));
        f.Exit(driver);
        Assert.Equal(0ul, f.Car.OwnerGuid);
        Assert.Equal(GuidOf(driver), f.Car.CoastingOwnerGuid);
        f.Clear();
        byte[] delta = Bytes(new VehicleStateData(f.Car.Guid, ListB: [new(1, 0)]).WriteTo);

        f.Deliver(driver, delta);

        Assert.Equal(delta, Assert.Single(f.Sent(observer)));
        Assert.Empty(f.Sent(driver));
        byte[] latest = Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 1)], ListB: [new(1, 0)]).WriteTo);
        Assert.Equal(latest, Bytes(f.Car.Animation.ToPacket(f.Car.Guid).WriteTo));
        f.Clear();
        f.Managed(driver, null, time: 200, stopped: true); // Native explicit rest ends the coast lease.

        Assert.Equal(0ul, f.Car.CoastingOwnerGuid);
        Assert.False(Movement(driver).TryGetManaged(f.Car.TransientId, out _));
        var sent = f.Sent(driver);
        int control = sent.FindIndex(p => p.SequenceEqual(Bytes(new ManagedObjectResponseControl(false, f.Car.Guid).WriteTo)));
        int grant = sent.FindIndex(p => p.SequenceEqual(Bytes(CharacterManagedObject.Release(f.Car.Guid).WriteTo)));
        int animation = sent.FindIndex(p => p.SequenceEqual(latest));
        Assert.True(control >= 0 && grant > control && animation > grant);
        Assert.Single(sent, p => Is(p, 0x88, 3));
        Assert.Equal(latest, Bytes(f.Car.Animation.ToPacket(f.Car.Guid).WriteTo));
        f.Clear();
        f.Deliver(driver, Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 0)]).WriteTo));
        Assert.Empty(f.Sent(driver)); Assert.Empty(f.Sent(observer));
        Assert.Equal(latest, Bytes(f.Car.Animation.ToPacket(f.Car.Guid).WriteTo));
    }

    [Fact]
    public void AnimationReplication_FormerOwnerWithStaleRegistrationCannotOverrideTheNewDriver()
    {
        using var f = new Fixture();
        var former = f.Add(); var driver = f.Add(); var observer = f.Add();
        f.SeeAll(); foreach (var link in new[] { former, driver, observer }) f.Stream(link);
        f.Enter(former, 0); f.Enter(driver, 1);
        f.Deliver(former, Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 1)]).WriteTo));
        f.Seat(former, 2); f.Seat(driver, 0);
        Movement(former).RegisterManagedEntity(f.Car.TransientId, f.Car.Guid);
        var retained = f.Car.Animation;
        f.Clear();
        byte[] update = Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 0)]).WriteTo);

        f.Deliver(former, update);

        Assert.Same(retained, f.Car.Animation);
        Assert.Empty(f.Sent(former)); Assert.Empty(f.Sent(driver)); Assert.Empty(f.Sent(observer));
        f.Deliver(driver, update);
        Assert.Equal(update, Assert.Single(f.Sent(former)));
        Assert.Equal(update, Assert.Single(f.Sent(observer)));
        Assert.Empty(f.Sent(driver));
        Assert.Equal(GuidOf(driver), f.Car.OwnerGuid);
    }

    [Fact]
    public void AnimationReplication_WreckClearsTheCacheWithoutInventingAResetMap()
    {
        using var f = new Fixture();
        var driver = f.Add(); var observer = f.Add();
        f.SeeAll(); f.Stream(driver); f.Stream(observer); f.Enter(driver, 0);
        byte[] report = Bytes(new VehicleStateData(f.Car.Guid, ListA: [new(0, 1)], ListB: [new(1, 50)]).WriteTo);
        f.Deliver(driver, report);
        Assert.NotEmpty(f.Car.Animation.ListA);
        f.Clear();

        Call(f.Service, "ApplyVehicleDamage", driver, driver.Tag, f.Car, 100000u, "animation lifecycle test", false, null);

        Assert.Equal(0u, f.Car.Health);
        Assert.Empty(f.Car.Animation.ListA); Assert.Empty(f.Car.Animation.ListB);
        Assert.DoesNotContain(f.Sent(driver), p => Is(p, 0x88, 3));
        Assert.DoesNotContain(f.Sent(observer), p => Is(p, 0x88, 3));
        f.Clear(); f.Deliver(driver, report);
        Assert.Empty(f.Car.Animation.ListA); Assert.Empty(f.Car.Animation.ListB);
        Assert.Empty(f.Sent(driver)); Assert.Empty(f.Sent(observer));
    }
}
