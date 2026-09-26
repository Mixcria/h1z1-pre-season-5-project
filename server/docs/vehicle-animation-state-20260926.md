# Vehicle animation state, August 2017

`88 03 Vehicle.StateData` is bidirectional control/animation replication. The
old handler discarded it as one-way telemetry. The target client sends changed
values and consumes received values for remote vehicles; its full `0xdb` record
contains the same state body. This establishes a missing replication path,
not the original server's acceptance or timing policy.

Evidence is the unchanged August executable SHA-256
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`,
72,818,304 bytes, image base `0x140000000`, protocol `ClientProtocol_1148`.
Fresh read-only Binary Ninja GUI MCP exports are private under
`C:\Aug2017\out\compatibility-20260926\vehicle-state`. Numbered query files and
`queries.py` reproduce them; `evidence-index.json` records export hashes.
The database was not saved or reanalysed. No original server capture is used.

## Wire format and native paths

All multi-byte fields are little endian.

| Field | Encoding | Meaning established here |
| --- | --- | --- |
| Opcode | `u8 0x88; u8 0x03` | Vehicle state |
| Vehicle identity | `u64` | Resolved entity GUID |
| Value | `u32` | Opaque; native sender writes zero |
| A count/entries | `i32 count; {u32 index; u8 value}` | Boolean state indices 0..29, values 0/1 |
| B count/entries | `i32 count; {u32 index; u8 rawValue}` | Float-state indices 0..14, signed byte hundredths |

Minimum size is 22 bytes; each entry adds five. The shared full-record body
starts at Value and therefore has 12 bytes before entries.

| Function VA (RVA) | Evidence |
| --- | --- |
| `0x140e7f6f0` (`0xe7f6f0`) | Sender runs after elapsed time exceeds 500 ms, checks local simulation control, sends pending maps with Value=0, then clears pending maps |
| `0x140e5d5c0` / `0x140e5d510` (`0xe5d5c0` / `0xe5d510`) | Packet/body serializers |
| `0x140c95340` / `0x140c8cc70` (`0xc95340` / `0xc8cc70`) | Receive dispatcher and reader |
| `0x140a511c0` / `0x140a4cd00` (`0xa511c0` / `0xa4cd00`) | A/B readers: signed counts and five-byte elements |
| `0x140c9b1b0` (`0xc9b1b0`) | Resolves GUID; passes state to local rider callback and remote vehicle consumer |
| `0x140e634e0` (`0xe634e0`) | Saves prior floats, records local receipt time, replaces queued frame, and copies state |
| `0x140e70e40` / `0x140e70f20` (`0xe70e40` / `0xe70f20`) | Remote tick and received-state application |
| `0x140e812f0` / `0x140e815c0` (`0xe812f0` / `0xe815c0`) | Boolean/float setters and pending-delta construction |
| `0x140a2f2f0` / `0x140b02e00` (`0xa2f2f0` / `0xb02e00`) | Full `0xdb` reads the same body and passes it to `0x140e634e0` |

Native list readers iterate only for positive counts; nonpositive counts are
empty. They set a stream error on missing count/key/value bytes. The vehicle
dispatcher checks that error but does not establish an exhaustion requirement.
The reader permits duplicate keys; its linked list preserves input order and
the consumer applies each entry in that order. The ordinary sender updates
unique keyed entries instead.

## Meaning, ownership, and lifecycle

A values update a bitset at vehicle+`0x45e8` and dispatch an animation event
through virtual `+0xc8`. The event handle comes from table `0x143fa6050` at
`2*index+value`. Initializer `0x1403168e0` names index 3's true event `Turbo`.
Other named entries include flight/cargo behavior, so their presence does not
establish active Battle Royale features. These events are separate from the
character effect-tag add/remove packets.

B values update vehicle+`0x45f0 + 4*index` and dispatch a float parameter through
virtual `+0xe0`. Initializer `0x140316f50` names indices 0..8 `Throttle`, `Strafe`,
`Turn`, `Climb`, `Roll`, `DamagePercent`, `VTOL`, `TurretYaw`, `TurretPitch`;
indices 9..14 have no named event handle there. Physics paths `0x140e7a090` and
`0x140e7b5a0` produce state. Names alone do not prove every index is networked
or active for every car.

The B sender truncates `clamp(float*100, -100, 100)` to a signed byte
(`0x140e81694` / `0x140e816d3`). The receiver sign-extends it at `0x140e71a24`,
multiplies by 0.01, and interpolates from the prior received state over 500 ms.
Its interpolation clock is the client's **local receipt time**, not Value.
Queued frames are sorted by Value, then all entries are applied without a
Value-zero filter. The ordinary packet path replaces the queue with one frame,
so forwarding the native zero does not require an invented server timestamp.
The original purpose of Value remains unknown.

Native setters reject indices above 29/14 using unsigned comparisons
(`0x140e8131f`, `0x140e815c3`). However, the B consumer reads an indexed prior
float at `0x140e71a32` before calling its guarded setter. A values also directly
index the event table. A server must validate these fields before relaying.

Simulation control, rather than seat occupancy alone, gates the sender. The
shared check `0x140e84740` recognizes the managed-control interface or matching
local owner GUID. Locally controlled vehicles suppress received interpolation.
This supports accepting updates from the server's current authorized simulator,
including its existing coasting lease, rather than every nearby player or only
an occupied driver. Exact original lease policy is not recovered here.

The sender clears **pending changes** after each send opportunity. Actual
bit/float state persists; omitted keys do not reset. Accepted deltas therefore
merge for late-viewer full snapshots. No native evidence here establishes a
blanket zero/reset on seat change or a timeout that erases these values. Server
storage should follow the vehicle object's lifetime and reject stale reporters
after ownership changes. These maps must not set authoritative damage, fuel,
installed parts, or boost permission merely because an animation has that name.

Bounded model/control reads `0x140c70720` and `0x140ab20c0` do not directly reset
these fields, but invoke further model/virtual callbacks. Their transitive
effect on existing animation state remains unknown. Clearing the server cache
on wreck prevents stale driving snapshots reaching later viewers; it does not
establish that every existing client animation resets on the model transition.

## Server work and validation

`VehicleStateData.TryParse` and `VehicleAnimationSnapshot` implement bounded
parsing and immutable delta merging. `WriteBodyTo` shares the exact body with
full vehicle records. The codec preserves opaque Value, signed raw bytes,
native nonpositive-count behavior, and acceptance of unknown trailers; relay
serialization drops trailers. It rejects duplicate keys, counts above the
30/15 native producer bounds, out-of-range indices, nonboolean A values, and
B bytes outside signed -100..100. Duplicate/count rejection is a deliberate
server validation rule based on the ordinary sender, not a claim that the
native reader or original server rejected them.

`ZoneService.VehicleAnimation.cs` accepts reports only from the current fleet
simulator with its managed-object registration, restricts incoming Value to
the native sender's zero, merges the snapshot, and relays to current observers.
`VehicleFullState` / `LightweightToFullVehicle` include that snapshot for later
viewers. Simulation release restores retained state after revoking local
control. Wreck handling clears the server cache without inventing an all-zero
native event sequence. Automated checks pass; native presentation remains to be
observed on the isolated candidate.

The passing final Release suite includes 26 codec/snapshot cases and 13 gateway
cases covering truncation, ranges, duplicates, negative counts, signed values,
shared full-state layout, immutable ownership, atomic merging, observer/late-viewer
replication, release ordering and wreck cleanup. The
[batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns exact totals and artifacts.

Reverse engineering: wire/producer/consumer path verified; original server
rules and Value purpose partial. Implementation: implemented.
Validation: **locally passing**; native gameplay repetitions for this batch: zero.
No original-server balance or native visual parity is established by these tests.
