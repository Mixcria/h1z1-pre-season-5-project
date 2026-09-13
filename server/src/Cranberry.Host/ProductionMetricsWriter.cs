using System.Text.Json;
using System.Threading.Channels;

namespace Cranberry.Host;

/// <summary>Bounded JSONL output off the listener threads. Optional rolling retention deletes only this writer's own oldest files.</summary>
public sealed class ProductionMetricsWriter : IAsyncDisposable
{
    private const int QueueCapacity = 4, MaxLineBytes = 2 << 20;
    private static readonly byte[] NewLine = [10];
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DictionaryKeyPolicy = JsonNamingPolicy.CamelCase };
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(QueueCapacity)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly bool _rolling;
    private readonly Queue<string> _retainedFiles = new();
    private long _rotatedFiles;
    private readonly Action<string>? _warning;
    private long _dropped, _written, _bytes;
    private int _files, _disposed;
    private volatile bool _limitReached;
    private string? _errorType;
    public string FilePrefix { get; }
    public bool CaptureStopped => _limitReached || Volatile.Read(ref _errorType) is not null;
    public long DroppedSamples => Interlocked.Read(ref _dropped);
    public long WrittenSamples => Interlocked.Read(ref _written);

    public ProductionMetricsWriter(string directory, ProductionMetricsOptions options, Action<string>? warning = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFileMiB, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxFileMiB, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFiles, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxFiles, 16);
        _maxFileBytes = (long)options.MaxFileMiB << 20;
        _maxFiles = options.MaxFiles; _rolling = options.Rolling; _warning = warning;
        Directory.CreateDirectory(directory);
        FilePrefix = Path.Combine(Path.GetFullPath(directory),
            $"metrics-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        _writer = Task.Run(DrainAsync);
    }

    public bool TryWrite(object sample)
    {
        if (Volatile.Read(ref _disposed) != 0 || CaptureStopped || !_queue.Writer.TryWrite(sample))
        { Interlocked.Increment(ref _dropped); return false; }
        return true;
    }

    public object CaptureStatus() => new
    {
        queuedSamples = _queue.Reader.Count, droppedSamples = DroppedSamples, writtenSamples = WrittenSamples,
        bytesWritten = Interlocked.Read(ref _bytes), filesCreated = Volatile.Read(ref _files),
        fileLimitReached = _limitReached, errorType = Volatile.Read(ref _errorType),
        rolling = _rolling, rotatedFiles = Interlocked.Read(ref _rotatedFiles),
    };

    private async Task DrainAsync()
    {
        FileStream? file = null;
        long fileBytes = 0;
        try
        {
            await foreach (object sample in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                byte[] line = JsonSerializer.SerializeToUtf8Bytes(sample, Json);
                if (line.Length + 1 > Math.Min(MaxLineBytes, _maxFileBytes))
                {
                    Interlocked.Increment(ref _dropped);
                    if (Interlocked.CompareExchange(ref _errorType, "SampleSizeLimit", null) is null)
                        Warn("Production metrics stopped: a sample exceeded the bounded line/file size.");
                    break;
                }
                if (file is null || fileBytes + line.Length + 1 > _maxFileBytes)
                {
                    if (file is not null) { await file.DisposeAsync(); file = null; }
                    if (_files >= _maxFiles && !_rolling)
                    {
                        _limitReached = true; Interlocked.Increment(ref _dropped);
                        Warn("Production metrics capture reached its per-run file limit; retained all files and stopped collection.");
                        break;
                    }
                    if (_rolling && _retainedFiles.Count >= _maxFiles)
                    {
                        File.Delete(_retainedFiles.Dequeue());
                        Interlocked.Increment(ref _rotatedFiles);
                    }
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    string path = $"{FilePrefix}.{_files:D3}.jsonl";
                    file = new FileStream(path, options);
                    _retainedFiles.Enqueue(path);
                    Interlocked.Increment(ref _files); fileBytes = 0;
                }
                await file.WriteAsync(line, _stop.Token);
                await file.WriteAsync(NewLine, _stop.Token);
                await file.FlushAsync(_stop.Token);
                fileBytes += line.Length + 1;
                Interlocked.Add(ref _bytes, line.Length + 1);
                Interlocked.Increment(ref _written);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Volatile.Write(ref _errorType, error.GetType().Name);
            Warn($"Production metrics writer stopped ({error.GetType().Name}); gameplay continues.");
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
            if (file is not null)
            {
                try { await file.DisposeAsync(); }
                catch (Exception error)
                {
                    Volatile.Write(ref _errorType, error.GetType().Name);
                    Warn($"Production metrics file cleanup failed ({error.GetType().Name}); gameplay continues.");
                }
            }
        }
    }

    private void Warn(string message)
    {
        // The ordinary host logger may itself fail when disk/stdout is unavailable.
        try { _warning?.Invoke(message); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException)
        {
            _stop.Cancel();
            Warn("Production metrics shutdown exceeded three seconds; pending output cancelled.");
            // Do not block server shutdown on a stalled filesystem.
            _ = _writer.ContinueWith(task => { _ = task.Exception; _stop.Dispose(); }, TaskScheduler.Default);
            return;
        }
        catch (Exception error)
        {
            Volatile.Write(ref _errorType, error.GetType().Name);
            Warn($"Production metrics shutdown failed ({error.GetType().Name}); gameplay shutdown continues.");
        }
        _stop.Dispose();
    }
}
