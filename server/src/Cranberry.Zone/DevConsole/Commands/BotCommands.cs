using Cranberry.Zone.Combat;

namespace Cranberry.Zone.DevConsole.Commands;

public static class BotCommands
{
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        registry.Register(new ConsoleCommand
        {
            Name = "bots", Aliases = ["spawnbots"], Group = "world", Tier = ConsoleTier.Owner,
            Gate = MatchGate.InMatch,
            Usage = "/bots [spawn <count=1> [easy|normal|hard] [metres=25]|clear|freeze|resume|status]",
            Summary = "shared moving combat bots; everyone in this match can fight them",
            Examples = ["/bots 10", "/bots spawn 5 easy 30", "/bots clear"],
            Detail = ["Spawns add bots, up to 50 per match. They chase and shoot nearby players with AR-15s.",
                "New bots have 5 seconds of preparation, then staggered firing. /godmode on prevents damage while testing.",
                "freeze stops movement and firing; resume continues. Existing /target dummies stay separate.",
                "Use outdoors: terrain movement and sight checks; building navigation is not implemented."],
            Run = Run,
        });
        return registry;
    }

    private static ConsoleReply Run(CommandCall call)
    {
        string verb = call.Line.Name == "spawnbots" ? "spawn" : call.Line.Word(0) ?? "status";
        int offset = call.Line.Name == "spawnbots" ? 0 : 1;
        if (call.Line.TryInt(0, out _)) { verb = "spawn"; offset = 0; }
        if (verb is "clear" or "freeze" or "resume" or "status")
        {
            if (call.Count > 1) return call.Usage("this action takes no extra arguments");
            return call.Ctx.Bots?.Invoke(verb, 0, "normal", 25) ?? ConsoleReply.Failed("bots unavailable");
        }
        if (verb != "spawn") return call.Usage("try /bots 10, /bots freeze, /bots resume or /bots clear");
        int count = 1;
        float distance = 25;
        if (call.Count > offset + 3 || call.Count > offset && !call.Line.TryInt(offset, out count)
            || count is < 1 or > CombatBot.MaximumPerMatch)
            return call.Usage("count must be a whole number from 1 to 50");
        string difficulty = call.Line.Word(offset + 1) ?? "normal";
        if (difficulty is not ("easy" or "normal" or "hard")) return call.Usage("difficulty: easy, normal or hard");
        if (call.Count > offset + 2 && !call.Line.TryFloat(offset + 2, out distance)
            || !float.IsFinite(distance) || distance is < 5 or > 100)
            return call.Usage("spawn distance must be 5-100 metres");
        return call.Ctx.Bots?.Invoke("spawn", count, difficulty, distance) ?? ConsoleReply.Failed("bots unavailable");
    }
}
