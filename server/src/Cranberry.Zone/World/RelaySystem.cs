using Cranberry.Protocol;

namespace Cranberry.Zone.World;

/// <summary>
/// Movement relay into per-observer sinks: for each viewer, walk its known subjects round-robin from
/// its own cursor and copy each stale pose out verbatim under a byte budget.
/// <para>
/// Round-robin by staleness rather than nearest-first is deliberate: a crowded landing zone then
/// degrades to a slower update rate for everyone instead of freezing the far half of the view
/// (docs/22 §6.1). The body is a client varint transient id followed by the client's own record —
/// never a re-encode.
/// </para>
/// <para>
/// <b>Scaffold stub (migration step 7).</b> Selection, staleness and the budget run every tick, but
/// bytes leave only when <c>MatchSettings.RelayEnabled</c> is on: the transport has no outbound
/// <c>Multi</c> aggregation yet, so one relay message is one retained reliable datagram
/// (docs/22 §12 row 1), and that the client accepts a peer record on channel 2 at all is still a
/// lead.
/// </para>
/// </summary>
public sealed class RelaySystem : ISystem
{
    /// <summary>Relay bodies actually written to a sink.</summary>
    public long Sent { get; private set; }

    /// <summary>Bodies selected while the relay was gated off — what step 7 will send.</summary>
    public long Suppressed { get; private set; }

    /// <summary>Viewers whose pass ended on the byte budget rather than on the subject list.</summary>
    public long BudgetStops { get; private set; }

    public long BytesWritten { get; private set; }

    /// <summary>Gateway channel the client streams its own movement on (docs/22 §6.1).</summary>
    public const byte MovementChannel = 2;

    public void Tick(in TickContext context)
    {
        World world = context.World;
        bool enabled = context.Settings.RelayEnabled;
        int budgetBytes = context.Settings.RelayBudgetBytes;

        foreach (MatchPlayer? candidate in world.Players)
        {
            if (candidate is not MatchPlayer viewer)
            {
                continue;
            }

            ObserverView view = viewer.View;
            int count = view.KnownCount;
            if (count == 0)
            {
                continue;
            }

            int budget = budgetBytes;
            int cursor = view.RelayCursor;
            bool completed = true;
            for (int n = 0; n < count; n++)
            {
                int index = (cursor + n) % count;
                EntityId id = view.KnownAt(index);

                if (!world.TryGetPlayer(id, out MatchPlayer? subject)
                    || subject.Pose.IsEmpty
                    || subject.LastPoseTick <= view.LastRelayTick)
                {
                    continue;
                }

                if (!view.Transients.TryGet(id, out uint transientId))
                {
                    continue;
                }

                int bytes = ClientVarInt.Length(transientId) + subject.Pose.Length;
                if (bytes > budget)
                {
                    BudgetStops++;
                    view.RelayCursor = index;
                    completed = false;
                    break;
                }

                budget -= bytes;
                BytesWritten += bytes;
                view.RelayCursor = index + 1;

                if (!enabled || viewer.Sink is not { IsOpen: true } sink)
                {
                    Suppressed++;
                    continue;
                }

                PacketWriter writer = sink.Begin(MovementChannel);
                ClientVarInt.Write(writer, transientId);
                writer.WriteRaw(subject.Pose.Span);
                sink.End();
                Sent++;
            }

            // Only a pass that reached the end of the subject list has relayed everything stale.
            // Advancing after a budget stop would mark the deferred subjects as sent: the staleness
            // gate is `subject.LastPoseTick <= view.LastRelayTick`, so a peer skipped at tick T and
            // then standing still is filtered out for good, and the viewer holds its last position
            // forever. Deferred, not lost (docs/22 §6.1).
            if (completed)
            {
                view.LastRelayTick = context.Time.Tick;
            }
        }
    }
}
