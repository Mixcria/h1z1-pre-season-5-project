using System.Diagnostics;
using System.Numerics;
using Cranberry.Harness.Soe;

namespace Cranberry.NetworkBots;

internal static partial class Program
{
    private sealed record MovementStreamSample(long EmittedFrames, long SkippedFrames,
        double MaximumLatenessMs, double MeanLatenessMs, long LongestSkippedRun);
    private sealed record MovementBotSample(int Id, long PlayerRecords, long CanopyRecords,
        double PlayerHz, double CanopyHz, long MissingCanopyIdentities);

    // Explicit controlled descent only. The caller retains the untouched legacy scheduler.
    private static async Task MoveForControlled(double seconds, Func<Bot, double, Vector3> pose, CancellationToken ct)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        int playerHz = MovementHz ?? throw new InvalidOperationException("Controlled descent requires player cadence.");
        int canopyHz = CanopyHz ?? throw new InvalidOperationException("Controlled descent requires canopy cadence.");
        long endTicks = checked((long)Math.Ceiling(seconds * Stopwatch.Frequency));
        var schedule = new MovementEmissionSchedule(Stopwatch.Frequency, playerHz, canopyHz);
        var participants = Bots.Where(b => !b.Disposed).ToArray();
        var generated = participants.ToDictionary(b => b.Id, _ => (Player: 0L, Canopy: 0L, Missing: 0L));
        var timer = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long now = timer.ElapsedTicks;
                if (now >= endTicks) break;
                long next = Math.Min(endTicks, schedule.NextDeadlineTicks);
                if (now < next)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1,
                        Math.Ceiling((next - now) * 1000d / Stopwatch.Frequency))), ct);
                    continue;
                }
                if (!schedule.TryTake(now, out var batch)) continue;
                uint frameTick = Tick;
                double elapsedSeconds = batch.ElapsedTicks / (double)Stopwatch.Frequency;
                var lost = participants.FirstOrDefault(b => !b.Disposed && !b.ExpectingDisconnect
                    && b.Client.GatewayLink?.CloseCause is not (null or LinkCloseCause.None));
                if (lost is not null)
                    throw new IOException($"Bot {lost.Id} lost its game connection: {lost.Client.GatewayLink!.CloseCause}; {lost.Client.GatewayLink.Fault}");
                foreach (var bot in participants)
                {
                    if (bot.Disposed) continue;
                    Vector3 sampledPosition = pose(bot, elapsedSeconds);
                    var counts = generated[bot.Id];
                    if (batch.PlayerDue)
                    {
                        // Canopy-only events never advance player travel/heading state.
                        Vector3 travel = sampledPosition - bot.Position;
                        bot.Position = sampledPosition;
                        if (travel.LengthSquared() > .0001f) bot.Yaw = MathF.Atan2(travel.X, travel.Z);
                        SendMovement(bot, BotWire.Movement(sampledPosition, frameTick, posture: 0x21u, yaw: bot.Yaw), "bot movement");
                        counts.Player++;
                        generated[bot.Id] = counts;
                    }
                    if (batch.CanopyDue)
                    {
                        uint? transient = bot.Read(o => o.OwnChuteGuid != 0
                            && o.MountedRiders.GetValueOrDefault(bot.Client.SelfGuid) == o.OwnChuteGuid
                            && o.Canopies.TryGetValue(o.OwnChuteGuid, out var canopy)
                            && o.ChuteTransient == canopy.TransientId ? o.ChuteTransient : null);
                        if (transient is { } id)
                        {
                            SendMovement(bot, BotWire.Movement(sampledPosition, frameTick, managed: id), "bot canopy movement");
                            counts.Canopy++;
                        }
                        else counts.Missing++;
                        generated[bot.Id] = counts;
                    }
                }
            }
        }
        finally
        {
            double actualSeconds = Math.Max(1d / Stopwatch.Frequency, timer.Elapsed.TotalSeconds);
            var perBot = generated.Select(p => new MovementBotSample(p.Key, p.Value.Player, p.Value.Canopy,
                p.Value.Player / actualSeconds, p.Value.Canopy / actualSeconds, p.Value.Missing)).ToArray();
            MovementStreamSample Describe(MovementEmissionStream stream) => new(stream.EmittedFrames,
                stream.SkippedFrames, stream.MaximumLatenessTicks * 1000d / Stopwatch.Frequency,
                stream.EmittedFrames == 0 ? 0 : stream.TotalLatenessTicks * 1000d / Stopwatch.Frequency / stream.EmittedFrames,
                stream.LongestSkippedRun);
            MovementGeneration.Add(new(true, playerHz, canopyHz, actualSeconds,
                schedule.Player.EmittedFrames, schedule.Player.SkippedFrames,
                schedule.Player.MaximumLatenessTicks * 1000d / Stopwatch.Frequency,
                perBot.Sum(p => p.PlayerRecords), perBot.Sum(p => p.CanopyRecords),
                perBot.Select(p => p.PlayerHz).DefaultIfEmpty(0).Min(), perBot.Select(p => p.CanopyHz).DefaultIfEmpty(0).Min())
            {
                PlayerStream = Describe(schedule.Player), CanopyStream = Describe(schedule.Canopy!),
                PerBot = perBot, MissingCanopyIdentities = perBot.Sum(p => p.MissingCanopyIdentities)
            });
        }
    }
}
