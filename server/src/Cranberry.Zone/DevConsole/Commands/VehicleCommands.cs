namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// The Vehicles group: spawn one beside you, get in, get out, fill the tank, count the fleet.
/// <para>
/// The roster words are matched here rather than in the world, because the world knows only
/// <c>Vehicles.txt</c> row ids (OffRoader 1, PickupTruck 2, PoliceCar 3, ATV 5 -
/// <c>Vehicles/VehicleDefinitions.cs:203</c>). Every spelling the owner is likely to type maps to
/// one of those four, and a near miss is answered with the names that would have worked rather than
/// a bare refusal. Common retail names such as <c>jeep</c> and <c>copcar</c> are accepted.
/// </para>
/// </summary>
public static class VehicleCommands
{
    /// <summary>Every accepted spelling, in the order <c>/help</c> and the refusal line list them.</summary>
    public static IReadOnlyList<(string Word, uint VehicleId, string Label)> Roster { get; } =
    [
        ("offroader", 1u, "Off-Roader"),
        ("off", 1u, "Off-Roader"),
        ("jeep", 1u, "Off-Roader"),
        ("off-roader", 1u, "Off-Roader"),
        ("1", 1u, "Off-Roader"),
        ("pickup", 2u, "Pickup"),
        ("truck", 2u, "Pickup"),
        ("pickuptruck", 2u, "Pickup"),
        ("2", 2u, "Pickup"),
        ("policecar", 3u, "Police car"),
        ("police", 3u, "Police car"),
        ("copcar", 3u, "Police car"),
        ("3", 3u, "Police car"),
        ("atv", 5u, "ATV"),
        ("quad", 5u, "ATV"),
        ("5", 5u, "ATV"),
    ];

    /// <summary>The four canonical words, for a usage line.</summary>
    public static IReadOnlyList<string> Canonical { get; } = ["offroader", "pickup", "policecar", "atv"];

    /// <summary>The <c>Vehicles.txt</c> row a typed word names, or null.</summary>
    public static uint? ResolveVehicle(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        string want = word.Trim();
        foreach ((string spelling, uint id, _) in Roster)
        {
            if (string.Equals(spelling, want, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Adds every Vehicles-group command to the registry, in <c>/help</c> order.</summary>
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new ConsoleCommand
        {
            Name = "car",
            Aliases = ["spawncar", "spawnvehicle"],
            Group = "vehicles",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/car [offroader|pickup|policecar|atv]",
            Summary = "spawn a vehicle beside you",
            Args = [new ArgSpec("kind", ArgKind.Enum, "offroader", "which vehicle", [.. Canonical])],
            Examples = ["/car atv", "/car police", "/spawnvehicle jeep"],
            Detail = ["It is placed three units to your east, parked, with a full tank.",
                "Aliases: jeep/off-roader, pickuptruck/truck, police/copcar, quad; ids 1/2/3/5.",
                "Land before spawning; when seated, the current vehicle position is used."],
            MenuPaths = ["Vehicles > Spawn ..."],
            Run = Spawn,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "enter",
            Group = "vehicles",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/enter",
            Summary = "take the wheel of the nearest vehicle",
            MenuPaths = ["Vehicles > Enter nearest"],
            Run = Enter,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "exit",
            Group = "vehicles",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/exit",
            Summary = "get out of the vehicle",
            MenuPaths = ["Vehicles > Exit"],
            Run = Exit,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "fuel",
            Group = "vehicles",
            Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch,
            Usage = "/fuel [pct=100]",
            Summary = "refuel the vehicle you are in, or the nearest one",
            Args = [new ArgSpec("pct", ArgKind.Int, "100", "percent of the tank")],
            MenuPaths = ["Vehicles > Fuel"],
            Run = Fuel,
        });

        registry.Register(new ConsoleCommand
        {
            Name = "cars",
            Group = "vehicles",
            Tier = ConsoleTier.Player,
            Usage = "/cars",
            Summary = "the fleet census: planned, streamed, occupied",
            MenuPaths = ["Vehicles > Census"],
            Run = Census,
        });

        return registry;
    }

    private static ConsoleReply Spawn(CommandCall call)
    {
        if (call.Count > 1)
            return call.Usage("choose one vehicle kind; for example /car policecar");

        if (call.Ctx.SpawnVehicle is not { } spawn)
        {
            return ConsoleReply.Failed("not available yet -- no vehicle spawner is wired on this build");
        }

        string word = call.Line.Word(0, "offroader");
        if (word is "list" or "help")
            return ConsoleReply.Plain(["* Vehicles: 1 offroader (jeep), 2 pickup (truck), 3 policecar (copcar), 5 atv (quad)",
                "  Native: /vehicle <id> [reward set id=0] [auto mount=0] [faction id=0]"]);
        if (ResolveVehicle(word) is null)
        {
            return ConsoleReply.Failed($"not a name: '{word}' -- {string.Join(' ', Canonical)}");
        }

        return spawn(word);
    }

    private static ConsoleReply Enter(CommandCall call) =>
        call.Ctx.EnterVehicle is { } enter
            ? enter()
            : ConsoleReply.Failed("no vehicles streamed -- the fleet is built at the landing");

    private static ConsoleReply Exit(CommandCall call) =>
        call.Ctx.ExitVehicle is { } exit
            ? exit()
            : ConsoleReply.Failed("no vehicles streamed -- the fleet is built at the landing");

    private static ConsoleReply Fuel(CommandCall call)
    {
        if (call.Ctx.Refuel is not { } refuel)
        {
            return ConsoleReply.Failed("no vehicles streamed -- the fleet is built at the landing");
        }

        int percent = 100;
        if (call.Count > 1 || (call.Count == 1 && !call.Line.TryInt(0, out percent)) || percent is < 0 or > 100)
        {
            return call.Usage($"'{call.Line.Word(0)}' is not a percentage between 0 and 100");
        }

        return refuel(percent / 100f);
    }

    private static ConsoleReply Census(CommandCall call) =>
        call.Ctx.VehicleCensus is { } census
            ? census()
            : ConsoleReply.Failed("no fleet yet -- vehicles are planned at the landing");
}
