using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

// Two live session sinks and the real combat drain/interest/dress paths. These assertions
// establish ordered observer packets and authoritative outcomes, never native sound.
public sealed partial class LivePlayerCombatTests
{
    private static InventoryItemInstance CombatGun(Fixture f, SoeConnection c, uint item, int rounds)
    {
        object state = c.Tag!;
        var inventory = Get<PlayerInventory?>(state, "Inventory");
        if (inventory is null)
        {
            inventory = new PlayerInventory(Get<ulong>(state, "Guid"), Get<LootWorld>(state, "Loot").NextItemGuid);
            inventory.Bootstrap();
            Set(state, "Inventory", inventory);
        }
        var gun = inventory.CreateInstance(item, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        Get<SessionCombat>(state, "Combat").Shooter.DeclareWeapon(gun.Guid, item, rounds);
        Call(f.Service, "SendCharacterAppearance", c, state, "combat regression draw");
        // The draw deadline remains independently tested by WeaponDrawState tests. Advance
        // this fixture's deterministic combat clock beyond it instead of sleeping.
        return gun;
    }

    private static void CombatInterest(Fixture f)
    {
        foreach (var c in f.Connections)
        {
            var viewer = Get<PeerSession>(c.Tag!, "Peer");
            var enters = new List<PeerEnter>();
            f.Service.PeerRegistry.Sweep(viewer, enters, []);
            foreach (var enter in enters) Call(f.Service, "SendPeerEnterBurst", c, viewer, enter);
        }
    }

    private static void CombatPacket(Fixture f, SoeConnection c, InventoryItemInstance gun,
        byte[] packet, long now)
    {
        var state = c.Tag!;
        var results = Get<List<WeaponArmResult>>(state, "WeaponArmResults");
        WeaponFireArm.Handle(Get<SessionCombat>(state, "Combat"), packet, CombatOptions.Default,
            gun.DefinitionId, gun.Guid, Vector3.Zero, now, results,
            new PlayerAmmoContext(Get<PlayerInventory>(state, "Inventory"), Get<ulong>(state, "Guid"), AmmoOptions.Default));
        Call(f.Service, "DrainCombatArm", c, state);
    }

    private static byte[] CombatHint(ulong guid, uint[] projectiles)
    {
        using var w = new PacketWriter();
        w.WriteRaw(ShootingPacketBuilder.Header(0x20));
        w.WriteUInt64(guid); w.WriteByte(0);
        w.WriteSingle(0); w.WriteSingle(0); w.WriteSingle(0);
        w.WriteUInt32((uint)projectiles.Length);
        foreach (uint id in projectiles)
        {
            w.WriteUInt32(id); w.WriteSingle(1); w.WriteSingle(0); w.WriteSingle(0); w.WriteUInt32(0);
        }
        return w.Written.ToArray();
    }

    private static byte[] CombatGuidPacket(byte sub, ulong guid, uint time = 0) =>
        [.. ShootingPacketBuilder.Header(sub, time), .. BitConverter.GetBytes(guid)];

    private static byte[][] CombatRemote(Fixture f, SoeConnection viewer, int mark = 0) =>
        [.. f.Recorder.Routed.Skip(mark).Where(r => ReferenceEquals(r.Connection, viewer)
            && r.Packet.Length > 8 && r.Packet[1] == 0x82 && r.Packet[6] == 0x15)
            .Select(r => r.Packet[1..])];

    private static uint CombatOwner(SoeConnection subject, SoeConnection viewer)
    {
        Assert.True(Get<PeerSession>(viewer.Tag!, "Peer").View.Transients.TryGet(
            Get<PeerSession>(subject.Tag!, "Peer").Key, out uint id));
        return id;
    }

    [Theory]
    [InlineData(2425u, 1)]
    [InlineData(1374u, 12)]
    public void CombatReplication_HitBeforeAimStillPlaysOnceAndFireReplayCannotPayAgain(uint item, int pellets)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var gun = CombatGun(f, shooter, item, 5);
        CombatInterest(f);
        uint owner = CombatOwner(shooter, viewer);
        long now = Environment.TickCount64 + 10_000;
        uint[] ids = [.. Enumerable.Range(1, pellets).Select(n => (uint)n)];
        byte[] fire = ShootingPacketBuilder.Fire(gun.Guid, 0, 0, 0, ids);
        int mark = f.Recorder.Routed.Count;
        CombatPacket(f, shooter, gun, fire, now);
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.HitReport(1, 2, "SPINE"), now);
        uint health = f.Health(viewer);
        Assert.True(health < 10000);
        CombatPacket(f, shooter, gun, CombatHint(gun.Guid, ids), now + 1);
        CombatPacket(f, shooter, gun, CombatHint(gun.Guid, ids), now + 2);
        var start = RemoteWeaponPackets.FireState(owner, gun.Guid, true, new Vector4(100, 0, 0, 1));
        Assert.Single(CombatRemote(f, viewer, mark), p => p.SequenceEqual(start));
        CombatPacket(f, shooter, gun, fire, now + 5000); // past the refire gate and hit consumption
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.HitReport(1, 2, "SPINE"), now + 5000);
        Assert.Equal(health, f.Health(viewer));
        Assert.Equal(4, Get<SessionCombat>(shooter.Tag!, "Combat").Shooter.AmmoOf(gun.Guid));
        Assert.Single(CombatRemote(f, viewer, mark), p => p.SequenceEqual(RemoteWeaponPackets.ProjectileLaunch(owner, gun.Guid, 0)));
        Assert.Empty(CombatRemote(f, shooter, mark));
    }

    [Theory]
    [InlineData(2425u)]
    [InlineData(1374u)]
    public void CombatReplication_ModeChangeReachesCurrentAndLateObserver(uint item)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var gun = CombatGun(f, shooter, item, 5);
        CombatInterest(f);
        int mark = f.Recorder.Routed.Count;
        long now = Environment.TickCount64 + 10_000;
        var mode = ShootingPacketBuilder.SwitchFireModeRequest(gun.Guid, 0, 1);
        CombatPacket(f, shooter, gun, mode, now);
        CombatPacket(f, shooter, gun, mode, now + 1);
        Assert.Single(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.SwitchFireMode(CombatOwner(shooter, viewer), gun.Guid, 0, 1)));
        Assert.Empty(CombatRemote(f, shooter, mark));
        var late = f.Add(3, new(6, 0, 0));
        CombatInterest(f);
        Assert.Contains(CombatRemote(f, late), p => p.SequenceEqual(
            RemoteWeaponPackets.SwitchFireMode(CombatOwner(shooter, late), gun.Guid, 0, 1)));
    }

    [Theory]
    [InlineData(2425u, 1429u)]
    [InlineData(1374u, 1511u)]
    public void CombatReplication_ReloadAndInterruptAreObserverEventsWithoutEarlyAmmo(uint item, uint ammunition)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { }; // keep timer completions on the fixture's explicit clock
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var gun = CombatGun(f, shooter, item, 0);
        var bag = Get<PlayerInventory>(shooter.Tag!, "Inventory");
        Assert.True(bag.TryStow(bag.CreateInstance(ammunition, 4)));
        CombatInterest(f);
        int mark = f.Recorder.Routed.Count;
        long now = Environment.TickCount64 + 10_000;
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid), now);
        var combat = Get<SessionCombat>(shooter.Tag!, "Combat");
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid), now + 1);
        Assert.Same(pending, combat.Reload);
        Assert.Equal(0, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Single(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.Reload(CombatOwner(shooter, viewer), gun.Guid)));
        CombatPacket(f, shooter, gun, CombatGuidPacket(0x09, gun.Guid), now + 2);
        CombatPacket(f, shooter, gun, CombatGuidPacket(0x09, gun.Guid), now + 3);
        Assert.Null(combat.Reload);
        Assert.Single(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.ReloadInterrupt(CombatOwner(shooter, viewer), gun.Guid)));
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, bag));
        Assert.Equal(4, new PlayerAmmoContext(bag, 1, AmmoOptions.Default).Count(ammunition));
    }

    [Fact]
    public void CombatReplication_NativeChamberNotificationIsForwardedOnceWithTheHeldIdentity()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var gun = CombatGun(f, shooter, 1374, 5);
        CombatInterest(f);
        int mark = f.Recorder.Routed.Count;
        long now = Environment.TickCount64 + 10_000;
        byte[] chamber = CombatGuidPacket(0x16, gun.Guid, 12345);
        CombatPacket(f, shooter, gun, chamber, now);
        CombatPacket(f, shooter, gun, chamber, now + 1);
        CombatPacket(f, shooter, gun, CombatGuidPacket(0x16, gun.Guid + 100, 12346), now + 2);
        Assert.Single(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.Chamber(CombatOwner(shooter, viewer), gun.Guid)));
        Assert.Equal(5, Get<SessionCombat>(shooter.Tag!, "Combat").Shooter.AmmoOf(gun.Guid));
    }

    private static void CombatCompleteReload(Fixture f, SoeConnection shooter, PendingWeaponReload pending)
    {
        var combat = Get<SessionCombat>(shooter.Tag!, "Combat");
        var result = WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs,
            pending.WeaponGuid, Get<PlayerInventory>(shooter.Tag!, "Inventory"));
        Assert.NotNull(result);
        var results = Get<List<WeaponArmResult>>(shooter.Tag!, "WeaponArmResults");
        results.Clear(); results.Add(result.Value);
        Call(f.Service, "DrainCombatArm", shooter, shooter.Tag);
    }

    [Theory]
    [InlineData(2425u, 1429u, 1)]
    [InlineData(1374u, 1511u, 4)]
    public void CombatReplication_ReloadCompletesFromTheBagOnceAndPumpEndsItsLoop(uint item, uint ammunition, int steps)
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var gun = CombatGun(f, shooter, item, 0);
        var bag = Get<PlayerInventory>(shooter.Tag!, "Inventory");
        Assert.True(bag.TryStow(bag.CreateInstance(ammunition, 4)));
        CombatInterest(f);
        int mark = f.Recorder.Routed.Count;
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid), Environment.TickCount64 + 10_000);
        var combat = Get<SessionCombat>(shooter.Tag!, "Combat");
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        for (int n = 0; n < steps; n++) CombatCompleteReload(f, shooter, pending);
        Assert.Equal(4, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(0, new PlayerAmmoContext(bag, 1, AmmoOptions.Default).Count(ammunition));
        Assert.Null(combat.Reload);
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs + 10_000, gun.Guid, bag));
        byte[] end = RemoteWeaponPackets.ReloadLoopEnd(CombatOwner(shooter, viewer), gun.Guid, false);
        Assert.Equal(steps == 4 ? 1 : 0, CombatRemote(f, viewer, mark).Count(p => p.SequenceEqual(end)));
    }

    [Fact]
    public void CombatReplication_FiringALoadedShellInterruptsReloadBeforeLaunching()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var gun = CombatGun(f, shooter, 1374, 1);
        var bag = Get<PlayerInventory>(shooter.Tag!, "Inventory");
        Assert.True(bag.TryStow(bag.CreateInstance(1511, 4)));
        CombatInterest(f);
        long now = Environment.TickCount64 + 10_000;
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid), now);
        var combat = Get<SessionCombat>(shooter.Tag!, "Combat");
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        int mark = f.Recorder.Routed.Count;
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.Fire(gun.Guid, 0, 0, 0, [1]), now + 1);
        var packets = CombatRemote(f, viewer, mark);
        Assert.Equal(RemoteWeaponPackets.ReloadInterrupt(CombatOwner(shooter, viewer), gun.Guid), packets[0]);
        Assert.Equal(RemoteWeaponPackets.ProjectileLaunch(CombatOwner(shooter, viewer), gun.Guid, 0), packets[1]);
        Assert.Null(combat.Reload);
        Assert.Equal(0, combat.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(4, new PlayerAmmoContext(bag, 1, AmmoOptions.Default).Count(1511));
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, bag));
    }

    [Fact]
    public void CombatReplication_SwitchInterruptsTheOldIdentityBeforeResetAndRestoresStance()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var old = CombatGun(f, shooter, 1374, 1);
        var bag = Get<PlayerInventory>(shooter.Tag!, "Inventory");
        Assert.True(bag.TryStow(bag.CreateInstance(1511, 4)));
        CombatInterest(f);
        long now = Environment.TickCount64 + 10_000;
        CombatPacket(f, shooter, old, ShootingPacketBuilder.ReloadRequest(old.Guid), now);
        Get<PeerSession>(shooter.Tag!, "Peer").WeaponStance = 3;
        int mark = f.Recorder.Routed.Count;
        var current = CombatGun(f, shooter, 2425, 5);
        byte[][] packets = CombatRemote(f, viewer, mark);
        uint owner = CombatOwner(shooter, viewer);
        Assert.Equal(RemoteWeaponPackets.ReloadInterrupt(owner, old.Guid), packets[0]);
        Assert.Equal(RemoteWeaponPackets.Reset(owner, []), packets[1]);
        Assert.Equal((byte)RemoteWeaponPackets.RemoteSub.AddWeapon, packets[2][6]);
        Assert.Equal(RemoteWeaponPackets.SwitchFireMode(owner, current.Guid, 0, 0), packets[3]);
        Assert.Contains(f.Recorder.Routed.Skip(mark), r => ReferenceEquals(r.Connection, viewer)
            && r.Packet[1..].SequenceEqual(new WeaponStance(1, 3).ToArray()));
        mark = f.Recorder.Routed.Count;
        CombatPacket(f, shooter, current, ShootingPacketBuilder.FireStateUpdate(old.Guid, 0), now + 1);
        CombatPacket(f, shooter, current, CombatGuidPacket(0x16, old.Guid, 2), now + 1);
        CombatPacket(f, shooter, current, CombatHint(old.Guid, [1]), now + 1);
        Assert.Empty(CombatRemote(f, viewer, mark));
        Assert.Null(Get<SessionCombat>(shooter.Tag!, "Combat").Reload);
    }

    [Fact]
    public void CombatReplication_ReleaseBeforeAimDoesNotLeaveTheRemoteWeaponFiring()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var gun = CombatGun(f, shooter, 2425, 5);
        CombatInterest(f);
        long now = Environment.TickCount64 + 10_000;
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.Fire(gun.Guid, 0, 0, 0, [1]), now);
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.FireStateUpdate(gun.Guid, 0), now + 1);
        int mark = f.Recorder.Routed.Count;
        CombatPacket(f, shooter, gun, CombatHint(gun.Guid, [1]), now + 2);
        uint owner = CombatOwner(shooter, viewer);
        Assert.Equal(new[] {
            RemoteWeaponPackets.FireState(owner, gun.Guid, true, new Vector4(100, 0, 0, 1)),
            RemoteWeaponPackets.FireState(owner, gun.Guid, false, new Vector4(100, 0, 0, 1)),
        }, CombatRemote(f, viewer, mark));
    }

    private static void CombatGatewayPacket(Fixture f, SoeConnection connection, byte[] packet, byte channel = 3)
    {
        Set(connection.Tag!, "Authenticated", true);
        f.Service.OnMessage(connection, [new GatewayHeader(GatewayTunnelFromClient.Opcode, channel).ToByte(), .. packet]);
    }

    [Fact]
    public void CombatReplication_ChannelThreeMultiWeaponUsesNativeOrderAndIsolatesMatches()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        var outsider = f.Add(3, new(6, 0, 0), matchId: 2);
        var gun = CombatGun(f, shooter, 2425, 5);
        Get<SessionCombat>(shooter.Tag!, "Combat").Draw.Clear();
        CombatInterest(f);
        int mark = f.Recorder.Routed.Count;
        CombatGatewayPacket(f, shooter, ShootingPacketBuilder.MultiWeapon(
            ShootingPacketBuilder.SwitchFireModeRequest(gun.Guid, 0, 1),
            CombatGuidPacket(0x16, gun.Guid, 42),
            ShootingPacketBuilder.Fire(gun.Guid, 0, 0, 0, [1]),
            CombatHint(gun.Guid, [1]), ShootingPacketBuilder.FireStateUpdate(gun.Guid, 0)));
        uint owner = CombatOwner(shooter, viewer);
        Assert.Equal(new[] {
            RemoteWeaponPackets.SwitchFireMode(owner, gun.Guid, 0, 1),
            RemoteWeaponPackets.Chamber(owner, gun.Guid),
            RemoteWeaponPackets.ProjectileLaunch(owner, gun.Guid, 0),
            RemoteWeaponPackets.FireState(owner, gun.Guid, true, new Vector4(100, 0, 0, 1)),
            RemoteWeaponPackets.FireState(owner, gun.Guid, false, new Vector4(0, 0, 0, 1)),
        }, CombatRemote(f, viewer, mark));
        Assert.Empty(CombatRemote(f, shooter, mark));
        Assert.Empty(CombatRemote(f, outsider, mark));
    }

    [Fact]
    public void CombatReplication_DeathStopsPresentationAndRejectsFurtherWeaponInput()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        f.Add(3, new(6, 0, 0)); // Keep the observer in a live match after the death.
        var gun = CombatGun(f, shooter, 1374, 0);
        var bag = Get<PlayerInventory>(shooter.Tag!, "Inventory");
        Assert.True(bag.TryStow(bag.CreateInstance(1511, 4)));
        CombatInterest(f);
        CombatPacket(f, shooter, gun, ShootingPacketBuilder.ReloadRequest(gun.Guid), Environment.TickCount64 + 10_000);
        var combat = Get<SessionCombat>(shooter.Tag!, "Combat");
        var pending = Assert.IsType<PendingWeaponReload>(combat.Reload);
        int mark = f.Recorder.Routed.Count;
        f.Service.ForTest(shooter).Damage(10_000, DamageCause.Bullet, 2, "Player2", 10000);
        Assert.Null(combat.Reload);
        Assert.Contains(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.ReloadInterrupt(CombatOwner(shooter, viewer), gun.Guid)));
        Assert.Contains(CombatRemote(f, viewer, mark), p => p.SequenceEqual(
            RemoteWeaponPackets.FireState(CombatOwner(shooter, viewer), gun.Guid, false, new Vector4(0, 0, 0, 1))));
        Assert.Null(WeaponFireArm.AdvanceReload(combat, pending, pending.DueAtMs, gun.Guid, bag));
        mark = f.Recorder.Routed.Count;
        CombatGatewayPacket(f, shooter, ShootingPacketBuilder.Fire(gun.Guid, 0, 0, 0, [1]));
        CombatGatewayPacket(f, shooter, CombatGuidPacket(0x16, gun.Guid));
        CombatGatewayPacket(f, shooter, ShootingPacketBuilder.ReloadRequest(gun.Guid));
        Assert.Equal(0, combat.Shooter.ShotsFired);
        Assert.Empty(CombatRemote(f, viewer, mark));
    }

    [Fact]
    public void CombatReplication_ReconnectCreatesFreshWeaponIdentityWithoutReplayingPriorActions()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero); var viewer = f.Add(2, new(5, 0, 0));
        f.Add(3, new(6, 0, 0));
        var old = CombatGun(f, shooter, 2425, 5);
        CombatInterest(f);
        CombatPacket(f, shooter, old, ShootingPacketBuilder.Fire(old.Guid, 0, 0, 0, [1]), Environment.TickCount64 + 10_000);
        int mark = f.Recorder.Routed.Count;
        shooter.Disconnect(); f.Service.OnDisconnected(shooter, DisconnectCause.ServerRequested);
        f.Connections.Remove(shooter);
        Assert.Contains(f.Recorder.Routed.Skip(mark), r => ReferenceEquals(r.Connection, viewer)
            && r.Packet[1..].SequenceEqual(PeerBurst.Leave(1)));
        var returned = f.Add(1, Vector3.Zero);
        var gun = CombatGun(f, returned, 1374, 5);
        Assert.NotEqual(old.Guid, gun.Guid);
        mark = f.Recorder.Routed.Count;
        CombatInterest(f);
        Assert.Equal(3, CombatRemote(f, viewer, mark).Length); // reset, add, selection; no historical action
        CombatPacket(f, returned, gun, ShootingPacketBuilder.Fire(gun.Guid, 0, 0, 0, [1]), Environment.TickCount64 + 10_000);
        Assert.Equal(4, Get<SessionCombat>(returned.Tag!, "Combat").Shooter.AmmoOf(gun.Guid));
    }
}
