using System.Net;
using Cranberry.Zone.HostedGames;
using System.Collections.Concurrent;
using Cranberry.Transport;

namespace Cranberry.Host;

/// <summary>Appends every session request and application message, both directions, to one file per run.</summary>
public interface IHostPacketRecorder : IPacketRecorder, IDisposable
{
    string FilePath { get; }
}

public sealed class FilePacketRecorder : IHostPacketRecorder
{
    private readonly StreamWriter _file;
    private readonly object _gate = new();
    private readonly BlockingCollection<string> _lines;
    private readonly Thread _writer;
    private readonly int _maxQueuedChars;
    private long _queuedChars;
    private long _dropped;
    private bool _disposed;
    public long DroppedRecords => Interlocked.Read(ref _dropped);
    public Exception? WriteError { get; private set; }

    public static IHostPacketRecorder FromEnvironment(string path, Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        return read("CRANBERRY_RECORD_PACKETS") == "0" ? NoPacketRecorder.Instance : new FilePacketRecorder(path);
    }

    private sealed class NoPacketRecorder : IHostPacketRecorder
    {
        public static NoPacketRecorder Instance { get; } = new();
        public string FilePath => "disabled";
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext) { }
        public void Dispose() { }
    }


    public FilePacketRecorder(string path, int queueCapacity = 8192, int maxQueuedChars = 8 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedChars);
        _lines = new BlockingCollection<string>(queueCapacity);
        _maxQueuedChars = maxQueuedChars;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = false };
        _file.WriteLine("# time | remote | protocol | direction | length | bytes");
        _file.Flush();
        FilePath = path;
        _writer = new Thread(Drain) { IsBackground = true, Name = "packet-capture" };
        _writer.Start();
    }

    public string FilePath { get; }

    public void RecordSession(IPEndPoint remote, in SessionRequest request)
    {
        Enqueue($"{DateTime.Now:HH:mm:ss.fff} | {remote} | {request.ProtocolName} | session-request | 0 | crc={request.CrcLength} id={request.SessionId:x8} udp={request.UdpLength}");
    }

    public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
    {
        // Issued and redeemed hosted secrets travel in ordinary text packets. Keep their
        // metadata in diagnostics without writing a usable bearer key into a wire capture.
        string payload = bytes.IndexOf("CRANBERRY_OVERLAY_V1"u8) >= 0 || bytes.IndexOf("@cranberry/overlay/1;"u8) >= 0
            ? "[private social payload redacted]" : HostedGameStore.ContainsSecret(bytes) ? "[hosted key redacted]" : Convert.ToHexString(bytes);
        string line = $"{DateTime.Now:HH:mm:ss.fff} | {connection.RemoteEndPoint} | {connection.ProtocolName} | {direction} | {bytes.Length} | {payload}";
        Enqueue(line);
    }

    public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} | {connection.RemoteEndPoint} | {connection.ProtocolName} | c2s-raw@{keystreamPosition} | {ciphertext.Length} | {Convert.ToHexString(ciphertext)}";
        Enqueue(line);
    }

    private void Enqueue(string line)
    {
        lock (_gate)
        {
            if (_disposed || WriteError is not null || _queuedChars + line.Length > _maxQueuedChars)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _queuedChars += line.Length;
            if (!_lines.TryAdd(line))
            {
                _queuedChars -= line.Length;
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    private void Drain()
    {
        try
        {
            long flushed = Environment.TickCount64;
            long reportedDrops = 0;
            while (!_lines.IsCompleted)
            {
                if (_lines.TryTake(out string? line, 100))
                {
                    lock (_gate) _queuedChars -= line.Length;
                    _file.WriteLine(line);
                }
                if (Environment.TickCount64 - flushed >= 200)
                {
                    long drops = DroppedRecords;
                    if (drops != reportedDrops)
                        _file.WriteLine($"# capture overload: {drops} records dropped in total");
                    reportedDrops = drops;
                    _file.Flush();
                    flushed = Environment.TickCount64;
                }
            }
            if (DroppedRecords != reportedDrops)
                _file.WriteLine($"# capture overload: {DroppedRecords} records dropped in total");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate) WriteError = ex;
            Console.Error.WriteLine($"Packet capture failed: {ex.Message}");
        }
        finally
        {
            while (_lines.TryTake(out var abandoned))
            {
                lock (_gate) _queuedChars -= abandoned.Length;
                Interlocked.Increment(ref _dropped);
            }
            try { _file.Dispose(); }
            catch (IOException ex) { lock (_gate) WriteError ??= ex; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lines.CompleteAdding();
        }
        _writer.Join();
        _lines.Dispose();
    }
}
