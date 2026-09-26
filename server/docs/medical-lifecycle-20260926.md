# Medical lifecycle audit — 26 September 2026

Death now cancels an unfinished medical application immediately. Previously,
`KillPlayer` cleared active healing, bleeding, craft/shred and vehicle-removal
state but retained `PendingMedicalCast` and `ConsumeBusyUntil`. The old callback
eventually noticed death and sent an interaction stop during the results phase.

`ZoneService.Endgame.cs::KillPlayer` now calls the existing `CancelMedicalCast`
before body-bag transfer and the death burst. The unconsumed item follows the
existing body-bag path; cancellation releases its pending identity, so the queued
callback cannot spend it or send a later stop. No healing amount, bleeding rule,
duration, packet layout or original-server gameplay claim changes.

## Evidence and reviewed transitions

The exact August client has one subject-owned interaction timer; `cf03` contains
no cast identifier. Its handler `0x140ccff80` clears that current timer, rather
than a particular historical cast. See [the native trace and identity record](interaction-ownership-20260926.md).
This supports finishing obsolete application presentation when death occurs.
Active recovery tags/modifiers are removed by instance through `9e08`; see
[the medical codec and native UI record](native-medical-presentation-20260926.md).

| Transition | Source finding |
| --- | --- |
| Death | Active healing and bleeding were already cleared. Unfinished application cancellation was missing and is now added. |
| Lobby/new drop vitals reset | `ResetPlayerVitals` cancels application and vehicle-removal state, clears healing/bleeding, advances the vitals generation, then restores maximum health. |
| World exit | `AbandonMatch` cancels application silently, clears native healing state and bleeding, and invalidates the old world. |
| World/inventory replacement | Application and recovery callbacks verify their captured world/inventory. Recovery also verifies vitals/HUD generations; later phase reset clears old bleeding state. |
| Disconnect/reconnect | `Later` drops work on a closed transport. Replacement admission closes the old transport before creating the new session state; only wardrobe selection is carried over. Medical state is not resumed. |

No additional cross-context health mutation was found by this bounded source
review. It is not a claim that every possible medical transition is validated.
The replacement-session behavior is documented separately in
[the reconnect ownership record](reconnect-ownership-20260926.md).

## Validation status and remaining leads

**Implemented; locally passing** in the final Release suite. The
[batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns exact totals and artifacts. Native gameplay repetitions for this batch: zero;
the earlier research-client medical acceptance remains a separate historical result.
`MedicalLifecycleTests.cs` contributes six passing cases: three medical types through
actual `KillPlayer` with a held application callback and body-bag retention;
active healing and a production wound tick through death, vitals reset and world
exit, verifying native effect removal and stale callback silence.
Reproduction filter: `FullyQualifiedName~MedicalLifecycle_`. The existing medical,
bleeding and reconnect suites are included in the passing final solution run.

The native projected recovery ages on an absolute synchronized clock, while the
authoritative one-second heal chain schedules its next tick after the current
listener callback runs. A sufficiently delayed listener could therefore finish
authoritative healing later than the initially advertised duration. This is a
source-supported timing lead, not an observed local failure or a recovered
original stall/catch-up policy; no retiming was introduced. Continuing recovery
during victory/results also remains existing policy, with original behavior
unknown. Accepted medical values are preserved.
