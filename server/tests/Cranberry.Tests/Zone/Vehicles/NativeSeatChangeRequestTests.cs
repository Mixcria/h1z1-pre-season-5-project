using System.Buffers.Binary;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Theory]
    [InlineData("700A0200000000", 2u, (byte)0)]
    [InlineData("700A0300000001", 3u, (byte)1)]
    [InlineData("700AFFFFFFFFFE", uint.MaxValue, (byte)254)]
    public void NativeSeatRequest_DecodesTheExactSevenByteSerializer(string hex, uint seat, byte mode)
    {
        // Manually transcribed native 140e84fc0 layout: base, sub, u32 seat, u8 mode.
        byte[] packet = Convert.FromHexString(hex);
        Assert.Equal(7, packet.Length);
        Assert.True(SeatChangeRequest.TryParse(packet, out var parsed));
        Assert.Equal(seat, parsed!.Seat);
        Assert.Equal(mode, parsed.Mode);
        Assert.Equal(packet, parsed.Raw.ToArray());
    }

    [Fact]
    public void NativeSeatRequest_RejectsEveryTruncationAndWrongFamilyOrSubtype()
    {
        byte[] packet = Convert.FromHexString("700A0100000000");
        for (int length = 0; length < packet.Length; length++)
            Assert.False(SeatChangeRequest.TryParse(packet.AsSpan(0, length), out _));
        packet[0] = 0x71;
        Assert.False(SeatChangeRequest.TryParse(packet, out _));
        packet[0] = 0x70;
        packet[1] = 0x0b;
        Assert.False(SeatChangeRequest.TryParse(packet, out _));
    }

    [Fact]
    public void NativeSeatRequest_TruncationHasNoSideEffectsAndTheNextValidRequestWorks()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        ulong originalOwner = car.OwnerGuid;
        long originalCooldown = car.LastSeatChangeMs;
        int mark = recorder.Sent.Count;

        session.Deliver(Convert.FromHexString("700A02000000")); // Missing mode byte.

        Assert.Equal(0, car.SeatOf(session.Guid));
        Assert.Equal(originalOwner, car.OwnerGuid);
        Assert.Equal(originalCooldown, car.LastSeatChangeMs);
        Assert.Empty(From(recorder, mark));

        session.Deliver(Convert.FromHexString("700A0200000000"));

        Assert.Equal(2, car.SeatOf(session.Guid));
        byte[] response = Assert.Single(Sub8(From(recorder, mark), 0x70, SeatChangeResponse.SubOpcode));
        Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(3)));
        Assert.Equal(car.Guid, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(11)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(59)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(63)));
    }

    [Fact]
    public void NativeSeatResponse_AcceptsPassengerChangesAndRestoresDriverOwnershipOnReturn()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();

        foreach (uint seat in new uint[] { 2, 1, 0 })
        {
            car.LastSeatChangeMs = long.MinValue; // Exercise transitions without a wall-clock sleep.
            int mark = recorder.Sent.Count;
            session.Deliver(SeatChange(seat));

            Assert.Equal((int)seat, car.SeatOf(session.Guid));
            Assert.Equal(seat == 0 ? session.Guid : 0ul, car.OwnerGuid);
            byte[] response = Assert.Single(Sub8(From(recorder, mark), 0x70, SeatChangeResponse.SubOpcode));
            Assert.Equal(session.Guid, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(3)));
            Assert.Equal(car.Guid, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(11)));
            Assert.Equal(seat, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(55)));
            // +1 is the gateway tunnel byte. Native 140c9fee0 checks zone+58 ==1
            // before invoking 140c3b4f0, which consumes zone+62 as the driver flag.
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(59)));
            Assert.Equal(seat == 0 ? 1u : 0u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(63)));
        }
    }

    [Fact]
    public void NativeSeatRequest_RejectsAnUnmountedSenderWithoutMovingAnyOccupant()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        Assert.True(session.Exit());
        int mark = recorder.Sent.Count;

        session.Deliver(Convert.FromHexString("700A0100000000"));

        Assert.Equal(-1, car.SeatOf(session.Guid));
        Assert.Equal(0ul, car.DriverGuid);
        Assert.Empty(From(recorder, mark));
    }

    [Fact]
    public void NativeSeatRequest_ModeDoesNotBypassTheExistingServerMotionGuard()
    {
        var (service, connection, recorder) = Admit();
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar();
        car.LastSpeed = 10;
        int mark = recorder.Sent.Count;

        session.Deliver(Convert.FromHexString("700A0100000001"));

        Assert.Equal(0, car.SeatOf(session.Guid));
        Assert.Equal(session.Guid, car.DriverGuid);
        Assert.Empty(From(recorder, mark));
    }
}
