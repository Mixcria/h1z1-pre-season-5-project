# Native playtest follow-up — 26 September 2026

This follows the [inventory/lifecycle batch](inventory-persistence-batch-20260926.md).
The owner played the existing restoration test server at HTTPS `127.0.0.1:22040`,
Login UDP `127.0.0.1:22042` and Gateway UDP `127.0.0.1:22043`. It used the pinned
August executable and the candidate runtime recorded in that batch. This is
local implementation evidence, not an original retail server capture.

| Report | Status and next action |
|---|---|
| Shotgun reload noise stops when moving between weapon slots | User-confirmed for this action. Observer audio and other refresh cases remain separate. |
| Stability is fine | General user confirmation. No number of death/menu/lobby repetitions was supplied. |
| Seat change removes driving but character/camera stays in the driver seat, with no sound | Native failure reproduced by the owner's report. Binary Ninja identifies reversed response status/driver fields; corrected in the server. Automated follow-up and native retest are recorded below. |
| All weapons stop reloading when looting | Exact native interaction code cancels pending reloads before ordinary pickup; the exemption is door-specific. All 21 new server checks pass: unrelated pickup preserves reload, explicit client interruption stops it. No supported server bypass established; see the [reload note](loot-reload-feedback-20260926.md). |
| Punching severely slows movement | Owner clarified that normal speed returns after the punch. Current fists omit the sprint-fire flag; exact client code has a corresponding sprint-stop path. Neutral movement multipliers do not establish another speed penalty. Original flags and the measured severity remain unknown; see the [punch trace](punch-movement-20260926.md). No gameplay values changed. |
| Cannot change outfits in this test account | The old test profile had no owned items or wardrobe selections. The owner subsequently selected their existing Community profile, and the main-local switch preserved its cosmetics and saved selections. No account migration or reset performed. |
| `/kill` stops healing because the player dies | Death observed. Application-bar removal and stale callback behavior were not separately confirmed; their automated lifecycle checks remain valid. |

## Seat correction

The client successfully sends the newly supported seven-byte `70/0a` request.
The server then emitted `70/0b` with its final fields in the wrong order.
The verified order is `u32 seat; u32 status; u32 isDriver`.
Dispatcher `0x140c9fee0` only invokes the seat consumer when status is `1`.
Consequently the old passenger response `[seat,0,1]` was discarded while the
server had already relinquished driving ownership. The corrected response is
`[seat,1,0]`. Returning to the driver uses `[0,1,1]`.

The [seat protocol note](vehicle-seat-request-20260926.md) records the exact
reader, dispatcher, consumer and attachment-setter evidence. Only the response
writer changes in this follow-up; existing ownership and motion policy remain.
Regression checks now pin distinct status and driver offsets, the passenger-to-
passenger and return-to-driver sequence, and the response sent to observers.

Automated follow-up: **624 passed, zero failed or skipped** in Release. The
filter covers all vehicle tests, inventory reload lifecycle, and the 21 new
loot/reload cases. The self-contained Windows host build also passed. Logs and
TRX are under `C:/Aug2017/out/compatibility-20260926/play-feedback-01`.
This is a focused follow-up to the historical full suite, not a new full-suite
result. Existing unrelated analyzer warnings remain.

The game had exited before the runtime update. The existing restoration host
was identified by its saved PID, path and start time, then stopped and replaced
with the new runtime. Accounts, configuration and client files were retained;
the previous runtime remains in
`local-host/runtime-before-seat-followup-20260926-162548`.
The updated `Cranberry.Zone.dll` SHA256 is
`333ebe2fe390f4da40d100e58e9f4751d1ebb30fa9b6352402d0fd52bd934d94`.
`local-host/seat-followup-runtime-update.json` records the update. The restarted
host passed checks for HTTPS `127.0.0.1:22040` and UDP `127.0.0.1:22042/22043`,
with no non-loopback listener. The read-only `/health` check returned the target
client version; the client executable hash remained unchanged. No game was
launched by the runtime update itself. The existing verify-only play helper was
then used to reopen the authenticated local game with the retained account and
client installation. It reported the target client running; seat/camera/sound
observations on the corrected runtime are still pending.

The protocol index was refreshed against the corrected source at
`play-feedback-01/zone-protocol-index.json`, SHA256
`84ad6d815fe153320b88f84aa759cc56e9a66d7e1f0a0d36939c2b812bb65bc2`.
Inventory counts are unchanged; the historical batch index remains intact.

Native retest of the corrected writer: the owner subsequently reported successful
changes mixed with intermittent refusals; the follow-up below separates those causes.
For that retest, park a car, switch from driver to a passenger seat, then another
passenger seat, then back to driver. Check seat/camera movement and restored
steering; report sound separately. Moving-seat policy remains unresolved.

## Local profile continuity

Read-only comparison found the likely usual local state at
`%LOCALAPPDATA%/CranberryCommunity`, last used on the same day through 13:24,
with HTTPS `127.0.0.1:50774`, Login UDP `63894` and Gateway UDP `63895`.
Its saved game path is `C:/Aug2017/Client`; it has 836 owned item entries and
14 saved outfit selections. The current restoration test account has zero of
each. Effective gameplay configuration comparison found no difference explaining
punching or reload interruption. Counts describe this inspection, not defaults.

The owner subsequently explicitly selected this existing Community profile for
all interactive testing. The [main local workflow](main-local-workflow-20260926.md)
records the completed switch, persistent launch entry point, unchanged saved
state and verified installed game. The old restoration host is stopped.

The corrected seat response worked on some attempts, but the owner reported
intermittent failures while repeatedly pressing the control. The follow-up log
contains 58 requests: 14 accepted, 44 refused as TooFast, none refused for
cooldown. The [seat note](vehicle-seat-request-20260926.md) records the mismatch
between server arrival-time displacement speed and the native speed check.
The native-speed correction is now implemented, passed the final full Release
suite (7,923 passed, 36 skipped, zero failed), and is installed on the main local
profile. The final parked-seat camera/steering check remains pending; the earlier
response-field fix alone did not close the complete seat action.
The owner authorized this completed source scope for GitHub. The
[publication record](compatibility-publication-20260926.md) distinguishes source
publication from a signed updater release or live deployment.
