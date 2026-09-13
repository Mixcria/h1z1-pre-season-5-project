using System.Collections.Concurrent;
using Cranberry.Transport;

namespace Cranberry.Host;

/// <summary>
/// Writes every line to the console and to one file per run. Files are never overwritten: the name
/// carries a timestamp.
/// <para>
/// The caller's thread only formats the line and hands it to a bounded queue. In production the
/// caller is the single gateway listener thread, which also runs every player's gameplay, and the
/// console is a journald stream socket, so a synchronous write there was a syscall on the game
/// thread per log line. A dedicated writer thread owns the console and the file. A full queue
/// drops lines and counts them rather than blocking the game thread; the writer announces each
/// burst of drops in the log itself. Dispose drains the queue before releasing the file, so a line
/// logged before shutdown is on disk after it, and an unhandled exception drains it too. The
/// writer thread owns the file for its whole life: if the drain outlives Dispose's wait, the file
/// stays open until the writer finishes instead of being torn out from under a batch in flight.
/// </para>
/// <para>
/// A sink that fails (disk full, a closed journald socket) is retired alone and the failure is
/// announced on the sink that still works. Only when neither sink can be written does the writer
/// stop; from then on <see cref="WriteError"/> is set, <see cref="Log"/> counts every line as
/// dropped, and the failure and the number of queued lines lost went to standard error.
/// </para>
/// </summary>
public sealed class ConsoleFileLog : ITransportLog, IDisposable
{
    private readonly StreamWriter? _file;
    private readonly TextWriter _console;
    private readonly TransportLogLevel _minimum;
    private readonly BlockingCollection<string> _lines;
    private readonly Thread _writer;
    private readonly UnhandledExceptionEventHandler _onUnhandled;
    private long _dropped;
    private long _droppedAnnounced;
    private long _enqueued;
    private long _written;
    private volatile bool _disposed;
    private bool _consoleAlive = true;
    private bool _fileAlive;
    private Exception? _consoleError;

    public ConsoleFileLog(
        string path,
        TransportLogLevel minimum = TransportLogLevel.Debug,
        bool writeFile = true,
        int queueCapacity = 16384,
        TextWriter? console = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        if (writeFile)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            _file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = false };
            _fileAlive = true;
        }
        _console = console ?? Console.Out;
        _minimum = minimum;
        FilePath = path;
        _lines = new BlockingCollection<string>(queueCapacity);
        _writer = new Thread(Drain) { IsBackground = true, Name = "host-log" };
        _writer.Start();
        _onUnhandled = (_, _) => Flush(TimeSpan.FromSeconds(3));
        AppDomain.CurrentDomain.UnhandledException += _onUnhandled;
    }

    public static ConsoleFileLog FromEnvironment(string path, Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var minimum = Enum.TryParse<TransportLogLevel>(read("CRANBERRY_LOG_LEVEL"), true, out var parsed)
            && Enum.IsDefined(parsed) ? parsed : TransportLogLevel.Debug;
        return new(path, minimum, writeFile: read("CRANBERRY_FILE_LOG") != "0");
    }

    public string FilePath { get; }

    /// <summary>Lines refused because the queue was full. Announced in the log by the writer.</summary>
    public long DroppedLines => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Set once if the writer thread failed, which happens only when neither the console nor the
    /// file could be written; later lines are counted as dropped and discarded.
    /// </summary>
    public Exception? WriteError { get; private set; }

    /// <summary>Set once if the file sink failed; the console keeps receiving every line.</summary>
    public Exception? FileError { get; private set; }

    public bool IsEnabled(TransportLogLevel level) => level >= _minimum;

    public void Log(TransportLogLevel level, string message)
    {
        if (!IsEnabled(level) || _disposed)
        {
            return;
        }

        if (WriteError is not null)
        {
            // The writer is gone; nothing will ever drain the queue. Count the loss rather
            // than parking the line where it can never be observed.
            Interlocked.Increment(ref _dropped);
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss.fff} {Tag(level)} {message}";
        try
        {
            if (_lines.TryAdd(line))
            {
                Interlocked.Increment(ref _enqueued);
            }
            else
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch (InvalidOperationException)
        {
            // Completed by a concurrent Dispose; the process is shutting down.
        }
    }

    /// <summary>
    /// Waits until everything queued so far has been written and the file flushed, or the timeout
    /// passes. Returns false on timeout. Used before the process dies on an unhandled exception.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        long target = Interlocked.Read(ref _enqueued);
        var deadline = DateTime.UtcNow + timeout;
        while (Interlocked.Read(ref _written) < target)
        {
            if (DateTime.UtcNow >= deadline || WriteError is not null || !_writer.IsAlive)
            {
                return false;
            }

            Thread.Sleep(5);
        }

        return true;
    }

    private void Drain()
    {
        try
        {
            while (!_lines.IsCompleted)
            {
                if (!_lines.TryTake(out string? line, 250))
                {
                    continue;
                }

                WriteBatch(line);
            }
        }
        catch (Exception ex)
        {
            WriteError = ex;
            long lost = _lines.Count;
            Interlocked.Add(ref _dropped, lost);
            try { Console.Error.WriteLine($"Host log writer failed: {ex.Message}; {lost} queued line(s) lost; later lines are dropped"); } catch { }
        }
        finally
        {
            // Closed here, and only here, so a batch in flight is never cut off by Dispose.
            try { _file?.Dispose(); } catch { }
        }
    }

    private void WriteBatch(string first)
    {
        // Everything already queued goes out as one console write and one file flush, bounded so
        // a sustained flood still reports its drops and flushes regularly.
        var batch = new System.Text.StringBuilder(first.Length + 64);
        batch.AppendLine(first);
        int count = 1;
        while (count < 512 && _lines.TryTake(out string? line))
        {
            batch.AppendLine(line);
            count++;
        }

        long dropped = Interlocked.Read(ref _dropped);
        if (dropped != _droppedAnnounced)
        {
            batch.AppendLine($"{DateTime.Now:HH:mm:ss.fff} WRN host log: {dropped - _droppedAnnounced} line(s) dropped because the log queue was full ({dropped} in total)");
            _droppedAnnounced = dropped;
        }

        string text = batch.ToString();
        if (_consoleAlive)
        {
            try
            {
                _console.Write(text);
                _console.Flush();
            }
            catch (Exception ex)
            {
                _consoleAlive = false;
                _consoleError = ex;
                text += $"{DateTime.Now:HH:mm:ss.fff} ERR host log: console write failed ({ex.GetType().Name}: {ex.Message}); continuing with the file only{Environment.NewLine}";
            }
        }

        if (_file is not null && _fileAlive)
        {
            try
            {
                _file.Write(text);
                _file.Flush();
            }
            catch (Exception ex)
            {
                _fileAlive = false;
                FileError = ex;
                string note = $"{DateTime.Now:HH:mm:ss.fff} ERR host log: file write failed ({ex.GetType().Name}: {ex.Message}); continuing with the console only{Environment.NewLine}";
                if (_consoleAlive)
                {
                    try { _console.Write(note); _console.Flush(); } catch { _consoleAlive = false; }
                }
            }
        }

        if (!_consoleAlive && !(_file is not null && _fileAlive))
        {
            // The batch in hand is lost with the sinks: count it, and keep the real cause.
            Interlocked.Add(ref _dropped, count);
            string why = _file is null
                ? $"console: {_consoleError?.Message}"
                : $"console: {_consoleError?.Message}; file: {FileError?.Message}";
            throw new IOException($"no log sink accepts writes ({why})", _consoleError ?? FileError);
        }

        Interlocked.Add(ref _written, count);
    }

    private static string Tag(TransportLogLevel level) => level switch
    {
        TransportLogLevel.Trace => "TRC",
        TransportLogLevel.Debug => "DBG",
        TransportLogLevel.Info => "INF",
        TransportLogLevel.Warning => "WRN",
        _ => "ERR",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
        _lines.CompleteAdding();
        if (!_writer.Join(TimeSpan.FromSeconds(5)))
        {
            // Still draining a backlog. The writer closes the file when it finishes; disposing
            // the queue or the file now would cut the last batch off mid-write.
            try { Console.Error.WriteLine($"Host log writer still draining {_lines.Count} line(s) at shutdown; leaving it to finish"); } catch { }
            return;
        }

        _lines.Dispose();
    }
}
