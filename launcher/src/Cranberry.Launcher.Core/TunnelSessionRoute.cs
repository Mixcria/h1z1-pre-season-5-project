using System.Buffers.Binary;
using System.Net;

namespace Cranberry.Launcher.Core;

// Character select replaces the game's UDP sockets without replacing its TLS tunnel.
// Old SOE responses may already be queued in TLS when the next SessionRequest arrives.
// Keep them away from the new socket until that request's SessionReply crosses the
// ordered TLS stream. SOE SessionRequest carries its BE session ID at byte 6; Reply at 2.
internal sealed class TunnelSessionRoute
{
    private readonly object _gate = new();
    private IPEndPoint? _client;
    private uint _session;
    private bool _awaitingReply;

    public bool AcceptClient(IPEndPoint endpoint, ReadOnlySpan<byte> datagram)
    {
        bool request = datagram.Length >= 14 && datagram[0] == 0 && datagram[1] == 1;
        lock (_gate)
        {
            if (_client is not null && !_client.Equals(endpoint) && !request) return false;
            _client = endpoint;
            if (request)
            {
                _session = BinaryPrimitives.ReadUInt32BigEndian(datagram[6..]);
                _awaitingReply = true;
            }
            return true;
        }
    }

    public IPEndPoint? ServerDestination(ReadOnlySpan<byte> datagram)
    {
        lock (_gate)
        {
            if (_awaitingReply)
            {
                if (datagram.Length < 6 || datagram[0] != 0 || datagram[1] != 2
                    || BinaryPrimitives.ReadUInt32BigEndian(datagram[2..]) != _session) return null;
                _awaitingReply = false;
            }
            return _client;
        }
    }
}
