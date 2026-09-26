# Weapon skin and delayed-action fixes — 26 September 2026

Changing a weapon skin reconstructs the August client's weapon component. The
server previously kept a pending reload and its counter while the replacement
client component started idle with a zero counter. A later shotgun reload could
therefore miss the native reserve-staging branch, and an old completion could
load a round after the client had stopped reloading.

The skin refresh now cancels only the affected weapon's pending reload through
the existing cancellation path. Observers receive its interruption before the
item is rebuilt. The item keeps its GUID, loaded ammunition, durability, firing
deadline and bindings. After ItemAdd, the existing zero-delta `82/08` snapshot
restores its retained counter and current ammunition. Skinning a spare leaves
another weapon's reload running. Rejected and repeated requests do not interrupt
an operation. No ammunition is spent by cancellation.

This is a consistency policy for the restoration; the original August server's
skin-interruption policy is unknown. It changes no balance, client asset or wire
layout. A counter of zero needs no snapshot because it matches the new component.

## The two earlier lifecycle fixes

The separately tested research follow-ups are also included in this branch:

- Crafting and shredding retain explicit ownership until their queued completion
  runs or is cancelled. An elapsed animation deadline cannot permit another cast
  to replace that ownership. Old callbacks cannot change a replacement inventory
  or clear its interaction bar. Carried and proximity shredding share the owner;
  existing medical and vehicle-component exclusions remain in force.
- A fire-mode notification is checked against the held item's descriptor before
  declaring combat state. Invalid or late requests can no longer recreate an old
  weapon after menu teardown. Valid first notifications and observer relays retain
  their existing behavior.

The conditional mode-reset question across item reconstruction remains open.
There is no broad fire-mode reset or guessed binocular zoom adjustment here.

## Evidence and checks

Target: August 5, 2017, ClientProtocol_1148, executable SHA-256
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
Native addresses use image base `0x140000000`:

- `0x140c35400` replaces the item component on ItemAdd; `0x14228bba0` initializes
  reload counters, and `0x14148c3c0` reads a tail without those counters.
- `0x14148abc0` expects the previous acknowledged counter plus one on pump reload.
  `0x1414887e0` stages reserve only on the matching acknowledgement while
  reloading; `0x141487990` consumes that cache.
- `0x1422935d0` validates mode descriptors before changing native selection;
  `0x14148c1c0` sends the client's successful `82/0c` notification.

The independent audit inspected fresh bytes from this executable and the installed
inventory UI. Its current Skin action uses `9a/05` and the saved preset. Tests also
exercise the retained native `ac/2c` → `ac/32` selector. These are synthetic service
requests and an independently transcribed native reload model, not original
server captures or native animation tests.

`WeaponSkinReloadTests` adds 46 cases: counters 0/1/255/256, empty/partial and
held/stowed weapons, both skin routes, partial completion, reserve removal,
another gun reloading, refusals/duplicates, observer identity/order and teardown.
Before the runtime fix, 24 counter cases and six cancellation cases reproduced
the two defects. The existing runtime-preservation test now expects cancellation
only when the skinned weapon is the reload target. The imported suites retain
19 cast-ownership cases and seven fire-mode lifecycle cases.

The focused Release run passed 117/117 tests. The full Release solution passed
7,657 tests, skipped 36 optional external-fixture/running-host cases, and failed
none. The self-contained developer package built successfully and passed all 22
isolated startup and persistence checks. Its seven runtime DLL/PDB identities
matched, and all 582 source-document checksums matched this worktree. Existing
analyzer warnings are unrelated to these changes. Automated success does not
establish native presentation.

Reproduce from the repository root:

```powershell
dotnet test server/Cranberry.slnx -c Release
./Build-Local.ps1 -Output ./artifacts/Cranberry-Local-Fixes -Version 0.2.0-fixes.20260926
dotnet run --project tests/LocalEdition.Smoke -c Release -- ./artifacts/Cranberry-Local-Fixes ./artifacts/fixes-smoke
```

## Native acceptance

The owner reported that the fixes appeared to work after testing the
`0.2.0-fixes.20260926` developer package and receiving the checklist below.
This is general native acceptance; no individual shell/sound observation or
transition count was reported. It does not establish original-server behavior
or a completed three-cycle transition test.

The supplied checklist remains useful for further regression testing:

1. Reload a shotgun, change its skin, then reload again. Check the shell loop,
   sound, magazine and reserve counts with both an empty and a partial magazine.
2. Change its skin during reload. Already loaded shells remain; unspent reserve
   remains available; another press of Reload works. Skin a spare gun while the
   held gun reloads and check that the held reload continues.
3. Craft and shred repeatedly, leave during a cast, and start another after
   rejoining. Check for duplicate output, stuck bars or a delayed old bar-stop.
4. Equip/release/switch binoculars and repeat three death → menu → lobby cycles.
   Check that the next world's actions work normally. Record actual observations
   separately from the automated results.

The owner approved publishing these source fixes to `main` independently after
confirming their files do not overlap the launcher update. A player release and
live deployment are separate actions. The tested build is a developer package,
so an automatic release update cannot replace this candidate.
