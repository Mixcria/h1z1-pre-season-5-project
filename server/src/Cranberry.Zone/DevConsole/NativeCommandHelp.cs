namespace Cranberry.Zone.DevConsole;

/// <summary>Discovery for original commands, without replacing their client handlers.</summary>
public static class NativeCommandHelp
{
    public static IReadOnlyList<ClientRegistryEntry> Commands { get; } = ClientRegistry1148.Entries
        .Where(e => e.Name is not null && e.Kind != "cvar-static")
        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> Render(string? topic)
    {
        if (topic is not null && !int.TryParse(topic, out _))
        {
            ClientRegistryEntry? entry = Commands.FirstOrDefault(e =>
                string.Equals(e.Name, topic, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return [$"- no original August command called '{topic}'", "? /commands native"];
            return Detail(entry);
        }

        int rows = HelpFormatter.PageSize - 2;
        int pages = (Commands.Count + rows - 1) / rows;
        int page = int.TryParse(topic, out int requested) ? Math.Clamp(requested, 1, pages) : 1;
        return [
            $"Original August commands -- page {page}/{pages}; {Commands.Count} commands/aliases",
            "Client handlers retained; some require server support. /commands native <name|page>",
            .. Commands.Skip((page - 1) * rows).Take(rows).Select(e => $"/{e.Name} ({e.Kind})")
        ];
    }

    private static IReadOnlyList<string> Detail(ClientRegistryEntry entry)
    {
        string[] usage = entry.Name switch
        {
            "vehicle" => [
                "/vehicle list -- original client definition list",
                "/vehicle <id> [rewardSet=0] [autoMount=0] [faction=0]",
                "1 OffRoader; 2 PickupTruck; 3 PoliceCar; 5 ATV.",
                "Look at nearby ground after landing; the client supplies the placement.",
                "Use numeric IDs with the original parser. /car accepts friendly names."
            ],
            "item" => [
                "/item add <id> <quantity> <tint=0> [targetGuid]",
                "/item list -- current inventory; /item find <text> searches client definitions.",
                "/item drop <itemGuid> <quantity>; /item delete <itemGuid> [targetGuid].",
                "Use instance GUIDs from /item list. Only your own inventory is supported.",
                "Tint/rental overrides are unsupported; the client disables /item add all."
            ],
            "goto" or "go" or "warp" => [
                "/goto <player name|guid>; /go and /warp are original aliases.",
                "/goto player|npc <name|guid> -- targets in your current match.",
                "NPC definition and waypoint destinations are unsupported.",
                "Exit your vehicle first. /tp remains the coordinate shortcut."
            ],
            "god" => ["Original alias: gm invuln. Toggles damage protection; /god on or /god off sets it.",
                "The server implements gm invuln with the same permissions as /godmode."],
            "loc" => ["Original alias: location; reports the current server position."],
            "run" => ["/run <metres per second> -- original absolute speed command.",
                "/run default restores normal speed and footwear effects.",
                "/run without arguments displays the client's current speed."],
            _ => ["Handled by the original August client; unchanged by the server command registry.",
                "A registered client name does not establish implementation of its server actions."]
        };
        return [$"/{entry.Name} -- original August {entry.Kind}", .. usage];
    }
}
