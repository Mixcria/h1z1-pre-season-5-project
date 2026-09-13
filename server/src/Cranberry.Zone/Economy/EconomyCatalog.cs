using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Cranberry.Zone.Economy;

/// <summary>An owned cosmetic and its appearance item, or a native emote using its own item ID.</summary>
public sealed record EconomySkin(uint AccountItemId, uint RewardItemId, string Name,
    uint RarityId, int ScrapValue, bool CanScrap, uint NameLocaleId = 0, uint ImageSetId = 0);

public sealed record EconomyRewardItem(uint AccountItemId, uint RewardItemId, uint Count);
public sealed record EconomyReward(uint AccountItemId, uint RewardItemId, uint Count, uint Weight,
    IReadOnlyList<EconomyRewardItem>? AdditionalItems = null, uint SourceItemId = 0)
{
    public bool Equals(EconomyReward? other) => other is not null
        && AccountItemId == other.AccountItemId && RewardItemId == other.RewardItemId
        && Count == other.Count && Weight == other.Weight && SourceItemId == other.SourceItemId
        && (AdditionalItems ?? []).SequenceEqual(other.AdditionalItems ?? []);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(AccountItemId); hash.Add(RewardItemId); hash.Add(Count); hash.Add(Weight); hash.Add(SourceItemId);
        foreach (var item in AdditionalItems ?? []) hash.Add(item);
        return hash.ToHashCode();
    }
}

public sealed record EconomyCrate(uint ItemId, string Name, uint RewardSetId,
    uint UnlockedItemId, uint CrownsCost, IReadOnlyList<EconomyReward> Rewards, string Provenance,
    uint UnlockBundleId = 0, uint NameLocaleId = 0, uint ImageSetId = 0, uint RarityId = 0,
    uint KeyItemId = 0);

/// <summary>
/// Validated account economy data. The generated data records August item provenance separately
/// from adopted emulator reward weights and explicit server design values. Cosmetic ownership
/// always uses AccountItemId; RewardItemId is only the appearance projected into a match.
/// </summary>
public sealed class EconomyCatalog
{
    public static EconomyCatalog Default { get; } = EconomyCatalogData.Create();

    public IReadOnlyDictionary<uint, EconomySkin> Skins { get; }
    public IReadOnlyDictionary<uint, EconomyCrate> Crates { get; }
    public IReadOnlyList<EconomyReward> ScrapyardRewards { get; }
    public uint ScrapyardCost { get; }
    public string ScrapyardProvenance { get; }

    public EconomyCatalog(IEnumerable<EconomySkin> skins, IEnumerable<EconomyCrate> crates,
        IEnumerable<EconomyReward> scrapyardRewards, uint scrapyardCost, string scrapyardProvenance)
    {
        EconomySkin[] skinRows = skins.ToArray();
        if (skinRows.Any(s => s.AccountItemId == 0 || s.RewardItemId == 0
            || (s.CanScrap && s.ScrapValue <= 0)))
            throw new InvalidDataException("Invalid owned cosmetic or scrap payout.");
        Skins = skinRows.ToFrozenDictionary(s => s.AccountItemId);
        EconomyCrate[] crateRows = crates.Select(c => c with
        {
            Rewards = FreezeRewards(c.Rewards),
        }).ToArray();
        if (crateRows.Any(c => c.ItemId == 0 || c.RewardSetId == 0 || c.UnlockedItemId == 0))
            throw new InvalidDataException("A crate is missing its August item/reward-set mapping.");
        if (crateRows.Any(c => c.KeyItemId != 0 && (c.CrownsCost != 0 || c.UnlockBundleId != 0)))
            throw new InvalidDataException("A legacy key opening cannot also charge Crowns.");
        foreach (EconomyCrate crate in crateRows)
            ValidateRewards(crate.Rewards, crate.Name);
        Crates = crateRows.ToFrozenDictionary(c => c.ItemId);
        ScrapyardRewards = FreezeRewards(scrapyardRewards);
        ValidateRewards(ScrapyardRewards, "Scrapyard");
        if (scrapyardCost == 0)
            throw new InvalidDataException("The Scrap-funded exchange requires a positive cost.");
        ScrapyardCost = scrapyardCost;
        ScrapyardProvenance = scrapyardProvenance;
    }

    public bool TryGetSkin(uint accountItemId, out EconomySkin skin) =>
        Skins.TryGetValue(accountItemId, out skin!);

    public bool TryGetCrate(uint crateItemId, out EconomyCrate crate) =>
        Crates.TryGetValue(crateItemId, out crate!);

    private static IReadOnlyList<EconomyReward> FreezeRewards(IEnumerable<EconomyReward> rewards) =>
        Array.AsReadOnly(rewards.Select(reward => reward.AdditionalItems is null ? reward : reward with
        {
            AdditionalItems = Array.AsReadOnly(reward.AdditionalItems.ToArray()),
        }).ToArray());

    /// <summary>A bundled reward is one weighted draw granting all of its constituent items.</summary>
    public static IEnumerable<EconomyRewardItem> ExpandReward(EconomyReward reward)
    {
        yield return new(reward.AccountItemId, reward.RewardItemId, reward.Count);
        foreach (var item in reward.AdditionalItems ?? []) yield return item;
    }

    public static IReadOnlyList<AccountRewardRow> PreviewRewards(EconomyCrate crate) =>
        crate.Rewards.SelectMany(ExpandReward).DistinctBy(item => item.AccountItemId)
            .Select(item => new AccountRewardRow(item.AccountItemId, item.Count)).ToArray();

    private void ValidateRewards(IReadOnlyList<EconomyReward> rewards, string label)
    {
        if (rewards.Count == 0 || rewards.Select(r => r.AccountItemId).Distinct().Count() != rewards.Count)
            throw new InvalidDataException($"{label}: empty or duplicate reward pool.");
        long total = 0;
        foreach (EconomyReward reward in rewards)
        {
            var contents = ExpandReward(reward).ToArray();
            if (reward.Weight == 0 || contents.Select(item => item.AccountItemId).Distinct().Count() != contents.Length
                || contents.Any(item => item.Count == 0 || !Skins.TryGetValue(item.AccountItemId, out var skin)
                    || skin.RewardItemId != item.RewardItemId))
                throw new InvalidDataException($"{label}: invalid owned cosmetic reward {reward.AccountItemId}.");
            total += reward.Weight;
        }
        if (total > int.MaxValue)
            throw new InvalidDataException($"{label}: reward weights exceed the supported range.");
    }

    /// <summary>Server-side draw. Inject an integer ticket for repeatable boundary tests.</summary>
    public static EconomyReward Roll(IReadOnlyList<EconomyReward> rewards, Func<int, int>? next = null)
    {
        if (rewards.Count == 0 || rewards.Any(r => r.Weight == 0))
            throw new ArgumentException("A draw needs positive reward weights.", nameof(rewards));
        int total = checked((int)rewards.Sum(r => (long)r.Weight));
        int ticket = next is null ? RandomNumberGenerator.GetInt32(total) : next(total);
        if ((uint)ticket >= (uint)total)
            throw new ArgumentOutOfRangeException(nameof(next), "The random ticket is outside the pool.");
        foreach (EconomyReward reward in rewards)
        {
            if (ticket < reward.Weight)
                return reward;
            ticket -= checked((int)reward.Weight);
        }
        throw new InvalidOperationException("Reward weights changed during a draw.");
    }
}
