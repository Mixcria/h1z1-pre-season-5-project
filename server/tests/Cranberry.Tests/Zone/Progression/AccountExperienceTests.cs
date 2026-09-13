using System.Globalization;
using System.Text.Json;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Progression;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Progression;

public sealed class AccountExperienceTests : IDisposable
{
    private readonly string _testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cranberry-experience-tests"));
    private readonly string _root;
    private readonly ITestOutputHelper _output;

    public AccountExperienceTests(ITestOutputHelper output)
    {
        _root = Path.Combine(_testParent, Guid.NewGuid().ToString("N"));
        _output = output;
    }

    public void Dispose()
    {
        // Resolve and check containment before removing only this test's unique temporary directory.
        string target = Path.GetFullPath(_root);
        if (!target.StartsWith(_testParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup must remain within its temporary parent.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    [Theory]
    [InlineData(0u, 1u, 0u)]
    [InlineData(1_999u, 1u, 99u)]
    [InlineData(2_000u, 2u, 0u)]
    [InlineData(2_050u, 2u, 1u)]
    [InlineData(31_662u, 6u, 13u)]
    [InlineData(90_000u, 10u, 0u)]
    [InlineData(2_450_000u, 50u, 0u)]
    [InlineData(9_900_000u, 100u, 100u)]
    [InlineData(2_147_483_647u, 100u, 100u)]
    public void DefaultCurveResolvesBoundariesProgressAndMaximumLevel(uint total, uint level, uint percent)
    {
        Assert.Equal(new ExperienceProgress(total, level, percent), ExperienceCurve.Default.At(total));
        Assert.Equal(100u, ExperienceCurve.Default.MaximumLevel);
    }

    [Fact]
    public void EveryHigherLevelCostsMoreExperienceIncludingBeyondLevelSix()
    {
        var curve = ExperienceCurve.Default;
        uint previousCost = 0;
        for (int level = 1; level < curve.MaximumLevel; level++)
        {
            uint current = curve.Thresholds[level - 1];
            uint next = curve.Thresholds[level];
            uint cost = next - current;
            Assert.True(cost > previousCost, $"Level {level} costs {cost}; previous cost was {previousCost}.");
            Assert.Equal((uint)level, curve.At(next - 1).Level);
            Assert.Equal((uint)level + 1, curve.At(next).Level);
            Assert.Equal(0u, curve.At(current).Percent);
            previousCost = cost;
        }
    }

    [Theory]
    [InlineData(1, 2_000u)]
    [InlineData(5, 10_000u)]
    [InlineData(6, 12_000u)]
    [InlineData(10, 20_000u)]
    [InlineData(50, 100_000u)]
    [InlineData(99, 198_000u)]
    public void KillRewardStaysAtRetail100XpWhileLaterLevelCostsIncrease(int level, uint expectedCost)
    {
        var curve = ExperienceCurve.Default;
        uint start = curve.Thresholds[level - 1];
        Assert.Equal(expectedCost, curve.Thresholds[level] - start);
        var experience = new AccountExperience(null, curve, start);
        var grant = experience.AwardKill("a", "kill-1");
        Assert.Equal(100u, grant.Amount);
        Assert.Equal(start + 100u, grant.After.Total);
        Assert.Equal((uint)level, grant.After.Level);
    }

    [Fact]
    public void KillCarriesExcessExperienceIntoNextLevel()
    {
        var experience = new AccountExperience(null, ExperienceCurve.Default, seed: 1_950);

        ExperienceGrant granted = experience.AwardKill("a", "kill-1");

        Assert.Equal(new ExperienceProgress(1_950, 1, 97), granted.Before);
        Assert.Equal(new ExperienceProgress(2_050, 2, 1), granted.After);
        Assert.Equal(100u, granted.Amount);
        Assert.False(granted.Replayed);
        Assert.Equal(granted.After, experience.Read("a"));
    }

    [Fact]
    public void OneKillCanCrossSeveralLevelsAndRetainRemainingProgress()
    {
        var curve = new ExperienceCurve([0, 20, 40, 60, 200]);
        var experience = new AccountExperience(null, curve);

        ExperienceGrant granted = experience.AwardKill("a", "kill-1");

        Assert.Equal(new ExperienceProgress(0, 1, 0), granted.Before);
        Assert.Equal(new ExperienceProgress(100, 4, 28), granted.After);
        Assert.Equal(100u, granted.Amount);
    }

    [Fact]
    public void ExperienceSaturatesWithoutWrappingAtTheClientIntegerLimit()
    {
        var experience = new AccountExperience(null, ExperienceCurve.Default, seed: int.MaxValue - 50u);

        ExperienceGrant lastPartial = experience.AwardKill("a", "kill-1");
        ExperienceGrant saturated = experience.AwardKill("a", "kill-2");

        Assert.Equal(50u, lastPartial.Amount);
        Assert.Equal(new ExperienceProgress(int.MaxValue, 100, 100), lastPartial.After);
        Assert.Equal(lastPartial.After, saturated.Before);
        Assert.Equal(lastPartial.After, saturated.After);
        Assert.Equal(0u, saturated.Amount);
        Assert.Equal(saturated.After, experience.Read("a"));
    }

    [Fact]
    public void ProgressPersistsAcrossNewStoresWithoutReapplyingTheSeed()
    {
        var original = new AccountExperience(new AccountEconomyStore(_root), ExperienceCurve.Default, seed: 1_950);
        Assert.Equal(new ExperienceProgress(1_950, 1, 97), original.Read("a"));
        ExperienceGrant granted = original.AwardKill("a", "kill-1");

        var restarted = new AccountExperience(new AccountEconomyStore(_root), ExperienceCurve.Default, seed: 999_999);

        Assert.Equal(granted.After, restarted.Read("a"));
        Assert.Equal(new ExperienceProgress(2_150, 2, 3), restarted.AwardKill("a", "kill-2").After);
        Assert.Equal("2150", new AccountEconomyStore(_root).GetOrCreate("a").States[AccountExperience.StateKey]);
    }

    [Fact]
    public void FullKillHistoryCanReachLevel100AndReplayOldAndNewAwardsAfterReload()
    {
        const string accountId = "level-cap-account";
        const int previousKills = 98_999;
        const uint previousExperience = 9_899_900;
        static string Operation(int kill) => $"kill-xp:11111111111111111111111111111111:1:{kill:x16}";
        var curve = ExperienceCurve.Default;
        var file = new EconomyAccountFile
        {
            AccountId = accountId,
            Revision = previousKills + 1,
            States = new() { [AccountExperience.StateKey] = previousExperience.ToString(CultureInfo.InvariantCulture) },
        };
        file.Receipts.Add(AccountExperience.StateKey, new(AccountExperience.StateKey,
            "experience-initialize", null, 1, DateTimeOffset.UnixEpoch));
        // Build one valid persisted snapshot instead of performing 98,999 disk transactions.
        // Every prior kill retains the same complete receipt production writes for replay.
        for (int kill = 1; kill <= previousKills; kill++)
        {
            var historical = new ExperienceGrant(curve.At((uint)(kill - 1) * 100),
                curve.At((uint)kill * 100), 100);
            string operation = Operation(kill);
            file.Receipts.Add(operation, new(operation, "experience-kill", JsonSerializer.Serialize(historical),
                kill + 1, DateTimeOffset.UnixEpoch.AddSeconds(kill)));
        }
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, AccountEconomyStore.FileNameFor(accountId));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(file, new JsonSerializerOptions { WriteIndented = true }));

        var experience = new AccountExperience(new AccountEconomyStore(_root), curve);
        ExperienceGrant granted = experience.AwardKill(accountId, Operation(previousKills + 1));

        Assert.Equal(new ExperienceProgress(previousExperience, 99, 99), granted.Before);
        Assert.Equal(new ExperienceProgress(9_900_000, 100, 100), granted.After);
        Assert.Equal(100u, granted.Amount);
        Assert.False(granted.Replayed);
        var restartedStore = new AccountEconomyStore(_root);
        var restarted = new AccountExperience(restartedStore, curve, seed: 999_999);
        Assert.Equal(granted with { Replayed = true }, restarted.AwardKill(accountId, Operation(previousKills + 1)));
        Assert.Equal(new ExperienceGrant(curve.At(0), curve.At(100), 100, Replayed: true),
            restarted.AwardKill(accountId, Operation(1)));
        AccountEconomySnapshot saved = restartedStore.GetOrCreate(accountId);
        Assert.Equal(previousKills + 2, saved.Receipts.Count);
        Assert.Equal(previousKills + 2L, saved.Revision);
        Assert.Equal("9900000", saved.States[AccountExperience.StateKey]);
        Assert.Equal(file.Receipts[Operation(1)], saved.Receipts[Operation(1)]);
        Assert.Equal(granted.After, restarted.Read(accountId));
        long size = new FileInfo(path).Length;
        Assert.True(size < AccountEconomyStore.MaximumFileBytes);
        _output.WriteLine($"Persisted level-100 account: {saved.Receipts.Count:N0} receipts, {size:N0} bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetriedKillReturnsOriginalGrantWithoutAwardingAgain(bool durable)
    {
        var original = new AccountExperience(durable ? new AccountEconomyStore(_root) : null,
            ExperienceCurve.Default, seed: 1_950);
        ExperienceGrant first = original.AwardKill("a", "kill-1");
        ExperienceGrant second = original.AwardKill("a", "kill-2");
        AccountExperience retryTarget = durable
            ? new AccountExperience(new AccountEconomyStore(_root), ExperienceCurve.Default, seed: 999_999)
            : original;

        ExperienceGrant replay = retryTarget.AwardKill("a", "kill-1");

        Assert.Equal(first with { Replayed = true }, replay);
        Assert.Equal(second.After, retryTarget.Read("a"));
        Assert.Equal(2_150u, retryTarget.Read("a").Total);
    }

    [Fact]
    public async Task ConcurrentDistinctStoreInstancesDoNotLoseKillAwards()
    {
        new AccountExperience(new AccountEconomyStore(_root), ExperienceCurve.Default, seed: 1_900).Read("a");

        ExperienceGrant[] grants = await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() =>
            new AccountExperience(new AccountEconomyStore(_root), ExperienceCurve.Default, seed: 999_999)
                .AwardKill("a", $"kill-{index}"))));

        Assert.All(grants, grant =>
        {
            Assert.False(grant.Replayed);
            Assert.Equal(100u, grant.Amount);
        });
        Assert.Equal(24, grants.Select(grant => grant.After.Total).Distinct().Count());
        Assert.Equal(4_300u, new AccountExperience(new AccountEconomyStore(_root), ExperienceCurve.Default).Read("a").Total);
        var saved = new AccountEconomyStore(_root).GetOrCreate("a");
        Assert.Equal(24, saved.Receipts.Values.Count(receipt => receipt.Kind == "experience-kill"));
    }

    [Theory]
    [InlineData("not-an-integer")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public void InvalidSavedExperienceIsNotResetAndCannotProduceAKillReceipt(string invalid)
    {
        var store = new AccountEconomyStore(_root);
        Assert.True(store.Execute("a", "install-invalid-state", "test-fixture", draft =>
        {
            draft.SetState(AccountExperience.StateKey, invalid);
            return null;
        }).Succeeded);
        AccountEconomySnapshot before = store.GetOrCreate("a");
        var experience = new AccountExperience(store, ExperienceCurve.Default, seed: 1_950);

        Assert.Throws<AccountEconomyStoreException>(() => experience.Read("a"));
        Assert.Throws<AccountEconomyStoreException>(() => experience.AwardKill("a", "kill-1"));

        AccountEconomySnapshot after = new AccountEconomyStore(_root).GetOrCreate("a");
        Assert.Equal(invalid, after.States[AccountExperience.StateKey]);
        Assert.Equal(before.Revision, after.Revision);
        Assert.False(after.Receipts.ContainsKey("kill-1"));
    }

    [Fact]
    public void CorruptAccountFileRemainsUntouchedAfterReadOrAwardFailure()
    {
        new AccountExperience(new AccountEconomyStore(_root), ExperienceCurve.Default, seed: 1_950).Read("a");
        string path = Path.Combine(_root, AccountEconomyStore.FileNameFor("a"));
        const string corrupt = "{broken json";
        File.WriteAllText(path, corrupt);
        var restarted = new AccountExperience(new AccountEconomyStore(_root), ExperienceCurve.Default, seed: 999_999);

        Assert.Throws<AccountEconomyStoreException>(() => restarted.Read("a"));
        Assert.Throws<AccountEconomyStoreException>(() => restarted.AwardKill("a", "kill-1"));

        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(EconomyPersistenceStage.BeforeWrite)]
    [InlineData(EconomyPersistenceStage.AfterFlushBeforeReplace)]
    public void FailedAwardPreservesProgressAndReceiptCanBeCommittedByRetry(EconomyPersistenceStage stage)
    {
        var initialStore = new AccountEconomyStore(_root);
        new AccountExperience(initialStore, ExperienceCurve.Default, seed: 1_950).Read("a");
        AccountEconomySnapshot before = initialStore.GetOrCreate("a");
        var failingStore = new AccountEconomyStore(_root, persistenceFault: current =>
        {
            if (current == stage) throw new IOException("Injected XP persistence failure.");
        });
        var failing = new AccountExperience(failingStore, ExperienceCurve.Default, seed: 999_999);

        Assert.Throws<AccountEconomyStoreException>(() => failing.AwardKill("a", "kill-1"));

        var restartedStore = new AccountEconomyStore(_root);
        AccountEconomySnapshot failed = restartedStore.GetOrCreate("a");
        Assert.Equal("1950", failed.States[AccountExperience.StateKey]);
        Assert.Equal(before.Revision, failed.Revision);
        Assert.False(failed.Receipts.ContainsKey("kill-1"));

        var restarted = new AccountExperience(restartedStore, ExperienceCurve.Default, seed: 999_999);
        ExperienceGrant retried = restarted.AwardKill("a", "kill-1");
        Assert.False(retried.Replayed);
        Assert.Equal(new ExperienceProgress(2_050, 2, 1), retried.After);
        Assert.Equal(retried with { Replayed = true }, restarted.AwardKill("a", "kill-1"));
        Assert.Equal(retried.After, restarted.Read("a"));
    }
}
