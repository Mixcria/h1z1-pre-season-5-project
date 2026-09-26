using System.Diagnostics;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>Owns only the launcher it started and the exact host identity reported by that launcher.</summary>
public sealed class CommunityChildProcess : ICommunityRunningChild
{
    private readonly CommunityChildContext _context;
    private readonly string _receiptPath;
    private readonly Process _process;
    private readonly Func<bool> _gameIsOpen;
    private Process? _host;
    private bool _disposed;

    private CommunityChildProcess(CommunityStartRequest request, ProcessStartInfo start, Func<bool>? gameIsOpen)
    {
        _context = CommunityChildSession.Create(request);
        _gameIsOpen = gameIsOpen ?? CommunityBootstrap.NativeGameIsOpen;
        _receiptPath = CommunityChildSession.ReceiptPath(_context);
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.Environment[CommunityChildSession.EnvironmentName] = JsonSerializer.Serialize(_context);
        _process = Process.Start(start) ?? throw new IOException("Could not open the updated launcher.");
    }

    public static ICommunityRunningChild Start(CommunityStartRequest request) => Start(request,
        new ProcessStartInfo(GameInstaller.SafePath(request.PackageDirectory, "Cranberry.Launcher.exe"))
        {
            WorkingDirectory = request.PackageDirectory,
            ArgumentList = { "--local-data", request.StateDirectory },
        });

    // Explicit process injection permits real pipe/exit tests without starting a game or a GUI.
    public static CommunityChildProcess Start(CommunityStartRequest request, ProcessStartInfo start,
        Func<bool>? gameIsOpen = null) => new(request, start, gameIsOpen);

    public async Task WaitForReady(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var receipt = ReadReceipt();
            if (receipt?.Failure is { } failure) throw new IOException("The updated launcher could not start: " + failure);
            if (_process.HasExited) throw new IOException("The updated launcher closed before its local server was ready.");
            if (receipt?.Ready == true)
            {
                if (_host is null || _host.HasExited) throw new IOException("The updated local server stopped during startup.");
                return;
            }
            await Task.Delay(100, cancellation);
        }
    }

    public async Task<int> WaitForExit(CancellationToken cancellation)
    {
        await _process.WaitForExitAsync(cancellation);
        return _process.ExitCode;
    }

    public async Task Stop(CancellationToken cancellation)
    {
        if (_disposed) return;
        ReadReceipt();
        if (!_process.HasExited)
        {
            try { _process.StandardInput.Close(); }
            catch (IOException) { }
            if (!await Exited(_process, TimeSpan.FromSeconds(15), cancellation))
            {
                ReadReceipt();
                if (_gameIsOpen())
                    throw new IOException("Close H1Z1 and its launcher before retrying. The running session was kept open.");
                // Without an owned-host receipt, leave a hung child alive holding its data
                // lease. Killing it could orphan an unidentifiable server and allow overlap.
                if (_host is null)
                    throw new IOException("The launcher has not closed. Close it before retrying; no replacement was started.");
                KillOwned(_process);
                if (!await Exited(_process, TimeSpan.FromSeconds(3), cancellation))
                    throw new IOException("The owned launcher did not stop; no replacement was started.");
            }
        }
        ReadReceipt();
        if (_host is not null && !_host.HasExited)
        {
            if (!await Exited(_host, TimeSpan.FromSeconds(8), cancellation))
            {
                KillOwned(_host);
                if (!await Exited(_host, TimeSpan.FromSeconds(3), cancellation))
                    throw new IOException("The owned server did not stop; no replacement was started.");
            }
        }
        // Dispose/EOF must release the data lease before the fallback can copy its metadata.
        using var lease = new FileStream(Path.Combine(_context.Request.StateDirectory, "local-edition.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await Stop(CancellationToken.None);
        _host?.Dispose(); _process.Dispose(); _disposed = true;
        try { File.Delete(_receiptPath); } catch (IOException) { }
    }

    private CommunityChildReceipt? ReadReceipt()
    {
        if (!File.Exists(_receiptPath)) return null;
        if (new FileInfo(_receiptPath).Length > 16 * 1024) throw new InvalidDataException("Invalid launcher readiness response.");
        using var input = new FileStream(_receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var receipt = JsonSerializer.Deserialize<CommunityChildReceipt>(input);
        if (receipt is null || receipt.Nonce != _context.Nonce || receipt.LauncherId != _process.Id)
            throw new InvalidDataException("Launcher readiness response does not match its owner.");
        if (_host is null && receipt.HostId is { } id && receipt.HostStartTicks is { } ticks)
        {
            Process? host = null;
            try
            {
                host = Process.GetProcessById(id);
                if (host.StartTime.ToUniversalTime().Ticks != ticks) return receipt; // PID reused; never take ownership.
                string expected = GameInstaller.SafePath(_context.Request.PackageDirectory, "runtime/Cranberry.Host.exe");
                if (!string.Equals(host.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Local server readiness response has an unexpected executable.");
                _ = host.Handle; // Retain a handle, never reopen by a potentially reused PID.
                _host = host; host = null;
            }
            catch (ArgumentException) { } // The owned host already exited.
            catch (InvalidOperationException) { }
            finally { host?.Dispose(); }
        }
        return receipt;
    }

    private static async Task<bool> Exited(Process process, TimeSpan timeout, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); return true; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return process.HasExited; }
    }

    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!process.HasExited) throw; }
    }
}
