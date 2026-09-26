# Native seat-change request — 26 September 2026

The server expected a guessed 14-byte `70/0a` request containing a vehicle GUID
and seat. The August client's actual serializer writes **seven bytes: `70 0a`,
a 32-bit seat index, and a one-byte mode**. Every normal seven-byte request was
therefore rejected before reaching the server's existing seat-change logic.
Earlier comments claiming this serializer could not be recovered were incorrect.

The first native playtest then exposed a second error: the server accepted seat
changes and removed driving control, but the rider and camera stayed in the
driver seat. The response writer had **status and driver fields reversed**.
Passenger responses failed the client's success gate and never reached its
seat-change consumer. The corrected writer now sends seat, status, then driver.

## Exact-build evidence

Target: app 433850, depot 433851, manifest 6373368576374184611,
ClientProtocol_1148, build 0.0.118.208059. Executable SHA256:
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
Addresses are VAs; subtract image base `0x140000000` for RVAs.

| Path | Address | Observation |
|---|---|---|
| Native control input | `0x14158ef20` | The `ChangeSeat%d` key path at `0x141592513` passes zero-based seat and mode0 at `0x1415926a5`. Two further callsites are `0x1415908fc` (mode1) and `0x141590b45` (mode0). |
| Seat request constructor | `0x141594e10`, through thunk `0x1400e60ce` | Sets base `0x70`, subtype `0x0a`, record+`0x18` to requested seat, record+`0x1c` to the third control argument. Sends through `0x140e84eb0`. |
| Packet send wrapper | `0x140e84eb0` | Calls `0x140e84fc0`, then transmits the serialized buffer through `0x140dcdb90`. |
| Serializer | `0x140e84fc0` | Calls base writer `0x140a27a90`, then writes one-byte subtype, four-byte seat, one-byte mode. Disassembly independently confirms the four-byte store for seat despite misleading local-variable typing in Pseudo C. |
| Response reader | `0x140c9e360` | Confirms existing `70/0b` response framing: rider, mount, identity record and three 32-bit values. An empty identity gives the server's existing 66-byte form. |
| Response dispatcher | `0x140c9fee0` (RVA `0xc9fee0`) | Reads `70/0b`, resolves the mount GUID, then requires record+`0x144` (wire+58 with empty identity) to equal `1` before calling the consumer. |
| Seat-change consumer | `0x140c3b4f0` (RVA `0xc3b4f0`) | Receives record+`0x140` as the requested seat and record+`0x148` as its driver flag. Updates vehicle occupant/driver state and invokes the rider attachment and local view-state paths. |
| Rider attachment setter | `0x140c72ef0` (RVA `0xc72ef0`) | Logs `RiderChangeSeat`, replaces the rider's stored seat index at mount-state+`0x0c`, and updates its seat-definition pointer at+`0x10`. |

Binary Ninja GUI MCP supplied fresh, read-only function exports from the supplied
database; no save or analysis update was issued. Its current Pseudo C for the
large input function does not expose the seat calls correctly, so the bounded
callsite evidence uses Capstone on the pinned executable's `.pdata` function
bytes. This is static disassembly, not client execution or injected code.
The small constructor and serializer exports are complete and report
`needsUpdate: false`.

Private evidence and reproduction jobs:
`C:/Aug2017/out/compatibility-20260926/vehicle-seats`.
`seat-control-evidence.py` verifies the executable hash before exporting the
bounded assembly; `queries.py` and numbered JSON files repeat the native reads.
The evidence index SHA256 is
`35492a081a68a1d5ca39e58af04cee7f8428b7d5365583bcc2cf76ce08c88e0`.
No other-version captures were used.

The follow-up's complete, fresh `playtest-seat-response-{reader,dispatch,consumer}`
and `playtest-seat-attachment-setter` exports report `needsUpdate: false` and no
truncation. `09-playtest-response.json` and `10-playtest-attachment.json` are
reproduction jobs for `queries.py`; `playtest-evidence-index.json` records their
hashes separately from the original request investigation. Its SHA256 is
`4c5c92e65567afd23cd3acd17bb869d6df291dce73c202bb9b18a6cf14c7efeb`.

## Protocol and implementation

Direction: client → server, zone payload after the gateway tunnel wrapper.
All multibyte fields are little-endian.

| Offset | Type | Field |
|---|---|---|
| 0 | u8 | `0x70` Mount family |
| 1 | u8 | `0x0a` SeatChangeRequest |
| 2 | u32 | Zero-based requested seat |
| 6 | u8 | Native control mode |

`SeatChangeRequest` now reads this layout, and the existing gateway handler finds
the vehicle by authenticated occupant. The request cannot nominate another
vehicle. Truncated requests are ignored before any seat, ownership or cooldown
change. The parser retains additional bytes in `Raw`; a stricter trailing-byte
policy has not been inferred from the sender alone.

The server-to-client `70/0b` response uses this tail when its identity record is
empty (36 bytes), as in the existing server writer:

| Offset | Type | Field |
|---|---|---|
| 2 | u64 | Rider GUID |
| 10 | u64 | Mount GUID |
| 18 | 36-byte record | Empty identity |
| 54 | u32 | New zero-based seat |
| 58 | u32 | Status; the native dispatcher proceeds only for `1` |
| 62 | u32 | Driver flag, `1` for driver and `0` for passenger |

The earlier writer emitted `[seat, isDriver, status=1]`. This happened to work
for driver responses, where both final values are `1`. A passenger response
`[seat,0,1]` was ignored. The corrected form is `[seat,1,0]`; both the requesting
player and observers use the same writer. No ownership or packet-order change
is required for this correction. Other status values and failure UI remain
unmapped. Sound and camera results still require native playtest confirmation.

Seat occupancy and cooldown checks remain. Native mode 0 checks squared
movement-vector magnitude against 1.0; mode 1 bypasses that particular client
check. The follow-up below now uses the accepted native speed magnitude for
the normal threshold instead of requiring a zero arrival-time pose estimate.
Mode 1 is still subject to the normal server guard; its original server policy
and complete loop-seat behavior remain unknown.

Source scope: `Vehicles/VehicleControlPackets.cs`, the seat handler in
`ZoneService.Vehicles.cs`, and corrected stale documentation in
`Vehicles/VehicleState.cs`. Existing seat fixtures now emit the native layout.
New `NativeSeatChangeRequestTests.cs` covers codec boundaries, malformed-then-valid
gateway requests, an unmounted sender and retained motion refusal.

## Validation

The seven-byte request initially passed the final Release suite, but the user's
native playtest failed the seat presentation described above. Those tests did
not pin the response status/driver ordering and were insufficient to validate
the complete action. The [batch validation record](inventory-persistence-batch-20260926.md#validation-record)
retains the historical run.

The response correction and new regression checks passed the follow-up Release
run: **624 passed, zero failed or skipped**, covering the vehicle namespace,
inventory reload lifecycle and new loot/reload cases. Results are
`C:/Aug2017/out/compatibility-20260926/play-feedback-01/results/seat-reload.trx`.
The host was rebuilt and installed in the existing restoration test runtime;
its loopback startup check passed. The next native playtest reported successful
changes mixed with intermittent refusal; see the diagnosis below.
Tests now pin the distinct
status and driver offsets for passenger changes, return to driver and observer
replication. They model the verified client layout and server transitions;
they are not original server captures.

The passing suite also includes `VehicleDamageIntegrationTests`,
`VehicleSkinTransitionTests`, `VehicleControlPacketTests` and `VehicleFleetTests`.
For the remaining native check, use the [selected main local setup](main-local-workflow-20260926.md), enter a parked car, switch to a passenger seat
with the configured seat key, and switch back to the driver. Confirm the seat
HUD, camera, steering ownership and appearance; repeat with another occupant
when two local clients are available. Moving and loop-seat parity remain separate.

The [publication record](compatibility-publication-20260926.md) tracks the
authorized source scope; a signed player release is separate.

## Intermittent refusals after the response correction

The local `console-20260926-162550-540.out.log` contains **58 seat requests:
14 accepted and 44 refused as `TooFast`**, with no seat cooldown refusals.
They span 16:31:49.911–16:33:21.660. For example, requests for seats 1 and 2 succeed,
then requests for seats 3/4 and back to the driver are repeatedly refused. These
are real inbound native seven-byte mode 0 requests, not merely inferred keypresses.
The log does not record the rejected speed values, and the corresponding wire
capture is empty. It cannot establish how many additional keys the client ignored.

The server's `LastSpeed` is horizontal displacement between accepted positions
divided by **server arrival time**, retained until another pose or explicit stop.
The previous `TryChangeSeat` guard refused any positive value. Native `0x141594e10` permits
mode 0 when its movement-vector magnitude is at most 1. That mismatch can reject
requests the client permits; the log establishes that the current guard caused
all recorded failures, but does not quantify tiny motion versus arrival jitter.

Fresh `0x140c56f20` confirms independent interaction and seat cooldown checks
using keys `0x383846b9` and `0x12d72c00`. The existing server supplies 1,000 and 250 ms,
respectively. There is no evidence to remove those checks. Fresh `0x142339800`
computes the linear-velocity vector's magnitude and stores it in MotionRecord+
`0x15c`; `0x1423395e0` reconstructs the vector using that magnitude. This is a
stronger candidate for seat validation than arrival-time position differences.

Private evidence is in the same `vehicle-seats` folder:
`intermittent-seat-log-summary.json` preserves sanitized timestamp/line/outcome
references, and the `intermittent-*` exports plus jobs 11/12 preserve the native
reads. No runtime edits, builds, tests or launches were performed for this
follow-up diagnosis. The subsequent [main local switch](main-local-workflow-20260926.md)
is complete; the native-speed guard correction is the separate increment below.

## Native speed correction

The fresh, complete `seat-speed-native-reader.txt` export closes the wire mapping:
`0x140a3ca40` (RVA `0xa3ca40`) tests movement mask `0x0010`, reads a signed packed
integer through `0x140a18f40`, multiplies by `0.1` and stores it at MotionRecord+
`0x15c`. `0x142339800` (RVA `0x2339800`) sets that field to the square root of the
linear-velocity vector's squared components. It is a magnitude, despite the
existing C# name `HorizontalSpeed`; the field is quantized in tenths.
`0x141594e10`'s normal seat gate compares that vector's squared magnitude to 1.
Applying that threshold to the quantized wire sample is restoration enforcement
aligned with the native client gate. It does not establish the original server's
rounding tolerance or an exact unquantized boundary near 1.

The stop flag is separately verified: `0x1423391e0` (RVA `0x23391e0`) names
MotionRecord flags+`0x118` bit `0x40` **STOP FLAG**. The sparse-record merge
`0x142335730` (RVA `0x2335730`) applies incoming fields, then clears both linear
and angular velocity vectors when that flag is set, including when the speed
field is absent. Its zero-vector constant at `0x143f52c70` contains 16 zero bytes.
The vehicle position handler `0x140b14d30` calls this merge at `0x140b15061`
through thunk `0x140094d55`. This is a MotionRecord flag, not an infantry posture
inference despite the historical C# field name `Posture`.

Implemented server behavior:

- Retain the magnitude in `VehicleSeatMotion` only after the current driver or
  coasting simulator's movement and any included pose pass authorization and
  validation. Passenger, former-simulator and rejected-position packets do not
  update it.
- Permit normal seat changes at a retained magnitude from 0 through 1; reject
  larger, negative or nonfinite values. Sparse packets retain the value; an
  explicit stop without a fresh magnitude records zero. A contradictory fresh
  nonzero or invalid magnitude cannot be hidden by the stop flag.
- Clear the value on movement-version change, effective simulator change or
  release, and wreck. Keep it when the same simulator moves between the driver
  seat and coasting control, including a return to the wheel.
- Until an initial native magnitude exists, preserve the conservative finite-zero
  fallback. This is restoration policy, not a newly proven original server rule.
  Cooldown, seat occupancy and the separate `LastSpeed`/dismount policy remain.

Seat logs now include native magnitude, invalid-value state and pose-derived
speed so another refusal can be explained. Source scope is
`Vehicles/VehicleSeatMotion.cs`, the vehicle state/seat guard, accepted managed
movement in `ZoneService.cs`, and seat diagnostics in `ZoneService.Vehicles.cs`.
`VehicleSeatSpeedTests.cs` adds boundary, malformed-state, sparse/version/stop,
arrival-jitter and simulator-lifecycle regressions. No new client packets,
client changes or hidden original-server timing rules are introduced.

This correction is **implemented and locally passing**: the Release vehicle
suite passed **620 tests, zero failed or skipped**, including all 17 new speed
and lifecycle cases. Results are
`C:/Aug2017/out/compatibility-20260926/seat-speed-01/vehicle-results/vehicles.trx`.
The earlier 624-test run predates this increment. The final full Release solution
passed 7,923 tests, with 36 skipped and zero failures. The resulting package passed
all 22 startup/persistence checks and is installed on the existing main local
profile, with verified loopback endpoints and unchanged saved state. Repeat native
confirmation is pending; this validation does not claim successful in-game seat
changes. The [publication record](compatibility-publication-20260926.md) and
[main local workflow](main-local-workflow-20260926.md) retain the operational details.

Private evidence remains in the same `vehicle-seats` folder. Job 13 reproduces
the native reader query; `seat-speed-evidence-index.json` hashes the relevant
request, reader, magnitude-conversion and cooldown exports; its SHA256 is
`2ed9134f2229aca1d2b8e845f26cf8e547d79447ea924811417ee18a6d2ac85b`.
Jobs 14/15 reproduce the stop-merge, debug-name, callers and zero-vector reads;
`seat-speed-stop-evidence-index.json` hashes those separate exports, with SHA256
`c4e6ca2f04ff7b26d34f07d4936f6fa335099b99706febe0d4791b352f5f1b02`.
All inspected
functions are from the pinned August binary and were read without saving or
updating analysis. Mode 1's original server policy remains unknown.
