# August zone protocol discovery

`gen-zone-opcodes.py` owns the generated constants and evidence-derived family
framing. `audit-zone-protocol.py` builds a separate, language-neutral JSON index
joining those records to lexical server references. It changes no generated
source or client files. Python 3.11 or newer is required.

From the repository root, using private exact-build evidence:

```powershell
py -3.12 server/tools/opcodes/audit-zone-protocol.py `
  --registrations C:/Aug2017/out/registrations-1148.json `
  --client-exe C:/Aug2017/Client/H1Z1.exe `
  --output C:/Aug2017/out/compatibility-20260926/zone-protocol-index.json
```

The executable SHA256 must match the pinned August target. The registration
SHA256 must match the provenance in `ZoneOpcodes.g.cs`, and generated base
identities must agree with the **one-level** registrations, matching the existing
generator. The output records input/source
hashes, every registration row and alias, the six existing framing records with
their native citations, and each `ZoneOpcodes.Symbol` source reference by path
and line. Re-run after relevant source changes; do not commit a stale generated
snapshot or proprietary input files.

Source is read as UTF-8. Any invalid comment bytes are preserved as explicit
byte escapes for lexical indexing, with the path and original file hash recorded
under `inputs.sourceDecodingNotes`; the tool does not guess a legacy encoding or
rewrite source files.

Registration depth is metadata, not a wire encoding. Even the first level of a
nested registration need not fit in one byte. `bases` contains only generated
bases and existing derived framing records; its rows are associated by first-level
equality, not by decoding a wire layout. `unmappedRegistrationGroups` preserves
all other rows and their original levels without masking or splitting them into
guessed opcodes. In the pinned input, these are 33 rows beginning with
1236, 1237 or 1321, and three Audio rows beginning with 220, which lacks a
one-level registration. These are metadata groups, not additional proven packets
or missing features.

A name or source reference does not establish
direction, August battle-royale applicability, full implementation or successful
gameplay. Literal opcodes and other routing styles are not indexed, so missing
references cannot identify missing features. The `ce` framing record is retained
from an existing native dispatcher derivation even though the base registration
inventory does not list it; numeric gaps are never used to invent messages.

Use this index to choose the next bounded native call path to investigate, then
update [the coverage matrix](../../docs/compatibility-status.md) with separately
reviewed RE, implementation and validation statuses. Counts describe discovered
metadata, not a percentage of the original game restored.
