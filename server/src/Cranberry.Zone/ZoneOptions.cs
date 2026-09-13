using System.Numerics;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Zone;

/// <summary>Host-configurable choices for the zone bootstrap of a freshly admitted gateway link.</summary>
public sealed record ZoneOptions
{
    public Progression.ExperienceCurve ExperienceCurve { get; init; } = Progression.ExperienceCurve.Default;
    /// <summary>
    /// Zone delivered by <c>SendZoneDetails</c>. Only names that exist in the client's own pack
    /// files can be loaded: <c>LoginZone</c> (the 16x16-tile lobby world) and <c>Z2</c> (the
    /// 256x256-tile battle-royale map), see docs/07 §8.1.
    /// </summary>
    public string ZoneName { get; init; } = "LoginZone";

    /// <summary>
    /// Spawn position written into the self record (player+0x3b0); read by the actor creator.
    /// LoginZone's period main-menu subject mark on the helicopter tarmac, paired with the
    /// <c>kotkdefault</c> static shot. Spawning at the mark avoids a visible first-view correction.
    /// <para>
    /// docs/105 §9 (MENU-RETAIL-GAP U-5): this was 279.72 until 2026-09-03, 25 cm below the mark
    /// the 2026-08-22 admin capture pairs with <c>kotkdefault</c>. It is now
    /// <see cref="MenuActorOptions.ProvenMenuMark"/>; <c>CRANBERRY_MENU_SPAWN_MARK=legacy</c>
    /// restores the old value in one word.
    /// </para>
    /// </summary>
    public Vector4 SpawnPosition { get; init; } = MenuActorOptions.ProvenMenuMark;

    /// <summary>
    /// Model ids (rows of the client's own <c>Models.txt</c>, column ID) for the local player's
    /// actor definition (self record +0xb9dc, looked up by <c>FUN_14220c720</c>; an unknown id
    /// makes the client fall back to the placeholder definition "HUM" and fail with
    /// "Failed to load local player actor"). 9469 = SurvivorMale_Skin_01.adr (GENDER 1),
    /// 9474 = SurvivorFemale_Skin_01.adr (GENDER 2), both IS_VALID_FOR_PC.
    /// </summary>
    public uint MaleModelId { get; init; } = 9469;
    public uint FemaleModelId { get; init; } = 9474;

    public uint ModelIdFor(uint gender) => gender == 2 ? FemaleModelId : MaleModelId;

    /// <summary>
    /// Milliseconds between the gateway <c>LoginReply</c> and the zone bootstrap packets. The
    /// client waits up to 120 s in <c>Connecting</c>; a delay leaves time to attach a debugger
    /// after the client's startup phase (attaching earlier kills it). Zero sends immediately.
    /// </summary>
    public int BootstrapDelayMs { get; init; }

    /// <summary>
    /// Also send the packets consumed by the later run states right after the self record:
    /// <c>UpdateWeatherData</c> (WaitForFirstZone), <c>ZoneDoneSendingInitialData</c> and
    /// <c>DoneSendingPreloadCharacters</c> (WaitForConfirmationPacket).
    /// </summary>
    public bool SendLaterGates { get; init; } = true;

    /// <summary>
    /// The u64 of every <c>GameTimeSync</c> reply. The client gmtime's it into seconds-of-day
    /// (<c>FUN_142099a70</c>, first reply only) and derives the sun position from the hour, so the
    /// UTC time of day is what matters. D16: always 14:00 — 2017-08-15T14:00:00Z.
    /// </summary>
    public ulong FixedUnixTime { get; init; } = 1502805600;

    /// <summary>
    /// The clock stays frozen at <see cref="FixedUnixTime"/>: the reply's u32 is the cycle scalar
    /// (float bits, 0.0 = no advance) and its flag is the freeze byte (<c>FUN_140ff2250</c>).
    /// </summary>
    public bool FreezeClock { get; init; } = true;

    /// <summary>
    /// The sky sent in <c>SendZoneDetails</c> and <c>UpdateWeatherData</c>.
    /// <para>
    /// D30 supersedes D18's fixed grey: the host builds this (together with
    /// <see cref="FixedUnixTime"/>, <see cref="FreezeClock"/> and <see cref="LightingFile"/>) from
    /// an <c>EnvironmentPresets</c> entry through
    /// <c>ZoneOptionsEnvironmentExtensions.WithEnvironment</c>, so the four fields that must move
    /// together cannot drift apart (docs/38 §11.2). The default here stays the legacy struct so a
    /// test or an embedder that never calls <c>WithEnvironment</c> keeps the pre-D30 bytes; the
    /// shipped host always applies a preset, and <c>CRANBERRY_SKY=LegacyD18Grey</c> is the rollback.
    /// </para>
    /// </summary>
    public WeatherSettings Weather { get; init; } = WeatherSettings.Kotk2017;

    /// <summary>
    /// Lighting table activated by <c>SendZoneDetails</c>. The August client ships this single
    /// Z2 table and uses it for both the LoginZone menu scene and the battle-royale world.
    /// </summary>
    public string LightingFile { get; init; } = SendZoneDetails.KotkLightingFile;

    /// <summary>
    /// docs/82 §6 - drop any composite-effect id this client build has no definition for, rather
    /// than sending it and letting the client log a failed queue call. Default on; false is the
    /// bisect row. The list and the reasoning live in
    /// <see cref="Lighting.CompositeEffectGate"/>. No sender reads this yet: Cranberry has no
    /// <c>PlayCompositeEffect</c> (0xd3) sender, and the four <c>Id #0</c> failures the August
    /// client logged on 2026-08-30 come from an origin outside this lane's files.
    /// </summary>
    public bool GateCompositeEffectIds { get; init; } = true;

    /// <summary>
    /// docs/105 - how many of the client's named menu viewpoints (<c>E9 01 00 str name</c>) get a
    /// <c>StaticViewReply</c>. The names are the client's own (<c>MenuItem.txt</c> column
    /// <c>STATIC_VIEW</c>) but nothing in the client defines them, so the table is the server's.
    /// Default <see cref="MenuViewCoverage.All"/>; <c>CRANBERRY_MENU_VIEWS=reference</c> is the
    /// one-word revert to the two capture-proven shots.
    /// </summary>
    public MenuViewOptions MenuViews { get; init; } = MenuViewOptions.Default;

    /// <summary>
    /// docs/105 §9 - the menu ACTOR: <c>Character.WeaponStance</c> = 0 after every answered view
    /// change, the one-for-one <c>09 16 00</c> echo, and which mark the self record spawns at.
    /// All three arms default ON; <c>CRANBERRY_MENU_STANCE</c>,
    /// <c>CRANBERRY_MENU_INTERACTION_ECHO</c> and <c>CRANBERRY_MENU_SPAWN_MARK</c> revert them
    /// one at a time. <see cref="SpawnPosition"/> is set from
    /// <see cref="MenuActorOptions.SpawnPosition"/> by the host.
    /// </summary>
    public MenuActorOptions MenuActor { get; init; } = MenuActorOptions.Default;

    /// <summary>
    /// docs/105 §10 - the menu TOP BAR (<c>Experience.SetExperience</c> 0x87/01 and three
    /// <c>Currency.SetAccountCurrencyRecord</c> 0xab/03 rows) and the MOTD panel (0x32), both sent
    /// once at the menu <c>ClientIsReady</c>. Both default ON; <c>CRANBERRY_MENU_TOPBAR=0</c> and
    /// <c>CRANBERRY_MENU_MOTD=0</c> revert them one at a time.
    /// </summary>
    public MenuTopBarOptions MenuTopBar { get; init; } = MenuTopBarOptions.Default;

    /// <summary>Persistent account economy directory. Null preserves the isolated legacy test fixture.</summary>
    public string? EconomyStoreRoot { get; init; }

    /// <summary>The production host provisions a durable starter package for non-owner accounts.</summary>
    public bool ProvisionStarterAccounts { get; init; }
    /// <summary>Bundled local edition: grant the full skin catalogue and starter crates to every account, including its owner.</summary>
    public bool ProvisionLocalAccounts { get; init; }
    public MatchAdmissionRegistry MatchAdmissions { get; init; } = MatchAdmissionRegistry.Default;

    /// <summary>Production rolling admission policy. Null keeps the immediate staging API used by
    /// embedded protocol fixtures/development zoning; Cranberry.Host always supplies the public policy.</summary>
    public PublicQueueOptions? PublicQueue { get; init; }

    /// <summary>Account-bound regional host grants and private player invitations.</summary>
    public HostedGames.HostedGameStore? HostedGames { get; init; }

    /// <summary>
    /// Optional zlib-compressed Z1 <c>ReferenceData.DynamicAppearance</c> packet used as a
    /// compatibility source. Cranberry validates it, retains only rows used by the August
    /// catalogue, and repacks those rows in ClientProtocol_1148's named-reference wrapper. Null
    /// keeps the smaller project-authored starter palette.
    /// </summary>
    public string? DynamicAppearanceSourcePath { get; init; }

    /// <summary>The battle-royale map the match flow zones into (docs/11).</summary>
    public string MatchZoneName { get; init; } = "Z2";

    /// <summary>
    /// Pre-game lobby spawn in Z2 (the retail staging compound; measured ground ≈ 506.16 at
    /// (-233.83, -4892.03) in the owner's earlier tree, +0.2 m clearance). Lead; live check pending.
    /// </summary>
    public Vector4 StagingSpawn { get; init; } = new(-233.83f, 506.36f, -4892.03f, 1);

    /// <summary>
    /// Where the drop happens: the aeroplane's air spawn is this point plus
    /// <see cref="DropAltitude"/>, and it is the point the player parachutes down to.
    /// <para>
    /// It is deliberately NOT <see cref="StagingSpawn"/>. The pre-game compound sits at
    /// (−233.83, −4892.03), outside the ±4,096 m terrain the client's own <c>Z2.zone</c> spawn
    /// markers live on: measured over the shipped <c>z2-loot-spawns.bin</c>, the nearest of the
    /// 168,322 markers is <b>1,127.5 m</b> away, so a vertical drop from the compound gives every
    /// match an empty world while the log happily reports "0 markers". docs/33 §6c argued for "the
    /// landing point, never StagingSpawn" — but a fall straight down from the staging spawn makes
    /// them the same point, which is what made the whole feature a no-op.
    /// </para>
    /// <para>
    /// The default is the centroid of <c>Loot.PVResidential.01</c> in Pleasant Valley, at the
    /// median height of its own markers (32.5 m): 1,441 markers within 120 m, the nearest 5.2 m
    /// from the centre and the nearest 64 all within 19.9 m; 372 m from the world origin, so it is
    /// also inside the default gas play area. Every number here comes from the client's own zone
    /// data, not from a guess about the retail flight path.
    /// </para>
    /// </summary>
    public Vector4 MatchDropSpawn { get; init; } = new(348.0f, 32.5f, 130.8f, 1);

    /// <summary>
    /// docs/48: the random drop. <see cref="MatchDropSpawn"/> above is now the <em>fallback</em>,
    /// kept deliberately — it is what a burst centre reads before the player has a pose, and it is
    /// where a match lands if the placement file is missing or hand-edited. When
    /// <see cref="DropOptions.Enabled"/> is set (the default) every match draws one of the 92 named
    /// places the client's own <c>z2-loot-spawns.bin</c> carries, weighted by marker count and
    /// constrained to the first gas circle, and drops from the client's own
    /// <c>KotK.SkySpawn</c> altitude of 850 m rather than <see cref="DropAltitude"/>.
    /// </summary>
    public DropOptions Drop { get; init; } = DropOptions.Default;

    /// <summary>
    /// docs/48 §5.1: the match seed. <b>0 (the default) means "draw a fresh one per match"</b> from
    /// the session guid and the wall clock; the drawn value is logged as 16 hex digits so a match
    /// can be replayed by setting <c>CRANBERRY_MATCH_SEED</c> to it. Every per-match random stream
    /// (gas, drop, and one day loot) is a salted sub-seed of this one, so one number reproduces the
    /// whole match rather than one subsystem of it.
    /// </summary>
    public ulong MatchSeed { get; init; }

    /// <summary>Seconds shown by the "Joining match in N seconds" prompt (LoginBase.QueueExit).</summary>
    public uint QueueExitSeconds { get; init; } = 3;

    /// <summary>
    /// Pre-game countdown before the drop (GameModeHud countdown + StartMatch).
    /// <para>
    /// <b>D248: 120,000 ms, not 20,000.</b> The owner's own Z1 retail lobby row
    /// (<c>C:\Z1\Server\Zone\ZoneRetailSpawn.cs:588</c>, adopted under D53) is
    /// <c>LobbySeconds: 120</c>, and Fort Destiny is where the Bounty is backed (client locale
    /// <c>4162258810</c>) - 20 s was not long enough to open a Tab page and read a payout table.
    /// <c>CRANBERRY_LOBBY_MS</c> moves it; <see cref="Lobby"/> owns everything else about it.
    /// </para>
    /// </summary>
    public uint LobbyCountdownMs { get; init; } = 120000;

    /// <summary>
    /// docs/113 (D248-D251): when the lobby arms, the minimum population, the two HUD labels and
    /// the <c>ce 14</c> banner steps. <see cref="LobbyCountdownMs"/> stays the length the service
    /// runs, so a test that shortens the lobby still shortens it.
    /// </summary>
    public Match.LobbyOptions Lobby { get; init; } = Match.LobbyOptions.Default;

    /// <summary>
    /// docs/113 (D252-D255): the Bounty screen's own packets, the zone currency rows they need,
    /// the ante round-trip and the drop-open suppression. <c>CRANBERRY_BOUNTY=0</c> removes every
    /// byte of it from the wire.
    /// </summary>
    public Match.BountyOptions Bounty { get; init; } = Match.BountyOptions.Default;

    /// <summary>
    /// Post-drop countdown shown in the same green HUD widget. Retail reveals the first safe zone
    /// two minutes after the match starts; a positive next-phase label is required because the
    /// August widget ignores an empty label and otherwise leaves "Starting game ..." frozen.
    /// </summary>
    public uint SafeZoneRevealMs { get; init; } = 120000;

    /// <summary>
    /// Altitude of the air spawn above the drop point. The "retail: 1500 m" this used to claim has
    /// no source in the tree and is contradicted by the client's own data: `Z2Areas.xml` carries
    /// <c>KotK.SkySpawn</c> as a box with a Y band of 845-855, i.e. an <b>absolute</b> world Y of
    /// 850 (docs/48 §2). This field is therefore only used when <see cref="Drop"/> is disabled;
    /// a planned drop takes <see cref="DropOptions.SkySpawnAltitude"/> instead.
    /// </summary>
    public float DropAltitude { get; init; } = 1500f;

    /// <summary>
    /// Development aid: when positive, the zone service starts the match itself this many
    /// milliseconds after the client's first <c>ClientIsReady</c> in the lobby — the in-place
    /// zoning into <see cref="MatchZoneName"/> without the queue (no PLAY click, no
    /// <c>PlayerWorldTransferRequest</c>, no transfer reply). Zero (the default) leaves the match
    /// to the player.
    /// </summary>
    public int AutoMatchMs { get; init; }

    /// <summary>
    /// The parachute: row 13 of the client's own <c>Vehicles.txt</c> (VEHICLE_TYPE 8, CONTROL_TYPE 4,
    /// LANDING_HEIGHT 20, one seat via <c>VehicleSeatMappings</c> 13 → <c>SeatInfo</c> 12) and model
    /// 9374 = <c>Common_Parachute01.adr</c> in <c>Models.txt</c> (docs/12 §Definition).
    /// </summary>
    public uint ParachuteVehicleId { get; init; } = 13;
    public uint ParachuteModelId { get; init; } = 9374;

    /// <summary>The chute entity's guid is the rider's guid plus this offset (0x1001 → 0x2001).</summary>
    public ulong ParachuteGuidOffset { get; init; } = 0x1000;

    /// <summary>
    /// <b>D239's near-ground landing guard.</b> True (the default) is the rebuilt
    /// <c>DescentDeadline</c>: the ride clock is computed at the client's hands-off
    /// <c>MIN_TERM_VELOCITY 10</c> m/s, and past it the landing handover is still only forced when
    /// the chute is near the ground or its pose stream has gone silent — so <b>a live chute in the
    /// air is never dismounted</b>.
    /// <para>
    /// False restores the pre-D239 deadline exactly (twice the DIVED expected ride plus 15 s, no
    /// altitude gate), which on 2026-09-03 force-dismounted three live players about 600 m up.
    /// <c>CRANBERRY_DESCENT_LANDING_GUARD=0</c>.
    /// </para>
    /// </summary>
    public bool DescentLandingGuard { get; init; } = true;

    /// <summary>
    /// <b>D241 (AUDIT-parachute G6), and 0 by default.</b> The shader-parameter group the canopy is
    /// dressed in — one of the client's own <c>ClientItemDefinitions.PARAM1</c> values for the three
    /// parachute skins <c>VehicleSkinMods</c> rows 11/12/13 name: <b>484</b> Green (item 4055),
    /// <b>492</b> Blue (4056), <b>491</b> Tan (4057). Zero is "the default parachute", which is what
    /// a character with no parachute skin selected gets — and therefore what ships.
    /// <para>
    /// <b>It is 0 by default because the CARRIER is unknown, not because the skins are.</b> That
    /// these three skins exist for vehicle 13 and what group each carries is proven from the
    /// client's own sheets (<see cref="Descent.ParachuteSkin"/>). Where a vehicle skin goes on the
    /// wire is not: the <c>0xd7</c> self-schema names the tail's unassigned dwords at <c>+0x1c8</c>
    /// and <c>+0x1cc</c> without a meaning (docs/12 §6 has had them open since wave 1), their
    /// consumer <c>FUN_140e6e8b0</c> is not decompiled, and the client registers a separate
    /// unimplemented family for exactly this — <c>ZoneOpcodes.VehicleSkinBase = 0xf2</c>, with no
    /// sub-opcodes derived. Setting this non-zero writes the group into the <c>d7</c> tail's
    /// <c>+0x1c8</c> dword as the standing experiment (<c>CRANBERRY_PARACHUTE_SKIN=green|blue|tan</c>);
    /// unset, the drop's bytes are exactly what they have always been.
    /// </para>
    /// </summary>
    public uint ParachuteShaderParameterGroupId { get; init; }

    /// <summary>Transient id carried by the chute's lightweight and full records (client varint).</summary>
    public uint ParachuteTransientId { get; init; } = 2;

    /// <summary>
    /// Also send <c>LightweightToFullVehicle</c> (0xdb) after the lightweight record. docs/12 §2:
    /// probably optional for a chute, but the mount apply needs the vehicle's seat table, whose
    /// setter was not found, so the full record goes out until a live run proves it unnecessary.
    /// </summary>
    public bool SendFullVehicleRecord { get; init; } = true;

    /// <summary>
    /// Shape of the position-update block in the chute's lightweight tail: false = the empty
    /// 7-byte block (docs/12 minimal, 227 B), true = the 1087-captured shape with the packed
    /// position and a 1.57 m/s vertical / 0.2 horizontal speed (241 B at the air spawn).
    /// </summary>
    public bool RetailPositionBlock { get; init; }

    /// <summary>
    /// Ground-loot sample item (docs/13 §2): <c>ClientItemDefinitions.txt</c> row <b>2423</b>
    /// Field Bandage (Generic, NAME_ID 12302, ITEM_CLASS 16053, MAX_STACK 1), whose held mesh
    /// <c>Common_Props_Bandages_BandageRoll_3P.adr</c> resolves without the <c>_3P</c> suffix to
    /// <c>Models.txt</c> row <b>9066</b> — the ground actor written into the spawn packet.
    /// </summary>
    public uint GroundLootItemDefinitionId { get; init; } = 2423;
    public uint GroundLootModelId { get; init; } = 9066;
    public uint GroundLootNameId { get; init; } = 12302;

    /// <summary>
    /// The bag a granted item lands in (<see cref="InventoryItem.ContainerGuid"/> /
    /// <c>ContainerDefinitionId</c>). Cranberry's self record still ships an empty container list,
    /// and docs/13 does not prove which container the August client creates for itself, so these
    /// stay configurable: the apply core <c>FUN_140c35400</c> looks the guid up and repaints that
    /// bag itself. Lead — the first values to change if a live grant parses but never appears.
    /// <para>
    /// <b>Legacy fallback only.</b> Used exclusively when <see cref="SendContainers"/> is false: with
    /// the container model on (the default) a granted item's container guid and definition come from
    /// the character's own bag, and these two zeroes — a guid that names no container — are exactly
    /// the bug docs/41 §0 diagnosed as "the item is in the count but in no panel".
    /// </para>
    /// </summary>
    public ulong GroundLootContainerGuid { get; init; }
    public uint GroundLootContainerDefinitionId { get; init; }

    /// <summary>
    /// docs/41: give the character a real container model — the survivor loadout (0x86/03, 0x86/04),
    /// the bag it carries (<c>Container.InitContainers</c> 0xc8/02) and, on every pickup, the
    /// loadout binding (0x86/05) plus a container repaint (0xc8/06) — so a picked-up item lands in
    /// the slot the client's own <c>LoadoutSlotItemClasses</c> rule chooses instead of nowhere.
    /// <para>
    /// On by default: without it the owner cannot test a weapon at all (the round's brief). The
    /// rollback is one environment variable, <c>CRANBERRY_CONTAINERS=0</c>, which restores the
    /// wave-2 grant byte for byte. <c>InitContainers</c> is destructive on the client
    /// (<c>FUN_140d7ed90</c> clears the whole list), so every use is a complete snapshot: once at
    /// bootstrap, again when <c>InventoryWindow</c> opens, and after an equipped container provider
    /// enters or leaves the loadout. It is never sent on the LoginZone menu path, which is the
    /// critical path to PLAY and remains byte-identical to the known-good capture (docs/32).
    /// </para>
    /// </summary>
    public bool SendContainers { get; init; } = true;

    /// <summary>The bulk/wield rules of the container model (docs/41 §4, §5). D26's base 100.</summary>
    public InventoryOptions Inventory { get; init; } = new();

    /// <summary>
    /// docs/40: the per-character movement stat burst (<c>Character.UpdateStat 0f 40</c>, 18 entries)
    /// plus the base-speed <c>ClientUpdate 11 05</c>. The client ships no speed value of its own
    /// (docs/40 §4.1), so the server must choose all of them, and without the burst every mode moves
    /// at the client's built-in default — which was the owner's original complaint.
    /// <para>
    /// <b>Since wave 8 the values are the owner's own Z1 numbers</b> — jog 4.10 m/s, sprint 5.74
    /// (docs/76). <b>This property is the switch for that</b>: it is a whole profile, so a match can
    /// be played on any preset without a rebuild. <c>CRANBERRY_MOVE_PRESET=Wave5Legacy</c> is the
    /// one-word revert to the set he played through waves 4-7 (jog 5.50, sprint 6.60), and the
    /// per-stat <c>CRANBERRY_MOVE_*</c> knobs tune on top — see <c>MovementTuning</c>.
    /// </para>
    /// </summary>
    public MovementProfile Movement { get; init; } = MovementProfile.Default;

    /// <summary>Send the movement stat burst at all. Off restores the client's built-in speeds.</summary>
    public bool SendMovementStats { get; init; } = true;

    /// <summary>
    /// docs/42: stream the map's own 4,103 door proxies around the player and answer the interact
    /// press with <c>Character.DoorStateUpdate (0f 0a)</c>. A door spawn is a loot spawn with the
    /// one <c>+0x19c</c> door-id field changed, so it rides the proven <c>0xd6 → 0xda → ea 04</c>
    /// order.
    /// </summary>
    public bool SendDoors { get; init; } = true;

    /// <summary>Metres around the player in which doors are registered and spawned (docs/42 §10.3).</summary>
    public float DoorRadius { get; init; } = 500f;

    /// <summary>
    /// Cap on one door burst. Since docs/114 §4 it counts the doors the burst actually SPAWNS,
    /// not the doors the disc holds — see <see cref="DoorCapCountsNewOnly"/>.
    /// </summary>
    public int DoorMaxPerBurst { get; init; } = 48;

    /// <summary>
    /// docs/114 §4 (AUDIT-doors gap 4): the burst cap counts NEW doors, so a door already on the
    /// client does not consume a slot.
    /// <para>
    /// <c>MatchDoors.RegisterNear</c> used to take the nearest <see cref="DoorMaxPerBurst"/> of the
    /// whole disc and then skip the live ones, so in a town with more than 48 doors inside
    /// <see cref="DoorRadius"/> the 49th onward were never spawned until the player had moved 30 m.
    /// <c>CRANBERRY_DOOR_CAP_NEW_ONLY=0</c> restores the wave-4 arithmetic byte for byte.
    /// </para>
    /// </summary>
    public bool DoorCapCountsNewOnly { get; init; } = true;

    /// <summary>
    /// docs/114 §4: metres past which a live door is taken back off the client with
    /// <c>Character.RemovePlayer (0f 01)</c>, mirroring
    /// <see cref="Loot.LootStreamOptions.DespawnRadiusMetres"/>.
    /// <para>
    /// Before this, doors were registered and never unregistered: the live set grew monotonically
    /// for the whole match, so a player who walked Z2 accumulated hundreds of door entities and it
    /// was the only door defect that got worse the longer the match ran. 650 m keeps a margin
    /// outside the 500 m spawn radius and is floored at <see cref="DoorRadius"/> so a door on the boundary
    /// cannot be spawned and destroyed on alternate bursts. <c>CRANBERRY_DOOR_DESPAWN_RADIUS=0</c>
    /// restores "never despawn".
    /// </para>
    /// </summary>
    public float DoorDespawnRadiusMetres { get; init; } = 650f;

    /// <summary>
    /// docs/114 §1 (AUDIT-doors gap 1): arm a door burst in the PRE-MATCH LOBBY, around
    /// <see cref="StagingSpawn"/>, the way the world does after a parachute landing.
    /// <para>
    /// The lobby compound has five doors and Cranberry shipped none of them: <c>gen-doors.py</c>
    /// discarded them as an off-map set piece (fixed in the dataset, docs/114 §1) and no burst was
    /// ever armed there — <c>ClientIsReady</c> in the menu arms ground loot and nothing else. They
    /// are the ONLY doors the friend's live server is on record spawning: the owner's 2026-08-22
    /// admin capture carries five <c>AddLightweightNpc</c> records that match Z2 proxies
    /// 1796604819 / 1435455604 / 1796604901 / 1796604902 / 1796604910 to 0.070 m and to the last
    /// decimal of the yaw. Two of them are 48 m and 57 m from where Cranberry's own player stands.
    /// </para>
    /// <para>
    /// Cranberry's are the more correct doors of the two: his five carry no <c>Doors.txt</c> row id,
    /// which at 1148 means no door controller is installed and the leaf can never swing (docs/42
    /// §2), so his lobby doors are non-interactive scenery. <c>CRANBERRY_DOOR_LOBBY=0</c> restores
    /// an empty lobby.
    /// </para>
    /// </summary>
    public bool SendLobbyDoors { get; init; } = true;

    /// <summary>
    /// How long after the lobby's <c>ClientIsReady</c> the lobby door burst is sent. The same
    /// deferral shape the landing burst uses and for the same reason: the menu burst is the
    /// critical path to PLAY and docs/32 is the cautionary tale about putting new packets inside
    /// it, so this lands well after it has drained.
    /// </summary>
    public int LobbyDoorDelayMs { get; init; } = 2000;

    /// <summary>
    /// docs/114 §3 (AUDIT-doors gap 3): the doors of a match are one shared object rather than one
    /// per connection, so a door one player opens is open for the other.
    /// <para>
    /// Two things become match state: the guid and transient id a door is known by — today every
    /// session mints its own from <c>0x4400…0001</c>, so the SAME guid names DIFFERENT doors 3 km
    /// apart in two live sessions (<c>captures/wire-20260903-174857.txt:21006</c>) and no
    /// <c>0f 0a</c> could ever be addressed to both — and the open bit itself, which is fanned out
    /// to every session that has the door spawned. It is provably a no-op with one player: the
    /// shared allocator starts at the same base and hands out the same ids in the same order.
    /// <c>CRANBERRY_DOOR_SHARED=0</c> restores per-connection doors.
    /// </para>
    /// </summary>
    public bool SharedMatchDoors { get; init; } = true;

    /// <summary>
    /// docs/114 §12 (D264): resolve every door family to one of the two swing sounds the retail
    /// client's own <c>getDoorSound</c> keeps resident, so <b>every</b> door makes a noise on open
    /// and close.
    /// <para>
    /// The dataset's per-family <c>Doors.txt</c> row is Cranberry's own choice (C1); eight of the
    /// eleven families get an "authored" row (Office 13, CommercialGlass 7, Camper 4, BathroomStall
    /// and Hospital 13, HospitalDouble* 8) whose composite effect is real but whose FMOD door bank
    /// the retail client never keeps loaded — because the reference server never triggers it — so
    /// those doors <b>swing in silence</b>. The reference plays only two door sounds
    /// (model 9903 → row 12 <c>SFX_Door_Metal_Industrial</c>, everything else → row 2
    /// <c>SFX_Door_Wood</c>), and the owner's own Z1 derives them identically
    /// (<c>ZoneWorldObjects.DoorDefinitionFor</c>). Adopted under D53. Any row &gt; 0 makes an
    /// equally working door (docs/42 §6c), so only the sound moves.
    /// <c>CRANBERRY_DOOR_RETAIL_SOUND=0</c> restores the dataset's authored per-family rows.
    /// </para>
    /// </summary>
    public bool DoorRetailSound { get; init; } = true;

    /// <summary>
    /// docs/114 §2 (AUDIT-doors gap 2): spawn the 39 <c>Hospital_*_Placer</c> doors.
    /// <para>
    /// The three hospital placers are <c>&lt;Invisible value="1"/&gt;</c> markers carrying the
    /// identical <c>Models.txt</c> marker text as the eight <c>Common_DPO_DoorProxy_*</c> families
    /// ("DO NOT DELETE!! Used to place objects in the world", rows 944/946/948), so they are door
    /// proxies by the client's own definition; Cranberry's generator simply did not list them and
    /// every hospital in Z2 had open, non-interactable doorways. The owner's own Z1 spawns all
    /// three (D53), and Models.txt confirms the mapping independently: each <c>_Placer</c> row sits
    /// immediately after the mesh row it places (9887/9888, 9889/9890, 9891/9892).
    /// <c>CRANBERRY_DOOR_HOSPITAL=0</c> leaves them in the dataset and out of the world.
    /// </para>
    /// </summary>
    public bool SpawnHospitalDoors { get; init; } = true;

    /// <summary>
    /// docs/114 §2, the double-leaf caveat (AUDIT-doors gap 10): the 14
    /// <c>Hospital_DoorDouble01</c> / <c>Hospital_DoorDoubleMetal01</c> doors, whose ONE actor holds
    /// TWO leaves.
    /// <para>
    /// The client's swing rotates the whole actor π/2 about a single pivot computed from the model's
    /// own bounds (<c>FUN_14149dd10</c> / <c>FUN_14149d4a0</c>), so a two-leaf actor may look wrong
    /// opened. That is <b>[U] until one look</b> — hence its own switch, so the 25 single-leaf
    /// hospital doors do not have to wait for it. <c>CRANBERRY_DOOR_HOSPITAL_DOUBLE=0</c> ships the
    /// 25 and holds the 14.
    /// </para>
    /// </summary>
    public bool SpawnHospitalDoubleDoors { get; init; } = true;

    /// <summary>
    /// How often the door pump checks movement or an unfinished spawn backlog.
    /// <c>MatchDoors.ShouldRestream</c> uses half the radius, capped at 30 metres. Once the backlog
    /// is empty a stationary player costs one distance test and no packets.
    /// 0 disables the pump and restores the wave-3 behaviour, where
    /// doors were streamed once at the touchdown and never again.
    /// </summary>
    public int DoorRestreamIntervalMs { get; init; } = 500;

    /// <summary>
    /// docs/79 §4 E1: how long after an accepted door toggle further requests for the same door are
    /// absorbed. <b>800</b> = the 1148 client's own swing — 2 rad/s over π/2 = 785 ms (docs/42 §6b)
    /// — rounded up, so the guard cannot expire while the leaf is still moving.
    /// <para>
    /// The wave-4 value was 250 ms, justified only against the 2 ms gap between the two packets of
    /// one press. It ignored the client re-firing the whole press while <c>[F]</c> is HELD: the
    /// August client re-evaluates its interaction target at a median 171 ms
    /// (<c>logs/host-20260830-163007.log</c>) and the owner's Z1 server measured the same ≈165 ms
    /// re-fire on the 1087 client, with a 640 ms worst case and no deliberate second press closer
    /// than 1,010 ms. At 250 ms a held key opens and re-closes a door inside its own animation.
    /// </para>
    /// <para><c>0</c> answers every request, which is the A/B for "the guard ate my press".</para>
    /// </summary>
    public int DoorPressWindowMs { get; init; } = 800;

    /// <summary>
    /// docs/85 §5 E2: the <c>+0x1b1</c> flag byte a door's <c>0xd6</c> carries. Bit <b>4</b>
    /// (<c>0x10</c>, <see cref="LightweightEntityBody.CollidableFlag"/>) is the collision switch —
    /// <c>FUN_140c51c90</c> at <c>0x140c51ddd</c> shifts this byte right by 4 and hands bit 0 to
    /// <c>FUN_140c75ef0</c>, the same function the <c>0f 1e Character.SetCollidable</c> packet
    /// reaches. Bit <b>5</b> (<c>0x20</c>, <see cref="LightweightEntityBody.PhysicsBodyFlag"/>) is
    /// what wave 8 shipped; it reached the client on 7 of 7 doors and the owner walked through them,
    /// so it is falsified as the lever and retained only because it is already on the wire.
    /// <para>
    /// A <b>byte</b>, not a bool, so every candidate is an env var rather than a rebuild.
    /// <c>CRANBERRY_DOOR_SPAWN_FLAGS1</c>: <c>0x30</c> is the default (bit 4 + the inert bit 5),
    /// <c>0x10</c> is the owner's Z1 byte exactly, <c>0x20</c> reproduces wave 8's known
    /// walk-through, and <c>0</c> is an exact wave-7 rollback.
    /// </para>
    /// <para>
    /// <b>Doors only.</b> A ground item must keep writing <c>0</c> here — an item that stops a player
    /// is an obstacle you cannot step over, which is also the owner's own rule on his server.
    /// </para>
    /// </summary>
    public byte DoorSpawnFlags1 { get; init; } = LightweightEntityBody.DoorSpawnFlagsRetail;

    /// <summary>
    /// docs/114 §5 (AUDIT-doors gaps 5 and 7): adopt the friend server's two door-interaction
    /// numbers under D53 — <c>ClientInteractComponent</c> range <b>2.0 m</b> and
    /// <c>flags1</c> <b>0x10</b>.
    /// <para>
    /// <b>The range.</b> His <c>InteractReplicationData</c> carries <c>00 00 00 40</c> = 2.0f in the
    /// same field of the same component, on the same <c>9d1cd550</c> class hash Cranberry writes
    /// <c>00 00 40 40</c> = 3.0f into (<c>C:\Project\out\ingest-admin-20260822-part1\ops\
    /// cPacketIdReplicationBase.txt:13</c> against <c>captures/wire-20260903-174857.txt:6692</c>).
    /// Cranberry's 3.0 was its own choice and nothing else. Caveat, stated rather than hidden: his
    /// 2.0 is on a DROPPED ITEM, because his door records never received an interact component at
    /// all — so this is his server's interaction range, not provably his DOOR range.
    /// </para>
    /// <para>
    /// <b>The flag byte.</b> His door records set <c>0x10</c> in the flag island and Z1's own
    /// <c>ZoneNpcs.Collidable = 1 &lt;&lt; 4</c> is the same bit. Cranberry sent <c>0x30</c>: bit 4
    /// plus bit 5, and docs/85 §2c FALSIFIED bit 5 as a collision lever (it rebuilds an existing
    /// physics body and cannot create one), so it was never doing anything. Dropping it is one byte
    /// closer to his record and costs nothing.
    /// </para>
    /// <para>
    /// <c>CRANBERRY_DOOR_RETAIL_INTERACT=0</c> reverts BOTH — 3.0 m and <c>0x30</c>. The two
    /// individual knobs (<c>CRANBERRY_DOOR_INTERACT_RANGE</c>,
    /// <c>CRANBERRY_DOOR_SPAWN_FLAGS1</c>) still move one at a time.
    /// </para>
    /// </summary>
    public bool DoorRetailInteract { get; init; } = true;

    /// <summary>
    /// The <c>InteractReplicationData.interactionRange</c> a DOOR's <c>ea 04</c> carries, in the
    /// client's own range units. See <see cref="DoorRetailInteract"/> for where 2.0 comes from.
    /// <para>
    /// Doors only. Ground loot keeps <see cref="InteractReplicationData.DefaultRange"/>: the loot
    /// reach is its own tuned number (<c>CRANBERRY_LOOT_PICKUP_REACH</c>) and this lane does not
    /// move it. Any value ≤ 0 makes the client fall back to its own global default.
    /// </para>
    /// </summary>
    public float DoorInteractRangeMetres { get; init; } = DoorInteractRange.Retail;

    /// <summary>
    /// docs/85 §5 E5: also state the collision bit on its own packet —
    /// <c>0f 1e Character.SetCollidable</c>, emitted as a <c>false</c> then <c>true</c> pair after
    /// the door's <c>0xda</c> promotion (<see cref="World.Doors.SetCollidablePacket"/>).
    /// <para>
    /// <b>Default OFF, and that is deliberate rather than an oversight.</b> The spawn flag above is
    /// the fix and it ships on. This is the backup lever for one specific failure —
    /// <c>FUN_140c75ef0</c> skips its actor half when the actor is not built yet, so a door whose
    /// model is still loading at <c>0xd6</c> apply time gets the entity bit and no body. But the
    /// owner already ran this experiment at 1087 and turned it off in his round 27: his click-proven
    /// reference server sends <b>zero</b> <c>SetCollidable</c> in 527 s and its doors are solid on
    /// the flag alone, while his own server's copy — which arrives in the same datagram train as the
    /// spawn, at a half-built actor — was his prime suspect for three <em>filmed</em> door rendering
    /// faults (leaf not drawn until you stand on it, pop-in through the camera, the swing teleporting
    /// between angles). Turning it on by default would risk trading walk-through doors for ugly ones
    /// and would confound the play-test. <c>CRANBERRY_DOOR_SET_COLLIDABLE=1</c> runs it as
    /// experiment B.
    /// </para>
    /// </summary>
    public bool DoorSetCollidable { get; init; }

    /// <summary>
    /// docs/85 §5 E6: the <c>+0x11c</c> <c>positionUpdateType</c> a door's <c>0xd6</c> carries.
    /// <b>0, and this lane re-derived from the 1148 binary that 0 is the only value that can work</b>
    /// — <c>FUN_141fdc290</c>'s own bytes at <c>0x141fdc290</c> are
    /// <c>test dl,dl / jnz …</c> then, on the zero path, the release call followed by
    /// <c>and [rbx+0x4e1],0xfb</c> / <c>or [rbx+0x4e1],dil&lt;&lt;2</c>: a non-zero
    /// <c>positionUpdateType</c> <em>releases</em> the collision instance and clears
    /// <c>actor+0x4e1</c> bit 2, the static bit that guards every acquire in the image.
    /// <para>
    /// It is an option only because the owner's Z1 server and the 1087 reference server both send
    /// <c>1</c> on doors (47 of 47 in his capture) and his doors work. At 1148 the binary says that
    /// value would make things worse, so <c>CRANBERRY_DOOR_POSITION_UPDATE_TYPE=1</c> is a
    /// <b>negative control</b> — if it makes doors solid, this reading of <c>FUN_141fdc290</c> is
    /// wrong and docs/55 §2 needs redoing. Do not move the default on the strength of the 1087
    /// number alone.
    /// </para>
    /// </summary>
    public byte DoorPositionUpdateType { get; init; } = World.Doors.DoorCollision.CollidablePositionUpdateType;

    /// <summary>
    /// The <c>0xd6</c> rotation convention (docs/42 §10.4) — the one thing in that lane that is
    /// untested, because every entity Cranberry has spawned so far used the identity, which reads
    /// the same under an Euler and a quaternion interpretation. A wrong choice shows as doors facing
    /// the wrong way, never as an error; <c>CRANBERRY_DOOR_ROTATION</c> sweeps the four candidates.
    /// <para>
    /// docs/47 §3 settled it: client object <c>+0xa0</c> is a <b>quaternion</b>, and the two Euler
    /// packings ask for a rotation about the world X axis plus a scale of <c>yaw^2 + 1</c> — 84.5 %
    /// of Z2's doors spawned tipped onto their face and several times too large. The default is
    /// therefore <see cref="DoorRotation.QuaternionYUp"/>. It is belt and braces: since docs/47
    /// <c>DoorRotationPacking.PackForWire</c> substitutes <c>QuaternionYUp</c> for either proven-
    /// impossible packing whatever this option says, so the option can no longer put a broken pose
    /// on the wire — it can only lie about what was sent, which this default stops it doing.
    /// The remaining live sweep is the yaw <em>sign</em>: <c>CRANBERRY_DOOR_ROTATION=QuaternionYUpNegated</c>.
    /// </para>
    /// </summary>
    public DoorRotation DoorRotation { get; init; } = DoorRotation.QuaternionYUp;

    /// <summary>
    /// docs/55 §I1: which mesh a door spawn names. <see cref="DoorCollisionMode.KinematicMesh"/> —
    /// the default and this wave's fix — names the family's <c>createAsKinematic="1"</c> collision
    /// twin, which is the only lever the 1148 protocol gives a server over whether a door blocks
    /// (there is no collision field, flag or component anywhere in the protocol; collision comes
    /// from the actor definition the model id names, and from nothing else).
    /// <para>
    /// <see cref="DoorCollisionMode.VisibleMesh"/> restores wave 4 byte for byte and is the A/B:
    /// if doors still walk through with the twin, docs/55 §4 C2 is the whole story and the model was
    /// never the cause. <c>CRANBERRY_DOOR_COLLISION</c> flips it without a rebuild, because a
    /// two-way decision that needs a rebuild costs a whole play-test.
    /// </para>
    /// </summary>
    public DoorCollisionMode DoorCollision { get; init; } = DoorCollisionMode.VisibleMesh;

    /// <summary>
    /// docs/39: the loot density dial and room caps. The whole map's floor is decided once per match
    /// by <c>Z2LootLayout</c> — gate, then a symmetric room-cap pass — so a landing sees a house with
    /// three things in it rather than a carpet of 64 items in a 20 m circle.
    /// </summary>
    public LootDensityOptions LootDensity { get; init; } = LootDensityOptions.Default;

    /// <summary>
    /// Also send the two ammunition boxes that belong to each spawned firearm (docs/39 §5). Without
    /// them every firearm on the map is inert, because §3 removed ammunition from the <c>Gear01</c>
    /// and <c>Weapons01</c> tables on purpose.
    /// <para>
    /// A gun the burst has already taken is <b>never</b> stranded without its pair — the boxes are
    /// emitted unconditionally for it — but they are counted against
    /// <see cref="GroundLootMaxPerBurst"/>, so the burst stops taking <i>new</i> markers once the
    /// total reaches the cap. Exempting them outright is what let one landing plan 384 world objects
    /// against a cap that read as 128.
    /// </para>
    /// </summary>
    public bool SendLootClusters { get; init; } = true;

    /// <summary>
    /// D328: a gun lying on the ground is spawned with its base appearance shader group in the
    /// <c>AddLightweightNpc</c> body at <c>+0x1a4</c> - the field the vehicle spawn already uses to
    /// tint the OffRoader (docs/117 §B) - looked up as <c>ShaderGroupForAnyBody(itemId)</c> in the
    /// appearance table. The AK-47 and pump meshes are tintable luminance masks (docs/106 A4), so a
    /// 0 there composites <c>white.dds</c>: the white floor guns of 2026-09-04 20:3x.
    /// <c>CRANBERRY_GROUND_LOOT_SHADER=0</c> writes 0 again.
    /// </summary>
    public bool GroundLootShader { get; init; } = true;

    /// <summary>
    /// docs/43: park a fleet of drivable vehicles on the map's own 3,519 derived parking anchors and
    /// stream the nearest of them in with the landing burst.
    /// <para>
    /// The spawn itself reuses two live-proven writers — <c>d7 AddLightweightVehicle</c> and
    /// <c>db LightweightToFullVehicle</c>, the parachute's own pair — with <c>OwnerGuid = 0</c>, so a
    /// parked car is a shape the client has already accepted. Everything past "the car is visible" is
    /// unproven: docs/43 blocker 1 is that nobody knows yet whether an E-press produces
    /// <c>70 01 MountRequest</c> or <c>09 07 InteractRequest</c>, and both arms are wired precisely so
    /// that one press settles it.
    /// </para>
    /// </summary>
    public bool SendVehicles { get; init; } = true;

    /// <summary>Initial vehicle interest, including a margin beyond the native draw range.</summary>
    public float VehicleRadius { get; init; } = VehicleStreamOptions.Default.StreamRadiusMetres;

    /// <summary>Horizontal vehicle interest under the canopy; defaults to the same range as walking.</summary>
    public float VehicleAirborneRadius { get; init; } = VehicleStreamOptions.Default.StreamRadiusMetres;

    /// <summary>Vehicle-specific native draw-distance override, covering the release altitude.</summary>
    public float VehicleRenderDistance { get; init; } = 1500f;

    /// <summary>
    /// Fill the local vehicle set during the initial paced burst. DrainBurst still slices packets;
    /// limiting the initial selection to twelve made visible cars wait for later pump ticks.
    /// </summary>
    public int VehicleMaxPerBurst { get; init; } = VehicleStreamOptions.Default.MaxLive;

    /// <summary>
    /// <b>docs/117 §B step 1 — DEFAULT ON.</b> Give a parked car a populated at-rest position-update
    /// block (<see cref="PositionUpdateBlock.AtRest"/>, flags <c>0x00da</c>) in its <c>0xd7</c> tail
    /// instead of the empty 7-byte block. A parked car is the only non-static entity Cranberry sends
    /// with no transform through the streamed path: the client builds the actor ("Mountable npc
    /// ready") but never draws it. A real server and the owner's Z1 both state the position twice —
    /// once in the body, once packed in the tail — and this is the second statement.
    /// <c>CRANBERRY_VEHICLE_POSITION_BLOCK=0</c> restores the empty block.
    /// </summary>
    public bool VehiclePositionBlock { get; init; } = true;

    /// <summary>
    /// <b>docs/117 §B step 2 — DEFAULT <c>0x10</c>.</b> The <c>+0x1b1</c> flag byte a parked car
    /// ships with: <see cref="LightweightEntityBody.CollidableFlag"/>, the same collision bit the
    /// doors carry (docs/85 §2a; Z1's <c>ZoneNpcs.Collidable = 1 &lt;&lt; 4</c> on every car,
    /// parachute excluded, D53). Without it a car is walk-through even once it draws.
    /// <c>CRANBERRY_VEHICLE_SPAWN_FLAGS1=0</c> restores the all-zero flag byte.
    /// </summary>
    public byte VehicleSpawnFlags1 { get; init; } = LightweightEntityBody.CollidableFlag;

    /// <summary>
    /// <b>docs/117 §B step 3 (cosmetic) — DEFAULT ON.</b> Write the client's own shader-parameter
    /// group into the parked car's body <c>+0x1a4</c> per model (<see cref="VehicleShaderGroups"/>):
    /// 838 for the OffRoader, 0 for the others. A car sent with 0 renders white/rust.
    /// <c>CRANBERRY_VEHICLE_SHADER=0</c> restores the untinted body.
    /// </summary>
    public bool VehicleShader { get; init; } = true;

    /// <summary>The map's pad occupancy; defaults to every validated authored vehicle location.</summary>
    public VehicleSpawnPlanOptions VehiclePlan { get; init; } = new();

    /// <summary>Health, fuel, cooldown and pose-sanity rules for a live vehicle (docs/43 §I.3).</summary>
    public VehicleFleetOptions VehicleFleet { get; init; } = new();

    /// <summary>
    /// docs/61 §2: keep the car park around the player instead of around the landing point. Wave 5
    /// spawned the map's 300 cars exactly once, at the landing, and never again — an OffRoader
    /// leaves that 250 m disc in about ten seconds. The third arm of the shared world pump.
    /// <para><see cref="VehicleStreamOptions.Enabled"/> false restores wave 5 exactly.</para>
    /// </summary>
    public VehicleStreamOptions VehicleStream { get; init; } = new();

    /// <summary>
    /// docs/61 §3: burn rate, gauge policy and the refuel amount. <b>Burning is OFF by default</b>
    /// (an engine that cuts out mid-drive has never been play-tested); the gauge is on, because it
    /// is a resource row the client already parses.
    /// </summary>
    public VehicleFuelOptions VehicleFuel { get; init; } = new();

    /// <summary>docs/61 §1: the <c>0x78</c> bystander relay's range, rate and fan-out.</summary>
    public VehicleRelayOptions VehicleRelay { get; init; } = new();

    /// <summary>
    /// docs/61 §4: hotwire / key. <see cref="VehicleIgnitionOptions.Required"/> is OFF until items
    /// 3458/3459/3460 are in the loot tables — with it on, ~65 % of cars would be unstartable.
    /// </summary>
    public VehicleIgnitionOptions VehicleIgnition { get; init; } = new();

    /// <summary>
    /// docs/115 §2: vehicle damage — a crash reported by <c>8e 01</c>, a bullet that resolves onto
    /// a vehicle guid, and the flip pulse. <c>CRANBERRY_VEHICLE_DAMAGE=0</c> restores the
    /// pre-lane behaviour, where <c>VehicleFleet.ApplyDamage</c> had no callers at all.
    /// </summary>
    public VehicleDamageOptions VehicleDamage { get; init; } = new();

    /// <summary>
    /// docs/115 §3: the boost. <c>CRANBERRY_VEHICLE_BOOST=0</c> silences the whole path — and note
    /// that a half-implemented boost is worse than none, because without the server's echo the
    /// client's own effect manager refuses every press after the first.
    /// </summary>
    public VehicleBoostOptions VehicleBoost { get; init; } = new();

    /// <summary>
    /// Development aid (mirrors <see cref="AutoMatchMs"/>): when positive, drop
    /// <see cref="DevGroundLootCount"/> sample items near the player this many milliseconds after
    /// the lobby's <c>ClientIsReady</c> and again after a parachute landing, so the ground-loot
    /// pickup flow can be exercised without a world loot table. Zero (the default) spawns nothing.
    /// </summary>
    public int DevGroundLootMs { get; init; }

    /// <summary>How many sample items the development spawn drops.</summary>
    public int DevGroundLootCount { get; init; } = 3;

    /// <summary>Metres between the player and each development sample item.</summary>
    public float DevGroundLootRadius { get; init; } = 2.5f;

    /// <summary>
    /// Spawn the map's own ground loot (docs/33): every Z2 spawn marker within this many metres of
    /// the player's <em>landing point</em> is rolled against the Cranberry loot tables built from
    /// the client's own <c>ClientItemDefinitions.txt</c>. Zero disables it and leaves only
    /// <see cref="DevGroundLootMs"/>'s sample drop.
    /// <para>
    /// The landing point, never <see cref="StagingSpawn"/>: the lobby compound sits at Z ≈ −4,892,
    /// outside the ±4,096 m play area, and has <b>zero</b> spawn markers within 1,000 m (docs/33
    /// §6c — the nearest is 1,127.5 m away in <c>Loot.WestPeaksRanch.01</c>). That is why the drop
    /// itself has its own <see cref="MatchDropSpawn"/>: falling straight down from the staging spawn
    /// lands the player on exactly the empty spot this note warns about.
    /// </para>
    /// <para>
    /// <b>WAVE 8: 80 m → 64 m, which is his <c>InitialRingUnits</c>.</b> The owner's server sends a
    /// 64 m ring <i>synchronously</i> at arrival, exempt from its own sweep budget, and his comment
    /// says why: <i>"so the player never lands in a spot that looks empty"</i>. 64 m is still a
    /// strict superset of <see cref="LootStreamOptions.StreamRadiusMetres"/> = 60, which is the
    /// invariant that makes the streamer purely additive — the first re-stream tick after a
    /// touchdown cannot spawn anything the landing has not already sent.
    /// </para>
    /// <para>
    /// <b>80 m was docs/39's number.</b> With the density gate in place the drop point held 14 items
    /// within 20 m, 25 within 40 m, 97 within 60 m and 341 within 120 m, so the old 120 m / 64
    /// pairing was a blob-then-nothing: the cap truncated a quarter-empty neighbourhood. That
    /// reasoning still holds; 64 m simply spends the same burst on the disc the streamer will
    /// actually maintain instead of a 20 m rim it will evict.
    /// </para>
    /// </summary>
    public float GroundLootRadius { get; init; } = 64f;

    /// <summary>
    /// Cap on how many real ground items one burst may send, so a landing inside a dense POI cannot
    /// emit thousands of <c>0xd6 + 0xda + 0xea</c> packets at once. The densest 100 m disc in Z2
    /// holds 4,006 markers (docs/33 §4.2). The cap keeps the <b>nearest</b> markers
    /// (<see cref="Loot.Z2LootSpawns.QueryNearest(in System.Numerics.Vector3, float, System.Span{int})"/>),
    /// so what it drops is always the far end of the disc.
    /// <para>
    /// Raised to 128 with the density gate (docs/39 §I.3). Clustered ammunition boxes <b>do</b> count
    /// against it: a gun already taken always gets its own pair, but the loop stops taking new
    /// markers once items + boxes reach the cap, so this really is the bound on how many world
    /// objects one landing puts on the wire. It was not, and at ~25 % firearms 128 meant up to 384.
    /// </para>
    /// <para>
    /// <b>WAVE 8: 128 → 192.</b> His <c>InitialRingMaxEntities</c> is 200 at a different protocol,
    /// and at the owner's density Cranberry's own 64 m ring holds <b>204 median / 298 p90 / 328
    /// max</b> objects in its accounting (a gun costs 3, a plain item 1). docs/78 §7 E10 asks for
    /// 256 to cover the median landing whole and names <b>192 as the conservative step</b> — and 192
    /// is what ships, because 128 is the only landing burst this build has proven live
    /// (<c>logs/host-20260830-090725.log</c>), 256 would be 2× that in one synchronous continuation,
    /// and the re-stream arm now backfills the remainder within 500 ms rather than 3 s. Raise it to
    /// 256 once 192 has been seen to land.
    /// </para>
    /// </summary>
    public int GroundLootMaxPerBurst { get; init; } = 192;

    /// <summary>
    /// Milliseconds after the parachute landing before the ground-loot burst goes out. It used to
    /// borrow <see cref="DevGroundLootMs"/>, so turning the development drop off silently moved the
    /// real burst to 0 ms — into the same tick as the landing packets.
    /// </summary>
    public int GroundLootDelayMs { get; init; } = 2000;

    /// <summary>
    /// How many world objects the landing burst may put on the wire in one go before yielding the
    /// listener thread for <see cref="BurstSliceDelayMs"/>.
    /// <para>
    /// The one landing the owner has confirmed live sent 64 items — 192 tunnel messages, ~35 KB, in
    /// 28 ms (<c>logs/host-20260829-201025.log</c> 20:13:59.878 → .903). This wave's defaults plan up
    /// to <see cref="GroundLootMaxPerBurst"/> loot objects plus <see cref="DoorMaxPerBurst"/> doors
    /// plus <see cref="VehicleMaxPerBurst"/> vehicles, which is several times that in a single
    /// synchronous continuation. <c>OutboundChannel</c> has no send window and no backpressure, so
    /// there is nothing between that loop and the peer's socket buffer; slicing is what keeps the
    /// instantaneous rate inside the shape the client is known to accept. 16 objects is ~48 tunnel
    /// messages, comfortably under the proven burst.
    /// </para>
    /// </summary>
    public int BurstSliceSize { get; init; } = 16;

    /// <summary>
    /// Milliseconds between burst slices. The whole burst already sits
    /// <see cref="GroundLootDelayMs"/> behind the landing, so spending a few hundred more
    /// milliseconds spreading it out costs the player nothing.
    /// </summary>
    public int BurstSliceDelayMs { get; init; } = 40;

    /// <summary>
    /// Match seed for the loot layout: same seed, same map (docs/33 §4.3). The gas seed is derived
    /// per match from the character guid and the wall clock, so this reproduces the loot layout
    /// only — it is not (yet) one number for a whole match.
    /// </summary>
    public ulong LootSeed { get; init; } = 1;

    /// <summary>
    /// docs/52: the ground-loot re-stream. Wave 4 spawned the map's loot exactly once, at the
    /// parachute touchdown, and never again — the owner's play-test found every building away from
    /// the landing point empty, which is the whole of brief item 1. This is the doors' re-stream
    /// (docs/47 §I3) applied to the 39,708 items on the Z2 floor: a 60 m working set capped at 128
    /// live objects, re-planned every 3 s once the player has moved half a radius, evicting what is
    /// past 90 m with a 13-byte <c>0f 01</c> before spawning what is newly near.
    /// <para>
    /// <see cref="LootStreamOptions.Enabled"/> false restores wave 4 exactly: the landing burst is
    /// untouched by this option in either direction, and the 60 m stream disc is a strict subset of
    /// the 80 m landing disc.
    /// </para>
    /// </summary>
    public LootStreamOptions LootStream { get; init; } = LootStreamOptions.Default;

    /// <summary>
    /// <b>D274 — retail's second loot channel.</b> When a crate is announced, where it lands, what
    /// is in it and how long it takes to unlock. <c>CRANBERRY_AIRDROPS=0</c> restores the pre-D274
    /// world exactly: no banner, no crate, and the .308 Hunting Rifle unreachable again
    /// (<c>AUDIT-loot.md</c> G4/F10).
    /// </summary>
    public AirdropOptions Airdrop { get; init; } = AirdropOptions.Default;

    /// <summary>
    /// docs/60: the staged weapon rollout (D33). Stage 1 — <c>ReferenceData "WeaponDefinitions"</c>
    /// — is the only one on by default, and it is the only one that cannot crash the client. The
    /// two that can (the real 68-byte Weapon <c>ItemAdd</c> tail, and narrowing regression guard 5
    /// so a cleared item guid may reach body slot 7) are opt-in, behind
    /// <c>CRANBERRY_WEAPON_TAIL=1</c> and <c>CRANBERRY_WIELD=1</c>.
    /// </summary>
    public WeaponStageOptions Weapons { get; init; } = new();

    /// <summary>
    /// docs/62: crafting. Stage 1 (<c>0x26 09 Recipe.List</c> after the in-match
    /// <c>ClientIsReady</c>) is on; the same records inside the self record's <c>0x11a</c> field are
    /// off, because a length error there fails the login outright (D35).
    /// </summary>
    public CraftingOptions Crafting { get; init; } = new();

    /// <summary>
    /// Put <see cref="AugustHeldWeapon"/> in the player's RHand (equipment slot 7) at match start —
    /// docs/36 §W, the combat-capture enabler. In 96 captured sessions the client has never emitted
    /// a single <c>0x82 WeaponBase</c> packet, because slot 7 has always carried
    /// <c>Weapon_Empty.adr</c> ("Fists", <c>WIELD_TYPE</c> 0); the c2s fire layout cannot be derived
    /// any other way (docs/16 §5, docs/20). One rifle plus one trigger press is the derivation.
    /// <para>
    /// <b>Match only, never the LoginZone menu.</b> docs/32 is the cautionary tale — both zoning
    /// regressions of 2026-08-29 were equipment-shaped, and the menu actor is on the critical path
    /// to PLAY. <c>LoadoutSlotItemClasses.txt</c> additionally restricts survivor loadout 17 slot 7 to
    /// classes 25006/25044, so a rifle does not belong there anyway.
    /// </para>
    /// <b>Off by default, opt in with <c>CRANBERRY_STARTER_WEAPON=1</c>.</b> The grant puts two
    /// packets on the wire that Cranberry has never sent — an <c>ItemAdd</c> and the first
    /// <c>SetCharacterEquipmentWithSlots</c> carrying a non-empty equipment-slot list, whose
    /// <em>effect</em> is UNVERIFIED (<see cref="AugustHeldWeapon"/>, <see cref="EquipmentSlotRow"/>)
    /// — and docs/32 is the cautionary tale about equipment-shaped additions to a proven flow. One
    /// new variable at a time: turn it on for the dedicated combat-derivation capture, not for the
    /// loot or zoning runs it would otherwise confound.
    /// <para>
    /// When it is on, the grant lands in the parachute-landing burst, never inside the
    /// <c>UpdateLocation(WaitForTeleport)</c> hold at StartMatch: a refused packet there would cost
    /// <c>SynchronizedTeleport.ClientReady</c>, and with it the drop, with nothing in the log to say
    /// the weapon caused it. A rifle is no use in the air anyway.
    /// </para>
    /// </summary>
    public bool GiveStarterWeapon { get; init; }

    /// <summary>
    /// The item definition granted by <see cref="GiveStarterWeapon"/>. Default
    /// <see cref="AugustHeldWeapon.ItemDefinitionId"/> = 2425 "AR-15"; 2239 is the byte-identical
    /// alternate row kept for a live A/B (docs/36 §W2).
    /// </summary>
    public uint StarterWeaponItemDefinitionId { get; init; } = AugustHeldWeapon.ItemDefinitionId;

    /// <summary>
    /// Also bind <c>Replication.CreateComponent 0xea/04 ClientInteractComponent</c> to each spawned
    /// item (docs/13 §3b). The envelope is proven; docs/19 §4b then derived the rep-data element,
    /// so the component now goes out with one <c>InteractReplicationData</c> entry rather than the
    /// empty list this option originally sent. docs/13 §3d leaves to a live run whether the [F]
    /// prompt needs this at all.
    /// </summary>
    public bool SendInteractComponent { get; init; } = true;

    /// <summary>
    /// Also publish the ground loot as <c>ProximateItems 0xf8/01</c> — the nearby-pickable list
    /// behind the proximity panel and QuickLoot (docs/13 §3c). The element layout is proven.
    /// </summary>
    public bool SendProximateItems { get; init; } = true;

    /// <summary>
    /// D23's gas / safe-zone schedule: phase count, timers, radii, damage curve and the two
    /// unverified-layout toggles. The client has no gas data of its own (docs/15 §1a), so every
    /// value here is Cranberry's own and free to tune; see <c>docs/23-gas-integration-note.md</c>
    /// for how the zone service drives it. <see cref="GasSettings.FirstRevealDelayMs"/> should be
    /// kept equal to <see cref="SafeZoneRevealMs"/>, which promises the same countdown on the HUD.
    /// </summary>
    public GasSettings Gas { get; init; } = new();

    /// <summary>
    /// docs/80 (D53): the wave-8 skin and world-dress switches - stowed-weapon meshes, per-group
    /// worn shader colour, re-tint-not-re-model, identical-dress suppression, the boot census and
    /// the durable wardrobe store. Every default is the behaviour the owner asked for and every one
    /// is a single environment variable away from its predecessor.
    /// </summary>
    public SkinOptions Skins { get; init; } = new();

    /// <summary>
    /// Run the gas at all. On by default: the schedule starts at the <c>ce 16</c> StartMatch send
    /// and is the only thing that currently ends a match. Setting it false leaves every
    /// <see cref="Gas"/> value in place but never opens a <c>GasController</c>, which is how a
    /// bring-up run isolates a client problem from the gas traffic.
    /// </summary>
    public bool EnableGas { get; init; } = true;

    /// <summary>
    /// docs/81: the fire / hit / damage loop. D52's hole - "no <c>0x82 WeaponBase</c> packet is ever
    /// answered" - is closed behind these switches, all defaulted to the behaviour the owner asked
    /// for. <see cref="CombatOptions.Enabled"/> false restores the pre-D52 arm exactly: one
    /// <c>WEAPONFIRE</c> line with untruncated hex, and no reply.
    /// </summary>
    public CombatOptions Combat { get; init; } = CombatOptions.Default;

    /// <summary>
    /// docs/103: the developer console and its menu. <c>CRANBERRY_CONSOLE=0</c> reverts to the
    /// behaviour before the wave exactly - a typed <c>09 42</c> is logged by the default arm and
    /// left unanswered, and not one <c>Command.AddWorldCommand</c> goes out.
    /// </summary>
    public DevConsole.ConsoleOptions Console { get; init; } = DevConsole.ConsoleOptions.Default;

    /// <summary>
    /// docs/97, lane 1D-lite: the death-and-victory orchestration. Every packet the lane can put on
    /// the wire that this server did not send before is behind one of these switches;
    /// <c>CRANBERRY_MATCH_ENDGAME=0</c> restores the pre-lane behaviour exactly (one <c>ce 04</c>
    /// and one <c>ce 09</c> for a gas death, no ragdoll, no kill feed, no hold and no reset).
    /// </summary>
    public MatchEndOptions MatchEnd { get; init; } = MatchEndOptions.Default;

    /// <summary>
    /// docs/109, lane 3C: two players in one match. <c>CRANBERRY_PEER_SPAWN=0</c> reverts the
    /// enter/leave burst, <c>CRANBERRY_PEER_RELAY=0</c> the <c>0x78</c> pose relay,
    /// <c>CRANBERRY_SELF_TRANSIENT_ID=0</c> the docs/100 §7 self-id patch and
    /// <c>CRANBERRY_SHARED_GAS_SEED=0</c> the shared gas plan. With one player on the host every
    /// arm is a provable no-op.
    /// </summary>
    public PeerOptions Peers { get; init; } = PeerOptions.Default;
}
