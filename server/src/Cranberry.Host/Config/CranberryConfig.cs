using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cranberry.Login;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Descent;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Lighting;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Host.Config;

/// <summary>The <c>root</c> block.</summary>
public sealed record RootBlock(
    string Path,
    string Zone,
    string? SeedCharacterName,
    string DynamicAppearanceSource);

/// <summary>The <c>ports</c> block.</summary>
public sealed record PortsBlock(int Login, int Gateway);

/// <summary>The <c>sky</c> block, bound to <see cref="EnvironmentPresets"/>.</summary>
public sealed record SkyBlock(EnvironmentSettings Settings);

/// <summary>The <c>gas</c> block, bound to <see cref="GasTuning"/>.</summary>
public sealed record GasBlock(bool Enabled, GasSettings Settings, string? Note);

/// <summary>The <c>movement</c> block, bound to <see cref="MovementTuning"/>.</summary>
public sealed record MovementBlock(MovementProfile Profile, string? Note);

/// <summary>
/// The <c>descent</c> block, bound to <see cref="DescentTuning"/>. <see cref="LandingGuard"/> is
/// D239's near-ground landing guard, which is not part of the ride settings because it is not a
/// ride: it decides whether the server may force a landing handover at all.
/// </summary>
public sealed record DescentBlock(DescentSettings Settings, string? Note, bool LandingGuard);

/// <summary>
/// The <c>drop</c> block, bound to <see cref="DropTuning"/> — D240/D241. Before it,
/// <see cref="DropOptions"/> had no environment override of any kind and every drop question needed
/// a rebuild to taste-test (AUDIT-parachute G9).
/// </summary>
public sealed record DropBlock(DropOptions Options, string? Note, uint ParachuteSkinItemId);

/// <summary>
/// The <c>loot</c> block, bound to <see cref="LootStreamOptions"/>, <see cref="LootDensityOptions"/>
/// and <see cref="AirdropOptions"/>. The density and airdrop halves arrived with D270-D275: before
/// them the floor's density and the whole airdrop channel needed a rebuild to taste-test, which is
/// the same trap docs/110 records for the parachute.
/// </summary>
public sealed record LootBlock(
    LootStreamOptions Stream,
    float GroundLootRadiusMetres,
    LootDensityOptions Density,
    AirdropOptions Airdrop);

/// <summary>The <c>vehicles</c> block, including the map spawn plan and live vehicle options.</summary>
public sealed record VehiclesBlock(
    VehicleSpawnPlanOptions Plan,
    VehicleStreamOptions Stream,
    VehicleFuelOptions Fuel,
    VehicleRelayOptions Relay,
    VehicleIgnitionOptions Ignition,
    VehicleDamageOptions Damage,
    VehicleBoostOptions Boost,
    bool PositionBlock,
    byte SpawnFlags1,
    bool Shader);

/// <summary>The <c>doors</c> block; its fields land on <see cref="ZoneOptions"/> directly.</summary>
public sealed record DoorsBlock(
    DoorRotation Rotation,
    DoorCollisionMode Collision,
    int RestreamIntervalMs,
    int PressWindowMs,
    byte SpawnFlags1,
    byte PositionUpdateType,
    bool SetCollidable,
    bool Lobby,
    bool Hospital,
    bool HospitalDouble,
    bool Shared,
    bool CapNewOnly,
    float DespawnRadiusMetres,
    bool RetailInteract,
    float InteractRangeMetres,
    bool RetailSound);

/// <summary>
/// The <c>skins</c> block, bound to <see cref="SkinOptions"/>. <see cref="AppearanceAuthoredRows"/>
/// is the process-wide static <c>AugustDynamicAppearanceTable.ApplyAuthoredRows</c>, folded into
/// the config record per the plan's §3 lane 0D.
/// </summary>
/// <param name="AppearanceTable">
/// <c>full</c> (D316's default) or <c>filtered</c> - the process-wide static
/// <c>AugustDynamicAppearanceTable.ShipWholeTable</c>.
/// </param>
public sealed record SkinsBlock(
    SkinOptions Options,
    bool AppearanceAuthoredRows,
    string AppearanceTable);

/// <summary>The <c>weapons</c> block, bound to <see cref="WeaponStageOptions"/>.</summary>
public sealed record WeaponsBlock(WeaponStageOptions Stages);

/// <summary>The <c>combat</c> block, bound to <see cref="CombatOptions"/>.</summary>
public sealed record CombatBlock(CombatOptions Options);

/// <summary>The <c>ammo</c> block — <see cref="CombatOptions.Ammo"/>, named separately.</summary>
public sealed record AmmoBlock(AmmoOptions Options);

/// <summary>The <c>crafting</c> block, bound to <see cref="CraftingOptions"/>.</summary>
public sealed record CraftingBlock(CraftingOptions Options);

/// <summary>The <c>match</c> block, bound to <see cref="MatchEndOptions"/> and the match seed.</summary>
public sealed record MatchBlock(MatchEndOptions End, ulong Seed, string? SeedNote);

/// <summary>The <c>lobby</c> block (docs/115, D222-D225), bound to <see cref="LobbyOptions"/>.</summary>
public sealed record LobbyBlock(LobbyOptions Options);

/// <summary>The <c>bounty</c> block (docs/115, D226-D229), bound to <see cref="BountyOptions"/>.</summary>
public sealed record BountyBlock(BountyOptions Options);

/// <summary>The <c>menu</c> block, bound to the three menu option records.</summary>
public sealed record MenuBlock(
    MenuViewOptions Views,
    MenuActorOptions Actor,
    MenuTopBarOptions TopBar);

/// <summary>The <c>console</c> block, bound to <see cref="ConsoleOptions"/>.</summary>
public sealed record ConsoleBlock(ConsoleOptions Options);

/// <summary>The <c>transport</c> block: the landing burst's send window.</summary>
public sealed record TransportBlock(int BurstSliceSize, int BurstSliceDelayMs,
    int MaxLoginConnections = 8192, int MaxGatewayConnections = 8192);

/// <summary>The <c>features</c> block: docs/32's whole-subsystem rollbacks.</summary>
public sealed record FeaturesBlock(
    bool Containers,
    bool MovementStats,
    bool Doors,
    bool Vehicles,
    bool LootClusters,
    bool GroundLootShader);

/// <summary>The <c>dev</c> block: development aids and the two open wield experiments.</summary>
public sealed record DevBlock(
    int BootstrapDelayMs,
    int AutoMatchMs,
    int GroundLootMs,
    bool StarterWeapon,
    InventoryOptions Inventory);

/// <summary>The <c>login</c> block, bound to <see cref="RegionNamingOptions"/>.</summary>
public sealed record LoginBlock(RegionNamingOptions Regions);

/// <summary>The <c>peers</c> block, bound to lane 3C's <see cref="PeerOptions"/>.</summary>
public sealed record PeersBlock(PeerOptions Options);

/// <summary>
/// One <c>cranberry.json</c> replacing the environment-switch sprawl of <c>Program.cs</c> — plan
/// §3 lane 0D, S1 §2.4.
/// <para>
/// <b>Precedence is defaults &lt; file &lt; environment</b>, and inside the environment the
/// <b>legacy</b> name wins over the generated <c>CRANBERRY_&lt;BLOCK&gt;_&lt;KEY&gt;</c> overlay.
/// Every one-word revert the owner has ever typed therefore keeps working, unchanged, whatever the
/// file says: <c>CRANBERRY_DRESS_LAST=0</c>, <c>CRANBERRY_WEAPON_MOVEMENT_MODIFIER=0.5</c>,
/// <c>CRANBERRY_MENU_VIEWS=reference</c>, and the other 160 in <see cref="ConfigKeys"/>. A name
/// this table has never heard of still reaches the environment untouched, so a lane that adds a
/// switch while this file is being written does not have to wait for a row here.
/// </para>
/// <para>
/// <b>Nothing here decides a value.</b> Every block is materialised by the option record that
/// already owned it (<c>GasTuning.FromEnvironment</c>, <c>WeaponStageOptions.FromEnvironment</c>,
/// …) with this reader in place of <c>Environment.GetEnvironmentVariable</c>, so the defaults, the
/// range checks, the clamps and the "ignored with a note, never throws" behaviour are exactly the
/// ones this project already proved. With no file and no environment, every bound record is its
/// own shipped default instance — which is what keeps the golden transcript byte-identical.
/// </para>
/// <para>
/// The host's positional arguments (root, ports, seed, zone, bootstrap delay, auto-match,
/// appearance source, dev loot) still win over both file and environment, because
/// <c>run-host.ps1</c> passes seven of them on every launch.
/// </para>
/// </summary>
public sealed record CranberryConfig
{
    /// <summary>Where the host looks when nothing names a root.</summary>
    public const string DefaultRoot = @"C:\Aug2017";

    /// <summary>Names the file, when the owner would rather not put it beside the root.</summary>
    public const string PathVariable = "CRANBERRY_CONFIG";

    /// <summary>The file this config was loaded from, whether or not it existed.</summary>
    public required string FilePath { get; init; }

    /// <summary>True when <see cref="FilePath"/> was there and parsed.</summary>
    public required bool FileLoaded { get; init; }

    /// <summary>
    /// A one-line note for the boot log when the file was missing, unreadable, or carried a key
    /// this build does not know. Null when there is nothing to say.
    /// </summary>
    public required string? FileNote { get; init; }

    /// <summary>
    /// The reader every option record is bound through: legacy environment name, then the
    /// generated overlay name, then <c>cranberry.json</c>, then null (meaning "the default").
    /// </summary>
    public required Func<string, string?> Read { get; init; }

    /// <summary>The <c>root</c> block.</summary>
    public required RootBlock Root { get; init; }

    /// <summary>The <c>ports</c> block.</summary>
    public required PortsBlock Ports { get; init; }

    /// <summary>The <c>sky</c> block.</summary>
    public required SkyBlock Sky { get; init; }

    /// <summary>The <c>gas</c> block.</summary>
    public required GasBlock Gas { get; init; }

    /// <summary>The <c>movement</c> block.</summary>
    public required MovementBlock Movement { get; init; }

    /// <summary>The <c>descent</c> block.</summary>
    public required DescentBlock Descent { get; init; }

    /// <summary>The <c>drop</c> block.</summary>
    public required DropBlock Drop { get; init; }

    /// <summary>The <c>loot</c> block.</summary>
    public required LootBlock Loot { get; init; }

    /// <summary>The <c>vehicles</c> block.</summary>
    public required VehiclesBlock Vehicles { get; init; }

    /// <summary>The <c>doors</c> block.</summary>
    public required DoorsBlock Doors { get; init; }

    /// <summary>The <c>skins</c> block.</summary>
    public required SkinsBlock Skins { get; init; }

    /// <summary>The <c>weapons</c> block.</summary>
    public required WeaponsBlock Weapons { get; init; }

    /// <summary>The <c>combat</c> block.</summary>
    public required CombatBlock Combat { get; init; }

    /// <summary>The <c>ammo</c> block.</summary>
    public required AmmoBlock Ammo { get; init; }

    /// <summary>The <c>crafting</c> block.</summary>
    public required CraftingBlock Crafting { get; init; }

    /// <summary>The <c>match</c> block.</summary>
    public required MatchBlock Match { get; init; }

    /// <summary>The <c>lobby</c> block (docs/115).</summary>
    public required LobbyBlock Lobby { get; init; }

    /// <summary>The <c>bounty</c> block (docs/115).</summary>
    public required BountyBlock Bounty { get; init; }

    /// <summary>The <c>menu</c> block.</summary>
    public required MenuBlock Menu { get; init; }

    /// <summary>The <c>console</c> block.</summary>
    public required ConsoleBlock Console { get; init; }

    /// <summary>The <c>transport</c> block.</summary>
    public required TransportBlock Transport { get; init; }

    /// <summary>Opt-in production telemetry. Does not change gameplay settings.</summary>
    public ProductionMetricsOptions Metrics { get; init; } = new();
    public PublicQueueOptions PublicQueue { get; init; } = new();

    /// <summary>The <c>features</c> block.</summary>
    public required FeaturesBlock Features { get; init; }

    /// <summary>The <c>dev</c> block.</summary>
    public required DevBlock Dev { get; init; }

    /// <summary>The <c>login</c> block.</summary>
    public required LoginBlock LoginNaming { get; init; }

    /// <summary>The <c>peers</c> block.</summary>
    public required PeersBlock Peers { get; init; }

    /// <summary>The gas time scale that was asked for (1 = the preset's own clock).</summary>
    public float GasScale => ReadFloat(GasTuning.ScaleVariable, 1f);

    /// <summary>The gas damage scale that was asked for (1 = the preset's own table).</summary>
    public float GasDamageScale => ReadFloat(GasTuning.DamageScaleVariable, 1f);

    private float ReadFloat(string name, float fallback) =>
        float.TryParse(Read(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
        && float.IsFinite(value)
            ? value
            : fallback;

    /// <summary>
    /// The whole effective configuration as ONE JSON line, for the boot log: every capture now has
    /// a machine-readable record of the configuration that produced it (S1 §2.3, §2.4).
    /// </summary>
    public string EffectiveJson()
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (string block in ConfigKeys.Blocks)
            {
                writer.WritePropertyName(block);
                writer.WriteStartObject();
                foreach (ConfigKey key in ConfigKeys.All.Where(k => k.Block == block))
                {
                    writer.WritePropertyName(key.Key);
                    WriteValue(writer, key.Effective(this));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case float number:
                writer.WriteNumberValue(Math.Round(number, 4));
                break;
            case double number:
                writer.WriteNumberValue(Math.Round(number, 4));
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// Loads the configuration: the file named by the first <c>.json</c> positional argument, by
    /// <c>CRANBERRY_CONFIG</c>, or <c>&lt;root&gt;\cranberry.json</c>. An absent file is not an
    /// error — it means "every default".
    /// </summary>
    /// <param name="args">The host's command line. Positional values still win.</param>
    /// <param name="environment">Usually <c>Environment.GetEnvironmentVariable</c>.</param>
    /// <param name="fileText">
    /// Test seam: the file's contents, in place of reading <paramref name="args"/>' path from disk.
    /// </param>
    public static CranberryConfig Load(
        string[] args,
        Func<string, string?>? environment = null,
        string? fileText = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        environment ??= System.Environment.GetEnvironmentVariable;

        string[] positional = args;
        string? namedPath = null;
        if (positional.Length > 0
            && positional[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            namedPath = positional[0];
            positional = positional[1..];
        }

        namedPath ??= Blank(environment(PathVariable)) ? null : environment(PathVariable);
        string provisionalRoot = positional.Length > 0 ? positional[0] : DefaultRoot;
        string path = namedPath ?? System.IO.Path.Combine(provisionalRoot, "cranberry.json");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? note = null;
        bool loaded = false;
        try
        {
            string? text = fileText ?? (File.Exists(path) ? File.ReadAllText(path) : null);
            if (text is not null)
            {
                note = Parse(text, values);
                loaded = true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            note = $"cranberry.json at {path} was not read ({ex.Message}); every default is used";
            values.Clear();
        }

        Func<string, string?> read = name =>
        {
            string? fromEnvironment = environment(name);
            if (fromEnvironment is not null)
            {
                return fromEnvironment;
            }

            if (!ConfigKeys.ByLegacy.TryGetValue(name, out ConfigKey? key))
            {
                return null;
            }

            string? fromOverlay = environment(key.Canonical);
            if (!Blank(fromOverlay))
            {
                return fromOverlay;
            }

            return values.TryGetValue(key.Path, out string? fromFile) ? fromFile : null;
        };

        return Bind(read, positional, path, loaded, note);
    }

    /// <summary>
    /// Binds every option record through <paramref name="read"/>. Exposed so a test can build a
    /// configuration from a bare lookup with no file and no process environment.
    /// </summary>
    public static CranberryConfig Bind(
        Func<string, string?> read,
        string[] positional,
        string filePath = "",
        bool fileLoaded = false,
        string? fileNote = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(positional);

        string root = positional.Length > 0 ? positional[0] : (read("CRANBERRY_ROOT") ?? DefaultRoot);
        int loginPort = positional.Length > 1
            ? int.Parse(positional[1], CultureInfo.InvariantCulture)
            : Number(read, "CRANBERRY_PORT_LOGIN", 20042);
        int gatewayPort = positional.Length > 2
            ? int.Parse(positional[2], CultureInfo.InvariantCulture)
            : Number(read, "CRANBERRY_PORT_GATEWAY", 20043);
        string? seedCharacterName = positional.Length > 3
            ? positional[3]
            : (Blank(read("CRANBERRY_SEED_CHARACTER")) ? null : read("CRANBERRY_SEED_CHARACTER"));
        string zoneName = positional.Length > 4
            ? positional[4]
            : (Blank(read("CRANBERRY_ZONE")) ? "LoginZone" : read("CRANBERRY_ZONE")!);
        int bootstrapDelayMs = positional.Length > 5
            ? int.Parse(positional[5], CultureInfo.InvariantCulture)
            : Number(read, "CRANBERRY_BOOTSTRAP_DELAY_MS", 0);
        int autoMatchMs = positional.Length > 6
            ? int.Parse(positional[6], CultureInfo.InvariantCulture)
            : Number(read, "CRANBERRY_AUTO_MATCH_MS", 0);
        string dynamicAppearanceSource = positional.Length > 7
            ? positional[7]
            : (read("CRANBERRY_DYNAMIC_APPEARANCE_SOURCE")
                ?? @"C:\Z1\Server\Data\dynamicAppearanceFriend.bin");
        // docs/13 §9: milliseconds after the lobby's ClientIsReady / a parachute landing before the
        // sample items are dropped around the player. 0 = off.
        int devGroundLootMs = positional.Length > 8
            ? int.Parse(positional[8], CultureInfo.InvariantCulture)
            : Number(read, "CRANBERRY_DEV_GROUND_LOOT_MS", 0);

        // docs/23, D23. The schedule is Cranberry's own, so it is tuned rather than rebuilt:
        // gas.enabled=false turns the whole thing off for a bring-up run, and GasTuning owns the
        // rest — presets, scale, and the range-checked knobs, none of which can throw.
        bool enableGas = Switch(read, "CRANBERRY_GAS", @default: true);
        GasSettings gasSettings = GasTuning.FromEnvironment(read, out string? gasNote);

        // D30 (docs/38): the sky, the frozen clock and the lighting table move together, so they are
        // chosen as one named preset rather than four independent fields.
        EnvironmentSettings sky = EnvironmentPresets.FromNameOrDefault(read("CRANBERRY_SKY"));

        // docs/49 §I3 / docs/56 §I2: both return the SAME instance when nothing is set, which is
        // what MovementProfile's ReferenceEquals short-circuit and regression guard 4 require.
        MovementProfile movementProfile = MovementTuning.FromEnvironment(read, out string? movementNote);
        DescentSettings descent = DescentTuning.FromEnvironment(read, out string? descentNote);
        // D240/D241 (AUDIT-parachute G9): the drop's own environment block. Nothing here can
        // throw and an out-of-range value is ignored with a note, exactly like the descent block.
        DropOptions drop = DropTuning.FromEnvironment(read, out string? dropNote);

        // docs/33: metres around the parachute landing point in which the map's own Z2 spawn markers
        // are rolled. 0 leaves only the development drop.
        float groundLootRadius = float.TryParse(
            read("CRANBERRY_GROUND_LOOT_RADIUS_M"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float radiusOverride) && radiusOverride >= 0f
            ? radiusOverride
            : new ZoneOptions().GroundLootRadius;

        bool lootClusters = Switch(read, "CRANBERRY_LOOT_CLUSTERS", @default: true);
        var features = new FeaturesBlock(
            Containers: Switch(read, "CRANBERRY_CONTAINERS", @default: true),
            MovementStats: Switch(read, "CRANBERRY_MOVEMENT_STATS", @default: true),
            Doors: Switch(read, "CRANBERRY_DOORS", @default: true),
            Vehicles: Switch(read, "CRANBERRY_VEHICLES", @default: true),
            LootClusters: lootClusters,
            // D328: a gun on the ground carries its base appearance shader group at +0x1a4, the
            // field the vehicle spawn already tints the OffRoader with; 0 there is the white
            // composite the owner reported on the floor AK and pump (2026-09-04 20:3x).
            GroundLootShader: Switch(read, "CRANBERRY_GROUND_LOOT_SHADER", @default: true));

        // The landing burst's pacing (docs/73). 0 restores the old single synchronous continuation.
        var transport = new TransportBlock(
            BurstSliceSize: Number(read, "CRANBERRY_BURST_SLICE", 16),
            BurstSliceDelayMs: Number(read, "CRANBERRY_BURST_SLICE_MS", 40),
            MaxLoginConnections: Math.Clamp(Number(read, "CRANBERRY_MAX_LOGIN_CONNECTIONS", 8192), 1, 65536),
            MaxGatewayConnections: Math.Clamp(Number(read, "CRANBERRY_MAX_GATEWAY_CONNECTIONS", 8192), 1, 65536));

        var zoneDefaults = new ZoneOptions();
        // docs/115 §5: one switch carries BOTH numbers the friend's server disagrees with us
        // about — the ClientInteractComponent range and the +0x1b1 flag byte — so it chooses
        // the DEFAULT of each and the two individual knobs still move one at a time.
        bool doorRetailInteract = Switch(read, "CRANBERRY_DOOR_RETAIL_INTERACT", @default: true);
        var doors = new DoorsBlock(
            Rotation: Enum.TryParse(read("CRANBERRY_DOOR_ROTATION"), true, out DoorRotation swept)
                ? swept
                : DoorRotation.QuaternionYUp,
            Collision: Enum.TryParse(
                read("CRANBERRY_DOOR_COLLISION"), true, out DoorCollisionMode collisionSwept)
                ? collisionSwept
                : DoorCollisionMode.VisibleMesh,
            RestreamIntervalMs: Number(
                read, "CRANBERRY_DOOR_RESTREAM_MS", zoneDefaults.DoorRestreamIntervalMs),
            PressWindowMs: Number(read, "CRANBERRY_DOOR_PRESS_MS", zoneDefaults.DoorPressWindowMs),
            SpawnFlags1: FlagByte(
                read("CRANBERRY_DOOR_SPAWN_FLAGS1"),
                doorRetailInteract
                    ? LightweightEntityBody.DoorSpawnFlagsRetail
                    : LightweightEntityBody.DoorSpawnFlagsDefault),
            PositionUpdateType: FlagByte(
                read("CRANBERRY_DOOR_POSITION_UPDATE_TYPE"), zoneDefaults.DoorPositionUpdateType),
            SetCollidable: Switch(read, "CRANBERRY_DOOR_SET_COLLIDABLE", @default: false),
            // docs/114 §1–§4: the lobby's own doors, the hospitals', match-scope door state,
            // and the two halves of the streaming fix.
            Lobby: Switch(read, "CRANBERRY_DOOR_LOBBY", @default: true),
            Hospital: Switch(read, "CRANBERRY_DOOR_HOSPITAL", @default: true),
            HospitalDouble: Switch(read, "CRANBERRY_DOOR_HOSPITAL_DOUBLE", @default: true),
            Shared: Switch(read, "CRANBERRY_DOOR_SHARED", @default: true),
            CapNewOnly: Switch(read, "CRANBERRY_DOOR_CAP_NEW_ONLY", @default: true),
            DespawnRadiusMetres: Metres(
                read, "CRANBERRY_DOOR_DESPAWN_RADIUS", zoneDefaults.DoorDespawnRadiusMetres),
            RetailInteract: doorRetailInteract,
            InteractRangeMetres: Metres(
                read,
                "CRANBERRY_DOOR_INTERACT_RANGE",
                doorRetailInteract ? DoorInteractRange.Retail : DoorInteractRange.Legacy),
            // docs/114 §12 (D264): every family plays one of the two door sounds the retail client
            // keeps resident, so no door swings in silence.
            RetailSound: Switch(read, "CRANBERRY_DOOR_RETAIL_SOUND", @default: true));

        // docs/48 §5.1: unset (or unparsable) = a fresh seed per match; a logged value replays that
        // match's drop AND its gas circles, because both are salted sub-seeds of this one number.
        string? matchSeedText = read("CRANBERRY_MATCH_SEED");
        ulong matchSeed = MatchSeeds.TryParse(matchSeedText, out ulong parsedMatchSeed)
            ? parsedMatchSeed
            : 0;
        string? matchSeedNote = matchSeedText is { Length: > 0 } && matchSeed == 0
            ? $"CRANBERRY_MATCH_SEED='{matchSeedText}' IGNORED (not 1-16 hex digits, or zero) — every "
                + "match will draw its own seed"
            : null;

        // docs/52: the ground-loot re-stream. Clamped rather than validated: an out-of-range knob in
        // a launch script must not be able to stop the host, and a streamer with a 0 ms period or a
        // 0-object cap is a silent no-op that looks exactly like the wave-4 bug it exists to fix.
        var lootStream = new LootStreamOptions
        {
            Enabled = Switch(read, "CRANBERRY_LOOT_STREAM", LootStreamOptions.Default.Enabled),
            RestreamIntervalMs = Math.Clamp(
                Number(read, "CRANBERRY_LOOT_STREAM_MS", LootStreamOptions.Default.RestreamIntervalMs),
                250,
                60_000),
            StreamRadiusMetres = Math.Clamp(
                Metres(read, "CRANBERRY_LOOT_STREAM_RADIUS", LootStreamOptions.Default.StreamRadiusMetres),
                5f,
                400f),
            DespawnRadiusMetres = Math.Clamp(
                Metres(
                    read,
                    "CRANBERRY_LOOT_DESPAWN_RADIUS",
                    LootStreamOptions.Default.DespawnRadiusMetres),
                5f,
                800f),
            MaxLive = Math.Clamp(
                Number(read, "CRANBERRY_LOOT_MAX_LIVE", LootStreamOptions.Default.MaxLive), 1, 1024),
            MaxPerRestream = Math.Clamp(
                Number(
                    read,
                    "CRANBERRY_LOOT_MAX_PER_RESTREAM",
                    LootStreamOptions.Default.MaxPerRestream),
                1,
                256),
            RestreamByteBudget = Math.Clamp(
                Number(
                    read,
                    "CRANBERRY_LOOT_BYTE_BUDGET",
                    LootStreamOptions.Default.RestreamByteBudget),
                0,
                200_000),
            MaxEvictionsPerRestream = Math.Clamp(
                Number(
                    read,
                    "CRANBERRY_LOOT_MAX_EVICTIONS",
                    LootStreamOptions.Default.MaxEvictionsPerRestream),
                0,
                1024),
            PanelRadiusMetres = Math.Clamp(
                Metres(read, "CRANBERRY_LOOT_PANEL_RADIUS", LootStreamOptions.Default.PanelRadiusMetres),
                0f,
                400f),
            PanelMaxRows = Math.Clamp(
                Number(read, "CRANBERRY_LOOT_PANEL_ROWS", LootStreamOptions.Default.PanelMaxRows),
                0,
                512),
            PickupReachMetres = Math.Clamp(
                Metres(read, "CRANBERRY_LOOT_PICKUP_REACH", LootStreamOptions.Default.PickupReachMetres),
                0f,
                100f),
            SendClusters = lootClusters,
        };

        // D270/D275: the floor's density and the laminated-armour rules. Clamped rather than
        // validated, exactly as the streamer's knobs are - a launch script must never be able to
        // stop the host, and LootDensityOptions.Validate would throw on an out-of-range value.
        // spawnChance 0 means "no override": the per-family gates in z2-loot-tables.json stand.
        double spawnChanceOverride = Math.Clamp(
            Real(read, "CRANBERRY_LOOT_SPAWN_CHANCE", 0.0), 0.0, 1.0);
        var lootDensity = LootDensityOptions.Default with
        {
            SpawnChanceOverride = spawnChanceOverride > 0.0 ? spawnChanceOverride : null,
            LaminatedArmourRules = Switch(
                read, "CRANBERRY_LOOT_ARMOUR_RULES", LootDensityOptions.Default.LaminatedArmourRules),
            LaminatedArmourWorldChance = Math.Clamp(
                Real(
                    read,
                    "CRANBERRY_LOOT_ARMOUR_CHANCE",
                    LootDensityOptions.Default.LaminatedArmourWorldChance),
                0.0,
                1.0),
            LaminatedArmourSpacingMetres = Math.Clamp(
                Metres(
                    read,
                    "CRANBERRY_LOOT_ARMOUR_SPACING",
                    LootDensityOptions.Default.LaminatedArmourSpacingMetres),
                0f,
                8192f),
            LaminatedArmourMaxPerSquare = Math.Clamp(
                Number(
                    read,
                    "CRANBERRY_LOOT_ARMOUR_PER_SQUARE",
                    LootDensityOptions.Default.LaminatedArmourMaxPerSquare),
                0,
                1024),
        };

        // D274: retail's second loot channel. Same clamping rule.
        var airdrop = AirdropOptions.Default with
        {
            Enabled = Switch(read, "CRANBERRY_AIRDROPS", AirdropOptions.Default.Enabled),
            FirstDropAtMs = Math.Clamp(
                Number(read, "CRANBERRY_AIRDROP_FIRST_MS", (int)AirdropOptions.Default.FirstDropAtMs),
                0,
                3_600_000),
            DropIntervalMs = Math.Clamp(
                Number(
                    read,
                    "CRANBERRY_AIRDROP_INTERVAL_MS",
                    (int)AirdropOptions.Default.DropIntervalMs),
                1_000,
                3_600_000),
            MaxDropsPerMatch = Math.Clamp(
                Number(read, "CRANBERRY_AIRDROP_MAX", AirdropOptions.Default.MaxDropsPerMatch), 0, 64),
            UnlockMs = Math.Clamp(
                Number(read, "CRANBERRY_AIRDROP_UNLOCK_MS", (int)AirdropOptions.Default.UnlockMs),
                0,
                600_000),
            RifleChance = Math.Clamp(
                Real(read, "CRANBERRY_AIRDROP_RIFLE_CHANCE", AirdropOptions.Default.RifleChance),
                0.0,
                1.0),
            PoolDraws = Math.Clamp(
                Number(read, "CRANBERRY_AIRDROP_POOL_DRAWS", AirdropOptions.Default.PoolDraws), 0, 32),
        };

        // docs/61 §2: vehicles.stream=false restores wave 5's single landing burst exactly. Clamped
        // rather than validated, for the same reason the loot streamer's knobs are.
        var vehicleStream = new VehicleStreamOptions
        {
            Enabled = Switch(read, "CRANBERRY_VEHICLE_STREAM", VehicleStreamOptions.Default.Enabled),
            RestreamIntervalMs = Math.Clamp(
                Number(
                    read,
                    "CRANBERRY_VEHICLE_STREAM_MS",
                    VehicleStreamOptions.Default.RestreamIntervalMs),
                250,
                60_000),
            StreamRadiusMetres = Math.Clamp(
                Metres(
                    read,
                    "CRANBERRY_VEHICLE_STREAM_RADIUS",
                    VehicleStreamOptions.Default.StreamRadiusMetres),
                25f,
                2_000f),
            DespawnRadiusMetres = Math.Clamp(
                Metres(
                    read,
                    "CRANBERRY_VEHICLE_DESPAWN_RADIUS",
                    VehicleStreamOptions.Default.DespawnRadiusMetres),
                25f,
                2_000f),
            MaxLive = Math.Clamp(
                Number(read, "CRANBERRY_VEHICLE_MAX_LIVE", VehicleStreamOptions.Default.MaxLive),
                1,
                300),
            MaxPerRestream = Math.Clamp(
                Number(
                    read,
                    "CRANBERRY_VEHICLE_MAX_PER_RESTREAM",
                    VehicleStreamOptions.Default.MaxPerRestream),
                1,
                64),
        };

        double vehicleSpawnChance = Real(read, "CRANBERRY_VEHICLE_SPAWN_CHANCE",
            zoneDefaults.VehiclePlan.SpawnChance!.Value);
        var vehicles = new VehiclesBlock(
            Plan: zoneDefaults.VehiclePlan with
            {
                // Zero deliberately empties the map; malformed/non-finite input uses the default.
                SpawnChance = double.IsFinite(vehicleSpawnChance)
                    ? Math.Clamp(vehicleSpawnChance, 0.0, 1.0)
                    : zoneDefaults.VehiclePlan.SpawnChance,
            },
            Stream: vehicleStream,
            // docs/115 §3.4: burning is now ON. It was off because an engine that cuts out
            // mid-drive had never been play-tested; the boost changes the trade, because the
            // client's own AbilityEx VehicleTurbo rows carry RESOURCE_TYPE 50 — the boost meter in
            // this build IS the fuel tank, so with no fuel model there is nothing for a boost to
            // spend and nothing to refuse it on. A full tank is still 20 min 50 s of cruising
            // against a 24:50 gas ladder, so fuel never ends a first drive.
            Fuel: new VehicleFuelOptions
            {
                Enabled = Switch(read, "CRANBERRY_VEHICLE_FUEL", @default: true),
                SendGauge = Switch(read, "CRANBERRY_VEHICLE_FUEL_GAUGE", @default: true),
            },
            Relay: new VehicleRelayOptions
            {
                Enabled = Switch(read, "CRANBERRY_VEHICLE_RELAY", VehicleRelayOptions.Default.Enabled),
            },
            // docs/61 §4.4: OFF. Items 3458/3459 (Hotwire) and 3460 (Vehicle Key) are in no loot
            // table, so requiring an ignition would make roughly two thirds of the map's cars
            // unstartable.
            Ignition: new VehicleIgnitionOptions
            {
                Required = Switch(read, "CRANBERRY_VEHICLE_IGNITION", @default: false),
            },
            // docs/115 §2 (AUDIT-vehicles gaps 1-3, 5): ON. Before this lane a car could not be
            // hurt at all - VehicleFleet.ApplyDamage had zero callers - so turning the model on
            // cannot regress anything that ever worked.
            Damage: new VehicleDamageOptions
            {
                Enabled = Switch(read, "CRANBERRY_VEHICLE_DAMAGE", VehicleDamageOptions.Default.Enabled),
                Bullets = Switch(
                    read, "CRANBERRY_VEHICLE_BULLET_DAMAGE", VehicleDamageOptions.Default.Bullets),
                Explosions = Switch(read, "CRANBERRY_VEHICLE_EXPLOSIONS", VehicleDamageOptions.Default.Explosions),
                ExplosionDamage = (uint)Math.Clamp(Number(read, "CRANBERRY_VEHICLE_EXPLOSION_DAMAGE",
                    (int)VehicleDamageOptions.Default.ExplosionDamage), 0, 100000),
                ExplosionFullDamageRadius = (float)Math.Clamp(Real(read, "CRANBERRY_VEHICLE_EXPLOSION_FULL_RADIUS",
                    VehicleDamageOptions.Default.ExplosionFullDamageRadius), 0, 100),
                ExplosionRadius = (float)Math.Clamp(Real(read, "CRANBERRY_VEHICLE_EXPLOSION_RADIUS",
                    VehicleDamageOptions.Default.ExplosionRadius), 0, 100),
                Flip = Switch(read, "CRANBERRY_VEHICLE_FLIP_DAMAGE", VehicleDamageOptions.Default.Flip),
                MaximumCollisionDamage = (uint)Math.Clamp(Number(read, "CRANBERRY_VEHICLE_COLLISION_CAP",
                    (int)VehicleDamageOptions.Default.MaximumCollisionDamage), 0, 100000),
                MinimumCollisionDamage = (uint)Math.Clamp(Number(read, "CRANBERRY_VEHICLE_COLLISION_MIN",
                    (int)VehicleDamageOptions.Default.MinimumCollisionDamage), 0, 100000),
                CollisionDamageMultiplier = Math.Clamp(Metres(read, "CRANBERRY_VEHICLE_COLLISION_MULTIPLIER",
                    VehicleDamageOptions.Default.CollisionDamageMultiplier), 0f, 1f),
                FlipDamageMultiplier = Math.Clamp(Metres(read, "CRANBERRY_VEHICLE_FLIP_MULTIPLIER",
                    VehicleDamageOptions.Default.FlipDamageMultiplier), 0f, 1f),
                FlipInitialGraceMs = Math.Clamp(Number(read, "CRANBERRY_VEHICLE_FLIP_GRACE_MS",
                    VehicleDamageOptions.Default.FlipInitialGraceMs), 0, 60000),
                FlipPulseIntervalMs = Math.Clamp(
                    Number(
                        read,
                        "CRANBERRY_VEHICLE_FLIP_MS",
                        VehicleDamageOptions.Default.FlipPulseIntervalMs),
                    250,
                    120_000),
                WreckOccupantDamage = (uint)Math.Clamp(
                    Number(
                        read,
                        "CRANBERRY_VEHICLE_WRECK_DAMAGE",
                        (int)VehicleDamageOptions.Default.WreckOccupantDamage),
                    0,
                    100_000),
            },
            // docs/115 §3 (AUDIT-vehicles gap 6): ON, and whole. The echo is the half that stops
            // the client's own ClientEffectManager sticking after press #1.
            Boost: new VehicleBoostOptions
            {
                Enabled = Switch(read, "CRANBERRY_VEHICLE_BOOST", VehicleBoostOptions.Default.Enabled),
                TurboOnValue = (byte)Math.Clamp(
                    Number(
                        read,
                        "CRANBERRY_VEHICLE_TURBO_BYTE",
                        VehicleBoostOptions.Default.TurboOnValue),
                    0,
                    255),
            },
            // docs/117 §B: the three deltas that turn a built-but-invisible parked car into a solid,
            // tinted one. All default on (SpawnFlags1 defaults to the collidable bit 0x10); a 0
            // restores the pre-fix bytes for one run.
            PositionBlock: Switch(read, "CRANBERRY_VEHICLE_POSITION_BLOCK", zoneDefaults.VehiclePositionBlock),
            SpawnFlags1: FlagByte(read("CRANBERRY_VEHICLE_SPAWN_FLAGS1"), zoneDefaults.VehicleSpawnFlags1),
            Shader: Switch(read, "CRANBERRY_VEHICLE_SHADER", zoneDefaults.VehicleShader));

        // docs/80 (D53) and docs/106/108: the skin rollbacks. Every default is the behaviour the
        // owner asked for, and every one is a single variable away from what shipped before it.
        var skins = new SkinsBlock(
            new SkinOptions
            {
                DressStowedWeapons = Switch(read, "CRANBERRY_STOWED_MESHES", @default: true),
                SendWornShaderGroups = Switch(read, "CRANBERRY_WORN_SHADER", @default: true),
                SendActiveHandAppearance = Switch(read, "CRANBERRY_HAND_APPEARANCE", @default: true),
                SendWardrobeShaderGroups = Switch(read, "CRANBERRY_WARDROBE_SHADER", @default: true),
                DressLastAfterSkinRows = Switch(read, "CRANBERRY_DRESS_LAST", @default: false),
                SkinRetintOnly = !Switch(read, "CRANBERRY_SKIN_REMODEL", @default: false),
                SuppressIdenticalDress = Switch(read, "CRANBERRY_DRESS_SUPPRESS", @default: true),
                RunSkinCensus = Switch(read, "CRANBERRY_SKIN_CENSUS", @default: true),
                OrderAppearanceRowsByGender = Switch(read, "CRANBERRY_GENDER_ROWS", @default: true),
                GateBackpacksAndPerformanceFootwear =
                    !Switch(read, "CRANBERRY_LOBBY_PACKS", @default: false),
                SendWornSkinsInWorld =
                    Switch(read, "CRANBERRY_WORN_SKINS_IN_WORLD", @default: true),
                RestoreWardrobeSelections =
                    Switch(read, "CRANBERRY_WARDROBE_RESTORE", @default: true),
                WardrobeStoreRoot = Blank(read("CRANBERRY_WARDROBE_STORE"))
                    ? System.IO.Path.Combine(root, "state", "wardrobe")
                    : read("CRANBERRY_WARDROBE_STORE")!,
            },
            AppearanceAuthoredRows: Switch(read, "CRANBERRY_APPEARANCE_AUTHORED_ROWS", @default: true),
            // D316 (docs/124). Anything but the exact word "filtered" is full, so a typo cannot
            // silently ship the grey table back.
            AppearanceTable: AugustDynamicAppearanceTable.WholeTableFromText(
                read("CRANBERRY_APPEARANCE_TABLE"))
                ? "full"
                : "filtered");

        WeaponStageOptions weaponStages = WeaponStageOptions.FromEnvironment(read);

        // docs/58 §11 / docs/95. The draw itself works; what is open is what else a wield needs, so
        // both halves stay OFF and are the experiment together.
        var inventory = new InventoryOptions
        {
            WieldFirstWeapon = Switch(read, "CRANBERRY_WIELD_FIRST_PICKUP", @default: false)
                && weaponStages.Effective.AllowWielding,
            UseWieldSequence = Switch(read, "CRANBERRY_WIELD_SEQUENCE", @default: true),
            GrantWeaponItemsAtBootstrap = Switch(
                read,
                "CRANBERRY_BOOTSTRAP_WEAPON_ITEMS",
                @default: weaponStages.Effective.WriteWeaponItemAddTail),
            FoldItemClassMappings = Switch(read, "CRANBERRY_CLASS_MAPPINGS", @default: true),
            QuickUseConsumables = Switch(read, "CRANBERRY_QUICK_USE_CONSUMABLES", @default: true),
            ShredEveryOfferedItem = Switch(read, "CRANBERRY_SHRED_ALL", @default: true),
            SendDroppedItemNotification = Switch(read, "CRANBERRY_DROP_NOTIFICATION", @default: true),
            DropCarriesFacing = Switch(read, "CRANBERRY_DROP_YAW", @default: true),
        };

        CombatOptions combat = CombatOptions.FromEnvironment(read);

        return new CranberryConfig
        {
            FilePath = filePath,
            FileLoaded = fileLoaded,
            FileNote = fileNote,
            Read = read,
            Root = new RootBlock(root, zoneName, seedCharacterName, dynamicAppearanceSource),
            Ports = new PortsBlock(loginPort, gatewayPort),
            Sky = new SkyBlock(sky),
            Gas = new GasBlock(enableGas, gasSettings, gasNote),
            Movement = new MovementBlock(movementProfile, movementNote),
            Descent = new DescentBlock(
                descent,
                descentNote,
                Switch(read, DescentTuning.LandingGuardVariable, @default: true)),
            Drop = new DropBlock(drop, dropNote, DropTuning.ParachuteSkinFromEnvironment(read)),
            Loot = new LootBlock(lootStream, groundLootRadius, lootDensity, airdrop),
            Vehicles = vehicles,
            Doors = doors,
            Skins = skins,
            Weapons = new WeaponsBlock(weaponStages),
            Combat = new CombatBlock(combat),
            Ammo = new AmmoBlock(combat.Ammo),
            Crafting = new CraftingBlock(CraftingOptions.FromEnvironment(read)),
            Match = new MatchBlock(MatchEndOptions.FromEnvironment(read), matchSeed, matchSeedNote),
            Lobby = new LobbyBlock(LobbyOptions.FromEnvironment(read)),
            Bounty = new BountyBlock(BountyOptions.FromEnvironment(read)),
            Menu = new MenuBlock(
                MenuViewOptions.FromEnvironment(read),
                MenuActorOptions.FromEnvironment(read),
                MenuTopBarOptions.FromEnvironment(read)),
            Console = new ConsoleBlock(ConsoleOptions.FromEnvironment(read)),
            Transport = transport,
            Metrics = ProductionMetricsOptions.FromEnvironment(read),
            PublicQueue = PublicQueueOptions.FromEnvironment(read),
            Features = features,
            Dev = new DevBlock(
                bootstrapDelayMs,
                autoMatchMs,
                devGroundLootMs,
                Switch(read, "CRANBERRY_STARTER_WEAPON", @default: false),
                inventory),
            LoginNaming = new LoginBlock(RegionNamingOptions.FromEnvironment(read)),
            Peers = new PeersBlock(PeerOptions.FromEnvironment(read)),
        };
    }

    /// <summary>Every default, with no file and no environment — the golden-transcript baseline.</summary>
    public static CranberryConfig Defaults() => Bind(_ => null, []);

    private static string? Parse(string text, IDictionary<string, string> values)
    {
        using JsonDocument document = JsonDocument.Parse(
            text,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        var unknown = new List<string>();
        foreach (JsonProperty block in document.RootElement.EnumerateObject())
        {
            if (block.Name.StartsWith('_') || block.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty entry in block.Value.EnumerateObject())
            {
                if (entry.Name.StartsWith('_'))
                {
                    continue;
                }

                string path = block.Name + "." + entry.Name;
                if (!ConfigKeys.ByPath.ContainsKey(path))
                {
                    unknown.Add(path);
                    continue;
                }

                string? value = entry.Value.ValueKind switch
                {
                    JsonValueKind.True => "1",
                    JsonValueKind.False => "0",
                    JsonValueKind.String => entry.Value.GetString(),
                    JsonValueKind.Number => entry.Value.GetRawText(),
                    _ => null,
                };

                if (value is not null)
                {
                    values[path] = value;
                }
            }
        }

        return unknown.Count == 0
            ? null
            : $"cranberry.json: {unknown.Count} key(s) this build does not know were ignored — "
                + string.Join(", ", unknown);
    }

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    private static int Number(Func<string, string?> read, string name, int @default) =>
        int.TryParse(read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : @default;

    /// <summary>
    /// A probability or any other <c>double</c> knob. D270/D274/D275 brought the first ones: the
    /// flat density override, the laminated-armour world chance and the airdrop's rifle chance are
    /// all <c>double</c> on their option records, and rounding them through <see cref="Metres"/>
    /// would quietly change what the host actually ran.
    /// </summary>
    private static double Real(Func<string, string?> read, string name, double @default) =>
        double.TryParse(read(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : @default;

    private static float Metres(Func<string, string?> read, string name, float @default) =>
        float.TryParse(read(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : @default;

    private static bool Switch(Func<string, string?> read, string name, bool @default)
    {
        string? value = read(name);
        if (value is null)
        {
            return @default;
        }

        return !(value.Equals("0", StringComparison.Ordinal)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    private static byte FlagByte(string? text, byte fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string trimmed = text.Trim();
        bool hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return byte.TryParse(
            hex ? trimmed.AsSpan(2) : trimmed.AsSpan(),
            hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out byte parsed)
            ? parsed
            : fallback;
    }
}
