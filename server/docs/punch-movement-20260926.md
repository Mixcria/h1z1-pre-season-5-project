# Temporary movement slowdown while punching — 2026-09-26

The user reports a pronounced slowdown during a punch and confirms that normal
speed returns afterward. This investigation found a concrete sprint-permission
lead, but does not establish the original August server's fists configuration.
No gameplay values or client files were changed for this investigation.

## Evidence and limits

Target: August 5, 2017 client, app 433850/depot 433851/manifest
6373368576374184611. The existing Binary Ninja database was queried read-only.
The executable SHA-256 is
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`;
image base is `0x140000000`. Addresses below are virtual addresses; subtract the
image base for RVAs. Reproducible queries and exports are in the private directory
`C:/Aug2017/out/compatibility-20260926/punch-movement`.

This is static client/source analysis plus the user's timing observation, not a
new in-game validation or an original-server packet capture. The precise minimum
speed during the animation has not been measured in a controlled reproduction.

## What the current server supplies

Item 85 maps to weapon 12, fire group 12, generated fire modes 24/25
(`Weapons/AugustWeaponFacts.g.cs`, `AugustFireModeFacts.g.cs`). Follow the complete
construction path through `WeaponSession.cs`, `CapturedWeaponTable.Apply` and
`WeaponListRecords.cs`; inspecting `OverridesFor` alone gives the wrong answer.

`CapturedWeaponTable.Apply` explicitly returns modes 24/25 with `EffectGroup=0`
**before applying captured overrides**. With the current default configuration:

| Field | Current fists value | Consequence / evidence |
| --- | --- | --- |
| Weapon movement multiplier, record `+0x5c` | 1.0 | Generated weapon default and configured weapon multiplier |
| Fire-mode movement multiplier, `+0x10c` | 1.0 | Generated mode default; captured overrides bypassed |
| Fire-group spin-up movement multiplier, `+0x54` | 1.0 | Generated group default |
| Fire-mode flags at `+0x20` | 0 | `SPRINT_FIRE` bit `0x40` is absent; unarmed modes have iron sights cleared |
| Fire-mode flags at `+0x21` | 0 | `HOLD_PLAYER_STATE_WHILE_FIRING` bit `0x40` is absent |
| Player-state group at `+0x108` | 0 | No captured player-state group is applied to these modes |
| Fire duration at `+0x38` | 0 ms | Generated default; throwable wind-up changes do not target fists |
| Melee ability at `+0x194` | 1111157 | Existing fists ability selection; unchanged here |

The other-version captured weapon 12/mode 15 instead contains movement 1.0,
player-state group 1, flag bytes `50 10 20`, and fire duration 1000 ms. Those values
are saved in `fists-captured-fields.json` as an investigation lead. **They are not
the emitted fists mode.** In particular, neither a captured 1000 ms duration nor
the captured sprint flag explains the actual server output. The general
non-firearm override filter also removes fire duration, but the earlier fists
return is decisive here.

## Exact client behavior traced

- `0x14228fa20` multiplies weapon `+0x5c`, current mode's movement multiplier,
  and the group spin-up multiplier only in component state 2. Fresh disassembly
  confirms the weapon load that Binary Ninja's pseudocode partly elides.
  `0x1422a4a60` reads the mode value at `+0x10c`.
- `0x1411acfa0` consumes that product in movement speed calculation, alongside
  stance, sprint and stamina factors. The three neutral server multipliers above
  do not provide an evidenced cause for a large punch-specific speed penalty.
- `0x1411abbb0` delegates local fire permission to `0x140c3b330`. The latter checks
  mode `+0x20 & 0x40` against the player's sprint state. This is a real,
  server-configurable permission, not a speed multiplier.
- `0x1411ceca0` contains a sprint/mode transition that queues `0x1411b31d0`.
  That helper creates action
  `0x0c` using vtable `0x1431ddc68`; its name function `0x1411bfce0` returns
  `StopSprinting`. This establishes native sprint cancellation, but does not by
  itself prove which branch caused every observed left-button punch slowdown.
- Native `ClientEffects.txt` has five `RequestAnimation` rows for ability
  1111157: LeftJab, RightStraight, LeftHook, RightHook and RightUppercut.
  `0x141634500` forwards PARAM3/PARAM2 to local-player virtual `+0x7e0`,
  `0x1411cbd00`, which records the clock and durations at
  `+0x5678/+0x5680/+0x5684`. `0x1411b7ea0` tests elapsed time against duration
  minus recovery before allowing an action. This proves an animation recovery
  gate, not a movement multiplier or retail damage timestamp.

The current fists permission and native sprint/action gates are a supported
explanation to investigate for a temporary sprint-to-run transition. Whether the
reported severity includes additional animation movement remains unknown.
The user's confirmation that speed recovers argues against a persistent cleanup
failure; it does not establish retail-correct punch behavior.

## Bounded follow-up: source history, stock ability and main local log

The fists bypass above predates this compatibility batch. Original-workspace
commit `446968f1732a9e161116cfb7f4a0e0212451c1f5` (September 12) adds the
`24 or 25` early return. The current weapon-table builder, writer, session,
`MeleeArm`, ability packets, initial resources and glass-melee partial are
unchanged from published community base `454f73d` after line-ending normalization.
This establishes source continuity, not that those older values were retail-correct.
The history patch and exact current file hashes are saved under the private
`punch-movement-followup` directory beside the original exports.

A fresh read-only extraction of installed `AbilityEx.txt`, `AbilityStages.txt`
and `ClientEffects.txt` matches the earlier `out/data_aug` copies byte-for-byte.
Ability 1111157 has exactly five associated client effects, all RequestAnimation;
there is no additional movement-modifier effect attached to that ability in this
table. Its ten stage rows include resource type 6 first-cost values 2, 3, 3 and 5
at stages 3, 5, 7 and 9. The ability has the pay-resource flag set. These facts are
a stamina investigation lead, not proof of the actual client's resource balance
at the reported slowdown. The server initializes stamina to 600 and the traced
melee resolution path sends no stamina or movement-stat update. The client speed
function's stamina thresholds are established above; whether native ability
prediction or recovery crossed one during this play session remains unknown.

The actual main local log snapshot was 964,473 bytes / 3,269 lines. A filtered,
hashed record preserves 27 melee events: 21 trigger reports (12 rejected by the
existing damage cadence guard) and six ability reports. It also preserves 29
position summaries. For example, the held-trigger run at 16:42:22.356?24.134 has
several reports between adjacent position summaries at 16:42:19.200 and
16:42:24.232. Those summaries are approximately five seconds apart and do not
record the precise inputs, immediate speed or stamina. They cannot distinguish a
short sprint-to-run transition from a deeper animation slowdown. The server's
"duplicate/early swing" response gates damage only; it does not send a speed
penalty or defer restoring movement.

No definite server movement inconsistency emerged from this bounded follow-up.
The later captured sprint flag, inferred animation timing and stamina lead do not
justify a gameplay tuning change. No runtime code, client files or settings were
changed, and no builds, tests or new play session were run in this follow-up.

## Next bounded check

Use the user's existing local server, account and game installation. On flat
ground with fists, compare forward movement without Shift and with Shift; perform
one punch in each case, then a short held-input combo, and release attack while
keeping the original movement keys held. Correlate one short recording with local
movement samples and the existing trigger/RequestAnimation reports. Establish
whether speed falls only from sprint to run, falls below ordinary run speed, or
briefly stops, and measure the return time. Do not interpret server log stance
labels alone as a complete record of the client's inputs.

Completion needs the reproduction above and evidence for the intended August
fists permission/timing. The later capture's sprint bit is a lead only. No speed
boost, animation-duration override, client patch or adopted later-version flags
are justified by this investigation. No new automated tests were run because no
runtime implementation changed; existing milestone validation remains separate.
