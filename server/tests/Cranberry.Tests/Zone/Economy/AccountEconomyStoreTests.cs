using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

public sealed class AccountEconomyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-economy-tests", Guid.NewGuid().ToString("N"));
    private static AccountEconomySeed Seed(uint scrap = 100, uint crowns = 1000) => new(
        new Dictionary<uint, uint> { [1] = scrap, [4] = crowns },
        [new OwnedAccountItem(123, 200, 300, 2, "explicit test migration")]);

    public void Dispose()
    {
        // Only the independently generated test directory is removed, never user data or logs.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ScrapAtomicallyConsumesOneDuplicateAndPersistsCreditWithoutReseeding()
    {
        var store = new AccountEconomyStore(_root);
        AccountEconomySnapshot initial = store.GetOrCreate("account-a", Seed());
        AccountEconomyResult result = store.Execute("account-a", "scrap-1", "scrap", draft =>
        {
            draft.Consume(123, 1);
            draft.Credit(1, 20);
            return "{\"scrap\":20}";
        });
        Assert.True(result.Succeeded);
        Assert.False(result.Replayed);
        Assert.Equal(120u, result.Snapshot!.Balance(1));
        Assert.Equal(1u, Assert.Single(result.Snapshot.Items).Count);
        Assert.Equal(2u, Assert.Single(initial.Items).Count);
        var restarted = new AccountEconomyStore(_root);
        AccountEconomySnapshot restored = restarted.GetOrCreate("account-a", Seed(scrap: 9999));
        Assert.Equal(120u, restored.Balance(1));
        Assert.Equal(1u, Assert.Single(restored.Items).Count);
        Assert.Equal(result.Receipt, restored.Receipts["scrap-1"]);
    }

    [Fact]
    public void SpentCurrencyGrantAndResultReplaySurviveRestartWithoutReroll()
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("a", Seed());
        int draws = 0;
        AccountEconomyResult Roll(AccountEconomyStore target) => target.Execute("a", "roll-1", "scrapyard", draft =>
        {
            draws++;
            draft.Debit(1, 100);
            OwnedAccountItem reward = draft.Grant(201, 301, 1, "scrapyard:v1");
            return reward.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        AccountEconomyResult first = Roll(store);
        AccountEconomyResult replay = Roll(new AccountEconomyStore(_root));
        Assert.True(first.Succeeded);
        Assert.True(replay.Replayed);
        Assert.Equal(1, draws);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(0u, replay.Snapshot!.Balance(1));
        Assert.Equal(2, replay.Snapshot.Items.Count);
        Assert.NotEqual(0UL, replay.Snapshot.Items.Single(item => item.AccountItemId == 201).InstanceId);
    }

    [Fact]
    public void LastCopyConsumptionAndBackingStateCanShareOneCommit()
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("a", Seed());
        AccountEconomyResult backed = store.Execute("a", "match-42:back", "bounty-back", draft =>
        {
            draft.Debit(4, 500);
            draft.Consume(123, 2);
            draft.SetState("match-42", "backed:500");
            return "backed";
        });
        Assert.True(backed.Succeeded);
        Assert.Empty(backed.Snapshot!.Items);
        Assert.Equal("backed:500", backed.Snapshot.States["match-42"]);
        AccountEconomyResult refunded = store.Execute("a", "match-42:refund", "bounty-refund", draft =>
        {
            Assert.Equal("backed:500", draft.GetState("match-42"));
            draft.Credit(4, 500);
            draft.SetState("match-42", "cancelled");
            return "refunded";
        });
        Assert.Equal(1000u, refunded.Snapshot!.Balance(4));
        Assert.Equal("cancelled", new AccountEconomyStore(_root).GetOrCreate("a").States["match-42"]);
    }

    [Fact]
    public async Task ConcurrentStoreInstancesSerializeAndAnOperationIdCommitsOnlyOnce()
    {
        new AccountEconomyStore(_root).GetOrCreate("a", Seed(scrap: 0));
        int mutations = 0;
        AccountEconomyResult[] results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            new AccountEconomyStore(_root).Execute("a", "same-credit", "credit", draft =>
            {
                Interlocked.Increment(ref mutations);
                draft.Credit(1, 20);
                return "20";
            }))));
        Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
        Assert.Equal(1, mutations);
        Assert.Single(results, result => !result.Replayed);
        Assert.Equal(20u, new AccountEconomyStore(_root).GetOrCreate("a").Balance(1));

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            AccountEconomyResult result = new AccountEconomyStore(_root).Execute("a", $"credit-{index}", "credit", draft =>
            {
                draft.Credit(1, 1);
                return null;
            });
            Assert.True(result.Succeeded, result.Error);
        })));
        Assert.Equal(36u, new AccountEconomyStore(_root).GetOrCreate("a").Balance(1));
    }

    [Fact]
    public void DomainRejectionAndOverflowDiscardEveryPriorDraftChange()
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("a", Seed(scrap: AccountEconomyStore.MaximumValue));
        AccountEconomyResult overflow = store.Execute("a", "overflow", "scrap", draft =>
        {
            draft.Consume(123, 1);
            draft.Credit(1, 1);
            return null;
        });
        Assert.False(overflow.Succeeded);
        Assert.Equal(2u, Assert.Single(overflow.Snapshot!.Items).Count);
        Assert.Empty(overflow.Snapshot.Receipts);

        AccountEconomyResult insufficient = store.Execute("a", "insufficient", "crate", draft =>
        {
            draft.Consume(123, 1);
            draft.Debit(4, 1001);
            return null;
        });
        Assert.False(insufficient.Succeeded);
        Assert.Equal(2u, Assert.Single(store.GetOrCreate("a").Items).Count);
    }

    [Theory]
    [InlineData(EconomyPersistenceStage.BeforeWrite)]
    [InlineData(EconomyPersistenceStage.AfterFlushBeforeReplace)]
    public void InterruptedCommitPreservesPreviouslyCommittedInventoryAndCurrency(EconomyPersistenceStage stage)
    {
        var initial = new AccountEconomyStore(_root);
        initial.GetOrCreate("a", Seed());
        var failing = new AccountEconomyStore(_root, persistenceFault: current =>
        {
            if (current == stage) throw new IOException("Injected persistence failure.");
        });
        AccountEconomyResult failed = failing.Execute("a", "scrap-1", "scrap", draft =>
        {
            draft.Consume(123, 1);
            draft.Credit(1, 20);
            return null;
        });
        Assert.False(failed.Succeeded);
        AccountEconomySnapshot restored = new AccountEconomyStore(_root).GetOrCreate("a");
        Assert.Equal(100u, restored.Balance(1));
        Assert.Equal(2u, Assert.Single(restored.Items).Count);
        Assert.Empty(restored.Receipts);
        Assert.Equal(0L, restored.Revision);
    }

    [Fact]
    public void InterruptedFirstCreationCannotSilentlyReseed()
    {
        var failing = new AccountEconomyStore(_root, persistenceFault: stage =>
        {
            if (stage == EconomyPersistenceStage.AfterFlushBeforeReplace) throw new IOException("Injected first-creation crash.");
        });
        Assert.Throws<AccountEconomyStoreException>(() => failing.GetOrCreate("a", Seed()));
        Assert.Throws<AccountEconomyStoreException>(() => new AccountEconomyStore(_root).GetOrCreate("a", Seed(scrap: 9999)));
        Assert.Single(Directory.GetFiles(_root, "*.pending-*"));
    }

    [Fact]
    public void BriefWindowsReaderDuringReplaceDoesNotRefuseAdmissionOrRepeatMutation()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows readers without delete sharing block ReplaceFile.
        var initial = new AccountEconomyStore(_root);
        initial.GetOrCreate("a", Seed());
        string path = Path.Combine(_root, AccountEconomyStore.FileNameFor("a"));
        FileStream? reader = null;
        using var release = new Timer(_ => reader?.Dispose(), null, Timeout.Infinite, Timeout.Infinite);
        int mutations = 0;
        var store = new AccountEconomyStore(_root, persistenceFault: stage =>
        {
            if (stage != EconomyPersistenceStage.AfterFlushBeforeReplace) return;
            reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            release.Change(50, Timeout.Infinite);
        });
        try
        {
            var result = store.Execute("a", "initialize", "admission", draft =>
            {
                mutations++;
                draft.Credit(1, 20);
                return null;
            });
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(1, mutations);
            Assert.Equal(120u, new AccountEconomyStore(_root).GetOrCreate("a").Balance(1));
            Assert.Single(result.Snapshot!.Receipts);
            Assert.Empty(Directory.GetFiles(_root, "*.pending-*"));
        }
        finally { reader?.Dispose(); }
    }

    [Fact]
    public void PersistentWindowsReaderPreservesCommittedBalanceAndPendingEvidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        var initial = new AccountEconomyStore(_root);
        initial.GetOrCreate("a", Seed());
        string path = Path.Combine(_root, AccountEconomyStore.FileNameFor("a"));
        byte[] before = File.ReadAllBytes(path);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var result = initial.Execute("a", "blocked", "credit", draft => { draft.Credit(1, 20); return null; });
        Assert.False(result.Succeeded);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(100u, initial.GetOrCreate("a").Balance(1));
        Assert.Empty(initial.GetOrCreate("a").Receipts);
        Assert.Single(Directory.GetFiles(_root, "*.pending-*"));
    }

    [Fact]
    public void CorruptOrWrongIdentityFilesFailClosedAndStayUntouched()
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("a", Seed());
        string path = Path.Combine(_root, AccountEconomyStore.FileNameFor("a"));
        File.WriteAllText(path, "{broken json");
        Assert.Throws<AccountEconomyStoreException>(() => store.GetOrCreate("a", Seed()));
        AccountEconomyResult result = store.Execute("a", "not-called", "credit", _ => throw new Exception("Must not execute."));
        Assert.False(result.Succeeded);
        Assert.Null(result.Snapshot);
        Assert.Equal("{broken json", File.ReadAllText(path));
        store.GetOrCreate("b", Seed());
        File.Copy(Path.Combine(_root, AccountEconomyStore.FileNameFor("b")), path, overwrite: true);
        Assert.Throws<AccountEconomyStoreException>(() => store.GetOrCreate("a", Seed()));
    }

    [Fact]
    public void AccountsAndReturnedSnapshotsAreIsolatedAndIdsCannotTraversePaths()
    {
        var store = new AccountEconomyStore(_root);
        AccountEconomySnapshot before = store.GetOrCreate("../account/a", Seed());
        store.GetOrCreate("b", Seed(scrap: 7));
        AccountEconomyDraft? retained = null;
        AccountEconomyResult result = store.Execute("../account/a", "spend", "debit", draft =>
        {
            retained = draft;
            draft.Debit(1, 10);
            return null;
        });
        retained!.Credit(1, 1000);
        Assert.True(result.Succeeded);
        Assert.Equal(100u, before.Balance(1));
        Assert.Equal(90u, result.Snapshot!.Balance(1));
        Assert.Equal(90u, store.GetOrCreate("../account/a").Balance(1));
        Assert.Equal(7u, store.GetOrCreate("b").Balance(1));
        Assert.DoesNotContain("/", AccountEconomyStore.FileNameFor("../account/a"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<uint, uint>)before.Balances)[1] = 999);
    }

    [Fact]
    public void ReusingReceiptIdForAnotherKindIsRefusedWithoutCallingMutation()
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("a", Seed());
        Assert.True(store.Execute("a", "operation", "one", _ => null).Succeeded);
        AccountEconomyResult conflict = store.Execute("a", "operation", "two", _ => throw new Exception("Must not execute."));
        Assert.False(conflict.Succeeded);
        Assert.Single(conflict.Snapshot!.Receipts);
    }

    [Fact]
    public void UnwritableRootDoesNotFallBackToMemoryOnlyEconomy()
    {
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(file, "occupied");
        var store = new AccountEconomyStore(file);
        Assert.Throws<AccountEconomyStoreException>(() => store.GetOrCreate("a", Seed()));
        AccountEconomyResult result = store.Execute("a", "one", "credit", draft => { draft.Credit(1, 1); return null; });
        Assert.False(result.Succeeded);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData(123UL, 0u)]
    [InlineData(123UL, 3u)]
    [InlineData(999UL, 1u)]
    public void InvalidConsumptionCannotCreditScrap(ulong instance, uint count)
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("a", Seed());
        AccountEconomyResult result = store.Execute("a", "invalid", "scrap", draft =>
        {
            draft.Credit(1, 20);
            draft.Consume(instance, count);
            return null;
        });
        Assert.False(result.Succeeded);
        Assert.Equal(100u, store.GetOrCreate("a").Balance(1));
        Assert.Equal(2u, Assert.Single(result.Snapshot!.Items).Count);
    }

    [Fact]
    public void MissingSchemaAndExcessiveMutationCountFailClosed()
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("a", Seed());
        AccountEconomyResult result = store.Execute("a", "excess", "credit", draft =>
        {
            for (int i = 0; i <= AccountEconomyStore.MaximumMutationsPerOperation; i++) draft.Credit(1, 1);
            return null;
        });
        Assert.False(result.Succeeded);
        Assert.Equal(100u, result.Snapshot!.Balance(1));
        File.WriteAllText(Path.Combine(_root, AccountEconomyStore.FileNameFor("a")), "{\"AccountId\":\"a\"}");
        Assert.Throws<AccountEconomyStoreException>(() => store.GetOrCreate("a", Seed()));
    }
}
