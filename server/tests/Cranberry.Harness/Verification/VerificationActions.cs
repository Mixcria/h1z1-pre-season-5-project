using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Soe;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Verification;

/// <summary>
/// The client-originated messages the run lane needs beyond Lane A's set.
///
/// <para><b>Provenance is stated per message, and it matters.</b> Two of these are bytes a real
/// client has been recorded sending (the BACK button, 17 times across the corpus; the [F] pair,
/// 115 times). Two are <b>synthesised</b> — <c>Mount.MountRequest</c> and
/// <c>Command.RecipeStart</c> — because <b>no capture in the 128-file corpus contains either</b>
/// (<c>grep -c '^067001'</c> and <c>'^06091A'</c> both return 0). A synthesised request proves that
/// the <i>server</i> answers a packet of that shape; it can never prove the August client would
/// send it. Every probe built on one says so in its own evidence line.</para>
/// </summary>
public static class VerificationActions
{
    /// <summary>
    /// <c>06 09 4E 00 00</c> — <c>Command.StartLogoutRequest</c>, the BACK button out of a match.
    /// <b>Recorded</b>: this exact 5-byte message appears 17 times in the capture corpus and in no
    /// other form. The server answers <c>ClientUpdate.CompleteLogoutProcess</c> and tears the match
    /// down (<c>ZoneService.AbandonMatch</c>), which is the only route the harness has to a
    /// <i>second</i> match on one link — and the second match is where the wave-3 movement-stat bug
    /// lived.
    /// </summary>
    public static void PressBack(this HarnessClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        RequireLink(client).Send(Convert.FromHexString("06094E0000"), "Command.StartLogoutRequest (BACK)");
        client.Notes.Add("client action: BACK (Command.StartLogoutRequest, recorded bytes)");
    }

    /// <summary>
    /// <c>06 70 01 | u64 vehicleGuid | u32 seat | u8 | u8</c> (17 B on the wire) —
    /// <c>Mount.MountRequest</c>. <b>SYNTHESISED.</b> docs/43 blocker 1 is precisely that nobody
    /// knows whether an E-press on a parked car produces this or the <c>09 07</c> interact pair, and
    /// the corpus settles it negatively: zero <c>70 01</c> packets have ever been recorded from the
    /// August client. This exists so the server's <i>other</i> entry arm can be exercised; a probe
    /// that uses it may only claim the server answered, never that the client would ask.
    /// </summary>
    public static void SendMountRequest(this HarnessClient client, ulong vehicleGuid, uint seat = 0)
    {
        ArgumentNullException.ThrowIfNull(client);
        var w = new WireWriter(32);
        w.U8(GatewayWire.Header(GatewayWire.OpcodeTunnelToServer, GatewayWire.ChannelDefault));
        w.U8(0x70).U8(0x01).LeU64(vehicleGuid).LeU32(seat).U8(0).U8(0);
        RequireLink(client).Send(w.ToArray(), $"Mount.MountRequest {vehicleGuid} (SYNTHESISED)");
        client.Notes.Add($"client action: SYNTHESISED Mount.MountRequest for {vehicleGuid} — no capture contains one");
    }

    /// <summary>
    /// <c>06 09 1A 00 | u32 recipeId | u32 count</c> (11 B) — <c>Command.RecipeStart</c>.
    /// <b>SYNTHESISED</b>, same caveat: the corpus contains no <c>09 1a</c>, because no Cranberry
    /// build has ever put a recipe list in front of the owner's client. The shape is the one the
    /// server's own tolerant reader calls "the expected shape" (docs/62 §6).
    /// </summary>
    public static void SendRecipeStart(this HarnessClient client, uint recipeId, uint count = 1)
    {
        ArgumentNullException.ThrowIfNull(client);
        var w = new WireWriter(32);
        w.U8(GatewayWire.Header(GatewayWire.OpcodeTunnelToServer, GatewayWire.ChannelDefault));
        w.U8(0x09).LeU16(0x001A).LeU32(recipeId).LeU32(count);
        RequireLink(client).Send(w.ToArray(), $"Command.RecipeStart recipe={recipeId} (SYNTHESISED)");
        client.Notes.Add($"client action: SYNTHESISED Command.RecipeStart recipe={recipeId} — no capture contains one");
    }

    /// <summary>
    /// The <c>09 2d</c> interaction-string request that names a <b>world object</b>:
    /// <c>06 09 2D 00 | u64 objectGuid | u32 0 | u32 0</c>.
    ///
    /// <para><b>Why not Lane A's builder.</b> <c>ZoneClientMessages.InteractionString</c> writes
    /// <c>u64 self | u64 target</c> — docs/71 §10.2's own inference from the packet's length, and
    /// the first live reading of this lane showed what it costs: the server's derived parser reads
    /// <c>u64 guid; u32; u32</c>, so a request built that way asks about the PLAYER and is answered
    /// with string id 0. The corpus contains both forms the client really sends, and this is the
    /// one that names an object — <c>06092D00 0100000000000020 00000000 00000000</c>, i.e. world
    /// object <c>0x2000000000000001</c> with both trailing words zero (8 such requests across the
    /// captures). The other recorded form starts with the character's own guid and carries a small
    /// index plus <c>0x20000000</c>, which is not a prompt for a specific object.</para>
    /// </summary>
    public static void RequestInteractionString(this HarnessClient client, ulong targetGuid)
    {
        ArgumentNullException.ThrowIfNull(client);
        var w = new WireWriter(32);
        w.U8(GatewayWire.Header(GatewayWire.OpcodeTunnelToServer, GatewayWire.ChannelDefault));
        w.U8(0x09).LeU16(0x002D).LeU64(targetGuid).LeU32(0).LeU32(0);
        RequireLink(client).Send(w.ToArray(), $"Command.InteractionString {targetGuid}");
    }

    private static SoeClientSession RequireLink(HarnessClient client) =>
        client.GatewayLink
        ?? throw new InvalidOperationException("The gateway link must be open before the client can act.");
}
