using System.Text.Json;

namespace Cranberry.Zone.Vehicles;

public sealed class VehicleSkinState
{
    private readonly Dictionary<(uint Vehicle, uint Mod), VehicleSkinChoice> _selected = [];
    public IReadOnlyList<VehicleSkinChoice> Snapshot() => _selected.Values.OrderBy(s => s.VehicleId).ThenBy(s => s.ModPoint).ToArray();
    public bool Set(uint vehicle, uint mod, uint item)
    {
        if (VehicleSkinCatalog.Find(vehicle, mod, item) is not { } skin) return false;
        _selected[(vehicle, mod)] = skin;
        return true;
    }
    public void Unset(uint vehicle, uint mod) => _selected.Remove((vehicle, mod));
    public uint ShaderFor(uint vehicle) => _selected.TryGetValue((vehicle, 1), out var skin)
        ? skin.ShaderGroupId : VehicleShaderGroups.For(vehicle);

    private static string PathFor(string root, ulong character) => Path.Combine(root, $"v-{character:x16}.json");
    public static VehicleSkinState Load(string? root, ulong character, Action<string>? log = null)
    {
        var state = new VehicleSkinState();
        if (string.IsNullOrWhiteSpace(root)) return state;
        try
        {
            string path = PathFor(root, character);
            if (File.Exists(path))
                foreach (var choice in JsonSerializer.Deserialize<VehicleSkinChoice[]>(File.ReadAllText(path)) ?? [])
                    state.Set(choice.VehicleId, choice.ModPoint, choice.ItemId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { log?.Invoke($"Vehicle skins could not be restored: {ex.Message}"); }
        return state;
    }
    public void Save(string? root, ulong character, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        string temporary = PathFor(root, character) + ".tmp";
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(temporary, JsonSerializer.Serialize(Snapshot()));
            File.Move(temporary, PathFor(root, character), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { log?.Invoke($"Vehicle skins could not be saved: {ex.Message}"); }
    }
}
