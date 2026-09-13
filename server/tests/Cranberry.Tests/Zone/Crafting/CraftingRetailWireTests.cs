using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Crafting;

/// <summary>
/// <b>docs/116 on the wire</b> — the retail recipe list byte for byte, the three packets the audit
/// found missing, and the three end-to-end sessions the owner is going to click:
/// <i>shred a shirt, craft a bandage, drop it.</i>
/// <para>
/// <b>D29, as always.</b> These are send-side assertions. They prove what this server EMITTED and
/// nothing about what the client did with it — except for one, which is stronger than that:
/// <see cref="TheRecipeListIsTheCapturedPacketByteForByte"/> compares Cranberry's output with
/// <b>936 bytes a real retail server actually sent to a real August client</b>, so a pass there is
/// parity evidence rather than self-consistency.
/// </para>
/// </summary>
public sealed class CraftingRetailWireTests
{
    [Fact]
    public void CraftingMakeshiftArmorKeepsLaminatedArmourEquippedOnTheWire()
    {
        var (service, connection, recorder, pending, _, _) = HoldingAShirt();
        var inventory = InventoryOf(connection);
        inventory.TryPickUp(2112, 1, out _);
        inventory.TryPickUp(2271, 1, out var laminated);
        Assert.NotNull(laminated);
        var recipe = CraftingCatalog.RetailRecipes.Single(r => r.OutputItemDefinitionId == CraftingCatalog.MakeshiftArmor);
        foreach (var ingredient in recipe.Ingredients)
        {
            inventory.TryPickUp(ingredient.ItemDefinitionId, ingredient.Quantity, out var item);
            Assert.NotNull(item);
        }
        int before = SentCount(recorder);

        SendRecipeStart(service, connection, CraftingCatalog.MakeshiftArmor, 1);
        PumpUntil(pending, recorder, before, IsInteractionStop);

        Assert.Same(laminated, inventory.LoadoutSlots[SurvivorLoadout.ChestArmor]);
        Assert.Same(laminated, inventory.EquipmentSlots[BodySlots.ChestArmor]);
        var crafted = Assert.Single(inventory.BaseBag!.Slots.Values, i => i.DefinitionId == CraftingCatalog.MakeshiftArmor);
        Assert.Equal(0u, crafted.LoadoutSlotId);
        using var expectedGrant = new PacketWriter();
        new ItemAdd(inventory.CharacterGuid, crafted.ToRecord(inventory.CharacterGuid)).WriteTo(expectedGrant);
        byte[] expectedGrantBytes = expectedGrant.Written.ToArray();
        Assert.Contains(Sent(recorder, before), p => IsItemAdd(p) && p.AsSpan(1).SequenceEqual(expectedGrantBytes));
        var dress = Assert.Single(Sent(recorder, before), p => p.Length > 2 && p[1] == 0x94 && p[2] == 1);
        Assert.DoesNotContain("_Armor_Homemade.adr", System.Text.Encoding.UTF8.GetString(dress));
    }

    [Fact]
    public void CraftingMakeshiftArmorPublishesItsEquippedCharacterMesh()
    {
        var (service, connection, recorder, pending, _, _) = HoldingAShirt();
        var inventory = InventoryOf(connection);
        inventory.TryPickUp(2112, 1, out _);
        var recipe = CraftingCatalog.RetailRecipes.Single(r => r.OutputItemDefinitionId == CraftingCatalog.MakeshiftArmor);
        foreach (var ingredient in recipe.Ingredients)
        {
            var placed = inventory.TryPickUp(ingredient.ItemDefinitionId, ingredient.Quantity, out var item);
            Assert.NotEqual(InventoryPlacementKind.Refused, placed.Kind);
            Assert.NotNull(item);
        }
        int before = SentCount(recorder);
        SendRecipeStart(service, connection, CraftingCatalog.MakeshiftArmor, 1);
        PumpUntil(pending, recorder, before, IsInteractionStop);

        Assert.Equal(CraftingCatalog.MakeshiftArmor, inventory.EquipmentSlots[100].DefinitionId);
        var dress = Assert.Single(Sent(recorder, before), p => p.Length > 2 && p[1] == 0x94 && p[2] == 1);
        Assert.Contains("_Armor_Homemade.adr", System.Text.Encoding.UTF8.GetString(dress));
    }

    /// <summary>
    /// The friend server's <c>26 09 Recipe.List</c>, 936 bytes, from the owner's 2026-08-22 admin
    /// capture. Both occurrences in the capture are byte-identical:
    /// <c>C:\Project\out\ingest-admin-20260822-part1\packets_1118_62892.log:144</c> (+27.097 s,
    /// session 1118:62892) and <c>packets_1119_53544.log:1684</c> (+107.934 s, session 1119:53544).
    /// </summary>
    private const string CapturedRecipeList =
        "2609060000004d080000940400002a010000000000009504000001000000010000000600000000000000000100000017"
        + "00000031000000160000000000000003040000f33d0000060000000000000000000000ffffffff170000004d0800009a"
        + "0500009a0400007e020000000000007c0200000100000000000000050000000000000000040000000e0000005a040000"
        + "2f02000000000000573d0000f43d0000010000000000000000000000ffffffff0e000000700000007802000030000000"
        + "00000000c4040000f43d0000050000000000000000000000ffffffff70000000860000008c0200005700000000000000"
        + "8d020000f43d0000010000000000000000000000ffffffff86000000e7050000aa050000d8000000000000009c230000"
        + "f43d0000050000000000000000000000ffffffffe70500009a050000320d0000bb360000ad05000000000000bd360000"
        + "010000000100000002000000000000000003000000860000008c02000057000000000000008d020000f43d0000010000"
        + "000000000000000000ffffffff86000000ab0d0000b73700001d00000000000000b8370000f53d000002000000000000"
        + "0000000000ffffffffab0d0000ac0d0000b9370000a600000000000000ba370000f63d00000400000000000000000000"
        + "00ffffffffac0d0000320d00008a000000882f00007f0200000000000095020000010000000500000004000000000000"
        + "000004000000410000005d0000003202000000000000a62e0000f43d0000010000000000000000000000ffffffff4100"
        + "000070000000780200003000000000000000c4040000f43d0000050000000000000000000000ffffffff700000008600"
        + "00008c02000057000000000000008d020000f43d0000010000000000000000000000ffffffff86000000e7050000aa05"
        + "0000d8000000000000009c230000f43d00000c0000000000000000000000ffffffffe70500008a0000002f0d0000b436"
        + "00009404000000000000c0360000010000000100000003000000000000000002000000770900000e3000001500000000"
        + "0000000b300000f73d00000a0000000000000000000000ffffffff77090000780900000d300000ab000000000000000c"
        + "300000f43d0000010000000000000000000000ffffffff780900002f0d0000770900000e30000015000000000000000b"
        + "3000000100000001000000010000000000000000010000001700000031000000160000000000000003040000f33d0000"
        + "020000000000000000000000ffffffff1700000077090000";

    /// <summary>The admitted character guid, which is also every lead guid a right-click carries.</summary>
    private const ulong Self = 0x1001;

    /// <summary>Item 2144, the owner's own starter shirt. <c>ITEM_CLASS</c> 25002.</summary>
    private const uint Shirt = 2144;

    /// <summary><c>Models.txt</c> row 9249, <c>Common_Props_Clothes_FoldedShirt.adr</c>.</summary>
    private const uint FoldedShirtModel = 9249;

    /// <summary>Its <c>NAME_ID</c>.</summary>
    private const uint ShirtNameId = 8947;

    private const uint DropItemOption = 4;
    private const uint SalvageItemOption = 6;

    private static byte[] CapturedBytes() => Convert.FromHexString(CapturedRecipeList);

    // =============================================================================== the writers

    /// <summary>
    /// <b>G1, and the whole point of the lane.</b> Cranberry's <c>26 09 Recipe.List</c> must be the
    /// friend server's 936 bytes, exactly. Compared field by field first — so a failure names the
    /// field rather than an offset — and then byte for byte.
    /// </summary>
    [Fact]
    public void TheRecipeListIsTheCapturedPacketByteForByte()
    {
        byte[] captured = CapturedBytes();
        Assert.Equal(936, captured.Length);

        using var writer = new PacketWriter();
        new RecipeList(CraftingCatalog.ToRecords()).WriteTo(writer);
        byte[] ours = writer.Written.ToArray();

        // 1. field by field, through the docs/62 layout, so a mismatch is readable.
        IReadOnlyList<DecodedRecipe> theirs = Decode(captured);
        IReadOnlyList<DecodedRecipe> mine = Decode(ours);
        Assert.Equal(theirs.Count, mine.Count);
        for (int index = 0; index < theirs.Count; index++)
        {
            Assert.Equal(theirs[index], mine[index]);
        }

        // 2. and then the bytes, which is the assertion that cannot be satisfied by a shared bug in
        //    the decoder above.
        Assert.Equal(Convert.ToHexString(captured), Convert.ToHexString(ours));
    }

    /// <summary>
    /// The <c>CRANBERRY_CRAFT_RECIPES=0</c> revert still produces the wave-6 packet: four recipes,
    /// eight components, 538 bytes, every display field zero. A revert that changed the old bytes
    /// would not be a revert.
    /// </summary>
    [Fact]
    public void TheLegacySwitchStillProducesTheWaveSixPacket()
    {
        using var writer = new PacketWriter();
        new RecipeList(CraftingCatalog.ToRecords(retailRecipes: false, sentinelFields: false))
            .WriteTo(writer);
        byte[] legacy = writer.Written.ToArray();

        Assert.Equal(2 + 4 + (4 * RecipeRecord.FixedLength) + (8 * RecipeComponentRecord.Length),
            legacy.Length);
        Assert.Equal(538, legacy.Length);

        foreach (DecodedRecipe recipe in Decode(legacy))
        {
            Assert.Equal(0u, recipe.ImageSetId);
            Assert.Equal(0u, recipe.DescriptionStringId);
            Assert.Equal(0u, recipe.Reserved);
            Assert.All(recipe.Components, c => Assert.Equal(0u, c.RecipeType));
        }
    }

    /// <summary>
    /// <c>cf 02 InteractionStart</c>, unchanged: the three fields the capture proves sit at +10,
    /// +38 and +42 (D117/D184, now [P] rather than [I]).
    /// </summary>
    [Fact]
    public void TheCastBarCarriesTheProvenThreeFields()
    {
        using var writer = new PacketWriter();
        new InteractionStart(Self, 1000, ShirtNameId, 10).WriteTo(writer);
        byte[] bar = writer.Written.ToArray();

        Assert.Equal(InteractionStart.Length, bar.Length);
        Assert.Equal(ZoneOpcodes.CharacterStateBase, bar[0]);
        Assert.Equal(0x02, bar[1]);
        Assert.Equal(Self, BinaryPrimitives.ReadUInt64LittleEndian(bar.AsSpan(2)));
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(10)));
        Assert.Equal(ShirtNameId, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(38)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(42)));
    }

    /// <summary>
    /// <c>cf 03 InteractionStop</c> — ten bytes and nothing else, matching every one of the six
    /// captured instances once the family base is shifted from <c>0xd0</c> to <c>0xcf</c>.
    /// </summary>
    [Fact]
    public void TheInteractionStopIsTenBytesOfHeaderAndGuid()
    {
        using var writer = new PacketWriter();
        new InteractionStop(0x8f481b32234b0d4cUL).WriteTo(writer);
        byte[] stop = writer.Written.ToArray();

        Assert.Equal(InteractionStop.Length, stop.Length);
        Assert.Equal(10, stop.Length);

        // The captured d0 03, with the August base substituted for the 1087 one.
        byte[] captured = Convert.FromHexString("d0034c0d4b23321b488f");
        captured[0] = ZoneOpcodes.CharacterStateBase;
        Assert.Equal(Convert.ToHexString(captured), Convert.ToHexString(stop));
    }

    /// <summary>
    /// <c>0f 4a DroppedItemNotification</c> — eighteen bytes, and identical to the captured
    /// instance at <c>packets_1119_53544.log:5288</c> for the same guid, item and count.
    /// </summary>
    [Fact]
    public void TheDropNotificationIsTheCapturedEighteenBytes()
    {
        using var writer = new PacketWriter();
        new DroppedItemNotification(0x8f481b32234b0d4cUL, 2109, 1).WriteTo(writer);
        byte[] toast = writer.Written.ToArray();

        Assert.Equal(DroppedItemNotification.Length, toast.Length);
        Assert.Equal(
            Convert.ToHexString(Convert.FromHexString("0f4a4c0d4b23321b488f3d08000001000000")),
            Convert.ToHexString(toast));

        // The second captured drop differs only in the definition id.
        using var second = new PacketWriter();
        new DroppedItemNotification(0x8f481b32234b0d4cUL, 3529, 1).WriteTo(second);
        Assert.Equal(
            Convert.ToHexString(Convert.FromHexString("0f4a4c0d4b23321b488fc90d000001000000")),
            Convert.ToHexString(second.Written.ToArray()));
    }

    /// <summary>
    /// <b>The Euler-vs-quaternion question, settled for this field.</b> The captured drop's own
    /// <c>d7</c> carries the four floats <c>(0, -3.0808, 0, 1)</c> at <c>+0xa0</c>: a yaw in
    /// component <b>Y</b> with <c>w</c> left at 1, which is not a normalised quaternion. This pins
    /// that Cranberry writes the same shape, and that the default is still the identity - which is
    /// the identity in <em>both</em> readings and is what the world-seeded markers keep.
    /// </summary>
    [Fact]
    public void TheDroppedObjectCarriesTheCapturedYawInComponentY()
    {
        // 232d45c0 = -3.0808799 rad, the first captured drop's yaw.
        float yaw = BitConverter.ToSingle(Convert.FromHexString("232d45c0"));
        var position = new System.Numerics.Vector3(-225.26f, 506.47f, -4951.11f);

        using var yawed = new PacketWriter();
        new AddLightweightItem(
            0x5a4ac9db6a0f39a9UL, 1, 0x2608, position, NameId: 0,
            Rotation: new System.Numerics.Vector4(0f, yaw, 0f, 1f)).WriteTo(yawed);
        string body = Convert.ToHexString(yawed.Written.ToArray());

        Assert.Contains("00000000232D45C0000000000000803F", body, StringComparison.Ordinal);

        using var identity = new PacketWriter();
        new AddLightweightItem(0x5a4ac9db6a0f39a9UL, 1, 0x2608, position).WriteTo(identity);
        Assert.Contains(
            "0000000000000000000000000000803F",
            Convert.ToHexString(identity.Written.ToArray()),
            StringComparison.Ordinal);
    }

    // ========================================================================= the live sessions

    /// <summary>
    /// <b>The owner's click 1: shred a shirt.</b> The bar goes out first and nothing is consumed;
    /// when the client's own <c>BUSY_MSEC</c> elapses the shirt is deleted, the cloth arrives, and
    /// <c>cf 03</c> closes the animation <b>twice</b> — which is the half that was missing and the
    /// reason the character kept shredding for a second after the item had already changed.
    /// </summary>
    [Fact]
    public void ShreddingAShirtDrawsTheBarThenGrantsClothThenStopsTheAnimation()
    {
        var (service, connection, recorder, pending, characterGuid, shirtGuid) = HoldingAShirt();

        int before = SentCount(recorder);
        SendRequestUseItem(service, connection, SalvageItemOption, characterGuid, shirtGuid);

        byte[][] armed = Sent(recorder, before);
        byte[] bar = Assert.Single(armed, IsInteractionStart);
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(11)));
        Assert.Equal(ShirtNameId, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(39)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(43)));
        Assert.DoesNotContain(armed, IsItemDelete);
        Assert.DoesNotContain(armed, IsInteractionStop);

        PumpUntil(pending, recorder, before, IsItemDelete);

        byte[][] shred = Sent(recorder, before);
        Assert.Contains(shred, IsItemDelete);                       // the shirt goes
        Assert.Contains(shred, IsItemAdd);                          // the cloth arrives
        Assert.Equal(2, shred.Count(IsInteractionStop));             // D258: retail sends two

        // ordering: the stops are the LAST thing the completion sends.
        Assert.True(
            Array.FindLastIndex(shred, IsInteractionStop) > Array.FindIndex(shred, IsItemDelete),
            "cf 03 must close the cast after the item packets, not before them");
    }

    /// <summary>
    /// <b>The owner's click 2: craft a bandage.</b> Two scraps of cloth, one Field Bandage — the
    /// retail recipe, not the wave-6 one-cloth version — and the whole thing on a cast bar it never
    /// had. Nothing is consumed until the timer elapses, and two <c>cf 03</c> close it.
    /// </summary>
    [Fact]
    public void CraftingABandageRunsOnACastBarAndConsumesTwoCloth()
    {
        var (service, connection, recorder, pending, characterGuid, _) = HoldingAShirt();
        PlayerInventory inventory = InventoryOf(connection);
        inventory.TryPickUp(CraftingCatalog.ScrapOfCloth, 2, out InventoryItemInstance? cloth);
        Assert.NotNull(cloth);
        Assert.Equal(2u, cloth!.Count);

        int before = SentCount(recorder);
        SendRecipeStart(service, connection, CraftingCatalog.FieldBandage, 1);

        byte[][] armed = Sent(recorder, before);
        byte[] bar = Assert.Single(armed, IsInteractionStart);
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(11)));
        InventoryItemFacts.TryGet(CraftingCatalog.FieldBandage, out InventoryItemFact bandage);
        Assert.Equal(bandage.NameId, BinaryPrimitives.ReadUInt32LittleEndian(bar.AsSpan(39)));
        Assert.Equal(2u, cloth.Count);                              // nothing spent yet
        Assert.DoesNotContain(armed, IsInteractionStop);

        PumpUntil(pending, recorder, before, IsItemAdd);

        byte[][] craft = Sent(recorder, before);
        Assert.Contains(craft, IsItemAdd);
        Assert.Equal(2, craft.Count(IsInteractionStop));

        Assert.DoesNotContain(
            inventory.Items.Values,
            i => i.DefinitionId == CraftingCatalog.ScrapOfCloth && i.Count > 0);
        Assert.Contains(inventory.Items.Values, i => i.DefinitionId == CraftingCatalog.FieldBandage);
        Assert.Equal(characterGuid, BinaryPrimitives.ReadUInt64LittleEndian(bar.AsSpan(3)));
    }

    /// <summary>A craft the player cannot afford never puts a bar on screen (Z1's round-34
    /// lesson): it is refused inside the request, exactly as it was before the cast bar existed.</summary>
    [Fact]
    public void ACraftWithoutIngredientsIsRefusedBeforeTheBarIsArmed()
    {
        var (service, connection, recorder, _, _, _) = HoldingAShirt();

        int before = SentCount(recorder);
        SendRecipeStart(service, connection, CraftingCatalog.MakeshiftArmor, 1);
        byte[][] refused = Sent(recorder, before);

        Assert.DoesNotContain(refused, IsInteractionStart);
        Assert.DoesNotContain(refused, IsItemAdd);
    }

    /// <summary>
    /// <b>The owner's click 3: drop it.</b> The chain now ends with the <c>0f 4a</c> toast —
    /// without which a drop simply looks like an item vanishing. (The rotation the spawn carries is
    /// pinned separately by <see cref="TheDroppedObjectCarriesTheCapturedYawInComponentY"/>: this
    /// session never sends a movement packet, so it has no facing to carry.)
    /// </summary>
    [Fact]
    public void DroppingAShirtSpawnsTheObjectAndRaisesTheToast()
    {
        var (service, connection, recorder, _, characterGuid, shirtGuid) = HoldingAShirt();

        int before = SentCount(recorder);
        SendRequestUseItem(service, connection, DropItemOption, characterGuid, shirtGuid);
        byte[][] drop = Sent(recorder, before);

        byte[] spawn = Assert.Single(drop, p => p.Length > 1 && p[1] == ZoneOpcodes.AddLightweightNpc);
        Assert.NotEmpty(spawn);

        byte[] toast = Assert.Single(drop, IsDropNotification);
        Assert.Equal(DroppedItemNotification.Length + 1, toast.Length);
        Assert.Equal(characterGuid, BinaryPrimitives.ReadUInt64LittleEndian(toast.AsSpan(3)));
        Assert.Equal(Shirt, BinaryPrimitives.ReadUInt32LittleEndian(toast.AsSpan(11)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(toast.AsSpan(15)));

        // LAST in the chain, where the friend's server puts it.
        Assert.Equal(drop.Length - 1, Array.FindIndex(drop, IsDropNotification));
    }

    // ================================================================================= plumbing

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
            (uint)Self, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
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

    /// <summary>
    /// A session holding one worn shirt, picked up off the development drop - the same shape
    /// <c>Wave9InventoryWiringTests.HoldingOne</c> uses, and the only way to learn the instance guid
    /// the server minted without reaching into a private type.
    /// </summary>
    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder,
        ConcurrentQueue<Action> Pending, ulong CharacterGuid, ulong ItemGuid) HoldingAShirt()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder) = Admit(
            new ZoneOptions
            {
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = Shirt,
                GroundLootModelId = FoldedShirtModel,
                GroundLootNameId = ShirtNameId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
            },
            pending.Enqueue);

        SendClientIsReady(service, connection);
        Pump(pending, "the development drop");
        // This fixture needs a worn shirt; normal pickups now keep an occupied chest unchanged.
        typeof(ZoneService).GetMethod("EnsureInventory", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(service, [connection, connection.Tag, "worn shirt fixture"]);
        var inventory = InventoryOf(connection);
        inventory.RemoveUnits(inventory.LoadoutSlots[SurvivorLoadout.Chest].Guid, 0);
        SendInteractRequest(service, connection, LootWorld.DefaultWorldGuidBase);

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
        Assert.True(InventoryItemFacts.TryGet(Shirt, out InventoryItemFact fact));
        ulong itemGuid = 0;
        for (int index = 0; index < slotCount; index++)
        {
            _ = reader.ReadUInt32();
            uint bodySlot = reader.ReadUInt32();
            ulong candidate = reader.ReadUInt64();
            _ = reader.ReadString();
            _ = reader.ReadString();
            if (bodySlot == fact.PassiveEquipSlotId)
            {
                itemGuid = candidate;
            }
        }

        Assert.NotEqual(0ul, itemGuid);
        return (service, connection, recorder, pending, characterGuid, itemGuid);
    }

    /// <summary>
    /// The session's own <see cref="PlayerInventory"/>. The session type is deliberately private to
    /// <see cref="ZoneService"/>, so this reads the property rather than exposing a production-only
    /// hook - the same technique <c>ZoneIntegrationTests.ArmMountedParachute</c> uses.
    /// </summary>
    private static PlayerInventory InventoryOf(SoeConnection connection)
    {
        Assert.NotNull(connection.Tag);
        object state = connection.Tag!;
        object? inventory = state.GetType().GetProperty("Inventory")?.GetValue(state);
        return Assert.IsType<PlayerInventory>(inventory);
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

    private static void SendRequestUseItem(
        ZoneService service, SoeConnection connection, uint optionId, ulong characterGuid, ulong itemGuid)
    {
        using var use = new PacketWriter();
        use.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        use.WriteByte(ItemUseOpcodes.ItemsBase);
        use.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        use.WriteUInt32(1);
        use.WriteUInt32(0);
        use.WriteUInt32(optionId);
        use.WriteUInt64(characterGuid);
        use.WriteUInt64(characterGuid);
        use.WriteUInt64(characterGuid);
        use.WriteUInt64(itemGuid);
        use.WriteByte(1);
        service.OnMessage(connection, use.Written.ToArray());
    }

    /// <summary>
    /// The client's own Craft button: <c>09 1A 00 | u32 recipeId | u32 count</c>, 11 bytes, no
    /// trailing - the framing four live requests in <c>host-20260830-131819.log</c> closed.
    /// </summary>
    private static void SendRecipeStart(
        ZoneService service, SoeConnection connection, uint recipeId, uint count)
    {
        using var craft = new PacketWriter();
        craft.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        craft.WriteByte(ZoneOpcodes.CommandBase);
        craft.WriteUInt16(0x001a);
        craft.WriteUInt32(recipeId);
        craft.WriteUInt32(count);
        service.OnMessage(connection, craft.Written.ToArray());
    }

    private static void Pump(ConcurrentQueue<Action> pending, string what)
    {
        Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000), $"{what} was never posted");
        Assert.True(pending.TryDequeue(out Action? work));
        work!();
    }

    /// <summary>Run posted work until <paramref name="until"/> appears in the sent stream.</summary>
    private static void PumpUntil(
        ConcurrentQueue<Action> pending,
        RecordingRecorder recorder,
        int skip,
        Func<byte[], bool> until)
    {
        Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000));
        while (!Sent(recorder, skip).Any(until))
        {
            Assert.True(pending.TryDequeue(out Action? work));
            work!();
            if (pending.IsEmpty && !Sent(recorder, skip).Any(until))
            {
                Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000));
            }
        }
    }

    private static byte[][] Sent(RecordingRecorder recorder, int skip = 0) =>
        [.. recorder.Messages.Where(m => m.Direction == "s2c").Select(m => m.Bytes).Skip(skip)];

    private static int SentCount(RecordingRecorder recorder) =>
        recorder.Messages.Count(m => m.Direction == "s2c");

    // The tunnelled packets carry one gateway header byte in front, hence every offset +1.
    private static bool IsInteractionStart(byte[] p) =>
        p.Length > 2 && p[1] == ZoneOpcodes.CharacterStateBase && p[2] == InteractionStart.SubOpcode;

    private static bool IsInteractionStop(byte[] p) =>
        p.Length > 2 && p[1] == ZoneOpcodes.CharacterStateBase && p[2] == InteractionStop.SubOpcode;

    private static bool IsDropNotification(byte[] p) =>
        p.Length > 2 && p[1] == ZoneOpcodes.CharacterBase && p[2] == DroppedItemNotification.SubOpcode;

    private static bool IsItemDelete(byte[] p) =>
        p.Length > 3 && p[1] == ZoneOpcodes.ClientUpdateBase
        && BitConverter.ToUInt16(p, 2) == ItemDelete.SubOpcode;

    private static bool IsItemAdd(byte[] p) =>
        p.Length > 3 && p[1] == ZoneOpcodes.ClientUpdateBase
        && BitConverter.ToUInt16(p, 2) == ItemAdd.SubOpcode;

    // ================================================================ the docs/62 §2 decoder

    private readonly record struct DecodedComponent(
        uint Key,
        uint NameStringId,
        uint ImageSetId,
        uint Reserved,
        uint DescriptionStringId,
        uint LocateDescriptionStringId,
        uint RequiredCount,
        ulong LiveCounts,
        uint RecipeType,
        uint ItemDefinitionId);

    private sealed record DecodedRecipe(
        uint RecipeId,
        uint NameStringId,
        uint ImageSetId,
        uint TintValue,
        uint DescriptionStringId,
        uint Reserved,
        uint BundleCount,
        uint SortOrdinal,
        bool MembersOnly,
        uint FilterType,
        IReadOnlyList<DecodedComponent> Components,
        uint OutputItemDefinitionId)
    {
        public bool Equals(DecodedRecipe? other) =>
            other is not null
            && (RecipeId, NameStringId, ImageSetId, TintValue, DescriptionStringId, Reserved,
                BundleCount, SortOrdinal, MembersOnly, FilterType, OutputItemDefinitionId)
                == (other.RecipeId, other.NameStringId, other.ImageSetId, other.TintValue,
                    other.DescriptionStringId, other.Reserved, other.BundleCount, other.SortOrdinal,
                    other.MembersOnly, other.FilterType, other.OutputItemDefinitionId)
            && Components.SequenceEqual(other.Components);

        public override int GetHashCode() => HashCode.Combine(RecipeId, SortOrdinal, BundleCount);
    }

    /// <summary>
    /// Read a <c>26 09</c> body back through docs/62 §2/§2b's layout - the same decode that consumed
    /// 936 of 936 bytes of the capture (<c>C:\Aug2017\out\audit\decode-recipebase.py</c>), written
    /// out again here so the test does not depend on the writer it is checking.
    /// </summary>
    private static IReadOnlyList<DecodedRecipe> Decode(byte[] packet)
    {
        Assert.Equal(RecipeOpcodes.RecipeBase, packet[0]);
        Assert.Equal(RecipeOpcodes.ListSub, packet[1]);
        int offset = 2;
        int count = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset));
        offset += 4;

        var recipes = new List<DecodedRecipe>(count);
        for (int index = 0; index < count; index++)
        {
            uint recipeId = Next(packet, ref offset);
            uint nameStringId = Next(packet, ref offset);
            uint imageSetId = Next(packet, ref offset);
            uint tintValue = Next(packet, ref offset);
            uint descriptionStringId = Next(packet, ref offset);
            uint reserved = Next(packet, ref offset);
            uint bundleCount = Next(packet, ref offset);
            uint sortOrdinal = Next(packet, ref offset);
            bool membersOnly = packet[offset++] != 0;
            uint filterType = Next(packet, ref offset);

            int componentCount = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset));
            offset += 4;
            var components = new List<DecodedComponent>(componentCount);
            for (int c = 0; c < componentCount; c++)
            {
                uint key = Next(packet, ref offset);
                uint componentName = Next(packet, ref offset);
                uint componentImage = Next(packet, ref offset);
                uint componentReserved = Next(packet, ref offset);
                uint componentDescription = Next(packet, ref offset);
                uint componentLocate = Next(packet, ref offset);
                uint required = Next(packet, ref offset);
                ulong live = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(offset));
                offset += 8;
                uint recipeType = Next(packet, ref offset);
                uint itemId = Next(packet, ref offset);
                components.Add(new DecodedComponent(
                    key, componentName, componentImage, componentReserved, componentDescription,
                    componentLocate, required, live, recipeType, itemId));
            }

            recipes.Add(new DecodedRecipe(
                recipeId, nameStringId, imageSetId, tintValue, descriptionStringId, reserved,
                bundleCount, sortOrdinal, membersOnly, filterType, components,
                Next(packet, ref offset)));
        }

        Assert.Equal(packet.Length, offset);
        return recipes;

        static uint Next(byte[] bytes, ref int at)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            at += 4;
            return value;
        }
    }
}
