using System.Buffers.Binary;
using System.Text;

namespace Cranberry.Protocol;

/// <summary>Thrown when an application packet does not match its expected layout.</summary>
public sealed class PacketFormatException(string message) : Exception(message);

/// <summary>
/// Bounds-checked little-endian cursor for application packets (login and zone). Strings are
/// a u32 byte count followed by the bytes, no terminator — the form the client uses.
/// </summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public PacketReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public int Position => _position;
    public int Remaining => _buffer.Length - _position;
    public bool AtEnd => _position >= _buffer.Length;

    public byte ReadByte()
    {
        Require(1);
        return _buffer[_position++];
    }

    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadUInt16()
    {
        Require(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
        _position += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        Require(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position));
        _position += 4;
        return v;
    }

    public int ReadInt32()
    {
        Require(4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position));
        _position += 4;
        return v;
    }

    public ulong ReadUInt64()
    {
        Require(8);
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position));
        _position += 8;
        return v;
    }

    public float ReadSingle()
    {
        Require(4);
        float v = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_position));
        _position += 4;
        return v;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0)
        {
            throw new PacketFormatException($"Negative byte count {count}.");
        }

        Require(count);
        ReadOnlySpan<byte> slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>u32 byte count, then that many bytes.</summary>
    public ReadOnlySpan<byte> ReadCountedBytes()
    {
        uint count = ReadUInt32();
        if (count > int.MaxValue)
        {
            throw new PacketFormatException($"Counted block of {count} bytes.");
        }

        return ReadBytes((int)count);
    }

    /// <summary>u32 byte count, then UTF-8 text of that many bytes.</summary>
    public string ReadString() => Encoding.UTF8.GetString(ReadCountedBytes());

    public ReadOnlySpan<byte> ReadRest()
    {
        ReadOnlySpan<byte> slice = _buffer.Slice(_position);
        _position = _buffer.Length;
        return slice;
    }

    public void Skip(int count) => _ = ReadBytes(count);

    private void Require(int count)
    {
        if (Remaining < count)
        {
            throw new PacketFormatException($"Need {count} byte(s) at offset {_position}, only {Remaining} left.");
        }
    }
}
