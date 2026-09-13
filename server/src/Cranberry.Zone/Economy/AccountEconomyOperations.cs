using System.Text.Json;

namespace Cranberry.Zone.Economy;

public sealed record EconomyAwardReceipt(IReadOnlyList<OwnedAccountItem> Consumed,
    IReadOnlyList<OwnedAccountItem> Granted, IReadOnlyDictionary<uint, long> CurrencyChanges);

/// <summary>Atomic account actions. The transport supplies an operation ID, never a reward or price.</summary>
public sealed class AccountEconomyOperations(AccountEconomyStore store, EconomyCatalog? catalog = null)
{
    private readonly EconomyCatalog _catalog = catalog ?? EconomyCatalog.Default;

    public AccountEconomyResult Scrap(string accountId, string operationId, uint accountItemId,
        uint? stackAtClick = null) => store.Execute(accountId, operationId, "scrap", draft =>
    {
        if (!_catalog.TryGetSkin(accountItemId, out EconomySkin skin) || !skin.CanScrap)
            throw new EconomyRejectedException("This cosmetic cannot be scrapped.");
        var copies = draft.Items.Where(item => item.AccountItemId == accountItemId).OrderBy(item => item.InstanceId).ToArray();
        ulong held = (ulong)copies.Sum(item => (long)item.Count);
        if (held == 0 || (stackAtClick.HasValue && stackAtClick.Value != held))
            throw new EconomyRejectedException("The selected cosmetic stack has changed.");
        var item = copies[0];
        if (!item.Scrappable) throw new EconomyRejectedException("This owned copy cannot be scrapped.");
        var consumed = draft.Consume(item.InstanceId, 1);
        draft.Credit(1, checked((uint)skin.ScrapValue));
        return JsonSerializer.Serialize(new EconomyAwardReceipt([consumed], [],
            new Dictionary<uint, long> { [1] = skin.ScrapValue }));
    });

    public AccountEconomyResult ScrapBatch(string accountId, string operationId,
        IReadOnlyList<AccountRewardRow> requests) => store.Execute(accountId, operationId, "scrap-batch", draft =>
    {
        if (requests.Count == 0 || requests.Any(row => row.Count == 0)
            || requests.Sum(row => (long)row.Count) > GrinderExchangeRequest.MaximumItems
            || requests.Select(row => row.AccountItemId).Distinct().Count() != requests.Count)
            throw new EconomyRejectedException("Invalid scrap selection.");
        var consumed = new List<OwnedAccountItem>();
        ulong total = 0;
        foreach (var request in requests)
        {
            if (!_catalog.TryGetSkin(request.AccountItemId, out var skin) || !skin.CanScrap)
                throw new EconomyRejectedException("This cosmetic cannot be scrapped.");
            int start = consumed.Count;
            ConsumeCopies(draft, request.AccountItemId, request.Count, consumed);
            if (consumed.Skip(start).Any(item => !item.Scrappable))
                throw new EconomyRejectedException("This owned copy cannot be scrapped.");
            total += (ulong)skin.ScrapValue * request.Count;
            if (total > AccountEconomyStore.MaximumValue)
                throw new EconomyRejectedException("Scrap reward is outside the supported range.");
        }
        draft.Credit(1, (uint)total);
        return JsonSerializer.Serialize(new EconomyAwardReceipt(consumed, [],
            new Dictionary<uint, long> { [1] = (long)total }));
    });

    public AccountEconomyResult OpenCrates(string accountId, string operationId,
        IReadOnlyList<AccountRewardRow> requests, Func<int, int>? next = null) =>
        store.Execute(accountId, operationId, "open-crates", draft =>
        {
            if (requests.Count == 0 || requests.Any(row => row.Count == 0)
                || requests.Sum(row => (long)row.Count) > 100)
                throw new EconomyRejectedException("Invalid crate opening quantity.");
            var consumed = new List<OwnedAccountItem>();
            var granted = new List<OwnedAccountItem>();
            ulong totalCost = 0;
            foreach (var request in requests)
            {
                if (!_catalog.TryGetCrate(request.AccountItemId, out EconomyCrate crate) || crate.Rewards.Count == 0)
                    throw new EconomyRejectedException("This crate is not available.");
                totalCost += (ulong)crate.CrownsCost * request.Count;
                if (totalCost > AccountEconomyStore.MaximumValue)
                    throw new EconomyRejectedException("Crate cost is outside the supported range.");
                ConsumeCopies(draft, crate.ItemId, request.Count, consumed);
                if (crate.KeyItemId != 0)
                    ConsumeCopies(draft, crate.KeyItemId, request.Count, consumed);
                for (uint i = 0; i < request.Count; i++)
                {
                    EconomyReward reward = EconomyCatalog.Roll(crate.Rewards, next);
                    foreach (var item in EconomyCatalog.ExpandReward(reward))
                    {
                        EconomySkin skin = _catalog.Skins[item.AccountItemId];
                        granted.Add(draft.Grant(item.AccountItemId, item.RewardItemId, item.Count,
                            $"crate:{crate.ItemId}", skin.CanScrap));
                    }
                }
            }
            draft.Debit(4, (uint)totalCost);
            return JsonSerializer.Serialize(new EconomyAwardReceipt(consumed, granted,
                new Dictionary<uint, long> { [4] = -(long)totalCost }));
        });

    public static void ConsumeCopies(AccountEconomyDraft draft, uint itemId, uint count,
        List<OwnedAccountItem> consumed)
    {
        uint remaining = count;
        foreach (var item in draft.Items.Where(item => item.AccountItemId == itemId).OrderBy(item => item.InstanceId))
        {
            uint take = Math.Min(item.Count, remaining);
            consumed.Add(draft.Consume(item.InstanceId, take));
            remaining -= take;
            if (remaining == 0) return;
        }
        throw new EconomyRejectedException("The account does not own enough copies.");
    }
}
