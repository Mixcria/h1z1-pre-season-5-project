using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// docs/47 §3 — the finding that explains why the owner saw no doors: <c>AddLightweightNpc</c>'s
/// float4 at client object <c>+0xa0</c> is a <b>quaternion</b>, and wave 3's shipped
/// <see cref="DoorRotation.ZoneEulerYawFirst"/> asked the client for a rotation about the world X
/// axis plus a scale of <c>yaw² + 1</c>.
/// <para>
/// These tests pin the arithmetic that proves it and the substitution that stops it reaching the
/// wire. They are <b>not</b> a live verification: no byte here has been in front of the client.
/// </para>
/// </summary>
public sealed class DoorRotationWireTests
{
    /// <summary>π/2 — 1,400 of Z2's 4,103 door proxies, the commonest yaw on the map.</summary>
    private const float RightAngle = 1.5707964f;

    private static byte[] Write(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// The quantitative half of docs/47 §3c, written out here so the finding cannot quietly rot: read
    /// as a quaternion, <c>(π/2, 0, 0, 1)</c> is a 115° turn about world <b>X</b> and a ×3.47 scale,
    /// because <c>FUN_140c43290</c> builds its matrix without dividing by the squared norm.
    /// </summary>
    [Fact]
    public void TheShippedEulerPackingIsATippedAndInflatedPoseWhenReadAsAQuaternion()
    {
        Vector4 packed = DoorRotationPacking.Pack(RightAngle, DoorRotation.ZoneEulerYawFirst);

        float normSquared = (packed.X * packed.X) + (packed.Y * packed.Y)
            + (packed.Z * packed.Z) + (packed.W * packed.W);
        Assert.Equal(3.4674f, normSquared, 3);                       // the scale the client applies

        float angleDegrees = 2f * MathF.Acos(packed.W / MathF.Sqrt(normSquared)) * 180f / MathF.PI;
        Assert.Equal(115.0f, angleDegrees, 1);                       // and about X, not about Y

        // The axis is the imaginary part: all of it is in X, none of it in Y. That is the whole bug.
        Assert.True(packed.X > 0f);
        Assert.Equal(0f, packed.Y);
        Assert.Equal(0f, packed.Z);
    }

    /// <summary>
    /// The two quaternion packings and the identity are unit quaternions, so the client's
    /// un-normalised matrix build scales the leaf by exactly 1. The two Euler packings are not.
    /// </summary>
    [Theory]
    [InlineData(DoorRotation.QuaternionYUp, true)]
    [InlineData(DoorRotation.QuaternionYUpNegated, true)]
    [InlineData(DoorRotation.Identity, true)]
    [InlineData(DoorRotation.ZoneEulerYawFirst, false)]
    [InlineData(DoorRotation.ZoneEulerYawSecond, false)]
    public void OnlyTheQuaternionPackingsLeaveTheDoorAtItsRealSize(DoorRotation convention, bool isUnit)
    {
        Vector4 packed = DoorRotationPacking.Pack(RightAngle, convention);
        float normSquared = (packed.X * packed.X) + (packed.Y * packed.Y)
            + (packed.Z * packed.Z) + (packed.W * packed.W);

        if (isUnit)
        {
            Assert.Equal(1f, normSquared, 5);
        }
        else
        {
            Assert.True(normSquared > 1.5f, $"{convention} packed to a norm² of {normSquared}");
        }
    }

    /// <summary>
    /// <see cref="DoorRotationPacking.ForWire"/> substitutes only what docs/47 §3b proves impossible.
    /// <see cref="DoorRotation.Identity"/> is a valid unit quaternion and a deliberate fallback, so it
    /// must survive.
    /// </summary>
    [Theory]
    [InlineData(DoorRotation.ZoneEulerYawFirst, DoorRotation.QuaternionYUp)]
    [InlineData(DoorRotation.ZoneEulerYawSecond, DoorRotation.QuaternionYUp)]
    [InlineData(DoorRotation.QuaternionYUp, DoorRotation.QuaternionYUp)]
    [InlineData(DoorRotation.QuaternionYUpNegated, DoorRotation.QuaternionYUpNegated)]
    [InlineData(DoorRotation.Identity, DoorRotation.Identity)]
    public void ForWireSubstitutesExactlyTheProvenImpossiblePackings(
        DoorRotation asked, DoorRotation onTheWire)
    {
        Assert.Equal(onTheWire, DoorRotationPacking.ForWire(asked));
        Assert.Equal(asked != onTheWire, DoorRotationPacking.IsProvenImpossible(asked));
        Assert.Equal(
            DoorRotationPacking.Pack(RightAngle, onTheWire),
            DoorRotationPacking.PackForWire(RightAngle, asked));
    }

    /// <summary>
    /// <b>The regression guard for the P0.</b> <c>ZoneOptions.DoorRotation</c> is not this lane's
    /// file and still defaults to <see cref="DoorRotation.ZoneEulerYawFirst"/>; asking for it must
    /// produce byte-for-byte the record <see cref="DoorRotation.QuaternionYUp"/> produces, so no
    /// option value anyone can set puts 84.5 % of Z2's doors on their face again.
    /// </summary>
    [Fact]
    public void ADoorSpawnNeverCarriesAProvenImpossiblePoseWhateverTheCallerAsksFor()
    {
        static AddLightweightDoor Door(DoorRotation rotation) => new(
            Guid: 0x4400_0000_0000_0001,
            TransientId: 1_000_000,
            ModelId: 9897,
            Position: new Vector3(-111.39320f, 33.55220f, 257.31131f),
            Yaw: RightAngle,
            DoorTableId: 7,
            Rotation: rotation);

        byte[] quaternion = Write(Door(DoorRotation.QuaternionYUp).WriteTo);

        foreach (DoorRotation asked in new[] { DoorRotation.ZoneEulerYawFirst, DoorRotation.ZoneEulerYawSecond })
        {
            AddLightweightDoor door = Door(asked);
            Assert.Equal(DoorRotation.QuaternionYUp, door.EffectiveRotation);
            Assert.Equal(quaternion, Write(door.WriteTo));
        }

        // The sign sweep still has to reach the wire, or docs/47 §7 q2 could not be answered.
        Assert.NotEqual(quaternion, Write(Door(DoorRotation.QuaternionYUpNegated).WriteTo));
        Assert.Equal(
            DoorRotation.QuaternionYUpNegated,
            Door(DoorRotation.QuaternionYUpNegated).EffectiveRotation);
    }

    /// <summary>
    /// The default a caller gets when it names no convention at all — <see cref="AddLightweightDoor"/>
    /// and <see cref="DoorInstance.Spawn"/> and <see cref="MatchDoorOptions"/> must agree, or a door
    /// spawned through one path would be posed differently from one spawned through another.
    /// </summary>
    [Fact]
    public void EveryDefaultInThisLaneIsTheQuaternionPacking()
    {
        var spawn = new AddLightweightDoor(1, 2, 9897, Vector3.Zero, RightAngle, DoorTableId: 7);

        Assert.Equal(DoorRotation.QuaternionYUp, spawn.Rotation);
        Assert.Equal(DoorRotation.QuaternionYUp, spawn.EffectiveRotation);
        Assert.Equal(DoorRotation.QuaternionYUp, MatchDoorOptions.Default.Rotation);
    }
}
