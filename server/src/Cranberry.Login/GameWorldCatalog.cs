using System.Diagnostics.CodeAnalysis;

namespace Cranberry.Login;

/// <summary>
/// Local world configuration. The ids are server policy; mode ids and tags are from the
/// August client's GameModeDefinitions.txt (13 SoloDoloZ2, 5 Team2, 7 Team5).
/// Login hosts, playable worlds and ranked-history modes are separate concepts.
/// </summary>
public sealed record GameWorldDefinition(
    uint WorldId, uint GameModeId, string ModeName, int WorldNumber = 1,
    bool IsHosted = false, string Region = "EU", string DataCenter = "AMS")
{
    public string DisplayName => IsHosted ? "Hosted Games" : $"{ModeName} {Region} World {WorldNumber}";

    public GameServerEntry ServerEntry(RegionNamingOptions? regions = null) => new()
    {
        Id = WorldId,
        Name = DisplayName,
        Region = (regions ?? RegionNamingOptions.Default).Resolve(Region),
        Info = new PopulationInfo
        {
            Mode = checked((int)GameModeId),
            IsLogin = WorldId == GameWorldCatalog.PrimaryWorldId,
            IsEvent = IsHosted,
            DataCenter = DataCenter,
        },
    };
}

public static class GameWorldCatalog
{
    public const uint PrimaryWorldId = 1;
    public const uint HostedWorldId = 8;
    public const uint SoloGameModeId = 13;
    public const uint DuosGameModeId = 5;
    public const uint FivesGameModeId = 7;

    // Keep the existing regional login ids 2-5 reserved. World numbers describe the
    // ordinal within one mode and region, and are deliberately independent of ids.
    public static IReadOnlyList<GameWorldDefinition> Default { get; } = Array.AsReadOnly<GameWorldDefinition>(
    [
        new(PrimaryWorldId, SoloGameModeId, "Solo"),
        new(6, DuosGameModeId, "Duos"),
        new(7, FivesGameModeId, "Fives"),
        new(HostedWorldId, SoloGameModeId, "Solo", IsHosted: true),
        new(9, SoloGameModeId, "Solo", WorldNumber: 2),
        new(10, DuosGameModeId, "Duos", WorldNumber: 2),
        new(11, FivesGameModeId, "Fives", WorldNumber: 2),
        // Reserve event destinations at login: August ignores schedule rows for unknown worlds.
        // Their live mode and access rights come from the hosted-game service, not this pool.
        .. Enumerable.Range(1000, 31).Select(id => new GameWorldDefinition(
            (uint)id, SoloGameModeId, "Solo", IsHosted: true)),
    ]);

    public static bool TryGet(uint worldId, [NotNullWhen(true)] out GameWorldDefinition? world)
    {
        world = Default.FirstOrDefault(candidate => candidate.WorldId == worldId);
        return world is not null;
    }
}
