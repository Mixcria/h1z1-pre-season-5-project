using Cranberry.Launcher.Core.Voice;

namespace Cranberry.Launcher.Tests;

public sealed class VoiceHudTests
{
    private static VoiceParticipant Player(string account, ulong id, float x = 0, ulong match = 1) => new(account, id, match, new(x, 0, 0), 0);

    [Fact]
    public void LocalSpeechShowsWithoutLoopbackAndOnlyAudibleNeighboursSeeIt()
    {
        var router = new VoiceRouter();
        var a = router.Connect("a"); router.Connect("b"); router.Connect("far"); router.Connect("other");
        router.UpdateWorld([Player("a", 1), Player("b", 2, 5), Player("far", 3, 100), Player("other", 4, 2, 2)], 1000);
        Assert.All(router.HudViews(1000), view => Assert.Empty(view.Speakers));
        Assert.True(router.Receive(a, VoiceWire.EncodeUpload(0, VoiceTests.Opus()), 1020));
        var views = router.HudViews(1021).ToDictionary(v => v.AccountId);
        Assert.Equal(new ulong[] { 1 }, views["a"].Speakers);
        Assert.Equal(new ulong[] { 1 }, views["b"].Speakers);
        Assert.Empty(views["far"].Speakers); Assert.Empty(views["other"].Speakers);
        while (a.Outgoing.TryRead(out var frame)) Assert.NotNull(frame.Control); // No feedback loop.
        Assert.All(router.HudViews(1321), view => Assert.Empty(view.Speakers));
        Assert.Empty(router.HudViews(1501)); // An expired world snapshot cannot expose names.
    }

    [Fact]
    public void MuteDeathMovementMatchChangeAndDisconnectClearSpeakerActivity()
    {
        var router = new VoiceRouter(); var a = router.Connect("a"); var b = router.Connect("b");
        var alice = Player("a", 1); var bob = Player("b", 2, 5);
        router.UpdateWorld([alice, bob], 1000);
        router.Receive(a, VoiceWire.EncodeUpload(0, VoiceTests.Opus()), 1020);
        router.Receive(a, VoiceWire.EncodeDeafen(true), 1030);
        Assert.All(router.HudViews(1031), view => Assert.Empty(view.Speakers));
        router.Receive(a, VoiceWire.EncodeDeafen(false), 1040);
        Assert.All(router.HudViews(1041), view => Assert.Empty(view.Speakers));
        router.Receive(a, VoiceWire.EncodeUpload(1, VoiceTests.Opus()), 1050);
        router.UpdateWorld([alice, bob with { Position = new(100, 0, 0) }], 1060);
        Assert.Empty(router.HudViews(1061).Single(v => v.AccountId == "b").Speakers);
        router.UpdateWorld([alice with { MatchId = 2 }, bob], 1070);
        Assert.All(router.HudViews(1071), view => Assert.Empty(view.Speakers));
        router.UpdateWorld([alice, bob], 1080);
        router.Receive(a, VoiceWire.EncodeUpload(2, VoiceTests.Opus()), 1100);
        router.UpdateWorld([bob], 1110); // Death/menu removes the authoritative voice projection.
        Assert.All(router.HudViews(1111), view => Assert.Empty(view.Speakers));
        router.UpdateWorld([alice, bob], 1120);
        router.Receive(a, VoiceWire.EncodeUpload(3, VoiceTests.Opus()), 1140);
        router.Disconnect(a);
        Assert.All(router.HudViews(1141), view => Assert.Empty(view.Speakers));
    }

    [Fact]
    public void TenNativeRowsPrioritizeSelfAndTheClosestAdmittedTalkers()
    {
        var router = new VoiceRouter(maxSpeakers: 16);
        var peers = Enumerable.Range(1, 16).Select(i => router.Connect("p" + i)).ToArray();
        router.UpdateWorld(Enumerable.Range(1, 16).Select(i => Player("p" + i, (ulong)i, i)).ToArray(), 1000);
        var opus = VoiceTests.Opus();
        for (int i = 15; i >= 0; i--) router.Receive(peers[i], VoiceWire.EncodeUpload(0, opus), 1020);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (ulong)i), router.HudViews(1021).Single(v => v.AccountId == "p1").Speakers);
        router.UpdateWorld([Player("p1", 1), Player("p1", 2)], 1030);
        Assert.Empty(router.HudViews(1031)); // Ambiguous accounts remain excluded.
    }
}
