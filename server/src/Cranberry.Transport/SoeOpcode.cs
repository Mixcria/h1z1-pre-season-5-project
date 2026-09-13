namespace Cranberry.Transport;

/// <summary>
/// Opcodes of the SOE UDP transport. Every datagram starts with one of these as a big-endian
/// u16. Values are protocol facts observed on the wire; the names are ours.
/// </summary>
public enum SoeOpcode : ushort
{
    SessionRequest = 0x0001,
    SessionReply = 0x0002,
    Multi = 0x0003,
    Disconnect = 0x0005,
    Ping = 0x0006,
    NetStatusRequest = 0x0007,
    NetStatusReply = 0x0008,
    Data = 0x0009,
    DataFragment = 0x000D,
    OutOfOrder = 0x0011,
    Ack = 0x0015,

    /// <summary>Not a datagram opcode: the marker at the start of a reliable payload that carries
    /// several length-prefixed application messages (observed from the August client, 2026-08-27).</summary>
    Bundle = 0x0019,
    FatalError = 0x001D,
    FatalErrorReply = 0x001E,
}
