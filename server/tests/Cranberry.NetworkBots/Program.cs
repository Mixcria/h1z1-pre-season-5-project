using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Cranberry.Harness;
using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Soe;
using Cranberry.Harness.Verification;

namespace Cranberry.NetworkBots;

internal static partial class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long TickOffset = 1_000_000 - Environment.TickCount64;
    private static uint Tick => unchecked((uint)(Environment.TickCount64 + TickOffset));
    private static readonly List<object> Checks = [];
    private static readonly List<object> MovementWindows = [];
    private static TimeSpan WindowGcStart;
    private static uint? AmmoRequestedTick, AmmoDeadlineTick;
    private static string Output = "";
    private static readonly List<Bot> Bots = [];
    private static bool Public;
    private static bool UnreliableMovement;
    private static int CombatSeconds;
    private static void Log(string message) => Console.WriteLine($"{Clock.Elapsed.TotalSeconds,8:F2}s {message}");
    private static void Check(string name, bool pass, object evidence)
    {
        Checks.Add(new { name, pass, evidence });
        Log($"{(pass ? "PASS" : "FAIL")} {name}: {JsonSerializer.Serialize(evidence)}");
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--multi-match") && !args.Contains("--server-only")) return await MultiMatchPopulation.Run(args);
        if (args.Contains("--menu-only") && !args.Contains("--server-only")) return await MenuPopulation.Run(args);
        int count = Int(args, "--bots", 2), seconds = Int(args, "--seconds", 30);
        int menuCount = Int(args, "--menu-bots", 0);
        int matchCycles = Int(args, "--match-cycles", 1);
        if (matchCycles is < 1 or > 20 || matchCycles > 1 && count != 2)
            throw new ArgumentException("match-cycles 1..20; repeated complete matches require exactly 2 bots");
        CombatSeconds = Int(args, "--combat-seconds", 0);
        if (CombatSeconds is < 0 or > 1800) throw new ArgumentException("combat-seconds 0..1800");
        if (menuCount is < 0 or > 7000) throw new ArgumentException("menu-bots 0..7000");
        int delay = Int(args, "--delay-ms", 0), jitter = Int(args, "--jitter-ms", 0);
        double loss = double.TryParse(Text(args, "--loss"), System.Globalization.CultureInfo.InvariantCulture, out var parsedLoss) ? parsedLoss : 0;
        if (delay < 0 || jitter < 0 || loss is < 0 or >= 1) throw new ArgumentException("Invalid impairment options");
        if (count is < 2 or > 150 || seconds is < 5 or > 1800) throw new ArgumentException("bots 2..150; seconds 5..1800");
        Output = Text(args, "--output") ?? Path.Combine(Path.GetTempPath(), "cranberry-network-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Output);
        if (args.Contains("--wire-audit")) WireAudit.Start(Output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15 + menuCount / 100 + seconds / 60 + CombatSeconds / 60 + (matchCycles - 1) * 3));
        if (args.Contains("--server-only"))
        {
            int matches = Int(args, "--matches", 1);
            if (matches is < 1 or > 10) throw new ArgumentException("matches 1..10");
            await FixtureHost.Run(count * matches + menuCount, Output, TickOffset, () => Tick, deadline.Token,
                menuCount > 0, args.Contains("--tls-fixture"), count, matches);
            return 0;
        }
        string? profile = Text(args, "--public-profile");
        string? fixturePath = Text(args, "--fixture");
        if (profile is not null && fixturePath is not null) throw new ArgumentException("Choose public profile or local fixture");
        FixtureAddress? fixture = fixturePath is null ? null : JsonSerializer.Deserialize<FixtureAddress>(File.ReadAllText(fixturePath));
        if (matchCycles > 1 && fixture?.Tls is null) throw new ArgumentException("Match cycles require a loopback TLS fixture");
        if (fixture is not null)
        {
            if (count > fixture.Population) throw new ArgumentException("Fixture roster is smaller than bot count");
            TickOffset = fixture.TickOffset;
        }
        string accountsFile = Text(args, "--accounts-file") ?? Path.Combine(Output, "private", "accounts.json");
        if (args.Contains("--prepare-cloud"))
        {
            await using var prepared = await CloudAccess.Open(profile ?? throw new ArgumentException("--public-profile required"), accountsFile, count, true, deadline.Token);
            return 0;
        }
        Public = profile is not null;
        if (Public && matchCycles > 1) throw new ArgumentException("Repeated complete matches require a local fixture");
        UnreliableMovement = args.Contains("--unreliable-movement");
        LocalServer? server = null;
        CloudAccess? cloud = null;
        var links = new List<ImpairedLink>();
        int exit = 0;
        MenuPopulation? menu = null;
        VoiceTrafficLoad? voice = null;
        try
        {
            if (Public) cloud = await CloudAccess.Open(profile!, accountsFile, count, false, deadline.Token);
            else if (fixture?.Tls is { } tls) cloud = await CloudAccess.OpenFixture(tls, count, deadline.Token);
            else if (fixture is null) server = new LocalServer(count + menuCount, Output, menuCount > 0, matchPopulation: count) { Tick = () => Tick };
            if (menuCount > 0)
            {
                if (Public) throw new ArgumentException("Menu capacity scenarios require a local fixture.");
                menu = new MenuPopulation(menuCount, count, fixture?.LoginPort ?? server!.LoginEndPoint.Port, fixture?.Tls);
                await menu.Connect(deadline.Token);
            }
            if (args.Contains("--voice-load")) voice = await VoiceTrafficLoad.Open(fixture?.Tls
                ?? throw new ArgumentException("--voice-load requires a loopback TLS fixture"), count, menuCount, deadline.Token);
            Log($"Gameplay run: {count} bots, TLS tunnel={cloud is not null}, public host={Public}, output {Output}");
            for (int i = 0; i < count; i++)
            {
                var observation = new BotObservation { Tick = () => Tick };
                System.Net.IPEndPoint? gateway = null;
                if (delay + jitter > 0 || loss > 0)
                {
                    var destination = cloud is not null ? new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, cloud.Players[i].Tunnel.GatewayPort)
                        : fixture is not null ? new(System.Net.IPAddress.Loopback, fixture.GatewayPort) : server!.GatewayEndPoint;
                    var link = new ImpairedLink(destination, delay, jitter, loss, 3100 + i);
                    links.Add(link); gateway = link.EndPoint;
                }
                var client = new HarnessClient(new HarnessOptions
                {
                    LoginEndPoint = cloud is not null ? new(System.Net.IPAddress.Loopback, cloud.Players[i].Tunnel.LoginPort)
                        : fixture is not null ? new(System.Net.IPAddress.Loopback, fixture.LoginPort) : server!.LoginEndPoint,
                    CharacterIndex = cloud is not null || menuCount > 0 ? 0 : i, Seed = 7000 + i,
                    LoginTicket = cloud is not null ? cloud.Players[i].Launch.Ticket : menuCount > 0 ? LocalServer.TicketFor(i) : null,
                    CreateCharacterIfEmpty = Public ? cloud!.Players[i].Name : null,
                    GatewayEndPointOverride = gateway ?? (cloud is not null ? new(System.Net.IPAddress.Loopback, cloud.Players[i].Tunnel.GatewayPort) : null),
                    RetainLedger = false, ObservePacket = observation.Observe, JournalCapacity = 24,
                    Timings = new ClientTimings { ReplayMovement = false },
                });
                observation.SelfGuid = () => client.SelfGuid;
                Bots.Add(new Bot(i, client, observation));
            }
            using var admissionSlots = new SemaphoreSlim(10);
            await Task.WhenAll(Bots.Select(async bot =>
            {
                await admissionSlots.WaitAsync(deadline.Token);
                try
                {
                    await bot.Client.ConnectAsync(deadline.Token);
                    await bot.Client.ExpectAsync(HarnessMilestone.MenuClientFinishedLoadingSent,
                        TimeSpan.FromSeconds(90), cancellationToken: deadline.Token);
                    if (WireAudit.Enabled && WireAudit.Selected(bot.Id))
                        bot.Client.GatewayLink!.ObserveDatagram = (incoming, bytes) => WireAudit.Record($"soe-{bot.Id}-{(incoming ? "receive" : "send")}", bytes.Span);
                }
                finally { admissionSlots.Release(); }
            }));
            Check("unique authenticated gateway characters", Bots.Select(b => b.Client.SelfGuid).Distinct().Count() == count,
                new { connected = count });
            foreach (var bot in Bots) bot.Client.ClickPlay();
            await Task.WhenAll(Bots.Select(bot => bot.Client.ExpectAsync(HarnessMilestone.AutoMountEchoSent,
                TimeSpan.FromSeconds(Public ? 220 : 100), cancellationToken: deadline.Token)));
            var mountWait = Stopwatch.StartNew();
            bool Mounted(Bot bot) => bot.Read(o => o.OwnChuteGuid != 0
                && o.MountedRiders.GetValueOrDefault(bot.Client.SelfGuid) == o.OwnChuteGuid);
            await Task.Delay(500, deadline.Token);
            // EchoSent is the client's request, not receipt of the server's mount reply.
            // Wait for that explicit reply before generating descent input under loss.
            while (mountWait.Elapsed.TotalSeconds < 5 && Bots.Any(b => !Mounted(b)))
                await Task.Delay(25, deadline.Token);
            Check("simultaneous zoning and parachute mount", Bots.All(Mounted),
                new { mounted = Bots.Count(Mounted), elapsedMs = mountWait.Elapsed.TotalMilliseconds, timeoutMs = 5000 });
            var starts = Bots.Select(b => b.Read(o => o.ChuteSpawn) ?? throw new InvalidDataException("No observed canopy position")).ToArray();
            var landing = fixture is not null ? new Vector3(fixture.Landing[0], fixture.Landing[1], fixture.Landing[2]) : server?.Landing ?? starts[0];
            if (Public) landing.Y = Cranberry.Zone.Loot.AirdropTerrain.LoadDefault().HeightAt(landing.X, landing.Z) + .1f;
            double descentSeconds = Math.Max(20, starts.Max(p => p.Y - landing.Y) / 6);
            double horizontal = starts.Max(p => Vector2.Distance(new(p.X, p.Z), new(landing.X, landing.Z)));
            if (horizontal / descentSeconds > 12) throw new InvalidDataException("Public drops are too far apart to gather at the modelled canopy speed.");
            Log($"Descending from observed canopy positions for {descentSeconds:F1}s; maximum horizontal glide {horizontal:F1}m.");
            BeginMovementWindow("descent");
            await MoveFor(descentSeconds, (bot, t) => Vector3.Lerp(starts[bot.Id], landing,
                (float)Math.Min(1, t / descentSeconds)) + new Vector3((float)Math.Sin(t * .4 + bot.Id * .17) * 2, 0,
                (float)Math.Cos(t * .4 + bot.Id * .17) * 2), deadline.Token, parachuting: true);
            EndMovementWindow("descent", airborne: true, clean: !Public && delay + jitter == 0 && loss == 0);
            CheckPeers("airborne peer visibility before touchdown");
            var airborneGuids = Bots.Select(b => b.Client.SelfGuid).ToHashSet();
            int[] observedCanopies = Bots.Select(b => b.Read(o => o.MountedRiders.Count(r =>
                r.Key != b.Client.SelfGuid && airborneGuids.Contains(r.Key)
                && o.Canopies.TryGetValue(r.Value, out var canopy)
                && o.PeerPoses.GetValueOrDefault(canopy.TransientId) > 0))).ToArray();
            Check("remote canopies are mounted and moving", observedCanopies.All(n => n == count - 1),
                new { expected = count - 1, minimum = observedCanopies.Min(), maximum = observedCanopies.Max() });
            foreach (var bot in Bots) bot.Client.ReportTouchdown();
            await MoveFor(5, (bot, t) => landing + Offset(bot.Id, t), deadline.Token);
            CheckPeers("dense landing peer visibility");
            Check("landed canopies and mounts are removed", Bots.All(b => b.Read(o => o.Canopies.Count == 0 && o.MountedRiders.Count == 0)),
                new { remainingCanopies = Bots.Sum(b => b.Read(o => o.Canopies.Count)), remainingRiders = Bots.Sum(b => b.Read(o => o.MountedRiders.Count)) });
            Check("landed with world loot", Bots.All(b => b.Read(o => o.Loot.Count) > 0),
                new { minimumLoot = Bots.Min(b => b.Read(o => o.Loot.Count)), maximumLoot = Bots.Max(b => b.Read(o => o.Loot.Count)) });

            // Pair-wide movement, crossing paths and changes of stance at the observed 25 Hz foot cadence.
            var poseCountsBefore = Bots.Select(b => b.Read(o => new Dictionary<uint, long>(o.PeerPoses))).ToArray();
            BeginMovementWindow("ground");
            await MoveFor(seconds, (bot, t) => landing + Offset(bot.Id, t), deadline.Token);
            EndMovementWindow("ground", airborne: false, clean: !Public && delay + jitter == 0 && loss == 0);
            CheckPeers("crowded movement reaches every peer");
            Check("movement latency has real client timestamps", Bots.All(b => b.Read(o => o.Poses) > 0),
                new { relays = Bots.Sum(b => b.Read(o => o.Poses)), worstBotP95Ms = Bots.Max(b => b.Observation.Percentile(.95)),
                    worstBotP99Ms = Bots.Max(b => b.Observation.Percentile(.99)) });
            double minimumPairHz = Bots.Min(b => b.Read(o => o.Peers.Values.Select(id =>
                (o.PeerPoses.GetValueOrDefault(id) - poseCountsBefore[b.Id].GetValueOrDefault(id)) / (double)seconds).DefaultIfEmpty(0).Min()));
            Check("each visible peer keeps delivering movement", minimumPairHz >= 10,
                new { minimumPairHz, requiredHz = 10 });
            if (!Public && delay + jitter == 0 && loss == 0)
                Check("clean local movement stays below 250 ms at p99", Bots.All(b => b.Observation.Percentile(.99) < 250),
                    new { worstBotP99Ms = Bots.Max(b => b.Observation.Percentile(.99)) });
            BeginMovementWindow("jumping");
            await MoveFor(9, (bot, t) => landing + Offset(bot.Id, t), deadline.Token, jumping: true);
            await MoveFor(1, (bot, t) => landing + Offset(bot.Id, 9 + t), deadline.Token);
            EndMovementWindow("jumping", airborne: false, clean: !Public && delay + jitter == 0 && loss == 0);
            int minimumJumpers = Bots.Min(b => b.Read(o => o.Peers.Values.Count(o.WindowJumpPeers.Contains)));
            Check("every observer receives every nearby player's jumping posture", minimumJumpers == count - 1,
                new { expected = count - 1, minimumJumpers });
            // The crowd scenario races fresh inventories. Repeated-match acceptance focuses
            // on ending/relogin/replay, without filling both duel players' weapon slots first.
            if (matchCycles == 1)
                for (int race = 0; race < 4; race++) await LootRace(race, deadline.Token);
            if (!Public) await Combat(deadline.Token, clean: delay + jitter == 0 && loss == 0);
            if (matchCycles > 1) await ReplayCompletedMatches(matchCycles, landing,
                cloud ?? throw new ArgumentException("Match cycles require a TLS fixture"), deadline.Token);
            if (args.Contains("--slow-client")) await SlowClientCheck(deadline.Token);
            if (cloud is not null) Check("encrypted tunnels connected before intentional disconnects", cloud.Players.All(p => !p.Tunnel.Completion.IsCompleted),
                new { connected = cloud.Players.Count(p => !p.Tunnel.Completion.IsCompleted) });
            await DisconnectCheck(deadline.Token, lossyUdp: cloud is null && loss > 0);
            var failedLinks = Bots.Where(b => !b.Disposed && (b.Client.GatewayLink!.CloseCause != LinkCloseCause.None || b.Client.Milestones.Has(HarnessMilestone.LinkClosed))).ToArray();
            Check("no unexpected gateway disconnects", failedLinks.Length == 0,
                new { faults = failedLinks.Select(b => new { b.Id, cause = b.Client.GatewayLink!.CloseCause.ToString() }).ToArray() });
            if (server is not null) Check("server listener has no errors", server.Errors.IsEmpty, new { errors = server.Errors.ToArray() });
            if (cloud is not null) Check("remaining encrypted tunnels stay connected", Bots.Where(b => !b.Disposed).All(b => !cloud.Players[b.Id].Tunnel.Completion.IsCompleted),
                new { expected = Bots.Count(b => !b.Disposed), connected = Bots.Count(b => !b.Disposed && !cloud.Players[b.Id].Tunnel.Completion.IsCompleted) });
            Check("impairment queue remains bounded without overflow", links.All(l => l.Overflow == 0),
                new { delay, jitter, loss, dropped = links.Sum(l => l.Dropped), duplicated = links.Sum(l => l.Duplicated), maximumQueue = links.Count == 0 ? 0 : links.Max(l => l.MaxQueue) });
        }
        catch (Exception ex)
        {
            exit = 1;
            File.WriteAllText(Path.Combine(Output, "failure.txt"), Public ? ex.GetType().Name + ": " + ex.Message.Split('\n')[0] : ex.ToString());
            Log("FAILED " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
            Checks.Add(new { name = "scenario completed", pass = false, evidence = ex.Message.Split('\n')[0] });
        }
        finally
        {
            using var clientProcess = Process.GetCurrentProcess();
            clientProcess.Refresh();
            var summaries = Bots.Select(b => new
            {
                b.Id, guid = b.Client.SelfGuid, disposed = b.Disposed,
                messages = b.Read(o => o.Messages), bytes = b.Read(o => o.Bytes),
                poses = b.Read(o => o.Poses), peerCount = b.Read(o => o.Peers.Count),
                p95Ms = b.Observation.Percentile(.95), p99Ms = b.Observation.Percentile(.99),
                health = b.Read(o => o.Health), healthChanges = b.Read(o => o.HealthChanges),
                itemAdds = b.Read(o => o.ItemAdds), remoteWeapons = b.Read(o => o.RemoteWeapons),
                reloads = b.Read(o => o.Reloads), loot = b.Read(o => o.Loot.Count),
                firstAmmoTick = b.Read(o => o.FirstAmmoTick),
                inventory = b.Read(o => o.Inventory.Values.ToArray()),
                fault = b.Client.GatewayLink?.Fault,
            }).ToArray();
            if (menu is not null) Check("menu players stay connected and receive no match replication", menu.Healthy, menu.Summary());
            if (voice is not null) Check("proximity voice supports simultaneous talkers and excludes menu players", voice.Healthy, voice.Summary());
            File.WriteAllText(Path.Combine(Output, "result.json"), JsonSerializer.Serialize(new
            {
                population = count, publicTunnel = Public, loopbackTls = fixture?.Tls is not null, durationSeconds = Clock.Elapsed.TotalSeconds,
                clientProcess = new { pid = clientProcess.Id, cpuSeconds = clientProcess.TotalProcessorTime.TotalSeconds,
                    peakWorkingSetBytes = clientProcess.PeakWorkingSet64, allocatedBytes = GC.GetTotalAllocatedBytes(),
                    serverGc = System.Runtime.GCSettings.IsServerGC, gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds },
                menuPopulation = menu?.Summary(),
                proximityVoice = voice?.Summary(),
                combatIncluded = !Public, matchCycles, slowClientIncluded = args.Contains("--slow-client"),
                movementTransport = UnreliableMovement ? "bare datagrams" : "reliable SOE",
                completedUtc = DateTimeOffset.UtcNow, checks = Checks, bots = summaries,
                movementWindows = MovementWindows,
                ammoRequestedTick = AmmoRequestedTick, ammoDeadlineTick = AmmoDeadlineTick,
                serverMessages = server?.Messages, serverBytes = server?.Bytes,
                serverMaxPendingBytes = server?.MaxPendingBytes, serverMaxPendingPackets = server?.MaxPendingPackets,
                serverResends = server?.Resends,
                serverPoseTimings = server?.PoseTimings, separateFixtureProcess = fixture is not null,
                impairment = new { delay, jitter, loss, dropped = links.Sum(l => l.Dropped), forwarded = links.Sum(l => l.Forwarded), overflow = links.Sum(l => l.Overflow) },
                limits = new[] { "Protocol bots do not execute H1Z1.exe or its renderer/physics.",
                    "Local fixture uses normal live services and real UDP; roster, starter AR-15 and a common drop point are test configuration.",
                    "The public scenario covers account login, drop, movement, loot and disconnect; its combat phase is disabled.",
                    "Pose age includes client generation, both transport legs and bot scheduling; histogram caps at 2000 ms." }
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (voice is not null) await voice.DisposeAsync();
            foreach (var bot in Bots)
            {
                if (exit != 0 && !Public) File.WriteAllText(Path.Combine(Output, $"bot-{bot.Id:D3}-tail.txt"), bot.Client.Journal.Tail());
                if (!bot.Disposed) { await bot.Client.DisposeAsync(); bot.Disposed = true; }
            }
            if (menu is not null) await menu.DisposeAsync();
            server?.Dispose();
            foreach (var link in links) await link.DisposeAsync();
            if (cloud is not null) await cloud.DisposeAsync();
        }
        bool failed = Checks.Any(c => JsonSerializer.SerializeToElement(c).GetProperty("pass").GetBoolean() == false);
        Log($"Run complete: {(exit == 0 && !failed ? "PASS" : "FAIL")}; {Path.Combine(Output, "result.json")}");
        return exit != 0 || failed ? 1 : 0;
    }

    private static Vector3 Offset(int id, double t)
    {
        double angle = id * 2.39996 + t * .65;
        float radius = 3 + id % 5;
        return new((float)Math.Sin(angle) * radius, 0, (float)Math.Cos(angle) * radius);
    }

    private static void BeginMovementWindow(string name)
    {
        WindowGcStart = GC.GetTotalPauseDuration();
        uint tick = Tick;
        foreach (var bot in Bots) bot.Observation.BeginMovementWindow(tick);
        File.WriteAllText(Path.Combine(Output, "phase.json"), JsonSerializer.Serialize(new { name, tick, utc = DateTimeOffset.UtcNow }));
    }

    private static void EndMovementWindow(string name, bool airborne, bool clean)
    {
        uint tick = Tick;
        var windows = Bots.Where(b => !b.Disposed).Select(b => b.Observation.MovementWindow(tick, airborne)).ToArray();
        var summary = new { name, airborne, clientGcPauseMs = (GC.GetTotalPauseDuration() - WindowGcStart).TotalMilliseconds,
            worstP95Ms = windows.Max(w => w.P95Ms), worstP99Ms = windows.Max(w => w.P99Ms),
            worstP50Ms = windows.Max(w => w.P50Ms), maxMs = windows.Max(w => w.MaxMs),
            outOfOrderTimestamps = windows.Sum(w => w.OutOfOrderTimestamps),
            minimumPairHz = windows.Min(w => w.MinimumPairHz), missingPeers = windows.Sum(w => w.MissingPeers),
            stalestPeerMs = windows.Max(w => w.StalestPeerMs), olderRecords = windows.Sum(w => w.OlderRecords), bots = windows };
        MovementWindows.Add(summary);
        Log($"Movement window {name}: p99={summary.worstP99Ms} ms, minimum={summary.minimumPairHz:F2} Hz, latest peer age={summary.stalestPeerMs} ms, older records={summary.olderRecords}.");
        if (clean)
        {
            Check($"{name} movement window stays below 250 ms at p99", windows.All(w => w.P99Ms is >= 0 and < 250),
                new { summary.worstP99Ms });
            Check($"{name} peers remain fresh at the end of the window", windows.All(w => w.MissingPeers == 0 && w.StalestPeerMs < (airborne ? 350 : 250)),
                new { summary.missingPeers, summary.stalestPeerMs });
        }
    }

    private static async Task MoveFor(double seconds, Func<Bot, double, Vector3> pose, CancellationToken ct, bool parachuting = false, bool jumping = false)
    {
        var timer = Stopwatch.StartNew();
        int frame = 0;
        using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(parachuting ? 24 : 40));
        while (timer.Elapsed.TotalSeconds < seconds && await cadence.WaitForNextTickAsync(ct))
        {
            var lost = Bots.FirstOrDefault(b => !b.Disposed && !b.ExpectingDisconnect
                && b.Client.GatewayLink?.CloseCause is not (null or LinkCloseCause.None));
            if (lost is not null)
                throw new IOException($"Bot {lost.Id} lost its game connection: {lost.Client.GatewayLink!.CloseCause}; {lost.Client.GatewayLink.Fault}");
            foreach (var bot in Bots.Where(b => !b.Disposed))
            {
                Vector3 previous = bot.Position;
                bot.Position = pose(bot, timer.Elapsed.TotalSeconds);
                double jumpPhase = Tick % 1500 / 1000d;
                bool inJump = jumping && jumpPhase < .6;
                if (inJump) bot.Position.Y += (float)(4 * jumpPhase / .6 * (1 - jumpPhase / .6));
                Vector3 travel = bot.Position - previous;
                float speed = new Vector2(travel.X, travel.Z).Length() / (parachuting ? .024f : .04f);
                uint posture = parachuting ? 0x21u : speed < .05f ? 0x441u : speed > 5.5f ? 0x405u :
                    speed < 2.8f && bot.Id % 3 == 0 ? 0x403u : 0x401u;
                if (inJump) posture = (posture & ~0x440u) | 0x20;
                if (travel.LengthSquared() > .0001f) bot.Yaw = MathF.Atan2(travel.X, travel.Z);
                SendMovement(bot, BotWire.Movement(bot.Position, Tick, posture: posture, yaw: bot.Yaw), "bot movement");
                if (parachuting && frame % 7 == 0)
                    SendMovement(bot, BotWire.Movement(bot.Position, Tick, managed: bot.Read(o => o.ChuteTransient) ?? 2), "bot canopy movement");
            }
            frame++;
        }
    }

    private static void SendMovement(Bot bot, byte[] packet, string description)
    {
        if (UnreliableMovement) bot.Client.GatewayLink!.SendUnreliable(packet, description);
        else bot.Client.GatewayLink!.Send(packet, description);
    }

    private static void CheckPeers(string name)
    {
        var expected = Bots.Where(b => !b.Disposed).Select(b => b.Client.SelfGuid).ToHashSet();
        int[] counts = Bots.Where(b => !b.Disposed).Select(b => b.Read(o => o.Peers.Keys.Count(g => expected.Contains(g)))).ToArray();
        Check(name, counts.All(n => n == expected.Count - 1), new { expected = expected.Count - 1, minimum = counts.Min(), maximum = counts.Max() });
    }

    private static async Task LootRace(int race, CancellationToken ct)
    {
        var first = Bots[0];
        var candidates = first.Read(o => o.Loot.Values.OrderBy(e => Vector3.Distance(e.Position, first.Position)).ToArray());
        LightweightEntity? target = null;
        Dictionary<int, ulong> views = [];
        foreach (var item in candidates)
        {
            views.Clear();
            foreach (var bot in Bots)
            {
                var view = bot.Read(o => o.Loot.Values.FirstOrDefault(e => e.ModelId == item.ModelId && Vector3.DistanceSquared(e.Position, item.Position) < .01f));
                if (view is not null) views[bot.Id] = view.Guid;
            }
            if (views.Count == Bots.Count) { target = item; break; }
        }
        if (target is null) { Check($"shared loot race {race + 1}", false, "No common streamed object"); return; }
        uint targetDefinition = first.Read(o => o.WorldItemDefinitions.GetValueOrDefault(target.Guid));
        if (targetDefinition == 0) { Check($"shared loot race {race + 1}", false, "Missing world item definition"); return; }
        var starts = Bots.Select(b => b.Position).ToArray();
        double walk = Math.Max(1, starts.Max(p => Vector3.Distance(p, target.Position)) / 5.8);
        await MoveFor(walk, (bot, t) => Vector3.Lerp(starts[bot.Id], target.Position, (float)Math.Min(1, t / walk)), ct);
        await MoveFor(.8, (_, _) => target.Position, ct);
        // A stack merge sends ItemUpdate, not ItemAdd. Count positive quantity changes for this
        // item's definition, so an unrelated update or a duplicate packet cannot win the race.
        long[] before = Bots.Select(b => b.Read(o => o.GrantedUnitsByDefinition.GetValueOrDefault(targetDefinition))).ToArray();
        await Task.WhenAll(Bots.Select(b => b.Client.PressInteractAsync(views[b.Id], target.Position, ct)));
        await MoveFor(1.5, (_, _) => target.Position, ct);
        int winners = Bots.Count(b => b.Read(o => o.GrantedUnitsByDefinition.GetValueOrDefault(targetDefinition)) > before[b.Id]);
        int removed = Bots.Count(b => b.Read(o => o.Removed.Contains(views[b.Id])));
        Check($"shared loot race {race + 1}", winners == 1 && removed == Bots.Count,
            new { contenders = Bots.Count, winners, removedViews = removed, target.ModelId, targetDefinition,
                grantedUnits = Bots.Sum(b => b.Read(o => o.GrantedUnitsByDefinition.GetValueOrDefault(targetDefinition)) - before[b.Id]) });
        before = Bots.Select(b => b.Read(o => o.GrantedUnitsByDefinition.GetValueOrDefault(targetDefinition))).ToArray();
        await Task.WhenAll(Bots.Select(b => b.Client.PressInteractAsync(views[b.Id], target.Position, ct)));
        await MoveFor(.8, (_, _) => target.Position, ct);
        Check($"duplicate loot press {race + 1}", winners == 1 && Bots.All(b => b.Read(o => o.GrantedUnitsByDefinition.GetValueOrDefault(targetDefinition)) == before[b.Id]),
            new { extraGrantedUnits = Bots.Sum(b => b.Read(o => o.GrantedUnitsByDefinition.GetValueOrDefault(targetDefinition)) - before[b.Id]) });
    }

    private static async Task Combat(CancellationToken ct, bool clean)
    {
        var centre = Bots[0].Position;
        await MoveFor(3, (bot, t) => centre + Offset(bot.Id, t) * .5f, ct);
        var guns = Bots.Select(b => b.Read(o => o.Inventory.Values.FirstOrDefault(i => i.DefinitionId == 2425))).ToArray();
        Check("every combat bot owns a wire-granted AR-15", guns.All(g => g is not null), new { armed = guns.Count(g => g is not null), total = Bots.Count });
        if (guns.Any(g => g is null)) return;
        BeginMovementWindow("weapon-draw");
        for (int i = 0; i < Bots.Count; i++) Bots[i].Client.GatewayLink!.Send(BotWire.SelectSlot(guns[i]!.SlotId));
        // The isolated fixture's loopback console supplies test ammunition; all firing/reloading
        // still uses the ordinary player messages. This is never sent to the public service.
        AmmoRequestedTick = Tick;
        foreach (var bot in Bots) bot.Client.GatewayLink!.Send(ZoneClientMessages.ExecuteCommand("give", "1429 60"));
        await MoveFor(1, (bot, t) => centre + Offset(bot.Id, t) * .5f, ct);
        // Loss can deliver the final inventory response on the same clock tick as the
        // one-second draw sample. Start firing from observed readiness, not that sleep.
        // Keep moving while waiting, retain the window's latency measurements, and fail
        // setup if any inventory still lacks ammunition after five seconds in total.
        while (unchecked(Tick - AmmoRequestedTick.Value) < 5000
            && Bots.Any(b => b.Read(o => !o.Inventory.Values.Any(i => i.DefinitionId == 1429))))
            await MoveFor(.05, (bot, t) => centre + Offset(bot.Id, t) * .5f, ct);
        AmmoDeadlineTick = Tick;
        Check("test ammunition reached each inventory", Bots.All(b => b.Read(o => o.Inventory.Values.Any(i => i.DefinitionId == 1429))),
            new { supplied = Bots.Count(b => b.Read(o => o.Inventory.Values.Any(i => i.DefinitionId == 1429))),
                elapsedMs = unchecked(AmmoDeadlineTick.Value - AmmoRequestedTick.Value), timeoutMs = 5000 });
        EndMovementWindow("weapon-draw", airborne: false, clean);
        BeginMovementWindow("combat");
        for (int i = 0; i < Bots.Count; i++) Bots[i].Client.GatewayLink!.Send(BotWire.Reload(guns[i]!.ItemGuid, Tick));
        await MoveFor(4, (bot, t) => centre + Offset(bot.Id, t) * .5f, ct);
        int remoteHands = Bots.Sum(viewer => viewer.Read(o => Bots.Count(subject => subject.Id != viewer.Id
            && o.RemoteHandItems.GetValueOrDefault(subject.Client.SelfGuid) == guns[subject.Id]!.ItemGuid)));
        Check("every observer receives each drawn weapon mesh and item binding", remoteHands == Bots.Count * (Bots.Count - 1),
            new { expected = Bots.Count * (Bots.Count - 1), received = remoteHands });
        uint[] before = Bots.Select(b => b.Read(o => o.Health)).ToArray();
        for (int i = 0; i < Bots.Count; i++)
        {
            var bot = Bots[i]; var victim = Bots[(i + 1) % Bots.Count];
            bot.Client.GatewayLink!.Send(BotWire.FireState(guns[i]!.ItemGuid, Tick, 1));
            bot.Client.GatewayLink.Send(BotWire.Fire(guns[i]!.ItemGuid, bot.Position, (uint)(9000 + i), Tick));
            bot.Client.GatewayLink.Send(BotWire.FireHint(guns[i]!.ItemGuid, bot.Position, victim.Position, (uint)(9000 + i), Tick));
            bot.Client.GatewayLink.Send(BotWire.Hit(victim.Client.SelfGuid, victim.Position, (uint)(9000 + i), Tick));
        }
        await MoveFor(1.5, (bot, t) => centre + Offset(bot.Id, t) * .5f, ct);
        Check("simultaneous close-range hits damage each opponent", Bots.All(b => b.Read(o => o.Health) < before[b.Id]),
            new { damaged = Bots.Count(b => b.Read(o => o.Health) < before[b.Id]), health = Bots.Take(8).Select(b => b.Read(o => o.Health)).ToArray() });
        Check("each victim receives native incoming-hit feedback", Bots.All(b => b.Read(o => o.IncomingHits) > 0),
            new { minimum = Bots.Min(b => b.Read(o => o.IncomingHits)) });
        before = Bots.Select(b => b.Read(o => o.Health)).ToArray();
        int[] incomingBefore = Bots.Select(b => b.Read(o => o.IncomingHits)).ToArray();
        for (int i = 0; i < Bots.Count; i++)
        {
            var victim = Bots[(i + 1) % Bots.Count];
            Bots[i].Client.GatewayLink!.Send(BotWire.Hit(victim.Client.SelfGuid, victim.Position, (uint)(9000 + i), Tick));
        }
        await MoveFor(.6, (bot, t) => centre + Offset(bot.Id, t) * .5f, ct);
        // A legitimate wound can tick while the replay is in flight. A replay must
        // produce no additional impact, and cannot remove another bullet's worth of HP.
        Check("replayed bullet reports cannot deal damage twice", Bots.All(b => b.Read(o => o.IncomingHits) == incomingBefore[b.Id]
            && b.Read(o => o.Health) >= before[b.Id] - Math.Min(before[b.Id], 50u)),
            new { extraImpacts = Bots.Sum(b => b.Read(o => o.IncomingHits) - incomingBefore[b.Id]) });
        // Sustained shooting while clustered, followed by every client requesting a reload.
        for (int shot = 0; shot < 12; shot++)
        {
            for (int i = 0; i < Bots.Count; i++)
            {
                Bots[i].Client.GatewayLink!.Send(BotWire.Fire(guns[i]!.ItemGuid, Bots[i].Position, (uint)(10000 + shot * 150 + i), Tick));
                Bots[i].Client.GatewayLink!.Send(BotWire.FireHint(guns[i]!.ItemGuid, Bots[i].Position, Bots[(i + 1) % Bots.Count].Position, (uint)(10000 + shot * 150 + i), Tick));
            }
            await MoveFor(.2, (bot, t) => centre + Offset(bot.Id, shot * .2 + t) * .5f, ct);
        }
        for (int i = 0; i < Bots.Count; i++) Bots[i].Client.GatewayLink!.Send(BotWire.Reload(guns[i]!.ItemGuid, Tick));
        await MoveFor(4, (bot, t) => centre + Offset(bot.Id, t) * .5f, ct);
        Check("nearby clients receive weapon activity", Bots.All(b => b.Read(o => o.RemoteWeapons) > 1),
            new { minimum = Bots.Min(b => b.Read(o => o.RemoteWeapons)), total = Bots.Sum(b => b.Read(o => o.RemoteWeapons)) });
        Check("reload requests receive server responses", Bots.All(b => b.Read(o => o.Reloads) > 0),
            new { acknowledged = Bots.Count(b => b.Read(o => o.Reloads) > 0) });
        Check("nearby clients receive actual firing events", Bots.All(b => b.Read(o => o.RemoteFireStarts) >= Bots.Count - 1),
            new { minimumFireEvents = Bots.Min(b => b.Read(o => o.RemoteFireStarts)) });
        EndMovementWindow("combat", airborne: false, clean);
        if (CombatSeconds > 0)
        {
            BeginMovementWindow("sustained-combat");
            var initialFireEvents = Bots.Select(b => b.Read(o => new Dictionary<uint, long>(o.PeerFireStarts))).ToArray();
            var sustained = Stopwatch.StartNew();
            int cycle = 0, shots = 0;
            while (sustained.Elapsed.TotalSeconds < CombatSeconds)
            {
                if (cycle % 3 == 0)
                    foreach (var bot in Bots) bot.Client.GatewayLink!.Send(ZoneClientMessages.ExecuteCommand("give", "1429 60"));
                for (int shot = 0; shot < 20; shot++, shots++)
                {
                    for (int i = 0; i < Bots.Count; i++)
                    {
                        uint projectile = (uint)(100000 + shots * 150 + i);
                        Bots[i].Client.GatewayLink!.Send(BotWire.Fire(guns[i]!.ItemGuid, Bots[i].Position, projectile, Tick));
                        Bots[i].Client.GatewayLink!.Send(BotWire.FireHint(guns[i]!.ItemGuid, Bots[i].Position,
                            Bots[(i + 1) % Bots.Count].Position, projectile, Tick));
                    }
                    await MoveFor(.2, (bot, _) => centre + Offset(bot.Id, sustained.Elapsed.TotalSeconds) * .5f, ct, jumping: true);
                }
                for (int i = 0; i < Bots.Count; i++) Bots[i].Client.GatewayLink!.Send(BotWire.Reload(guns[i]!.ItemGuid, Tick));
                await MoveFor(4, (bot, _) => centre + Offset(bot.Id, sustained.Elapsed.TotalSeconds) * .5f, ct, jumping: true);
                cycle++;
            }
            EndMovementWindow("sustained-combat", airborne: false, clean);
            double minimumFireRate = Bots.Min(b => b.Read(o => o.Peers.Values.Select(id =>
                (o.PeerFireStarts.GetValueOrDefault(id) - initialFireEvents[b.Id].GetValueOrDefault(id))
                / sustained.Elapsed.TotalSeconds).DefaultIfEmpty(0).Min()));
            Check("sustained crowded shooting and reloads continue reaching every observer", minimumFireRate >= 1,
                new { seconds = sustained.Elapsed.TotalSeconds, cycles = cycle, shotsPerPlayer = shots, minimumFireEventsPerPeerPerSecond = minimumFireRate });
        }
        for (uint shot = 0; shot < 3; shot++)
        {
            var shooter = Bots[0]; var victim = Bots[1]; uint projectile = 2_000_000 + shot;
            shooter.Client.GatewayLink!.Send(BotWire.Fire(guns[0]!.ItemGuid, shooter.Position, projectile, Tick));
            shooter.Client.GatewayLink.Send(BotWire.FireHint(guns[0]!.ItemGuid, shooter.Position, victim.Position, projectile, Tick));
            shooter.Client.GatewayLink.Send(BotWire.Hit(victim.Client.SelfGuid, victim.Position, projectile, Tick));
            await MoveFor(.25, (bot, _) => bot.Position, ct);
        }
        await MoveFor(1, (bot, _) => bot.Position, ct);
        Check("lethal combat updates victim and nearby death state", Bots[1].Read(o => o.Health) == 0 && Bots.Where(b => b.Id != 1).All(b => b.Read(o => o.Deaths.Contains(Bots[1].Client.SelfGuid))),
            new { victimHealth = Bots[1].Read(o => o.Health), informedPeers = Bots.Count(b => b.Read(o => o.Deaths.Contains(Bots[1].Client.SelfGuid))) });
    }

    private static async Task DisconnectCheck(CancellationToken ct, bool lossyUdp)
    {
        int remove = Math.Max(1, Bots.Count / 5);
        var leaving = Bots.Where(b => !b.Disposed).TakeLast(remove).ToArray();
        foreach (var bot in leaving) bot.Client.GatewayLink!.Disconnect();
        await Task.WhenAll(leaving.Select(b => b.Client.ExpectAsync(HarnessMilestone.LinkClosed, TimeSpan.FromSeconds(5), cancellationToken: ct)));
        await Task.WhenAll(leaving.Select(async bot => { await bot.Client.DisposeAsync(); bot.Disposed = true; }));
        bool Despawned() => Bots.Where(b => !b.Disposed).All(b =>
            b.Read(o => leaving.All(g => !o.Peers.ContainsKey(g.Client.SelfGuid))));
        var cleanup = Stopwatch.StartNew();
        await MoveFor(3, (bot, _) => bot.Position, ct);
        // A UDP disconnect is not reliable. If it is lost, the normal 45-second silent
        // peer timeout must still retire the player. TLS and clean UDP retain the
        // three-second cleanup check; record the actual wait in the impaired case.
        while (lossyUdp && cleanup.Elapsed.TotalSeconds < 60 && !Despawned())
            await MoveFor(.1, (bot, _) => bot.Position, ct);
        Check("disconnecting players despawn for remaining clients", Despawned(),
            new { disconnected = leaving.Length, remaining = Bots.Count(b => !b.Disposed),
                elapsedSeconds = cleanup.Elapsed.TotalSeconds, timeoutSeconds = lossyUdp ? 60 : 3 });
    }

    private static async Task SlowClientCheck(CancellationToken ct)
    {
        var slow = Bots[^1];
        slow.ExpectingDisconnect = true;
        slow.Client.GatewayLink!.AckingEnabled = false;
        var centre = Bots[0].Position;
        // The current server grants 45 s for a stalled native world load. It then closes silently;
        // this independent client needs up to another 7.5 s to detect missing ACKs of its own sends.
        await MoveFor(60, (bot, t) => centre + Offset(bot.Id, t) * .5f, ct);
        Check("a client that stops acknowledging is disconnected", slow.Client.GatewayLink.CloseCause != LinkCloseCause.None,
            new { slow.Id, cause = slow.Client.GatewayLink.CloseCause.ToString(), budgetSeconds = 60 });
        Check("one unresponsive client leaves the others connected", Bots.Where(b => b != slow).All(b => b.Client.GatewayLink!.CloseCause == LinkCloseCause.None),
            new { connected = Bots.Count(b => b != slow && b.Client.GatewayLink!.CloseCause == LinkCloseCause.None), expected = Bots.Count - 1 });
        await slow.Client.DisposeAsync(); slow.Disposed = true;
        await MoveFor(3, (bot, _) => bot.Position, ct);
        Check("the unresponsive player is removed from every view", Bots.Where(b => !b.Disposed).All(b => b.Read(o => !o.Peers.ContainsKey(slow.Client.SelfGuid))),
            new { viewers = Bots.Count(b => !b.Disposed) });
    }

    private static string? Text(string[] args, string name) { int i = Array.IndexOf(args, name); return i < 0 ? null : args[i + 1]; }
    private static int Int(string[] args, string name, int fallback) => int.TryParse(Text(args, name), out int value) ? value : fallback;
    internal sealed class Bot(int id, HarnessClient client, BotObservation observation)
    {
        public int Id = id;
        public HarnessClient Client = client;
        public BotObservation Observation = observation;
        public Vector3 Position;
        public float Yaw;
        public bool Disposed;
        public bool ExpectingDisconnect;
        public T Read<T>(Func<BotObservation, T> read) { lock (Observation.Gate) return read(Observation); }
    }
}
