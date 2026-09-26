# Vehicle turbo observer presentation

The server sent its accepted turbo composite effect only to the driver. Existing
vehicle horn, headlights and siren already used viewer replication, but turbo
did not use that path and was absent from late stream-in state. This batch sends
accepted turbo tag transitions to riders and viewers holding the vehicle, and
retains the current effect owner for later stream-in. Fuel costs, engine rules,
boost magnitude and the configurable local turbo byte are unchanged.

## Exact-build evidence

Target executable: `C:/Aug2017/Client/H1Z1.exe`, SHA256
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
Addresses are VA at image base `0x140000000`. Read-only Binary Ninja queries,
exports, selected data rows and hashes are retained in
`C:/Aug2017/out/compatibility-20260926/vehicle-boost/evidence-index.json`.

`ClientEffects.txt` names four Turbo rows: client effects 90000/90068/90069/90193,
with active composites 5016/319/279/354 for offroader/pickup/police/ATV. The server
already uses these exact values. This trace does not infer original boost balance.

The Character dispatcher `140af9ca0` resolves the subject GUID, then invokes its
actor handler. Handler `140c4d240` accepts the composite add/remove subtypes.
Binary Ninja could not produce this large handler's decompilation; its actual
assembly establishes calls at `140c4fa48` and `140c4faf6` to the exact readers,
and virtual add/remove calls at `140c4faa2` and `140c4fb14`. The readers' fresh
decompilations were available:

| Message | Exact reader | Little-endian wire layout |
|---|---|---|
| `0f15` add composite tag | `140c24430` -> `140c20cb0`, common head `140a2cc00` | u8 family, u8 subtype, u64 subject, u32 tag key, u32 effect, u64, u64, u32; 38 bytes |
| `0f16` remove composite tag | `140c24e90` | u8 family, u8 subtype, u64 subject, u32 tag key, u32 replacement; 18 bytes |

The existing writers use the composite ID for the tag key and zero replacement.
Their bytes are unchanged. Other unused fields retain existing values; this
increment does not claim their complete semantics. Both reader wrappers require
the complete frame. This closes the former reliance on another version for the
field widths and order.

`0f33 Character.Turbo` differs: `140af9ca0` acts on the receiving client's local
player regardless of the packet GUID. It must remain sender-only. The existing
`9e03` runtime-removal echo also remains sender-only. `88/03` vehicle animation
state is a separate native mechanism; a named Turbo animation event does not
replace the actor's composite-effect tag.

## Implementation and lifecycle

`MatchVehicle.BoostingCharacterGuid` records accepted presentation ownership.
Duplicate presses do not add another composite. A release still acknowledges
the sender's local effect, but a former driver's delayed release cannot remove
a later driver's shared tag. The request's source must match the authenticated
player, and accepted activation requires current driver/control ownership.

Existing engine-off, fuel exhaustion, component removal, dismount, driver-seat
loss and disconnect paths use `ReleaseBoost`; their tag removal now reaches
viewers. Wreck handling also clears retained turbo state, removes the tag for
affected viewers, and clears the former booster's local turbo state before death
handling. Late stream-in sends the current tag only after the vehicle actor and
full data. State is scoped to the existing match vehicle and never persisted.

## Validation

**Implemented; locally passing.** Ten gateway regression cases passed in the final
Release suite, covering normal/late viewers, ownership changes, duplicate requests
and cleanup. The [batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns exact totals and artifacts. Native gameplay repetitions for this batch: zero.
Native confirmation needs
two isolated local clients: watch turbo begin/end, approach an already boosting
vehicle, then check seat change, departure and destruction. The exact original
server's interest policy and effect timing remain unknown.
