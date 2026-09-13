namespace Cranberry.Zone.Combat;

/// <summary>
/// docs/121 §6 (D334): groups an arm's extra replies by the delay each one is owed at, so the
/// sender can put every packet of one instant on one deferred continuation and keep their order.
/// A null delay list is "all immediate"; a short one treats the missing tail as immediate.
/// </summary>
public static class WeaponReplySchedule
{
    /// <summary>
    /// Consecutive replies with the same delay, in their original order, each group tagged with
    /// that delay in milliseconds. Never reorders across groups.
    /// </summary>
    public static IReadOnlyList<(int DelayMs, byte[][] Packets)> Group(
        IReadOnlyList<byte[]> replies,
        IReadOnlyList<int>? delaysMs)
    {
        ArgumentNullException.ThrowIfNull(replies);

        var groups = new List<(int, byte[][])>();
        var current = new List<byte[]>();
        int currentDelay = 0;

        for (int i = 0; i < replies.Count; i++)
        {
            int delay = delaysMs is not null && i < delaysMs.Count ? Math.Max(0, delaysMs[i]) : 0;

            if (current.Count > 0 && delay != currentDelay)
            {
                groups.Add((currentDelay, [.. current]));
                current.Clear();
            }

            currentDelay = delay;
            current.Add(replies[i]);
        }

        if (current.Count > 0)
        {
            groups.Add((currentDelay, [.. current]));
        }

        return groups;
    }
}
