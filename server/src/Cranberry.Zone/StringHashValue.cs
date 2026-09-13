using System.Text;
using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// One entry of the client's StringHashToValueManager (the global map <c>DAT_143f696d0</c>):
/// a dotted name, its value string, and the client's own 32-bit hash of the name, which is what
/// every consumer looks the entry up by (the binary bakes the hashes in, e.g.
/// <c>FUN_14220be70</c> reads <c>0x8f15ddc2</c> = <c>Model.DescriptorReplaceString</c> while
/// building a model file name and dereferences null when the entry is missing).
/// </summary>
/// <param name="Name">Dotted variable name, e.g. <c>Network.KeepAlive</c> (entry+0x28, StringFixed&lt;64&gt;).</param>
/// <param name="Value">Value string (entry+0x8); the client also stores <c>atof(value)</c> at entry+0x18.</param>
/// <param name="Hash">The key at entry+0x98: <see cref="HashName"/> of <paramref name="Name"/>.</param>
public sealed record StringHashValue(string Name, string Value, uint Hash)
{
    public StringHashValue(string name, string value)
        : this(name, value, HashName(name))
    {
    }

    /// <summary>
    /// The client's name hash, from <c>FUN_140b80030</c> (UpdateStringHashToValueManager): a
    /// one-at-a-time hash with arithmetic (signed) shifts — <c>h = (c + h) * 0x401; h ^= h &gt;&gt; 6</c>
    /// per signed char, then <c>t = h * 9; (t ^ t &gt;&gt; 11) * 0x8001</c>.
    /// </summary>
    public static uint HashName(string name)
    {
        int h = 0;
        foreach (byte b in Encoding.Latin1.GetBytes(name))
        {
            int c = (sbyte)b;
            h = unchecked((c + h) * 0x401);
            h ^= h >> 6;
        }

        int t = unchecked(h * 9);
        return unchecked((uint)((t ^ (t >> 11)) * 0x8001));
    }

    /// <summary>
    /// Writes one record the way <c>FUN_140a4dc30</c> reads it: <c>i32 hash; str value; u8 flag;
    /// str name</c>. The flag byte lands in entry+0x20 (its meaning is not yet derived; 0 is what
    /// the update path stores for plain sets).
    /// </summary>
    public void WriteTo(PacketWriter writer, bool flag = false)
    {
        writer.WriteUInt32(Hash);
        writer.WriteString(Value);
        writer.WriteBool(flag);
        writer.WriteString(Name);
    }

    /// <summary>
    /// Writes the list <c>FUN_140a4dc30</c> parses: <c>i32 count</c> then the records. The parser
    /// empties the client's map before reading, so the list must be complete — a short or empty
    /// list removes the defaults the client loaded from its own <c>StringHashToValue.txt</c>.
    /// </summary>
    public static void WriteList(PacketWriter writer, IReadOnlyList<StringHashValue>? entries)
    {
        entries ??= [];
        writer.WriteInt32(entries.Count);
        foreach (StringHashValue entry in entries)
        {
            entry.WriteTo(writer);
        }
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 0xfb: <c>u8 0xfb</c> followed by the
/// <see cref="StringHashValue.WriteList"/> list. The dispatcher hands <c>FUN_140a63ae0</c> the
/// global map, and <c>FUN_140a4dc30</c> replaces the map's content with the list.
/// </summary>
public sealed record StringHashToValueManager(IReadOnlyList<StringHashValue> Entries)
{
    public const byte Opcode = ZoneOpcodes.StringHashToValueManager;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        StringHashValue.WriteList(writer, Entries);
    }
}
