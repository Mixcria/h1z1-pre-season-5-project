using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleOccupantReplicationTests
{
    [Fact]
    public void BoostReplication_CurrentViewersReceiveOneCompositeWhileLocalControlsStayWithTheSender()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add();
        var reserved = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll();
        foreach (var link in new[] { driver, passenger, observer }) f.Stream(link);
        f.Plan(reserved); // Reserved interest is not a native vehicle spawn yet.
        Assert.True(Get<MatchVehicleStream>(reserved, "StreamedVehicles").IsStreamed(f.Car.Guid));
        Assert.False(Get<MatchVehicleStream>(reserved, "StreamedVehicles").IsSpawned(f.Car.Guid));
        f.Enter(driver, 0); f.Enter(passenger, 1); f.Clear();

        f.Deliver(driver, BoostAdd(f.Car, GuidOf(driver)));

        Assert.Equal(GuidOf(driver), f.Car.BoostingCharacterGuid);
        Assert.True(Get<VehicleBoostState>(driver, "Boost").IsBoosting(f.Car.Guid));
        foreach (var link in new[] { driver, passenger, observer })
            Assert.Single(f.Sent(link), p => IsBoostComposite(p, f.Car, add: true));
        Assert.Single(f.Sent(driver), p => Is(p, 0x0f, 0x33));
        AssertNoBoostLocalControl(f.Sent(passenger));
        AssertNoBoostLocalControl(f.Sent(observer));
        Assert.Empty(f.Sent(reserved));
        Assert.Empty(f.Sent(outsider));

        f.Clear();
        f.Deliver(driver, BoostAdd(f.Car, GuidOf(driver)));
        Assert.DoesNotContain(f.Sent(driver), p => IsBoostComposite(p, f.Car, add: true));
        Assert.Single(f.Sent(driver), p => Is(p, 0x0f, 0x33));
        Assert.Empty(f.Sent(passenger));
        Assert.Empty(f.Sent(observer));
        Assert.Empty(f.Sent(reserved));
        Assert.Empty(f.Sent(outsider));

        f.Clear();
        f.Deliver(driver, BoostRemove(f.Car, GuidOf(driver)));
        Assert.Equal(0ul, f.Car.BoostingCharacterGuid);
        Assert.False(Get<VehicleBoostState>(driver, "Boost").IsBoosting(f.Car.Guid));
        foreach (var link in new[] { driver, passenger, observer })
            Assert.Single(f.Sent(link), p => IsBoostComposite(p, f.Car, add: false));
        byte[] echo = Assert.Single(f.Sent(driver), p => IsBoostEcho(p));
        Assert.Equal(54, echo.Length);
        Assert.Equal(f.Car.Guid, U64(echo, 14));
        Assert.Equal(GuidOf(driver), U64(echo, 22));
        Assert.Single(f.Sent(driver), p => Is(p, 0x0f, 0x33));
        AssertNoBoostLocalControl(f.Sent(passenger));
        AssertNoBoostLocalControl(f.Sent(observer));
        Assert.Empty(f.Sent(reserved));
        Assert.Empty(f.Sent(outsider));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoostReplication_LateInterestReceivesOnlyCurrentEffectAfterTheFullVehicle(bool releasedBeforeSpawn)
    {
        using var f = new Fixture();
        var driver = f.Add(); f.Stream(driver); f.Enter(driver, 0);
        f.Deliver(driver, BoostAdd(f.Car, GuidOf(driver)));
        var observer = f.Add(at: f.Car.Position + new Vector3(ObserverView.PlayerLeaveMetres + 100, 0, 0));
        Movement(observer).PinPlayer(f.Car.Position);
        var spawn = f.Plan(observer);
        f.Clear();
        f.Full(observer, f.Car.Guid);
        Assert.Empty(f.Sent(observer));
        if (releasedBeforeSpawn) f.Deliver(driver, BoostRemove(f.Car, GuidOf(driver)));
        Assert.Empty(f.Sent(observer));
        foreach (var action in spawn) action();

        var sent = f.Sent(observer);
        int lightweight = sent.FindIndex(p => p[0] == 0xd7);
        int full = sent.FindIndex(p => p[0] == 0xdb);
        Assert.True(lightweight >= 0 && full > lightweight);
        if (releasedBeforeSpawn)
            Assert.DoesNotContain(sent, p => IsBoostComposite(p, f.Car, add: true));
        else
        {
            Assert.Single(sent, p => IsBoostComposite(p, f.Car, add: true));
            Assert.True(sent.FindIndex(p => IsBoostComposite(p, f.Car, add: true)) > full);
        }
        AssertNoBoostLocalControl(sent);
    }

    [Fact]
    public void BoostReplication_PreviousDriverReleaseCannotRemoveTheNewDriversSharedEffect()
    {
        using var f = new Fixture();
        var previous = f.Add(); var current = f.Add(); var observer = f.Add();
        f.SeeAll(); foreach (var link in new[] { previous, current, observer }) f.Stream(link);
        f.Enter(previous, 0); f.Enter(current, 1);
        f.Deliver(previous, BoostAdd(f.Car, GuidOf(previous)));
        f.Seat(previous, 2); f.Seat(current, 0);
        Assert.False(Get<VehicleBoostState>(previous, "Boost").IsBoosting(f.Car.Guid));
        f.Deliver(current, BoostAdd(f.Car, GuidOf(current)));
        Assert.Equal(GuidOf(current), f.Car.BoostingCharacterGuid);
        f.Clear();

        f.Deliver(previous, BoostRemove(f.Car, GuidOf(previous)));
        f.Deliver(previous, BoostRemove(f.Car, GuidOf(previous)));

        Assert.Equal(GuidOf(current), f.Car.BoostingCharacterGuid);
        Assert.True(Get<VehicleBoostState>(current, "Boost").IsBoosting(f.Car.Guid));
        Assert.False(Get<VehicleBoostState>(previous, "Boost").IsBoosting(f.Car.Guid));
        Assert.Equal(2, f.Sent(previous).Count(p => IsBoostEcho(p)));
        Assert.Equal(2, f.Sent(previous).Count(p => Is(p, 0x0f, 0x33)));
        Assert.DoesNotContain(f.Sent(previous), p => IsBoostComposite(p, f.Car, add: false));
        Assert.Empty(f.Sent(current));
        Assert.Empty(f.Sent(observer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoostReplication_ForgedSourceCannotChangeAnActiveDriversState(bool remove)
    {
        using var f = new Fixture();
        var driver = f.Add(); var observer = f.Add();
        f.SeeAll(); f.Stream(driver); f.Stream(observer); f.Enter(driver, 0);
        f.Deliver(driver, BoostAdd(f.Car, GuidOf(driver)));
        int presses = Get<VehicleBoostState>(driver, "Boost").Presses;
        int releases = Get<VehicleBoostState>(driver, "Boost").Releases;
        f.Clear();
        f.Deliver(driver, remove ? BoostRemove(f.Car, GuidOf(observer)) : BoostAdd(f.Car, GuidOf(observer)));

        Assert.True(Get<VehicleBoostState>(driver, "Boost").IsBoosting(f.Car.Guid));
        Assert.Equal(GuidOf(driver), f.Car.BoostingCharacterGuid);
        Assert.Equal(presses, Get<VehicleBoostState>(driver, "Boost").Presses);
        Assert.Equal(releases, Get<VehicleBoostState>(driver, "Boost").Releases);
        Assert.Empty(f.Sent(driver));
        Assert.Empty(f.Sent(observer));
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("seat")]
    [InlineData("disconnect")]
    public void BoostReplication_ActualDriverDepartureClearsTheSharedEffect(string departure)
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll(); foreach (var link in new[] { driver, passenger, observer }) f.Stream(link);
        f.Enter(driver, 0); f.Enter(passenger, 1);
        f.Deliver(driver, BoostAdd(f.Car, GuidOf(driver)));
        f.Clear();
        switch (departure)
        {
            case "exit": f.Exit(driver); break;
            case "seat": f.Seat(driver, 2); break;
            case "disconnect": f.Disconnect(driver); break;
        }

        Assert.Equal(0ul, f.Car.BoostingCharacterGuid);
        Assert.False(Get<VehicleBoostState>(driver, "Boost").IsBoosting(f.Car.Guid));
        foreach (var link in new[] { passenger, observer })
        {
            Assert.Single(f.Sent(link), p => IsBoostComposite(p, f.Car, add: false));
            AssertNoBoostLocalControl(f.Sent(link));
        }
        Assert.Empty(f.Sent(outsider));
    }

    [Fact]
    public void BoostReplication_WreckClearsSharedAndPerSessionStateAndCannotRestreamAnActiveBoost()
    {
        using var f = new Fixture();
        var driver = f.Add(); var passenger = f.Add(); var observer = f.Add(); var outsider = f.Add(match: 2);
        f.SeeAll(); foreach (var link in new[] { driver, passenger, observer }) f.Stream(link);
        f.Enter(driver, 0); f.Enter(passenger, 1);
        f.Deliver(driver, BoostAdd(f.Car, GuidOf(driver)));
        // Explicitly seed stale per-session state as a cleanup regression; this is
        // not a claim that a passenger's native boost request is accepted.
        Assert.True(Get<VehicleBoostState>(passenger, "Boost").Press(f.Car.Guid));
        f.Clear();

        Call(f.Service, "ApplyVehicleDamage", driver, driver.Tag, f.Car, 100000u, "boost cleanup test", false, null);

        Assert.Equal(0u, f.Car.Health);
        Assert.Equal(0ul, f.Car.BoostingCharacterGuid);
        foreach (var link in new[] { driver, passenger, observer })
            Assert.Single(f.Sent(link), p => IsBoostComposite(p, f.Car, add: false));
        foreach (var link in new[] { driver, passenger })
        {
            Assert.False(Get<VehicleBoostState>(link, "Boost").IsBoosting(f.Car.Guid));
            Assert.Single(f.Sent(link), p => Is(p, 0x0f, 0x33));
            Assert.DoesNotContain(f.Sent(link), p => IsBoostEcho(p));
        }
        AssertNoBoostLocalControl(f.Sent(observer));
        Assert.Empty(f.Sent(outsider));

        var late = f.Add(); f.Clear(); f.Stream(late);
        Assert.DoesNotContain(f.Sent(late), p => IsBoostComposite(p, f.Car, add: true));
        AssertNoBoostLocalControl(f.Sent(late));
    }

    // Synthetic requests shaped by fresh August readers 140ce9e70 / 140cea390.
    // Ancillary fields are neutral fixture values, not a retail packet capture.
    // Evidence: out/compatibility-20260926/vehicle-boost/runtime-{add,remove}-reader.txt.
    private static byte[] BoostAdd(MatchVehicle car, ulong source) => Bytes(w =>
    {
        BoostHead(w, car, 1);
        w.WriteUInt32(0); w.WriteUInt64(source); w.WriteUInt32(0); w.WriteUInt64(0);
        w.WriteUInt64(car.Guid); w.WriteUInt64(0);
        w.WriteSingle(0); w.WriteSingle(0); w.WriteSingle(0); w.WriteSingle(1);
        w.WriteByte(1); // Simple form: no property lists.
    });

    private static byte[] BoostRemove(MatchVehicle car, ulong source) => Bytes(w =>
    {
        BoostHead(w, car, 3);
        w.WriteUInt64(source); w.WriteUInt64(car.Guid); w.WriteUInt64(0);
        w.WriteSingle(0); w.WriteSingle(0); w.WriteSingle(0); w.WriteSingle(1);
    });

    private static void BoostHead(PacketWriter w, MatchVehicle car, byte sub)
    {
        w.WriteByte(0x9e); w.WriteByte(sub); w.WriteUInt32(1);
        w.WriteUInt32(AugustVehicleBoostFacts.TurboClientEffect(car.Definition.VehicleId));
        w.WriteUInt32(AugustVehicleBoostFacts.TurboServerEffect(car.Definition.VehicleId));
    }

    private static bool IsBoostComposite(byte[] packet, MatchVehicle car, bool add) =>
        packet.Length == (add ? AddEffectTagCompositeEffect.Length : RemoveEffectTagCompositeEffect.Length)
        && Is(packet, 0x0f, add ? (byte)0x15 : (byte)0x16) && U64(packet, 2) == car.Guid
        && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10))
            == AugustVehicleBoostFacts.TurboCompositeEffect(car.Definition.VehicleId);

    private static bool IsBoostEcho(byte[] packet) =>
        packet.Length == 54 && Is(packet, 0x9e, 3)
        && AugustVehicleBoostFacts.IsTurboClientEffect(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6)));

    private static void AssertNoBoostLocalControl(IEnumerable<byte[]> packets) =>
        Assert.DoesNotContain(packets, p => Is(p, 0x0f, 0x33) || IsBoostEcho(p));
}
