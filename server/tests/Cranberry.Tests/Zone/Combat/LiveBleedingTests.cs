using System.Collections.Concurrent;
using System.Diagnostics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    private static void PumpWound(ConcurrentQueue<Action> pending, Func<bool> done, int limit = 2000)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < limit)
        {
            if (pending.TryDequeue(out var action)) action(); else Thread.Sleep(1);
        }
        Assert.True(done());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulletAndBrokenHelmetWoundsSendVictimFeedbackBleedAndStopAfterBandaging(bool helmet)
    {
        using var f = new Fixture();
        var pending = new ConcurrentQueue<Action>(); f.Service.Post = pending.Enqueue;
        var shooter = f.Add(1, new(0, 0, 0));
        AddFeedbackVictim(f, shooter, practice: false, helmetItem: helmet ? 2168u : 0u);
        var victim = f.Connections.Single(c => Get<ulong>(c.Tag!, "Guid") == 2);
        f.Add(3, new(8, 0, 0)); // Keep the round live.
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;
        f.Shot(shooter, victim, 1, helmet ? "HEAD" : "SPINE");
        uint afterShot = f.Health(victim);
        Assert.Equal(helmet ? 10000u : 7500u, afterShot);
        var incoming = Assert.Single(f.Recorder.Routed.Skip(mark), r => r.Packet.Length > 3
            && r.Packet[1] == 0x11 && r.Packet[2] == 0x1e);
        Assert.Same(victim, incoming.Connection);
        Assert.Equal(34, incoming.Packet.Length);
        Assert.Equal(5106u, Get<uint>(victim.Tag!, "BleedEffect"));
        PumpWound(pending, () => f.Health(victim) < afterShot);
        Assert.Equal(afterShot - 50, f.Health(victim));
        Call(f.Service, "BeginHeal", victim, victim.Tag, MedicalModel.Items[24]);
        Assert.Equal(0u, Get<uint>(victim.Tag!, "BleedEffect"));
        uint beforeHeal = f.Health(victim);
        PumpWound(pending, () => f.Health(victim) > beforeHeal);
        Assert.Equal(Math.Min(10000u, beforeHeal + 100), f.Health(victim));
    }

    [Fact]
    public void ResettingVitalsInvalidatesTheOldWoundTimer()
    {
        using var f = new Fixture();
        var pending = new ConcurrentQueue<Action>(); f.Service.Post = pending.Enqueue;
        var shooter = f.Add(1, new(0, 0, 0)); var victim = f.Add(2, new(5, 0, 0));
        f.SeeEveryone(); f.Shot(shooter, victim, 1);
        Call(f.Service, "ResetPlayerVitals", victim, victim.Tag);
        var clock = Stopwatch.StartNew();
        PumpWound(pending, () => clock.ElapsedMilliseconds > 1200);
        Assert.Equal(10000u, f.Health(victim));
        Assert.Equal(0u, Get<uint>(victim.Tag!, "BleedEffect"));
    }
}
