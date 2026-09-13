using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Emotes;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone;

/// <summary>
/// Drives <see cref="ZoneService"/> through a session object directly (no sockets) and checks the
/// exact sequence of application packets it emits after the clear gateway LoginRequest.
/// </summary>
public class ZoneBootstrapTests
{
    // Native 0xc1 layout: group count; {key, group name, property count, hash, string value};
    // active name and property count; primary and secondary environment names. SKU 2 belongs
    // in DEFAULT so selecting LIVE_KOTK retains it through the native fallback lookup.
    private const string KotkEnvironmentSettingsHex =
        "C1010000000700000044454641554C540700000044454641554C54010000007F9F27720100000032"
        + "090000004C4956455F4B4F544B00000000090000004C4956455F4B4F544B00000000";

    [Fact]
    public void EnvironmentSettingsWireSeedsTheNativeDefaultSkuBeforeSelectingLiveKotk()
    {
        using var writer = new PacketWriter();
        new EnvironmentSettingsUpdate().WriteTo(writer);

        Assert.Equal(KotkEnvironmentSettingsHex, Convert.ToHexString(writer.Written));
    }

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

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder, GatewayAdmission Admission)
        Admit(ZoneOptions options, Action<Action>? post = null)
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001,
            "Cranberry",
            gender: 2,
            headId: 3,
            hairId: 2,
            skinToneId: 664,
            profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options)
        {
            Post = post,
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
        byte[] login = writer.Written.ToArray();
        service.OnMessage(connection, login);
        return (service, connection, recorder, admission);
    }

    [Fact]
    public void AdmissionSendsReplyZoneDetailsAndSelfRecordThenGatesFollowGameTimeSync()
    {
        var (service, connection, recorder, _) = Admit(new ZoneOptions { ZoneName = "LoginZone", Console = new Cranberry.Zone.DevConsole.ConsoleOptions { SelfFlagOpensConsole = false } });

        byte[][] sent = recorder.Messages
            .Where(m => m.Direction == "s2c")
            .Select(m => m.Bytes)
            .ToArray();
        AssertNoEquipmentSlotClears(sent);
        Assert.True(connection.EncryptionEnabled);
        // docs/60 stage 1 (D33) would add ONE packet to this burst, ReferenceData
        // "WeaponDefinitions" between ProfileDefinitions and ZoneDoneSendingInitialData — but it is
        // OPT-IN (CRANBERRY_WEAPON_DEFINITIONS=1). This burst is the owner's only confirmed path to
        // PLAY. EnvironmentSettings now precedes native init so rebuilding the item managers keeps KotK SKU 2.
        Assert.Equal(14, sent.Length);                                               // reply, environment, native init, world/avatar, ready managers, profiles, zone-done

        // The client asks for the game time when it enters WaitForFirstZone; only then may the
        // remaining gates be sent (FUN_140b864c0 clears the flags on entry).
        using var sync = new PacketWriter();
        sync.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        new GameTimeSync(Time: 1787948744).WriteTo(sync);
        service.OnMessage(connection, sync.Written.ToArray());
        sent = recorder.Messages
            .Where(m => m.Direction == "s2c")
            .Select(m => m.Bytes)
            .ToArray();
        AssertNoEquipmentSlotClears(sent);
        Assert.Equal(18, sent.Length);                                               // + time reply, weather, preload-done, proximity-complete
        Assert.Equal(0x05, sent[14][0]);
        GameTimeSync reply = GameTimeSync.Parse(sent[14].AsSpan(1));
        Assert.Equal(1502805600ul, reply.Time);                                       // D16: 2017-08-15T14:00:00Z, fixed
        Assert.Equal(0u, reply.Value);                                                // cycle scalar 0.0 (float bits)
        Assert.True(reply.Flag);                                                      // freeze
        Assert.Equal(1 + 1 + 8 + 4 + 1, sent[14].Length);

        Assert.Equal(new byte[] { 0x02, 0x01 }, sent[0]);                          // gateway LoginReply

        // The native environment table must exist before InitializationParameters rebuilds managers.
        // Otherwise LIVE_KOTK selects an empty SKU and the Crowns controls disappear.
        Assert.Equal(Convert.FromHexString("05" + KotkEnvironmentSettingsHex), sent[1]);
        Assert.Equal(Convert.FromHexString(
            "056E090000004C4956455F4B4F544B0000000000000000"), sent[2]);

        Assert.Equal(0x05, sent[3][0]);                                              // tunnel opcode 5, channel 0
        Assert.Equal(ZoneOpcodes.SendZoneDetails, sent[3][1]);
        Assert.Equal("LoginZone", System.Text.Encoding.ASCII.GetString(sent[3], 6, 9));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(sent[3].AsSpan(15)));   // HeightfieldLod

        Assert.Equal(0x05, sent[4][0]);
        Assert.Equal(ZoneOpcodes.ClientGameSettings, sent[4][1]);
        Assert.Equal(35, sent[4].Length);
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(sent[4].AsSpan(6)));
        Assert.Equal(1, sent[4][10]);
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(sent[4].AsSpan(11)));

        Assert.Equal(ZoneOpcodes.ReferenceData, sent[5][1]);
        Assert.Equal(DynamicAppearanceReference.TypeName,
            System.Text.Encoding.ASCII.GetString(sent[5], 4, DynamicAppearanceReference.TypeName.Length));

        Assert.Equal(0x05, sent[6][0]);
        Assert.Equal(ZoneOpcodes.SendSelfToClient, sent[6][1]);
        AssertSelfEmoteAssignments(sent[6]);
        int recordLength = BinaryPrimitives.ReadInt32LittleEndian(sent[6].AsSpan(2));
        CharacterVisuals visuals = CharacterVisuals.FromSelection(2, 3, 2, 664, 270);
        Assert.Equal(
            SelfRecordCodec.MinimalLength
                + "Cranberry".Length
                + visuals.HeadModel.Length
                + visuals.HairModel.Length
                + 12 * 20 // current F-key assignments survive this self-record reload
                + CharacterResource.Starter.Count * CharacterResource.WireLength
                // D289: the six retail recipes now ride in the self record's 0x11a list by default
                // (the only delivery that populates the crafting window). The list replaces the four
                // zero bytes the empty count wrote, so the record grows by ListLength - 4.
                + RecipeRecord.ListLength(CraftingCatalog.ToRecords()) - sizeof(int),
            recordLength);
        Assert.Equal(2 + 4 + recordLength, sent[6].Length);                          // exact: nothing trails the record
        Assert.Equal(0x1001ul, BinaryPrimitives.ReadUInt64LittleEndian(sent[6].AsSpan(6 + 8)));
        Assert.Equal(9474u, BinaryPrimitives.ReadUInt32LittleEndian(sent[6].AsSpan(6 + 25)));   // female model at +0xb9dc

        var selfReader = new PacketReader(sent[6].AsSpan(6, recordLength));
        _ = selfReader.ReadUInt64();
        _ = selfReader.ReadUInt64();
        // docs/100 §7 / D213, re-baselined by lane 3C: the self record's +0xe0 varint now carries
        // TransientIdTable.LocalPlayer (1), which the client copies to world+0x324f8 as its answer
        // to "is this packet about me?". A client varint is (value << 2), so 1 is 0x04 — ONE byte,
        // exactly as 0 was, which is why no length in this burst moved. CRANBERRY_SELF_TRANSIENT_ID=0
        // puts the 0 back.
        Assert.Equal(0x04, selfReader.ReadByte());                           // transient id varint = 1
        _ = selfReader.ReadUInt64();
        _ = selfReader.ReadUInt32();
        Assert.Equal(visuals.HeadModel, selfReader.ReadString());
        Assert.Equal(visuals.HairModel, selfReader.ReadString());
        Assert.Equal(0u, selfReader.ReadUInt32());                         // hair tint (not HairMappings row id)
        Assert.Equal(0u, selfReader.ReadUInt32());                         // eye tint
        Assert.Equal(string.Empty, selfReader.ReadString());
        Assert.Equal(string.Empty, selfReader.ReadString());
        Assert.Equal(string.Empty, selfReader.ReadString());
        Assert.Equal(0u, selfReader.ReadUInt32());
        Assert.Equal(0u, selfReader.ReadUInt32());
        Assert.Equal(visuals.SkinToneId, selfReader.ReadUInt32());         // player+0xb9f0

        Assert.Equal(ZoneOpcodes.EquipmentBase, sent[11][1]);                         // SetCharacterEquipment (un-hides the body)
        Assert.Equal(0x1001ul, BinaryPrimitives.ReadUInt64LittleEndian(sent[11].AsSpan(7)));
        var equipmentReader = new PacketReader(sent[11].AsSpan(1));
        Assert.Equal(ZoneOpcodes.EquipmentBase, equipmentReader.ReadByte());
        Assert.Equal(SetCharacterEquipment.SubOpcode, equipmentReader.ReadByte());
        Assert.Equal(5u, equipmentReader.ReadUInt32());
        Assert.Equal(0x1001ul, equipmentReader.ReadUInt64());
        _ = equipmentReader.ReadUInt32();
        _ = equipmentReader.ReadString();
        _ = equipmentReader.ReadString();
        Assert.Equal(0, equipmentReader.ReadInt32());
        Assert.Equal(visuals.StarterOutfit.Count + 2, equipmentReader.ReadInt32());
        var dressed = new List<(string Model, uint Slot)>();
        for (int index = 0; index < visuals.StarterOutfit.Count + 2; index++)
        {
            string model = equipmentReader.ReadString();
            _ = equipmentReader.ReadString();
            _ = equipmentReader.ReadString();
            _ = equipmentReader.ReadString();
            _ = equipmentReader.ReadUInt32();
            _ = equipmentReader.ReadUInt32();
            _ = equipmentReader.ReadUInt32();
            uint slot = equipmentReader.ReadUInt32();
            _ = equipmentReader.ReadUInt32();
            Assert.Equal(0, equipmentReader.ReadInt32());
            Assert.False(equipmentReader.ReadBool());
            dressed.Add((model, slot));
        }

        Assert.True(equipmentReader.ReadBool());
        Assert.True(equipmentReader.AtEnd);
        Assert.Equal(
            visuals.StarterOutfit.Select(a => (a.ModelName, a.SlotId))
                .Concat([(visuals.HeadModel, 15u), (visuals.HairModel, 27u)]),
            dressed);
        Assert.Equal(new byte[] { SetAccountItemManager.Opcode, SetAccountItemManager.SubOpcode }, sent[7][1..3]);
        Assert.Equal(1 + SetAccountItemManager.FullCatalogLength, sent[7].Length);
        Assert.Equal(Convert.FromHexString("05AC1901000000010000000100000001000000"), sent[8]);
        Assert.Equal(0x05, sent[9][0]);
        Assert.Equal(SetSkinItemManager.Opcode, sent[9][1]);
        Assert.Equal(SetSkinItemManager.SubOpcode, sent[9][2]);
        Assert.Equal(1 + SetSkinItemManager.FullCatalogLength + (3 * 12 * EmotePackets.ItemRowLength), sent[9].Length);
        AssertManagerDefaultEmotes(sent[9]);
        Assert.Equal(SetCurrentSkinItemCollection.Opcode, sent[10][1]);
        Assert.Equal(SetCurrentSkinItemCollection.SubOpcode, sent[10][2]);
        Assert.Equal(1 + SetCurrentSkinItemCollection.Length + (12 * EmotePackets.ItemRowLength), sent[10].Length);
        AssertCurrentCollectionDefaultEmotes(sent[10]);
        Assert.Equal(ZoneOpcodes.ReferenceData, sent[12][1]);                          // ProfileDefinitions, empty table
        Assert.Equal("ProfileDefinitions", System.Text.Encoding.ASCII.GetString(sent[12], 4, 18));
        // docs/60 stage 1 is OPT-IN, so ZoneDoneSendingInitialData follows ProfileDefinitions
        // directly — exactly as in captures\wire-20260829-150206.txt.
        Assert.DoesNotContain(
            sent,
            packet => packet.Length > 4
                && packet[1] == ZoneOpcodes.ReferenceData
                && System.Text.Encoding.ASCII.GetString(packet).Contains(
                    Cranberry.Zone.Weapons.WeaponDefinitionsBlob.TypeName, StringComparison.Ordinal));
        Assert.Equal(new byte[] { 0x05, ZoneOpcodes.ZoneDoneSendingInitialData }, sent[13]);
        Assert.Equal(ZoneOpcodes.UpdateWeatherData, sent[15][1]);
        Assert.Equal(Convert.FromHexString("0511190000"), sent[16]);
        Assert.Equal(Convert.FromHexString("05113400"), sent[17]);                    // ClientUpdate.NetworkProximityUpdatesComplete

        // The zone details carry the client's complete StringHashToValue table (its parser
        // empties the map first; an empty list crashed the model loader, docs/02 2026-08-28).
        Assert.True(sent[3].Length > 20_000);
        using var weather = new PacketWriter();
        WeatherSettings.Kotk2017.WriteTo(weather);
        int lightingLengthOffset = 1 + 1 + 4 + "LoginZone".Length + 4 + 1 + weather.Position
            + 5 * sizeof(uint) + sizeof(ulong) + 1;
        int lightingLength = BinaryPrimitives.ReadInt32LittleEndian(sent[3].AsSpan(lightingLengthOffset));
        Assert.Equal(SendZoneDetails.KotkLightingFile.Length, lightingLength);
        Assert.Equal(
            SendZoneDetails.KotkLightingFile,
            System.Text.Encoding.ASCII.GetString(sent[3], lightingLengthOffset + sizeof(uint), lightingLength));
        int listCountOffset = lightingLengthOffset + sizeof(uint) + lightingLength + 2;
        var values = new PacketReader(sent[3].AsSpan(listCountOffset));
        Assert.Equal(StringHashValues.Entries.Count + 2, values.ReadInt32());
        foreach (StringHashValue entry in StringHashValues.Entries)
        {
            Assert.Equal(entry.Hash, values.ReadUInt32());
            Assert.Equal(entry.Value, values.ReadString());
            Assert.False(values.ReadBool());
            Assert.Equal(entry.Name, values.ReadString());
        }
        // Returning to the menu clears the prior healing icon and world label.
        Assert.Equal(StringHashValue.HashName("Cranberry.Healing"), values.ReadUInt32());
        Assert.Equal("0", values.ReadString());
        Assert.False(values.ReadBool());
        Assert.Equal("Cranberry.Healing", values.ReadString());
        Assert.Equal(StringHashValue.HashName(WorldDisplayLabel.Key), values.ReadUInt32());
        Assert.Equal(string.Empty, values.ReadString());
        Assert.False(values.ReadBool());
        Assert.Equal(WorldDisplayLabel.Key, values.ReadString());
        Assert.True(values.AtEnd);
    }

    private static void AssertManagerDefaultEmotes(ReadOnlySpan<byte> packet)
    {
        var reader = new PacketReader(packet[3..]); // gateway, ac, 23
        reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadString();
        for (int worn = reader.ReadInt32(); worn > 0; worn--) reader.ReadBytes(21);
        AssertDefaultEmoteItems(ref reader);
        Assert.Equal(2, reader.ReadInt32());
        foreach (uint collectionId in new[] { 1u, 2u })
        {
            Assert.Equal(collectionId, reader.ReadUInt32());
            reader.ReadUInt32(); reader.ReadString();
            for (int selected = reader.ReadInt32(); selected > 0; selected--) reader.ReadBytes(21);
            AssertDefaultEmoteItems(ref reader);
        }
        Assert.True(reader.AtEnd);
    }

    private static void AssertCurrentCollectionDefaultEmotes(ReadOnlySpan<byte> packet)
    {
        var reader = new PacketReader(packet[3..]); // gateway, ac, 28
        reader.ReadUInt32(); reader.ReadString();
        for (int selected = reader.ReadInt32(); selected > 0; selected--) reader.ReadBytes(21);
        AssertDefaultEmoteItems(ref reader);
        Assert.True(reader.AtEnd);
    }

    private static void AssertDefaultEmoteItems(ref PacketReader reader)
    {
        Assert.Equal(12, reader.ReadInt32());
        foreach (AugustEmote emote in AugustEmotes.DefaultSlots)
        {
            Assert.Equal(emote.SlotId, reader.ReadUInt32()); // outer map key
            Assert.Equal(emote.SlotId, reader.ReadUInt32());
            Assert.Equal(0ul, reader.ReadUInt64());
            Assert.Equal(emote.ItemDefinitionId, reader.ReadUInt32());
        }
    }

    [Fact]
    public void InitialAppearanceRefreshNeverClearsAControlledCosmeticSlot()
    {
        var (_, _, recorder, _) = Admit(new ZoneOptions());
        byte[][] sent = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Select(message => message.Bytes)
            .ToArray();

        AssertNoEquipmentSlotClears(sent);

        // The single SetCharacterEquipment is the whole appearance: the attachment list is
        // authoritative and absent slots are implied by omission, so no explicit clear is needed.
        byte[] dress = Assert.Single(sent, IsCharacterEquipment);
        Assert.NotEmpty(ReadEquipmentAttachments(dress));
    }

    [Fact]
    public void GameTimeSyncRoundTripsTheClientLayout()
    {
        // Observed c2s packet 2026-08-28 20:17:28: 1D C8DE916A00000000 00000000 00
        GameTimeSync request = GameTimeSync.Parse(Convert.FromHexString("1DC8DE916A000000000000000000"));
        Assert.Equal(0x6A91DEC8ul, request.Time);
        Assert.Equal(0u, request.Value);
        Assert.False(request.Flag);

        using var writer = new PacketWriter();
        request.WriteTo(writer);
        Assert.Equal("1DC8DE916A000000000000000000", Convert.ToHexString(writer.Written));
        Assert.Throws<PacketFormatException>(() => GameTimeSync.Parse(Convert.FromHexString("1DC8DE916A00000000000000000000")));
    }

    [Fact]
    public void LaterGatesCanBeSwitchedOffAndBootstrapCanBeDeferred()
    {
        var (_, _, immediate, _) = Admit(new ZoneOptions { SendLaterGates = false });
        Assert.Equal(
            13,
            immediate.Messages.Count(m => m.Direction == "s2c"));  // reply through ProfileDefinitions; no zone-done
    }

    [Fact]
    public void GearWindowOpenRefreshesTheSelectedCatalogueAfterTheEditorIsActive()
    {
        var (service, connection, recorder, _) = Admit(new ZoneOptions());
        int before = recorder.Messages.Count(m => m.Direction == "s2c");

        using var request = new PacketWriter();
        request.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 1).ToByte());
        request.WriteByte(ZoneOpcodes.WallOfDataBase);
        request.WriteByte(0x05);
        request.WriteString("CUSTOMIZATION_WINDOW");
        request.WriteString("open");
        request.WriteInt32(0);
        service.OnMessage(connection, request.Written.ToArray());

        byte[][] sent = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Skip(before)
            .Select(message => message.Bytes)
            .ToArray();
        AssertNoEquipmentSlotClears(sent);
        Assert.Equal(5, sent.Length);
        Assert.Equal(new byte[] { SetAccountItemManager.Opcode, SetAccountItemManager.SubOpcode }, sent[0][1..3]);
        Assert.Equal(1 + SetAccountItemManager.FullCatalogLength, sent[0].Length);
        Assert.Equal(new byte[] { AccountItemManagerStateChanged.Opcode, AccountItemManagerStateChanged.SubOpcode }, sent[1][1..3]);
        Assert.Equal(new byte[] { SetSkinItemManager.Opcode, SetSkinItemManager.SubOpcode }, sent[2][1..3]);
        Assert.Equal(new byte[] { SetCurrentSkinItemCollection.Opcode, SetCurrentSkinItemCollection.SubOpcode }, sent[3][1..3]);
        Assert.Equal(new byte[] { ZoneOpcodes.EquipmentBase, SetCharacterEquipment.SubOpcode }, sent[4][1..3]);
    }

    [Fact]
    public void SkinClickEchoesImmediatelyAndGearCloseCommitsTheSelectedMesh()
    {
        var (service, connection, recorder, _) = Admit(new ZoneOptions());
        AugustSkinCatalogEntry boonie = AugustSkinCatalog.Apparel.Single(entry =>
            entry.CategoryPrototypeId == 2158 && entry.RewardItemId == 2484);
        int beforeClick = recorder.Messages.Count(message => message.Direction == "s2c");

        using (var click = new PacketWriter())
        {
            click.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            click.WriteByte(ZoneOpcodes.ItemsBase);
            click.WriteByte(SkinItemSelectionRequest.RequestSetSkinItemByItemId);
            click.WriteUInt32(1);
            click.WriteUInt32(0);
            click.WriteUInt32(SetSkinItemManager.ApparelCollectionId);
            click.WriteUInt32(boonie.CategoryPrototypeId);
            click.WriteUInt32(boonie.AccountItemId);
            service.OnMessage(connection, click.Written.ToArray());
        }

        // D222 (docs/111): the click answer is the retail one-row echo AND, because Cranberry
        // bakes the chosen garment into the dress rather than into an escrow slot row the way the
        // friend server does, the dress that makes the change visible before the editor closes.
        byte[][] clickBurst =
        [
            .. recorder.Messages
                .Where(message => message.Direction == "s2c")
                .Skip(beforeClick)
                .Select(message => message.Bytes),
        ];
        Assert.Equal(2, clickBurst.Length);
        byte[] clickReply = clickBurst[0];
        Assert.Equal(
            new byte[] { ZoneOpcodes.EquipmentBase, SetCharacterEquipment.SubOpcode },
            clickBurst[1][1..3]);
        Assert.Contains(
            boonie.FemaleModelName,
            System.Text.Encoding.ASCII.GetString(clickBurst[1]));
        Assert.Equal(1 + SetSkinItem.Length, clickReply.Length);
        Assert.Equal(new byte[] { SetSkinItem.Opcode, SetSkinItem.SubOpcode }, clickReply[1..3]);
        Assert.Equal(boonie.CategoryPrototypeId,
            BinaryPrimitives.ReadUInt32LittleEndian(clickReply.AsSpan(1 + 2 + 4)));
        Assert.Equal(0x1001ul,
            BinaryPrimitives.ReadUInt64LittleEndian(clickReply.AsSpan(1 + 2 + 4 + 4)));
        Assert.Equal(boonie.AccountItemId,
            BinaryPrimitives.ReadUInt32LittleEndian(clickReply.AsSpan(1 + 2 + 4 + 4 + 8)));

        int beforeClose = recorder.Messages.Count(message => message.Direction == "s2c");
        using (var close = new PacketWriter())
        {
            close.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 1).ToByte());
            close.WriteByte(ZoneOpcodes.WallOfDataBase);
            close.WriteByte(0x05);
            close.WriteString("CUSTOMIZATION_WINDOW");
            close.WriteString("close");
            close.WriteInt32(0);
            service.OnMessage(connection, close.Written.ToArray());
        }

        byte[][] committed = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Skip(beforeClose)
            .Select(message => message.Bytes)
            .ToArray();
        AssertNoEquipmentSlotClears(committed);
        Assert.Equal(4, committed.Length); // manager, current collection, worn SetSkinItem, equipment
        Assert.Equal(new byte[] { SetSkinItemManager.Opcode, SetSkinItemManager.SubOpcode }, committed[0][1..3]);
        Assert.Equal(1 + SetSkinItemManager.FullCatalogLength + 42 + (3 * 12 * EmotePackets.ItemRowLength), committed[0].Length);
        AssertManagerDefaultEmotes(committed[0]);
        Assert.Equal(new byte[] { SetCurrentSkinItemCollection.Opcode, SetCurrentSkinItemCollection.SubOpcode }, committed[1][1..3]);
        AssertCurrentCollectionDefaultEmotes(committed[1]);
        Assert.Equal(new byte[] { SetSkinItem.Opcode, SetSkinItem.SubOpcode }, committed[2][1..3]);
        Assert.Equal(new byte[] { ZoneOpcodes.EquipmentBase, SetCharacterEquipment.SubOpcode }, committed[3][1..3]);
        Assert.Contains(
            boonie.FemaleModelName,
            System.Text.Encoding.ASCII.GetString(committed[3]));
    }

    [Fact]
    public void GearClosePublishesOnlyWornApparelAndKeepsWeaponPresetSeparate()
    {
        var (service, connection, recorder, _) = Admit(new ZoneOptions());
        AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.Single(entry =>
            entry.CategoryPrototypeId == 2158 && entry.RewardItemId == 2484);
        AugustSkinCatalogEntry helmet = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2827 && !string.IsNullOrWhiteSpace(entry.FemaleModelName));
        AugustSkinCatalogEntry backpack = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2038 && !string.IsNullOrWhiteSpace(entry.FemaleModelName));
        AugustSkinCatalogEntry weapon = AugustSkinCatalog.Weapons.First(entry =>
            !string.IsNullOrWhiteSpace(entry.FemaleModelName));

        SendSkinClick(service, connection, hat, SetSkinItemManager.ApparelCollectionId);
        SendSkinClick(service, connection, helmet, SetSkinItemManager.ApparelCollectionId);
        SendSkinClick(service, connection, backpack, SetSkinItemManager.ApparelCollectionId);
        SendSkinClick(service, connection, weapon, SetSkinItemManager.WeaponCollectionId);

        int beforeClose = recorder.Messages.Count(message => message.Direction == "s2c");
        CloseGearWindow(service, connection);

        byte[][] committed = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Skip(beforeClose)
            .Select(message => message.Bytes)
            .ToArray();

        byte[] manager = Assert.Single(committed, IsSkinManager);
        SkinManagerWornRow worn = Assert.Single(ReadManagerWornRows(manager));
        Assert.Equal((hat.CategoryPrototypeId, hat.RewardItemId, hat.AccountItemId),
            (worn.CategoryPrototypeId, worn.RewardItemId, worn.AccountItemId));

        SkinAnnouncement[] announcements = committed
            .Where(IsSkinAnnouncement)
            .Select(ReadSkinAnnouncement)
            .ToArray();
        Assert.Collection(
            announcements.OrderBy(row => row.CollectionId).ThenBy(row => row.CategoryPrototypeId),
            row =>
            {
                Assert.Equal(SetSkinItemManager.ApparelCollectionId, row.CollectionId);
                Assert.Equal(hat.CategoryPrototypeId, row.CategoryPrototypeId);
                Assert.Equal(hat.AccountItemId, row.AccountItemId);
            },
            row =>
            {
                Assert.Equal(SetSkinItemManager.WeaponCollectionId, row.CollectionId);
                Assert.Equal(weapon.CategoryPrototypeId, row.CategoryPrototypeId);
                Assert.Equal(weapon.AccountItemId, row.AccountItemId);
            });
        Assert.DoesNotContain(announcements, row =>
            row.CategoryPrototypeId == helmet.CategoryPrototypeId
            || row.CategoryPrototypeId == backpack.CategoryPrototypeId);

        byte[] equipment = Assert.Single(committed, IsCharacterEquipment);
        EquipmentAttachmentRow[] attachments = ReadEquipmentAttachments(equipment);
        EquipmentAttachmentRow wornHead = Assert.Single(attachments, row => row.SlotId == 1);
        Assert.Equal(hat.FemaleModelName, wornHead.ModelName);
        Assert.DoesNotContain(attachments, row => row.SlotId is 10 or 100);
        Assert.DoesNotContain(attachments, row =>
            row.ModelName == helmet.FemaleModelName || row.ModelName == backpack.FemaleModelName);
        AssertNoEquipmentSlotClears(committed);
    }

    [Fact]
    public void ClientReadyResyncKeepsUnacquiredPickupGearOffThePregameBody()
    {
        var (service, connection, recorder, _) = Admit(new ZoneOptions());
        AugustSkinCatalogEntry trousers = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2109 && !string.IsNullOrWhiteSpace(entry.FemaleModelName));
        AugustSkinCatalogEntry helmet = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2827 && !string.IsNullOrWhiteSpace(entry.FemaleModelName));
        AugustSkinCatalogEntry backpack = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2121 && !string.IsNullOrWhiteSpace(entry.FemaleModelName));
        AugustSkinCatalogEntry runningShoes = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2215 && !string.IsNullOrWhiteSpace(entry.FemaleModelName));
        AugustSkinCatalogEntry bodyArmor = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2271 && !string.IsNullOrWhiteSpace(entry.FemaleModelName));

        SendSkinClick(service, connection, trousers, SetSkinItemManager.ApparelCollectionId);
        SendSkinClick(service, connection, helmet, SetSkinItemManager.ApparelCollectionId);
        SendSkinClick(service, connection, backpack, SetSkinItemManager.ApparelCollectionId);
        SendSkinClick(service, connection, runningShoes, SetSkinItemManager.ApparelCollectionId);
        SendSkinClick(service, connection, bodyArmor, SetSkinItemManager.ApparelCollectionId);

        int beforeReady = recorder.Messages.Count(message => message.Direction == "s2c");
        using (var ready = new PacketWriter())
        {
            ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            ready.WriteByte(ZoneOpcodes.ClientIsReady);
            service.OnMessage(connection, ready.Written.ToArray());
        }

        byte[][] sent = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Skip(beforeReady)
            .Select(message => message.Bytes)
            .ToArray();
        // D221 (docs/111): the LOBBY ready reply announces no skin rows at all any more. The rows
        // went out with the click and with the bootstrap; re-stating them here left the client's
        // own skin-manager recomposite as the last writer of the menu outfit, which is why the
        // owner's main menu and his Gear editor showed two different characters on 2026-09-03.
        // Relative order in the burst that still carries both is asserted against the known-good
        // capture by MatchZoningSendsTheDressBeforeTheWornSkinRows.
        Assert.DoesNotContain(sent, IsSkinAnnouncement);

        // This test is about what is ON the body, so it reads the dress the client is left with —
        // and asserts every dress in the burst really is the same one.
        byte[][] dresses = [.. sent.Where(IsCharacterEquipment)];
        Assert.All(dresses, dress => Assert.Equal(dresses[0], dress));
        byte[] equipment = dresses[^1];
        EquipmentAttachmentRow[] attachments = ReadEquipmentAttachments(equipment);
        Assert.Equal(
            trousers.FemaleModelName,
            Assert.Single(attachments, row => row.SlotId == trousers.EquipmentSlotId).ModelName);
        Assert.DoesNotContain(attachments, row => row.SlotId is 10 or 100);
        Assert.DoesNotContain(attachments, row =>
            row.ModelName == helmet.FemaleModelName
            || row.ModelName == backpack.FemaleModelName
            || row.ModelName == runningShoes.FemaleModelName
            || row.ModelName == bodyArmor.FemaleModelName);
        AssertNoEquipmentSlotClears(sent);
    }

    [Fact]
    public void DelayedBootstrapWithoutADispatcherSendsImmediatelyRatherThanRaceOffThread()
    {
        // ZoneService.Post is null here (no listener-thread dispatcher). A delayed bootstrap must
        // not run on a timer/thread-pool thread against the non-thread-safe connection, so the
        // service falls back to sending on the calling (transport) thread synchronously.
        var (service, _, recorder, _) = Admit(new ZoneOptions { BootstrapDelayMs = 60_000 });
        Assert.Null(service.Post);
        Assert.Equal(
            14,
            recorder.Messages.Count(m => m.Direction == "s2c"));  // complete immediate bootstrap
    }

    [Fact]
    public void DelayedBootstrapWithADispatcherPostsThroughItAndCanBeCancelled()
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(0x1001, "Cranberry", gender: 1);
        var recorder = new RecordingRecorder();
        var pending = new List<Action>();
        var service = new ZoneService(new SilentLog(), recorder, tickets, new ZoneOptions { BootstrapDelayMs = 10 })
        {
            Post = pending.Add,   // capture instead of running, standing in for the listener thread
        };
        var request = new SessionRequest(3, 1, 512, ZoneService.ProtocolName);
        var connection = new SoeConnection(
            new IPEndPoint(IPAddress.Loopback, 5555), in request, SessionSettings.WithSeed(1),
            service.OnSessionRequest(new IPEndPoint(IPAddress.Loopback, 5555), in request),
            service, new SilentLog(), (_, _) => { }, now: 0);
        service.OnConnected(connection);

        using var login = new PacketWriter();
        login.WriteByte(GatewayLoginRequest.Opcode);
        login.WriteUInt64(admission.Guid);
        login.WriteString(admission.Ticket);
        login.WriteString(GatewayLoginRequest.AugustProtocol);
        login.WriteString(GatewayLoginRequest.AugustVersion);
        service.OnMessage(connection, login.Written.ToArray());

        // Only the LoginReply has gone out; the bootstrap is deferred through Post.
        Assert.Single(recorder.Messages, m => m.Direction == "s2c");
        Assert.True(SpinWait.SpinUntil(() => pending.Count > 0, 2000));

        // A close before the timer fires cancels the pending callback: draining it is a no-op.
        service.OnDisconnected(connection, DisconnectCause.PeerRequested);
        foreach (Action work in pending)
        {
            work();
        }

        Assert.Equal(1, recorder.Messages.Count(m => m.Direction == "s2c"));         // still just the reply
    }

    [Fact]
    public void ClientTunnelPacketsAreDecodedByRegisteredName()
    {
        var (service, connection, _, _) = Admit(new ZoneOptions());

        using var locale = new PacketWriter();
        locale.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 1).ToByte());
        locale.WriteByte(ZoneOpcodes.SetLocale);
        locale.WriteString("en_US");
        service.OnMessage(connection, locale.Written.ToArray());   // must not throw

        using var init = new PacketWriter();
        init.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        init.WriteByte(ZoneOpcodes.ClientInitializationDetails);
        init.WriteUInt32(7);
        service.OnMessage(connection, init.Written.ToArray());

        using var truncated = new PacketWriter();
        truncated.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 1).ToByte());
        truncated.WriteByte(ZoneOpcodes.SetLocale);
        truncated.WriteByte(0xFF);
        service.OnMessage(connection, truncated.Written.ToArray()); // malformed: logged, not thrown
    }

    [Fact]
    public void InventoryWindowOpenSendsTheAugustReferenceBundleAndCloseKeepsQuickUseAccess()
    {
        var pending = new ConcurrentQueue<Action>();
        var (service, connection, recorder, admission) =
            Admit(new ZoneOptions { AutoMatchMs = 1 }, pending.Enqueue);

        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        byte[] readyPacket = ready.Written.ToArray();
        service.OnMessage(connection, readyPacket);

        // Bootstrap callbacks can interleave with auto-match and the delayed zoning burst.
        for (int hop = 0; service.ForTest(connection).Step != "Zoning" && hop < 32; hop++)
        {
            Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000));
            Assert.True(pending.TryDequeue(out Action? work));
            work!();
        }
        Assert.Equal("Zoning", service.ForTest(connection).Step);

        // The zoning ClientIsReady creates and announces the server-side inventory model.
        service.OnMessage(connection, readyPacket);

        int beforeOpen = recorder.Messages.Count(message => message.Direction == "s2c");
        using (var open = new PacketWriter())
        {
            open.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 1).ToByte());
            open.WriteByte(ZoneOpcodes.WallOfDataBase);
            open.WriteByte(0x05);
            open.WriteString("InventoryWindow");
            open.WriteString("open");
            open.WriteInt32(0);
            service.OnMessage(connection, open.Written.ToArray());
        }

        byte[][] opened = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Skip(beforeOpen)
            .Select(message => message.Bytes[1..])
            .ToArray();

        int proximate = Array.FindIndex(opened, packet =>
            packet.Length > 1
            && packet[0] == ZoneOpcodes.ProximateItemBase
            && packet[1] == 0x01);
        int containers = Array.FindIndex(opened, packet =>
            packet.Length > 2
            && packet[0] == ContainerOpcodes.ContainerBase
            && packet[1] == 0x02
            && packet[2] == 0x00);
        int loadoutSlots = Array.FindIndex(opened, packet =>
            packet.Length > 1
            && packet[0] == ZoneOpcodes.LoadoutsBase
            && packet[1] == LoadoutOpcodes.SetLoadoutSlotsSub);
        int abilities = Array.FindIndex(opened, packet =>
            packet.Length > 1
            && packet[0] == ZoneOpcodes.AbilitiesBase
            && packet[1] == AbilityOpcodes.SetActivatableAbilityManagerSub);
        int access = Array.FindIndex(opened, packet =>
            packet.Length > 2
            && packet[0] == ZoneOpcodes.AccessedCharacterBase
            && packet[1] == 0x01
            && packet[2] == 0x00);

        Assert.True(proximate >= 0, "InventoryWindow open did not publish ProximateItems");
        Assert.True(containers > proximate, "InitContainers must follow the proximity snapshot");
        Assert.True(loadoutSlots > containers, "86/04 must follow InitContainers");
        // Opening a passive inventory window must not impersonate an explicit c2s 86/06 selection.
        // In particular, neither a current-slot echo nor an equipment re-dress may enter the
        // client's active-hand update path merely because the window was opened.
        Assert.DoesNotContain(opened, packet =>
            packet.Length > 1
            && packet[0] == ZoneOpcodes.LoadoutsBase
            && packet[1] == LoadoutOpcodes.SelectSlotSub);
        Assert.DoesNotContain(opened, packet =>
            packet.Length > 1
            && packet[0] == ZoneOpcodes.EquipmentBase
            && packet[1] == SetCharacterEquipment.SubOpcode);
        Assert.True(abilities > loadoutSlots, "a0/05 must follow the rebuilt loadout table");
        Assert.True(access > abilities, "BeginCharacterAccess must be last in the open bundle");

        Assert.Equal(BeginCharacterAccess.Length, opened[access].Length);
        Assert.Equal(admission.Guid, BinaryPrimitives.ReadUInt64LittleEndian(opened[access].AsSpan(3)));
        Assert.Equal(admission.Guid, BinaryPrimitives.ReadUInt64LittleEndian(opened[access].AsSpan(11)));

        // Default outfit: chest (shirt), legs (trousers), hidden inventory bag.
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(opened[containers].AsSpan(19)));

        int beforeClose = recorder.Messages.Count(message => message.Direction == "s2c");
        using (var close = new PacketWriter())
        {
            close.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 1).ToByte());
            close.WriteByte(ZoneOpcodes.WallOfDataBase);
            close.WriteByte(0x05);
            close.WriteString("InventoryWindow");
            close.WriteString("close");
            close.WriteInt32(0);
            service.OnMessage(connection, close.Written.ToArray());
        }

        Assert.DoesNotContain(recorder.Messages.Where(m => m.Direction == "s2c")
            .Skip(beforeClose).Select(m => m.Bytes), p => p.Length > 3
                && p[1] == ZoneOpcodes.AccessedCharacterBase && p[2] == 2);

        // Even a client-initiated self close must restore the permission needed by Q/E.
        using var revoke = new PacketWriter();
        revoke.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        new EndCharacterAccess(admission.Guid).WriteTo(revoke);
        service.OnMessage(connection, revoke.Written.ToArray());
        var restored = recorder.Messages.Last(m => m.Direction == "s2c").Bytes[1..];
        Assert.Equal(ZoneOpcodes.AccessedCharacterBase, restored[0]);
        Assert.Equal(1, restored[1]);
        Assert.Equal(admission.Guid, BitConverter.ToUInt64(restored, 3));
        Assert.Equal(admission.Guid, BitConverter.ToUInt64(restored, 11));

    }

    [Fact]
    public void FirstClientIsReadyRepeatsTheFullDressExactlyOnce()
    {
        var (service, connection, recorder, _) = Admit(new ZoneOptions());
        byte[] initialDress = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Select(message => message.Bytes)
            .Single(IsCharacterEquipment);

        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        service.OnMessage(connection, ready.Written.ToArray());
        service.OnMessage(connection, ready.Written.ToArray());

        byte[][] dresses = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Select(message => message.Bytes)
            .Where(IsCharacterEquipment)
            .ToArray();
        Assert.Equal(2, dresses.Length);
        Assert.Equal(initialDress, dresses[1]);

        byte[][] resources = recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Select(message => message.Bytes)
            .Where(packet => packet.Length > 2 && packet[1] == ZoneOpcodes.ResourceEventBase)
            .ToArray();
        Assert.Equal(2, resources.Length);                                      // health + stamina, once
        Assert.All(resources, packet => Assert.Equal(1 + CharacterResourceUpdate.WireLength, packet.Length));
    }

    /// <summary>
    /// The match-zoning burst, byte-for-opcode against the two captures in which the client
    /// actually entered Z2. Both carry exactly this order out of the gateway on channel 0:
    ///
    /// <code>
    /// wire-20260829-184346.txt rows 137..144 (18:44:29.738, 0 wardrobe selections)
    ///   05 0b ClientBeginZoning           232 B
    ///   05 ca UpdateWeatherData           154 B
    ///   05 17 ReferenceData         1,268,872 B   (DynamicAppearance, overrides off)
    ///   05 ce GameModeHud mode trio        20 B
    ///   05 03 SendSelfToClient          1,093 B
    ///   05 94 01 SetCharacterEquipment    476 B
    ///   05 17 ReferenceData                35 B   (ProfileDefinitions, empty)
    ///   05 05 ZoneDoneSendingInitialData    2 B
    /// </code>
    ///
    /// This is the sequence the client answered with ClientIsReady (Zoning) 2.1 s later, then
    /// ClientFinishedLoading and its own LoadingScreenWindow close. Any extra packet on this path
    /// requires a specific reason (docs/32). The world display label now adds one full
    /// StringHashToValueManager update before this unchanged captured sequence.
    /// </summary>
    [Fact]
    public void MatchZoningMatchesTheKnownGoodCaptureOpcodeOrder()
    {
        byte[][] zoning = DriveToMatchZoning(out _);

        Assert.Equal(
            ["fb", "0b", "ca", "17", "ce", "03", "94.01", "17", "05"],
            zoning.Select(ZoneLabel));
        Assert.All(zoning, packet => Assert.Equal(0x05, packet[0]));   // tunnel, channel 0
        AssertNoEquipmentSlotClears(zoning);
        AssertSelfEmoteAssignments(Assert.Single(zoning, packet => ZoneLabel(packet) == "03"));
    }

    private static void AssertSelfEmoteAssignments(byte[] packet)
    {
        // Native EmoteAnimationSlots defaults. Look specifically in the production self
        // record: ac23/ac28 can be correct while this later packet clears every assignment.
        uint[] items = [3276, 3287, 3277, 3278, 3279, 3280, 3281, 3282, 3283, 3284, 3877, 3878];
        byte[] expected = new byte[4 + 12 * 20];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, 12);
        for (int index = 0; index < items.Length; index++)
        {
            Span<byte> row = expected.AsSpan(4 + index * 20, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)index + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], (uint)index + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(row[16..], items[index]);
        }
        ReadOnlySpan<byte> self = packet.AsSpan(6);
        int offset = self.IndexOf(expected);
        Assert.True(offset >= 0, "SendSelfToClient must restore the F1-F12 assignment map.");
        Assert.Equal(offset, self.LastIndexOf(expected));
        Assert.Equal(self.Length, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(2)));
    }

    /// <summary>
    /// Same burst with wardrobe selections active. The one capture that carries them —
    /// wire-20260829-150206.txt rows 256..269, the session that reached Z2 and parachuted — puts
    /// the 726-byte SetCharacterEquipment <b>before</b> the six 24-byte SetSkinItem rows:
    ///
    /// <code>
    ///   05 0b, 05 ca, 05 17, 05 ce, 05 03,
    ///   05 94 01 SetCharacterEquipment    726 B
    ///   05 ac 24 SetSkinItem ×6            24 B each
    ///   05 17 ProfileDefinitions, 05 05 ZoneDoneSendingInitialData
    /// </code>
    ///
    /// Its host log agrees (logs/host-20260829-150206.log:123-124 — "sent SetCharacterEquipment
    /// (7 meshes, 6 wardrobe selections, match zoning)" then "re-announced 6 worn skin
    /// selection(s) (match zoning)"), and the same order appears on that session's ClientIsReady
    /// resync at 15:02:49.589 (lines 136-137).
    ///
    /// The broken sessions invert it (logs/host-20260829-173352.log 17:36:04.707 re-announce,
    /// 17:36:04.708 SetCharacterEquipment) and none of them entered Z2. The session that did enter
    /// Z2 under the current build (18:43) had 0 wardrobe selections, so SendWornSkinItems emitted
    /// nothing and the inversion was invisible: skins-before-dress has never been accepted by a
    /// client.
    /// </summary>
    [Fact]
    public void MatchZoningSendsTheDressBeforeTheWornSkinRows()
    {
        byte[][] zoning = DriveToMatchZoning(
            out _,
            configure: (service, connection) =>
            {
                AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.Single(entry =>
                    entry.CategoryPrototypeId == 2158 && entry.RewardItemId == 2484);
                AugustSkinCatalogEntry weapon = AugustSkinCatalog.Weapons.First(entry =>
                    !string.IsNullOrWhiteSpace(entry.FemaleModelName));
                SendSkinClick(service, connection, hat, SetSkinItemManager.ApparelCollectionId);
                SendSkinClick(service, connection, weapon, SetSkinItemManager.WeaponCollectionId);
            });

        Assert.Equal(
            ["fb", "0b", "ca", "17", "ce", "03", "94.01", "ac.24", "ac.24", "17", "05"],
            zoning.Select(ZoneLabel));
        AssertNoEquipmentSlotClears(zoning);
    }

    /// <summary>
    /// docs/108 (D211). The sibling of the opcode-order guard above, on the other axis: the
    /// data-independent packets of the zoning burst are the exact LENGTHS the last known-good
    /// client session put on the wire — <c>captures\wire-20260902-212215.txt</c> rows 328-341, the
    /// 2026-09-02 21:22 session that reached the landing:
    ///
    /// <code>
    ///   05 0b ClientBeginZoning              231 B
    ///   05 ca UpdateWeatherData              153 B
    ///   05 17 DynamicAppearanceDefinitions   (table size — data, not shape)
    ///   05 ce                                 19 B
    ///   05 03 SendSelfToClient              1095 B
    ///   05 94 01 SetCharacterEquipment       (dress size — wardrobe, not shape)
    ///   05 17 ProfileDefinitions              34 B
    ///   05 05 ZoneDoneSendingInitialData       1 B
    /// </code>
    ///
    /// The 2026-09-03 07:39 and 07:41 failures put exactly these lengths on the wire — the burst
    /// was never the thing that broke, which is why this test pins it and
    /// <see cref="LobbyReadyNeverRestatesTheDressAfterTheSkinRows"/> pins what did.
    /// </summary>
    [Fact]
    public void MatchZoningPacketLengthsMatchTheKnownGoodCapture()
    {
        byte[][] zoning = DriveToMatchZoning(out _);

        // Every packet carries one gateway tunnel byte in front of the zone payload.
        Dictionary<string, int> byLabel = zoning
            .GroupBy(ZoneLabel)
            .ToDictionary(group => group.Key, group => group.First().Length - 1);

        Assert.Equal(231, byLabel["0b"]);
        Assert.Equal(153, byLabel["ca"]);
        Assert.Equal(19, byLabel["ce"]);
        Assert.Equal(1, byLabel["05"]);

        // The self record carries the character's name inline, so it is the capture's 1,095 bytes
        // plus the six characters by which this fixture's "Cranberry" exceeds the owner's "DDa" —
        // and, since D289, plus the six retail recipes the self record now carries at 0x11a. The
        // capture's own server (a 1118/1119 build) shipped an EMPTY recipe list here; the 1148
        // client this server targets populates its crafting window ONLY from this list
        // (DIAG-recipes-vehicles §A), so this length legitimately exceeds the capture's. The opcode
        // ORDER — guard 4 — is unchanged; only the self record's length moves. The twelve
        // keyed emote assignments add 20 bytes each so SendSelf cannot clear the F-key map.
        Assert.Equal(
            1095
                + ("Cranberry".Length - "DDa".Length)
                + RecipeRecord.ListLength(CraftingCatalog.ToRecords()) - sizeof(int)
                + (12 * 20),
            byLabel["03"]);

        // Both ReferenceData packets are present and the trailing one is the 34-byte empty
        // ProfileDefinitions table the capture ends the burst with.
        byte[][] reference = [.. zoning.Where(packet => ZoneLabel(packet) == "17")];
        Assert.Equal(2, reference.Length);
        Assert.Equal(34, reference[1].Length - 1);
        Assert.Contains(
            "DynamicAppearanceDefinitions",
            System.Text.Encoding.ASCII.GetString(reference[0], 0, Math.Min(64, reference[0].Length)));
    }

    /// <summary>
    /// docs/108 (D211) — the 2026-09-03 "G10" regression, as a guard.
    ///
    /// <para>
    /// The lobby's <c>ClientIsReady</c> burst must end with the skin rows, never with a second
    /// <c>94 01 SetCharacterEquipment</c> re-stating the dress after them. D190's third repair
    /// appended exactly that, and the August client answers it by never finishing the NEXT
    /// <c>ClientBeginZoning</c> world load: its own log stops at
    /// <c>WFWR: Waiting for load : Zoning = true, Loading = true</c>, it stops acknowledging, and
    /// the session dies with the client's "G10". Seven owner-free live runs on 2026-09-03 isolate
    /// this one field — runs 1/3/4/5 wedge with it on, runs 2/6/7 reach the parachute landing with
    /// it off, and run 7 differs from run 4 by nothing else.
    /// </para>
    /// <para>It is the same law docs/32's third regression states for the zoning burst.</para>
    /// </summary>
    [Fact]
    public void LobbyReadyNeverRestatesTheDressAfterTheSkinRows()
    {
        var pending = new ConcurrentQueue<Action>();
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder, _) =
            Admit(new ZoneOptions(), pending.Enqueue);

        AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.Single(entry =>
            entry.CategoryPrototypeId == 2158 && entry.RewardItemId == 2484);
        SendSkinClick(service, connection, hat, SetSkinItemManager.ApparelCollectionId);

        int before = recorder.Messages.Count(message => message.Direction == "s2c");

        using (var ready = new PacketWriter())
        {
            ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            ready.WriteByte(ZoneOpcodes.ClientIsReady);
            service.OnMessage(connection, ready.Written.ToArray());
        }

        string[] burst =
        [
            .. recorder.Messages
                .Where(message => message.Direction == "s2c")
                .Skip(before)
                .Select(message => ZoneLabel(message.Bytes)),
        ];

        // D221 (docs/111) makes D211 unbreakable rather than merely satisfied: the lobby ready
        // burst now carries no ac/24 at all, so there is nothing for a dress to be re-stated
        // after. One 94/01, zero skin rows — which is byte-for-byte the shape of the 2026-08-29
        // 18:43 session that proved zoning works (that character simply had no selections).
        int lastDress = Array.LastIndexOf(burst, "94.01");
        Assert.True(lastDress >= 0, $"the lobby ready burst sent no dress at all: {string.Join(" ", burst)}");
        Assert.True(
            !burst.Contains("ac.24"),
            "the lobby ready burst still re-announces skin rows (D221): " + string.Join(" ", burst));
        Assert.Equal(1, burst.Count(label => label == "94.01"));
    }

    /// <summary>
    /// Runs the dev auto-match to the point where <c>EnterMatch</c> has emitted the whole zoning
    /// burst, and returns just that burst (everything the service sent after ClientIsReady).
    /// </summary>
    private static byte[][] DriveToMatchZoning(
        out RecordingRecorder recorder,
        Action<ZoneService, SoeConnection>? configure = null)
    {
        var pending = new ConcurrentQueue<Action>();
        (ZoneService service, SoeConnection connection, RecordingRecorder recorded, _) =
            Admit(new ZoneOptions
            {
                AutoMatchMs = 1,
                // This fixture pins the complete zoning protocol burst. Deferred lobby
                // scenery uses the same dispatcher and can otherwise precede that burst
                // when the full suite delays the auto-match callback.
                SendDoors = false,
                SendVehicles = false,
            }, pending.Enqueue);
        recorder = recorded;
        configure?.Invoke(service, connection);

        using (var ready = new PacketWriter())
        {
            ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            ready.WriteByte(ZoneOpcodes.ClientIsReady);
            service.OnMessage(connection, ready.Written.ToArray());
        }

        int beforeZoning = recorder.Messages.Count(message => message.Direction == "s2c");

        // Pump until the actual zoning burst finishes. Watchdog work shares this queue and can
        // arrive between the auto-match and transfer callbacks, especially in the full suite;
        // counting two arbitrary callbacks can return before zoning has begun.
        long deadline = System.Environment.TickCount64 + 30_000;
        while (!recorded.Messages.Where(message => message.Direction == "s2c")
            .Skip(beforeZoning).Any(message => message.Bytes.Length > 1
                && message.Bytes[1] == ZoneDoneSendingInitialData.Opcode))
        {
            int remaining = (int)Math.Max(0, deadline - System.Environment.TickCount64);
            Assert.True(
                remaining > 0 && SpinWait.SpinUntil(() => !pending.IsEmpty, remaining),
                "match zoning did not finish before the deadline");
            Assert.True(pending.TryDequeue(out Action? work));
            work!();
        }

        return
        [
            .. recorder.Messages
                .Where(message => message.Direction == "s2c")
                .Skip(beforeZoning)
                .Select(message => message.Bytes),
        ];
    }

    /// <summary>Opcode label for a channel-0 tunnelled packet, sub-opcode included where one exists.</summary>
    private static string ZoneLabel(byte[] packet)
    {
        Assert.True(packet.Length >= 2, "tunnelled packet is too short to carry a zone opcode");
        byte opcode = packet[1];
        return opcode switch
        {
            ZoneOpcodes.EquipmentBase or ZoneOpcodes.ItemsBase => $"{opcode:x2}.{packet[2]:x2}",
            _ => $"{opcode:x2}",
        };
    }

    private static void SendSkinClick(
        ZoneService service,
        SoeConnection connection,
        AugustSkinCatalogEntry entry,
        uint collectionId)
    {
        using var click = new PacketWriter();
        click.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        click.WriteByte(ZoneOpcodes.ItemsBase);
        click.WriteByte(SkinItemSelectionRequest.RequestSetSkinItemByItemId);
        click.WriteUInt32(1);
        click.WriteUInt32(0);
        click.WriteUInt32(collectionId);
        click.WriteUInt32(entry.CategoryPrototypeId);
        click.WriteUInt32(entry.AccountItemId);
        service.OnMessage(connection, click.Written.ToArray());
    }

    private static void CloseGearWindow(ZoneService service, SoeConnection connection)
    {
        using var close = new PacketWriter();
        close.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 1).ToByte());
        close.WriteByte(ZoneOpcodes.WallOfDataBase);
        close.WriteByte(0x05);
        close.WriteString("CUSTOMIZATION_WINDOW");
        close.WriteString("close");
        close.WriteInt32(0);
        service.OnMessage(connection, close.Written.ToArray());
    }

    private static bool IsSkinManager(byte[] packet) =>
        packet.Length > 2
        && packet[1] == SetSkinItemManager.Opcode
        && packet[2] == SetSkinItemManager.SubOpcode;

    private static bool IsSkinAnnouncement(byte[] packet) =>
        packet.Length > 2
        && packet[1] == SetSkinItem.Opcode
        && packet[2] == SetSkinItem.SubOpcode;

    private static bool IsCharacterEquipment(byte[] packet) =>
        packet.Length > 2
        && packet[1] == ZoneOpcodes.EquipmentBase
        && packet[2] == SetCharacterEquipment.SubOpcode;

    private static bool IsUnsetCharacterEquipmentSlot(byte[] packet) =>
        packet.Length > 2
        && packet[1] == ZoneOpcodes.EquipmentBase
        && packet[2] == UnsetCharacterEquipmentSlot.SubOpcode;

    /// <summary>
    /// No appearance refresh — menu, gear-editor, ClientIsReady or match zoning — may emit an
    /// <c>Equipment.UnsetCharacterEquipmentSlot</c> (0x94 sub 3). Evidence, all four Aug-2017
    /// sessions counted over <c>captures\wire-20260829-*.txt</c> (ExternalGatewayApi_3, s2c):
    ///
    /// <list type="table">
    ///   <item><description>wire-20260829-150206 — reached Z2, parachuted: 7× 0x94/01, <b>0</b>× 0x94/03</description></item>
    ///   <item><description>wire-20260829-184346 — reached Z2, 4,618 movement packets: 5× 0x94/01, <b>0</b>× 0x94/03</description></item>
    ///   <item><description>wire-20260829-151627 — never entered Z2: 18× 0x94/01, <b>55</b>× 0x94/03</description></item>
    ///   <item><description>wire-20260829-173352 — never entered Z2, error G10: 11× 0x94/01, <b>53</b>× 0x94/03</description></item>
    /// </list>
    ///
    /// The clear burst appears in exactly the sessions the client refused and in neither session it
    /// accepted (docs/32). <c>SetCharacterEquipment</c> already carries the complete attachment
    /// list, so an absent slot is expressed by omission; clearing slot 1 explicitly is what
    /// produced the G10 exit. The superseded expectation — a clear for every unoccupied member of
    /// <c>[1,2,3,4,5,10,28,29,100]</c> — was asserted by six tests here until 2026-08-29 and was
    /// derived from server send-logs alone, never from a client-originated reply.
    /// </summary>
    private static void AssertNoEquipmentSlotClears(byte[][] packets)
    {
        byte[][] clears = [.. packets.Where(IsUnsetCharacterEquipmentSlot)];
        Assert.True(
            clears.Length == 0,
            $"{clears.Length} UnsetCharacterEquipmentSlot packet(s) emitted; the client only ever "
            + "accepted a zone transition when none were sent (docs/32, wire-20260829-150206.txt "
            + "and wire-20260829-184346.txt both carry zero)");
    }

    private static SkinManagerWornRow[] ReadManagerWornRows(byte[] tunneledPacket)
    {
        var reader = new PacketReader(tunneledPacket.AsSpan(1));
        Assert.Equal(SetSkinItemManager.Opcode, reader.ReadByte());
        Assert.Equal(SetSkinItemManager.SubOpcode, reader.ReadByte());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadString();

        int count = reader.ReadInt32();
        var rows = new SkinManagerWornRow[count];
        for (int index = 0; index < count; index++)
        {
            rows[index] = new SkinManagerWornRow(
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt64(),
                reader.ReadUInt32(),
                reader.ReadByte());
        }

        return rows;
    }

    private static SkinAnnouncement ReadSkinAnnouncement(byte[] tunneledPacket)
    {
        var reader = new PacketReader(tunneledPacket.AsSpan(1));
        Assert.Equal(SetSkinItem.Opcode, reader.ReadByte());
        Assert.Equal(SetSkinItem.SubOpcode, reader.ReadByte());
        var row = new SkinAnnouncement(
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt64(),
            reader.ReadUInt32(),
            reader.ReadByte());
        Assert.True(reader.AtEnd);
        return row;
    }

    private static EquipmentAttachmentRow[] ReadEquipmentAttachments(byte[] tunneledPacket)
    {
        var reader = new PacketReader(tunneledPacket.AsSpan(1));
        Assert.Equal(ZoneOpcodes.EquipmentBase, reader.ReadByte());
        Assert.Equal(SetCharacterEquipment.SubOpcode, reader.ReadByte());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt64();
        _ = reader.ReadUInt32();
        _ = reader.ReadString();
        _ = reader.ReadString();
        Assert.Equal(0, reader.ReadInt32());

        int count = reader.ReadInt32();
        var attachments = new EquipmentAttachmentRow[count];
        for (int index = 0; index < count; index++)
        {
            string modelName = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            uint slotId = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            int appearanceCount = reader.ReadInt32();
            for (int appearanceIndex = 0; appearanceIndex < appearanceCount; appearanceIndex++)
            {
                _ = reader.ReadUInt32();
            }

            _ = reader.ReadBool();
            attachments[index] = new EquipmentAttachmentRow(modelName, slotId);
        }

        Assert.True(reader.ReadBool());
        Assert.True(reader.AtEnd);
        return attachments;
    }

    private sealed record SkinManagerWornRow(
        uint CategoryPrototypeId,
        uint RewardItemId,
        ulong ItemInstanceId,
        uint AccountItemId,
        byte Flags);

    private sealed record SkinAnnouncement(
        uint CollectionId,
        uint CategoryPrototypeId,
        ulong CharacterId,
        uint AccountItemId,
        byte Flags);

    private sealed record EquipmentAttachmentRow(string ModelName, uint SlotId);
}
