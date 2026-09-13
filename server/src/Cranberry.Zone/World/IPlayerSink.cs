using Cranberry.Protocol;

namespace Cranberry.Zone.World;

/// <summary>
/// Everything the simulation may do to a client. Deliberately closure-free and deliberately
/// compatible with every existing <c>WriteTo(PacketWriter)</c> body:
/// <code>
/// PacketWriter w = sink.Begin(channel: 0);
/// GameModeHud.WritePlayersRemaining(w, 12);
/// sink.End();
/// </code>
/// The simulation never names a transport type; <see cref="SessionBridge"/> is the single
/// implementation that does (docs/22 §4.9, §9.4).
/// </summary>
public interface IPlayerSink
{
    /// <summary>
    /// True while a live outbound path exists. This is the liveness guard today's <c>Later</c>
    /// performs at fire time (<c>connection.State == ConnectionState.Open</c>,
    /// <c>ZoneService.cs:934</c>); dropping it would send packets to a closed link.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Starts a tunnel message. The returned writer is positioned after the gateway header byte, so
    /// the caller writes exactly the zone packet body.
    /// </summary>
    PacketWriter Begin(byte channel = 0);

    /// <summary>Finishes the message started by <see cref="Begin"/> and queues it.</summary>
    void End();

    /// <summary>Relay path: a body that is already bytes, written verbatim after the header.</summary>
    void SendRaw(byte channel, ReadOnlySpan<byte> body);

    /// <summary>Hands everything queued to the transport. Called once per session per pump.</summary>
    void Flush();
}
