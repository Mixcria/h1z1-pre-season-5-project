# Gameplay compatibility milestone — 26 September 2026

The owner authorized fixing the reported issues, updating the existing main local
setup, and publishing the completed source changes to GitHub. The scope comprises
the [inventory/lifecycle batch](inventory-persistence-batch-20260926.md),
[reconnect correction](reconnect-ownership-20260926.md), and
[seat request, response and speed corrections](vehicle-seat-request-20260926.md).
No client executable, DLL, game asset or original-balance value is changed.

## Validation

| Check | Result |
|---|---|
| Vehicle Release regressions | 620 passed, zero failed/skipped; includes all 17 new seat-speed cases |
| Full Release solution | 7,923 passed, 36 skipped, zero failed: server 7,402/2 skipped, launcher 243, harness 278/34 skipped |
| Windows x64 developer package | Built; 386 immutable files, 276,034,670 bytes, no player data |
| Package startup and persistence | All 22 checks passed, including owner/account persistence, normal shutdown, port conflicts and complete installation with downloads forbidden |
| Source review | No additional blocking defect found; outgoing files reviewed against `454f73d` |
| Protocol discovery index | Refreshed against current source; 1,743 registration rows and 251 lexical references, with unmapped metadata retained |
| Native parked-seat check | Owner confirmed the updated main-local seat changes worked perfectly, then explicitly approved GitHub publication |

Private evidence is under
`C:/Aug2017/out/compatibility-20260926/seat-speed-01`: `vehicle-results`,
`release-results`, `release-summary.json`, build logs, `package-smoke/results.json`
and the refreshed protocol index. Test outputs stayed under the canonical
repository's ignored `server/out` directory. Optional capture/reference fixtures
and live-harness scenarios remain skipped; no live service was tested.
Existing unrelated analyzer warnings remain. These results establish local
implementation behavior, not original retail server rules or native rendering.

## Local build and source publication

Developer version: `0.2.0-compat.20260926.2`. Zone assembly SHA256:
`5a5b5934e06273a50563729e9d15e2387b3fb878cda7a904104591c91e9140fb`.
The [main local workflow](main-local-workflow-20260926.md) identifies the existing
Community state and `C:/Aug2017/Client` installation used for interactive checks.
The owner confirmed the requested parked driver/passenger/driver check worked
perfectly on this build. This is user-confirmed local behavior; it does not prove
original server policy, moving-seat behavior, observer sound or every vehicle.
Installation completed at 16:04 UTC on the existing main profile. The owned host
passed the pinned health check and loopback listener inspection; all eight saved
state/data files matched their pre-install hashes. Prior binaries and a fresh
state/configuration backup are retained. No game download or asset change was needed.
The refreshed protocol index SHA256 is
`e49e57d1f7bb288d58bcaf08fa8f6705ff4c7b18fd3a309d4942fafeded24516`.

Source review destination:
[`feature/august-compatibility-audit-20260926`](https://github.com/Mixcria/h1z1-pre-season-5-project/tree/feature/august-compatibility-audit-20260926)
into `main`. The repository's sole workflow runs tests/builds and uploads an
Actions artifact; it has read-only contents permission and no deployment or
release step. The GitHub history and pull request record the resulting commits
and checks. A signed player-update release is a separate publication action;
this source milestone does not activate one or deploy a live server.

## Remaining limits

The seat response now uses the verified native field order. The intermittent
movement refusal now uses the accepted simulator's native velocity magnitude,
preserving cooldown, occupancy and a conservative missing-value fallback.
Mode 1's original server policy and broader vehicle balance remain unresolved.

The client itself cancels reload when interacting with ordinary loot. Server
pickup continuity and explicit interruption are covered by 21 passing cases;
no invented bypass is included. [Punch movement](punch-movement-20260926.md)
remains an open investigation: temporary slowdown was reported, but its precise
trajectory and original August fist settings are not established. Neither is
represented as fixed. Stock medical parity, original binocular camera values
and wider native coverage remain tracked in the [coverage matrix](compatibility-status.md).
