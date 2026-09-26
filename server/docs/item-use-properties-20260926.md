# August 2017 item-use properties

Scope: the `AC 2C` request codec and quantity binding, checked against the exact
August client on 2026-09-26. This establishes client encoding and decoding; it
does not establish original server rules for bulk shredding or other item verbs.

## Target and evidence

- Steam app 433850, depot 433851, manifest 6373368576374184611; protocol string
  `ClientProtocol_1148` at VA `0x1435fcda0`.
- `H1Z1.exe`, 72,818,304 bytes, SHA-256
  `d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
- Binary Ninja GUI MCP read the existing `H1Z1-August-2017.bndb` PE view. Image
  base `0x140000000`; the queried functions reported `needsUpdate: false`.
  No database save, reanalysis, client modification, or playtest was performed.
- Private exports: `C:\Aug2017\out\compatibility-20260926\item-protocol`.
  `queries.py` and numbered JSON jobs reproduce the read-only requests.
  `evidence-index.json` lists 16 export hashes; its SHA-256 is
  `78c745166f7b5a96542e284d175f175c3c7997894c30c901b87b63fa5c1694a9`.

Relevant functions (RVA = VA minus image base):

| Role | VA | RVA | Private export |
| --- | --- | --- | --- |
| Request constructor/sender | `0x14162a270` | `0x162a270` | `request-use-direct-sender.txt` |
| Base reader | `0x140d08f10` | `0xd08f10` | `packet-base-reader.txt` |
| Request record reader | `0x140d1b330` | `0xd1b330` | `request-packet-receiver.txt` |
| Request record writer | `0x140d1ad40` | `0xd1ad40` | `request-outbound-serializer.txt` |
| Property-block reader | `0x140d1afa0` | `0xd1afa0` | `property-block-reader.txt` |
| Property-block writer | `0x140d1a850` | `0xd1a850` | `property-outbound-serializer.txt` |
| First 32-bit list reader | `0x140d1c830` | `0xd1c830` | `integer-property-reader.txt` |
| Second 32-bit list reader | `0x140d1c9c0` | `0xd1c9c0` | `second-property-reader.txt` |
| 64-bit list reader | `0x140d1ceb0` | `0xd1ceb0` | `qword-property-reader.txt` |
| Four-component list reader | `0x140d1cd10` | `0xd1cd10` | `vector-property-reader.txt` |
| String list reader | `0x140d1cb50` | `0xd1cb50` | `string-property-reader.txt` |
| Length-prefixed string reader | `0x140b78f60` | `0xb78f60` | `native-string-reader.txt` |

## Wire layout

All multibyte values below are little-endian. Offsets include `AC 2C`.

| Offset | Size | Meaning supported by this trace |
| --- | --- | --- |
| 0 | 1 | Items family `0xac` |
| 1 | 1 | RequestUseItem subtype `0x2c` |
| 2 | 4 | Fixed `1` in the outbound constructor; **not item quantity** |
| 6 | 4 | Fixed `0` in the outbound constructor; further semantics unresolved |
| 10 | 4 | Item-use option ID, read from the executor object at `+0x4c` |
| 14 | 8 | Requesting character GUID |
| 22 | 8 | Resolved action target GUID |
| 30 | 8 | Item source/container-owner GUID |
| 38 | 8 | Item instance GUID |
| 46 | 1 | Empty-properties flag: any nonzero byte ends the property block |
| 47 | variable | Five lists, present only when the flag is zero |

Each list starts with a signed 32-bit entry count. Positive counts are followed
by that many entries, in this fixed order:

| List | Entry after its count | Entry size |
| --- | --- | --- |
| 1 | 32-bit property ID, 32-bit value | 8 bytes |
| 2 | 32-bit property ID, 32-bit value bits | 8 bytes |
| 3 | 32-bit property ID, 64-bit value | 12 bytes |
| 4 | 32-bit property ID, **four** 32-bit components | 20 bytes |
| 5 | 32-bit property ID, signed 32-bit byte length, raw string bytes | 8 + length |

The second list's numeric interpretation and the fourth list's component meaning
are not established here. Their exact widths are established. The string reader
copies the specified byte count into `SoeUtil::IString<char>`; this layer does not
validate text encoding or require a terminator.

The native readers treat nonpositive list counts as empty. They mark the stream
as failed when a count, entry field, or string body is truncated; negative string
lengths also fail. Preserve that distinction: a negative list count is not proof
of a native decode error. Bound positive counts by available bytes before loops
or allocation, and reject the entire server request when any required field is
missing. Parsing a valid prefix and executing a fallback action is unsafe.

The writer emits flag `1` when all five maps are empty, otherwise flag `0` and
all five lists. Therefore the normal empty encoding is 47 bytes; a zero flag
plus five empty counts is also accepted by the reader and occupies 67 bytes.
One entry in list 1 with the other four empty occupies 75 bytes. Seventy-five is
not a universal parameter-block length. These readers do not enforce packet-end
exhaustion; this trace does not establish a surrounding strict exhaustion rule.
Retain and measure unconsumed trailing bytes instead of rejecting them by guess.

## Quantity and GUID semantics

The extracted original `ActionManager.as` defines quantity property ID `1` and
passes `(1, selectedQuantity)` to `UIBindingItem.RequestUseItem`. For actions with
`RequiresQuantity`, Shift/controller hold on a larger stack opens the selector;
the ordinary path requests one. Drop has a separate path that can request the
whole stack or one. Thus property 1 means the **requested quantity**, which can
equal the stack size; it is not inherently the tile's stack size at click time.

The native bindings at `0x14121c910` (RVA `0x121c910`, Scaleform registration from
`0x14047fd30`) and `0x14143cf80` (RVA `0x143cf80`) map extra argument pairs with
IDs 1/4 into list 1, and IDs 2/3 into the 64-bit list. Both call `0x140d25e00`,
which delegates to `0x140d24300`. The latter checks the item and option and calls
the option executor. The presence of both bindings does not establish which UI
path was active during any earlier playtest.

The manager's local variable `var_58` comes from the local character's GUID at
player+`0xd8`; `var_60` comes from the UI container-owner argument, defaults to
local when absent, and selects the inventory containing the item. `var_48` comes
from an explicit target or the option definition's target resolver (virtual
slot `+0x50`). The executor call (virtual slot `+0x70`) supplies pointers in order:
`local, resolved target, container owner, item, properties, manager`.

All 18 distinct executor bodies discovered as callers of `0x14162a270` preserve
those three GUID arguments in that order. The caller list has 21 rows because
three callsites also belong to overlapping discovered functions. Exports are
`request-executor-a.txt`, `request-executor-b.txt`, and the 16 `executor-*.txt`
files; `guid-role-evidence-index.json` records their hashes and the manager/
sender chain. The common sender writes them at +14, +22, and +30, respectively.
Thus **target is +22 and source/owner is +30**, opposite the historical C# names.
Earlier captures with both fields equal to the player could not reveal this.
## Concrete action classes and salvage

The `TYPE_NAME` loader `0x14223dc80` hashes characters using signed arithmetic
right shifts (`sar` at `0x14223dd11` and `0x14223dd22`). Registry `0x14130d980`
binds those hashes to factories. Following the constructors and virtual tables
establishes these specific paths:

| Type and relevant sheet options | Name hash | Definition constructor / vtable | Executor at virtual slot `+0x70` |
| --- | --- | --- | --- |
| ConsumeItem, including 17 | `0x9b04b307` | `0x141305c70` / `0x14320bf88` | `0x14162b420` |
| EquipItem, including 60 | `0x70f8c0e0` | `0x141306180` / `0x14320c138` | `0x14162ba10` |
| SalvageItem, including 6 and 63 | `0xecabdec2` | `0x141306f40` / `0x14320c528` | `0x14162bfe0` |

For salvage, registration `0x1412fa390` chooses factory builder `0x1412f6fe0`
in the initialized client mode. Its factory vtable `0x14320d738` creates the
definition above. The definition's virtual `+0x58` points through `0x1400ace8c`
to `0x14162a1e0`: `xor al,al; ret`, unconditionally false. Its executor getter
`0x14162a1a0` returns the definition itself. The normal stock salvage executor
preserves the common GUID order and forwards the existing properties; it does
not add a requested-quantity property.

The original SQL UI path is connected too: `0x1414e0190` calls definition
virtual `+0x58` with property ID 1 and writes column 5, named `RequiresQuantity`
by schema `0x1414df4a0` / name lookup `0x1414e0000`. The other native data source
at `0x1414e2bb0` makes the same property query (`0x1414e2f15`). Therefore the
normal `ActionManager.as` salvage path has **RequiresQuantity=false** and sends
the three ordinary item/action/owner arguments without a quantity selector or
quantity property. Preserve single-item salvage. This does not establish what
the original server would do with a fabricated bulk request, or original hidden
yield, timing, and cancellation rules.

Target acquisition is a separate issue from field order. Resolver `0x141629ff0`
reads `TARGET_TYPE` at definition+`0x5c`: 1 chooses the local character; 2 calls
`0x140c950a0` on the local target manager; other values select the source entity
or local fallback. The exact sheet has type 2 for refuel option 17 and type 0
for EquipItem option 60. Do not infer that a carried key's ordinary EquipItem UI
action targets a vehicle merely because option 60 exists.

The additional raw exports and hashes are indexed privately in
`option-class-evidence-index.json`; numbered jobs 37–46 reproduce the factory,
property-query, and executor reads. `find-type-key.py` reproduces the signed
hash calculation from the exact executable. Earlier candidate filenames are
discovery labels; the final addresses and type mapping above are authoritative.

Original UI evidence is private under
`C:\Aug2017\out\hosted-retail-audit-20260917\original-components\scripts`:
`ui/managers/ActionManager.as` SHA-256
`c73f5e7054891ef540351ebdec5ec167ea15c83e69cf0a94b055d8c259b9eed4`.
Its source `Component_Library.gfx` SHA-256 is
`8ff1fed5d2a04651387bae99a6f8efa3caa84ea10890c63806493ed1d288763a`.

## Server scope and validation

`src/Cranberry.Zone/Inventory/ItemUsePackets.cs` now validates all five lists,
preserves existing first-list quantity selection and trailing compatibility, and
corrects the header/quantity comments. Its parser maps target from +22 and source
from +30 while preserving the public record API. Added source-level regression
cases use distinct GUIDs; self-inventory samples alone could not catch the old
reversal. Carried refuel already reads the target property; cargo refuel now also
retains it instead of substituting the source vehicle. Existing distance,
occupancy, capacity and completion-time validation remain authoritative. The
requester is checked before external vehicle dispatch as well as carried item
resolution, closing the external route's earlier ownership-check omission.
No guessed bulk-action behavior was introduced.

Reverse engineering: codec, common GUID roles, and the stock salvage quantity
gate verified; remaining per-option gameplay semantics partial.
Implementation: implemented. Validation: **locally passing** for codec boundaries,
distinct-GUID targeting, external requester checks and malformed-then-valid gateway
actions in the final Release suite. The [batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns exact totals and artifacts. Native gameplay repetitions for this batch: zero;
client evidence and synthetic gateway checks do not prove original server policy.
