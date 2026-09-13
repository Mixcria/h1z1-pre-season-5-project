using System.Buffers;
using System.Buffers.Binary;

namespace Cranberry.Transport;

/// <summary>
/// Rebuilds one application message from a run of DataFragment payloads. The first fragment of
/// a group starts with the total message length as a big-endian u32; later fragments are raw.
/// </summary>
internal sealed class ReassemblyBuffer
{
    public const int MaxMessageLength = 1 << 20;

    private byte[]? _buffer;
    private int _expected;
    private int _filled;

    public bool InProgress => _buffer is not null;

    /// <summary>
    /// Adds the next in-order fragment. When the group completes, returns true and hands out the
    /// rented buffer holding the message; the caller must return it to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public bool Add(ReadOnlySpan<byte> fragment, out byte[] message, out int length)
    {
        if (_buffer is null)
        {
            if (fragment.Length < 4)
            {
                throw new SoeProtocolException("First fragment is shorter than its length prefix.");
            }

            uint declared = BinaryPrimitives.ReadUInt32BigEndian(fragment);
            if (declared == 0 || declared > MaxMessageLength)
            {
                throw new SoeProtocolException($"Fragment group declares {declared} byte(s).");
            }

            _buffer = ArrayPool<byte>.Shared.Rent((int)declared);
            _expected = (int)declared;
            _filled = 0;
            fragment = fragment.Slice(4);
        }

        if (_filled + fragment.Length > _expected)
        {
            Reset();
            throw new SoeProtocolException("Fragment group overflows its declared length.");
        }

        fragment.CopyTo(_buffer.AsSpan(_filled));
        _filled += fragment.Length;

        if (_filled == _expected)
        {
            message = _buffer;
            length = _expected;
            _buffer = null;
            return true;
        }

        message = [];
        length = 0;
        return false;
    }

    public void Reset()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}
