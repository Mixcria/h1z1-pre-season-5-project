using System.Buffers.Binary;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Launcher.Tests;

public sealed class BidirectionalDoorsTests
{
    private static byte[] Original()
    {
        using var input = typeof(BidirectionalDoorsTests).Assembly.GetManifestResourceStream(
            "Cranberry.Launcher.Tests.Fixtures.door-state.original.bin")!;
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }

    [Theory]
    [InlineData(0x140000000L, 0x145000000L)]
    [InlineData(0x7FF700000000L, 0x7FF6F0000000L)]
    public void NativeFunctionAndStubVerifyAfterRelocation(long image, long address)
    {
        byte[] original = Original(), function = Original(), stub = BidirectionalDoors.BuildStub(image, address);
        Assert.Equal(0, BidirectionalDoors.Inspect(function, image, (_, _) => throw new Exception()));
        BidirectionalDoors.BuildEntry(image, address).CopyTo(function, 0);
        Assert.Equal(address, BidirectionalDoors.Inspect(function, image, (a, n) =>
        { Assert.Equal(address, a); Assert.Equal(stub.Length, n); return stub; }));
        Assert.Equal(original[5..], function[5..]);
        Assert.Equal(image + 0x3256DD8, BinaryPrimitives.ReadInt64LittleEndian(stub.AsSpan(97)));
        Assert.True(stub.AsSpan().IndexOf(BitConverter.GetBytes(DoorSwing.PositiveSource)) >= 0);
        Assert.Equal(DoorSwing.PositiveSource + 1, DoorSwing.NegativeSource);
    }

    [Fact]
    public void AllForeignNativeFunctionAndStubBytesAreRejected()
    {
        const long image = 0x140000000, address = 0x145000000;
        byte[] original = Original(), function = Original(), code = BidirectionalDoors.BuildStub(image, address);
        for (int i = 0; i < original.Length; i++)
        {
            byte[] bad = original.ToArray(); bad[i] ^= 1;
            Assert.Throws<InvalidDataException>(() => BidirectionalDoors.Inspect(bad, image, (_, _) => []));
        }
        BidirectionalDoors.BuildEntry(image, address).CopyTo(function, 0);
        for (int i = 0; i < code.Length; i++)
        {
            byte[] bad = code.ToArray(); bad[i] ^= 1;
            Assert.Throws<InvalidDataException>(() => BidirectionalDoors.Inspect(function, image, (_, _) => bad));
        }
        Assert.Throws<InvalidDataException>(() => BidirectionalDoors.Inspect(original[..^1], image, (_, _) => []));
        Assert.Throws<OverflowException>(() => BidirectionalDoors.BuildEntry(image, image + (1L << 33)));
    }

    [Fact]
    public void ReadinessIsScopedToTheVerifiedActiveLaunchAndClearedOnDisconnect()
    {
        var clients = new DoorClientReadiness();
        Assert.False(clients.IsReady("a"));
        Assert.Throws<InvalidOperationException>(() => clients.Begin("a", "old", 0));
        clients.Begin("a", "first", 1);
        Assert.False(clients.IsReady("a"));
        Assert.False(clients.Confirm("b", "first", 1));
        Assert.False(clients.Confirm("a", "wrong", 1));
        Assert.True(clients.Confirm("a", "first", 1)); Assert.True(clients.IsReady("a"));
        Assert.True(clients.Confirm("a", "first", 1));
        clients.Begin("a", "second", 1);
        Assert.False(clients.IsReady("a")); Assert.False(clients.Confirm("a", "first", 1));
        Assert.True(clients.Confirm("a", "second", 1));
        clients.Remove("a"); Assert.False(clients.IsReady("a")); Assert.False(clients.Confirm("a", "second", 1));
    }
}
