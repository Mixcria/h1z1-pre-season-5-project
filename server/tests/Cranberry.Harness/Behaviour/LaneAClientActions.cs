using Cranberry.Harness.Protocol;

namespace Cranberry.Harness.Behaviour;

/// <summary>
/// The client actions Lane A needs that the build lane's <see cref="ClientResponder"/> does not
/// drive on its own.
///
/// <para><b>Why they are here rather than in the responder.</b> The responder models the client's
/// <i>automatic</i> half: the timers and the reflexes the captures show fire unconditionally.
/// Everything below is the half a <i>player</i> drives — pressing [F] on a particular object,
/// walking somewhere, and the touchdown the client's own descent physics decides. The harness
/// cannot compute the August client's descent, so a scenario states when those happen and the
/// bytes are the recorded ones. Keeping them out of the responder also keeps the build lane's
/// state machine and its socket-free tests exactly as they were.</para>
///
/// <para>Every builder used here is already pinned to the captures by
/// <c>RecordedClientBytesTests</c>; nothing new is invented on the wire.</para>
/// </summary>
public static class LaneAClientActions
{
    /// <summary>
    /// The [F] press, as the August client actually emits it (docs/71 §10.2, docs/61 §5): one press
    /// produces <b>both</b> <c>09 15 PlayerSelect</c> and <c>09 07 InteractRequest</c> 1–3 ms apart,
    /// then <c>09 08 InteractCancel</c>. The duplicate is not a bug in the harness: the server's
    /// pickup path is written to be idempotent precisely because of it, and a scenario that sent
    /// only one of the two would not be testing what the client does.
    /// </summary>
    public static async Task PressInteractAsync(
        this HarnessClient client,
        ulong targetGuid,
        System.Numerics.Vector3 at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        Soe.SoeClientSession link = RequireLink(client);

        link.Send(ZoneClientMessages.PlayerSelect(client.SelfGuid, targetGuid), $"PlayerSelect {targetGuid}");
        client.Milestones.Note(HarnessMilestone.PlayerSelectSent, targetGuid.ToString());

        await client.Clock.DelayAsync(TimeSpan.FromMilliseconds(3), cancellationToken).ConfigureAwait(false);
        link.Send(ZoneClientMessages.InteractRequest(targetGuid, at.X, at.Y, at.Z), $"InteractRequest {targetGuid}");
        client.Milestones.Note(HarnessMilestone.InteractRequestSent, targetGuid.ToString());

        await client.Clock.DelayAsync(TimeSpan.FromMilliseconds(2), cancellationToken).ConfigureAwait(false);
        link.Send(ZoneClientMessages.InteractCancel(), "InteractCancel");
        client.Milestones.Note(HarnessMilestone.InteractCancelSent);
    }

    /// <summary>
    /// One <c>Character.FullCharacterDataRequest</c> (0F 45) per guid, all in the same millisecond,
    /// which is how the client asks (docs/71 §10.1, n=7). The reply is <c>LightweightToFullNpc</c>
    /// (0xda), matched by transient id.
    /// </summary>
    public static void RequestFullCharacterData(this HarnessClient client, IEnumerable<ulong> guids)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(guids);
        Soe.SoeClientSession link = RequireLink(client);

        int count = 0;
        foreach (ulong guid in guids)
        {
            link.Send(ZoneClientMessages.FullCharacterDataRequest(guid), $"FullCharacterDataRequest {guid}");
            count++;
        }

        if (count > 0)
        {
            client.Milestones.Note(HarnessMilestone.FullCharacterDataRequestsSent, $"{count} guid(s)");
        }
    }

    /// <summary>
    /// The touchdown: the one null-guid <c>Vehicle.Dismiss</c> (06 88 18) the client sends when its
    /// own descent finishes. Both known-good captures carry exactly one, and the server's
    /// <c>Vehicle.Dismiss</c> case treats it as the landing while the chute is still possessed.
    /// </summary>
    public static void ReportTouchdown(this HarnessClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        RequireLink(client).Send(ZoneClientMessages.VehicleDismiss(), "Vehicle.Dismiss (touchdown)");
        client.Milestones.Note(HarnessMilestone.ParachuteDismissSent);
    }

    /// <summary>
    /// Walks by replaying recorded channel-2 packets at the on-foot cadence for
    /// <paramref name="duration"/>. The bytes are a real client's; nothing is synthesised, and the
    /// harness never decodes them (docs/71 §1, §9). What the walk is <i>for</i> is measured on the
    /// server's side of the conversation: where the loot it spawns afterwards ends up.
    /// </summary>
    public static async Task WalkAsync(
        this HarnessClient client,
        MovementReplay route,
        TimeSpan duration,
        TimeSpan? cadence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(route);
        Soe.SoeClientSession link = RequireLink(client);

        TimeSpan step = cadence ?? TimeSpan.FromMilliseconds(40);
        TimeSpan until = client.Clock.Now + duration;
        int sent = 0;
        while (client.Clock.Now < until)
        {
            link.Send(route.Next(), "ch2 PlayerMovement (replayed walk)");
            sent++;
            await client.Clock.DelayAsync(step, cancellationToken).ConfigureAwait(false);
        }

        client.Milestones.Note(HarnessMilestone.WalkReplayed, $"{sent} replayed ch2 packet(s)");
    }

    private static Soe.SoeClientSession RequireLink(HarnessClient client) =>
        client.GatewayLink
        ?? throw new InvalidOperationException("The gateway link must be open before the client can act.");
}
