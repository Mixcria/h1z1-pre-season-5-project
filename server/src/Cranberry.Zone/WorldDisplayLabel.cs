namespace Cranberry.Zone;

/// <summary>
/// Carries the admitted world's display name to the compass HUD through August's existing
/// StringHashToValueManager. The UI binding supports strings when its default is a string
/// (FUN_14120afe0); see docs/world-display-label-20260906.md and the paired HUD patch.
/// </summary>
public static class WorldDisplayLabel
{
    public const string Key = "Cranberry.WorldDisplayName";

    /// <summary>
    /// Includes every gameplay default: the client's map loader replaces the entire table.
    /// An empty name clears a previous match's label when entering the main menu.
    /// </summary>
    public static IReadOnlyList<StringHashValue> Values(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        return [.. StringHashValues.Entries, new StringHashValue("Cranberry.Healing", "0"), new StringHashValue(Key, displayName)];
    }
}
