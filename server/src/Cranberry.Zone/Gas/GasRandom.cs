namespace Cranberry.Zone.Gas;

/// <summary>
/// The seedable generator that places every circle of a match. It is this project's own
/// implementation (SplitMix64 mixing) rather than <see cref="System.Random"/> so that a match
/// replays byte-for-byte from its seed on any runtime version — the schedule is compared against
/// frozen expectations in the tests and re-derived on the host from the same seed.
/// </summary>
public struct GasRandom(ulong seed)
{
    private ulong _state = seed;

    /// <summary>The next 64 raw bits.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E37_79B9_7F4A_7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A uniform value in [0, 1) built from the top 53 bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
