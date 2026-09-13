using System.Numerics;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

/// <summary>
/// The pure-simulation harness of docs/22 §9.1: a match with a fake clock, a
/// <see cref="RecordingPlayerSink"/> per player and synthetic players, stepped deterministically.
/// <c>Match.Tick()</c> takes no arguments precisely so a test can call it 400 times to advance
/// 20 seconds instantly.
/// </summary>
internal sealed class MatchHarness
{
    private int _nextAccount = 0x1001;

    public MatchHarness(MatchSettings? settings = null, ushort matchId = 1)
    {
        Settings = settings ?? MatchSettings.Default;
        Match = new Match(matchId, Settings);
    }

    public Match Match { get; }

    public MatchSettings Settings { get; }

    public MatchPlayer AddPlayer(string name = "Player", bool connected = true)
    {
        var sink = new RecordingPlayerSink { IsOpen = connected };
        return Match.AddPlayer((ulong)_nextAccount++, name, sink);
    }

    public static RecordingPlayerSink SinkOf(MatchPlayer player) => (RecordingPlayerSink)player.Sink!;

    public static List<RecordedPacket> Sent(MatchPlayer to) => SinkOf(to).Packets;

    /// <summary>Teleports a player without going through the movement gate — a test fixture, not a flow.</summary>
    public void MoveTo(MatchPlayer player, in Vector3 position)
    {
        player.Position = position;
        Match.World.UpdateCell(player);
    }

    /// <summary>
    /// Posts one synthetic channel-2 record carrying <paramref name="position"/>, exactly as the
    /// client's own encoder would lay it out (<c>FUN_140a3ca40</c>'s read order, mask bit 0x0002,
    /// two-decimal packed signed values).
    /// </summary>
    public bool PostMovement(MatchPlayer player, in Vector3 position, uint clientTime = 0)
        => Match.Post(
            new Command(CommandKind.Movement, player.Slot, EntityId.None, 0, 0, 0),
            MovementRecord.Position(position, clientTime));

    public bool PostCommand(MatchPlayer player, CommandKind kind, EntityId target = default) =>
        Match.Post(new Command(kind, player.Slot, target, 0, 0, 0), []);

    public void Step(int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            Match.Tick();
        }
    }

    public void StepSeconds(double seconds) => Step((int)Math.Round(seconds * MatchClock.Hz));

    public void ClearSinks()
    {
        foreach (MatchPlayer? player in Match.World.Players)
        {
            if (player?.Sink is RecordingPlayerSink sink)
            {
                sink.Clear();
            }
        }
    }
}

/// <summary>
/// Builds the c2s movement records the harness feeds in. The encoder is the exact inverse of
/// <c>ClientMovementUpdate.Parse</c>'s packed-signed reader (<c>FUN_140a18f40</c>: bit 0 is the
/// sign, bits 1-2 the extra byte count, the rest the magnitude).
/// </summary>
internal static class MovementRecord
{
    public const ushort PositionMask = 0x0002;

    public static byte[] Position(in Vector3 position, uint clientTime = 0, byte state = 0)
    {
        var bytes = new List<byte>(24);
        bytes.Add((byte)(PositionMask & 0xFF));
        bytes.Add((byte)(PositionMask >> 8));
        bytes.Add((byte)(clientTime & 0xFF));
        bytes.Add((byte)((clientTime >> 8) & 0xFF));
        bytes.Add((byte)((clientTime >> 16) & 0xFF));
        bytes.Add((byte)((clientTime >> 24) & 0xFF));
        bytes.Add(state);
        WriteScaled(bytes, position.X);
        WriteScaled(bytes, position.Y);
        WriteScaled(bytes, position.Z);
        return [.. bytes];
    }

    private static void WriteScaled(List<byte> bytes, float value) =>
        WritePackedSigned(bytes, (int)MathF.Round(value * 100f));

    private static void WritePackedSigned(List<byte> bytes, int value)
    {
        uint magnitude = (uint)Math.Abs(value);
        uint sign = value < 0 ? 1u : 0u;
        for (int extra = 0; extra <= 3; extra++)
        {
            uint packed = (magnitude << 3) | ((uint)extra << 1) | sign;
            bool fits = extra == 3 || packed < 1u << (8 * (extra + 1));
            if (!fits)
            {
                continue;
            }

            for (int i = 0; i <= extra; i++)
            {
                bytes.Add((byte)(packed >> (8 * i)));
            }

            return;
        }
    }
}
