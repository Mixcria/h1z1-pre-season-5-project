using System.Net;
using Cranberry.Host;
using Cranberry.Host.Config;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Descent;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Lighting;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World.Doors;

// Every setting this host runs with comes from one place: `cranberry.json`, overlaid by the
// environment (lane 0D; plan §3 Phase 0, S1 §2.4). `Config/ConfigKeys.cs` is the table, and
// `Config/CranberryConfig.cs` binds it to the option records that already owned every default —
// nothing here decides a value. Precedence is defaults < file < environment, and inside the
// environment the LEGACY name wins, so every one-word revert in the owner's launch scripts keeps
// working unchanged: CRANBERRY_DRESS_LAST=0, CRANBERRY_WEAPON_MOVEMENT_MODIFIER=0.5,
// CRANBERRY_MENU_VIEWS=reference, CRANBERRY_WIELD_FIRST_PICKUP=1, and the rest of docs/110.
//
// Usage: Cranberry.Host [root=C:\Aug2017] [loginPort=20042] [gatewayPort=20043] [seedCharacterName]
//                       [zoneName=LoginZone] [bootstrapDelayMs=0] [autoMatchMs=0]
//                       [dynamicAppearanceSource] [devGroundLootMs=0]
//        Cranberry.Host <cranberry.json> [the same positional list]
// The positional arguments still win over both file and environment, because run-host.ps1 passes
// seven of them on every launch. An absent file is not an error: it means every default.
CranberryConfig config = CranberryConfig.Load(args);

string root = config.Root.Path;
int loginPort = config.Ports.Login;
int gatewayPort = config.Ports.Gateway;
string? seedCharacterName = config.Root.SeedCharacterName;
string zoneName = config.Root.Zone;
int bootstrapDelayMs = config.Dev.BootstrapDelayMs;
int autoMatchMs = config.Dev.AutoMatchMs;
string dynamicAppearanceSource = config.Root.DynamicAppearanceSource;
// Development ground loot (docs/13 §9): milliseconds after the lobby's ClientIsReady / a parachute
// landing before the sample items are dropped around the player. 0 = off.
int devGroundLootMs = config.Dev.GroundLootMs;

// docs/23, D23. The gas schedule is Cranberry's own design, so it is tuned from the config rather
// than rebuilt: gas.enabled=false turns the whole thing off for a bring-up run, and GasTuning owns
// the preset, the two scales and the range-checked knobs — every one of which is reported in the
// note rather than failing the boot.
bool enableGas = config.Gas.Enabled;
GasSettings gasSettings = config.Gas.Settings;
string? gasNote = config.Gas.Note;

// docs/36 §W: the starter weapon is OFF by default. It puts two never-before-sent packets on the
// wire (an ItemAdd and the first equipment-slot row) and docs/32's two regressions were both
// equipment-shaped, so it stays one variable at a time. Plan §5.1 retires it in favour of the
// admin channel's `give <item> wield`, which is lane 0B and does not exist yet.
bool giveStarterWeapon = config.Dev.StarterWeapon;

// docs/33: metres around the parachute landing point in which the map's own Z2 spawn markers are
// rolled. 0 leaves only the development drop.
float groundLootRadius = config.Loot.GroundLootRadiusMetres;

// D30 (docs/38): the sky, the frozen clock and the lighting table move together, so they are chosen
// as one named preset rather than four independent fields. Default Aug2017KotkClear (docs/82 §4).
EnvironmentSettings sky = config.Sky.Settings;

// Every one of these subsystems has a one-variable rollback, because docs/32's four-hour outage was
// a new packet on a proven-safe path and the cheapest way to bisect one is to turn it off.
bool containers = config.Features.Containers;
bool movementStats = config.Features.MovementStats;
bool doors = config.Features.Doors;
bool vehicles = config.Features.Vehicles;
bool lootClusters = config.Features.LootClusters;
bool groundLootShader = config.Features.GroundLootShader;

// Slice landing bootstrap work in addition to the transport's reliable send window.
// transport.burstSlice=0 restores the old single synchronous continuation.
int burstSliceSize = config.Transport.BurstSliceSize;
int burstSliceDelayMs = config.Transport.BurstSliceDelayMs;

// docs/47 §3b / docs/55 §I1 / docs/79 §4 / docs/85 §5: the door levers. The +0xa0 packing is a
// quaternion and only its yaw SIGN is still open; DoorCollisionMode is cosmetic (D77); the press
// window is the client's own 800 ms swing; and the two flag bytes are bytes, so every candidate is
// a restart and not a rebuild.
DoorRotation doorRotation = config.Doors.Rotation;
DoorCollisionMode doorCollision = config.Doors.Collision;
int doorRestreamMs = config.Doors.RestreamIntervalMs;
int doorPressMs = config.Doors.PressWindowMs;
byte doorSpawnFlags1 = config.Doors.SpawnFlags1;
byte doorPositionUpdateType = config.Doors.PositionUpdateType;
// docs/114: the lobby's five doors, the 39 hospital doors, match-scope door state, the two
// halves of the streaming fix, and the friend server's two interaction numbers (D53).
bool lobbyDoors = config.Doors.Lobby;
bool hospitalDoors = config.Doors.Hospital;
bool hospitalDoubleDoors = config.Doors.HospitalDouble;
bool sharedMatchDoors = config.Doors.Shared;
bool doorCapNewOnly = config.Doors.CapNewOnly;
float doorDespawnRadius = config.Doors.DespawnRadiusMetres;
bool doorRetailInteract = config.Doors.RetailInteract;
float doorInteractRange = config.Doors.InteractRangeMetres;
bool doorRetailSound = config.Doors.RetailSound;

// docs/48 §5.1: the match seed. Unset (or unparsable) = a fresh seed per match, drawn from the
// session guid and the wall clock and logged as 16 hex digits; setting it to a logged value replays
// that match's drop AND its gas circles, because both are salted sub-seeds of this one number.
ulong matchSeed = config.Match.Seed;
string? matchSeedNote = config.Match.SeedNote;

// docs/49 §I3 and docs/56 §I2: unset, both return the SAME instance the defaults ship, which is
// what SendMovementStats' ReferenceEquals short-circuit and regression guard 4 require.
MovementProfile movementProfile = config.Movement.Profile;
string? movementNote = config.Movement.Note;
DescentSettings descent = config.Descent.Settings;
string? descentNote = config.Descent.Note;

// D240/D241 (AUDIT-parachute G5, G6, G9): the drop's own environment block, and the parachute skin.
// Unset, FromEnvironment returns DropOptions.Default itself and the skin is 0 — the default canopy —
// so a quiet boot plans and dresses exactly the drop it always has.
DropOptions dropOptions = config.Drop.Options;
string? dropNote = config.Drop.Note;
uint parachuteSkinItemId = config.Drop.ParachuteSkinItemId;

// docs/52 and docs/61 §2: the two streamers. Every knob is clamped rather than validated — an
// out-of-range value in a launch script must not be able to stop the host, and a streamer with a
// 0 ms period is a silent no-op that looks exactly like the wave-4 bug it exists to fix.
LootStreamOptions lootStream = config.Loot.Stream;
VehicleStreamOptions vehicleStream = config.Vehicles.Stream;
VehicleFuelOptions vehicleFuel = config.Vehicles.Fuel;
VehicleRelayOptions vehicleRelay = config.Vehicles.Relay;
VehicleIgnitionOptions vehicleIgnition = config.Vehicles.Ignition;
// docs/115: the two arms this lane added. Damage is on because before it a car could not be hurt
// at all; boost is on because a half-boost is worse than none — without the server's echo the
// client's own effect manager refuses every press after the first.
VehicleDamageOptions vehicleDamage = config.Vehicles.Damage;
VehicleBoostOptions vehicleBoost = config.Vehicles.Boost;

// docs/60 (D33): weapon data remains staged and observable. These are the RAW stages, because
// Describe() is what prints "CRANBERRY_WIELD=1 IGNORED"; Effective is what the server then runs.
WeaponStageOptions weaponStages = config.Weapons.Stages;

// docs/58 §11 and docs/95: the draw itself works — the slot-7 row reached the wire and the client
// did not crash, so docs/45 is closed — but the owner could not move or act until he dropped the
// gun. Both halves therefore stay OFF and are the experiment together: dev.wieldFirstPickup re-arms
// the automatic draw, dev.wieldSequence swaps the one-packet dress for Z1's eight-packet order.
InventoryOptions inventoryOptions = config.Dev.Inventory;

// docs/62 (D34/D35): crafting. Stage 1 (the recipe list after the in-match ClientIsReady) is on.
CraftingOptions crafting = config.Crafting.Options;

// docs/80 (D53) with docs/106 (D190) and docs/108 (D211): the skin switches. Every default is the
// behaviour the owner asked for — stowed guns get a mesh, worn and wardrobe attachments carry their
// own colourway, the gun in the hand carries the same rows as the gun on the back, a pick re-tints
// rather than re-models, an identical re-dress is dropped, the grey census runs — and every one is
// a single variable away from what shipped before it. D211: skins.dressLast is OFF, because an
// appended dress after the lobby skin rows is the packet that wedged the client's Z2 world load
// twice on 2026-09-03; CRANBERRY_DRESS_LAST=1 puts it back and the client refuses to zone.
SkinOptions skins = config.Skins.Options;

// D203 (docs/107): the thirteen Cranberry-authored appearance rows are reached through a
// process-wide static, so lane 0D's config record sets it here — before any table is built —
// rather than leaving the type to read the environment behind the host's back.
AugustDynamicAppearanceTable.ApplyAuthoredRows = config.Skins.AppearanceAuthoredRows;

// D316 (docs/124): the same road for the whole-table switch. Set before the table is built.
AugustDynamicAppearanceTable.ShipWholeTable =
    AugustDynamicAppearanceTable.WholeTableFromText(config.Skins.AppearanceTable);

string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

using var log = ConsoleFileLog.FromEnvironment(Path.Combine(root, "logs", $"host-{stamp}.log"));
log.Info($"Cranberry host starting; root={root} loginPort={loginPort} gatewayPort={gatewayPort}");
if (matchSeedNote is not null)
{
    log.Warn(matchSeedNote);
}

log.Info(movementNote is not null
    ? $"movement tuning: {movementNote}"
    : $"movement tuning: {MovementTuning.NameOf(movementProfile)} (no CRANBERRY_MOVE_* set) — "
        + MovementTuning.DescribeSpeeds(movementProfile));

log.Info(gasNote is not null
    ? $"gas tuning: {gasNote}"
    : $"gas tuning: {GasTuning.NameOf(gasSettings)} (no CRANBERRY_GAS_* set) — "
        + GasTuning.DescribeSchedule(gasSettings));

// D238: the shipped ride is the client's own KotK.SkySpawn slab, so the "unset" hint now names
// the presets that RAISE the release rather than the one that restores it.
log.Info(descentNote is not null
    ? $"descent tuning: {descentNote}"
    : $"descent tuning: {DescentTuning.NameOf(descent)} (shipped default; CRANBERRY_DESCENT_PRESET "
        + "unset — Owner30 and Legacy36 raise the release to ~1,212 m and ~1,454 m) — "
        + DescentTuning.Describe(descent));
// D239: the one line that says whether a live chute can still be dismounted in mid-air.
log.Info(config.Descent.LandingGuard
    ? "descent landing guard: ON (D239) — the handover is forced only when the chute is within "
        + $"{DescentDeadline.GroundProximityMetres:F0} m of the ground or its pose stream has been "
        + $"silent for {DescentDeadline.StreamSilenceSeconds:F0} s; a live chute in the air is never dismounted"
    : "descent landing guard: OFF (CRANBERRY_DESCENT_LANDING_GUARD=0) — the pre-D239 deadline is back, "
        + "and it CAN force-dismount a live rider in mid-air (it did so three times on 2026-09-03)");
if (dropNote is not null)
{
    log.Info(dropNote);
}

// Read the 4.1 MB placement file and the loot tables here, not on the listener thread at the first
// parachute landing.
log.Info(ZoneService.PreloadLootData());

// The login key the client carries as a base64 string (present in build 0.0.118.208059;
// confirmed on 2026-08-27 by decrypting the first LoginRequest with it).
byte[] loginKey = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");

using var recorder = FilePacketRecorder.FromEnvironment(Path.Combine(root, "captures", $"wire-{stamp}.txt"));
var gatewayTickets = new GatewayTicketRegistry();
string gatewayAddress = $"127.0.0.1:{gatewayPort}";
// The roster persists across restarts (data\roster.json); the development seed only fills an
// empty roster, so characters created or deleted in the client survive.
string rosterPath = Path.Combine(root, "data", "roster.json");
var characters = CharacterRosterStore.Load(rosterPath);
log.Info($"roster {rosterPath}: {characters.Snapshot().Length} character(s)");
CharacterEntry? newDevelopmentCharacter = null;
if (!string.IsNullOrWhiteSpace(seedCharacterName) && characters.Snapshot().Length == 0)
{
    CharacterEntry seeded = characters.Create(
        serverId: 1,
        new CharacterCreatePayload(
            EmpireId: 2,
            HeadId: 3,
            ProfileId: 270,
            Gender: 2,
            Name: seedCharacterName,
            SkinToneId: 664,
            HairId: 2,
            RuntimeValue: 2,
            OperatingSystem: "Windows 10 Home",
            PlatformVersion: "6.2",
            ClientVersion: GatewayLoginRequest.AugustVersion,
            Environment: "Live"));
    log.Info($"seeded development character '{seedCharacterName}' as entity={seeded.EntityKey} server={seeded.ServerId}");
    newDevelopmentCharacter = seeded;
}

var localAccounts = LocalAccountDirectory.Load(Path.Combine(root, "data", "local-accounts.json"),
    characters.Snapshot().Select(character => character.EntityKey));
if (newDevelopmentCharacter is not null)
    localAccounts.BindCharacter(localAccounts.LocalAccountId, newDevelopmentCharacter.EntityKey);

// Repair existing playtest accounts before admitting players. Future accounts take the same
// receipt-protected path at their first gateway login. The owner's wallet/items are excluded.
var starterAccounts = new AccountEconomyStore(Path.Combine(root, "state", "economy"), log.Warn);
foreach (string accountId in localAccounts.CharacterOwnersSnapshot().Values.Distinct()
    .Where(accountId => accountId != localAccounts.LocalAccountId))
{
    var starter = StarterAccountProfile.Apply(starterAccounts, accountId);
    if (!starter.Succeeded)
        throw new AccountEconomyStoreException(starter.Error ?? "Starter account provisioning failed.");
    if (!starter.Replayed) log.Info($"provisioned starter package for existing account {accountId}");
    var clientSkins = ClientSkinGrant.Apply(starterAccounts, accountId);
    if (!clientSkins.Succeeded)
        throw new AccountEconomyStoreException(clientSkins.Error ?? "Client skin grant failed.");
    if (!clientSkins.Replayed) log.Info($"provisioned missing Client skins for existing account {accountId}");
}
var login = new LoginService(
    log,
    recorder,
    loginKey,
    characters: characters,
    gatewayTickets: gatewayTickets,
    gatewayAddress: gatewayAddress,
    // docs/105 §9 (U-7): read from the config record rather than by the service itself, so
    // login.regionKeys in cranberry.json moves it as well as CRANBERRY_LOGIN_REGION_KEYS.
    regions: config.LoginNaming.Regions,
    accounts: localAccounts);
// Public traffic enters through the authenticated TLS launcher tunnel. Legacy UDP stays local.
using var loginListener = new SoeListener(new IPEndPoint(IPAddress.Loopback, loginPort), login, log,
    new SoeListenerOptions { MaxConnections = config.Transport.MaxLoginConnections, EnableDiagnostics = config.Metrics.Enabled });
// docs/105 §9 (U-7): whether ServerInfo Region carries the client's own resolvable locale key.
log.Info(login.Regions.Describe());
// docs/105 §9 (U-1, U-5): the menu actor's stance, its 09 16 00 echo, and the mark the self record
// spawns at. SpawnPosition is taken from here so the one switch moves the value everywhere.
var menuActor = config.Menu.Actor;
// docs/105 §10 (U-3, U-4): the menu top bar (Experience + Currency) and the MOTD panel.
var menuTopBar = config.Menu.TopBar;
var zoneOptions = new ZoneOptions
{
    MenuActor = menuActor,
    MenuTopBar = menuTopBar,
    EconomyStoreRoot = Path.Combine(root, "state", "economy"),
    ProvisionStarterAccounts = true,
    HostedGames = new Cranberry.Zone.HostedGames.HostedGameStore(Path.Combine(root, "state", "hosted-games.json")),
    SpawnPosition = menuActor.SpawnPosition,
    ZoneName = zoneName,
    BootstrapDelayMs = bootstrapDelayMs,
    AutoMatchMs = autoMatchMs,
    DynamicAppearanceSourcePath = dynamicAppearanceSource,
    DevGroundLootMs = devGroundLootMs,
    GroundLootRadius = groundLootRadius,
    GiveStarterWeapon = giveStarterWeapon,
    EnableGas = enableGas,
    Gas = gasSettings,
    // The lobby's green countdown promises the first reveal, so it tracks the gas schedule.
    SafeZoneRevealMs = gasSettings.FirstRevealDelayMs,
    SendContainers = containers,
    SendMovementStats = movementStats,
    SendDoors = doors,
    DoorRotation = doorRotation,
    DoorCollision = doorCollision,
    DoorRestreamIntervalMs = doorRestreamMs,
    DoorPressWindowMs = doorPressMs,
    DoorSpawnFlags1 = doorSpawnFlags1,
    DoorPositionUpdateType = doorPositionUpdateType,
    SendLobbyDoors = lobbyDoors,
    SpawnHospitalDoors = hospitalDoors,
    SpawnHospitalDoubleDoors = hospitalDoubleDoors,
    SharedMatchDoors = sharedMatchDoors,
    DoorCapCountsNewOnly = doorCapNewOnly,
    DoorDespawnRadiusMetres = doorDespawnRadius,
    DoorRetailInteract = doorRetailInteract,
    DoorInteractRangeMetres = doorInteractRange,
    DoorRetailSound = doorRetailSound,
    // docs/85 §5 E5, experiment B. OFF by default and deliberately so: the spawn flag above is the
    // fix. This restates it on its own packet (0f 1e, a false/true pair) for the case where the
    // door's actor was not built when the 0xd6 applied. The owner ran this at 1087 and parked it in
    // his round 27 — his click-proven reference server sends zero of them and its doors are solid on
    // the flag alone, while his own copy was his prime suspect for three filmed door RENDERING
    // faults. CRANBERRY_DOOR_SET_COLLIDABLE=1 turns it on.
    DoorSetCollidable = config.Doors.SetCollidable,
    LootStream = lootStream,
    // D270/D271/D275: the floor's density and the laminated-armour rules, and D274's airdrop
    // channel. Both are retunable without a rebuild for the same reason the streamer is.
    LootDensity = config.Loot.Density,
    Airdrop = config.Loot.Airdrop,
    Movement = movementProfile,
    MatchSeed = matchSeed,
    SendVehicles = vehicles,
    VehiclePlan = config.Vehicles.Plan,
    VehicleStream = vehicleStream,
    VehicleFuel = vehicleFuel,
    VehicleRelay = vehicleRelay,
    VehicleIgnition = vehicleIgnition,
    VehicleDamage = vehicleDamage,
    VehicleBoost = vehicleBoost,
    // docs/117 §B: parked cars reach the client but do not draw until they carry an at-rest
    // transform, the collidable bit and their shader group. CRANBERRY_VEHICLE_POSITION_BLOCK,
    // CRANBERRY_VEHICLE_SPAWN_FLAGS1, CRANBERRY_VEHICLE_SHADER — all default on.
    VehiclePositionBlock = config.Vehicles.PositionBlock,
    VehicleSpawnFlags1 = config.Vehicles.SpawnFlags1,
    VehicleShader = config.Vehicles.Shader,
    Weapons = weaponStages,
    // docs/89 §1d (wave 9). CombatOptions.FromEnvironment() existed for a whole wave and nothing
    // ever called it, so every session ran CombatOptions.Default and CRANBERRY_PRACTICE_TARGET,
    // CRANBERRY_COMBAT, CRANBERRY_COMBAT_DAMAGE, CRANBERRY_HIT_MARKER and
    // CRANBERRY_WEAPON_FIRE_LAYOUT were inert strings. This one line is the fix.
    // D336: under the captured weapon table AMMO_PER_SHOT is 1 on every gun, so the client's own
    // executor (FUN_1422934f0 -> FUN_142290a90: aps <= rounds[slot]) refuses an empty magazine and
    // sends the fire STOP straight after the START (82 01 0x11 then 0xE0 - the high bits of the
    // STOP byte are uninitialised stack, not a flag; out\ghidra-aug\w20-*). It reloads on R.
    // The D223/D333 magazine resync - "the trigger going down means the client has something to
    // fire" - was only ever true while the generated table said AMMO_PER_SHOT 0; kept on, it fills
    // the SERVER's magazine from the bag while the client's stays at 0, and the two never agree.
    Combat = weaponStages.Effective.WeaponTable == WeaponTableSource.Captured
        ? config.Combat.Options with
        {
            MagazineResync = false, ShippedRefireGate = true, ShippedReloadTime = true,
            Z1LiveGunplay = weaponStages.Z1LiveGunplay, RefireJitterMs = 16,
        }
        : config.Combat.Options,
    // docs/103: the developer console. CRANBERRY_CONSOLE, _SURFACE, _LOCAL_OWNER, _TIERS,
    // _REGISTER, _SELF_FLAG, _ROWS, _WIDTH, _CONFIRMS and CRANBERRY_CLIENT_LOGS.
    Console = config.Console.Options,
    // docs/105: the server-owned menu camera table. CRANBERRY_MENU_VIEWS=all|reference|off.
    MenuViews = config.Menu.Views,
    // docs/97, lane 1D-lite: CRANBERRY_MATCH_ENDGAME / CRANBERRY_MATCH_TARGET_COUNTS /
    // CRANBERRY_MATCH_LEAVE_ON_END / CRANBERRY_COLLISION_DAMAGE.
    MatchEnd = config.Match.End,
    // docs/113 (D247-D250): the pre-game lobby at Fort Destiny. LobbyCountdownMs stays the length
    // the service runs so a test can still shorten it; Lobby owns when it arms, the minimum
    // population, the two HUD labels and the ce 14 banner steps.
    LobbyCountdownMs = config.Lobby.Options.CountdownMs,
    PublicQueue = config.PublicQueue,
    Lobby = config.Lobby.Options,
    // docs/113 (D251-D254): Backing your Match. CRANBERRY_BOUNTY=0 removes every byte of it.
    Bounty = config.Bounty.Options,
    // docs/109, lane 3C (two players in one match): CRANBERRY_PEER_SPAWN / CRANBERRY_PEER_RELAY /
    // CRANBERRY_SELF_TRANSIENT_ID / CRANBERRY_SHARED_GAS_SEED. All default ON, and every one of
    // them is a provable no-op with a single session on the host.
    Peers = config.Peers.Options,
    Inventory = inventoryOptions,
    Crafting = crafting,
    SendLootClusters = lootClusters,
    GroundLootShader = groundLootShader,
    // 0 (or less) means "no pacing": ZoneService clamps the slice to at least 1, so a slice larger
    // than the whole burst is the old one-continuation behaviour.
    BurstSliceSize = burstSliceSize <= 0 ? int.MaxValue : burstSliceSize,
    BurstSliceDelayMs = burstSliceDelayMs,
    Skins = skins,
    // D239 (docs/115 §3): whether the server may force a landing handover on a chute that is
    // still in the air. ON. CRANBERRY_DESCENT_LANDING_GUARD=0 is the revert.
    DescentLandingGuard = config.Descent.LandingGuard,
    // D241 (docs/115 §6): the client's own shader group for the selected parachute skin —
    // 484 Green / 492 Blue / 491 Tan — or 0, the default canopy, which is what ships.
    ParachuteShaderParameterGroupId = ParachuteSkin.ShaderParameterGroupFor(parachuteSkinItemId),
}.WithEnvironment(sky);   // D30: moves Weather, FixedUnixTime, FreezeClock and LightingFile together

// docs/56 §I2: applied after the record is built because it only ever RAISES the drop's minimum
// clearance. Unset, WithDescent returns the same DropOptions instance and nothing about the drop
// changes — which is what keeps regression guard 4 (the match-zoning opcode order) out of this.
zoneOptions = zoneOptions with { Drop = dropOptions.WithDescent(descent) };

var zone = new ZoneService(log, recorder, gatewayTickets, zoneOptions) { LocalOwnerAccountId = localAccounts.LocalAccountId };
zone.DiagnosticsEnabled = config.Metrics.Enabled;
var rankedOwners = localAccounts.CharacterOwnersSnapshot();
zone.SeedRankedIdentities(characters.Snapshot()
    .Where(character => rankedOwners.ContainsKey(character.EntityKey))
    .Select(character => (rankedOwners[character.EntityKey], character.EntityKey, character.Name)));
log.Info($"sky preset {sky.Describe()}");
foreach (string line in zone.PreloadWorldData())
{
    log.Info(line);
}

// docs/80 edit 7: the switches, then the grey census. Byte-free, and it answers before launch
// which ids the client will draw grey - the question docs/69 needed a whole session to ask.
foreach (string line in zone.PreloadSkinData())
{
    log.Info(line);
}

// docs/60 §5 step 0: the boot line the owner reads before run 1. Describe() prints the RAW stages,
// so a CRANBERRY_WIELD=1 that was refused for want of CRANBERRY_WEAPON_TAIL=1 says so here.
// Describe() already carries its own "weapon stages: " / "crafting: " prefix — wrapping it in a
// second one printed the label twice on the very line docs/60 §5 tells the owner to read.
log.Info(weaponStages.Describe());
if (weaponStages.Effective.WeaponTable == WeaponTableSource.Captured)
{
    log.Info(
        "combat: magazine resync OFF under the captured weapon table (D336) - AMMO_PER_SHOT is the "
        + "client's own now, an empty gun answers the trigger START with an immediate STOP and reloads on R "
        + "(82 07 -> 82 08); CRANBERRY_WEAPON_TABLE=generated restores the resync");
}
log.Info("active-hand safety: draw order="
    + (inventoryOptions.UseWieldSequence ? "Z1 8-packet sequence (docs/95)" : "one-packet dress")
    + "; automatic first-pickup draw="
    + (inventoryOptions.WieldFirstWeapon ? "ON" : "off")
    + "; 94/01 RHand rows="
    + (weaponStages.Effective.AllowWielding
        ? "permitted for items this session delivered a resolvable fire group for "
            + "(WeaponFireGroupLedger); refused for every other item"
        : "blocked unconditionally (docs/45)"));
// docs/89 §1d: the missing "combat:" line. Nothing called Describe(), which is why no boot banner
// in this project has ever said whether the practice dummy or the damage model was live.
log.Info(zoneOptions.Combat.Describe());
// docs/103 §4: which console arms are live this run, how many names go out, and how to revert.
log.Info(zoneOptions.Console.Describe(zone.ConsoleSummary));
// docs/105 §5: which menu viewpoints answer this run, and the one-word revert.
log.Info(zoneOptions.MenuViews.Describe());
// docs/105 §9: the menu actor — stance, interaction echo, spawn mark, and the three reverts.
log.Info(zoneOptions.MenuActor.Describe());
// docs/105 §10: the menu top bar and the MOTD, and their two reverts.
log.Info(zoneOptions.MenuTopBar.Describe());
// docs/102 §7 (lane 1F): where this run's magazines come from, and whether 11 03 goes out.
log.Info(zoneOptions.Combat.Ammo.Describe());
// docs/97 §3: which of the death-and-victory arms are live this run.
log.Info(zoneOptions.MatchEnd.Describe());
// docs/113 §2: how long the owner will stand in Fort Destiny, and where the banners fall.
log.Info(zoneOptions.Lobby.Describe());
// docs/113 §4: which bounty arms are live, and what the payout ladders hold.
log.Info(zoneOptions.Bounty.Describe());
// docs/109 §1: which of the two-player arms are live, and what the self record's +0xe0 carries.
log.Info(zoneOptions.Peers.Describe());
log.Info(crafting.Describe());
log.Info($"vehicles: map spawn chance {zoneOptions.VehiclePlan.SpawnChance:P0} per validated authored pad; "
    + "server population policy, applied when a match fleet is created");
log.Info($"vehicles: re-stream {(vehicles && vehicleStream.Enabled ? $"on ({vehicleStream.StreamRadiusMetres:F0} m disc, despawn at {vehicleStream.EffectiveDespawnRadiusMetres:F0} m, {vehicleStream.MaxLive} live, {vehicleStream.MaxPerRestream}/tick every {vehicleStream.RestreamIntervalMs} ms)" : "OFF (wave-5 landing burst only)")}, "
    + $"0x78 bystander relay {(vehicleRelay.Enabled ? "on" : "OFF")}, "
    + $"fuel gauge {(vehicleFuel.SendGauge ? "on" : "OFF")}, fuel burn {(vehicleFuel.Enabled ? "ON (an engine can cut out mid-drive)" : "off")}, "
    + $"ignition required {(vehicleIgnition.Required ? "ON (~65 % of cars unstartable until the hotwire loot rows exist)" : "off")}");
// docs/115: the damage and boost arms, on their own lines because a play-test that goes wrong must
// never have to guess which of them ran.
log.Info(vehicleDamage.Describe());
log.Info(vehicleBoost.Describe());

log.Info($"subsystems: containers {(containers ? "on" : "OFF")}, movement stats "
    + $"{(movementStats ? "on" : "OFF")}, doors {(doors ? $"on ({doorRotation}, {doorCollision}, re-stream {(doorRestreamMs > 0 ? doorRestreamMs + " ms" : "OFF")})" : "OFF")}, "
    + $"vehicles {(vehicles ? "on" : "OFF")}, loot clusters {(lootClusters ? "on" : "OFF")}, "
    + $"loot re-stream {(lootStream.Enabled ? $"on ({lootStream.StreamRadiusMetres:F0} m disc, despawn at {lootStream.EffectiveDespawnRadiusMetres:F0} m, {lootStream.MaxLive} live, {lootStream.MaxPerRestream}/tick every {lootStream.RestreamIntervalMs} ms)" : "OFF (wave-4 landing burst only)")}, "
    // D238 / AUDIT-parachute G10: this line used to print "from 850 m" while the effective release
    // was 1,454 m above ground — the one line the owner reads at boot, understating the altitude by
    // 604 m. It now prints what the planner actually computes,
    // max(SkySpawnAltitude, ground + MinimumClearanceMetres), and says which of the two wins.
    + "random drop "
    + $"{(zoneOptions.Drop.Enabled ? $"on (release = {DescribeRelease(zoneOptions.Drop)})" : "OFF")}, "
    + $"match seed {(matchSeed == 0 ? "per match" : MatchSeeds.Format(matchSeed))}");
static string DescribeRelease(DropOptions drop)
{
    // The planner's own formula, written out: air.Y = max(SkySpawnAltitude, groundY + clearance).
    // Over Z2 the ground under the 92 anchors runs -34.9 m to 241.9 m, so which term wins depends on
    // where the match drew — and the honest line says both.
    string slab = $"{drop.SkySpawnAltitude:F0} m absolute (the client's own KotK.SkySpawn)";
    return drop.MinimumClearanceMetres <= 0f
        ? slab
        : $"max({slab}, ground + {drop.MinimumClearanceMetres:F0} m)";
}

using var gatewayListener = new SoeListener(new IPEndPoint(IPAddress.Loopback, gatewayPort), zone, log,
    new SoeListenerOptions { MaxConnections = config.Transport.MaxGatewayConnections, EnableDiagnostics = config.Metrics.Enabled });
zone.Post = gatewayListener.Post;
log.Info($"zone bootstrap will use zone '{zoneName}' (type 4, HeightfieldLod), delay {bootstrapDelayMs} ms"
    + (autoMatchMs > 0 ? $"; dev auto-match into the match zone {autoMatchMs} ms after the lobby's ClientIsReady" : string.Empty)
    + (devGroundLootMs > 0 ? $"; dev ground loot {devGroundLootMs} ms after ClientIsReady / a parachute landing" : string.Empty)
    // docs/53 §Integration 5: FirstPhaseWindowMs and FirstPhaseDamage are FixedWindows fields and
    // the shipped SpeedPaced schedule reads neither, so printing them told the reader nothing about
    // the match they were about to play. DescribeSchedule prints what the schedule computes.
    + (enableGas
        ? $"; gas on — {GasTuning.DescribeSchedule(gasSettings)}"
        : "; gas off (CRANBERRY_GAS)"));
loginListener.Start();
gatewayListener.Start();

await using var launcherHost = new Cranberry.Launcher.Service.LauncherHost(root, localAccounts, zone, loginPort, gatewayPort,
    loginListener: loginListener, gatewayListener: gatewayListener, enableDiagnostics: config.Metrics.Enabled);
await launcherHost.StartAsync();
log.Info($"launcher HTTPS ready on {launcherHost.Options.BindAddress}:{launcherHost.Options.Port}; UDP restricted to loopback; certificate SHA256={launcherHost.CertificateSha256}");
await using var metrics = ProductionMetrics.TryStart(config, loginListener, gatewayListener, zone, log,
    launcherHost.CaptureDiagnostics, () => recorder is FilePacketRecorder capture ? capture.DroppedRecords : 0);
if (metrics is not null) log.Info($"production metrics enabled: {metrics.FilePrefix}.*.jsonl");

var stop = new ManualResetEventSlim();
// Local distribution only: the launcher owns this redirected input pipe. Closing it also
// stops the server after a launcher crash, through the normal score-flush/disposal path.
if (Environment.GetEnvironmentVariable("CRANBERRY_LOCAL_MANAGED") == "1")
    _ = Task.Run(async () =>
    {
        while (!stop.IsSet)
        {
            string? command = await Console.In.ReadLineAsync();
            if (command is null || command == "stop") { stop.Set(); return; }
        }
    });
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();

// Lane 0D: the whole effective configuration as ONE JSON line, after the banner it does not
// replace. Every capture in captures\ now has a machine-readable record of the configuration that
// produced it — the thing S1 §2.3 found missing, because "the env-var convention leaves no record
// of what a session ran with except the banner in the host log".
log.Info($"config file {config.FilePath} {(config.FileLoaded ? "loaded" : "absent (every default)")}");
if (config.FileNote is not null)
{
    log.Warn(config.FileNote);
}

log.Info("effective config: " + config.EffectiveJson());

log.Info($"login and gateway services up; advertising {gatewayAddress}; wire record at {recorder.FilePath}; Ctrl+C to stop");
stop.Wait();
if (metrics is not null) await metrics.DisposeAsync();
gatewayListener.Stop();
loginListener.Stop();
try { zone.FlushRankedScores(); }
catch (IOException error) { log.Warn(error.Message); }
log.Info("stopped");
