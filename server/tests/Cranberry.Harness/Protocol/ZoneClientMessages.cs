using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Protocol;

/// <summary>
/// Every message the harness sends as the client, built to the byte layouts recorded in
/// <c>wire-20260829-184346.txt</c> and catalogued in docs/71. Each builder returns a complete
/// gateway message including its header byte, so the channel assignment of docs/71 §1.1 is baked
/// in and cannot be got wrong at the call site.
/// </summary>
public static class ZoneClientMessages
{
    private const byte Ch0 = GatewayWire.ChannelDefault;
    private const byte Ch1 = GatewayWire.ChannelUi;

    private static byte[] Zone(byte channel, Action<WireWriter> write)
    {
        var w = new WireWriter(64);
        w.U8(GatewayWire.Header(GatewayWire.OpcodeTunnelToServer, channel));
        write(w);
        return w.ToArray();
    }

    // ---- Phase C: menu bootstrap (docs/71 §4) -------------------------------------------------

    /// <summary><c>06 47 | str name | str body</c>. The client uploads its own log files this way.</summary>
    public static byte[] ClientLog(string name, string body) =>
        Zone(Ch0, w => w.U8(0x47).CountedString(name).CountedString(body));

    /// <summary><c>26 33 | str locale</c> — channel 1. The first packet after the local player exists.</summary>
    public static byte[] SetLocale(string locale = "en_US") =>
        Zone(Ch1, w => w.U8(0x33).CountedString(locale));

    /// <summary><c>06 71 00000000</c>.</summary>
    public static byte[] ClientInitializationDetails(uint value = 0) =>
        Zone(Ch0, w => w.U8(0x71).LeU32(value));

    /// <summary><c>26 97</c> — channel 1, two bytes.</summary>
    public static byte[] GetContinentBattleInfo() => Zone(Ch1, w => w.U8(0x97));

    /// <summary><c>06 B1</c> — two bytes.</summary>
    public static byte[] GetRewardBuffInfo() => Zone(Ch0, w => w.U8(0xB1));

    /// <summary>
    /// <c>06 57 | 4 varying bytes | 01 | 15 zero bytes</c> (22 B). Unregistered in
    /// registrations-1148.json (docs/71 §14.1) and therefore opaque — but it is the packet
    /// <c>ClientIsReady</c> is timed from, and the client sends it once per bootstrap in 52 of 52
    /// zonings, hung ones included. The four varying bytes differ per bootstrap.
    /// </summary>
    public static byte[] Unregistered0x57(uint tag) =>
        Zone(Ch0, w =>
        {
            w.U8(0x57).LeU32(tag).U8(0x01);
            for (int i = 0; i < 15; i++)
            {
                w.U8(0);
            }
        });

    /// <summary><c>06 04</c> — two bytes, no body. The single sharpest client-health signal.</summary>
    public static byte[] ClientIsReady() => Zone(Ch0, w => w.U8(0x04));

    /// <summary><c>26 41 01 00</c> — channel 1. Follows ClientIsReady by 0 ms.</summary>
    public static byte[] LobbyGameDefinitionRequest() => Zone(Ch1, w => w.U8(0x41).LeU16(1));

    /// <summary>
    /// <c>06 02 00</c> in the menu and after zoning; <c>06 02 01</c> once, after the
    /// SynchronizedTeleport ack (docs/71 §8).
    /// </summary>
    public static byte[] ClientFinishedLoading(byte tail = 0) => Zone(Ch0, w => w.U8(0x02).U8(tail));

    /// <summary><c>06 11 50 00</c>. Arrives within 1 ms of ClientFinishedLoading.</summary>
    public static byte[] UpdateBattlEyeRegistration() => Zone(Ch0, w => w.U8(0x11).LeU16(0x0050));

    // ---- Phase D: the menu request set (docs/71 §5) -------------------------------------------

    /// <summary><c>26 9A 05 | str window | str action | u32 0</c> — channel 1.</summary>
    public static byte[] WindowEvent(string window, string action) =>
        Zone(Ch1, w => w.U8(0x9A).U8(0x05).CountedString(window).CountedString(action).LeU32(0));

    /// <summary><c>26 9A 06 | str json</c> — channel 1; the 3,749-byte driver blob has this shape.</summary>
    public static byte[] WallOfDataBlob(string json) =>
        Zone(Ch1, w => w.U8(0x9A).U8(0x06).CountedString(json));

    /// <summary><c>26 9A 0A 00000000 00000000</c> — channel 1, 11 bytes.</summary>
    public static byte[] WallOfDataCounter() =>
        Zone(Ch1, w => w.U8(0x9A).U8(0x0A).LeU32(0).LeU32(0));

    /// <summary><c>26 F3 0A 00</c> — channel 1, unregistered (docs/71 §1.1).</summary>
    public static byte[] Unregistered0xF3() => Zone(Ch1, w => w.U8(0xF3).LeU16(0x000A));

    /// <summary>
    /// The 27-byte channel-1 MatchHistoryBase request. Replayed as the recorded template with
    /// only the index byte changed, because only that byte differed between the three the client
    /// sent (indices 1, 2, 3).
    /// </summary>
    public static byte[] MatchHistoryRequest(byte index)
    {
        byte[] message = Convert.FromHexString("266706000000000000000000000000000000000100000000000000");
        message[19] = index;
        return message;
    }

    /// <summary><c>06 E9 01 00 | str view</c>. The menu camera the UI is focusing.</summary>
    public static byte[] StaticViewRequest(string view) =>
        Zone(Ch0, w => w.U8(0xE9).LeU16(1).CountedString(view));

    /// <summary><c>26 27 &lt;u16 sub&gt;</c> — channel 1, no argument.</summary>
    public static byte[] InGamePurchaseRequest(ushort sub) => Zone(Ch1, w => w.U8(0x27).LeU16(sub));

    /// <summary><c>26 27 &lt;u16 sub&gt; | str argument</c> — channel 1.</summary>
    public static byte[] InGamePurchaseRequest(ushort sub, string argument) =>
        Zone(Ch1, w => w.U8(0x27).LeU16(sub).CountedString(argument));

    /// <summary>The five requests the client fires in its menu burst (docs/71 §5).</summary>
    public static IReadOnlyList<byte[]> InGamePurchaseBurst(string locale = "en_US", string currency = "USD") =>
    [
        InGamePurchaseRequest(0x000A),
        InGamePurchaseRequest(0x001A, locale),
        InGamePurchaseRequest(0x0015, locale),
        InGamePurchaseRequest(0x000E, currency),
        InGamePurchaseRequest(0x000F, currency),
    ];

    /// <summary><c>06 09 16 00</c> — cCommandPacketFreeInteractionNpc.</summary>
    public static byte[] FreeInteractionNpc() => Zone(Ch0, w => w.U8(0x09).LeU16(0x0016));

    /// <summary>
    /// <c>06 8E 01 00 | u64 self | u64 self | u32 0 | u32 offset | u32 2 | 3×f32 | 00</c> (45 B).
    /// The client fires 24–26 of these in ≤10 ms during the menu burst (cCollisionPacketIdDamage).
    /// </summary>
    public static byte[] CollisionDamage(ulong self, uint offset, float x, float y, float z) =>
        Zone(Ch0, w => w.U8(0x8E).LeU16(1).LeU64(self).LeU64(self)
            .LeU32(0).LeU32(offset).LeU32(2).LeF32(x).LeF32(y).LeF32(z).U8(0));

    /// <summary><c>26 81 09 01</c> — channel 1.</summary>
    public static byte[] VoiceBase() => Zone(Ch1, w => w.U8(0x81).U8(0x09).U8(0x01));

    // ---- Phase E: the transfer (docs/71 §6) ---------------------------------------------------

    /// <summary>
    /// <c>26 EC 01000000 00000000 00000000 01 01000000</c> (19 B) — channel 1. The client sends
    /// this twice: once on the PLAY click and once 4.542 s later on its own timer, byte-identical.
    /// </summary>
    public static byte[] PlayerWorldTransferRequest(uint worldId = 1) =>
        Zone(Ch1, w => w.U8(0xEC).LeU32(worldId).LeU32(0).LeU32(0).U8(1).LeU32(1));

    // ---- Phase G: teleport and mount (docs/71 §8) ---------------------------------------------

    /// <summary><c>06 E8 02 00</c> — the client's answer to <c>05 E8 03 00</c>.</summary>
    public static byte[] SynchronizedTeleportAck() => Zone(Ch0, w => w.U8(0xE8).U8(0x02).U8(0x00));

    /// <summary>
    /// The AutoMount echo is <b>the server's own packet with two edits</b>: the gateway header
    /// changes <c>05 → 06</c> and byte 11 changes <c>01 → 00</c>. Everything else is byte-identical
    /// (docs/71 §8). Synthesising it from scratch is how a harness gets this wrong.
    /// </summary>
    public static byte[] VehicleAutoMountEcho(ReadOnlySpan<byte> serverPacket)
    {
        if (serverPacket.Length < 12)
        {
            throw new WireFormatException(
                $"A VehicleAutoMount to echo is at least 12 bytes; got {serverPacket.Length}.");
        }

        byte[] echo = serverPacket.ToArray();
        echo[0] = GatewayWire.Header(GatewayWire.OpcodeTunnelToServer, Ch0);
        echo[11] = 0;
        return echo;
    }

    /// <summary><c>06 88 27 | u64 guid | u8 mode</c>.</summary>
    public static byte[] VehicleCurrentMoveMode(ulong guid, byte mode) =>
        Zone(Ch0, w => w.U8(0x88).U8(0x27).LeU64(guid).U8(mode));

    /// <summary><c>06 88 18 | u64 guid</c> (11 B). Sent on landing.</summary>
    public static byte[] VehicleDismiss(ulong guid = 0) =>
        Zone(Ch0, w => w.U8(0x88).U8(0x18).LeU64(guid));

    // ---- Phase H: free-running timers (docs/71 §9) --------------------------------------------

    /// <summary><c>06 3B | u32 tick</c> (6 B). Always sent in the same millisecond as MonitorTimeDrift.</summary>
    public static byte[] KeepAlive(uint tick) => Zone(Ch0, w => w.U8(0x3B).LeU32(tick));

    /// <summary><c>06 11 44 00 | u32 0</c> (8 B). The other half of the 1 Hz pair.</summary>
    public static byte[] MonitorTimeDrift(uint drift = 0) =>
        Zone(Ch0, w => w.U8(0x11).LeU16(0x0044).LeU32(drift));

    /// <summary><c>06 8C | u64 tick | u64 tick | u64 unixTime | 24 zero bytes</c> (50 B), every 5.002 s.</summary>
    public static byte[] Synchronization(ulong tick, ulong unixTime) =>
        Zone(Ch0, w =>
        {
            w.U8(0x8C).LeU64(tick).LeU64(tick).LeU64(unixTime);
            for (int i = 0; i < 24; i++)
            {
                w.U8(0);
            }
        });

    /// <summary><c>06 1D | u64 unixTime | u32 0 | u8 0</c> (15 B), every 11.00 s.</summary>
    public static byte[] GameTimeSync(ulong unixTime) =>
        Zone(Ch0, w => w.U8(0x1D).LeU64(unixTime).LeU32(0).U8(0));

    /// <summary><c>06 44 | 148 bytes of counters</c> (150 B), every 30.00 s. Replayed as recorded.</summary>
    public static byte[] ClientMetrics()
    {
        byte[] recorded = Convert.FromHexString(RecordedClientMetricsHex);
        return recorded;
    }

    // ---- Phase I: loot (docs/71 §10) ----------------------------------------------------------

    /// <summary><c>06 0F 45 | u64 guid</c> (11 B). One per new guid, all in the same millisecond.</summary>
    public static byte[] FullCharacterDataRequest(ulong guid) =>
        Zone(Ch0, w => w.U8(0x0F).U8(0x45).LeU64(guid));

    /// <summary><c>06 09 15 00 | u64 self | u64 target</c> (20 B).</summary>
    public static byte[] PlayerSelect(ulong self, ulong target) =>
        Zone(Ch0, w => w.U8(0x09).LeU16(0x0015).LeU64(self).LeU64(target));

    /// <summary><c>06 09 07 00 | u64 target | f32 x y z | f32 1.0 | u8 1</c> (29 B).</summary>
    public static byte[] InteractRequest(ulong target, float x, float y, float z) =>
        Zone(Ch0, w => w.U8(0x09).LeU16(0x0007).LeU64(target).LeF32(x).LeF32(y).LeF32(z).LeF32(1f).U8(1));

    /// <summary><c>06 09 08 00</c> (4 B). Follows InteractRequest by a median of 0 ms, unconditionally.</summary>
    public static byte[] InteractCancel() => Zone(Ch0, w => w.U8(0x09).LeU16(0x0008));

    /// <summary>
    /// <c>06 09 2D 00 | u64 | u64</c> (20 B). The two 8-byte fields are inferred from the observed
    /// length matching PlayerSelect's shape; docs/71 §10.2 records only the length and the cadence.
    /// </summary>
    public static byte[] InteractionString(ulong self, ulong target) =>
        Zone(Ch0, w => w.U8(0x09).LeU16(0x002D).LeU64(self).LeU64(target));

    // ---- The developer console (design out\devconsole-20260901\DESIGN-dev-console.md §1.1) ------

    /// <summary>
    /// <c>06 09 42 00 | u32 hash | u32 len | len x utf8</c> — what the client sends when a
    /// <c>/name args</c> is typed into its debug console.
    /// <para>
    /// The layout is the one the client's own serialiser writes (<c>FUN_141296a90:281-282</c> for
    /// the level and sub-opcode, <c>FUN_14126add0</c> for the field order). This overload takes the
    /// hash as a number so a scenario can pin a literal the way the wire carries it — including a
    /// hash no name maps to, which is how the HELP catch-all is probed.
    /// </para>
    /// </summary>
    public static byte[] ExecuteCommand(uint hash, string arguments = "") =>
        Zone(Ch0, w => w.U8(0x09).LeU16(0x0042).LeU32(hash).CountedString(arguments));

    /// <summary>
    /// The same packet, addressed by name. The hash comes from <c>Cranberry.Zone.DevConsole</c>'s
    /// <c>CommandHash</c>, which is shared with the server — normally the harness re-implements what
    /// it asserts on (see the csproj note), but here the shared function is itself pinned to 38
    /// constants read out of the client binary (<c>CommandHashTests</c>), so the client, not
    /// Cranberry, is the oracle either way.
    /// </summary>
    public static byte[] ExecuteCommand(string name, string arguments = "") =>
        ExecuteCommand(Cranberry.Zone.DevConsole.CommandHash.Compute(name), arguments);

    /// <summary>
    /// <c>06 09 10 05 | u32 len | "ObserverCamera"</c> — the packet the client's console-toggle
    /// command sends on every toggle (<c>FUN_141291d50:100-104</c>). The server must ignore it; the
    /// harness sends it so a scenario can prove the ignoring rather than assume it.
    /// </summary>
    public static byte[] SpectateObserverCamera() =>
        Zone(Ch0, w => w.U8(0x09).LeU16(0x0510).CountedString("ObserverCamera"));

    // ---- Phase J: logout (docs/71 §11) --------------------------------------------------------

    /// <summary><c>06 FA | u32 seconds</c> (6 B). Sent twice, 0–15 ms apart.</summary>
    public static byte[] PlayLength(uint seconds) => Zone(Ch0, w => w.U8(0xFA).LeU32(seconds));

    /// <summary><c>06 07</c> (2 B). Two seconds of silence follow, then the SOE Disconnect.</summary>
    public static byte[] ClientLogout() => Zone(Ch0, w => w.U8(0x07));

    /// <summary>
    /// Recorded from wire-20260829-184346 at 18:44:40.071. ClientMetrics is a block of counters
    /// the harness has no reason to invent values for.
    /// </summary>
    private const string RecordedClientMetricsHex =
        "064401000000000000003275000000000000C50F000000000000BACF280100000000C81FE200000000005ADE"
        + "0900000000001BF6510000000000AC98000000000000E237000000000000D019610000000000470C00000000"
        + "0000FFFF7F3F03000000030000005605000000000000000300000000000000C07AFC01000000090000000000"
        + "000001000000000000000000000000000000";
}
