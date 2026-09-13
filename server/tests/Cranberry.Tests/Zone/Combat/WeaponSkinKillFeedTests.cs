using System.Net;
using System.Numerics;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Tests.Zone.World;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed class WeaponSkinKillFeedTests
{
    private const uint Ar15 = 10;
    private const uint WildstyleAr15 = 2601; // Reward item; the account unlock is 2602.
    private const uint RoyaltyAr15 = 4072;

    [Theory]
    [InlineData(false, false, Ar15)]
    [InlineData(false, true, Ar15)]
    [InlineData(true, false, Ar15)]
    [InlineData(true, true, Ar15)]
    [InlineData(false, false, WildstyleAr15)]
    [InlineData(false, true, WildstyleAr15)]
    [InlineData(true, false, WildstyleAr15)]
    [InlineData(true, true, WildstyleAr15)]
    public void GatewayKillFeedUsesTheHeldWeaponDisplayItemForPlayersAndFullKitDummies(
        bool practice, bool ammoFromBag, uint displayItem)
    {
        using var f = new Fixture(ammoFromBag);
        f.RifleDisplayId = displayItem;
        var target = f.PrepareTarget(practice);
        Assert.Equal(Ar15, f.Rifle.DefinitionId);
        Assert.Equal(displayItem, f.Rifle.DisplayDefinitionId);
        int mark = f.Recorder.Routed.Count;

        // The live route must obtain the display item even when no PlayerAmmoContext exists.
        f.Gateway(ShootingPacketBuilder.Fire(f.Rifle.Guid, 0, 0, 0, [2]));
        f.Gateway(ShootingPacketBuilder.HitReport(2, target, "HEAD"));

        Assert.Equal(0u, f.TargetHealth(practice));
        Assert.Equal(2, Get<SessionCombat>(f.Shooter.Tag!, "Combat").Shooter.ShotsFired);
        f.AssertFeed(mark, target, displayItem, practice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedShotKeepsItsSkinAfterTheWardrobeAndHeldWeaponChange(bool practice)
    {
        using var f = new Fixture();
        f.RifleDisplayId = WildstyleAr15;
        ulong target = f.PrepareTarget(practice);
        int mark = f.Recorder.Routed.Count;
        f.Arm(ShootingPacketBuilder.Fire(f.Rifle.Guid, 0, 0, 0, [2]), 2000);

        f.RifleDisplayId = RoyaltyAr15;
        Assert.Equal(RoyaltyAr15, f.Rifle.DisplayDefinitionId);
        f.Inventory.TryPickUp(1374, 1, out var shotgun);
        Assert.NotNull(shotgun);
        Assert.True(f.Inventory.TrySelectLoadoutSlot(shotgun.LoadoutSlotId, out _));
        Assert.Equal(shotgun.Guid, f.Inventory.WieldedItemGuid);
        f.Arm(ShootingPacketBuilder.HitReport(2, target, "HEAD"), 2000);

        // A shotgun head pellet would not kill this target. Both damage and appearance
        // must still come from the AR-15 shot accepted before the inventory changed.
        Assert.Equal(0u, f.TargetHealth(practice));
        f.AssertFeed(mark, target, WildstyleAr15, practice);
    }

    [Fact]
    public void LegacyArmCallerWithoutDisplayIdentityFallsBackToTheBaseWeapon()
    {
        using var f = new Fixture();
        f.RifleDisplayId = WildstyleAr15;
        int mark = f.Recorder.Routed.Count;
        var state = f.Shooter.Tag!;
        WeaponFireArm.Handle(Get<SessionCombat>(state, "Combat"),
            ShootingPacketBuilder.Fire(f.Rifle.Guid, 0, 0, 0, [1]), CombatOptions.Default,
            Ar15, f.Rifle.Guid, Vector3.Zero, 1000,
            Get<List<WeaponArmResult>>(state, "WeaponArmResults"), ammo: null);
        Call(f.Service, "DrainCombatArm", f.Shooter, state);
        f.Arm(ShootingPacketBuilder.HitReport(1, 2, "HEAD"), 1000);

        Assert.Equal(0u, f.TargetHealth(practice: false));
        f.AssertFeed(mark, 2, Ar15, practice: false);
    }

    private sealed class Fixture : IDisposable
    {
        public Recorder Recorder { get; } = new();
        public ZoneService Service { get; }
        public SoeConnection Shooter { get; }
        public SoeConnection Victim { get; }
        public SoeConnection Observer { get; }
        public SoeConnection OtherMatch { get; }
        public PlayerInventory Inventory { get; }
        public InventoryItemInstance Rifle { get; }
        public uint RifleDisplayId { get; set; } = Ar15;
        private readonly List<SoeConnection> _connections = [];
        private PracticeTarget? _dummy;

        public Fixture(bool ammoFromBag = true)
        {
            Service = new(new SilentLog(), Recorder, new GatewayTicketRegistry(),
                new ZoneOptions { Combat = CombatOptions.Default with
                {
                    Ammo = new AmmoOptions { AmmoFromBag = ammoFromBag },
                } });
            Shooter = Add(1, Vector3.Zero);
            Victim = Add(2, new(5, 0, 0));
            Observer = Add(3, new(7, 0, 0));
            OtherMatch = Add(4, new(8, 0, 0), matchId: 2);
            var enters = new List<PeerEnter>();
            var leaves = new List<PeerLeave>();
            for (int i = 0; i < 20; i++)
                foreach (var c in _connections)
                    Service.PeerRegistry.Sweep(Get<PeerSession>(c.Tag!, "Peer"), enters, leaves);

            Inventory = new(1, Get<LootWorld>(Shooter.Tag!, "Loot").NextItemGuid,
                new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 10000 })
            { SkinDefinition = id => id == Ar15 ? RifleDisplayId : id };
            Inventory.Bootstrap();
            Inventory.TryPickUp(Ar15, 1, out var rifle);
            Rifle = Assert.IsType<InventoryItemInstance>(rifle);
            Assert.True(Inventory.TrySelectLoadoutSlot(Rifle.LoadoutSlotId, out _));
            Set(Shooter.Tag!, "Inventory", Inventory);
            Get<SessionCombat>(Shooter.Tag!, "Combat").Shooter.DeclareWeapon(Rifle.Guid, Ar15, 30);
        }

        private SoeConnection Add(ulong guid, Vector3 position, ulong matchId = 1)
        {
            var request = new SessionRequest(3, (uint)guid, 512, ZoneService.ProtocolName);
            var c = new SoeConnection(new(IPAddress.Loopback, 12000 + (int)guid), in request,
                new(), SessionDecision.Clear, Service, new SilentLog(), (_, _) => { }, 0);
            Service.OnConnected(c);
            object state = c.Tag!;
            Set(state, "Authenticated", true);
            Set(state, "Guid", guid);
            Set(state, "CharacterName", $"Player{guid}");
            Set(state, "Visuals", CharacterVisuals.FromSelection(1, 1, 1, 664, 270));
            Set(state, "Wardrobe", new AugustWardrobeState());
            Set(state, "BountyAdmission", new MatchAdmissionContext(matchId, MatchQueueKind.Public, MatchMode.Solo));
            Service.ForTest(c).EnterMatch();
            Call(Service, "RegisterPeerSession", c, state);
            Call(Service, "JoinSharedLoot", c, state);
            Call(Service, "NotePeerInMatch", state, true);
            byte[] movement = MovementRecord.Position(position);
            Get<SessionMovementState>(state, "Movement").ApplyPlayer(ClientMovementUpdate.Parse(movement));
            var peer = Get<PeerSession>(state, "Peer");
            peer.Position = position;
            peer.SetPose(movement);
            _connections.Add(c);
            return c;
        }

        public ulong PrepareTarget(bool practice)
        {
            ulong target = 2;
            if (practice)
            {
                Call(Service, "ConsolePracticeTarget", Shooter, Shooter.Tag, "spawn royalty");
                _dummy = Get<SessionCombat>(Shooter.Tag!, "Combat").Targets.All.Single();
                Assert.True(_dummy.FullKit);
                Assert.True(_dummy.Armour.HelmetIntact);
                target = _dummy.WorldGuid;
            }
            // Fixed-time preparation leaves the next live shot beyond the rifle refire gate.
            // It also pins unchanged base damage: 2500 to a bare body, or a blocked helmet hit.
            Arm(ShootingPacketBuilder.Fire(Rifle.Guid, 0, 0, 0, [1]), 1000);
            Arm(ShootingPacketBuilder.HitReport(1, target, practice ? "HEAD" : "SPINE"), 1000);
            Assert.Equal(practice ? 10000u : 7500u, TargetHealth(practice));
            if (practice) Assert.False(_dummy!.Armour.HelmetIntact);
            return target;
        }

        public uint TargetHealth(bool practice) => practice
            ? checked((uint)_dummy!.Health) : Service.ForTest(Victim).Hitpoints;

        public void Gateway(byte[] packet) => Service.OnMessage(Shooter,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 3).ToByte(), .. packet]);

        public void Arm(byte[] packet, long now)
        {
            var state = Shooter.Tag!;
            var held = Inventory.EquipmentSlots[BodySlots.RightHand];
            WeaponFireArm.Handle(Get<SessionCombat>(state, "Combat"), packet, CombatOptions.Default,
                held.DefinitionId, held.Guid, Vector3.Zero, now,
                Get<List<WeaponArmResult>>(state, "WeaponArmResults"), ammo: null,
                heldWeaponDisplayDefinitionId: held.DisplayDefinitionId);
            Call(Service, "DrainCombatArm", Shooter, state);
        }

        public void AssertFeed(int mark, ulong target, uint expectedItem, bool practice)
        {
            var feeds = Recorder.Routed.Skip(mark).Where(record => IsFeed(record.Packet)).ToArray();
            SoeConnection[] recipients = practice ? [Shooter] : [Shooter, Victim, Observer];
            Assert.Equal(recipients.Length, feeds.Length);
            foreach (var recipient in recipients)
            {
                var feed = Assert.Single(feeds, record => ReferenceEquals(record.Connection, recipient));
                Assert.Equal((target, 1UL, expectedItem, true), ReadFeed(feed.Packet));
            }
            Assert.DoesNotContain(feeds, record => ReferenceEquals(record.Connection, OtherMatch));
        }

        public void Dispose()
        {
            foreach (var c in _connections)
            {
                c.Disconnect();
                Service.OnDisconnected(c, DisconnectCause.ServerRequested);
            }
        }
    }

    private static bool IsFeed(byte[] packet) =>
        packet.Length > 3 && packet[1] == 0xce && packet[2] == 0x0e && packet[3] == 0;

    private static (ulong Victim, ulong Killer, uint Item, bool Headshot) ReadFeed(byte[] packet)
    {
        var reader = new PacketReader(packet.AsSpan(4));
        Assert.Equal((byte)8, reader.ReadByte());
        Assert.Equal(2u, reader.ReadUInt32());
        ulong victim = ReadFeedPlayer(ref reader);
        ulong killer = ReadFeedPlayer(ref reader);
        uint item = reader.ReadUInt32();
        reader.Skip(16); // Damage cause, two reserved integers and one reserved float.
        return (victim, killer, item, reader.ReadBool());
    }

    private static ulong ReadFeedPlayer(ref PacketReader reader)
    {
        ulong guid = reader.ReadUInt64();
        reader.Skip(12);
        for (int i = 0; i < 4; i++) reader.ReadString();
        reader.Skip(25); // Reserved identity data, compact integer, and rank badge.
        return guid;
    }

    private static object? Call(ZoneService service, string name, params object?[] args) =>
        typeof(ZoneService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args);
    private static T Get<T>(object state, string name) => (T)state.GetType().GetProperty(name)!.GetValue(state)!;
    private static void Set(object state, string name, object value) => state.GetType().GetProperty(name)!.SetValue(state, value);

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }

    private sealed class Recorder : IPacketRecorder
    {
        public List<(SoeConnection Connection, byte[] Packet)> Routed { get; } = [];
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c") Routed.Add((connection, bytes.ToArray()));
        }
    }
}
