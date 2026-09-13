using Cranberry.Protocol;
using Cranberry.Zone.World;

namespace Cranberry.Zone.Combat;

/// <summary>Uses the existing August ordinary movement record and peer pose envelope.</summary>
public static class BotMovement
{
    public static byte[] Record(CombatBot bot, long nowMs)
    {
        using var w = new PacketWriter();
        w.WriteUInt16((ushort)(MovementFieldMask.Posture | MovementFieldMask.Position
            | MovementFieldMask.Orientation | MovementFieldMask.HorizontalSpeed));
        w.WriteUInt32(unchecked((uint)nowMs));
        w.WriteByte(0);
        ClientVarInt.Write(w, bot.Speed > 0.01f ? 0x0401u : 0x0441u);
        Signed(w, bot.Body.Position.X * 100); Signed(w, bot.Body.Position.Y * 100); Signed(w, bot.Body.Position.Z * 100);
        w.WriteSingle(bot.Heading);
        Signed(w, bot.Speed * 10);
        return w.Written.ToArray();
    }

    public static byte[] Packet(CombatBot bot, long nowMs) => PeerSpawnWriter.PlayerUpdatePosition(bot.Body.TransientId, Record(bot, nowMs));

    private static void Signed(PacketWriter w, float value)
    {
        int number = checked((int)MathF.Round(value));
        uint packed = (uint)Math.Abs(number) << 3 | (number < 0 ? 1u : 0u);
        int bytes = packed <= 0xff ? 1 : packed <= 0xffff ? 2 : packed <= 0xffffff ? 3 : 4;
        packed |= (uint)(bytes - 1) << 1;
        for (int i = 0; i < bytes; i++) w.WriteByte((byte)(packed >> (i * 8)));
    }
}
