using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cranberry.Zone.Economy;

public sealed record EconomyPurchaseOffer(EconomyStoreBundle Bundle, EconomyCrate? LockedCrate = null);
public sealed record EconomyQuotedLine(uint BundleId, uint Quantity, uint CurrencyId, uint UnitPrice,
    uint ContentItemId, uint LockedCrateItemId);
public sealed record EconomyOrderQuote(uint Result, uint Total, string? Error,
    IReadOnlyList<EconomyQuotedLine> Lines)
{
    public bool Allowed => Result == EconomyOrderResponse.Success;
}

public sealed record EconomyPurchaseReceipt(string OrderId, string Fingerprint, uint TrackId, uint Total,
    string TableVersion, IReadOnlyList<EconomyQuotedLine> Lines, EconomyAwardReceipt Award);

/// <summary>
/// Local-currency commerce only. Bundle IDs, prices and ownership come from the server catalogue.
/// A Crown unlock consumes a locked crate and grants its unlocked definition; the later free-open
/// request awards the cosmetic. Scrap bundle 244 draws its cosmetic immediately;
/// Skull Store orders grant the selected cosmetic directly.
/// </summary>
public sealed class EconomyPurchaseService
{
    public const uint ScrapyardBundleId = 244; // August GrinderWindow.GRINDER_BUNDLE_ID
    public const uint ScrapyardContentItemId = 3806; // August Scrap Conversion, GiveRewardSet 5320
    public const string TableVersion = "menu-purchases-v2";
    private readonly AccountEconomyStore _store;
    private readonly EconomyCatalog _catalog;
    public IReadOnlyDictionary<uint, EconomyPurchaseOffer> Offers { get; }

    public EconomyPurchaseService(AccountEconomyStore store, EconomyCatalog? catalog = null)
    {
        _store = store;
        _catalog = catalog ?? EconomyCatalog.Default;
        var offers = new Dictionary<uint, EconomyPurchaseOffer>
        {
            // Bundle ID and displayed price are client facts. Mapping its content to existing
            // Scrap Conversion 3806 is an explicit server data choice, not a captured bundle row.
            [ScrapyardBundleId] = new(new(ScrapyardBundleId, "Scrapyard", 1, _catalog.ScrapyardCost,
                ScrapyardContentItemId, 1)),
        };
        foreach (EconomyCrate crate in _catalog.Crates.Values.Where(c => c.CrownsCost > 0 && c.UnlockBundleId > 0))
        {
            if (!_catalog.Crates.TryGetValue(crate.UnlockedItemId, out var unlocked) || unlocked.CrownsCost != 0)
                throw new InvalidDataException("A Crown unlock must lead to a configured free-open crate.");
            if (!offers.TryAdd(crate.UnlockBundleId,
                new(new(crate.UnlockBundleId, crate.Name, 4, crate.CrownsCost, crate.UnlockedItemId,
                    EconomyStoreBundle.MaximumCrateQuantity), crate)))
                throw new InvalidDataException("Ambiguous purchase bundle mapping.");
        }
        foreach (var offer in SkullStoreCatalog.Offers(_catalog))
            if (!offers.TryAdd(offer.Bundle.BundleId, offer))
                throw new InvalidDataException("Ambiguous Skull Store bundle mapping.");
        Offers = offers.ToFrozenDictionary();
    }

    /// <summary>Read-only quote; cancelling a local client order before Place sends no purchase.</summary>
    public EconomyOrderQuote Quote(string accountId, ulong characterGuid, EconomyOrderRequest request)
    {
        AccountEconomySnapshot account = _store.GetOrCreate(accountId);
        return Quote(characterGuid, request, account.Balance, account.Items);
    }

    public AccountEconomyResult Place(string accountId, string linkId, ulong characterGuid,
        EconomyOrderRequest request, Func<int, int>? next = null)
    {
        if (request.SubOpcode != EconomyOrderRequest.Place)
            return new(false, false, "Only PlaceOrder can commit a purchase.", _store.GetOrCreate(accountId), null);
        if (string.IsNullOrWhiteSpace(linkId) || linkId.Length > 64)
            throw new ArgumentException("A server-issued connection namespace is required.", nameof(linkId));
        string operation = $"purchase:{linkId}:{request.TrackId}";
        string fingerprint = request.Fingerprint();
        return _store.Execute(accountId, operation, "purchase:" + fingerprint, draft =>
        {
            EconomyOrderQuote quote = Quote(characterGuid, request, draft.Balance, draft.Items);
            if (!quote.Allowed) draft.Reject(quote.Error ?? "Order is unavailable.");
            var consumed = new List<OwnedAccountItem>();
            var granted = new List<OwnedAccountItem>();
            var changes = new Dictionary<uint, long>();
            foreach (EconomyQuotedLine line in quote.Lines)
            {
                uint cost = checked(line.Quantity * line.UnitPrice);
                draft.Debit(line.CurrencyId, cost);
                changes[line.CurrencyId] = changes.GetValueOrDefault(line.CurrencyId) - cost;
                if (line.BundleId == ScrapyardBundleId)
                {
                    EconomyReward reward = EconomyCatalog.Roll(_catalog.ScrapyardRewards, next);
                    var skin = _catalog.Skins[reward.AccountItemId];
                    granted.Add(draft.Grant(reward.AccountItemId, reward.RewardItemId, reward.Count,
                        $"scrapyard:{TableVersion}", skin.CanScrap));
                }
                else if (line.CurrencyId == SkullStoreCatalog.CurrencyId)
                {
                    var skin = _catalog.Skins[line.ContentItemId];
                    granted.Add(draft.Grant(skin.AccountItemId, skin.RewardItemId, line.Quantity,
                        $"skull-store:{line.BundleId}:{TableVersion}", skin.CanScrap));
                }
                else
                {
                    uint canonical = EconomyMenuInventory.CanonicalItemId(line.LockedCrateItemId, _catalog);
                    uint remaining = line.Quantity;
                    foreach (var item in draft.Items.Where(item =>
                        EconomyMenuInventory.CanonicalItemId(item.AccountItemId, _catalog) == canonical)
                        .OrderBy(item => item.InstanceId))
                    {
                        uint take = Math.Min(remaining, item.Count);
                        consumed.Add(draft.Consume(item.InstanceId, take));
                        remaining -= take;
                        if (remaining == 0) break;
                    }
                    if (remaining != 0) draft.Reject("The account does not own enough locked crates.");
                    granted.Add(draft.Grant(line.ContentItemId, 0, line.Quantity,
                        $"crate-unlock:{line.BundleId}:{TableVersion}", scrappable: false));
                }
            }
            string orderId = Guid.NewGuid().ToString("N");
            var receipt = new EconomyPurchaseReceipt(orderId, fingerprint, request.TrackId, quote.Total,
                CatalogueVersion(), quote.Lines, new(consumed.AsReadOnly(), granted.AsReadOnly(), changes));
            string json = JsonSerializer.Serialize(receipt);
            foreach (EconomyQuotedLine line in quote.Lines)
                draft.SetState(LastResultKey(characterGuid, line.BundleId), operation);
            return json;
        });
    }

    /// <summary>
    /// Restore the committed reveal after a reconnect/restart without running PlaceOrder or drawing
    /// again. The native protocol has no persistent client order GUID: new links namespace new
    /// actions, while old committed items, balances and result records remain durable.
    /// </summary>
    public EconomyPurchaseReceipt? LastResult(string accountId, ulong characterGuid, uint bundleId)
    {
        var account = _store.GetOrCreate(accountId);
        if (!account.States.TryGetValue(LastResultKey(characterGuid, bundleId), out string? operation)) return null;
        if (!account.Receipts.TryGetValue(operation, out var receipt) || receipt.ResultJson is null)
            throw new AccountEconomyStoreException("Saved purchase result references a missing receipt.");
        try
        {
            return JsonSerializer.Deserialize<EconomyPurchaseReceipt>(receipt.ResultJson)
                ?? throw new AccountEconomyStoreException("Saved purchase result is empty.");
        }
        catch (JsonException exception)
        {
            throw new AccountEconomyStoreException("Saved purchase result is invalid.", exception);
        }
    }

    private EconomyOrderQuote Quote(ulong characterGuid, EconomyOrderRequest request,
        Func<uint, uint> balance, IReadOnlyList<OwnedAccountItem> items)
    {
        EconomyOrderQuote Reject(string error, uint result = EconomyOrderResponse.Failure) => new(result, 0, error, []);
        if (request.TrackId == 0 || characterGuid == 0
            || !ulong.TryParse(request.CharacterReference, NumberStyles.None, CultureInfo.InvariantCulture, out ulong target)
            || target != characterGuid)
            return Reject("The order does not target the admitted character.");
        if (!string.IsNullOrEmpty(request.Coupon) || !string.IsNullOrEmpty(request.GiftMessage)
            || request.Lines.Count is < 1 or > EconomyOrderRequest.MaximumLines)
            return Reject("Coupons, gifts or an empty order are not supported.");
        var seen = new HashSet<uint>();
        var totals = new Dictionary<uint, ulong>();
        var requestedCrates = new Dictionary<uint, ulong>();
        var lines = new List<EconomyQuotedLine>();
        ulong total = 0;
        foreach (EconomyOrderLine line in request.Lines)
        {
            if (!seen.Add(line.BundleId) || line.StoreId != EconomyStoreUpdate.StoreId
                || !Offers.TryGetValue(line.BundleId, out var offer)
                || line.Quantity == 0 || line.Quantity > offer.Bundle.MaximumQuantity
                || (line.Reference != "0" && line.Reference != string.Empty)
                || (line.PaymentSource != "0" && line.PaymentSource != string.Empty
                    && line.PaymentSource != EconomyOrderLine.NativeDefaultOutOfBandData))
                return Reject("Unsupported bundle, quantity or payment source.");
            string currency = offer.Bundle.CurrencyId switch
            {
                1 => "SCP",
                4 => "KH$",
                SkullStoreCatalog.CurrencyId => SkullStoreCatalog.CurrencyCode,
                _ => throw new InvalidDataException("Unsupported purchase currency."),
            };
            if (!string.Equals(request.CurrencyCode, currency, StringComparison.Ordinal))
                return Reject("The selected currency does not pay for this bundle.");
            if (offer.Bundle.CurrencyId == SkullStoreCatalog.CurrencyId
                && items.Any(item => item.AccountItemId == offer.Bundle.ContentItemId && item.Count > 0))
                return Reject("The account already owns this cosmetic.");
            if (offer.LockedCrate is { } crate)
            {
                uint canonical = EconomyMenuInventory.CanonicalItemId(crate.ItemId, _catalog);
                requestedCrates[canonical] = requestedCrates.GetValueOrDefault(canonical) + line.Quantity;
                if ((ulong)items.Where(item => EconomyMenuInventory.CanonicalItemId(item.AccountItemId, _catalog) == canonical)
                    .Sum(item => (long)item.Count) < requestedCrates[canonical])
                    return Reject("The account does not own enough locked crates.");
            }
            ulong lineTotal = (ulong)line.Quantity * offer.Bundle.Price;
            total += lineTotal;
            if (total > AccountEconomyStore.MaximumValue) return Reject("Order total exceeds its limit.");
            totals[offer.Bundle.CurrencyId] = totals.GetValueOrDefault(offer.Bundle.CurrencyId) + lineTotal;
            lines.Add(new(line.BundleId, line.Quantity, offer.Bundle.CurrencyId, offer.Bundle.Price,
                offer.Bundle.ContentItemId, offer.LockedCrate?.ItemId ?? 0));
        }
        if (totals.Any(pair => pair.Value > balance(pair.Key)))
            return Reject("Insufficient currency.", EconomyOrderResponse.InsufficientFunds);
        return new(EconomyOrderResponse.Success, (uint)total, null, lines.AsReadOnly());
    }

    private string CatalogueVersion()
    {
        string catalogue = JsonSerializer.Serialize(Offers.Values.OrderBy(offer => offer.Bundle.BundleId))
            + JsonSerializer.Serialize(_catalog.ScrapyardRewards);
        return TableVersion + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalogue))).ToLowerInvariant();
    }

    private static string LastResultKey(ulong characterGuid, uint bundleId) => $"purchase:last:{characterGuid:x16}:{bundleId}";
}
