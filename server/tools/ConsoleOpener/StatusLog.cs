namespace Cranberry.Tools.ConsoleOpener;

/// <summary>
/// Console output plus an optional append-only status file (<c>--log</c>).
///
/// <para>
/// The file is the thing <c>start-opener.ps1</c> (and the owner's eye) greps for
/// <c>PATCH OK</c> / <c>READY</c> to prove the console really is available this session. It is
/// APPENDED to and never truncated, never rotated, never deleted — project law: no diagnostic log is
/// ever destroyed. A log that cannot be written must never stop the console from opening, so every
/// write failure is swallowed after one warning on stderr.
/// </para>
/// </summary>
public static class StatusLog
{
    private static string? _path;
    private static bool _warned;

    /// <summary>Where the status lines are appended, or null for console only.</summary>
    public static string? Path => _path;

    /// <summary>Point the log at a file. The directory is created if it does not exist.</summary>
    public static void UseFile(string path)
    {
        _path = path;

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            Warn($"cannot create the directory for the status log \"{path}\": {ex.Message}");
        }
    }

    /// <summary>One status line on stdout and, if configured, appended to the log file.</summary>
    public static void Info(string message) => Write(message, error: false);

    /// <summary>One failure line on stderr and, if configured, appended to the log file.</summary>
    public static void Error(string message) => Write(message, error: true);

    private static void Write(string message, bool error)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {(error ? "ERROR " : "")}{message}";

        if (error)
        {
            Console.Error.WriteLine(line);
        }
        else
        {
            Console.WriteLine(line);
        }

        if (_path is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Warn($"cannot append to the status log \"{_path}\": {ex.Message}");
        }
    }

    /// <summary>One warning per process about a broken log path; never fatal.</summary>
    private static void Warn(string message)
    {
        if (_warned)
        {
            return;
        }

        _warned = true;
        Console.Error.WriteLine($"warning: {message} (continuing without the file log)");
    }
}
