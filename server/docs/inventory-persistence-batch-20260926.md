# Inventory and persistence batch — 26 September 2026

This local batch follows the [reconnect ownership correction](reconnect-ownership-20260926.md).
Implementation of the 14 bounded changes below passed the recorded automated
and isolated package checks. The subsequent [native playtest](native-play-feedback-20260926.md)
confirmed shotgun slot-change audio cleanup and general stability, but exposed
an additional seat-response error and reported punching slowdown. The corrected
seat response requires the separate follow-up checks; the earlier suite did not
validate that complete native action.
The owner requested that implementation be batched before running local checks.
The owner subsequently authorized source publication after the main-local
update. The [publication record](compatibility-publication-20260926.md) owns the
final combined checks and scope; player-update releases remain separate.

## Changes and evidence

| Change | Evidence and scope | Files |
|---|---|---|
| Preserve a raised hood when shredding is refused | `TryStow` clears the worn chest's hood posture. The NoRoom rollback restored its bindings but omitted that posture. Save and restore the posture for this rollback only; successful shredding still removes the garment. This is server consistency, not a newly inferred retail rule. | `Crafting/ShredTable.cs`; `ShredRollbackTests`, `HoodShredGatewayTests` |
| Preserve the latest outfit across a quick ordinary reconnect | The store loaded disk while the departure save was queued or in flight. It also relinquished queued work before a durable write and could strand a save arriving during its last drain. Keep the latest uncommitted revision visible and schedule subsequent work under the same lock as Save. A successful older write cannot remove a newer revision. | `WardrobeStore.cs`; `WardrobeReadConsistencyTests` |
| Keep reload state consistent with inventory refreshes | Previously an unload request cancelled reload before ownership/capacity validation; the accepted path did not send the full interruption to observers. Repaint/equip/unequip and displaced-item refreshes could leave a server reload running after native ItemAdd reset the client component. Cancel only accepted, affected refreshes, relay the full cancellation, then restore each rebuilt weapon's retained counter. | `ZoneService.cs`, `ZoneService.Reload.cs`; `InventoryReloadLifecycleTests`, one corrected expectation in `BodyBagPlaytestTests` |
| Parse complete item-use properties | Fresh Binary Ninja readers establish five typed property lists, not just the first integer list. Reject incomplete lengths before action dispatch; preserve simple packets and report actual trailing bytes. Property1 is the UI quantity; the leading header word is not a stack count. See the [protocol note](item-use-properties-20260926.md). | `Inventory/ItemUsePackets.cs`; `ItemUsePropertyCodecTests`, one corrected synthetic packet in `Wave9InventoryPortTests` |
| Preserve the requested action target | The common native executor/sender chain establishes target at +22 and owner/source at +30; the parser had reversed them. Bag refuel could choose the wrong nearby car. Cargo refuel also discarded the decoded target, now retained for existing validation. The independent container-move codec is unchanged. | `Inventory/ItemUsePackets.cs`, `ZoneService.VehicleInventory.cs`; distinct-GUID codec and external-target refuel fixtures |
| Restore native seat changes | The client writes seven request bytes, while the guessed parser required fourteen. Read seat and mode, then use the authenticated occupant's vehicle. The native follow-up also found reversed response status/driver fields; the writer now emits the verified order. The speed guard now uses accepted native magnitude; cooldown and occupancy rules remain. See the [seat protocol note](vehicle-seat-request-20260926.md). | `Vehicles/VehicleControlPackets.cs`, `Vehicles/VehicleSeatMotion.cs`, `ZoneService.Vehicles.cs`; native-layout vehicle fixtures |
| Prevent competing cast callbacks clearing each other's bar | Exact August cf02/cf03 consumers prove one current subject timer. Apply existing refusal policy consistently across craft, shred, consume and component-removal ownership; see the [interaction note](interaction-ownership-20260926.md). | `ZoneService.InventoryCasts.cs`, cast entry guards; `CrossKindInventoryCastTests` |
| Require the complete crafting request | The original UI reaches a serializer that always writes both recipe and count. Remove the speculative alternate layouts that treated a truncated request as a valid craft; see the [recipe note](recipe-start-request-20260926.md). | `Crafting/RecipeStartRequest.cs`, dispatch comment; recipe codec/gateway fixtures |
| Validate the requester before external vehicle actions | The native requester GUID is now checked before dispatch to external cargo/component handlers, matching the carried-inventory ownership guard. A rejected request cannot mutate either inventory or interrupt the current action. | `ZoneService.cs`; `VehicleItemRequesterTests` |
| Cancel unfinished medical application at death | Native cf03 clears the current subject timer. Retaining the application's old callback could clear later presentation during endgame. Cancel before the death burst and body-bag transfer; see the [medical lifecycle note](medical-lifecycle-20260926.md). | `ZoneService.Endgame.cs`; `MedicalLifecycleTests` |
| Keep character persistence and gateway admission consistent | Publish roster changes only after durable rename and return existing failure statuses on I/O failure. Successful deletion revokes outstanding gateway tickets; see the [login lifecycle note](login-character-lifecycle-20260926.md). The separate two-store account-bind recovery gap remains documented. | `Cranberry.Login/CharacterRosterStore.cs`, `Cranberry.Login/LoginService.cs`, `Cranberry.Login/GatewayTicketRegistry.cs`; `CharacterLifecycleTests` |
| Replicate accepted turbo presentation | Exact native 0f15/16 frames apply to the named actor; the server previously sent them only to the driver and omitted late viewers. Track effect ownership, broadcast transitions, restore late-viewer state and clear on departure/wreck; see the [turbo note](vehicle-boost-replication-20260926.md). Local-only 0f33 and runtime echoes stay local. | Vehicle orchestration partials and `Vehicles/VehicleState.cs`; `VehicleBoostReplicationTests` |
| Relay native vehicle animation state | The client emits bounded boolean and signed control-value deltas through 88/03; the server previously discarded them. Validate simulation ownership and native bounds, merge retained maps, relay to current viewers and include them in 0xdb for late viewers. See the [animation protocol note](vehicle-animation-state-20260926.md). The opaque native field remains zero as produced by this client; no server clock or balance value is invented. | `ZoneService.VehicleAnimation.cs`, `Vehicles/VehicleAnimationSnapshot.cs`, vehicle packet/full-state integration; codec and gateway animation fixtures |
| Require complete runtime-effect requests | Exact 9e01/03 readers establish a 71-byte minimum Add with five lists when nonsimple, and a 54-byte Remove. Reject incomplete bodies before turbo, MotorRun or punch-animation dispatch; see the [effect-request note](effect-request-20260926.md). Existing effect IDs and gameplay policies remain unchanged. | `Vehicles/VehicleBoost.cs`; `EffectRequestCodecTests`, `EffectRequestGatewayTests`, corrected synthetic Add helpers |

Zone runtime paths above are relative to `server/src/Cranberry.Zone`;
`Cranberry.Login` paths are relative to `server/src`. Fixtures are under
`server/tests/Cranberry.Tests/Zone` or `server/tests/Cranberry.Tests/Login`.
No client assets, balance tables,
launcher configuration or production state are changed.

The reload correction extends the [skin refresh mechanism](reload-cast-fixes-20260926.md).
Fresh read-only Binary Ninja decompilation of `0x140c35400` confirms that ItemAdd
constructs and initializes the new item before checking for an existing GUID.
There is no same-definition shortcut in this handler. On a collision it removes
the old item and notifies the owner's change listener. Weapon initialization
through `0x141454800` / `0x141484e00` / `0x14228bba0` resets its counters; reader
`0x14148c3c0` does not supply them in the item tail. All addresses are virtual,
image base `0x140000000`; target executable SHA256 is
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
The fresh export is
`C:/Aug2017/out/compatibility-20260926/item-protocol/reload-itemadd-handler.txt`.
This establishes native reconstruction, not original-server interruption policy.
The conditional fire-mode reset question remains open.

Pending wardrobe state is retained after I/O failure and retried by the next
save for that character or an explicit flush, without an automatic retry loop.
Only the latest uncommitted revision per character is retained, plus any write
already in progress. Persistent failures involving many different characters
can retain more memory. Unsaved memory cannot survive a host crash. Atomic file
replacement and off-thread disk writes remain in place.

## Validation record

Release validation completed on 26 September 2026 against this local working tree,
based on `454f73d051cd1c077d468cf5ca06e7206e6db442`. Results:

| Check | Actual result |
|---|---|
| Focused affected regressions | 886 passed, one new hood-fixture failure. The fixture incorrectly expected an unchanged appearance to be resent; it now verifies suppression and then a fresh appearance baseline. Its focused recheck passed. |
| Complete `server/Cranberry.slnx`, Release | **7,884 passed, 36 skipped, 0 failed**: Cranberry.Tests 7,363/2 skipped; Launcher.Tests 243/0; Harness.Tests 278/34. Includes the corrected hood fixture and all 14 bounded changes. |
| Fresh Windows x64 local package | Build and inventory passed; 386 immutable files, 276,032,873 bytes; no player data included. |
| Isolated package startup | **22 passed**, including fresh owner registration, persistent identity/account, normal shutdown and port-conflict handling. |
| Updater process lifecycle | **37 passed**, including handoff/rollback, failed startup, timeout, owned-process cleanup and simulated active-game protection. No native game or public network requests. |
| Exact-build protocol index | 1,743 raw registration rows preserved; 241 registered byte bases plus existing `ce` framing; six framing records, 251 lexical references across 75 bases. The 36 unmapped rows remain separate metadata, not guessed opcodes. |

The 36 skipped tests require unavailable capture/reference fixtures, deferred
native/two-client scenarios, or an explicitly running harness host. Live-harness
tests stayed disabled. Existing unrelated test-analyzer warnings remain; no new
runtime compile error remains. The initial compile also caught a missing namespace
import in a new fixture, corrected before these successful runs.

Evidence root: `C:/Aug2017/out/compatibility-20260926/final-batch-01/`:
`release-results/*.trx`, `release-summary.json`, `focused-results/`,
`hood-recheck/`, build logs, `package-smoke/results.json`,
`process-smoke/results.json` and `local-endpoints.json`.
Test build outputs stayed under ignored
`server/out/compatibility-20260926/final-batch-01` so existing source-location
checks could find `Cranberry.slnx`. Each child shell cleared inherited
`CRANBERRY_*` settings and set `CRANBERRY_HARNESS_LIVE=0`.

Package-smoke endpoints were `https://127.0.0.1:54088/`, Login UDP
`127.0.0.1:56111`, Gateway UDP `127.0.0.1:56112`, with state only in
`final-batch-01/package-smoke/local-data`. LocalEdition validated loopback bindings,
profile and port separation before starting its owned host. The updater harness
used separate private roots. Zero batch-owned hosts remained after cleanup.

The protocol index is `C:/Aug2017/out/compatibility-20260926/zone-protocol-index.json`,
SHA256 `962251379509d3469e9658ab3889cb4a2a2188029080e499fef3418581c110ec`.
It is a discovery aid; lexical references do not establish direction, active game
mode, or implementation completeness.

Regression cases cover hood refusal
and retry, queued/in-flight/failed wardrobe writes and ordinary reconnect,
accepted/rejected reload interactions and observer ordering, and complete or
truncated item-use parameter blocks, exact recipe/seat frames, explicit refuel
targets and external requester validation. They also cover medical cancellation
on death, character persistence failures and ticket revocation, turbo observers,
vehicle animation replication and cleanup, and complete runtime-effect framing.
They use synthetic in-memory gateway
traffic and isolated temporary storage; they are not original-server captures
or native game observations.

The full run also includes the earlier reconnect regressions. Its earlier results
remain historical records, not additional tests to add to this total. These checks
establish local implementation behavior; they do not prove native rendering,
original server rules, or crash freedom.

## Local candidate

Candidate directory:
`C:/Aug2017/out/compatibility-20260926/final-batch-01/Cranberry-Local-0.2.0-compat.20260926.1`.
The adjacent ZIP has SHA256
`2138fcdab1289d2c988bc08f50dcc154e2b6f54cba18c3bceacb4d975c8c693f`.
This is an unsigned developer candidate with update sequence 0. It has not been
uploaded or added to the public updater feed; no publishing approval covers it.
The client executable's target hash was rechecked unchanged after validation.

The initial candidate launch used a separate `play-data` profile. That profile
defaulted to an empty `Game` directory and prompted an unnecessary new download.
The owner subsequently requested one persistent local development setup. The
following existing entry point was used to launch this batch, but the owner then
clarified that it is the older isolated restoration setup, **not their main local
server**. The subsequent [main-local switch](main-local-workflow-20260926.md)
identified and reused the Community profile, stopped this restoration host, and
established `Start-Main-Local.cmd` as the persistent entry point. The following
old entry point is retained here only as historical evidence:

```powershell
& 'C:\Aug2017\Play-Local-August-Test.cmd'
```

Restoration test root: `C:/Aug2017/out/restoration-20260926/local-host`; game folder:
`local-host/client`. It reuses the retained account, certificate, files and ports:
HTTPS `127.0.0.1:22040`, Login UDP `127.0.0.1:22042`, Gateway UDP `127.0.0.1:22043`.
The existing helper verifies without downloading or repairing, authenticates through
the local tunnel, and keeps it open while the game runs. It starts no client patches.
Do not use the older general `run-client.ps1` or `run-host.ps1`; they contain
experimental client-helper paths.

On 26 September, the validated candidate runtime was copied into this existing
local server after checking its exact process and loopback bindings. The previous
runtime is retained as `runtime-before-compatibility-20260926-160137` beside it.
Accounts, client files and host configuration were retained. The updated
`Cranberry.Zone.dll` SHA256 is
`d9752f03d0fe078b3b622e47959510fa6e1846909e31c000ac37c38ee9c7b26c`;
`local-host/compatibility-runtime-update.json` records the source and rollback path.
The native game launched and reached its menu/transfer screen. The owner then
reported that it "seems to be good still" while noting that the test objectives
were unclear. Record this as general positive play feedback on the restoration
test server, not confirmation of all 14 fixes. Specific actions and transition
counts were not supplied; no death/menu/lobby repetition count is established.
The subsequent specific feedback and seat correction are recorded in the
[native playtest follow-up](native-play-feedback-20260926.md). In particular,
seat presentation failed despite the earlier synthetic suite passing; this is
not a successful native seat validation.

For subsequent milestones, build from the canonical Git workspace and update
`C:/Aug2017/Local-Development` with a rollback copy, retaining the main Community
profile and existing game installation. The later switch installed the seat
follow-up in that binary folder and verified existing state remained unchanged.
See the main-local record for exact paths, endpoints and receipts. No production
runtime was changed.
The restoration start script must use `-NoBuild` with that prepared runtime;
its default build source is the older research tree. Fresh throwaway storage is
still appropriate for automated persistence/failure tests and package smoke checks.
The existing research HUD cannot establish unmodified-stock visual parity.

## Native checks after automated validation

Use the retained main Community profile and existing client in the
[main local workflow](main-local-workflow-20260926.md), with verified loopback endpoints.
Do not point the research client or launcher at a live host.

1. Raise a hood and attempt a shred whose output cannot fit. The garment and
   raised hood should remain; after freeing space, shredding should work once.
2. Change outfit and immediately leave/rejoin several times. The latest outfit
   should persist, including after a clean local host restart.
3. With a shotgun reloading, unload or move it between weapon slots. The old
   reload should stop, another reload should work, and ammunition should agree.
   A refused unload or a refresh of a spare weapon should preserve the held reload.
   An observer check remains pending until two isolated local clients are available.
4. Try normal consume/drop/shred actions and a quantity dialog. Try another timed
   action while the first is pending: one bar should remain, and the next action
   should work after completion. Synthetic malformed-packet checks belong to automated tests.
5. Switch passenger/driver seats in a parked car using its normal seat key.
   Confirm the seat HUD, camera and control ownership. Refuel an explicitly
   selected car near a second car; only the selected valid target should gain fuel.
6. With a second local observer, drive, turn and turbo. Check current and late
   viewers, then seat change, driver departure and wreck presentation. Animation
   indices do not establish original handling/balance; note any residual effect.
7. Begin healing, then die before application completes. Its bar should stop at
   death and the former callback should not affect results or a later session.
8. Create/select/delete a local test character, reconnect and restart the host;
   the deleted row should stay absent. Disk-failure and stale-ticket checks are
   automated fixtures, not instructions to alter real account storage.
9. Repeat death → menu → lobby three times and record the actual observations.

No native repetition count has been reported for this batch. Existing medical
acceptance and the custom binocular zoom's unresolved provenance remain as
recorded in the [coverage matrix](compatibility-status.md).
