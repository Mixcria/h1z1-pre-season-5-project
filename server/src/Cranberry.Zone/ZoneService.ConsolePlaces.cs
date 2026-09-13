using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private ConsoleReply ConsolePlaces(string search, int page)
    {
        var places = _drop.Places(out string? reason);
        if (places is null) return ConsoleReply.Failed($"map locations unavailable: {reason}; /tp drop and coordinates still work");
        var found = TeleportPlaces.Find(places, search, exactFirst: false);
        if (found.Count == 0) return ConsoleReply.Failed($"no map location matches '{search}'; /places lists all locations");
        const int rows = 8;
        int pages = (found.Count + rows - 1) / rows;
        if (page > pages) return ConsoleReply.Failed($"only {pages} page(s); /places {search} {pages}");
        var lines = new List<string> { $"* Map locations - page {page}/{pages} ({found.Count} matches)",
            "  Shortcuts: /tp pv | cranberry | ranchito | hospital | dam | military",
            "  Also: /tp drop | staging | spawn | back; /tp save <name> saves a spot" };
        lines.AddRange(found.Skip((page - 1) * rows).Take(rows).Select(p => $"  /tp {p.DisplayName}"));
        if (page < pages) lines.Add($"  Next: /places {search} {page + 1}");
        return ConsoleReply.Plain(lines);
    }

    private ConsoleReply ConsoleTeleportPlace(SoeConnection connection, GatewaySessionState state, string name)
    {
        Vector3? special = ConsolePlace(state, name);
        if (name.Trim().ToLowerInvariant() is "drop" or "staging" or "spawn")
            return special is Vector3 position ? ConsoleTeleport(connection, state, position)
                : ConsoleReply.Failed("destination unavailable");
        if (state.Match != MatchStep.InMatch)
            return ConsoleReply.Refused("map destinations need InMatch; /startmatch first");
        var places = _drop.Places(out string? reason);
        if (places is null) return ConsoleReply.Failed($"map locations unavailable: {reason}");
        var found = TeleportPlaces.Find(places, name);
        if (found.Count == 0) return ConsoleReply.Failed($"unknown place '{name}'; /places searches the map");
        if (found.Count > 1) return ConsoleReply.Failed($"'{name}' matches {found.Count} locations; /places {name} lists them. Use a full name.");
        // The anchor is an authored loot placement, with a valid height including floors.
        return ConsoleTeleport(connection, state, found[0].Anchor + new Vector3(0, 0.5f, 0));
    }
}
