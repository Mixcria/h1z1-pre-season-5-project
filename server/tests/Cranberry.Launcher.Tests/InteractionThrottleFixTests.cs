using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class InteractionThrottleFixTests
{
    private static Dictionary<long, byte[]> Memory(int version = 0)
    {
        byte[] Fixture(string name) => Convert.FromHexString(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Trim());
        var memory = new Dictionary<long, byte[]>
        {
            [LootReloadFix.FunctionRva] = Fixture("interaction-august.hex"),
            [LootReloadFix.ThrottleRva] = Fixture("interaction-throttle-august.hex"),
        };
        if (version >= 1) memory[LootReloadFix.FunctionRva][LootReloadFix.CancelIndex] = 0xEB;
        if (version >= 2) memory[LootReloadFix.FunctionRva][LootReloadFix.AcknowledgementIndex] = 0xEB;
        if (version >= 3) memory[LootReloadFix.ThrottleRva][LootReloadFix.ThrottleIndex] = 0xCE;
        return memory;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllKnownNativeVersionsUpgradeIdempotentlyAndRestore(int version)
    {
        var memory = Memory(version);
        int writes = 0;
        byte[] Read(long rva, int size) { Assert.Equal(size, memory[rva].Length); return memory[rva].ToArray(); }
        void Write(long rva, int index, byte from, byte to)
        { Assert.Equal(from, memory[rva][index]); memory[rva][index] = to; writes++; }
        Assert.Equal(version != 3, LootReloadFix.ApplyAllVerified(Read, Write));
        Assert.Equal(3 - version, writes);
        foreach (var pair in Memory(3)) Assert.Equal(pair.Value, memory[pair.Key]);
        Assert.False(LootReloadFix.ApplyAllVerified(Read, Write));
        Assert.True(LootReloadFix.ApplyAllVerified(Read, Write, restore: true));
        foreach (var pair in Memory()) Assert.Equal(pair.Value, memory[pair.Key]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x96)]
    [InlineData(282)]
    public void UnknownThrottlePreventsEveryInteractionWrite(int offset)
    {
        var memory = Memory(); memory[LootReloadFix.ThrottleRva][offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => LootReloadFix.ApplyAllVerified(
            (rva, _) => memory[rva], (_, _, _, _) => Assert.Fail("No code may change before both functions are verified")));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void FailureAfterChangingThrottleRestoresBothRegions(int version, bool restore)
    {
        var memory = Memory(version); var before = Memory(version); bool failed = false;
        Assert.Throws<IOException>(() => LootReloadFix.ApplyAllVerified((rva, _) => memory[rva].ToArray(),
            (rva, index, from, to) =>
            {
                Assert.Equal(from, memory[rva][index]); memory[rva][index] = to;
                if (rva == LootReloadFix.ThrottleRva && !failed)
                { failed = true; throw new IOException("Simulated protection failure after write"); }
            }, restore));
        foreach (var pair in before) Assert.Equal(pair.Value, memory[pair.Key]);
    }

    [Fact]
    public void FailedReadbackRollsBackEarlierReloadBranches()
    {
        var memory = Memory(); bool rejectThrottleWrite = true;
        Assert.Throws<InvalidDataException>(() => LootReloadFix.ApplyAllVerified((rva, _) => memory[rva].ToArray(),
            (rva, index, from, to) =>
            {
                Assert.Equal(from, memory[rva][index]);
                if (rva == LootReloadFix.ThrottleRva && rejectThrottleWrite)
                { rejectThrottleWrite = false; return; }
                memory[rva][index] = to;
            }));
        foreach (var pair in Memory()) Assert.Equal(pair.Value, memory[pair.Key]);
    }

    [Fact]
    public void ReviewedInstructionPermitsEitherNativeTimerSignWithoutChangingBookkeeping()
    {
        byte[] original = Memory()[LootReloadFix.ThrottleRva];
        Assert.Equal(new byte[] { 0x44, 0x8B, 0xF0, 0x41, 0xC1, 0xEE, 0x1F, 0x41, 0x80, 0xF6, 0x01 },
            original[(LootReloadFix.ThrottleIndex - 9)..(LootReloadFix.ThrottleIndex + 2)]);
        var patched = Memory(3)[LootReloadFix.ThrottleRva];
        Assert.True(LootReloadFix.ClassifyThrottle(patched));
        Assert.False(LootReloadFix.ClassifyThrottle(original));
        Assert.Equal(new byte[] { 0x41, 0x80, 0xCE, 0x01 },
            patched[(LootReloadFix.ThrottleIndex - 2)..(LootReloadFix.ThrottleIndex + 2)]);
        foreach (uint timerDifference in new uint[] { 0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF })
            Assert.Equal(1u, (timerDifference >> 31) | 1u);
        Assert.Equal(original[..LootReloadFix.ThrottleIndex], patched[..LootReloadFix.ThrottleIndex]);
        Assert.Equal(original[(LootReloadFix.ThrottleIndex + 1)..], patched[(LootReloadFix.ThrottleIndex + 1)..]);
    }
}
