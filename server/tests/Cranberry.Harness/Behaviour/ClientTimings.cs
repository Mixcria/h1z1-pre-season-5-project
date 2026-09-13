namespace Cranberry.Harness.Behaviour;

/// <summary>
/// Every delay the modelled client waits, defaulted to what the real client actually did
/// (docs/71, medians over 49 captures unless noted). These are not comfort values: several are
/// hard-coded client timers whose spread is a few milliseconds, and a harness that shortens them
/// is no longer imitating the client. They are settable so a scenario can deliberately probe a
/// boundary — and <see cref="Runtime.HarnessClock.Scale"/> scales all of them together for a unit
/// test that must not take a minute.
/// </summary>
public sealed record ClientTimings
{
    /// <summary>LoginReply → ServerListRequest. Median 77 ms, min 56, max 85, n=89.</summary>
    public TimeSpan ServerListRequest { get; init; } = TimeSpan.FromMilliseconds(77);

    /// <summary>ServerListReply → CharacterSelectInfoRequest. Median 0.0 ms: the client pipelines them.</summary>
    public TimeSpan CharacterSelectInfoRequest { get; init; } = TimeSpan.Zero;

    /// <summary>The PLAY click on character select. The only human delay in the flow; median 1.303 s.</summary>
    public TimeSpan CharacterLoginClick { get; init; } = TimeSpan.FromSeconds(1.0);

    /// <summary>CharacterLoginReply → gateway SessionRequest. Median 27 ms, min 9, p90 33, n=81.</summary>
    public TimeSpan GatewaySessionRequest { get; init; } = TimeSpan.FromMilliseconds(27);

    /// <summary>Gateway SessionReply → the clear Gateway.LoginRequest. 30–34 ms: a very tight timer.</summary>
    public TimeSpan GatewayLoginRequest { get; init; } = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// ZoneDoneSendingInitialData → ClientLog PingInfo.log. Bimodal, 0.11–3.35 s; docs/71 §14.7
    /// says to accept the whole range rather than model a mean. The default sits in the fast mode
    /// so a scenario is not three seconds slower than it needs to be.
    /// </summary>
    public TimeSpan PingInfoLog { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>PingInfo.log → SetLocale (ch1). 12 ms median, min 0, p90 17.</summary>
    public TimeSpan SetLocale { get; init; } = TimeSpan.FromMilliseconds(12);

    /// <summary>SetLocale → ClientInitializationDetails / GetContinentBattleInfo / GetRewardBuffInfo / 0x57.</summary>
    public TimeSpan MenuProbeBurst { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// 0x57 → ClientIsReady. 1.615 s median, min 1.590, p90 1.625, n=79 — the tightest interval in
    /// the whole protocol, and a hard-coded client delay rather than a load.
    /// </summary>
    public TimeSpan MenuClientIsReady { get; init; } = TimeSpan.FromMilliseconds(1615);

    /// <summary>ClientIsReady → ClientFinishedLoading. Asset-load bound, 0.00–8.97 s, median 2.99 s.</summary>
    public TimeSpan ClientFinishedLoading { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>ClientFinishedLoading → the LoadingScreenWindow close. Observed +3.01 s.</summary>
    public TimeSpan LoadingScreenClose { get; init; } = TimeSpan.FromMilliseconds(3010);

    /// <summary>
    /// First PlayerWorldTransferRequest → the second, byte-identical one. 4.542 s, spread ±25 ms
    /// over n=46, and independent of when the queue-done packet arrives: this is a client timer,
    /// not a reaction (docs/71 §6).
    /// </summary>
    public TimeSpan TransferRequestRetry { get; init; } = TimeSpan.FromMilliseconds(4542);

    /// <summary>ClientBeginZoning → the WallOfData teardown burst.</summary>
    public TimeSpan ZoningWallOfData { get; init; } = TimeSpan.FromMilliseconds(33);

    /// <summary>ClientBeginZoning → 0x57 and the single 49-byte channel-2 packet.</summary>
    public TimeSpan Zoning0x57 { get; init; } = TimeSpan.FromMilliseconds(67);

    /// <summary>
    /// ClientBeginZoning → ClientIsReady. min 1.970, p25 2.125, median 2.204, p90 2.426, max 2.926
    /// across all 40 successful zonings; never in 12 of 12 hung ones.
    /// </summary>
    public TimeSpan ZoningClientIsReady { get; init; } = TimeSpan.FromMilliseconds(2085);

    /// <summary>
    /// ClientBeginZoning → the client re-opening its own loading screen
    /// (<c>WallOfData WindowEvent LoadingScreenWindow open</c>, channel 1). Observed +369, +381,
    /// +407, +416 and +431 ms across five zonings — and sent by the <b>hung</b> client too
    /// (wire-20260829-173352 17:36:05.041), so it proves nothing on its own. Its matching close
    /// does: see F4.
    /// </summary>
    public TimeSpan ZoningLoadingScreenOpen { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The zoning ClientFinishedLoading → the loading screen closing again. +5 to +22 ms: the
    /// close follows the load, it is not a separate timer.
    /// </summary>
    public TimeSpan LoadingScreenCloseAfterLoad { get; init; } = TimeSpan.FromMilliseconds(22);

    /// <summary>Server SynchronizedTeleport start → the client re-opening the loading screen. +406 ms.</summary>
    public TimeSpan TeleportLoadingScreenOpen { get; init; } = TimeSpan.FromMilliseconds(406);

    /// <summary>ClientBeginZoning → the channel-2 stream restarting. Median +5.5 s, worst +24.7 s.</summary>
    public TimeSpan ZoningMovementResume { get; init; } = TimeSpan.FromMilliseconds(3730);

    /// <summary>Server SynchronizedTeleportBase → the client's ack. 1.527–3.879 s, median 2.050, n=28.</summary>
    public TimeSpan TeleportAck { get; init; } = TimeSpan.FromMilliseconds(1531);

    /// <summary>The client's ack → ClientFinishedLoading with tail 01, then the AutoMount echo.</summary>
    public TimeSpan MountEcho { get; init; } = TimeSpan.FromMilliseconds(15);

    /// <summary>Channel-2 player movement cadence. 24 ms median; 21 ms while parachuting, 40 on foot.</summary>
    public TimeSpan PlayerMovementInterval { get; init; } = TimeSpan.FromMilliseconds(24);

    /// <summary>Channel-3 managed-object movement cadence, only while mounted. 171 ms median.</summary>
    public TimeSpan ManagedMovementInterval { get; init; } = TimeSpan.FromMilliseconds(171);

    /// <summary>KeepAlive and MonitorTimeDrift, always in the same millisecond. 1.002 s.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromMilliseconds(1002);

    /// <summary>Synchronization. 5.002 s; the server echoes in ≤1 ms.</summary>
    public TimeSpan SynchronizationInterval { get; init; } = TimeSpan.FromMilliseconds(5002);

    /// <summary>GameTimeSync. 11.00 s — free-running, and NOT evidence of health (docs/71 §12.2).</summary>
    public TimeSpan GameTimeSyncInterval { get; init; } = TimeSpan.FromMilliseconds(11000);

    /// <summary>ClientMetrics. 30.00 s.</summary>
    public TimeSpan ClientMetricsInterval { get; init; } = TimeSpan.FromMilliseconds(30000);

    /// <summary>PlayLength ×2 → ClientLogout → 2.00 s of silence → the SOE Disconnect.</summary>
    public TimeSpan LogoutSilence { get; init; } = TimeSpan.FromMilliseconds(2000);

    /// <summary>Whether the responder replays the channel-2 stream once in world.</summary>
    public bool ReplayMovement { get; init; } = true;
}
