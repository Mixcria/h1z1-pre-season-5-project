using System.Numerics;
using System.Text.Json;

namespace Cranberry.Zone.Vehicles;

/// <summary>An authored vehicle location, joined to the August map's named areas.</summary>
/// <param name="InstanceId">Stable retail Z2 vehicle-marker instance id.</param>
/// <param name="Position">Retail X/Z with the audited August parking height.</param>
/// <param name="Yaw">Authored heading in radians.</param>
/// <param name="Spaces">One car per authored location; retained for legacy anchor files.</param>
/// <param name="AreaIndex">Named-area index, or -1 outside named areas.</param>
/// <param name="VehicleId">Authored vehicle family, or zero for legacy untyped anchors.</param>
/// <param name="Pitch">Authored slope in radians; zero in legacy files.</param>
/// <param name="Roll">Authored bank in radians; zero in legacy files.</param>
public readonly record struct VehicleAnchor(
    uint InstanceId,
    Vector3 Position,
    float Yaw,
    byte Spaces,
    int AreaIndex,
    uint VehicleId = 0,
    float Pitch = 0,
    float Roll = 0)
{
    public bool HasArea => AreaIndex != VehicleAnchorSet.NoArea;
}

/// <summary>
/// Whole-map locations recovered from retail Z2.zone v7, with unchanged X/Z, family and
/// orientation. Parking heights and explicit exclusions are audited against August geometry.
/// The complete source and every compatibility decision remain in tools/world/data;
/// the retired local reference and server-selected station bays are no longer generator inputs.
/// See docs/vehicle-spawns-retail-20260912.md for evidence and cross-version limitations.
/// </summary>
public sealed class VehicleAnchorSet
{
    /// <summary><see cref="VehicleAnchor.AreaIndex"/> for an anchor on open road.</summary>
    public const int NoArea = -1;

    public const string DefaultFileName = "z2-vehicle-anchors.json";

    public const string Schema = "cranberry/vehicle-anchors/1";

    private readonly VehicleAnchor[] _anchors;
    private readonly string[] _areas;

    private VehicleAnchorSet(VehicleAnchor[] anchors, string[] areas)
    {
        _anchors = anchors;
        _areas = areas;
    }

    /// <summary>Every anchor, in ascending instance id — a stable, extraction-independent order.</summary>
    public ReadOnlySpan<VehicleAnchor> Anchors => _anchors;

    /// <summary>The named places, indexed by <see cref="VehicleAnchor.AreaIndex"/>.</summary>
    public ReadOnlySpan<string> Areas => _areas;

    public int Count => _anchors.Length;

    public int AreaCount => _areas.Length;

    /// <summary>Total capacity across the retained locations.</summary>
    public int TotalSpaces
    {
        get
        {
            int total = 0;
            foreach (VehicleAnchor anchor in _anchors)
            {
                total += anchor.Spaces;
            }

            return total;
        }
    }

    public ref readonly VehicleAnchor this[int index] => ref _anchors[index];

    /// <summary>Name of the place an anchor sits in, or null when it is on open road.</summary>
    public string? AreaNameOf(in VehicleAnchor anchor) =>
        anchor.AreaIndex == NoArea ? null : _areas[anchor.AreaIndex];

    /// <summary>Ordinal of a named place such as <c>PVCommercialEast</c>, or <see cref="NoArea"/>.</summary>
    public int AreaIndexOf(string name)
    {
        for (int i = 0; i < _areas.Length; i++)
        {
            if (string.Equals(_areas[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return NoArea;
    }

    public static VehicleAnchorSet LoadDefault() => Load(VehicleDataPaths.Require(DefaultFileName));

    public static VehicleAnchorSet Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), path);
    }

    public static VehicleAnchorSet Parse(ReadOnlySpan<byte> json, string origin)
    {
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        JsonElement root = document.RootElement;

        string? schema = root.TryGetProperty("schema", out JsonElement schemaElement)
            ? schemaElement.GetString()
            : null;
        if (!string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{origin}: schema '{schema}', expected '{Schema}'.");
        }

        var areaIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var areas = new List<string>();
        var anchors = new List<VehicleAnchor>();

        foreach (JsonElement entry in root.GetProperty("anchors").EnumerateArray())
        {
            int areaIndex = NoArea;
            if (entry.TryGetProperty("area", out JsonElement areaElement)
                && areaElement.ValueKind == JsonValueKind.String)
            {
                string area = areaElement.GetString()!;
                if (!areaIndices.TryGetValue(area, out areaIndex))
                {
                    areaIndex = areas.Count;
                    areaIndices[area] = areaIndex;
                    areas.Add(area);
                }
            }

            int spaces = entry.GetProperty("spaces").GetInt32();
            if (spaces is < 1 or > 255)
            {
                throw new InvalidDataException($"{origin}: an anchor claims {spaces} spaces.");
            }

            anchors.Add(new VehicleAnchor(
                InstanceId: entry.GetProperty("id").GetUInt32(),
                Position: new Vector3(
                    entry.GetProperty("x").GetSingle(),
                    entry.GetProperty("y").GetSingle(),
                    entry.GetProperty("z").GetSingle()),
                Yaw: entry.GetProperty("yaw").GetSingle(),
                Spaces: (byte)spaces,
                AreaIndex: areaIndex,
                VehicleId: entry.TryGetProperty("vehicleId", out var vehicleId) ? vehicleId.GetUInt32() : 0,
                Pitch: entry.TryGetProperty("pitch", out var pitch) ? pitch.GetSingle() : 0,
                Roll: entry.TryGetProperty("roll", out var roll) ? roll.GetSingle() : 0));
            VehicleAnchor added = anchors[^1];
            if (!float.IsFinite(added.Position.X) || !float.IsFinite(added.Position.Y)
                || !float.IsFinite(added.Position.Z) || !float.IsFinite(added.Yaw)
                || !float.IsFinite(added.Pitch) || !float.IsFinite(added.Roll))
                throw new InvalidDataException($"{origin}: anchor {added.InstanceId} has a non-finite pose.");
        }

        if (anchors.Count == 0)
        {
            throw new InvalidDataException($"{origin}: no anchors — a match would spawn no vehicles at all.");
        }

        // The seeded plan is only reproducible if the input order is, and the generator sorts by
        // instance id. Prove it here rather than trusting the file.
        for (int i = 1; i < anchors.Count; i++)
        {
            if (anchors[i].InstanceId <= anchors[i - 1].InstanceId)
            {
                throw new InvalidDataException(
                    $"{origin}: anchor {i} has instance id {anchors[i].InstanceId} after "
                    + $"{anchors[i - 1].InstanceId}; the file must be sorted by a unique instance id "
                    + "or a seeded plan is not reproducible.");
            }
        }

        return new VehicleAnchorSet([.. anchors], [.. areas]);
    }
}
