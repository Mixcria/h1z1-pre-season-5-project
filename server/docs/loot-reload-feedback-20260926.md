# Reload interruption while looting: native play feedback

The owner reported that looting stops every weapon's reload. The inspected local
session confirms that the client sends `82/09 ReloadInterrupt` **before** the
pickup request, including an ordinary cargo item. This is separate from the new
server cancellation when the reloading weapon itself changes inventory slots.
No server gameplay change or client workaround was made for this finding.

## Observed order on the isolated host

The running local candidate's console log records:

| Local log time | Ordered events |
|---|---|
| 16:12:08.473–08.474 | Inbound reload interrupt; PlayerSelect; item 10 picked up as a stowed spare weapon |
| 16:12:10.642 | Inbound reload interrupt; PlayerSelect; ordinary item 134 picked up into a container |

These are observations of this local implementation, not original-server
captures. A filtered snapshot records source path, byte length, SHA256, line
numbers and timestamps at
`C:/Aug2017/out/compatibility-20260926/play-feedback-01/reload/local-reload-loot-events.json`.
It excludes authentication, tickets and account records. The broader snapshot has
11 stops attributed to `82/09` and one to vehicle entry; those counts do not
establish eleven identical pickup scenarios or confirm every weapon visually.

## Exact August client evidence

Fresh read-only Binary Ninja exports use the August executable SHA256
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`,
base `0x140000000`. The evidence index is
`C:/Aug2017/out/compatibility-20260926/play-feedback-01/reload/evidence-index.json`,
SHA256 `716F68AFD083203BC475F04CB630609A22085DF71021A7AD524534816A008FE3`.

- `0x1411c6d70` resolves the held weapon and target. After player-busy,
  outstanding-response and ironsight checks, a weapon state in 10–13 causes
  `0x1411b8610` (`CancelPendingReloads`) instead of immediate interaction dispatch.
  Native reload start `0x142294340` enters state 10; cancellation `0x142291750`
  changes state 10 to idle and states 11/12 to their ending state. The cancellation
  decision contains no configuration/hash or weapon-table lookup.
- The response gate is distinct: local-player `0x1411b7c80` reaches
  `0x141486330`, which checks the outstanding flag in weapon states 10–12.
  Acknowledgement applier `0x1414887e0` clears that flag without ending the reload.
  Our existing immediate acknowledgement therefore permits interaction to reach
  the later cancellation branch; delaying it would block interaction, not enable
  simultaneous looting and reloading.
- The explicit exemption is target actor `+0x4348 > 0`. Fresh independent tracing
  shows `0x140c51c90` takes spawn-record `+0x19c`; `0x140c51c00` stores that value
  and installs a door controller. `0x14149dac0` uses the same value as a Doors
  table key for opening/closing effects. This is door identity, not a general
  permission flag. The supporting `native-door/evidence-index.json` SHA256 is
  `1e65b62a173d43abce677498215d82765f34573b8bb6fee1cb6dd369ca35c029`.

No legitimate server-controlled bypass was established by this bounded trace.
Marking loot as a door, ignoring a native interruption, or inventing an automatic
reload restart would not reproduce the established client behavior. Historical
experimental client helpers are investigation history only and remain unused.
Original retail server policy beyond the observed client behavior is unknown.

## Server path and validation

`PublishLootPickup` does not call `StopReloadForItemRefresh`. That helper is scoped
to the affected item's GUID on equip/unequip/repaint/unload. Ordinary cargo,
ammunition stacks, apparel and stowed spare pickups do not rebuild the held
weapon. An actual held-weapon replacement remains a distinct action.

`LootReloadContinuityTests` adds 21 cases: 18 server pickup checks across AR-15,
AK-47 and shotgun, including ordinary cargo via interact/quick-loot/drag plus
ammo stacking, apparel and spare weapons; three explicit interrupt-before-pickup
sequences. The tests verify retained deadlines, counters and held identity, no
held-item reconstruction or stop reply, exact ammunition conservation, and no
old completion after an explicit interruption. They use synthetic gateway
requests and an explicit server clock; they do not simulate the native F branch.

All 21 passed in the parent-run Release selection: **624 passed, 0 failed,
0 skipped**, including all vehicle tests and `InventoryReloadLifecycleTests`.
Result: `play-feedback-01/results/seat-reload.trx`. Filter for this file alone:
`FullyQualifiedName~LootReload_`.

The reported F behavior therefore remains a documented native-client limitation
within the unchanged-client scope. Direct inventory pickup continuing a reload
is technically checked on the server; native quick-loot/drag presentation still
requires separate observation and is not claimed here.
