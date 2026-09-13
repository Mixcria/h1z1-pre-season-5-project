namespace Cranberry.Harness.Behaviour;

/// <summary>
/// Things the <b>client</b> does. Every value is a client-originated event: a message the harness
/// (playing the client) emitted because the server gave it what the real client demonstrably
/// needs, or a link-level fact about the client's own side.
///
/// This is the point of the whole harness. A host log proves what the server sent; a milestone
/// here is only reached when the modelled client, following the transitions the captures record,
/// actually got far enough to send the next thing. A missed milestone is the shape of the failure
/// the owner used to find by playing (docs/32, D29).
/// </summary>
public enum HarnessMilestone
{
    // Phase A — the login link (docs/71 §2).
    LoginSessionOpen,
    LoginRequestSent,
    LoginReplyReceived,
    ServerListRequestSent,
    ServerListReplyReceived,
    CharacterSelectInfoRequestSent,
    CharacterSelectInfoReplyReceived,
    CharacterLoginRequestSent,
    CharacterLoginReplyReceived,
    LoginLogoutSent,

    // Phase B — the gateway handoff and the RC4 arming point (docs/71 §3).
    GatewaySessionOpen,
    GatewayLoginRequestSentClear,
    GatewayRc4Armed,
    GatewayLoginReplyDecrypted,

    // Phase C — menu bootstrap (docs/71 §4).
    MenuZoneDataComplete,
    MenuPingInfoLogSent,
    MenuSetLocaleSent,
    Menu0x57Sent,
    MenuClientIsReadySent,
    MenuLobbyGameDefinitionRequestSent,
    MenuClientFinishedLoadingSent,

    // Phase D — the menu request set (docs/71 §5).
    LoadingScreenClosed,
    MenuBurstSent,

    // Phase E — PLAY, the transfer and the client's own retry timer (docs/71 §6).
    TransferRequestSent,
    TransferRequestResent,
    TransferReplyReceived,

    // Phase F — Z2 zoning (docs/71 §7). ZoningClientIsReadySent is the single most valuable one.
    ZoningBegun,
    Zoning0x57Sent,
    ZoningFirstMovementSent,
    ZoningZoneDataComplete,
    ZoningClientIsReadySent,
    ZoningClientFinishedLoadingSent,
    ZoningMovementResumed,

    // Phase G — the drop, the teleport handshake and the AutoMount echo (docs/71 §8).
    TeleportStartReceived,
    TeleportAckSent,
    MountedClientFinishedLoadingSent,
    AutoMountEchoSent,
    MountConfirmed,

    /// <summary>
    /// The client's own landing signal: one 10-byte null-guid <c>Vehicle.Dismiss</c> (06 88 18).
    /// Exactly one per session in both known-good captures, ~36 s after the mount burst, and the
    /// packet the server treats as the touchdown (docs/71 §8; ZoneService's Vehicle.Dismiss case).
    /// </summary>
    ParachuteDismissSent,

    /// <summary>A stretch of recorded channel-2 movement replayed on purpose by a scenario.</summary>
    WalkReplayed,

    // Phase H — steady state (docs/71 §9).
    KeepAlivePairSent,
    SynchronizationSent,
    SynchronizationEchoed,
    GameTimeSyncSent,

    // Phase I — loot (docs/71 §10).
    ProximateItemsReceived,
    FullCharacterDataRequestsSent,
    PlayerSelectSent,
    InteractRequestSent,
    InteractCancelSent,

    // Phase J — logout (docs/71 §11).
    LogoutSent,
    LinkClosed,
}

/// <summary>One row of the docs/71 §13 harness contract.</summary>
public sealed record MilestoneDefinition(
    HarnessMilestone Milestone,
    string Id,
    string After,
    string Expect,
    TimeSpan Budget,
    string Evidence);

/// <summary>
/// The docs/71 §13 assertion table, as data. Budgets are the observed maximum plus headroom:
/// failing one means the real client would have hung there. Keeping them here rather than at each
/// call site means a scenario cites the evidence automatically and a budget is changed in one
/// place when a new capture moves it.
/// </summary>
public static class MilestoneCatalogue
{
    private static readonly MilestoneDefinition[] Rows =
    [
        new(HarnessMilestone.LoginRequestSent, "A1", "SOE SessionReply (login)",
            "the client sends LoginRequest", TimeSpan.FromSeconds(0.5), "docs/71 §2"),
        new(HarnessMilestone.ServerListRequestSent, "A2", "LoginReply",
            "the client sends ServerListRequest, one byte 0D", TimeSpan.FromSeconds(0.5), "docs/71 §2, n=89"),
        new(HarnessMilestone.CharacterSelectInfoRequestSent, "A3", "ServerListReply",
            "the client sends CharacterSelectInfoRequest 0B with no measurable delay",
            TimeSpan.FromSeconds(0.5), "docs/71 §2, n=89, median 0.0 ms"),
        new(HarnessMilestone.CharacterLoginRequestSent, "A4", "CharacterSelectInfoReply",
            "the player clicks PLAY and the client sends CharacterLoginRequest",
            TimeSpan.FromSeconds(10), "docs/71 §2 — the one human delay in the flow"),
        new(HarnessMilestone.GatewaySessionOpen, "B1", "CharacterLoginReply",
            "the client opens an SOE session on ExternalGatewayApi_3", TimeSpan.FromSeconds(0.5), "docs/71 §3, n=81"),
        new(HarnessMilestone.GatewayLoginRequestSentClear, "B2", "gateway SessionReply",
            "the client sends Gateway.LoginRequest in the clear, consuming no keystream",
            TimeSpan.FromSeconds(0.5), "docs/71 §3, n=81"),
        new(HarnessMilestone.GatewayLoginReplyDecrypted, "B3", "Gateway.LoginReply",
            "the reply decrypts at inbound keystream position 0 — the RC4 arming test",
            TimeSpan.FromSeconds(2), "docs/71 §3, 81 of 81 gateway sessions"),
        new(HarnessMilestone.MenuPingInfoLogSent, "C1", "ZoneDoneSendingInitialData",
            "the client uploads ClientLog PingInfo.log", TimeSpan.FromSeconds(5), "docs/71 §4, n=129"),
        new(HarnessMilestone.MenuSetLocaleSent, "C2", "that ClientLog",
            "the client sends SetLocale on channel 1", TimeSpan.FromSeconds(0.5), "docs/71 §4, n=80"),
        new(HarnessMilestone.Menu0x57Sent, "C3", "SetLocale",
            "ClientInitializationDetails, GetContinentBattleInfo (ch1), GetRewardBuffInfo, then 0x57",
            TimeSpan.FromSeconds(0.5), "docs/71 §4"),
        new(HarnessMilestone.MenuClientIsReadySent, "C4", "the 0x57 packet",
            "the client sends ClientIsReady 1.590-1.625 s later", TimeSpan.FromSeconds(2.5), "docs/71 §4, n=79"),
        new(HarnessMilestone.MenuLobbyGameDefinitionRequestSent, "C5", "ClientIsReady (Menu)",
            "the client asks LobbyGameDefinitionBase on channel 1 in the same millisecond",
            TimeSpan.FromSeconds(0.5), "docs/71 §4"),
        new(HarnessMilestone.MenuClientFinishedLoadingSent, "C6", "ClientIsReady (Menu)",
            "the client sends ClientFinishedLoading (06 02 00)", TimeSpan.FromSeconds(12),
            "docs/71 §4, asset-load bound, observed max 8.97 s"),
        new(HarnessMilestone.LoadingScreenClosed, "D1", "ClientFinishedLoading",
            "the client sends WallOfData WindowEvent LoadingScreenWindow close",
            TimeSpan.FromSeconds(6), "docs/71 §5 — the last thing a hung client never reaches"),
        new(HarnessMilestone.MenuBurstSent, "D2", "that close",
            "the fixed menu burst: 3x MatchHistoryBase, StaticViewBase kotkdefault, 5 InGamePurchase, Damage x>=24",
            TimeSpan.FromSeconds(2), "docs/71 §5"),
        new(HarnessMilestone.TransferRequestResent, "E2", "the first PlayerWorldTransferRequest",
            "a second, byte-identical request at 4.514-4.564 s, independent of the queue",
            TimeSpan.FromSeconds(6), "docs/71 §6, n=46, spread +-25 ms"),
        new(HarnessMilestone.ZoningClientIsReadySent, "F1", "ClientBeginZoning",
            "the client sends ClientIsReady within 1.970-2.926 s",
            TimeSpan.FromSeconds(4),
            "docs/71 §7/§12 — 40 of 40 successful zonings, 0 of 12 hung ones. The Z2 hang detector."),
        new(HarnessMilestone.ZoningClientFinishedLoadingSent, "F2", "ClientBeginZoning",
            "the client sends ClientFinishedLoading (06 02 00)", TimeSpan.FromSeconds(12), "docs/71 §7"),
        new(HarnessMilestone.LoadingScreenClosed, "F4", "the zoning ClientFinishedLoading",
            "the client closes its own loading screen again (WallOfData LoadingScreenWindow close)",
            TimeSpan.FromSeconds(8),
            "docs/71 §7 / docs/32's own acceptance check — 2 of 2 good zonings close it at +3.055 s "
            + "and +3.891 s after ClientBeginZoning; in 6 of 6 hung zonings (wire-20260829-151627 x4, "
            + "-173352) the next close does not arrive until the client gives up, 27-54 s later. "
            + "The client re-OPENS it in both cases, so only the close discriminates."),
        new(HarnessMilestone.ZoningMovementResumed, "F3", "ClientBeginZoning",
            "a SECOND channel-2 packet (the first, 49 B at +0.067 s, is sent even by a hung client)",
            TimeSpan.FromSeconds(30),
            "docs/71 §7/§12.1 — 40 of 40 good, 0 of 12 hung; median +5.5 s, worst +24.7 s"),
        new(HarnessMilestone.TeleportAckSent, "G1", "server SynchronizedTeleportBase 05 E8 03 00",
            "the client acks with 06 E8 02 00", TimeSpan.FromSeconds(6), "docs/71 §8, observed max 3.879 s"),
        new(HarnessMilestone.MountedClientFinishedLoadingSent, "G2", "that ack",
            "ClientFinishedLoading with tail 01 (06 02 01)", TimeSpan.FromSeconds(10), "docs/71 §8"),
        new(HarnessMilestone.AutoMountEchoSent, "G3", "server VehicleAutoMount",
            "the client echoes the server's bytes with header 05->06 and byte 11 01->00, after G1 and G2",
            TimeSpan.FromSeconds(15), "docs/71 §8, n=27, 1.545-11.796 s"),
        new(HarnessMilestone.MountConfirmed, "G4", "the client echo",
            "server VehicleOwner + MountResponse + VehicleOccupy", TimeSpan.FromSeconds(1), "docs/71 §8"),
        new(HarnessMilestone.KeepAlivePairSent, "H1", "any point in world",
            "KeepAlive and MonitorTimeDrift in the same millisecond, every 1.00 s",
            TimeSpan.FromSeconds(1.5), "docs/71 §9"),
        new(HarnessMilestone.SynchronizationEchoed, "H2", "the client's Synchronization",
            "the server echoes within 5 ms", TimeSpan.FromSeconds(6), "docs/71 §9"),
        new(HarnessMilestone.GameTimeSyncSent, "H3", "any point in world",
            "GameTimeSync every 11.00 s — and note this alone does NOT prove health",
            TimeSpan.FromSeconds(13), "docs/71 §9, §12.2"),
        new(HarnessMilestone.FullCharacterDataRequestsSent, "I1", "ProximateItemBase",
            "one FullCharacterDataRequest per new guid, all in one millisecond",
            TimeSpan.FromSeconds(0.5), "docs/71 §10.1, n=7"),
        new(HarnessMilestone.InteractCancelSent, "I2", "PlayerSelect",
            "InteractRequest within 34 ms then InteractCancel within 2 ms, unconditionally",
            TimeSpan.FromSeconds(0.5), "docs/71 §10.2, n=112"),

        // ---- Lane A additions -----------------------------------------------------------------
        // Rows the scenario lane needs and the build lane did not. Each cites the capture as well
        // as docs/71, because these are the client facts the scenarios are asserting against.
        new(HarnessMilestone.TransferRequestSent, "E1", "the PLAY click",
            "the client sends PlayerWorldTransferRequest (06 ec 00)", TimeSpan.FromSeconds(1),
            "docs/71 §6 — the first of the pair; the second is E2 on the client's own timer"),
        new(HarnessMilestone.TransferReplyReceived, "E3", "PlayerWorldTransferRequest",
            "the server answers PlayerWorldTransferReply (05 ed) before it may zone the client",
            TimeSpan.FromSeconds(30),
            "docs/71 §6; wire-20260829-150206 15:02:46 ed then 0b, and wire-20260830-131819 13:20:31.739 ed "
            + "0.54 s before 0b — the reply always precedes ClientBeginZoning"),
        new(HarnessMilestone.ParachuteDismissSent, "G5", "the AutoMount echo and the descent",
            "the client sends one null-guid Vehicle.Dismiss (06 88 18) at touchdown",
            TimeSpan.FromSeconds(120),
            "docs/71 §8; 1 of 1 in wire-20260829-150206 (15:04:02.642, +36.5 s after the mount burst) "
            + "and 1 of 1 in wire-20260829-184346"),
        new(HarnessMilestone.ProximateItemsReceived, "I0", "the parachute landing",
            "the server publishes a ProximateItemBase list for the ground loot it just spawned",
            TimeSpan.FromSeconds(30),
            "docs/71 §10.1; docs/13 §9 step 5 — one f8 per burst, and the landing burst is the first"),
    ];

    public static IReadOnlyList<MilestoneDefinition> All => Rows;

    /// <summary>The catalogued row for a milestone, or null when the milestone has no §13 budget.</summary>
    public static MilestoneDefinition? For(HarnessMilestone milestone) =>
        Rows.FirstOrDefault(r => r.Milestone == milestone);

    /// <summary>The §13 row with the given id ("F1", "C4", …).</summary>
    public static MilestoneDefinition ById(string id) =>
        Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"docs/71 §13 has no row '{id}'.");
}
