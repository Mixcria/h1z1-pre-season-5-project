using System.Text.Json;
using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class CrateRewardSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cranberry-crate-draw-tests", Guid.NewGuid().ToString("N"));
    private AccountEconomyStore Store() => new(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private void Seed(uint itemId, uint count) => Store().GetOrCreate("account", new(Items:
        [new(10, itemId, 0, count, "test", Scrappable: false)]));

    private static EconomyAwardReceipt Award(AccountEconomyResult result)
    {
        Assert.True(result.Succeeded, result.Error);
        return JsonSerializer.Deserialize<EconomyAwardReceipt>(result.Receipt!.ResultJson!)!;
    }

    [Theory]
    [InlineData(3502u, false)]
    [InlineData(3502u, true)]
    [InlineData(3630u, false)]
    [InlineData(3630u, true)]
    public void EveryCopyInOpenAllGetsItsOwnDrawAndRepeatedWinnersRemainSeparateOwnedCopies(uint crateId, bool repeat)
    {
        Seed(crateId, 2);
        var crate = EconomyCatalog.Default.Crates[crateId];
        int total = checked((int)crate.Rewards.Sum(reward => (long)reward.Weight));
        var tickets = new Queue<int>([0, repeat ? 0 : checked((int)crate.Rewards[0].Weight)]);
        var result = new AccountEconomyOperations(Store()).OpenCrates("account", "open-all", [new(crateId, 2)], max =>
        {
            Assert.Equal(total, max);
            return tickets.Dequeue();
        });
        var award = Award(result);
        Assert.Empty(tickets);
        Assert.Equal(new[] { crate.Rewards[0].AccountItemId, crate.Rewards[repeat ? 0 : 1].AccountItemId },
            award.Granted.Select(item => item.AccountItemId));
        Assert.Equal(2, award.Granted.Select(item => item.InstanceId).Distinct().Count());
        Assert.All(award.Granted, item => Assert.Equal(1u, item.Count));
        Assert.False(result.Snapshot!.Owns(crateId));

        var replay = new AccountEconomyOperations(Store()).OpenCrates("account", "open-all", [new(crateId, 2)],
            _ => throw new InvalidOperationException("A committed opening must not draw again."));
        Assert.True(replay.Replayed);
        Assert.Equal(result.Receipt, replay.Receipt);
        Assert.Equal(result.Snapshot.Items, replay.Snapshot!.Items);
        Assert.Equal(result.Snapshot.Revision, replay.Snapshot.Revision);
    }

    [Fact]
    public void NewOpeningsOfTheSameCrateDrawAgainAfterRestartAndReplayingAnOlderOpeningPreservesEveryAward()
    {
        const uint crateId = 3630;
        Seed(crateId, 3);
        var crate = EconomyCatalog.Default.Crates[crateId];
        int draws = 0;
        AccountEconomyResult Open(string operation, int ticket) =>
            new AccountEconomyOperations(Store()).OpenCrates("account", operation, [new(crateId)], _ =>
            {
                draws++;
                return ticket;
            });

        var first = Open("first", 0);
        var second = Open("second", checked((int)crate.Rewards[0].Weight));
        var third = Open("third", 0);
        Assert.Equal(crate.Rewards[0].AccountItemId, Assert.Single(Award(first).Granted).AccountItemId);
        Assert.Equal(crate.Rewards[1].AccountItemId, Assert.Single(Award(second).Granted).AccountItemId);
        Assert.Equal(crate.Rewards[0].AccountItemId, Assert.Single(Award(third).Granted).AccountItemId);
        Assert.Equal(3, draws);
        Assert.Equal(3, third.Snapshot!.Items.Count);
        Assert.False(third.Snapshot.Owns(crateId));

        var replay = new AccountEconomyOperations(Store()).OpenCrates("account", "first", [new(crateId)],
            _ => throw new InvalidOperationException("An earlier result must not replace later committed draws."));
        Assert.True(replay.Replayed);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(third.Snapshot.Revision, replay.Snapshot!.Revision);
        Assert.Equal(third.Snapshot.Items, replay.Snapshot.Items);
    }
}
