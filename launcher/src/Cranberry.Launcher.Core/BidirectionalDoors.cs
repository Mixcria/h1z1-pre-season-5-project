using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cranberry.Launcher.Core;

/// <summary>Applies the server's chosen quarter-turn to the native August door controller.</summary>
public static partial class BidirectionalDoors
{
    public const int ProtocolVersion = 1;
    public const long FunctionRva = 0xC81FE0;
    public const int FunctionSize = 359;
    public const int PatchIndex = 0;
    public const int EntrySize = 5;
    public const string FunctionSha256 = "3ACD968A9B56E86283DD772BF410A3F5CE6A118958275A0117368F4EA53EB741";
    private static ReadOnlySpan<byte> OriginalEntry => [0x48, 0x89, 0x5C, 0x24, 0x18];

    internal static byte[] BuildEntry(long image, long stub)
    {
        byte[] bytes = [0xE9, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), checked((int)(stub - (image + FunctionRva + EntrySize))));
        return bytes;
    }

    internal static byte[] BuildStub(long image, long address)
    {
        // Generated from tools/client/bidirectional-doors.asm. Only two addresses
        // relocate: the exact door vtable and the continuation after the saved prolog.
        byte[] code = Convert.FromHexString("9C50415241534883EC10F30F7F0424488B4424584885C00F8484000000488B0049BB0031524F4F4452434931C34983FB01776E41F6400601746741F64106017560F681DE18000001755783B948430000007E4E4C8B91100D00004D85D2744249BB88776655443322114D391A753349394A08752DF3410F10426841BBDB0FC93FA80174074181CB000000804153F30F5804244883C408F3410F11426441C6425000F30F6F04244883C410415B415A589D48895C2418E900000000");
        BinaryPrimitives.WriteInt64LittleEndian(code.AsSpan(97), image + 0x3256DD8);
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(code.Length - 4),
            checked((int)(image + FunctionRva + EntrySize - (address + code.Length))));
        return code;
    }

    internal static long Inspect(ReadOnlySpan<byte> function, long image, Func<long, int, byte[]> read)
    {
        if (function.Length != FunctionSize) throw new InvalidDataException("Unexpected August character-state function length.");
        byte[] normalized = function.ToArray();
        ReadOnlySpan<byte> entry = function[..EntrySize];
        bool original = entry.SequenceEqual(OriginalEntry);
        if (!original && entry[0] != 0xE9) throw new InvalidDataException("Unknown character-state hook; door update refused.");
        OriginalEntry.CopyTo(normalized);
        if (Convert.ToHexString(SHA256.HashData(normalized)) != FunctionSha256)
            throw new InvalidDataException("August character-state function differs from the reviewed build.");
        if (original) return 0;
        long stub = image + FunctionRva + EntrySize + BinaryPrimitives.ReadInt32LittleEndian(entry[1..]);
        byte[] expected = BuildStub(image, stub);
        if (!read(stub, expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Existing character-state hook is not the verified door update.");
        return stub;
    }
}
