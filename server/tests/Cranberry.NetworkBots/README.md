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
