namespace Cranberry.Transport;

/// <summary>
/// RC4 stream cipher (key-scheduling + generation, as published). One instance per direction
/// per session: the keystream runs continuously across every message, so messages must be
/// transformed exactly once, in sequence order.
/// </summary>
public sealed class Rc4Cipher
{
    private readonly byte[] _state = new byte[256];
    private int _i;
    private int _j;

    public Rc4Cipher(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("Key must not be empty.", nameof(key));
        }

        for (int n = 0; n < 256; n++)
        {
            _state[n] = (byte)n;
        }

        int j = 0;
        for (int n = 0; n < 256; n++)
        {
            j = (j + _state[n] + key[n % key.Length]) & 0xFF;
            (_state[n], _state[j]) = (_state[j], _state[n]);
        }
    }

    /// <summary>Bytes transformed so far. A desynchronised peer disagrees about this number.</summary>
    public long Position { get; private set; }

    /// <summary>XORs the keystream into <paramref name="data"/> in place. Encrypts and decrypts alike.</summary>
    public void Transform(Span<byte> data)
    {
        byte[] s = _state;
        int i = _i;
        int j = _j;

        for (int n = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            data[n] ^= s[(s[i] + s[j]) & 0xFF];
        }

        _i = i;
        _j = j;
        Position += data.Length;
    }
}
