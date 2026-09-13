using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class LootReloadFixTests
{
    private static byte[] Original() => Convert.FromHexString(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "interaction-august.hex")).Trim());

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void VerifiedNativeBranchesUpgradeAndRestoreWithoutTouchingOtherInstructions(int version)
    {
        byte[] memory = Original();
        if (version >= 1) memory[LootReloadFix.CancelIndex] = 0xEB;
        if (version >= 2) memory[LootReloadFix.AcknowledgementIndex] = 0xEB;
        Assert.Equal(version, LootReloadFix.Classify(memory));
        int writes = 0;
        void Write(int index, byte from, byte to)
        {
            Assert.Equal(from, memory[index]);
            memory[index] = to; writes++;
        }
        Assert.Equal(version != 2, LootReloadFix.ApplyVerified(() => memory.ToArray(), Write));
        Assert.Equal(2 - version, writes);
        Assert.Equal(2, LootReloadFix.Classify(memory));
        // Both are short jumps to the existing F dispatcher / ADS check.
        Assert.Equal(0x1411C6EF7L, 0x140000000L + LootReloadFix.FunctionRva + LootReloadFix.CancelIndex + 2 + memory[LootReloadFix.CancelIndex + 1]);
        Assert.Equal(0x1411C6E99L, 0x140000000L + LootReloadFix.FunctionRva + LootReloadFix.AcknowledgementIndex + 2 + memory[LootReloadFix.AcknowledgementIndex + 1]);
        Assert.True(LootReloadFix.ApplyVerified(() => memory.ToArray(), Write, restore: true));
        Assert.Equal(Original(), memory);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x10C)]
    [InlineData(0x15C)]
    [InlineData(467)]
    public void UnknownCodeCannotTriggerAnyWrite(int offset)
    {
        byte[] memory = Original(); memory[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => LootReloadFix.ApplyVerified(() => memory, (_, _, _) => Assert.Fail("Unexpected write")));
        Assert.Throws<InvalidDataException>(() => LootReloadFix.Classify(memory[..^1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedWriteOrReadbackRestoresTheExactPriorVersion(bool previousFix)
    {
        byte[] memory = Original();
        if (previousFix) memory[LootReloadFix.CancelIndex] = 0xEB;
        byte[] before = memory.ToArray();
        bool failed = false;
        Assert.Throws<IOException>(() => LootReloadFix.ApplyVerified(() => memory.ToArray(), (index, from, to) =>
        {
            Assert.Equal(from, memory[index]);
            memory[index] = to;
            if (!failed && index == LootReloadFix.AcknowledgementIndex)
            {
                failed = true;
                throw new IOException("Simulated cleanup failure after changing the byte");
            }
        }));
        Assert.Equal(before, memory);
    }

    [Fact]
    public void AnAcknowledgementBypassWithoutTheCancellationFixIsRefused()
    {
        byte[] memory = Original(); memory[LootReloadFix.AcknowledgementIndex] = 0xEB;
        Assert.Throws<InvalidDataException>(() => LootReloadFix.Classify(memory));
    }
}
