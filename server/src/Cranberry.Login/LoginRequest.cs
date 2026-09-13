using Cranberry.Protocol;

namespace Cranberry.Login;

/// <summary>
/// The client's first message on a LoginUdp_14 link. Layout measured from the August client
/// (derivation log, 2026-08-27): opcode, ticket string, fingerprint string, four u32s.
/// </summary>
public sealed record LoginRequest(string Ticket, string Fingerprint, uint Field1, uint Field2, uint Field3, uint Field4)
{
    public const byte Opcode = 0x01;

    /// <summary>Parses the body that follows the opcode byte.</summary>
    public static LoginRequest Parse(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body);
        string ticket = reader.ReadString();
        string fingerprint = reader.ReadString();
        uint f1 = reader.Remaining >= 4 ? reader.ReadUInt32() : 0;
        uint f2 = reader.Remaining >= 4 ? reader.ReadUInt32() : 0;
        uint f3 = reader.Remaining >= 4 ? reader.ReadUInt32() : 0;
        uint f4 = reader.Remaining >= 4 ? reader.ReadUInt32() : 0;
        return new LoginRequest(ticket, fingerprint, f1, f2, f3, f4);
    }
}
