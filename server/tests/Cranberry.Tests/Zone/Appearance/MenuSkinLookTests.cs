using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Emotes;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// docs/111, D221 and D222 — the three menu-skin defects the owner reported on 2026-09-03 from
/// <c>logs/host-20260903-174857.log</c> and <c>captures/wire-20260903-174857.txt</c>: wrong
/// default skins in the main menu, no real-time change when a skin is clicked, and a look that
/// changes between the main menu and the wardrobe screens.
/// <para>
/// The walk is the owner's own: login → lobby <c>ClientIsReady</c> → <c>kotkappearance</c> →
/// <c>CUSTOMIZATION_WINDOW open</c> → one skin click → <c>CUSTOMIZATION_WINDOW close</c> →
/// <c>kotkdefault</c>. The invariant is that the <c>94 01</c> dress is byte-identical at every
/// step except after the click, where exactly the clicked slot's attachment changes.
/// </para>
/// </summary>
[Collection(AppearanceStaticsCollection.Name)]
public sealed class MenuSkinLookTests
{
    [Fact]
    public void WeaponCategoryNavigationShowsEachBaseGunWithoutSelectingASkin()
    {
        var session = Admit(new ZoneOptions());
        session.Send(View("kotkappearanceweaponsguns"));
        var expected = new Dictionary<uint, string>
        {
            [2] = "Weapon_Pistol_45Auto_3P.adr", [10] = "Weapon_M16A4_3P.adr",
            [83] = "Weapons_Machete01_3P.adr", [84] = "Weapons_CombatKnife01_3P.adr",
            [1373] = "Weapon_M24_3P.adr", [1374] = "Weapons_PumpShotgun01_3P.adr",
            [1718] = "Weapons_Pistol_44Magnum01_3P.adr", [1986] = "Weapons_Bow01_3P.adr",
            [1991] = "Weapon_Pistol_380Auto_3P.adr", [1997] = "Weapons_M9Auto_3P.adr",
            [2229] = "Weapon_AK47_3P.adr",
        };
        Assert.Equal(expected.Keys.Order(), AugustSkinCatalog.Weapons.Select(s => s.CategoryPrototypeId).Distinct().Order());
        foreach ((uint prototype, string model) in expected)
        {
            var packets = session.Send(View("kotkweaponpreview:" + prototype));
            var dress = Assert.Single(Dresses(packets));
            Assert.Contains(model, Attachments(dress)[7], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Zone(packets), p => p[0] == 0xac); // no account/skin-manager mutation
            Assert.DoesNotContain(Zone(packets), p => p[0] == ZoneOpcodes.StaticViewBase);
        }
        var leaving = session.Send(View("kotkappearancegearchest"));
        Assert.Contains("Weapon_Empty.adr", Attachments(Assert.Single(Dresses(leaving)))[7]);
        Assert.Empty(Zone(session.Send(View("kotkweaponpreview:2229"))));
    }

    [Fact]
    public void WeaponCategoryNavigationUsesTheSelectedSkinAndIgnoresInvalidCategories()
    {
        var session = Admit(new ZoneOptions());
        var skin = AugustSkinCatalog.Weapons.Single(s => s.RewardItemId == 4032);
        session.Send(SkinClick(skin.CategoryPrototypeId, skin.AccountItemId));
        session.Send(View("kotkappearanceweaponsguns"));
        var selected = session.Send(View("kotkweaponpreview:" + skin.CategoryPrototypeId));
        Assert.Contains(skin.ModelNameFor(2), Attachments(Assert.Single(Dresses(selected)))[7]);
        foreach (string invalid in new[] { "0", "99999999", "-1", "2229extra", "" })
            Assert.Empty(Zone(session.Send(View("kotkweaponpreview:" + invalid))));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void NautilusPreviewPreservesEveryClothingAttachment(uint gender)
    {
        var session = Admit(new ZoneOptions
        {
            DynamicAppearanceSourcePath = @"C:\Z1\Server\Data\dynamicAppearanceFriend.bin",
        }, gender);
        var skin = AugustSkinCatalog.Weapons.Single(s => s.RewardItemId == 4032);
        byte[] before = Assert.Single(Dresses(session.Send(WindowEvent("CUSTOMIZATION_WINDOW", "open"))));
        byte[] after = Assert.Single(Dresses(session.Send(SkinClick(skin.CategoryPrototypeId, skin.AccountItemId))));

        Assert.Equal(7u, OnlyChangedSlot(before, after));
        var attachments = Attachments(after);
        Assert.Contains(skin.ModelNameFor(gender), attachments[7]);
        Assert.Equal("1862", attachments[7].Split('|')[2]);
        byte[] closed = Assert.Single(Dresses(session.Send(WindowEvent("CUSTOMIZATION_WINDOW", "close"))));
        Assert.Equal(7u, OnlyChangedSlot(after, closed)); // leaving the preview puts the gun away
        Assert.Equal(Attachments(before).Where(a => a.Key != 7).OrderBy(a => a.Key),
            Attachments(closed).Where(a => a.Key != 7).OrderBy(a => a.Key));
    }

    [Fact]
    public void ReopeningGearContainsOnlyTheChosenWeaponAndClothes()
    {
        var session = Admit(new ZoneOptions());
        var ak = AugustSkinCatalog.Weapons.First(s => s.CategoryPrototypeId == 2229 && s.RewardItemId == 2662);
        var hoodie = AugustSkinCatalog.Apparel.Single(s => s.RewardItemId == 4266);
        var trousers = AugustSkinCatalog.Apparel.Single(s => s.RewardItemId == 3875);
        foreach (var choice in new[] { ak, hoodie, trousers })
            session.Send(SkinClick(choice.CategoryPrototypeId, choice.AccountItemId));
        var burst = Zone(session.Send(WindowEvent("CUSTOMIZATION_WINDOW", "open"))).ToArray();
        var manager = burst.Single(p => p[0] == 0xac && p[1] == 0x23);
        var reader = new PacketReader(manager);
        reader.ReadByte(); reader.ReadByte(); reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadString();
        int worn = reader.ReadInt32();
        for (int i = 0; i < worn; i++) reader.ReadBytes(21);
        Assert.Equal(2, worn);
        AssertDefaultEmoteItems(ref reader);
        Assert.Equal(2, reader.ReadInt32());
        var selected = new List<uint>();
        for (int collection = 0; collection < 2; collection++)
        {
            reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadString();
            int count = reader.ReadInt32();
            var categories = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                Assert.True(categories.Add(reader.ReadUInt32()));
                reader.ReadUInt32(); reader.ReadUInt64();
                selected.Add(reader.ReadUInt32()); reader.ReadByte();
            }
            AssertDefaultEmoteItems(ref reader);
        }
        Assert.True(reader.AtEnd);
        Assert.Equal(new[] { ak.AccountItemId, hoodie.AccountItemId, trousers.AccountItemId }.Order(), selected.Order());
    }

    [Fact]
    public void OffroaderPreviewSpawnsAnswersFullDataAndUsesTheSelectedSkin()
    {
        var session = Admit(new ZoneOptions());
        byte[] Request(byte sub, params uint[] words)
        {
            using var writer = new PacketWriter();
            writer.WriteByte(0xf2); writer.WriteByte(sub);
            foreach (uint word in words) writer.WriteUInt32(word);
            return writer.Written.ToArray();
        }
        var preview = Zone(session.Send(Request(6, 1))).ToArray();
        byte[] response = preview.Single(p => p[0] == 0xf2 && p[1] == 7);
        ulong guid = BitConverter.ToUInt64(response, 2);
        Assert.NotEqual(0ul, guid);
        Assert.Single(preview, p => p[0] == ZoneOpcodes.AddLightweightVehicle);
        Assert.Single(preview, p => p[0] == ZoneOpcodes.LightweightToFullVehicle);
        using var full = new PacketWriter();
        full.WriteByte(0x0f); full.WriteByte(0x45); full.WriteUInt64(guid);
        Assert.Single(Zone(session.Send(full.Written.ToArray())), p => p[0] == ZoneOpcodes.LightweightToFullVehicle);
        var change = Zone(session.Send(Request(2, 1, 1, 3802))).ToArray();
        byte[] notify = change.Single(p => p[0] == 0xf2 && p[1] == 1);
        Assert.Equal(guid, BitConverter.ToUInt64(notify, 2));
        Assert.Equal(840u, BitConverter.ToUInt32(notify, 18));
        Assert.Empty(Zone(session.Send(Request(2, 1, 1, 4302)))); // ATV skin cannot be assigned to offroader.
        var close = Zone(session.Send(Request(6, 0))).ToArray();
        Assert.Contains(close, p => p[0] == 0x0f && p[1] == 1);
        Assert.All(close.Where(p => p[0] == 0xf2 && p[1] == 7), p => Assert.Equal(0ul, BitConverter.ToUInt64(p, 2)));
    }

    [Fact]
    public void ReplacingASkinLoadedFromDiskRefreshesTheCollectionAndTheVisibleGun()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-menu-relaunch", Guid.NewGuid().ToString("N"));
        var old = AugustSkinCatalog.Weapons.Single(e => e.RewardItemId == 4032);
        var next = AugustSkinCatalog.Weapons.First(e => e.CategoryPrototypeId == old.CategoryPrototypeId && e.AccountItemId != old.AccountItemId);
        try
        {
            using (var store = new WardrobeStore(root, coalesceMs: 0))
            {
                var wardrobe = store.Load(CharacterGuid);
                Assert.True(wardrobe.TryApply(SkinItemSelectionRequest.Parse(SkinClick(old.CategoryPrototypeId, old.AccountItemId)), out _, out _, out _));
                store.Save(CharacterGuid, wardrobe);
                store.FlushPending();
            }
            var session = Admit(new ZoneOptions { Skins = new SkinOptions { WardrobeStoreRoot = root } });
            byte[][] changed = Zone(session.Send(SkinClick(next.CategoryPrototypeId, next.AccountItemId))).ToArray();
            byte[] manager = Assert.Single(changed, p => p[0] == 0xac && p[1] == 0x23);
            var reader = new PacketReader(manager.AsSpan(2));
            reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadString();
            for (int n = reader.ReadInt32(); n > 0; n--) reader.ReadBytes(21);
            AssertDefaultEmoteItems(ref reader);
            var selections = new List<uint>();
            for (int n = reader.ReadInt32(); n > 0; n--)
            {
                reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadString();
                for (int items = reader.ReadInt32(); items > 0; items--)
                {
                    reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt64();
                    selections.Add(reader.ReadUInt32()); reader.ReadByte();
                }
                AssertDefaultEmoteItems(ref reader);
            }
            Assert.True(reader.AtEnd);
            Assert.Equal(next.AccountItemId, Assert.Single(selections));
            byte[] dress = Assert.Single(changed, p => p[0] == 0x94 && p[1] == 1);
            Assert.Contains(next.FemaleModelName, Attachments(dress)[7]);
            Assert.True(Array.IndexOf(changed, dress) > Array.IndexOf(changed, manager));
            // Let the coalesced write finish before removing this unique test directory.
            SpinWait.SpinUntil(() => File.ReadAllText(Path.Combine(root, WardrobeStore.FileNameFor(CharacterGuid))).Contains(next.AccountItemId.ToString()), 2000);
        }
        finally
        {
            // Test-owned directory only; pending stores may keep it alive briefly on Windows.
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void BackpackViewShowsTheSelectedPackAndTurnsBackToTheFrontOnExit()
    {
        var session = Admit(new ZoneOptions());
        var pack = AugustSkinCatalog.Apparel.Single(e => e.RewardItemId == 2778);
        session.Send(SkinClick(pack.CategoryPrototypeId, pack.AccountItemId));
        byte[][] back = session.Send(View("kotkappearancegearback"));
        int moveIndex = Array.FindIndex(back, p => p.Length > 3 && p[1] == 0x11 && p[2] == 0x0a && p[3] == 0);
        int cameraIndex = Array.FindIndex(back, p => p.Length > 2 && p[1] == 0xe9 && p[2] == 2);
        Assert.True(moveIndex >= 0 && cameraIndex > moveIndex);
        Assert.Equal(1f, BitConverter.ToSingle(back[moveIndex], 24)); // quaternion Y
        Assert.Contains(pack.FemaleModelName, Attachments(Assert.Single(Dresses(back)))[10]);
        Assert.Equal(new System.Numerics.Vector4(0, 1, 0, 0), MenuViewTable.SubjectRotation("kotkappearancegearback"));
        byte[][] front = session.Send(View("kotkappearancegear"));
        byte[] frontCamera = Assert.Single(front, p => p.Length > 2 && p[1] == 0xe9 && p[2] == 2);
        // Native StaticViewReply applies Heading to the actor after UpdateLocation.
        float backHeading = BitConverter.ToSingle(back[cameraIndex], 20);
        float frontHeading = BitConverter.ToSingle(frontCamera, 20);
        Assert.InRange(backHeading - frontHeading, MathF.PI - 0.00001f, MathF.PI + 0.00001f);
        Assert.InRange(backHeading + BitConverter.ToSingle(back[cameraIndex], 24) - frontHeading, -0.00001f, 0.00001f);
        Assert.False(Attachments(Assert.Single(Dresses(front))).ContainsKey(10));
        Assert.Equal(new System.Numerics.Vector4(0, 0, 0, 1), MenuViewTable.SubjectRotation("kotkappearancegear"));
    }

    [Theory]
    [InlineData("kotkappearancegearchest")]
    [InlineData("kotkappearanceweaponsguns")]
    public void LeavingTheWeaponTabRemovesThePreviewGun(string nextView)
    {
        var session = Admit(new ZoneOptions());
        var skin = AugustSkinCatalog.Weapons.First(e => e.CategoryPrototypeId == 2229);
        session.Send(View("kotkappearanceweaponsguns"));
        Assert.Contains(7u, Attachments(Assert.Single(Dresses(session.Send(
            SkinClick(skin.CategoryPrototypeId, skin.AccountItemId))))).Keys);
        var next = session.Send(View(nextView));
        Assert.Contains("Weapon_Empty.adr", Attachments(Assert.Single(Dresses(next)))[7]);
    }

    [Fact]
    public void HelmetPreviewAppearsOnlyInTheHeadView()
    {
        var session = Admit(new ZoneOptions());
        var helmet = AugustSkinCatalog.Apparel.First(e => e.EquipmentSlotId == 1
            && AugustWardrobeCatalog.IsPickupOnly(e));
        session.Send(View("kotkappearancegearhead"));
        var preview = session.Send(SkinClick(helmet.CategoryPrototypeId, helmet.AccountItemId));
        Assert.Contains(helmet.ModelNameFor(2), Attachments(Assert.Single(Dresses(preview)))[1]);
        var leaving = session.Send(View("kotkappearancegearchest"));
        var attachments = Attachments(Assert.Single(Dresses(leaving)));
        Assert.True(!attachments.TryGetValue(1, out var model) || !model.Contains(helmet.ModelNameFor(2)));
    }

    [Fact]
    public void SelectedFootwearRemainsVisibleWhenLeavingFeet()
    {
        var session = Admit(new ZoneOptions());
        var footwear = AugustSkinCatalog.Apparel.First(e => e.EquipmentSlotId == 5
            && !e.ModelNameFor(2).Contains("Jeds"));
        session.Send(View("kotkappearancegearfeet"));
        var preview = session.Send(SkinClick(footwear.CategoryPrototypeId, footwear.AccountItemId));
        Assert.Contains(footwear.ModelNameFor(2), Attachments(Assert.Single(Dresses(preview)))[5]);
        var leaving = session.Send(View("kotkappearancegearchest"));
        Assert.Contains(footwear.ModelNameFor(2), Attachments(Assert.Single(Dresses(leaving)))[5]);
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

    private const ulong CharacterGuid = 0x1004;

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

    private sealed class Session
    {
        public required ZoneService Service { get; init; }

        public required SoeConnection Connection { get; init; }

        public required RecordingRecorder Recorder { get; init; }

        public byte[][] Send(byte[] body)
        {
            int before = Recorder.Messages.Count(m => m.Direction == "s2c");
            using var request = new PacketWriter();
            request.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            foreach (byte b in body)
            {
                request.WriteByte(b);
            }

            Service.OnMessage(Connection, request.Written.ToArray());
            return
            [
                .. Recorder.Messages
                    .Where(m => m.Direction == "s2c")
                    .Skip(before)
                    .Select(m => m.Bytes),
            ];
        }

        public byte[][] Bootstrap() =>
        [
            .. Recorder.Messages.Where(m => m.Direction == "s2c").Select(m => m.Bytes),
        ];
    }

    private static Session Admit(ZoneOptions options, uint gender = 2)
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            CharacterGuid, "DDa", gender: gender, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options);
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

        using var login = new PacketWriter();
        login.WriteByte(GatewayLoginRequest.Opcode);
        login.WriteUInt64(admission.Guid);
        login.WriteString(admission.Ticket);
        login.WriteString(GatewayLoginRequest.AugustProtocol);
        login.WriteString(GatewayLoginRequest.AugustVersion);
        service.OnMessage(connection, login.Written.ToArray());
        return new Session { Service = service, Connection = connection, Recorder = recorder };
    }

    /// <summary>The inner zone packets of a burst, with the one gateway tunnel byte stripped.</summary>
    private static IEnumerable<byte[]> Zone(IEnumerable<byte[]> sent) =>
        sent.Where(bytes => bytes.Length > 1 && bytes[0] == 0x05).Select(bytes => bytes[1..]);

    private static byte[][] Dresses(IEnumerable<byte[]> sent) =>
    [
        .. Zone(sent).Where(packet => packet[0] == ZoneOpcodes.EquipmentBase
            && packet[1] == SetCharacterEquipment.SubOpcode),
    ];

    private static byte[][] SkinRows(IEnumerable<byte[]> sent) =>
    [
        .. Zone(sent).Where(packet => packet[0] == ZoneOpcodes.ItemsBase
            && packet[1] == SetSkinItem.SubOpcode),
    ];

    private static byte[] WindowEvent(string window, string action)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(ZoneOpcodes.WallOfDataBase);
        writer.WriteByte(0x05);
        writer.WriteString(window);
        writer.WriteString(action);
        writer.WriteUInt32(0);
        return writer.Written.ToArray();
    }

    private static byte[] SkinClick(uint categoryPrototypeId, uint clickedId)
    {
        // captures/wire-20260903-174857.txt:11798 — ac 32 {1, 0, 1, category, clicked}, 22 bytes.
        using var writer = new PacketWriter();
        writer.WriteByte(ZoneOpcodes.ItemsBase);
        writer.WriteByte(SkinItemSelectionRequest.RequestSetSkinItemByItemId);
        writer.WriteUInt32(1);
        writer.WriteUInt32(0);
        writer.WriteUInt32(1);
        writer.WriteUInt32(categoryPrototypeId);
        writer.WriteUInt32(clickedId);
        return writer.Written.ToArray();
    }

    private static byte[] ClientIsReady() => [ZoneOpcodes.ClientIsReady];

    private static byte[] View(string name)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(ZoneOpcodes.StaticViewBase);
        writer.WriteUInt16(1);
        writer.WriteString(name);
        return writer.Written.ToArray();
    }

    /// <summary>A trouser skin: an ordinary apparel category that reaches the lobby body.</summary>
    private static AugustSkinCatalogEntry Trousers => AugustSkinCatalog.Apparel
        .First(entry => entry.CategoryPrototypeId == 2109
            && AugustWardrobeCatalog.ProjectsOntoStarterBody(entry)
            && entry.FemaleModelName.Length > 0);

    [Fact]
    public void TheOwnersWalkKeepsOneLookAndTheClickIsTheOnlyThingThatChangesIt()
    {
        Session session = Admit(new ZoneOptions());

        // 1. the login bootstrap dress — ac 11/19/23/28, the rows, then the packet that unhides
        //    the actor (captures/wire-20260903-174857.txt:23-34).
        byte[] look = Assert.Single(Dresses(session.Bootstrap()));

        // 2. the lobby ClientIsReady: the same dress, and NOTHING after it (D221). The re-announce
        //    that used to be here is what made the main menu disagree with the Gear editor. A
        //    fresh character has no selections, so this row-count assertion is only a floor;
        //    TheLobbyReadyReplyReAnnouncesNothingButTheSwitchPutsItBack is the real guard.
        byte[][] ready = session.Send(ClientIsReady());
        Assert.Equal(look, Assert.Single(Dresses(ready)));
        Assert.Empty(SkinRows(ready));

        // 3. navigating to the appearance camera re-dresses nobody.
        Assert.Empty(Dresses(session.Send(View("kotkappearance"))));

        // 4. the Gear editor opens: managers, the rows, then the SAME dress.
        byte[][] open = session.Send(WindowEvent("CUSTOMIZATION_WINDOW", "open"));
        Assert.Equal(look, Assert.Single(Dresses(open)));

        // 5. ONE click. The echo row goes out, and behind it a dress that differs from the look
        //    before it in exactly one attachment: the clicked slot.
        AugustSkinCatalogEntry picked = Trousers;
        byte[][] click = session.Send(SkinClick(picked.CategoryPrototypeId, picked.AccountItemId));
        byte[] echo = Assert.Single(SkinRows(click));
        Assert.Equal(SetSkinItemManager.ApparelCollectionId, BitConverter.ToUInt32(echo, 2));
        Assert.Equal(picked.CategoryPrototypeId, BitConverter.ToUInt32(echo, 6));
        Assert.Equal(CharacterGuid, BitConverter.ToUInt64(echo, 10));
        Assert.Equal(picked.AccountItemId, BitConverter.ToUInt32(echo, 18));

        byte[] clicked = Assert.Single(Dresses(click));
        Assert.NotEqual(look, clicked);
        Assert.Equal(picked.EquipmentSlotId, OnlyChangedSlot(look, clicked));

        // 6. the Gear editor closes: the committed look is the click's look, unchanged.
        byte[][] close = session.Send(WindowEvent("CUSTOMIZATION_WINDOW", "close"));
        Assert.Equal(clicked, Assert.Single(Dresses(close)));

        // 7. back to the main menu: still no re-dress, so the menu shows what step 6 showed.
        Assert.Empty(Dresses(session.Send(View("kotkdefault"))));
    }

    [Fact]
    public void TheLobbyReadyReplyReAnnouncesNothingButTheSwitchPutsItBack()
    {
        AugustSkinCatalogEntry picked = Trousers;

        Session on = Admit(new ZoneOptions());
        on.Send(WindowEvent("CUSTOMIZATION_WINDOW", "open"));
        on.Send(SkinClick(picked.CategoryPrototypeId, picked.AccountItemId));
        Assert.Empty(SkinRows(on.Send(ClientIsReady())));

        Session off = Admit(new ZoneOptions
        {
            Skins = new SkinOptions { MenuLookFromDressOnly = false },
        });
        off.Send(WindowEvent("CUSTOMIZATION_WINDOW", "open"));
        off.Send(SkinClick(picked.CategoryPrototypeId, picked.AccountItemId));
        byte[][] ready = off.Send(ClientIsReady());
        Assert.NotEmpty(SkinRows(ready));

        // With the re-announce back, the dress still leads it: docs/32's third regression.
        byte[][] zone = [.. Zone(ready)];
        int dress = Array.FindIndex(zone, packet => packet[0] == ZoneOpcodes.EquipmentBase);
        int row = Array.FindIndex(
            zone,
            packet => packet[0] == ZoneOpcodes.ItemsBase && packet[1] == SetSkinItem.SubOpcode);
        Assert.True(dress >= 0);
        Assert.True(row > dress);
    }

    [Fact]
    public void TheLivePreviewSwitchTakesTheClickDressBackOff()
    {
        AugustSkinCatalogEntry picked = Trousers;
        Session session = Admit(new ZoneOptions
        {
            Skins = new SkinOptions { LiveSkinPreview = false },
        });

        session.Send(WindowEvent("CUSTOMIZATION_WINDOW", "open"));
        byte[][] click = session.Send(SkinClick(picked.CategoryPrototypeId, picked.AccountItemId));
        Assert.Single(SkinRows(click));
        Assert.Empty(Dresses(click));
    }

    [Fact]
    public void AWeaponPresetClickImmediatelyPreviewsTheChosenGun()
    {
        AugustSkinCatalogEntry weapon = AugustSkinCatalog.Weapons[0];
        Session session = Admit(new ZoneOptions());

        session.Send(WindowEvent("CUSTOMIZATION_WINDOW", "open"));
        byte[][] click = session.Send(SkinClick(weapon.CategoryPrototypeId, weapon.AccountItemId));
        byte[] echo = Assert.Single(SkinRows(click));
        Assert.Equal(SetSkinItemManager.WeaponCollectionId, BitConverter.ToUInt32(echo, 2));
        byte[] dress = Assert.Single(Dresses(click));
        Assert.Contains(weapon.FemaleModelName, Attachments(dress)[7]);
        Assert.Contains(Zone(click), p => p[0] == 0x0f && p[1] == 0x20);
    }

    /// <summary>
    /// The single body slot whose attachment differs between two <c>94 01</c> packets. Fails when
    /// zero or more than one slot moved.
    /// </summary>
    private static uint OnlyChangedSlot(byte[] before, byte[] after)
    {
        Dictionary<uint, string> left = Attachments(before);
        Dictionary<uint, string> right = Attachments(after);
        uint[] changed =
        [
            .. left.Keys.Union(right.Keys)
                .Where(slot => !left.TryGetValue(slot, out string? was)
                    || !right.TryGetValue(slot, out string? now)
                    || was != now),
        ];
        return Assert.Single(changed);
    }

    private static Dictionary<uint, string> Attachments(byte[] packet)
    {
        var reader = new PacketReader(packet.AsSpan(2));
        reader.ReadUInt32();                 // profile id
        reader.ReadUInt64();                 // character guid
        reader.ReadUInt32();                 // unknown
        reader.ReadString();                 // tint alias
        reader.ReadString();                 // decal alias
        int slots = reader.ReadInt32();
        for (int i = 0; i < slots; i++)
        {
            reader.ReadUInt32();
            reader.ReadUInt64();
        }

        int count = reader.ReadInt32();
        var found = new Dictionary<uint, string>(count);
        for (int i = 0; i < count; i++)
        {
            string model = reader.ReadString();
            string texture = reader.ReadString();
            reader.ReadString();             // tint alias
            reader.ReadString();             // decal alias
            reader.ReadUInt32();             // tint id
            reader.ReadUInt32();             // composite effect
            reader.ReadUInt32();             // effect
            uint slot = reader.ReadUInt32();
            uint shaderGroup = reader.ReadUInt32();
            int appearances = reader.ReadInt32();
            var rows = new List<uint>(appearances);
            for (int row = 0; row < appearances; row++)
            {
                rows.Add(reader.ReadUInt32());
            }

            reader.ReadByte();               // trailing flag
            found[slot] = $"{model}|{texture}|{shaderGroup}|{string.Join(',', rows)}";
        }

        return found;
    }
}
