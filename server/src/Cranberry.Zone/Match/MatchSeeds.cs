namespace Cranberry.Zone.Match;

/// <summary>
/// <b>One match seed, many independent streams.</b> docs/48 §5.1.
///
/// <para>Before this type there was no such thing as a match seed: the gas invented
/// <c>state.Guid ^ DateTime.UtcNow.Ticks</c> inside <c>StartGas</c> — nineteen lines <i>after</i>
/// the drop had already gone on the wire — and the loot used the process-wide
/// <c>ZoneOptions.LootSeed</c>. The drop has to be decided from the same match that draws the ring,
/// and it has to replay from a logged number, so both need a seed that exists before either system
/// runs.</para>
///
/// <para><b>Why salted sub-seeds and not one shared stream.</b> If the gas and the drop drew from a
/// single <see cref="Gas.GasRandom"/>, adding or removing one draw in either would silently move
/// the other — a frozen-expectation test in one lane would break in the next. <see cref="For"/>
/// gives each consumer its own 64-bit start, mixed hard enough that neighbouring match seeds do not
/// produce neighbouring sub-seeds.</para>
///
/// <para>DESIGNED, not derived: the client has no match seed and never sees one. The only thing the
/// wire carries is the consequence — a position, a circle.</para>
/// </summary>
public static class MatchSeeds
{
    /// <summary>Salt for the gas schedule's stream — ASCII <c>"GAS\0"</c>, twice.</summary>
    public const ulong GasSalt = 0x4741_5300_4741_5300UL;

    /// <summary>Salt for the drop's stream — ASCII <c>"DROP"</c> with a version tail.</summary>
    public const ulong DropSalt = 0x4452_4F50_0000_0001UL;

    /// <summary>Salt for a per-match loot layout, for whenever the loot lane stops using a host-wide seed.</summary>
    public const ulong LootSalt = 0x4C4F_4F54_0000_0001UL;

    /// <summary>
    /// The sub-seed one system draws from. SplitMix64's finalizer over <c>matchSeed ^ salt</c>:
    /// the same avalanche <see cref="Gas.GasRandom"/> itself uses, so two match seeds one apart
    /// give two unrelated sub-seeds and no consumer has to re-mix before its first draw.
    /// </summary>
    public static ulong For(ulong matchSeed, ulong salt)
    {
        unchecked
        {
            ulong z = matchSeed ^ salt;
            z += 0x9E37_79B9_7F4A_7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// The match seed itself: <paramref name="configured"/> when a host or a test has pinned one
    /// (<c>CRANBERRY_MATCH_SEED</c>), otherwise a fresh one per match. The fresh expression is
    /// exactly the one the gas used before this type existed — session guid mixed with the wall
    /// clock — so nothing about the ring's statistics changes, only when it is drawn.
    /// <para>
    /// Never returns 0, because 0 is the "not pinned" sentinel and a match must be able to log and
    /// replay its own seed.
    /// </para>
    /// </summary>
    public static ulong Draw(ulong configured, ulong sessionGuid, long utcTicks)
    {
        if (configured != 0)
        {
            return configured;
        }

        unchecked
        {
            ulong drawn = sessionGuid ^ (ulong)utcTicks;
            return drawn == 0 ? 0x9E37_79B9_7F4A_7C15UL : drawn;
        }
    }

    /// <summary>The 16-digit hex a match logs, and the value <c>CRANBERRY_MATCH_SEED</c> takes back.</summary>
    public static string Format(ulong matchSeed) => matchSeed.ToString("x16");

    /// <summary>Reads <see cref="Format"/>'s output, with or without a <c>0x</c> prefix.</summary>
    public static bool TryParse(string? text, out ulong matchSeed)
    {
        matchSeed = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out matchSeed);
    }
}
