using System.Security.Cryptography;

namespace Cranberry.Launcher.Core;

public static partial class LootReloadFix
{
    // FUN_14158cd30 is called only by the F dispatcher. R14D is already 0 or 1.
    // XOR R14B,1 -> OR R14B,1 permits this physical press without generating input
    // or bypassing inventory validation. Native timer/checksum bookkeeping is retained.
    // Provenance: tools/client/interaction_throttle_evidence.py.
    public const long ThrottleRva = 0x158CD30;
    public const int ThrottleSize = 283, ThrottleIndex = 0x96;
    public const string ThrottleSha256 = "072CC6DDB6F80D14F6817DBD1B95E56CFA1D90858DA7266E301351C073F133D4";

    private sealed record Site(long Rva, int Index, byte Original, byte Patched);
    private static readonly Site[] Sites =
    [
        new(FunctionRva, CancelIndex, 0x77, 0xEB),
        new(FunctionRva, AcknowledgementIndex, 0x75, 0xEB),
        new(ThrottleRva, ThrottleIndex, 0xF6, 0xCE),
    ];

    public static bool ClassifyThrottle(ReadOnlySpan<byte> function)
    {
        if (function.Length != ThrottleSize || function[ThrottleIndex] is not (0xF6 or 0xCE))
            throw new InvalidDataException("Unrecognized August F throttle function.");
        byte[] original = function.ToArray();
        original[ThrottleIndex] = 0xF6;
        if (Convert.ToHexString(SHA256.HashData(original)) != ThrottleSha256)
            throw new InvalidDataException("August F throttle code differs from the reviewed build.");
        return function[ThrottleIndex] == 0xCE;
    }

    /// <summary>Validate both regions before writing; roll back all edits to the exact prior version.</summary>
    public static bool ApplyAllVerified(Func<long, int, byte[]> read,
        Action<long, int, byte, byte> write, bool restore = false)
    {
        var before = new Dictionary<long, byte[]>
        {
            [FunctionRva] = read(FunctionRva, FunctionSize).ToArray(),
            [ThrottleRva] = read(ThrottleRva, ThrottleSize).ToArray(),
        };
        Classify(before[FunctionRva]);
        ClassifyThrottle(before[ThrottleRva]);
        var expected = before.ToDictionary(p => p.Key, p => p.Value.ToArray());
        var attempted = new List<Site>();
        void Verify(Dictionary<long, byte[]> regions)
        {
            foreach (var region in regions)
                if (!read(region.Key, region.Value.Length).AsSpan().SequenceEqual(region.Value))
                    throw new InvalidDataException("Interaction code changed during verification.");
        }
        try
        {
            foreach (Site site in restore ? Sites.Reverse() : Sites)
            {
                byte target = restore ? site.Original : site.Patched;
                byte from = expected[site.Rva][site.Index];
                if (from == target) continue;
                Verify(expected);
                attempted.Add(site); // The native writer may change a byte before cleanup fails.
                write(site.Rva, site.Index, from, target);
                expected[site.Rva][site.Index] = target;
                Verify(expected);
            }
            Verify(expected);
            return attempted.Count != 0;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (Site site in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    byte[] current = read(site.Rva, before[site.Rva].Length);
                    if (site.Rva == FunctionRva) Classify(current);
                    else ClassifyThrottle(current);
                    byte target = before[site.Rva][site.Index];
                    if (current[site.Index] != target) write(site.Rva, site.Index, current[site.Index], target);
                }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            try { Verify(before); }
            catch (Exception rollback) { errors.Add(rollback); }
            if (errors.Count > 1) throw new AggregateException("Interaction update and rollback failed.", errors);
            throw;
        }
    }
}
