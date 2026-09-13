using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Cranberry.Zone.Economy;

/// <summary>A durable owned account item, separate from physical match inventory and outfit presets.</summary>
public sealed record OwnedAccountItem(
    ulong InstanceId,
    uint AccountItemId,
    uint RewardItemId,
    uint Count,
    string Source,
    bool Scrappable = true,
    uint ItemType = 0);

/// <summary>Explicit one-time development or migration grants, used only when the account is absent.</summary>
public sealed record AccountEconomySeed(
    IReadOnlyDictionary<uint, uint>? Balances = null,
    IReadOnlyList<OwnedAccountItem>? Items = null);

/// <summary>A committed operation's original result. The operation ID is supplied by server logic.</summary>
public sealed record EconomyOperationReceipt(
    string OperationId,
    string Kind,
    string? ResultJson,
    long Revision,
    DateTimeOffset CommittedUtc);

/// <summary>Detached, immutable views: callers cannot modify the store through a returned snapshot.</summary>
public sealed class AccountEconomySnapshot
{
    public string AccountId { get; }
    public long Revision { get; }
    public IReadOnlyDictionary<uint, uint> Balances { get; }
    public IReadOnlyList<OwnedAccountItem> Items { get; }
    public IReadOnlyDictionary<string, string> States { get; }
    public IReadOnlyDictionary<string, EconomyOperationReceipt> Receipts { get; }

    internal AccountEconomySnapshot(EconomyAccountFile file)
    {
        AccountId = file.AccountId;
        Revision = file.Revision;
        Balances = new ReadOnlyDictionary<uint, uint>(new Dictionary<uint, uint>(file.Balances));
        Items = Array.AsReadOnly(file.Items.Values.OrderBy(item => item.InstanceId).ToArray());
        States = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(file.States, StringComparer.Ordinal));
        Receipts = new ReadOnlyDictionary<string, EconomyOperationReceipt>(new Dictionary<string, EconomyOperationReceipt>(file.Receipts, StringComparer.Ordinal));
    }

    public uint Balance(uint currencyId) => Balances.GetValueOrDefault(currencyId);
    public bool Owns(uint accountItemId) => Items.Any(item => item.AccountItemId == accountItemId && item.Count > 0);
}

public sealed record AccountEconomyResult(
    bool Succeeded,
    bool Replayed,
    string? Error,
    AccountEconomySnapshot? Snapshot,
    EconomyOperationReceipt? Receipt);

/// <summary>A rejected business action; all draft changes are discarded.</summary>
public sealed class EconomyRejectedException(string message) : Exception(message);

/// <summary>Storage was unavailable or invalid. Callers must not substitute funded fresh state.</summary>
public sealed class AccountEconomyStoreException(string message, Exception? inner = null) : Exception(message, inner);

public enum EconomyPersistenceStage
{
    BeforeWrite,
    AfterFlushBeforeReplace,
}

/// <summary>
/// A private transaction draft. Domain handlers perform their eligibility/catalogue checks, then
/// combine balance changes, consumption, grants and backing state in this one durable transaction.
/// Retaining a draft after its callback returns cannot mutate a committed account.
/// </summary>
public sealed class AccountEconomyDraft
{
    private readonly EconomyAccountFile _file;
    private int _mutations;
    internal AccountEconomyDraft(EconomyAccountFile file) => _file = file;

    public IReadOnlyList<OwnedAccountItem> Items => Array.AsReadOnly(_file.Items.Values.ToArray());
    public uint Balance(uint currencyId) => _file.Balances.GetValueOrDefault(currencyId);
    public bool Owns(uint accountItemId) => _file.Items.Values.Any(item => item.AccountItemId == accountItemId && item.Count > 0);
    public OwnedAccountItem? GetItem(ulong instanceId) => _file.Items.GetValueOrDefault(instanceId);
    public string? GetState(string key) => _file.States.GetValueOrDefault(key);

    public void Debit(uint currencyId, uint amount)
    {
        BeginMutation();
        ValidateCurrency(currencyId, amount);
        uint held = Balance(currencyId);
        if (held < amount) throw new EconomyRejectedException("Insufficient currency.");
        _file.Balances[currencyId] = held - amount;
    }

    public void Credit(uint currencyId, uint amount)
    {
        BeginMutation();
        ValidateCurrency(currencyId, amount);
        ulong total = (ulong)Balance(currencyId) + amount;
        if (total > AccountEconomyStore.MaximumValue)
            throw new EconomyRejectedException("Currency exceeds the supported maximum.");
        _file.Balances[currencyId] = (uint)total;
    }

    public OwnedAccountItem Grant(
        uint accountItemId, uint rewardItemId, uint count, string source,
        bool scrappable = true, uint itemType = 0)
    {
        BeginMutation();
        if (_file.Items.Count >= AccountEconomyStore.MaximumItems)
            throw new EconomyRejectedException("Account item limit reached.");
        ulong instanceId;
        do
        {
            instanceId = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(sizeof(ulong)));
            instanceId &= long.MaxValue;
        } while (instanceId == 0 || _file.Items.ContainsKey(instanceId));

        var item = new OwnedAccountItem(instanceId, accountItemId, rewardItemId, count, source, scrappable, itemType);
        AccountEconomyStore.ValidateItem(item);
        _file.Items.Add(instanceId, item);
        return item;
    }

    /// <summary>Consume the indicated owned copies. Scrappability and operation eligibility remain domain rules.</summary>
    public OwnedAccountItem Consume(ulong instanceId, uint count)
    {
        BeginMutation();
        if (count == 0 || count > AccountEconomyStore.MaximumValue)
            throw new EconomyRejectedException("Invalid item quantity.");
        if (!_file.Items.TryGetValue(instanceId, out OwnedAccountItem? item) || item.Count < count)
            throw new EconomyRejectedException("The account does not own the requested item quantity.");
        if (item.Count == count) _file.Items.Remove(instanceId);
        else _file.Items[instanceId] = item with { Count = item.Count - count };
        return item with { Count = count };
    }

    /// <summary>Bounded domain state, for example a JSON backing record keyed by authoritative match identity.</summary>
    public void SetState(string key, string value)
    {
        BeginMutation();
        AccountEconomyStore.ValidateText(key, 256, "State key");
        AccountEconomyStore.ValidateText(value, AccountEconomyStore.MaximumPayloadLength, "State value", allowEmpty: true);
        if (!_file.States.ContainsKey(key) && _file.States.Count >= AccountEconomyStore.MaximumStates)
            throw new EconomyRejectedException("Account state limit reached.");
        long size = _file.States.Sum(pair => (long)pair.Key.Length + pair.Value.Length);
        if (_file.States.TryGetValue(key, out string? previous)) size -= key.Length + previous.Length;
        if (size + key.Length + value.Length > AccountEconomyStore.MaximumTextLength)
            throw new EconomyRejectedException("Account state payload limit reached.");
        _file.States[key] = value;
    }

    public void RemoveState(string key)
    {
        BeginMutation();
        _file.States.Remove(key);
    }
    public void Reject(string reason) => throw new EconomyRejectedException(reason);

    private void BeginMutation()
    {
        if (++_mutations > AccountEconomyStore.MaximumMutationsPerOperation)
            throw new EconomyRejectedException("Transaction mutation limit reached.");
    }

    private void ValidateCurrency(uint currencyId, uint amount)
    {
        if (currencyId == 0 || amount > AccountEconomyStore.MaximumValue)
            throw new EconomyRejectedException("Invalid currency amount or ID.");
        if (!_file.Balances.ContainsKey(currencyId) && _file.Balances.Count >= AccountEconomyStore.MaximumCurrencies)
            throw new EconomyRejectedException("Currency type limit reached.");
    }
}

internal sealed class EconomyAccountFile
{
    [JsonRequired]
    public int FormatVersion { get; set; } = 1;
    [JsonRequired]
    public string AccountId { get; set; } = string.Empty;
    [JsonRequired]
    public long Revision { get; set; }
    [JsonRequired]
    public Dictionary<uint, uint> Balances { get; set; } = [];
    [JsonRequired]
    public Dictionary<ulong, OwnedAccountItem> Items { get; set; } = [];
    [JsonRequired]
    public Dictionary<string, string> States { get; set; } = new(StringComparer.Ordinal);
    [JsonRequired]
    public Dictionary<string, EconomyOperationReceipt> Receipts { get; set; } = new(StringComparer.Ordinal);

    public EconomyAccountFile Clone() => new()
    {
        FormatVersion = FormatVersion,
        AccountId = AccountId,
        Revision = Revision,
        Balances = new(Balances),
        Items = new(Items),
        States = new(States, StringComparer.Ordinal),
        Receipts = new(Receipts, StringComparer.Ordinal),
    };
}
