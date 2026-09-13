namespace Cranberry.Zone.DevConsole;

/// <summary>
/// The August 2017 client's console-command hash (build 0.0.118.208059, ClientProtocol_1148).
/// <para>
/// Derived from the client binary only. The client computes it in <c>FUN_14097fb10</c> and inlines the
/// same loop in <c>FUN_141296a90</c> (the <c>__sendworldcommand</c> executor that fills the u32 hash of
/// <c>Command.ExecuteCommand</c> 09 42), <c>FUN_141280280</c> (the <c>Command.AddWorldCommand</c> 09 40
/// handler that keys the server-name registry) and <c>FUN_141e9d950</c> (the CVar registry lookup that
/// decides "conflicts with a local command"). The 36 built-in constants compiled into
/// <c>FUN_141277a50</c> (<c>attack</c>=0x49acdc9d ... <c>netstats</c>=0x0fcc2872) and the wire-measured
/// <c>HELP</c>=0xd51bdb69 all reproduce (see <c>CommandHashTests</c>, and
/// <c>out\devconsole-20260901\hash-check.py</c> for the byte-exact Python model).
/// </para>
/// <para>
/// Algorithm, in the binary's own terms (32-bit int arithmetic, arithmetic right shifts - the decompile
/// reads <c>(int)h &gt;&gt; 6</c> / <c>&gt;&gt; 0xb</c>, i.e. <c>sar</c>, and that is load-bearing for
/// every vector):
/// <code>
///   h = 0
///   for each byte b of the NUL-terminated name:
///       c = (signed char) FOLD[b]        // FOLD = DAT_143cd0290: ASCII to-upper, identity elsewhere
///       h = (h + c) * 0x401              // h += c ; h += h &lt;&lt; 10
///       h ^= h &gt;&gt; 6                      // arithmetic
///   h *= 9                               // h += h &lt;&lt; 3
///   h ^= h &gt;&gt; 11                         // arithmetic
///   h *= 0x8001                          // h += h &lt;&lt; 15
/// </code>
/// Because the fold table upper-cases a-z, the hash is case-insensitive over ASCII: typing
/// <c>/Give</c>, <c>/give</c> or <c>/GIVE</c> yields the same u32. Bytes 0x80-0xff fold to themselves and
/// are sign-extended (movsx) before the add, exactly as the client does.
/// </para>
/// </summary>
public static class CommandHash
{
    /// <summary>The value the client sends for an unknown / help request: <c>Compute("help")</c>.</summary>
    public const uint Help = 0xd51bdb69;

    /// <summary>Hashes a command name the way the client does (UTF-8 bytes, ASCII case folded).</summary>
    public static uint Compute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Compute(System.Text.Encoding.UTF8.GetBytes(name));
    }

    /// <summary>Hashes raw name bytes (stops at the first NUL, as the client's C string loop does).</summary>
    public static uint Compute(ReadOnlySpan<byte> name)
    {
        int h = 0;
        foreach (byte b in name)
        {
            if (b == 0) break;
            int c = (sbyte)Fold(b);
            h = unchecked((h + c) * 0x401);
            h ^= h >> 6;                       // C# >> on int is arithmetic, like the client's sar
        }
        h = unchecked(h * 9);
        h ^= h >> 11;
        h = unchecked(h * 0x8001);
        return unchecked((uint)h);
    }

    /// <summary>DAT_143cd0290: the 256-entry fold table, ASCII to-upper and identity everywhere else.</summary>
    private static byte Fold(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 0x20) : b;
}
