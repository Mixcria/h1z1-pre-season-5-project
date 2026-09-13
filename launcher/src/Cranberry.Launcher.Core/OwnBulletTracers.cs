using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cranberry.Launcher.Core;

/// <summary>
/// Filters the local shooter's firearm tracer assignment in August FUN_140c7bb60.
/// The native local-projectile flag, not the camera's first-person predicate, selects
/// the owner. Other projectiles and every remote shot keep their native effect path.
/// </summary>
public static partial class OwnBulletTracers
{
    public const long FunctionRva = 0xC7BB60;
    public const int FunctionSize = 4446;
    public const int PatchIndex = 0xF26;
    public const string FunctionSha256 = "3CFFA27DCB8386D2053A7CC0A9065C207A368282783EA0E586659F3FFCB17DBB";
    public const string DisableVariable = "CRANBERRY_HIDE_OWN_BULLET_TRACERS";
    private static ReadOnlySpan<byte> OriginalEntry => [0xF6, 0x85, 0x98, 0x02, 0, 0, 0x80];

    // AugustProjectileFacts: .45, .223, 12GA, .308, .44, .380, 9mm, AK 7.62.
    // Weapon skins resolve to these same projectile definitions.
    private static ReadOnlySpan<uint> FirearmProjectiles => [19000, 19002, 70040, 70041, 70047, 70053, 70055, 70063];

    internal static byte[] BuildEntry(long image, long stub)
    {
        byte[] bytes = [0xE9, 0, 0, 0, 0, 0x90, 0x90];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), checked((int)(stub - (image + FunctionRva + PatchIndex + 5))));
        return bytes;
    }

    internal static byte[] BuildStub(long image, long address)
    {
        // Only the spawned bullet's visual-effect slot is cleared. Registers,
        // projectile definitions, ballistics and camera state are unchanged.
        // All exits land where the native path overwrites condition flags itself.
        var code = new List<byte>(106);
        code.AddRange(OriginalEntry.ToArray()); // test local-projectile bit 0x80
        code.AddRange([0x74, 0]); // jz remote
        var matches = new List<int>();
        foreach (uint id in FirearmProjectiles)
        {
            code.AddRange([0x81, 0x7F, 0x18]); // cmp dword ptr [rdi+0x18], projectile id
            code.AddRange(BitConverter.GetBytes(id));
            code.Add(0x74); // je omit local tracer assignment
            matches.Add(code.Count);
            code.Add(0);
        }
        void Jump(long target)
        {
            code.Add(0xE9);
            code.AddRange(BitConverter.GetBytes(checked((int)(target - (address + code.Count + 4)))));
        }
        Jump(image + 0xC7CAA7); // other local projectiles: original TP/FP effect selection
        int remote = code.Count;
        Jump(image + 0xC7CA8F); // remote: original visibility cvar and tracer selection
        int hidden = code.Count;
        // The .44 also supplies a base PROJECTILE_EFFECT_ID (5275). Skipping its
        // tracer override alone would leave that effect visible to the shooter.
        code.AddRange([0xC7, 0x85, 0x78, 0x01, 0, 0, 0, 0, 0, 0]);
        Jump(image + 0xC7CAD4); // retain cadence/flag handling, omit only tracer assignment
        code[8] = checked((byte)(remote - 9));
        foreach (int at in matches) code[at] = checked((byte)(hidden - (at + 1)));
        return code.ToArray();
    }

    /// <returns>Zero for the original function; otherwise the installed stub address.</returns>
    internal static long Inspect(ReadOnlySpan<byte> function, long image, Func<long, int, byte[]> read)
    {
        if (function.Length != FunctionSize) throw new InvalidDataException("Unexpected August projectile function length.");
        byte[] normalized = function.ToArray();
        ReadOnlySpan<byte> entry = function.Slice(PatchIndex, 7);
        bool original = entry.SequenceEqual(OriginalEntry);
        if (!original && (entry[0] != 0xE9 || entry[5] != 0x90 || entry[6] != 0x90))
            throw new InvalidDataException("Unknown projectile hook; no tracer change applied.");
        OriginalEntry.CopyTo(normalized.AsSpan(PatchIndex));
        if (Convert.ToHexString(SHA256.HashData(normalized)) != FunctionSha256)
            throw new InvalidDataException("August projectile function differs from the reviewed build.");
        if (original) return 0;
        long stub = image + FunctionRva + PatchIndex + 5 + BinaryPrimitives.ReadInt32LittleEndian(entry[1..]);
        byte[] expected = BuildStub(image, stub);
        if (!read(stub, expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Existing projectile hook is not the own-tracer filter.");
        return stub;
    }
}
