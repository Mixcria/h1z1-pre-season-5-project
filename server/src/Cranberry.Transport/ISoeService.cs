using System.Net;

namespace Cranberry.Transport;

/// <summary>What the transport does with a new SessionRequest.</summary>
public readonly record struct SessionDecision(bool Accept, byte[]? Key, bool EncryptFromStart)
{
    public static SessionDecision Refuse => new(false, null, false);

    /// <summary>Accept; the link is encrypted with <paramref name="key"/> from its first reliable datagram.</summary>
    public static SessionDecision Encrypted(byte[] key) => new(true, key, true);

    /// <summary>Accept; the link starts in the clear and the service may call <see cref="SoeConnection.EnableEncryption"/> later.</summary>
    public static SessionDecision ClearUntilEnabled(byte[] key) => new(true, key, false);

    /// <summary>
    /// Accept without a pre-provisioned key. The service may remain clear or install a key later
    /// with <see cref="SoeConnection.EnableEncryption(ReadOnlySpan{byte})"/>.
    /// </summary>
    public static SessionDecision Clear => new(true, null, false);
}

/// <summary>Why a connection went away.</summary>
public enum DisconnectCause
{
    PeerRequested,
    Timeout,
    ProtocolError,
    ServerRequested,
    Replaced,
}

/// <summary>
/// The application side of the transport. All calls arrive on the listener's single thread;
/// sending from a callback is always safe, sending from elsewhere goes through <see cref="SoeListener.Post"/>.
/// </summary>
public interface ISoeService
{
    SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request);

    void OnConnected(SoeConnection connection);

    /// <summary>One complete application message, already decrypted. The span is only valid during the call.</summary>
    void OnMessage(SoeConnection connection, Span<byte> message);

    void OnDisconnected(SoeConnection connection, DisconnectCause cause);
}

public enum TransportLogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Minimal logging seam so the transport carries no logging dependency.</summary>
public interface ITransportLog
{
    bool IsEnabled(TransportLogLevel level);

    void Log(TransportLogLevel level, string message);
}

public static class TransportLogExtensions
{
    public static void Debug(this ITransportLog log, string message) => log.Log(TransportLogLevel.Debug, message);

    public static void Info(this ITransportLog log, string message) => log.Log(TransportLogLevel.Info, message);

    public static void Warn(this ITransportLog log, string message) => log.Log(TransportLogLevel.Warning, message);

    public static void Error(this ITransportLog log, string message) => log.Log(TransportLogLevel.Error, message);
}
