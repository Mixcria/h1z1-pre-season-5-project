namespace Cranberry.Harness.Wire;

/// <summary>
/// RC4, written from the published algorithm rather than reused from
/// <c>Cranberry.Transport.Rc4Cipher</c>. Sharing the server's implementation would make a
/// key-schedule or keystream bug symmetric and therefore invisible: both ends would be wrong in
/// the same way and every test would pass. This is the harness's independent second opinion.
/// One instance per direction per link; the keystream runs continuously across messages, so a
/// message must be transformed exactly once and in order.
/// </summary>
public sealed class HarnessRc4
{
    private readonly byte[] _s = new byte[256];
    private byte _x;
    private byte _y;

    public HarnessRc4(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("RC4 needs a non-empty key.", nameof(key));
        }

        for (int i = 0; i < 256; i++)
        {
            _s[i] = (byte)i;
        }

        byte j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (byte)(j + _s[i] + key[i % key.Length]);
            Swap(i, j);
        }
    }

    /// <summary>Keystream bytes consumed so far. A desynchronised peer disagrees about this.</summary>
    public long Position { get; private set; }

    /// <summary>XORs the keystream into <paramref name="data"/>. Encrypt and decrypt are the same operation.</summary>
    public void Transform(Span<byte> data)
    {
        for (int n = 0; n < data.Length; n++)
        {
            _x = (byte)(_x + 1);
            _y = (byte)(_y + _s[_x]);
            Swap(_x, _y);
            data[n] ^= _s[(byte)(_s[_x] + _s[_y])];
        }

        Position += data.Length;
    }

    private void Swap(int a, int b) => (_s[a], _s[b]) = (_s[b], _s[a]);
}
