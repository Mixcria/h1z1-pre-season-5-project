using Cranberry.Tools.ConsoleOpener;

namespace ConsoleOpener.Tests;

/// <summary>
/// The patch table against the binary it came from.
///
/// <para>
/// These are the tests that matter: <see cref="PatchSites"/> hard-codes bytes the tool will look for
/// in a live process, and the only honest check is to re-read them out of
/// <c>C:\Aug2017\Client\H1Z1.exe</c> at the file offsets the table computes. If a row ever drifts —
/// a mistyped RVA, a byte transcribed from the 1087 client — this fails long before anything is
/// written into a running game.
/// </para>
///
/// <para>The exe is opened READ-ONLY. Nothing here touches a process.</para>
/// </summary>
public sealed class PatchSitesTests
{
    /// <summary>The client this project's addresses were derived from.</summary>
    private const string ClientPath = @"C:\Aug2017\Client\H1Z1.exe";

    /// <summary>
    /// <c>.text</c> maps RVA <c>0x1000</c> to file offset <c>0x600</c>, so
    /// <c>file = 0x140000000 + rva - 0x140000A00</c>. The three rows are the ones R5 read by hand.
    /// </summary>
    [Theory]
    [InlineData(0x1291d50L, 0x1291350L)]
    [InlineData(0x1291d8cL, 0x129138cL)]
    [InlineData(0x1291fafL, 0x12915afL)]
    public void FileOffsetMatchesTheTextSectionMapping(long rva, long expected) =>
        Assert.Equal(expected, PatchSites.FileOffset(rva));

    [Fact]
    public void EverySiteReadsBackItsOriginalBytesFromTheClientOnDisk()
    {
        if (!File.Exists(ClientPath))
        {
            return;   // the client is not installed on this machine; nothing to compare against
        }

        using FileStream exe = File.OpenRead(ClientPath);

        foreach (PatchSite site in PatchSites.All)
        {
            byte[] actual = new byte[site.Original.Length];
            exe.Seek(site.FileOffset, SeekOrigin.Begin);
            exe.ReadExactly(actual);

            Assert.True(
                actual.AsSpan().SequenceEqual(site.Original),
                $"{site.Name} at file 0x{site.FileOffset:x} reads {Convert.ToHexString(actual)}, "
                + $"but the table says {Convert.ToHexString(site.Original)} ({site.Evidence})");
        }
    }

    [Fact]
    public void TheToggleEntryIsNeverWritten()
    {
        Assert.True(PatchSites.ToggleEntry.ReadOnly);
        Assert.Empty(PatchSites.ToggleEntry.Patched);
        Assert.Equal(PatchSites.TogglePrologue, PatchSites.ToggleEntry.Original);
    }

    /// <summary>
    /// The gate patch flips <c>jne</c> to <c>jmp</c> and MUST leave the displacement alone: one byte
    /// changes, the second stays <c>0x1d</c>, and the instruction keeps its length. A patch that
    /// changed the length would desynchronise everything after it.
    /// </summary>
    [Fact]
    public void TheGatePatchChangesOneByteAndKeepsTheJumpDistance()
    {
        Assert.Equal(PatchSites.Gate.Original.Length, PatchSites.Gate.Patched.Length);
        Assert.Equal(0x75, PatchSites.Gate.Original[0]);
        Assert.Equal(0xEB, PatchSites.Gate.Patched[0]);
        Assert.Equal(PatchSites.Gate.Original[1], PatchSites.Gate.Patched[1]);
        Assert.False(PatchSites.Gate.OptIn);

        // verified as the whole instruction, written as one atomic byte
        Assert.Equal(1, PatchSites.Gate.WriteLength);
        Assert.Equal(new byte[] { 0xEB }, PatchSites.Gate.PatchWrite);
        Assert.Equal(new byte[] { 0x75 }, PatchSites.Gate.RestoreWrite);
    }

    /// <summary>
    /// The five-byte CALL becomes five NOPs — same length, no fall-through surprise — and it is
    /// OPT-IN, because Cranberry ignores the packet it suppresses and every extra write is another
    /// thing for BattlEye to notice (design §1.5).
    /// </summary>
    [Fact]
    public void TheSpectateCallBecomesFiveNopsAndIsOptIn()
    {
        Assert.Equal(5, PatchSites.SpectateCall.Original.Length);
        Assert.Equal(0xE8, PatchSites.SpectateCall.Original[0]);
        Assert.Equal(new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 }, PatchSites.SpectateCall.Patched);
        Assert.True(PatchSites.SpectateCall.OptIn);
        Assert.Equal(5, PatchSites.SpectateCall.WriteLength);
    }

    /// <summary>
    /// The owner's 2026-08-23 rule, pinned as a test: exactly ONE site takes a multi-byte write, it
    /// is opt-in, and it is the cold console-toggle function. A one-byte write is atomic; a five-byte
    /// write into a hot CALL is what faulted a client that day.
    /// </summary>
    [Fact]
    public void OnlyOneSiteTakesAMultiByteWriteAndItIsOptIn()
    {
        PatchSite[] multiByte = PatchSites.All
            .Where(s => !s.ReadOnly && s.WriteLength > 1)
            .ToArray();

        PatchSite site = Assert.Single(multiByte);
        Assert.Same(PatchSites.SpectateCall, site);
        Assert.True(site.OptIn);
    }

    [Fact]
    public void EveryWrittenSiteKeepsItsInstructionLength()
    {
        foreach (PatchSite site in PatchSites.All.Where(s => !s.ReadOnly))
        {
            Assert.Equal(site.Original.Length, site.Patched.Length);
        }
    }
}
