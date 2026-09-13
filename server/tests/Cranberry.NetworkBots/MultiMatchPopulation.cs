using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Text.Json;
using Cranberry.Harness;
using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Soe;

namespace Cranberry.NetworkBots;

/// <summary>Separate protocol clients, one real gateway, sequential rolling admissions and simultaneous dense movement.</summary>
internal static class MultiMatchPopulation
{
    public static async Task<int> Run(string[] args)
    {
        string? Text(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int count = int.Parse(Text("--bots") ?? "25"), matches = int.Parse(Text("--matches") ?? "2");
        int seconds = int.Parse(Text("--seconds") ?? "30");
        bool mixed = args.Contains("--mixed-modes");
        if (count is < 6 or > 150 || matches is < 2 or > 10 || seconds is < 5 or > 1800)
            throw new ArgumentException("Multi-match: bots per match 6..150, matches 2..10, seconds 5..1800.");
        string output = Text("--output") ?? throw new ArgumentException("--output required");
        Directory.CreateDirectory(output);
        var fixture = JsonSerializer.Deserialize<FixtureAddress>(File.ReadAllText(Text("--fixture")
            ?? throw new ArgumentException("Multi-match requires an isolated fixture")))!;
        if (fixture.Tls is null || fixture.Population < count * matches)
            throw new ArgumentException("Insufficient authenticated TLS fixture population");
        uint Tick() => unchecked((uint)(Environment.TickCount64 + fixture.TickOffset));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10 + seconds / 60));
        using var movementStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var checks = new List<object>();
        var windows = new List<object>();
        var bots = new List<Program.Bot>();
        var joined = new bool[count * matches];
        var landed = new bool[count * matches];
        var started = new long[count * matches];
        var starts = new Vector3[count * matches];
        Vector3 landing = new(fixture.Landing[0], fixture.Landing[1], fixture.Landing[2]);
        Task? mover = null;
        bool success = true;
        void Check(string name, bool pass, object evidence)
        {
            checks.Add(new { name, pass, evidence }); success &= pass;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: {JsonSerializer.Serialize(evidence)}");
        }
        async Task Until(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (mover?.IsFaulted == true) await mover;
                if (watch.Elapsed > timeout) throw new TimeoutException("Multi-match phase deadline exceeded");
                await Task.Delay(25, deadline.Token);
            }
        }
        await using var access = await CloudAccess.OpenFixture(fixture.Tls, count * matches, deadline.Token);
        try
        {
            for (int i = 0; i < count * matches; i++)
            {
                var observation = new BotObservation { Tick = Tick };
                var player = access.Players[i];
                var client = new HarnessClient(new HarnessOptions
                {
                    LoginEndPoint = new(IPAddress.Loopback, player.Tunnel.LoginPort),
                    GatewayEndPointOverride = new(IPAddress.Loopback, player.Tunnel.GatewayPort),
                    LoginTicket = player.Launch.Ticket, CharacterIndex = 0, Seed = 9000 + i,
                    RetainLedger = false, JournalCapacity = 12, ObservePacket = observation.Observe,
                    Timings = new ClientTimings { ReplayMovement = false }
                });
                observation.SelfGuid = () => client.SelfGuid;
                bots.Add(new(i, client, observation));
            }
            using var slots = new SemaphoreSlim(16);
            await Task.WhenAll(bots.Select(async bot =>
            {
                await slots.WaitAsync(deadline.Token);
                try
                {
                    await bot.Client.ConnectAsync(deadline.Token);
                    await bot.Client.ExpectAsync(HarnessMilestone.MenuClientFinishedLoadingSent,
                        TimeSpan.FromSeconds(120), cancellationToken: deadline.Token);
                }
                finally { slots.Release(); }
            }));
            mover = Move();
            for (int round = 0; round < matches; round++)
            {
                uint world = mixed ? new uint[] { 1, 6, 7 }[round % 3] : 1;
                var group = bots.Skip(round * count).Take(count).ToArray();
                foreach (var bot in group) { joined[bot.Id] = true; bot.Client.ClickPlay(world); }
                await Until(() => group.All(b => b.Read(o => o.OwnChuteGuid != 0
                    && o.MountedRiders.GetValueOrDefault(b.Client.SelfGuid) == o.OwnChuteGuid)), TimeSpan.FromSeconds(100));
                Check($"rolling round {round + 1} starts while earlier rounds retain their sessions",
                    bots.Take((round + 1) * count).All(b => b.Client.GatewayLink!.CloseCause == LinkCloseCause.None),
                    new { world, players = count, earlierRounds = round, utc = DateTimeOffset.UtcNow });
            }
            await Until(() => landed.All(v => v), TimeSpan.FromSeconds(45));
            await Task.Delay(4000, deadline.Token);
            foreach (var bot in bots) bot.Observation.BeginMovementWindow(Tick());
            File.WriteAllText(Path.Combine(output, "phase.json"), JsonSerializer.Serialize(new
                { name = "simultaneous-ground", utc = DateTimeOffset.UtcNow }));
            await Task.Delay(TimeSpan.FromSeconds(seconds), deadline.Token);
            for (int round = 0; round < matches; round++)
            {
                var group = bots.Skip(round * count).Take(count).ToArray();
                var guids = group.Select(b => b.Client.SelfGuid).ToHashSet();
                var measured = group.Select(b => b.Observation.MovementWindow(Tick(), false)).ToArray();
                int outsiders = group.Sum(b => b.Read(o => o.Peers.Keys.Count(g => !guids.Contains(g))));
                int minimum = group.Min(b => b.Read(o => o.Peers.Keys.Count(g => guids.Contains(g))));
                Check($"round {round + 1} sees exactly its own roster at overlapping coordinates",
                    outsiders == 0 && minimum == count - 1, new { outsiders, minimum, expected = count - 1 });
                var window = new { round = round + 1, name = "simultaneous-ground", measured,
                    worstP50Ms = measured.Max(w => w.P50Ms), worstP95Ms = measured.Max(w => w.P95Ms),
                    worstP99Ms = measured.Max(w => w.P99Ms), maxMs = measured.Max(w => w.MaxMs),
                    minimumPairHz = measured.Min(w => w.MinimumPairHz), missingPeers = measured.Sum(w => w.MissingPeers) };
                windows.Add(window);
                Check($"round {round + 1} movement remains fresh", window.worstP99Ms < 250
                    && window.minimumPairHz >= 10 && window.missingPeers == 0, window);
            }
            Check("all match tunnels remain connected", access.Players.All(p => !p.Tunnel.Completion.IsCompleted),
                new { total = bots.Count, nativeClients = 0 });

            async Task Move()
            {
                int frame = 0;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
                while (await timer.WaitForNextTickAsync(movementStop.Token))
                {
                    foreach (var bot in bots)
                    {
                        if (!joined[bot.Id]) continue;
                        var chute = bot.Read(o => (o.OwnChuteGuid, o.ChuteSpawn, o.ChuteTransient));
                        if (chute.OwnChuteGuid == 0 || chute.ChuteSpawn is null) continue;
                        if (started[bot.Id] == 0) { started[bot.Id] = Environment.TickCount64; starts[bot.Id] = chute.ChuteSpawn.Value; }
                        double t = (Environment.TickCount64 - started[bot.Id]) / 1000d;
                        if (t >= 22 && !landed[bot.Id]) { bot.Client.ReportTouchdown(); landed[bot.Id] = true; }
                        double angle = bot.Id * 2.39996 + t * .65;
                        bot.Position = Vector3.Lerp(starts[bot.Id], landing, (float)Math.Min(1, t / 22))
                            + new Vector3((float)Math.Sin(angle) * 4, 0, (float)Math.Cos(angle) * 4);
                        bot.Client.GatewayLink!.SendUnreliable(BotWire.Movement(bot.Position, Tick(),
                            posture: landed[bot.Id] ? 0x401u : 0x21u, yaw: (float)angle), "multi-match player movement");
                        if (!landed[bot.Id] && frame % 4 == 0)
                            bot.Client.GatewayLink.SendUnreliable(BotWire.Movement(bot.Position, Tick(),
                                managed: chute.ChuteTransient ?? 2), "multi-match canopy movement");
                    }
                    frame++;
                }
            }
        }
        catch (Exception ex)
        {
            Check("scenario completes", false, new { error = ex.ToString() });
        }
        finally
        {
            movementStop.Cancel();
            if (mover is not null) try { await mover; } catch (OperationCanceledException) { } catch (Exception ex) { Check("movement task", false, new { ex.Message }); }
            foreach (var bot in bots) await bot.Client.DisposeAsync();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                { success, transport = "authenticated loopback WSS", nativeClients = 0, matches, playersPerMatch = count,
                    mixedModes = mixed, checks, movementWindows = windows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return success ? 0 : 1;
    }
}
