using System.Diagnostics.CodeAnalysis;

namespace Cranberry.Zone.Match;

/// <summary>A client option id, the currency it consumes, and its server-configured cost.</summary>
public sealed record BountyAnte(uint BountyType, uint CurrencyId, uint Amount, string Name);

/// <summary>
/// Back Your Match options. Option ids and currencies are the August UIBountyManager's
/// button contracts. Prices and placement ladders remain server design values.
/// See docs/bounty-contract-20260905.md for the native and UI evidence.
/// </summary>
public sealed record BountyOptions
{
    public const string EnabledVariable = "CRANBERRY_BOUNTY";
    public const string CurrencyVariable = "CRANBERRY_BOUNTY_CURRENCY";
    public const string AnteVariable = "CRANBERRY_BOUNTY_ANTE";
    public const string SuppressDropOpenVariable = "CRANBERRY_BOUNTY_SUPPRESS_DROP_OPEN";
    public const uint CrownsOption = 1;
    public const uint SkullsOption = 2;
    public const uint FreeCreditsOption = 3;

    public static IReadOnlyList<uint> DesignSkullPayouts { get; } =
        [500u, 300u, 200u, 150u, 100u, 75u, 50u, 40u, 30u, 25u];
    public static IReadOnlyList<uint> DesignCreditPayouts { get; } =
        [100u, 75u, 50u, 40u, 30u, 25u, 20u, 15u, 10u, 5u];

    /// <summary>
    /// 1 spends Crowns (4), 2 spends Skulls (5), 3 spends earned Credits (6).
    /// Free Bounty consumes earned credits, not Crowns/Skulls. Type 0 is unbacked.
    /// The 100-credit requirement is an explicit server design, matching native progress units.
    /// </summary>
    public static IReadOnlyList<BountyAnte> DesignAntes { get; } =
    [
        new(CrownsOption, 4u, 500u, "Crowns (hard)"),
        new(SkullsOption, 5u, 100u, "Skulls (soft)"),
        new(FreeCreditsOption, 6u, 100u, "Free Bounty (earned Credits)"),
    ];

    public bool Enabled { get; init; } = true;
    public bool Currency { get; init; } = true;
    public bool AcceptAnte { get; init; } = true;

    /// <summary>
    /// Retained configuration compatibility. Packet suppression alone never fixed the popup:
    /// ce 15 is IsInBox, true in pregame and false before the drop. Eligibility/phase checks
    /// must not be bypassed when this switch is off.
    /// </summary>
    public bool SuppressDropOpen { get; init; } = true;
    public IReadOnlyList<uint> SkullPayouts { get; init; } = DesignSkullPayouts;
    public IReadOnlyList<uint> CreditPayouts { get; init; } = DesignCreditPayouts;
    public IReadOnlyList<BountyAnte> Antes { get; init; } = DesignAntes;
    public static BountyOptions Default { get; } = new();

    /// <summary>Reject unknown option ids; they must never create free backing.</summary>
    public bool TryGetAnte(uint bountyType, [NotNullWhen(true)] out BountyAnte? ante)
    {
        ante = null;
        if (bountyType is not (CrownsOption or SkullsOption or FreeCreditsOption)) return false;
        uint expectedCurrency = bountyType + 3;
        foreach (BountyAnte candidate in Antes)
        {
            if (candidate.BountyType != bountyType) continue;
            if (ante is not null || candidate.CurrencyId != expectedCurrency
                || candidate.Amount is 0 or > int.MaxValue)
            {
                ante = null;
                return false;
            }
            ante = candidate;
        }
        return ante is not null;
    }

    public BountyAnte AnteFor(uint bountyType) => TryGetAnte(bountyType, out BountyAnte? ante)
        ? ante
        : throw new ArgumentOutOfRangeException(nameof(bountyType), "Unsupported bounty option.");

    public MatchBountyCosts Costs(bool eligible, ulong headerId = 0) => new(
        eligible && Enabled && AcceptAnte && TryGetAnte(CrownsOption, out BountyAnte? hard) ? hard.Amount : 0,
        eligible && Enabled && AcceptAnte && TryGetAnte(SkullsOption, out BountyAnte? soft) ? soft.Amount : 0,
        eligible && Enabled && AcceptAnte && TryGetAnte(FreeCreditsOption, out BountyAnte? free) ? free.Amount : 0,
        headerId);

    public BountyPayoutTable SkullTable() => new(SkullPayouts);
    public BountyPayoutTable CreditTable() => new(CreditPayouts);
    public static BountyOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        return Default with
        {
            Enabled = Switch(read, EnabledVariable, true),
            Currency = Switch(read, CurrencyVariable, true),
            AcceptAnte = Switch(read, AnteVariable, true),
            SuppressDropOpen = Switch(read, SuppressDropOpenVariable, true),
        };
    }

    public string Describe() =>
        $"bounty: {On(Enabled)}, lobby cost/state/tables 67 10/0d/0e; "
        + $"currency {On(Currency)}, backing {On(AcceptAnte)}; "
        + $"antes DESIGN ({string.Join(", ", Antes.Select(a => $"{a.BountyType}={a.Amount} {a.Name}"))}); "
        + "public Solo pregame only; ce 15 controls IsInBox, not in-match state";

    private static string On(bool value) => value ? "ON" : "off";
    private static bool Switch(Func<string, string?> read, string name, bool fallback) =>
        read(name) switch { "1" => true, "0" => false, _ => fallback };
}
