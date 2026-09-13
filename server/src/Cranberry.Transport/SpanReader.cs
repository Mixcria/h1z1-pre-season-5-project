using System.Buffers.Binary;
using System.Text;

namespace Cranberry.Transport;

/// <summary>Bounds-checked big-endian cursor over a span. Transport headers are big-endian.</summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public SpanReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public int Position => _position;
    public int Remaining => _buffer.Length - _position;

    public byte ReadByte()
    {
        Require(1);
        return _buffer[_position++];
    }

    public ushort ReadUInt16()
    {
        Require(2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(_position));
        _position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        Require(4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_position));
        _position += 4;
        return value;
    }

    public ulong ReadUInt64()
    {
        Require(8);
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(_buffer.Slice(_position));
        _position += 8;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Require(count);
        ReadOnlySpan<byte> slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    public ReadOnlySpan<byte> ReadRest()
    {
        ReadOnlySpan<byte> slice = _buffer.Slice(_position);
        _position = _buffer.Length;
        return slice;
    }

    /// <summary>Reads an ASCII string terminated by a zero byte, consuming the terminator.</summary>
    public string ReadCString(int maxLength)
    {
        ReadOnlySpan<byte> rest = _buffer.Slice(_position);
        int end = rest.IndexOf((byte)0);
        if (end < 0 || end > maxLength)
        {
            throw new SoeProtocolException($"Unterminated or over-long string (limit {maxLength}).");
        }

        string value = Encoding.ASCII.GetString(rest.Slice(0, end));
        _position += end + 1;
        return value;
    }

    private void Require(int count)
    {
        if (Remaining < count)
        {
            throw new SoeProtocolException($"Need {count} byte(s) at offset {_position}, only {Remaining} left.");
        }
    }
}
