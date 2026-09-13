namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// The Loot group - one command with four sub-verbs, because that is how the owner's own server
/// spells it and because a sub-verb costs no name in the client's registry (design §2.2).
/// <para>
/// <c>spawn</c> puts one item on the ground 1.5 units ahead of you (his number, D176) through
/// <c>SpawnGroundLoot</c> + <c>SendProximateItems</c> + <c>NoteSpawned</c> with a <c>Dropped</c>
/// stream key, which is what makes a console-spawned item unevictable by the re-stream pump
/// (R3 #5, docs/02). <c>ring</c> is the existing <c>SpawnDevGroundLoot</c> circle. <c>find</c> and
/// <c>stats</c> only read.
/// </para>
/// </summary>
public static class LootCommands
{
    /// <summary>The sub-verbs, in the order an ambiguous prefix lists them.</summary>
    public static IReadOnlyList<string> SubVerbs { get; } = ["spawn", "ring", "find", "stats", "airdrop"];

    /// <summary>Metres a bare <c>/loot find</c> searches (the owner's own 200, D176).</summary>
    public const float DefaultFindRadius = 200f;

    /// <summary>Adds the Loot group to the registry.</summary>
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new ConsoleCommand
        {
            Name = "loot",
            Group = "loot",
            Tier = ConsoleTier.Player,
            Usage = "/loot spawn <item|id> [n] | ring | find <item> [m=200] | stats | airdrop",
            Summary = "ground loot: spawn, ring, find, stats, airdrop",
            Args =
            [
                new ArgSpec("verb", ArgKind.Enum, "stats", "which action", [.. SubVerbs]),
                new ArgSpec("item", ArgKind.Item, "-", "roster name or definition id"),
                new ArgSpec("n", ArgKind.Int, "1", "how many"),
            ],
            Examples =
            [
                "/loot spawn 2423 3", "/loot spawn bandage", "/loot find ar15 300", "/loot stats",
                "/loot airdrop",
            ],
            MenuPaths = ["Loot > Spawn item on ground...", "Loot > Spawn the dev ring"],
            Run = Run,
        });

        return registry;
    }

    private static ConsoleReply Run(CommandCall call)
    {
        string? verb = call.Line.SubVerb(0, SubVerbs, out string? error);
        if (error is not null)
        {
            return ConsoleReply.Failed(error);
        }

        if (verb is "spawn" or "ring")
        {
            if (call.Tier < ConsoleTier.Tester) return ConsoleReply.Refused("Tester or above");
            if (!MatchGates.Allows(MatchGate.InMatch, call.Step))
                return ConsoleReply.Refused(MatchGates.Refusal(MatchGate.InMatch, call.Step));
        }
        return verb switch
        {
            "spawn" => Spawn(call),
            "ring" => Ring(call),
            "find" => Find(call),
            "airdrop" => Airdrop(call),
            _ => Stats(call),
        };
    }

    private static ConsoleReply Spawn(CommandCall call)
    {
        if (call.Ctx.SpawnLoot is not { } spawn)
        {
            return ConsoleReply.Failed("not available yet -- no ground-loot spawner is wired on this build");
        }

        string? typed = call.Line.Word(1);
        if (typed is null)
        {
            return call.Usage("name an item: /loot spawn <item|id> [n]");
        }

        int? definitionId = call.Line.Item(1, name => ItemNames.Resolve(name));
        if (definitionId is null or <= 0)
        {
            string? nearest = ItemNames.Nearest(typed);
            return ConsoleReply.Failed(
                nearest is null
                    ? $"unknown item '{typed}' -- /loot spawn takes a roster name or a definition id"
                    : $"unknown item '{typed}' -- did you mean {nearest}?");
        }

        int count = 1;
        if (call.Count > 3 || (call.Count == 3 && !call.Line.TryInt(2, out count)) || count is < 1 or > 100)
        {
            return call.Usage($"'{call.Line.Word(2)}' is not a count between 1 and 100");
        }

        return spawn((uint)definitionId.Value, (uint)count);
    }

    private static ConsoleReply Ring(CommandCall call) =>
        call.Ctx.SpawnLootRing is { } ring
            ? ring()
            : ConsoleReply.Failed("not available yet -- the developer ring is not wired on this build");

    private static ConsoleReply Find(CommandCall call)
    {
        if (call.Ctx.FindLoot is not { } find)
        {
            return ConsoleReply.Failed("no loot streamed yet");
        }

        string? typed = call.Line.Word(1);
        if (typed is null)
        {
            return call.Usage("name an item: /loot find <item> [m=200]");
        }

        float radius = DefaultFindRadius;
        if (call.Count > 3 || (call.Count == 3 && !call.Line.TryFloat(2, out radius)) || radius is <= 0f or > 5_000f)
        {
            return call.Usage($"'{call.Line.Word(2)}' is not a radius in metres");
        }

        return find(typed, radius);
    }

    /// <summary>
    /// D274: <c>/loot airdrop</c> - what this match's airdrop schedule has done and when the next
    /// crate is due. Read-only, and a sub-verb rather than a command of its own for the same reason
    /// the other four are: a sub-verb costs no name in the client's registry (design section 2.2).
    /// </summary>
    private static ConsoleReply Airdrop(CommandCall call) =>
        call.Ctx.AirdropStats is { } airdrops
            ? airdrops()
            : ConsoleReply.Failed("no airdrops on this build -- CRANBERRY_AIRDROPS=0, or no match is running");

    private static ConsoleReply Stats(CommandCall call) =>
        call.Ctx.LootStats is { } stats
            ? stats()
            : ConsoleReply.Failed("no loot counters yet -- nothing has been spawned on this link");
}
