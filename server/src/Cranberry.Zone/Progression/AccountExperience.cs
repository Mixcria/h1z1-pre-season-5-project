using System.Globalization;
using System.Text.Json;
using Cranberry.Zone.Economy;

namespace Cranberry.Zone.Progression;

/// <summary>Atomic account XP commits using the same durable store as the menu economy.</summary>
public sealed class AccountExperience(AccountEconomyStore? store, ExperienceCurve curve, uint seed = 0)
{
    // August Experience.txt: ID 1, award type 1, string 13935 (Killer), XP 100.
    // The reward does not scale with the account's level; the next-level cost does.
    public const uint KillReward = 100;
    public const string StateKey = "account-experience:v1";
    private readonly object _gate = new();
    private readonly Dictionary<string, uint> _memory = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Account, string Operation), ExperienceGrant> _receipts = [];

    public ExperienceProgress Read(string accountId)
    {
        lock (_gate)
        {
            if (store is null) return curve.At(_memory.GetValueOrDefault(accountId, seed));
            var account = store.GetOrCreate(accountId);
            if (account.States.TryGetValue(StateKey, out string? value)) return curve.At(Parse(value));
            var initialized = store.Execute(accountId, StateKey, "experience-initialize", draft =>
            {
                if (draft.GetState(StateKey) is null) draft.SetState(StateKey, Format(seed));
                return null;
            });
            if (!initialized.Succeeded) throw new AccountEconomyStoreException(initialized.Error!);
            return curve.At(Parse(initialized.Snapshot!.States[StateKey]));
        }
    }

    public ExperienceGrant AwardKill(string accountId, string operationId)
    {
        lock (_gate)
        {
            if (store is null)
            {
                var key = (accountId, operationId);
                if (_receipts.TryGetValue(key, out var previous)) return previous with { Replayed = true };
                var granted = Grant(_memory.GetValueOrDefault(accountId, seed));
                _memory[accountId] = granted.After.Total;
                _receipts.Add(key, granted);
                return granted;
            }
            var result = store.Execute(accountId, operationId, "experience-kill", draft =>
            {
                var granted = Grant(draft.GetState(StateKey) is { } value ? Parse(value) : seed);
                draft.SetState(StateKey, Format(granted.After.Total));
                return JsonSerializer.Serialize(granted);
            });
            if (!result.Succeeded) throw new AccountEconomyStoreException(result.Error!);
            return JsonSerializer.Deserialize<ExperienceGrant>(result.Receipt!.ResultJson!)!
                with { Replayed = result.Replayed };
        }
    }

    private ExperienceGrant Grant(uint total)
    {
        uint next = (uint)Math.Min((ulong)total + KillReward, int.MaxValue);
        return new(curve.At(total), curve.At(next), next - total);
    }

    private static string Format(uint total) => total.ToString(CultureInfo.InvariantCulture);
    private static uint Parse(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint total)
        && total <= int.MaxValue ? total : throw new AccountEconomyStoreException("Saved account experience is invalid.");
}

public sealed record ExperienceGrant(ExperienceProgress Before, ExperienceProgress After, uint Amount, bool Replayed = false);
