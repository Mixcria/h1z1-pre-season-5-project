using System.Buffers.Binary;

namespace Cranberry.Zone.World;

// Retain packed field bytes, not decoded/re-quantized floats. The ordinary field order is
// ClientMovementUpdate.Parse / FUN_140a3ca40. Precise-pose records are explicit barriers until
// their interaction with ordinary fields can be preserved without changing their wire form.
internal sealed class PeerMovementSnapshot
{
    private static readonly ushort[] Order = [1, 2, 0x20, 0x40, 0x80, 4, 8, 0x10, 0x100, 0x200, 0x400, 0x800];
    private readonly byte[] _fields = new byte[12 * 16];
    private readonly int[] _lengths = new int[12];
    private readonly byte[] _record = new byte[256];
    private ushort _mask;
    private int _length;

    public ReadOnlySpan<byte> Record => _record.AsSpan(0, _length);
    public void Clear() { _mask = 0; _length = 0; Array.Clear(_lengths); }

    public bool Update(ReadOnlySpan<byte> record)
    {
        if (record.Length < 7) { Clear(); return false; }
        ushort mask = BinaryPrimitives.ReadUInt16LittleEndian(record);
        if ((mask & ~0x0fff) != 0) { Clear(); return false; }
        Span<int> lengths = stackalloc int[12];
        lengths.Clear();
        int cursor = 7;
        for (int field = 0; field < Order.Length; field++)
        {
            ushort bit = Order[field];
            if ((mask & bit) == 0) continue;
            int start = cursor;
            int parts = bit is 2 or 0x100 ? 3 : bit == 0x200 ? 4 : 1;
            for (int part = 0; part < parts; part++)
            {
                if (cursor >= record.Length) { Clear(); return false; }
                cursor += bit == 0x20 ? 4 : bit == 1 ? 1 + (record[cursor] & 3) : 1 + ((record[cursor] >> 1) & 3);
                if (cursor > record.Length) { Clear(); return false; }
            }
            lengths[field] = cursor - start;
        }
        if (cursor != record.Length) { Clear(); return false; }
        cursor = 7;
        for (int field = 0; field < Order.Length; field++)
        {
            if (lengths[field] == 0) continue;
            record.Slice(cursor, lengths[field]).CopyTo(_fields.AsSpan(field * 16));
            _lengths[field] = lengths[field];
            cursor += lengths[field];
        }
        _mask |= mask;
        record[..7].CopyTo(_record);
        BinaryPrimitives.WriteUInt16LittleEndian(_record, _mask);
        cursor = 7;
        for (int field = 0; field < Order.Length; field++)
        {
            _fields.AsSpan(field * 16, _lengths[field]).CopyTo(_record.AsSpan(cursor));
            cursor += _lengths[field];
        }
        _length = cursor;
        return true;
    }
}
