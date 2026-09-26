# Working baseline: 26 September 2026

This source baseline brings the current locally tested restoration server into
the community repository. It includes native healing and bleeding presentation,
the stable launch path without client-memory helpers, session/disconnect guards,
current inventory slot handling and lobby peer-interest handling. Community local
accounts, solo queues, persistence, portable fixtures and launcher startup remain
part of this repository.

The target is H1Z1 August 5, 2017: app 433850, depot 433851, manifest
6373368576374184611, ClientProtocol_1148. The analyzed executable SHA256 is
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
The Binary Ninja database and Ghidra exports identify this executable; original
August server captures are unavailable. The later Z1BR capture uses protocol
1315 and is not proof of original August values.

## What is accepted, and what remains unknown

The owner confirmed healing presentation and bleeding's indicator, continuing
health drain after hits stop, and clearing after treatment in the isolated local
restoration workspace. Its executable matched the preserved original; its HUD
already contained earlier modifications. This confirms the observed local
behavior, not unmodified-stock visual parity or original server balance.

Native `9e/06`, `9e/07` and `9e/08` effect messages now drive the existing client
HUD. See the [medical wire and lifecycle note](../server/docs/native-medical-presentation-20260926.md).
Existing gameplay policy is retained, including the working workspace's bandage
completion boost; original healing amounts, bleed thresholds and timing rules
are not established merely by decoding the client.

Binoculars still use the existing custom zoom approximation. Native camera and
reticle paths have been traced, but original server-authored camera values are
missing. No guessed replacement or client patch is included. The later inventory
cast-ownership and rejected-fire-mode follow-ups were not part of the
owner-playtested baseline exported here. They are now included in the separate
[reload and cast fixes](../server/docs/reload-cast-fixes-20260926.md) increment,
which has since received general native acceptance from the owner. Individual
observations and repetition counts remain as recorded in that note.

The owner's stable live death/menu/lobby report remains useful baseline evidence.
Repeated local native transition cycles have not yet been counted. A successful
test suite is not proof that the native client cannot crash.

## Validation

The Release solution run covered 7,621 cases: 7,584 passed, 36 skipped, and one
configuration-documentation check identified two missing lobby-setting rows.
Those rows were added; all three configuration-documentation checks then passed.
No gameplay assertion failed. The skips cover optional external fixtures and
opt-in running-host scenarios. The fresh self-contained `Build-Local.ps1 -Zip`
package built successfully and `LocalEdition.Smoke` passed all 22 checks.
An independent source review found no blocking defect. The two inventory generator
outputs pass their selected provenance checks and isolated regeneration; the
other historical generators are not claimed as newly validated.

The pinned community client manifest matches all 853 path/size/hash entries used
in the accepted local play session. A retained but unused September 20 grenade
asset candidate differs; it is not adopted here, and grenade-layout compatibility
still requires its own native check. The manifest and client assets are unchanged.

Package smoke tests do not launch the native game; the exact rebuilt community
package still needs its own native playtest. The historical live-import hashes remain unchanged in
`provenance/live-baseline.json`; the current source identity is recorded separately
in `provenance/source-baseline.json`.

## Maintaining a current working main branch

Use this repository as the source of accepted changes. Each bounded improvement
gets a feature branch, evidence, relevant automated checks, and local native
confirmation where needed. Review the complete diff before merging to `main`.
Keep unfinished research separate so the default branch remains useful.

Source publication does not update installed packages. A downloadable release
and any live deployment are separate actions. The existing workflow builds/tests
PRs and main pushes and uploads a preview artifact; it has no deployment step.
This baseline update does not change the pinned client manifest or game assets.

Next priorities: record repeated local death/menu/lobby cycles, verify the
rebuilt package's native medical behavior,
and obtain same-build binocular camera values. Wider gameplay remains an
[evidence-backed inventory and backlog](../server/docs/compatibility-status.md),
not a claim that the game is retail-complete.
