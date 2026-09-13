using Cranberry.Protocol;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;
using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// <c>/win</c> - R6 recommendation (a): the server opens the client's own windows with
/// <c>Ui.ExecuteScript</c> and zero files added to the client.
/// <para>
/// Three things are pinned here and nowhere else. <b>The bytes</b>, one vector per allow-listed
/// alias, generated from the same table the command reads, so a typo in a Lua name is a red test
/// rather than a silent no-op on the owner's screen (the client never answers a bad name -
/// <c>FUN_140ba89e0</c> <c>LAB_140ba8c1a</c>). <b>The deny-list</b>, because a table that can reach
/// <c>Ui.Logout</c> or <c>Ui.Quit</c> would end the owner's session from a menu row. <b>The
/// parser</b>, because <c>/win raw</c> is the probe and it has to refuse the shapes the client
/// cannot carry - a bare word, a trailing dot, a parenthesis, a non-integer argument.
/// </para>
/// </summary>
public sealed class WindowScriptTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>Every allow-listed row, as an xunit theory.</summary>
    public static TheoryData<string> Aliases()
    {
        var data = new TheoryData<string>();
        foreach (WindowScript row in WindowScripts.All)
        {
            data.Add(row.Alias);
        }

        return data;
    }

    // ---- the bytes ----------------------------------------------------------------------------

    [Fact]
    public void TheDesignsOwnVectorIsWhatTheTableProduces()
    {
        // DESIGN §5.1 / ConsolePacketTests: UiExecuteScript("Console.Show") is 22 bytes - two
        // header bytes (the Ui sub is a u8, FUN_1412caa00 reads (uint)*(byte*)(p+1)), the u32
        // string length, twelve text bytes and the u32 argument count. Nothing else may appear.
        byte[] bytes = Bytes(w => new UiExecuteScript("Console.Show").WriteTo(w));

        Assert.Equal(
            Convert.FromHexString("1A07" + "0C000000" + "436F6E736F6C652E53686F77" + "00000000"),
            bytes);
        Assert.Equal(22, bytes.Length);
        Assert.Equal(bytes.Length, WindowScripts.ByteLength("Console.Show"));
    }

    [Theory]
    [MemberData(nameof(Aliases))]
    public void EachAliasProducesTheExactBytesItsNameSpells(string alias)
    {
        // One vector per row, generated from the table rather than typed out: the framing is what
        // is being pinned (u8 sub, u32 length, UTF-8 text, u32 count and nothing after it), and the
        // Lua name itself is the payload a rename would move.
        WindowScript row = WindowScripts.ByAlias(alias)!;
        byte[] text = System.Text.Encoding.UTF8.GetBytes(row.Script);
        byte[] expected =
        [
            0x1A, 0x07,
            .. BitConverter.GetBytes((uint)text.Length),
            .. text,
            0x00, 0x00, 0x00, 0x00,
        ];

        Assert.Equal(expected, Bytes(w => new UiExecuteScript(row.Script).WriteTo(w)));
        Assert.Equal(expected.Length, WindowScripts.ByteLength(row.Script));
    }

    [Fact]
    public void TheProbeSequenceIsPinnedByteForByte()
    {
        // R6 §5.4 C1, C3 and C6 - the four sends /win probe makes, in order. If a rename ever moves
        // one of these the click recipe in docs/103 §11 is wrong and the owner chases a client bug.
        Assert.Equal(
            ["GameEvents.OnInventoryToggle", "Cranberry.NoSuchMethod", "HudHandler.Hide", "HudHandler.Show"],
            WindowScripts.ProbeSteps.Select(step => step.Script));

        Assert.Equal(
            Convert.FromHexString(
                "1A07" + "1C000000" + "47616D654576656E74732E4F6E496E76656E746F7279546F67676C65" + "00000000"),
            Bytes(w => new UiExecuteScript(WindowScripts.ProbeSteps[0].Script).WriteTo(w)));

        Assert.Equal(
            Convert.FromHexString(
                "1A07" + "16000000" + "4372616E62657272792E4E6F537563684D6574686F64" + "00000000"),
            Bytes(w => new UiExecuteScript(WindowScripts.ProbeSteps[1].Script).WriteTo(w)));

        Assert.Equal(
            Convert.FromHexString("1A07" + "0F000000" + "48756448616E646C65722E48696465" + "00000000"),
            Bytes(w => new UiExecuteScript(WindowScripts.ProbeSteps[2].Script).WriteTo(w)));

        Assert.Equal(
            Convert.FromHexString("1A07" + "0F000000" + "48756448616E646C65722E53686F77" + "00000000"),
            Bytes(w => new UiExecuteScript(WindowScripts.ProbeSteps[3].Script).WriteTo(w)));

        // The negative control must be a name no table in ScriptsBase.bin defines, and must never
        // be one the deny-list would refuse - a refused probe step proves nothing.
        Assert.False(WindowScripts.IsDenied(WindowScripts.ProbeSteps[1].Script, out _));
        Assert.Null(WindowScripts.All.FirstOrDefault(r => r.Script == WindowScripts.ProbeSteps[1].Script));
    }

    [Fact]
    public void ByteLengthIsTheHeaderTheLengthTheTextTheCountAndFourPerArgument()
    {
        Assert.Equal(22, WindowScripts.ByteLength("Console.Show"));
        Assert.Equal(26, WindowScripts.ByteLength("Console.Show", 1));
        Assert.Equal(
            Bytes(w => new UiExecuteScript("hi", 1u, 0xFFFFFFFFu).WriteTo(w)).Length,
            WindowScripts.ByteLength("hi", 2));
    }

    // ---- the table itself ---------------------------------------------------------------------

    [Fact]
    public void EveryAliasIsAShortLowerCaseWordAndNoneRepeats()
    {
        foreach (WindowScript row in WindowScripts.All)
        {
            Assert.Equal(row.Alias, row.Alias.ToLowerInvariant());
            Assert.InRange(row.Alias.Length, 2, 16);
            Assert.All(row.Alias, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"'{row.Alias}' is not a word"));
            Assert.DoesNotContain(row.Alias, WindowScripts.ReservedVerbs);
            Assert.False(string.IsNullOrWhiteSpace(row.Note), $"{row.Alias} has no note");
        }

        Assert.Equal(
            WindowScripts.All.Count,
            WindowScripts.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryScriptIsAWellFormedZeroArgObjectDotMethod()
    {
        foreach (WindowScript row in WindowScripts.All)
        {
            Assert.True(
                WindowScripts.IsWellFormed(row.Script, out string why),
                $"{row.Alias} -> {row.Script}: {why}");
        }
    }

    [Fact]
    public void EveryRowCarriesItsR6EvidenceMark()
    {
        Assert.All(WindowScripts.All, row => Assert.Contains(row.Mark, (string[])["[P]", "[I]"]));
        Assert.Contains(WindowScripts.All, row => row.Evidence == ScriptEvidence.Proven);
        Assert.Contains(WindowScripts.All, row => row.Evidence == ScriptEvidence.Inferred);
    }

    [Fact]
    public void TheTableHoldsTheWindowsR6NamesAsUsefulAndReachable()
    {
        // R6 §1.5's own table, and §2.1's three "can the server open it? yes" rows.
        string[] wanted =
        [
            "HudHandler.ShowInventory", "HudHandler.HideInventory", "GameEvents.OnInventoryToggle",
            "HudHandler.ShowSettings", "GameEvents.ShowKeyBindings",
            "HudHandler.ShowEscapeMenu", "HudHandler.HideMapWindow",
            "HudHandler.Show", "HudHandler.Hide",
            "Console.Show", "Console.Hide",
            "ConsoleWrapper.UnlockConsole", "ConsoleWrapper.LockConsole",
            "HudHandler.ShowDeathList", "HudHandler.ShowRewards", "HudHandler.ShowGrinder",
            "HudHandler.ShowCredits", "HudHandler.ShowNotes", "HudHandler.ShowBuilder",
            "HudHandler.ShowContainer", "GameEvents.openRespawnMap", "GameEvents.ShowBrowser",
            "GameEvents.HideAll", "GameEvents.RestoreAll", "GameEvents.OnMapToggle",
            "LoadingScreenHandler.Hide", "ChatHandler.CycleChatTabs",
        ];

        foreach (string script in wanted)
        {
            Assert.Contains(WindowScripts.All, row => row.Script == script);
        }
    }

    // ---- the deny-list --------------------------------------------------------------------

    [Theory]
    [InlineData("Ui.Logout")]
    [InlineData("Ui.Quit")]
    [InlineData("ui.quit")]
    [InlineData("Ui.ConvertToSpectator")]
    [InlineData("GameEvents.OnPlayerLogout")]
    [InlineData("GameEvents.UnloadAll")]
    [InlineData("CharacterSelectHandler.OnForcedDisconnect")]
    [InlineData("SettingsHandler.SetAllControlsToDefault")]
    [InlineData("SettingsHandler.SetProfileToDefault")]
    [InlineData("Console.ClearHistory")]
    [InlineData("ConsoleWrapper.ClearConsoleHistory")]
    [InlineData("ConsoleWrapper.ClearConsoleLogFile")]
    [InlineData("ChatHandler.ResetChatHistory")]
    [InlineData("ChatChannelGroup.ResetLog")]
    [InlineData("CharacterSelectHandler.RequestCharacterDelete")]
    [InlineData("Whatever.QuitToDesktop")]
    public void NothingThatExitsDisconnectsOrDeletesIsEverSent(string script)
    {
        Assert.True(WindowScripts.IsDenied(script, out string why), $"{script} must be refused");
        Assert.False(string.IsNullOrWhiteSpace(why));
        Assert.DoesNotContain(WindowScripts.All, row =>
            string.Equals(row.Script, script, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NotOneAllowListedWindowIsCaughtByTheDenyList()
    {
        // The two lists must not overlap: a row that is both allow-listed and denied would be a
        // menu leaf that always refuses, which reads as a server bug.
        foreach (WindowScript row in WindowScripts.All)
        {
            Assert.False(
                WindowScripts.IsDenied(row.Script, out string why),
                $"{row.Alias} -> {row.Script} is denied: {why}");
        }
    }

    // ---- the /win raw name shape ------------------------------------------------------------

    [Theory]
    [InlineData("HudHandler.Show")]
    [InlineData("Console.Show")]
    [InlineData("GameEvents.openRespawnMap")]
    [InlineData("A.b")]
    [InlineData("_Private.Method")]
    [InlineData("Deep.Nested.Name")]
    public void AWellFormedNameIsIdentifierDotIdentifier(string script) =>
        Assert.True(WindowScripts.IsWellFormed(script, out _), script);

    [Theory]
    [InlineData("", "give an Object.Method")]
    [InlineData("   ", "give an Object.Method")]
    [InlineData("HudHandler", "no dot")]
    [InlineData("HudHandler.", "empty part")]
    [InlineData(".Show", "empty part")]
    [InlineData("HudHandler..Show", "empty part")]
    [InlineData("HudHandler.Show()", "letters, digits")]
    [InlineData("HudHandler.Show, 1", "letters, digits")]
    [InlineData("Hud Handler.Show", "letters, digits")]
    [InlineData("HudHandler:Show", "no dot")]
    [InlineData("9Bad.Name", "not Identifier")]
    public void AMalformedNameIsRefusedWithTheReason(string script, string fragment)
    {
        Assert.False(WindowScripts.IsWellFormed(script, out string why), script);
        Assert.Contains(fragment, why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAbsurdlyLongNameIsRefusedBeforeTheClientsStackBuffer()
    {
        string long_ = "A." + new string('b', WindowScripts.MaximumScriptLength);

        Assert.False(WindowScripts.IsWellFormed(long_, out string why));
        Assert.Contains("the limit is", why, StringComparison.Ordinal);
    }

    // ---- registry and menu ------------------------------------------------------------------

    [Fact]
    public void WinIsRegisteredAndDoesNotCollideWithTheClientsOwnRegistry()
    {
        CommandRegistry registry = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true });
        ConsoleCommand? win = registry.ByName("win");

        Assert.NotNull(win);
        Assert.Equal(ConsoleTier.Tester, win!.Tier);
        Assert.True(win.KeepCase, "Lua lookups are case-sensitive; /win raw must keep its case");
        Assert.Contains("win", registry.Names);

        uint hash = CommandHash.Compute("win");
        Assert.Equal(0x19c89f3au, hash);
        Assert.False(
            ClientRegistry1148.Contains(hash),
            $"/win collides with the client's {ClientRegistry1148.Describe(hash)}");
        Assert.Same(win, registry.ByHash(hash));
    }

    [Fact]
    public void RegisteringWinTwiceIsRefusedByTheRegistry()
    {
        CommandRegistry registry = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true });

        Assert.Throws<ConsoleCommandRefusedException>(() => WindowCommands.AddTo(registry));
    }

    [Fact]
    public void TheWindowsBranchIsAllAllowListedAliasesAndTheThreeVerbs()
    {
        MenuTree tree = MenuTree.Build(CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true }));
        MenuNode windows = tree.ById("windows")!;

        Assert.Equal(MenuKind.Submenu, windows.Kind);
        Assert.Equal("Windows", windows.Label);

        List<MenuNode> leaves = [.. Walk(windows).Where(n => n.IsLeaf)];
        Assert.NotEmpty(leaves);

        foreach (MenuNode leaf in leaves)
        {
            Assert.Equal("win", leaf.CommandName);
            Assert.NotNull(tree.CommandOf(leaf));

            if (leaf.BoundArgs is "probe" or "list" or "raw")
            {
                continue;
            }

            WindowScript? row = WindowScripts.ByAlias(leaf.BoundArgs);
            Assert.NotNull(row);
            Assert.True(leaf.Tier >= row!.Tier, $"{leaf.Id} is drawn below the tier /win {row.Alias} needs");
        }

        Assert.Contains(leaves, l => l.BoundArgs == "probe");
        Assert.Contains(leaves, l => l.BoundArgs == "list");
        Assert.Contains(leaves, l => l is { BoundArgs: "raw", Kind: MenuKind.Prompt, Tier: ConsoleTier.Owner });
    }

    private static IEnumerable<MenuNode> Walk(MenuNode node)
    {
        yield return node;
        foreach (MenuNode child in node.Children)
        {
            foreach (MenuNode deeper in Walk(child))
            {
                yield return deeper;
            }
        }
    }
}
