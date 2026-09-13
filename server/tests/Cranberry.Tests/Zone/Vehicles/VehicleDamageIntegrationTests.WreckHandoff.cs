using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed partial class VehicleDamageIntegrationTests
{
    [Theory]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(5u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(5u, true)]
    public void WreckReleaseSynchronizesTheFinalPhysicsTransform(uint vehicleId, bool coasting)
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            VehicleDamage = new VehicleDamageOptions
            {
                MaximumCollisionDamage = uint.MaxValue,
                ExplosionDamage = 0,
            },
        });
        var session = service.ForVehicleTest(connection);
        var car = session.EnterMatchWithCar(vehicleId);
        var finalPosition = new Vector3(1467.35f, 51.29f, -2236.71f);
        var finalRotation = Quaternion.CreateFromYawPitchRoll(1.4f, 0.3f, -0.2f);
        Assert.True(session.Fleet.TryApplyOwnerPose(car.TransientId, session.Guid,
            finalPosition, 1.4f, Environment.TickCount64, out _));
        session.Fleet.NoteAttitude(car, finalRotation, -0.25f);
        car.MovementVersion = 7;

        if (coasting)
        {
            int exitMark = recorder.Sent.Count;
            Assert.True(session.Exit());
            Assert.DoesNotContain(From(recorder, exitMark), packet =>
                packet[1] == 0x78 || IsWreckManagedLocation(packet));
            Assert.Equal(session.Guid, car.CoastingOwnerGuid);
        }

        int mark = recorder.Sent.Count;
        session.Deliver(Collision(car.Guid, car.Guid, 500_000,
            CollisionDamageCause.VehicleCollision));

        Assert.Equal(0u, car.Health);
        Assert.Equal(finalPosition, car.Position);
        Assert.Equal(0ul, car.CoastingOwnerGuid);
        var sent = From(recorder, mark).ToList();
        int controlRelease = sent.FindIndex(packet =>
            packet.Length > 3 && packet[1] == 0x11 && packet[2] == 0x39 && packet[3] == 0);
        int ownershipRelease = sent.FindIndex(packet =>
            packet.Length > 2 && packet[1] == 0x0f && packet[2] == 0x3b);
        int transform = sent.FindIndex(IsWreckManagedLocation);
        int baseline = sent.FindIndex(packet => packet[1] == 0x78);
        Assert.True(controlRelease >= 0 && ownershipRelease > controlRelease
            && transform > ownershipRelease && baseline > transform);
        int destroyed = sent.FindIndex(packet => packet.Length > 2 && packet[1] == 0x0f && packet[2] == 0x25);
        Assert.True(destroyed > baseline); // Release physics and establish the final pose before swapping the model.
        int dismount = sent.FindIndex(packet => packet[1] == 0x70 && packet[2] == 4);
        if (coasting) Assert.Equal(-1, dismount);
        else Assert.True(dismount >= 0 && dismount < controlRelease);

        byte[] location = Assert.Single(sent, IsWreckManagedLocation);
        Assert.Equal(VehicleManagedLocation.Length + 1, location.Length);
        Assert.Equal(car.Guid, BinaryPrimitives.ReadUInt64LittleEndian(location.AsSpan(4)));
        float ReadFloat(int offset) => BinaryPrimitives.ReadSingleLittleEndian(location.AsSpan(offset));
        Assert.Equal(finalPosition, new Vector3(ReadFloat(12), ReadFloat(16), ReadFloat(20)));
        Assert.Equal(1f, ReadFloat(24));
        var rotation = new Quaternion(ReadFloat(28), ReadFloat(32), ReadFloat(36), ReadFloat(40));
        Assert.True(MathF.Abs(Quaternion.Dot(finalRotation, rotation)) > 0.999f);
        Assert.Equal(1, location[44]);
        Assert.Equal(7, location[45]);
    }

    private static bool IsWreckManagedLocation(byte[] packet) =>
        packet.Length > 3 && packet[1] == 0x11 && packet[2] == 0x23 && packet[3] == 0;
}
