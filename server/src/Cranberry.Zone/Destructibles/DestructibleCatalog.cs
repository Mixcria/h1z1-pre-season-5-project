using System.Numerics;
using System.Text.Json;

namespace Cranberry.Zone.Destructibles;

public sealed record DestructibleProp(uint ObjectId, string Model, Vector3 Position, uint EffectId, int Health)
{
    public bool IsGlass => Model.Equals("Common_Props_GlassWindow01.adr", StringComparison.OrdinalIgnoreCase)
        || Model.Equals("Common_Props_TintedWindow01.adr", StringComparison.OrdinalIgnoreCase);
    public bool IsExplosive => string.Equals(Model, "Common_Props_IndustrialElements_RedBarrel01.adr",
        StringComparison.OrdinalIgnoreCase);
    public bool IsWallHoleBlocker => string.Equals(Model, "City_Structures_Buildings_Int_WallHoleBlocked01.adr",
        StringComparison.OrdinalIgnoreCase);
    public bool IsWoodenFence => Model.StartsWith("Farm_Props_Fences_", StringComparison.OrdinalIgnoreCase)
        || Model.StartsWith("Residential_Props_Fence_", StringComparison.OrdinalIgnoreCase)
        || Model.StartsWith("Farm_Props_CorralFence_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Immutable map data; mutable damage belongs to one match.</summary>
public sealed class DestructibleCatalog
{
    private static readonly Lazy<DestructibleCatalog> DefaultCatalog = new(LoadDefault);
    public static DestructibleCatalog Default => DefaultCatalog.Value;
    public IReadOnlyDictionary<uint, DestructibleProp> Props { get; }
    public IReadOnlyList<string> Models { get; }

    public DestructibleCatalog(IEnumerable<DestructibleProp> props)
    {
        Props = props.ToDictionary(p => p.ObjectId);
        Models = Props.Values.Select(p => p.Model).Distinct(StringComparer.Ordinal).Order().ToArray();
    }

    private static DestructibleCatalog LoadDefault()
    {
        const string relative = "Data/Destructibles/z2-destructibles.json";
        string path = Path.Combine(AppContext.BaseDirectory, relative);
        if (!File.Exists(path))
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "src/Cranberry.Zone", relative);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var props = new List<DestructibleProp>();
        foreach (var type in doc.RootElement.GetProperty("types").EnumerateArray())
        {
            string model = type.GetProperty("model").GetString()!;
            uint effect = type.GetProperty("effectId").GetUInt32();
            int health = type.GetProperty("health").GetInt32();
            foreach (var row in type.GetProperty("instances").EnumerateArray())
                props.Add(new(row[0].GetUInt32(), model,
                    new(row[1].GetSingle(), row[2].GetSingle(), row[3].GetSingle()), effect, health));
        }
        return new(props);
    }
}
