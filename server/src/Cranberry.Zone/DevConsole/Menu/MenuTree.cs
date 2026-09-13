namespace Cranberry.Zone.DevConsole.Menu;

/// <summary>What one menu row is.</summary>
public enum MenuKind : byte
{
    /// <summary>Walks into a submenu. Value column <c>&gt;</c>.</summary>
    Submenu = 0,

    /// <summary>Runs a command with fixed arguments. No value column of its own.</summary>
    Action = 1,

    /// <summary>Flips something. Value column <c>[ON]</c> / <c>[OFF]</c>.</summary>
    Toggle = 2,

    /// <summary>
    /// Runs with one of a cycle of values. Value column <c>&lt; 2500 &gt;</c>: the value shown is
    /// what the next select will use, and selecting advances the cycle afterwards.
    /// </summary>
    Value = 3,

    /// <summary>Asks for the command's arguments one at a time, then runs it.</summary>
    Prompt = 4,

    /// <summary>Changes one of the menu's own Settings; never touches the world.</summary>
    Setting = 5,
}

/// <summary>
/// One node of the menu tree. The tree is data: nothing here knows how to draw itself or how to
/// run anything, which is what lets the renderer, the state machine and <c>/help</c> all read the
/// same declaration (design §2.5, §4.4).
/// </summary>
public sealed record MenuNode
{
    /// <summary>Stable dotted id, e.g. <c>player.god</c>. Cursor memory and live values key on it.</summary>
    public required string Id { get; init; }

    /// <summary>What the row says. At most 26 characters at W = 46.</summary>
    public required string Label { get; init; }

    /// <summary>What the row is.</summary>
    public MenuKind Kind { get; init; } = MenuKind.Action;

    /// <summary>The lowest tier that may see and run the row; below it the row is hidden.</summary>
    public ConsoleTier Tier { get; init; } = ConsoleTier.Player;

    /// <summary>The match phase the row needs; outside it the row draws its marker and refuses.</summary>
    public MatchGate Gate { get; init; } = MatchGate.Any;

    /// <summary>Children, for <see cref="MenuKind.Submenu"/>.</summary>
    public IReadOnlyList<MenuNode> Children { get; init; } = [];

    /// <summary>The console command this row runs, by name.</summary>
    public string? CommandName { get; init; }

    /// <summary>The argument tail the row runs with, e.g. <c>spawn</c> for <c>/target spawn</c>.</summary>
    public string BoundArgs { get; init; } = string.Empty;

    /// <summary>For a toggle: the tail that turns it on.</summary>
    public string OnArgs { get; init; } = "on";

    /// <summary>For a toggle: the tail that turns it off.</summary>
    public string OffArgs { get; init; } = "off";

    /// <summary>For a value row: the cycle, first entry shown first.</summary>
    public string[] Presets { get; init; } = [];

    /// <summary>For a Setting row: which <see cref="ConsoleSettings"/> member it moves.</summary>
    public string? SettingKey { get; init; }

    /// <summary>Non-null greys the row <c>(n/a)</c> and explains itself when selected.</summary>
    public string? NotYet { get; init; }

    /// <summary>The info line's description; falls back to the bound command's summary.</summary>
    public string? Summary { get; init; }

    /// <summary>The info line's typed form; falls back to <c>/name args</c>.</summary>
    public string? TypedForm { get; init; }

    /// <summary>True when this row cannot be walked into.</summary>
    public bool IsLeaf => Kind != MenuKind.Submenu;
}

/// <summary>
/// The whole menu of design §2.5, built once per registry.
/// <para>
/// <b>Every leaf is a registered command</b> - that is the "one engine, two front doors" rule: the
/// menu never has a private way to do something, it types the same line the owner could have typed,
/// and the info line shows him that line. A leaf whose command is missing from the registry (a
/// backend lane has not landed it yet) is greyed rather than dropped, so the tree is a complete map
/// of the intent and never a lie about what exists.
/// </para>
/// </summary>
public sealed class MenuTree
{
    private readonly Dictionary<string, MenuNode> _byId = [];

    private MenuTree(MenuNode root, CommandRegistry registry)
    {
        Root = root;
        Registry = registry;
        Index(root);
    }

    /// <summary>The root node. Its children are the eleven categories.</summary>
    public MenuNode Root { get; }

    /// <summary>The registry every leaf resolves against.</summary>
    public CommandRegistry Registry { get; }

    /// <summary>The node with this id, or null.</summary>
    public MenuNode? ById(string? id) => id is null ? null : _byId.GetValueOrDefault(id);

    /// <summary>The command a leaf runs, or null when it is not registered (yet).</summary>
    public ConsoleCommand? CommandOf(MenuNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.CommandName is null ? null : Registry.ByName(node.CommandName);
    }

    /// <summary>
    /// The reason a row is unavailable, or null when it is live: the node's own reason, the
    /// command's <c>NotYet</c>, or the fact that no such command is registered.
    /// </summary>
    public string? NotYetReason(MenuNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.NotYet is not null)
        {
            return node.NotYet;
        }

        if (node.Kind == MenuKind.Submenu || node.SettingKey is not null)
        {
            // A submenu runs nothing, and a Settings row moves this session's own frame, not the
            // world - neither needs a registered command behind it.
            return null;
        }

        if (node.CommandName is null)
        {
            return "no command bound";
        }

        ConsoleCommand? command = Registry.ByName(node.CommandName);
        return command is null ? $"/{node.CommandName} is not registered on this build" : command.NotYet;
    }

    /// <summary>True when the client refused the name this leaf types.</summary>
    public bool IsRefused(MenuNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.CommandName is not null && Registry.IsNameRefused(node.CommandName);
    }

    /// <summary>
    /// The children a caller of this tier may see. Rows above the tier are hidden, or greyed when
    /// the caller is an Owner who turned <c>Show all rows</c> on (design §2.7).
    /// </summary>
    public IReadOnlyList<MenuNode> VisibleChildren(MenuNode node, ConsoleTier tier, bool showAll)
    {
        ArgumentNullException.ThrowIfNull(node);
        return showAll ? node.Children : [.. node.Children.Where(c => c.Tier <= tier)];
    }

    /// <summary>The path of labels from the root to a node, for the breadcrumb.</summary>
    public IReadOnlyList<string> LabelPath(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        List<string> labels = [];
        foreach (string id in ids)
        {
            MenuNode? node = ById(id);
            if (node is not null)
            {
                labels.Add(node.Label);
            }
        }

        return labels;
    }

    /// <summary>The node a path of ids names, or the root when the path is empty or broken.</summary>
    public MenuNode Resolve(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        MenuNode node = Root;
        foreach (string id in ids)
        {
            MenuNode? child = node.Children.FirstOrDefault(c => c.Id == id);
            if (child is null)
            {
                return node;
            }

            node = child;
        }

        return node;
    }

    private void Index(MenuNode node)
    {
        _byId[node.Id] = node;
        foreach (MenuNode child in node.Children)
        {
            Index(child);
        }
    }

    /// <summary>Builds the tree of design §2.5 against a registry.</summary>
    public static MenuTree Build(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        MenuNode root = new()
        {
            Id = "root",
            Label = "root",
            Kind = MenuKind.Submenu,
            Children =
            [
                Player(),
                Weapons(),
                Items(),
                Vehicles(),
                Loot(),
                Match(),
                World(),
                Players(),
                Debug(),
                Info(),
                Windows(),
                Settings(),
            ],
        };

        return new MenuTree(root, registry);
    }

    private static MenuNode Player() => new()
    {
        Id = "player",
        Label = "Player",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "godmode, heal, tp, where...",
        Children =
        [
            new MenuNode
            {
                Id = "player.god", Label = "God mode", Kind = MenuKind.Toggle,
                Tier = ConsoleTier.Tester, CommandName = "godmode",
                Summary = "ignore all damage to you",
            },
            new MenuNode
            {
                Id = "player.heal", Label = "Heal", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "heal",
            },
            new MenuNode
            {
                Id = "player.hurt", Label = "Hurt", Kind = MenuKind.Value,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "hurt",
                Presets = ["2500", "100", "1000", "9999"],
            },
            new MenuNode
            {
                Id = "player.kill", Label = "Kill me", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "kill",
            },
            new MenuNode
            {
                Id = "player.where", Label = "Where am I", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, CommandName = "where",
            },
            new MenuNode
            {
                Id = "player.tp", Label = "Teleport", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester,
                Summary = "coordinates, drop point, staging, loot areas",
                Children =
                [
                    new MenuNode
                    {
                        Id = "player.tp.xyz", Label = "To coordinates...", Kind = MenuKind.Prompt,
                        Tier = ConsoleTier.Tester, CommandName = "tp",
                    },
                    new MenuNode
                    {
                        Id = "player.tp.back", Label = "Back to last", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Tester, CommandName = "tp", BoundArgs = "back",
                    },
                    new MenuNode
                    {
                        Id = "player.tp.drop", Label = "Drop point", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Tester, CommandName = "tp", BoundArgs = "drop",
                    },
                    new MenuNode
                    {
                        Id = "player.tp.staging", Label = "Staging spawn", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Tester, CommandName = "tp", BoundArgs = "staging",
                    },
                ],
            },
            new MenuNode
            {
                Id = "player.up", Label = "Hover +50 m", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "up",
            },
            new MenuNode
            {
                Id = "player.chute", Label = "Parachute", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "chute",
            },
            new MenuNode
            {
                Id = "player.speed", Label = "Speed", Kind = MenuKind.Value,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "speed",
                Presets = ["1.0", "1.5", "2.0", "0.5"],
            },
            new MenuNode
            {
                Id = "player.noclip", Label = "Noclip", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester,
                NotYet = "the August client has no server-driven noclip",
                Summary = "no server control at 1148",
            },
        ],
    };

    private static MenuNode Weapons() => new()
    {
        Id = "weapons",
        Label = "Weapons",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Tester,
        Summary = "give a gun, ammo, skins",
        Children =
        [
            new MenuNode
            {
                Id = "weapons.give", Label = "Give weapon", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
                Summary = "the roster, with an empty magazine",
                Children = [.. WeaponRoster()],
            },
            new MenuNode
            {
                Id = "weapons.ammo", Label = "Infinite ammo", Kind = MenuKind.Toggle,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "infammo",
                NotYet = "the magazine counter is not server-side yet",
            },
            new MenuNode
            {
                Id = "weapons.wield", Label = "Wield", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "wield",
                NotYet = "the wave-10 wield sequence freezes input",
            },
            new MenuNode
            {
                Id = "weapons.skins", Label = "Skins", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "skin",
                NotYet = "the wardrobe path is not wired to the console yet",
            },
        ],
    };

    private static IEnumerable<MenuNode> WeaponRoster()
    {
        (string Id, string Label)[] roster =
        [
            ("ar15", "AR-15"), ("ak47", "AK-47"), ("308", ".308 hunting rifle"),
            ("12ga", "Shotgun 12ga"), ("44", ".44 magnum"), ("1911", "M1911"),
            ("m9", "M9"), ("r380", "R380"), ("machete", "Machete"), ("hatchet", "Hatchet"),
        ];

        foreach ((string id, string label) in roster)
        {
            yield return new MenuNode
            {
                Id = $"weapons.give.{id}",
                Label = label,
                Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester,
                Gate = MatchGate.InMatch,
                CommandName = "give",
                BoundArgs = id,
            };
        }
    }

    private static MenuNode Items() => new()
    {
        Id = "items",
        Label = "Items",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "give, kits, drop, inventory",
        Children =
        [
            new MenuNode
            {
                Id = "items.give", Label = "Give item...", Kind = MenuKind.Prompt,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "give",
            },
            new MenuNode
            {
                Id = "items.kits", Label = "Kits", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
                Children =
                [
                    Kit("pvp", "PvP kit"), Kit("guns", "Guns"), Kit("ammo", "Ammo"),
                    Kit("meds", "Medical"), Kit("armour", "Armour"),
                ],
            },
            new MenuNode
            {
                Id = "items.drop", Label = "Drop in hand", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, Gate = MatchGate.InMatch, CommandName = "drop",
                BoundArgs = "hand",
            },
            new MenuNode
            {
                Id = "items.inv", Label = "Show inventory", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, CommandName = "inv",
            },
        ],
    };

    private static MenuNode Kit(string id, string label) => new()
    {
        Id = $"items.kits.{id}",
        Label = label,
        Kind = MenuKind.Action,
        Tier = ConsoleTier.Tester,
        Gate = MatchGate.InMatch,
        CommandName = "kit",
        BoundArgs = id,
    };

    private static MenuNode Vehicles() => new()
    {
        Id = "vehicles",
        Label = "Vehicles",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "spawn, enter, fuel, census",
        Children =
        [
            Car("offroader", "Spawn Off-Roader"),
            Car("pickup", "Spawn Pickup"),
            Car("policecar", "Spawn Police car"),
            Car("atv", "Spawn ATV"),
            new MenuNode
            {
                Id = "vehicles.enter", Label = "Enter nearest", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "enter",
            },
            new MenuNode
            {
                Id = "vehicles.exit", Label = "Exit", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "exit",
            },
            new MenuNode
            {
                Id = "vehicles.fuel", Label = "Fuel", Kind = MenuKind.Value,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "fuel",
                Presets = ["100", "50", "10"],
            },
            new MenuNode
            {
                Id = "vehicles.cars", Label = "Census", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, CommandName = "cars",
            },
        ],
    };

    private static MenuNode Car(string id, string label) => new()
    {
        Id = $"vehicles.{id}",
        Label = label,
        Kind = MenuKind.Action,
        Tier = ConsoleTier.Tester,
        Gate = MatchGate.InMatch,
        CommandName = "car",
        BoundArgs = id,
    };

    private static MenuNode Loot() => new()
    {
        Id = "loot",
        Label = "Loot",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "spawn on the ground, find, stats",
        Children =
        [
            new MenuNode
            {
                Id = "loot.spawn", Label = "Spawn item on ground...", Kind = MenuKind.Prompt,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "loot",
                BoundArgs = "spawn",
            },
            new MenuNode
            {
                Id = "loot.ring", Label = "Spawn the dev ring", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "loot",
                BoundArgs = "ring",
            },
            new MenuNode
            {
                Id = "loot.find", Label = "Find item...", Kind = MenuKind.Prompt,
                Tier = ConsoleTier.Player, Gate = MatchGate.InMatch, CommandName = "loot",
                BoundArgs = "find",
            },
            new MenuNode
            {
                Id = "loot.stats", Label = "Stats", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, CommandName = "loot", BoundArgs = "stats",
            },
        ],
    };

    private static MenuNode Match() => new()
    {
        Id = "match",
        Label = "Match",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "start, drop, end, status, gas",
        Children =
        [
            new MenuNode
            {
                Id = "match.start", Label = "Start now", Kind = MenuKind.Action,
                Tier = ConsoleTier.Owner, Gate = MatchGate.MenuOnly, CommandName = "match",
                BoundArgs = "start",
            },
            new MenuNode
            {
                Id = "match.drop", Label = "Drop now", Kind = MenuKind.Action,
                Tier = ConsoleTier.Owner, CommandName = "match", BoundArgs = "drop",
                NotYet = "BeginDrop is still inline in SendLobbyHud",
            },
            new MenuNode
            {
                Id = "match.lobby", Label = "Back to lobby", Kind = MenuKind.Action,
                Tier = ConsoleTier.Owner, CommandName = "match", BoundArgs = "lobby",
            },
            new MenuNode
            {
                Id = "match.end", Label = "End match", Kind = MenuKind.Action,
                Tier = ConsoleTier.Owner, Gate = MatchGate.InMatch, CommandName = "match",
                BoundArgs = "end",
            },
            new MenuNode
            {
                Id = "match.status", Label = "Status", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, CommandName = "match", BoundArgs = "status",
            },
            new MenuNode
            {
                Id = "match.gas", Label = "Gas", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Owner, Gate = MatchGate.InMatch,
                Summary = "start, stop, status",
                Children =
                [
                    new MenuNode
                    {
                        Id = "match.gas.start", Label = "Start gas", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Owner, Gate = MatchGate.InMatch, CommandName = "gas",
                        BoundArgs = "start",
                    },
                    new MenuNode
                    {
                        Id = "match.gas.stop", Label = "Stop gas", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Owner, Gate = MatchGate.InMatch, CommandName = "gas",
                        BoundArgs = "stop",
                    },
                    new MenuNode
                    {
                        Id = "match.gas.status", Label = "Status", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Owner, Gate = MatchGate.InMatch, CommandName = "gas",
                        BoundArgs = "status",
                    },
                    new MenuNode
                    {
                        Id = "match.gas.next", Label = "Next ring now", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Owner, Gate = MatchGate.InMatch, CommandName = "gas",
                        BoundArgs = "next",
                        NotYet = "GasController.Skew is not exposed to the console yet",
                    },
                ],
            },
        ],
    };

    private static MenuNode World() => new()
    {
        Id = "world",
        Label = "World",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "doors, practice targets",
        Children =
        [
            new MenuNode
            {
                Id = "world.doors.open", Label = "Open doors near", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "doors",
                BoundArgs = "open",
            },
            new MenuNode
            {
                Id = "world.doors.close", Label = "Close doors near", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "doors",
                BoundArgs = "close",
            },
            new MenuNode
            {
                Id = "world.doors.toggle", Label = "Toggle nearest door", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "doors",
                BoundArgs = "toggle",
            },
            new MenuNode
            {
                Id = "world.target", Label = "Practice target", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch,
                Children =
                [
                    new MenuNode
                    {
                        Id = "world.target.spawn", Label = "Spawn a target", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "target",
                        BoundArgs = "spawn",
                    },
                    new MenuNode
                    {
                        Id = "world.target.status", Label = "Status", Kind = MenuKind.Action,
                        Tier = ConsoleTier.Tester, Gate = MatchGate.InMatch, CommandName = "target",
                        BoundArgs = "status",
                    },
                ],
            },
        ],
    };

    private static MenuNode Players() => new()
    {
        Id = "players",
        Label = "Players",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "who is online, announce, evict",
        Children =
        [
            new MenuNode
            {
                Id = "players.all", Label = "All players", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Player,
                Children =
                [
                    new MenuNode
                    {
                        Id = "players.all.announce", Label = "Announce...", Kind = MenuKind.Prompt,
                        Tier = ConsoleTier.Owner, CommandName = "announce",
                    },
                    new MenuNode
                    {
                        Id = "players.all.evict", Label = "Evict...", Kind = MenuKind.Prompt,
                        Tier = ConsoleTier.Owner, CommandName = "evict",
                        NotYet = "there is no session registry yet",
                    },
                    new MenuNode
                    {
                        Id = "players.all.tier", Label = "Tier...", Kind = MenuKind.Prompt,
                        Tier = ConsoleTier.Owner, CommandName = "tier",
                        NotYet = "there is no session registry yet",
                    },
                    new MenuNode
                    {
                        Id = "players.all.tphere", Label = "Teleport here...", Kind = MenuKind.Prompt,
                        Tier = ConsoleTier.Owner, CommandName = "tphere",
                        NotYet = "there is no session registry yet",
                    },
                ],
            },
            new MenuNode
            {
                Id = "players.list", Label = "List online", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, CommandName = "players",
            },
        ],
    };

    private static MenuNode Debug() => new()
    {
        Id = "debug",
        Label = "Debug",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "dumps, watchdog, wire log, raw",
        Children =
        [
            new MenuNode
            {
                Id = "debug.where", Label = "Where am I", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, CommandName = "where",
            },
            new MenuNode
            {
                Id = "debug.pos", Label = "Dump position stream", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "dump", BoundArgs = "pos",
            },
            new MenuNode
            {
                Id = "debug.inv", Label = "Dump inventory", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "dump", BoundArgs = "inv",
            },
            new MenuNode
            {
                Id = "debug.self", Label = "Dump self record", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "dump", BoundArgs = "self",
            },
            new MenuNode
            {
                Id = "debug.watchdog", Label = "Watchdog", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "watchdog",
            },
            new MenuNode
            {
                Id = "debug.log", Label = "Wire log", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "log", BoundArgs = "wire",
            },
            new MenuNode
            {
                Id = "debug.raw", Label = "Send raw hex...", Kind = MenuKind.Prompt,
                Tier = ConsoleTier.Owner, CommandName = "raw",
            },
            new MenuNode
            {
                Id = "debug.replay", Label = "Replay last packet", Kind = MenuKind.Action,
                Tier = ConsoleTier.Owner, CommandName = "replay",
                NotYet = "there is no per-session s2c ring yet",
            },
        ],
    };

    private static MenuNode Info() => new()
    {
        Id = "info",
        Label = "Info",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "build, options, registry, surfaces",
        Children =
        [
            new MenuNode
            {
                Id = "info.show", Label = "Show info", Kind = MenuKind.Action,
                Tier = ConsoleTier.Player, CommandName = "info",
            },
            new MenuNode
            {
                Id = "info.probe", Label = "Surface probe", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "surface", BoundArgs = "probe",
            },
            new MenuNode
            {
                Id = "info.surface", Label = "Surface", Kind = MenuKind.Value,
                Tier = ConsoleTier.Tester, CommandName = "surface",
                Presets = ["print", "chat", "chat0", "alert", "lua"],
                Summary = "print|chat|chat0|alert|lua",
            },
        ],
    };

    /// <summary>
    /// The Windows branch: R6 recommendation (a), the client's own windows opened by
    /// <c>Ui.ExecuteScript</c>. Every leaf types a <c>/win &lt;alias&gt;</c> that
    /// <see cref="WindowScripts.All"/> also lists, so the two front doors stay one engine.
    /// </summary>
    private static MenuNode Windows() => new()
    {
        Id = "windows",
        Label = "Windows",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Tester,
        Summary = "open the client's own windows (Ui.ExecuteScript)",
        Children =
        [
            new MenuNode
            {
                Id = "windows.inv", Label = "Inventory", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester,
                Summary = "show, hide, or the C++-proven toggle",
                Children =
                [
                    Window("windows.inv.show", "Show inventory", "inventory"),
                    Window("windows.inv.hide", "Hide inventory", "inventoryoff"),
                    Window("windows.inv.toggle", "Toggle inventory", "invtoggle"),
                ],
            },
            new MenuNode
            {
                Id = "windows.screens", Label = "Screens", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester,
                Summary = "settings, keys, escape, death list, rewards...",
                Children =
                [
                    Window("windows.screens.settings", "Settings", "settings"),
                    Window("windows.screens.keys", "Key bindings", "keys"),
                    Window("windows.screens.escape", "Escape menu", "escape"),
                    Window("windows.screens.deaths", "Death list", "deaths"),
                    Window("windows.screens.rewards", "Rewards", "rewards"),
                    Window("windows.screens.grinder", "Grinder", "grinder"),
                    Window("windows.screens.credits", "Credits", "credits"),
                    Window("windows.screens.notes", "Notes", "notes"),
                    Window("windows.screens.builder", "Builder", "builder"),
                    Window("windows.screens.container", "Container", "container"),
                    Window("windows.screens.respawn", "Respawn map", "respawnmap"),
                    Window("windows.screens.browser", "Browser", "browser"),
                    Window("windows.screens.help", "Help screen", "helpscreen"),
                    Window("windows.screens.market", "Marketplace", "market"),
                ],
            },
            new MenuNode
            {
                Id = "windows.hud", Label = "HUD", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester,
                Summary = "the whole HUD, the map, the loading screen",
                Children =
                [
                    Window("windows.hud.on", "HUD on", "hud"),
                    Window("windows.hud.off", "HUD off", "hudoff"),
                    Window("windows.hud.hideall", "Hide all UI", "hideall"),
                    Window("windows.hud.restoreall", "Restore all UI", "restoreall"),
                    Window("windows.hud.map", "Toggle map", "maptoggle"),
                    Window("windows.hud.mapoff", "Map off", "mapoff"),
                    Window("windows.hud.loading", "Loading screen off", "loadingoff"),
                    Window("windows.hud.chat", "Cycle chat tabs", "chattabs"),
                ],
            },
            new MenuNode
            {
                Id = "windows.console", Label = "Console pane", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester,
                Summary = "the R5 gate: can the server open the pane",
                Children =
                [
                    Window("windows.console.on", "Console on", "console"),
                    Window("windows.console.off", "Console off", "consoleoff"),
                    Window("windows.console.unlock", "Unlock console", "conunlock", ConsoleTier.Owner),
                    Window("windows.console.lock", "Lock console", "conlock", ConsoleTier.Owner),
                ],
            },
            new MenuNode
            {
                Id = "windows.cranberry", Label = "Cranberry menu", Kind = MenuKind.Submenu,
                Tier = ConsoleTier.Tester,
                Summary = "R6 (b'): our own window, drawn by the client-side CranberryMenu.lua",
                Children =
                [
                    Window("windows.cranberry.toggle", "Toggle", "menu"),
                    Window("windows.cranberry.open", "Open", "menuon"),
                    Window("windows.cranberry.close", "Close", "menuoff"),
                    Window("windows.cranberry.ping", "Ping the script", "menuping"),
                    Window("windows.cranberry.diag", "Diagnostics", "menudiag", ConsoleTier.Owner),
                    Window("windows.cranberry.reload", "Reload the .lua", "menureload", ConsoleTier.Owner),
                ],
            },
            new MenuNode
            {
                Id = "windows.probe", Label = "Probe the route", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "win", BoundArgs = "probe",
                Summary = "four sends 1 s apart, with a positive and a negative control",
            },
            new MenuNode
            {
                Id = "windows.list", Label = "List windows", Kind = MenuKind.Action,
                Tier = ConsoleTier.Tester, CommandName = "win", BoundArgs = "list",
                Summary = "every alias, its Lua name and its evidence mark",
            },
            new MenuNode
            {
                Id = "windows.raw", Label = "Raw script...", Kind = MenuKind.Prompt,
                Tier = ConsoleTier.Owner, CommandName = "win", BoundArgs = "raw",
                Summary = "any Object.Method; asks once, and refuses exit/disconnect names",
            },
        ],
    };

    private static MenuNode Window(
        string id,
        string label,
        string alias,
        ConsoleTier tier = ConsoleTier.Tester) => new()
        {
            Id = id,
            Label = label,
            Kind = MenuKind.Action,
            Tier = tier,
            CommandName = "win",
            BoundArgs = alias,
            Summary = WindowScripts.ByAlias(alias)?.Script ?? alias,
        };

    private static MenuNode Settings() => new()
    {
        Id = "settings",
        Label = "Settings",
        Kind = MenuKind.Submenu,
        Tier = ConsoleTier.Player,
        Summary = "the menu's own geometry and habits",
        Children =
        [
            Setting("settings.rows", "Rows", "rows", MenuKind.Value, ["10", "12", "14", "16", "6", "8"]),
            Setting("settings.width", "Width", "width", MenuKind.Value, ["46", "50", "54", "60", "40", "44"]),
            Setting("settings.keys", "Hint keys", "keys", MenuKind.Toggle, []),
            Setting("settings.typed", "Typed hints", "typed", MenuKind.Toggle, []),
            Setting("settings.confirm", "Confirms", "confirm", MenuKind.Toggle, []),
            Setting("settings.wrap", "Wrap around", "wrap", MenuKind.Toggle, []),
            Setting("settings.suppress", "Frame suppression", "suppress", MenuKind.Toggle, []),
            Setting("settings.remember", "Remember path", "remember", MenuKind.Toggle, []),
            Setting("settings.theme", "Theme", "theme", MenuKind.Value, ["plain", "heavy"]),
            Setting("settings.surface", "Surface", "surface", MenuKind.Value, ["print", "chat", "chat0", "alert", "lua"]),
            Setting("settings.live", "Live refresh", "live", MenuKind.Toggle, []),
            Setting("settings.showall", "Show all rows", "showall", MenuKind.Toggle, []),
        ],
    };

    private static MenuNode Setting(string id, string label, string key, MenuKind kind, string[] presets) => new()
    {
        Id = id,
        Label = label,
        Kind = kind == MenuKind.Value ? MenuKind.Value : MenuKind.Toggle,
        Tier = ConsoleTier.Player,
        SettingKey = key,
        Presets = presets,
        TypedForm = key == "surface" ? "/surface chat" : $"(menu only: {label.ToLowerInvariant()})",
        Summary = key == "surface" ? "print|chat|chat0|alert|lua" : "menu setting, this session only",
    };
}
