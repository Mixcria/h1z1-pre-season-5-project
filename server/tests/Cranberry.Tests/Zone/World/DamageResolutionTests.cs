using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/22 §6.3: exactly one resolver writes health and exactly one place kills. Without that, gas
// and a bullet landing on the same tick kill twice - two ce 04, two decrements, two hand-offs.
public sealed class DamageResolutionTests
{
    private const byte GameModeOpcode = GameModeHud.Opcode;              // 0xce
    private const ushort DeathInfoSub = 0x0004;
    private const ushort PlayersRemainingSub = 0x0009;
    private const byte ClientUpdateOpcode = ZoneOpcodes.ClientUpdateBase; // 0x11
    private const ushort HitpointsSub = 0x0001;
    private const ushort DamageInfoSub = 0x001e;

    [Fact]
    public void QueuedDamageDoesNothingUntilTheTickResolvesIt()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.Match.Damage(player, 100, DamageCause.Bullet);

        Assert.Equal(harness.Settings.StartingHealth, player.Health);
        Assert.Equal(1, harness.Match.PendingDamageCount);

        harness.Step();

        Assert.Equal(harness.Settings.StartingHealth - 100, player.Health);
        Assert.Equal(0, harness.Match.PendingDamageCount);
    }

    [Fact]
    public void EachResolvedHitUnicastsHitpoints()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.Match.Damage(player, 100, DamageCause.Bullet);
        harness.Match.Damage(player, 250, DamageCause.ToxicGas);
        harness.Step();

        RecordingPlayerSink sink = MatchHarness.SinkOf(player);
        Assert.Equal(2, sink.CountOf(ClientUpdateOpcode, HitpointsSub));
        Assert.Equal(harness.Settings.StartingHealth - 350, player.Health);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(5000)]
    public void DamageAndHealingPublishCurrentHudResourcesWithThePreviousAuthoritativeValue(int maximum)
    {
        var harness = new MatchHarness(MatchSettings.Default with { StartingHealth = maximum });
        MatchPlayer player = harness.AddPlayer();
        MatchPlayer other = harness.AddPlayer("Other");

        harness.Match.Damage(player, maximum / 5, DamageCause.Bullet);
        harness.Step();
        harness.Match.Heal(player, maximum / 10);
        harness.Step();

        var sink = MatchHarness.SinkOf(player);
        var resources = sink.Packets.Where(p => p.Opcode == CharacterResourceUpdate.Opcode).ToArray();
        Assert.Equal(2, resources.Length);
        AssertHealthResource(resources[0], player.Id.Value, current: 8000, previous: 10000);
        AssertHealthResource(resources[1], player.Id.Value, current: 9000, previous: 8000);
        Assert.Equal(maximum * 9 / 10, player.Health);
        Assert.Equal(2, sink.CountOf(ClientUpdateOpcode, HitpointsSub));
        Assert.DoesNotContain(MatchHarness.SinkOf(other).Packets, p => p.Opcode == CharacterResourceUpdate.Opcode);
        foreach (var resource in resources)
        {
            int index = sink.Packets.IndexOf(resource);
            Assert.True(index > 0 && sink.Packets[index - 1].Is(ClientUpdateOpcode, HitpointsSub));
        }
    }

    [Fact]
    public void SameTickLethalDamagePublishesZeroResourceBeforeOneDeathAndIgnoresLaterHeal()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.AddPlayer("Survivor");
        harness.Match.Damage(player, 9000, DamageCause.ToxicGas);
        harness.Match.Damage(player, 2000, DamageCause.Bullet);
        harness.Match.Heal(player, 5000);

        harness.Step();

        var sink = MatchHarness.SinkOf(player);
        var resources = sink.Packets.Where(p => p.Opcode == CharacterResourceUpdate.Opcode).ToArray();
        Assert.Equal(2, resources.Length);
        AssertHealthResource(resources[0], player.Id.Value, current: 1000, previous: 10000);
        AssertHealthResource(resources[1], player.Id.Value, current: 0, previous: 1000);
        Assert.Equal(0, player.Health);
        Assert.Equal(1, sink.CountOf(GameModeOpcode, DeathInfoSub));
        int deathIndex = sink.Packets.FindIndex(p => p.Is(GameModeOpcode, DeathInfoSub));
        Assert.True(deathIndex > sink.Packets.IndexOf(resources[1]));
    }

    private static void AssertHealthResource(RecordedPacket packet, ulong subject, uint current, uint previous)
    {
        Assert.Equal(CharacterResourceUpdate.WireLength, packet.Body.Length);
        Assert.Equal(0, packet.Channel);
        Assert.Equal(CharacterResourceUpdate.EventType, packet.Body[5]);
        Assert.Equal(subject, BitConverter.ToUInt64(packet.Body, 6));
        Assert.Equal(1u, BitConverter.ToUInt32(packet.Body, 14));
        Assert.Equal(1u, BitConverter.ToUInt32(packet.Body, 18));
        Assert.Equal(current, BitConverter.ToUInt32(packet.Body, 22));
        Assert.Equal(previous, BitConverter.ToUInt32(packet.Body, 26));
    }

    [Fact]
    public void GasAndABulletOnOneTickKillExactlyOnce()
    {
        var harness = new MatchHarness();
        MatchPlayer victim = harness.AddPlayer("Victim");
        MatchPlayer survivor = harness.AddPlayer("Survivor");

        int lethal = harness.Settings.StartingHealth;
        harness.Match.Damage(victim, lethal, DamageCause.ToxicGas);
        harness.Match.Damage(victim, lethal, DamageCause.Bullet);
        harness.Step();

        Assert.Equal(LifeState.Dead, victim.Life);
        Assert.Equal(0, victim.Health);
        Assert.Equal(1, harness.Match.Deaths);
        Assert.Equal(1, MatchHarness.SinkOf(victim).CountOf(GameModeOpcode, DeathInfoSub));
        Assert.Equal(1, harness.Match.World.AliveCount);
        Assert.Equal(LifeState.Alive, survivor.Life);

        // One death, one players-remaining broadcast - not two.
        Assert.Equal(1, MatchHarness.SinkOf(survivor).CountOf(GameModeOpcode, PlayersRemainingSub));
    }

    [Fact]
    public void HealthNeverGoesBelowZero()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.Match.Damage(player, harness.Settings.StartingHealth * 10, DamageCause.Falling);
        harness.Step();

        Assert.Equal(0, player.Health);
    }

    [Fact]
    public void DamageAgainstAnAlreadyDeadPlayerIsDropped()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.Match.Damage(player, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.Step();
        MatchHarness.SinkOf(player).Clear();

        harness.Match.Damage(player, 500, DamageCause.Bullet);
        harness.Step();

        Assert.Equal(1, harness.Match.Deaths);
        Assert.Empty(MatchHarness.SinkOf(player).Packets);
    }

    [Fact]
    public void ZeroAndNegativeDamageAreNeverQueued()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.Match.Damage(player, 0, DamageCause.Bullet);
        harness.Match.Damage(player, -100, DamageCause.Bullet);

        Assert.Equal(0, harness.Match.PendingDamageCount);
    }

    [Fact]
    public void AKillIsCreditedToItsAttacker()
    {
        var harness = new MatchHarness();
        MatchPlayer victim = harness.AddPlayer("Victim");
        MatchPlayer killer = harness.AddPlayer("Killer");

        harness.Match.Damage(victim, harness.Settings.StartingHealth, DamageCause.Bullet, killer.Id);
        harness.Step();

        Assert.Equal(1u, killer.Kills);
        Assert.Equal(0u, victim.Kills);
    }

    [Fact]
    public void PlacementIsOneBasedAndCountsDownAsPlayersDie()
    {
        // Placement is the human placement: the first of three to die placed 3rd. The wire field is
        // one lower - see TheDeathPacketsRankIndexIsZeroBased.
        var harness = new MatchHarness();
        MatchPlayer first = harness.AddPlayer("First");
        MatchPlayer second = harness.AddPlayer("Second");
        harness.AddPlayer("Third");

        harness.Match.Damage(first, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.Step();
        Assert.Equal(3, first.Placement);

        harness.Match.Damage(second, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.Step();
        Assert.Equal(2, second.Placement);
    }

    [Fact]
    public void TheDeathPacketsRankIndexIsZeroBased()
    {
        // docs/18 §3a: FUN_140bbb120 displays rankIndex + 1 everywhere (DAT_143f6e918,
        // FUN_141449e20(obj+0xb0, rank+1), KOTK_ELIMINATION_RANK), so the wire value is one below
        // the placement. A 1-based rank shows "#4" in a three-player match, and "#2" for the last
        // player alive in the solo dev match - which is what ZoneService.SendGasDeath's
        // DeathInfo.Gas(rankIndex: 0) already gets right.
        var harness = new MatchHarness();
        MatchPlayer first = harness.AddPlayer("First");
        harness.AddPlayer("Second");
        harness.AddPlayer("Third");

        harness.Match.Damage(first, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.Step();

        RecordedPacket death = MatchHarness.SinkOf(first).With(GameModeOpcode, DeathInfoSub).Single();
        Assert.Equal(3, first.Placement);
        Assert.Equal(2u, BitConverter.ToUInt32(death.Body, 3));
    }

    [Fact]
    public void ASoloDeathRanksZeroAndNamesNoKiller()
    {
        // The attacker of a gas death resolves to the victim; that credits no kill, and it must not
        // write the victim's own name into ce 04's killer field either.
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer("Solo");

        harness.Match.Damage(player, harness.Settings.StartingHealth, DamageCause.ToxicGas, player.Id);
        harness.Step();

        RecordedPacket death = MatchHarness.SinkOf(player).With(GameModeOpcode, DeathInfoSub).Single();
        Assert.Equal(0u, BitConverter.ToUInt32(death.Body, 3));
        Assert.Equal(GasPackets.DeathInfo.BaseLength, death.Body.Length);   // empty killer name
        Assert.Equal(0u, player.Kills);
    }

    [Fact]
    public void ADisconnectedVictimStillDiesButReceivesNothing()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        MatchHarness.SinkOf(player).IsOpen = false;

        harness.Match.Damage(player, harness.Settings.StartingHealth, DamageCause.Disconnected);
        harness.Step();

        Assert.Equal(LifeState.Dead, player.Life);
        Assert.Empty(MatchHarness.SinkOf(player).Packets);
    }

    [Fact]
    public void DamageInfoIsGatedByItsOwnSetting()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();
        harness.Match.Damage(player, 100, DamageCause.ToxicGas);
        harness.Step();

        // GasSettings.SendDamageInfo defaults to false: the packet's fields are still unverified.
        Assert.Equal(0, MatchHarness.SinkOf(player).CountOf(ClientUpdateOpcode, DamageInfoSub));

        var gated = new MatchHarness(MatchSettings.Default with
        {
            Gas = new GasSettings { SendDamageInfo = true },
        });
        MatchPlayer gatedPlayer = gated.AddPlayer();
        gated.Match.Damage(gatedPlayer, 100, DamageCause.ToxicGas);
        gated.Step();

        Assert.Equal(1, MatchHarness.SinkOf(gatedPlayer).CountOf(ClientUpdateOpcode, DamageInfoSub));
    }

    [Fact]
    public void TheDeathPacketCarriesTheClientsOwnToxicGasCause()
    {
        var harness = new MatchHarness();
        MatchPlayer player = harness.AddPlayer();

        harness.Match.Damage(player, harness.Settings.StartingHealth, DamageCause.ToxicGas);
        harness.Step();

        RecordedPacket death = MatchHarness.SinkOf(player).With(GameModeOpcode, DeathInfoSub).Single();

        // ce 04: u32 rankIndex; u8 flag; str killer; u32 field4; u32 sourceId; u32 cause.
        // docs/18 §3b: the wire cause for gas is 0x42 (UI.Results.Rank.Gas).
        uint cause = BitConverter.ToUInt32(death.Body, death.Body.Length - 4);
        Assert.Equal(GasPackets.DeathCause.Gas, cause);
    }
}
