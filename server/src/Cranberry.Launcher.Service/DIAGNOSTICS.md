# Launcher service diagnostics

`LauncherHost` accepts a final optional `enableDiagnostics` argument, defaulting
to `false`. When enabled, `CaptureDiagnostics()` returns aggregate measurements
and resets their windows. It is safe to call from a separate exporter thread;
it does not enumerate mutable game-world dictionaries. No account, IP address,
request path, credentials, exception message or packet contents are included.

`ActiveTunnelRoutes` samples the existing concurrent account-to-tunnel route
registry. A route includes its brief setup/teardown period, so this is not exactly
the count of completed WebSocket upgrades. `ActiveVoiceConnections` uses
`VoiceRouter.ConnectionCount`, which already protects its peer collection with a
lock. Neither gauge resets. Voice traffic is not included in the game-tunnel
packet counters below.

All timing values use `Stopwatch` and `Transport.DiagnosticTiming`. Histograms are
fixed-size, with count, sum, average, maximum and non-cumulative buckets. A sample
belongs to the window in which its operation finishes. `WindowMs` is elapsed time
since the previous capture. Individual counters and histograms reset atomically;
the whole multi-field snapshot is not a transaction, so an event racing a capture
may straddle adjacent windows. Sum successive windows when comparing counts.

| Measurement | Meaning |
|---|---|
| `Http.Started/Completed/Faulted` | Requests entering/leaving the middleware; faulted means an exception escaped the existing application handlers. Includes health, API, content and WebSocket endpoints. |
| `Http.ActiveRequests` | Non-resetting count, including open game/voice WebSockets. It is not the number of requests currently consuming CPU. |
| HTTP status counters | Fixed status classes, plus 401 and 429 counts. A WebSocket 101 is counted when its endpoint finishes. An unhandled exception before headers is classified as 500 without modifying the response. Faults after headers retain the started status. |
| `GateWait` | Successfully acquiring the shared launcher account/store semaphore, including asynchronous scheduling delay. |
| `GateHold` | Work while that semaphore is held, including party queue/leave waits for `OnZone`. Histogram updates occur after releasing it. |
| `Tunnel.Started/Accepted` | Tunnel runs attempted / WebSocket upgrades accepted. A setup failure may have no accepted upgrade. |
| `InboundFrames/InboundPayloadBytes` | Complete binary game-tunnel messages received, including a complete message with an invalid selector. Bytes include the one-byte login/gateway selector; they exclude WS/TLS/TCP/IP headers. Incomplete or non-binary rejected messages are not counted here. |
| `EnqueuedFrames` | Valid messages successfully passed to the local SOE queue or UDP-send adapter. |
| `OutboundFrames/OutboundPayloadBytes` | Successfully completed WebSocket sends, including the selector. Completion means the server send operation completed, not that a player received or rendered it. |
| `SendSemaphoreWait` | Successful waits for the per-tunnel shared login/gateway send semaphore. Cancelled waits are not timing samples. |
| `SendCompleted` | The completed WebSocket send call, including its scheduling/backpressure. Failed or timed-out sends appear in closure categories, not this histogram. |
| `CompleteFrameToGameEnqueue` | Work after receiving a complete binary frame through successful local queue admission or UDP `SendAsync`. Includes validation and any configured observer callback before enqueue. It excludes the socket receive await and the later SOE processing queue. |
| `Closures` | One terminal outcome per tunnel run: completed, cancelled, malformed frame, timeout, socket/WebSocket/queue/IO error, disposed or other. Sibling pump failures during cleanup are not counted separately. `Completed` means a normal method return, not proof of a client-requested close. |

Idle socket receive waits are deliberately not reported as server latency. The
metrics do not measure native rendering, Internet RTT, NIC bandwidth, TCP loss,
HTTP body bytes or voice packet latency. Use transport/world and host measurements
alongside these stages. TLS failures before an HTTP request reaches the application
do not enter these counters.

With diagnostics disabled, no timing accumulators or extra HTTP middleware are
created and packet/gate paths do not read `Stopwatch`. `CaptureDiagnostics()` then
returns `Enabled: false` plus the two connection gauges. Existing timeouts, queue
limits, authentication, certificate behavior, packet order and error propagation
remain unchanged. No diagnostic endpoint is exposed publicly by this feature.
