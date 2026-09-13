using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;
using Cranberry.Zone.Progression;
using Cranberry.Zone.World;
using Cranberry.Tests.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
    private sealed class Recorder : IPacketRecorder
    {
        public List<byte[]> Sent { get; } = [];
        public List<(SoeConnection Connection, byte[] Packet)> Routed { get; } = [];
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") { var packet = bytes.ToArray(); Sent.Add(packet); Routed.Add((connection, packet)); } }
    }
    private static object? Call(ZoneService service, string name, params object?[] args) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args);
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);

    private sealed class FaultingExperienceStore : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("cranberry-combat-xp-");
        public bool FailWrites { get; set; }
        public AccountExperience Experience { get; }

        public FaultingExperienceStore()
        {
            var store = new AccountEconomyStore(_directory.FullName, persistenceFault: stage =>
            {
                if (FailWrites && stage == EconomyPersistenceStage.BeforeWrite)
                    throw new IOException("Injected combat XP save failure.");
            });
            Experience = new(store, ExperienceCurve.Default);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class Fixture : IDisposable
    {
        public Recorder Recorder { get; } = new();
        public ZoneService Service { get; }
        public Fixture(ZoneOptions? options = null) => Service = new(new SilentLog(), Recorder, new GatewayTicketRegistry(), options);
        public List<SoeConnection> Connections { get; } = [];
        public SoeConnection Add(ulong guid, Vector3 position, ulong matchId = 1,
            MatchMode mode = MatchMode.Solo, MatchQueueKind queue = MatchQueueKind.Public)
        {
            var request = new SessionRequest(3, (uint)guid, 512, ZoneService.ProtocolName);
            var c = new SoeConnection(new(IPAddress.Loopback, 10000 + (int)guid), in request, new(),
                SessionDecision.Clear, Service, new SilentLog(), (_, _) => { }, 0);
            Service.OnConnected(c);
            object state = c.Tag!;
            Set(state, "Guid", guid);
            Set(state, "CharacterName", $"Player{guid}");
            Set(state, "Visuals", CharacterVisuals.FromSelection(1, 1, 1, 664, 270));
            Set(state, "Wardrobe", new AugustWardrobeState());
            Set(state, "BountyAdmission", new MatchAdmissionContext(matchId, queue, mode));
            var party = Parties.Find(guid);
            Set(state, "MatchPartyId", party?.Id ?? 0u);
            Set(state, "MatchPartySize", party?.Members.Count ?? 1);
            Service.ForTest(c).EnterMatch();
            Call(Service, "RegisterPeerSession", c, state);
            Call(Service, "JoinSharedLoot", c, state);
            Call(Service, "NotePeerInMatch", state, true);
            Move(c, position);
            Connections.Add(c);
            return c;
        }
        public void Move(SoeConnection c, Vector3 position)
        {
            var bytes = MovementRecord.Position(position);
            Get<SessionMovementState>(c.Tag!, "Movement").ApplyPlayer(ClientMovementUpdate.Parse(bytes));
            var peer = Get<PeerSession>(c.Tag!, "Peer");
            peer.Position = position; peer.SetPose(bytes);
        }
        public void PublishMovedTeamPose(SoeConnection c, Vector3 position)
        {
            Move(c, position);
            // Make the production one-second pose cadence due without a wall-clock sleep.
            var lastSent = (System.Collections.IDictionary)typeof(ZoneService)
                .GetField("_teamPoseSent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Service)!;
            lastSent.Remove(c.Tag!);
            Call(Service, "PublishTeamPose", c.Tag);
        }
        public void SeeEveryone()
        {
            var enters = new List<PeerEnter>(); var leaves = new List<PeerLeave>();
            for (int n = 0; n < 20; n++)
                foreach (var c in Connections) Service.PeerRegistry.Sweep(Get<PeerSession>(c.Tag!, "Peer"), enters, leaves);
        }
        public void Weapon(SoeConnection c, byte[] packet, long now)
        {
            object state = c.Tag!;
            WeaponFireArm.Handle(Get<SessionCombat>(state, "Combat"), packet, CombatOptions.Default,
                2425, Get<SessionMovementState>(state, "Movement").Player!.Position!.Value, now,
                Get<List<WeaponArmResult>>(state, "WeaponArmResults"));
            Call(Service, "DrainCombatArm", c, state);
        }
        public void Shot(SoeConnection shooter, SoeConnection target, uint projectile, string location = "SPINE")
        {
            long now = projectile * 1000;
            Weapon(shooter, ShootingPacketBuilder.Fire(0x3100000000000001, 0, 0, 0, [projectile]), now);
            Weapon(shooter, ShootingPacketBuilder.HitReport(projectile, Get<ulong>(target.Tag!, "Guid"), location), now);
        }
        public ulong SpawnPlayerDummy(SoeConnection shooter)
        {
            Call(Service, "ConsolePracticeTarget", shooter, shooter.Tag, "spawn royalty");
            return Get<SessionCombat>(shooter.Tag!, "Combat").Targets.All.Single().WorldGuid;
        }
        public uint Health(SoeConnection c) => Service.ForTest(c).Hitpoints;
        public PartyRegistry Parties => (PartyRegistry)typeof(ZoneService)
            .GetField("_parties", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Service)!;
        public RankedProfile Profile(SoeConnection c, MatchMode mode) =>
            ((RankedScoreStore)typeof(ZoneService).GetProperty("RankedScores", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(Service)!).Read("character:" + Get<ulong>(c.Tag!, "Guid"), mode);
        public void Dispose()
        {
            foreach (var c in Connections) { c.Disconnect(); Service.OnDisconnected(c, DisconnectCause.ServerRequested); }
        }
    }

    private static SoeConnection[] AddFullSquadWithOpponents(Fixture fixture, MatchMode mode, int size)
    {
        var squad = Enumerable.Range(1, size)
            .Select(guid => fixture.Add((ulong)guid, new(guid, 0, 0), mode: mode)).ToArray();
        fixture.Add(100, new(7, 0, 0), mode: mode);
        fixture.Add(101, new(8, 0, 0), mode: mode); // Keep the match live after one enemy dies.
        fixture.Add(200, new(9, 0, 0), matchId: 2, mode: mode);
        fixture.SeeEveryone();
        return squad;
    }

    private static bool IsGroupPacket(byte[] packet, byte sub) =>
        packet.Length > 3 && packet[1] == 0x13 && packet[2] == sub && packet[3] == 2;

    private static bool IsMatchScore(byte[] packet) =>
        packet.Length == 110 && packet[1] == 0x67 && packet[2] == 8;

    private static bool IsExperience(byte[] packet) =>
        packet.Length >= 56 && packet[1] == 0x87 && packet[2] == 1;

    private static bool IsRetailKillFeed(byte[] packet) =>
        packet.Length > 3 && packet[1] == 0xce && packet[2] == 0x0e && packet[3] == 0;

    private static uint WireUInt32(byte[] packet, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset));

    private static ulong WireUInt64(byte[] packet, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(offset));

    // August 140a342a0/140a603c0: ClientUpdate 11/4a is metric u32, value u32, guid u64.
    // Metric 1 writes the group kill map; a later 13/14 rebuilds the SQL PlayerKills row.
    private static bool IsMemberKills(byte[] packet, ulong characterGuid) =>
        packet.Length == 20 && packet[1] == 0x11 && packet[2] == 0x4a && packet[3] == 0
        && WireUInt32(packet, 4) == 1 && WireUInt64(packet, 12) == characterGuid;

    private static void AssertMemberKills(Fixture fixture, int mark, SoeConnection[] recipients,
        ulong characterGuid, uint kills)
    {
        var packets = fixture.Recorder.Routed.Skip(mark).ToArray();
        var updates = packets.Select((record, index) => (record, index))
            .Where(item => IsMemberKills(item.record.Packet, characterGuid)).ToArray();
        foreach (var recipient in recipients)
            Assert.Contains(updates, item => ReferenceEquals(item.record.Connection, recipient));
        Assert.All(updates, item =>
        {
            Assert.Contains(item.record.Connection, recipients);
            Assert.Equal(kills, WireUInt32(item.record.Packet, 8));
            Assert.Contains(packets.Skip(item.index + 1), record =>
                ReferenceEquals(record.Connection, item.record.Connection)
                && IsGroupPacket(record.Packet, 0x14) && WireUInt64(record.Packet, 4) == characterGuid);
        });
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2, true)]
    [InlineData(MatchMode.Duos, 2, false)]
    [InlineData(MatchMode.Fives, 5, true)]
    [InlineData(MatchMode.Fives, 5, false)]
    public void PlayerAndDummyKillsAwardPrivateExperienceAfterTheNativeFeedOnlyOnce(
        MatchMode mode, int size, bool practiceFirst)
    {
        using var f = new Fixture();
        var squad = AddFullSquadWithOpponents(f, mode, size);
        var shooter = squad[0];
        var victim = f.Connections[size];
        ulong dummyGuid = f.SpawnPlayerDummy(shooter);
        string dummyName = Get<SessionCombat>(shooter.Tag!, "Combat").Targets.Find(dummyGuid)!.Name;
        // Fixture skips ClientIsReady; establish the same account baseline the real client gets.
        Call(f.Service, "SendAccountExperience", shooter, shooter.Tag);
        uint total = 0;
        uint previousProgress = 0;

        foreach (bool practice in new[] { practiceFirst, !practiceFirst })
        {
            ulong victimGuid = practice ? dummyGuid : Get<ulong>(victim.Tag!, "Guid");
            string victimName = practice ? dummyName : Get<string>(victim.Tag!, "CharacterName");
            int mark = f.Recorder.Routed.Count;
            if (practice)
                f.Service.ForTest(shooter).KillTarget(dummyGuid);
            else
                for (uint projectile = 1; projectile <= 4; projectile++) f.Shot(shooter, victim, projectile);

            total += 100;
            var sent = f.Recorder.Routed.Skip(mark).ToArray();
            // Includes teammates, the victim, opponents and another match: only the killer
            // receives the native award, even though squad members also receive score updates.
            var award = Assert.Single(sent, record => IsExperience(record.Packet));
            Assert.Same(shooter, award.Connection);
            Assert.Equal(total, WireUInt32(award.Packet, 11));
            Assert.Equal(1u, WireUInt32(award.Packet, 19));
            uint progress = WireUInt32(award.Packet, 23);
            Assert.InRange(progress, previousProgress + 1, 99u);
            previousProgress = progress;
            Assert.Equal(1u, WireUInt32(award.Packet, 35));
            Assert.Equal(100u, WireUInt32(award.Packet, 39));
            Assert.Equal(1u, WireUInt32(award.Packet, 43));
            Assert.Equal(1u, WireUInt32(award.Packet, 47));
            Assert.Equal(0u, WireUInt32(award.Packet, 51));
            Assert.Equal(victimGuid, WireUInt64(award.Packet, 55));
            int nameLength = checked((int)WireUInt32(award.Packet, 63));
            Assert.Equal(victimName, System.Text.Encoding.UTF8.GetString(award.Packet, 67, nameLength));

            var received = sent.Where(record => ReferenceEquals(record.Connection, shooter)).ToArray();
            int feedIndex = Array.FindIndex(received, record => IsRetailKillFeed(record.Packet));
            int experienceIndex = Array.FindIndex(received, record => IsExperience(record.Packet));
            Assert.True(feedIndex >= 0 && feedIndex < experienceIndex,
                "The native kill feed must establish the victim before the killer XP notification.");
            Assert.Contains(received, record => IsMatchScore(record.Packet)
                && WireUInt32(record.Packet, 91) == total);
            Assert.All(sent.Where(record => IsMatchScore(record.Packet)
                && !ReferenceEquals(record.Connection, shooter)),
                record => Assert.Equal(0u, WireUInt32(record.Packet, 91)));

            mark = f.Recorder.Routed.Count;
            if (practice)
                f.Service.ForTest(shooter).KillTarget(dummyGuid);
            else
            {
                f.Weapon(shooter, ShootingPacketBuilder.HitReport(4, victimGuid, "SPINE"), 4000);
                Assert.False(f.Service.ForTest(victim).Damage(10_000, DamageCause.Bullet,
                    Get<ulong>(shooter.Tag!, "Guid"), "Player1", 10000));
                Call(f.Service, "ScorePlayerDeath", victim, victim.Tag,
                    Get<ulong>(shooter.Tag!, "Guid"), DamageCause.Bullet);
            }
            Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet));
            Assert.Equal((int)(total / 100), Get<MatchScore>(shooter.Tag!, "Score").Kills);
        }
    }

    [Theory]
    [InlineData(DamageCause.ToxicGas, 0UL)]
    [InlineData(DamageCause.Falling, 0UL)]
    [InlineData(DamageCause.Explosion, 1UL)] // Self-inflicted death.
    [InlineData(DamageCause.Bullet, 200UL)] // A player in another match cannot earn credit.
    public void EnvironmentalSelfAndCrossMatchDeathsDoNotAwardExperience(DamageCause cause, ulong killerGuid)
    {
        using var f = new Fixture();
        var victim = f.Add(1, Vector3.Zero);
        f.Add(2, new(5, 0, 0));
        f.Add(3, new(6, 0, 0));
        f.Add(200, new(7, 0, 0), matchId: 2);
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;

        Assert.True(f.Service.ForTest(victim).Damage(10_000, cause, killerGuid));

        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet));
        Assert.All(f.Connections, connection => Assert.Equal(0, Get<MatchScore>(connection.Tag!, "Score").Kills));
        Assert.All(f.Recorder.Routed.Skip(mark).Where(record => IsMatchScore(record.Packet)),
            record => Assert.Equal(0u, WireUInt32(record.Packet, 91)));
    }

    [Fact]
    public void KillCrossesAccountLevelAndReturningToMenuDoesNotReplayMatchExperience()
    {
        using var f = new Fixture();
        typeof(ZoneService).GetField("_experience", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(f.Service, new AccountExperience(null, ExperienceCurve.Default, 1950));
        var shooter = f.Add(1, Vector3.Zero);
        var victim = f.Add(2, new(5, 0, 0));
        f.Add(3, new(7, 0, 0)); // Keep the match live after the second kill.
        f.SeeEveryone();
        Call(f.Service, "SendAccountExperience", shooter, shooter.Tag);
        ulong dummy = f.SpawnPlayerDummy(shooter);
        Assert.True(Get<SessionCombat>(shooter.Tag!, "Combat").Targets.Find(dummy)!.FullKit);

        int mark = f.Recorder.Routed.Count;
        f.Service.ForTest(shooter).KillTarget(dummy);
        var levelUp = Assert.Single(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet));
        Assert.Same(shooter, levelUp.Connection);
        Assert.Equal(1u, WireUInt32(levelUp.Packet, 3));
        Assert.Equal(2050u, WireUInt32(levelUp.Packet, 11));
        Assert.Equal(2u, WireUInt32(levelUp.Packet, 19));
        Assert.Equal(1u, WireUInt32(levelUp.Packet, 23));
        Assert.Equal(1950u, WireUInt32(levelUp.Packet, 27));
        Assert.Equal(1u, WireUInt32(levelUp.Packet, 31));

        mark = f.Recorder.Routed.Count;
        for (uint projectile = 1; projectile <= 4; projectile++) f.Shot(shooter, victim, projectile);
        var secondKill = Assert.Single(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet));
        Assert.Same(shooter, secondKill.Connection);
        Assert.Equal(0u, WireUInt32(secondKill.Packet, 3));
        Assert.Equal(2150u, WireUInt32(secondKill.Packet, 11));
        Assert.Equal(2u, WireUInt32(secondKill.Packet, 19));
        Assert.Equal(3u, WireUInt32(secondKill.Packet, 23));
        Assert.Equal(1950u, WireUInt32(secondKill.Packet, 27));
        Assert.Equal(1u, WireUInt32(secondKill.Packet, 31));
        Assert.Equal(200u, Get<uint>(shooter.Tag!, "ExperienceEarned"));
        Assert.Contains(f.Recorder.Routed.Skip(mark), record => ReferenceEquals(record.Connection, shooter)
            && IsMatchScore(record.Packet) && WireUInt32(record.Packet, 91) == 200);

        var matchType = shooter.Tag!.GetType().GetProperty("Match")!.PropertyType;
        Set(shooter.Tag!, "Match", Enum.Parse(matchType, "Menu"));
        mark = f.Recorder.Routed.Count;
        Call(f.Service, "SendAccountExperience", shooter, shooter.Tag);
        var menu = Assert.Single(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet));
        Assert.Same(shooter, menu.Connection);
        Assert.Equal(0u, WireUInt32(menu.Packet, 3));
        Assert.Equal(2150u, WireUInt32(menu.Packet, 11));
        Assert.Equal(2u, WireUInt32(menu.Packet, 19));
        Assert.Equal(3u, WireUInt32(menu.Packet, 23));
        Assert.Equal(2150u, WireUInt32(menu.Packet, 27));
        Assert.Equal(2u, WireUInt32(menu.Packet, 31));
        Assert.Equal(0u, WireUInt32(menu.Packet, 35));
    }

    [Fact]
    public void FailedKillExperienceSaveRetriesWithoutLosingOrDuplicatingTheAward()
    {
        using var storage = new FaultingExperienceStore();
        using var f = new Fixture();
        typeof(ZoneService).GetField("_experience", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(f.Service, storage.Experience);
        var shooter = f.Add(1, Vector3.Zero);
        f.Add(2, new(5, 0, 0));
        Call(f.Service, "SendAccountExperience", shooter, shooter.Tag);
        ulong dummy = f.SpawnPlayerDummy(shooter);
        storage.FailWrites = true;
        int mark = f.Recorder.Routed.Count;

        f.Service.ForTest(shooter).KillTarget(dummy);

        Assert.Equal(1, Get<MatchScore>(shooter.Tag!, "Score").Kills);
        Assert.Equal(0u, storage.Experience.Read("character:1").Total);
        Assert.Equal(0u, Get<uint>(shooter.Tag!, "ExperienceEarned"));
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet));
        Assert.Contains(f.Recorder.Routed.Skip(mark), record => ReferenceEquals(record.Connection, shooter)
            && IsMatchScore(record.Packet) && WireUInt32(record.Packet, 91) == 0);

        storage.FailWrites = false;
        mark = f.Recorder.Routed.Count;
        Call(f.Service, "RetryPendingExperience", true);

        var award = Assert.Single(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet));
        Assert.Same(shooter, award.Connection);
        Assert.Equal(100u, WireUInt32(award.Packet, 11));
        Assert.Equal(1u, WireUInt32(award.Packet, 35));
        Assert.Equal(100u, WireUInt32(award.Packet, 39));
        Assert.Equal(dummy, WireUInt64(award.Packet, 55));
        Assert.Equal(100u, storage.Experience.Read("character:1").Total);
        Assert.Equal(100u, Get<uint>(shooter.Tag!, "ExperienceEarned"));
        var score = Assert.Single(f.Recorder.Routed.Skip(mark), record => IsMatchScore(record.Packet));
        Assert.Same(shooter, score.Connection);
        Assert.Equal(100u, WireUInt32(score.Packet, 91));

        mark = f.Recorder.Routed.Count;
        Call(f.Service, "RetryPendingExperience", true);
        f.Service.ForTest(shooter).KillTarget(dummy);
        Assert.Equal(100u, storage.Experience.Read("character:1").Total);
        Assert.Equal(1, Get<MatchScore>(shooter.Tag!, "Score").Kills);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet)
            || IsMatchScore(record.Packet));
    }

    [Fact]
    public void PendingKillExperienceCommitsToOriginalAccountWithoutCreditingTheNextMatch()
    {
        using var storage = new FaultingExperienceStore();
        using var f = new Fixture();
        typeof(ZoneService).GetField("_experience", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(f.Service, storage.Experience);
        var shooter = f.Add(1, Vector3.Zero);
        f.Add(2, new(5, 0, 0));
        Set(shooter.Tag!, "AccountId", "original-account");
        Call(f.Service, "SendAccountExperience", shooter, shooter.Tag);
        ulong dummy = f.SpawnPlayerDummy(shooter);
        storage.FailWrites = true;
        f.Service.ForTest(shooter).KillTarget(dummy);
        Assert.Equal(0u, storage.Experience.Read("original-account").Total);

        // Reuse the session state for a different admission/account before storage recovers.
        // The queued kill must retain its original ownership and match generation.
        Set(shooter.Tag!, "AccountId", "replacement-account");
        Set(shooter.Tag!, "MatchAdmissionGeneration", Get<int>(shooter.Tag!, "MatchAdmissionGeneration") + 1);
        Set(shooter.Tag!, "BountyAdmission", new MatchAdmissionContext(2, MatchQueueKind.Public, MatchMode.Solo));
        Get<MatchScore>(shooter.Tag!, "Score").Reset();
        Set(shooter.Tag!, "ExperienceEarned", 0u);
        shooter.Tag!.GetType().GetProperty("ExperienceAtMatchStart")!.SetValue(shooter.Tag, null);
        storage.FailWrites = false;
        Call(f.Service, "SendAccountExperience", shooter, shooter.Tag);
        int mark = f.Recorder.Routed.Count;

        Call(f.Service, "RetryPendingExperience", true);

        Assert.Equal(100u, storage.Experience.Read("original-account").Total);
        Assert.Equal(0u, storage.Experience.Read("replacement-account").Total);
        Assert.Equal(0u, Get<uint>(shooter.Tag!, "ExperienceEarned"));
        Assert.Equal(0, Get<MatchScore>(shooter.Tag!, "Score").Kills);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet)
            || IsMatchScore(record.Packet));

        Call(f.Service, "RetryPendingExperience", true);
        Assert.Equal(100u, storage.Experience.Read("original-account").Total);
        Assert.Equal(0u, storage.Experience.Read("replacement-account").Total);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet)
            || IsMatchScore(record.Packet));
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2, true)]
    [InlineData(MatchMode.Duos, 2, false)]
    [InlineData(MatchMode.Fives, 5, true)]
    [InlineData(MatchMode.Fives, 5, false)]
    public void CreditedPracticeAndLiveKillsRefreshEverySquadMembersCounts(MatchMode mode, int size, bool practice)
    {
        using var f = new Fixture();
        var squad = AddFullSquadWithOpponents(f, mode, size);
        var shooter = squad[0];
        var victim = f.Connections[size];
        ulong target = practice ? f.SpawnPlayerDummy(shooter) : Get<ulong>(victim.Tag!, "Guid");
        int mark = f.Recorder.Routed.Count;
        if (practice)
            f.Service.ForTest(shooter).KillTarget(target);
        else
            for (uint projectile = 1; projectile <= 4; projectile++) f.Shot(shooter, victim, projectile);

        Assert.Equal(1, Get<MatchScore>(shooter.Tag!, "Score").Kills);
        var scoreUpdates = f.Recorder.Routed.Skip(mark).Where(record => IsMatchScore(record.Packet)).ToArray();
        foreach (var member in squad)
        {
            var updates = scoreUpdates.Where(record => ReferenceEquals(record.Connection, member)).ToArray();
            Assert.NotEmpty(updates);
            Assert.All(updates, record =>
            {
                Assert.Equal(ReferenceEquals(member, shooter) ? 1u : 0u, WireUInt32(record.Packet, 31));
                Assert.Equal(1u, WireUInt32(record.Packet, 55));
            });
        }
        if (practice)
            Assert.All(scoreUpdates, record => Assert.Contains(record.Connection, squad));
        else
            Assert.All(scoreUpdates.Where(record => !squad.Contains(record.Connection)),
                record => Assert.Equal(0u, WireUInt32(record.Packet, 55)));
        AssertMemberKills(f, mark, squad, 1, 1);

        mark = f.Recorder.Routed.Count;
        Call(f.Service, "PublishTeamRoster", shooter.Tag);
        foreach (var member in squad)
            Assert.Contains(f.Recorder.Routed.Skip(mark), record =>
                ReferenceEquals(record.Connection, member) && IsGroupPacket(record.Packet, 0x12));
        AssertMemberKills(f, mark, squad, 1, 1);

        mark = f.Recorder.Routed.Count;
        f.PublishMovedTeamPose(shooter, new(20, 0, 0));
        foreach (var member in squad)
            Assert.Contains(f.Recorder.Routed.Skip(mark), record => ReferenceEquals(record.Connection, member)
                && IsGroupPacket(record.Packet, 0x14) && WireUInt64(record.Packet, 4) == 1);
        Assert.All(f.Recorder.Routed.Skip(mark).Where(record => IsMemberKills(record.Packet, 1)),
            record => Assert.Equal(1u, WireUInt32(record.Packet, 8)));

        mark = f.Recorder.Routed.Count;
        if (practice)
            f.Service.ForTest(shooter).KillTarget(target);
        else
            f.Weapon(shooter, ShootingPacketBuilder.HitReport(4, target, "SPINE"), 4000);
        Assert.Equal(1, Get<MatchScore>(shooter.Tag!, "Score").Kills);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsMatchScore(record.Packet)
            && squad.Contains(record.Connection) && WireUInt32(record.Packet, 55) != 1);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsMemberKills(record.Packet, 1)
            && WireUInt32(record.Packet, 8) != 1);
        Call(f.Service, "PublishTeamRoster", shooter.Tag);
        AssertMemberKills(f, mark, squad, 1, 1);
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2)]
    [InlineData(MatchMode.Fives, 5)]
    public void LateSquadMemberReceivesExistingKillsAfterTheFullRoster(MatchMode mode, int size)
    {
        using var f = new Fixture();
        for (ulong guid = 2; guid <= (ulong)size; guid++)
        {
            var invitation = f.Parties.Invite(1, guid, 1000)!;
            Assert.NotNull(f.Parties.Respond(invitation.Token, guid, true, 1100));
        }
        var shooter = f.Add(1, Vector3.Zero, mode: mode);
        var opponent = f.Add(100, new(7, 0, 0), mode: mode);
        f.Add(101, new(8, 0, 0), mode: mode);
        f.Add(200, new(9, 0, 0), matchId: 2, mode: mode);
        Assert.False((bool)Call(f.Service, "AreTeammates", shooter.Tag, opponent.Tag)!);
        f.Service.ForTest(shooter).KillTarget(f.SpawnPlayerDummy(shooter));

        int mark = f.Recorder.Routed.Count;
        var late = f.Add(2, new(5, 0, 0), mode: mode);
        Assert.True((bool)Call(f.Service, "AreTeammates", shooter.Tag, late.Tag)!);
        var received = f.Recorder.Routed.Skip(mark).Where(record => ReferenceEquals(record.Connection, late)).ToArray();
        int rosterIndex = Array.FindIndex(received, record => IsGroupPacket(record.Packet, 0x12));
        Assert.True(rosterIndex >= 0);
        Assert.Contains(received.Skip(rosterIndex + 1), record => IsMemberKills(record.Packet, 1)
            && WireUInt32(record.Packet, 8) == 1);
        AssertMemberKills(f, mark, [shooter, late], 1, 1);
        AssertMemberKills(f, mark, [shooter, late], 2, 0);
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2)]
    [InlineData(MatchMode.Fives, 5)]
    public void FriendlyFireDeathDoesNotIncreaseTheSquadMemberKillCounter(MatchMode mode, int size)
    {
        using var f = new Fixture();
        var squad = AddFullSquadWithOpponents(f, mode, size);
        int mark = f.Recorder.Routed.Count;
        for (uint projectile = 1; projectile <= 4; projectile++) f.Shot(squad[0], squad[1], projectile);
        Assert.Equal(0u, f.Health(squad[1]));
        Assert.Equal(0, Get<MatchScore>(squad[0].Tag!, "Score").Kills);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsExperience(record.Packet));
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), record => IsMemberKills(record.Packet, 1)
            && WireUInt32(record.Packet, 8) != 0);
        Call(f.Service, "PublishTeamRoster", squad[0].Tag);
        AssertMemberKills(f, mark, squad, 1, 0);
    }

    [Fact]
    public void ValidatedLiveBulletHurtsOnlyItsTargetAndCannotBeReplayed()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero); var b = f.Add(2, new(5, 0, 0)); var c = f.Add(3, new(7, 0, 0));
        f.SeeEveryone();
        f.Shot(a, b, 1);
        Assert.Equal(7500u, f.Health(b)); Assert.Equal(10000u, f.Health(a)); Assert.Equal(10000u, f.Health(c));
        f.Weapon(a, ShootingPacketBuilder.HitReport(1, 2, "SPINE"), 1000);
        Assert.Equal(7500u, f.Health(b));
        for (uint shot = 2; shot <= 4; shot++) f.Shot(a, b, shot);
        Assert.Equal(0u, f.Health(b));
        Assert.True(Get<bool>(b.Tag!, "DeathSent"));
        Assert.Equal(1, Get<MatchScore>(a.Tag!, "Score").Kills);
        f.Weapon(a, ShootingPacketBuilder.HitReport(4, 2, "SPINE"), 4000);
        Assert.Equal(1, Get<MatchScore>(a.Tag!, "Score").Kills);
        Assert.Equal(2, (int)Call(f.Service, "AliveCount", a.Tag)!);
    }

    [Fact]
    public void FullKitPlayerDummyTakesTwoHeadshotsAndAwardsOneRetailKillWithoutEndingPractice()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        Call(f.Service, "ConsolePracticeTarget", shooter, shooter.Tag, "spawn royalty");
        var target = Get<SessionCombat>(shooter.Tag!, "Combat").Targets.All.Single();
        Assert.True(target.FullKit);
        Assert.Equal(RankedTier.Royalty, target.Badge.Tier);
        Assert.True(target.Armour.HelmetIntact);
        Assert.Contains(PracticeTargetKit.Dress(target), x => x.SlotId == 100);
        var head = Assert.Single(PracticeTargetKit.Dress(target).Where(x => x.SlotId == 15));
        Assert.Equal("SurvivorMale_Head_01.adr", head.ModelName);
        Assert.Equal(664u, head.ShaderParameterGroupId);
        Assert.Contains(PracticeTargetKit.Dress(target), x => x.SlotId == 27 && x.ModelName == "SurvivorMale_Hair_MediumMessy.adr");
        var spawn = Assert.Single(f.Recorder.Sent.Where(p => p.Length > 1 && p[1] == 0xd5));
        // Native d5: 125 fixed bytes + 2 extra transient bytes + 22 name bytes + gateway byte.
        // An extra position W silently rejected these real dummy spawns in the August client.
        Assert.Equal(150, spawn.Length);
        int mark = f.Recorder.Sent.Count;
        for (uint shot = 1; shot <= 2; shot++)
        {
            f.Weapon(shooter, ShootingPacketBuilder.Fire(0x3100000000000001, 0, 0, 0, [shot]), shot * 1000);
            f.Weapon(shooter, ShootingPacketBuilder.HitReport(shot, target.WorldGuid, "HEAD"), shot * 1000);
            Assert.Equal(shot == 1 ? 10000 : 0, target.Health);
            Assert.False(target.Armour.HelmetIntact);
            Assert.Contains(head, PracticeTargetKit.Dress(target)); // breaking helmet must retain the head
        }
        f.Weapon(shooter, ShootingPacketBuilder.HitReport(2, target.WorldGuid, "HEAD"), 2000);
        f.Service.ForTest(shooter).KillTarget(target.WorldGuid);
        var score = Get<MatchScore>(shooter.Tag!, "Score");
        Assert.Equal(1, score.Kills); Assert.Equal(1000, score.KillPoints);
        Assert.Equal(1, score.PracticeKills); Assert.True(score.PracticeSession);
        Assert.False(Get<bool>(shooter.Tag!, "VictorySent")); Assert.False(score.Settled);
        var sent = f.Recorder.Sent.Skip(mark).ToArray();
        Assert.Single(sent.Where(p => p.Length > 3 && p[1] == 0xce && p[2] == 0x0e && p[3] == 0));
        var hud = Assert.Single(sent.Where(p => p.Length > 2 && p[1] == 0x67 && p[2] == 8));
        Assert.Equal(110, hud.Length);
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hud.AsSpan(31)));
        Assert.Equal(1000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hud.AsSpan(51)));
        Assert.DoesNotContain(sent, p => p.Length > 2 && p[1] == 0x0f && p[2] == 0x48);
    }

    [Fact]
    public void CrossMatchUnseenAndOutOfRangeReportsCannotDamagePlayers()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero); var b = f.Add(2, new(5, 0, 0)); var other = f.Add(3, new(6, 0, 0), 2);
        f.Shot(a, b, 1); // no interest relationship yet
        Assert.Equal(10000u, f.Health(b));
        f.SeeEveryone();
        f.Shot(a, other, 2);
        Assert.Equal(10000u, f.Health(other));
        f.Move(b, new(10000, 0, 0)); // still known, but outside registration range
        f.Shot(a, b, 3);
        Assert.Equal(10000u, f.Health(b));
    }

    [Fact]
    public void HelmetIsConsumedOnceAndTheLastSoloSurvivorWins()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero); var b = f.Add(2, new(5, 0, 0));
        var inventory = new PlayerInventory(2, Get<LootWorld>(b.Tag!, "Loot").NextItemGuid,
            new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        inventory.TryPickUp(2168, 1, out var helmet);
        Assert.NotNull(helmet);
        Set(b.Tag!, "Inventory", inventory);
        f.SeeEveryone();
        f.Shot(a, b, 1, "HEAD");
        Assert.Equal(10000u, f.Health(b));
        Assert.DoesNotContain(helmet.Guid, inventory.Items.Keys);
        f.Shot(a, b, 2, "HEAD");
        Assert.Equal(0u, f.Health(b));
        Assert.True(Get<bool>(a.Tag!, "VictorySent"));
    }

    [Fact]
    public void All150LivePlayersCanHitTheirChosenOpponent()
    {
        using var f = new Fixture();
        for (ulong n = 1; n <= 150; n++) f.Add(n, new(n % 10, 0, n / 10));
        f.SeeEveryone();
        for (int n = 0; n < 150; n++) f.Shot(f.Connections[n], f.Connections[(n + 1) % 150], 1);
        foreach (var player in f.Connections) Assert.Equal(7500u, f.Health(player));
        Assert.Equal(150, (int)Call(f.Service, "AliveCount", f.Connections[0].Tag)!);
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2)]
    [InlineData(MatchMode.Fives, 5)]
    public void TeamQueuesFillStableSquadsAndIsolateMatches(MatchMode mode, int size)
    {
        using var f = new Fixture();
        for (ulong n = 1; n <= (ulong)(size * 2 + 1); n++) f.Add(n, new(n, 0, 0), mode: mode);
        ulong first = (ulong)Call(f.Service, "TeamGuid", f.Connections[0].Tag)!;
        ulong second = (ulong)Call(f.Service, "TeamGuid", f.Connections[size].Tag)!;
        Assert.NotEqual(first, second);
        Assert.Equal(size, ((Array)Call(f.Service, "TeamMembers", f.Connections[0].Tag)!).Length);
        for (int n = 0; n < size; n++)
            Assert.Equal(first, (ulong)Call(f.Service, "TeamGuid", f.Connections[n].Tag)!);
        var other = f.Add(150, new(150, 0, 0), matchId: 2, mode: mode);
        Assert.False((bool)Call(f.Service, "AreTeammates", f.Connections[0].Tag, other.Tag)!);
        Call(f.Service, "LeaveSharedLoot", f.Connections[1].Tag);
        Assert.Equal(first, (ulong)Call(f.Service, "TeamGuid", f.Connections[0].Tag)!);
        Assert.Equal(second, (ulong)Call(f.Service, "TeamGuid", f.Connections[size].Tag)!);
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2)]
    [InlineData(MatchMode.Fives, 5)]
    public void TeamPlacementWaitsForSquadEliminationAndAllWinnersReceiveVictory(MatchMode mode, int size)
    {
        using var f = new Fixture();
        for (ulong n = 1; n <= (ulong)(size * 2); n++) f.Add(n, new(n, 0, 0), mode: mode);
        var killer = f.Connections[size];
        int mark = f.Recorder.Sent.Count;
        for (int n = 0; n < size; n++)
        {
            f.Service.ForTest(f.Connections[n]).Damage(10_000, DamageCause.Bullet,
                Get<ulong>(killer.Tag!, "Guid"), "Opponent", 10000);
            if (n == 0)
            {
                Assert.False(Get<MatchScore>(f.Connections[0].Tag!, "Score").Settled);
                Assert.False(Get<bool>(killer.Tag!, "VictorySent"));
                f.Service.ForTest(f.Connections[0]).ExpireEndedHold();
                Assert.Equal("Ended", f.Service.ForTest(f.Connections[0]).Step);
            }
        }
        for (int n = 0; n < size * 2; n++)
        {
            var score = Get<MatchScore>(f.Connections[n].Tag!, "Score");
            Assert.True(score.Settled);
            Assert.Equal(n < size ? 2u : 1u, score.FinalPlacement);
            Assert.Equal(n >= size, Get<bool>(f.Connections[n].Tag!, "VictorySent"));
            Assert.Equal(1, f.Profile(f.Connections[n], mode).Matches);
            Assert.Equal(0, f.Profile(f.Connections[n], MatchMode.Solo).Matches);
        }
        var sent = f.Recorder.Sent.Skip(mark).ToArray();
        Assert.DoesNotContain(sent, p => p.Length > 3 && p[1] == 0xce && p[2] == 9 && p[3] == 0);
        Assert.Contains(sent, p => p.Length == 12 && p[1] == 0xce && p[2] == 10 && p[3] == 0
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == size
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8)) == 1);
    }

    [Fact]
    public void DeadTeammateReceivesWinningPlacementAndFriendlyFireDoesNotCreditKills()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero, mode: MatchMode.Duos);
        var b = f.Add(2, new(5, 0, 0), mode: MatchMode.Duos);
        var c = f.Add(3, new(6, 0, 0), mode: MatchMode.Duos);
        var d = f.Add(4, new(7, 0, 0), mode: MatchMode.Duos);
        f.SeeEveryone();
        f.Shot(a, b, 1);
        Assert.Equal(7500u, f.Health(b));
        f.Service.ForTest(b).Damage(10_000, DamageCause.Bullet, 1, "Player1", 10000);
        Assert.Equal(0, Get<MatchScore>(a.Tag!, "Score").Kills);
        Assert.False(Get<MatchScore>(b.Tag!, "Score").Settled);
        f.Service.ForTest(c).Damage(10_000, DamageCause.Bullet, 1, "Player1", 10000);
        Assert.False(Get<bool>(a.Tag!, "VictorySent"));
        f.Service.ForTest(d).Damage(10_000, DamageCause.Bullet, 1, "Player1", 10000);
        Assert.Equal(2, Get<MatchScore>(a.Tag!, "Score").Kills);
        Assert.True(Get<bool>(a.Tag!, "VictorySent"));
        Assert.True(Get<bool>(b.Tag!, "VictorySent"));
        Assert.Equal(1u, Get<MatchScore>(b.Tag!, "Score").FinalPlacement);
    }

    [Fact]
    public void LastOpponentDisconnectAwardsTheSurvivingSquadAndSingleSquadDoesNotAutoWin()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero, mode: MatchMode.Duos);
        var b = f.Add(2, new(5, 0, 0), mode: MatchMode.Duos);
        f.Service.ForTest(b).Damage(10_000, DamageCause.ToxicGas);
        Assert.False(Get<bool>(a.Tag!, "VictorySent"));
        var c = f.Add(3, new(6, 0, 0), mode: MatchMode.Duos);
        c.Disconnect();
        f.Service.OnDisconnected(c, DisconnectCause.ServerRequested);
        Assert.True(Get<bool>(a.Tag!, "VictorySent"));
        Assert.True(Get<bool>(b.Tag!, "VictorySent"));
    }

    [Fact]
    public void HostedTeamResultsDoNotChangePublicRankings()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero, mode: MatchMode.Duos, queue: MatchQueueKind.Hosted);
        var b = f.Add(2, new(5, 0, 0), mode: MatchMode.Duos, queue: MatchQueueKind.Hosted);
        var c = f.Add(3, new(6, 0, 0), mode: MatchMode.Duos, queue: MatchQueueKind.Hosted);
        f.Service.ForTest(c).Damage(10_000, DamageCause.Bullet, 1, "Player1", 10000);
        Assert.True(Get<bool>(a.Tag!, "VictorySent"));
        Assert.True(Get<bool>(b.Tag!, "VictorySent"));
        Assert.Equal(0, f.Profile(a, MatchMode.Duos).Matches);
        Assert.Equal(0, f.Profile(c, MatchMode.Duos).Matches);
    }

    [Fact]
    public void NativeRosterAndPositionUpdatesReachOnlyOwnSquadAndClearOnDeparture()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero, mode: MatchMode.Duos);
        var b = f.Add(2, new(5, 0, 0), mode: MatchMode.Duos);
        var c = f.Add(3, new(6, 0, 0), mode: MatchMode.Duos);
        int mark = f.Recorder.Routed.Count;
        Call(f.Service, "SendTeamRoster", a, a.Tag);
        var roster = Assert.Single(f.Recorder.Routed.Skip(mark), record => IsGroupPacket(record.Packet, 0x12));
        Assert.Same(a, roster.Connection);
        Assert.Equal(new byte[] { 0x13, 0x12, 2 }, roster.Packet.AsSpan(1, 3).ToArray());
        Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(roster.Packet.AsSpan(26)));
        string text = System.Text.Encoding.UTF8.GetString(roster.Packet);
        Assert.Contains("Player1", text);
        Assert.Contains("Player2", text);
        Assert.DoesNotContain("Player3", text);

        mark = f.Recorder.Routed.Count;
        Call(f.Service, "PublishTeamPose", b.Tag);
        var updates = f.Recorder.Routed.Skip(mark).ToArray();
        Assert.Equal(4, updates.Length);
        Assert.DoesNotContain(updates, update => ReferenceEquals(c, update.Connection));
        foreach (var recipient in new[] { a, b })
        {
            var own = updates.Where(update => ReferenceEquals(recipient, update.Connection)).ToArray();
            Assert.Equal(2, own.Length);
            Assert.Equal(new byte[] { 0x13, 0x14, 2 }, own[0].Packet.AsSpan(1, 3).ToArray());
            // A rebuilt position row clears UiColor; the following native status restores it.
            Assert.Equal(23, own[1].Packet.Length);
            Assert.Equal(new byte[] { 0x13, 0x24, 2 }, own[1].Packet.AsSpan(1, 3).ToArray());
            Assert.All(own, update => Assert.Equal(Get<ulong>(b.Tag!, "Guid"),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(update.Packet.AsSpan(4))));
        }
        Call(f.Service, "PublishTeamPose", b.Tag);
        Assert.Equal(mark + 4, f.Recorder.Routed.Count); // throttle the complete pose/status pair

        mark = f.Recorder.Routed.Count;
        Call(f.Service, "LeaveSharedLoot", b.Tag);
        Assert.Contains(f.Recorder.Routed.Skip(mark), record => ReferenceEquals(record.Connection, b)
            && record.Packet.Length == 12 && record.Packet[1] == 0x13 && record.Packet[2] == 0x16);
        Assert.Contains(f.Recorder.Routed.Skip(mark), record => ReferenceEquals(record.Connection, a)
            && record.Packet.Length > 29 && record.Packet[1] == 0x13 && record.Packet[2] == 0x12
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record.Packet.AsSpan(26)) == 1);
    }

    [Theory]
    [InlineData(MatchMode.Duos, 2)]
    [InlineData(MatchMode.Fives, 5)]
    public void APartyReservesItsWholeSquadBeforeAllTransferAcknowledgementsArrive(MatchMode mode, int size)
    {
        using var f = new Fixture();
        for (ulong n = 2; n <= (ulong)size; n++)
        {
            var invite = f.Parties.Invite(1, n, 1000)!;
            Assert.NotNull(f.Parties.Respond(invite.Token, n, true, 1100));
        }
        var first = f.Add(1, Vector3.Zero, mode: mode);
        var stranger = f.Add(100, new(5, 0, 0), mode: mode);
        ulong partyTeam = (ulong)Call(f.Service, "TeamGuid", first.Tag)!;
        Assert.NotEqual(partyTeam, (ulong)Call(f.Service, "TeamGuid", stranger.Tag)!);
        for (ulong n = 2; n <= (ulong)size; n++)
        {
            var member = f.Add(n, new(n, 0, 0), mode: mode);
            Assert.Equal(partyTeam, (ulong)Call(f.Service, "TeamGuid", member.Tag)!);
        }
        Assert.Equal(size, ((Array)Call(f.Service, "TeamMembers", first.Tag)!).Length);
    }
}
