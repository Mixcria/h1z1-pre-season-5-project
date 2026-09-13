using Concentus;
using Concentus.Enums;

namespace Cranberry.Launcher.Core.Voice;

public sealed class VoiceEncoder : IDisposable
{
    private readonly IOpusEncoder _encoder = OpusCodecFactory.CreateEncoder(VoiceWire.SampleRate, 1,
        OpusApplication.OPUS_APPLICATION_VOIP);
    public VoiceEncoder() { _encoder.Bitrate = 24000; _encoder.Complexity = 5; _encoder.UseDTX = true; }
    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length != VoiceWire.FrameSamples) throw new ArgumentException("One 20 ms mono frame is required");
        Span<byte> encoded = stackalloc byte[VoiceWire.MaxOpusBytes];
        int length = _encoder.Encode(pcm, VoiceWire.FrameSamples, encoded, encoded.Length);
        if (length <= 0) throw new InvalidDataException("Voice encoder returned no frame");
        return encoded[..length].ToArray();
    }
    public void Dispose() => _encoder.Dispose();
}

public sealed class VoiceDecoder : IDisposable
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(VoiceWire.SampleRate, 1);
    public bool TryDecode(ReadOnlySpan<byte> opus, Span<short> pcm)
    {
        if (opus.Length is < 1 or > VoiceWire.MaxOpusBytes || pcm.Length < VoiceWire.FrameSamples) return false;
        try { return _decoder.Decode(opus, pcm, VoiceWire.FrameSamples, false) == VoiceWire.FrameSamples; }
        catch (Exception ex) when (ex is ArgumentException or OpusException or IndexOutOfRangeException) { return false; }
    }
    public void Dispose() => _decoder.Dispose();
}
