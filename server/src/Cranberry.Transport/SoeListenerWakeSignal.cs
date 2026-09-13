using System.Net;
using System.Net.Sockets;

namespace Cranberry.Transport;

/// <summary>
/// A private, connected loopback pair lets queue producers interrupt Socket.Select.
/// Wake bytes never enter the game socket, protocol, packet capture or traffic counters.
/// At most one notification is outstanding; the managed queues retain the actual work.
/// </summary>
internal sealed class SoeListenerWakeSignal : IDisposable
{
    public Socket Reader { get; } = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Socket _writer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private int _pending;

    public SoeListenerWakeSignal()
    {
        try
        {
            Reader.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _writer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _writer.Connect(Reader.LocalEndPoint!);
            Reader.Connect(_writer.LocalEndPoint!);
            Reader.Blocking = false;
            _writer.Blocking = false;
        }
        catch { Dispose(); throw; }
    }

    public void Signal()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        ReadOnlySpan<byte> notification = [1];
        try { _writer.Send(notification); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.NoBufferSpaceAvailable)
        {
            // The regular timer remains a fallback if the OS cannot accept a wake byte.
            // Permit a later enqueue to retry instead of leaving notifications disabled.
            Interlocked.Exchange(ref _pending, 0);
        }
        catch (ObjectDisposedException)
        {
            // A producer may have captured this instance just before Stop disposed it.
        }
    }

    public void Drain()
    {
        Span<byte> buffer = stackalloc byte[16];
        try { while (Reader.Poll(0, SelectMode.SelectRead)) Reader.Receive(buffer); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { }
        // Clear only AFTER draining. Work queued while the notification was coalesced
        // is processed this pass; a producer after this reset leaves a fresh wake byte.
        Interlocked.Exchange(ref _pending, 0);
    }

    public void Dispose()
    {
        _writer.Dispose();
        Reader.Dispose();
    }
}
