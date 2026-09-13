using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// One seat of one vehicle, straight from the client's <c>SeatInfo.txt</c> joined through
/// <c>VehicleSeatMappings.txt</c>. <see cref="Index"/> is the <c>SEAT</c> column and is the number
/// that goes on the wire in <c>70 02 MountResponse</c> and <c>88 02 Vehicle.Occupy</c>.
/// </summary>
/// <param name="LoopNextSeatIndex">
/// The seat <c>SEAT_LOOP_NEXT_SEAT_ID</c> points at, resolved from a SeatInfo id to a seat index,
/// or −1. Only the PickupTruck bed uses it: seats 2↔5 and 3↔6 form the two loops.
/// </param>
public sealed record VehicleSeatDefinition(
    int Index,
    uint SeatInfoId,
    bool IsDriver,
    bool CanFire,
    bool CanBail,
    bool Enclosed,
    bool KickCorpse,
    int LoopNextSeatIndex);

/// <summary>
/// One row of the vehicle's <c>MoveInfo</c> block (<c>VehicleMoveInfoMappings.txt</c> base + ordinal).
///
/// <para><b>The server never integrates any of this.</b> The client owns vehicle physics for every
/// vehicle id — <c>FUN_140e6e8b0</c> grants simulation on the owner guid alone with no branch on
/// vehicle id, type, control type or model, and the full PhysX setup (torque, gear ratios, per-wheel
/// brake and steer, tyre stiffness, suspension) ships in the client's own datasheets while
/// <c>Vehicles.txt</c> MASS/HEALTH are 0 on every row (docs/43 §0.1, §2.3). These numbers are kept
/// only so the server can <i>referee</i>: pick a degraded mode on damage, and bound a plausible pose
/// delta.</para>
///
/// <para><b>Units are undeclared.</b> The client's sheets state none for <see cref="MaxForward"/> or
/// <see cref="EstimatedMaxSpeed"/>, so neither is treated as metres per second anywhere in this
/// lane; see <see cref="VehicleFleetOptions.MaxPoseSpeedMetresPerSecond"/>.</para>
/// </summary>
public readonly record struct VehicleDriveMode(
    int Ordinal,
    uint MoveInfoId,
    uint MovementMode,
    float MaxForward,
    float EstimatedMaxSpeed);

/// <summary>
/// A drivable land vehicle: what the spawn packets need (<see cref="ModelId"/>,
/// <see cref="VehicleId"/>) and what possession needs (<see cref="Seats"/>).
/// </summary>
public sealed class VehicleDefinition
{
    internal VehicleDefinition(
        uint vehicleId,
        string name,
        uint nameId,
        uint modelId,
        string modelFile,
        uint destroyedModelId,
        uint physicsFamily,
        uint decaySeconds,
        float maxDismountSpeed,
        IReadOnlyList<VehicleSeatDefinition> seats,
        IReadOnlyList<VehicleDriveMode> driveModes)
    {
        VehicleId = vehicleId;
        Name = name;
        NameId = nameId;
        ModelId = modelId;
        ModelFile = modelFile;
        DestroyedModelId = destroyedModelId;
        PhysicsFamily = physicsFamily;
        DecaySeconds = decaySeconds;
        MaxDismountSpeed = maxDismountSpeed;
        Seats = seats;
        DriveModes = driveModes;
    }

    /// <summary>The <c>Vehicles.txt</c> row id: the value that rides in <c>0xd7</c>'s vehicle id
    /// field and selects the vehicle actor class client-side.</summary>
    public uint VehicleId { get; }

    /// <summary>The datasheet's own name — for logs, never on the wire.</summary>
    public string Name { get; }

    /// <summary><c>NAME_ID</c>, the locale string id.</summary>
    public uint NameId { get; }

    /// <summary><c>Models.txt</c> row id of the intact mesh: 7225 / 9258 / 9301 / 9588.</summary>
    public uint ModelId { get; }

    public string ModelFile { get; }

    /// <summary><c>Models.txt</c> row id of the wreck: 7226 / 9315 / 9316 / 9593.</summary>
    public uint DestroyedModelId { get; }

    /// <summary><c>MoveInfo</c> base for this family (10 / 20 / 30 / 50).</summary>
    public uint PhysicsFamily { get; }

    public uint DecaySeconds { get; }

    /// <summary>
    /// <c>MAX_DISMOUNT_SPEED_OVERRIDE</c>, or the build's <c>Vehicle.DefaultMaxDismountSpeed</c>
    /// (12) when the override is 0 — which it is on all four land rows. Above this the client
    /// refuses with "Vehicle is moving too fast to exit." (docs/43 §4.6).
    /// </summary>
    public float MaxDismountSpeed { get; }

    /// <summary>Seats in index order; index 0 is always the driver on all four land vehicles.</summary>
    public IReadOnlyList<VehicleSeatDefinition> Seats { get; }

    /// <summary>The eight <c>MoveInfo</c> rows in ordinal order.</summary>
    public IReadOnlyList<VehicleDriveMode> DriveModes { get; }

    public int SeatCount => Seats.Count;

    /// <summary>The one seat with <c>IS_DRIVER</c>. Index 0 on every land vehicle in this build.</summary>
    public VehicleSeatDefinition DriverSeat
    {
        get
        {
            foreach (VehicleSeatDefinition seat in Seats)
            {
                if (seat.IsDriver)
                {
                    return seat;
                }
            }

            throw new InvalidOperationException($"Vehicle {VehicleId} ({Name}) has no driver seat.");
        }
    }

    public bool TryGetSeat(int index, [NotNullWhen(true)] out VehicleSeatDefinition? seat)
    {
        if (index >= 0 && index < Seats.Count && Seats[index].Index == index)
        {
            seat = Seats[index];
            return true;
        }

        // The roster is emitted in index order, so the fast path above holds; the scan is a
        // correctness guard, not a hot path.
        foreach (VehicleSeatDefinition candidate in Seats)
        {
            if (candidate.Index == index)
            {
                seat = candidate;
                return true;
            }
        }

        seat = null;
        return false;
    }

    /// <summary>
    /// The <c>MAX_FORWARD</c> of the fastest mode — the PoliceCar's 105 is the roster's highest and
    /// the reason the planner makes it the rarest spawn.
    /// </summary>
    public float TopMaxForward
    {
        get
        {
            float top = 0f;
            foreach (VehicleDriveMode mode in DriveModes)
            {
                top = MathF.Max(top, mode.MaxForward);
            }

            return top;
        }
    }

    public override string ToString() => $"{Name} (vehicle {VehicleId}, model {ModelId}, {SeatCount} seats)";
}

/// <summary>
/// The build's named vehicle constants, from <c>StringHashToValue.txt</c> — the client enforces the
/// matching messages, so the server must apply the same numbers or the two disagree (docs/43 §4.6).
/// </summary>
/// <param name="InteractionCooldownMs">"Too early to exit vehicle" — 1000.</param>
/// <param name="SeatSwapCooldownMs">"Too early to change seats." — 250.</param>
/// <param name="DefaultMaxDismountSpeed">"Vehicle is moving too fast to exit." — 12.</param>
/// <param name="DefaultMinDismountDamageSpeed">Bail-damage threshold — 10.</param>
public readonly record struct VehicleConstants(
    int InteractionCooldownMs,
    int SeatSwapCooldownMs,
    float DefaultMaxDismountSpeed,
    float DefaultMinDismountDamageSpeed);

/// <summary>
/// The four drivable land vehicles of a BR match, read from
/// <c>Data/Vehicles/vehicle-roster.json</c> (generated by <c>tools/world/vehicle_anchors.py</c> from
/// the client's own datasheets via <c>out/data_aug/derived/vehicles.json</c> and <c>Models.txt</c>).
///
/// <para><b>Why four.</b> <c>Vehicles.txt</c> has eight rows: 13 is the parachute and 1337 the
/// observer, and <b>15/16 are the <i>Ignition match-mode</i> variants, not hotwire variants</b> —
/// the binary's own strings put "Ignition" in the match-mode vocabulary
/// (<c>IgnitionLoweringIntoMatch</c>, <c>cAdminCommandPacketIdTestIgnitionWave</c>,
/// <c>UI.Results.Rank.Ignition.Detonation</c>) and <c>VehicleSets.txt</c> set 4 is exactly
/// <c>{15, 16}</c> while sets 1/2/3/10 carry <c>{1,2,3,5}</c> (docs/43 §2.1). So the roster is
/// OffRoader 1, PickupTruck 2, PoliceCar 3, ATV 5.</para>
/// </summary>
public sealed class VehicleRoster
{
    /// <summary>Default file name inside <see cref="VehicleDataPaths"/>' directory.</summary>
    public const string DefaultFileName = "vehicle-roster.json";

    /// <summary>The expected schema string; a mismatch is a hard load error.</summary>
    public const string Schema = "cranberry/vehicle-roster/1";

    private readonly VehicleDefinition[] _vehicles;

    private VehicleRoster(VehicleDefinition[] vehicles, VehicleConstants constants)
    {
        _vehicles = vehicles;
        Constants = constants;
    }

    /// <summary>Every drivable vehicle, in ascending vehicle id.</summary>
    public IReadOnlyList<VehicleDefinition> Vehicles => _vehicles;

    public VehicleConstants Constants { get; }

    public int Count => _vehicles.Length;

    public bool TryGet(uint vehicleId, [NotNullWhen(true)] out VehicleDefinition? definition)
    {
        foreach (VehicleDefinition candidate in _vehicles)
        {
            if (candidate.VehicleId == vehicleId)
            {
                definition = candidate;
                return true;
            }
        }

        definition = null;
        return false;
    }

    public VehicleDefinition Require(uint vehicleId) =>
        TryGet(vehicleId, out VehicleDefinition? definition)
            ? definition
            : throw new KeyNotFoundException(
                $"Vehicle {vehicleId} is not a drivable land vehicle. The roster holds "
                + string.Join(", ", _vehicles.Select(v => v.VehicleId)) + ".");

    public static VehicleRoster LoadDefault() => Load(VehicleDataPaths.Require(DefaultFileName));

    public static VehicleRoster Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), path);
    }

    public static VehicleRoster Parse(ReadOnlySpan<byte> json, string origin)
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

        JsonElement constants = root.GetProperty("constants");
        var vehicleConstants = new VehicleConstants(
            InteractionCooldownMs: constants.GetProperty("interactionCooldownMs").GetInt32(),
            SeatSwapCooldownMs: constants.GetProperty("seatSwapCooldownMs").GetInt32(),
            DefaultMaxDismountSpeed: constants.GetProperty("defaultMaxDismountSpeed").GetSingle(),
            DefaultMinDismountDamageSpeed: constants.GetProperty("defaultMinDismountDamageSpeed").GetSingle());

        var vehicles = new List<VehicleDefinition>();
        foreach (JsonElement entry in root.GetProperty("vehicles").EnumerateArray())
        {
            vehicles.Add(ReadVehicle(entry, origin));
        }

        if (vehicles.Count == 0)
        {
            throw new InvalidDataException($"{origin}: the roster is empty.");
        }

        vehicles.Sort((left, right) => left.VehicleId.CompareTo(right.VehicleId));
        return new VehicleRoster([.. vehicles], vehicleConstants);
    }

    private static VehicleDefinition ReadVehicle(JsonElement entry, string origin)
    {
        uint vehicleId = entry.GetProperty("id").GetUInt32();

        var seats = new List<VehicleSeatDefinition>();
        foreach (JsonElement seat in entry.GetProperty("seats").EnumerateArray())
        {
            seats.Add(new VehicleSeatDefinition(
                Index: seat.GetProperty("index").GetInt32(),
                SeatInfoId: seat.GetProperty("seatInfoId").GetUInt32(),
                IsDriver: seat.GetProperty("isDriver").GetBoolean(),
                CanFire: seat.GetProperty("canFire").GetBoolean(),
                CanBail: seat.GetProperty("canBail").GetBoolean(),
                Enclosed: seat.GetProperty("enclosed").GetBoolean(),
                KickCorpse: seat.GetProperty("kickCorpse").GetBoolean(),
                LoopNextSeatIndex: seat.GetProperty("loopNextSeatIndex").GetInt32()));
        }

        seats.Sort((left, right) => left.Index.CompareTo(right.Index));
        for (int i = 0; i < seats.Count; i++)
        {
            if (seats[i].Index != i)
            {
                throw new InvalidDataException(
                    $"{origin}: vehicle {vehicleId} seat indices are {string.Join(",", seats.Select(s => s.Index))}, "
                    + "expected a dense 0..n-1 run — the seat index goes on the wire as an array position.");
            }
        }

        if (seats.Count(s => s.IsDriver) != 1)
        {
            throw new InvalidDataException($"{origin}: vehicle {vehicleId} does not have exactly one driver seat.");
        }

        var modes = new List<VehicleDriveMode>();
        foreach (JsonElement mode in entry.GetProperty("driveModes").EnumerateArray())
        {
            modes.Add(new VehicleDriveMode(
                Ordinal: mode.GetProperty("ordinal").GetInt32(),
                MoveInfoId: mode.GetProperty("moveInfoId").GetUInt32(),
                MovementMode: mode.GetProperty("movementMode").GetUInt32(),
                MaxForward: mode.GetProperty("maxForward").GetSingle(),
                EstimatedMaxSpeed: mode.GetProperty("estimatedMaxSpeed").GetSingle()));
        }

        modes.Sort((left, right) => left.Ordinal.CompareTo(right.Ordinal));

        return new VehicleDefinition(
            vehicleId: vehicleId,
            name: entry.GetProperty("name").GetString() ?? $"Vehicle{vehicleId}",
            nameId: entry.GetProperty("nameId").GetUInt32(),
            modelId: entry.GetProperty("modelId").GetUInt32(),
            modelFile: entry.GetProperty("modelFile").GetString() ?? string.Empty,
            destroyedModelId: entry.GetProperty("destroyedModelId").GetUInt32(),
            physicsFamily: entry.GetProperty("physicsFamily").GetUInt32(),
            decaySeconds: entry.GetProperty("decaySeconds").GetUInt32(),
            maxDismountSpeed: entry.GetProperty("maxDismountSpeed").GetSingle(),
            seats: seats,
            driveModes: modes);
    }
}

/// <summary>
/// Where the generated vehicle data lives. Same two-step probe as
/// <see cref="Cranberry.Zone.Loot.LootDataPaths"/>: <c>&lt;BaseDirectory&gt;/Data/Vehicles</c> is
/// what makes a deployed build work, and the walk up to <c>src/Cranberry.Zone/Data/Vehicles</c> is
/// what makes <c>dotnet run</c> and <c>dotnet test</c> from the tree work with no deployment step.
/// </summary>
public static class VehicleDataPaths
{
    private const string RelativeInOutput = "Data/Vehicles";
    private const string RelativeInRepository = "src/Cranberry.Zone/Data/Vehicles";

    /// <summary>Set by the host to point at an explicit data directory; null uses the probe.</summary>
    public static string? Override { get; set; }

    public static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrEmpty(Override))
        {
            yield return Override;
        }

        string baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, RelativeInOutput);

        foreach (string root in new[] { baseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(root);
            while (directory is not null)
            {
                yield return Path.Combine(directory.FullName, RelativeInRepository);
                directory = directory.Parent;
            }
        }
    }

    public static string? Find(string fileName)
    {
        foreach (string directory in Candidates())
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string Require(string fileName) =>
        Find(fileName)
        ?? throw new FileNotFoundException(
            $"Vehicle data file '{fileName}' not found. Looked in: " + string.Join(", ", Candidates()),
            fileName);
}
