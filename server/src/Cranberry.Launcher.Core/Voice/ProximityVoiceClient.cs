using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Cranberry.Launcher.Core.Voice;

public sealed class ProximityVoiceClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<(byte[] Bytes, long At, bool Audio)> _outgoing = Channel.CreateBounded<(byte[], long, bool)>(
        new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly VoiceEncoder _encoder = new();
    private Task? _running;
    private long _stateAt;
    private volatile bool _allowed, _connected, _deafened, _transmitting;
    private ulong _character;
    private uint _sequence;
    private int _disposed;
    private readonly object _encodeGate = new();
    public VoiceMixer Mixer { get; } = new();
    public Task Completion => _running ?? Task.CompletedTask;
    public bool Connected => _connected;
    public bool CanTalk => _connected && _allowed && !_deafened && Environment.TickCount64 - Interlocked.Read(ref _stateAt) <= 500;
    public bool Deafened => _deafened;

    public async Task Connect(LauncherSettings settings, string token, CancellationToken ct = default)
    {
        Uri server = LauncherSettings.ValidateServer(settings.ServerUrl);
        if (server.Scheme != "https") throw new InvalidDataException("Voice requires HTTPS.");
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        _socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            LauncherConnection.ValidateCertificate(certificate, errors, settings.CertificateSha256);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        await _socket.ConnectAsync(new UriBuilder(new Uri(server, "api/voice/proximity")) { Scheme = "wss" }.Uri, ct);
        // WinForms callers must not run either network pump on their UI synchronization
        // context: delayed painting/input would otherwise expire voice permission/audio.
        _connected = true; _running = Task.Run(Run);
    }

    public void SetDeafened(bool value)
    {
        _deafened = value;
        if (value) { _transmitting = false; Mixer.Clear(); }
        _outgoing.Writer.TryWrite((VoiceWire.EncodeDeafen(value), Environment.TickCount64, false));
    }
    public void SetTransmitting(bool value) => _transmitting = value && CanTalk;
    public bool Submit(ReadOnlySpan<short> pcm)
    {
        lock (_encodeGate)
        {
            if (!_transmitting || !CanTalk || pcm.Length != VoiceWire.FrameSamples) return false;
            byte[] opus = _encoder.Encode(pcm);
            return _outgoing.Writer.TryWrite((VoiceWire.EncodeUpload(_sequence++, opus), Environment.TickCount64, true));
        }
    }
    private async Task Run()
    {
        Task[] pumps = [Receive(), Send()];
        try { await await Task.WhenAny(pumps); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException) { }
        finally
        {
            _connected = _allowed = _transmitting = false;
            Mixer.Clear(); _stop.Cancel(); _socket.Abort();
            try { await Task.WhenAll(pumps); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException) { }
        }
    }
    private async Task Receive()
    {
        byte[] bytes = new byte[VoiceWire.MaxMessageBytes];
        while (!_stop.IsCancellationRequested)
        {
            int size = 0; ValueWebSocketReceiveResult part;
            do
            {
                if (size == bytes.Length) throw new InvalidDataException("Voice frame too large.");
                part = await _socket.ReceiveAsync(bytes.AsMemory(size), _stop.Token);
                if (part.MessageType == WebSocketMessageType.Close) return;
                if (part.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Invalid voice frame.");
                size += part.Count;
            } while (!part.EndOfMessage);
            if (VoiceWire.Is(bytes.AsSpan(0, size), VoiceWire.State) && size == 13 && bytes[12] <= 1)
            {
                ulong character = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(4));
                if (_character != character || bytes[12] == 0) Mixer.Clear();
                _character = character; _allowed = character != 0 && bytes[12] == 1;
                Interlocked.Exchange(ref _stateAt, Environment.TickCount64);
            }
            else if (CanTalk) Mixer.Receive(bytes.AsSpan(0, size), Environment.TickCount64);
        }
    }
    private async Task Send()
    {
        await foreach (var frame in _outgoing.Reader.ReadAllAsync(_stop.Token))
        {
            if (frame.Audio && (!_transmitting || !CanTalk || Environment.TickCount64 - frame.At > 60)) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(250);
            await _socket.SendAsync(frame.Bytes.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _connected = _allowed = _transmitting = false;
        _stop.Cancel(); _socket.Abort(); _outgoing.Writer.TryComplete();
        if (_running is not null) await _running;
        lock (_encodeGate) _encoder.Dispose();
        Mixer.Dispose(); _socket.Dispose(); _stop.Dispose();
    }
}
