namespace Cranberry.Transport;

/// <summary>A datagram that does not follow the transport's framing. The connection that sent it is dropped.</summary>
public sealed class SoeProtocolException(string message) : Exception(message);
