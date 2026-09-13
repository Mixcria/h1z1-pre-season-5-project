namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>Player-facing hosted games, with authority checked by each backend action.</summary>
public static class HostedGameCommands
{
    /// <summary>Adds the hosted-game command without colliding with the client's native /hosted.</summary>
    public static CommandRegistry AddTo(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new ConsoleCommand
        {
            Name = "hostgame",
            Group = "match",
            Tier = ConsoleTier.Player,
            KeepCase = true,
            SensitiveArguments = true,
            Usage = "/hostgame panel|key|redeem|create|list|keys|invite|admininvite|revoke|close|join|start|mode|roster|announce|bring|goto|kick|spectate|fly",
            Summary = "host private games with region keys and player invitations",
            Detail =
            [
                "/hostgame key <region> <permanent|30m|2h|7d> [accountId] -- owner/admin only; /hostgame redeem <key>",
                "/hostgame create <region> <solo|duos|fives> <name>; createform adds <queueMinutes:1-60> <maxPlayers:1-150> before name; /hostgame list",
                "/hostgame invite <worldId> <duration|permanent> [accountId]; /hostgame admininvite uses the same syntax",
                "/hostgame keys [worldId] -- key IDs and status; /hostgame revoke <keyId> -- revoke access",
                "/hostgame join <worldId>; hosts: /hostgame start <worldId>, /hostgame close <worldId>",
                "/hostgame mode <worldId> <solo|duos|fives> -- before players queue; /hostgame panel [worldId] -- controls",
                "/hostgame roster <worldId> -- character IDs; /hostgame announce <worldId> <message> -- this game only",
                "/hostgame bring <worldId> <characterId>; /hostgame goto <worldId> <characterId> -- your current round",
                "/hostgame kick <worldId> <characterId> -- remove a player from your current hosted round; does not ban the account",
                "/hostgame spectate <worldId> <characterId>; /hostgame fly <worldId> [on|off] -- hosts after elimination",
            ],
            Examples =
            [
                "/hostgame key eu 7d",
                "/hostgame redeem <key>",
                "/hostgame create eu duos Friday Friends",
                "/hostgame invite 8 2h",
            ],
            Run = call => call.Ctx.HostedGame?.Invoke(call)
                ?? ConsoleReply.Failed("hosted games unavailable"),
        });

        return registry;
    }
}
