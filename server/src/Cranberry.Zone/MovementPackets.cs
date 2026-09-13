using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// Fields selected by the 16-bit mask at the start of an August client movement record.
/// Names that are not yet proven semantically retain the destination offset used by
/// <c>FUN_140a3ca40</c> instead of guessing what the value means.
/// </summary>
[Flags]
public enum MovementFieldMask : ushort
{
    Posture = 0x0001,
    Position = 0x0002,
    Scalar154 = 0x0004,
    VerticalSpeed = 0x0008,
    HorizontalSpeed = 0x0010,
    Orientation = 0x0020,
    Scalar14C = 0x0040,
    Scalar150 = 0x0080,
    AuxiliaryVector = 0x0100,
    Rotation = 0x0200,
    Scalar140 = 0x0400,
    Scalar144 = 0x0800,
    PrecisePose = 0x1000,

    All = 0x1fff,
}

/// <summary>
/// The seven values selected by movement bit 0x1000: world position followed by rotation,
/// all carried at two decimal places by the August client.
/// </summary>
public readonly record struct PreciseMovementPose(Vector3 Position, Quaternion Rotation);

/// <summary>
/// One opcode-free channel-2 record sent by the local August client. The decoder follows the
/// exact read order of <c>FUN_140a3ca40</c>; that order is deliberately not numeric flag order.
/// The original bytes are retained so another client can receive the movement sample without
/// a lossy float decode/re-encode.
/// </summary>
public sealed class ClientMovementUpdate
{
    private const ushort KnownMask = (ushort)MovementFieldMask.All;
    private readonly byte[] _payload;

    private ClientMovementUpdate(
        MovementFieldMask fields,
        uint clientTime,
        byte state,
        uint? posture,
        Vector3? position,
        float? orientation,
        float? scalar14C,
        float? scalar150,
        float? scalar154,
        float? verticalSpeed,
        float? horizontalSpeed,
        Vector3? auxiliaryVector,
        Quaternion? rotation,
        float? scalar140,
        float? scalar144,
        PreciseMovementPose? precisePose,
        ReadOnlySpan<byte> payload)
    {
        Fields = fields;
        ClientTime = clientTime;
        State = state;
        Posture = posture;
        Position = position;
        Orientation = orientation;
        Scalar14C = scalar14C;
        Scalar150 = scalar150;
        Scalar154 = scalar154;
        VerticalSpeed = verticalSpeed;
        HorizontalSpeed = horizontalSpeed;
        AuxiliaryVector = auxiliaryVector;
        Rotation = rotation;
        Scalar140 = scalar140;
        Scalar144 = scalar144;
        PrecisePose = precisePose;
        _payload = payload.ToArray();
    }

    public MovementFieldMask Fields { get; }
    public uint ClientTime { get; }
    public byte State { get; }
    public uint? Posture { get; }
    public Vector3? Position { get; }
    public float? Orientation { get; }
    public float? Scalar14C { get; }
    public float? Scalar150 { get; }
    public float? Scalar154 { get; }
    public float? VerticalSpeed { get; }
    public float? HorizontalSpeed { get; }
    public Vector3? AuxiliaryVector { get; }
    public Quaternion? Rotation { get; }
    public float? Scalar140 { get; }
    public float? Scalar144 { get; }
    public PreciseMovementPose? PrecisePose { get; }

    /// <summary>
    /// The ordinary position when present, otherwise the position carried by a precise-pose
    /// update. Seated and client-managed actors can send only the latter form.
    /// </summary>
    public Vector3? EffectivePosition => Position ?? PrecisePose?.Position;

    /// <summary>The exact client bytes, excluding the gateway header and entity id.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    public bool Has(MovementFieldMask field) => (Fields & field) != 0;

    /// <summary>
    /// Parses one complete client-to-server channel-2 payload. Unknown flag bits, truncation,
    /// and trailing bytes are rejected so a malformed sample cannot silently shift field
    /// boundaries before it is relayed to another player.
    /// </summary>
    public static ClientMovementUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        ushort rawFields = reader.ReadUInt16();
        ushort unknownFields = (ushort)(rawFields & ~KnownMask);
        if (unknownFields != 0)
        {
            throw new PacketFormatException($"Movement record has unknown flag bits 0x{unknownFields:X4}.");
        }

        var fields = (MovementFieldMask)rawFields;
        uint clientTime = reader.ReadUInt32();
        byte state = reader.ReadByte();

        uint? posture = Has(fields, MovementFieldMask.Posture)
            ? ReadPackedUnsigned(ref reader)
            : null;

        Vector3? position = Has(fields, MovementFieldMask.Position)
            ? ReadVector3(ref reader, scale: 100f)
            : null;

        float? orientation = Has(fields, MovementFieldMask.Orientation)
            ? reader.ReadSingle()
            : null;

        // FUN_140a3ca40 reads 0x40, 0x80, then 0x04; preserving that unusual order is vital.
        float? scalar14C = Has(fields, MovementFieldMask.Scalar14C)
            ? ReadScaledSigned(ref reader, scale: 100f)
            : null;
        float? scalar150 = Has(fields, MovementFieldMask.Scalar150)
            ? ReadScaledSigned(ref reader, scale: 100f)
            : null;
        float? scalar154 = Has(fields, MovementFieldMask.Scalar154)
            ? ReadScaledSigned(ref reader, scale: 100f)
            : null;
        float? verticalSpeed = Has(fields, MovementFieldMask.VerticalSpeed)
            ? ReadScaledSigned(ref reader, scale: 100f)
            : null;
        float? horizontalSpeed = Has(fields, MovementFieldMask.HorizontalSpeed)
            ? ReadScaledSigned(ref reader, scale: 10f)
            : null;

        Vector3? auxiliaryVector = Has(fields, MovementFieldMask.AuxiliaryVector)
            ? ReadVector3(ref reader, scale: 100f)
            : null;
        Quaternion? rotation = Has(fields, MovementFieldMask.Rotation)
            ? ReadQuaternion(ref reader, scale: 100f)
            : null;
        float? scalar140 = Has(fields, MovementFieldMask.Scalar140)
            ? ReadScaledSigned(ref reader, scale: 10f)
            : null;
        float? scalar144 = Has(fields, MovementFieldMask.Scalar144)
            ? ReadScaledSigned(ref reader, scale: 10f)
            : null;

        PreciseMovementPose? precisePose = null;
        if (Has(fields, MovementFieldMask.PrecisePose))
        {
            // FUN_140a3ca40 uses the same 1 / pow(10, 2) multiplier as the other vectors.
            Vector3 precisePosition = ReadVector3(ref reader, scale: 100f);
            Quaternion preciseRotation = ReadQuaternion(ref reader, scale: 100f);
            precisePose = new PreciseMovementPose(precisePosition, preciseRotation);
        }

        if (!reader.AtEnd)
        {
            throw new PacketFormatException(
                $"Movement record has {reader.Remaining} trailing byte(s) after flags 0x{rawFields:X4}.");
        }

        return new ClientMovementUpdate(
            fields,
            clientTime,
            state,
            posture,
            position,
            orientation,
            scalar14C,
            scalar150,
            scalar154,
            verticalSpeed,
            horizontalSpeed,
            auxiliaryVector,
            rotation,
            scalar140,
            scalar144,
            precisePose,
            payload);
    }

    /// <summary>
    /// Writes the server-to-client channel-2 body: packed transient id followed by the exact
    /// movement record. The caller supplies gateway channel 2 framing.
    /// </summary>
    public void WriteForEntity(PacketWriter writer, uint transientId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ClientVarInt.Write(writer, transientId);
        writer.WriteRaw(_payload);
    }

    private static bool Has(MovementFieldMask fields, MovementFieldMask field) =>
        (fields & field) != 0;

    private static Vector3 ReadVector3(ref PacketReader reader, float scale) => new(
        ReadScaledSigned(ref reader, scale),
        ReadScaledSigned(ref reader, scale),
        ReadScaledSigned(ref reader, scale));

    private static Quaternion ReadQuaternion(ref PacketReader reader, float scale) => new(
        ReadScaledSigned(ref reader, scale),
        ReadScaledSigned(ref reader, scale),
        ReadScaledSigned(ref reader, scale),
        ReadScaledSigned(ref reader, scale));

    private static float ReadScaledSigned(ref PacketReader reader, float scale) =>
        ReadPackedSigned(ref reader) / scale;

    /// <summary>FUN_140a190f0: low two bits are the number of extra bytes.</summary>
    private static uint ReadPackedUnsigned(ref PacketReader reader)
    {
        byte first = reader.ReadByte();
        int extra = first & 0x03;
        uint packed = first;
        for (int i = 1; i <= extra; i++)
        {
            packed |= (uint)reader.ReadByte() << (i * 8);
        }

        return packed >> 2;
    }

    /// <summary>FUN_140a18f40: bit 0 is sign; bits 1-2 are the number of extra bytes.</summary>
    private static int ReadPackedSigned(ref PacketReader reader)
    {
        byte first = reader.ReadByte();
        bool negative = (first & 0x01) != 0;
        int extra = (first >> 1) & 0x03;
        uint packed = first;
        for (int i = 1; i <= extra; i++)
        {
            packed |= (uint)reader.ReadByte() << (i * 8);
        }

        int magnitude = (int)(packed >> 3);
        return negative ? -magnitude : magnitude;
    }
}
