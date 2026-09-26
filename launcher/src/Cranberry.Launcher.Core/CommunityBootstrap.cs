using System.Diagnostics;

namespace Cranberry.Launcher.Core;

public sealed record CommunityStartRequest(string PackageDirectory, string StateDirectory, string UpdateDirectory, string? Notice);

public interface ICommunityRunningChild : IAsyncDisposable
{
    Task WaitForReady(CancellationToken cancellation);
    Task<int> WaitForExit(CancellationToken cancellation);
    Task Stop(CancellationToken cancellation);
}

public sealed record CommunityRunResult(long Sequence, bool RolledBack, string? Notice, int ExitCode);

/// <summary>
/// The original launcher remains the entry point. It checks and stages updates before
/// starting a GUI/server child, and owns that child until the session ends.
/// </summary>
public sealed class CommunityBootstrap
{
    private readonly string _installation, _data;
    private readonly CommunityUpdateSettings _trust;
    private readonly CommunityUpdateStateStore _store;
    private readonly Func<bool> _gameIsOpen;

    public CommunityBootstrap(string installation, string? stateDirectory = null, Func<bool>? gameIsOpen = null)
    {
        _installation = Path.GetFullPath(installation);
        _gameIsOpen = gameIsOpen ?? NativeGameIsOpen;
        _trust = CommunityUpdateSettings.Load(_installation);
        if (_trust.Sequence == 0 || string.IsNullOrEmpty(_trust.PublicKey))
            throw new InvalidOperationException("Automatic updates are disabled in this development package.");
        _store = new(_installation, _trust);
        _data = Path.GetFullPath(stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CranberryCommunity"));
        if (Within(_data, _store.Root)) throw new InvalidDataException("Player data cannot be kept inside the updater's release directory.");
    }

    public async Task<CommunityRunResult> Run(CommunityReleaseFeed? feed = null,
        Func<CommunityStartRequest, ICommunityRunningChild>? start = null,
        IProgress<InstallProgress>? progress = null, Action? ready = null, CancellationToken cancellation = default)
    {
        start ??= CommunityChildProcess.Start;
        Directory.CreateDirectory(_data);
        // This parent retains both leases while its GUI/server child is running. A second
        // launcher therefore cannot download/activate an update in the middle of a game.
        using var installationLease = _store.AcquireLease();
        using var sessionLease = new FileStream(Path.Combine(_data, "community-launch.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        EnsureNoExistingLocalEdition();
        if (_gameIsOpen()) throw new IOException("Close H1Z1 before opening an updated local launcher. No game process was stopped.");
        var state = _store.Read();
        if (state.Pending is { } interrupted)
        {
            state = state with { Pending = null, Failed = CommunityUpdateStateStore.MarkFailed(state.Failed, interrupted) };
            _store.Write(state);
        }

        string? notice = null;
        CommunityRelease? candidate = null;
        using var ownedFeed = feed is null ? new CommunityReleaseFeed(_trust) : null;
        var source = feed ?? ownedFeed!;
        try
        {
            progress?.Report(new("Checking for updates...", 0, 0));
            using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            check.CancelAfter(TimeSpan.FromSeconds(15));
            var download = await source.Check(state.HighestAccepted, check.Token);
            if (download is not null && !state.Failed.Contains(download.Release.Sha256, StringComparer.OrdinalIgnoreCase))
            {
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                transfer.CancelAfter(TimeSpan.FromMinutes(15));
                string archive = await source.Download(download, _store.Cache, progress, transfer.Token);
                progress?.Report(new("Verifying the update...", 0, 0));
                await CommunityBundleStore.Stage(archive, _store.Versions, download.Release, _trust, transfer.Token);
                candidate = download.Release;
            }
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is HttpRequestException or IOException
            or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
        {
            notice = "Updates are unavailable. Your installed version is ready.";
            Log(ex);
        }
        cancellation.ThrowIfCancellationRequested();

        bool rolledBack = false;
        var attempts = new List<CommunityRelease?>();
        if (candidate is not null) attempts.Add(candidate);
        if (state.Active is not null) attempts.Add(state.Active);
        if (state.Previous is not null && state.Previous.Sha256 != state.Active?.Sha256) attempts.Add(state.Previous);
        attempts.Add(null); // The original complete package is always retained as the last fallback.
        Exception? lastFailure = null;

        foreach (var release in attempts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_gameIsOpen()) throw new IOException("Close H1Z1 before opening an updated local launcher. No game process was stopped.");
            string package = release is null ? _installation : _store.BundlePath(release);
            bool isCandidate = release is not null && release.Sha256 == candidate?.Sha256;
            ICommunityRunningChild? child = null;
            bool acknowledged = false;
            try
            {
                if (release is not null) await CommunityBundleStore.ValidatePackage(package, _trust, release, cancellation);
                EnsureNoExistingLocalEdition();
                if (isCandidate)
                {
                    state = state with { Pending = release, Failed = CommunityUpdateStateStore.MarkFailed(state.Failed, release!) };
                    _store.Write(state); // A killed bootstrap must not retry a broken release forever.
                }
                progress?.Report(new(rolledBack ? "Starting your previous version..." : "Starting Cranberry...", 0, 0));
                child = start(new(package, _data, _store.Root, notice));
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    startup.CancelAfter(TimeSpan.FromSeconds(75));
                    await child.WaitForReady(startup.Token);
                }
                // Readiness is the real local host health response after the GUI has reached
                // its event loop. Save formats remain compatible in update schema 1.
                if (isCandidate)
                {
                    state = state with { HighestAccepted = Math.Max(state.HighestAccepted, release!.Sequence),
                        Previous = state.Active, Active = release, Pending = null,
                        Failed = state.Failed.Where(h => !h.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray() };
                    _store.Write(state);
                }
                else if (rolledBack)
                {
                    state = state with { Active = release, Previous = null, Pending = null };
                    _store.Write(state); // Keep HighestAccepted even when using older binaries.
                }
                acknowledged = true;
                ready?.Invoke();
                int exit = await child.WaitForExit(cancellation);
                return new(release?.Sequence ?? _trust.Sequence, rolledBack, notice, exit);
            }
            catch (Exception ex) when (!acknowledged && !cancellation.IsCancellationRequested && ex is IOException
                or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException
                or System.ComponentModel.Win32Exception)
            {
                lastFailure = ex; Log(ex);
                if (child is not null) await child.Stop(CancellationToken.None);
                if (release is not null)
                {
                    state = state with { Pending = null, Failed = CommunityUpdateStateStore.MarkFailed(state.Failed, release) };
                    _store.Write(state);
                }
                rolledBack = true;
                notice = "The update could not start. Your previous version has been restored.";
            }
            finally
            {
                if (child is not null) await child.DisposeAsync();
            }
        }
        throw new IOException("Cranberry could not start. The installed versions and your saves have been kept.", lastFailure);
    }

    private void EnsureNoExistingLocalEdition()
    {
        try
        {
            using var probe = new FileStream(Path.Combine(_data, "local-edition.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { throw new IOException("Cranberry Local is already open. Close it before opening another launcher."); }
    }

    private void Log(Exception exception)
    {
        try { File.AppendAllText(GameInstaller.SafePath(_store.Root, "last-update.log"), $"{DateTime.UtcNow:O} {exception}\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static bool NativeGameIsOpen()
    {
        var games = Process.GetProcessesByName("H1Z1");
        bool playing = games.Length != 0;
        foreach (var game in games) game.Dispose();
        return playing;
    }

    private static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
