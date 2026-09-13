using System.Numerics;
using System.Text.Json;
using Cranberry.Protocol;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone.Destructibles;

/// <summary>
/// Native DTO collision policy for fences, road signs and fragile props (1413b8330 / 140e6e1e0).
/// The generated data is the common source for model flags, report names and exact map ids.
/// Percent speed loss is server tuning; the historical class/data name remains compatible.
/// </summary>
public static class VehicleFencePolicy
{
    private sealed record Policy(DestructibleCatalog Catalog, IReadOnlyDictionary<string, uint> ModelIds,
        IReadOnlyDictionary<uint, float> SpeedLoss);
    private static readonly Lazy<Policy> Data = new(Load);
    public static IReadOnlyDictionary<string, uint> ModelIds => Data.Value.ModelIds;
    public static DestructibleCatalog Catalog => Data.Value.Catalog;
    public static bool IsVehicleBreakable(string model) => ModelIds.ContainsKey(model);
    public static bool IsFence(string model) => IsVehicleBreakable(model)
        && model.Contains("Fence", StringComparison.OrdinalIgnoreCase);

    private static Policy Load()
    {
        const string relative = "Data/Destructibles/z2-vehicle-fences.json";
        string path = Path.Combine(AppContext.BaseDirectory, relative);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); !File.Exists(path) && dir is not null; dir = dir.Parent)
            path = Path.Combine(dir.FullName, "src/Cranberry.Zone", relative);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var props = new List<DestructibleProp>();
        var models = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var speedLoss = new Dictionary<uint, float>();
        foreach (var type in doc.RootElement.GetProperty("types").EnumerateArray())
        {
            string model = type.GetProperty("model").GetString()!;
            uint modelId = type.GetProperty("modelId").GetUInt32();
            models.Add(model, modelId);
            speedLoss.Add(modelId, type.GetProperty("speedLossPercent").GetSingle());
            foreach (var row in type.GetProperty("instances").EnumerateArray())
                props.Add(new(row[0].GetUInt32(), model,
                    new(row[1].GetSingle(), row[2].GetSingle(), row[3].GetSingle()),
                    type.GetProperty("effectId").GetUInt32(), type.GetProperty("health").GetInt32()));
        }
        return new(new(props), models, speedLoss);
    }

    public static void WriteModels(PacketWriter writer)
    {
        writer.WriteByte(DestructiblePackets.Opcode);
        writer.WriteUInt16(6);
        writer.WriteUInt32(0);
        writer.WriteUInt32((uint)ModelIds.Count);
        foreach (uint id in ModelIds.Values) writer.WriteUInt32(id);
        writer.WriteUInt32((uint)ModelIds.Count);
        foreach (uint id in ModelIds.Values)
        {
            writer.WriteUInt32(id);
            // Native scales horizontal velocity by 1 - clamp(loss * resistance, 0, 100) / 100.
            writer.WriteSingle(Data.Value.SpeedLoss[id]);
        }
    }

    public static void WriteLoadedInstances(PacketWriter writer, IReadOnlyList<uint> ids)
    {
        writer.WriteByte(DestructiblePackets.Opcode);
        writer.WriteUInt16(5);
        writer.WriteUInt32(0);
        writer.WriteUInt32((uint)ids.Count);
        foreach (uint id in ids) writer.WriteUInt32(id);
    }
}

public sealed partial class DestructibleWorld
{
    public DestructibleDamage? HitByVehicle(DestructibleCatalog catalog, in DestructibleHit report,
        VehicleFleet fleet, ulong reporterGuid, long nowMs)
    {
        if (report.ProjectileId != 0 || !fleet.TryGet(report.SourceGuid, out var vehicle)
            || (vehicle.OwnerGuid != reporterGuid && (vehicle.OwnerGuid != 0 || vehicle.CoastingOwnerGuid != reporterGuid))
            || vehicle.Health == 0 || vehicle.LastPoseMs == long.MinValue
            || nowMs < vehicle.LastPoseMs || nowMs - vehicle.LastPoseMs > 2000
            || !catalog.Props.TryGetValue(report.ObjectId, out var prop)
            || !VehicleFencePolicy.IsVehicleBreakable(prop.Model)
            || !string.Equals(prop.Model, report.Model, StringComparison.OrdinalIgnoreCase)) return null;
        float distance = Vector3.Distance(vehicle.Position, prop.Position);
        if (!float.IsFinite(distance) || distance > 18f) return null;
        int remaining = _health.GetValueOrDefault(prop.ObjectId, prop.Health);
        if (remaining <= 0) return null;
        _health[prop.ObjectId] = 0;
        _destroyed.Add(prop);
        return new(prop, 0, remaining, true);
    }
}
