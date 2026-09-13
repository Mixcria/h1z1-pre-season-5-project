# H1Z1 Pre-Season 5 Project

Maintained by Mixcria.

https://www.youtube.com/watch?v=qL_Sh2vITYw&t=119s

A community project for H1Z1's August 2017 client. Its Cranberry Local Windows package includes the server and launcher. The launcher starts the bundled server, installs the matching game files from Cloudflare, and connects the game to your own PC. Accounts and saves belong to your local installation.

This is a community preview prepared from the running `20260913-landing-fall-grace` server and launcher `2026.9.13.3`. Startup, account persistence, the encrypted connection and client installation have automated verification. Native game playtesting of this local package is still pending.

## Play

Download the **Cranberry Local Windows x64 ZIP** from [Releases](https://github.com/Mixcria/h1z1-pre-season-5-project/releases). Extract the entire ZIP and open `Cranberry.Launcher.exe`. Register your first local account with **Make this the local administrator** selected, then use **Install / repair** and **Play**. See [PLAYER-GUIDE.txt](PLAYER-GUIDE.txt).

The package includes the launcher, server and their .NET runtimes. The game download is approximately 14.6 GB. Installation verifies every client file against the release's SHA-256 manifest. Installed files are reused; only missing or changed files need downloading. A complete installation can be verified with every download request blocked.

Local data is stored in `%LOCALAPPDATA%\CranberryCommunity`, outside the release folder. Close the launcher before switching releases. Extract a newer ZIP into a new folder and launch it to reuse your existing accounts, preferences and game installation. Back up the data folder before testing changes to persistent data formats. This preview uses loopback only; it is intended for local development and does not provide LAN hosting.

## Build and test

Use Windows x64, PowerShell and the .NET 10 SDK. This snapshot was built with SDK 10.0.400. NuGet access is required for the first source build. From this repository:

```powershell
./Build-Local.ps1 -Output ./artifacts/Cranberry-Local -Zip
dotnet test ./server/Cranberry.slnx -c Release
dotnet run --project ./tests/LocalEdition.Smoke -c Release -- ./artifacts/Cranberry-Local ./artifacts/local-smoke
```

Choose new output and smoke directories for another run. The package smoke test starts only an isolated local server; it never starts the native game. Historical live-host/capture tests skip unless their opt-in fixtures are configured.

An optional full client check downloads the release's game files and verifies the completed installation with downloads forbidden. It needs about 15 GB of disk space without an existing client:

```powershell
dotnet run --project ./tests/ClientDownload.Smoke -c Release -- ./artifacts/Cranberry-Local ./artifacts/client-smoke
```

A third argument can name an existing client on the same drive. The checker creates hard links in its new test directory to save disk space; replacements never write into the existing installation. Do not launch the native game as part of an automated build.

## Source layout

| Directory | Purpose |
| --- | --- |
| `server/src` | Server source recovered from its verified live-release build snapshot |
| `server/tests` | Server, launcher-service and protocol tests from that snapshot |
| `launcher/src` | Independently released Windows launcher and its matching core library |
| `tests` | Local-package startup, persistence and client-download checks |
| `package` | Pinned client manifest, public download address and safe gameplay defaults |
| `compatibility` | Static appearance input matching the live server |
| `provenance` | Baseline file hashes and a record of the local adaptations |
| `server/tools`, `server/rulings` | Existing generators, client tooling and design decisions |

The server and launcher were released from different source snapshots. Their copies of `Cranberry.Launcher.Core` remain separate to preserve those exact versions. Build them through `Build-Local.ps1`. When changing shared wire contracts, update and test both copies together. The server solution intentionally omits its older GUI source; the standalone launcher lives under `launcher/`.

The shipped generated data and static runtime files are sufficient for an ordinary build. Some historical generators and capture tools still refer to the original developer's external game-data/research folders; those inputs are not included. Do not run the old deployment tools to publish a community release. See [provenance/LOCAL-CHANGES.md](provenance/LOCAL-CHANGES.md).

## Community updates

Keep normal development on `main`, use branches and pull requests for changes, and publish a numbered release when a build is ready for players. There is no requirement to update on a schedule. A commit on GitHub does not change someone's installed server.

GitHub holds source, issues, pull requests and the launcher/server ZIP. Cloudflare R2 holds game content at the public download origin in `package/download-host.json`. Each client file is addressed by its SHA-256, so old releases can continue requesting their matching content. Retain objects referenced by releases that remain supported. See [RELEASING.md](RELEASING.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

The included GitHub Actions workflow builds and tests pull requests and produces a preview artifact. It does not deploy a live server or publish a Release. No production credentials are required.

## Attribution

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). This snapshot retains original provenance headers, including imported/captured data markers. The original server implementation, third-party libraries and game-derived materials have separate provenance. A project license and the treatment of those materials remain to be settled before this candidate is published as an open-source release.
