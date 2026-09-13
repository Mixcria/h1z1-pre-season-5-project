using System.Runtime.InteropServices;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Core.Voice;
using NAudio.Wave;

namespace Cranberry.Launcher;

/// <summary>Microphone capture is gated by physical push-to-talk, game focus and live server permission.</summary>
internal sealed class ProximityAudio : IDisposable
{
    private sealed class Output(VoiceMixer mixer) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoiceWire.SampleRate, 2);
        public int Read(byte[] buffer, int offset, int count)
        {
            mixer.Read(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count)), Environment.TickCount64);
            return count;
        }
    }
    private readonly ProximityVoiceClient _voice;
    private readonly LauncherSettings _settings;
    private readonly int _gamePid;
    private readonly GameInput _keys;
    private readonly WaveOutEvent _output;
    private readonly object _gate = new();
    private WaveInEvent? _input;
    private bool _disposed, _stopping, _muteWasDown, _upWasDown, _downWasDown;
    private VoiceBindings _bindings;
    private long _nextBindingsRead;
    private readonly short[] _frame = new short[VoiceWire.FrameSamples];
    private int _samples;
    public bool Talking { get; private set; }
    private string? _bindingError, _deviceError;
    public string? Error => _deviceError ?? _bindingError;
    public string BindingLabel => _bindings.TalkLabel;

    public ProximityAudio(ProximityVoiceClient voice, LauncherSettings settings, int gamePid, GameInput keys)
    {
        _voice = voice; _settings = settings; _gamePid = gamePid; _keys = keys;
        _bindings = ReadBindings();
        voice.Mixer.Volume = Math.Clamp(settings.VoiceVolume, 0, 100) / 100f;
        _output = new WaveOutEvent { DeviceNumber = settings.VoiceOutputDevice, DesiredLatency = 60, NumberOfBuffers = 3 };
        try { _output.Init(new Output(voice.Mixer)); _output.Play(); }
        catch { _output.Dispose(); throw; }
    }
    private VoiceBindings ReadBindings()
    {
        try
        {
            var bindings = VoiceBindings.Load(Path.Combine(_settings.InstallDirectory, "InputProfile_User.xml"), _settings.VoicePushToTalkKey);
            _bindingError = null;
            return bindings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        { _bindingError = "Cannot read voice keys. Set a push-to-talk key in launcher Settings."; return new([], [], [], [], "unavailable"); }
    }
    private bool GameFocused()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
        return process == (uint)_gamePid;
    }
    private bool Down(int key) => _keys.Down(key);
    private bool CaptureAllowed() => !_disposed && Error is null && _voice.CanTalk && GameFocused() && _keys.Responsive
        && VoiceBindings.Pressed(_bindings.Talk, Down);
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (Environment.TickCount64 >= _nextBindingsRead)
            {
                _nextBindingsRead = Environment.TickCount64 + 1000;
                _bindings = ReadBindings();
            }
            bool focused = GameFocused();
            bool mute = focused && VoiceBindings.Pressed(_bindings.Deafen, Down);
            if (mute && !_muteWasDown) _voice.SetDeafened(!_voice.Deafened);
            _muteWasDown = mute;
            bool up = focused && VoiceBindings.Pressed(_bindings.Louder, Down);
            bool down = focused && VoiceBindings.Pressed(_bindings.Quieter, Down);
            if (up && !_upWasDown) _voice.Mixer.Volume = Math.Min(1, _voice.Mixer.Volume + .1f);
            if (down && !_downWasDown) _voice.Mixer.Volume = Math.Max(0, _voice.Mixer.Volume - .1f);
            _upWasDown = up; _downWasDown = down;
            Talking = CaptureAllowed(); _voice.SetTransmitting(Talking);
            if (Talking && _input is null)
            {
                var input = new WaveInEvent { DeviceNumber = _settings.VoiceInputDevice,
                    WaveFormat = new WaveFormat(VoiceWire.SampleRate, 16, 1), BufferMilliseconds = VoiceWire.FrameMs, NumberOfBuffers = 3 };
                _input = input; _samples = 0;
                input.DataAvailable += Captured;
                input.RecordingStopped += (_, e) =>
                {
                    lock (_gate)
                    {
                        input.Dispose();
                        if (_input == input) { _input = null; _stopping = false; _samples = 0; }
                        if (!_disposed && e.Exception is not null)
                        {
                            _deviceError = "Microphone stopped. Check your input device in Settings.";
                            Talking = false; _voice.SetTransmitting(false);
                        }
                    }
                };
                try { input.StartRecording(); }
                catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
                {
                    _deviceError = "Microphone unavailable. Check Windows microphone access and your input device.";
                    Talking = false; _voice.SetTransmitting(false); _input = null; input.Dispose();
                }
            }
            else if (!Talking && _input is not null && !_stopping)
            { _stopping = true; _samples = 0; _input.StopRecording(); }
        }
    }
    private void Captured(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            // Also check on the capture callback, so a stalled launcher UI cannot keep transmitting.
            if (sender != _input || _stopping || !CaptureAllowed())
            {
                _samples = 0; Array.Clear(_frame); Talking = false; _voice.SetTransmitting(false);
                if (sender == _input && !_stopping) { _stopping = true; _input?.StopRecording(); }
                return;
            }
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
            foreach (short sample in samples)
            {
                _frame[_samples++] = sample;
                if (_samples == _frame.Length) { _voice.Submit(_frame); _samples = 0; }
            }
        }
    }
    public void Dispose()
    {
        WaveInEvent? input;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; Talking = false; _voice.SetTransmitting(false);
            input = _input; _input = null; _samples = 0;
        }
        // Stop native capture immediately. RecordingStopped disposes its buffers after the
        // capture thread has finished; freeing them while DataAvailable runs races WinMM.
        try { input?.StopRecording(); }
        catch (NAudio.MmException)
        {
            // An unplugged device can fail its stop call; still release its native handle.
            try { input?.Dispose(); } catch (NAudio.MmException) { }
        }
        try { _output.Dispose(); } catch (NAudio.MmException) { }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
