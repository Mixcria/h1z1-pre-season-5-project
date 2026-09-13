namespace Cranberry.Zone.Progression;

/// <summary>Account levels, independent of the competitive season's rank and score.</summary>
public sealed class ExperienceCurve
{
    // Local progression tuning, not a recovered retail table. Advancing from level L
    // costs 2,000 * L XP, so every later level takes more work than the preceding one.
    // Retail provenance and the replacement-table contract: docs/experience-20260906.md.
    public static ExperienceCurve Default { get; } = new(
        Enumerable.Range(1, 100).Select(level => checked(1_000u * (uint)level * (uint)(level - 1))).ToArray());

    public IReadOnlyList<uint> Thresholds { get; }
    public uint MaximumLevel => (uint)Thresholds.Count;

    public ExperienceCurve(IReadOnlyList<uint> thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (thresholds.Count is < 2 or > 1000 || thresholds[0] != 0)
            throw new ArgumentException("An experience curve starts at zero and contains 2 to 1000 levels.", nameof(thresholds));
        for (int i = 1; i < thresholds.Count; i++)
            if (thresholds[i] <= thresholds[i - 1] || thresholds[i] > int.MaxValue)
                throw new ArgumentException("Experience thresholds must increase within the client's signed integer range.", nameof(thresholds));
        Thresholds = Array.AsReadOnly(thresholds.ToArray());
    }

    public ExperienceProgress At(uint total)
    {
        if (total > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(total));
        int index = 0;
        while (index + 1 < Thresholds.Count && total >= Thresholds[index + 1]) index++;
        uint percent = index + 1 == Thresholds.Count ? 100u
            : (uint)((ulong)(total - Thresholds[index]) * 100 / (Thresholds[index + 1] - Thresholds[index]));
        return new(total, (uint)index + 1, percent);
    }

    public uint Seed(uint experience, uint level) => Math.Max(Math.Min(experience, int.MaxValue),
        Thresholds[(int)Math.Clamp(level, 1, MaximumLevel) - 1]);
}

public sealed record ExperienceProgress(uint Total, uint Level, uint Percent);
