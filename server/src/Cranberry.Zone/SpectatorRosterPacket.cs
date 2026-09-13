using Cranberry.Protocol;

namespace Cranberry.Zone;

public sealed record SpectatorPlayerRow(ulong Guid, string Name, byte HealthPercent, byte Heading,
    short X, short Y, short Z, byte ActionFlags = 0);

/// <summary>
/// August E2/02 kind zero replaces BaseClient.Spectator. Derived from native 140a3f2a0 /
/// 140a46280 and consumer 140ed30c0; the unknown strings, fields, and both maps stay empty.
/// </summary>
public sealed record SpectatorRosterPacket(IReadOnlyList<SpectatorPlayerRow> Players)
{
    public const int MaximumPlayers = 150;

    public void WriteTo(PacketWriter writer)
    {
        if (Players.Count > MaximumPlayers || Players.Any(player => player.Guid == 0 || player.Name is null
            || player.Name.Length > 80 || player.HealthPercent > 100 || player.ActionFlags > 3))
            throw new ArgumentException("The spectator roster exceeds the bounded August schema.", nameof(Players));
        writer.WriteByte(ZoneOpcodes.SpectatorBase);
        writer.WriteUInt16(2);
        writer.WriteUInt32(0); // kind zero: ordinary spectator roster, a full replacement
        writer.WriteUInt32((uint)Players.Count);
        foreach (var player in Players)
        {
            writer.WriteUInt64(player.Guid);
            writer.WriteString(player.Name);
            writer.WriteString(string.Empty);
            writer.WriteByte(player.HealthPercent);
            writer.WriteByte(0);
            writer.WriteUInt32(0); // Native serialized order is u32 followed by u16.
            writer.WriteUInt16(0);
            writer.WriteByte(player.Heading);
            writer.WriteByte(player.ActionFlags);
            writer.WriteUInt16(unchecked((ushort)player.X));
            writer.WriteUInt16(unchecked((ushort)player.Y));
            writer.WriteUInt16(unchecked((ushort)player.Z));
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteUInt32(0); // per-player nested map
        }
        writer.WriteUInt32(0); // final extra map; mandatory even for an empty roster
    }

    public static byte HeadingFromRadians(float radians)
    {
        if (!float.IsFinite(radians)) return 0;
        float turn = radians % MathF.Tau;
        if (turn < 0) turn += MathF.Tau;
        return (byte)Math.Clamp((int)MathF.Round(turn * 255 / MathF.Tau), 0, 255);
    }

    public static short Coordinate(float value) => float.IsFinite(value)
        ? (short)Math.Clamp(MathF.Round(value), short.MinValue, short.MaxValue) : (short)0;
}
