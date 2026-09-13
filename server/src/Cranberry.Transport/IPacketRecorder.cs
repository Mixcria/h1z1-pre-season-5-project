using System.Net;

namespace Cranberry.Transport;

/// <summary>
/// Records transport sessions and complete application messages without coupling a service to
/// the host's file format. A recorder may be called concurrently by multiple listeners.
/// </summary>
public interface IPacketRecorder
{
    void RecordSession(IPEndPoint remote, in SessionRequest request);

    void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes);

    /// <summary>Raw reliable payload before decryption, with the inbound keystream position.</summary>
    void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext);
}
