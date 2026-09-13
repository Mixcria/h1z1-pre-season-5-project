using System.Globalization;
using System.Text.Json;
using Cranberry.Zone.Match;

namespace Cranberry.Zone.Economy;

public sealed record BountyBackingRecord(ulong MatchId, string RunId, uint OptionId,
    uint CurrencyId, uint Amount, string Status, uint[] SkullPayouts, uint[] CreditPayouts,
    uint Placement = 0, uint AwardedSkulls = 0, uint AwardedCredits = 0);

/// <summary>Saved backing lifecycle. Prices/tables are snapshotted when the ante is committed.</summary>
public sealed class BountyLedger(AccountEconomyStore store, string runId)
{
    public const string RecordPrefix = "bounty:record:";
    private const string FreeActive = "bounty:free-active";
    public static string Key(ulong matchId) => RecordPrefix + matchId.ToString("x16");

    public BountyBackingRecord? Read(string accountId, ulong matchId)
    {
        var account = store.GetOrCreate(accountId);
        return account.States.TryGetValue(Key(matchId), out string? json)
            ? ParseRecord(json, matchId) : null;
    }

    public AccountEconomyResult Back(string accountId, MatchAdmissionContext context, BountyPhase phase,
        uint optionId, BountyOptions options)
    {
        if (!BountyEligibility.CanBack(context, phase, options.Enabled, options.AcceptAnte)
            || !options.TryGetAnte(optionId, out var ante))
            return new(false, false, "Backing is unavailable for this match, phase or option.", null, null);
        var result = store.Execute(accountId, $"bounty:back:{context.MatchId:x16}", "bounty-back", draft =>
        {
            if (draft.GetState(Key(context.MatchId)) is not null)
                throw new EconomyRejectedException("This match already has a backing outcome.");
            if (optionId == BountyOptions.FreeCreditsOption && draft.GetState(FreeActive) is not null)
                throw new EconomyRejectedException("Another Free Bounty is already outstanding.");
            if (options.SkullPayouts.Any(value => value > AccountEconomyStore.MaximumValue)
                || options.CreditPayouts.Any(value => value > AccountEconomyStore.MaximumValue))
                throw new EconomyRejectedException("Invalid payout table.");
            draft.Debit(ante.CurrencyId, ante.Amount);
            var record = new BountyBackingRecord(context.MatchId, runId, optionId, ante.CurrencyId,
                ante.Amount, "Backed", options.SkullPayouts.ToArray(), options.CreditPayouts.ToArray());
            string json = JsonSerializer.Serialize(record);
            draft.SetState(Key(context.MatchId), json);
            if (optionId == BountyOptions.FreeCreditsOption) draft.SetState(FreeActive, Key(context.MatchId));
            return json;
        });
        // A receipt replays the original operation, but its current snapshot may already be
        // locked, cancelled or settled. Never present that historic receipt as fresh backing.
        // Inspect the transaction's snapshot instead of a separate pre-read with a race window.
        if (result.Succeeded)
        {
            BountyBackingRecord? current = result.Snapshot!.States.TryGetValue(Key(context.MatchId), out string? json)
                ? ParseRecord(json, context.MatchId) : null;
            if (current?.Status != "Backed")
                return new(false, false, "This match is no longer accepting backing.", result.Snapshot, null);
        }
        return result;
    }

    public AccountEconomyResult Lock(string accountId, ulong matchId) =>
        store.Execute(accountId, $"bounty:lock:{matchId:x16}", "bounty-lock", draft =>
        {
            var record = Require(draft, matchId);
            if (record.Status != "Backed") throw new EconomyRejectedException("Backing cannot be locked in its current state.");
            string json = JsonSerializer.Serialize(record with { Status = "Locked" });
            draft.SetState(Key(matchId), json);
            return json;
        });

    /// <summary>Persist the authoritative result before paying it, so restart retains placement.</summary>
    public AccountEconomyResult RecordResult(string accountId, MatchAdmissionContext context, uint placement,
        BountyOptions options) => store.Execute(accountId, $"bounty:result:{context.MatchId:x16}", "bounty-result", draft =>
        {
            if (!BountyEligibility.IsEligible(context) || !options.Enabled || placement == 0)
                throw new EconomyRejectedException("Invalid bounty result.");
            string? previous = draft.GetState(Key(context.MatchId));
            var record = previous is null ? new BountyBackingRecord(context.MatchId, runId, 0, 0, 0,
                "Unbacked", [], options.CreditPayouts.ToArray()) : ParseRecord(previous, context.MatchId);
            if (record.Status is "ResultPending" or "Settled") return previous;
            if (record.Status is not ("Locked" or "Unbacked"))
                throw new EconomyRejectedException("Only a started match can record a result.");
            if (record.CreditPayouts.Any(value => value > AccountEconomyStore.MaximumValue))
                throw new EconomyRejectedException("Invalid payout table.");
            string json = JsonSerializer.Serialize(record with { Status = "ResultPending", Placement = placement });
            draft.SetState(Key(context.MatchId), json);
            return json;
        });

    /// <summary>beforeStart is an authoritative host fact, never a client-supplied refund flag.</summary>
    public AccountEconomyResult RecordRefund(string accountId, ulong matchId, bool beforeStart = false) =>
        store.Execute(accountId, $"bounty:refund:{matchId:x16}", "bounty-refund", draft =>
        {
            var record = Require(draft, matchId);
            if (record.Status is "RefundPending" or "Cancelled") return JsonSerializer.Serialize(record);
            if (record.Status != "Backed" && !(beforeStart && record.RunId == runId && record.Status == "Locked"))
                throw new EconomyRejectedException("This backing cannot record a refund.");
            string json = JsonSerializer.Serialize(record with { Status = "RefundPending" });
            draft.SetState(Key(matchId), json);
            return json;
        });

    public AccountEconomyResult Settle(string accountId, MatchAdmissionContext context, uint placement,
        BountyOptions options) => store.Execute(accountId, $"bounty:settle:{context.MatchId:x16}", "bounty-settle", draft =>
        {
            if (!BountyEligibility.IsEligible(context) || !options.Enabled || placement == 0)
                throw new EconomyRejectedException("Invalid bounty result.");
            string? previous = draft.GetState(Key(context.MatchId));
            var record = previous is null ? new BountyBackingRecord(context.MatchId, runId, 0, 0, 0,
                "Unbacked", [], options.CreditPayouts.ToArray())
                : ParseRecord(previous, context.MatchId);
            if (record.Status is not ("Locked" or "Unbacked" or "ResultPending"))
                throw new EconomyRejectedException("Only a started match can be settled.");
            if (record.Status == "ResultPending" && record.Placement != placement)
                throw new EconomyRejectedException("The original recorded placement cannot change.");
            uint skulls = record.OptionId == 0 ? 0 : Payout(record.SkullPayouts, placement);
            uint credits = Payout(record.CreditPayouts, placement);
            draft.Credit(5, skulls);
            draft.Credit(6, credits);
            record = record with { Status = "Settled", Placement = placement,
                AwardedSkulls = skulls, AwardedCredits = credits };
            string json = JsonSerializer.Serialize(record);
            draft.SetState(Key(context.MatchId), json);
            ClearFree(draft, context.MatchId);
            return json;
        });

    public AccountEconomyResult Cancel(string accountId, ulong matchId, bool abandonedRun = false) =>
        store.Execute(accountId, $"bounty:cancel:{matchId:x16}", "bounty-cancel", draft =>
        {
            var record = Require(draft, matchId);
            if (record.Status is not ("Backed" or "RefundPending")
                && !(abandonedRun && record.Status == "Locked" && record.RunId != runId))
                throw new EconomyRejectedException("This backing cannot be refunded.");
            draft.Credit(record.CurrencyId, record.Amount);
            string json = JsonSerializer.Serialize(record with { Status = "Cancelled" });
            draft.SetState(Key(matchId), json);
            ClearFree(draft, matchId);
            return json;
        });

    /// <summary>Single-host recovery policy: an unrecoverable previous run refunds outstanding antes once.</summary>
    public void RecoverAbandonedRun(string accountId)
    {
        foreach (var pair in store.GetOrCreate(accountId).States
            .Where(pair => pair.Key.StartsWith(RecordPrefix, StringComparison.Ordinal)))
        {
            if (!ulong.TryParse(pair.Key.AsSpan(RecordPrefix.Length), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out ulong matchId) || pair.Key != Key(matchId))
                throw new AccountEconomyStoreException("Invalid saved bounty record key.");
            var record = ParseRecord(pair.Value, matchId);
            if (record.Status is "ResultPending" or "RefundPending")
            {
                var completed = record.Status == "ResultPending"
                    ? Settle(accountId, new(matchId, MatchQueueKind.Public, MatchMode.Solo), record.Placement, BountyOptions.Default)
                    : Cancel(accountId, matchId);
                if (!completed.Succeeded) throw new AccountEconomyStoreException(completed.Error ?? "Pending bounty completion failed.");
                continue;
            }
            if (record.RunId == runId || record.Status is not ("Backed" or "Locked")) continue;
            var recovered = Cancel(accountId, record.MatchId, abandonedRun: true);
            if (!recovered.Succeeded) throw new AccountEconomyStoreException(recovered.Error ?? "Bounty recovery failed.");
        }
    }

    private static BountyBackingRecord Require(AccountEconomyDraft draft, ulong matchId) =>
        ParseRecord(draft.GetState(Key(matchId))
            ?? throw new EconomyRejectedException("No backing exists for this match."), matchId);

    private static BountyBackingRecord ParseRecord(string json, ulong expectedMatchId)
    {
        BountyBackingRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<BountyBackingRecord>(json);
        }
        catch (JsonException exception)
        {
            throw new AccountEconomyStoreException("Invalid saved bounty JSON.", exception);
        }
        if (record is null || record.SkullPayouts is null || record.CreditPayouts is null)
            throw new AccountEconomyStoreException("Invalid saved bounty record.");
        if (record.MatchId == 0 || record.MatchId != expectedMatchId
            || string.IsNullOrWhiteSpace(record.RunId) || record.RunId.Length > 256
            || record.Status is not ("Backed" or "Locked" or "Cancelled" or "Settled" or "ResultPending" or "RefundPending")
            || record.SkullPayouts.Any(value => value > AccountEconomyStore.MaximumValue)
            || record.CreditPayouts.Any(value => value > AccountEconomyStore.MaximumValue))
            throw new AccountEconomyStoreException("Invalid saved bounty record.");
        bool valid;
        if (record.OptionId == 0)
            valid = record.CurrencyId == 0 && record.Amount == 0 && record.Status is ("Settled" or "ResultPending")
                && record.SkullPayouts.Length == 0;
        else
            valid = record.OptionId is BountyOptions.CrownsOption or BountyOptions.SkullsOption or BountyOptions.FreeCreditsOption
                && record.CurrencyId == record.OptionId + 3 && record.Amount is > 0 and <= AccountEconomyStore.MaximumValue;
        if (record.Status == "Settled")
            valid &= record.Placement > 0
                && record.AwardedSkulls == (record.OptionId == 0 ? 0 : Payout(record.SkullPayouts, record.Placement))
                && record.AwardedCredits == Payout(record.CreditPayouts, record.Placement);
        else if (record.Status == "ResultPending")
            valid &= record.Placement > 0 && record.AwardedSkulls == 0 && record.AwardedCredits == 0;
        else
            valid &= record.Placement == 0 && record.AwardedSkulls == 0 && record.AwardedCredits == 0;
        if (!valid) throw new AccountEconomyStoreException("Invalid saved bounty lifecycle or payout.");
        return record;
    }
    private static void ClearFree(AccountEconomyDraft draft, ulong matchId)
    {
        if (draft.GetState(FreeActive) == Key(matchId)) draft.RemoveState(FreeActive);
    }
    private static uint Payout(uint[] table, uint placement) => placement <= table.Length ? table[placement - 1] : 0;
}
