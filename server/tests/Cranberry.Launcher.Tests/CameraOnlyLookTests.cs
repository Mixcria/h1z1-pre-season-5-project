using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class CameraOnlyLookTests
{
    // Native August .text 140c69200..140c69860, copied from the original executable.
    private static byte[] OriginalFunction() => Convert.FromHexString(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "headlook-august.hex")).Trim());

    [Fact]
    public void TheOriginalAndSingleBytePatchAreRecognizedWithoutChangingTheInstruction()
    {
        byte[] original = OriginalFunction(), patched = OriginalFunction();
        Assert.False(CameraOnlyLook.IsPatched(original));
        patched[0x5F6] = 0x7A;
        Assert.True(CameraOnlyLook.IsPatched(patched));
        Assert.Equal(Convert.FromHexString("F30F10158E584802"), original[0x5F2..0x5FA]);
        Assert.Equal(0x1430EF074L, 0x140C697FAL + BitConverter.ToInt32(patched, 0x5F6));
        Assert.Equal(0x1430EF088L, 0x140C697FAL + BitConverter.ToInt32(original, 0x5F6));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(0x5F6)]
    [InlineData(0x65F)]
    public void UnknownBytesAnywhereInTheFunctionAreRefused(int offset)
    {
        byte[] function = OriginalFunction();
        function[offset] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => CameraOnlyLook.IsPatched(function));
        Assert.Throws<InvalidDataException>(() => CameraOnlyLook.IsPatched(function[..^1]));
    }

    [Fact]
    public void OldLaunchLogsAndSubsequentZoningCannotTriggerThePatch()
    {
        const long start = 1800000000;
        string running = "a\tb\tc\t1800000000\tTransitionClientRunState: newState=cClientRunStateRunning\n";
        Assert.True(CameraOnlyLook.IsRunningLog(running, start));
        Assert.False(CameraOnlyLook.IsRunningLog(running, start + 60));
        Assert.False(CameraOnlyLook.IsRunningLog(running + "a\tb\tc\t1800000000\tTransitionClientRunState: newState=cClientRunStateZoning\n", start));
        Assert.False(CameraOnlyLook.IsRunningLog("cClientRunStateRunning", start));
    }
}
