# Community launcher and server updates

The community updater is separate from the production launcher's update feed.
Only published GitHub Releases in `Mixcria/h1z1-pre-season-5-project` with a valid
community signature can supply executable updates. A source push or CI artifact
does not publish a player update. See [RELEASING.md](../RELEASING.md).

## Application lifecycle

The original `Cranberry.Launcher.exe` checks for a release before starting a local
session. It downloads the complete launcher/server bundle, verifies its signature,
archive and file hashes, and stages it in `.community-updates/versions`. It then
starts that bundle's launcher with the same persistent data directory. The child
reports readiness only after its GUI event loop starts and its own local server
responds to the pinned HTTPS health check. Only then does the parent accept the
new version. A failed startup returns to the previous verified bundle, then the
original installation if necessary.

The parent holds an installation lock and a data-folder launch lock for the
entire session. The child holds the existing local-edition data lock. An owned
server must exit before another version prepares the same data. Shutdown uses
the ownership pipe and confirmed process exit; escalation only targets the
recorded launcher and server, never a process selected only by name or port.
A running H1Z1 session prevents activation and forced updater shutdown.

Accepted sequence numbers remain recorded even after rollback, so rollback
does not permit a remote downgrade. Failed startup hashes are quarantined.
Interrupted downloads and unverified staging directories are never activated.
Offline discovery failure uses the installed version. Installed update bundles
are verified again before each launch. The root entry launcher and older bundles
are retained; automatic disk cleanup is not implemented.

Accounts, certificates, preferences, saves and game files are outside the release
bundle. Existing defaults remain user-owned. Data schema 1 requires compatible
save formats; binary rollback cannot undo an incompatible save migration.
The root entry engine stays in place. A future bootstrap protocol change or key
replacement can require a separately reviewed manual installation. Its original
prerelease preference controls discovery even when the selected bundle's build
preference differs.

## Local lobby policy

Solo, Duos and Fives wait indefinitely with one ready player. Two ready players
start a 180-second countdown; dropping below two cancels it, and returning to two
starts a fresh countdown. The local administrator can use `/startmatch` to start
alone or skip the countdown. These local runtime rules override older saved
one-player/five-second settings without rewriting the settings file. This is
community hosting policy, not a claim about original retail population rules.

## Signed format

`community-release.json` uses camel-case JSON with schema 1. Its signature is
ECDSA NIST P-256 with SHA-256 and the 64-byte IEEE P1363 format, encoded as base64.
The public key is a base64 DER SubjectPublicKeyInfo in `community-update.json`.
The signing payload is UTF-8 with LF line endings, including a final LF:

```text
cranberry-community-v1
<sequence decimal>
<version>
win-x64
1
1
<archive size decimal>
<uppercase archive SHA-256>
<uppercase raw inventory SHA-256>
```

The two `1` lines are minimum updater version and persistent-data schema.
The flat `Cranberry-Local-update.zip` contains the full application and a
`community-files.json` array of `{path,size,sha256}` entries. The inventory covers
every file except itself; its raw bytes are covered by the signed manifest.
Unexpected files, path traversal, links, Windows device names, duplicate paths,
oversized expansion and packaged private state are rejected. The package must
retain the trusted repository/key and match the signed sequence and version.

## Validation and remaining confirmation

`CommunityUpdater.Tests` covers signatures, feed scope, redirects, malformed
archives, interrupted downloads, file verification, leases, readiness, rollback,
anti-replay and game-start races. All release responses in these tests are local
injected responses. `CommunityUpdater.Smoke` starts real isolated server/child
processes and checks accounts, settings, graceful shutdown, startup failure,
timeout, forced owned-process shutdown and preservation of a running session.
`LocalEdition.Smoke` retains the existing package/startup regression checks.

`CommunityUpdater.GuiSmoke` exercises actual packaged launcher windows and local
servers through signed update, failed-update fallback and offline restart. Its
synthetic signed packages use disposable test keys and must never be published.
These checks do not launch H1Z1 or establish in-game compatibility. Before a
player release is marked stable, confirm PLAY, the native menu/game transitions,
and that reopening the original shortcut retains the same account and settings.

Local verification on 26 September 2026 passed 75 updater tests, 89 offline
publisher assertions, 37 real-process lifecycle checks and the existing 22
package checks. The three real GUI/server phases also passed: signed sequence 2
activation, signed sequence 3 startup failure with fallback to 2, and offline
restart of 2. Four GUI processes and three server processes closed, with the
data lease and all three local ports released after each phase. The game-active
shutdown guard was tested with an injected game-state signal; H1Z1 was not run.
The enabled self-contained 0.2.0-preview.1 package and signed update archive were
built locally. The owner subsequently confirmed successful native launch, death
and return to the main menu. This is one reported sequence; repeated transition
cycles and reopening/preferences retention were not separately confirmed in that
report. GitHub check results are recorded on the updater pull request.

That playtest preceded the revised waiting/two-player lobby policy. The revised
source passed the complete server solution: 7,591 tests passed and 36 existing
external-input/live scenarios were skipped. All three modes passed waiting,
two-ready-player countdown, cancellation/restart and owner-start checks. The
rebuilt signed package passed all 22 startup/account/persistence checks. Native
waiting-to-`/startmatch` confirmation remains pending; the earlier launch/death/menu
result does not validate this revision. Evidence is retained locally under
`C:\Aug2017\out\restoration-20260926\community-lobby-ready`.

The first GitHub run exposed an existing gateway test with a five-second wait
budget for roughly four seconds of chained timers. Only that wait now permits
ten seconds of CI scheduling margin; gameplay timers and assertions are unchanged.
The next run exposed an identity test's callback-order assumption and contention
in real-timer gateway tests. The identity test now waits for zoning, and gateway
timer tests run separately from concurrent classes. Their behavior assertions
remain in place.

The 0.2.0-preview.1 source change adds the updater, offline publisher, package
inventory, build/test integration and the local lobby policy above. Client
binaries and assets are unchanged. Publication remains a separate approval step;
build success alone is not a playtest.
