# Interaction timer ownership — August 2017

## Client evidence

Target: Steam 433850 / depot 433851 / manifest 6373368576374184611,
`ClientProtocol_1148`. Analyzed executable SHA-256:
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`;
image base `0x140000000`. Addresses below are virtual addresses.

The September 26 read-only Binary Ninja trace establishes this active path:

| Function | Observed behavior |
| --- | --- |
| `140af3950`, cf case `140af64dc` | Main dispatcher routes family cf to `140ccfc70`. |
| `140ccfc70` → `140cd07c0` | Subtypes 2 and 3 read the subject GUID. A local subject reaches `140ccfd50` / `140ccff80`; other resolved subjects reach `141477df0`. |
| `140ccfd50` → `140a30370`, `1422a2830` | Start decodes and replaces the one current record at manager+8. |
| `140cd0d90` | Publishes the label and duration to `UpdateCharacterStateTimerDataSource`; zero duration publishes an empty label and zero. |
| `140ccff80` → `1422a28e0`, `1421e7290` | Stop reads only the two one-byte header fields and u64 subject, then clears the same current record and republishes it. No cast ID or cast-kind selector is read. |

Private reproducible exports: `C:\Aug2017\out\compatibility-20260926\interaction-state`.
`findings.md` describes the dispatch table calculation; `evidence-index.json`
lists the raw exports and hashes. Index SHA-256:
`7054fb4b6a8cec370dbabea6e84dbceb190ed0c512555ab4a9e866752bb60022`.
These local files are references, not files included in a player package.

The empty-string start reader consumes **66 bytes**: header/subject (10), two
u32 fields, two u64 fields, three u32 fields, four further u32 fields, and a
signed 32-bit string length. The existing server writer sends **67 bytes**, with
one trailing zero beyond that empty string. No strict exhaustion check appears
in the observed handler. This change preserves those working bytes. The prior
comment describing two strings was not supported by this native reader.
The stop reader consumes exactly 10 bytes. This evidence does not establish
the original server's duplicate-stop count, durations, or gameplay arbitration.

## Server consistency policy

The server already refuses overlapping casts of the same kind and blocks most
actions during vehicle-component removal. Its cross-kind guards were incomplete:
crafting could overlap medical use or shredding; proximity shredding could overlap
crafting or vehicle removal. Independent callbacks could both mutate inventory,
and the earlier callback's subject-only stop could clear the later action's bar.

`ZoneService.InventoryCasts.cs` now applies the same **refuse competing timed
actions** policy across craft, shred, consume and component-removal ownership.
Carried and proximity shredding share the shred owner. A pending callback owns
the interaction even after its animation deadline has elapsed. Failed scheduling
releases ownership before a start is published. Existing callback identity checks
prevent a stale completion from clearing a newer owner. Stale craft/shred context
cleanup is retained; stale medical world/inventory cleanup now also runs before
the other timed verbs. Ongoing healing after item application is not a pending
cast and does not block a later action.

This is a server consistency policy supported by the native single-timer design.
The original server might have refused, queued, or interrupted competing actions;
the client trace does not resolve that question. Timings, yields, medical amounts,
movement rules, and packet bytes are unchanged. Immediate inventory actions such
as raising a worn hood remain permitted.

Implementation entry points: `RunCraft`, `RunShred`, `RunConsume` in
`ZoneService.cs`, and `HandleProximityShred` in `ZoneService.ProximityShred.cs`.
Vehicle-component removal already checks the other three pending owners; its
runtime implementation is unchanged.

## Validation status

**Implemented; locally passing** in the final Release suite. The
[batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns exact totals and artifacts. Native gameplay repetitions for this batch: zero.
`CrossKindInventoryCastTests.cs` adds real in-memory gateway requests for all 12
ordered pairs of craft/carried-shred/ground-shred/consume, holding the actual
elapsed timer callback before sending the competing request. It also covers
failed scheduling, immediate hood actions, component-removal interactions in both
directions, and stale medical context. The component-removal cases use the
existing explicit-completion clock seam; they are not ten-second native playtests.

Reproduction filter: `FullyQualifiedName~PendingInventoryCastTests`.
The native client still needs a local check that competing inventory verbs
leave one bar visible, consume only the completed action's inputs, and permit the
next action after completion. No live service, client modification or publishing
is part of this change.
