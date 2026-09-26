using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

/// <summary>A short startup window; the message loop stays alive while its child owns the play session.</summary>
internal sealed class CommunityUpdateWindow : Form
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Label _message = new() { AutoSize = false, Dock = DockStyle.Top, Height = 50, Text = "Checking for updates..." };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 20, Style = ProgressBarStyle.Marquee };
    private bool _finished;

    internal CommunityUpdateWindow(string installation, string? data)
    {
        Text = "Cranberry Local";
        ClientSize = new(430, 110); Padding = new(24);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        Controls.Add(_progress); Controls.Add(_message);
        Shown += async (_, _) =>
        {
            try
            {
                var progress = new Progress<InstallProgress>(value =>
                {
                    _message.Text = value.Message;
                    _progress.Style = value.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                    if (value.Total > 0) _progress.Value = (int)Math.Clamp(value.Complete * 100L / value.Total, 0, 100);
                });
                await Task.Run(() => new CommunityBootstrap(installation, data).Run(progress: progress,
                    ready: () => BeginInvoke(() => Hide()), cancellation: _stop.Token));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                Show();
                MessageBox.Show(this, error.Message, "Cranberry Local", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _finished = true; Close(); }
        };
        FormClosing += (_, e) =>
        {
            if (_finished) return;
            e.Cancel = true; _message.Text = "Closing Cranberry..."; _stop.Cancel();
        };
        FormClosed += (_, _) => _stop.Dispose();
    }
}
