using System.Buffers.Binary;

namespace Cranberry.Transport;

/// <summary>
/// The variable-length size that prefixes each sub-packet inside a <see cref="SoeOpcode.Multi"/>
/// datagram. Wire facts:
/// <list type="bullet">
/// <item>first byte below 0xFF: that byte is the length (1 byte total)</item>
/// <item>first byte 0xFF, next two bytes not both 0xFF: big-endian u16 follows (3 bytes total)</item>
/// <item>first three bytes all 0xFF: big-endian u32 follows (7 bytes total)</item>
/// </list>
/// </summary>
public static class SoeVarInt
{
    public static int Read(ReadOnlySpan<byte> buffer, ref int offset)
    {
        Demand(buffer, offset, 1);
        byte first = buffer[offset];
        if (first < 0xFF)
        {
            offset += 1;
            return first;
        }

        Demand(buffer, offset, 3);
        if (buffer[offset + 1] == 0xFF && buffer[offset + 2] == 0xFF)
        {
            Demand(buffer, offset, 7);
            uint wide = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 3));
            if (wide > int.MaxValue)
            {
                throw new SoeProtocolException("Sub-packet length does not fit in an int.");
            }

            offset += 7;
            return (int)wide;
        }

        ushort medium = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 1));
        offset += 3;
        return medium;
    }

    public static int EncodedSize(int value) => value switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(value)),
        < 0xFF => 1,
        < 0xFFFF => 3,
        _ => 7,
    };

    public static int Write(Span<byte> buffer, int value)
    {
        int size = EncodedSize(value);
        if (buffer.Length < size)
        {
            throw new InvalidOperationException("Buffer too small for the length prefix.");
        }

        switch (size)
        {
            case 1:
                buffer[0] = (byte)value;
                break;
            case 3:
                buffer[0] = 0xFF;
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(1), (ushort)value);
                break;
            default:
                buffer[0] = 0xFF;
                buffer[1] = 0xFF;
                buffer[2] = 0xFF;
                BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(3), (uint)value);
                break;
        }

        return size;
    }

    private static void Demand(ReadOnlySpan<byte> buffer, int offset, int count)
    {
        if (offset < 0 || buffer.Length - offset < count)
        {
            throw new SoeProtocolException($"Sub-packet length prefix at {offset} runs past the datagram.");
        }
    }
}
