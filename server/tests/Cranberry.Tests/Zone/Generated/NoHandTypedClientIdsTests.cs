using System.Text.RegularExpressions;

namespace Cranberry.Tests.Zone.Generated;

/// <summary>
/// The lane-2B exit condition, as a test: <b>zero hand-typed locale, composite-effect or
/// <c>Doors.txt</c> ids are left in the files this lane owns</b>.
/// <para>
/// It is a source grep, and it is deliberately narrow. It reads the four hand-written files lane
/// 2B migrated plus the door tables, strips comments, and fails if any of the exact numbers those
/// files used to carry reappears as a literal. It says nothing about the rest of <c>src</c>: other
/// lanes own those files, and <c>MatchFlowPackets.cs</c> in particular still holds
/// <c>GameModeHud</c>'s four label literals — <c>ClientTableMigrationTests</c> pins them against
/// the generated table instead, which is the most this lane may do without editing another lane's
/// file mid-wave.
/// </para>
/// <para>
/// Comments are stripped rather than searched, on purpose. The evidence a derivation rests on —
/// "11113, string hash 1256554906" — belongs in the doc comment; what must not come back is a
/// number the compiler reads.
/// </para>
/// </summary>
public sealed class NoHandTypedClientIdsTests
{
    /// <summary>The files this lane owns and migrated. Relative to <c>src/Cranberry.Zone</c>.</summary>
    private static readonly string[] OwnedSources =
    [
        "Gas/GasAlerts.cs",
        "Gas/GasHud.cs",
        "Match/MatchAlerts.cs",
        "World/Doors/InteractionStringPackets.cs",
        "Environment/CompositeEffectGate.cs",
    ];

    /// <summary>
    /// Every id these files used to type: the 8 alert/HUD locale ids, the 8 interaction prompt
    /// ids, the 17 <c>Doors.txt</c> row ids' effect ids, and the door row ids themselves.
    /// </summary>
    public static TheoryData<int> FormerlyHandTypedIds()
    {
        var data = new TheoryData<int>();
        foreach (int id in new[]
                 {
                     // ClientUpdate.TextAlert sentences (GasAlerts.cs, MatchAlerts.cs)
                     11102, 11106, 11113, 11115, 11118, 11120, 11123, 11124,
                     // ce 0f countdown labels (GasHud.cs reached for these)
                     13198, 13356, 14151, 14152, 14153,
                     // 09 2d world prompts (InteractionStringPackets.cs)
                     29, 1004, 1326, 8882, 8922, 12156, 12416, 13338,
                     // Doors.txt swing effects (gen-doors.py's KINDS used to name rows, not sounds)
                     5048, 5049, 5075, 5076, 5085, 5086, 5089, 5090, 5095, 5096,
                 })
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FormerlyHandTypedIds))]
    public void NoOwnedSourceFileTypesAClientIdAnyMore(int id)
    {
        var pattern = new Regex($@"(?<![\w.]){id}(?![\w.])", RegexOptions.Compiled);

        foreach (string relative in OwnedSources)
        {
            string code = StripComments(File.ReadAllText(Path.Combine(ZoneSourceRoot(), relative)));
            Match hit = pattern.Match(code);
            Assert.False(
                hit.Success,
                $"{relative} still types the client id {id} as a literal at offset {hit.Index}. "
                + "It belongs in AugustStrings.g.cs / AugustEffectCatalog.g.cs / "
                + "AugustDoorTable.g.cs, generated from the client's own tables (docs/104).");
        }
    }

    /// <summary>
    /// The generator that writes the door table must not type a <c>Doors.txt</c> row id either -
    /// that was S8 defect T4, and it is the one place a hand id could come back without any C#
    /// file changing. Each family names a composite-effect NAME instead.
    /// </summary>
    [Fact]
    public void TheDoorGeneratorNamesSoundsAndNotRows()
    {
        string generator = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "data", "gen-doors.py"));
        int start = generator.IndexOf("KINDS: list[", StringComparison.Ordinal);
        int end = generator.IndexOf("EXPECTED_TOTAL", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start, "gen-doors.py no longer has a KINDS table");
        string kinds = generator[start..end];

        Assert.Contains("SFX_Door_Wood_Open", kinds, StringComparison.Ordinal);
        Assert.Contains("SFX_Door_Office_Open", kinds, StringComparison.Ordinal);
        Assert.DoesNotContain("Doors.txt row id per kind", kinds, StringComparison.Ordinal);

        // Every quoted sound name resolves in the client's own effect table.
        foreach (Match sound in Regex.Matches(kinds, @"""(SFX_[A-Za-z0-9_]+)"""))
        {
            Assert.True(
                Cranberry.Zone.Generated.AugustEffectCatalog.TryIdOf(sound.Groups[1].Value, out _),
                $"gen-doors.py names {sound.Groups[1].Value}, which this build has no definition for");
        }
    }

    /// <summary>
    /// The migrated files really do read the generated tables - a grep test that only ever says
    /// "no literals" would also pass on a file that stopped carrying the value at all.
    /// </summary>
    [Theory]
    [InlineData("Gas/GasAlerts.cs", "AugustStrings.Alerts.")]
    [InlineData("Gas/GasHud.cs", "AugustStrings.HudLabels.")]
    [InlineData("Match/MatchAlerts.cs", "AugustStrings.Alerts.")]
    [InlineData("World/Doors/InteractionStringPackets.cs", "AugustStrings.Prompts.")]
    [InlineData("Environment/CompositeEffectGate.cs", "AugustEffectCatalog.")]
    public void EveryMigratedFileReadsTheGeneratedTable(string relative, string expected)
    {
        string code = StripComments(File.ReadAllText(Path.Combine(ZoneSourceRoot(), relative)));
        Assert.Contains(expected, code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The composite-effect side of the exit condition: <b>the server sends no non-zero effect id
    /// at all today</b>, so "every id the server sends resolves in the catalogue" is vacuously
    /// true and stays checkable. Every <c>CompositeEffectId</c> / <c>EffectId</c> the tree writes
    /// is a record parameter defaulted to 0; if a lane ever ships a real one, this test is where
    /// it must be declared and looked up.
    /// </summary>
    [Fact]
    public void TheServerSendsNoCompositeEffectIdThisBuildCannotResolve()
    {
        var assignment = new Regex(
            @"\b(?:Composite)?EffectId\s*(?:=|:)\s*(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = StripComments(File.ReadAllText(file));
            foreach (Match match in assignment.Matches(code))
            {
                uint id = uint.Parse(match.Groups[1].Value);
                if (id != 0 && !Cranberry.Zone.Generated.AugustEffectCatalog.Contains(id))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>Blanks <c>//</c> lines and <c>/* */</c> blocks, keeping offsets and string bodies.</summary>
    private static string StripComments(string code)
    {
        var output = new char[code.Length];
        bool inLine = false, inBlock = false, inString = false, inChar = false, verbatim = false;

        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            char next = i + 1 < code.Length ? code[i + 1] : '\0';

            if (inLine)
            {
                output[i] = c == '\n' ? '\n' : ' ';
                inLine = c != '\n';
                continue;
            }

            if (inBlock)
            {
                output[i] = c == '\n' ? '\n' : ' ';
                if (c == '*' && next == '/')
                {
                    output[i + 1] = ' ';
                    i++;
                    inBlock = false;
                }

                continue;
            }

            if (inString)
            {
                output[i] = c;
                if (!verbatim && c == '\\' && next != '\0')
                {
                    output[i + 1] = next;
                    i++;
                }
                else if (c == '"')
                {
                    if (verbatim && next == '"')
                    {
                        output[i + 1] = next;
                        i++;
                    }
                    else
                    {
                        inString = false;
                        verbatim = false;
                    }
                }

                continue;
            }

            if (inChar)
            {
                output[i] = c;
                if (c == '\\' && next != '\0')
                {
                    output[i + 1] = next;
                    i++;
                }
                else if (c == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (c == '/' && next == '/')
            {
                output[i] = ' ';
                inLine = true;
                continue;
            }

            if (c == '/' && next == '*')
            {
                output[i] = ' ';
                inBlock = true;
                continue;
            }

            output[i] = c;
            if (c == '"')
            {
                inString = true;
                verbatim = i > 0 && (code[i - 1] == '@' || (i > 1 && code[i - 2] == '@'));
            }
            else if (c == '\'')
            {
                inChar = true;
            }
        }

        return new string(output);
    }

    private static string ZoneSourceRoot() => Path.Combine(SourceRoot(), "Cranberry.Zone");

    private static string SourceRoot() => Path.Combine(RepoRoot(), "src");

    /// <summary>Walks up from the test assembly to the directory holding <c>Cranberry.slnx</c>.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cranberry.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No directory holding Cranberry.slnx was found above {AppContext.BaseDirectory}");
    }
}
