using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Cranberry.Protocol;

/// <summary>
/// Growable little-endian writer for application packets. Rent one, write, hand
/// <see cref="Written"/> to the transport, dispose to return the buffer.
/// </summary>
public sealed class PacketWriter : IDisposable
{
    private byte[] _buffer;
    private int _position;

    public PacketWriter(int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public int Position => _position;

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _position);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _position);

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_position++] = value;
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteInt32(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    public void WriteSingle(float value)
    {
        Ensure(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    /// <summary>Raw bytes, no count.</summary>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_position));
        _position += bytes.Length;
    }

    /// <summary>u32 byte count, then the bytes.</summary>
    public void WriteCountedBytes(ReadOnlySpan<byte> bytes)
    {
        WriteUInt32((uint)bytes.Length);
        WriteRaw(bytes);
    }

    /// <summary>u32 byte count, then UTF-8 text.</summary>
    public void WriteString(string value)
    {
        int count = Encoding.UTF8.GetByteCount(value);
        WriteUInt32((uint)count);
        Ensure(count);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position));
        _position += count;
    }

    /// <summary>Reserves a u32 slot to be filled by <see cref="PatchUInt32"/> once its value is known.</summary>
    public int ReserveUInt32()
    {
        int at = _position;
        WriteUInt32(0);
        return at;
    }

    public void PatchUInt32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(at), value);

    private void Ensure(int count)
    {
        if (_buffer.Length - _position >= count)
        {
            return;
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _position + count));
        _buffer.AsSpan(0, _position).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }
}
