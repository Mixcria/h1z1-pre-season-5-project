using Cranberry.Harness.Behaviour;

namespace Cranberry.Harness.Scenarios;

/// <summary>
/// The scenarios worth running against every build. Each one is the docs/71 §13 contract for a
/// phase, written out; adding a row to the catalogue and a line here is how a new client fact
/// becomes a regression guard.
/// </summary>
public static class StandardScenarios
{
    /// <summary>
    /// Phases A and B only: does a client get through login, the gateway handoff and the RC4
    /// arming point? This is the cheapest possible smoke test and the one that answers
    /// "is the host up and speaking the August protocol".
    /// </summary>
    public static Scenario Login() =>
        Scenario.Named("login-and-gateway-handoff")
            .Connect()
            .Require(
                "the gateway LoginReply decrypted at inbound keystream position 0",
                c => c.Milestones.Get(HarnessMilestone.GatewayLoginReplyDecrypted)?.Detail?.EndsWith("position 0", StringComparison.Ordinal) == true,
                "docs/71 §3 — 81 of 81 gateway sessions; the clear LoginRequest consumes no keystream");

    /// <summary>
    /// Phases A–D: through the zone bootstrap to the moment the loading screen closes. The close
    /// is the strongest in-world evidence short of a screenshot and the last thing in the menu
    /// burst that a hung client never reaches (docs/71 §5).
    /// </summary>
    public static Scenario Menu() =>
        Scenario.Named("menu-bootstrap")
            .Connect()
            .Expect("C1")
            .Expect("C2")
            .Expect("C4")
            .Expect("C5")
            .Expect("C6")
            .Require(
                "SendZoneDetails carried world type 4 (HeightfieldLod)",
                c => c.Responder?.ZoneType == 4,
                "docs/06/07 — FUN_140b37510 registers a world implementation for type 4 only")
            .Expect("D1")
            .Expect("D2");

    /// <summary>
    /// The Z2 zoning run: the reason the harness exists. F1 alone would have caught the four-hour
    /// hang the first time it happened.
    /// </summary>
    public static Scenario ZoneToZ2() =>
        Scenario.Named("z2-zoning")
            .Connect()
            .Expect("C4")
            .Expect("C6")
            .Expect("D1")
            .Do("PLAY", c => c.ClickPlay())
            .Expect("E2")
            .Expect(HarnessMilestone.ZoningBegun, TimeSpan.FromSeconds(15), after: "the second transfer request")
            .Expect("F1")
            .Expect("F2")
            .Expect("F3");

    /// <summary>
    /// The whole flow to a parachute mount: zoning, the SynchronizedTeleport handshake and the
    /// AutoMount echo, which the captures show is the server's own packet with two edits.
    /// </summary>
    public static Scenario Drop() =>
        Scenario.Named("z2-drop-and-mount")
            .Connect()
            .Expect("C4")
            .Expect("C6")
            .Expect("D1")
            .Do("PLAY", c => c.ClickPlay())
            .Expect("E2")
            // F1's four-second budget is measured from ClientBeginZoning, not from the PLAY click:
            // the client's own retry timer alone puts 4.542 s between the two. Waiting for the
            // zoning packet first is what makes the F1 budget mean what docs/71 §7 measured.
            .Expect(HarnessMilestone.ZoningBegun, TimeSpan.FromSeconds(15), after: "the second transfer request")
            .Expect("F1")
            .Expect("F3")
            .Expect(HarnessMilestone.TeleportStartReceived, TimeSpan.FromSeconds(90), after: "entering the world")
            .Expect("G1")
            .Expect("G2")
            .Expect("G3")
            .Expect("G4");

    /// <summary>
    /// Steady state: the three free-running client timers keep running and the server keeps
    /// answering. Note that H3 alone proves nothing — docs/71 §12.2 records a hung client whose
    /// GameTimeSync ticked forever.
    /// </summary>
    public static Scenario SteadyState() =>
        Scenario.Named("steady-state")
            .Connect()
            .Expect("C6")
            .Expect("H1")
            .Expect("H2")
            .Expect("H3")
            // Occurrence budgets count from where the scenario starts waiting, so five one-second
            // ticks need more than three seconds of headroom.
            .Expect(HarnessMilestone.KeepAlivePairSent, TimeSpan.FromSeconds(8), occurrence: 5)
            .Expect(HarnessMilestone.SynchronizationEchoed, TimeSpan.FromSeconds(12), occurrence: 2);

    /// <summary>Login, get to the menu, then leave the way the client leaves (docs/71 §11).</summary>
    public static Scenario Logout() =>
        Scenario.Named("logout")
            .Connect()
            .Expect("C6")
            .Expect("D1")
            .Quit();
}
