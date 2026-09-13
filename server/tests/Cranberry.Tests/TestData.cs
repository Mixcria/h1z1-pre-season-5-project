namespace Cranberry.Tests;

internal static class TestData
{
    public static string DynamicAppearanceSource { get; } =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "dynamicAppearance.bin");

    public static string? AccountCratesReference =>
        Environment.GetEnvironmentVariable("CRANBERRY_ACCOUNT_CRATES_REFERENCE");

    public static string? ClientPackIndex =>
        Environment.GetEnvironmentVariable("CRANBERRY_CLIENT_PACK_INDEX");
}

public sealed class ClientPackIndexTheoryAttribute : TheoryAttribute
{
    public ClientPackIndexTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(TestData.ClientPackIndex))
            Skip = "Set CRANBERRY_CLIENT_PACK_INDEX to an external pack-index-aug.tsv to compare texture names with the game client.";
    }
}

public sealed class AccountCratesReferenceFactAttribute : FactAttribute
{
    public AccountCratesReferenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(TestData.AccountCratesReference))
            Skip = "Set CRANBERRY_ACCOUNT_CRATES_REFERENCE to an external AccountCrates.json to compare its original reward pools.";
    }
}
