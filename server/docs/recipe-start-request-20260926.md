# August 2017 crafting request

`Command.RecipeStart` is an eleven-byte request with two mandatory fields. The
old parser accepted four speculative layouts and could execute a seven-byte
truncated request as one craft. The exact native serializer removes that ambiguity.

## Exact-build evidence

Target: Steam app 433850, depot 433851, manifest 6373368576374184611.
`C:\Aug2017\Client\H1Z1.exe`, 72,818,304 bytes, SHA-256
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
Binary Ninja read the existing `H1Z1-August-2017.bndb` PE view, image base
`0x140000000`. The decompiled functions reported `needsUpdate: false`.
No client file, database analysis, or live service was modified.

Private reproducible queries and exports are in
`C:\Aug2017\out\compatibility-20260926\recipe-request`; `queries.py` runs the
three numbered JSON jobs against the local Binary Ninja MCP service.
`evidence-index.json` records the export hashes and analyzed binary identity.

| Role | VA (RVA) | Export |
| --- | --- | --- |
| RecipeStart command-table entry | `0x143247400` (`0x3247400`) | `recipe-command-table.txt` |
| UI binding | `0x14143a3e0` (`0x143a3e0`) | `recipe-ui-binding.txt` |
| Outbound sender | `0x1411b39d0` (`0x11b39d0`) | `recipe-outbound-send.txt` |
| Exact serializer | `0x1411b41e0` (`0x11b41e0`) | `recipe-outbound-serializer.txt`, `recipe-serializer-assembly.txt` |
| Shared one-byte family writer | `0x140a27a90` (`0xa27a90`) | `zone-base-writer.txt` |

The binding initializes recipe to zero and count to one, replacing them with
numeric UI arguments when supplied. A nonzero recipe constructs the request;
the sender directly calls the serializer above. Assembly at `0x1411b420d`
writes the subtype as two bytes, `0x1411b4257` stages four recipe bytes, and
`0x1411b42a4` stages four count bytes. The default UI argument does **not**
make the wire count optional, and an explicitly supplied zero is serialized as zero.

## UI path and wire contract

The original `CraftingWindow.gfx` was freshly exported read-only with JPEXS
26.2.1 into `recipe-request\original-ui`. Its SHA-256 is
`3f54542ba0b7b861a813bcd05370676a0a9683874608509cd8ab6b4457bfb696`,
matching the original UI archive and earlier August extraction.
`views/crafting/CraftingWindow.as:161` handles key release/hold and calls
`RecipeManager.makeOne`/`makeMax`. `RecipeManager.as:79` calls
`UIBindingRecipe.RecipeStart(recipeId, count)`; one supplies 1, max supplies the
recipe row's `maxProduction`. This connects the serializer to the crafting UI,
without treating a standalone handler or string as proof of original server rules.

Fresh export SHA-256: `RecipeManager.as`
`b2dc2f657bf8a431d1e09e381264a238c413c7be3aa3fcacd5f0cdca8c907d27`;
`CraftingWindow.as`
`c14c856a009760515557b92b74ff09d64fcfc2351902bda78f04fb26c1f40135`.

Direction: client to server, inside the existing gateway zone tunnel.
All multibyte fields are little-endian.

| Offset | Width | Field |
| --- | --- | --- |
| 0 | u8 | Command family `0x09` |
| 1 | u16 | RecipeStart subtype `0x001a` |
| 3 | 4 bytes | Recipe ID |
| 7 | 4 bytes | Requested count |

The native binding obtains signed 32-bit UI values; the C# public model preserves
their wire bits as `uint`. The native sender writes eleven bytes. Original-server
trailing-byte rejection is unknown; the parser continues to report extra bytes.
The known-recipe predicate remains authoritative for this server's advertised
catalogue. This trace does not prove recipe contents, duration, zero/negative-count
rules, or other original server mechanics.

## Implementation and validation

`Crafting/RecipeStartRequest.cs` now accepts only the complete native frame,
preserves count bits, and retains the existing enum/method API. Legacy enum values
are no longer emitted. `RunCraft`/`CraftingService` retain their existing minimum-one
gameplay policy; that policy is separate from decoding and is not a retail finding.

`RecipeStartRequestTests` covers native framing, all truncated prefixes, rejected
legacy alternatives, exact opcode widths, count preservation, catalogue checks,
low-byte-zero recipe IDs, and trailers. `RecipeStartGatewayTests` exercises truncated
requests through the real gateway with both cast-bar settings: no inventory mutation,
no pending cast, and a subsequent complete request accepted. The cast-bar case checks
arming only; the synchronous case checks ingredient consumption and output creation.
The obsolete catalogue constraint forbidding IDs divisible by 256 was removed.

Status: protocol framing **verified against this client**; implementation **implemented**;
local validation **locally passing** in the final Release suite. The
[batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns exact totals and artifacts. Native gameplay repetitions for this batch: zero.
Reproduction filter:
`FullyQualifiedName~RecipeStartRequestTests|FullyQualifiedName~RecipeStartGatewayTests|FullyQualifiedName~CraftingRetailWireTests|FullyQualifiedName~CraftingCatalogTests`.
