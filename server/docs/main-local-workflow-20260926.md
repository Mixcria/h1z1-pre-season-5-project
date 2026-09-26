# Main local development setup — 26 September 2026

The owner explicitly selected their existing main local setup for subsequent
interactive testing. Do not reopen the older restoration profile for playtests.

| Setting | Selected value |
|---|---|
| Launch entry point | `C:/Aug2017/Start-Main-Local.cmd` |
| Development binaries | `C:/Aug2017/Local-Development` |
| Existing accounts, outfits and configuration | `%LOCALAPPDATA%/CranberryCommunity` |
| Existing game | `C:/Aug2017/Client` |
| HTTPS API | `https://127.0.0.1:50774/` |
| Login / Gateway UDP | `127.0.0.1:63894` / `127.0.0.1:63895` |

The entry point starts `Cranberry.Launcher.exe --local-data` with the existing
Community root. The binary folder contains no player state or game copy. Its
development update sequence is zero, so an automatic release update cannot
replace the working build during testing. Existing signed public packages and
their update metadata were not altered.

The installed runtime includes the [native seat-speed correction](vehicle-seat-request-20260926.md).
The final Release suite passed 7,923 tests, with 36 skipped and zero failures;
the built package passed all 22 startup/persistence checks. `Cranberry.Zone.dll`
SHA256: `5a5b5934e06273a50563729e9d15e2387b3fb878cda7a904104591c91e9140fb`.
The earlier runtime refused 44 of 58 seat requests as TooFast. The new guard uses
accepted native speed instead of arrival-time displacement. The owner confirmed
the requested parked-seat check worked perfectly on this build. Broader vehicle
behavior, observer presentation and sound remain separate observations.

## Verification and continuity

- All 853 installed game files matched the unchanged package manifest by size
  and SHA256. No game download or asset repair was needed. Manifest SHA256:
  `f5a8e044cea8897aa1ead0b3cde998817effe2bc7f6710203d0fc5d91c091c77`.
- The development package inventory validated 386 immutable files. Its game
  manifest and appearance hashes match the existing main package metadata.
- The old restoration host was stopped. The main host started with its existing
  configuration and all observed listeners bound to loopback.
- `/health` returned HTTP200 and client version `0.0.118.208059`.
- All eight backed-up files under the main `state` and `data` directories were
  byte-identical immediately after startup. Accounts were not reset or migrated.
- The native game was subsequently observed running from `C:/Aug2017/Client`.
  This confirms the selected installation, not a successful in-game feature test.

Private backup and receipts:
`C:/Aug2017/out/compatibility-20260926/main-local-switch-01`.
The backup includes existing state/data/configuration, not another running server.
`switch-receipt.json`, `running.json` and `startup-check.json` record this switch.
The earlier offline client verification remains under
`final-batch-01/main-client-offline-verification.json`.

The seat-speed package was installed on 26 September at 16:04 UTC, after the
game, launcher and host had all exited. The previous binaries are retained at
`C:/Aug2017/Local-Development-before-seat-speed-20260926-170411`.
Fresh state/configuration backup and `main-install.json`/`main-startup.json` are
under `C:/Aug2017/out/compatibility-20260926/seat-speed-01`.
The new launcher's owned host listens only on loopback at the same ports.
Certificate-pinned `/health` returned HTTP200 and the exact target client version.
All eight saved state/data files still matched their pre-install hashes after
startup. The existing game path, manifest and appearance hashes are unchanged.

For future updates, build from the canonical Git workspace and retain this same
main profile and game path. Close the game and its owning launcher before
replacing development binaries; keep a rollback copy. Never use the older
general research launch scripts that start experimental client-patch helpers.
Automated storage/failure fixtures may still use temporary data directories.

The launcher owns the main host and closes it when the launcher exits. Sign into
the existing account. The normal Play path checks the manifest before launching;
if a future build or game change introduces a mismatch, inspect it before
allowing repair, because game assets must remain unchanged for this project.
Source publication is authorized and tracked in the [publication record](compatibility-publication-20260926.md).
This local activation does not publish a player update or change a live server.
