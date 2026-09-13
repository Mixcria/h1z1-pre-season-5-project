using System.Numerics;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class CombatBotStartupTests
{
    private const ulong FirstProductionGuid = 0x5b00_0000_0000_0001;
    private static readonly BotOpponent[] Opponent = [new(1, new(0, 0, 25))];

    private static CombatBot Create(ulong guid, string difficulty = "normal", long spawnedAt = 0,
        Vector3? position = null) => new(new PracticeTarget(guid, 4_000_000,
            position ?? Vector3.Zero, new(0, 0, 0, 1), 10_000) { IsCombatBot = true }, difficulty, spawnedAt);

    private static BotShot? Tick(CombatBot bot, long at, IReadOnlyList<BotOpponent>? opponents = null,
        bool frozen = false) => bot.Tick(at, opponents ?? Opponent, (_, _) => 0, frozen);

    private static (long At, bool Hit) FirstShot(CombatBot bot, long lastTick = 0,
        IReadOnlyList<BotOpponent>? opponents = null)
    {
        for (long at = lastTick + CombatBot.TickMs; at <= lastTick + 10_000; at += CombatBot.TickMs)
            if (Tick(bot, at, opponents) is { } shot) return (at, shot.Hit);
        Assert.Fail("An active bot with an unobstructed nearby opponent never fired.");
        return default;
    }

    [Theory]
    [InlineData("easy")]
    [InlineData("normal")]
    [InlineData("hard")]
    public void SpawnPreparationCannotBeBypassedByMovingOrChangingTargets(string difficulty)
    {
        const long spawnedAt = 12_300;
        var bot = Create(FirstProductionGuid, difficulty, spawnedAt);
        long lastTick = spawnedAt;
        for (long elapsed = CombatBot.TickMs; elapsed < CombatBot.SpawnGraceMs; elapsed += CombatBot.TickMs)
        {
            // Change targets immediately before preparation ends as well as during movement.
            ulong target = elapsed >= CombatBot.SpawnGraceMs - CombatBot.TickMs ? 3UL
                : elapsed >= 2_000 ? 2UL : 1UL;
            BotOpponent[] moving = [new(target, new(MathF.Sin(elapsed / 600f) * 2, 0, 25))];
            lastTick = spawnedAt + elapsed;
            Assert.Null(Tick(bot, lastTick, moving));
            Assert.Equal(30, bot.Magazine);
        }

        Assert.Equal(0, bot.ShotsFired);
        Assert.Equal(3UL, bot.Opponent);
        Assert.NotEqual(Vector3.Zero, bot.Body.Position);
        var first = FirstShot(bot, lastTick, [new(3, new(0, 0, 25))]);
        Assert.True(first.At >= spawnedAt + CombatBot.SpawnGraceMs);
    }

    [Theory]
    [InlineData("easy", 1400)]
    [InlineData("normal", 900)]
    [InlineData("hard", 450)]
    public void TargetAppearingAfterPreparationStillGetsAReactionDelay(string difficulty, int reactionMs)
    {
        var bot = Create(FirstProductionGuid, difficulty);
        long appearsAt = CombatBot.SpawnGraceMs + 2_000;
        for (long at = CombatBot.TickMs; at < appearsAt; at += CombatBot.TickMs)
            Assert.Null(Tick(bot, at, []));

        long lastTick = appearsAt;
        for (long elapsed = 0; elapsed < reactionMs; elapsed += CombatBot.TickMs)
        {
            lastTick = appearsAt + elapsed;
            Assert.Null(Tick(bot, lastTick));
        }
        Assert.True(FirstShot(bot, lastTick).At >= appearsAt + reactionMs);
    }

    [Fact]
    public void FirstFiveProductionIdsDoNotRepeatTheSynchronizedAllHitOpeningVolley()
    {
        BotOpponent[] player = [new(1, Vector3.Zero)];
        var opening = Enumerable.Range(0, 5).Select(index => FirstShot(
            Create(FirstProductionGuid + (ulong)index, position: new((index - 3.5f) * 3, 0, 25)),
            opponents: player)).ToArray();

        Assert.All(opening, shot => Assert.True(shot.At >= CombatBot.SpawnGraceMs));
        Assert.True(opening.Select(shot => shot.At).Distinct().Count() > 1,
            "New bots must not all finish acquisition and fire in the same world tick.");
        Assert.Contains(opening, shot => shot.Hit);
        Assert.Contains(opening, shot => !shot.Hit);
    }

    [Theory]
    [InlineData("easy", 0.10, 0.28)]
    [InlineData("normal", 0.25, 0.45)]
    [InlineData("hard", 0.55, 0.75)]
    public void OpeningShotAccuracyMatchesDifficultyAcrossSequentialProductionIds(
        string difficulty, double minimum, double maximum)
    {
        // Check each bot's FIRST shot: a long run can hide badly biased startup seeds.
        const int sample = 512;
        int hits = Enumerable.Range(0, sample).Count(index =>
            FirstShot(Create(FirstProductionGuid + (ulong)index, difficulty)).Hit);
        Assert.InRange(hits / (double)sample, minimum, maximum);
    }

    [Fact]
    public void AdjacentIdsAndHighGuidBitsProduceDifferentButReproducibleShotSequences()
    {
        ulong even = FirstProductionGuid + 1;
        var original = ShotTrace(even);
        Assert.Equal(original, ShotTrace(even));
        Assert.False(original.Select(shot => shot.Hit).SequenceEqual(
            ShotTrace(even + 1).Select(shot => shot.Hit)), "Even and odd bot IDs must not share a hit sequence.");
        Assert.False(original.Select(shot => shot.Hit).SequenceEqual(
            ShotTrace(even + (1UL << 32)).Select(shot => shot.Hit)), "The upper GUID bits must affect the hit sequence.");
    }

    private static (long At, bool Hit)[] ShotTrace(ulong guid)
    {
        var bot = Create(guid);
        var shots = new List<(long At, bool Hit)>();
        for (long at = CombatBot.TickMs; at <= 60_000 && shots.Count < 30; at += CombatBot.TickMs)
            if (Tick(bot, at) is { } shot) shots.Add((at, shot.Hit));
        Assert.Equal(30, shots.Count);
        return shots.ToArray();
    }

    [Theory]
    [InlineData("easy")]
    [InlineData("normal")]
    [InlineData("hard")]
    public void RetargetingAndBrieflyFreezingCannotShortenAnEmptyMagazineReload(string difficulty)
    {
        var bot = Create(FirstProductionGuid, difficulty);
        long emptiedAt = 0;
        for (long at = CombatBot.TickMs; at <= 60_000; at += CombatBot.TickMs)
        {
            Tick(bot, at);
            if (!bot.Reloading) continue;
            emptiedAt = at;
            break;
        }
        Assert.True(emptiedAt > 0, "The bot must empty its first magazine during the scenario.");
        Assert.Equal(30, bot.ShotsFired);
        Assert.Equal(0, bot.Magazine);
        BotOpponent[] replacement = [new(2, new(0, 0, 25))];
        for (long elapsed = CombatBot.TickMs; elapsed < 2_500; elapsed += CombatBot.TickMs)
        {
            Assert.Null(Tick(bot, emptiedAt + elapsed, replacement, frozen: elapsed == 200));
            Assert.Equal(0, bot.Magazine);
            Assert.True(bot.Reloading);
        }
        Assert.NotNull(Tick(bot, emptiedAt + 2_500, replacement));
        Assert.False(bot.Reloading);
        Assert.Equal(29, bot.Magazine);
        Assert.Equal(31, bot.ShotsFired);
    }
}
