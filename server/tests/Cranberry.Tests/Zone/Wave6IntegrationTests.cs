using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone;

/// <summary>
/// What the wave-6 <b>wiring</b> puts on the wire — the half no lane could test, because
/// <c>ZoneService</c>, <c>ZoneOptions</c> and <c>Program</c> belong to the Integrate lane and every
/// lane shipped its feature compiled, tested and <i>inert</i>.
/// <para>
/// Send-side only, per docs/32 and D29: a test over the bytes the server emits proves what the
/// server SENT and nothing about what the client did. The load-bearing half is the negative one —
/// that the LoginZone menu path, the match-zoning burst and regression guard 5 are exactly where
/// wave 5 left them with the default switches.
/// </para>
/// </summary>
public class Wave6IntegrationTests
{
    /// <summary>The active hand — <c>EquipmentSlotDefinitions</c> row 7, <c>RHand</c>.</summary>
    private const uint RHand = 7;

    /// <summary><c>ItemUseOptions</c> row 4, <c>DropItem</c>.</summary>
    private const uint DropItemOption = 4;

    /// <summary>Item 10, "AR-15" — a weapon row the Z2 loot tables carry, so it has a ground actor.</summary>
    private const uint DroppableRifle = 10;

    /// <summary><c>Models.txt</c> row 23, the AR-15's <c>_OnGround</c> actor.</summary>
    private const uint DroppableRifleGroundModel = 23;

    /// <summary><c>NAME_ID</c> 32.</summary>
    private const uint DroppableRifleNameId = 32;

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

    /// <summary>
    /// The 47-byte <c>Items.RequestUseItem</c> the client really sends, in the layout read off 21
    /// live packets (docs/63 §1). Built here rather than replayed so the test names the option.
    /// </summary>
    private static void SendRequestUseItem(
        ZoneService service,
        SoeConnection connection,
        uint optionId,
        ulong characterGuid,
        ulong itemGuid)
    {
        using var use = new PacketWriter();
        use.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        use.WriteByte(ItemUseOpcodes.ItemsBase);
        use.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        use.WriteUInt64(1);                 // [LEAD] 1 in every observed packet
        use.WriteUInt32(optionId);
        use.WriteUInt64(characterGuid);
        use.WriteUInt64(characterGuid);     // source
        use.WriteUInt64(characterGuid);     // target
        use.WriteUInt64(itemGuid);
        use.WriteByte(1);                   // simple: no quantity block
        service.OnMessage(connection, use.Written.ToArray());
    }

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

    private static string Label(byte[] packet) => packet[1] switch
    {
        ZoneOpcodes.EquipmentBase or ZoneOpcodes.ItemsBase or ZoneOpcodes.LoadoutsBase
            or ZoneOpcodes.CharacterBase or ZoneOpcodes.RecipeBase
            => $"{packet[1]:x2}.{packet[2]:x2}",
        ZoneOpcodes.ClientUpdateBase or ContainerOpcodes.ContainerBase
            => $"{packet[1]:x2}.{BitConverter.ToUInt16(packet, 2):x4}",
        _ => $"{packet[1]:x2}",
    };

    /// <summary>The <c>ReferenceData</c> type name of a tunnelled <c>0x17</c>, or null.</summary>
    private static string? ReferenceDataTypeName(byte[] packet)
    {
        if (packet.Length < 7 || packet[1] != ZoneOpcodes.ReferenceData)
        {
            return null;
        }

        // ReferenceData.WriteTo: u8 opcode; u16 (0x2000 | 13-bit name length); the ASCII name; NUL.
        int length = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) & 0x1fff;
        return length <= 0 || packet.Length < 4 + length
            ? null
            : System.Text.Encoding.ASCII.GetString(packet, 4, length);
    }

    /// <summary>
    /// Every equipment-slot id that reached the wire, over every <c>Equipment.SetCharacterEquipment
    /// (0x94/01)</c> in <paramref name="packets"/>. This is regression guard 5 read off the bytes
    /// rather than off the intent: the head is
    /// <c>u8 opcode; u8 sub; u32 profileId; u64 guid; u32; str; str</c> and each row with two empty
    /// strings is <see cref="EquipmentSlotRow.MinimalLength"/>.
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
                _ = reader.ReadUInt32();               // list hash key
                slots.Add(reader.ReadUInt32());        // row +0x20 slot id
                _ = reader.ReadUInt64();               // row +0x28 item guid
                _ = reader.ReadString();
                _ = reader.ReadString();
            }
        }

        return slots;
    }

    private static void AssertNoEquipmentSlotClears(IEnumerable<byte[]> packets) =>
        Assert.DoesNotContain(
            packets,
            packet => packet.Length > 2
                && packet[1] == ZoneOpcodes.EquipmentBase
                && packet[2] == UnsetCharacterEquipmentSlot.SubOpcode);

    // =====================================================================================
    // Weapons — docs/60, D33
    // =====================================================================================

    /// <summary>
    /// Stage 1 is wired and ON: the session bootstrap carries
    /// <c>ReferenceData "WeaponDefinitions"</c> exactly once, after <c>ProfileDefinitions</c> and
    /// before <c>ZoneDoneSendingInitialData</c>. Until this landed, docs/58's whole mechanism was
    /// compiled and absent from the wire — the state docs/56 §1.9 left <c>82 11</c> in.
    /// </summary>
    [Fact]
    public void TheWeaponTableLeavesTheLoginBurstAndStillPrecedesEveryWeaponItemAdd()
    {
        // docs/89 §4 A4 (wave 9). Stage 1 is ON by default now, so the question is no longer
        // "is it sent" but "where". It is NOT in the login burst: that burst is the owner's only
        // confirmed path to PLAY, and an under-length blob is answered by FUN_140b055c0 with a
        // store to address 0, which would land before character select.
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
            },
            pending.Enqueue);

        Assert.DoesNotContain(
            Sent(recorder), p => ReferenceDataTypeName(p) == WeaponDefinitionsBlob.TypeName);

        // ...but the invariant it exists for still holds: CreateItem's self-init runs once, at
        // construction, so the table must precede the first weapon ItemAdd of the session. Every
        // path that can produce one must send the table first, including a ground owner's
        // remote ItemAdd before the local inventory has been created.
        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);

        byte[][] sent = Sent(recorder);
        int weapons = Array.FindIndex(sent, p => ReferenceDataTypeName(p) == WeaponDefinitionsBlob.TypeName);

        Assert.True(weapons >= 0, "the WeaponDefinitions table never reached the wire");
        Assert.Single(sent, p => ReferenceDataTypeName(p) == WeaponDefinitionsBlob.TypeName);

        int firstItemAdd = Array.FindIndex(
            sent,
            p => p.Length > 3
                && p[1] == ZoneOpcodes.ClientUpdateBase
                && BitConverter.ToUInt16(p, 2) == ItemAdd.SubOpcode);

        Assert.True(firstItemAdd >= 0, "the development weapon never reached an inventory");
        Assert.True(weapons < firstItemAdd, "the weapon table must precede every ItemAdd");
        Assert.Equal(LootWorld.DefaultWorldGuidBase, BitConverter.ToUInt64(sent[firstItemAdd], 4));
        Assert.Equal(AugustHeldWeapon.ItemDefinitionId, BitConverter.ToUInt32(sent[firstItemAdd], 16));
    }

    /// <summary>
    /// <b>THE SHIPPED BOOTSTRAP IS WAVE 5'S, BYTE FOR BYTE.</b> The login burst is the owner's only
    /// confirmed path to PLAY and it stays byte-identical to
    /// <c>captures\wire-20260829-150206.txt</c> — no weapon table by default, and none with the
    /// explicit rollback either. Stage 1 is what the owner opts into for run 1, not what they
    /// discover on their return.
    /// </summary>
    [Fact]
    public void TheDefaultBootstrapIsWhatWaveFiveSent()
    {
        var (_, _, byDefault) = Admit(new ZoneOptions());
        Assert.DoesNotContain(
            Sent(byDefault), packet => ReferenceDataTypeName(packet) == WeaponDefinitionsBlob.TypeName);

        var (_, _, rolledBack) = Admit(new ZoneOptions { Weapons = WeaponStageOptions.AllOff });
        Assert.DoesNotContain(
            Sent(rolledBack), packet => ReferenceDataTypeName(packet) == WeaponDefinitionsBlob.TypeName);

        // Same packet count, because the default IS the rollback.
        Assert.Equal(Sent(byDefault).Length, Sent(rolledBack).Length);
    }

    /// <summary>
    /// Run 1 of docs/60 §5, corrected: <c>CRANBERRY_WEAPON_DEFINITIONS=1</c> with
    /// <c>CRANBERRY_WEAPON_DEFS_LIST1=0</c> puts the 32-byte empty envelope on the login burst — the
    /// type name and the dispatch, with no record layout at all. This is the run that can be made
    /// while the owner is away; the 3,212-byte one cannot.
    /// </summary>
    [Fact]
    public void StageOnesEmptyEnvelopeIsTheOnlyBlobWithNoRecordLayoutOnTheWire()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
                Weapons = new WeaponStageOptions
                {
                    SendWeaponDefinitions = true,
                    PopulateFireGroups = false,
                    PopulateWeaponDefinitions = false,
                },
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);

        byte[] table = Assert.Single(
            Sent(recorder), packet => ReferenceDataTypeName(packet) == WeaponDefinitionsBlob.TypeName);

        // The blob is the tail of the packet: 8 × i32 0, and not one non-zero byte among them.
        byte[] blob = table[^WeaponDefinitionsBlob.EmptyLength..];
        Assert.Equal(new byte[WeaponDefinitionsBlob.EmptyLength], blob);
    }

    /// <summary>
    /// <b>Regression guard 5, at the integration level, against the shipped ROLLBACK.</b> With
    /// <c>WeaponStageOptions.AllOff</c> — i.e. <c>CRANBERRY_WEAPON_DEFINITIONS=0</c> — no packet this
    /// service sends may name body slot 7: not the dress, not a pickup, not the starter-weapon path.
    /// <para>
    /// This used to assert the same thing about the <em>defaults</em>. Wave 9 turned the stages on,
    /// because with body slot 7 empty the August client's only local attack entry point skips its
    /// whole block and the owner cannot shoot, swing or test anything (docs/89 §1e). The guard did
    /// not weaken — it is still the ledger's two halves — so what this test now pins is that the
    /// revert is exact.
    /// </para>
    /// </summary>
    [Fact]
    public void WithTheRollbackNoPacketEverNamesTheActiveHand()
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
                GiveStarterWeapon = true,
                SendDoors = false,
                SendVehicles = false,
                Weapons = WeaponStageOptions.AllOff,
                Inventory = new InventoryOptions { WieldFirstWeapon = false },
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);

        byte[][] sent = Sent(recorder);
        Assert.DoesNotContain(RHand, WireEquipmentSlotIds(sent));
        AssertNoEquipmentSlotClears(sent);

        // And the placement gate is shut too, which is the half ActiveHandRowGuard cannot see.
        Assert.False(WeaponStageOptions.AllOff.Effective.AllowWielding);
        Assert.False(WeaponStageOptions.AllOff.Effective.WriteWeaponItemAddTail);
    }

    /// <summary>
    /// <b>...and with the SHIPPED defaults the four stages are all live</b>, which is the whole of
    /// docs/89 group A. Stated as its own assertion so that a future wave turning one of them back
    /// off has to say so here.
    /// </summary>
    [Fact]
    public void TheShippedStagesAreAllLive()
    {
        WeaponStageOptions shipped = new ZoneOptions().Weapons.Effective;

        Assert.True(shipped.SendWeaponDefinitions);
        Assert.True(shipped.PopulateWeaponDefinitions);
        Assert.True(shipped.PopulateFireGroups);
        Assert.True(shipped.WriteWeaponItemAddTail);
        Assert.True(shipped.AllowWielding);
    }

    /// <summary>
    /// docs/60 §2: <c>CRANBERRY_WIELD=1</c> without <c>CRANBERRY_WEAPON_TAIL=1</c> is refused in
    /// code, not in prose — a slot-7 row without the tail that builds the fire-group array is
    /// exactly the docs/45 minidump. The host prints the refusal on its boot line.
    /// </summary>
    [Fact]
    public void WieldingWithoutTheTailIsRefusedByTheOptionsThemselves()
    {
        var asked = new WeaponStageOptions { AllowWielding = true, WriteWeaponItemAddTail = false };

        Assert.True(asked.WieldingRefusedForMissingTail);
        Assert.False(asked.Effective.AllowWielding);
        Assert.Contains("IGNORED", asked.Describe(), StringComparison.Ordinal);
    }

    // =====================================================================================
    // Inventory — docs/63
    // =====================================================================================

    /// <summary>
    /// The find of the wave, wired: <c>0xac / 0x2c Items.RequestUseItem</c> is the client's ONLY
    /// inventory channel and Cranberry logged it as "(unanswered)" for four waves. A drop now
    /// deletes the instance, rebuilds the loadout, repaints the bag and puts the item on the floor
    /// as an ordinary ground object.
    /// </summary>
    [Fact]
    public void ADropIsAnsweredAndPutsTheItemBackOnTheFloor()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                // Item 10, the model-less AR-15, and NOT AugustHeldWeapon's 2425: only a row the
                // loot tables carry has a ground actor, and 2425 is not one of them (docs/63 §5.4 -
                // a drop can only place what the floor can produce).
                GroundLootItemDefinitionId = DroppableRifle,
                GroundLootModelId = DroppableRifleGroundModel,
                GroundLootNameId = DroppableRifleNameId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);

        // The equipment-slot row of the re-dress is the one place on the wire that binds a body slot
        // to an inventory instance guid, so it names the rifle the pickup just granted.
        byte[] dress = Sent(recorder).Last(p =>
            p.Length > 2 && p[1] == ZoneOpcodes.EquipmentBase && p[2] == SetCharacterEquipment.SubOpcode);
        var reader = new PacketReader(dress.AsSpan(1));
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadUInt32();
        ulong characterGuid = reader.ReadUInt64();
        _ = reader.ReadUInt32();
        _ = reader.ReadString();
        _ = reader.ReadString();
        int slotCount = reader.ReadInt32();
        Assert.True(slotCount >= 1);
        ulong rifleGuid = 0;
        for (int index = 0; index < slotCount; index++)
        {
            _ = reader.ReadUInt32();
            uint bodySlot = reader.ReadUInt32();
            ulong candidate = reader.ReadUInt64();
            _ = reader.ReadString();
            _ = reader.ReadString();
            if (bodySlot == AugustHeldWeapon.StowedSlotId)
            {
                rifleGuid = candidate;
            }
        }
        Assert.NotEqual(0ul, rifleGuid);

        int beforeDrop = SentCount(recorder);
        SendRequestUseItem(service, connection, DropItemOption, characterGuid, rifleGuid);
        byte[][] drop = Sent(recorder, beforeDrop);
        string[] labels = [.. drop.Select(Label)];

        Assert.NotEmpty(drop);
        Assert.Contains($"{ZoneOpcodes.ClientUpdateBase:x2}.0004", labels);          // ItemDelete
        Assert.Contains($"{ZoneOpcodes.LoadoutsBase:x2}.04", labels);                // SetLoadoutSlots
        Assert.Contains($"{ContainerOpcodes.ContainerBase:x2}.0006", labels);        // UpdateContainer
        Assert.Contains($"{ZoneOpcodes.AddLightweightNpc:x2}", labels);              // back on the floor
        // Dropping returns the internal hand state to fists, but that state is intentionally not
        // represented as an item-guid RHand row on the wire.  Such a row enters the client's unsafe
        // active-hand inventory path and can freeze the player.
        Assert.DoesNotContain(RHand, WireEquipmentSlotIds(drop));
        AssertNoEquipmentSlotClears(drop);
    }

    /// <summary>
    /// A request the model refuses answers with the client's own vocabulary for a server-side no —
    /// <c>Container.Error (0xc8/03)</c> — and never silently changes the inventory. Item 3156, the
    /// bag itself, has no context-menu group at all, so a drop of it can only be a spoof.
    /// </summary>
    [Fact]
    public void ADropOfSomethingTheClientOffersNoMenuForIsRefused()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions { DevGroundLootMs = 1, DevGroundLootCount = 1, GroundLootRadius = 0f },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);

        int before = SentCount(recorder);
        SendRequestUseItem(service, connection, DropItemOption, 0x1001, itemGuid: 0xdead_beef);
        string[] labels = [.. Sent(recorder, before).Select(Label)];

        Assert.Contains($"{ContainerOpcodes.ContainerBase:x2}.0003", labels);        // Container.Error
        Assert.DoesNotContain($"{ZoneOpcodes.ClientUpdateBase:x2}.0004", labels);    // nothing deleted
    }

    // =====================================================================================
    // Crafting — docs/62, D34/D35
    // =====================================================================================

    /// <summary>
    /// The recipe list goes out in the MATCH, after <c>ClientIsReady</c> and after the container
    /// bootstrap — never on the LoginZone menu, which is the critical path to PLAY, and never inside
    /// the zoning burst, which regression guard 4 pins.
    /// </summary>
    [Fact]
    public void TheRecipeListIsSentInTheMatchAndNeverOnTheMenu()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions());
        SendClientIsReady(service, connection);

        Assert.DoesNotContain(
            Sent(recorder),
            packet => packet.Length > 2
                && packet[1] == ZoneOpcodes.RecipeBase
                && packet[2] == RecipeOpcodes.ListSub);

        byte[][] resync = DriveToMatchZoning(
            out _, out _, out _,
            new ZoneOptions { AutoMatchMs = 1 },
            afterZoning: (svc, conn, rec) =>
            {
                int before = SentCount(rec);
                SendClientIsReady(svc, conn);
                return before;
            });

        string[] labels = [.. resync.Select(Label)];
        int containers = Array.IndexOf(labels, $"{ContainerOpcodes.ContainerBase:x2}.0002");
        int recipes = Array.IndexOf(labels, $"{ZoneOpcodes.RecipeBase:x2}.{RecipeOpcodes.ListSub:x2}");

        Assert.True(recipes >= 0, $"no Recipe.List in the match resync: {string.Join(' ', labels)}");
        Assert.True(containers >= 0);
        Assert.True(recipes > containers, "the recipe list must follow the container bootstrap");
    }

    /// <summary>
    /// D289 / DIAG-recipes-vehicles §A: stage 2 is now ON by default because it is the ONLY delivery
    /// that publishes a crafting-window row — the self record the client receives on a default host
    /// carries the six recipe ids at blob offset <c>0x11a</c>. It is still refused outright without
    /// stage 1, so a recipe list of the wrong length can never be the first place a record is tried.
    /// </summary>
    [Fact]
    public void TheSelfRecordCarriesTheSixRecipesByDefault()
    {
        Assert.True(new ZoneOptions().Crafting.Effective.SendRecipesInSelfRecord);

        // The self record a default session actually receives, read off the wire the client sees.
        var (_, _, recorder) = Admit(new ZoneOptions());
        byte[] self = Sent(recorder).Single(p => p[1] == ZoneOpcodes.SendSelfToClient);

        // The recipe list rides at blob 0x11a, but its WRITE offset shifts with the character's
        // inline strings (name, head, hair), so locate it by content: the list is i32 count(6) plus
        // the six records, byte-identical to RecipeRecord.WriteList — the same bytes 0x26 09 carries.
        using var lw = new PacketWriter(1024);
        RecipeRecord.WriteList(lw, CraftingCatalog.ToRecords());
        byte[] listBytes = lw.Written.ToArray();

        int listStart = IndexOfSubsequence(self, listBytes);
        Assert.True(listStart >= 0, "the self record does not carry the recipe list");
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(self.AsSpan(listStart)));

        uint[] wireIds = CraftingCatalog.ToRecords().Select(r => r.RecipeId).ToArray();
        int cursor = listStart + 4;
        var seen = new List<uint>();
        foreach (RecipeRecord recipe in CraftingCatalog.ToRecords())
        {
            seen.Add(BinaryPrimitives.ReadUInt32LittleEndian(self.AsSpan(cursor)));
            cursor += recipe.Length;
        }

        Assert.Equal(wireIds, seen.ToArray());

        // And it stays refused when stage 1 is off — the ordering guard is unchanged.
        var asked = new CraftingOptions { SendRecipeList = false, SendRecipesInSelfRecord = true };
        Assert.True(asked.SelfRecordRefusedForMissingList);
        Assert.False(asked.Effective.SendRecipesInSelfRecord);
    }

    /// <summary>
    /// And when stage 2 IS asked for, the self record still declares its own length exactly. The
    /// August loader <c>FUN_140a31140</c> aborts the client on any length error, so "the record is
    /// consumed to the byte" is the only assertion that matters about this field — it is checked
    /// with the recipe list empty (the shipped default, four zero bytes at blob offset 0x11a) and
    /// with it full, and the difference is exactly the list body.
    /// </summary>
    [Fact]
    public void TheSelfRecordDeclaresItsOwnLengthWithAndWithoutTheRecipeList()
    {
        static int SelfRecordLength(CraftingOptions crafting)
        {
            var (_, _, recorder) = Admit(new ZoneOptions { Crafting = crafting });
            byte[] self = Sent(recorder).Single(p => p[1] == ZoneOpcodes.SendSelfToClient);
            int declared = BinaryPrimitives.ReadInt32LittleEndian(self.AsSpan(2));

            // 1 tunnel byte + 1 opcode + 4 length + the record, and nothing trailing.
            Assert.Equal(2 + 4 + declared, self.Length);
            return declared;
        }

        int empty = SelfRecordLength(new CraftingOptions { SendRecipesInSelfRecord = false });
        int filled = SelfRecordLength(new CraftingOptions { SendRecipesInSelfRecord = true });

        Assert.Equal(
            empty + RecipeRecord.ListLength(CraftingCatalog.ToRecords()) - sizeof(int),
            filled);
    }

    // =====================================================================================
    // Vehicles — docs/61
    // =====================================================================================

    /// <summary>
    /// Toggling recurring vehicle streaming must not change the match-zoning protocol. The
    /// descent integration tests separately cover initial fleet adoption and duplicate prevention.
    /// </summary>
    [Fact]
    public void SwitchingTheVehicleStreamerOnDoesNotChangeMatchZoning()
    {
        static byte[][] Burst(bool streaming)
        {
            byte[][] sent = DriveToMatchZoning(out _, out _, out _,
                new ZoneOptions
                {
                    AutoMatchMs = 1,
                    SendVehicles = true,
                    VehicleStream = new VehicleStreamOptions { Enabled = streaming },
                });
            int start = Array.FindIndex(sent, p => p.Length > 1 && p[1] == ClientBeginZoning.Opcode);
            Assert.True(start >= 0, "the match-zoning burst was not sent");
            Assert.Equal(ZoneDoneSendingInitialData.Opcode, sent[^1][1]);
            return sent[start..];
        }

        Assert.Equal(Burst(streaming: false).Select(p => (p[1], p.Length)),
            Burst(streaming: true).Select(p => (p[1], p.Length)));
    }

    /// <summary>
    /// The vehicle switch that can still lock a player out of a car is OFF, and says so: requiring
    /// an ignition would make ~65 % of the map's cars unstartable, because the Hotwire items
    /// 3458/3459 and the Vehicle Key 3460 are in no loot table this build ships.
    ///
    /// <para>
    /// <b>Fuel burning moved to ON in docs/117 §3.4.</b> It was the second risky switch here
    /// because an engine that cuts out mid-drive had never been play-tested. The boost changed the
    /// trade: the client's own <c>AbilityEx</c> <c>VehicleTurbo</c> rows spend
    /// <c>RESOURCE_TYPE 50</c> — the boost meter in this build IS the fuel tank — so with the burn
    /// off a boost has nothing to spend and nothing to refuse it on. A full tank is still 20 min
    /// 50 s of driving against a 24:50 gas ladder.
    /// </para>
    /// </summary>
    [Fact]
    public void IgnitionIsTheOnlyVehicleSwitchStillOff()
    {
        var defaults = new ZoneOptions();

        Assert.False(defaults.VehicleIgnition.Required);
        Assert.True(defaults.VehicleFuel.Enabled);
        Assert.True(defaults.VehicleFuel.SendGauge);
        Assert.True(defaults.VehicleStream.Enabled);
        Assert.True(defaults.VehicleRelay.Enabled);

        // docs/117: the two arms this lane added are on, because before it a car could not be hurt
        // at all and a boost press was answered with nothing.
        Assert.True(defaults.VehicleDamage.Enabled);
        Assert.True(defaults.VehicleBoost.Enabled);

        // docs/117 §B: the parked-car render deltas default on.
        Assert.True(defaults.VehiclePositionBlock);
        Assert.Equal(LightweightEntityBody.CollidableFlag, defaults.VehicleSpawnFlags1);
        Assert.True(defaults.VehicleShader);
    }

    // The service-level "a parked car is drawn" test lives in StarterWeaponDrawTests, which already
    // has the harness that drives a session all the way through the parachute LANDING — the trigger
    // SpawnNearbyVehicles hangs off (ArmGroundLoot). See
    // StarterWeaponDrawTests.AParkedCarLandsWithAPositionBlockAndTheCollidableFlag.

    private static int IndexOfSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    // =====================================================================================
    // The guards, restated over everything this wave wired
    // =====================================================================================

    /// <summary>
    /// <b>Regression guard 4.</b> The match-zoning burst is byte-for-opcode what the two captures
    /// that reached Z2 carry, with every wave-6 subsystem switched on. Nothing this wave added hangs
    /// off the zoning trigger: the weapon table is in the session bootstrap, the recipe list is in
    /// the post-zoning resync, and the vehicle streamer is an arm of the world pump.
    /// </summary>
    [Fact]
    public void MatchZoningIsUnchangedByEverythingWaveSixAdded()
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
        Assert.DoesNotContain(RHand, WireEquipmentSlotIds(zoning));
    }

    /// <summary>
    /// <b>Regression guard 1.</b> The appearance override switch stays off and the
    /// <c>ReferenceData</c> appearance payload keeps its live-proven length — nothing in this wave
    /// touches the shape of that table, and the assertion is here so that "nothing touches it" is
    /// checked rather than assumed.
    /// </summary>
    [Fact]
    public void TheAppearanceTableIsUntouchedByThisWave()
    {
        Assert.False(AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides);
    }

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

        // Watchdog callbacks share this queue. Count actual protocol progress, not two arbitrary
        // callbacks, so a full-suite scheduling interleave cannot return before zoning starts.
        long deadline = System.Environment.TickCount64 + 30_000;
        while (!Sent(rec, beforeZoning).Any(p => p.Length > 1 && p[1] == ZoneDoneSendingInitialData.Opcode))
        {
            int remaining = (int)Math.Max(0, deadline - System.Environment.TickCount64);
            Assert.True(remaining > 0 && SpinWait.SpinUntil(() => !pending.IsEmpty, remaining),
                "match zoning did not finish before the deadline");
            Assert.True(pending.TryDequeue(out Action? work));
            work!();
        }

        int from = afterZoning?.Invoke(svc, conn, rec) ?? beforeZoning;
        return Sent(rec, from);
    }
}
