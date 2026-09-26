# Releasing a community update

1. Merge the intended source changes and update `VERSION`. Keep the player-facing release notes focused on changed behavior and known limitations.
2. If game files change, construct the matching `package/game-manifest.json`. Upload its SHA-256-named objects to the configured Cloudflare R2 content prefix and verify every referenced object's size and hash before distributing the manifest. Keep R2 credentials only in your private publishing environment. A server-only change can reuse the existing game manifest.
3. Run the server tests and build a new output directory with `Build-Local.ps1`. The script bundles fresh Windows runtimes, the matching launcher, the game manifest and static compatibility input. It never copies the maintainer's accounts or settings. Follow the signed-package steps below for a player release; an ordinary build has update sequence zero and never checks a release feed.
4. Run `LocalEdition.Smoke` on the resulting folder. Verify client installation/repair when its manifest changes. Playtest the actual game explicitly before marking a new player release stable.
5. After reviewing and approving the exact package, create a Git tag and GitHub Release containing the manual Windows ZIP, its SHA-256, `Cranberry-Local-update.zip`, `community-release.json`, and clear installation/update notes. Preview builds should be marked prerelease. Upload the package and signed manifest together to the same release; do not publish a release while its update assets are incomplete. Upload release assets rather than committing build outputs to source. These tools never publish automatically.

Publishing a community release does not deploy to the production game server. The community updater uses its own public key and the pinned `Mixcria/h1z1-pre-season-5-project` release feed. It does not consume the production launcher update feed or its signing key. Source pushes and CI artifacts do not publish a player update.

## Offline signing and package creation

Create a dedicated community key once, in a private directory outside this repository and all package folders. Keep an offline backup of the private PEM; distribute only the public key through the built package. `keygen` refuses to overwrite either file and never prints private key material.

```powershell
dotnet run --project ./tools/CommunityRelease -c Release -- keygen --private-key C:/Private/CranberryCommunity/release-private.pem --public-key C:/Private/CranberryCommunity/release-public.pem
```

Use a strictly increasing positive sequence for every published update, even when restoring older source behavior. The example version and sequence below are placeholders; choose values newer than the latest published community update. The same sequence/version must appear in the built package and signed manifest.

```powershell
./Build-Local.ps1 -Output ./artifacts/Cranberry-Local-0.2.0-preview.1 -Version 0.2.0-preview.1 -UpdateSequence 1 -UpdatePublicKeyFile C:/Private/CranberryCommunity/release-public.pem -Zip
dotnet run --project ./tests/LocalEdition.Smoke -c Release -- ./artifacts/Cranberry-Local-0.2.0-preview.1 ./artifacts/release-smoke
dotnet run --project ./tools/CommunityRelease -c Release -- sign --package ./artifacts/Cranberry-Local-0.2.0-preview.1 --output ./artifacts/signed-0.2.0-preview.1 --private-key C:/Private/CranberryCommunity/release-private.pem --sequence 1 --version 0.2.0-preview.1
```

The build records `community-files.json` before creating the optional manual ZIP. The signing tool verifies this inventory and the configured public key, then writes a separate flat update ZIP and its signed metadata into a new output directory. It does not modify the built package. Both ZIPs contain the inventory; the manual ZIP retains its outer directory for extraction by players. Do not edit a built package after inventory generation: rebuild into a fresh directory instead. If signing fails, discard its incomplete output directory and choose a new one for the retry.

Preview packages include prereleases when checking for updates. Add `-StableUpdates` when building a package that should follow only releases marked stable. A plain `Build-Local.ps1` invocation uses `-UpdateSequence 0`, needs no signing key, and keeps CI/developer packages off the update feed. `-UpdatePublicKeyFile` accepts the public SPKI PEM from `keygen`; it must never be the private PEM.

The signature covers schema, release sequence/version, platform, minimum updater version, persistent-data schema, archive size/hash, and the exact inventory hash. The first updater and persistent-data schema are version 1; `sign` defaults to `--minimum-updater-version 1 --data-schema 1`. A release requiring a newer entry launcher or an incompatible data format needs a separately reviewed migration/install path, not a lowered metadata requirement.

The reusable offline publisher smoke test creates disposable fixture keys and tiny fake application files in its new evidence directory. It exercises successful signing, content identity, changed/unlisted files, wrong keys, metadata mismatches, and rejection of private state. It never executes those fake files or contacts GitHub. Keep its evidence directory outside release package folders.

```powershell
dotnet build ./tools/CommunityRelease -c Release --artifacts-path ./artifacts/publisher
./tools/CommunityRelease/Smoke-Publisher.ps1 -Publisher ./artifacts/publisher/bin/CommunityRelease/release/CommunityRelease.dll -Evidence ./artifacts/publisher-smoke
dotnet test ./tests/CommunityUpdater.Tests -c Release
dotnet run --project ./tests/CommunityUpdater.Smoke -c Release -- ./artifacts/Cranberry-Local-0.2.0-preview.1 ./artifacts/updater-smoke
./tools/CommunityRelease/Prepare-GuiSmoke.ps1 -Package ./artifacts/Cranberry-Local-0.2.0-preview.1 -Publisher ./artifacts/publisher/bin/CommunityRelease/release/CommunityRelease.dll -Evidence ./artifacts/gui-fixtures
dotnet run --project ./tests/CommunityUpdater.GuiSmoke -c Release -- ./artifacts/gui-fixtures/seed ./artifacts/gui-fixtures/signed-good ./artifacts/gui-fixtures/signed-broken ./artifacts/gui-smoke
```

The GUI smoke preparation copies the real built launcher/server into three isolated fixture bundles with a fresh disposable signing key. The third deliberately contains an invalid host executable to exercise startup rollback; it does not modify the source package. Never publish these fixtures. The GUI harness starts and closes the actual launcher and its local host using a simulated release feed; it does not start the native game or contact GitHub.

## Player upgrade and rollback

Players on an older manual-only launcher need to extract one updater-enabled manual release and open its `Cranberry.Launcher.exe`. That original entry launcher remains in place and selects verified, versioned bundles containing the updated full launcher and server. The original bootstrap engine is not replaced in place; a future updater protocol beyond version 1 may require another manual installation. Accounts, certificates, preferences, saves, and the installed Game folder remain under the player's existing data directory. Starting the latest source code on GitHub is not an update mechanism; only approved, signed release assets are eligible.

Keep the previous working bundle available for startup rollback. Restoring application files does not reverse a save-format migration; maintain backward-compatible persistent data for automatic rollback and test account/settings preservation through an update and a failed update. A running game must be closed before switching bundles. Test offline startup and interrupted downloads as well as a successful update.

Loss of the private signing key prevents trusted automatic updates for existing installations. Do not bypass signature checking or substitute the production key. Recovery requires a reviewed manual release carrying a new trust key. A key compromise likewise requires a documented recovery plan; publishing an unsigned replacement manifest is not a recovery mechanism.

Retain Cloudflare objects referenced by older supported releases. Content is addressed by hash, so existing objects must not be overwritten with different bytes. Changes to the public download domain should preserve old published URLs or provide compatible redirects.

For the first public release, choose the project license and settle the redistribution treatment of the imported/game-derived material listed in `THIRD-PARTY-NOTICES.md`. That decision has not been made by this prepared snapshot.

Relevant platform documentation: [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases), [repository licensing](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository), and [Cloudflare R2 public custom domains](https://developers.cloudflare.com/r2/buckets/public-buckets/).
