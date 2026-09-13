using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.World;

/// <summary>
/// Applies the channel-2 records staged by the drain step: parse the field mask, reject a pose that
/// is not finite or that implies an impossible speed, keep the record verbatim for relay, push a
/// history sample and re-bucket the player in the interest grid.
/// <para>
/// The record is never re-encoded. <c>ClientMovementUpdate</c> is self-describing through its own
/// field mask and rejects unknown flag bits and trailing bytes, so relaying a peer's pose is a
/// varint plus a span copy — the server never has to understand a field whose semantics are still
/// open (docs/17, docs/22 §6.1).
/// </para>
/// </summary>
public sealed class MovementSystem : ISystem
{
    /// <summary>Poses accepted and applied.</summary>
    public long Applied { get; private set; }

    /// <summary>Records refused by the speed or finiteness gate; the previous pose is kept.</summary>
    public long Rejected { get; private set; }

    /// <summary>Records longer than <see cref="MovementSample.MaxBytes"/>; never truncated.</summary>
    public long Oversized { get; private set; }

    /// <summary>Records <c>ClientMovementUpdate.Parse</c> refused.</summary>
    public long Malformed { get; private set; }

    /// <summary>Stages one client record for this tick. The last record of a tick wins.</summary>
    public void Stage(MatchPlayer player, ReadOnlySpan<byte> record, TickTime now)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!player.Staged.Set(record, now.Tick))
        {
            Oversized++;
            return;
        }

        player.HasStaged = true;
    }

    public void Tick(in TickContext context)
    {
        World world = context.World;
        TickTime now = context.Time;
        float maxSpeed = context.Settings.MaxSpeedMetresPerSecond;

        foreach (MatchPlayer? candidate in world.Players)
        {
            if (candidate is not { HasStaged: true } player)
            {
                continue;
            }

            player.HasStaged = false;

            ClientMovementUpdate update;
            try
            {
                // Allocates one record per applied sample; migration step 7 replaces this with a
                // span-only mask reader once the relay path is live.
                update = ClientMovementUpdate.Parse(player.Staged.Span);
            }
            catch (PacketFormatException)
            {
                Malformed++;
                continue;
            }

            if (update.EffectivePosition is Vector3 position)
            {
                if (!IsFinite(position))
                {
                    Reject(player);
                    continue;
                }

                // The gate needs a previous accepted pose to measure against; the first record of a
                // player's life is always taken (it is the spawn or a teleport the server ordered).
                if (!player.Pose.IsEmpty && !IsReachable(player, position, now, maxSpeed))
                {
                    Reject(player);
                    continue;
                }

                player.Position = position;
            }

            if ((update.Rotation ?? update.PrecisePose?.Rotation) is Quaternion rotation)
            {
                player.Rotation = rotation;
            }

            if (update.Orientation is float heading)
            {
                player.Heading = heading;
            }

            player.Pose.Set(player.Staged.Span, now.Tick);
            player.LastPoseTick = now.Tick;
            player.History.Push(new PoseSnapshot(now.Tick, player.Position, player.Rotation));
            world.UpdateCell(player);
            Applied++;
        }
    }

    private void Reject(MatchPlayer player)
    {
        player.MovementViolations++;
        Rejected++;
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// Cranberry's own sanity gate, not a client fact: the implied speed between the last accepted
    /// pose and this one must stay under the configured ceiling.
    /// </summary>
    private static bool IsReachable(MatchPlayer player, in Vector3 position, TickTime now, float maxSpeed)
    {
        long ticks = Math.Max(1, now.Tick - player.LastPoseTick);
        float seconds = ticks * MatchClock.FixedDeltaSeconds;
        float distance = Vector3.Distance(player.Position, position);
        return distance <= maxSpeed * seconds;
    }
}
