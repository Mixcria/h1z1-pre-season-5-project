namespace Cranberry.Launcher.Core.Voice;

/// <summary>Bounded, short-lived per-speaker Opus decoders and stereo playback buffers.</summary>
public sealed class VoiceMixer : IDisposable
{
    private sealed class Speaker : IDisposable
    {
        public readonly VoiceDecoder Decoder = new();
        public readonly Queue<(short[] Samples, float Left, float Right)> Frames = [];
        public uint Sequence;
        public bool HasSequence, Started;
        public int Offset;
        public long LastMs, FirstMs;
        public void Dispose() => Decoder.Dispose();
    }
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Speaker> _speakers = [];
    private long _clockOffset = long.MaxValue;
    private bool _disposed;
    public int BufferedSpeakers { get { lock (_gate) return _speakers.Count; } }
    public float Volume { get; set; } = 1;

    public bool Receive(ReadOnlySpan<byte> message, long now)
    {
        if (!VoiceWire.TryAudio(message, out var frame)) return false;
        lock (_gate)
        {
            if (_disposed) return false;
            // A connection establishes a monotonic clock offset; excess transit delay is stale speech.
            long offset = now - frame.SentMs;
            _clockOffset = Math.Min(_clockOffset, offset);
            if (offset - _clockOffset > VoiceRouter.MaxQueuedAgeMs) return false;
            Prune(now);
            if (!_speakers.TryGetValue(frame.Speaker, out var speaker))
            {
                if (_speakers.Count >= 150) return false;
                _speakers.Add(frame.Speaker, speaker = new() { FirstMs = now });
            }
            if (speaker.HasSequence && unchecked((int)(frame.Sequence - speaker.Sequence)) <= 0) return false;
            speaker.HasSequence = true; speaker.Sequence = frame.Sequence; speaker.LastMs = now;
            short[] pcm = new short[VoiceWire.FrameSamples];
            if (!speaker.Decoder.TryDecode(message[VoiceWire.AudioHeader..], pcm)) return false;
            while (speaker.Frames.Count >= 3) { speaker.Frames.Dequeue(); speaker.Offset = 0; }
            speaker.Frames.Enqueue((pcm, frame.Left, frame.Right));
            return true;
        }
    }

    public void Read(Span<float> stereo, long now)
    {
        stereo.Clear();
        lock (_gate)
        {
            if (_disposed) return;
            Prune(now);
            foreach (var speaker in _speakers.Values)
            {
                if (!speaker.Started)
                {
                    if (speaker.Frames.Count < 2 && now - speaker.FirstMs < 40) continue;
                    speaker.Started = true;
                }
                for (int i = 0; i + 1 < stereo.Length && speaker.Frames.TryPeek(out var frame); i += 2)
                {
                    float sample = frame.Samples[speaker.Offset++] / 32768f;
                    stereo[i] += sample * frame.Left;
                    stereo[i + 1] += sample * frame.Right;
                    if (speaker.Offset == frame.Samples.Length) { speaker.Frames.Dequeue(); speaker.Offset = 0; }
                }
            }
            float volume = float.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : 0;
            for (int i = 0; i < stereo.Length; i++) stereo[i] = Math.Clamp(stereo[i] * volume, -1, 1);
        }
    }
    private void Prune(long now)
    {
        foreach (var entry in _speakers.ToArray())
        {
            if (now - entry.Value.LastMs > 120) { entry.Value.Frames.Clear(); entry.Value.Offset = 0; }
            if (now - entry.Value.LastMs <= 1000) continue;
            entry.Value.Dispose(); _speakers.Remove(entry.Key);
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var speaker in _speakers.Values) speaker.Dispose();
            _speakers.Clear(); _clockOffset = long.MaxValue;
        }
    }
    public void Dispose() { lock (_gate) { Clear(); _disposed = true; } }
}
