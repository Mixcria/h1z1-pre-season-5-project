using System.Numerics;
using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Replay;
using Cranberry.Zone;
using Cranberry.Zone.Movement;

namespace Cranberry.Harness.Scenarios;

/// <summary>
/// <b>Scenario lane A — the core flow, asserted on client-originated packets.</b>
///
/// <para>Every wave of this project shipped code that passed its unit tests and then broke in the
/// owner's client, and each time the only way that was discovered was by playing: the four-hour Z2
/// zoning hang, the one-way ground-loot latch that left every building past the landing point
/// empty, the body-slot-7 equipment row that hard-crashed the client. A unit test proves the bytes
/// are what we <i>think</i>; none of the 1,472 of them can prove the client accepts them. These
/// seven scenarios drive the live server with a modelled client and state their acceptance in terms
/// of what that client <i>sent back</i> (docs/32, D29, docs/72).</para>
///
/// <list type="table">
/// <item><term>S1</term><description>login → character list → character login → gateway handoff,
///   with the RC4 arming point asserted specifically</description></item>
/// <item><term>S2</term><description>zone bootstrap → ClientIsReady (Menu) → ClientFinishedLoading,
///   and the menu request set answered</description></item>
/// <item><term>S3</term><description>PLAY → queue → PlayerWorldTransferReply → ClientBeginZoning Z2
///   → <b>ClientIsReady (Zoning)</b> — the assertion that would have caught the outage</description></item>
/// <item><term>S4</term><description>StartMatch → air spawn → parachute → AutoMount echo → descent
///   → landing</description></item>
/// <item><term>S5</term><description>ground objects → <c>0F 45</c> answered with <c>0xda</c> → a
///   pickup succeeds</description></item>
/// <item><term>S6</term><description>loot streaming: relocate and assert new loot spawns</description></item>
/// <item><term>S7</term><description>the docs/32 / docs/45 regression guards, as live assertions</description></item>
/// <item><term>S8</term><description>the inverse of F1: a silent client is not talked at</description></item>
/// <item><term>S10</term><description>death and victory: stand in the gas until it kills, then
///   assert <c>0f 4f</c> + <c>ce 04</c> cause <c>0x42</c> + <c>ce 09</c> (docs/97)</description></item>
/// </list>
///
/// <para><b>What these can and cannot show.</b> A failure is a real finding: it means the server
/// did not give a healthy client what the captures show a healthy client needs. A pass is not proof
/// the owner's client will be happy — docs/71 §14.6 records that the Z2 hang's <i>trigger</i> is
/// not isolated from the wire, so the responder models a healthy client and F1 catches the class of
/// bug where a precondition is never sent, not an unknown trigger (docs/72 §2.4).</para>
/// </summary>
public static class LaneAScenarios
{
    /// <summary>Where the captures live. Overridable so the lane is not tied to one machine.</summary>
    public static string CaptureRoot { get; set; } =
        Environment.GetEnvironmentVariable("CRANBERRY_CAPTURES") ?? @"C:\Aug2017\captures";

    /// <summary>
    /// The reference session for the match-zoning opcode order: reached Z2, parachuted, five
    /// landings (docs/32 "the working case").
    /// </summary>
    public const string KnownGoodCapture = "wire-20260829-150206.txt";

    /// <summary>
    /// The capture the S6 relocation replays from: a real client walking roughly 200 m across Z2 on
    /// foot between 12:16:59 and 12:21:30 (host-20260830-120333.log records the decoded positions,
    /// from (-258.3, 33.0, 305.4) out to (-306.0, 35.1, 500.4)). No capture in the corpus contains a
    /// longer continuous on-foot channel-2 stream, which is itself worth knowing.
    /// </summary>
    public const string WalkCapture = "wire-20260830-120333.txt";

    /// <summary>The first packet of the walk window, by the capture's own clock.</summary>
    public const string WalkFrom = "12:16:59";

    /// <summary>The last packet of the walk window.</summary>
    public const string WalkTo = "12:21:30";

    // Base opcodes this lane asserts on. Restated here rather than imported so a scenario reads as
    // the contract it enforces; the names are the client's own (out\registrations-1148.json).
    private const byte ZoneDone = 0x05;
    private const byte SendSelf = 0x03;
    private const byte ClientUpdateBase = 0x11;
    private const byte InGamePurchaseBase = 0x27;
    private const byte LobbyGameDefinitionBase = 0x41;
    private const byte MatchHistoryBase = 0x67;
    private const byte MountBase = 0x70;
    private const byte LoginBase = 0xA6;
    private const byte GameModeHud = 0xCE;
    private const byte StaticViewBase = 0xE9;

    /// <summary>
    /// A recorded on-foot channel-2 stream from a real client standing in Z2 (PV Commercial East,
    /// <see cref="WalkCapture"/> between <see cref="WalkFrom"/> and <see cref="WalkTo"/>).
    ///
    /// <para><b>Why a scenario has to choose this deliberately.</b> The harness never synthesises a
    /// movement packet, so every channel-2 byte it sends carries the coordinates of the session it
    /// was recorded in — and the server takes the ground-loot, door and vehicle centre straight from
    /// the position the client reports. The build lane's default pool was recorded in the pre-match
    /// lobby at (-230, 506, -4889): a landing driven by it arms the world 4.9 km from the drop the
    /// server just performed, over open water, where there are no spawn markers at all. Swapping to
    /// a pool recorded on the ground in Z2 is what lets a landing scenario mean anything. The finding
    /// that fell out of discovering this is written up in the return of this wave: the server never
    /// cross-checks the reported position against the drop point it chose itself.</para>
    /// </summary>
    public static MovementReplay GroundWalkPool(int max = 3000) =>
        MovementReplay.FromCapture(
            Path.Combine(CaptureRoot, WalkCapture), channel: 2, max: max,
            notBefore: WalkFrom, notAfter: WalkTo);

    /// <summary>The appearance table's payload length, to the byte (docs/32; guard 1).</summary>
    public const uint AppearancePayloadBytes = 1_273_927;

    /// <summary>
    /// The pre-2026-09-03 payload: the filtered capture alone. The 5,096-byte difference is the
    /// Cranberry-authored rows for the thirteen ground-loot wearables the boot census reported as
    /// grey (docs/106 addendum). Every captured byte is unchanged - AuthoredAppearanceTests
    /// compares the three sections one by one - and CRANBERRY_APPEARANCE_AUTHORED_ROWS=0 puts the
    /// wire back to exactly this number, which is what the two sessions that entered Z2 carried.
    /// </summary>
    public const uint CapturedOnlyAppearancePayloadBytes = 1_268_831;

    /// <summary>docs/45 regression guard 5: slot 7 is RHand, and a row naming it crashes the client.</summary>
    public const uint ActiveHandSlotId = 7;

    /// <summary>
    /// <b>S1 — login to the gateway handoff, with the RC4 arming point asserted specifically.</b>
    ///
    /// <para>docs/05 records that the gateway link arms RC4 immediately after the one clear
    /// <c>LoginRequest</c>, with both keystreams starting at position 0, and that a mis-timed
    /// <c>LoginReply</c> kills the link. That is not observable from a host log at all: the server
    /// logs "encrypted LoginReply sent" either way. It is observable here, because the harness
    /// decrypts the reply with a cipher it armed at the client's moment — if the server armed at a
    /// different point the bytes would not decrypt to <c>02 01</c>, and B3 would never fire.</para>
    /// </summary>
    public static Scenario S1Login() =>
        Scenario.Named("S1 login → character list → character login → gateway handoff")
            .Connect()
            .Expect("A1")
            .Expect("A2")
            .Expect("A3")
            .Expect("A4")
            .Expect("B1")
            .Expect("B2")
            .Expect("B3")
            .Require(
                "the CharacterSelectInfoReply carried at least one available character",
                c => c.Roster is not null && c.Roster.Characters.Count > 0,
                "docs/71 §2 step 7 — the client cannot click PLAY on an empty roster")
            .Require(
                "the CharacterLoginReply handed back an RC4 gateway ticket",
                c => c.Handoff is not null && c.Handoff.UsesRc4 && c.Handoff.Key.Length > 0,
                "docs/05 — cipher mode 3; the August client arms RC4 and nothing else")
            .Require(
                "the gateway LoginReply decrypted at inbound keystream position 0 (THE arming test)",
                c => c.Milestones.Get(HarnessMilestone.GatewayLoginReplyDecrypted)?.Detail
                    ?.EndsWith("position 0", StringComparison.Ordinal) == true,
                "docs/05, docs/71 §3 — 81 of 81 gateway sessions; the 78 clear bytes consume no keystream")
            .Require(
                "the client armed RC4 before the server's reply arrived, not after",
                c => c.Milestones.At(HarnessMilestone.GatewayRc4Armed)
                    <= c.Milestones.At(HarnessMilestone.GatewayLoginReplyDecrypted),
                "docs/05 — arming is one operation with the send; two calls leave a race the client does not have")
            .Read("gateway", c => $"{c.Handoff?.Address} cipher={c.Handoff?.CipherMode} guid={c.SelfGuid}");

    /// <summary>
    /// <b>S2 — zone bootstrap to the answered menu.</b>
    ///
    /// <para>C4 (ClientIsReady) and C6 (ClientFinishedLoading) are the two client-originated
    /// packets that say the bootstrap worked; D1, the loading-screen close, is the strongest
    /// in-world evidence short of a screenshot. The menu request set is then asserted from the
    /// other side: the client asked for lobby definitions, a static view, purchases and match
    /// history in its D2 burst, and this scenario requires the server to have answered each.</para>
    /// </summary>
    public static Scenario S2Menu() =>
        Scenario.Named("S2 zone bootstrap → ClientIsReady (Menu) → ClientFinishedLoading → menu answered")
            .Connect()
            .Expect("C1")
            .Expect("C2")
            .Expect("C3")
            .Expect("C4")
            .Expect("C5")
            .Expect("C6")
            .Require(
                "SendZoneDetails carried world type 4 (HeightfieldLod)",
                c => c.Responder?.ZoneType == 4,
                "docs/06/07 — FUN_140b37510 registers a world implementation for type 4 only; "
                + "every other value fails PostInitialize and the client never becomes ready")
            .Require(
                "the bootstrap delivered a self record before the client was asked to be ready",
                c => c.Ledger.Count(SendSelf) > 0 && c.Ledger.Count(ZoneDone) > 0,
                "docs/09/10 — SendSelfToClient then ZoneDoneSendingInitialData is the client's starting gun")
            .Expect("D1")
            .Expect("D2")
            .RequireEventually(
                "the server answered LobbyGameDefinitionBase (0x41)",
                c => c.Ledger.Count(LobbyGameDefinitionBase) > 0,
                TimeSpan.FromSeconds(5),
                "docs/71 §4 — the client asks in the same millisecond as ClientIsReady (C5)")
            .RequireEventually(
                "the server answered the D2 burst: StaticViewBase (0xe9), InGamePurchase (0x27) and MatchHistoryBase (0x67)",
                c => c.Ledger.Count(StaticViewBase) > 0
                    && c.Ledger.Count(InGamePurchaseBase) > 0
                    && c.Ledger.Count(MatchHistoryBase) > 0,
                TimeSpan.FromSeconds(10),
                "docs/71 §5 — the fixed menu burst; wire-20260829-150206 answers all three "
                + "(3x 05 67 07, 05 e9 02, 05 27 0b) within 60 ms of the close")
            .Read("menu replies", c =>
                $"0x41 x{c.Ledger.Count(LobbyGameDefinitionBase)}, 0xe9 x{c.Ledger.Count(StaticViewBase)}, "
                + $"0x27 x{c.Ledger.Count(InGamePurchaseBase)}, 0x67 x{c.Ledger.Count(MatchHistoryBase)}");

    /// <summary>
    /// <b>S3 — PLAY to ClientIsReady (Zoning). This is the scenario the lane exists for.</b>
    ///
    /// <para>docs/32: on 2026-08-29 the client sat on the loading screen for four hours while the
    /// server sent the lobby HUD, StartMatch and a parachute to a client that had never answered.
    /// The one packet that separates the 40 successful zonings from the 12 hung ones is
    /// <c>ClientIsReady</c> within 1.970–2.926 s of <c>ClientBeginZoning</c> (F1). Nothing else
    /// does: both a healthy and a hung client send the WallOfData teardown, the 0x57 and the single
    /// 49-byte channel-2 packet, and a hung client's GameTimeSync ticks forever (docs/71 §12).</para>
    /// </summary>
    public static Scenario S3ZoneToZ2() =>
        Scenario.Named("S3 PLAY → queue → transfer reply → ClientBeginZoning Z2 → ClientIsReady (Zoning)")
            .Connect()
            .Expect("C4")
            .Expect("C6")
            .Expect("D1")
            .Do("PLAY", c => c.ClickPlay())
            .Expect("E1")
            .Expect("E2")
            .Expect("E3")
            .RequireEventually(
                "the server ran a queue before the transfer (LoginBase 0xa6 QueueUpdateGameMode / QueueExit)",
                c => c.Ledger.Count(LoginBase, 0x08) > 0 || c.Ledger.Count(LoginBase, 0x04) > 0,
                TimeSpan.FromSeconds(20),
                "docs/11 match flow; wire-20260829-150206 15:02:41-46 a6 08 x3 then a6 04")
            // F1's four-second budget is measured from ClientBeginZoning, not from the PLAY click:
            // the client's own retry timer alone puts 4.542 s between the two (docs/72 §3.4).
            .Expect(HarnessMilestone.ZoningBegun, TimeSpan.FromSeconds(30), after: "the second transfer request")
            .Require(
                "ClientBeginZoning named zone Z2",
                c => string.Equals(c.Ledger.ZoningZoneName, "Z2", StringComparison.Ordinal),
                "docs/11 — the match world; the menu bootstrap is LoginZone and zoning is the transition to Z2")
            .Require(
                "PlayerWorldTransferReply arrived before ClientBeginZoning",
                c => c.Milestones.At(HarnessMilestone.TransferReplyReceived) is TimeSpan reply
                    && c.Milestones.At(HarnessMilestone.ZoningBegun) is TimeSpan zoning
                    && reply <= zoning,
                "docs/71 §6 — 05 ed always precedes 05 0b; the client is told the transfer succeeded first")
            .Expect("F1")
            .Expect("F2")
            .Expect("F3")
            .Expect("F4", occurrence: 2)
            .Read("zoning", c =>
                $"ClientBeginZoning at {c.Milestones.At(HarnessMilestone.ZoningBegun)?.TotalSeconds:F3}s, "
                + $"ClientIsReady at {c.Milestones.At(HarnessMilestone.ZoningClientIsReadySent)?.TotalSeconds:F3}s, "
                + $"second ch2 at {c.Milestones.At(HarnessMilestone.ZoningMovementResumed)?.TotalSeconds:F3}s");

    /// <summary>
    /// <b>S4 — the drop: StartMatch, air spawn, parachute, the AutoMount echo, descent, landing.</b>
    ///
    /// <para>The mount is not a reflex. docs/71 §8: the client's echo of the server's
    /// <c>Vehicle.AutoMount</c> always follows both its own teleport ack and its
    /// <c>06 02 01 ClientFinishedLoading</c>, and it is the server's own packet with two bytes
    /// changed, never a synthesised one.</para>
    ///
    /// <para>The <b>descent is the client's</b>, and the harness cannot compute it: the August
    /// client falls under its own physics and announces the touchdown with one null-guid
    /// <c>Vehicle.Dismiss</c> (06 88 18) — 1 of 1 in each known-good capture. So this scenario
    /// replays the recorded channel-3 chute stream for the derived fall time (850 m at the client's
    /// 40.4 m/s ≈ 21 s; the owner's own 13:21 session took 20.6 s) and then sends that packet. What
    /// is being tested is the server's landing burst and the loot it arms, not the client's
    /// physics.</para>
    /// </summary>
    public static Scenario S4Drop() =>
        Scenario.Named("S4 StartMatch → air spawn → parachute → AutoMount echo → descent → landing")
            .Connect()
            .Expect("C4")
            .Expect("C6")
            .Expect("D1")
            .Do("PLAY", c => c.ClickPlay())
            .Expect("E2")
            .Expect(HarnessMilestone.ZoningBegun, TimeSpan.FromSeconds(30), after: "the second transfer request")
            .Expect("F1")
            .Expect("F3")
            .Expect(HarnessMilestone.TeleportStartReceived, TimeSpan.FromSeconds(120), after: "entering the world")
            .RequireEventually(
                "the server sent StartMatch (ce 0016) and the in-match HUD state (ce 0015)",
                c => c.Ledger.Zone.Any(r => r.Opcode == GameModeHud && r.Sub16 == 0x0016)
                    && c.Ledger.Zone.Any(r => r.Opcode == GameModeHud && r.Sub16 == 0x0015),
                TimeSpan.FromSeconds(60),
                "docs/11 §match start; wire-20260829-150206 15:03:22.259 ce 16 then ce 15")
            .RequireEventually(
                "the server moved the client to the air spawn (ClientUpdate.UpdateLocation 11 000a)",
                c => c.Ledger.Zone.Any(r => r.Opcode == ClientUpdateBase && r.Sub16 == 0x000A),
                TimeSpan.FromSeconds(60),
                "docs/12 §5 — the 850 m sky spawn is an UpdateLocation with WaitForTeleport set")
            .RequireEventually(
                "the parachute arrived as an AddLightweightVehicle (0xd7)",
                c => c.Ledger.Vehicles.Count > 0,
                TimeSpan.FromSeconds(60),
                "docs/12 §1 — d7 body + tail, then Vehicle.AutoMount; wire-20260829-150206 15:03:22.263")
            .Expect("G1")
            .Expect("G2")
            .Expect("G3")
            .Expect("G4")
            .Require(
                "the client's echo named the vehicle the server introduced",
                c => c.Responder?.PendingAutoMount is byte[] mount
                    && c.Ledger.Vehicles.Any(v => ServerPackets.Mentions(mount, v.Guid)),
                "docs/71 §8 — the echo is the server's own bytes with header 05→06 and byte 11 01→00")
            .Read("air spawn", c => c.Ledger.Vehicles.Count == 0
                ? "(no parachute)"
                : $"chute {c.Ledger.Vehicles[0].Guid} at {c.Ledger.Vehicles[0].Position}")
            // The modelled client has to be standing somewhere real when it lands, because the
            // server takes the world it builds around the player from the position the client
            // reports. See GroundWalkPool.
            .Do("stand somewhere real in Z2: swap the channel-2 pool to a recorded on-foot stream",
                c => c.Responder!.UsePlayerMovement(GroundWalkPool()))
            // The client-owned descent. Replaying channel 3 is what the responder already does once
            // mounted; the wait is the derived fall time and the Dismiss is the client's own signal.
            .Wait(TimeSpan.FromSeconds(22))
            .Do("touchdown: the client's one null-guid Vehicle.Dismiss", c => c.ReportTouchdown())
            .Expect("G5")
            .RequireEventually(
                "the server ran the landing burst: DismountResponse (70 04) and RemovePlayer of the chute",
                c => c.Ledger.Count(MountBase, 0x04) > 0
                    && c.Ledger.Vehicles.Count > 0
                    && c.Ledger.RemovedObjects.Contains(c.Ledger.Vehicles[0].Guid),
                TimeSpan.FromSeconds(15),
                "docs/12 §6 — 70 04 collapses the chute, 88 02 / 88 01 clear possession, 0f 01 removes the canopy")
            .RequireEventually(
                "the landing armed ground loot: AddLightweightNpc objects on the floor",
                c => c.Ledger.Npcs.Count > 0,
                TimeSpan.FromSeconds(30),
                "docs/13 §9 step 1 — the touchdown is the first moment the player stands on terrain "
                + "with a known position, which is where the ground loot lands")
            .Expect("I0")
            .Read("landing", c =>
                $"{c.Ledger.Npcs.Count} ground object(s), centroid "
                + $"{ServerLedger.Centroid(c.Ledger.Npcs)}, {c.Ledger.ProximateItemLists} ProximateItems list(s)");

    /// <summary>
    /// <b>S5 — the loot loop: <c>0F 45</c> answered with <c>0xda</c>, then a pickup that succeeds.</b>
    ///
    /// <para>docs/19 §2: the client sends exactly one <c>Character.FullCharacterDataRequest</c>
    /// (0F 45) per <c>AddLightweightNpc</c> and never retries; without the <c>0xda</c>
    /// <c>LightweightToFullNpc</c> answer the object stays half-created forever — visible, and
    /// impossible to pick up. docs/71 §14.2 records that no capture contains a reply, because this
    /// server sends the <c>0xda</c> proactively with every <c>0xd6</c>; this scenario therefore
    /// tests the <i>demand-side</i> path that a real client would fall back on, and it is a path no
    /// capture has ever exercised.</para>
    ///
    /// <para>The pickup is the [F] press as the client actually sends it — <c>09 15 PlayerSelect</c>
    /// and <c>09 07 InteractRequest</c> from one press, 3 ms apart (docs/61 §5) — and success is
    /// asserted on the two packets the server owes: <c>ClientUpdate.ItemAdd</c> (11 0002) and
    /// <c>Character.RemovePlayer</c> (0f 01) naming the object that was taken.</para>
    /// </summary>
    public static Scenario S5Loot()
    {
        var targets = new List<LightweightEntity>();
        var beforeRequest = new Dictionary<ulong, int>();
        ulong picked = 0;

        return Scenario.Named("S5 ground loot → 0F 45 answered with 0xda → a pickup succeeds")
            .Then(S4Drop())
            .Do("choose the objects a player would press F on", c =>
            {
                targets.Clear();
                // Everything the landing burst introduced. Doors ride the same 0xd6, so the
                // candidates are tried in order rather than filtered by a guid range the client
                // knows nothing about.
                targets.AddRange(c.Ledger.Npcs.Take(12));

                // This server also sends the 0xda PROACTIVELY with every 0xd6, so "a 0xda naming
                // this guid exists" is already true before the request. Counting first is what
                // makes the next assertion about the ANSWER rather than about the proactive send.
                beforeRequest.Clear();
                foreach (LightweightEntity target in targets)
                {
                    beforeRequest[target.Guid] = c.Ledger.FullNpcMentions(target.Guid);
                }
            })
            .Do("the client asks for full data on the new objects (0F 45, one per guid)",
                c => c.RequestFullCharacterData(targets.Select(t => t.Guid)))
            .Expect("I1")
            .RequireEventually(
                "each 0F 45 drew a FRESH LightweightToFullNpc (0xda) naming that guid",
                c => targets.Count > 0
                    && targets.All(t => c.Ledger.FullNpcMentions(t.Guid) > beforeRequest[t.Guid]),
                TimeSpan.FromSeconds(10),
                "docs/19 §2, docs/13 §3a — the 0xda apply keys on the transient id, but the packet "
                + "carries the guid; an unanswered 0F 45 leaves the object stuck in 'full data "
                + "pending' forever, because the client asks once and never retries. docs/71 §14.2 "
                + "records that NO capture contains a reply, because this server volunteers the 0xda "
                + "with every 0xd6 — so this is the first time the demand-side path has been exercised")
            .Do("press F on the ground objects until one is granted", async (c, token) =>
            {
                foreach (LightweightEntity target in targets)
                {
                    int adds = c.Ledger.ItemAdds;
                    await c.PressInteractAsync(target.Guid, target.Position, token).ConfigureAwait(false);
                    await c.Clock.DelayAsync(TimeSpan.FromMilliseconds(400), token).ConfigureAwait(false);
                    if (c.Ledger.ItemAdds > adds && c.Ledger.RemovedObjects.Contains(target.Guid))
                    {
                        picked = target.Guid;
                        return;
                    }
                }
            })
            .Expect("I2")
            .Measure(
                "the pickup was granted and the world object removed",
                c => picked != 0
                    ? null
                    : $"none of the {targets.Count} object(s) offered produced a ClientUpdate.ItemAdd "
                        + $"(11 0002) and a Character.RemovePlayer (0f 01); {c.Ledger.ItemAdds} ItemAdd(s) "
                        + $"and {c.Ledger.RemovedObjects.Count} removal(s) in the whole session",
                "docs/13 §5 — the grant goes out before the removal, so a refused pickup can never "
                + "vanish the object; both packets together are what a successful pickup looks like")
            .RequireEventually(
                "the pickup panel was republished (ProximateItemBase) after the grant",
                c => c.Ledger.ProximateItemLists >= 2,
                TimeSpan.FromSeconds(10),
                "docs/13 §9 step 5 — the panel drops the claimed row only when the list is republished")
            .Read("full data", c =>
                $"{targets.Count} guid(s) asked; fresh 0xda for "
                + $"{targets.Count(t => c.Ledger.FullNpcMentions(t.Guid) > beforeRequest[t.Guid])} of them")
            .Read("pickup", c => picked == 0
                ? "nothing was granted"
                : $"object {picked} granted; {c.Ledger.ItemAdds} ItemAdd(s), "
                    + $"{c.Ledger.ProximateItemLists} ProximateItems list(s)");
    }

    /// <summary>
    /// <b>S6 — loot streaming: does the world follow the player?</b>
    ///
    /// <para>The owner's report was <i>"no matter what building I went into there was no loot"</i>.
    /// docs/52 diagnosed it: <c>RealGroundLootArmed</c> was a one-way latch, so the landing burst was
    /// the only loot the whole match ever saw and the other 39,708 items on the Z2 floor were never
    /// mentioned again. This scenario is the live test of the fix.</para>
    ///
    /// <para><b>How the client moves, and what that costs.</b> The harness never synthesises a
    /// channel-2 packet (docs/71 §1, §14.3), so it walks by replaying a real client's recorded
    /// on-foot stream — <see cref="GroundWalkPool"/>, four and a half minutes of a real session
    /// walking across PV Commercial East, from (-258.3, 33.0, 305.4) out to (-306.0, 35.1, 500.4)
    /// and back. That is the longest continuous on-foot channel-2 stretch in the whole 128-capture
    /// corpus, and its extremes are 200.7 m apart; a scenario cannot demand a longer walk than any
    /// client has ever been recorded taking. Nothing here is decoded from channel 2: the distances
    /// are measured from the positions inside the server's own <c>AddLightweightNpc</c> bodies.</para>
    /// </summary>
    public static Scenario S6LootStreaming()
    {
        int landingMark = 0;
        Vector3 landingCentroid = Vector3.Zero;
        int landingCount = 0;

        return Scenario.Named("S6 walk 200+ m from the landing point and assert new loot spawns")
            .Then(S4Drop())
            .Do("note the landing burst", c =>
            {
                landingMark = c.Ledger.Mark();
                IReadOnlyList<LightweightEntity> landed = c.Ledger.Npcs;
                landingCount = landed.Count;
                landingCentroid = ServerLedger.Centroid(landed);
                c.Notes.Add($"landing burst: {landingCount} object(s) around {landingCentroid}");
            })
            // The pool the touchdown installed keeps replaying at the client's own 24 ms cadence, so
            // the modelled player walks the recorded route without the scenario touching the wire.
            .Wait(TimeSpan.FromSeconds(100))
            .Measure(
                "the server kept spawning ground objects as the player walked",
                c => c.Ledger.NpcsSince(landingMark).Count > 0
                    ? null
                    : $"no AddLightweightNpc at all in the {c.Ledger.Since(landingMark).Count} message(s) "
                        + "the server sent during the walk — this is the empty-buildings regression",
                "docs/52 — the loot arm of the world pump re-streams once the player has moved half a "
                + "radius; without it the landing burst is the only loot a match ever sees")
            .Measure(
                "loot appeared more than 200 m from the landing loot",
                c =>
                {
                    IReadOnlyList<LightweightEntity> fresh = c.Ledger.NpcsSince(landingMark);
                    if (fresh.Count == 0)
                    {
                        return "no new objects to measure";
                    }

                    float furthest = fresh.Max(e => ServerLedger.HorizontalDistance(landingCentroid, e.Position));
                    return furthest > 200f
                        ? null
                        : $"the furthest of the {fresh.Count} new object(s) is {furthest:F1} m from the "
                            + $"landing loot at {landingCentroid}, not the 200 m the walk covers";
                },
                "docs/52 §Integration — loot is streamed around the player's reported position, so a "
                + "player who has walked 200 m must be given loot 200 m from where they landed")
            .Measure(
                "the server evicted the loot left behind",
                c => c.Ledger.RemovedObjects.Count > 1
                    ? null
                    : $"only {c.Ledger.RemovedObjects.Count} Character.RemovePlayer in the whole session; "
                        + "the landing loot was never despawned, so the live set can only grow",
                "docs/52 — the streamer evicts past EffectiveDespawnRadius (90 m) as it spawns, "
                + "which is what keeps the live count under MaxLive")
            .Read("streaming", c =>
            {
                IReadOnlyList<LightweightEntity> fresh = c.Ledger.NpcsSince(landingMark);
                float furthest = fresh.Count == 0
                    ? 0f
                    : fresh.Max(e => ServerLedger.HorizontalDistance(landingCentroid, e.Position));
                return $"{landingCount} at the landing → {fresh.Count} more during the walk, furthest "
                    + $"{furthest:F1} m from the landing centroid; {c.Ledger.RemovedObjects.Count} removal(s), "
                    + $"{c.Ledger.ProximateItemLists} ProximateItems list(s)";
            });
    }

    /// <summary>
    /// <b>S7 — the docs/32 and docs/45 regression guards, as live assertions.</b>
    ///
    /// <para>These four are guards precisely because each one, once, cost the owner a session:</para>
    /// <list type="number">
    /// <item><description><c>UnsetCharacterEquipmentSlot</c> (0x94 sub 3) is never sent. It appears
    ///   55× and 53× in the two sessions the client refused and 0× in the two it accepted
    ///   (docs/02 2026-08-29, docs/32).</description></item>
    /// <item><description>No equipment row ever names body slot 7 (RHand). 4 of 4 packets carrying
    ///   one killed the client within a millisecond, over three unrelated items; 5 of 5 without one
    ///   were accepted (docs/45 §2/§3).</description></item>
    /// <item><description>The appearance <c>ReferenceData</c> payload is exactly 1,273,927 bytes —
    ///   the filtered Z1-compatible table (1,268,831 B) plus the 5,096 B of Cranberry-authored
    ///   rows docs/106's addendum adds for the thirteen grey loot wearables. The captured table
    ///   alone is 1,268,872 B on the wire in
    ///   wire-20260829-150206, and the 41-byte difference is the envelope.</description></item>
    /// <item><description>The match-zoning opcode order matches the known-good capture:
    ///   <c>0b → ca → ReferenceData(DynamicAppearanceDefinitions) → ce 0010 → 03 → 94 01 →
    ///   (worn skins) → ReferenceData(ProfileDefinitions) → 05</c>. docs/32's first difference was
    ///   an ordering change, and its third regression is that the dress must precede the worn
    ///   skins.</description></item>
    /// </list>
    /// </summary>
    public static Scenario S7RegressionGuards() =>
        Scenario.Named("S7 the docs/32 + docs/45 regression guards, live")
            .Then(S3ZoneToZ2())
            .Measure(
                "guard 2: UnsetCharacterEquipmentSlot (0x94 sub 3) was never sent",
                c => c.Ledger.EquipmentSubCount(0x03) == 0
                    ? null
                    : $"{c.Ledger.EquipmentSubCount(0x03)} of them arrived",
                "docs/02 2026-08-29 / docs/32 — 0x in wire-20260829-150206 and -184346 (both entered Z2), "
                + "55x in -151627 and 53x in -173352 (neither did, the second ending in G10)")
            .Measure(
                "guard 2b: no SetCharacterEquipmentSlot(s) either (0x94 sub 2 / sub 4)",
                c => c.Ledger.EquipmentSubCount(0x02) + c.Ledger.EquipmentSubCount(0x04) == 0
                    ? null
                    : $"sub 2 x{c.Ledger.EquipmentSubCount(0x02)}, sub 4 x{c.Ledger.EquipmentSubCount(0x04)}",
                "docs/56 §1.8 — a slot-7 binding sent through 0x94/02 or 0x94/04 reaches the identical "
                + "faulting handler family while bypassing ActiveHandRowGuard, which lives inside "
                + "SetCharacterEquipmentWithSlots.WriteTo")
            .Measure(
                "guard 5: no equipment row ever named body slot 7 (RHand)",
                c => c.Ledger.EquipmentSlotIdsEverSent().Contains(ActiveHandSlotId)
                    ? $"a 0x94/01 row bound slot {ActiveHandSlotId}; slots seen: "
                        + string.Join(", ", c.Ledger.EquipmentSlotIdsEverSent())
                    : null,
                "docs/45 §2/§3 — 4 of 4 packets with a slot-7 row killed the client within a millisecond "
                + "over three unrelated item definitions; 5 of 5 without one were accepted")
            .Measure(
                "guard 1: the appearance ReferenceData payload is exactly 1,273,927 bytes",
                c => c.Ledger.ReferenceData.TryGetValue("DynamicAppearanceDefinitions", out ReferenceDataHead? head)
                    ? head.DeclaredPayloadLength == AppearancePayloadBytes
                        ? null
                        : $"it declared {head.DeclaredPayloadLength:N0} bytes in a {head.MessageLength:N0}-byte message"
                    : "no DynamicAppearanceDefinitions ReferenceData arrived at all",
                "docs/32 / the host's own filtered table; wire-20260829-150206 15:02:47.163 is "
                + "1,268,872 B on the wire = 41 B of envelope + 1,268,831 B of payload, and "
                + "docs/106's addendum adds 5,096 B of Cranberry-authored rows on top "
                + "(CRANBERRY_APPEARANCE_AUTHORED_ROWS=0 restores the captured-only length)")
            .Measure(
                "guard 3+4: the match-zoning opcode order matches wire-20260829-150206",
                c => CompareZoningOrder(c.Ledger.MatchZoningOrder()),
                "docs/32 'The two differences' — the ordering change is the first of the two, and the "
                + "dress-before-worn-skins order is regression 3; wire-20260829-150206 15:02:47.162-195")
            .Read("zoning order", c => string.Join(" → ", c.Ledger.MatchZoningOrder()))
            .Read("equipment", c =>
                $"0x94/01 x{c.Ledger.EquipmentSubCount(0x01)}, slots ever bound: "
                + (c.Ledger.EquipmentSlotIdsEverSent().Count == 0
                    ? "(none)"
                    : string.Join(", ", c.Ledger.EquipmentSlotIdsEverSent())));

    /// <summary>
    /// <b>S8 — the inverse of F1: does the server stop talking to a client that never answers?</b>
    ///
    /// <para>docs/32's third fix item, and the one nothing in the repository could prove: "the
    /// lobby HUD, StartMatch and the parachute currently fire on <c>Task.Delay</c> regardless of
    /// client state. They must be gated on the client's ClientIsReady / ClientFinishedLoading for
    /// the new zone, so a failed transition surfaces as a stalled match instead of a server
    /// monologue." The 2026-08-29 outage is that monologue: 17:36:04 ClientBeginZoning, then
    /// silence from the client, then a lobby HUD at +15 s, StartMatch at +35 s and a parachute
    /// handed to a client that was still on its loading screen.</para>
    ///
    /// <para>The modelled client here does everything a hung client is recorded doing (the
    /// WallOfData teardown, the 0x57, the single 49-byte channel-2 packet) and nothing more. The
    /// assertion is an <b>absence</b>: 50 s later — past the 15 s lobby timer and the 20 s
    /// countdown — no StartMatch, no lobby countdown and no parachute have arrived.</para>
    ///
    /// <para>This does not reproduce the hang's cause, which docs/71 §14.6 leaves unisolated. It
    /// reproduces its <i>shape</i>, which is all the suppression guard needs to see.</para>
    /// </summary>
    public static Scenario S8SilentClientIsNotTalkedAt() =>
        Scenario.Named("S8 a client that never answers the zoning burst is not dropped out of an aeroplane")
            .Connect()
            .Expect("C4")
            .Expect("C6")
            .Expect("D1")
            .Do("PLAY", c => c.ClickPlay())
            .Expect("E2")
            .Do("the client goes silent from here: it will not send ClientIsReady (Zoning)",
                c => c.Responder!.AnswerZoning = false)
            .Expect(HarnessMilestone.ZoningBegun, TimeSpan.FromSeconds(30), after: "the second transfer request")
            .Wait(TimeSpan.FromSeconds(50))
            .Measure(
                "the client really did stay silent (no ClientIsReady for the new zone)",
                c => c.Milestones.Has(HarnessMilestone.ZoningClientIsReadySent)
                    ? "the harness answered after all, so the probe proves nothing"
                    : null,
                "docs/71 §12.1 — 0 of 12 hung sessions ever sent it")
            .Measure(
                "the server did NOT send StartMatch to a client that never became ready",
                c => c.Ledger.Zone.Any(r => r.Opcode == GameModeHud && r.Sub16 == 0x0016)
                    ? "StartMatch (ce 0016) went out anyway"
                    : null,
                "docs/32 fix 3 / docs/35 §5f — the suppression guard is decided at fire time, not arm time")
            .Measure(
                "no parachute was handed to it either",
                c => c.Ledger.Vehicles.Count > 0
                    ? $"{c.Ledger.Vehicles.Count} AddLightweightVehicle(s) arrived"
                    : null,
                "docs/32 — '17:36:39 match: parachute guid=8195 ... sent blind'")
            .Measure(
                "no lobby countdown was drawn on its loading screen",
                c => c.Ledger.Zone.Any(r => r.Opcode == GameModeHud && r.Sub16 == 0x000F)
                    ? "the lobby countdown (ce 000f) went out anyway"
                    : null,
                "docs/32 — '17:36:19 match: lobby HUD ... sent blind'")
            .Read("silence", c =>
                $"{c.Ledger.Zone.Count} server message(s) after the zoning burst; "
                + $"ce 0016 x{c.Ledger.Zone.Count(r => r.Opcode == GameModeHud && r.Sub16 == 0x0016)}, "
                + $"ce 000f x{c.Ledger.Zone.Count(r => r.Opcode == GameModeHud && r.Sub16 == 0x000F)}, "
                + $"0xd7 x{c.Ledger.Vehicles.Count}");

    /// <summary>
    /// <b>S9 — the draw: everything the server must say to put a gun in the hand, and nothing that
    /// stops the hand from walking.</b>
    ///
    /// <para>S5 gets an item granted; this asserts the five things a draw is made of, each of which
    /// was a separate finding of the 2026-09-01 survey:</para>
    ///
    /// <list type="number">
    /// <item><description>the <c>ReferenceData "WeaponDefinitions"</c> record carries its own id at
    ///   bytes 4..7 (<c>def+0x18</c>, the key the local weapon resolves its definition by —
    ///   refute-1 §2) and <c>0x3f800000</c> at 57..60 and 61..64 (<c>def+0x58 TurnModifier</c> and
    ///   <c>def+0x5c MovementModifier</c>, the two multipliers the client applies to the local
    ///   player's turn rate and movement speed; Cranberry shipped <b>0</b> in all 61 records until
    ///   wave 11, and 0 is the freeze);</description></item>
    /// <item><description>the binding is a <c>94 02 SetCharacterEquipmentSlot</c> row for body
    ///   slot 7, sent last (docs/95) — never a slot-7 row on the whole-character
    ///   <c>94 01</c>;</description></item>
    /// <item><description>guard G2 is still live and finds nothing Fatal in the live
    ///   stream;</description></item>
    /// <item><description><c>0f 20 WeaponStance</c> goes out as the animation step it
    ///   is (S6 §7.3);</description></item>
    /// <item><description><c>86 04 SetLoadoutSlots</c> ends with the trailing <c>u32
    ///   currentSlotId</c> the 1148 reader requires before it applies the current slot, the wheel
    ///   or the listeners (S6 §7.1) — its absence is why the client never sent
    ///   <c>86 06</c>;</description></item>
    /// <item><description>and the <see cref="LocomotionLockDetector"/> is silent on the movement
    ///   this scenario drives, so a LOCK in a live run is the server's doing and not the replay
    ///   pool's.</description></item>
    /// </list>
    ///
    /// <para><b>This scenario is expected to fail until the draw is finished.</b> That is what it is
    /// for: it was written before the fire path landed so that "wield works" has an oracle that is
    /// not a person saying so (OVERHAUL-PLAN §2, O4). Read the failing Measure, not the pass.</para>
    /// </summary>
    public static Scenario S9WieldDraw()
    {
        // Body slot 7 rows read out of the 94 02 packets, so a failure can print what WAS bound.
        var handRows = new List<(uint Slot, ulong Guid)>();

        return Scenario.Named("S9 the draw: definitions, the slot-7 binding, the stance, the loadout trailer")
            .Then(S5Loot())
            .Wait(TimeSpan.FromSeconds(2))
            .Do("read the slot-7 bindings out of every 94 02", c =>
            {
                handRows.Clear();
                foreach (byte[] payload in c.Ledger.Payloads(EquipmentBase, sub8: SetEquipmentSlotSub))
                {
                    if (TryReadEquipmentSlotBinding(payload) is (uint slot, ulong guid))
                    {
                        handRows.Add((slot, guid));
                    }
                }
            })
            .Measure(
                "the WeaponDefinitions table arrived and every record carries its id and both 1.0f multipliers",
                c => DescribeWeaponDefinitions(c.Ledger.WeaponDefinitions),
                "refute-1 §2 / refute-2 §3.2 / S5b — def+0x18 is the lookup key (FUN_1421e5f50 is "
                + "mov eax,[rcx+0x18]), def+0x5c multiplies movement speed (FUN_14228fa20) and "
                + "def+0x58 the turn rate (FUN_140c4c440); the client's own record constructor "
                + "FUN_1421e5b70 presets both to 1.0f, and wire-20260831-192743.txt L302 shows "
                + "Cranberry shipping 0 in all 61 records")
            .Measure(
                "the gun was bound through 94 02, on body slot 7",
                _ => handRows.Any(row => row.Slot == ActiveHandSlotId)
                    ? null
                    : handRows.Count == 0
                        ? "no 94 02 SetCharacterEquipmentSlot was sent at all"
                        : "94 02 rows bound slots [" + string.Join(", ", handRows.Select(r => r.Slot)) + "], not 7",
                "docs/95 §2 — the draw's last packet; the owner's Z1 server never puts a slot-7 row "
                + "on the whole-character 94 01 in 60 draws")
            .Measure(
                "guard G2 is live and clean: no slot-7 row on a whole-character 94 01",
                c => c.Ledger.EquipmentSlotIdsEverSent().Contains(ActiveHandSlotId)
                    ? "a 0x94/01 row bound slot 7; slots seen: "
                        + string.Join(", ", c.Ledger.EquipmentSlotIdsEverSent())
                    : null,
                "RegressionGuards G2 — the shape did not crash the August client on 2026-08-31, it "
                + "froze the player until he dropped the item (S5a §2.2)")
            .Measure(
                "Character.WeaponStance (0f 20) was sent",
                c => c.Ledger.Count(CharacterBase, WeaponStanceSub) > 0
                    ? null
                    : "no 0f 20 in the whole session",
                "S6 §7.3 — the animation step of the draw; the owner's own round-26 finding in "
                + "ZoneAbilities.cs:770-856, re-expressed for 1148")
            .Measure(
                "every 86 04 SetLoadoutSlots ends with the trailing u32 currentSlotId",
                c => DescribeLoadoutTrailers(c.Ledger.Payloads(LoadoutsBase, sub8: SetLoadoutSlotsSub)),
                "S6 §7.1 — FUN_140d32b70 reads a u32 into packet+0x28 AFTER the list; without it "
                + "the applier skips the current slot on every loadout packet Cranberry has sent")
            .Measure(
                "the detector is silent on the movement this scenario drove",
                _ => DescribeDetector(GroundWalkPool()),
                "OVERHAUL-PLAN §2 insight 12 — the loop's grade only means something if the replay "
                + "pool itself is not a freeze")
            .Read("definitions", c => c.Ledger.WeaponDefinitions is byte[] message
                ? $"{message.Length:N0} B, "
                    + (WeaponDefinitionsReader.TryReadListZero(message) is IReadOnlyList<WeaponDefinitionRecord> list
                        ? $"{list.Count} record(s), {WeaponDefinitionsReader.Offenders(list).Count} not at the client's defaults"
                        : "list 0 did not walk")
                : "none arrived")
            .Read("hand", _ => handRows.Count == 0
                ? "no 94 02 rows"
                : string.Join(", ", handRows.Select(r => $"slot {r.Slot} <- {r.Guid:x16}")));
    }

    /// <summary>Base 0x94, the equipment family.</summary>
    private const byte EquipmentBase = 0x94;

    /// <summary>Sub 2: <c>SetCharacterEquipmentSlot</c>, one row (docs/95 §2).</summary>
    private const byte SetEquipmentSlotSub = 0x02;

    /// <summary>Base 0x0f, the character family.</summary>
    private const byte CharacterBase = 0x0F;

    /// <summary>Sub 0x20: <c>Character.WeaponStance</c>.</summary>
    private const byte WeaponStanceSub = 0x20;

    /// <summary>Base 0x86, the loadouts family.</summary>
    private const byte LoadoutsBase = 0x86;

    /// <summary>Sub 4: <c>SetLoadoutSlots</c>, the whole table.</summary>
    private const byte SetLoadoutSlotsSub = 0x04;

    /// <summary>
    /// <c>94 02</c>: <c>u8 base; u8 sub; u32 profileId; u64 characterGuid; u32 key; u32 slotId;
    /// u64 itemGuid; …</c> — the head <c>FUN_140a2cda0</c> reads, then the
    /// <c>FUN_140cd4f90</c> row. Only the slot and the guid are wanted here.
    /// </summary>
    private static (uint Slot, ulong Guid)? TryReadEquipmentSlotBinding(byte[] payload) =>
        payload.Length < 30
            ? null
            : (BitConverter.ToUInt32(payload, 18), BitConverter.ToUInt64(payload, 22));

    private static string? DescribeWeaponDefinitions(byte[]? message)
    {
        if (message is null)
        {
            return "no ReferenceData \"WeaponDefinitions\" arrived at all";
        }

        IReadOnlyList<WeaponDefinitionRecord>? records = WeaponDefinitionsReader.TryReadListZero(message);
        if (records is null || records.Count == 0)
        {
            return $"list 0 of the {message.Length:N0}-byte table did not walk to a record boundary";
        }

        IReadOnlyList<WeaponDefinitionRecord> offenders = WeaponDefinitionsReader.Offenders(records);
        return offenders.Count == 0
            ? null
            : $"{offenders.Count} of {records.Count} record(s) are not at the client's own defaults; "
                + "first three: " + string.Join(" | ", offenders.Take(3));
    }

    /// <summary>
    /// <c>86 04</c> is <c>1 + 1 + 8 + 4 + 4</c> of header, <c>29</c> bytes an entry, then the
    /// trailing <c>u32</c>. A message whose length is the header plus the entries and nothing else
    /// is the pre-S6 shape that made the client skip the current slot.
    /// </summary>
    private static string? DescribeLoadoutTrailers(IReadOnlyList<byte[]> payloads)
    {
        if (payloads.Count == 0)
        {
            return "no 86 04 SetLoadoutSlots was sent at all";
        }

        var short_ = new List<string>();
        foreach (byte[] payload in payloads)
        {
            if (payload.Length < 18)
            {
                short_.Add($"{payload.Length} B (shorter than the header)");
                continue;
            }

            int entries = BitConverter.ToInt32(payload, 14);
            int withTrailer = 18 + (29 * entries) + 4;
            if (payload.Length != withTrailer)
            {
                short_.Add($"{payload.Length} B for {entries} entr(ies); with the trailer it would be {withTrailer} B");
            }
        }

        return short_.Count == 0
            ? null
            : $"{short_.Count} of {payloads.Count} packet(s) are the wrong length: " + string.Join(" | ", short_.Take(3));
    }

    /// <summary>
    /// Replays the scenario's own movement pool through the detector. A LOCK here would mean the
    /// recorded walk is itself a freeze, and every live grade taken with it would be worthless.
    /// </summary>
    private static string? DescribeDetector(MovementReplay pool)
    {
        var detector = new LocomotionLockDetector();
        long at = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            // The pool carries no timestamps, so the client's own median channel-2 interval is
            // used: 22-26 records a second while moving (S5a §3.2).
            at += 45;
            detector.Observe(ClientMovementUpdate.Parse(pool.Next()), at);
        }

        return detector.LockCount == 0
            ? null
            : $"the {pool.Count}-record walk pool reported LOCK {detector.LockCount} time(s): {detector.Last.ToLogLine()}";
    }

    /// <summary>
    /// <b>S10 - death and victory: does a player who dies in the gas actually die?</b>
    ///
    /// <para>docs/97, lane 1D-lite. Before it, a gas death sent one <c>ce 04</c> and one
    /// <c>ce 09</c> and nothing else: the wrap-up slides filled in, but the character never fell
    /// over and the client was never taken out of its own match, because
    /// <c>0f 4f StartMultiStateDeath</c> is the packet that does that (docs/21 §2h) and nothing
    /// sent it. This scenario is the live assertion of the fix.</para>
    ///
    /// <para><b>How it kills the client.</b> <c>CRANBERRY_GAS_PRESET=Sprint</c> is the retail
    /// ladder at a fifth of its length, so the whole match - reveal, hold and every shrink - runs
    /// inside the budget below, and a modelled client that lands and then stands still is outside
    /// the circle long before the last phase closes. The scenario deliberately does <b>not</b>
    /// move: standing in the gas is the only way a harness can reach a death without a second
    /// player or an admin command.</para>
    ///
    /// <para><b>What is asserted:</b> the ragdoll (<c>0f 4f</c>) arrives, exactly one
    /// <c>ce 04 DeathInfo</c> arrives and its trailing cause is <c>0x42</c>
    /// (<c>UI.Results.Rank.Gas</c>) - a second one would be the old inline gas send firing beside
    /// the new burst - and a <c>ce 09</c> follows. The kill feed (<c>0f 48</c>) is <i>read</i>
    /// rather than required: for a gas death its killer guid is 0, and whether the client draws a
    /// feed line for a killer of 0 is not something the wire can answer.</para>
    ///
    /// <para><b>Live only</b>, like every scenario in this file: it needs a running host, and
    /// <c>LiveFact</c> skips it without <c>CRANBERRY_HARNESS_LIVE=1</c>. It also needs the host
    /// started with <c>CRANBERRY_GAS_PRESET=Sprint</c>; on the retail ladder the death is twenty
    /// minutes away and the budget below fails honestly rather than hanging.</para>
    /// </summary>
    public static Scenario S10DeathAndVictory()
    {
        var mark = 0;

        return Scenario.Named("S10 stand in the gas until it kills: 0f 4f + ce 04 cause 0x42 + ce 09")
            .Then(S4Drop())
            .Do("stop moving: standing still in a Sprint-preset match is what makes the gas lethal",
                c => mark = c.Ledger.Mark())
            .RequireEventually(
                "the server sent StartMultiStateDeath (0f 4f) for the dying player",
                c => c.Ledger.Count(CharacterBase, StartMultiStateDeathSub) > 0,
                GasDeathBudget,
                "docs/97 §2, docs/21 §2h - 0f 4f is what flips the client's own is-dead bit and "
                + "drives it into run state 0x21; before lane 1D-lite a gas death sent none, so the "
                + "player kept standing there with an empty health bar")
            .RequireEventually(
                "a GameMode.DeathInfo (ce 04) followed it",
                c => c.Ledger.Payloads(GameModeHud, sub16: DeathInfoSub, since: mark).Count > 0,
                TimeSpan.FromSeconds(10),
                "docs/18 §3a - ce 04 fills the wrap-up slides; it does not open them")
            .Measure(
                "exactly one ce 04 for the death, carrying cause 0x42 (UI.Results.Rank.Gas)",
                c =>
                {
                    IReadOnlyList<byte[]> deaths =
                        c.Ledger.Payloads(GameModeHud, sub16: DeathInfoSub, since: mark);
                    if (deaths.Count != 1)
                    {
                        return $"{deaths.Count} ce 04(s) after the death, not 1 - a second one is "
                            + "the old inline gas send firing beside the new burst";
                    }

                    byte[] death = deaths[0];
                    uint cause = death.Length >= 4 ? BitConverter.ToUInt32(death, death.Length - 4) : 0;
                    return cause == GasDeathCause ? null : $"cause 0x{cause:x2}, not 0x{GasDeathCause:x2}";
                },
                "docs/97 §2 / DeathCauseCode.Gas - FUN_140bbb120:213-216 resolves 0x42 to "
                + "UI.Results.Rank.Gas; docs/15 §5's 'cause = 9' is the results-FILE enum")
            .RequireEventually(
                "the alive counter was republished (ce 09)",
                c => c.Ledger.Zone.Count(r => r.Opcode == GameModeHud && r.Sub16 == PlayersRemainingSub) > 0,
                TimeSpan.FromSeconds(10),
                "docs/97 §2 step 5 - ce 09 goes out on change; -1 hides the counter and belongs to "
                + "the lobby")
            .Read("kill feed", c =>
            {
                int feeds = c.Ledger.Count(CharacterBase, KilledBySub);
                return feeds == 0
                    ? "no 0f 48 (the envelope guid for a gas death is killer 0)"
                    : $"{feeds} x 0f 48 KilledBy";
            })
            .Read("death", c =>
                $"0f 4f x{c.Ledger.Count(CharacterBase, StartMultiStateDeathSub)}, "
                + $"ce 04 x{c.Ledger.Payloads(GameModeHud, sub16: DeathInfoSub, since: mark).Count}, "
                + $"ce 09 x{c.Ledger.Zone.Count(r => r.Opcode == GameModeHud && r.Sub16 == PlayersRemainingSub)}, "
                + $"ce 18 x{c.Ledger.Zone.Count(r => r.Opcode == GameModeHud && r.Sub16 == VictorySub)}");
    }

    /// <summary><c>Character.StartMultiStateDeath</c>.</summary>
    private const byte StartMultiStateDeathSub = 0x4F;

    /// <summary><c>Character.KilledBy</c>.</summary>
    private const byte KilledBySub = 0x48;

    /// <summary><c>GameMode.DeathInfo</c>.</summary>
    private const ushort DeathInfoSub = 0x0004;

    /// <summary><c>GameMode.PlayersRemaining</c>.</summary>
    private const ushort PlayersRemainingSub = 0x0009;

    /// <summary><c>GameMode.ShowVictoryScreen</c>.</summary>
    private const ushort VictorySub = 0x0018;

    /// <summary><c>DeathCauseCode.Gas</c> - the ce 04 wire value, not docs/15 §5's results file.</summary>
    private const uint GasDeathCause = 0x42;

    /// <summary>
    /// How long S10 waits for the gas to finish a stationary player. The Sprint preset is the
    /// retail ladder at 0.2x, so a full match is about five minutes; eight is that plus the drop
    /// and a margin, and it fails rather than hangs if the host was started on the retail ladder.
    /// </summary>
    private static readonly TimeSpan GasDeathBudget = TimeSpan.FromMinutes(8);

    /// <summary>
    /// The match-zoning burst of the known-good capture, with runs collapsed. The worn-skin rows
    /// (<c>ac</c>) are optional because their count is the wardrobe's, not the protocol's: the
    /// 2026-08-29 session had six selections and today's roster has none. Their <i>position</i> is
    /// not optional — regression 3 is that the dress precedes them.
    /// </summary>
    public static IReadOnlyList<string> KnownGoodZoningOrder { get; } =
    [
        "0b",                                   // ClientBeginZoning Z2
        "ca",                                   // UpdateWeatherData
        "17:DynamicAppearanceDefinitions",      // the appearance table
        "ce/0010",                              // the game-mode trio
        "03",                                   // SendSelfToClient
        "94/01",                                // SetCharacterEquipment - the dress
        "17:ProfileDefinitions",                // the empty profile table
        "05",                                   // ZoneDoneSendingInitialData
    ];

    private static string? CompareZoningOrder(IReadOnlyList<string> observed)
    {
        if (observed.Count == 0)
        {
            return "no ClientBeginZoning was seen at all";
        }

        string[] mandatory = [.. observed.Where(s => s != "ac")];
        if (!mandatory.SequenceEqual(KnownGoodZoningOrder))
        {
            return $"the burst was [{string.Join(" → ", observed)}], not "
                + $"[{string.Join(" → ", KnownGoodZoningOrder)}] (worn-skin 'ac' rows optional)";
        }

        int dress = observed.ToList().IndexOf("94/01");
        int firstSkin = observed.ToList().IndexOf("ac");
        return firstSkin >= 0 && firstSkin < dress
            ? "worn skin items (0xac) were sent BEFORE SetCharacterEquipment — docs/32 regression 3"
            : null;
    }
}
