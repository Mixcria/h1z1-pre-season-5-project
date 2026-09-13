using Cranberry.Host.Config;

namespace Cranberry.Tests.Host;

/// <summary>
/// `cranberry.json.example` and `docs/110-config.md` are generated from <see cref="ConfigKeys"/>,
/// so adding a switch without documenting it has to fail the build rather than the next play-test.
/// <para>
/// Set <c>CRANBERRY_WRITE_CONFIG_DOCS=1</c> to rewrite both committed files from the table instead
/// of asserting against them; that is how they are regenerated after a row is added.
/// </para>
/// </summary>
public sealed class CranberryConfigDocumentationTests
{
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cranberry.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static void AssertOrWrite(string path, string expected)
    {
        if (System.Environment.GetEnvironmentVariable("CRANBERRY_WRITE_CONFIG_DOCS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing; run with CRANBERRY_WRITE_CONFIG_DOCS=1");
        string actual = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    [Fact]
    public void TheExampleFileMatchesTheShippedDefaults()
    {
        AssertOrWrite(
            Path.Combine(RepositoryRoot(), "src", "Cranberry.Host", "cranberry.json.example"),
            ConfigDocs.ExampleJson());
    }

    [Fact]
    public void TheConfigDocumentMatchesTheKeyTable()
    {
        AssertOrWrite(
            Path.Combine(RepositoryRoot(), "docs", "110-config.md"),
            ConfigDocs.Markdown());
    }

    /// <summary>
    /// The example file is a real, loadable configuration and, being every default, loading it must
    /// leave every bound record exactly where it was.
    /// </summary>
    [Fact]
    public void TheExampleFileLoadsAndChangesNothing()
    {
        CranberryConfig fromExample = CranberryConfig.Load([], _ => null, ConfigDocs.ExampleJson());
        CranberryConfig defaults = CranberryConfig.Defaults();

        Assert.True(fromExample.FileLoaded);
        Assert.Null(fromExample.FileNote);
        Assert.Equal(defaults.EffectiveJson(), fromExample.EffectiveJson());
    }
}
