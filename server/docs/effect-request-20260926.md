# Complete August runtime-effect requests

The `9e01`/`9e03` action parser accepted a 30-byte shortcut containing only the
head and two GUIDs. It also accepted a full Add prefix whose required property
lists were missing. These malformed requests could reach existing turbo,
MotorRun and punch-animation state changes. This increment requires the complete
August bodies before dispatch. It does not change effect IDs, gameplay amounts,
engine policy, the removal echo, or composite-effect writer bytes.

## Evidence and limits

Target: August 5, 2017, Steam 433850/depot 433851/manifest
6373368576374184611. Executable SHA256:
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
Addresses below are VA at image base `0x140000000`.

Read-only Binary Ninja exports and their hashes are recorded in
`C:/Aug2017/out/compatibility-20260926/effect-request/evidence-index.json`, SHA256
`9862BDA76CDD31C03D49193B9F9AE3CB5F1A97477AB4F24C1458B0D6D66BC5BA`.
The five property readers were exported fresh from the completed August database;
all decompilations were available, complete, and reported `needsUpdate: false`.
The index also references this batch's native Add/Remove exports and the shared
native string reader exported during the item-use investigation.

These client readers establish the field widths, order and acceptance behavior.
The repository already has a locally recorded August Add fixture using the
distinct source/target offsets. Neither that local fixture nor this server's
regressions prove original retail server policy. Ancillary GUIDs, vector meaning,
and property meanings unused by the server remain unresolved.

## Wire contract

All fields are little-endian. Offsets include the two-byte family/subtype header.
Both bodies start `u8 0x9e; u8 subtype; u32; u32 clientEffect; u32 serverEffect`.

| Form | Reader | Body after the common 14-byte head |
|---|---|---|
| Add `01` | `0x140ce9e70` | u32 actor kind; u64 source (+18); u32 transient; u64 ancillary; u64 target (+38); u64 ancillary; float4; u8 simple flag (+70) |
| Remove `03` | `0x140cea390` | u64 source (+14); u64 target (+22); u64 ancillary; float4 |

Add requires at least 71 bytes. Any nonzero simple flag means no property lists.
A zero flag requires these five lists in order, so even five empty lists require
91 bytes total. Remove requires 54 bytes. Neither reader requires exact input
exhaustion; the server continues to tolerate trailing bytes.

Each list begins with a signed i32 count. Native loops run only for positive
counts; zero and negative counts represent empty lists.

| List | Reader | Entry wire fields |
|---|---|---|
| 1 | `0x140caa4e0` | u32 property ID + 4 raw bytes |
| 2 | `0x140caa680` | u32 property ID + 4 raw bytes |
| 3 | `0x140caad30` | u32 property ID + 8 raw bytes |
| 4 | `0x140caa9f0` | u32 property ID + 16 raw bytes |
| 5 | `0x140caa820` -> `0x140b78f60` | u32 property ID + signed i32 byte length + that many string bytes |

The string reader rejects negative lengths and incomplete data. The server
bounds counts by bytes present before multiplying or looping, skips these unused
values without allocating them, and returns no request on any incomplete field.
Update `02` and other subtypes have no implemented action in this parser and are
now refused; their native layouts and possible gameplay purpose are not inferred.

## Source gap and correction

`Vehicles/VehicleBoost.cs::EffectRequest.TryParse` formerly reinterpreted short
Add bodies as Remove-shaped GUIDs and never consumed Add properties. Authenticated
source/driver checks still applied, but a synthetic compact Add could start the
sender's turbo/engine or update an eligible punch-animation report. A 30–53-byte
Remove could release an existing turbo or stop the sender's engine. A normal
truncated Add was often rejected later because its misread GUID did not match;
that incidental rejection did not validate its wire shape.

The only active parser caller is `ZoneService.Vehicles.cs::HandleEffects`, which
also routes punch reports to `ZoneService.Destructibles.cs`. No production caller
requires the shortcut. The shared test Add helper in
`VehicleDamageIntegrationTests.cs` used it in eight existing tests and now emits
a complete synthetic August frame. The prior packet test endorsing a 30-byte Add
now asserts refusal. Complete existing 54-byte Remove fixtures, native-shaped
MotorRun/punch fixtures, and the new replication fixtures retain their layout.

Composite add/remove writer comments now cite the exact August readers
`0x140c24430` -> `0x140c20cb0` (38 bytes) and `0x140c24e90` (18 bytes), rather than
attributing field widths to another version. Their bytes remain unchanged; see
[the turbo replication note](vehicle-boost-replication-20260926.md).

## Validation status

**Implemented; locally passing** in the final Release suite. The
[batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns exact totals and artifacts. Native gameplay repetitions for this batch: zero.
Source review by a second agent found no parser blocker.

`EffectRequestCodecTests` contributes 23 passing cases covering complete frames, every shorter
prefix, all property lists, signed counts, string bounds, nonzero flags, inactive
subtypes and trailing-byte tolerance. `EffectRequestGatewayTests` contributes 13 passing cases:
malformed Add cannot mutate turbo/MotorRun/punch state; truncated Remove cannot
release turbo/MotorRun; a subsequent complete request still works. These are
synthetic requests through the real in-memory gateway, not native captures.

Reproduction filter: `FullyQualifiedName~EffectRequestCodecTests|FullyQualifiedName~EffectsGateway_|FullyQualifiedName~VehicleBoostPacketTests`.
The passing final suite also includes the eight corrected helper users and
existing vehicle/animation/destructible regressions. Native confirmation remains
separate: local repeated boost/release, engine toggling and punching must still
work with the unchanged August client.
