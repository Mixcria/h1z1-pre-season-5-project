using System.Diagnostics;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>Optional host-PC startup. Ordinary installed launchers never call this.</summary>
public static class LocalHostStart
{
    public static async Task EnsureRunning(string root, LauncherSettings settings, CancellationToken stop = default)
    {
        root = Path.GetFullPath(root);
        var server = LauncherSettings.ValidateServer(settings.ServerUrl);
        if (!server.IsLoopback || server.Scheme != "https")
            throw new InvalidOperationException("The local host shortcut requires your local HTTPS server in Settings. Use the normal launcher for a remote server.");
        var profile = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(Path.Combine(root, "Launcher", "launcher.json")))
            ?? throw new InvalidDataException("The local launcher profile is invalid.");
        if (server != LauncherSettings.ValidateServer(profile.ServerUrl)
            || !string.Equals(settings.CertificateSha256, profile.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The launcher settings do not match this local server. Restore the local connection settings, or use the normal launcher.");

        string script = Path.Combine(root, "Server", "tools", "launcher", "Start-LocalLauncher.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("The local server startup script is missing.", script);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        // ArgumentList quotes paths without executing them as command text. The launcher is
        // already painted; only this optional host helper uses a hidden PowerShell process.
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Root", root, "-UseExistingBuild", "-NoOpenLauncher" }) start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the local server helper.");
        var output = helper.StandardOutput.ReadToEndAsync();
        var errors = helper.StandardError.ReadToEndAsync();
        try
        {
            await helper.WaitForExitAsync(deadline.Token);
            await output;
            string error = await errors;
            if (helper.ExitCode != 0)
            {
                string log = Path.Combine(root, "logs", "launcher-start-error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                await File.WriteAllTextAsync(log, error, stop);
                throw new InvalidOperationException("Local server startup failed. Run Update Local Server; details are in logs/launcher-start-error.log.");
            }
            using var http = LauncherConnection.CreateHttp(settings);
            while (true)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    using var response = await http.GetAsync("health", attempt.Token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(250, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new TimeoutException("The local server is still unavailable. Check the latest launcher-host log or run Update-LocalServer.cmd, then reopen the launcher.");
        }
        finally
        {
            // A helper timeout must never terminate the game server or its runtime helpers.
            try { if (!helper.HasExited) helper.Kill(); }
            catch (InvalidOperationException) { } // The helper may have exited between the check and Kill.
        }
    }
}
