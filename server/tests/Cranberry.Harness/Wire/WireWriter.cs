using System.Buffers.Binary;
using System.Text;

namespace Cranberry.Harness.Wire;

/// <summary>
/// The harness's own growable writer. Mirrors <see cref="WireReader"/>: big-endian for transport
/// headers, little-endian with u32-counted strings for application packets. Written independently
/// of the server's PacketWriter for the reason given on <see cref="HarnessRc4"/>.
/// </summary>
public sealed class WireWriter
{
    private byte[] _buffer;
    private int _length;

    public WireWriter(int capacity = 128) => _buffer = new byte[Math.Max(16, capacity)];

    public int Length => _length;

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => Written.ToArray();

    public WireWriter U8(byte value)
    {
        Reserve(1);
        _buffer[_length++] = value;
        return this;
    }

    public WireWriter Bool(bool value) => U8(value ? (byte)1 : (byte)0);

    public WireWriter BeU16(ushort value)
    {
        Reserve(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_length), value);
        _length += 2;
        return this;
    }

    public WireWriter BeU32(uint value)
    {
        Reserve(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public WireWriter LeU16(ushort value)
    {
        Reserve(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += 2;
        return this;
    }

    public WireWriter LeU32(uint value)
    {
        Reserve(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public WireWriter LeU64(ulong value)
    {
        Reserve(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
        return this;
    }

    public WireWriter LeF32(float value)
    {
        Reserve(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public WireWriter Raw(ReadOnlySpan<byte> bytes)
    {
        Reserve(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        return this;
    }

    /// <summary>u32 byte count then the UTF-8 bytes, no terminator.</summary>
    public WireWriter CountedString(string value)
    {
        int count = Encoding.UTF8.GetByteCount(value);
        LeU32((uint)count);
        Reserve(count);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
        _length += count;
        return this;
    }

    public WireWriter CountedBytes(ReadOnlySpan<byte> bytes)
    {
        LeU32((uint)bytes.Length);
        return Raw(bytes);
    }

    /// <summary>NUL-terminated ASCII, as used by the SOE SessionRequest protocol name.</summary>
    public WireWriter CString(string value)
    {
        int count = Encoding.ASCII.GetByteCount(value);
        Reserve(count + 1);
        Encoding.ASCII.GetBytes(value, _buffer.AsSpan(_length));
        _length += count;
        _buffer[_length++] = 0;
        return this;
    }

    private void Reserve(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        int capacity = _buffer.Length;
        while (capacity < _length + extra)
        {
            capacity *= 2;
        }

        Array.Resize(ref _buffer, capacity);
    }
}
