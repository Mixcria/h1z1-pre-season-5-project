using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class OwnBulletTracersTests
{
    private static byte[] Original()
    {
        using var stream = typeof(OwnBulletTracersTests).Assembly.GetManifestResourceStream(
            "Cranberry.Launcher.Tests.Fixtures.fire-projectile.original.bin")!;
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
        Assert.Equal(0, OwnBulletTracers.Inspect(original, image, (_, _) => throw new Exception("Unexpected stub read")));
        byte[] code = OwnBulletTracers.BuildStub(image, stub);
        OwnBulletTracers.BuildEntry(image, stub).CopyTo(function, OwnBulletTracers.PatchIndex);
        Assert.Equal(stub, OwnBulletTracers.Inspect(function, image, (address, count) =>
        {
            Assert.Equal(stub, address);
            Assert.Equal(code.Length, count);
            return code;
        }));
        Assert.Equal(original[..OwnBulletTracers.PatchIndex], function[..OwnBulletTracers.PatchIndex]);
        Assert.Equal(original[(OwnBulletTracers.PatchIndex + 7)..], function[(OwnBulletTracers.PatchIndex + 7)..]);
        // The trampoline carries only the eight August firearm ids, with original
        // remote, non-firearm and post-assignment destinations tested by native emulation.
        Assert.Equal(106, code.Length);
    }

    [Fact]
    public void EveryForeignFunctionByteAndTruncationRefuseBeforeInstallation()
    {
        byte[] original = Original();
        for (int i = 0; i < original.Length; i++)
        {
            byte[] unknown = original.ToArray();
            unknown[i] ^= 1;
            Assert.Throws<InvalidDataException>(() => OwnBulletTracers.Inspect(unknown, 0x140000000, (_, _) => new byte[96]));
        }
        Assert.Throws<InvalidDataException>(() => OwnBulletTracers.Inspect(original[..^1], 0x140000000, (_, _) => []));
        Assert.Throws<InvalidDataException>(() => OwnBulletTracers.Inspect([.. original, 0], 0x140000000, (_, _) => []));
    }

    [Fact]
    public void EveryForeignStubByteIsRejected()
    {
        const long image = 0x140000000, stub = 0x145000000;
        byte[] function = Original(), code = OwnBulletTracers.BuildStub(image, stub);
        OwnBulletTracers.BuildEntry(image, stub).CopyTo(function, OwnBulletTracers.PatchIndex);
        for (int i = 0; i < code.Length; i++)
        {
            byte[] unknown = code.ToArray();
            unknown[i] ^= 1;
            Assert.Throws<InvalidDataException>(() => OwnBulletTracers.Inspect(function, image, (_, _) => unknown));
        }
        Assert.Throws<OverflowException>(() => OwnBulletTracers.BuildEntry(image, image + (1L << 33)));
    }
}
