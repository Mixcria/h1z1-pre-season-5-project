using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private readonly record struct BotWorldKey(ulong Match, uint World, ulong PrivateOwner, int Generation);
    private sealed class BotWorld(BotWorldKey key)
    {
        public BotWorldKey Key { get; } = key;
        public List<CombatBot> Bots { get; } = [];
        public Dictionary<GatewaySessionState, SoeConnection> Viewers { get; } = [];
        public bool Frozen { get; set; }
    }
    private readonly Dictionary<BotWorldKey, BotWorld> _botWorlds = [];
    private ulong _nextBotGuid = 0x5b00_0000_0000_0001;
    private uint _nextBotTransient = 4_000_000;

    private static BotWorldKey BotKey(GatewaySessionState state) => new(state.BountyAdmission.MatchId,
        state.BountyWorldId, state.BountyAdmission.MatchId == 0 ? state.Guid : 0,
        state.BountyAdmission.MatchId == 0 ? state.WorldGeneration : 0);

    private static float? BotGround(float x, float z) =>
        AirdropTerrainData.Value.TryGetHeight(x, z, out float height) ? height : null;

    private ConsoleReply ConsoleBots(SoeConnection connection, GatewaySessionState state,
        string verb, int count, string difficulty, float distance)
    {
        var key = BotKey(state);
        _botWorlds.TryGetValue(key, out var world);
        if (verb == "status") return ConsoleReply.Plain(world is null
            ? ["* no combat bots in this match; /bots 10 spawns ten"]
            : [$"* bots: {world.Bots.Count(b => b.Body.IsAlive)} alive, {world.Bots.Count} total, {(world.Frozen ? "FROZEN" : "active")}; visible to {world.Viewers.Count} player(s)",
                $"  shots={world.Bots.Sum(b => b.ShotsFired)}, hits={world.Bots.Sum(b => b.Hits)}; /bots clear removes them",
                "  Hostile to players; outdoor terrain movement. Building navigation is not implemented."]);
        if (verb == "clear")
        {
            int removed = world?.Bots.Count ?? 0;
            if (world is not null) RemoveBotWorld(world);
            foreach (var (link, viewer) in _throwableSessions.Values)
                if (link.State == ConnectionState.Open && BotKey(viewer) == key && viewer.Match == MatchStep.InMatch)
                    PublishAliveCount(link, viewer, AliveCount(viewer));
            return ConsoleReply.Did($"removed {removed} combat bot(s) for everyone in this match");
        }
        if (verb is "freeze" or "resume")
        {
            if (world is null) return ConsoleReply.Failed("no bots yet; /bots 10 spawns ten");
            world.Frozen = verb == "freeze";
            return ConsoleReply.Did($"bots {(world.Frozen ? "frozen (movement and firing stopped)" : "resumed")} for everyone in this match");
        }
        if (state.DeathSent || state.VictorySent || state.Score.Settled || state.ChuteGuid != 0 || state.MountRequested
            || state.Movement.Player?.Position is not Vector3 origin)
            return ConsoleReply.Failed("enter a match and land before spawning bots");
        if (Post is null) return ConsoleReply.Failed("bot AI needs the host's world scheduler; no bots spawned");
        if ((world?.Bots.Count ?? 0) + count > CombatBot.MaximumPerMatch)
            return ConsoleReply.Failed($"limit is 50 bots per match; {world!.Bots.Count} already exist. /bots clear first");
        // Validate the whole group before publishing any actor. Terrain data is shared with airdrops.
        var positions = new List<Vector3>();
        try
        {
            float heading = state.Movement.Player.Orientation ?? 0;
            int existing = world?.Bots.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                float side = ((existing + i) % 8 - 3.5f) * 3;
                float ahead = distance + ((existing + i) / 8 * 4);
                float x = origin.X + MathF.Sin(heading) * ahead + MathF.Cos(heading) * side;
                float z = origin.Z + MathF.Cos(heading) * ahead - MathF.Sin(heading) * side;
                if (BotGround(x, z) is not float y || !float.IsFinite(y))
                    return ConsoleReply.Failed("spawn group reaches outside the terrain; move inland or use a shorter distance");
                positions.Add(new(x, y, z));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { return ConsoleReply.Failed($"bot terrain unavailable: {ex.Message}"); }
        bool created = world is null;
        if (world is null) _botWorlds.Add(key, world = new(key));
        long now = Environment.TickCount64;
        foreach (var position in positions)
        {
            ulong id = _nextBotGuid++;
            var body = new PracticeTarget(id, _nextBotTransient++, position, new(0, 0, 0, 1), 10_000)
                { IsCombatBot = true, Name = $"Combat Bot {id & 0xffffff}" };
            PracticeTargetKit.Equip(body);
            world.Bots.Add(new(body, difficulty, now));
        }
        SyncBotViewers(world);
        if (_sharedLootMembership.TryGetValue(state, out ulong sharedId))
        {
            _sharedLootMatches[sharedId].HadMultiplePlayers = true;
            _sharedLootMatches[sharedId].HadMultipleTeams = true;
        }
        foreach (var (viewer, link) in world.Viewers) PublishAliveCount(link, viewer, AliveCount(viewer));
        if (created) ScheduleBots(world);
        return ConsoleReply.Did($"spawned {count} {difficulty} combat bots ({world.Bots.Count}/50), shared with {world.Viewers.Count} player(s); 5s preparation before firing; /bots freeze or /bots clear");
    }

    private void ScheduleBots(BotWorld world) => Later(null, CombatBot.TickMs, () => PumpBots(world));

    private void PumpBots(BotWorld world)
    {
        if (!_botWorlds.TryGetValue(world.Key, out var current) || !ReferenceEquals(world, current)) return;
        SyncBotViewers(world);
        if (world.Viewers.Count == 0) { RemoveBotWorld(world); return; }
        long now = Environment.TickCount64;
        var opponents = world.Viewers.Where(p => p.Value.State == ConnectionState.Open
                && !p.Key.DeathSent && !p.Key.VictorySent && p.Key.Hitpoints > 0
                && p.Key.ChuteGuid == 0 && !p.Key.MountRequested && p.Key.Movement.Player?.Position is not null)
            .Select(p => new BotOpponent(p.Key.Guid, GameplayDamagePosition(p.Key)!.Value)).ToArray();
        foreach (var bot in world.Bots.ToArray())
        {
            if (!bot.Body.IsAlive)
            {
                if (bot.Body.DespawnDue(now, _options.Combat.PracticeTargetDespawnAfterDeathMs)) RemoveCombatBot(world, bot);
                continue;
            }
            BotShot? shot = bot.Tick(now, opponents, BotGround, world.Frozen);
            byte[] record = BotMovement.Record(bot, now);
            foreach (var (viewer, link) in world.Viewers)
            {
                if (link.State != ConnectionState.Open || BotKey(viewer) != world.Key) continue;
                // Use the peer sender's existing latest-pose queue so a slow viewer cannot grow
                // an unbounded backlog of bot positions.
                if (viewer.Peer?.Sink is SessionPeerSink sink)
                    sink.SendPose(bot.Body.WorldGuid, bot.Body.TransientId, record, completeSnapshot: true);
                else SendTunnel(link, w => w.WriteRaw(PeerSpawnWriter.PlayerUpdatePosition(bot.Body.TransientId, record)));
            }
            if (shot is { } fire) FireCombatBot(world, bot, fire);
        }
        ScheduleBots(world);
    }

    private void SyncBotViewers(BotWorld world)
    {
        foreach (var (viewer, link) in world.Viewers.ToArray())
            if (link.State != ConnectionState.Open || BotKey(viewer) != world.Key || viewer.Match != MatchStep.InMatch)
            {
                foreach (var bot in world.Bots) RemoveBotFromViewer(viewer, link, bot.Body);
                world.Viewers.Remove(viewer);
            }
        foreach (var (link, viewer) in _throwableSessions.Values)
        {
            if (link.State != ConnectionState.Open || viewer.Match != MatchStep.InMatch || BotKey(viewer) != world.Key) continue;
            world.Viewers[viewer] = link;
            viewer.Score.PracticeSession = true;
            foreach (var bot in world.Bots)
            {
                var body = bot.Body;
                if (viewer.Combat.Targets.Find(body.WorldGuid) is not null || !body.IsAlive) continue;
                viewer.Combat.Targets.AddShared(body);
                var spawn = new PracticeTargetSpawn(body, w => PracticeTargetKit.WriteSpawn(w, body),
                    w => PracticeTargetKit.WriteDress(w, body), "shared combat bot");
                SendPracticeTargetSpawn(link, spawn);
                viewer.FullNpcSent.Add(body.WorldGuid);
            }
            PublishAliveCount(link, viewer, AliveCount(viewer));
        }
    }

    private void RemoveBotFromViewer(GatewaySessionState state, SoeConnection connection, PracticeTarget body)
    {
        state.Combat.Targets.Remove(body);
        state.FullNpcSent.Remove(body.WorldGuid);
        connection.ForgetLatest(body.WorldGuid);
        if (connection.State == ConnectionState.Open)
            SendTunnel(connection, w => new RemovePlayer(body.WorldGuid).WriteTo(w));
    }

    private void RemoveCombatBot(BotWorld world, CombatBot bot)
    {
        foreach (var (state, link) in world.Viewers) RemoveBotFromViewer(state, link, bot.Body);
        world.Bots.Remove(bot);
    }

    private void RemoveBotWorld(BotWorld world)
    {
        foreach (var bot in world.Bots.ToArray()) RemoveCombatBot(world, bot);
        _botWorlds.Remove(world.Key);
        world.Viewers.Clear();
    }

    private void PublishBotDress(GatewaySessionState shooter, PracticeTarget bot)
    {
        if (!_botWorlds.TryGetValue(BotKey(shooter), out var world)) return;
        foreach (var (viewer, link) in world.Viewers)
            if (link.State == ConnectionState.Open && BotKey(viewer) == world.Key)
                SendTunnel(link, w => PracticeTargetKit.WriteDress(w, bot));
    }

    private void KillCombatBot(SoeConnection connection, GatewaySessionState killer, PracticeTarget dead)
    {
        if (dead.IsAlive || dead.DeathPublished || !_botWorlds.TryGetValue(BotKey(killer), out var world)) return;
        dead.DeathPublished = true;
        DropPracticeBodyBag(connection, killer, dead);
        bool credited = killer.Score.Credit(dead.WorldGuid, true);
        var feed = new RetailKillFeed(new(dead.WorldGuid, dead.Name, dead.Badge), FeedPlayer(killer),
            dead.LastWeaponItemId, Headshot: dead.LastHeadshot);
        foreach (var (viewer, link) in world.Viewers)
        {
            if (link.State != ConnectionState.Open || BotKey(viewer) != world.Key) continue;
            link.ForgetLatest(dead.WorldGuid);
            SendTunnel(link, w => StartMultiStateDeath.RagdollFor(dead.WorldGuid, viewer.Guid).WriteTo(w));
            if (credited) SendTunnel(link, feed.WriteTo);
        }
        if (credited) PublishKillScore(connection, killer, dead.WorldGuid, dead.Name);
        if (_sharedLootMembership.TryGetValue(killer, out ulong id)) PublishSharedAlive(_sharedLootMatches[id]);
        else
        {
            int alive = AliveCount(killer);
            PublishAliveCount(connection, killer, alive);
            if (alive == 1 && !killer.DeathSent) SendVictory(connection, killer);
        }
    }

    private int CountCombatBots(GatewaySessionState state) => _botWorlds.TryGetValue(BotKey(state), out var world)
        ? world.Bots.Count(b => b.Body.IsAlive) : 0;

    private int CountCombatBots(SharedLootMatch match) => match.Members.Keys.FirstOrDefault() is { } member
        ? CountCombatBots(member) : 0;

    private PracticeTarget? FindCombatBot(GatewaySessionState state, ulong guid) =>
        _botWorlds.TryGetValue(BotKey(state), out var world) ? world.Bots.FirstOrDefault(b => b.Body.WorldGuid == guid)?.Body : null;

    private void FireCombatBot(BotWorld world, CombatBot bot, BotShot shot)
    {
        ulong weapon = bot.Body.WorldGuid + 0x1000_0000;
        byte[] start = RemoteWeaponPackets.FireState(bot.Body.TransientId, weapon, true, new(shot.AimPoint, 1));
        byte[] launch = RemoteWeaponPackets.ProjectileLaunch(bot.Body.TransientId, weapon, (uint)bot.ShotsFired);
        byte[] stop = RemoteWeaponPackets.FireState(bot.Body.TransientId, weapon, false, new(shot.AimPoint, 1));
        foreach (var (viewer, link) in world.Viewers)
        {
            if (link.State != ConnectionState.Open || BotKey(viewer) != world.Key) continue;
            SendTunnel(link, w => w.WriteRaw(start));
            SendTunnel(link, w => w.WriteRaw(launch));
            SendTunnel(link, w => w.WriteRaw(stop));
        }
        if (!shot.Hit || !_options.Combat.EnableCombatDamage) return;
        var victim = world.Viewers.FirstOrDefault(p => p.Key.Guid == shot.VictimGuid);
        if (victim.Key is not { } state || victim.Value.State != ConnectionState.Open
            || BotKey(state) != world.Key || !CanTakeGameplayDamage(state) || state.MountRequested
            || state.ChuteGuid != 0 || GameplayDamagePosition(state) is not Vector3 at) return;
        InventoryItemInstance? protection = state.Inventory?.EquipmentSlots.Values.FirstOrDefault(i => ArmourModel.TierOf(i.DefinitionId) != ArmourTier.None);
        Armour armour = protection is null ? default : state.PlayerArmour.GetValueOrDefault(protection.Guid);
        HitOutcome outcome = HitRule.Resolve(ref armour, protection?.DefinitionId ?? 0, 0,
            RetailBalance.WeaponDefinitionIdFor(PracticeTargetKit.Rifle), "", Vector3.Distance(bot.Body.Position, at),
            _options.Combat.UnmappedWeaponBodyUnits);
        float bearing = MathF.Atan2(bot.Body.Position.Z - at.Z, bot.Body.Position.X - at.X);
        SendTunnel(victim.Value, new IncomingDamage((uint)Math.Max(1, outcome.DamageUnits), bearing,
            (float)outcome.DamageUnits / _options.Gas.MaxHitpoints, outcome.DamagedArmour ? 1 : 0, false).WriteTo);
        if (protection is not null)
        {
            state.PlayerArmour[protection.Guid] = armour;
            if (outcome.BrokeArmour && state.Inventory is PlayerInventory inventory)
            {
                inventory.RemoveUnits(protection.Guid, 0);
                SendTunnel(victim.Value, w => new ItemDelete(state.Guid, protection.Guid).WriteTo(w));
                SendLoadoutSlots(victim.Value, inventory);
                SendCharacterAppearance(victim.Value, state, "combat bot broke armour");
            }
        }
        state.LastDamageWeapon = PracticeTargetKit.Rifle;
        state.LastDamageHeadshot = false;
        if (outcome.DamageUnits > 0)
            ApplyDamage(victim.Value, state, (uint)outcome.DamageUnits, DamageCause.Bullet,
                bot.Body.WorldGuid, bot.Body.Name, (uint)bot.Body.Health);
        if (outcome.Bleeds)
            WoundPlayer(victim.Value, state, bot.Body.WorldGuid, bot.Body.Name, (uint)bot.Body.Health, PracticeTargetKit.Rifle);
    }
}
