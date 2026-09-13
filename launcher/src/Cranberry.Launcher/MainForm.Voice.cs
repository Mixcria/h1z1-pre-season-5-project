using Cranberry.Launcher.Core.Voice;
using NAudio.Wave;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private readonly CheckBox _voiceEnabled = new() { Text = "Enable proximity voice", AutoSize = true, Margin = new(0, 12, 0, 6) };
    private readonly ComboBox _voiceInput = VoiceDeviceBox(), _voiceOutput = VoiceDeviceBox();
    private readonly TextBox _voiceKey = Input("Use the game's proximity chat binding");
    private readonly NumericUpDown _voiceVolume = new() { Minimum = 0, Maximum = 100, Width = 85 };
    private readonly Label _voiceStatus = TextLabel("Voice connects when you launch the game.");
    private readonly System.Windows.Forms.Timer _voiceTimer = new() { Interval = 20 };
    private CancellationTokenSource? _voiceStop;
    private Task? _voiceLoop;
    private ProximityAudio? _audio;
    private ProximityVoiceClient? _voiceClient;
    private string? _voiceLastProblem;

    private void ReportVoiceProblem(string message)
    {
        _voiceStatus.Text = message;
        if (_voiceLastProblem == message) return;
        _voiceLastProblem = message;
        _status.Text = "Proximity voice: " + message;
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cranberry", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "voice-errors.log"), $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record AudioDevice(int Id, string Name) { public override string ToString() => Name; }
    private static ComboBox VoiceDeviceBox() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 660,
        BackColor = LauncherTheme.Field, ForeColor = LauncherTheme.Text, Margin = new(0, 0, 0, 8) };
    private void BuildVoiceSettings(FlowLayoutPanel flow)
    {
        _voiceEnabled.Checked = _settings.ProximityVoiceEnabled;
        _voiceKey.Text = _settings.VoicePushToTalkKey;
        _voiceVolume.Value = Math.Clamp(_settings.VoiceVolume, 0, 100);
        _voiceStatus.Width = 700; _voiceStatus.Height = 42;
        flow.Controls.Add(_voiceEnabled);
        _voiceInput.Items.Add(new AudioDevice(-1, "Windows default microphone"));
        _voiceOutput.Items.Add(new AudioDevice(-1, "Windows default speakers / headphones"));
        try
        {
            for (int i = 0; i < WaveIn.DeviceCount; i++) _voiceInput.Items.Add(new AudioDevice(i, WaveIn.GetCapabilities(i).ProductName));
            for (int i = 0; i < WaveOut.DeviceCount; i++) _voiceOutput.Items.Add(new AudioDevice(i, WaveOut.GetCapabilities(i).ProductName));
        }
        catch (NAudio.MmException) { _voiceStatus.Text = "Audio devices unavailable. Check Windows sound settings."; }
        _voiceInput.SelectedIndex = Math.Clamp(_settings.VoiceInputDevice + 1, 0, _voiceInput.Items.Count - 1);
        _voiceOutput.SelectedIndex = Math.Clamp(_settings.VoiceOutputDevice + 1, 0, _voiceOutput.Items.Count - 1);
        var inputLabel = TextLabel("Microphone"); inputLabel.Width = 660;
        var outputLabel = TextLabel("Speakers / headphones"); outputLabel.Width = 660;
        flow.Controls.Add(inputLabel); flow.Controls.Add(_voiceInput);
        flow.Controls.Add(outputLabel); flow.Controls.Add(_voiceOutput);
        Field(flow, "Push-to-talk override (optional, e.g. KP_4, Mouse_2 or Alt+V)", _voiceKey, 660);
        var volumeLabel = TextLabel("Voice volume (%)"); volumeLabel.Width = 180;
        flow.Controls.Add(Row(volumeLabel, _voiceVolume));
        var hint = TextLabel("Hold your proximity key while in the game. Default: middle mouse / numpad 4. Alt+M mutes voice.");
        hint.Width = 700; hint.Height = 40; flow.Controls.Add(hint); flow.Controls.Add(_voiceStatus);
        _voiceTimer.Tick += (_, _) =>
        {
            if (_audio is null || _voiceClient is null) return;
            try
            {
                _audio.Tick();
                _voiceStatus.Text = _audio.Error ?? (_voiceClient.Deafened ? "Voice muted. Press your voice mute key to enable it."
                    : _audio.Talking ? "Transmitting proximity voice."
                    : _voiceClient.CanTalk ? "Proximity voice ready. Hold " + _audio.BindingLabel + " to talk."
                    : "Voice connected. Available when your character is alive in a match.");
                if (_audio.Error is { } problem) ReportVoiceProblem(problem);
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            { ReportVoiceProblem("Audio device unavailable. Select your microphone and headphones in Settings, then save to retry."); _audio.Dispose(); _audio = null; }
        };
    }
    private void StartVoice(int gamePid)
    {
        if (!_settings.ProximityVoiceEnabled) { _voiceStatus.Text = "Proximity voice disabled in Settings."; return; }
        _voiceLastProblem = null;
        _voiceStop = new(); _voiceTimer.Start();
        _voiceLoop = RunVoice(gamePid, _voiceStop.Token);
    }
    private async Task RunVoice(int gamePid, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await using var client = new ProximityVoiceClient();
                try
                {
                    _voiceStatus.Text = "Connecting proximity voice...";
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    deadline.CancelAfter(6000);
                    await client.Connect(_settings, _session!.Token, deadline.Token);
                    stop.ThrowIfCancellationRequested();
                    _voiceClient = client;
                    _audio = new(client, _settings, gamePid, _gameInput!);
                    if (_voiceLastProblem is not null) _status.Text = "Proximity voice reconnected.";
                    _voiceLastProblem = null;
                    await client.Completion.WaitAsync(stop);
                    _voiceStatus.Text = "Voice disconnected. Reconnecting...";
                }
                catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or IOException or NAudio.MmException or InvalidOperationException)
                {
                    if (stop.IsCancellationRequested) break;
                    ReportVoiceProblem(ex is NAudio.MmException
                        ? "Cannot open the selected audio device. Check voice devices in Settings. Retrying..."
                        : "Cannot connect to voice. Retrying; check the server address and voice settings.");
                }
                finally { _audio?.Dispose(); _audio = null; _voiceClient = null; }
                await client.DisposeAsync();
                await Task.Delay(5000, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task StopVoice()
    {
        _voiceTimer.Stop(); _voiceStop?.Cancel();
        _audio?.Dispose(); _audio = null;
        if (_voiceLoop is not null) await _voiceLoop;
        _voiceLoop = null; _voiceStop?.Dispose(); _voiceStop = null;
        _voiceStatus.Text = "Voice connects when you launch the game.";
    }
}
