# Network player bots

Run from the Server directory. These are independent SOE/RC4 protocol clients talking to real
LoginService and ZoneService listeners. They authenticate, join together, drive their owned
parachutes, move in a dense group, race for shared loot and disconnect. The local fixture also
equips AR-15s and supplies ammunition through the console so they can fire, reload, report hits,
replay duplicate hit reports and verify a death. Assertions read replies received by the bots.

They do not launch H1Z1.exe, render remote players, simulate collision or find paths around buildings.
Use actual August clients to accept graphics, animation, sound and terrain behaviour.

```powershell
dotnet run --project tests/Cranberry.NetworkBots -c Release -- --bots 50 --seconds 60 --output C:/Aug2017/out/my-network-run
dotnet run --project tests/Cranberry.NetworkBots -c Release -- --bots 16 --seconds 45 --delay-ms 40 --jitter-ms 20 --loss 0.01 --output C:/Aug2017/out/my-loss-run
dotnet run --project tests/Cranberry.NetworkBots -c Release -- --bots 100 --seconds 120 --slow-client --output C:/Aug2017/out/my-crowd-run
```

Delay and jitter are milliseconds **per direction** on the gateway path; loss is a fraction
per datagram. The proxy also introduces 0.2% duplication. Each proxy has a 16,384-packet bound.
`--slow-client` stops the last bot's ACKs and checks that other sessions survive its removal.
`--unreliable-movement` exercises bare, unsequenced channel-2/3 datagrams. Otherwise movement
uses the existing harness's reliable SOE path. Both paths are accepted by the server; application-only
capture logs do not establish the original client's outer framing for every movement record.
Use a new output directory for every run to preserve failures and comparisons.

## Controlled movement-rate experiments

`--movement-hz 60` requests synthetic 60 Hz player movement and, during descent,
60 Hz owned-canopy movement. It uses absolute fractional deadlines, measures
actual generated rates, and counts skipped deadlines instead of replaying a
burst after a stall. It changes the bot input generator, not the server's tick
rate or the native client's sender. The supported override range is 20..120 Hz.
It currently rejects public, server-only, menu-only and multi-match modes rather
than silently ignoring the setting. The server process needs no rate argument.

Omitting the option retains the legacy nominal profile: 25 Hz ground player
input, 1000/24 Hz airborne player input and one canopy report per seven airborne
frames. An explicit override sends both player and canopy input at the selected
rate; report those as separate packet streams when comparing bandwidth.

```powershell
.\tests\Cranberry.NetworkBots\Run-LocalScenario.ps1 -ServerDll <built-dll> -Output C:/Aug2017/out/fresh-60hz-run -Bots 2 -Seconds 30 -Tls -ServerGcMode Server -MovementHz 60
```

The runner records the requested rate and binary hashes. The result records
actual per-phase generation rates, skipped frames and maximum deadline lateness
under `movementGeneration` and each movement window's `generation`. Speed/posture
calculations use actual elapsed time between generated frames.

`MinimumFreshPairHz` counts only timestamps strictly advancing a transient's
high-water mark. `NonAdvancingRecords` combines repeated and reordered timestamps;
it is not a distinct-packet count. These additive fields leave the historical
record-count rates unchanged. `FreshPeerCoverage` describes the expected peers
at window end, not a complete visibility history; no expected peers gives null.

With an explicit rate, the ground window independently requires generation and
fresh delivery to reach at least 90% of the request, alongside existing checks.
This tolerance is an experiment gate, not a claim of exactly 60 Hz service,
native-client compatibility or adequate performance at larger populations.
Inspect measured values and generator lateness even when the run passes.

For capacity measurements, run the fixture server and bots in separate processes. This avoids
sharing a .NET thread pool and garbage collector between the server and all 150 clients. They
still share this computer's CPU and network stack; this is not a distributed capacity certificate.
Build once, then run the executable in two terminals (substitute its actual build path):

```powershell
dotnet Cranberry.NetworkBots.dll --server-only --bots 150 --seconds 600 --output C:/Aug2017/out/fixture-server
dotnet Cranberry.NetworkBots.dll --fixture C:/Aug2017/out/fixture-server/endpoint.json --bots 150 --seconds 60 --unreliable-movement --output C:/Aug2017/out/fixture-clients
```

Start the clients after `endpoint.json` appears. The fixture only binds loopback and its endpoint
file shares the monotonic clock offset so movement timestamps are comparable on the same computer.
`server-profile.json` records aggregate backlog/resends, listener errors and movement age at
server ingress and before the outbound reliable queue. These are whole-run histograms capped
at 10,000 ms; they are not CPU measurements or isolated server processing times. In separate
mode, inspect this file alongside the clients' `result.json`, including its `errors` array.
Stop the fixture after the run by writing `stop` to its output directory's `stop.request` file.
It also exits when its deadline expires. Each fixture is intended for one fresh scenario.

The PowerShell runner manages both processes and their shutdown automatically:

```powershell
.\tests\Cranberry.NetworkBots\Run-LocalScenario.ps1 -ServerDll C:/Aug2017/out/my-build/bin/Cranberry.NetworkBots/release/Cranberry.NetworkBots.dll -Output C:/Aug2017/out/my-150-run -Bots 150 -Seconds 300 -SlowClient
```

`-ClientDll` selects a separately saved client binary for controlled server comparisons. The
runner defaults to bare movement datagrams; `-ReliableMovement` selects reliable movement.
`-ServerGcMode Server` applies server GC to the fixture process only; `Workstation` forces
the comparison mode and `Default` preserves runtime/environment configuration.
`-ClientGcMode Server` separately selects server GC for the load generator, which hosts
thousands of independent clients in one process. Neither switch changes machine-wide settings.
The production host selects server GC in its project file. Each movement window records
the generator's GC pause time; its final process record includes GC mode, CPU, memory and
allocation counters so generator stalls can be distinguished from server-side evidence.
`-MenuBots 2000` adds separately authenticated accounts which remain in the menu throughout
the match. `-MenuOnly` runs just their idle workload. `-Tls` uses the real launcher HTTPS
service, per-account game tunnels, launch tickets and menu status polling on loopback.
`-Voice` (with `-Tls`) connects voice for every active and idle account and sends generated
Opus tones from all match participants; it never opens a microphone. Fixture accounts are
provisioned directly through `SocialStore` before the timed workload, avoiding the public
registration rate limit. Disposable fixture session tokens remain in its local endpoint
file and are invalid when that fixture exits. Do not publish that directory as a launcher.
The loopback fixture uses the native-compatible version/ticket HTTP handshake
with door protocol 0. Bots do not run the native process, so successful fixture
readiness does not verify door visuals or native transitions.

```powershell
.\tests\Cranberry.NetworkBots\Run-LocalScenario.ps1 -ServerDll <built-dll> -Output C:/Aug2017/out/fresh-voice-run -Bots 150 -MenuBots 2000 -Seconds 300 -Tls -Voice -ReliableMovement -SlowClient
```

`-MatchCycles 3 -Bots 2 -Tls` repeats complete two-player matches. It checks one result per
player and one winner, the real 30-second results hold, BACK cancelling the old countdown,
the native `c3/c4` return ticket, login back to the same character, and another playable drop.
The authenticated launcher tunnel survives; the game's SOE login/gateway sessions are renewed
for the character-select handoff. This is still a protocol test, not a native UI recording.

`-DelayMs`, `-JitterMs` and `-Loss` configure the same impairment proxy. A fresh output directory
is required. Inspect `clients/result.json`, `server/server-profile.json`, `scenario.log` and
`completion.json` together. The profile includes process CPU time, memory and allocation counters;
these describe the fixture process on this machine, not the public host's capacity.

Movement windows separate descent, ground, simultaneous weapon drawing and sustained combat.
Their histograms cap at 10,000 ms. They count only records generated within that window, retain
the number of older queued records, and check missing peers and the last position's age. Rates
use actual elapsed time. An end-of-window freshness check does not rule out a shorter stall
earlier in the window; inspect the rate as well. The legacy whole-run histogram remains separate.
Setup timestamps distinguish ammunition request time, server grant recording and bot receipt.
Movement windows now retain UTC start/end boundaries, also written to
`clients/phase-timeline.jsonl`, so fixture telemetry can be aligned without inferring
phase times from rounded log messages. These are nearby sequential snapshots, not
an atomic cut across all clients and server threads.

The single-match generator enables optional harness receive timings. Each window
reports aggregate and per-bot datagram queue wait, application inbox wait, and
application handler work (parsing, observation and responder handling). Queue
timing starts immediately before the corresponding local channel enqueue. Counts
refer to dequeued datagrams or handled application messages, respectively; these
include control traffic and movement queued in an earlier phase. Histogram p99
values are bucket upper bounds; a null p99 with `P99Overflow=true` means it exceeded
10 seconds. These measurements do not include time before the UDP socket read,
isolate TLS delivery or measure reliable reassembly wait. Each stage is coherent
individually. Recording adds some generator overhead, so compare runs made with
the same instrumented generator. The general harness keeps recording disabled.
Per-stage samples represent work completed between that stage's two snapshots;
they do not describe a common set of messages and must not be added as a latency
decomposition. Reports include captured/expected client counts. Repeated-match
reconnections enable recording on the replacement clients and retain retired
session totals in the whole-run aggregate.

Combat setup waits for observed ammunition in every inventory, with a five-second total
deadline and continued movement sampling, before asking the bots to reload and fire.
Parachute setup retains a minimum 500-ms settling interval and waits up to five seconds
for each server-confirmed self-to-owned-canopy mount, rather than equating a sent mount echo
with its server reply. The normal disconnect observation is three seconds. With deliberately
lossy direct UDP, observation can continue for up to 60 seconds to cover a lost close datagram
and the existing 45-second silent-session timeout. Setup and cleanup checks retain their
actual elapsed times. These waits do not relax the clean movement-latency or ground-rate gates.
The combat phase also checks that all observers receive the drawn gun's remote slot-7 mesh
and item binding, independently of the remote firing-event checks. TLS fixture impairment
still affects SOE datagrams at the local tunnel edge; it is not TCP packet-loss emulation.

For the published server, use its distributed launcher profile and a private QA account file:

The public-profile example below is historical: this bot flow still omits the
door-version/readiness handshake required by the current launcher service and
has not been upgraded in this campaign. It cannot currently establish a gameplay
launch against that service. Use the loopback fixture for these rate experiments;
its simulated readiness is not native-client verification.

```powershell
dotnet run --project tests/Cranberry.NetworkBots -c Release -- --bots 2 --seconds 10 --public-profile C:/Aug2017/out/cloud-deploy-test/friend-public/launcher.json --accounts-file C:/Aug2017/out/network-bots-20260907/private/accounts.json --output C:/Aug2017/out/my-public-run
```

Public mode uses certificate pinning, launcher authentication, launch tickets and separate real
WebSocket tunnels. It creates a QA character only if that account has none. Credentials stay in
the private account file. Public mode skips the console-supplied combat phase. Accounts are logged
out and tunnels disposed on completion. `--prepare-cloud` only prepares accounts, without gameplay.

The exit code is nonzero if **any** check fails. `result.json` retains each check and measurements;
`server.log` belongs to the isolated local service. Local packet tails are kept on exceptions.
Legacy whole-run histogram percentiles cap at 2,000 ms; movement-window histograms cap at
10,000 ms. A value at either cap is an overflow bucket, not a measured maximum. The final file
retains both whole-run and separate-window statistics; the movement checkpoint printed in
the console contains samples accumulated up to that checkpoint.

Known September 7 failures and measured results: [networking report](../../docs/networking-bots-20260907.md).
In particular, a successful landing or movement test does not waive the airborne visibility check.
The follow-up [improvements report](../../docs/networking-improvements-20260907.md) covers remote
canopy replication, lifecycle tests and separate-process measurements.
The [150-player target campaign](../../docs/networking-150-target-20260907.md) records the
bounded movement queue, reliable send-window comparison, expanded combat windows and stress runs.

## Independent cadence experiments and the 175-player target

The local single-match fixture accepts 2..175 players. Its explicit test queue can
hold all 175 in one match; production public/hosted policies still cap at 150.
This tooling range does not establish native-client support or smooth gameplay.
See the [175-player direction](../../docs/network175-direction-20260919.md).

`--canopy-hz 6 --movement-hz 60` (runner: `-CanopyHz 6 -MovementHz 60`)
controls the airborne canopy stream independently from player input. The canopy
range is 1..120 Hz; it requires an explicit player rate and one local match cycle.
Without this option the existing scheduler is retained. An explicit canopy rate
can exceed the player rate; canopy-only deadlines never emit a player record.
Both streams skip overdue slots rather than generating a catch-up burst. Reports
retain per-stream emission, skips and lateness plus per-bot rates and missing
owned-canopy identities. The controlled path never guesses a canopy identity.

Controlled descent requires at least 90% of both requested input rates, at most
10% skipped slots in each stream, and fresh canopy delivery at least 90% of its
requested rate to every observer. Every observer must retain the complete starting
rider/canopy/transient cohort. Removed or rebound canopies cannot shrink the
expected population to make a run pass. Existing all-pose p99 and final-freshness
checks remain separate; the p99 histogram is not a canopy-only histogram.
Before freezing that cohort, the controlled path holds observed spawn positions
and gives the normal interest pass up to five seconds to expose all remote mounted
canopies. This startup workload, duration and acceptance check are retained
separately. Default scenarios keep their original setup and observation behavior.
Requested rates of 1–2 Hz remain useful controls but cannot satisfy the existing
350 ms end-freshness requirement; a low-rate control failure alone is not overload.

`--omit-movement-journal` (runner: `-OmitMovementJournal`) optionally avoids
journal naming/formatting for structurally identified inbound gateway movement.
Normal application delivery, decoding, observations, ACKs and acceptance checks
remain active. Default journaling is unchanged. Whole-run, per-bot and per-window
reports disclose suppressed entries; failure tails also disclose the omission.
Malformed prefixes and control packets remain journaled, but this classification
is not full movement-payload validation. Reconnected clients inherit the option
and retain retired-session suppression totals. This policy is a generator control,
not a server optimization; compare it as a separate experimental variable.

For controlled comparisons explicitly select both GC modes and keep all other
settings fixed. A lower requested canopy rate is an experimental workload, not
evidence that the native client has been changed to that rate. WAN TCP loss also
requires a different impairment point from the runner's local SOE-edge proxy.
