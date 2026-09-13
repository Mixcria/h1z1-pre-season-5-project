using System.Buffers.Binary;
using System.Net.WebSockets;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Core.Voice;

namespace Cranberry.NetworkBots;

// Synthetic Opus tones only. This stress client never opens a microphone or an audio device.
// It drains every TLS frame but leaves decoding to the separate codec / HTTPS playback tests,
// avoiding the cost of emulating 150 audio devices on the server's own test computer.
internal sealed class VoiceTrafficLoad : IAsyncDisposable
{
    private sealed class Peer(int index)
    {
        public int Index = index;
        public readonly ClientWebSocket Socket = new();
        public volatile bool Allowed;
        public ulong Character;
        public long Received, Sent, Bytes, Violations, LastState;
        public uint Sequence;
        public string? Failure;
        public Task? Reader;
    }
    private readonly Peer[] _peers;
    private readonly int _talkers;
    private readonly CancellationTokenSource _stop = new();
    private Task? _sending;
    private readonly byte[] _opus;
    private VoiceTrafficLoad(int count, int talkers)
    {
        _talkers = talkers; _peers = Enumerable.Range(0, count).Select(i => new Peer(i)).ToArray();
        using var codec = new VoiceEncoder();
        _opus = codec.Encode(Enumerable.Range(0, 320).Select(i => (short)(1000 * Math.Sin(i * 2 * Math.PI * 440 / 16000))).ToArray());
    }
    public static async Task<VoiceTrafficLoad> Open(TlsFixtureAddress fixture, int talkers, int menu, CancellationToken ct)
    {
        if (!LauncherSettings.ValidateServer(fixture.Settings.ServerUrl).IsLoopback) throw new InvalidDataException("Synthetic voice is loopback only.");
        var load = new VoiceTrafficLoad(talkers + menu, talkers);
        try
        {
            await Parallel.ForEachAsync(load._peers, new ParallelOptions { MaxDegreeOfParallelism = 24, CancellationToken = ct }, async (peer, token) =>
            {
                peer.Socket.Options.SetRequestHeader("Authorization", "Bearer " + fixture.Tokens[peer.Index]);
                peer.Socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    LauncherConnection.ValidateCertificate(certificate, errors, fixture.Settings.CertificateSha256);
                var uri = new UriBuilder(new Uri(LauncherSettings.ValidateServer(fixture.Settings.ServerUrl), "api/voice/proximity")) { Scheme = "wss" };
                await peer.Socket.ConnectAsync(uri.Uri, token);
                peer.Reader = load.Receive(peer);
            });
            load._sending = load.Send(); return load;
        }
        catch { await load.DisposeAsync(); throw; }
    }
    private async Task Receive(Peer peer)
    {
        byte[] bytes = new byte[VoiceWire.MaxMessageBytes];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int size = 0; ValueWebSocketReceiveResult part;
                do
                {
                    if (size == bytes.Length) throw new InvalidDataException("Oversized voice response");
                    part = await peer.Socket.ReceiveAsync(bytes.AsMemory(size), _stop.Token);
                    if (part.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Voice link ended");
                    size += part.Count;
                } while (!part.EndOfMessage);
                if (VoiceWire.Is(bytes.AsSpan(0, size), VoiceWire.State) && size == 13)
                {
                    peer.Character = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(4));
                    peer.Allowed = bytes[12] == 1;
                    Interlocked.Exchange(ref peer.LastState, Environment.TickCount64);
                }
                else if (VoiceWire.TryAudio(bytes.AsSpan(0, size), out var frame))
                {
                    Interlocked.Increment(ref peer.Received); Interlocked.Add(ref peer.Bytes, size);
                    if (peer.Index >= _talkers || !peer.Allowed || frame.Speaker == peer.Character) Interlocked.Increment(ref peer.Violations);
                }
                else Interlocked.Increment(ref peer.Violations);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException)
        { if (!_stop.IsCancellationRequested) peer.Failure = ex.GetType().Name; }
    }
    private async Task Send()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await Task.WhenAll(_peers.Take(_talkers).Where(p => p.Allowed && p.Failure is null
                    && Environment.TickCount64 - Interlocked.Read(ref p.LastState) <= 500).Select(async peer =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(250);
                    try
                    {
                        await peer.Socket.SendAsync(VoiceWire.EncodeUpload(peer.Sequence++, _opus).AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
                        Interlocked.Increment(ref peer.Sent);
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                    { if (!_stop.IsCancellationRequested) peer.Failure = ex.GetType().Name; }
                }));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public bool Healthy => _peers.All(p => p.Failure is null && p.Violations == 0)
        && _peers.Take(_talkers).All(p => p.Received > 100 && p.Sent > 100)
        && _peers.Skip(_talkers).All(p => p.Received == 0 && p.Sent == 0);
    public object Summary() => new
    {
        connections = _peers.Length, simultaneousTalkers = _talkers, menuListeners = _peers.Length - _talkers,
        received = _peers.Sum(p => Interlocked.Read(ref p.Received)), sent = _peers.Sum(p => Interlocked.Read(ref p.Sent)),
        minimumReceived = _peers.Take(_talkers).Min(p => Interlocked.Read(ref p.Received)),
        minimumSent = _peers.Take(_talkers).Min(p => Interlocked.Read(ref p.Sent)),
        bytes = _peers.Sum(p => Interlocked.Read(ref p.Bytes)), violations = _peers.Sum(p => p.Violations),
        faults = _peers.Where(p => p.Failure is not null).Select(p => new { p.Index, p.Failure }).Take(12).ToArray(),
        microphoneUsed = false, receiverDecoding = false,
    };
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); foreach (var peer in _peers) peer.Socket.Abort();
        if (_sending is not null) await _sending;
        foreach (var peer in _peers) { if (peer.Reader is not null) await peer.Reader; peer.Socket.Dispose(); }
        _stop.Dispose();
    }
}
