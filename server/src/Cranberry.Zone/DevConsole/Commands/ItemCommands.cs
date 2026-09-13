namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>Development grants and drops through the same inventory paths as the client.</summary>
public static class ItemCommands
{
    public const string InventoryLaneReason = "Inventory is required";
    public static IReadOnlyList<string> KitNames { get; } = ["pvp", "guns", "ammo", "meds", "armour", "throwables"];

    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        registry.Register(new ConsoleCommand { Name = "give", Group = "items", Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch, Usage = "/give <item|id> [count=1]",
            Summary = "grant an item; normal capacity, equip and skin rules apply", Run = Give });
        registry.Register(new ConsoleCommand { Name = "kit", Group = "items", Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch, Usage = "/kit [pvp|guns|ammo|meds|armour|throwables]",
            Summary = "grant a testing kit, with a backpack before the combat supplies", Run = Kit });
        registry.Register(new ConsoleCommand { Name = "ammo", Group = "items", Tier = ConsoleTier.Tester,
            Gate = MatchGate.InMatch, Usage = "/ammo [count=100]",
            Summary = "give reserve ammunition for the weapon in your hand", Run = Ammo });
        registry.Register(new ConsoleCommand { Name = "drop", Group = "items", Tier = ConsoleTier.Player,
            Gate = MatchGate.InMatch, Usage = "/drop [hand|guns|loadout-slot]",
            Summary = "drop your hand, every gun, or one loadout slot", Run = Drop });
        registry.Register(new ConsoleCommand { Name = "inv", Group = "items", Tier = ConsoleTier.Player,
            Usage = "/inv", Summary = "list worn and bagged items, slots, identifiers and bulk",
            MenuPaths = ["Items > Show inventory"], Run = c => c.Ctx.Inventory?.Invoke() ?? ConsoleReply.Failed("inventory unavailable") });
        return registry;
    }

    private static ConsoleReply Give(CommandCall call)
    {
        int? id = call.Line.Item(0, ItemNames.Resolve);
        int count = 1;
        if (call.Count is < 1 or > 2 || id is null or <= 0
            || (call.Count == 2 && !call.Line.TryInt(1, out count)) || count is < 1 or > 1000)
            return call.Usage("use an item name or definition id and a count from 1 to 1000");
        return call.Ctx.GiveItem?.Invoke((uint)id.Value, (uint)count) ?? ConsoleReply.Failed("inventory unavailable");
    }

    private static ConsoleReply Kit(CommandCall call)
    {
        string name = call.Line.Word(0, "pvp");
        if (call.Count > 1 || !KitNames.Contains(name)) return call.Usage("kits: " + string.Join(", ", KitNames));
        return call.Ctx.GiveKit?.Invoke(name) ?? ConsoleReply.Failed("inventory unavailable");
    }

    private static ConsoleReply Ammo(CommandCall call)
    {
        int count = 100;
        if (call.Count > 1 || (call.Count == 1 && !call.Line.TryInt(0, out count)) || count is < 1 or > 1000)
            return call.Usage("count must be between 1 and 1000");
        return call.Ctx.GiveAmmo?.Invoke((uint)count) ?? ConsoleReply.Failed("inventory unavailable");
    }

    private static ConsoleReply Drop(CommandCall call)
    {
        if (call.Count > 1) return call.Usage();
        return call.Ctx.DropItem?.Invoke(call.Line.Word(0, "hand")) ?? ConsoleReply.Failed("inventory unavailable");
    }
}
