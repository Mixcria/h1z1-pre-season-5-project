using System.Collections;
using System.Numerics;
using System.Reflection;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    private static ConsoleReply Bots(Fixture f, SoeConnection caller, string verb, int count = 1) =>
        (ConsoleReply)Call(f.Service, "ConsoleBots", caller, caller.Tag, verb, count, "normal", 25f)!;

    private static object BotWorldFor(Fixture f, PracticeTarget bot) =>
        ((IDictionary)typeof(ZoneService).GetField("_botWorlds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(f.Service)!).Values.Cast<object>()
            .Single(w => Get<List<CombatBot>>(w, "Bots").Any(b => ReferenceEquals(b.Body, bot)));

    private static Vector3 BotTestOrigin => new(400, AirdropTerrain.LoadDefault().HeightAt(400, 400), 400);

    [Fact]
    public void MultiplePlayersSeeAndShootOneSharedBotWhileOtherMatchesSeeNothing()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { }; // Drive the world owner synchronously below; no background mutations.
        var a = f.Add(1, BotTestOrigin);
        var b = f.Add(2, BotTestOrigin + Vector3.UnitX * 2);
        var outsider = f.Add(3, BotTestOrigin, matchId: 2);
        f.Recorder.Routed.Clear();
        Assert.True(Bots(f, a, "spawn", 3).Ok);
        var targetsA = Get<SessionCombat>(a.Tag!, "Combat").Targets.All;
        var targetsB = Get<SessionCombat>(b.Tag!, "Combat").Targets.All;
        Assert.Equal(3, targetsA.Count);
        Assert.Equal(3, targetsB.Count);
        Assert.All(targetsA, t => Assert.Same(t, targetsB.Single(other => other.WorldGuid == t.WorldGuid)));
        Assert.Empty(Get<SessionCombat>(outsider.Tag!, "Combat").Targets.All);
        Assert.NotEmpty(PracticeTargetKit.Weapon.FireGroups);
        Assert.True(PracticeTargetKit.Weapon.FireGroups[0].Modes[0].Charge > 0);
        foreach (var player in new[] { a, b })
            Assert.Equal(3, f.Recorder.Routed.Count(p => p.Connection == player && p.Packet.Length > 1 && p.Packet[1] == 0xd5));
        Assert.DoesNotContain(f.Recorder.Routed, p => p.Connection == outsider && p.Packet.Length > 1 && p.Packet[1] == 0xd5);
        var victim = targetsA[0];
        var world = BotWorldFor(f, victim);
        var moving = Get<List<CombatBot>>(world, "Bots")[0];
        moving.Body.Move(moving.Body.Position + new Vector3(2, 0, 0), 0.4f);
        Call(f.Service, "PumpBots", world);
        a.FlushLatest(victim.WorldGuid); b.FlushLatest(victim.WorldGuid);
        var poseA = f.Recorder.Routed.Last(p => p.Connection == a && p.Packet.Length > 1 && p.Packet[1] == 0x78).Packet;
        var poseB = f.Recorder.Routed.Last(p => p.Connection == b && p.Packet.Length > 1 && p.Packet[1] == 0x78).Packet;
        Assert.Equal(poseA, poseB);
        void Headshot(SoeConnection shooter, uint projectile)
        {
            f.Weapon(shooter, ShootingPacketBuilder.Fire(0x3100000000000001, 0, 0, 0, [projectile]), projectile * 1000);
            f.Weapon(shooter, ShootingPacketBuilder.HitReport(projectile, victim.WorldGuid, "HEAD"), projectile * 1000);
        }
        Headshot(a, 1);
        Assert.False(targetsB[0].Armour.HelmetIntact);
        f.Recorder.Routed.Clear();
        Headshot(b, 2);
        Assert.False(victim.IsAlive);
        Assert.True(victim.DeathPublished);
        foreach (var player in new[] { a, b })
        {
            Assert.Single(f.Recorder.Routed, p => p.Connection == player && IsRetailKillFeed(p.Packet));
            Assert.Contains(f.Recorder.Routed, p => p.Connection == player && p.Packet.Length > 3 && p.Packet[1] == 0x0f && p.Packet[2] == 0x4f);
        }
        Assert.DoesNotContain(f.Recorder.Routed, p => p.Connection == outsider && IsRetailKillFeed(p.Packet));
        int feedCount = f.Recorder.Routed.Count(p => IsRetailKillFeed(p.Packet));
        Headshot(a, 3);
        Assert.Equal(feedCount, f.Recorder.Routed.Count(p => IsRetailKillFeed(p.Packet)));
    }

    [Fact]
    public void BotsSurviveOwnerDepartureReachLateJoinersAndClearForEveryone()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var a = f.Add(1, BotTestOrigin);
        var b = f.Add(2, BotTestOrigin);
        Assert.True(Bots(f, a, "spawn", 2).Ok);
        var target = Get<SessionCombat>(b.Tag!, "Combat").Targets.All[0];
        var world = BotWorldFor(f, target);
        Assert.True(Bots(f, a, "freeze").Ok);
        a.Disconnect(); f.Service.OnDisconnected(a, DisconnectCause.ServerRequested);
        var late = f.Add(4, BotTestOrigin);
        Call(f.Service, "PumpBots", world);
        Assert.Same(target, Get<SessionCombat>(late.Tag!, "Combat").Targets.All[0]);
        Assert.True(Get<bool>(world, "Frozen"));
        Call(f.Service, "ConsolePracticeTarget", b, b.Tag, "spawn");
        Assert.Equal(3, Get<SessionCombat>(b.Tag!, "Combat").Targets.Count);
        Call(f.Service, "ConsolePracticeTarget", b, b.Tag, "clear");
        Assert.Equal(2, Get<SessionCombat>(b.Tag!, "Combat").Targets.Count);
        Assert.True(Bots(f, b, "clear").Ok);
        Assert.Empty(Get<SessionCombat>(b.Tag!, "Combat").Targets.All);
        Assert.Empty(Get<SessionCombat>(late.Tag!, "Combat").Targets.All);
        Call(f.Service, "PumpBots", world); // A queued tick from before /bots clear must stay inert.
        Assert.Empty(Get<SessionCombat>(b.Tag!, "Combat").Targets.All);
    }

    [Fact]
    public void BotFireUsesPlayerDamageAndGodModeAndCannotHitOtherMatches()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var a = f.Add(1, BotTestOrigin);
        var b = f.Add(2, BotTestOrigin);
        var outsider = f.Add(3, BotTestOrigin, matchId: 2);
        Assert.True(Bots(f, a, "spawn").Ok);
        var target = Get<SessionCombat>(b.Tag!, "Combat").Targets.All[0];
        var world = BotWorldFor(f, target);
        var bot = Get<List<CombatBot>>(world, "Bots")[0];
        uint before = f.Health(b);
        Call(f.Service, "FireCombatBot", world, bot, new BotShot(2, BotTestOrigin, true));
        Assert.Equal(before - 2500u, f.Health(b));
        before = f.Health(b);
        Get<ConsoleSession>(b.Tag!, "DevConsole").Invulnerable = true;
        Call(f.Service, "FireCombatBot", world, bot, new BotShot(2, BotTestOrigin, true));
        Assert.Equal(before, f.Health(b));
        before = f.Health(outsider);
        Call(f.Service, "FireCombatBot", world, bot, new BotShot(3, BotTestOrigin, true));
        Assert.Equal(before, f.Health(outsider));
    }

    [Fact]
    public void MatchGasPauseAndResumeReachPeersAndLateJoinersWithoutAffectingOtherMatches()
    {
        using var f = new Fixture();
        var a = f.Add(1, BotTestOrigin);
        var b = f.Add(2, BotTestOrigin);
        var outsider = f.Add(3, BotTestOrigin, matchId: 2);
        foreach (var c in new[] { a, b, outsider }) Call(f.Service, "StartGas", c, c.Tag);
        Assert.True(((ConsoleReply)Call(f.Service, "ConsoleGas", a, a.Tag, "pause")!).Ok);
        var gasA = Get<GasController>(a.Tag!, "Gas");
        var gasB = Get<GasController>(b.Tag!, "Gas");
        Assert.True(gasA.Paused); Assert.True(gasB.Paused);
        Assert.False(Get<GasController>(outsider.Tag!, "Gas").Paused);
        var late = f.Add(4, BotTestOrigin);
        Call(f.Service, "StartGas", late, late.Tag);
        var gasLate = Get<GasController>(late.Tag!, "Gas");
        Assert.True(gasLate.Paused);
        Assert.Equal(gasA.MatchClockAt(long.MaxValue), gasLate.MatchClockAt(long.MaxValue));
        Call(f.Service, "ConsoleGas", a, a.Tag, "resume");
        Assert.False(gasB.Paused); Assert.False(gasLate.Paused);
        Assert.Equal(gasA.StartMs, gasB.StartMs);
        Assert.Equal(gasA.StartMs, gasLate.StartMs);
        Call(f.Service, "ConsoleGas", b, b.Tag, "next");
        Assert.Equal(gasA.StartMs, gasB.StartMs);
        Assert.Equal(gasA.StartMs, gasLate.StartMs);
    }
}
