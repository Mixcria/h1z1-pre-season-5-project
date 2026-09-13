using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class CombatBotTests
{
    private static CombatBot Create() => new(new PracticeTarget(12, 4_000_000, Vector3.Zero,
        new(0, 0, 0, 1), 10000) { IsCombatBot = true }, "normal", 0);

    [Fact]
    public void BotChasesFiresReloadsAndCanBeFrozenOrKilled()
    {
        var bot = Create();
        BotOpponent[] players = [new(1, new(0, 0, 50))];
        bool reloaded = false;
        int rounds = 30;
        for (long time = 100; time < 90000; time += 100)
        {
            bot.Tick(time, players, (_, _) => 0, false);
            if (bot.Magazine > rounds) reloaded = true;
            rounds = bot.Magazine;
        }
        Assert.NotEqual(Vector3.Zero, bot.Body.Position);
        Assert.True(bot.ShotsFired > 30);
        Assert.InRange(bot.Hits, 1, bot.ShotsFired - 1);
        Assert.True(reloaded);
        var position = bot.Body.Position;
        int shots = bot.ShotsFired;
        Assert.Null(bot.Tick(91000, players, (_, _) => 0, true));
        Assert.Equal(position, bot.Body.Position);
        Assert.Equal(shots, bot.ShotsFired);
        bot.Body.Damage(10000, 92000);
        Assert.Null(bot.Tick(93000, players, (_, _) => 0, false));
        Assert.Equal(shots, bot.ShotsFired);
    }

    [Fact]
    public void BotDoesNotShootAcrossTerrainOrChaseOutOfRangePlayers()
    {
        var bot = Create();
        for (long time = 100; time < 10000; time += 100)
            Assert.Null(bot.Tick(time, [new(1, new(0, 0, 40))], (_, z) => z is > 1 and < 39 ? 100 : 0, false));
        Assert.Equal(0, bot.ShotsFired);
        var position = bot.Body.Position;
        Assert.Null(bot.Tick(12000, [new(1, new(5000, 0, 5000))], (_, _) => 0, false));
        Assert.Equal(position, bot.Body.Position);
    }

    [Fact]
    public void MovingPoseRoundTripsThroughTheAugustDecoderIncludingNegativeCoordinates()
    {
        var bot = Create();
        bot.Body.Move(new(-1234.56f, 30.25f, -123.45f), 0);
        bot.Tick(100, [new(1, new(-1220, 30.25f, -123))], (_, _) => 30.25f, false);
        var record = ClientMovementUpdate.Parse(BotMovement.Record(bot, 100));
        Assert.InRange(Vector3.Distance(bot.Body.Position, record.Position!.Value), 0, 0.02f);
        Assert.Equal(bot.Heading, record.Orientation);
        Assert.InRange(Math.Abs(bot.Speed - record.HorizontalSpeed!.Value), 0, 0.051f);
        Assert.Equal(0x0401u, record.Posture);
    }
}
