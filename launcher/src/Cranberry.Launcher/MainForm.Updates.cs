using System.Diagnostics;
using System.Reflection;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private CancellationTokenSource? _launcherUpdate;
    private bool _replacingLauncher;

    internal void EnableLauncherUpdates(string[] arguments)
    {
        long.TryParse(typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LauncherRelease")?.Value, out long sequence);
        if (sequence == 0) return; // Ordinary developer builds never consume production updates.
        Shown += async (_, _) =>
        {
            RecordUpdateStatus("Started release " + sequence);
            if (LauncherUpdater.HasStartupReceipt)
            {
                try { LauncherUpdater.AcknowledgeStartup(sequence); RecordUpdateStatus("Update startup acknowledged."); }
                catch { Environment.Exit(1); }
                return;
            }
            if (_busy || _install is not null || _game is { HasExited: false }) return;
            if (GameIsOpen()) { RecordUpdateStatus("Update deferred while the game is open."); return; }
            _busy = true; _launcherUpdate = new CancellationTokenSource(); UpdateSessionView();
            bool active = true;
            try
            {
                _status.Text = "Checking for launcher updates...";
                // This dedicated client carries no login token or account cookies.
                using var http = LauncherConnection.CreateHttp(_settings);
                var updater = new LauncherUpdater(http, Environment.ProcessPath!, sequence);
                var release = await updater.Check(_launcherUpdate.Token);
                RecordUpdateStatus(release is null ? "Already up to date." : "Downloading release " + release.Sequence);
                if (release is null) { _status.Text = "Launcher is up to date. Ready to sign in."; return; }
                var downloads = await DownloadConfiguration.ReadTrustedAsync(http, _settings, _launcherUpdate.Token);
                updater = new LauncherUpdater(http, Environment.ProcessPath!, sequence, downloads.LauncherContentBaseUrl);
                _cancel.Visible = true;
                var progress = new Progress<InstallProgress>(p =>
                {
                    if (!active || IsDisposed) return;
                    _status.Text = p.Message;
                    _progress.Value = p.Total == 0 ? 0 : (int)Math.Clamp(p.Complete * 100 / p.Total, 0, 100);
                });
                await updater.Apply(release, arguments, progress, _launcherUpdate.Token,
                    () =>
                    {
                        if (GameIsOpen()) throw new InvalidOperationException("Close the game and reopen the launcher to finish this update.");
                        _replacingLauncher = true; _cancel.Visible = false;
                    });
                active = false; _replacingLauncher = false; Close();
            }
            catch (OperationCanceledException) { RecordUpdateStatus("Update paused or timed out."); if (!IsDisposed) _status.Text = "Launcher update paused. Open the launcher again to retry."; }
            catch (Exception ex) { RecordUpdateStatus(ex.ToString()); if (!IsDisposed) _status.Text = "Launcher update: " + Friendly(ex); }
            finally
            {
                active = false; _replacingLauncher = false; _launcherUpdate.Dispose(); _launcherUpdate = null;
                if (!IsDisposed) { _cancel.Visible = false; _busy = false; UpdateSessionView(); }
            }
        };
    }

    private static void RecordUpdateStatus(string message)
    {
        try
        {
            string directory = GameInstaller.SafePath(AppContext.BaseDirectory, ".cranberry-updates");
            Directory.CreateDirectory(directory);
            File.WriteAllText(GameInstaller.SafePath(directory, "last-update.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static bool GameIsOpen()
    {
        var games = Process.GetProcessesByName("H1Z1");
        bool playing = games.Length != 0;
        foreach (var game in games) game.Dispose();
        return playing;
    }
}
