using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cranberry.Launcher.Core;

/// <summary>
/// Completes authoritative retirement of August physics projectiles without a network NPC.
/// Native FireRejected marks removal, but the physics-start callback's pending-NPC bit
/// otherwise prevents the projectile pump from deleting the actor until its lifespan ends.
/// </summary>
public static partial class ThrowableCleanupFix
{
    public const long FunctionRva = 0xB07010;
    public const int FunctionSize = 3164;
    public const int PatchIndex = 0x908;
    public const string FunctionSha256 = "8F15BBD6A5F58EF7EE3BC65D5223F76D982365D40AB43C016FF01B5365B2BE4C";
    public const string DisableVariable = "CRANBERRY_THROWABLE_CLEANUP";
    private static ReadOnlySpan<byte> OriginalEntry => [0x80, 0x88, 0x51, 0x06, 0, 0, 0x40];

    internal static byte[] BuildEntry(long image, long stub)
    {
        byte[] bytes = [0xE9, 0, 0, 0, 0, 0x90, 0x90];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), checked((int)(stub - (image + FunctionRva + PatchIndex + 5))));
        return bytes;
    }

    internal static byte[] BuildStub(long image, long address)
    {
        // RAX is the projectile already selected by the server's counted id list.
        // Preserve all registers. The next native OR overwrites condition flags.
        var code = new List<byte>(37);
        code.AddRange(OriginalEntry.ToArray()); // original remove marker: [rax+0x651] |= 0x40
        code.AddRange([0x83, 0xB8, 0x0C, 0x04, 0, 0, 0x09]); // FlightType == Physics (9)
        code.AddRange([0x75, 0x10]);
        code.AddRange([0x83, 0xB8, 0x18, 0x05, 0, 0, 0x00]); // NPCDefinitionId == 0
        code.AddRange([0x75, 0x07]);
        code.AddRange([0x80, 0xA0, 0x52, 0x06, 0, 0, 0xDF]); // clear only pending-NPC bit 0x20
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(checked((int)(image + FunctionRva + PatchIndex + 7 - (address + code.Count + 4)))));
        return code.ToArray(); // resume original rejected marker [rax+0x652] |= 0x10
    }

    /// <returns>Zero for the original function; otherwise the installed stub address.</returns>
    internal static long Inspect(ReadOnlySpan<byte> function, long image, Func<long, int, byte[]> read)
    {
        if (function.Length != FunctionSize) throw new InvalidDataException("Unexpected August FireRejected function length.");
        byte[] normalized = function.ToArray();
        ReadOnlySpan<byte> entry = function.Slice(PatchIndex, 7);
        bool original = entry.SequenceEqual(OriginalEntry);
        if (!original && (entry[0] != 0xE9 || entry[5] != 0x90 || entry[6] != 0x90))
            throw new InvalidDataException("Unknown retirement hook; no throwable change applied.");
        OriginalEntry.CopyTo(normalized.AsSpan(PatchIndex));
        if (Convert.ToHexString(SHA256.HashData(normalized)) != FunctionSha256)
            throw new InvalidDataException("August FireRejected function differs from the reviewed build.");
        if (original) return 0;
        long stub = image + FunctionRva + PatchIndex + 5 + BinaryPrimitives.ReadInt32LittleEndian(entry[1..]);
        byte[] expected = BuildStub(image, stub);
        if (!read(stub, expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Existing retirement hook is not the throwable cleanup fix.");
        return stub;
    }
}
