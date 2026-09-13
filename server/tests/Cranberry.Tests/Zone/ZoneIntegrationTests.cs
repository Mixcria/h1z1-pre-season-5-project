using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone;

/// <summary>
/// The wave-3 integration surface: what <see cref="ZoneService"/> actually puts on the wire once the
/// lighting, loot, movement, inventory, door and vehicle lanes are wired together.
/// <para>
/// These are <b>send-side</b> assertions and nothing more. Per docs/32's evidence standard a host log
/// line — and equally a test over the bytes the server emits — proves only what the server
/// <em>sent</em>; none of it is LIVE-VERIFIED until a client-originated packet or a screenshot says
/// so. What these tests do buy is the negative half, which is the half that cost four hours on
/// 2026-08-29: that the LoginZone menu burst and the match-zoning burst are unchanged by any of it.
/// </para>
/// </summary>
public partial class ZoneIntegrationTests
{
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

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder)
        Admit(ZoneOptions options, Action<Action>? post = null)
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options) { Post = post };
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
        return (service, connection, recorder);
    }

    private static void SendClientIsReady(ZoneService service, SoeConnection connection)
    {
        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        service.OnMessage(connection, ready.Written.ToArray());
    }

    private static void SendInteractRequest(ZoneService service, SoeConnection connection, ulong targetGuid)
    {
        using var interact = new PacketWriter();
        interact.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        interact.WriteByte(ZoneOpcodes.CommandBase);
        interact.WriteUInt16(InteractRequest.SubOpcode);
        interact.WriteUInt64(targetGuid);
        service.OnMessage(connection, interact.Written.ToArray());
    }

    /// <summary>The August client's c2s hotbar request: tunnel + <c>86 06 | 0 | slot | game time</c>.</summary>
    private static void SendHotbarSelection(ZoneService service, SoeConnection connection, uint slotId)
    {
        using var select = new PacketWriter();
        select.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        select.WriteByte(ZoneOpcodes.LoadoutsBase);
        select.WriteByte(LoadoutOpcodes.SelectSlotRequestSub);
        select.WriteUInt32(0);
        select.WriteUInt32(slotId);
        select.WriteUInt32(0x1234_5678);
        service.OnMessage(connection, select.Written.ToArray());
    }

    /// <summary>
    /// Arms only the state gates that the real c2s <c>Vehicle.Dismiss</c> dispatcher checks before
    /// it enters the parachute landing burst.  The session type is deliberately private to
    /// <see cref="ZoneService"/>, so the test drives the public wire path rather than exposing a
    /// production-only landing hook.
    /// </summary>
    private static void ArmMountedParachute(SoeConnection connection, ulong chuteGuid)
    {
        Assert.NotNull(connection.Tag);
        object state = connection.Tag!;
        var chute = state.GetType().GetProperty("ChuteGuid");
        var mounted = state.GetType().GetProperty("MountRequested");
        Assert.NotNull(chute);
        Assert.NotNull(mounted);
        chute!.SetValue(state, chuteGuid);
        mounted!.SetValue(state, true);
    }

    /// <summary>The captured c2s touchdown signal: tunnel + <c>88 18 | u64(0)</c>.</summary>
    private static void SendVehicleDismiss(ZoneService service, SoeConnection connection)
    {
        using var dismiss = new PacketWriter();
        dismiss.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        dismiss.WriteByte(ZoneOpcodes.VehicleBase);
        dismiss.WriteByte(VehicleDismiss.SubOpcode);
        dismiss.WriteUInt64(0);       // August's live null-guid landing acknowledgement
        service.OnMessage(connection, dismiss.Written.ToArray());
    }

    /// <summary>Runs the next piece of deferred work the service posted. Every timer in
    /// <see cref="ZoneService"/> rides <c>Task.Delay</c>, so the post is asynchronous even at 1 ms.</summary>
    private static void Pump(ConcurrentQueue<Action> pending, string what)
    {
        Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000), $"{what} was never posted");
        Assert.True(pending.TryDequeue(out Action? work));
        work!();
    }

    private static byte[][] Sent(RecordingRecorder recorder, int skip = 0) =>
        [.. recorder.Messages.Where(m => m.Direction == "s2c").Select(m => m.Bytes).Skip(skip)];

    private static int SentCount(RecordingRecorder recorder) =>
        recorder.Messages.Count(m => m.Direction == "s2c");

    /// <summary>Zone opcode of a tunnelled packet, with the sub-opcode where the family has one.</summary>
    private static string Label(byte[] packet) => packet[1] switch
    {
        ZoneOpcodes.EquipmentBase or ZoneOpcodes.ItemsBase or ZoneOpcodes.LoadoutsBase or ZoneOpcodes.CharacterBase
            => $"{packet[1]:x2}.{packet[2]:x2}",
        ZoneOpcodes.ClientUpdateBase or ContainerOpcodes.ContainerBase
            => $"{packet[1]:x2}.{BitConverter.ToUInt16(packet, 2):x4}",
        _ => $"{packet[1]:x2}",
    };

    /// <summary>
    /// docs/32 regression guard 2, restated for every path this wave added: an
    /// <c>Equipment.UnsetCharacterEquipmentSlot</c> (0x94 sub 3) appears in both captures the client
    /// refused (55× and 53×) and in neither capture it accepted (0× and 0×).
    /// </summary>
    private static void AssertNoEquipmentSlotClears(IEnumerable<byte[]> packets) =>
        Assert.DoesNotContain(
            packets,
            packet => packet.Length > 2
                && packet[1] == ZoneOpcodes.EquipmentBase
                && packet[2] == UnsetCharacterEquipmentSlot.SubOpcode);

    /// <summary>
    /// Reads the equipment rows actually serialized on the wire.  Attachments deliberately are
    /// not included: the active-hand mesh is safe there, while an item-guid equipment row at RHand
    /// is not.
    /// </summary>
    private static List<uint> WireEquipmentSlotIds(IEnumerable<byte[]> packets)
    {
        var slots = new List<uint>();
        foreach (byte[] packet in packets)
        {
            if (packet.Length < 3
                || packet[1] != ZoneOpcodes.EquipmentBase
                || packet[2] != SetCharacterEquipment.SubOpcode)
            {
                continue;
            }

            var reader = new PacketReader(packet.AsSpan(1));
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt64();
            _ = reader.ReadUInt32();
            _ = reader.ReadString();
            _ = reader.ReadString();
            int count = reader.ReadInt32();
            for (int row = 0; row < count; row++)
            {
                _ = reader.ReadUInt32();
                slots.Add(reader.ReadUInt32());
                _ = reader.ReadUInt64();
                _ = reader.ReadString();
                _ = reader.ReadString();
            }
        }

        return slots;
    }

    /// <summary>
    /// The same rows as <see cref="WireEquipmentSlotIds"/>, but keeping the item guid each one
    /// binds. The guid is the whole point of an equipment row at body slot 7: it is what
    /// <c>FUN_1411ceca0</c> resolves to find the weapon whose fire groups it reads.
    /// </summary>
    private static List<(uint SlotId, ulong ItemGuid)> WireEquipmentRows(IEnumerable<byte[]> packets)
    {
        var rows = new List<(uint, ulong)>();
        foreach (byte[] packet in packets)
        {
            if (packet.Length < 3
                || packet[1] != ZoneOpcodes.EquipmentBase
                || packet[2] != SetCharacterEquipment.SubOpcode)
            {
                continue;
            }

            var reader = new PacketReader(packet.AsSpan(1));
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt64();
            _ = reader.ReadUInt32();
            _ = reader.ReadString();
            _ = reader.ReadString();
            int count = reader.ReadInt32();
            for (int row = 0; row < count; row++)
            {
                _ = reader.ReadUInt32();
                uint slotId = reader.ReadUInt32();
                ulong itemGuid = reader.ReadUInt64();
                _ = reader.ReadString();
                _ = reader.ReadString();
                rows.Add((slotId, itemGuid));
            }
        }

        return rows;
    }

    /// <summary>
    /// Every data file this wave's subsystems read is shipped, parses, and reports the census its own
    /// lane derived. This is the boot-time check the host performs, run once here so that a missing
    /// or hand-broken <c>Data\</c> file fails in CI rather than at a landing.
    /// <para>
    /// The loot figure is docs/39 §I.2's measured ladder at <c>p = 0.27</c>, seed 1: 44,830 gated,
    /// 5,122 suppressed by the room caps, <b>39,708</b> items on the floor — against the 39,828 the
    /// owner's own world census counted, 0.3 % apart.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryWorldDataFileLoadsAndReportsItsDerivedCensus()
    {
        var (service, _, _) = Admit(new ZoneOptions { SendDoors = true, SendVehicles = true });

        string[] lines = service.PreloadWorldData();

        // Destructibles, loot, drop locations, doors and vehicles are all loaded before play.
        Assert.Equal(5, lines.Length);
        Assert.Contains("Z2 glass/wood objects", lines[0]);
        Assert.Contains("168,322 markers", lines[1]);
        // D270/D271/D275 (docs/112): the owner's ruled 0.27 density replaces the wave-8 weighted
        // 0.1223, so the floor is 39,346 items rather than 19,475 — twice as much on the ground, and
        // the world his own ten-room retail census describes. (45,205 gated, 5,186 trimmed by the
        // room caps, which stop being inert, and 673 vests taken back by D275's laminated-armour
        // rules.)
        // September 8: no naturally spawned bandages; the recalculated seed-one total is 38,739.
        Assert.Contains("38,739 items on the Z2 floor", lines[1]);
        Assert.Contains("92 named Z2 place(s)", lines[2]);
        Assert.Contains("4,147 Z2 door proxies", lines[3]);
        Assert.Contains("550 spawn anchors", lines[4]);
        Assert.DoesNotContain(lines, line => line.Contains("unavailable", StringComparison.Ordinal));
    }

    /// <summary>
    /// The LoginZone menu is the critical path to PLAY, and its burst is the one the client accepted
    /// in <c>captures/wire-20260829-150206.txt</c>. Neither of this wave's new packet families —
    /// the container/loadout model (0xc8, 0x86) nor the movement stat burst (<c>0f 40</c>,
    /// <c>11 05</c>) — may appear on it, whatever the options say. Both are armed only once the
    /// client answers <c>ClientIsReady</c> from inside the match.
    /// </summary>
    [Fact]
    public void TheMenuBurstCarriesNoContainerLoadoutOrStatPacket()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            SendContainers = true,
            SendMovementStats = true,
            SendDoors = true,
            SendVehicles = true,
        });

        SendClientIsReady(service, connection);
        SendClientIsReady(service, connection);

        string[] labels = [.. Sent(recorder).Select(Label)];
        Assert.DoesNotContain($"{ContainerOpcodes.ContainerBase:x2}.0002", labels);   // InitContainers
        Assert.DoesNotContain($"{ZoneOpcodes.LoadoutsBase:x2}.03", labels);           // SetCurrentLoadout
        Assert.DoesNotContain($"{ZoneOpcodes.LoadoutsBase:x2}.04", labels);           // SetLoadoutSlots
        Assert.DoesNotContain($"{ZoneOpcodes.CharacterBase:x2}.40", labels);          // UpdateStat
        Assert.DoesNotContain($"{ZoneOpcodes.ClientUpdateBase:x2}.0005", labels);     // ClientUpdateStat
        AssertNoEquipmentSlotClears(Sent(recorder));
    }

    /// <summary>
    /// docs/32 regression guard 4, re-asserted with every one of this wave's subsystems switched on:
    /// the match-zoning burst is byte-for-opcode what the two captures that reached Z2 carry. The
    /// container bootstrap, the stat burst, the doors and the vehicles all hang off later triggers
    /// precisely so that this list cannot grow.
    /// </summary>
    [Fact]
    public void MatchZoningIsUnchangedByEverythingThisWaveAdded()
    {
        byte[][] zoning = DriveToMatchZoning(out _, out _, out _, new ZoneOptions
        {
            AutoMatchMs = 1,
            SendContainers = true,
            SendMovementStats = true,
            SendDoors = true,
            SendVehicles = true,
            SendLootClusters = true,
        });

        // The world-name table now precedes the unchanged world/appearance burst.
        Assert.Equal(["fb", "0b", "ca", "17", "ce", "03", "94.01", "17", "05"], zoning.Select(Label));
        AssertNoEquipmentSlotClears(zoning);
    }

    /// <summary>
    /// The in-match <c>ClientIsReady</c> resync, in the order docs/40 and docs/41 both require: the
    /// dress first (docs/32's third regression), then the resources, then the movement stats, then
    /// the container model — and <c>InitContainers</c> before anything that could name a container
    /// guid, because <c>FUN_140d7ed90</c> clears the client's whole container list.
    /// </summary>
    [Fact]
    public void TheMatchResyncCreatesInventoryBeforePublishingFootwearMovement()
    {
        byte[][] resync = DriveToMatchZoning(
            out ZoneService service,
            out SoeConnection connection,
            out RecordingRecorder recorder,
            new ZoneOptions { AutoMatchMs = 1 },
            afterZoning: (svc, conn, rec) =>
            {
                int before = SentCount(rec);
                SendClientIsReady(svc, conn);
                return before;
            });

        string[] labels = [.. resync.Select(Label)];
        int dress = Array.IndexOf(labels, "94.01");
        int stats = Array.IndexOf(labels, $"{ZoneOpcodes.CharacterBase:x2}.40");
        int baseSpeed = Array.IndexOf(labels, $"{ZoneOpcodes.ClientUpdateBase:x2}.0005");
        int loadout = Array.IndexOf(labels, $"{ZoneOpcodes.LoadoutsBase:x2}.03");
        int containers = Array.IndexOf(labels, $"{ContainerOpcodes.ContainerBase:x2}.0002");
        int slots = Array.IndexOf(labels, $"{ZoneOpcodes.LoadoutsBase:x2}.04");

        Assert.True(dress >= 0, $"no SetCharacterEquipment in the resync: {string.Join(' ', labels)}");
        Assert.True(stats > dress, $"the stat burst must follow the character dress: {string.Join(' ', labels)}");
        Assert.True(baseSpeed > stats);
        Assert.True(loadout > dress);
        Assert.True(stats > slots);
        Assert.True(containers > loadout);
        Assert.True(slots > containers);
        AssertNoEquipmentSlotClears(resync);

        // The stat burst is the 18 entries of MovementProfile.ToStats, on the character's own guid.
        byte[] burst = resync[stats];
        Assert.Equal(
            CharacterStatPackets.UpdateStatHeaderLength
                + (MovementProfile.Default.ToStats().Count * CharacterStat.Length),
            burst.Length - 1);   // less the gateway tunnel byte

        _ = service;
        _ = connection;
        _ = recorder;
    }

    /// <summary>
    /// Landing replaces the locally possessed actor, so it must reassert the character dress even
    /// when nothing visual changed.  This is deliberately the no-active-hand-row case: the packet
    /// is byte-identical to the bootstrap dress and contains zero equipment-slot rows, which is the
    /// exact shape that the identical-dress suppressor would otherwise swallow.
    /// </summary>
    [Fact]
    public void ParachuteLandingReassertsAnIdenticalDressWithoutAnActiveHandRow()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            GiveStarterWeapon = false,
            GroundLootRadius = 0f,
            SendDoors = false,
            SendVehicles = false,
        });

        byte[] bootstrapDress = Assert.Single(Sent(recorder), packet => packet.Length > 2
            && packet[1] == ZoneOpcodes.EquipmentBase
            && packet[2] == SetCharacterEquipment.SubOpcode);

        ArmMountedParachute(connection, chuteGuid: 0x2001);
        int beforeLanding = SentCount(recorder);
        SendVehicleDismiss(service, connection);
        byte[][] landing = Sent(recorder, beforeLanding);

        int removedChute = Array.FindIndex(landing, packet => packet.Length > 1
            && packet[1] == RemovePlayer.Opcode
            && packet[2] == RemovePlayer.SubOpcode);
        int dress = Array.FindIndex(landing, packet => packet.Length > 2
            && packet[1] == ZoneOpcodes.EquipmentBase
            && packet[2] == SetCharacterEquipment.SubOpcode);

        Assert.True(removedChute >= 0, "the landing burst did not remove the possessed chute");
        Assert.True(dress > removedChute, "the landing dress must follow possession clear/removal");
        byte[] landingDress = landing[dress];
        Assert.Equal(bootstrapDress, landingDress);
        AssertNoEquipmentSlotClears(landing);

        var reader = new PacketReader(landingDress.AsSpan(1));
        Assert.Equal(ZoneOpcodes.EquipmentBase, reader.ReadByte());
        Assert.Equal(SetCharacterEquipment.SubOpcode, reader.ReadByte());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt64();
        _ = reader.ReadUInt32();
        _ = reader.ReadString();
        _ = reader.ReadString();
        Assert.Equal(0, reader.ReadInt32());       // no slot rows, especially never RHand / slot 7
    }

    /// <summary>
    /// The owner's blocker this round: "I did pick up some items but they just went into the
    /// inventory and not the correct slot". A pickup now resolves a destination by the client's own
    /// <c>LoadoutSlotItemClasses</c> rule and, when that destination is a loadout slot, sends the
    /// binding (<c>86 05</c>) and re-dresses with an equipment-slot row — which is what puts the
    /// rifle on the character rather than only in the grid.
    /// <para>
    /// Driven through the development drop in the menu, because that is the one path that spawns a
    /// known item at a known guid without a full match; the container bootstrap happens on demand at
    /// the pickup, which is the same code the match uses.
    /// </para>
    /// <para>
    /// <b>The row names body slot 76 (<c>R_LongWeapon_1</c>, stowed), never body slot 7 (RHand).</b>
    /// docs/45: a slot-7 row hard-crashes the August client while the bound weapon has no fire-group
    /// data. This assertion used to read 7 and is now the integration-level half of regression guard
    /// 5 (<c>GunPickupCrashGuardTests</c>).
    /// </para>
    /// </summary>
    [Fact]
    public void PickingUpARifleBindsItToALoadoutSlotAndStowsItOnTheBody()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = AugustHeldWeapon.ItemDefinitionId,
                GroundLootModelId = AugustHeldWeapon.GroundModelId,
                GroundLootNameId = AugustHeldWeapon.NameId,
                GroundLootRadius = 0f,          // no real Z2 roll in this test
                SendDoors = false,
                SendVehicles = false,
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");

        int beforePickup = SentCount(recorder);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);
        byte[][] pickup = Sent(recorder, beforePickup);
        string[] labels = [.. pickup.Select(Label)];

        // The on-demand bootstrap establishes the loadout table and combat manager only.  It must
        // not select a hand or re-dress it until the client actually sends 86/06.  The direct pickup
        // then adds the rifle, binds it, and issues one dress carrying its passive stow row.
        Assert.Contains($"{ZoneOpcodes.LoadoutsBase:x2}.03", labels);                 // SetCurrentLoadout
        Assert.Contains($"{ContainerOpcodes.ContainerBase:x2}.0002", labels);         // InitContainers
        int bootstrapSlots = Array.IndexOf(labels, $"{ZoneOpcodes.LoadoutsBase:x2}.04");
        int bootstrapAbilities = Array.IndexOf(labels, $"{ZoneOpcodes.AbilitiesBase:x2}");
        int add = Array.FindIndex(
            labels,
            bootstrapAbilities + 1,
            label => label == $"{ZoneOpcodes.ClientUpdateBase:x2}.0002");
        int bind = Array.FindIndex(
            labels,
            add + 1,
            label => label == $"{ZoneOpcodes.LoadoutsBase:x2}.05");
        int dress = Array.FindIndex(labels, bind + 1, label => label == "94.01");
        int update = Array.FindIndex(
            labels,
            dress + 1,
            label => label == $"{ContainerOpcodes.ContainerBase:x2}.0006");

        Assert.True(bootstrapSlots >= 0, $"no bootstrap 86/04: {string.Join(' ', labels)}");
        Assert.True(bootstrapAbilities > bootstrapSlots, $"a0/05 must follow bootstrap 86/04: {string.Join(' ', labels)}");
        Assert.DoesNotContain($"{ZoneOpcodes.LoadoutsBase:x2}.07", labels);
        Assert.DoesNotContain("94.01", labels.Take(add));
        Assert.True(add > bootstrapAbilities, $"no rifle ItemAdd after the combat HUD refresh: {string.Join(' ', labels)}");
        Assert.True(bind > add, $"the rifle binding must follow its ItemAdd: {string.Join(' ', labels)}");
        Assert.True(dress > bind, $"no re-dress after the rifle binding: {string.Join(' ', labels)}");
        Assert.True(update > dress, $"no container repaint after the rifle dress: {string.Join(' ', labels)}");
        AssertNoEquipmentSlotClears(pickup);
        Assert.DoesNotContain(AugustHeldWeapon.RightHandSlotId, WireEquipmentSlotIds(pickup));

        // The dress carries exactly one equipment-slot row: that row is the only place on the wire
        // where a slot id is bound to an inventory item guid (docs/36 §W3). It names the STOWED slot
        // 76, not the RHand slot 7 — a slot-7 row is an EXCEPTION_ACCESS_VIOLATION in the client's
        // per-frame active-hand update (docs/45), and ActiveHandRowGuard would drop it here anyway,
        // which would make this count 0.
        var reader = new PacketReader(pickup[dress].AsSpan(1));
        Assert.Equal(ZoneOpcodes.EquipmentBase, reader.ReadByte());
        Assert.Equal(SetCharacterEquipment.SubOpcode, reader.ReadByte());
        _ = reader.ReadUInt32();                       // profile id
        _ = reader.ReadUInt64();                       // character guid
        _ = reader.ReadUInt32();
        _ = reader.ReadString();                       // tint alias
        _ = reader.ReadString();                       // decal alias
        int slotCount = reader.ReadInt32();
        Assert.True(slotCount >= 1);
        bool foundRifle = false;
        for (int index = 0; index < slotCount; index++)
        {
            _ = reader.ReadUInt32();
            uint bodySlot = reader.ReadUInt32();
            ulong itemGuid = reader.ReadUInt64();
            _ = reader.ReadString();
            _ = reader.ReadString();
            if (bodySlot == AugustHeldWeapon.StowedSlotId)
            {
                foundRifle = itemGuid != 0;
            }
        }
        Assert.True(foundRifle);
        Assert.NotEqual(AugustHeldWeapon.RightHandSlotId, AugustHeldWeapon.StowedSlotId);
    }

    /// <summary>
    /// <b>WAVE 10 - the first pickup is drawn, and the ledger is what lets it through.</b>
    ///
    /// <para>
    /// With <c>WieldFirstWeapon</c> on and all four weapon stages on - the configuration
    /// <c>Program.cs</c> now ships - picking a rifle up puts an item-guid equipment row at body
    /// slot 7 (RHand) on the wire, naming the granted instance. That row is the binding the August
    /// client's per-frame active-hand update resolves (docs/45 4: "the row is the binding"), so
    /// until it exists there is nothing in the hand to fire, whatever mesh the dress attaches.
    /// </para>
    /// <para>
    /// This is the assertion that would have caught the defect this wave fixed:
    /// <c>ZoneService</c> passed <c>Clearance: null</c> to every
    /// <c>SetCharacterEquipmentWithSlots</c>, so <c>ActiveHandRowGuard</c> dropped the row even
    /// with stage 3 reported ON in the boot banner - the wire and the banner disagreed and nothing
    /// tested the wire.
    /// </para>
    /// </summary>
    [Fact]
    public void WithTheFirstPickupDrawnTheRhandRowNamesTheGrantedRifle()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = AugustHeldWeapon.ItemDefinitionId,
                GroundLootModelId = AugustHeldWeapon.GroundModelId,
                GroundLootNameId = AugustHeldWeapon.NameId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
                Inventory = new InventoryOptions { WieldFirstWeapon = true, UseWieldSequence = false },
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");

        int beforePickup = SentCount(recorder);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);
        byte[][] pickup = Sent(recorder, beforePickup);

        List<(uint SlotId, ulong ItemGuid)> rows = WireEquipmentRows(pickup);
        (uint SlotId, ulong ItemGuid) hand = Assert.Single(
            rows,
            row => row.SlotId == AugustHeldWeapon.RightHandSlotId);
        Assert.NotEqual(0ul, hand.ItemGuid);

        // The wielded rifle occupies the hand INSTEAD of its passive peg, not as well as it: two
        // rows for one guid would be the client holding and stowing the same item.
        Assert.DoesNotContain(
            rows,
            row => row.SlotId == AugustHeldWeapon.StowedSlotId && row.ItemGuid == hand.ItemGuid);
    }

    /// <summary>
    /// <b>docs/95 end to end.</b> With the sequence on, the pickup puts a <c>94 02</c> on the wire
    /// and the whole-character <c>94 01</c> carries NO body-slot-7 row — which is the exact shape
    /// harness guard G2 calls fatal, so the two halves of this wave agree.
    /// <para>
    /// The unit tests in <c>WieldSequenceTests</c> prove the bytes and the order; this one proves
    /// the wiring, which is the half that was wrong in wave 9 (a correct mechanism nothing called).
    /// </para>
    /// </summary>
    [Fact]
    public void WithTheWieldSequenceOnThePickupBindsWithNineFourOhTwoAndNotWithTheDress()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = AugustHeldWeapon.ItemDefinitionId,
                GroundLootModelId = AugustHeldWeapon.GroundModelId,
                GroundLootNameId = AugustHeldWeapon.NameId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
                Inventory = new InventoryOptions
                {
                    WieldFirstWeapon = true,
                    UseWieldSequence = true,
                },
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");

        int beforePickup = SentCount(recorder);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);
        byte[][] pickup = Sent(recorder, beforePickup);

        byte[] bind = Assert.Single(
            pickup,
            p => p.Length > 2
                && p[1] == ZoneOpcodes.EquipmentBase
                && p[2] == Cranberry.Zone.Equipment.SetCharacterEquipmentSlot.SubOpcode);

        // ... and it names body slot 7 and the granted instance, not just any slot.
        var reader = new PacketReader(bind.AsSpan(1));
        Assert.Equal(ZoneOpcodes.EquipmentBase, reader.ReadByte());
        Assert.Equal(Cranberry.Zone.Equipment.SetCharacterEquipmentSlot.SubOpcode, reader.ReadByte());
        _ = reader.ReadUInt32();                       // profile id
        _ = reader.ReadUInt64();                       // character guid
        _ = reader.ReadUInt32();                       // row key
        Assert.Equal(AugustHeldWeapon.RightHandSlotId, reader.ReadUInt32());
        Assert.NotEqual(0ul, reader.ReadUInt64());     // the bound item instance

        // The binding is LAST: no 94 01 may follow it and re-state the whole character.
        int bindIndex = Array.IndexOf(pickup, bind);
        Assert.DoesNotContain(
            pickup.Skip(bindIndex + 1),
            p => p.Length > 2
                && p[1] == ZoneOpcodes.EquipmentBase
                && p[2] == SetCharacterEquipment.SubOpcode);

        // And no dress in this pickup carries the G2 shape at all.
        Assert.DoesNotContain(AugustHeldWeapon.RightHandSlotId, WireEquipmentSlotIds(pickup));
    }

    /// <summary>
    /// The same pickup with the weapon stages off: <c>ActiveHandRowGuard</c> still refuses body
    /// slot 7, because the session delivered no fire-group data the client could resolve. Wave 10
    /// NARROWED guard 5; this is the half of it that must never move
    /// (<c>GunPickupCrashGuardTests</c> is the unit-level statement of the same rule).
    /// </summary>
    [Fact]
    public void WithTheWeaponStagesOffTheRhandRowIsStillRefusedEvenWhenTheHandIsAskedFor()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = AugustHeldWeapon.ItemDefinitionId,
                GroundLootModelId = AugustHeldWeapon.GroundModelId,
                GroundLootNameId = AugustHeldWeapon.NameId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
                Inventory = new InventoryOptions { WieldFirstWeapon = true },
                Weapons = Cranberry.Zone.Weapons.WeaponStageOptions.AllOff,
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");

        int beforePickup = SentCount(recorder);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);
        byte[][] pickup = Sent(recorder, beforePickup);

        Assert.DoesNotContain(AugustHeldWeapon.RightHandSlotId, WireEquipmentSlotIds(pickup));
    }

    /// <summary>
    /// The client selects a hotbar tile with <c>86 06</c>, not with an inventory verb.
    /// <para>
    /// <b>UPDATED BY LANE 1F (docs/102 §5).</b> This used to pin <c>Assert.Empty</c> - the handler
    /// validated the slot, wrote a log line and answered nothing, so pressing a number key did
    /// literally nothing. It now runs the SAME draw a ground pickup runs. What has <b>not</b>
    /// changed, and is what this test still exists to protect, is the one packet that must never go
    /// out: <c>86 07 SelectLoadoutSlot</c> enters a lower client routine with its c2s echo disabled,
    /// and publishing it on a draw froze the owner's mouse-look and movement for as long as the gun
    /// was in hand (S6 §7.4, live A/B 2026-08-31).
    /// </para>
    /// </summary>
    [Fact]
    public void HotbarSelectionIsObservedWithoutForcingAnUnsafeServerEcho()
    {
        const uint rifleDefinitionId = 10;
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = rifleDefinitionId,
                GroundLootModelId = AugustHeldWeapon.GroundModelId,
                GroundLootNameId = AugustHeldWeapon.NameId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
                // Deliberately stow the pickup first: this test exercises the later c2s 86/06
                // transition rather than the separate first-pickup auto-wield policy.
                Inventory = new InventoryOptions { WieldFirstWeapon = false },
                // All three weapon stages are explicitly on so the WeaponSession ledger clears
                // the active-hand row and the test covers the actual playable configuration.
                Weapons = new()
                {
                    SendWeaponDefinitions = true,
                    PopulateWeaponDefinitions = true,
                    PopulateFireGroups = true,
                    WriteWeaponItemAddTail = true,
                    AllowWielding = true,
                },
                Combat = new()
                {
                    Enabled = true,
                    SendAbilityManager = true,
                    PracticeTarget = false,
                },
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the hotbar test development drop");

        int beforePickup = SentCount(recorder);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);
        byte[][] pickup = Sent(recorder, beforePickup);

        // The direct 86/05 grant gives us both the rifle instance guid and its wheel slot.  It is
        // stowed because WieldFirstWeapon above is false, so selecting this slot is meaningful.
        byte[] binding = pickup.Single(packet => packet.Length > 2
            && packet[1] == ZoneOpcodes.LoadoutsBase
            && packet[2] == LoadoutOpcodes.SetLoadoutSlotSub);
        var bindingReader = new PacketReader(binding.AsSpan(1));
        Assert.Equal(ZoneOpcodes.LoadoutsBase, bindingReader.ReadByte());
        Assert.Equal(LoadoutOpcodes.SetLoadoutSlotSub, bindingReader.ReadByte());
        _ = bindingReader.ReadUInt64();
        Assert.Equal(SurvivorLoadout.Id, bindingReader.ReadUInt32());
        uint rifleSlotId = bindingReader.ReadUInt32();
        Assert.Equal(rifleDefinitionId, bindingReader.ReadUInt32());
        Assert.NotEqual(0ul, bindingReader.ReadUInt64());
        Assert.Equal(SurvivorLoadout.Wheel1, rifleSlotId);

        int beforeSelection = SentCount(recorder);
        SendHotbarSelection(service, connection, rifleSlotId);
        byte[][] selected = Sent(recorder, beforeSelection);

        // The draw ran: the wheel was refreshed with the new current slot.
        Assert.NotEmpty(selected);
        Assert.Contains(selected, packet => packet.Length > 2
            && packet[1] == ZoneOpcodes.LoadoutsBase
            && packet[2] == SetLoadoutSlots.SubOpcode);

        // ...and 86 07 stayed off the wire, which is the thing this test guards.
        Assert.DoesNotContain(selected, packet => packet.Length > 2
            && packet[1] == ZoneOpcodes.LoadoutsBase
            && packet[2] == LoadoutOpcodes.SelectSlotSub);
    }

    /// <summary>
    /// The rollback path has to stay a rollback: with <c>SendContainers</c> off the pickup is the
    /// wave-2 grant the owner already confirmed working — one <c>ItemAdd</c>, one
    /// <c>RemovePlayer</c>, no container or loadout traffic at all.
    /// </summary>
    [Fact]
    public void WithTheContainerModelOffThePickupIsTheWaveTwoGrant()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootRadius = 0f,
                SendContainers = false,
                SendDoors = false,
                SendVehicles = false,
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");

        int beforePickup = SentCount(recorder);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);
        string[] labels = [.. Sent(recorder, beforePickup).Select(Label)];

        Assert.Contains($"{ZoneOpcodes.ClientUpdateBase:x2}.0002", labels);   // ItemAdd
        Assert.DoesNotContain(labels, label => label.StartsWith($"{ContainerOpcodes.ContainerBase:x2}.", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, label => label.StartsWith($"{ZoneOpcodes.LoadoutsBase:x2}.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs the dev auto-match to the point where <c>EnterMatch</c> has emitted the whole zoning
    /// burst, and returns either that burst or — when <paramref name="afterZoning"/> is given —
    /// whatever the service sent after it.
    /// </summary>
    /// <summary>The client's own <c>0f 20</c>: tunnel + <c>0f 20 | u64 guid | u32 stance</c>.</summary>
    private static void SendWeaponStanceFromClient(
        ZoneService service,
        SoeConnection connection,
        ulong guid,
        uint stance)
    {
        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        new WeaponStance(guid, stance).WriteTo(packet);
        service.OnMessage(connection, packet.Written.ToArray());
    }

    private static bool IsWeaponStance(byte[] packet) =>
        packet.Length == 1 + WeaponStance.Length
        && packet[1] == WeaponStance.Opcode
        && packet[2] == WeaponStance.SubOpcode;

    /// <summary>
    /// S6 §7.3: <c>0f 20 Character.WeaponStance {self, 1}</c> goes out ONCE, immediately after the
    /// world session's first <c>a0 05 SetActivatableAbilityManager</c>, and the client's own first
    /// <c>0f 20</c> stops the server ever asserting another one.
    /// </summary>
    [Fact]
    public void TheWorldSessionAssertsWeaponStanceOnceAfterTheFirstAbilityManagerAndThenYieldsToTheClient()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(WeaponStanceOptions(), pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");

        int beforePickup = SentCount(recorder);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);
        byte[][] pickup = Sent(recorder, beforePickup);

        int manager = Array.FindIndex(pickup, packet => packet.Length > 2
            && packet[1] == ZoneOpcodes.AbilitiesBase
            && packet[2] == AbilityOpcodes.SetActivatableAbilityManagerSub);
        int stance = Array.FindIndex(pickup, IsWeaponStance);

        Assert.True(manager >= 0, "the on-demand bootstrap sent no a0 05");
        Assert.Equal(manager + 1, stance);
        Assert.Single(pickup, IsWeaponStance);

        Assert.True(WeaponStance.TryParse(pickup[stance].AsSpan(1), out WeaponStance? sent));
        Assert.Equal(WeaponStance.Initial, sent!.Stance);
        Assert.NotEqual(0ul, sent.CharacterGuid);

        // (b) of S6 §7.3, and what lane 1F made reachable: a hotbar press is a DRAW, and a draw
        // re-asserts the stance until the client has spoken. Before docs/102 §5 this selection sent
        // nothing at all, so the rule had no second site to fire at.
        int beforeSelection = SentCount(recorder);
        SendHotbarSelection(service, connection, SurvivorLoadout.Wheel1);
        Assert.Contains(Sent(recorder, beforeSelection), IsWeaponStance);

        // And the client's own 0f 20 ends every re-assert for good - including on a later draw.
        SendWeaponStanceFromClient(service, connection, sent.CharacterGuid, 2);
        int beforeSecondSelection = SentCount(recorder);
        SendHotbarSelection(service, connection, SurvivorLoadout.Wheel1);
        Assert.DoesNotContain(Sent(recorder, beforeSecondSelection), IsWeaponStance);
    }

    /// <summary><c>CRANBERRY_WEAPON_STANCE=0</c> takes the packet off the wire entirely.</summary>
    [Fact]
    public void TheWeaponStanceSwitchRemovesThePacketFromTheBootstrap()
    {
        var pending = new ConcurrentQueue<Action>();
        ZoneOptions options = WeaponStanceOptions() with
        {
            Weapons = WeaponStageOptions.Default with { SendWeaponStance = false },
        };
        var (service, connection, recorder) = Admit(options, pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);

        Assert.DoesNotContain(Sent(recorder), IsWeaponStance);
    }

    private static ZoneOptions WeaponStanceOptions() => new()
    {
        DevGroundLootMs = 1,
        DevGroundLootCount = 1,
        GroundLootItemDefinitionId = AugustHeldWeapon.ItemDefinitionId,
        GroundLootModelId = AugustHeldWeapon.GroundModelId,
        GroundLootNameId = AugustHeldWeapon.NameId,
        GroundLootRadius = 0f,
        SendDoors = false,
        SendVehicles = false,
    };

    private static byte[][] DriveToMatchZoning(
        out ZoneService service,
        out SoeConnection connection,
        out RecordingRecorder recorder,
        ZoneOptions options,
        Func<ZoneService, SoeConnection, RecordingRecorder, int>? afterZoning = null)
    {
        var pending = new ConcurrentQueue<Action>();
        (ZoneService svc, SoeConnection conn, RecordingRecorder rec) = Admit(options, pending.Enqueue);
        service = svc;
        connection = conn;
        recorder = rec;

        SendClientIsReady(svc, conn);
        int beforeZoning = SentCount(rec);

        // Other bootstrap callbacks may share this queue. Drain until the zoning burst has
        // actually run instead of assuming the next two callbacks must both be match timers.
        for (int hop = 0; svc.ForTest(conn).Step != "Zoning" && hop < 32; hop++)
        {
            Pump(pending, $"deferred match work {hop}");
        }
        Assert.Equal("Zoning", svc.ForTest(conn).Step);

        int from = afterZoning?.Invoke(svc, conn, rec) ?? beforeZoning;
        return Sent(rec, from);
    }
}
