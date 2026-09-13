using System.Buffers.Binary;

namespace Cranberry.Zone;

/// <summary>
/// A raw LZ4 block for the August ReferenceData decoder (FUN_14220e860 ->
/// FUN_14212a7b0 -> FUN_14212b6a0). Lengths belong to the ReferenceData envelope;
/// this block has no frame header or trailer. Only trusted server tables are encoded.
/// </summary>
internal static class ReferenceDataCompression
{
    public static byte[] Encode(ReadOnlySpan<byte> input)
    {
        byte[] output = new byte[checked(input.Length + input.Length / 255 + 16)];
        int[] heads = new int[65536], previous = new int[65536];
        Array.Fill(heads, -1);
        Array.Fill(previous, -1);
        int cursor = 0, anchor = 0, written = 0;
        // Historic LZ4 decoders require a final literal sequence of at least five
        // bytes, and the final match must begin at least twelve bytes from the end.
        int lastMatch = input.Length - 12, matchLimit = input.Length - 5;
        while (cursor <= lastMatch)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(input[cursor..]);
            int hash = (int)(unchecked(word * 2654435761u) >> 16);
            int candidate = heads[hash];
            previous[cursor & 65535] = candidate;
            heads[hash] = cursor;
            int best = 3, offset = 0, oldest = Math.Max(0, cursor - 65535);
            for (int attempts = 0; candidate >= oldest && attempts < 64; attempts++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(input[candidate..]) == word)
                {
                    int length = 4;
                    while (cursor + length < matchLimit && input[candidate + length] == input[cursor + length]) length++;
                    if (length > best) { best = length; offset = cursor - candidate; }
                    if (cursor + length == matchLimit) break;
                }
                candidate = previous[candidate & 65535];
            }
            if (offset == 0) { cursor++; continue; }

            int literals = cursor - anchor, match = best - 4;
            output[written++] = (byte)((Math.Min(literals, 15) << 4) | Math.Min(match, 15));
            if (literals >= 15) WriteExtra(output, ref written, literals - 15);
            input.Slice(anchor, literals).CopyTo(output.AsSpan(written));
            written += literals;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(written), (ushort)offset);
            written += 2;
            if (match >= 15) WriteExtra(output, ref written, match - 15);

            int end = cursor + best;
            // Index the skipped bytes so later matches can use any preceding row.
            for (int i = cursor + 1; i < end && i <= lastMatch; i++)
            {
                int h = (int)(unchecked(BinaryPrimitives.ReadUInt32LittleEndian(input[i..]) * 2654435761u) >> 16);
                previous[i & 65535] = heads[h];
                heads[h] = i;
            }
            cursor = anchor = end;
        }
        int remaining = input.Length - anchor;
        output[written++] = (byte)(Math.Min(remaining, 15) << 4);
        if (remaining >= 15) WriteExtra(output, ref written, remaining - 15);
        input[anchor..].CopyTo(output.AsSpan(written));
        return output.AsSpan(0, written + remaining).ToArray();
    }

    private static void WriteExtra(byte[] output, ref int cursor, int length)
    {
        while (length >= 255) { output[cursor++] = 255; length -= 255; }
        output[cursor++] = (byte)length;
    }
}
