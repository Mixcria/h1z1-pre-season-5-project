using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Equipment;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone;

/// <summary>
/// <b>Lane 1F on the wire</b> - what a real session sends when the owner picks a gun up, presses a
/// number key, pulls a dry trigger and presses R with nothing to load.
///
/// <para>
/// Send-side only, per docs/32 and D29: these tests prove what the server SENT and nothing about
/// what the client did with it. The three things they pin are the three the lane exists for - that a
/// looted gun arrives EMPTY, that <c>86 06</c> now runs a draw instead of writing a log line, and
/// that a reload with an empty bag is <em>answered</em> rather than met with silence.
/// </para>
/// <para>
/// The half that consumes an ammunition box is pinned in
/// <c>Cranberry.Tests.Zone.Combat.AmmoStoreTests</c> instead, over the same real
/// <c>WeaponFireArm</c> / <c>PlayerInventory</c> / <c>PlayerAmmoContext</c> triple: the development
/// drop can only spawn ONE item definition per session (<c>ZoneOptions.GroundLootItemDefinitionId</c>),
/// so a session cannot be made to contain both a gun and a box without the Z2 cluster path.
/// </para>
/// </summary>
public sealed class HandAndAmmoIntegrationTests
{
    /// <summary><c>ClientItemDefinitions</c> 2425, "AR-15" - the dev drop's weapon.</summary>
    private const uint Rifle = AugustHeldWeapon.ItemDefinitionId;

    /// <summary>The admitted character guid, which is also every lead guid a right-click carries.</summary>
    private const ulong Self = 0x1001;

    private sealed class RecordingRecorder : IPacketRecorder
    {
        public List<(string Direction, byte[] Bytes)> Messages { get; } = [];

        public void RecordSession(IPEndPoint remote, in SessionRequest request)
        {
        }

        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) =>
            Messages.Add((direction, bytes.ToArray()));

        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext)
        {
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;

        public void Log(TransportLogLevel level, string message)
        {
        }
    }

    /// <summary>
    /// Pick two rifles up, then press 2 and then 1. Before this lane the handler wrote one log line
    /// and returned; now each press runs the same draw the pickup does.
    /// </summary>
    [Fact]
    public void PressingTwoThenOneRunsTheDrawPath()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World();

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase);
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase + 1);

        int before = recorder.Messages.Count;
        SendSelectSlot(service, connection, SurvivorLoadout.Wheel2);
        byte[][] second = Sent(recorder, before);

        before = recorder.Messages.Count;
        SendSelectSlot(service, connection, SurvivorLoadout.Wheel1);
        byte[][] first = Sent(recorder, before);

        // Every draw refreshes the wheel: 86 04 SetLoadoutSlots is the packet the old handler
        // deliberately withheld ("no 86/07 echo, 86/04 refresh, or 94/01 RHand row").
        Assert.Contains(second, IsSetLoadoutSlots);
        Assert.Contains(first, IsSetLoadoutSlots);

        // ...and the draw ends in an equipment writer, whichever arm ran: the eight-packet sequence
        // binds with 94 02, the fallback dress re-states the whole character with 94 01.
        Assert.Contains(first, p => IsEquipment(p, SetCharacterEquipmentSlot.SubOpcode)
            || IsEquipment(p, SetCharacterEquipment.SubOpcode));
    }

    /// <summary>
    /// Key 4 is the FISTS (loadout 17 slot 7, <c>ITEM_ID 85</c>, <c>SLOT_INPUT_ACTION Slot4</c>) and
    /// key 5 is the BINOCULARS (slot 5, class 25081, which <c>ItemClassMappings</c> row 337 maps
    /// item 1542 onto). Both are accepted; neither ever produces a body-slot-7 row for item 85,
    /// which is the regression that locked movement and mouse-look after the bootstrap.
    /// </summary>
    [Theory]
    [InlineData(SurvivorLoadout.Fists)]
    [InlineData(SurvivorLoadout.Binoculars)]
    public void KeysFourAndFiveAreAcceptedAndNeverWieldTheFists(uint slotId)
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World();

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase);

        int before = recorder.Messages.Count;
        SendSelectSlot(service, connection, slotId);
        byte[][] sent = Sent(recorder, before);

        if (slotId == SurvivorLoadout.Fists) Assert.Empty(sent); // Already selected: no ability reset.
        else Assert.Contains(sent, IsSetLoadoutSlots);

        foreach (byte[] packet in sent)
        {
            if (!IsEquipment(packet, SetCharacterEquipmentSlot.SubOpcode))
            {
                continue;
            }

            // 94 02: u8 header; u8 opcode; u8 sub; u64 characterGuid; u32 profileId; then the row.
            Assert.NotEqual(
                PlayerInventory.SurvivorFistsItemDefinitionId,
                BitConverter.ToUInt32(packet, 3));
        }
    }

    /// <summary>
    /// <b>The gun arrives empty and says so.</b> A pull of the trigger on a freshly looted rifle is
    /// refused and answered with <c>82 1e FireRejected</c>; pressing R with no .223 in the bag is
    /// refused and answered with <c>82 0b ReloadRejected</c>. Neither packet had ever been sent by
    /// this server, because before lane 1F neither refusal could happen.
    /// </summary>
    [Fact]
    public void ALootedGunIsDryAndBothRefusalsAreAnswered()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World();

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase);
        SendSelectSlot(service, connection, SurvivorLoadout.Wheel1);

        ulong gun = FirstRifleInstanceGuid(recorder);
        Assert.NotEqual(0UL, gun);

        int before = recorder.Messages.Count;
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.Fire(gun, 0, 0, 0, [7]));
        byte[][] afterFire = Sent(recorder, before);

        Assert.Contains(afterFire, p => IsWeapon(p, WeaponReplyPackets.SubFireRejected));

        before = recorder.Messages.Count;
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));
        byte[][] afterReload = Sent(recorder, before);

        Assert.Contains(afterReload, p => IsWeapon(p, WeaponReplyPackets.SubReloadRejected));

        // And nothing was minted: no 82 08 Reload went out, because there was nothing to load.
        Assert.DoesNotContain(afterReload, p => IsWeapon(p, WeaponReplyPackets.SubReload));
    }

    /// <summary>
    /// <b>docs/102 §6 — <c>UnloadWeapon</c> is live, and it is conservative.</b> The magazine's
    /// rounds come out of the gun and land in the bag as a stack of its own calibre; the weapon is
    /// re-announced so its tile stops claiming a loaded magazine, and the ammunition is announced
    /// with the packet its state calls for — <c>11 02 ItemAdd</c> for a stack the client has never
    /// held, <c>11 03 ItemUpdate</c> for one that grew (an update for an unknown guid is a no-op in
    /// <c>FUN_140dbfcb0</c>).
    /// <para>
    /// Two guns, because that is the only way one session can produce both cases: the first unload
    /// creates the .223 stack and the second merges into it. Until this lane's two-line wiring
    /// (<c>InventoryActions.Resolve(inventory, request, state.Combat.Shooter)</c>) the whole verb was
    /// D118's named refusal and neither packet was ever sent.
    /// </para>
    /// </summary>
    [Fact]
    public void AnUnloadPutsTheMagazineInTheBagAndAnnouncesTheStackWithElevenOhThree()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World(
                // The guns arrive LOADED, so there is a magazine to unload at all. With the shipped
                // default (empty) the verb correctly answers "its magazine is already empty".
                new AmmoOptions { GunsSpawnEmpty = false });

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase);
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase + 1);

        ulong[] guns = RifleInstanceGuids(recorder);
        Assert.Equal(2, guns.Length);

        uint round = AmmoTypes.AmmoItemFor(Rifle);
        Assert.NotEqual(0u, round);

        // Combat's FIRST SIGHT of a weapon is its trigger or its reload (WeaponFireArm), so a gun
        // nobody has touched has no magazine to unload and the verb correctly answers with a
        // repaint. Press R on each - only the active-hand instance is accepted, so each key press
        // is followed by both guids and the wrong one is refused harmlessly.
        foreach (uint slot in new[] { SurvivorLoadout.Wheel1, SurvivorLoadout.Wheel2 })
        {
            SendSelectSlot(service, connection, slot);
            foreach (ulong gun in guns)
            {
                SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));
            }
        }

        // The first unload: a stack the client has never been told about, so an ADD.
        int before = recorder.Messages.Count;
        SendUseItem(service, connection, guns[0], UnloadWeaponOption);
        byte[][] first = Sent(recorder, before);

        Assert.Contains(first, p => IsItemAdd(p, round));
        Assert.DoesNotContain(first, IsItemUpdate);
        // And the gun itself was re-announced, so its tile stops claiming a full magazine.
        Assert.Contains(first, p => IsItemAdd(p, Rifle));

        // The second merges into that stack, which is the 11 03 this lane derived.
        before = recorder.Messages.Count;
        SendUseItem(service, connection, guns[1], UnloadWeaponOption);
        byte[][] second = Sent(recorder, before);

        Assert.Contains(second, IsItemUpdate);
        // Nothing was refused: a Container.Error would be D118's answer, not this one.
        Assert.DoesNotContain(second, IsContainerError);
    }

    // ---------------------------------------------------------------------------- the harness

    /// <summary><c>ItemUseOptions</c> row 7, <c>UnloadWeapon</c> — one of item 2425's own options.</summary>
    private const uint UnloadWeaponOption = 7;

    /// <summary>
    /// <b>docs/107 §4 - the whole shot, end to end, over the real <c>ZoneService</c>.</b> Draw a
    /// gun, put its magazine in the bag, reload it back out of the bag, fire it three times, watch
    /// the magazine come down by three, empty it and pull the trigger on nothing.
    /// <para>
    /// Every step is asserted on the WIRE, not on a counter: the reload's <c>82 08</c> carries
    /// <c>ammoCount</c>, each accepted shot spends durability and produces its <c>11 03</c>, the
    /// unload hands back exactly the rounds that are left, and the dry trigger is answered with
    /// <c>82 1e FireRejected</c>. Before wave 13 the whole ladder was invisible to the client: the
    /// <c>82 08</c> was byte-exact and STILL could not move the counter, because the applier
    /// <c>FUN_1414887e0</c> requires <c>ammoSlot &lt; item+0xc8</c> and <c>item+0xc8</c> is the
    /// <c>ItemAdd</c> tail's <c>n0</c>, which this server wrote as 0 (docs/107 §1).
    /// </para>
    /// <para>
    /// The shots are spaced past the AR-15's own <c>REFIRE_TIME_MS</c> (120 ms,
    /// <c>AugustWeaponFacts</c> row 2425) because the rate-of-fire gate is real and answers a shot
    /// fired early with silence - the test would otherwise be measuring the gate.
    /// </para>
    /// </summary>
    [Fact]
    public void TheWholeShotLadderDrawReloadFireDecrementDryRefusal()
    {
        // The guns arrive LOADED, which is the only way one session can contain both a gun and the
        // rounds for it: the development drop spawns a single item definition, so the bag is seeded
        // by unloading the gun's own magazine into it (the same trick
        // AnUnloadPutsTheMagazineInTheBagAndAnnouncesTheStackWithElevenOhThree uses).
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World(new AmmoOptions { GunsSpawnEmpty = false });

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase);
        SendSelectSlot(service, connection, SurvivorLoadout.Wheel1);

        ulong gun = FirstRifleInstanceGuid(recorder);
        Assert.NotEqual(0UL, gun);
        uint round = AmmoTypes.AmmoItemFor(Rifle);

        // Combat's first sight of the weapon is this reload; the magazine is full, so it is refused
        // - and that refusal is what declares the instance at its clip of 30.
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));

        // Step 1: empty it into the bag, so there is something to reload FROM.
        SendUseItem(service, connection, gun, UnloadWeaponOption);

        // Step 2: reload out of the bag. The 82 08 that comes back carries the magazine.
        int before = recorder.Messages.Count;
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));
        byte[] reload = Assert.Single(
            Sent(recorder, before), p => IsWeapon(p, WeaponReplyPackets.SubReload));

        Assert.Equal(30u, ReloadAmmoCount(reload));
        Assert.Equal(0u, ReloadInventoryAmmoCount(reload));      // the bag gave up all thirty
        var draw = ((SessionCombat)connection.Tag!.GetType().GetProperty("Combat")!
            .GetValue(connection.Tag)!).Draw;
        Assert.True(SpinWait.SpinUntil(() => draw.IsReady(Environment.TickCount64), 2000));

        // Step 3: three shots, each past the refire gate, each spending a round and 5 durability.
        const int shots = 3;
        for (int i = 0; i < shots; i++)
        {
            before = recorder.Messages.Count;
            SendWeapon(
                service,
                connection,
                Combat.ShootingPacketBuilder.Fire(gun, 0, 0, 0, [(uint)(100 + i)]));
            byte[][] afterShot = Sent(recorder, before);

            Assert.DoesNotContain(afterShot, p => IsWeapon(p, WeaponReplyPackets.SubFireRejected));
            Assert.Contains(afterShot, IsItemUpdate);            // the durability 11 03 of lane 1F

            Thread.Sleep(RetailBalance.RefireGateMs(Rifle, 40) + 30);
        }

        // Step 4: the magazine really came down by three - the unload hands back 27, not 30.
        before = recorder.Messages.Count;
        SendUseItem(service, connection, gun, UnloadWeaponOption);
        byte[] stack = Assert.Single(
            Sent(recorder, before), p => IsItemAdd(p, round) || IsItemUpdate(p));

        Assert.True(IsItemAdd(stack, round), "the .223 came back as a fresh stack");
        Assert.Equal((uint)(30 - shots), BitConverter.ToUInt32(stack, 32));

        // ...and the gun was re-announced with an EMPTY magazine in its tail, which is the field
        // the hotbar counter reads (docs/107 §1). Base record 62 B from offset 16, then the tail's
        // u8 baseFlag, i32 n0 and the one u32 element.
        byte[] repaint = Sent(recorder, before).Last(p => IsItemAdd(p, Rifle));
        Assert.Equal(1 + 149, repaint.Length);       // the gateway header byte, then the 149-byte 11 02
        Assert.Equal(1, BitConverter.ToInt32(repaint, 16 + 62 + 1));
        Assert.Equal(0u, BitConverter.ToUInt32(repaint, 16 + 62 + 5));

        // Step 5: the dry trigger, answered rather than met with silence.
        Thread.Sleep(RetailBalance.RefireGateMs(Rifle, 40) + 30);
        before = recorder.Messages.Count;
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.Fire(gun, 0, 0, 0, [200]));
        byte[][] dry = Sent(recorder, before);

        Assert.Contains(dry, p => IsWeapon(p, WeaponReplyPackets.SubFireRejected));
        Assert.DoesNotContain(dry, IsItemUpdate);                // nothing was spent
    }

    /// <summary>
    /// <b>docs/107 §2 - the ADS packet arrives on channel 3 and is recorded.</b> This is the shape
    /// D189 unblocked: the same <c>82 1f MultiWeapon</c> envelope the client actually sends, on the
    /// channel it actually sends it on, carrying a <c>82 0c</c> that Cranberry used to log as
    /// "malformed managed movement". Nothing is sent back, because sub <c>0x0c</c> has no receive
    /// case in the client (<c>FUN_140b07010</c>).
    /// </summary>
    [Fact]
    public void TheAdsRequestArrivesOnChannelThreeAndIsAnsweredWithNothing()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World();

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase);
        SendSelectSlot(service, connection, SurvivorLoadout.Wheel1);

        ulong gun = FirstRifleInstanceGuid(recorder);
        Assert.NotEqual(0UL, gun);

        int before = recorder.Messages.Count;
        SendWeaponOnChannel(
            service,
            connection,
            Combat.ShootingPacketBuilder.MultiWeapon(
                Combat.ShootingPacketBuilder.SwitchFireModeRequest(gun, 0, 1)),
            channel: 3);
        SendWeaponOnChannel(
            service,
            connection,
            Combat.ShootingPacketBuilder.MultiWeapon(
                Combat.ShootingPacketBuilder.SwitchFireModeRequest(gun, 0, 0)),
            channel: 3);

        // No s2c 0x82 at all: the local switch is client-authoritative.
        Assert.DoesNotContain(Sent(recorder, before), p => p.Length > 1 && p[1] == ZoneOpcodes.WeaponBase);
    }

    [Fact]
    public void TimedReloadAcknowledgesCurrentCountsThenLoadsAfterTheListenerRunsTheCompletion()
    {
        static bool IsReload(byte[] packet) => IsWeapon(packet, WeaponReplyPackets.SubReload);
        var (service, connection, recorder, pending) = World(
            new AmmoOptions { GunsSpawnEmpty = false }, timedReload: true);
        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase);
        ulong gun = Assert.Single(RifleInstanceGuids(recorder));
        SendSelectSlot(service, connection, SurvivorLoadout.Wheel1);
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));
        SendUseItem(service, connection, gun, UnloadWeaponOption);

        int before = recorder.Messages.Count;
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));
        byte[] acknowledgement = Assert.Single(Sent(recorder, before), IsReload);
        Assert.Equal(0u, ReloadAmmoCount(acknowledgement));
        Assert.Equal(30u, ReloadInventoryAmmoCount(acknowledgement));
        Assert.DoesNotContain(Sent(recorder, before), IsItemUpdate);
        before = recorder.Messages.Count;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 6000 && !Sent(recorder, before).Any(IsReload))
        {
            while (pending.TryDequeue(out Action? work))
            {
                work();
            }
            Thread.Sleep(1);
        }

        byte[] reply = Assert.Single(Sent(recorder, before), IsReload);
        Assert.Equal(30u, ReloadAmmoCount(reply));
        Assert.Equal(0u, ReloadInventoryAmmoCount(reply));

        // Unload during a second pending reload must cancel it, even with an empty magazine.
        SendUseItem(service, connection, gun, UnloadWeaponOption);
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));
        before = recorder.Messages.Count;
        SendUseItem(service, connection, gun, UnloadWeaponOption);
        byte[][] stopped = [.. Sent(recorder, before).Where(IsReload)];
        Assert.Equal(2, stopped.Length); // cancellation, then counter sync after the idle ItemAdd
        Assert.All(stopped, packet =>
        {
            Assert.Equal(0u, ReloadAmmoCount(packet));
            Assert.Equal(30u, ReloadInventoryAmmoCount(packet));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LootDuringReloadGrantsImmediatelyOnceAndPreservesTheReload(bool itemUpdates)
    {
        var (service, connection, recorder, pending) = World(
            new AmmoOptions { GunsSpawnEmpty = false, SendItemUpdate = itemUpdates }, timedReload: true);
        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteract(service, connection, LootWorld.DefaultWorldGuidBase);
        ulong gun = Assert.Single(RifleInstanceGuids(recorder));
        SendSelectSlot(service, connection, SurvivorLoadout.Wheel1);
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));
        SendUseItem(service, connection, gun, UnloadWeaponOption);
        SendWeapon(service, connection, Combat.ShootingPacketBuilder.ReloadRequest(gun));

        T State<T>(string name) => (T)connection.Tag!.GetType().GetProperty(name)!.GetValue(connection.Tag)!;
        var combat = State<SessionCombat>("Combat");
        var inventory = State<PlayerInventory>("Inventory");
        var loot = State<LootWorld>("Loot");
        var reload = Assert.IsType<PendingWeaponReload>(combat.Reload);
        uint rounds = AmmoTypes.AmmoItemFor(Rifle);
        var stack = Assert.Single(inventory.Items.Values, item => item.DefinitionId == rounds);
        ulong stackGuid = stack.Guid;
        var ground = loot.Spawn(rounds, 1, System.Numerics.Vector3.Zero, count: 5);

        int before = recorder.Messages.Count;
        SendInteract(service, connection, ground.WorldGuid);
        byte[][] replies = Sent(recorder, before);
        Assert.Equal(35u, stack.Count);
        Assert.Equal(stackGuid, stack.Guid);
        Assert.False(loot.TryGet(ground.WorldGuid, out _));
        Assert.Same(reload, combat.Reload);
        Assert.Equal(0, combat.Shooter.AmmoOf(gun));
        if (itemUpdates)
        {
            byte[] update = Assert.Single(replies, IsItemUpdate);
            Assert.Equal(stackGuid, BitConverter.ToUInt64(update, 20));
            Assert.Equal(35u, BitConverter.ToUInt32(update, 28));
            Assert.DoesNotContain(replies, p => IsItemAdd(p, rounds));
            Assert.DoesNotContain(replies, p => p.Length > 3 && p[1] == 0x11
                && BitConverter.ToUInt16(p, 2) == ItemDelete.SubOpcode);
        }
        else Assert.Contains(replies, p => IsItemAdd(p, rounds));

        before = recorder.Messages.Count;
        SendInteract(service, connection, ground.WorldGuid);
        Assert.Equal(35u, stack.Count);
        Assert.DoesNotContain(Sent(recorder, before), p => IsItemUpdate(p) || IsItemAdd(p, rounds));

        // Apparel and a stowed spare gun must also grant while the original gun reloads.
        foreach (uint definition in new uint[] { 2170, Rifle })
        {
            ground = loot.Spawn(definition, 1, System.Numerics.Vector3.Zero);
            before = recorder.Messages.Count;
            SendInteract(service, connection, ground.WorldGuid);
            Assert.Contains(Sent(recorder, before), p => IsItemAdd(p, definition));
            Assert.False(loot.TryGet(ground.WorldGuid, out _));
            Assert.Equal(gun, inventory.WieldedItemGuid);
            Assert.Same(reload, combat.Reload);
            Assert.DoesNotContain(Sent(recorder, before), p => IsWeapon(p, WeaponReplyPackets.SubReload));
        }

        Assert.NotNull(WeaponFireArm.AdvanceReload(combat, reload, reload.DueAtMs, gun, inventory));
        Assert.Null(combat.Reload);
        Assert.Equal(30, combat.Shooter.AmmoOf(gun));
        Assert.Equal(5u, stack.Count);
        connection.Disconnect();
    }

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder,
        ConcurrentQueue<Action> Pending) World(AmmoOptions? ammo = null, bool timedReload = false)
    {
        var pending = new ConcurrentQueue<Action>();
        var options = new ZoneOptions
        {
            // docs/121 §6 (D334): the timed reload defers the 82 08 by the client's own
            // RELOAD_TIME_MS through Later(); this world asserts the ladder synchronously on the
            // recorder, so the timer is off here and ShootingRetailAuditTests pins the delays.
            Combat = CombatOptions.Default with { Ammo = ammo ?? AmmoOptions.Default, TimedReload = timedReload },
            DevGroundLootMs = 1,
            DevGroundLootCount = 2,
            GroundLootItemDefinitionId = Rifle,
            GroundLootModelId = 23,
            GroundLootNameId = 32,
            GroundLootRadius = 0f,
            SendDoors = false,
            SendVehicles = false,
            Inventory = new InventoryOptions { UseWieldSequence = true },
        };

        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            Self, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options)
        {
            Post = pending.Enqueue,
        };

        var request = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
        var connection = new SoeConnection(
            new IPEndPoint(IPAddress.Loopback, 5555),
            in request,
            SessionSettings.WithSeed(1),
            service.OnSessionRequest(new IPEndPoint(IPAddress.Loopback, 5555), in request),
            service,
            new SilentLog(),
            (_, _) => { },
            now: 0);
        service.OnConnected(connection);

        using var writer = new PacketWriter();
        writer.WriteByte(GatewayLoginRequest.Opcode);
        writer.WriteUInt64(admission.Guid);
        writer.WriteString(admission.Ticket);
        writer.WriteString(GatewayLoginRequest.AugustProtocol);
        writer.WriteString(GatewayLoginRequest.AugustVersion);
        service.OnMessage(connection, writer.Written.ToArray());

        return (service, connection, recorder, pending);
    }

    private static void SendClientIsReady(ZoneService service, SoeConnection connection)
    {
        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        service.OnMessage(connection, ready.Written.ToArray());
    }

    private static void SendInteract(ZoneService service, SoeConnection connection, ulong targetGuid)
    {
        using var interact = new PacketWriter();
        interact.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        interact.WriteByte(ZoneOpcodes.CommandBase);
        interact.WriteUInt16(InteractRequest.SubOpcode);
        interact.WriteUInt64(targetGuid);
        service.OnMessage(connection, interact.Written.ToArray());
    }

    /// <summary>The client's own hotbar press: <c>86 06 | u32 | u32 slotId | u32 clientGameTime</c>.</summary>
    private static void SendSelectSlot(ZoneService service, SoeConnection connection, uint slotId)
    {
        using var select = new PacketWriter();
        select.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        select.WriteByte(SelectLoadoutSlotRequest.Opcode);
        select.WriteByte(SelectLoadoutSlotRequest.SubOpcode);
        select.WriteUInt32(0);
        select.WriteUInt32(slotId);
        select.WriteUInt32(1234);
        service.OnMessage(connection, select.Written.ToArray());
    }

    /// <summary>
    /// The client's own right-click: <c>ac 2c</c>, the 47-byte simple form (docs/63 §1) —
    /// <c>u32 itemCount; u32 reservedA; u32 optionId; u64 character; u64 source; u64 target;
    /// u64 itemGuid; u8 simple</c>. The three lead guids are the same value in all 21 captured
    /// packets, because every action the owner performed was on his own inventory.
    /// </summary>
    private static void SendUseItem(
        ZoneService service, SoeConnection connection, ulong itemGuid, uint optionId)
    {
        using var use = new PacketWriter();
        use.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        use.WriteByte(ItemUseOpcodes.ItemsBase);
        use.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        use.WriteUInt32(1);
        use.WriteUInt32(0);
        use.WriteUInt32(optionId);
        use.WriteUInt64(Self);
        use.WriteUInt64(Self);
        use.WriteUInt64(Self);
        use.WriteUInt64(itemGuid);
        use.WriteByte(1);
        service.OnMessage(connection, use.Written.ToArray());
    }

    private static void SendWeapon(ZoneService service, SoeConnection connection, byte[] weaponPacket) =>
        SendWeaponOnChannel(service, connection, weaponPacket, channel: 0);

    /// <summary>
    /// <b>The August client sends its weapon traffic on channel 3</b>, not 0 (D189,
    /// FIRE-PATH-DIAGNOSIS §0), so a test that only ever used channel 0 could not have caught the
    /// divert that swallowed 431 packets.
    /// </summary>
    private static void SendWeaponOnChannel(
        ZoneService service, SoeConnection connection, byte[] weaponPacket, byte channel)
    {
        using var wrapped = new PacketWriter();
        wrapped.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, channel).ToByte());
        wrapped.WriteRaw(weaponPacket);
        service.OnMessage(connection, wrapped.Written.ToArray());
    }

    /// <summary>
    /// <c>82 08 Weapon.Reload</c>'s <c>ammoCount</c> - <b>the magazine the client will display</b>
    /// (<c>FUN_1414887e0</c> writes exactly this field into the ammo slot). Wire:
    /// <c>u8 gateway; u8 0x82; u32 gameTime; u8 0x08; u64 guid; u32 projectileCount; u32 ammoCount;
    /// u32 inventoryAmmoCount; u64 reloadCount</c>.
    /// </summary>
    private static uint ReloadAmmoCount(byte[] packet) => BitConverter.ToUInt32(packet, 19);

    /// <inheritdoc cref="ReloadAmmoCount"/>
    private static uint ReloadInventoryAmmoCount(byte[] packet) => BitConverter.ToUInt32(packet, 23);

    private static void Pump(ConcurrentQueue<Action> pending, string what)
    {
        Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000), $"{what} was never posted");
        Assert.True(pending.TryDequeue(out Action? work));
        work!();
    }

    private static byte[][] Sent(RecordingRecorder recorder, int skip = 0) =>
        [.. recorder.Messages.Skip(skip).Where(m => m.Direction == "s2c").Select(m => m.Bytes)];

    private static bool IsSetLoadoutSlots(byte[] packet) =>
        packet.Length > 2
        && packet[1] == ZoneOpcodes.LoadoutsBase
        && packet[2] == SetLoadoutSlots.SubOpcode;

    private static bool IsEquipment(byte[] packet, byte sub) =>
        packet.Length > 2 && packet[1] == ZoneOpcodes.EquipmentBase && packet[2] == sub;

    private static bool IsWeapon(byte[] packet, byte sub) =>
        packet.Length > 6 && packet[1] == ZoneOpcodes.WeaponBase && packet[6] == sub;

    /// <summary>
    /// The instance guid of the first AR-15 the server announced. <c>11 02 ItemAdd</c> is
    /// <c>u8 header; u8 0x11; u16 0x0002; u64 owner; i32 length; u32 definitionId; u32 tint;
    /// u64 itemGuid; ...</c>.
    /// </summary>
    private static ulong FirstRifleInstanceGuid(RecordingRecorder recorder) =>
        RifleInstanceGuids(recorder).FirstOrDefault();

    /// <summary>Every distinct AR-15 instance the server announced, in announcement order.</summary>
    private static ulong[] RifleInstanceGuids(RecordingRecorder recorder)
    {
        var guids = new List<ulong>();
        foreach (byte[] packet in Sent(recorder).Where(p => IsItemAdd(p, Rifle)))
        {
            ulong guid = BitConverter.ToUInt64(packet, 24);
            if (!guids.Contains(guid))
            {
                guids.Add(guid);
            }
        }

        return [.. guids];
    }

    /// <summary>
    /// <c>11 02 ItemAdd</c> for one definition: <c>u8 header; u8 0x11; u16 0x0002; u64 owner;
    /// i32 length; u32 definitionId; u32 tint; u64 itemGuid; ...</c>.
    /// </summary>
    private static bool IsItemAdd(byte[] packet, uint definitionId) =>
        packet.Length >= 32
        && packet[1] == ZoneOpcodes.ClientUpdateBase
        && BitConverter.ToUInt16(packet, 2) == ItemAdd.SubOpcode
        // Ground objects now receive their own item record for native proximity clicks.
        // This ladder exercises only rifles granted to the player.
        && BitConverter.ToUInt64(packet, 4) == Self
        && BitConverter.ToUInt32(packet, 16) == definitionId;

    /// <summary><c>11 03 ClientUpdate.ItemUpdate</c> — 73 bytes plus the gateway header.</summary>
    private static bool IsItemUpdate(byte[] packet) =>
        packet.Length > 3
        && packet[1] == ZoneOpcodes.ClientUpdateBase
        && BitConverter.ToUInt16(packet, 2) == ItemUpdate.SubOpcode;

    /// <summary><c>c8 03 Container.Error</c> — the client's vocabulary for a server-side no.</summary>
    private static bool IsContainerError(byte[] packet) =>
        packet.Length > 2
        && packet[1] == ContainerOpcodes.ContainerBase
        && packet[2] == ContainerError.SubOpcode;
}
