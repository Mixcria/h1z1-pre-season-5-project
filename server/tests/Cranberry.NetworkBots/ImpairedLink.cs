using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Cranberry.NetworkBots;

/// <summary>Bounded bidirectional UDP impairment between a bot and its normal server/tunnel endpoint.</summary>
internal sealed class ImpairedLink : IAsyncDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly IPEndPoint _server;
    private IPEndPoint? _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly PriorityQueue<(byte[] Bytes, IPEndPoint Destination), long> _queue = new();
    private readonly Task _receive, _send;
    private readonly Random _random;
    private readonly int _delay, _jitter;
    private readonly double _loss;
    public long Received, Dropped, Duplicated, Forwarded, Overflow;
    public int MaxQueue;

    public ImpairedLink(IPEndPoint server, int delay, int jitter, double loss, int seed)
    {
        _server = server; _delay = delay; _jitter = jitter; _loss = loss; _random = new(seed);
        _socket.Client.ReceiveBufferSize = 4 << 20;
        _receive = Receive(); _send = Send();
    }
    public IPEndPoint EndPoint => (IPEndPoint)_socket.Client.LocalEndPoint!;

    private async Task Receive()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult data;
                try { data = await _socket.ReceiveAsync(_stop.Token); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows reports ICMP port-unreachable after an intentionally closed bot.
                    // A delayed datagram to that peer must not fault the whole proxy's shutdown.
                    continue;
                }
                Received++;
                IPEndPoint? destination;
                if (data.RemoteEndPoint.Equals(_server)) destination = _client;
                else { _client ??= data.RemoteEndPoint; if (!_client.Equals(data.RemoteEndPoint)) continue; destination = _server; }
                if (destination is null) continue;
                if (_random.NextDouble() < _loss) { Dropped++; continue; }
                Enqueue(data.Buffer, destination);
                if (_random.NextDouble() < .002) { Duplicated++; Enqueue(data.Buffer, destination); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Enqueue(byte[] bytes, IPEndPoint destination)
    {
        long due = Environment.TickCount64 + Math.Max(0, _delay + _random.Next(-_jitter, _jitter + 1));
        lock (_queue)
        {
            if (_queue.Count >= 16384) { Overflow++; return; }
            _queue.Enqueue((bytes, destination), due);
            MaxQueue = Math.Max(MaxQueue, _queue.Count);
        }
    }
    private async Task Send()
    {
        try
        {
            using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(2));
            while (await cadence.WaitForNextTickAsync(_stop.Token))
            {
                while (true)
                {
                    (byte[] Bytes, IPEndPoint Destination) next;
                    lock (_queue)
                    {
                        if (!_queue.TryPeek(out next, out long due) || due > Environment.TickCount64) break;
                        _queue.Dequeue();
                    }
                    await _socket.SendAsync(next.Bytes, next.Destination, _stop.Token);
                    Forwarded++;
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_receive, _send);
        _socket.Dispose(); _stop.Dispose();
    }
}
