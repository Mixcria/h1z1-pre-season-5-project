using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Menu;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// An engine, a session, a recording surface and a clock the test owns - everything the console
/// needs to run except a client, a socket and a match.
/// <para>
/// The clock is the point: the engine drops an input that arrives inside
/// <see cref="ConsoleOptions.RateLimitMs"/> of the last one, so a test that sent two lines in the
/// same millisecond would silently lose the second. <see cref="Tick"/> moves time forward by more
/// than the limit before every send, and <see cref="Immediate"/> is how the rate limit itself is
/// tested.
/// </para>
/// </summary>
internal sealed class ConsoleFixture
{
    private long _now;

    private ConsoleFixture(ConsoleOptions options, CommandRegistry registry)
    {
        Options = options;
        Registry = registry;
        Engine = ConsoleEngine.Create(options, registry, log: null, clock: () => _now);
        Session = new ConsoleSession
        {
            Tier = ConsoleTier.Owner,
            Settings = ConsoleSettings.From(options),
        };
        Context = new ConsoleContext
        {
            Surface = Surface,
            Remote = "127.0.0.1:20042",
            Name = "Cranberry",
            Tier = ConsoleTier.Owner,
            ServerVersion = "7b8dd95",
            ClientBuild = "1148",
            MatchStep = () => Step,
            Log = Logged.Add,
            Warn = Warned.Add,
            Send = writer => Sent.Add(Bytes(writer)),
            RowNote = id => Notes.GetValueOrDefault(id),
            RowToggle = id => id == "player.god" ? Session.Invulnerable : (bool?)null,

            // /win: record what would go on the wire and answer the line ZoneService.Console.cs
            // answers, so the engine-level tests read the same reply the owner will.
            UiScript = (script, ints) =>
            {
                Scripts.Add((script, [.. ints]));
                return ConsoleReply.Did(
                    $"sent Ui.ExecuteScript {script}"
                    + (ints.Count == 0 ? string.Empty : $" [{string.Join(' ', ints)}]")
                    + $" ({WindowScripts.ByteLength(script, ints.Count)} bytes) - "
                    + "the client does not acknowledge; watch the screen");
            },
            Later = (ms, work) =>
            {
                if (!SchedulerWired)
                {
                    return false;
                }

                Deferred.Add((ms, work));
                return true;
            },
        };
    }

    /// <summary>Builds a fixture over <see cref="TestCatalog"/>.</summary>
    public static ConsoleFixture Create(ConsoleOptions? options = null) =>
        new(options ?? (ConsoleOptions.Default with { ModMenuEnabled = true }), TestCatalog.Build());

    /// <summary>Builds a fixture over a registry the test made itself.</summary>
    public static ConsoleFixture Over(CommandRegistry registry, ConsoleOptions? options = null) =>
        new(options ?? (ConsoleOptions.Default with { ModMenuEnabled = true }), registry);

    /// <summary>The switches.</summary>
    public ConsoleOptions Options { get; }

    /// <summary>The commands.</summary>
    public CommandRegistry Registry { get; }

    /// <summary>The engine under test.</summary>
    public ConsoleEngine Engine { get; }

    /// <summary>The one session.</summary>
    public ConsoleSession Session { get; }

    /// <summary>Where the lines land.</summary>
    public RecordingSurface Surface { get; } = new();

    /// <summary>The context handed to every command.</summary>
    public ConsoleContext Context { get; }

    /// <summary>Every <c>console:</c> Info line.</summary>
    public List<string> Logged { get; } = [];

    /// <summary>Every <c>console:</c> Warn line.</summary>
    public List<string> Warned { get; } = [];

    /// <summary>Every packet the engine put on the wire, as raw bytes.</summary>
    public List<byte[]> Sent { get; } = [];

    /// <summary>Live right-hand notes, by node id.</summary>
    public Dictionary<string, string> Notes { get; } = [];

    /// <summary>Every <c>Ui.ExecuteScript</c> the console asked for, in order.</summary>
    public List<(string Script, uint[] Ints)> Scripts { get; } = [];

    /// <summary>Work handed to <c>ConsoleContext.Later</c>, with its delay - never run on its own.</summary>
    public List<(int Ms, Action Work)> Deferred { get; } = [];

    /// <summary>False makes <c>ConsoleContext.Later</c> refuse, as a host with no dispatcher does.</summary>
    public bool SchedulerWired { get; set; } = true;

    /// <summary>Runs the deferred work in delay order, as the listener thread eventually would.</summary>
    public void RunDeferred()
    {
        foreach ((int _, Action work) in Deferred.OrderBy(d => d.Ms).ToList())
        {
            work();
        }

        Deferred.Clear();
    }

    /// <summary>What <c>ConsoleContext.MatchStep()</c> answers.</summary>
    public string Step { get; set; } = "InMatch";

    /// <summary>The lines drawn since the last <see cref="Clear"/>.</summary>
    public IReadOnlyList<string> Lines => Surface.Lines;

    /// <summary>The current menu frame, or an empty list when the menu is closed.</summary>
    public IReadOnlyList<string> Frame() =>
        FrameRenderer.Render(Session.Menu, Engine.Tree, Engine.BuildView(Context, Session));

    /// <summary>Forgets the drawn lines, so one assertion covers one step.</summary>
    public ConsoleFixture Clear()
    {
        Surface.Clear();
        return this;
    }

    /// <summary>Moves the clock past the rate limit.</summary>
    public void Tick(long ms = 1000) => _now += ms;

    /// <summary>Sends a line, moving the clock first so the rate limit never eats it.</summary>
    public ConsoleFixture Send(string name, string arguments = "")
    {
        Tick();
        Immediate(name, arguments);
        return this;
    }

    /// <summary>Sends a line without moving the clock - how the rate limit is tested.</summary>
    public ConsoleFixture Immediate(string name, string arguments = "")
    {
        uint hash = CommandHash.Compute(name);
        Engine.Execute(Context, Session, new ExecuteCommandRequest(0x09, 0x0042, hash, arguments, false));
        return this;
    }

    /// <summary>Sends a raw hash, for the HELP catch-all and unknown-name probes.</summary>
    public ConsoleFixture SendHash(uint hash, string arguments = "")
    {
        Tick();
        Engine.Execute(Context, Session, new ExecuteCommandRequest(0x09, 0x0042, hash, arguments, false));
        return this;
    }

    private static byte[] Bytes(Action<Cranberry.Protocol.PacketWriter> write)
    {
        using Cranberry.Protocol.PacketWriter writer = new();
        write(writer);
        return writer.Written.ToArray();
    }
}
