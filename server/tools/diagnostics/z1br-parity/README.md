# Offline 1315 parity evidence

These tools read the owner's immutable September 12 capture. They make no
network connections to the captured service and make no server/client changes.
Credentials and generic decrypted messages stay in memory. Only static public
definition slices, synthetic identities/movement and sanitized evidence leave the
reader. Reports default to the shared team root `reports/evidence`.

Run with Python 3.12 from the evidence workspace:

```
python -B tools/diagnostics/z1br-parity/audit_capture.py
python -B tools/diagnostics/z1br-parity/event_index.py
python -B tools/diagnostics/z1br-parity/field_comparison.py
python -B -m unittest discover -s tools/diagnostics/z1br-parity -p test_*.py -v
```

The full audit requires the retained capture, Wireshark/tshark, reviewed scripts
and prior ROTK definition body at their documented local paths. Fixture tests
need only this directory. The field comparison also requires the weapons owner's
`reports/weapons/table-audit/prospective/effective.json`, whose exact file, table
and configuration hashes are recorded in the output.
`make_fixtures.py` regenerates the small public definition subset (nonempty rows
in all eight lists), 34 wholly synthetic movement records and five event shapes
with synthetic identities. It never exports
player coordinates, identities, session keys or tickets.

`capture_source.py` reuses the reviewed transport audit with its single report
write removed from an in-memory AST. The historical source is not edited. It
verifies the capture SHA, all existing protected CRC checks and an additional
443 raw movement CRCs, stripping those two-byte trailers before parsing.

`weapon_1315.py` consumes eight lists and verifies key/body agreement, unique
keys, bounds and joins. List 2 contains fixed spans of **unknown semantics** and
two explicit counted nine-byte scalar/scalar/byte arrays. Lists 3/6 differ from
1148. Full structural consumption is not a complete named fire-mode schema.

`movement_1315.py` consumes normalized gateway channel-2 messages in either
direction. The current server prefix is a fixed eight-byte identity. Numeric
field order and widths are established across the captured session; units,
body/look ownership and individual stance/input bit meanings are not. Bits
0x1000 and 0x4000 only co-occur in full masks, so the 11/1 split around the
independently observed three-component 0x2000 field remains an inferred grouping.

`events_1315.py` checks nested weapon envelopes, fire, bounce-shaped notices,
wall replacement and vehicle appearance shapes. `event_index.py` joins private
identities/projectiles in memory and emits anonymous labels, offsets and frame
references around six visual anchors. UTC/video navigation uses segment CSV
origins plus invocation time; its two-second search padding is not a measured
alignment error bound. Audio is late and the initial pickup sound is absent.

`field_comparison.py` records 16 explicitly inferred historical column
correspondences and keeps unnamed current recoil scalars separate from named
final August fields. Neither historical signature agreement nor group order
proves the current native consumer or active mode. Recovery, inheritance, audio
and optic-stage semantics remain bounded follow-ups in the evidence handoff.

The seven fixture tests reject every truncated prefix of the table, movement
and event shapes; invalid counts, joins, duplicate keys, direction, trailing
data and excessive nested weapon framing are also covered. These are offline
framing checks, not native gameplay acceptance.

These readers target source protocol **1315**. They are behavioral evidence for
target **1148**, never replay packet writers. Arrival intervals do not measure
simulation tick, input latency, one-way transit or native frame pacing.
