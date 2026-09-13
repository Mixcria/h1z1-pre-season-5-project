using System.Numerics;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The parser that turns the client's argument tail into typed values (design §2.2).
/// </summary>
public sealed class CommandLineTests
{
    [Fact]
    public void WhitespaceSplitsTokensAndQuotesJoinThem()
    {
        CommandLine line = CommandLine.Parse("tp", "  348.0   32.5  \"Pleasant Valley\" ");

        Assert.Equal(3, line.Count);
        Assert.Equal("348.0", line.Word(0));
        Assert.Equal("32.5", line.Word(1));
        Assert.Equal("pleasant valley", line.Word(2));
    }

    [Fact]
    public void AnUnterminatedQuoteRunsToTheEndInsteadOfThrowing()
    {
        CommandLine line = CommandLine.Parse("tp", "\"Pleasant Val");

        Assert.Equal(1, line.Count);
        Assert.Equal("pleasant val", line.Word(0));
    }

    [Fact]
    public void WordsAreLowerCasedUnlessTheCommandKeepsCase()
    {
        Assert.Equal("ar15", CommandLine.Parse("give", "AR15").Word(0));
        Assert.Equal("AR15", CommandLine.Parse("give", "AR15", keepCase: true).Word(0));
    }

    [Fact]
    public void TextKeepsCaseAndSpacingWhateverTheCommandAsksFor()
    {
        CommandLine line = CommandLine.Parse("announce", "Hello   There Owner");

        Assert.Equal("Hello   There Owner", line.Text());
        Assert.Equal("There Owner", line.Text(1));
        Assert.Equal(string.Empty, line.Text(9));
    }

    [Fact]
    public void IntReadsDecimalAndHexAndFallsBackOnRubbish()
    {
        CommandLine line = CommandLine.Parse("hurt", "90 0x1234 nope");

        Assert.Equal(90, line.Int(0, -1));
        Assert.Equal(0x1234, line.Int(1, -1));
        Assert.Equal(-1, line.Int(2, -1));
        Assert.Equal(2500, line.Int(7, 2500));
    }

    [Fact]
    public void FloatReadsInvariantDecimals()
    {
        CommandLine line = CommandLine.Parse("speed", "2.5");

        Assert.Equal(2.5f, line.Float(0, 1f));
        Assert.Equal(1f, line.Float(1, 1f));
    }

    [Fact]
    public void HexReadsWithOrWithoutThePrefix()
    {
        Assert.Equal(0xd51bdb69u, CommandLine.Parse("raw", "0xD51BDB69").Hex(0, 0));
        Assert.Equal(0xd51bdb69u, CommandLine.Parse("raw", "d51bdb69").Hex(0, 0));
        Assert.Equal(7u, CommandLine.Parse("raw", "zz").Hex(0, 7));
    }

    [Fact]
    public void HexBytesJoinsEveryTokenAndRefusesAnOddRun()
    {
        Assert.Equal(new byte[] { 0x09, 0x42, 0x00 }, CommandLine.Parse("raw", "09 42 00").HexBytes());
        Assert.Equal(new byte[] { 0x09, 0x42, 0x00 }, CommandLine.Parse("raw", "0942 0x00").HexBytes());
        Assert.Null(CommandLine.Parse("raw", "094").HexBytes());
        Assert.Null(CommandLine.Parse("raw", "09 4z").HexBytes());
        Assert.Null(CommandLine.Parse("raw", string.Empty).HexBytes());
    }

    [Fact]
    public void TildeKeepsAnAxisAndOffsetsIt()
    {
        Vector3 origin = new(100f, 32f, 200f);
        CommandLine line = CommandLine.Parse("tp", "~ 200 ~");

        Assert.True(line.TryVector3(0, origin, out Vector3 moved));
        Assert.Equal(new Vector3(100f, 200f, 200f), moved);

        Assert.True(CommandLine.Parse("tp", "~10 ~ ~-5").TryVector3(0, origin, out Vector3 offset));
        Assert.Equal(new Vector3(110f, 32f, 195f), offset);
    }

    [Fact]
    public void AShortOrBrokenCoordinateIsRefusedRatherThanGuessed()
    {
        Vector3 origin = new(1f, 2f, 3f);

        Assert.False(CommandLine.Parse("tp", "1 2").TryVector3(0, origin, out _));
        Assert.False(CommandLine.Parse("tp", "1 2 north").TryVector3(0, origin, out _));
    }

    [Fact]
    public void ItemTakesABareNumberAsAnIdAndOtherwiseAsksTheResolver()
    {
        Assert.Equal(2604, CommandLine.Parse("give", "2604").Item(0, _ => null));
        Assert.Equal(10, CommandLine.Parse("give", "ar15").Item(0, name => name == "ar15" ? 10 : null));
        Assert.Null(CommandLine.Parse("give", "ar16").Item(0, _ => null));
        Assert.Null(CommandLine.Parse("give", string.Empty).Item(0, _ => 1));
    }

    [Fact]
    public void SubVerbMatchesExactlyThenByUniquePrefix()
    {
        string[] choices = ["start", "status", "stop"];

        Assert.Equal("start", CommandLine.Parse("gas", "start").SubVerb(0, choices, out _));
        Assert.Equal("status", CommandLine.Parse("gas", "statu").SubVerb(0, choices, out _));
        Assert.Null(CommandLine.Parse("gas", "st").SubVerb(0, choices, out string? ambiguous));
        Assert.Equal("ambiguous: 'st' -- start status stop", ambiguous);
        Assert.Null(CommandLine.Parse("gas", "go").SubVerb(0, choices, out string? unknown));
        Assert.Equal("not a sub-command: 'go' -- try start status stop", unknown);
        Assert.Null(CommandLine.Parse("gas", string.Empty).SubVerb(0, choices, out string? none));
        Assert.Null(none);
    }

    [Fact]
    public void TypedRebuildsTheLineTheOwnerCouldHaveTyped()
    {
        Assert.Equal("/give ar15 1", CommandLine.Parse("give", "ar15 1").Typed);
        Assert.Equal("/where", CommandLine.Parse("where", string.Empty).Typed);
    }

    [Fact]
    public void AnEmptyTailIsAnEmptyLineNotAnException()
    {
        CommandLine line = CommandLine.Parse("where", null);

        Assert.True(line.IsEmpty);
        Assert.Equal(0, line.Count);
        Assert.Null(line.Word(0));
        Assert.Equal("fallback", line.Word(0, "fallback"));
    }
}
