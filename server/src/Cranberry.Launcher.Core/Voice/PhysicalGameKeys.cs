using System.Buffers.Binary;

namespace Cranberry.Launcher.Core.Voice;

/// <summary>Current button states only; no text, input history or device identities are retained.</summary>
public sealed class PhysicalGameKeys
{
    private readonly int[] _down = new int[256];
    public bool Down(int key) => key switch
    {
        0x10 => Down(0xA0) || Down(0xA1),
        0x11 => Down(0xA2) || Down(0xA3),
        0x12 => Down(0xA4) || Down(0xA5),
        >= 0 and < 256 => Volatile.Read(ref _down[key]) != 0,
        _ => false,
    };
    public void Clear() { for (int i = 0; i < _down.Length; i++) Volatile.Write(ref _down[i], 0); }
    public void ReadRawPacket(ReadOnlySpan<byte> packet, int headerSize)
    {
        if (headerSize is not (16 or 24) || packet.Length < headerSize) return;
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        var data = packet[headerSize..];
        if (type == 0 && data.Length >= 24)
        {
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            ReadOnlySpan<int> keys = [1, 2, 4, 5, 6];
            for (int i = 0; i < keys.Length; i++)
            {
                if ((flags & (1 << (2 * i))) != 0) Volatile.Write(ref _down[keys[i]], 1);
                if ((flags & (2 << (2 * i))) != 0) Volatile.Write(ref _down[keys[i]], 0);
            }
        }
        else if (type == 1 && data.Length >= 16)
        {
            ushort scan = BinaryPrimitives.ReadUInt16LittleEndian(data);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            int key = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            if ((flags & 2) == 0)
                key = scan switch { 0x52 => 0x60, 0x4f => 0x61, 0x50 => 0x62, 0x51 => 0x63,
                    0x4b => 0x64, 0x4c => 0x65, 0x4d => 0x66, 0x47 => 0x67, 0x48 => 0x68, 0x49 => 0x69,
                    0x53 => 0x6e, _ => key }; // Physical keypad bindings also work with Num Lock off.
            key = key switch { 0x10 => scan == 0x36 ? 0xA1 : 0xA0,
                0x11 => (flags & 2) != 0 ? 0xA3 : 0xA2, 0x12 => (flags & 2) != 0 ? 0xA5 : 0xA4, _ => key };
            if (key is > 0 and < 255 && scan != 0xff) Volatile.Write(ref _down[key], (flags & 1) == 0 ? 1 : 0);
        }
    }
}
