using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The refusal-log reader: the runtime backstop for a name the start-up census cannot see.
/// <para>
/// The format string is the client's own -
/// <c>Server sent %s (%s) that conflicts with a local command</c>, written by
/// <c>FUN_141280280</c> to <c>Client\Logs\AdminCommands.log</c> - so these tests are about two
/// things: that the name inside the parentheses is recovered, and that reading is cheap and safe.
/// A missing file is the healthy case, a locked file must not take a packet handler down, and an
/// unchanged file must not be re-read on every inbound <c>09 42</c>.
/// </para>
/// </summary>
public class ClientCollisionLogTests
{
    private const string RealLine =
        "2026-09-03 19:02:11 Server sent __sendworldcommand (announce) that conflicts with a local command";

    [Fact]
    public void TheNameInsideTheParenthesesIsWhatIsRecovered()
    {
        Assert.Equal(["announce"], ClientCollisionLog.Parse([RealLine]));
    }

    [Fact]
    public void EveryOtherLineIsIgnored()
    {
        Assert.Empty(ClientCollisionLog.Parse(
        [
            string.Empty,
            "2026-09-03 19:02:10 Loaded 41 admin commands",
            "Server sent __sendworldcommand (announce) that does something else entirely",
            "(announce)",
        ]));
    }

    [Fact]
    public void ANameIsListedOnceHoweverManyTimesTheClientComplained()
    {
        Assert.Equal(
            ["announce", "evict"],
            ClientCollisionLog.Parse([RealLine, RealLine.Replace("announce", "evict"), RealLine]));
    }

    [Fact]
    public void AMissingFileIsTheHealthyCaseAndNotAnError()
    {
        ClientCollisionLog log = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "AdminCommands.log"));

        Assert.False(log.Exists);
        Assert.Empty(log.Read());
        Assert.Equal(0, log.Reads);
    }

    [Fact]
    public void AnUnchangedFileIsNotReadTwice()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "AdminCommands.log");
        try
        {
            File.WriteAllText(path, RealLine + Environment.NewLine);
            ClientCollisionLog log = new(path);

            Assert.Equal(["announce"], log.Read());
            Assert.Equal(1, log.Reads);

            // The second call is what happens on every inbound 09 42. It must cost a FileInfo and
            // nothing else - that is the whole reason there is no timer (design §4.3).
            Assert.Equal(["announce"], log.Read());
            Assert.Equal(1, log.Reads);

            File.AppendAllText(path, RealLine.Replace("announce", "evict") + Environment.NewLine);
            Assert.Equal(["announce", "evict"], log.Read());
            Assert.Equal(2, log.Reads);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ARefusedNameLeavesTheBurstAndGreysItsLeaf()
    {
        CommandRegistry registry = Cranberry.Zone.DevConsole.Commands.CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true });
        int before = registry.Names.Count;

        Assert.True(registry.MarkRefused("announce"));

        Assert.Equal(before - 1, registry.Names.Count);
        Assert.DoesNotContain("announce", registry.Names);
        Assert.True(registry.IsNameRefused("announce"));

        // The command itself stays in the registry: /help still lists it, and its menu leaf draws
        // "(refused)" rather than vanishing, which is what tells the owner to rename it.
        Assert.NotNull(registry.ByName("announce"));
    }
}
