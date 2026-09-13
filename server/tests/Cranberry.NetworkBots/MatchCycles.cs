using System.Numerics;
using System.Net;
using Cranberry.Harness;
using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Soe;
using Cranberry.Harness.Verification;

namespace Cranberry.NetworkBots;

internal static partial class Program
{
    private static async Task ReplayCompletedMatches(int cycles, Vector3 landing, CloudAccess cloud, CancellationToken ct)
    {
        // The first ordinary combat scenario has killed bot 1 and left bot 0 as winner.
        // Exercise the real results hold, automatic exit, timer cancellation and the native c3/c4
        // character-select ticket handoff before a new login through the same launcher tunnel.
        // No fixture state or server clock is changed during these transitions.
        var characters = Bots.Select(b => b.Client.SelfGuid).ToArray();
        for (int round = 1; round <= cycles; round++)
        {
            await WaitForCycleState(() => Bots.All(b => b.Read(o => o.Results) >= round),
                TimeSpan.FromSeconds(5), $"round {round} results", ct);
            Check($"round {round} has exactly one result per player and one winner",
                Bots.All(b => b.Read(o => o.Results) == round)
                && Bots[0].Read(o => o.VictoryScreens) == round && Bots[1].Read(o => o.VictoryScreens) == 0,
                new { results = Bots.Select(b => b.Read(o => o.Results)).ToArray(),
                    victories = Bots.Select(b => b.Read(o => o.VictoryScreens)).ToArray() });
            await WaitForCycleState(() => Bots.All(b => b.Read(o => unchecked((int)(o.LastLogoutTick - o.LastResultTick)) > 0)),
                TimeSpan.FromSeconds(45), $"round {round} results hold and automatic menu exit", ct);
            int[] holdMs = Bots.Select(b => b.Read(o => unchecked((int)(o.LastLogoutTick - o.LastResultTick)))).ToArray();
            Check($"round {round} preserves the results hold and exits to menu",
                holdMs.All(ms => ms >= 29_500) && Bots.All(b => b.Read(o => o.CompletedLogouts) == round),
                new { holdMs, completedLogouts = Bots.Select(b => b.Read(o => o.CompletedLogouts)).ToArray() });
            int[] startsAfterLogout = Bots.Select(b => b.Read(o => o.StartMatches)).ToArray();
            // The fixture countdown is eight seconds. Stay in the menu past its old deadline.
            await Task.Delay(TimeSpan.FromSeconds(9), ct);
            Check($"round {round} results exit cannot remount a player in the menu",
                Bots.All(b => b.Read(o => o.StartMatches == startsAfterLogout[b.Id])),
                new { startMatches = Bots.Select(b => b.Read(o => o.StartMatches)).ToArray() });

            foreach (var bot in Bots) bot.Client.GatewayLink!.Send([0x06, 0xc3], "CharacterSelectSessionRequest");
            await WaitForCycleState(() => Bots.All(b => b.Read(o => o.ReturnTicket) is not null),
                TimeSpan.FromSeconds(5), $"round {round} character-select tickets", ct);
            string[] tickets = Bots.Select(b => b.Read(o => o.ReturnTicket)!).ToArray();
            foreach (var bot in Bots) await bot.Client.DisposeAsync();
            await Task.Delay(250, ct);
            foreach (var bot in Bots)
            {
                bot.Observation.ResetMatchView();
                var tunnel = cloud.Players[bot.Id].Tunnel;
                bot.Client = new HarnessClient(new HarnessOptions
                {
                    LoginEndPoint = new(IPAddress.Loopback, tunnel.LoginPort),
                    GatewayEndPointOverride = new(IPAddress.Loopback, tunnel.GatewayPort),
                    LoginTicket = tickets[bot.Id], CharacterIndex = 0, Seed = 9000 + round * 10 + bot.Id,
                    RetainLedger = false, ObservePacket = bot.Observation.Observe, JournalCapacity = 24,
                    Timings = new ClientTimings { ReplayMovement = false },
                });
                bot.Observation.SelfGuid = () => bot.Client.SelfGuid;
            }
            await Task.WhenAll(Bots.Select(async bot =>
            {
                await bot.Client.ConnectAsync(ct);
                await bot.Client.ExpectAsync(HarnessMilestone.MenuClientFinishedLoadingSent, TimeSpan.FromSeconds(30), cancellationToken: ct);
            }));
            Check($"round {round} returns the same characters to the menu using server-issued tickets",
                Bots.All(b => b.Client.SelfGuid == characters[b.Id] && b.Client.GatewayLink!.CloseCause == LinkCloseCause.None
                    && !cloud.Players[b.Id].Tunnel.Completion.IsCompleted), new { connected = Bots.Count });
            if (round == cycles) break;

            foreach (var bot in Bots) bot.Client.ClickPlay();
            await WaitForCycleState(() => Bots.All(b => b.Client.Milestones.Count(HarnessMilestone.AutoMountEchoSent) > 0),
                TimeSpan.FromSeconds(100), $"round {round + 1} new zoning and mount", ct);
            var starts = Bots.Select(b => b.Read(o => o.ChuteSpawn) ?? throw new InvalidDataException("Replay has no new canopy")).ToArray();
            double descent = Math.Max(20, starts.Max(p => p.Y - landing.Y) / 6);
            await MoveFor(descent, (bot, t) => Vector3.Lerp(starts[bot.Id], landing, (float)Math.Min(1, t / descent)), ct, parachuting: true);
            foreach (var bot in Bots) bot.Client.ReportTouchdown();
            await MoveFor(5, (bot, t) => landing + Offset(bot.Id, t), ct);
            CheckPeers($"round {round + 1} rebuilds peer visibility");
            Check($"round {round + 1} receives fresh health and inventory",
                Bots.All(b => b.Read(o => o.Health == 10_000 && o.Inventory.Values.Any(i => i.DefinitionId == 2425))),
                new { health = Bots.Select(b => b.Read(o => o.Health)).ToArray() });
            await Combat(ct, clean: true);
        }
    }

    private static async Task WaitForCycleState(Func<bool> ready, TimeSpan timeout, string stage, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!ready())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(stage);
            if (Bots.Any(b => b.Client.GatewayLink!.CloseCause != LinkCloseCause.None)) throw new IOException("Connection lost during " + stage);
            await Task.Delay(50, ct);
        }
    }
}
