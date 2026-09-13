using System.Security.Cryptography;

namespace Cranberry.Launcher.Core;

/// <summary>
/// Delivers the reviewed August ForceFpScope correction from binocular-scope-live.py.
/// Only held scope entry and synthetic airborne release branches change. FOV, ammo,
/// ordinary aiming, inventory/busy gates and the final held-button check stay native.
/// See docs/binocular-airborne-scope-20260906.md; this is unrelated to Alt freelook.
/// </summary>
public static partial class BinocularScopeFix
{
    public const long FunctionRva = 0x158B700;
    public const int FunctionSize = 1551;
    public const int EntryIndex = 0x3E1, ReleaseIndex = 0x45E;
    public const string FunctionSha256 = "C2953412538EF98C84959B78BEE1A4870CC7D22008386C3C5755E7B364824053";
    public const string PatchedSha256 = "9A1B5E0634454BCB9A5D6350D3B83F224DC5EFDC76533FF3EBEBD6D839FFEF12";

    // These are all four full-function states recognized by the existing Python helper.
    // Bit 1 permits held entry; bit 2 suppresses synthetic release.
    public static int Classify(ReadOnlySpan<byte> function)
    {
        if (function.Length != FunctionSize)
            throw new InvalidDataException("SecondaryFire requires the reviewed 1,551-byte August function.");
        return Convert.ToHexString(SHA256.HashData(function)) switch
        {
            FunctionSha256 => 0,
            "0800DD7B6844351482171C025A7014A29BE9049AF359B40151F5BCBAD188CC87" => 1,
            "4BCEC25AD42E9F0F43D4BAE686F32274FC07EB2461B4907FBD5F764B97787776" => 2,
            PatchedSha256 => 3,
            _ => throw new InvalidDataException("SecondaryFire differs from the reviewed August function."),
        };
    }

    /// <summary>Two atomic byte writes, full-function guards, and exact starting-state rollback.</summary>
    public static bool ApplyVerified(Func<byte[]> read, Action<int, byte, byte> write, bool restore = false)
    {
        int initial = Classify(read());
        int target = restore ? 0 : 3;
        if (initial == target) return false;
        void Transition(int wanted)
        {
            int current = Classify(read());
            // Suppress synthetic release before enabling entry. Close entry before
            // restoring release, including rollback to a recognized partial state.
            int[] order = (wanted & 1) == 0 ? [1, 2] : [2, 1];
            foreach (int mask in order)
            {
                if ((current & mask) == (wanted & mask)) continue;
                if (Classify(read()) != current)
                    throw new InvalidDataException("SecondaryFire changed between guarded writes.");
                int index = mask == 1 ? EntryIndex : ReleaseIndex;
                byte original = mask == 1 ? (byte)0x33 : (byte)0x74;
                byte patched = mask == 1 ? (byte)0x41 : (byte)0xEB;
                write(index, (current & mask) != 0 ? patched : original,
                    (wanted & mask) != 0 ? patched : original);
                current ^= mask;
                if (Classify(read()) != current)
                    throw new InvalidDataException("SecondaryFire readback differs after a one-byte write.");
            }
            if (Classify(read()) != wanted)
                throw new InvalidDataException("SecondaryFire final verification failed.");
        }
        try { Transition(target); }
        catch (Exception failure)
        {
            // A writer may mutate memory and then report a cache/protection failure.
            // Reclassify the entire function; refuse rollback over any foreign edit.
            try { Transition(initial); }
            catch (Exception rollback)
            {
                throw new AggregateException("Scope update and rollback failed or were refused.", failure, rollback);
            }
            throw new IOException($"Scope update failed and was rolled back to state {initial}.", failure);
        }
        return true;
    }
}
