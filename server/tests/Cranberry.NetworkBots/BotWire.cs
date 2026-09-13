using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Harness.Protocol;

namespace Cranberry.NetworkBots;

// Independent client encoders from the repository's recorded/derived wire layouts.
// No server packet writer or gameplay method is used to perform a bot action.
internal static class BotWire
{
    public static byte[] Movement(Vector3 p, uint tick, byte stance = 0, uint? managed = null, uint? posture = null, float? yaw = null)
    {
        var b = new List<byte> { managed.HasValue ? (byte)0x66 : (byte)0x46 };
        if (managed.HasValue) { b.Add(0x90); Unsigned(b, managed.Value); }
        b.AddRange(BitConverter.GetBytes((ushort)(2 | (posture.HasValue ? 1 : 0) | (yaw.HasValue ? 0x20 : 0))));
        b.AddRange(BitConverter.GetBytes(tick));
        b.Add(stance);
        if (posture.HasValue) Unsigned(b, posture.Value);
        Signed(b, (int)MathF.Round(p.X * 100));
        Signed(b, (int)MathF.Round(p.Y * 100));
        Signed(b, (int)MathF.Round(p.Z * 100));
        if (yaw.HasValue) b.AddRange(BitConverter.GetBytes(yaw.Value));
        return b.ToArray();
    }

    private static void Unsigned(List<byte> b, uint value)
    {
        for (int extra = 0; extra < 4; extra++)
        {
            uint packed = (value << 2) | (uint)extra;
            if (extra < 3 && packed >= 1u << ((extra + 1) * 8)) continue;
            for (int i = 0; i <= extra; i++) b.Add((byte)(packed >> (8 * i)));
            return;
        }
    }

    private static void Signed(List<byte> b, int value)
    {
        for (int extra = 0; extra < 4; extra++)
        {
            uint packed = ((uint)Math.Abs(value) << 3) | ((uint)extra << 1) | (value < 0 ? 1u : 0u);
            if (extra < 3 && packed >= 1u << ((extra + 1) * 8)) continue;
            for (int i = 0; i <= extra; i++) b.Add((byte)(packed >> (8 * i)));
            return;
        }
    }

    public static uint VarInt(ReadOnlySpan<byte> b, out int length)
    {
        length = 1 + (b[0] & 3);
        uint p = 0;
        for (int i = 0; i < length; i++) p |= (uint)b[i] << (i * 8);
        return p >> 2;
    }

    private static byte[] Weapon(byte sub, uint tick, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((byte)0x06); w.Write((byte)0x82); w.Write(tick); w.Write(sub);
        body(w);
        return stream.ToArray();
    }

    public static byte[] Fire(ulong weapon, Vector3 p, uint projectile, uint tick) => Weapon(3, tick, w =>
    {
        w.Write(weapon); w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
        w.Write(1u); w.Write(projectile); w.Write(0u);
    });

    public static byte[] FireHint(ulong weapon, Vector3 muzzle, Vector3 aim, uint projectile, uint tick) => Weapon(0x20, tick, w =>
    {
        Vector3 delta = aim - muzzle;
        Vector3 direction = delta.LengthSquared() > .001f ? Vector3.Normalize(delta) : Vector3.UnitZ;
        w.Write(weapon); w.Write((byte)255); w.Write(muzzle.X); w.Write(muzzle.Y); w.Write(muzzle.Z);
        w.Write(1u); w.Write(projectile); w.Write(direction.X); w.Write(direction.Y); w.Write(direction.Z); w.Write(0u);
    });

    public static byte[] Hit(ulong victim, Vector3 p, uint projectile, uint tick) => Weapon(6, tick, w =>
    {
        w.Write(projectile); w.Write(victim); w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
        w.Write((ushort)5); w.Write("SPINE"u8); w.Write((byte)0);
        w.Write(0u); w.Write(0u); w.Write((byte)1); w.Write((byte)0x80);
    });

    public static byte[] Reload(ulong weapon, uint tick) => Weapon(7, tick, w => w.Write(weapon));
    public static byte[] SelectSlot(uint slot) => [0x06, 0x86, 0x06, 0, 0, 0, 0, .. BitConverter.GetBytes(slot), 0, 0, 0, 0];
    public static byte[] FireState(ulong weapon, uint tick, byte state) => Weapon(1, tick, w =>
    { w.Write(weapon); w.Write(state); w.Write((byte)0); });
}
