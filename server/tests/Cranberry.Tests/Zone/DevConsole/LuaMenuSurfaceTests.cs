using System.Text;
using Cranberry.Protocol;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Menu;
using Cranberry.Zone.DevConsole.Surfaces;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The <c>lua</c> surface - R6 recommendation (b'), server half.
/// <para>
/// Two things are pinned here and nowhere else. First, the <b>framing</b>: the engine draws a menu
/// by calling <c>Line</c> once per row with no begin or end signal, so
/// <see cref="LuaMenuSurface"/> recovers the boundaries from <see cref="FrameRenderer"/>'s own two
/// rules, and a change to either side would silently hand the client a frame it never closes. The
/// tests below therefore render a <b>real</b> frame through the real renderer rather than a
/// hand-written one. Second, the <b>token routing</b>: every step the client-side script sends back
/// as <c>WallOfData 9a 05</c> must reduce to the exact console line the owner would have typed, so
/// that the typed door and the drawn door cannot drift.
/// </para>
/// <para>
/// <b>Nothing here is clicked.</b> No August client has ever had <c>CranberryMenu.lua</c> loaded;
/// what these tests prove is that the bytes the server would send are the bytes the file's hook
/// parses, and that the tokens it would send back land on the same state machine as <c>/d</c>.
/// </para>
/// </summary>
public sealed class LuaMenuSurfaceTests
{
    private static (LuaMenuSurface Surface, List<string> Lines) Wired()
    {
        List<string> lines = [];
        LuaMenuSurface surface = new(write =>
        {
            using PacketWriter writer = new();
            write(writer);
            byte[] bytes = writer.Written.ToArray();

            // 06 03 00 | u32 len | utf8 | u8 0 | u32 0
            Assert.Equal(0x06, bytes[0]);
            Assert.Equal(0x03, bytes[1]);
            Assert.Equal(0x00, bytes[2]);
            int length = BitConverter.ToInt32(bytes, 3);
            lines.Add(Encoding.UTF8.GetString(bytes, 7, length));
        });

        return (surface, lines);
    }

    // ---- the sentinel and the packet ----------------------------------------------------------

    [Fact]
    public void EveryLineIsAConsolePrintCarryingTheSentinel()
    {
        (LuaMenuSurface surface, List<string> lines) = Wired();

        surface.Line("hello");

        string only = Assert.Single(lines);
        Assert.StartsWith(LuaMenuSurface.Sentinel, only, StringComparison.Ordinal);
        Assert.Equal($"{LuaMenuSurface.Sentinel}{LuaMenuSurface.Loose}|hello", only);
    }

    [Fact]
    public void TheSentinelIsNotTheOnePrefixTheClientStealsForALocaleKey() =>
        // R2 §3.1.1: 06 05 reads a leading ## as a locale id. 06 03 does not, and this prefix is
        // not that one either - which is what lets a client with no script loaded still read the
        // line instead of losing it to a string-table lookup.
        Assert.False(LuaMenuSurface.Sentinel.StartsWith("##", StringComparison.Ordinal));

    [Fact]
    public void ABannerStaysABannerAndNeverBecomesAFrameRow()
    {
        List<byte[]> sent = [];
        LuaMenuSurface surface = new(write =>
        {
            using PacketWriter writer = new();
            write(writer);
            sent.Add(writer.Written.ToArray());
        });

        surface.Banner("gas is moving");

        byte[] only = Assert.Single(sent);
        Assert.Equal(0x11, only[0]);
        Assert.Equal(0x31, only[1]);
    }

    // ---- the frame ----------------------------------------------------------------------------

    [Fact]
    public void TheRuleTestMatchesBothThemesAndNothingElse()
    {
        Assert.True(LuaMenuSurface.IsRule("+====+"));
        Assert.True(LuaMenuSurface.IsRule("+----+"));
        Assert.True(LuaMenuSurface.IsRule("#====#"));

        Assert.False(LuaMenuSurface.IsRule("|    |"));
        Assert.False(LuaMenuSurface.IsRule("#    #"));
        Assert.False(LuaMenuSurface.IsRule("| > 1 Player      |"));
        Assert.False(LuaMenuSurface.IsRule("* Cranberry console ready"));
        Assert.False(LuaMenuSurface.IsRule("+"));
        Assert.False(LuaMenuSurface.IsRule(" "));
    }

    [Theory]
    [InlineData(MenuTheme.Plain)]
    [InlineData(MenuTheme.Heavy)]
    public void ARealRenderedFrameArrivesAsBeginRowsEnd(MenuTheme theme)
    {
        (LuaMenuSurface surface, List<string> lines) = Wired();
        ConsoleFixture fixture = ConsoleFixture.Create();
        fixture.Send("m");
        fixture.Session.Settings = fixture.Session.Settings with { Theme = theme };

        IReadOnlyList<string> frame = fixture.Frame();
        Assert.NotEmpty(frame);

        foreach (string row in frame)
        {
            surface.Line(row);
        }

        Assert.False(surface.InFrame);
        Assert.Equal(frame.Count + 2, lines.Count);

        Assert.Equal($"{LuaMenuSurface.Sentinel}{LuaMenuSurface.BeginFrame}|", lines[0]);
        Assert.Equal($"{LuaMenuSurface.Sentinel}{LuaMenuSurface.EndFrame}|", lines[^1]);

        // Everything between the markers is a row, in the renderer's own order, byte for byte.
        for (int i = 0; i < frame.Count; i++)
        {
            Assert.Equal($"{LuaMenuSurface.Sentinel}{LuaMenuSurface.Row}|{frame[i]}", lines[i + 1]);
        }
    }

    [Fact]
    public void TwoFramesInARowEachOpenAndClose()
    {
        (LuaMenuSurface surface, List<string> lines) = Wired();

        surface.Line("+==+");
        surface.Line("|  |");
        surface.Line("+--+");
        surface.Line("+==+");
        surface.Line("|  |");
        surface.Line("+--+");

        int begins = lines.Count(l => l.StartsWith($"{LuaMenuSurface.Sentinel}B|", StringComparison.Ordinal));
        int ends = lines.Count(l => l.StartsWith($"{LuaMenuSurface.Sentinel}E|", StringComparison.Ordinal));
        Assert.Equal(2, begins);
        Assert.Equal(2, ends);
        Assert.False(surface.InFrame);
    }

    [Fact]
    public void ALineOutsideAFrameIsLooseAndDoesNotDisturbTheBuffer()
    {
        (LuaMenuSurface surface, List<string> lines) = Wired();

        surface.Line("* Cranberry console ready");
        surface.Line("+==+");
        surface.Line("| x |");
        surface.Line("+--+");
        surface.Line("+ Teleported");

        Assert.Equal($"{LuaMenuSurface.Sentinel}L|* Cranberry console ready", lines[0]);
        Assert.Equal($"{LuaMenuSurface.Sentinel}L|+ Teleported", lines[^1]);
    }

    [Fact]
    public void AnUnclosedFrameCanBeClosedByHandAndClosingTwiceIsANoOp()
    {
        (LuaMenuSurface surface, List<string> lines) = Wired();

        surface.Line("+==+");
        Assert.True(surface.InFrame);

        surface.CloseFrame();
        Assert.False(surface.InFrame);
        Assert.Equal($"{LuaMenuSurface.Sentinel}E|", lines[^1]);

        int before = lines.Count;
        surface.CloseFrame();
        Assert.Equal(before, lines.Count);
    }

    [Fact]
    public void AnEmptyLineIsStillNeverEmptyOnTheWire()
    {
        (LuaMenuSurface surface, List<string> lines) = Wired();

        surface.Line(string.Empty);

        // ConsoleReply.NonEmpty turns "" into one space: the client's PrintConsole early-outs on an
        // empty string, so a blank row would vanish and the frame would lose a line.
        Assert.Equal($"{LuaMenuSurface.Sentinel}L| ", Assert.Single(lines));
    }

    // ---- chunking -----------------------------------------------------------------------------

    [Fact]
    public void AShortPayloadIsOnePacket()
    {
        IReadOnlyList<string> chunks = LuaMenuSurface.Encode(LuaMenuSurface.Row, "short");
        Assert.Equal([$"{LuaMenuSurface.Sentinel}R|short"], chunks);
    }

    [Fact]
    public void ALongPayloadIsChunkedAndOnlyTheFirstChunkCarriesTheCommand()
    {
        string payload = new('x', (LuaMenuSurface.MaximumPayload * 2) + 5);

        IReadOnlyList<string> chunks = LuaMenuSurface.Encode(LuaMenuSurface.Row, payload);

        Assert.Equal(3, chunks.Count);
        Assert.StartsWith($"{LuaMenuSurface.Sentinel}R|", chunks[0], StringComparison.Ordinal);
        Assert.StartsWith($"{LuaMenuSurface.Sentinel}+|", chunks[1], StringComparison.Ordinal);
        Assert.StartsWith($"{LuaMenuSurface.Sentinel}+|", chunks[2], StringComparison.Ordinal);

        // Reassembled exactly as CranberryMenu.lua's '+' arm reassembles it.
        string rebuilt = string.Concat(chunks.Select(c => c[(LuaMenuSurface.Sentinel.Length + 2)..]));
        Assert.Equal(payload, rebuilt);
    }

    [Fact]
    public void NoMenuFrameRowIsEverWideEnoughToBeChunked() =>
        // The widest frame the grid is defined for is 60 columns, so chunking only ever applies to
        // a long command answer - which is why the '+' arm is allowed to append blindly to the last
        // row rather than carrying a row index.
        Assert.True(ConsoleOptions.MaximumWidth < LuaMenuSurface.MaximumPayload);

    // ---- the surface is selectable ------------------------------------------------------------

    [Theory]
    [InlineData("lua")]
    [InlineData("LUA")]
    [InlineData(" Lua ")]
    public void TheEnvironmentAndSlashSurfaceBothSpellItLua(string word) =>
        Assert.Equal(ConsoleSurfaceKind.Lua, ConsoleOptions.ParseSurface(word, ConsoleSurfaceKind.Print));

    [Fact]
    public void TheSurfaceWordAndOpcodeRoundTrip()
    {
        Assert.Equal("lua", ConsoleOptions.SurfaceWord(ConsoleSurfaceKind.Lua));
        Assert.StartsWith("06 03", ConsoleOptions.SurfaceOpcode(ConsoleSurfaceKind.Lua), StringComparison.Ordinal);
        Assert.Contains(LuaMenuSurface.Sentinel, ConsoleOptions.SurfaceOpcode(ConsoleSurfaceKind.Lua), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFactoryBuildsItAndStillDropsLinesWithNoWire()
    {
        Assert.IsType<LuaMenuSurface>(SurfaceFactory.For(ConsoleSurfaceKind.Lua, _ => { }));
        Assert.IsType<NullSurface>(SurfaceFactory.For(ConsoleSurfaceKind.Lua, null));
    }

    [Fact]
    public void ItIsNotTheDefaultAndCannotBecomeOneByAccident()
    {
        // A client with no CranberryMenu.lua would show the raw CRANBERRY: prefixes, so the surface
        // is opt-in twice over: the shipped default, and an unreadable environment word.
        Assert.Equal(ConsoleSurfaceKind.Print, ConsoleOptions.Default.Surface);
        Assert.Equal(
            ConsoleSurfaceKind.Print,
            ConsoleOptions.ParseSurface("luaa", ConsoleSurfaceKind.Print));
    }

    // ---- the 9a 05 reply channel --------------------------------------------------------------

    [Fact]
    public void TheWallOfDataTableNameIsNotAnyWindowTheClientAlreadySends() =>
        Assert.DoesNotContain(
            LuaMenuSurface.WallOfDataTable,
            (string[])["CUSTOMIZATION_WINDOW", "LoadingScreenWindow", "InventoryWindow", "HudGameModeWindow"]);

    [Theory]
    [InlineData("u", "u", "")]
    [InlineData("d", "d", "")]
    [InlineData("s", "s", "")]
    [InlineData("b", "b", "")]
    [InlineData("q", "q", "")]
    [InlineData("r", "r", "")]
    [InlineData("D", "d", "")]
    [InlineData(" d ", "d", "")]
    [InlineData("m", "m", "")]
    [InlineData("open", "m", "")]
    [InlineData("toggle", "m", "")]
    [InlineData("close", "q", "")]
    [InlineData("hide", "q", "")]
    [InlineData("uu", "m", "uu")]
    [InlineData("dd", "m", "dd")]
    [InlineData("home", "m", "home")]
    [InlineData("end", "m", "end")]
    [InlineData("y", "m", "y")]
    [InlineData("n", "m", "n")]
    [InlineData("3", "m", "3")]
    [InlineData("12", "m", "12")]
    [InlineData("m 3", "m", "3")]
    [InlineData("win inventory", "win", "inventory")]
    public void EveryTokenReducesToTheLineTheOwnerWouldHaveTyped(string token, string name, string arguments)
    {
        (string Name, string Arguments)? routed = LuaMenuSurface.Route(token);

        Assert.NotNull(routed);
        Assert.Equal(name, routed!.Value.Name);
        Assert.Equal(arguments, routed.Value.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTokenIsIgnoredRatherThanGuessed(string? token) =>
        Assert.Null(LuaMenuSurface.Route(token));

    [Fact]
    public void AnAbsurdlyLongTokenIsRefusedBeforeItReachesTheParser() =>
        Assert.Null(LuaMenuSurface.Route(new string('x', 200)));

    [Fact]
    public void EveryShortVerbTheMenuKnowsIsReachableFromAToken()
    {
        // MenuInput.ShortVerbs is the alphabet the typed door registers; if a verb were ever added
        // there and not here, the drawn door would quietly lose a key.
        foreach (string verb in MenuInput.ShortVerbs)
        {
            (string Name, string Arguments)? routed = LuaMenuSurface.Route(verb);
            Assert.NotNull(routed);
            Assert.Equal(verb, routed!.Value.Name);
            Assert.True(MenuInput.IsMenuName(routed.Value.Name));
        }
    }

    // ---- both front doors, one state machine --------------------------------------------------

    [Fact]
    public void ATokenMovesTheCursorExactlyAsTheTypedVerbDoes()
    {
        ConsoleFixture typed = ConsoleFixture.Create();
        ConsoleFixture drawn = ConsoleFixture.Create();

        typed.Send("m");
        typed.Send("d");
        typed.Send("d");

        Drive(drawn, "m");
        Drive(drawn, "d");
        Drive(drawn, "d");

        Assert.True(typed.Session.Menu.IsOpen);
        Assert.True(drawn.Session.Menu.IsOpen);
        Assert.Equal(typed.Frame(), drawn.Frame());
    }

    [Fact]
    public void ARowNumberFromTheScriptWalksIntoTheSameSubmenuAsSlashM3()
    {
        ConsoleFixture typed = ConsoleFixture.Create();
        ConsoleFixture drawn = ConsoleFixture.Create();

        typed.Send("m");
        typed.Send("m", "1");

        Drive(drawn, "m");
        Drive(drawn, "1");

        Assert.Equal(typed.Session.Menu.Path, drawn.Session.Menu.Path);
        Assert.Equal(typed.Frame(), drawn.Frame());
    }

    [Fact]
    public void CloseFromTheScriptClosesTheMenu()
    {
        ConsoleFixture drawn = ConsoleFixture.Create();

        Drive(drawn, "m");
        Assert.True(drawn.Session.Menu.IsOpen);

        Drive(drawn, "close");
        Assert.False(drawn.Session.Menu.IsOpen);
    }

    [Fact]
    public void AnUnknownTokenAnswersAHintAndNeverThrows()
    {
        ConsoleFixture drawn = ConsoleFixture.Create();

        Drive(drawn, "m");
        drawn.Clear();
        Drive(drawn, "wibble");

        Assert.NotEmpty(drawn.Lines);
        Assert.All(drawn.Lines, line => Assert.False(string.IsNullOrEmpty(line)));
    }

    /// <summary>Feeds one <c>9a 05</c> token through the same route <c>ZoneService</c> uses.</summary>
    private static void Drive(ConsoleFixture fixture, string token)
    {
        (string Name, string Arguments)? routed = LuaMenuSurface.Route(token);
        Assert.NotNull(routed);
        fixture.Tick();
        fixture.Engine.ExecuteLine(fixture.Context, fixture.Session, routed!.Value.Name, routed.Value.Arguments);
    }
}
