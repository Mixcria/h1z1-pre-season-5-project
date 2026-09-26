# August 2017 compatibility inventory

This living inventory covers **26 discovered investigation units** as of
26 September 2026. It can grow; these counts are not a percentage of the original
game restored. The target is app 433850, depot 433851, manifest
6373368576374184611, build 0.0.118.208059, ClientProtocol_1148. The analyzed
`H1Z1.exe` SHA256 is
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
Native addresses below are virtual addresses at image base `0x140000000`.

The [baseline record](../../docs/RESTORATION.md) records the published baseline;
the [current batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns the historical batch results; the [publication milestone](compatibility-publication-20260926.md)
records the combined final checks and authorized scope. This page tracks feature
coverage, not a second set of global test totals. The two user-confirmed medical results came
from the isolated research workspace using this target executable and manifest;
its HUD already contained earlier modifications. Native confirmation of the
fresh community package and unmodified-stock visual parity remain pending.

RE, implementation and validation are independent. RE is `unknown`, `partial` or
`verified`; implementation is `missing`, `stub`, `partial` or `implemented`;
validation is `untested`, `failing`, `locally passing` or `user-confirmed`.
`Partial` includes functioning systems whose original rules remain unknown.
For broad units, validation applies only to the observations named in the row;
an automated pass does not establish native presentation or original server
behavior. Source references are relative to `server/src/Cranberry.Zone` unless
a project or repository path is given. Registration names and source comments
are investigation leads until checked against an active client path.

The later rejected-fire-mode and inventory-cast ownership fixes are included in
the current [reload and cast increment](reload-cast-fixes-20260926.md), alongside
the weapon skin/reload correction. They were excluded from the earlier public
baseline. Current automated results and the owner's general native acceptance
are recorded in that increment's note. Individual visual observations and
transition counts remain unspecified; historical playtests do not validate them.

The next local increment corrects [reconnect ownership](reconnect-ownership-20260926.md).
Three baseline regressions reproduced a late old disconnect removing the new
peer, despawning it for observers and dropping it from throwable lookup. The
replacement now retires its predecessor before registration. This is a shared
session prerequisite; original-server reconnect/resume policy remains unknown.

The subsequent [inventory and lifecycle batch](inventory-persistence-batch-20260926.md)
corrects hood rollback, ordinary reconnect reads during pending wardrobe writes,
inventory/reload reconstruction and incomplete item-use property parsing. It
also corrects the item's reversed target/source GUIDs, respects explicit cargo
refuel targets, restores [native seat-change requests](vehicle-seat-request-20260926.md),
serializes [competing interaction casts](interaction-ownership-20260926.md), and
requires the [native crafting request](recipe-start-request-20260926.md). Its
implementation passed the final Release suite for all 14 bounded subitems.
The subsequent [native playtest](native-play-feedback-20260926.md) exposed an
additional seat-response error and reported punching slowdown. The seat writer
has been corrected; its follow-up validation is separate from the earlier run.
The candidate package and updater smoke checks also
passed; exact totals and artifacts are in that batch's validation record. Earlier
passing results are retained separately. Fresh Binary Ninja
evidence is recorded in the [item-use protocol note](item-use-properties-20260926.md).
The batch also closes [medical application cleanup on death](medical-lifecycle-20260926.md),
[character deletion and save consistency](login-character-lifecycle-20260926.md),
and [turbo observer presentation](vehicle-boost-replication-20260926.md).
It also restores [native vehicle animation replication](vehicle-animation-state-20260926.md)
using bounded client-reported deltas and retained late-viewer state.
It requires [complete runtime-effect requests](effect-request-20260926.md) before
turbo, MotorRun or punch-animation action dispatch.
The [protocol discovery indexer](../tools/opcodes/README.md) joins pinned native
registrations to source references without inferring missing features from names
or numeric gaps. These findings deepen the existing 26 units; they do not add
claimed retail coverage.

The owner has selected their existing main local setup for interactive tests.
Use [the main local workflow](main-local-workflow-20260926.md): existing Community
accounts/outfits, `C:/Aug2017/Client`, and `Start-Main-Local.cmd`. The restoration
host is stopped. Older references to its isolated play profile are historical;
temporary stores remain appropriate for automated failure/persistence fixtures.

Current bounded implementation subitems (14 changes; the recorded automated suite passed; native failures below take precedence for the complete action):

| Units | Gap established in this batch | Evidence / dependency | Bounded local result / remaining native check |
|---|---|---|---|
| C16,C8 | Failed worn-hoodie shred lost raised posture during rollback | Server `TryStow`/`Unbind` path, unchanged native appearance writer | Refusal preserves bindings/posture; later redraw and successful retry agree |
| C17 | Ordinary relogin could read older disk while the latest outfit save was queued or in flight | Store queue/read/write lifecycle; separate from overlapping-session admission | Pending/in-flight/failure and revision races, ordinary gateway relogin, restart after successful flush |
| C9,C8 | Invalid unload interrupted reload; valid refreshes could leave obsolete reload work | Native ItemAdd constructs before GUID lookup at `140c35400`; same reset already handled for skinning | Refused action preserves reload; accepted refresh interrupts observer and restores both moved/displaced counters |
| C8 | Item request parser ignored mandatory property lists | Exact writer/readers in item-use note | Valid five-list requests parse; malformed requests cannot mutate; quantity and trailing bytes remain correctly represented |
| C8,C13 | Item request target/source GUIDs were reversed and cargo refuel discarded the target | Exact common manager/executor chain in item-use note | Distinct-GUID actions preserve the requested target; explicit refuel affects only the selected valid vehicle |
| C13 | Native seven-byte request was rejected; follow-up revealed reversed response status/driver fields | Request serializer `140e84fc0`; response dispatcher `140c9fee0` and consumer `140c3b4f0` | Earlier request regression passed but native passenger presentation failed. Response corrected to seat/status/driver; follow-up checks in seat note |
| C16,P4,C13 | Different cast kinds could own timers concurrently and stop each other's UI | August cf02 overwrites one subject record; cf03 has no cast identity | Ordered cross-kind pairs with delayed callbacks, vehicle removal, scheduling failures and ordinary non-cast actions |
| C16 | A truncated recipe request could execute through speculative no-count parsing | UI binding `14143a3e0`, mandatory eleven-byte serializer `1411b41e0` | Truncated requests cannot schedule or consume; subsequent complete request works with both cast settings |
| C8,C13 | External vehicle item actions bypassed requester ownership validation | Native item-use +14 is the requesting player; external dispatch preceded the carried-inventory guard | Wrong requester cannot loot/equip/remove components or replace a pending action; valid retry works |
| P4,C16 | Death retained an unfinished medical application until its old callback ran | `KillPlayer` omitted medical cancellation; native cf03 addresses the current subject timer | Immediate cancellation before death presentation/body-bag transfer; obsolete callback is silent |
| C3,C17 | Durable deletion left valid gateway tickets; failed roster writes left unsaved memory visible | Login/store call order and shared ticket registry; existing ownership checks preserved | Delete revokes all outstanding tickets; failed writes return failure and permit retry without a ghost row |
| C13 | Turbo composite transitions and late stream-in omitted observers | Exact 0f15/16 readers and original client effect rows; 0f33 affects the local player only | Correct viewers receive tags, control bytes stay local, stale/duplicate requests and departure/wreck cleanup work |
| C13 | The server discarded native animation deltas and always wrote empty full-state maps | Exact 88/03 sender/consumer, bounded flag/value indices and shared 0xdb body | Current simulator alone updates presentation; correct viewers receive deltas; late viewers and released controllers receive retained state; no authoritative health/fuel changes |
| C11,C13 | Incomplete Add/Remove effect requests could reach gameplay actions | Exact 9e01/03 readers and Add's five mandatory property lists | Malformed requests cannot alter turbo, MotorRun or punch state; subsequent complete requests work |

These are implementation subitems within the 26 investigation units, not fourteen
whole systems newly verified. Broad row validation below does not inherit a
subitem pass automatically. Stock salvage options do not request the quantity
selector; bulk shredding remains unchanged. Native gameplay occurred and the
owner confirmed shotgun slot-change audio cleanup and general stability; exact
death/menu/lobby repetition counts remain unspecified. Seat presentation failed
and punching slowdown was reported; see the follow-up record. Source publication
has since been authorized; the publication milestone records its scope. A player
update and live deployment remain separate.

## Coverage

| ID / expected behavior | Evidence and relevant messages | Existing implementation | RE | Implementation | Validation | Confidence, unknowns and next action |
|---|---|---|---|---|---|---|
| P1 Stable death, menu and lobby transitions | Owner's historical live report and latest general local stability confirmation; source session/world-generation guards and logout paths | `ZoneService.Logout.cs`, `ZoneService.Endgame.cs`, `ZoneService.cs`; `Cranberry.Transport/SoeConnection.cs`; launcher helper exclusions | partial | implemented | locally passing | High confidence safeguards are preserved; exact live revision is unknown. Bounded regression checks are local evidence. Exact latest native cycle count was not supplied. C1 prerequisite; record 3 explicit local cycles. |
| P2 Binoculars activate native aiming/reticle and exit cleanly | Item1542, weapon1384, firegroup21, ability1111157; `82/0c`; camera `0x14158ad50`, FOV `0x140e3fff0`, mode setter `0x1422935d0` | `Weapons/WeaponDefinitionsBlob.cs`, `Combat/WeaponFireArm.cs`, `Inventory/InventorySlots.g.cs` | partial | partial | untested | Native mechanism traced; current 20-degree zoom is a custom approximation. Original server-authored camera values remain unknown. Obtain same-build values before changing zoom; test equip/release/swap/death/menu. Rejected-mode lifecycle correction is included and locally tested; native transition checks remain. |
| P3 Bleeding changes health, presents native state and clears | Resource21 via `8d`; `9e/06` tag, `9e/08` removal; five named HUD tags; [medical evidence](native-medical-presentation-20260926.md) | `ZoneService.Bleeding.cs`, `ZoneService.Health.cs`, `Combat/MedicalModel.cs`, `Combat/MedicalEffectPackets.cs` | partial | partial | user-confirmed | Research client showed indicator, continuing drain after hits stopped and clearing after treatment. Wire confidence high; original onset/rates/severity thresholds unknown. Fresh-package, all-severity, observer and transition checks remain. |
| P4 Healing shows remaining projected health and progress | Stock HUD `ResourceBar.setRegen`; `9e/07` resource modifier; native consumers `0x1414d2820`, `0x1414d1550`; [medical evidence](native-medical-presentation-20260926.md) | `Combat/MedicalEffectPackets.cs`, `Combat/MedicalModel.cs`, `Combat/PendingMedicalCast.cs`, `ZoneService.HealingHud.cs`, `ZoneService.cs` | partial | partial | user-confirmed | Overall healing accepted on research client; exact color and individual edges were not separately confirmed. Original item values and some icons remain unknown. Preserve accepted behavior; check the fresh package at max health, with bleeding and across interruption/death/rejoin. |
| C1 Isolated local hosting and launch | Loopback endpoint inspection, separate state, local host smoke checks; helper-free native launch path | Repository `Build-Local.ps1`; `Cranberry.Host`; launcher `LocalEdition` and `MainForm`; `tests/LocalEdition.Smoke` | verified | implemented | locally passing | Isolation is verified within the tested setup. The current candidate package and updater smoke checks passed; exact results belong in the batch record. Native package play remains pending. Keep test accounts/state separate and record configured endpoints before each session. |
| C2 Reliable transport, encryption and reconnect | SOE control packets, RC4 and channel framing; session-ID disconnect handling | `Cranberry.Transport/SoeConnection.cs`, `InboundChannel.cs`, `OutboundChannel.cs`, `ControlPackets.cs` | partial | implemented | untested | Implemented framing and regressions exist; exact August escaping provenance remains incomplete. Shared dependency: verify fragmentation, leading-zero handling and stale disconnect against same-build evidence. |
| C3 Login, character selection and menu session | LoginUdp14 pairs `01/02`, `05/06`, `07/08`, `09/0a`, `0b/0c`, `0d/0e`, `10/11` | `Cranberry.Login/LoginService.cs`, packet classes and account/character stores | partial | implemented | untested | Create/delete, failed-save retries and stale-ticket regressions pass locally. Original entitlement/status policy is unresolved. After C1/C2, check native fresh-account create/select/delete/relogin; cross-file create recovery remains open. |
| C4 Gateway authentication and world bootstrap | Header low5 opcode/high3 channel; clear `01`, encrypted `02`; client/server tunnels6/5 | `GatewayPackets.cs`, `ZoneService.HandleLogin`, `SelfRecord.cs`, `ClientProtocolPackets.cs` | partial | implemented | untested | Partly derived layouts; large self-record defaults remain evidence-specific. After C3, trace `6e`, tables, self and ready ordering, interrupted entry and repeat zoning. |
| C5 Movement, spawn and parachute landing | Channel2 sparse movement; channel3 `90` managed movement; `70/88` mount families | `MovementPackets.cs`, `MovementState.cs`, `ManagedMovementPackets.cs`, `ZoneService.cs`, `Descent/` | partial | partial | untested | State-delta handling exists; tuning includes adopted/custom values. After C4/P1, check native spawn/landing/rezone and first post-dismount pose; separate interpolation from server authority. |
| C6 Peer appearance and entity interest | Character/movement families; peer spawn and removal paths | `ZoneService.Peers.cs`, `World/SessionRegistry.cs`, `World/PeerSpawnWriter.cs` | partial | partial | untested | Replication exists; interest limits are local policy. After C5, use two local clients for range entry/exit, equipment, death and reconnect; check transient IDs and stale actors. |
| C7 Ground loot and world containers | `09` interaction, `0f` full NPC, `11` item updates | `ZoneService.SharedLoot.cs`, `Loot/`, `LootPackets.cs`, container handlers | partial | partial | untested | Shared loot/bodybags exist; old refusal text alone does not prove a missing path. After C5/C8, trace claim, ownership, inventory update and peer removal under competing pickup/full-container conditions. |
| C8 Inventory, equipment and item verbs | Items/Container families; `86` loadout, `94` equipment; active verb dispatch | `Inventory/InventoryActions.cs`, `Inventory/InventoryAutoAssign.cs`, `Inventory/InventoryMoves.cs`, `ZoneService.cs` | partial | partial | untested | Broad actions need native traces. Item-property, target/requester, reload-refresh and cross-kind cast regressions pass locally; native repeated-action checks remain. After C4/C16, trace stack/swap/drop/transfer; establish reachable Give/Repair/PlaceItem actions before treating refusals as defects. |
| C9 Weapon selection, ammunition, mode and reload | `82 + u32 gameTime + u8 sub`, including multiweapon messages | `Combat/WeaponFireArm.cs`, `Combat/WeaponFireArm.Reload.cs`, `Weapons/WeaponSession.cs`, `Weapons/WeaponDefinitionsBlob.cs` | partial | partial | untested | Owner confirmed shotgun slot-change audio cleanup; broader row remains untested. Native F cancels reload before ordinary pickup; exact path and passing server continuity checks in [reload note](loot-reload-feedback-20260926.md). Tables mix derived/adopted/generated values; skin/reload animation, observer audio and conditional mode reset remain separate. |
| C10 Shooting, hit arbitration and damage | `82` shot/fire hints; health `11/0001` and `8d/3` | `Combat/WeaponFireArm.cs`, `Combat/HitFeedbackPackets.cs`, `ZoneService.PlayerCombat.cs`, `ZoneService.Endgame.cs` | partial | partial | untested | Active combat and alternate world scaffolding coexist; source presence does not establish active authority. After C5/C6/C9, trace a two-player hit through armor/health/death/feedback and negative authority cases. |
| C11 Melee, throwables and environmental damage | Ability, weapon and effect families; client ability/projectile definitions | `Combat/MeleeArm.cs`, `Combat/ThrowableArm.cs`, `ZoneService.Throwables.cs`, `ZoneService.EnvironmentalDamage.cs` | partial | partial | failing | Owner reports severe temporary slowdown while punching; normal speed returns afterward. Current fists omit SPRINT_FIRE; native sprint-stop path and neutral movement multipliers are documented in the [punch trace](punch-movement-20260926.md). Establish intended same-build flag and quantify the slowdown before changing values. Broader throwable and environmental behavior remains untested. |
| C12 Match queue, scores, results and return | `ce` BR dispatcher exists beyond the base registration list; active result/logout paths | `ZoneService.MatchWorlds.cs`, `ZoneService.TeamResults.cs`, `ZoneService.Logout.cs`, `Match/` | partial | partial | untested | Native and launcher/custom UI flows coexist; original queue policy is unproven. After P1/C3/C6, run queue/lobby/round/results/menu, duplicate results and play-again with exactly-once persistence. |
| C13 Vehicles, seats, movement, damage and inventory | `70` mount, `88` vehicle, `78` pose, `9e` boost; exact seat serializer/consumer and 88/03 producer/consumer | `ZoneService.Vehicles.cs`, vehicle partials, `Vehicles/` | partial | partial | failing | Native seat presentation failed; the response order is now corrected. Later intermittent refusal traced to arrival-time speed; the native-speed guard passed 620 vehicle cases and the full Release suite, and is installed on main local. Repeat parked driver/passenger/driver camera/steering check remains pending. Broader native behavior and original balance remain unverified. |
| C14 Doors, world objects and destructibles | Native Character flags; `0f/0a` door state and native door spawn/state ordering | `ZoneService.Doors.cs`, `World/Doors/`, `ZoneService.Destructibles.cs` | partial | partial | untested | Native-door safeguards preserved; broader interactions are not freshly validated. After P1/C5, check open/close/collision, teardown and destructible replication with two clients. |
| C15 Gas, safe zone and toxicity | BR HUD, resource611 and health1 | `Gas/`, `ZoneService.cs`, `ZoneService.Health.cs` | partial | partial | untested | Presentation fields partly derived; server schedule is policy, not proof of original timing. After P3/P4/C12, check shared rings, damage/healing interaction, spectator state and match-end cleanup. |
| C16 Crafting, salvage and interrupted casts | `09/1a` recipe, `ac/2c` use, `cf/02/03` interaction | `Crafting/RecipePackets.cs`, `Inventory/InventoryActions.cs`, `ZoneService.cs`, `ZoneService.ProximityShred.cs` | partial | partial | untested | Same-kind callback ownership, cross-kind guards, hood rollback and native recipe framing pass local regressions. Original yields/timings remain unknown. After C8/P4, test native repeated craft/shred, cancel/death/rezone and input drop/transfer. |
| C17 Persistence and reconnect lifecycle | Character stores; gateway admission; `0f/01` GUID-based removal, reader `0x140a5fd50`, dispatcher `0x140af9ca0`; [reconnect increment](reconnect-ownership-20260926.md) | `Cranberry.Login/`, `WardrobeStore.cs`, `Economy/`, `ZoneService.cs`, `ZoneService.Peers.cs`, `Cranberry.Transport/SoeConnection.cs` | partial | partial | locally passing | Gateway replacement regressions pass: stale close cannot remove the new peer, observer mapping, throwable target, vehicle observer, party or wardrobe cache. Ordinary relogin during pending/in-flight wardrobe saves, durable store reload, deleted-character tickets and failed-save retries also pass. Native two-client reconnect and restart observations, original resume policy and cross-file create recovery remain open. |
| C18 Parties, teams and team results | Groups/party messages and `ce` team HUD | `ZoneService.Parties.cs`, `ZoneService.Teams.cs`, `ZoneService.TeamResults.cs` | partial | partial | untested | Native and launcher surfaces coexist; API success is not native parity. After C3/C12/C17, use two local accounts to invite/join/queue/leave/die and compare native roster/results with server state. |
| C19 Economy, cosmetics and progression | Items/InGamePurchase/Grinder/MatchHistory families | `ZoneService.Economy*.cs`, `ZoneService.InventorySkins.cs`, `Economy/`, `Progression/` | partial | partial | untested | Extensive implementation; prices/rewards and customized UI are not original evidence. After C3/C17, trace transactions to durable entitlement and native menu, including retry/reconnect/no-funds cases. |
| C20 Social and voice surfaces | Native social paths plus launcher/service voice extensions | `ZoneService.Social*.cs`, `ZoneService.LauncherSocial.cs`, launcher voice/service components | partial | partial | untested | Extension transport is not evidence of original voice equivalence. After C18, classify native requirements versus extensions and check session/party membership locally. |
| C21 Reference data, assets and configuration provenance | Same-build registration/table extraction; server WeaponDefinitions and StringHashToValue | `server/tools/pipeline/`, `Weapons/`, `StringHashValues.g.cs`, `ClientProtocolPackets.cs` | partial | partial | locally passing | Bounded registry and six-family-width reproduction passed. Some active values remain adopted/custom; installed research HUD is modified. Shared prerequisite: record source/hash per active table/field; do not promote later protocol1315 values to August facts. |
| C22 Legacy, debug and other-mode candidates | Registration rows, Twitch/metrics arms and PlaceItem/GiveItem/RepairItem names | `ZoneOpcodes.g.cs`, `ZoneService.HandleClientTunnel`, `Inventory/InventoryActions.cs` | unknown | partial | untested | Mode applicability unresolved. A name, empty handler or numeric gap is not a defect. Trace same-build callsites/UI gates; classify unused/legacy/debug candidates before implementing replies. |

Counts for this deliberately broad inventory: RE **1 verified, 24 partial,
1 unknown**; implementation **5 implemented, 21 partial**; validation
**2 user-confirmed, 4 locally passing, 2 failing, 18 untested**. These broad statuses remain
conservative: an untested broad row can contain passing subitem tests, which do
not establish the complete feature's behavior. The 14 new subitem passes are
recorded separately above.
The two confirmations concern the research medical behavior, not the freshly
rebuilt package. Verified medical codecs do not establish every medical rule.

## Ordered next work

| Order / units | Gap and supporting evidence | Investigation / dependencies | Completion demonstration |
|---|---|---|---|
| 0 / C13,C11,C9 | Native test exposed rejected passenger-seat response and punching slowdown; looting sends a client reload interrupt before pickup | Correct verified seat field order, trace exact fist movement/ability consumers, distinguish native interaction cancellation from server inventory refresh. Preserve accepted slot-change audio fix | Seat regressions and native parked driver/passenger/driver retest; evidence-backed punch correction if justified; direct ordinary pickup preserves server reload and explicit native interruption remains honored |
| 1 / P1,C1 | Live stability and latest local stability are owner-reported; exact native cycle counts are not recorded | Preserve session guards and helper-free launch; inspect loopback endpoints and separate state before play | Preserve the passing candidate package checks in the batch record, then record at least 3 local death/menu/lobby cycles with no surviving prior-world effects |
| 2 / P3,P4 | Native `9e/06/07/08` presentation is accepted in research; fresh package and stock visual parity remain separate | Use the exact target client; check max health, simultaneous bleed/heal, interruption, death and rejoin without changing assets | Native package observation for projection/progress, continuing bleed after hits stop, treatment clearing and cleanup; label each observed edge explicitly |
| 3 / P2 | Native binocular path exists but its 20-degree value is an approximation | Need original same-build camera/firemode configuration or a proven native default source; later1315 capture is only a lead | Evidence-backed field values followed by native equip/aim/release/unequip/death/menu checks; no replacement guess |
| 4 / P2,C8,C9,C16 | Rejected-mode, skin/reload and inventory-cast ownership fixes are already integrated; native edge observations remain incomplete | Preserve their regressions and the recorded general owner acceptance; trace the conditional mode-reset question before changing behavior | Record native mode/craft/shred cancellation, skin/reload and rejoin observations; do not repeat completed implementation work |
| 5 / P3,P4,C21 | Codecs establish accepted/rendered fields, not original bleeding/healing balance or every icon | Trace same-build resources/definitions and active item use; record missing original onset, rates, durations, stacking and icon assignments | Field-by-field provenance and bounded corrections only where evidence supports them; preserve accepted policy otherwise |
| 6 / C2,C4,C5,C6,C17 | Reconnect ownership, pending wardrobe reads and durable deletion now pass bounded local regressions; native continuity and cross-file create recovery remain open | Use the retained main local profile for rapid native rejoin and clean restart. Design recovery for interruption between roster creation and account binding before implementing it | Native outfit/peer continuity is observed across rejoin/restart; a future recovery fixture proves no unowned roster row after interruption between the two durable saves |
| 7 / C7,C8,C9,C10,C11,C16 | Hood rollback, reload lifecycle, item properties/targets, shared casts and native recipe/effect frames pass local regressions; native action chains remain unobserved | Play the main local build through refusal/retry, reload/slot refresh, explicit targeting and competing timed actions. Keep the closed stock salvage quantity gate unchanged | Native actions retain refused items/posture, affect the selected target, keep one interaction bar and permit the next action; record reload sound/animation and inventory results |
| 8 / C12,C13,C14,C15,C18 | Match/team/gas/vehicle/object transitions remain only partially established | Seat, turbo and 88/03 ownership/replication regressions pass; observe native driving/handoff/wreck presentation before deeper vehicle changes. Distinguish native versus custom match/team flow | Local rounds with queue cancel/results/play-again; two-client vehicle/door/destructible tests; exactly-once result and cleanup |
| 9 / C3,C17,C19,C20,C22 | Durable account/social behavior and legacy scope remain uncertain | Fresh local stores; trace native display and retries; classify candidate handlers by same-build reachability | Restart/reconnect without duplicate transactions or stale membership; classify each legacy candidate with evidence or leave it unknown |

Original August server captures remain unavailable. Client analysis can prove
what the client accepts or renders, but cannot by itself prove hidden server
policy. Current-retail research must retain a separate identity. The C++ rewrite
remains deferred until the agreed C# behavior is validated with the owner.
