using System.Text.Json;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.Economy;

public sealed class BountyLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-bounty-tests", Guid.NewGuid().ToString("N"));
    private static MatchAdmissionContext Solo(ulong id = 42) => new(id, MatchQueueKind.Public, MatchMode.Solo);
    private static readonly BountyOptions Options = BountyOptions.Default;

    private AccountEconomyStore Seed(uint crowns = 1000, uint skulls = 1000, uint credits = 300)
    {
        var store = new AccountEconomyStore(_root);
        store.GetOrCreate("a", new(new Dictionary<uint, uint> { [4] = crowns, [5] = skulls, [6] = credits }));
        return store;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(1u, 4u, 500u)]
    [InlineData(2u, 5u, 100u)]
    [InlineData(3u, 6u, 100u)]
    public void EachVerifiedButtonDebitsOnlyItsCurrencyAndPersistsItsOption(uint option, uint currency, uint amount)
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        AccountEconomySnapshot before = store.GetOrCreate("a");
        AccountEconomyResult result = ledger.Back("a", Solo(), BountyPhase.Lobby, option, Options);
        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.Replayed);
        foreach (uint id in new[] { 4u, 5u, 6u })
            Assert.Equal(before.Balance(id) - (id == currency ? amount : 0), result.Snapshot!.Balance(id));
        var persisted = new BountyLedger(new AccountEconomyStore(_root), "run-a").Read("a", 42)!;
        Assert.Equal(option, persisted.OptionId);
        Assert.Equal(currency, persisted.CurrencyId);
        Assert.Equal(amount, persisted.Amount);
        Assert.Equal("Backed", persisted.Status);
    }

    [Fact]
    public void RepeatAndChangedOptionCannotDebitAgainOrChangeTheOriginalChoice()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        AccountEconomyResult original = ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options);
        AccountEconomyResult replay = ledger.Back("a", Solo(), BountyPhase.Countdown, 2, Options);
        Assert.True(replay.Succeeded);
        Assert.True(replay.Replayed);
        Assert.Equal(original.Receipt, replay.Receipt);
        Assert.Equal(500u, replay.Snapshot!.Balance(4));
        Assert.Equal(1000u, replay.Snapshot.Balance(5));
        Assert.Equal(1u, ledger.Read("a", 42)!.OptionId);
    }

    [Fact]
    public void AdmissionPhaseAndUnknownOptionRejectWithoutAnyAccountMutation()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        long revision = store.GetOrCreate("a").Revision;
        foreach (MatchQueueKind queue in Enum.GetValues<MatchQueueKind>())
        foreach (MatchMode mode in Enum.GetValues<MatchMode>())
        foreach (BountyPhase phase in Enum.GetValues<BountyPhase>())
        {
            if (queue == MatchQueueKind.Public && mode == MatchMode.Solo && phase is BountyPhase.Lobby or BountyPhase.Countdown)
                continue;
            Assert.False(ledger.Back("a", new(42, queue, mode), phase, 1, Options).Succeeded);
        }
        foreach (uint option in new[] { 0u, 4u, uint.MaxValue })
            Assert.False(ledger.Back("a", Solo(), BountyPhase.Lobby, option, Options).Succeeded);
        Assert.False(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options with { Enabled = false }).Succeeded);
        Assert.False(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options with { AcceptAnte = false }).Succeeded);
        Assert.False(ledger.Back("a", Solo(0), BountyPhase.Lobby, 1, Options).Succeeded);
        Assert.Equal(revision, store.GetOrCreate("a").Revision);
        Assert.Empty(store.GetOrCreate("a").States);
    }

    [Fact]
    public void InsufficientCurrencyDoesNotReserveBackingOrCreateAReceipt()
    {
        var store = Seed(crowns: 499, skulls: 99, credits: 99);
        var ledger = new BountyLedger(store, "run-a");
        long revision = store.GetOrCreate("a").Revision;
        for (uint option = 1; option <= 3; option++)
            Assert.False(ledger.Back("a", Solo(), BountyPhase.Lobby, option, Options).Succeeded);
        var current = store.GetOrCreate("a");
        Assert.Equal(revision, current.Revision);
        Assert.Empty(current.States);
        Assert.Empty(current.Receipts);
        Assert.Equal(99u, current.Balance(6));
    }

    [Fact]
    public void FreeEntitlementIsReservedAcrossMatchesAndReleasedBySettlement()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        Assert.True(ledger.Back("a", Solo(), BountyPhase.Lobby, 3, Options).Succeeded);
        Assert.False(ledger.Back("a", Solo(43), BountyPhase.Lobby, 3, Options).Succeeded);
        Assert.Equal(200u, store.GetOrCreate("a").Balance(6));
        Assert.True(ledger.Lock("a", 42).Succeeded);
        Assert.True(ledger.Settle("a", Solo(), 1, Options).Succeeded);
        Assert.True(ledger.Back("a", Solo(43), BountyPhase.Lobby, 3, Options).Succeeded);
        Assert.Equal(200u, store.GetOrCreate("a").Balance(6));
    }

    [Fact]
    public void FreeCancellationRefundsAndReleasesOnlyOnce()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        Assert.True(ledger.Back("a", Solo(), BountyPhase.Lobby, 3, Options).Succeeded);
        Assert.True(ledger.Cancel("a", 42).Succeeded);
        AccountEconomyResult replay = ledger.Cancel("a", 42);
        Assert.True(replay.Replayed);
        Assert.Equal(300u, replay.Snapshot!.Balance(6));
        Assert.Equal("Cancelled", ledger.Read("a", 42)!.Status);
        Assert.True(ledger.Back("a", Solo(43), BountyPhase.Lobby, 3, Options).Succeeded);
        // A stale Back receipt cannot resurrect a cancelled record.
        Assert.False(ledger.Back("a", Solo(), BountyPhase.Lobby, 3, Options).Succeeded);
        Assert.Equal("Cancelled", ledger.Read("a", 42)!.Status);
        Assert.Equal(200u, store.GetOrCreate("a").Balance(6));
    }

    [Fact]
    public void OnlyLockedBackingSettlesAndDuplicateResultsNeverPayAgain()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        Assert.False(ledger.Lock("a", 42).Succeeded);
        Assert.True(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options).Succeeded);
        Assert.False(ledger.Settle("a", Solo(), 1, Options).Succeeded);
        Assert.True(ledger.Lock("a", 42).Succeeded);
        Assert.True(ledger.Lock("a", 42).Replayed);
        Assert.False(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options).Succeeded);
        Assert.False(ledger.Cancel("a", 42).Succeeded);
        Assert.False(ledger.Cancel("a", 42, abandonedRun: true).Succeeded);
        Assert.False(ledger.Settle("a", Solo(), 0, Options).Succeeded);
        AccountEconomyResult first = ledger.Settle("a", Solo(), 2, Options);
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(1300u, first.Snapshot!.Balance(5));
        Assert.Equal(375u, first.Snapshot.Balance(6));
        AccountEconomyResult duplicate = ledger.Settle("a", Solo(), 1, Options);
        Assert.True(duplicate.Replayed);
        Assert.Equal(first.Receipt, duplicate.Receipt);
        Assert.Equal(1300u, duplicate.Snapshot!.Balance(5));
        Assert.Equal(2u, ledger.Read("a", 42)!.Placement);
        Assert.Equal("Settled", ledger.Read("a", 42)!.Status);
        Assert.False(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options).Succeeded);
        Assert.False(ledger.Cancel("a", 42).Succeeded);
    }

    [Fact]
    public void PayoutTablesAreSnapshottedAtBackingAndOutOfRangePlacementPaysZero()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        uint[] skulls = [17], credits = [11];
        var original = Options with { SkullPayouts = skulls, CreditPayouts = credits };
        Assert.True(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, original).Succeeded);
        skulls[0] = 999;
        credits[0] = 999;
        Assert.True(ledger.Lock("a", 42).Succeeded);
        Assert.True(ledger.Settle("a", Solo(), 1, Options).Succeeded);
        Assert.Equal(1017u, store.GetOrCreate("a").Balance(5));
        Assert.Equal(311u, store.GetOrCreate("a").Balance(6));
        Assert.True(ledger.Back("a", Solo(43), BountyPhase.Lobby, 2, Options).Succeeded);
        Assert.True(ledger.Lock("a", 43).Succeeded);
        Assert.True(ledger.Settle("a", Solo(43), uint.MaxValue, Options).Succeeded);
        Assert.Equal(917u, store.GetOrCreate("a").Balance(5));
        Assert.Equal(311u, store.GetOrCreate("a").Balance(6));
    }

    [Fact]
    public void UnbackedSoloEarnsCreditsButNoSkullsAndIneligibleResultsEarnNeither()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        foreach (MatchQueueKind queue in Enum.GetValues<MatchQueueKind>())
        foreach (MatchMode mode in Enum.GetValues<MatchMode>())
        {
            if (queue == MatchQueueKind.Public && mode == MatchMode.Solo) continue;
            Assert.False(ledger.Settle("a", new(42, queue, mode), 1, Options).Succeeded);
        }
        Assert.False(ledger.Settle("a", Solo(), 1, Options with { Enabled = false }).Succeeded);
        Assert.True(ledger.Settle("a", Solo(), 1, Options).Succeeded);
        Assert.Equal(1000u, store.GetOrCreate("a").Balance(5));
        Assert.Equal(400u, store.GetOrCreate("a").Balance(6));
        Assert.Equal(0u, ledger.Read("a", 42)!.OptionId);
    }

    [Fact]
    public void PreviousRunRecoveryRefundsBackedAndLockedButLeavesCurrentAndSettledAlone()
    {
        var store = Seed(crowns: 2000);
        var previous = new BountyLedger(store, "old");
        Assert.True(previous.Back("a", Solo(), BountyPhase.Lobby, 1, Options).Succeeded);
        Assert.True(previous.Back("a", Solo(43), BountyPhase.Lobby, 3, Options).Succeeded);
        Assert.True(previous.Lock("a", 43).Succeeded);
        Assert.True(previous.Back("a", Solo(44), BountyPhase.Lobby, 2, Options).Succeeded);
        Assert.True(previous.Lock("a", 44).Succeeded);
        Assert.True(previous.Settle("a", Solo(44), 1, Options).Succeeded);
        var current = new BountyLedger(new AccountEconomyStore(_root), "new");
        Assert.True(current.Back("a", Solo(45), BountyPhase.Lobby, 1, Options).Succeeded);
        current.RecoverAbandonedRun("a");
        long recoveredRevision = store.GetOrCreate("a").Revision;
        current.RecoverAbandonedRun("a");
        Assert.Equal(recoveredRevision, store.GetOrCreate("a").Revision);
        Assert.Equal("Cancelled", current.Read("a", 42)!.Status);
        Assert.Equal("Cancelled", current.Read("a", 43)!.Status);
        Assert.Equal("Settled", current.Read("a", 44)!.Status);
        Assert.Equal("Backed", current.Read("a", 45)!.Status);
        Assert.Equal(1500u, store.GetOrCreate("a").Balance(4));
        Assert.Equal(1400u, store.GetOrCreate("a").Balance(5));
        Assert.Equal(400u, store.GetOrCreate("a").Balance(6));
        Assert.True(current.Back("a", Solo(46), BountyPhase.Lobby, 3, Options).Succeeded);
    }

    [Theory]
    [InlineData(EconomyPersistenceStage.BeforeWrite)]
    [InlineData(EconomyPersistenceStage.AfterFlushBeforeReplace)]
    public void FailedBackingStoreWriteCannotDebitOrReserveFreeEntitlement(EconomyPersistenceStage failedStage)
    {
        var store = Seed();
        long revision = store.GetOrCreate("a").Revision;
        var failing = new AccountEconomyStore(_root, persistenceFault: stage =>
        {
            if (stage == failedStage) throw new IOException("test disk failure");
        });
        Assert.False(new BountyLedger(failing, "run-a").Back("a", Solo(), BountyPhase.Lobby, 3, Options).Succeeded);
        var after = store.GetOrCreate("a");
        Assert.Equal(revision, after.Revision);
        Assert.Equal(300u, after.Balance(6));
        Assert.Empty(after.States);
        Assert.Empty(after.Receipts);
        Assert.True(new BountyLedger(store, "run-a").Back("a", Solo(), BountyPhase.Lobby, 3, Options).Succeeded);
    }

    [Fact]
    public void SettlementOverflowDiscardsBothCurrencyAwardsAndKeepsLockedBacking()
    {
        var store = Seed(credits: AccountEconomyStore.MaximumValue);
        var ledger = new BountyLedger(store, "run-a");
        Assert.True(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options).Succeeded);
        Assert.True(ledger.Lock("a", 42).Succeeded);
        long revision = store.GetOrCreate("a").Revision;
        Assert.False(ledger.Settle("a", Solo(), 1, Options).Succeeded);
        Assert.Equal(revision, store.GetOrCreate("a").Revision);
        Assert.Equal(1000u, store.GetOrCreate("a").Balance(5));
        Assert.Equal(AccountEconomyStore.MaximumValue, store.GetOrCreate("a").Balance(6));
        Assert.Equal("Locked", ledger.Read("a", 42)!.Status);
    }

    [Fact]
    public void FailedSettlementCommitCanRetryWithoutLostOrDoublePayment()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        Assert.True(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options).Succeeded);
        Assert.True(ledger.Lock("a", 42).Succeeded);
        var failing = new AccountEconomyStore(_root, persistenceFault: _ => throw new IOException("test disk failure"));
        Assert.False(new BountyLedger(failing, "run-a").Settle("a", Solo(), 1, Options).Succeeded);
        Assert.Equal("Locked", ledger.Read("a", 42)!.Status);
        Assert.Equal(1000u, store.GetOrCreate("a").Balance(5));
        Assert.True(ledger.Settle("a", Solo(), 1, Options).Succeeded);
        Assert.True(ledger.Settle("a", Solo(), 1, Options).Replayed);
        Assert.Equal(1500u, store.GetOrCreate("a").Balance(5));
    }

    public static IEnumerable<object[]> InvalidSavedRecords()
    {
        yield return ["{broken"];
        yield return ["null"];
        yield return ["{}"];
        yield return ["[]"];
        var valid = new BountyBackingRecord(42, "old", 1, 4, 500, "Backed", [500], [100]);
        BountyBackingRecord[] invalid =
        [
            valid with { MatchId = 43 },
            valid with { RunId = null! },
            valid with { RunId = "" },
            valid with { Status = "Unknown" },
            valid with { SkullPayouts = null! },
            valid with { CreditPayouts = [uint.MaxValue] },
            valid with { OptionId = 4 },
            valid with { CurrencyId = 6 },
            valid with { Amount = 0 },
            valid with { Amount = uint.MaxValue },
            valid with { OptionId = 0, CurrencyId = 0, Amount = 0 },
            valid with { Placement = 1 },
            valid with { AwardedSkulls = 1 },
            valid with { Status = "Settled", Placement = 0 },
            valid with { Status = "Settled", Placement = 1, AwardedSkulls = 500, AwardedCredits = 99 },
        ];
        foreach (BountyBackingRecord record in invalid) yield return [JsonSerializer.Serialize(record)];
    }

    [Theory]
    [MemberData(nameof(InvalidSavedRecords))]
    public void CorruptDomainRecordsFailClosedWithoutLeakingJsonExceptionsOrMutatingBalances(string json)
    {
        var store = Seed();
        Assert.True(store.Execute("a", "fixture", "test", draft =>
        {
            draft.SetState(BountyLedger.Key(42), json);
            return null;
        }).Succeeded);
        var ledger = new BountyLedger(store, "new");
        long revision = store.GetOrCreate("a").Revision;
        Assert.Throws<AccountEconomyStoreException>(() => ledger.Read("a", 42));
        Assert.Throws<AccountEconomyStoreException>(() => ledger.RecoverAbandonedRun("a"));
        Assert.False(ledger.Lock("a", 42).Succeeded);
        Assert.False(ledger.Cancel("a", 42, abandonedRun: true).Succeeded);
        Assert.False(ledger.Settle("a", Solo(), 1, Options).Succeeded);
        Assert.Equal(revision, store.GetOrCreate("a").Revision);
        Assert.Equal(1000u, store.GetOrCreate("a").Balance(4));
        Assert.Equal(1000u, store.GetOrCreate("a").Balance(5));
        Assert.Equal(300u, store.GetOrCreate("a").Balance(6));
    }

    [Fact]
    public void MalformedRecordKeysCannotRedirectRecoveryToAnotherMatch()
    {
        var store = Seed();
        var record = new BountyBackingRecord(42, "old", 1, 4, 500, "Backed", [500], [100]);
        Assert.True(store.Execute("a", "fixture", "test", draft =>
        {
            draft.SetState(BountyLedger.RecordPrefix + "not-hex", JsonSerializer.Serialize(record));
            return null;
        }).Succeeded);
        Assert.Throws<AccountEconomyStoreException>(() => new BountyLedger(store, "new").RecoverAbandonedRun("a"));
        Assert.Equal(1000u, store.GetOrCreate("a").Balance(4));
    }

    [Fact]
    public void FailedRecoveryRefundCanRetryWithoutReleasingOrDuplicatingTheEntitlement()
    {
        var store = Seed();
        var old = new BountyLedger(store, "old");
        Assert.True(old.Back("a", Solo(), BountyPhase.Lobby, 3, Options).Succeeded);
        Assert.True(old.Lock("a", 42).Succeeded);
        var failing = new AccountEconomyStore(_root, persistenceFault: _ => throw new IOException("test disk failure"));
        Assert.Throws<AccountEconomyStoreException>(() => new BountyLedger(failing, "new").RecoverAbandonedRun("a"));
        Assert.Equal("Locked", old.Read("a", 42)!.Status);
        Assert.Equal(200u, store.GetOrCreate("a").Balance(6));
        var current = new BountyLedger(store, "new");
        current.RecoverAbandonedRun("a");
        current.RecoverAbandonedRun("a");
        Assert.Equal(300u, store.GetOrCreate("a").Balance(6));
        Assert.True(current.Back("a", Solo(43), BountyPhase.Lobby, 3, Options).Succeeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordedResultSurvivesRestartAndKeepsItsOriginalPlacementAndPayouts(bool backed)
    {
        var store = Seed();
        var old = new BountyLedger(store, "old");
        var options = Options with { SkullPayouts = [17, 13], CreditPayouts = [11, 7] };
        if (backed)
        {
            Assert.True(old.Back("a", Solo(), BountyPhase.Lobby, 1, options).Succeeded);
            Assert.True(old.Lock("a", 42).Succeeded);
        }
        Assert.True(old.RecordResult("a", Solo(), 2, options).Succeeded);
        Assert.True(old.RecordResult("a", Solo(), 1, Options).Replayed);
        Assert.Equal("ResultPending", old.Read("a", 42)!.Status);
        Assert.False(old.Settle("a", Solo(), 1, options).Succeeded);
        var restarted = new BountyLedger(new AccountEconomyStore(_root), "new");
        restarted.RecoverAbandonedRun("a");
        restarted.RecoverAbandonedRun("a");
        Assert.Equal("Settled", restarted.Read("a", 42)!.Status);
        Assert.Equal(2u, restarted.Read("a", 42)!.Placement);
        Assert.Equal(backed ? 1013u : 1000u, store.GetOrCreate("a").Balance(5));
        Assert.Equal(307u, store.GetOrCreate("a").Balance(6));
        Assert.Equal(backed ? 500u : 1000u, store.GetOrCreate("a").Balance(4));
    }

    [Fact]
    public void RecordedRefundSurvivesRestartAndReleasesTheFreeReservationOnlyWhenPaid()
    {
        var store = Seed();
        var old = new BountyLedger(store, "old");
        Assert.True(old.Back("a", Solo(), BountyPhase.Lobby, 3, Options).Succeeded);
        Assert.True(old.RecordRefund("a", 42).Succeeded);
        Assert.Equal("RefundPending", old.Read("a", 42)!.Status);
        Assert.Equal(200u, store.GetOrCreate("a").Balance(6));
        Assert.False(old.Back("a", Solo(43), BountyPhase.Lobby, 3, Options).Succeeded);
        var restarted = new BountyLedger(new AccountEconomyStore(_root), "new");
        restarted.RecoverAbandonedRun("a");
        restarted.RecoverAbandonedRun("a");
        Assert.Equal("Cancelled", restarted.Read("a", 42)!.Status);
        Assert.Equal(300u, store.GetOrCreate("a").Balance(6));
        Assert.True(restarted.Back("a", Solo(43), BountyPhase.Lobby, 3, Options).Succeeded);
    }

    [Fact]
    public void PartiallyLockedMatchNeedsExplicitPrestartAuthorityToRecordARefund()
    {
        var store = Seed();
        var ledger = new BountyLedger(store, "run-a");
        Assert.True(ledger.Back("a", Solo(), BountyPhase.Lobby, 1, Options).Succeeded);
        Assert.True(ledger.Lock("a", 42).Succeeded);
        Assert.False(ledger.RecordRefund("a", 42).Succeeded);
        Assert.True(ledger.RecordRefund("a", 42, beforeStart: true).Succeeded);
        Assert.True(ledger.Cancel("a", 42).Succeeded);
        Assert.Equal(1000u, store.GetOrCreate("a").Balance(4));
    }
}
