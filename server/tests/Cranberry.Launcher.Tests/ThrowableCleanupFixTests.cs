using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class ThrowableCleanupFixTests
{
    private static byte[] Original()
    {
        using var stream = typeof(ThrowableCleanupFixTests).Assembly.GetManifestResourceStream(
            "Cranberry.Launcher.Tests.Fixtures.fire-rejected.original.bin")!;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0x140000000L, 0x145000000L)]
    [InlineData(0x7FF700000000L, 0x7FF6F0000000L)]
    public void OriginalAndInstalledFilterAreRecognizedWithRelocation(long image, long stub)
    {
        byte[] original = Original(), function = Original();
        Assert.Equal(0, ThrowableCleanupFix.Inspect(original, image, (_, _) => throw new Exception("Unexpected stub read")));
        byte[] code = ThrowableCleanupFix.BuildStub(image, stub);
        ThrowableCleanupFix.BuildEntry(image, stub).CopyTo(function, ThrowableCleanupFix.PatchIndex);
        Assert.Equal(stub, ThrowableCleanupFix.Inspect(function, image, (address, count) =>
        {
            Assert.Equal(stub, address);
            Assert.Equal(code.Length, count);
            return code;
        }));
        Assert.Equal(original[..ThrowableCleanupFix.PatchIndex], function[..ThrowableCleanupFix.PatchIndex]);
        Assert.Equal(original[(ThrowableCleanupFix.PatchIndex + 7)..], function[(ThrowableCleanupFix.PatchIndex + 7)..]);
        // Native emulation covers all status bytes, type/NPC branches and preserved registers.
        Assert.Equal(37, code.Length);
    }

    [Fact]
    public void EveryForeignFunctionByteAndTruncationRefuseBeforeInstallation()
    {
        byte[] original = Original();
        for (int i = 0; i < original.Length; i++)
        {
            byte[] unknown = original.ToArray();
            unknown[i] ^= 1;
            Assert.Throws<InvalidDataException>(() => ThrowableCleanupFix.Inspect(unknown, 0x140000000, (_, _) => new byte[96]));
        }
        Assert.Throws<InvalidDataException>(() => ThrowableCleanupFix.Inspect(original[..^1], 0x140000000, (_, _) => []));
        Assert.Throws<InvalidDataException>(() => ThrowableCleanupFix.Inspect([.. original, 0], 0x140000000, (_, _) => []));
    }

    [Fact]
    public void EveryForeignStubByteIsRejected()
    {
        const long image = 0x140000000, stub = 0x145000000;
        byte[] function = Original(), code = ThrowableCleanupFix.BuildStub(image, stub);
        ThrowableCleanupFix.BuildEntry(image, stub).CopyTo(function, ThrowableCleanupFix.PatchIndex);
        for (int i = 0; i < code.Length; i++)
        {
            byte[] unknown = code.ToArray();
            unknown[i] ^= 1;
            Assert.Throws<InvalidDataException>(() => ThrowableCleanupFix.Inspect(function, image, (_, _) => unknown));
        }
        Assert.Throws<OverflowException>(() => ThrowableCleanupFix.BuildEntry(image, image + (1L << 33)));
    }
}
