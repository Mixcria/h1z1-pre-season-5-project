using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// Pins <see cref="CommandHash"/> to the constants compiled into the August client.
/// <para>
/// Every (name, value) pair below is read out of <c>FUN_141277a50</c>, the constructor of the client's
/// built-in console-command table (dump: <c>out\ghidra-aug\devconsole-__sendworldcommand</c>); the
/// un-inlined names (<c>hit</c>, <c>item</c>, <c>run</c>, <c>goto</c>, <c>fog</c>, <c>afk</c>,
/// <c>host</c>) were read back from the exe at the DAT_ addresses the decompile cites. <c>HELP</c> is
/// the value measured on the wire for an unknown command. These are [P] fixtures: a change to the
/// algorithm that still passes them is byte-compatible with the client.
/// </para>
/// </summary>
public sealed class CommandHashTests
{
    [Theory]
    [InlineData("attack", 0x49acdc9du)]
    [InlineData("hit", 0x9f745c1fu)]
    [InlineData("report", 0x62b4abddu)]
    [InlineData("respawn", 0xddc66baeu)]
    [InlineData("vehicle", 0xbca31210u)]
    [InlineData("item", 0x2c65c77fu)]
    [InlineData("clienttime", 0x321279a9u)]
    [InlineData("currency", 0xebf9b3e0u)]
    [InlineData("run", 0x70fe8d29u)]
    [InlineData("goto", 0xd8599004u)]
    [InlineData("servertime", 0xfe0b6700u)]
    [InlineData("zonetime", 0x54665599u)]
    [InlineData("fog", 0x408751d2u)]
    [InlineData("shadows", 0xd10b4dc1u)]
    [InlineData("zrange", 0xecce644fu)]
    [InlineData("demomode", 0x9749a4f6u)]
    [InlineData("foregroundzoom", 0x4d9ebff2u)]
    [InlineData("afk", 0x969ad030u)]
    [InlineData("profanity", 0x01b45052u)]
    [InlineData("guild", 0x1f90bf9bu)]
    [InlineData("ignore", 0xc495b345u)]
    [InlineData("__sendworldcommand", 0x7a4ad185u)]
    [InlineData("__sendzonecommand", 0xa2497932u)]
    [InlineData("spectate", 0xfe71d25du)]
    [InlineData("observer", 0xd4b0c68cu)]
    [InlineData("abtest", 0xeee53e34u)]
    [InlineData("reportlastdeath", 0x6147c390u)]
    [InlineData("whitelist", 0x2b6ae143u)]
    [InlineData("organizedplay", 0xcb5d72e8u)]
    [InlineData("organized", 0x6fc566afu)]
    [InlineData("hosted", 0xa65ff6b2u)]
    [InlineData("host", 0x474ec401u)]
    [InlineData("foundationaccess", 0x6a144144u)]
    [InlineData("timescene", 0x855f63e9u)]
    [InlineData("autotest", 0x79c7a747u)]
    [InlineData("serverinfo", 0x8d547377u)]
    [InlineData("netstats", 0x0fcc2872u)]
    [InlineData("HELP", 0xd51bdb69u)]   // wire-measured for an unknown command (1087), same function at 1148
    public void MatchesTheClientsBuiltInConstants(string name, uint expected) =>
        Assert.Equal(expected, CommandHash.Compute(name));

    [Theory]
    [InlineData("help")]
    [InlineData("Help")]
    [InlineData("HELP")]
    [InlineData("hElP")]
    public void IsCaseInsensitiveOverAscii(string spelling) =>
        Assert.Equal(CommandHash.Help, CommandHash.Compute(spelling));

    [Fact]
    public void StopsAtTheFirstNulLikeTheClientsCStringLoop() =>
        Assert.Equal(CommandHash.Compute("fog"), CommandHash.Compute("fog\0shadows"u8));

    [Fact]
    public void EmptyNameHashesToZero() => Assert.Equal(0u, CommandHash.Compute(""));

    /// <summary>
    /// The 50 names Cranberry pushes, with the number the client will compute for each. These are
    /// not read from the binary — they are what the design fixed — so this theory is a rename
    /// alarm: change a verb and the pinned hash, the AddWorldCommand bytes and the menu's typed
    /// form all move together, and nothing else silently keeps the old number.
    /// </summary>
    [Theory]
    [MemberData(nameof(CranberryNames))]
    public void MatchesTheHashesPinnedForCranberrysOwnNames(string name, uint expected) =>
        Assert.Equal(expected, CommandHash.Compute(name));

    public static TheoryData<string, uint> CranberryNames() => ConsoleNameVectors.Rows();

    [Fact]
    public void HighBytesAreSignExtendedNotFolded()
    {
        // 0xE9 folds to itself and enters the sum as -23 (movsx). A port that zero-extends the byte
        // would compute the value below instead; the two must differ.
        uint viaSigned = CommandHash.Compute(new byte[] { 0xE9 });
        int h = unchecked((0 + 0xE9) * 0x401);
        h ^= h >> 6;
        h = unchecked(h * 9);
        h ^= h >> 11;
        h = unchecked(h * 0x8001);
        Assert.NotEqual(unchecked((uint)h), viaSigned);
    }
}
