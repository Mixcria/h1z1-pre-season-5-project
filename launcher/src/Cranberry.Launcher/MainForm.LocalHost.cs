using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private bool _startingLocalHost;
    private readonly CancellationTokenSource _localHostStop = new();

    internal void EnableLocalEdition(LocalEdition edition)
    {
        _startingLocalHost = true;
        Text = "Cranberry Local";
        _status.Text = "Starting your local server...";
        _server.ReadOnly = _pin.ReadOnly = true;
        _keepCharacter.Text = "Make this the local administrator";
        _keepCharacter.Checked = !File.Exists(Path.Combine(edition.Root, "state", "launcher", "social.json"));
        UpdateSessionView();
        Shown += (_, _) => BeginInvoke(async () =>
        {
            try
            {
                await Task.Run(() => edition.Start(_localHostStop.Token));
                if (!IsDisposed && !Disposing)
                {
                    _startingLocalHost = false;
                    _status.Text = "Local server ready. Create a local account or sign in to play.";
                    UpdateSessionView();
                }
            }
            catch (OperationCanceledException) when (_localHostStop.IsCancellationRequested) { }
            catch (Exception ex)
            { if (!IsDisposed && !Disposing) _status.Text = Friendly(ex); }
        });
        FormClosed += (_, _) => _localHostStop.Cancel();
    }

    internal void EnableLocalHost(string root)
    {
        _startingLocalHost = true;
        _status.Text = "Starting local server...";
        UpdateSessionView();
        Shown += (_, _) => BeginInvoke(async () =>
        {
            try
            {
                // File/process setup runs off the UI thread; the window opens first.
                await Task.Run(() => LocalHostStart.EnsureRunning(root, _settings, _localHostStop.Token));
                if (!IsDisposed && !Disposing) _status.Text = "Local server ready. Sign in to play.";
            }
            catch (OperationCanceledException) when (_localHostStop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing) _status.Text = Friendly(ex);
            }
            finally
            {
                _startingLocalHost = false;
                if (!IsDisposed && !Disposing) UpdateSessionView();
            }
        });
        FormClosed += (_, _) => _localHostStop.Cancel();
    }
}
