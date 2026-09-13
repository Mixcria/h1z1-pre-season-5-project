using Cranberry.Zone.Match;

namespace Cranberry.Zone.DevConsole;

/// <summary>Search the August map's existing drop anchors; no guessed coordinates.</summary>
public static class TeleportPlaces
{
    public static IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>
    {
        ["pv"] = "PVResidential", ["pleasantvalley"] = "PVResidential",
        ["cranberry"] = "CranberryResidential", ["ranchito"] = "RanchitoCentral",
        ["ranchitotaquito"] = "RanchitoCentral", ["harrisbluffs"] = "HarrisBluffsResidential",
        ["wakehills"] = "WakeHillsHamlet", ["lonepine"] = "LonePine",
        ["hospital"] = "KuramaHospital", ["dam"] = "Dam", ["military"] = "CampCerberus",
    };

    public static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static IReadOnlyList<DropPoi> Find(Z2DropPois places, string query, bool exactFirst = true)
    {
        string key = Key(query);
        if (key.Length == 0) return places.Places;
        if (Aliases.TryGetValue(key, out string? area) && places.Find(area) is { } aliased)
            return [aliased];
        var exact = places.Places.Where(p => Key(p.Area) == key).ToArray();
        if (exactFirst && exact.Length > 0) return exact;
        return places.Places.Where(p => Key(p.Area.Replace("PV", "PleasantValley", StringComparison.Ordinal)).Contains(key)
            || Key(p.Area).Contains(key)).ToArray();
    }
}
