namespace Cranberry.Zone.DevConsole;

/// <summary>Ordinary account features that share the client's slash-command transport.</summary>
public static class ConsoleAccessPolicy
{
    public static bool HasDevelopmentAccess(ConsoleTier tier) => tier >= ConsoleTier.Tester;

    // Hosting rights are still checked by HostedGameStore for every backend action.
    public static bool IsPlayerCommand(string name) =>
        name.Equals("hostgame", StringComparison.OrdinalIgnoreCase);

    public static bool Allows(ConsoleTier tier, string? name, string arguments) =>
        HasDevelopmentAccess(tier)
        || name is not null && (IsPlayerCommand(name)
            || ((name.Equals("help", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("commands", StringComparison.OrdinalIgnoreCase))
                && arguments.Trim().Equals("hostgame", StringComparison.OrdinalIgnoreCase)));
}
