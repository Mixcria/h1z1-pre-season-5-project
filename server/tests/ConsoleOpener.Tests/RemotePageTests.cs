using Cranberry.Tools.ConsoleOpener;

namespace ConsoleOpener.Tests;

/// <summary>
/// The remote page — the string and the x64 stub that calls
/// <c>FUN_141291d50(rcx = 0, rdx = &amp;string)</c>. These bytes are executed inside the client, so
/// they are pinned byte for byte.
/// </summary>
public sealed class RemotePageTests
{
    private const long PageVa = 0x0000_1234_5678_0000L;
    private const long ToggleVa = 0x0000_0001_4129_1D50L;

    [Fact]
    public void TheStubIsByteExact()
    {
        byte[] page = PatchSites.BuildRemotePage("", PageVa, ToggleVa);

        byte[] expected =
        [
            0x48, 0x83, 0xEC, 0x28,                                     // sub rsp,0x28
            0x48, 0x31, 0xC9,                                           // xor rcx,rcx
            0x48, 0xBA, .. BitConverter.GetBytes(PageVa),               // mov rdx,pageVa
            0x48, 0xB8, .. BitConverter.GetBytes(ToggleVa),             // mov rax,toggleVa
            0xFF, 0xD0,                                                 // call rax
            0x48, 0x83, 0xC4, 0x28,                                     // add rsp,0x28
            0xC3,                                                       // ret
        ];

        Assert.Equal(expected, page[PatchSites.StubOffset..(PatchSites.StubOffset + expected.Length)]);
    }

    /// <summary>
    /// The ABI's 16-byte alignment, spelled out: a thread entry starts at <c>rsp % 16 == 8</c>,
    /// <c>sub rsp,0x28</c> brings it to 0, and the <c>call</c> pushes the 8 the callee expects. The
    /// 0x28 is 0x20 of shadow space plus that 8.
    /// </summary>
    [Fact]
    public void TheStubReservesShadowSpacePlusTheAlignmentEight()
    {
        byte[] page = PatchSites.BuildRemotePage("", PageVa, ToggleVa);
        byte adjustment = page[PatchSites.StubOffset + 3];

        Assert.Equal(0x28, adjustment);
        Assert.Equal(0, (8 - adjustment) % 16);
    }

    [Fact]
    public void TheCommandStringIsNulTerminatedAtTheStartOfThePage()
    {
        byte[] page = PatchSites.BuildRemotePage("7", PageVa, ToggleVa);

        Assert.Equal((byte)'7', page[0]);
        Assert.Equal(0, page[1]);
        Assert.Equal(PatchSites.PageSize, page.Length);
    }

    [Fact]
    public void AnEmptyArgumentIsAnEmptyString()
    {
        byte[] page = PatchSites.BuildRemotePage("", PageVa, ToggleVa);

        Assert.Equal(0, page[0]);
    }

    /// <summary>An argument long enough to reach the stub would overwrite it; that is refused.</summary>
    [Fact]
    public void AnArgumentThatWouldReachTheStubIsRefused() =>
        Assert.Throws<ArgumentException>(() =>
            PatchSites.BuildRemotePage(new string('9', PatchSites.StubOffset), PageVa, ToggleVa));
}
