# H1Z1 Pre-Season 5 Project

Maintained by Mixcria.

This definitely isn’t 100% complete, and there are still bugs and features that need fixing, but it’s in a really good state. Feel free to contribute and help make this the best version of H1 it can be.

https://www.youtube.com/watch?v=qL_Sh2vITYw&t

https://www.youtube.com/watch?v=W2wlguQvdXQ&t

A community project for H1Z1's August 2017 client. Its Cranberry Local Windows package includes the server and launcher. The launcher starts the bundled server, installs the matching game files from Cloudflare, and connects the game to your own PC. Accounts and saves belong to your local installation.

The source now tracks the 26 September working restoration baseline, including native healing/bleeding presentation and launch compatibility without the retired client-memory helpers. Community local accounts and startup are preserved. See [restoration status and validation](docs/RESTORATION.md). Published player ZIPs have their own versions; updating source does not update an installed package.

## Play

Download the **Cranberry Local Windows x64 ZIP** from [Releases](https://github.com/Mixcria/h1z1-pre-season-5-project/releases). Extract the entire ZIP and open `Cranberry.Launcher.exe`. Register your first local account with **Make this the local administrator** selected, then use **Install / repair** and **Play**. See [PLAYER-GUIDE.txt](PLAYER-GUIDE.txt).

The package includes the launcher, server and their .NET runtimes. The game download is approximately 14.6 GB. Installation verifies every client file against the release's SHA-256 manifest. Installed files are reused; only missing or changed files need downloading. A complete installation can be verified with every download request blocked.

Solo, Duos and Fives accept one local player and start five seconds after the pre-game world finishes loading. Every local account, including the administrator, receives all catalogue skins, 200,000 starter Crowns and 500 locked crates in each of 31 families. Existing accounts receive missing local entitlements on upgrade; spent starter currency and crates are not refilled.

Local data is stored in `%LOCALAPPDATA%\CranberryCommunity`, outside the release folder. Signed community releases can update the launcher and bundled server together while retaining your accounts, preferences and game installation. An older manual-only launcher needs one manual installation of an updater-enabled release first. Close the game before switching releases. Back up the data folder before testing changes to persistent data formats. This preview uses loopback only; it is intended for local development and does not provide LAN hosting.

## Build and test

Use Windows x64, PowerShell and the .NET 10 SDK. This snapshot was built with SDK 10.0.400. NuGet access is required for the first source build. From this repository:

```powershell
./Build-Local.ps1 -Output ./artifacts/Cranberry-Local -Zip
dotnet test ./server/Cranberry.slnx -c Release
dotnet run --project ./tests/LocalEdition.Smoke -c Release -- ./artifacts/Cranberry-Local ./artifacts/local-smoke
```

Choose new output and smoke directories for another run. The package smoke test starts only an isolated local server; it never starts the native game. Historical live-host/capture tests skip unless their opt-in fixtures are configured.

Appearance tests use the bundled `compatibility/dynamicAppearance.bin` fixture. Historical comparisons against external research files are opt-in: set `CRANBERRY_ACCOUNT_CRATES_REFERENCE` to an `AccountCrates.json` path, or `CRANBERRY_CLIENT_PACK_INDEX` to a `pack-index-aug.tsv` path. Unconfigured comparisons are reported as skipped. The ordinary crate, appearance, shader and model behavior tests still run.

An optional full client check downloads the release's game files and verifies the completed installation with downloads forbidden. It needs about 15 GB of disk space without an existing client:

```powershell
dotnet run --project ./tests/ClientDownload.Smoke -c Release -- ./artifacts/Cranberry-Local ./artifacts/client-smoke
```

A third argument can name an existing client on the same drive. The checker creates hard links in its new test directory to save disk space; replacements never write into the existing installation. Do not launch the native game as part of an automated build.

## Source layout

| Directory | Purpose |
| --- | --- |
| `server/src` | Current restoration server source with community local-hosting adaptations |
| `server/tests` | Server, launcher-service and protocol regression tests |
| `launcher/src` | Independently released Windows launcher and its matching core library |
| `tests` | Local-package startup, persistence and client-download checks |
| `package` | Pinned client manifest, public download address and safe gameplay defaults |
| `compatibility` | Static appearance input matching the live server |
| `provenance` | Historical import hashes, current source identity and local adaptations |
| `server/tools`, `server/rulings` | Existing generators, client tooling and design decisions |

The server and launcher were released from different source snapshots. Their copies of `Cranberry.Launcher.Core` remain separate to preserve those exact versions. Build them through `Build-Local.ps1`. When changing shared wire contracts, update and test both copies together. The server solution intentionally omits its older GUI source; the standalone launcher lives under `launcher/`.

The shipped generated data and static runtime files are sufficient for an ordinary build. Some historical generators and capture tools still refer to the original developer's external game-data/research folders; those inputs are not included. Do not run the old deployment tools to publish a community release. See [provenance/LOCAL-CHANGES.md](provenance/LOCAL-CHANGES.md).

## Community updates

Keep normal development on `main`, use branches and pull requests for changes, and publish a numbered release when a build is ready for players. There is no requirement to update on a schedule. A commit on GitHub does not change someone's installed server. Automatic updates require a reviewed release with a community-signed manifest and matching launcher/server ZIP; ordinary developer builds have update checks disabled.

GitHub holds source, issues, pull requests and the launcher/server ZIP. Cloudflare R2 holds game content at the public download origin in `package/download-host.json`. Each client file is addressed by its SHA-256, so old releases can continue requesting their matching content. Retain objects referenced by releases that remain supported. See [RELEASING.md](RELEASING.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

The included GitHub Actions workflow builds and tests pull requests and produces an unsigned preview artifact. It does not deploy a live server, hold a release signing key, or publish a Release. No production credentials are required. Player releases use a dedicated offline community key; see [the signing and recovery procedure](RELEASING.md).

## Attribution

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). This snapshot retains original provenance headers, including imported/captured data markers. The original server implementation, third-party libraries and game-derived materials have separate provenance. A project license and the treatment of those materials remain to be settled before this candidate is published as an open-source release.
