using System.Buffers.Binary;

namespace Cranberry.Transport;

/// <summary>Bounds-checked big-endian cursor that writes into a caller-supplied span.</summary>
public ref struct SpanWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    public SpanWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public int Written => _position;
    public int Remaining => _buffer.Length - _position;

    public void WriteByte(byte value)
    {
        Require(1);
        _buffer[_position++] = value;
    }

    public void WriteUInt16(ushort value)
    {
        Require(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.Slice(_position), value);
        _position += 2;
    }

    public void WriteUInt32(uint value)
    {
        Require(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.Slice(_position), value);
        _position += 4;
    }

    public void WriteUInt64(ulong value)
    {
        Require(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.Slice(_position), value);
        _position += 8;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length);
        bytes.CopyTo(_buffer.Slice(_position));
        _position += bytes.Length;
    }

    private void Require(int count)
    {
        if (Remaining < count)
        {
            throw new InvalidOperationException($"Buffer of {_buffer.Length} byte(s) cannot take {count} more at {_position}.");
        }
    }
}
