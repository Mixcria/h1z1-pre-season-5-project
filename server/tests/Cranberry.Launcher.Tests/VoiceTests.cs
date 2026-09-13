using System.Numerics;
using Cranberry.Launcher.Core.Voice;

namespace Cranberry.Launcher.Tests;

public sealed class VoiceTests
{
    internal static short[] Tone(int frame = 0) => Enumerable.Range(0, VoiceWire.FrameSamples)
        .Select(i => (short)(Math.Sin(2 * Math.PI * 440 * (i + frame * VoiceWire.FrameSamples) / VoiceWire.SampleRate) * 12000)).ToArray();
    internal static byte[] Opus() { using var encoder = new VoiceEncoder(); return encoder.Encode(Tone()); }
    private static VoiceParticipant Player(string id, ulong guid, float x = 0, ulong match = 1) => new(id, guid, match, new(x, 0, 0), 0);
    private static VoiceDelivery[] Audio(VoicePeer peer)
    {
        List<VoiceDelivery> frames = [];
        while (peer.Outgoing.TryRead(out var frame)) if (frame.Control is null) frames.Add(frame);
        return frames.ToArray();
    }

    [Fact]
    public void OpusRoundTripPreservesAnAudibleToneAndRejectsOversizedFrames()
    {
        using var encoder = new VoiceEncoder(); using var decoder = new VoiceDecoder();
        short[] decoded = new short[VoiceWire.FrameSamples];
        for (int i = 0; i < 8; i++)
        {
            byte[] bytes = encoder.Encode(Tone(i));
            Assert.InRange(bytes.Length, 1, VoiceWire.MaxOpusBytes);
            Assert.True(decoder.TryDecode(bytes, decoded));
        }
        Assert.InRange(decoded.Select(v => (double)v * v).Average(), 20_000_000, 150_000_000);
        Assert.False(decoder.TryDecode(new byte[VoiceWire.MaxOpusBytes + 1], decoded));
        Assert.False(decoder.TryDecode([], decoded));
        Assert.Throws<ArgumentException>(() => encoder.Encode(new short[319]));
    }

    [Fact]
    public void SpatialAudioFadesInThreeDimensionsPansAndSeparatesMatches()
    {
        var listener = Player("listener", 1);
        Assert.True(VoiceSpatial.TryGains(Player("speaker", 2, 2), listener, 75, out float left, out float right));
        Assert.True(right > left);
        Assert.True(VoiceSpatial.TryGains(Player("speaker", 2, -2), listener, 75, out float left2, out float right2));
        Assert.Equal(left, right2, 5); Assert.Equal(right, left2, 5);
        Assert.True(VoiceSpatial.TryGains(Player("speaker", 2, 40), listener, 75, out _, out float distant));
        Assert.True(distant < right);
        Assert.False(VoiceSpatial.TryGains(Player("speaker", 2, 75), listener, 75, out _, out _));
        Assert.False(VoiceSpatial.TryGains(Player("speaker", 2) with { Position = new(0, 100, 0) }, listener, 75, out _, out _));
        Assert.False(VoiceSpatial.TryGains(Player("speaker", 2, match: 2), listener, 75, out _, out _));
        Assert.False(VoiceSpatial.TryGains(listener, listener, 75, out _, out _));
        Assert.False(VoiceSpatial.TryGains(Player("speaker", 2, float.NaN), listener, 75, out _, out _));
    }

    [Fact]
    public void OnlyAuthoritativeNearbyPlayersReceiveVoiceAndQueuedSpeechIsRechecked()
    {
        var router = new VoiceRouter();
        var source = router.Connect("source"); var target = router.Connect("target");
        var menu = router.Connect("menu"); var far = router.Connect("far"); var otherMatch = router.Connect("other");
        var world = new[] { Player("source", 1), Player("target", 2, 3), Player("far", 3, 100), Player("other", 4, match: 2) };
        router.UpdateWorld(world, 1000);
        Assert.True(router.Receive(source, VoiceWire.EncodeUpload(1, Opus()), 1001));
        var delivery = Assert.Single(Audio(target));
        Assert.Equal(1UL, delivery.Speaker); Assert.True(router.CanDeliver(target, delivery, 1100));
        Assert.Empty(Audio(menu)); Assert.Empty(Audio(far)); Assert.Empty(Audio(otherMatch)); Assert.Empty(Audio(source));
        Assert.False(router.CanDeliver(target, delivery, 1122));
        router.UpdateWorld([world[0], world[1] with { MatchId = 2 }], 1010);
        Assert.False(router.CanDeliver(target, delivery, 1011));
        router.UpdateWorld(world, 1020);
        Assert.True(router.Receive(source, VoiceWire.EncodeUpload(2, Opus()), 1021));
        delivery = Assert.Single(Audio(target));
        router.UpdateWorld([world[1]], 1022); // death / leaving world removes the talker.
        Assert.False(router.CanDeliver(target, delivery, 1023));
        Assert.True(router.Receive(source, VoiceWire.EncodeUpload(3, Opus()), 1024));
        Assert.Empty(Audio(target));
    }

    [Fact]
    public void StaleSnapshotsMenuAndAmbiguousIdentitiesFailClosed()
    {
        var router = new VoiceRouter(); var a = router.Connect("a"); var b = router.Connect("b");
        byte[] bytes = Opus();
        Assert.Throws<InvalidOperationException>(() => router.Connect("a"));
        var world = new[] { Player("a", 1), Player("b", 2) };
        router.UpdateWorld(world, 1000);
        Assert.True(router.Receive(a, VoiceWire.EncodeUpload(0, bytes), 1501)); Assert.Empty(Audio(b));
        router.UpdateWorld([world[0], world[1], world[0] with { CharacterId = 3 }], 1600);
        Assert.True(router.Receive(a, VoiceWire.EncodeUpload(1, bytes), 1601)); Assert.Empty(Audio(b));
        router.UpdateWorld([world[0], world[1] with { CharacterId = 1 }], 1700);
        Assert.True(router.Receive(a, VoiceWire.EncodeUpload(2, bytes), 1701)); Assert.Empty(Audio(b));
        router.UpdateWorld([], 1800);
        Assert.True(router.Receive(a, VoiceWire.EncodeUpload(3, bytes), 1801)); Assert.Empty(Audio(b));
    }

    [Fact]
    public void MutingStopsBothDirectionsAndInvalidReplayFloodsAreBounded()
    {
        var router = new VoiceRouter(); var a = router.Connect("a"); var b = router.Connect("b");
        router.UpdateWorld([Player("a", 1), Player("b", 2)], 1000);
        byte[] bytes = Opus();
        Assert.True(router.Receive(a, VoiceWire.EncodeUpload(uint.MaxValue, bytes), 1000));
        Assert.True(router.Receive(a, VoiceWire.EncodeUpload(0, bytes), 1020));
        Assert.False(router.Receive(a, VoiceWire.EncodeUpload(0, bytes), 1021));
        Assert.False(router.Receive(a, new byte[1000], 1021));
        Assert.True(router.Receive(b, VoiceWire.EncodeDeafen(true), 1022));
        foreach (var delivery in Audio(b)) Assert.False(router.CanDeliver(b, delivery, 1023));
        Assert.True(router.Receive(a, VoiceWire.EncodeUpload(1, bytes), 1040)); Assert.Empty(Audio(b));
        Assert.True(router.Receive(b, VoiceWire.EncodeUpload(0, bytes), 1040)); Assert.Empty(Audio(a));
        Assert.True(router.Receive(b, VoiceWire.EncodeDeafen(false), 1050));
        int admitted = 0;
        for (uint i = 2; i < 102; i++) if (router.Receive(a, VoiceWire.EncodeUpload(i, bytes), 1060)) admitted++;
        Assert.InRange(admitted, 1, 10);
        router.Disconnect(a); Assert.False(router.Receive(a, VoiceWire.EncodeUpload(102, bytes), 1080));
        router.Close(); Assert.Equal(0, router.ConnectionCount);
    }

    [Fact]
    public void CrowdCapsNearestTalkersAndBoundsSlowListenersWithoutBlockingHealthyOnes()
    {
        var router = new VoiceRouter(maxSpeakers: 2);
        var target = router.Connect("target"); var slow = router.Connect("slow");
        VoicePeer[] talkers = Enumerable.Range(1, 150).Select(i => router.Connect("p" + i)).ToArray();
        var world = new[] { Player("target", 1000), Player("slow", 1001) }
            .Concat(Enumerable.Range(1, 150).Select(i => Player("p" + i, (ulong)i, i / 10f))).ToArray();
        byte[] bytes = Opus();
        long received = 0;
        for (uint frame = 0; frame < 100; frame++)
        {
            long now = 1000 + frame * 20;
            router.UpdateWorld(world, now);
            // Farthest first exercises replacement by nearer simultaneous speakers.
            for (int i = 149; i >= 0; i--) router.Receive(talkers[i], VoiceWire.EncodeUpload(frame, bytes), now);
            var deliveries = Audio(target).Where(d => router.CanDeliver(target, d, now)).ToArray();
            Assert.Equal(new ulong[] { 1, 2 }, deliveries.Select(d => d.Speaker).Order().ToArray());
            received += deliveries.Length;
        }
        Assert.Equal(200, received);
        Assert.True(slow.DroppedFrames > 0); Assert.InRange(slow.Outgoing.Count, 0, 64);
    }

    [Fact]
    public void MixerRendersStereoRejectsReplayClearsStaleSpeechAndNeverClips()
    {
        using var mixer = new VoiceMixer(); using var encoder = new VoiceEncoder();
        for (uint i = 0; i < 2; i++) Assert.True(mixer.Receive(VoiceWire.EncodeAudio(1, i, i * 20, 0, 1, encoder.Encode(Tone((int)i))), 1000 + i * 20));
        Assert.False(mixer.Receive(VoiceWire.EncodeAudio(1, 1, 20, 0, 1, Opus()), 1021));
        float[] pcm = new float[1280]; mixer.Read(pcm, 1040);
        Assert.All(pcm.Where((_, i) => i % 2 == 0), sample => Assert.Equal(0, sample));
        Assert.True(pcm.Where((_, i) => i % 2 == 1).Sum(v => v * v) > 1);
        Assert.False(mixer.Receive(VoiceWire.EncodeAudio(1, 2, 40, 1, 1, Opus()), 1400));
        mixer.Read(pcm, 1400); Assert.All(pcm, v => Assert.Equal(0, v));
        mixer.Clear(); Assert.Equal(0, mixer.BufferedSpeakers);
        for (ulong id = 1; id <= 150; id++) mixer.Receive(VoiceWire.EncodeAudio(id, 0, 1, 1, 1, Opus()), 1000);
        mixer.Read(pcm, 1040); Assert.All(pcm, v => Assert.InRange(v, -1, 1));
        mixer.Read(pcm, 2100); Assert.Equal(0, mixer.BufferedSpeakers);
    }

    [Fact]
    public void MalformedCodecPacketsCannotCrashPlaybackOrForgeWireFields()
    {
        using var mixer = new VoiceMixer(); var random = new Random(1127);
        using var decoder = new VoiceDecoder(); short[] pcm = new short[320];
        for (int i = 0; i < 500; i++)
        {
            byte[] bytes = new byte[random.Next(1, 257)]; random.NextBytes(bytes);
            decoder.TryDecode(bytes, pcm);
            Assert.False(VoiceWire.TryAudio(bytes, out _));
        }
        Assert.Throws<ArgumentException>(() => VoiceWire.EncodeAudio(1, 0, 0, float.NaN, 1, Opus()));
        Assert.Throws<ArgumentException>(() => VoiceWire.EncodeAudio(0, 0, 0, 1, 1, Opus()));
    }

    [Theory]
    [InlineData("Mouse_2", 4)] [InlineData("KP_4", 100)] [InlineData("V", 86)] [InlineData("F12", 123)]
    public void PhysicalBindingsMatchClientKeyNames(string name, int key) => Assert.Equal(new[] { key }, VoiceBindings.Parse(name));

    [Fact]
    public void UnknownModifiersCannotAccidentallyEnableCapture()
    {
        Assert.Empty(VoiceBindings.Parse("Unrecognised+V"));
        Assert.False(VoiceBindings.Pressed([VoiceBindings.Parse("Alt+V")], key => key == 86));
        Assert.True(VoiceBindings.Pressed([VoiceBindings.Parse("Alt+V")], key => key is 18 or 86));
        Assert.False(VoiceBindings.Pressed([[]], _ => true));
    }

    [Fact]
    public void PartialUserProfileInheritsMiddleMouseButAnExplicitUnbindIsRespected()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "<Profile><Action name='Jump'><Trigger>Space</Trigger></Action></Profile>");
            Assert.True(VoiceBindings.Pressed(VoiceBindings.Load(file).Talk, key => key == 4));
            File.WriteAllText(file, "<Profile><Action name='VoiceChatProximity'/></Profile>");
            Assert.Empty(VoiceBindings.Load(file).Talk);
            Assert.True(VoiceBindings.Pressed(VoiceBindings.Load(file, "Mouse_2").Talk, key => key == 4));
        }
        finally { File.Delete(file); }
    }
}
