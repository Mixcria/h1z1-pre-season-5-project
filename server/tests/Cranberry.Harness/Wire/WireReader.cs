using System.Buffers.Binary;
using System.Text;

namespace Cranberry.Harness.Wire;

/// <summary>Thrown when bytes on the wire do not match the layout the harness expects.</summary>
public sealed class WireFormatException(string message) : Exception(message);

/// <summary>
/// The harness's own bounds-checked cursor. SOE transport headers are big-endian; application
/// packets (login and zone) are little-endian with u32-counted strings. Both live here so the
/// harness never borrows the server's readers — an endianness mistake shared by both sides is a
/// mistake no test can see.
/// </summary>
public ref struct WireReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position = 0;

    public readonly int Position => _position;

    public readonly int Remaining => _buffer.Length - _position;

    public readonly bool AtEnd => _position >= _buffer.Length;

    public byte U8()
    {
        Need(1);
        return _buffer[_position++];
    }

    public bool Bool() => U8() != 0;

    public ushort BeU16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(_buffer[_position..]);
        _position += 2;
        return v;
    }

    public uint BeU32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_buffer[_position..]);
        _position += 4;
        return v;
    }

    public ushort LeU16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_position..]);
        _position += 2;
        return v;
    }

    public uint LeU32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_position..]);
        _position += 4;
        return v;
    }

    public ulong LeU64()
    {
        Need(8);
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_buffer[_position..]);
        _position += 8;
        return v;
    }

    public float LeF32()
    {
        Need(4);
        float v = BinaryPrimitives.ReadSingleLittleEndian(_buffer[_position..]);
        _position += 4;
        return v;
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Need(count);
        ReadOnlySpan<byte> slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    public ReadOnlySpan<byte> Rest()
    {
        ReadOnlySpan<byte> slice = _buffer[_position..];
        _position = _buffer.Length;
        return slice;
    }

    /// <summary>A u32 byte count followed by that many bytes: the client's own string form.</summary>
    public string CountedString()
    {
        uint count = LeU32();
        if (count > (uint)Remaining)
        {
            throw new WireFormatException(
                $"Counted string of {count} byte(s) at offset {_position} runs past the {_buffer.Length}-byte message.");
        }

        return Encoding.UTF8.GetString(Bytes((int)count));
    }

    /// <summary>A u32 byte count followed by that many bytes, returned raw.</summary>
    public ReadOnlySpan<byte> CountedBytes()
    {
        uint count = LeU32();
        if (count > (uint)Remaining)
        {
            throw new WireFormatException(
                $"Counted block of {count} byte(s) at offset {_position} runs past the {_buffer.Length}-byte message.");
        }

        return Bytes((int)count);
    }

    /// <summary>NUL-terminated ASCII, as used by the SOE SessionRequest protocol name.</summary>
    public string CString(int maxLength)
    {
        ReadOnlySpan<byte> rest = _buffer[_position..];
        int end = rest.IndexOf((byte)0);
        if (end < 0 || end > maxLength)
        {
            throw new WireFormatException($"Unterminated or over-long C string (limit {maxLength}).");
        }

        string value = Encoding.ASCII.GetString(rest[..end]);
        _position += end + 1;
        return value;
    }

    private readonly void Need(int count)
    {
        if (count < 0 || Remaining < count)
        {
            throw new WireFormatException(
                $"Need {count} byte(s) at offset {_position}; only {Remaining} of {_buffer.Length} left.");
        }
    }
}
