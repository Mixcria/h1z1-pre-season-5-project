using System.Buffers.Binary;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Fact]
    public void CapturedPoliceSirenRequestReachesControlsWithoutCombatAndAcknowledgesEveryToggle()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            Combat = new CombatOptions { Enabled = false },
        });
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId: 3);
        // 19:05:21.352, wire-20260906-190153.txt: the actual X request. Only actors change.
        byte[] captured = Convert.FromHexString(
            "A0010100000000000000FFF410000E000000211000000000000001000000000000000000000025000000000000460000000000000000D38FBF441B064C42A30207C50000803F0001");
        BinaryPrimitives.WriteUInt64LittleEndian(captured.AsSpan(18), session.Guid);
        BinaryPrimitives.WriteUInt64LittleEndian(captured.AsSpan(38), car.Guid);
        byte[] off = NativeDriverAbility(1111295, 14, false, session.Guid, car.Guid);
        int mark = recorder.Sent.Count;

        for (int cycle = 0; cycle < 3; cycle++)
        {
            session.Deliver(captured);
            session.Deliver(captured);
            Assert.True(car.SirenOn);
            session.Deliver(off);
            session.Deliver(off);
            Assert.False(car.SirenOn);
        }

        var sent = From(recorder, mark).ToList();
        Assert.Equal(3, SirenTags(sent, on: true).Count);
        Assert.Equal(3, SirenTags(sent, on: false).Count);
        // ACK duplicate transitions too: otherwise the client's active/busy state can stick.
        Assert.Equal(6, SirenAcknowledgements(sent, on: true).Count);
        Assert.Equal(6, SirenAcknowledgements(sent, on: false).Count);
        Assert.All(SirenTags(sent, on: true).Concat(SirenTags(sent, on: false)), packet =>
            Assert.Equal(car.Guid, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(3))));
        Assert.Empty(Sub8(sent, 0xa0, 1)); // This server-run ability has no motor-style runtime.
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(5u, true)]
    [InlineData(3u, false)]
    public void OnlyThePoliceDriverCanActivateTheSiren(uint vehicleId, bool asDriver)
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId, asDriver);
        int mark = recorder.Sent.Count;
        session.Deliver(NativeDriverAbility(1111295, 14, true, session.Guid, car.Guid));
        Assert.False(car.SirenOn);
        Assert.Empty(SirenTags(From(recorder, mark), on: true));
        Assert.Empty(SirenAcknowledgements(From(recorder, mark), on: true));
    }

    [Fact]
    public void SirenRejectsWrongActorsManagerKeyAndServerNotificationAsARequest()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId: 3);
        int mark = recorder.Sent.Count;
        session.Deliver(NativeDriverAbility(1111295, 14, true, session.Guid + 1, car.Guid));
        session.Deliver(NativeDriverAbility(1111295, 14, true, session.Guid, car.Guid + 1));
        session.Deliver(NativeDriverAbility(1111295, 13, true, session.Guid, car.Guid));
        session.Deliver(VehicleDriverControls.Ability(1111295, 14, true));
        Assert.False(car.SirenOn);
        Assert.Empty(SirenTags(From(recorder, mark), on: true));
        Assert.Empty(SirenAcknowledgements(From(recorder, mark), on: true));
    }

    [Fact]
    public void PoliceSirenWorksWithEngineOffAndAnEmptyFuelTank()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId: 3, fuel: 0);
        session.Deliver(NativeDriverAbility(1111288, 11, false, session.Guid, car.Guid));
        Assert.False(car.EngineOn);
        int mark = recorder.Sent.Count;
        session.Deliver(NativeDriverAbility(1111295, 14, true, session.Guid, car.Guid));
        Assert.True(car.SirenOn);
        Assert.False(car.EngineOn);
        session.Deliver(NativeDriverAbility(1111295, 14, false, session.Guid, car.Guid));
        Assert.False(car.SirenOn);
        Assert.False(car.EngineOn);
        Assert.Single(SirenTags(From(recorder, mark), on: true));
        Assert.Single(SirenTags(From(recorder, mark), on: false));
        Assert.Empty(Sub8(From(recorder, mark), 0xa0, 1));
    }

    [Fact]
    public void SirenPersistsAcrossExitAndReseedsTheToggleAfterReentryAndInventoryRefresh()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId: 3);
        session.Deliver(NativeDriverAbility(1111295, 14, true, session.Guid, car.Guid));
        int mark = recorder.Sent.Count;
        Assert.True(session.Exit());
        Assert.True(car.SirenOn);
        Assert.Empty(SirenTags(From(recorder, mark), on: false));
        car.LastInteractionMs = long.MinValue;
        mark = recorder.Sent.Count;
        Assert.True(session.Enter(car.Guid));
        Assert.True(car.SirenOn);
        Assert.Single(SirenAcknowledgements(From(recorder, mark), on: true));
        mark = recorder.Sent.Count;
        session.RefreshInventory(car);
        Assert.Single(SirenAcknowledgements(From(recorder, mark), on: true));
        Assert.Empty(SirenTags(From(recorder, mark), on: true));
        session.Deliver(NativeDriverAbility(1111295, 14, false, session.Guid, car.Guid));
        Assert.False(car.SirenOn);
        Assert.Single(SirenTags(From(recorder, mark), on: false));
    }

    [Fact]
    public void ActivePoliceSirenIsRestoredAfterTheCarStreamsOutAndBackIn()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId: 3);
        session.Deliver(NativeDriverAbility(1111295, 14, true, session.Guid, car.Guid));
        Assert.True(session.Exit());
        session.RestreamVehiclesAt(new System.Numerics.Vector3(9000, 51, 9000));
        int mark = recorder.Sent.Count;
        session.RestreamVehiclesAt(car.Position);
        var sent = From(recorder, mark).ToList();
        Assert.True(car.SirenOn);
        Assert.Single(SirenTags(sent, on: true));
        int spawn = sent.FindIndex(packet => packet[1] == 0xd7);
        int effect = sent.FindIndex(packet => packet[1] == 0x0f && packet[2] == 0x15
            && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(11)) == 275);
        Assert.True(spawn >= 0 && effect > spawn);
    }

    [Fact]
    public void DestroyingThePoliceCarRemovesItsActiveSirenEffect()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            VehicleDamage = new VehicleDamageOptions { MaximumCollisionDamage = uint.MaxValue },
        });
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId: 3);
        session.Deliver(NativeDriverAbility(1111295, 14, true, session.Guid, car.Guid));
        int mark = recorder.Sent.Count;
        session.Deliver(Collision(car.Guid, car.Guid, 100000, CollisionDamageCause.VehicleCollision));
        Assert.Equal(0u, car.Health);
        Assert.False(car.SirenOn);
        Assert.Single(SirenTags(From(recorder, mark), on: false));
    }

    [Fact]
    public void PoliceManagerPublishesTheAugustLightBarForTheConfiguredVehicleAbilityBinding()
    {
        var inventory = new VehicleInventory(0x4600000000000025, 123, vehicleId: 3);
        var lightBar = Assert.Single(inventory.Items, item => item.SlotId == 35);
        Assert.Equal(1732u, lightBar.DefinitionId);
        byte[] manager = inventory.AbilityManager(driver: true);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(manager.AsSpan(2));
        byte[] entry = Assert.Single(Enumerable.Range(0, count)
            .Select(index => manager[(6 + index * 33)..(6 + (index + 1) * 33)]),
            row => BinaryPrimitives.ReadUInt32LittleEndian(row) == 14);
        Assert.Equal(14u, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4)));
        Assert.Equal(1111295u, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(12)));
        Assert.Equal(1111295u, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(16)));
        Assert.Equal(1732u, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(28)));
        Assert.Equal(275u, AugustEffectCatalog.IdOf("VEH_SirenLight_PoliceCar"));
        Assert.Equal(275u, VehicleDriverControls.SirenEffect);
        Assert.Equal(4, AugustEffectCatalog.ById(275)!.Value.PartCount);
    }

    private static List<byte[]> SirenTags(IEnumerable<byte[]> packets, bool on) =>
        [.. Sub8(packets, 0x0f, on ? (byte)0x15 : (byte)0x16)
            .Where(packet => packet.Length >= 15
                && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(11)) == 275)];

    private static List<byte[]> SirenAcknowledgements(IEnumerable<byte[]> packets, bool on) =>
        [.. Sub8(packets, 0xa0, on ? (byte)0x0d : (byte)0x0f)
            .Where(packet => packet.Length == 11
                && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(3)) == 1111295
                && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7)) == 14)];
}
