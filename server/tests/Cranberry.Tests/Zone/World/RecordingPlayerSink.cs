using Cranberry.Protocol;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

/// <summary>One message a sink was handed: the gateway channel and the zone body verbatim.</summary>
internal sealed record RecordedPacket(byte Channel, byte[] Body)
{
    public byte Opcode => Body.Length > 0 ? Body[0] : (byte)0;

    /// <summary>The <c>u16</c> sub-opcode of a family packet (<c>ce xx xx</c>, <c>11 xx xx</c>).</summary>
    public ushort SubOpcode =>
        Body.Length >= 3 ? (ushort)(Body[1] | (Body[2] << 8)) : (ushort)0;

    public bool Is(byte opcode, ushort subOpcode) => Opcode == opcode && SubOpcode == subOpcode;
}

/// <summary>
/// <see cref="IPlayerSink"/> test double: captures <c>(channel, bytes)</c> instead of sending, so a
/// whole match can be driven with no socket, no client and no clock (docs/22 §9.1).
/// The bytes are exactly what <see cref="SessionBridge"/> would put after the gateway header byte.
/// </summary>
internal sealed class RecordingPlayerSink : IPlayerSink
{
    private PacketWriter? _open;
    private byte _channel;

    public List<RecordedPacket> Packets { get; } = [];

    public bool IsOpen { get; set; } = true;

    public int Flushes { get; private set; }

    public PacketWriter Begin(byte channel = 0)
    {
        if (_open is not null)
        {
            throw new InvalidOperationException("Begin was called twice without an End.");
        }

        _channel = channel;
        _open = new PacketWriter();
        return _open;
    }

    public void End()
    {
        if (_open is not PacketWriter writer)
        {
            throw new InvalidOperationException("End was called without a matching Begin.");
        }

        Packets.Add(new RecordedPacket(_channel, writer.Written.ToArray()));
        writer.Dispose();
        _open = null;
    }

    public void SendRaw(byte channel, ReadOnlySpan<byte> body) =>
        Packets.Add(new RecordedPacket(channel, body.ToArray()));

    public void Flush() => Flushes++;

    public IEnumerable<RecordedPacket> With(byte opcode, ushort subOpcode) =>
        Packets.Where(packet => packet.Is(opcode, subOpcode));

    public int CountOf(byte opcode, ushort subOpcode) => With(opcode, subOpcode).Count();

    public void Clear() => Packets.Clear();
}
