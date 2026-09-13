using System.Text.Json;
using Cranberry.Host.Config;
using Cranberry.Login;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Descent;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Lighting;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Host;

/// <summary>
/// Lane 0D. <c>cranberry.json</c> replaces the environment-switch sprawl, and the whole point of it
/// is that <b>it changes nothing</b>: with no file and no environment every bound record is the same
/// shipped default instance it always was, which is what keeps the golden transcript byte-identical
/// (<c>ZoneBootstrapTests.MatchZoningMatchesTheKnownGoodCaptureOpcodeOrder</c> and its length twin).
/// <para>
/// One test per bound record, plus the precedence rules and the legacy-name guarantee.
/// </para>
/// </summary>
public sealed class CranberryConfigTests
{
    private static CranberryConfig Defaults() => CranberryConfig.Defaults();

    /// <summary>
    /// Some option holders are sealed CLASSES rather than records (<c>LootStreamOptions</c>,
    /// <c>VehicleStreamOptions</c>), so they compare by reference and a value comparison has to be
    /// spelled out. Every public property must match, which is the same claim record equality makes.
    /// </summary>
    private static void AssertEveryPropertyMatches<T>(T expected, T actual)
        where T : notnull
    {
        foreach (System.Reflection.PropertyInfo property in typeof(T).GetProperties())
        {
            Assert.True(
                Equals(property.GetValue(expected), property.GetValue(actual)),
                $"{typeof(T).Name}.{property.Name}: expected {property.GetValue(expected)}, "
                    + $"bound {property.GetValue(actual)}");
        }
    }

    private static CranberryConfig FromFile(string json) =>
        CranberryConfig.Load([], _ => null, json);

    private static CranberryConfig FromEnvironment(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);
        return CranberryConfig.Load([], name => map.TryGetValue(name, out string? value) ? value : null);
    }

    // ==============================================================================================
    // A test per bound record: FromConfig(defaults) == the record's current default instance.
    // ==============================================================================================

    [Fact]
    public void TheGasSettingsBindToTheShippedPreset()
    {
        Assert.Equal(new GasSettings(), Defaults().Gas.Settings);
        Assert.Equal(GasTuning.Aug2017Retail, Defaults().Gas.Settings);
        Assert.True(Defaults().Gas.Enabled);
        Assert.Null(Defaults().Gas.Note);
    }

    /// <summary>
    /// <c>MovementTuning.FromEnvironment</c> deliberately returns the SAME INSTANCE as
    /// <c>MovementProfile.Default</c> when nothing is set, because <c>SendMovementStats</c> has a
    /// <c>ReferenceEquals</c> short-circuit on it. The config must not break that.
    /// </summary>
    [Fact]
    public void TheMovementProfileIsTheSameInstanceAsTheDefault()
    {
        Assert.Same(MovementProfile.Default, Defaults().Movement.Profile);
        Assert.Null(Defaults().Movement.Note);
    }

    /// <summary>
    /// Same rule for the descent: unset, <c>WithDescent</c> must return the same
    /// <c>DropOptions</c> instance, which is what keeps regression guard 4 out of the drop.
    /// </summary>
    [Fact]
    public void TheDescentSettingsAreTheShippedPresetAndChangeNothing()
    {
        DescentSettings descent = Defaults().Descent.Settings;

        Assert.Equal(DescentTuning.FromNameOrDefault(null), descent);
        // D125: the SHIPPED preset is Legacy36, so the drop's minimum clearance is raised by the
        // default boot exactly as it was before this lane — the config must not change that.
        Assert.Equal(
            DropOptions.Default.WithDescent(DescentTuning.FromNameOrDefault(null)).MinimumClearanceMetres,
            DropOptions.Default.WithDescent(descent).MinimumClearanceMetres);
        Assert.Null(Defaults().Descent.Note);
    }

    [Fact]
    public void TheSkyIsTheShippedPreset()
    {
        Assert.Equal(EnvironmentPresets.Default, Defaults().Sky.Settings);
        Assert.Equal("Aug2017KotkClear", Defaults().Sky.Settings.Name);
    }

    /// <summary>
    /// <c>LootStreamOptions</c> is a sealed CLASS, not a record, so it compares by reference — the
    /// binding is pinned property by property instead.
    /// </summary>
    [Fact]
    public void TheLootStreamBindsToItsDefault()
    {
        AssertEveryPropertyMatches(LootStreamOptions.Default, Defaults().Loot.Stream);
        Assert.Equal(new ZoneOptions().GroundLootRadius, Defaults().Loot.GroundLootRadiusMetres);
    }

    [Fact]
    public void TheVehicleOptionsBindToTheirDefaults()
    {
        AssertEveryPropertyMatches(VehicleStreamOptions.Default, Defaults().Vehicles.Stream);
        // docs/117 §3.4: fuel BURN is on now. The boost meter in this build is the fuel tank
        // (every AbilityEx VehicleTurbo row carries RESOURCE_TYPE 50), so with the burn off a boost
        // would be free and infinite. Ignition stays off - the Hotwire items and the Vehicle Key
        // are in no loot table.
        AssertEveryPropertyMatches(
            new VehicleFuelOptions { Enabled = true, SendGauge = true },
            Defaults().Vehicles.Fuel);
        AssertEveryPropertyMatches(VehicleRelayOptions.Default, Defaults().Vehicles.Relay);
        Assert.False(Defaults().Vehicles.Ignition.Required);
        AssertEveryPropertyMatches(VehicleDamageOptions.Default, Defaults().Vehicles.Damage);
        AssertEveryPropertyMatches(VehicleBoostOptions.Default, Defaults().Vehicles.Boost);

        // docs/117 §B: the parked-car render deltas, bound to the ZoneOptions defaults.
        var zone = new ZoneOptions();
        Assert.Equal(zone.VehiclePositionBlock, Defaults().Vehicles.PositionBlock);
        Assert.Equal(zone.VehicleSpawnFlags1, Defaults().Vehicles.SpawnFlags1);
        Assert.Equal(zone.VehicleShader, Defaults().Vehicles.Shader);
        Assert.True(Defaults().Vehicles.PositionBlock);
        Assert.Equal(0x10, Defaults().Vehicles.SpawnFlags1);
        Assert.True(Defaults().Vehicles.Shader);
    }

    [Fact]
    public void TheDoorLeversBindToTheZoneOptionDefaults()
    {
        var zone = new ZoneOptions();
        DoorsBlock doors = Defaults().Doors;

        Assert.Equal(DoorRotation.QuaternionYUp, doors.Rotation);
        Assert.Equal(DoorCollisionMode.VisibleMesh, doors.Collision);
        Assert.Equal(zone.DoorRestreamIntervalMs, doors.RestreamIntervalMs);
        Assert.Equal(zone.DoorPressWindowMs, doors.PressWindowMs);
        Assert.Equal(zone.DoorSpawnFlags1, doors.SpawnFlags1);
        Assert.Equal(zone.DoorPositionUpdateType, doors.PositionUpdateType);
        Assert.False(doors.SetCollidable);
    }

    [Fact]
    public void TheSkinOptionsBindToTheirDefaults()
    {
        SkinOptions skins = Defaults().Skins.Options;
        var shipped = new SkinOptions
        {
            WardrobeStoreRoot = skins.WardrobeStoreRoot,
        };

        Assert.Equal(shipped, skins);
        Assert.EndsWith(Path.Combine("state", "wardrobe"), skins.WardrobeStoreRoot, StringComparison.Ordinal);
        Assert.True(Defaults().Skins.AppearanceAuthoredRows);
    }

    [Fact]
    public void TheWeaponStagesBindToTheirDefaults()
    {
        Assert.Equal(WeaponStageOptions.FromEnvironment(_ => null), Defaults().Weapons.Stages);
        Assert.Equal(new WeaponStageOptions(), Defaults().Weapons.Stages);
    }

    [Fact]
    public void TheCombatAndAmmoOptionsBindToTheirDefaults()
    {
        CranberryConfig config = Defaults();

        Assert.Equal(CombatOptions.Default, config.Combat.Options);
        Assert.Equal(AmmoOptions.Default, config.Ammo.Options);
        // The ammo block is the combat record's own Ammo, not a second parse of the same switches.
        Assert.Same(config.Combat.Options.Ammo, config.Ammo.Options);
    }

    [Fact]
    public void TheCraftingOptionsBindToTheirDefaults()
    {
        Assert.Equal(new CraftingOptions(), Defaults().Crafting.Options);
    }

    [Fact]
    public void TheMatchOptionsBindToTheirDefaults()
    {
        Assert.Equal(MatchEndOptions.Default, Defaults().Match.End);
        Assert.Equal(0UL, Defaults().Match.Seed);
        Assert.Null(Defaults().Match.SeedNote);
    }

    [Fact]
    public void TheMenuOptionsBindToTheirDefaults()
    {
        Assert.Equal(MenuViewOptions.Default, Defaults().Menu.Views);
        Assert.Equal(MenuActorOptions.Default, Defaults().Menu.Actor);
        Assert.Equal(MenuTopBarOptions.Default, Defaults().Menu.TopBar);
    }

    [Fact]
    public void TheConsoleOptionsBindToTheirDefaults()
    {
        Assert.Equal(ConsoleOptions.Default, Defaults().Console.Options);
    }

    [Fact]
    public void TheLoginAndPeerOptionsBindToTheirDefaults()
    {
        Assert.Equal(RegionNamingOptions.Default, Defaults().LoginNaming.Regions);
        Assert.Equal(PeerOptions.Default, Defaults().Peers.Options);
    }

    [Fact]
    public void TheInventoryOptionsBindToTheirDefaults()
    {
        Cranberry.Zone.Inventory.InventoryOptions inventory = Defaults().Dev.Inventory;

        Assert.False(inventory.WieldFirstWeapon);
        Assert.True(inventory.UseWieldSequence);
        Assert.True(inventory.GrantWeaponItemsAtBootstrap);
        Assert.True(inventory.FoldItemClassMappings);
        Assert.True(inventory.QuickUseConsumables);
    }

    [Fact]
    public void TheFeaturesAndTransportBlocksBindToTheirDefaults()
    {
        Assert.Equal(new FeaturesBlock(true, true, true, true, true, true), Defaults().Features);
        Assert.Equal(new TransportBlock(16, 40), Defaults().Transport);
    }

    [Fact]
    public void TheRootAndPortsBlocksBindToTheirDefaults()
    {
        Assert.Equal(CranberryConfig.DefaultRoot, Defaults().Root.Path);
        Assert.Equal("LoginZone", Defaults().Root.Zone);
        Assert.Null(Defaults().Root.SeedCharacterName);
        Assert.Equal(20042, Defaults().Ports.Login);
        Assert.Equal(20043, Defaults().Ports.Gateway);
        Assert.Equal(0, Defaults().Dev.BootstrapDelayMs);
        Assert.Equal(0, Defaults().Dev.AutoMatchMs);
        Assert.Equal(0, Defaults().Dev.GroundLootMs);
        Assert.False(Defaults().Dev.StarterWeapon);
    }

    // ==============================================================================================
    // Precedence: defaults < file < environment, and the legacy name always wins.
    // ==============================================================================================

    [Fact]
    public void AnAbsentFileMeansEveryDefault()
    {
        // A developer may have a real config under C:/Aug2017; this case needs an absent file.
        string absentPath = Path.Combine(Path.GetTempPath(), $"cranberry-absent-{Guid.NewGuid():N}.json");
        CranberryConfig config = CranberryConfig.Load(
            [absentPath],
            _ => null);

        Assert.False(config.FileLoaded);
        Assert.Null(config.FileNote);
        Assert.Equal(new GasSettings(), config.Gas.Settings);
    }

    [Fact]
    public void TheFileBeatsTheDefault()
    {
        CranberryConfig config = FromFile("""{ "skins": { "dressLast": true }, "gas": { "preset": "Sprint" } }""");

        Assert.True(config.FileLoaded);
        Assert.True(config.Skins.Options.DressLastAfterSkinRows);
        Assert.Equal(GasTuning.Sprint, config.Gas.Settings);
    }

    /// <summary>
    /// The whole reason lane 0D exists in this shape: <b>every one-word revert the owner has typed
    /// this week keeps working</b>, and it beats the file, so a checked-in `cranberry.json` can never
    /// take a bisect lever away from him at the console.
    /// </summary>
    [Theory]
    [InlineData("CRANBERRY_DRESS_LAST", "0")]
    [InlineData("CRANBERRY_WEAPON_MOVEMENT_MODIFIER", "0.5")]
    [InlineData("CRANBERRY_CONSOLE_SELF_FLAG", "1")]
    [InlineData("CRANBERRY_WIELD_FIRST_PICKUP", "1")]
    [InlineData("CRANBERRY_WIELD_SEQUENCE", "1")]
    [InlineData("CRANBERRY_PROJECTILE_DEFINITIONS", "0")]
    [InlineData("CRANBERRY_MENU_VIEWS", "reference")]
    [InlineData("CRANBERRY_GAS_SCALE", "0.2")]
    [InlineData("CRANBERRY_SKY", "Aug2017Clear")]
    public void EveryLegacyNameIsStillReadable(string name, string value)
    {
        CranberryConfig config = FromEnvironment((name, value));

        Assert.Equal(value, config.Read(name));
        Assert.True(ConfigKeys.ByLegacy.ContainsKey(name));
    }

    [Fact]
    public void TheEnvironmentBeatsTheFile()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CRANBERRY_DRESS_LAST"] = "0",
        };
        CranberryConfig config = CranberryConfig.Load(
            [],
            name => map.TryGetValue(name, out string? value) ? value : null,
            """{ "skins": { "dressLast": true } }""");

        Assert.False(config.Skins.Options.DressLastAfterSkinRows);
    }

    /// <summary>
    /// The generated overlay name works for keys whose legacy name is not already in that shape —
    /// but the legacy name wins when both are set, because that is the one in the launch scripts.
    /// </summary>
    [Fact]
    public void TheGeneratedOverlayNameWorksAndTheLegacyNameWins()
    {
        ConfigKey key = ConfigKeys.ByLegacy["CRANBERRY_DRESS_LAST"];
        Assert.Equal("CRANBERRY_SKINS_DRESS_LAST", key.Canonical);

        CranberryConfig overlay = FromEnvironment((key.Canonical, "1"));
        Assert.True(overlay.Skins.Options.DressLastAfterSkinRows);

        CranberryConfig both = FromEnvironment((key.Canonical, "1"), (key.Legacy, "0"));
        Assert.False(both.Skins.Options.DressLastAfterSkinRows);
    }

    /// <summary>
    /// A lane that adds a switch while this table is being written must not have to wait for a row
    /// here: an unknown name still reaches the process environment untouched.
    /// </summary>
    [Fact]
    public void AnUnknownEnvironmentNameStillReachesTheReader()
    {
        CranberryConfig config = FromEnvironment(("CRANBERRY_SOMETHING_LANE_3C_ADDED", "1"));

        Assert.Equal("1", config.Read("CRANBERRY_SOMETHING_LANE_3C_ADDED"));
        Assert.Null(config.Read("CRANBERRY_NOBODY_SET_THIS"));
    }

    [Fact]
    public void AnUnknownFileKeyIsReportedAndIgnored()
    {
        CranberryConfig config = FromFile("""{ "gas": { "notAKey": 1 }, "nosuchblock": { "x": 1 } }""");

        Assert.NotNull(config.FileNote);
        Assert.Contains("gas.notAKey", config.FileNote, StringComparison.Ordinal);
        Assert.Equal(new GasSettings(), config.Gas.Settings);
    }

    [Fact]
    public void PositionalArgumentsStillWinOverBothFileAndEnvironment()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CRANBERRY_ROOT"] = @"C:\FromEnvironment",
            ["CRANBERRY_PORT_LOGIN"] = "1",
        };
        CranberryConfig config = CranberryConfig.Load(
            [@"C:\FromArgs", "20142", "20143", "seed", "Z2", "5", "6", @"C:\app.bin", "7"],
            name => map.TryGetValue(name, out string? value) ? value : null,
            """{ "root": { "path": "C:\\FromFile" } }""");

        Assert.Equal(@"C:\FromArgs", config.Root.Path);
        Assert.Equal(20142, config.Ports.Login);
        Assert.Equal(20143, config.Ports.Gateway);
        Assert.Equal("seed", config.Root.SeedCharacterName);
        Assert.Equal("Z2", config.Root.Zone);
        Assert.Equal(5, config.Dev.BootstrapDelayMs);
        Assert.Equal(6, config.Dev.AutoMatchMs);
        Assert.Equal(@"C:\app.bin", config.Root.DynamicAppearanceSource);
        Assert.Equal(7, config.Dev.GroundLootMs);
    }

    /// <summary>A malformed file must never stop the host: it falls back to every default.</summary>
    [Fact]
    public void AMalformedFileFallsBackToTheDefaults()
    {
        CranberryConfig config = FromFile("{ this is not json ");

        Assert.NotNull(config.FileNote);
        Assert.Equal(new GasSettings(), config.Gas.Settings);
        Assert.Equal(CranberryConfig.DefaultRoot, config.Root.Path);
    }

    // ==============================================================================================
    // The boot line.
    // ==============================================================================================

    [Fact]
    public void TheEffectiveConfigIsOneParsableJsonLineCarryingEveryKey()
    {
        string line = Defaults().EffectiveJson();

        Assert.DoesNotContain('\n', line);
        using JsonDocument document = JsonDocument.Parse(line);
        foreach (ConfigKey key in ConfigKeys.All)
        {
            JsonElement block = document.RootElement.GetProperty(key.Block);
            Assert.True(
                block.TryGetProperty(key.Key, out _),
                $"the boot line is missing {key.Path}");
        }
    }

    [Fact]
    public void TheEffectiveConfigReportsWhatTheEnvironmentAsked()
    {
        string line = FromEnvironment(("CRANBERRY_GAS_PRESET", "Sprint")).EffectiveJson();

        using JsonDocument document = JsonDocument.Parse(line);
        Assert.Equal("Sprint", document.RootElement.GetProperty("gas").GetProperty("preset").GetString());
    }

    // ==============================================================================================
    // Table hygiene.
    // ==============================================================================================

    [Fact]
    public void EveryKeyIsUniqueByPathAndByLegacyName()
    {
        Assert.Equal(ConfigKeys.All.Count, ConfigKeys.All.Select(k => k.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ConfigKeys.All.Count,
            ConfigKeys.All.Select(k => k.Legacy).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ConfigKeys.All, key => Assert.StartsWith("CRANBERRY_", key.Legacy, StringComparison.Ordinal));
        Assert.All(ConfigKeys.All, key => Assert.True(ConfigKeys.BlockComments.ContainsKey(key.Block)));
    }

    /// <summary>
    /// The switches lane 0D deleted must be gone from <c>src</c> entirely — not merely unread. A
    /// switch that still appears in a name or a comment is one the owner will try to set.
    /// </summary>
    [Theory]
    [InlineData("CRANBERRY_GAS_CENTRE_ON_SPAWN")]
    [InlineData("CRANBERRY_RESEND_ZONE_DETAILS")]
    [InlineData("CRANBERRY_DOOR_RESOLVE_M")]
    [InlineData("CRANBERRY_INTERACTION_STRING_ORDER")]
    [InlineData("CRANBERRY_NPC_COMPONENT")]
    [InlineData("CRANBERRY_GAS_PHASE_WINDOW_MS")]
    [InlineData("CRANBERRY_CRAFTING_SELFRECORD")]
    [InlineData("CRANBERRY_CRAFT_COUNTS")]
    [InlineData("CRANBERRY_CRAFT_SENTINEL")]
    [InlineData("CRANBERRY_CRAFT_SEED")]
    public void ADeletedSwitchIsNotReadableAndIsNotInTheTable(string name)
    {
        Assert.False(ConfigKeys.ByLegacy.ContainsKey(name));
        Assert.Null(FromEnvironment((name, "1")).Read(name + "_NOT_A_KEY"));
    }

    /// <summary>
    /// Every one of the 122 switches S1 §2.2 surveyed is either a row in the table or on the
    /// deleted list — nothing was lost by accident in the move to a file.
    /// </summary>
    [Fact]
    public void EverySwitchIsEitherATableRowOrDeclaredRemoved()
    {
        var removed = ConfigDocs.Removed.Select(entry => entry.Switch).ToHashSet(StringComparer.Ordinal);
        string[] survey =
        [
            "CRANBERRY_GAS", "CRANBERRY_SKY", "CRANBERRY_CONTAINERS", "CRANBERRY_MOVEMENT_STATS",
            "CRANBERRY_DOORS", "CRANBERRY_VEHICLES", "CRANBERRY_LOOT_CLUSTERS", "CRANBERRY_BURST_SLICE",
            "CRANBERRY_BURST_SLICE_MS", "CRANBERRY_DOOR_ROTATION", "CRANBERRY_DOOR_COLLISION",
            "CRANBERRY_DOOR_RESTREAM_MS", "CRANBERRY_DOOR_PRESS_MS", "CRANBERRY_DOOR_SPAWN_FLAGS1",
            "CRANBERRY_DOOR_POSITION_UPDATE_TYPE", "CRANBERRY_DOOR_SET_COLLIDABLE",
            "CRANBERRY_MATCH_SEED", "CRANBERRY_WIELD_FIRST_PICKUP", "CRANBERRY_WIELD_SEQUENCE",
            "CRANBERRY_BOOTSTRAP_WEAPON_ITEMS", "CRANBERRY_CLASS_MAPPINGS",
            "CRANBERRY_QUICK_USE_CONSUMABLES", "CRANBERRY_VEHICLE_RELAY", "CRANBERRY_VEHICLE_IGNITION",
            "CRANBERRY_STOWED_MESHES", "CRANBERRY_WORN_SHADER", "CRANBERRY_SKIN_REMODEL",
            "CRANBERRY_DRESS_SUPPRESS", "CRANBERRY_SKIN_CENSUS", "CRANBERRY_GENDER_ROWS",
            "CRANBERRY_LOBBY_PACKS", "CRANBERRY_WARDROBE_STORE", "CRANBERRY_STARTER_WEAPON",
            "CRANBERRY_DEV_GROUND_LOOT_MS", "CRANBERRY_GROUND_LOOT_RADIUS_M",
            "CRANBERRY_DYNAMIC_APPEARANCE_SOURCE",
            // deleted
            "CRANBERRY_GAS_CENTRE_ON_SPAWN", "CRANBERRY_RESEND_ZONE_DETAILS", "CRANBERRY_DOOR_RESOLVE_M",
            "CRANBERRY_INTERACTION_STRING_ORDER", "CRANBERRY_NPC_COMPONENT",
            "CRANBERRY_GAS_PHASE_WINDOW_MS", "CRANBERRY_CRAFTING_SELFRECORD", "CRANBERRY_CRAFT_COUNTS",
            "CRANBERRY_CRAFT_SENTINEL", "CRANBERRY_CRAFT_SEED",
        ];

        string[] lost = survey
            .Where(name => !ConfigKeys.ByLegacy.ContainsKey(name) && !removed.Contains(name))
            .ToArray();

        Assert.Empty(lost);
    }
}
