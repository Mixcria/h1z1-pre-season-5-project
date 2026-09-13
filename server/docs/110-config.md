# 110 — `cranberry.json`: every setting, its legacy switch, its default and its ruling

Lane 0D of the overhaul plan (§3 Phase 0, S1 §2.4). One file replaces the environment-switch
sprawl that had grown to 122 names in `Program.cs` and the option records, **without taking a
single one-word revert away from the owner**.

## How a value is decided

1. **The option record's own default** — unchanged, and still the thing every test pins.
2. **`cranberry.json`** — `<root>\cranberry.json`, or the first `.json` positional argument,
   or `CRANBERRY_CONFIG`. **An absent file is not an error**: it means every default.
3. **The environment** — the exact legacy name in the tables below (highest), then the
   generated overlay name `CRANBERRY_<BLOCK>_<KEY>`.

The host's **positional arguments still win over both** for root, ports, seed character, zone,
bootstrap delay, auto-match, appearance source and dev loot, because `run-host.ps1` passes seven
of them on every launch. A key this build has never heard of is ignored with a note; an
environment name this table has never heard of still reaches the process environment untouched,
so a lane that adds a switch does not have to wait for a row here.

With no file and no environment every bound record is its own shipped default instance, which is
what keeps the golden transcript (`MatchZoningMatchesTheKnownGoodCaptureOpcodeOrder`,
`MatchZoningPacketLengthsMatchTheKnownGoodCapture`, `ZoneBootstrapTests`) byte-identical.

## The boot line

The effective configuration is written to the host log as **one JSON line** after the existing
banner, so every capture in `captures\` has a machine-readable record of the configuration that
produced it (S1 §2.3: "the env-var convention leaves no record of what a session ran with"):

```
[info] effective config: {"root":{...},"ports":{...},"gas":{...}, …}
```

## Every key

### `root` — where the host reads data and writes logs; positional arguments 1-9 still win over both file and environment

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `path` | `CRANBERRY_ROOT` (or `CRANBERRY_ROOT_PATH`) | `C:\Aug2017` | D150 | data, logs, captures and state live under here |
| `zone` | `CRANBERRY_ZONE` (or `CRANBERRY_ROOT_ZONE`) | `LoginZone` | D24 | the zone name the bootstrap sends the client into first |
| `seedCharacter` | `CRANBERRY_SEED_CHARACTER` (or `CRANBERRY_ROOT_SEED_CHARACTER`) | `` | - | development seed name, used only when the roster is empty |
| `dynamicAppearanceSource` | `CRANBERRY_DYNAMIC_APPEARANCE_SOURCE` (or `CRANBERRY_ROOT_DYNAMIC_APPEARANCE_SOURCE`) | `C:\Z1\Server\Data\dynamicAppearanceFriend.bin` | D22 | the checksummed DynamicAppearance blob (D22's declared compatibility input) |

### `ports` — the two UDP listeners: login, then the gateway the login reply advertises

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `login` | `CRANBERRY_PORT_LOGIN` (or `CRANBERRY_PORTS_LOGIN`) | `20042` | - | the login UDP listener |
| `gateway` | `CRANBERRY_PORT_GATEWAY` (or `CRANBERRY_PORTS_GATEWAY`) | `20043` | - | the gateway UDP listener, advertised in the login reply |

### `metrics` — opt-in bounded production JSONL; listener work and queue timing, not a simulation tick or client-display measurement

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `enabled` | `CRANBERRY_METRICS` (or `CRANBERRY_METRICS_ENABLED`) | `false` | - | enable bounded local production metrics; default off |
| `intervalMs` | `CRANBERRY_METRICS_INTERVAL_MS` | `5000` | - | snapshot interval, clamped to 1000..60000 ms |
| `maxSessions` | `CRANBERRY_METRICS_MAX_SESSIONS` | `16` | - | maximum anonymous session detail rows per source, 0..64 |
| `maxFileMiB` | `CRANBERRY_METRICS_MAX_FILE_MIB` (or `CRANBERRY_METRICS_MAX_FILE_MI_B`) | `16` | - | maximum size per capture file, 1..64 MiB |
| `maxFiles` | `CRANBERRY_METRICS_MAX_FILES` | `4` | - | maximum retained files per run, 1..16; stop at limit unless rolling is enabled |
| `rolling` | `CRANBERRY_METRICS_ROLLING` | `false` | - | continue capture by deleting only this run's oldest file at the retention limit; default off |
| `nodeId` | `CRANBERRY_METRICS_NODE_ID` | `local` | - | non-secret node label, up to 64 ASCII letters/digits/dot/dash/underscore |

### `queue` — public rolling rosters and measured node admission limits; hosted games retain their explicit lifecycle

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `waitMs` | `CRANBERRY_QUEUE_WAIT_MS` | `180000` | - | pregame countdown after the ready-player minimum; 0..900000 ms |
| `loadTimeoutMs` | `CRANBERRY_QUEUE_LOAD_TIMEOUT_MS` | `180000` | - | release failed loading reservations after 30000..600000 ms; ready players retain their match |
| `maxPlayers` | `CRANBERRY_QUEUE_MAX_PLAYERS` | `150` | - | whole-party roster capacity, 6..150 |
| `minPlayers` | `CRANBERRY_QUEUE_MIN_PLAYERS` | `2` | - | ready pregame minimum, also requires more than one full team |
| `maxMatches` | `CRANBERRY_QUEUE_MAX_MATCHES` | `2` | - | node-wide allocated public match limit; raise only after measured acceptance |
| `acceptTimeoutMs` | `CRANBERRY_QUEUE_ACCEPT_TIMEOUT_MS` | `60000` | - | frozen reservation accept timeout, 10000..180000 ms |

### `sky` — D30: the sky, the frozen clock and the lighting table move together as one named preset

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `preset` | `CRANBERRY_SKY` (or `CRANBERRY_SKY_PRESET`) | `Aug2017KotkClear` | D30 | sky, frozen clock and lighting table as one named preset |

### `gas` — D23: every number here is Cranberry's own design, so it is retuned by restart and never by rebuild

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `enabled` | `CRANBERRY_GAS` (or `CRANBERRY_GAS_ENABLED`) | `true` | D25 | the whole safe-zone runtime; off is the bring-up rollback |
| `preset` | `CRANBERRY_GAS_PRESET` | `Aug2017Retail` | D23 | the named ladder; Sprint plays the same schedule at one fifth of the clock |
| `pacing` | `CRANBERRY_GAS_PACING` | `PhaseTable` | D43 | PhaseTable (per-phase hold and movement durations), SpeedPaced (fixed radius rate), or FixedWindows (legacy timed windows) |
| `preMoveRing` | `CRANBERRY_GAS_PRE_MOVE_RING` | `None` | D65 | what is drawn before phase 1 first moves; None is D65 |
| `scale` | `CRANBERRY_GAS_SCALE` | `1` | D23 | multiplies every time in the ladder, leaving the radii and the damage alone |
| `damageScale` | `CRANBERRY_GAS_DAMAGE_SCALE` | `1` | D23 | multiplies every entry of the per-phase damage table |
| `phases` | `CRANBERRY_GAS_PHASES` | `10` | D43 | how many waves the ladder runs; changing the table's count selects SpeedPaced with geometric radii unless PhaseTable was explicitly requested |
| `firstRevealMs` | `CRANBERRY_GAS_FIRST_REVEAL_MS` | `120000` | D43 | when the first circle is drawn; table pacing adjusts its first hold to preserve the first movement time |
| `firstMoveMs` | `CRANBERRY_GAS_FIRST_MOVE_MS` | `370000` | D43 | when the wall first moves; table pacing adjusts its first hold |
| `holdMs` | `CRANBERRY_GAS_HOLD_MS` | `16000` | D43 | the pause before later waves; changing this scalar replaces later hold-table entries, while repeating its default preserves the table |
| `wallSpeed` | `CRANBERRY_GAS_WALL_SPEED` | `3.866` | D63 | radius decrease in m/s; selects SpeedPaced unless PhaseTable was explicitly requested, and remains limited by maxEdge |
| `finalRadiusM` | `CRANBERRY_GAS_FINAL_RADIUS_M` | `40` | D63 | the last circle's radius |
| `initialRadiusM` | `CRANBERRY_GAS_INITIAL_RADIUS_M` | `8000` | D63 | the play area's radius at t=0 |
| `centreX` | `CRANBERRY_GAS_CENTRE_X` | `-250` | D63 | the play area's centre, x |
| `centreZ` | `CRANBERRY_GAS_CENTRE_Z` | `100` | D63 | the play area's centre, z |
| `drift` | `CRANBERRY_GAS_DRIFT` | `0.96` | D122 | how far a new circle's centre may walk (D62 caps it against maxEdge) |
| `driftCone` | `CRANBERRY_GAS_DRIFT_CONE` | `120` | D122 | the heading cone the walk is drawn in |
| `maxEdge` | `CRANBERRY_GAS_MAX_EDGE` | `40` | D276 | the cap on the drawn ring's leading edge, including centre movement |
| `radiusRoundM` | `CRANBERRY_GAS_RADIUS_ROUND_M` | `5` | D63 | rounds every radius to a whole number of metres |
| `radiusLadder` | `CRANBERRY_GAS_RADIUS_LADDER` | `1` | D285 | 0 selects geometric radii and SpeedPaced unless PhaseTable was explicitly requested; 1 retains the preset mode; inherited tables rescale with radius bounds |
| `tickMs` | `CRANBERRY_GAS_TICK_MS` | `1000` | D23 | the damage tick |
| `updateMs` | `CRANBERRY_GAS_UPDATE_MS` | `500` | D123 | how often a moving circle is re-stated |
| `blendMs` | `CRANBERRY_GAS_BLEND_MS` | `1000` | D23 | the client-side ring blend |
| `blendMode` | `CRANBERRY_GAS_BLEND_MODE` | `SendPeriod` | D280 | SendPeriod writes ce 01's blend as the interval to the next ce 01; Fixed is the flat blendMs |
| `centrePlan` | `CRANBERRY_GAS_CENTRE_PLAN` | `PoiDestination` | D277 | PoiDestination aims the match at one of the client's nine GasWeightArea volumes; Drift is D62's cone walk |
| `poiExponent` | `CRANBERRY_GAS_POI_EXPONENT` | `0.5` | D277 | how hard the nine volumes are weighted by their own footprint; 0 makes them equally likely |
| `playAreaLeadM` | `CRANBERRY_GAS_PLAY_AREA_LEAD_M` | `1200` | D278 | how far the play area may lead toward a destination containment cannot otherwise reach; 0 pins it |
| `toxicity` | `CRANBERRY_GAS_TOXICITY` | `1` | D279 | the client's own resource-611 toxicity meter, filled in the gas and drained outside it |
| `toxicityDrain` | `CRANBERRY_GAS_TOXICITY_DRAIN` | `1000` | D279 | how fast the toxicity meter drains outside the gas, units per second |
| `hudHealMs` | `CRANBERRY_GAS_HUD_HEAL_MS` | `1000` | D120 | the 1 Hz ce 0f countdown heal |
| `safezoneHealMs` | `CRANBERRY_GAS_SAFEZONE_HEAL_MS` | `15000` | D123 | how often ce 02 is re-sent while a circle is revealed |
| `banners` | `CRANBERRY_GAS_BANNERS` | `1` | D64 | the three HUD banner labels |

### `movement` — D54: the owner's own six speeds and eight blend times, retunable without a rebuild

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `preset` | `CRANBERRY_MOVE_PRESET` (or `CRANBERRY_MOVEMENT_PRESET`) | `Aug2017Default` | D54 | the named speed profile |
| `base` | `CRANBERRY_MOVE_BASE` (or `CRANBERRY_MOVEMENT_BASE`) | `4.1` | D54 | base movement speed, m/s |
| `sprint` | `CRANBERRY_MOVE_SPRINT` (or `CRANBERRY_MOVEMENT_SPRINT`) | `1.4` | D54 | sprint modifier |
| `walk` | `CRANBERRY_MOVE_WALK` (or `CRANBERRY_MOVEMENT_WALK`) | `0.3` | D54 | walk modifier |
| `crouch` | `CRANBERRY_MOVE_CROUCH` (or `CRANBERRY_MOVEMENT_CROUCH`) | `0.7` | D54 | crouch modifier |
| `back` | `CRANBERRY_MOVE_BACK` (or `CRANBERRY_MOVEMENT_BACK`) | `0.75` | D59 | backpedal modifier |
| `strafe` | `CRANBERRY_MOVE_STRAFE` (or `CRANBERRY_MOVEMENT_STRAFE`) | `0.75` | D54 | strafe modifier |
| `swim` | `CRANBERRY_MOVE_SWIM` (or `CRANBERRY_MOVEMENT_SWIM`) | `0.55` | D56 | swim modifier (Z1's 0 is refused) |
| `water` | `CRANBERRY_MOVE_WATER` (or `CRANBERRY_MOVEMENT_WATER`) | `0.8` | D54 | wading modifier |
| `sprintAccel` | `CRANBERRY_MOVE_SPRINT_ACCEL` (or `CRANBERRY_MOVEMENT_SPRINT_ACCEL`) | `0.35` | D55 | sprint acceleration time |
| `sprintDecel` | `CRANBERRY_MOVE_SPRINT_DECEL` (or `CRANBERRY_MOVEMENT_SPRINT_DECEL`) | `0` | D55 | sprint deceleration time |
| `fwdAccel` | `CRANBERRY_MOVE_FWD_ACCEL` (or `CRANBERRY_MOVEMENT_FWD_ACCEL`) | `0.35` | D55 | forward acceleration time |
| `backAccel` | `CRANBERRY_MOVE_BACK_ACCEL` (or `CRANBERRY_MOVEMENT_BACK_ACCEL`) | `0.35` | D55 | back acceleration time |
| `strafeAccel` | `CRANBERRY_MOVE_STRAFE_ACCEL` (or `CRANBERRY_MOVEMENT_STRAFE_ACCEL`) | `0.35` | D55 | strafe acceleration time |

### `descent` — D238/D239: the parachute ride - the release is the client's own 850 m KotK.SkySpawn slab, and the landing guard never dismounts a live chute in the air

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `preset` | `CRANBERRY_DESCENT_PRESET` | `ClientSkySpawn850` | D125 | the named descent profile |
| `seconds` | `CRANBERRY_DESCENT_SECONDS` | `0` | D125 | seconds under canopy; the sky-spawn altitude follows from it |
| `rate` | `CRANBERRY_DESCENT_RATE` | `40.4` | D239 | the PLANNING mean, m/s - the mean of dived rides, not a client constant (the real rate is a 10-56 m/s player-controlled band) |
| `landingGuard` | `CRANBERRY_DESCENT_LANDING_GUARD` | `true` | D239 | never force-dismount a live chute in the air; 0 restores the pre-D239 deadline that did |

### `drop` — D240/D241: where the match drops and what the canopy is dressed in

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `jitterFloor` | `CRANBERRY_DROP_JITTER_FLOOR` | `0.5` | D240 | a jittered marker must keep this fraction of its place's own anchor density; 0 is the pre-D240 absolute-only floor |
| `parachuteSkin` | `CRANBERRY_PARACHUTE_SKIN` (or `CRANBERRY_DROP_PARACHUTE_SKIN`) | `0` | D241 | green / blue / tan (items 4055/4056/4057) dresses the canopy; unset is the default parachute, and the carrier field is still unproven |

### `loot` — D42/D69 with D270-D275: the ground-loot working set that follows the player, the proximity panel, the floor's density, the laminated-armour rules and the airdrop channel

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `groundRadiusM` | `CRANBERRY_GROUND_LOOT_RADIUS_M` (or `CRANBERRY_LOOT_GROUND_RADIUS_M`) | `64` | D39 | metres around the landing in which the map's own spawn markers are rolled |
| `stream` | `CRANBERRY_LOOT_STREAM` | `true` | D42 | the working set that follows the player; off is wave 4's single landing burst |
| `streamMs` | `CRANBERRY_LOOT_STREAM_MS` | `500` | D42 | how often the streamer re-plans |
| `streamRadius` | `CRANBERRY_LOOT_STREAM_RADIUS` | `60` | D69 | the streamed disc |
| `despawnRadius` | `CRANBERRY_LOOT_DESPAWN_RADIUS` | `90` | D69 | where a streamed object is taken back |
| `maxLive` | `CRANBERRY_LOOT_MAX_LIVE` | `704` | D69 | the live-object ceiling |
| `maxPerRestream` | `CRANBERRY_LOOT_MAX_PER_RESTREAM` | `64` | D69 | spawns per re-plan |
| `byteBudget` | `CRANBERRY_LOOT_BYTE_BUDGET` | `22000` | D73 | the owner's per-re-stream byte budget |
| `maxEvictions` | `CRANBERRY_LOOT_MAX_EVICTIONS` | `64` | D73 | the owner's eviction ceiling |
| `panelRadius` | `CRANBERRY_LOOT_PANEL_RADIUS` | `2` | D70 | the f8 01 proximity panel's disc |
| `panelRows` | `CRANBERRY_LOOT_PANEL_ROWS` | `32` | D70 | the panel's row cap |
| `pickupReach` | `CRANBERRY_LOOT_PICKUP_REACH` | `4` | D73 | how far a pickup may reach |
| `spawnChance` | `CRANBERRY_LOOT_SPAWN_CHANCE` | `0` | D270 | one flat gate for every family, overriding the file's five; 0 = use the file |
| `armourRules` | `CRANBERRY_LOOT_ARMOUR_RULES` | `true` | D275 | the 2017-06-29 laminated-armour rules: 5 % world chance, 250 m apart, 3 per map square |
| `armourChance` | `CRANBERRY_LOOT_ARMOUR_CHANCE` | `0.05` | D275 | the vest's world spawn chance; retail cut it from 10 % to 5 % |
| `armourSpacing` | `CRANBERRY_LOOT_ARMOUR_SPACING` | `250` | D275 | the vest's anti-cluster radius |
| `armourPerSquare` | `CRANBERRY_LOOT_ARMOUR_PER_SQUARE` | `3` | D275 | most vests one map square may hold |
| `airdrops` | `CRANBERRY_AIRDROPS` (or `CRANBERRY_LOOT_AIRDROPS`) | `true` | D274 | retail's second loot channel; off is the pre-D274 world, with no route to the .308 |
| `airdropFirstMs` | `CRANBERRY_AIRDROP_FIRST_MS` (or `CRANBERRY_LOOT_AIRDROP_FIRST_MS`) | `300000` | D274 | match clock at the first crate |
| `airdropIntervalMs` | `CRANBERRY_AIRDROP_INTERVAL_MS` (or `CRANBERRY_LOOT_AIRDROP_INTERVAL_MS`) | `240000` | D274 | match clock between crates |
| `airdropMax` | `CRANBERRY_AIRDROP_MAX` (or `CRANBERRY_LOOT_AIRDROP_MAX`) | `4` | D274 | most crates one match delivers |
| `airdropUnlockMs` | `CRANBERRY_AIRDROP_UNLOCK_MS` (or `CRANBERRY_LOOT_AIRDROP_UNLOCK_MS`) | `8000` | D274 | retail's ~8 s crate unlock |
| `airdropRifleChance` | `CRANBERRY_AIRDROP_RIFLE_CHANCE` (or `CRANBERRY_LOOT_AIRDROP_RIFLE_CHANCE`) | `0.5` | D274 | retail's 50 % chance of the .308 Hunting Rifle |
| `airdropPoolDraws` | `CRANBERRY_AIRDROP_POOL_DRAWS` (or `CRANBERRY_LOOT_AIRDROP_POOL_DRAWS`) | `3` | D274 | weighted bundles drawn on top of the guaranteed set |

### `vehicles` — D35/D50: the car park that follows the player, and the three driving arms

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `spawnChance` | `CRANBERRY_VEHICLE_SPAWN_CHANCE` (or `CRANBERRY_VEHICLES_SPAWN_CHANCE`) | `0.3` | - | ordinary vehicle pad chance (0-1); fractional chances keep 1-2 cars per police station; 0 disables, 1 fills every pad; applies to new matches |
| `stream` | `CRANBERRY_VEHICLE_STREAM` (or `CRANBERRY_VEHICLES_STREAM`) | `true` | D50 | the car park that follows the player; off is wave 5's single landing burst |
| `streamMs` | `CRANBERRY_VEHICLE_STREAM_MS` (or `CRANBERRY_VEHICLES_STREAM_MS`) | `1000` | D50 | how often the car park re-plans |
| `streamRadius` | `CRANBERRY_VEHICLE_STREAM_RADIUS` (or `CRANBERRY_VEHICLES_STREAM_RADIUS`) | `1700` | D50 | the streamed disc |
| `despawnRadius` | `CRANBERRY_VEHICLE_DESPAWN_RADIUS` (or `CRANBERRY_VEHICLES_DESPAWN_RADIUS`) | `2000` | D50 | where a car is taken back |
| `maxLive` | `CRANBERRY_VEHICLE_MAX_LIVE` (or `CRANBERRY_VEHICLES_MAX_LIVE`) | `192` | D50 | the live-car ceiling |
| `maxPerRestream` | `CRANBERRY_VEHICLE_MAX_PER_RESTREAM` (or `CRANBERRY_VEHICLES_MAX_PER_RESTREAM`) | `32` | D50 | spawns per re-plan |
| `fuel` | `CRANBERRY_VEHICLE_FUEL` (or `CRANBERRY_VEHICLES_FUEL`) | `true` | D35 | fuel BURN - and the boost meter, because the client's VehicleTurbo rows spend RESOURCE_TYPE 50 |
| `fuelGauge` | `CRANBERRY_VEHICLE_FUEL_GAUGE` (or `CRANBERRY_VEHICLES_FUEL_GAUGE`) | `true` | D35 | the fuel resource row the client already parses |
| `relay` | `CRANBERRY_VEHICLE_RELAY` (or `CRANBERRY_VEHICLES_RELAY`) | `true` | D50 | the 0x78 bystander relay |
| `ignition` | `CRANBERRY_VEHICLE_IGNITION` (or `CRANBERRY_VEHICLES_IGNITION`) | `false` | D51 | require a hotwire or a key; off because neither is in a loot table |
| `damage` | `CRANBERRY_VEHICLE_DAMAGE` (or `CRANBERRY_VEHICLES_DAMAGE`) | `true` | D270 | the whole vehicle damage model - crashes, bullets, flips, the condition ladder |
| `bulletDamage` | `CRANBERRY_VEHICLE_BULLET_DAMAGE` (or `CRANBERRY_VEHICLES_BULLET_DAMAGE`) | `true` | D270 | a 82 06 ProjectileHitReport whose target guid names a car damages it |
| `flipDamage` | `CRANBERRY_VEHICLE_FLIP_DAMAGE` (or `CRANBERRY_VEHICLES_FLIP_DAMAGE`) | `true` | D270 | an upside-down car takes its own UPSIDE_DOWN_DAMAGE_PULSE |
| `flipMs` | `CRANBERRY_VEHICLE_FLIP_MS` (or `CRANBERRY_VEHICLES_FLIP_MS`) | `3000` | D270 | how often a car on its roof is pulsed; the amount is the client's, the period is ours |
| `collisionCap` | `CRANBERRY_VEHICLE_COLLISION_CAP` (or `CRANBERRY_VEHICLES_COLLISION_CAP`) | `2000` | - | maximum condition damage per continuous impact; server tuning (1000 units = 1 vehicle HP) |
| `collisionMinimum` | `CRANBERRY_VEHICLE_COLLISION_MIN` (or `CRANBERRY_VEHICLES_COLLISION_MINIMUM`) | `1000` | - | ignore this much of each cumulative terrain impact |
| `collisionMultiplier` | `CRANBERRY_VEHICLE_COLLISION_MULTIPLIER` (or `CRANBERRY_VEHICLES_COLLISION_MULTIPLIER`) | `0.1` | - | scale crash damage after removing small bumps |
| `flipMultiplier` | `CRANBERRY_VEHICLE_FLIP_MULTIPLIER` (or `CRANBERRY_VEHICLES_FLIP_MULTIPLIER`) | `0.1` | - | scale sustained rollover damage |
| `flipGraceMs` | `CRANBERRY_VEHICLE_FLIP_GRACE_MS` (or `CRANBERRY_VEHICLES_FLIP_GRACE_MS`) | `15000` | - | time allowed to right a tumbling car before the first roof-damage pulse; server tuning |
| `wreckDamage` | `CRANBERRY_VEHICLE_WRECK_DAMAGE` (or `CRANBERRY_VEHICLES_WRECK_DAMAGE`) | `0` | D270 | optional minimum occupant damage, in addition to the zero-health blast (zero disables the extra minimum) |
| `explosions` | `CRANBERRY_VEHICLE_EXPLOSIONS` (or `CRANBERRY_VEHICLES_EXPLOSIONS`) | `true` | - | zero-health vehicle blasts damage players; owner request 2026-09-06 |
| `explosionDamage` | `CRANBERRY_VEHICLE_EXPLOSION_DAMAGE` (or `CRANBERRY_VEHICLES_EXPLOSION_DAMAGE`) | `10000` | - | blast damage in player health units (100 units = 1 HP); server tuning |
| `explosionFullRadius` | `CRANBERRY_VEHICLE_EXPLOSION_FULL_RADIUS` (or `CRANBERRY_VEHICLES_EXPLOSION_FULL_RADIUS`) | `3` | - | full-damage blast radius in metres; server tuning |
| `explosionRadius` | `CRANBERRY_VEHICLE_EXPLOSION_RADIUS` (or `CRANBERRY_VEHICLES_EXPLOSION_RADIUS`) | `8` | - | blast reaches zero damage at this radius in metres; server tuning |
| `boost` | `CRANBERRY_VEHICLE_BOOST` (or `CRANBERRY_VEHICLES_BOOST`) | `true` | D271 | answer a boost press: 9e 01/03, 0f 15/16, 0f 33 and the 88 2b refusal |
| `turboByte` | `CRANBERRY_VEHICLE_TURBO_BYTE` (or `CRANBERRY_VEHICLES_TURBO_BYTE`) | `0` | D271 | the byte 0f 33 Character.Turbo carries on a grant; the polarity is inferred and one live press settles it |
| `positionBlock` | `CRANBERRY_VEHICLE_POSITION_BLOCK` (or `CRANBERRY_VEHICLES_POSITION_BLOCK`) | `true` | D290 | the parked car's at-rest position-update block in its 0xd7 tail; off restores the empty block that never drew |
| `spawnFlags1` | `CRANBERRY_VEHICLE_SPAWN_FLAGS1` (or `CRANBERRY_VEHICLES_SPAWN_FLAGS1`) | `0x10` | D290 | the +0x1b1 flag byte a parked car ships; 0x10 is the collidable bit, 0 restores the walk-through car |
| `shader` | `CRANBERRY_VEHICLE_SHADER` (or `CRANBERRY_VEHICLES_SHADER`) | `true` | D290 | the client's own shader group at +0x1a4 (838 for the OffRoader); off leaves the car white/rust |

### `doors` — D34/D45 with D243-D247 and D264: door spawn shape, the collision levers, the press guard, the lobby and hospital doors, match-scope door state, the streaming band and the retail swing sound

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `rotation` | `CRANBERRY_DOOR_ROTATION` (or `CRANBERRY_DOORS_ROTATION`) | `QuaternionYUp` | D38 | the +0xa0 quaternion packing; the yaw SIGN is what is still open |
| `collision` | `CRANBERRY_DOOR_COLLISION` (or `CRANBERRY_DOORS_COLLISION`) | `VisibleMesh` | D77 | which mesh a door spawn names - cosmetic, not collision (D77) |
| `restreamMs` | `CRANBERRY_DOOR_RESTREAM_MS` (or `CRANBERRY_DOORS_RESTREAM_MS`) | `500` | D42 | how often the door re-stream pump asks whether the player moved |
| `pressMs` | `CRANBERRY_DOOR_PRESS_MS` (or `CRANBERRY_DOORS_PRESS_MS`) | `800` | D75 | how long an accepted toggle absorbs further requests for the same door |
| `spawnFlags1` | `CRANBERRY_DOOR_SPAWN_FLAGS1` (or `CRANBERRY_DOORS_SPAWN_FLAGS1`) | `0x10` | D76 | the +0x1b1 physics flag byte; accepts 0x-hex or plain decimal |
| `positionUpdateType` | `CRANBERRY_DOOR_POSITION_UPDATE_TYPE` (or `CRANBERRY_DOORS_POSITION_UPDATE_TYPE`) | `0x00` | D80 | the +0x11c positionUpdateType; non-zero RELEASES collision - a negative control |
| `setCollidable` | `CRANBERRY_DOOR_SET_COLLIDABLE` (or `CRANBERRY_DOORS_SET_COLLIDABLE`) | `false` | D45 | restate collision on its own 0f 1e pair; experiment B of docs/85 |
| `lobby` | `CRANBERRY_DOOR_LOBBY` (or `CRANBERRY_DOORS_LOBBY`) | `true` | D243 | arm a door burst in the pre-match lobby - the five doors the friend's own server spawns there |
| `hospital` | `CRANBERRY_DOOR_HOSPITAL` (or `CRANBERRY_DOORS_HOSPITAL`) | `true` | D244 | spawn the 39 Hospital_*_Placer doors the client's own Models.txt marks as placement markers |
| `hospitalDouble` | `CRANBERRY_DOOR_HOSPITAL_DOUBLE` (or `CRANBERRY_DOORS_HOSPITAL_DOUBLE`) | `true` | D244 | include the 14 TWO-LEAF hospital doors, which the client swings about one pivot |
| `shared` | `CRANBERRY_DOOR_SHARED` (or `CRANBERRY_DOORS_SHARED`) | `true` | D245 | one guid space and one open bit per MATCH, so a door one player opens is open for the other |
| `capNewOnly` | `CRANBERRY_DOOR_CAP_NEW_ONLY` (or `CRANBERRY_DOORS_CAP_NEW_ONLY`) | `true` | D246 | the burst cap counts the doors it SPAWNS, not the doors the disc holds |
| `despawnRadius` | `CRANBERRY_DOOR_DESPAWN_RADIUS` (or `CRANBERRY_DOORS_DESPAWN_RADIUS`) | `650` | D246 | where a door is taken back with 0f 01; 0 never despawns, which is what doors did until now |
| `retailInteract` | `CRANBERRY_DOOR_RETAIL_INTERACT` (or `CRANBERRY_DOORS_RETAIL_INTERACT`) | `true` | D247 | the friend server's two door numbers under D53: interact range 2.0 m and flags1 0x10 |
| `interactRange` | `CRANBERRY_DOOR_INTERACT_RANGE` (or `CRANBERRY_DOORS_INTERACT_RANGE`) | `2` | D247 | the InteractReplicationData range on a DOOR's ea 04; ground loot keeps its own |
| `retailSound` | `CRANBERRY_DOOR_RETAIL_SOUND` (or `CRANBERRY_DOORS_RETAIL_SOUND`) | `true` | D264 | every family plays one of the two door sounds the retail client keeps resident (model 9903 -> row 12 metal, everything else -> row 2 wood), so no door is silent |

### `skins` — D83-D90 with D190/D203/D211: what a dressed character carries, and where the wardrobe is kept

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `stowedMeshes` | `CRANBERRY_STOWED_MESHES` (or `CRANBERRY_SKINS_STOWED_MESHES`) | `true` | D83 | a stowed gun gets a mesh |
| `wornShader` | `CRANBERRY_WORN_SHADER` (or `CRANBERRY_SKINS_WORN_SHADER`) | `true` | D84 | a worn attachment carries its own ShaderParameterGroupId |
| `handAppearance` | `CRANBERRY_HAND_APPEARANCE` (or `CRANBERRY_SKINS_HAND_APPEARANCE`) | `true` | D190 | the gun in the HAND carries the same appearance rows as the gun on the back |
| `wardrobeShader` | `CRANBERRY_WARDROBE_SHADER` (or `CRANBERRY_SKINS_WARDROBE_SHADER`) | `true` | D190 | a wardrobe attachment gets a fallback group instead of the neutral Default tint |
| `dressLast` | `CRANBERRY_DRESS_LAST` (or `CRANBERRY_SKINS_DRESS_LAST`) | `false` | D211 | OFF: an appended dress after the lobby skin rows wedges the Z2 world load |
| `remodel` | `CRANBERRY_SKIN_REMODEL` (or `CRANBERRY_SKINS_REMODEL`) | `false` | D85 | let a wardrobe pick re-MODEL rather than re-tint (default: re-tint only) |
| `dressSuppress` | `CRANBERRY_DRESS_SUPPRESS` (or `CRANBERRY_SKINS_DRESS_SUPPRESS`) | `true` | D86 | drop a SetCharacterEquipment byte-identical to the last one this session |
| `census` | `CRANBERRY_SKIN_CENSUS` (or `CRANBERRY_SKINS_CENSUS`) | `true` | D87 | the boot-time grey census - a diagnostic, byte-free |
| `genderRows` | `CRANBERRY_GENDER_ROWS` (or `CRANBERRY_SKINS_GENDER_ROWS`) | `true` | D88 | order an attachment's appearance-id pair by the wearer's own body |
| `lobbyPacks` | `CRANBERRY_LOBBY_PACKS` (or `CRANBERRY_SKINS_LOBBY_PACKS`) | `false` | D90 | let backpacks and performance footwear through the preview-only gate |
| `wardrobeStore` | `CRANBERRY_WARDROBE_STORE` (or `CRANBERRY_SKINS_WARDROBE_STORE`) | `C:\Aug2017\state\wardrobe` | D89 | where wardrobe selections are kept, per character guid |
| `wornSkins` | `CRANBERRY_WORN_SKINS_IN_WORLD` (or `CRANBERRY_SKINS_WORN_SKINS`) | `true` | D283 | a picked-up item wears its selected skin on every worn slot and stow peg, not only the hand |
| `wardrobeRestore` | `CRANBERRY_WARDROBE_RESTORE` (or `CRANBERRY_SKINS_WARDROBE_RESTORE`) | `true` | D284 | replay the stored wardrobe selections at login; OFF is the retail starter outfit |
| `authoredRows` | `CRANBERRY_APPEARANCE_AUTHORED_ROWS` (or `CRANBERRY_SKINS_AUTHORED_ROWS`) | `true` | D203 | Cranberry's own appearance rows for the thirteen grey ground-loot wearables |
| `appearanceTable` | `CRANBERRY_APPEARANCE_TABLE` (or `CRANBERRY_SKINS_APPEARANCE_TABLE`) | `full` | D316 | full: the whole friend DynamicAppearance capture; filtered: only the reward catalogue |

### `weapons` — D129/D142/D143: the staged WeaponDefinitions blob and the two multipliers that open the hand

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `z1LiveGunplay` | `CRANBERRY_Z1_LIVE_GUNPLAY` (or `CRANBERRY_WEAPONS_Z1_LIVE_GUNPLAY`) | `true` | - | Recorded Z1BR firearm tuning adapted to August; excludes shotguns |
| `table` | `CRANBERRY_WEAPON_TABLE` (or `CRANBERRY_WEAPONS_TABLE`) | `captured` | D313 | which WeaponDefinitions table ships: "captured" crosses the friend's own recoil, cone-of-fire, reload and pellet numbers into list 2 and fills lists 3 and 5 from his capture; "generated" is Cranberry's own table, byte for byte |
| `projectileTable` | `CRANBERRY_PROJECTILE_TABLE` (or `CRANBERRY_WEAPONS_PROJECTILE_TABLE`) | `z1` | D314 | which ProjectileDefinitions table ships: "z1" is all 131 rows of the owner's own projectileDefinitions.json (models, speeds, flight types, tracers); "august" is Cranberry's 14 constructor-default records |
| `definitions` | `CRANBERRY_WEAPON_DEFINITIONS` (or `CRANBERRY_WEAPONS_DEFINITIONS`) | `true` | D130 | send the WeaponDefinitions ReferenceData blob at all |
| `list0` | `CRANBERRY_WEAPON_DEFS_LIST0` (or `CRANBERRY_WEAPONS_LIST0`) | `true` | D129 | populate list 0 (the weapon records) |
| `list1` | `CRANBERRY_WEAPON_DEFS_LIST1` (or `CRANBERRY_WEAPONS_LIST1`) | `true` | D129 | populate list 1 (fire groups) |
| `list2` | `CRANBERRY_WEAPON_DEFS_LIST2` (or `CRANBERRY_WEAPONS_LIST2`) | `true` | D160 | populate list 2 (fire modes) |
| `list4` | `CRANBERRY_WEAPON_DEFS_LIST4` (or `CRANBERRY_WEAPONS_LIST4`) | `true` | D272 | populate list 4 (fire mode to projectile) |
| `ammoSlots` | `CRANBERRY_WEAPON_DEFS_AMMOSLOTS` (or `CRANBERRY_WEAPONS_AMMO_SLOTS`) | `true` | D196 | populate the weapon record's ammo-slot array |
| `projectileDefinitions` | `CRANBERRY_PROJECTILE_DEFINITIONS` (or `CRANBERRY_WEAPONS_PROJECTILE_DEFINITIONS`) | `true` | D273 | send ReferenceData "ProjectileDefinitions" |
| `adsFov` | `CRANBERRY_WEAPON_ADS_FOV` (or `CRANBERRY_WEAPONS_ADS_FOV`) | `true` | D212 | write DEFAULT_ZOOM into list 2 |
| `adsZoom` | `CRANBERRY_WEAPON_ADS_ZOOM` (or `CRANBERRY_WEAPONS_ADS_ZOOM`) | `1.2` | D212 | the ADS zoom written when adsFov is on |
| `tail` | `CRANBERRY_WEAPON_TAIL` (or `CRANBERRY_WEAPONS_TAIL`) | `true` | D129 | the Weapon ItemAdd tail |
| `tailState` | `CRANBERRY_WEAPON_TAIL_STATE` (or `CRANBERRY_WEAPONS_TAIL_STATE`) | `true` | D148 | the tail's idle scalars |
| `tailAmmo` | `CRANBERRY_WEAPON_TAIL_AMMO` (or `CRANBERRY_WEAPONS_TAIL_AMMO`) | `true` | D196 | the tail's magazine word |
| `wield` | `CRANBERRY_WIELD` (or `CRANBERRY_WEAPONS_WIELD`) | `true` | D129 | permit a body-slot-7 equipment row at all |
| `automatic` | `CRANBERRY_WEAPON_AUTOMATIC` (or `CRANBERRY_WEAPONS_AUTOMATIC`) | `true` | D129 | mark fire groups automatic |
| `stance` | `CRANBERRY_WEAPON_STANCE` (or `CRANBERRY_WEAPONS_STANCE`) | `true` | D149 | send 0f 20 Character.WeaponStance after a draw |
| `movementModifier` | `CRANBERRY_WEAPON_MOVEMENT_MODIFIER` (or `CRANBERRY_WEAPONS_MOVEMENT_MODIFIER`) | `1` | D143 | def+0x5c and +0x58 - the client presets 1.0f and 0 freezes the hand (D143) |
| `bodyId` | `CRANBERRY_WEAPON_DEF_BODY_ID` (or `CRANBERRY_WEAPONS_BODY_ID`) | `true` | D142 | write the record's id at def+0x18 as well as at the list key |
| `ironSightsArmedOnly` | `CRANBERRY_WEAPON_IRONSIGHTS_ARMED` (or `CRANBERRY_WEAPONS_IRON_SIGHTS_ARMED_ONLY`) | `true` | D287 | IRON_SIGHTS on the fire modes of armed groups only - not the fists or the binoculars |
| `meleeAbility` | `CRANBERRY_WEAPON_MELEE_ABILITY` (or `CRANBERRY_WEAPONS_MELEE_ABILITY`) | `true` | D288 | write MELEE_ABILITY_ID (rec+0x194) on the fire modes of unarmed groups |
| `fireSound` | `CRANBERRY_WEAPON_FIRE_SOUND` (or `CRANBERRY_WEAPONS_FIRE_SOUND`) | `true` | D234 | write EFFECT_GROUP (rec+0x104) - the fire composite / gunshot sound |
| `noMeleeMag` | `CRANBERRY_WEAPON_NO_MELEE_MAG` (or `CRANBERRY_WEAPONS_NO_MELEE_MAG`) | `true` | D236 | ship mode-0 charge 0 for a magazine-less weapon so the fists and binoculars show no magazine |
| `binocularsOptic` | `CRANBERRY_BINOCULARS_OPTIC` (or `CRANBERRY_WEAPONS_BINOCULARS_OPTIC`) | `true` | D235 | FORCE_FP_SCOPE on the binoculars - raise to first person and zoom on use |
| `binocularsOpticFov` | `CRANBERRY_BINOCULARS_OPTIC_FOV` (or `CRANBERRY_WEAPONS_BINOCULARS_OPTIC_FOV`) | `20` | D235 | the binoculars' aimed first-person field of view in degrees |
| `fireModeTypes` | `CRANBERRY_WEAPON_FIRE_MODE_TYPES` (or `CRANBERRY_WEAPONS_FIRE_MODE_TYPES`) | `true` | D291 | write TYPE (rec+0x24): 3 melee on the fists and blades, 12 throwable on the grenades, 8 on the binoculars - what the trigger does, and what hides the hotbar's 0-0 |
| `binocularsAbility` | `CRANBERRY_BINOCULARS_ABILITY` (or `CRANBERRY_WEAPONS_BINOCULARS_ABILITY`) | `true` | D293 | TYPE 8 on the binoculars - the trigger runs the item's own activatable ability 1111157 |
| `autoRetail` | `CRANBERRY_WEAPON_AUTO_RETAIL` (or `CRANBERRY_WEAPONS_AUTO_RETAIL`) | `true` | D331 | the AK-47 family is automatic (its own text says so) and the AR-15 is not; AUTO_FIRE_TIME_MS = REFIRE_TIME_MS on the automatic modes - off is the wave-9 AR-15 diagnostic |
| `shotgunPellets` | `CRANBERRY_SHOTGUN_PELLETS` (or `CRANBERRY_WEAPONS_SHOTGUN_PELLETS`) | `17` | D332 | Generated-table diagnostic pellet count; positive values enable the systematic 17-pellet early-August overlay; 0 omits that overlay |
| `shotgunSpreadDeg` | `CRANBERRY_SHOTGUN_SPREAD_DEG` (or `CRANBERRY_WEAPONS_SHOTGUN_SPREAD_DEG`) | `4` | D332 | PELLET_SPREAD on the same modes, in degrees (RULING 4.0); 0 puts every pellet on one line |
| `throwables` | `CRANBERRY_THROWABLES` (or `CRANBERRY_WEAPONS_THROWABLES`) | `true` | D300 | the grenades' data: the frag's ruled fire group 1404, FIRE_DURATION_MS on every throwable mode, a list-4 row per throwable mode and the five physics ProjectileDefinitions records |
| `throwableSpeed` | `CRANBERRY_THROWABLE_SPEED` (or `CRANBERRY_WEAPONS_THROWABLE_SPEED`) | `30` | D301 | SPEED of the five grenade projectiles, world units per second |
| `throwableWindupMs` | `CRANBERRY_THROWABLE_WINDUP_MS` (or `CRANBERRY_WEAPONS_THROWABLE_WINDUP_MS`) | `400` | D304 | FIRE_DURATION_MS on every throwable mode - the throw wind-up the client's trigger press needs to be >= 1 |

### `combat` — D92-D95: whether the 0x82 family is answered at all, and what a hit does

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `enabled` | `CRANBERRY_COMBAT` (or `CRANBERRY_COMBAT_ENABLED`) | `true` | D95 | answer the 0x82 weapon family at all |
| `damage` | `CRANBERRY_COMBAT_DAMAGE` | `true` | D92 | apply the retail damage table to a hit |
| `hitMarker` | `CRANBERRY_HIT_MARKER` (or `CRANBERRY_COMBAT_HIT_MARKER`) | `true` | D94 | send the hit marker |
| `fireLayout` | `CRANBERRY_WEAPON_FIRE_LAYOUT` (or `CRANBERRY_COMBAT_FIRE_LAYOUT`) | `true` | D93 | read c2s fire with the decoded layout rather than treating it as unknown |
| `practiceTarget` | `CRANBERRY_PRACTICE_TARGET` (or `CRANBERRY_COMBAT_PRACTICE_TARGET`) | `false` | D95 | opt-in NPC practice dummy; normal landings do not spawn a human actor (landing-body-20260906.md) |
| `meleeDamage` | `CRANBERRY_MELEE_DAMAGE` (or `CRANBERRY_COMBAT_MELEE_DAMAGE`) | `true` | D132 | melee through the 0xa0 ABILITY family |
| `meleeOnTrigger` | `CRANBERRY_MELEE_ON_TRIGGER` (or `CRANBERRY_COMBAT_MELEE_ON_TRIGGER`) | `true` | D237 | arbitrate a melee swing on the 82 01 trigger-down, not the 0xa0 abilities family |
| `abilityManager` | `CRANBERRY_ABILITY_MANAGER` (or `CRANBERRY_COMBAT_ABILITY_MANAGER`) | `true` | D132 | send the ability manager |
| `fireModeReply` | `CRANBERRY_WEAPON_FIREMODE_REPLY` (or `CRANBERRY_COMBAT_FIRE_MODE_REPLY`) | `true` | D198 | answer 82 0c SwitchFireModeRequest |
| `magazineResync` | `CRANBERRY_MAGAZINE_RESYNC` (or `CRANBERRY_COMBAT_MAGAZINE_RESYNC`) | `true` | D223 | adopt the client's non-dry trigger on a weapon this server declared empty (docs/107 section 10) |
| `resyncFromBag` | `CRANBERRY_MAGAZINE_RESYNC_FROM_BAG` (or `CRANBERRY_COMBAT_RESYNC_FROM_BAG`) | `true` | D333 | the adopted magazine is paid for out of the bag, and withheld when the bag holds none |
| `fireHint` | `CRANBERRY_WEAPON_FIRE_HINT` (or `CRANBERRY_COMBAT_FIRE_HINT`) | `true` | D223 | decode 82 20 WeaponFireHint and cross-check it against the accepted shot (docs/107 section 10) |
| `reloadTimed` | `CRANBERRY_RELOAD_TIMED` (or `CRANBERRY_COMBAT_RELOAD_TIMED`) | `true` | D334 | the 82 08 Reload and the stack packets go out after the weapon's own RELOAD_TIME_MS, a pump's shells one apart |
| `throwables` | `CRANBERRY_THROWABLES_ARM` (or `CRANBERRY_COMBAT_THROWABLES`) | `true` | D305 | a throwable's 82 03 is a THROW: one grenade leaves the stack, 82 19 GuidedExplode places the blast, 82 26 bounces are counted, the fallback detonates at the throw point |
| `throwableDamage` | `CRANBERRY_THROWABLE_DAMAGE` (or `CRANBERRY_COMBAT_THROWABLE_DAMAGE`) | `true` | D308 | a detonation hurts what is inside its radius - practice targets, players, the thrower |

### `ammo` — D165-D168: where a magazine comes from, and whether 11 03 ItemUpdate goes out

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `fromBag` | `CRANBERRY_AMMO_FROM_BAG` | `true` | D165 | a magazine is fed from the bag |
| `gunsSpawnEmpty` | `CRANBERRY_GUNS_SPAWN_EMPTY` (or `CRANBERRY_AMMO_GUNS_SPAWN_EMPTY`) | `true` | D165 | a looted gun spawns EMPTY |
| `itemUpdate` | `CRANBERRY_ITEM_UPDATE` (or `CRANBERRY_AMMO_ITEM_UPDATE`) | `true` | D168 | send 11 03 ItemUpdate for stack counts and per-shot durability |

### `crafting` — D48 with D256-D258: the recipe list, whether a craft request is accepted, which recipe set is shipped, and the cast bar that opens and closes it

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `recipeList` | `CRANBERRY_CRAFTING` (or `CRANBERRY_CRAFTING_RECIPE_LIST`) | `true` | D48 | the 0x26 09 Recipe.List burst after the in-match ClientIsReady |
| `craft` | `CRANBERRY_CRAFT` (or `CRANBERRY_CRAFTING_CRAFT`) | `true` | D48 | accept a craft request |
| `retailRecipes` | `CRANBERRY_CRAFT_RECIPES` (or `CRANBERRY_CRAFTING_RETAIL_RECIPES`) | `true` | D256 | ship the six recipes decoded from the owner's admin capture, not the wave-6 design four |
| `castBar` | `CRANBERRY_CRAFT_CAST_BAR` (or `CRANBERRY_CRAFTING_CAST_BAR`) | `true` | D257 | a craft runs on a cf 02 cast bar and a timer instead of completing inside the request |
| `interactionStop` | `CRANBERRY_INTERACTION_STOP` (or `CRANBERRY_CRAFTING_INTERACTION_STOP`) | `true` | D258 | close every completed cast with cf 03 InteractionStop, twice, as retail does |

### `match` — D39 with D151-D155: the replayable match seed and the death-and-victory arms

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `seed` | `CRANBERRY_MATCH_SEED` | `per match` | D39 | 1-16 hex digits replays that match's drop AND its gas circles; unset = per match |
| `endgame` | `CRANBERRY_MATCH_ENDGAME` | `true` | D154 | the death-and-victory sends |
| `targetCounts` | `CRANBERRY_MATCH_TARGET_COUNTS` | `false` | D152 | let the practice target count as the last opponent (dev only); the older alias CRANBERRY_PRACTICE_TARGET_ENDS_MATCH still works |
| `leaveOnEnd` | `CRANBERRY_MATCH_LEAVE_ON_END` | `false` | D154 | send ce 1b LeaveMatch; the re-login path is untested |
| `collisionDamage` | `CRANBERRY_COLLISION_DAMAGE` (or `CRANBERRY_MATCH_COLLISION_DAMAGE`) | `true` | D155 | apply 8e 01 Collision.Damage as fall or vehicle damage; the 44-byte body is proven from 47 live records |

### `lobby` — D248-D251: the pre-game lobby at Fort Destiny - its length, when it arms, its labels and its banners

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `ms` | `CRANBERRY_LOBBY_MS` | `120000` | D248 | how long the player stands in Fort Destiny before StartMatch (Z1 retail row: 120 s) |
| `armMs` | `CRANBERRY_LOBBY_ARM_MS` | `0` | D249 | 0 arms the lobby HUD at the client's own ClientFinishedLoading; a positive value is the old blind timer |
| `minPlayers` | `CRANBERRY_LOBBY_MIN_PLAYERS` | `1` | D250 | population at or above which the countdown runs rather than holding on "Waiting for players..." |
| `labels` | `CRANBERRY_LOBBY_LABELS` | `true` | D250 | label the green widget 13198 below the minimum population and 13356 while counting |
| `banners` | `CRANBERRY_LOBBY_BANNERS` | `true` | D251 | the ce 14 "Match starts in N seconds." banners at 60 / 30 / 10 s |

### `bounty` — D252-D255: Backing your Match - the balances, the payout tables, the ante and the drop-open suppression

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `enabled` | `CRANBERRY_BOUNTY` (or `CRANBERRY_BOUNTY_ENABLED`) | `true` | D253 | 67 0e payout tables and 67 0d backed state with the lobby HUD |
| `currency` | `CRANBERRY_BOUNTY_CURRENCY` | `true` | D255 | the four ab 03 balance rows at Fort Destiny arrival, Credits included |
| `ante` | `CRANBERRY_BOUNTY_ANTE` | `true` | D253 | answer 67 0c SelectBounty: debit, re-send ab 03, echo 67 0d |
| `suppressDropOpen` | `CRANBERRY_BOUNTY_SUPPRESS_DROP_OPEN` | `true` | D254 | keep every bounty byte inside the lobby and send nothing bounty-shaped at/after ce 16 StartMatch (the in-match auto-open is a 1148 client artefact); off restores the old pre-StartMatch 67 0d re-state |

### `menu` — D193/D201/D202/D274/D275: the server-owned lobby camera table, the actor's pose, the top bar

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `views` | `CRANBERRY_MENU_VIEWS` | `All` | D193 | all | reference (the two capture-proven shots) | off |
| `stance` | `CRANBERRY_MENU_STANCE` | `true` | D201 | pose the menu actor with Character.WeaponStance 0 on every answered view |
| `interactionEcho` | `CRANBERRY_MENU_INTERACTION_ECHO` | `true` | D201 | echo the client's own 09 16 00 straight back at it |
| `spawnMark` | `CRANBERRY_MENU_SPAWN_MARK` | `true` | D202 | spawn the lobby actor on the capture's own kotkdefault mark (z 279.97, not 279.72) |
| `topBar` | `CRANBERRY_MENU_TOPBAR` (or `CRANBERRY_MENU_TOP_BAR`) | `true` | D274 | the Experience and Currency top bar |
| `topBarXp` | `CRANBERRY_MENU_TOPBAR_XP` (or `CRANBERRY_MENU_TOP_BAR_XP`) | `0` | D274 | the experience number |
| `topBarRank` | `CRANBERRY_MENU_TOPBAR_RANK` (or `CRANBERRY_MENU_TOP_BAR_RANK`) | `1` | D274 | the rank number |
| `topBarScrap` | `CRANBERRY_MENU_TOPBAR_SCRAP` (or `CRANBERRY_MENU_TOP_BAR_SCRAP`) | `0` | D274 | the scrap number |
| `topBarCrowns` | `CRANBERRY_MENU_TOPBAR_CROWNS` (or `CRANBERRY_MENU_TOP_BAR_CROWNS`) | `0` | D255 | the Crowns balance - the Bounty screen's HARD ante spends it |
| `topBarSkulls` | `CRANBERRY_MENU_TOPBAR_SKULLS` (or `CRANBERRY_MENU_TOP_BAR_SKULLS`) | `0` | D255 | the Skulls balance - what a Bounty pays out in |
| `topBarCredits` | `CRANBERRY_MENU_TOPBAR_CREDITS` (or `CRANBERRY_MENU_TOP_BAR_CREDITS`) | `0` | D255 | the Credits balance - the Bounty screen's SOFT ante spends it |
| `motd` | `CRANBERRY_MENU_MOTD` | `true` | D275 | the MOTD panel |
| `motdText` | `CRANBERRY_MENU_MOTD_TEXT` | `Cranberry - clean-room August 2017 server (client build 0.0.118.208059)` | D275 | the MOTD body text |

### `console` — D174-D178: the August client's own debug console

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `enabled` | `CRANBERRY_CONSOLE` (or `CRANBERRY_CONSOLE_ENABLED`) | `true` | D174 | the August client's own debug console |
| `surface` | `CRANBERRY_CONSOLE_SURFACE` | `Print` | D174 | print | chat | chat0 | alert | lua |
| `localOwner` | `CRANBERRY_CONSOLE_LOCAL_OWNER` | `true` | D174 | treat the local player as the console's owner |
| `tiers` | `CRANBERRY_CONSOLE_TIERS` | `` | D176 | which command tiers are registered |
| `register` | `CRANBERRY_CONSOLE_REGISTER` | `EachZone` | D178 | each-zone | once | zoning | off |
| `selfFlag` | `CRANBERRY_CONSOLE_SELF_FLAG` | `true` | D174 | set the self record's flag that opens the console |
| `rows` | `CRANBERRY_CONSOLE_ROWS` | `10` | D210 | the mod menu's row count |
| `width` | `CRANBERRY_CONSOLE_WIDTH` | `46` | D210 | the mod menu's width |
| `confirms` | `CRANBERRY_CONSOLE_CONFIRMS` | `true` | D177 | echo a confirmation for every accepted command |
| `clientLogs` | `CRANBERRY_CLIENT_LOGS` (or `CRANBERRY_CONSOLE_CLIENT_LOGS`) | `C:\Aug2017\Client\Logs` | D174 | where the client's own AdminCommands.log is read from |

### `transport` — the landing burst's send window; 0 restores the single synchronous continuation

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `maxLoginConnections` | `CRANBERRY_MAX_LOGIN_CONNECTIONS` (or `CRANBERRY_TRANSPORT_MAX_LOGIN_CONNECTIONS`) | `8192` | network-scale-20260908 | maximum concurrent login transport sessions (1..65536) |
| `maxGatewayConnections` | `CRANBERRY_MAX_GATEWAY_CONNECTIONS` (or `CRANBERRY_TRANSPORT_MAX_GATEWAY_CONNECTIONS`) | `8192` | network-scale-20260908 | maximum gateway sessions, including menu and match players (1..65536) |
| `burstSlice` | `CRANBERRY_BURST_SLICE` (or `CRANBERRY_TRANSPORT_BURST_SLICE`) | `16` | D73 | tunnel messages per slice of the landing burst; 0 = one synchronous continuation |
| `burstSliceMs` | `CRANBERRY_BURST_SLICE_MS` (or `CRANBERRY_TRANSPORT_BURST_SLICE_MS`) | `40` | D73 | the pause between two slices |

### `features` — the whole-subsystem rollbacks of docs/32 - one variable each, because a new packet on a proven path is bisected by turning it off

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `containers` | `CRANBERRY_CONTAINERS` (or `CRANBERRY_FEATURES_CONTAINERS`) | `true` | D33 | the container and loadout model |
| `movementStats` | `CRANBERRY_MOVEMENT_STATS` (or `CRANBERRY_FEATURES_MOVEMENT_STATS`) | `true` | D32 | the per-character movement stat burst |
| `doors` | `CRANBERRY_DOORS` (or `CRANBERRY_FEATURES_DOORS`) | `true` | D34 | spawn doors at all |
| `vehicles` | `CRANBERRY_VEHICLES` (or `CRANBERRY_FEATURES_VEHICLES`) | `true` | D35 | spawn vehicles at all |
| `lootClusters` | `CRANBERRY_LOOT_CLUSTERS` (or `CRANBERRY_FEATURES_LOOT_CLUSTERS`) | `true` | D73 | cluster ground loot rather than scattering it |
| `groundLootShader` | `CRANBERRY_GROUND_LOOT_SHADER` (or `CRANBERRY_FEATURES_GROUND_LOOT_SHADER`) | `true` | D328 | a gun lying on the ground carries its base appearance shader group (AddLightweightNpc +0x1a4, the vehicle tint field) instead of 0, which composites white |

### `dev` — development aids and the two open wield experiments; none of these is retail behaviour

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `bootstrapDelayMs` | `CRANBERRY_BOOTSTRAP_DELAY_MS` (or `CRANBERRY_DEV_BOOTSTRAP_DELAY_MS`) | `0` | - | hold the zone bootstrap this long (bring-up aid) |
| `autoMatchMs` | `CRANBERRY_AUTO_MATCH_MS` (or `CRANBERRY_DEV_AUTO_MATCH_MS`) | `0` | - | zone into Z2 without a PLAY click, this long after the lobby's ClientIsReady |
| `groundLootMs` | `CRANBERRY_DEV_GROUND_LOOT_MS` | `0` | D31 | drop the sample items around the player; superseded by real loot, kept until `loot ring` |
| `starterWeapon` | `CRANBERRY_STARTER_WEAPON` (or `CRANBERRY_DEV_STARTER_WEAPON`) | `false` | D182 | grant and draw a weapon at bootstrap; kept until `give <item> wield` exists |
| `wieldFirstPickup` | `CRANBERRY_WIELD_FIRST_PICKUP` (or `CRANBERRY_DEV_WIELD_FIRST_PICKUP`) | `false` | D36 | draw the first gun picked up - an open experiment (docs/95) |
| `wieldSequence` | `CRANBERRY_WIELD_SEQUENCE` (or `CRANBERRY_DEV_WIELD_SEQUENCE`) | `true` | D183 | draw with the eight-packet sequence instead of one whole-character dress |
| `bootstrapWeaponItems` | `CRANBERRY_BOOTSTRAP_WEAPON_ITEMS` (or `CRANBERRY_DEV_BOOTSTRAP_WEAPON_ITEMS`) | `true` | D129 | grant CODE_FACTORY_NAME=Weapon items in the bootstrap burst |
| `classMappings` | `CRANBERRY_CLASS_MAPPINGS` (or `CRANBERRY_DEV_CLASS_MAPPINGS`) | `true` | D72 | fold ItemClassMappings.txt into a loadout slot's class test |
| `quickUseConsumables` | `CRANBERRY_QUICK_USE_CONSUMABLES` (or `CRANBERRY_DEV_QUICK_USE_CONSUMABLES`) | `true` | D141 | let a bandage take a free quick-use tile |
| `shredAll` | `CRANBERRY_SHRED_ALL` (or `CRANBERRY_DEV_SHRED_ALL`) | `true` | D260 | every item whose own context menu offers Shred actually shreds - Boots included |
| `dropNotification` | `CRANBERRY_DROP_NOTIFICATION` (or `CRANBERRY_DEV_DROP_NOTIFICATION`) | `true` | D259 | finish a drop with 0f 4a DroppedItemNotification |
| `dropYaw` | `CRANBERRY_DROP_YAW` (or `CRANBERRY_DEV_DROP_YAW`) | `true` | D259 | a dropped object carries the player's facing instead of an identity rotation |

### `login` — D200: how ServerInfo names a region

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `regionKeys` | `CRANBERRY_LOGIN_REGION_KEYS` | `true` | D200 | ServerInfo Region carries the client's own resolvable locale key, not a bare code |

### `peers` — reserved for lane 3C's two-client switches; any name it adds keeps working through the environment even before it has a row here

| key | legacy environment name | default | rules | what it does |
|---|---|---|---|---|
| `selfTransientId` | `CRANBERRY_SELF_TRANSIENT_ID` (or `CRANBERRY_PEERS_SELF_TRANSIENT_ID`) | `true` | D214 | write TransientIdTable.LocalPlayer (1) into the self record's +0xe0, not 0 |
| `spawn` | `CRANBERRY_PEER_SPAWN` (or `CRANBERRY_PEERS_SPAWN`) | `true` | D215 | the peer enter/leave burst: d5, the peer's 94 01, 82 15, 0f 01 |
| `relay` | `CRANBERRY_PEER_RELAY` (or `CRANBERRY_PEERS_RELAY`) | `true` | D215 | re-frame each decoded channel-2 record as 0x78 for every viewer |
| `coalesceMovement` | `CRANBERRY_PEER_COALESCE_MOVEMENT` (or `CRANBERRY_PEERS_COALESCE_MOVEMENT`) | `false` | Audit20260910 | diagnostic A/B only: expand sparse poses with retained fields before coalescing; default preserves native sparse records |
| `fireRelay` | `CRANBERRY_PEER_FIRE_RELAY` (or `CRANBERRY_PEERS_FIRE_RELAY`) | `true` | D335 | forward a shooter's trigger to his viewers as 82 15 04 01 FireState (start on the corroborated hint, stop on the trigger-up) |
| `projectileLaunch` | `CRANBERRY_PROJECTILE_LAUNCH_RELAY` (or `CRANBERRY_PEERS_PROJECTILE_LAUNCH`) | `true` | D315 | 82 15 04 0b ProjectileLaunch with 12 zero bytes to a shooter's viewers on every accepted 82 03 (shot and throw) |
| `sharedGasSeed` | `CRANBERRY_SHARED_GAS_SEED` (or `CRANBERRY_PEERS_SHARED_GAS_SEED`) | `true` | D217 | seed a second session's gas from the first so both players see one circle |
| `redress` | `CRANBERRY_PEER_REDRESS` (or `CRANBERRY_PEERS_REDRESS`) | `true` | D322 | re-send a peer's 94 01 (and 82 15 when its hand changed) to viewers that already see it |

## Removed in lane 0D

Concluded or superseded experiments, per plan §5.1 ("delete now with the code path") and the
reason column of S1 §2.2. In every case the **winning behaviour is now a constant**: the switch,
the option field where it had one, and the dead branch are gone.

| switch | rules | why it is gone |
|---|---|---|
| `CRANBERRY_GAS_CENTRE_ON_SPAWN` | D23 | bring-up only, and "must never be set for a real match" — the play area's centre and radius have been ordinary gas.centreX / gas.centreZ / gas.initialRadiusM knobs since docs/77 §8 |
| `CRANBERRY_RESEND_ZONE_DETAILS` | D19 | the LGT-H3 A/B; it added a packet to the zoning burst docs/32 proves is fragile, and it was never once turned on. The winning behaviour — send zone details once — is now the only behaviour |
| `CRANBERRY_DOOR_RESOLVE_M` | D78 | the 6 m positional fallback for an unrecognised interact guid. D78 settled it: doors resolve by guid only. Now a constant 0 |
| `CRANBERRY_INTERACTION_STRING_ORDER` | D38 | "unknown, possibly fatal": the alternative writes a locale id into the entry-count word and walks the client off the end of a 19-byte buffer. The derived order is now the only order |
| `CRANBERRY_NPC_COMPONENT` | D71 | superseded by the August proximity predicate: ground items always receive ClientNpcComponent with IsWorldItem=true; the optional switch stays removed (docs/loot-proximity-20260906.md) |
| `CRANBERRY_GAS_PHASE_WINDOW_MS` | D43 | inert under the shipped SpeedPaced pacing and documented as inert since docs/53; the knob only ever produced a rejection note |
| `CRANBERRY_CRAFTING_SELFRECORD` | D289 | the experiment is concluded FOR: DIAG-recipes-vehicles §A proved the crafting window reads its rows only from the self record's 0x11a list, so stage 2 is the delivery. The field keeps its ruling value (now true) as a constant |
| `CRANBERRY_CRAFT_COUNTS` | D48 | concluded experiment; the field keeps its ruling value (false) as a constant |
| `CRANBERRY_CRAFT_SENTINEL` | D48 | concluded experiment; the field keeps its ruling value (false) as a constant |
| `CRANBERRY_CRAFT_SEED` | D48 | concluded experiment; the field keeps its ruling value (false) as a constant |
| `--rc4probe (with ProbeService.cs)` | - | a one-off keystream-offset diagnostic from the 2026-08-27 bring-up, referenced by nothing |

## Not removed, and why

Plan §5.1 also lists a **fold** set (become a constant, switch deleted) and four preset
deletions. They are kept for now, deliberately:

- **The fold set** (`CRANBERRY_CONTAINERS`, `_MOVEMENT_STATS`, `_BOOTSTRAP_WEAPON_ITEMS`,
  `_CLASS_MAPPINGS`, `_QUICK_USE_CONSUMABLES`, `_VEHICLE_RELAY`, the skin rollbacks, the weapon
  stages, five of the seven combat switches, `_DOOR_COLLISION`) — the owner's standing
  instruction for this lane is that **every one-word revert he has used this week keeps
  working**. Folding them removes exactly that. They are now rows in this file instead, which
  costs nothing and keeps the bisect lever.
- **`CRANBERRY_STARTER_WEAPON`, `_DEV_GROUND_LOOT_MS`, `_WIELD_FIRST_PICKUP`,
  `_WIELD_SEQUENCE`, `_PRACTICE_TARGET`** — §5.1 converts these to admin verbs
  (`give <item> wield`, `loot ring`, `/target`). `Host/AdminChannel.cs` is lane 0B and does not
  exist yet; deleting the switch before its replacement lands would take a lever away with
  nothing to put in its place.
- **The `Wave4Legacy` / `Wave3Legacy` / `Wave5Legacy` / `Wave4Default` / `Owner30` /
  `LegacyD18Grey` presets and `CRANBERRY_GAS_PACING`** — §5.2 freezes "every `Gas/`, `Loot/`,
  `Inventory/`, `Combat/`, `Appearance/`, `Movement/`, `Descent/` value class", the values are
  pinned by `Generated/Rulings.g.cs` (lane 2C owns `rulings/`), and each preset is the A/B for a
  play-test the owner has actually run. Removing them is a rulings-lane change, not a config one.

## Adding a setting

One row in `src/Cranberry.Host/Config/ConfigKeys.cs`: block, key, the legacy environment name,
the kind, the D-row, one line of prose, a reader for the effective value, and a flip value for
the per-option test. `cranberry.json.example`, this document and the boot line all follow from
it, and `CranberryConfigOptionMatrixTests` immediately asserts that flipping it moves that value
**and nothing else**.
