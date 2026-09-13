using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

// Frozen vectors of the menu-time replies (docs/02 2026-08-28 "menu" rows); each one parses to
// its exact end in the client's own parser.
public sealed class MenuPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void LobbyGameDefinitionsIsFiveEmptyTablesInA20ByteBlob() =>
        Assert.Equal(
            Convert.FromHexString("410200" + "14000000" + "00000000" + "00000000" + "00000000" + "00000000" + "00000000"),
            Bytes(w => new LobbyGameDefinitions().WriteTo(w)));

    [Fact]
    public void StaticViewReplyIs52BytesWithTheClientDefaults() =>
        Assert.Equal(
            Convert.FromHexString(
                "E90200" + "00000000" +
                "00000000" + "00000000" + "00000000" +       // target
                "00000000" + "00000000" +                    // heading, yaw offset
                "9A99193E" + "00008040" +                    // pitch 0.15, distance 4
                "00000000" + "00000000" +                    // aim offsets
                "00005C42" +                                 // fov 55
                "00000000" + "00"),                          // focus area, hide subject
            Bytes(w => new StaticViewReply().WriteTo(w)));

    [Fact]
    public void PeriodMainMenuShotAndSubjectMarkMatchTheCapturedComposition()
    {
        Assert.True(StaticViewReply.TryForView("kotkdefault", out StaticViewReply camera, out var mark));
        Assert.Equal(
            Convert.FromHexString(
                "E9020000000000000006C266E6FD438F228C430000C0BF000000000000000000000000EE7CFF3E23DBF93E"
                    + "00005C420000000000"),
            Bytes(camera.WriteTo));
        Assert.Equal(
            Convert.FromHexString(
                "110A00713DFCC1C335FD4329FC8B430000803F0000000000000000000000000000803F010000"),
            Bytes(writer => new UpdateLocation(mark, new(0, 0, 0, 1)).WriteTo(writer)));
    }

    [Fact]
    public void GameModeShotMatchesTheCapturedOverheadComposition()
    {
        Assert.True(StaticViewReply.TryForView("kotkgamemodes", out StaticViewReply camera, out var mark));
        Assert.Equal(
            Convert.FromHexString(
                "E90200000000000000C8C1CDEC004400C083433333B3BF00000000000000000000000085EBD13EA4701DBF"
                    + "00006C420000000000"),
            Bytes(camera.WriteTo));
        Assert.Equal(
            Convert.FromHexString(
                "110A0014AE9541E1FAFC439A598C430000803F0000000000000000000000000000803F010000"),
            Bytes(writer => new UpdateLocation(mark, new(0, 0, 0, 1)).WriteTo(writer)));
    }

    [Fact]
    public void TryForViewCarriesOnlyTheTwoShotsThePeriodTraceHas()
    {
        // This writer is the PERIOD-TRACE table and nothing else — D193's capture-decoded rows live
        // in MenuViewTable, which delegates these two here so their frozen vectors above stay the
        // single source. A character screen resolved HERE would mean a composed shot had leaked
        // into the proven pair.
        Assert.False(StaticViewReply.TryForView("kotkcharacter", out _, out _));
        Assert.False(StaticViewReply.TryForView("kotkappearance", out _, out _));
        Assert.False(StaticViewReply.TryForView("kotkappearancegearhead", out _, out _));

        // …and the shipped table does answer all three (docs/105).
        Assert.True(MenuViewTable.TryResolve("kotkcharacter", MenuViewCoverage.All, out _, out _));
        Assert.True(MenuViewTable.TryResolve("kotkappearance", MenuViewCoverage.All, out _, out _));
        Assert.True(MenuViewTable.TryResolve("kotkappearancegearhead", MenuViewCoverage.All, out _, out _));
    }

    [Fact]
    public void RewardBuffInfoIsThirteenZeroFloats()
    {
        byte[] bytes = Bytes(w => new RewardBuffInfo().WriteTo(w));
        Assert.Equal(53, bytes.Length);
        Assert.Equal(0xB0, bytes[0]);
        Assert.All(bytes.Skip(1), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ContinentBattleInfoEmptyListIsFiveBytes() =>
        Assert.Equal(Convert.FromHexString("9600000000"), Bytes(w => new ContinentBattleInfo().WriteTo(w)));

    [Fact]
    public void InGamePurchaseRepliesMatchTheDerivedVectors()
    {
        Assert.Equal(
            Convert.FromHexString("270B00" + "01000000" + "00" + "00000000" + "00000000" + "03000000" + "4B4824" + "00000000" + "00" + "00000000"),
            Bytes(w => new WalletInfoResponse().WriteTo(w)));
        Assert.Equal(
            Convert.FromHexString("271B00" + "01000000" + "05000000" + "656E5F5553" + "03000000" + "555344" + "00"),
            Bytes(w => new AccountInfoResponse().WriteTo(w)));
        Assert.Equal(
            Convert.FromHexString("271600" + "01000000" + "00000000"),
            Bytes(w => new CountryCodesResponse().WriteTo(w)));
        Assert.Equal(
            Convert.FromHexString("271000" + "01000000" + "00000000"),
            Bytes(w => new StoreProductsResponse(StoreProductsResponse.StationCashProducts).WriteTo(w)));
        Assert.Equal(
            Convert.FromHexString("273900" + "01000000" + "00000000"),
            Bytes(w => new StoreProductsResponse(StoreProductsResponse.TargetedPromoProducts).WriteTo(w)));
    }

    [Fact]
    public void MatchRankingReplyIs127BytesAndEchoesTheGameMode()
    {
        byte[] bytes = Bytes(w => new MatchRankingReply(GameMode: 2).WriteTo(w));
        Assert.Equal(127, bytes.Length);
        Assert.Equal(Convert.FromHexString("6707"), bytes[..2]);
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 18));
        Assert.All(bytes.Skip(2).Where((_, i) => i is < 16 or > 19), b => Assert.Equal(0, b));
    }

    [Fact]
    public void MatchScheduleReplyIsAnEmptyList() =>
        Assert.Equal(Convert.FromHexString("671200000000"), Bytes(w => new MatchScheduleReply().WriteTo(w)));

    [Fact]
    public void SynchronizationReplyEchoesThreeAndCarriesTheServerClock()
    {
        // Request 8C + 6 x u64 (t1, t1, x, 0, g1, g2); reply echoes the first three and sends S, S, S & 0xffffffff.
        byte[] request = Convert.FromHexString("8C" + "40420F0000000000" + "40420F0000000000" + "0000000000000000" + "0000000000000000" + "0000000000000000" + "0000000000000000");
        SynchronizationReply reply = SynchronizationReply.For(request, 3_600_000);
        Assert.Equal(
            Convert.FromHexString("8C" + "40420F0000000000" + "40420F0000000000" + "0000000000000000" + "80EE360000000000" + "80EE360000000000" + "80EE360000000000"),
            Bytes(w => reply.WriteTo(w)));
        Assert.Equal(49, Bytes(w => reply.WriteTo(w)).Length);
    }

    [Fact]
    public void SetCharacterEquipmentEmptyDressIs35Bytes() =>
        Assert.Equal(
            Convert.FromHexString("9401" + "05000000" + "0110000000000000" + "00000000" + "00000000" + "00000000" + "00000000" + "00000000" + "01"),
            Bytes(w => new SetCharacterEquipment(0x1001).WriteTo(w)));

    [Fact]
    public void UnsetCharacterEquipmentSlotMatchesTheAugustTwentyTwoByteShape() =>
        Assert.Equal(
            Convert.FromHexString(
                "9403" +
                "03000000" +
                "0110000000000000" +
                "00000000" +
                "0A000000"),
            Bytes(w => new UnsetCharacterEquipmentSlot(
                CharacterId: 0x1001,
                SlotId: 10).WriteTo(w)));

    [Fact]
    public void SetCharacterEquipmentWritesTheCompleteAttachmentSchema()
    {
        var attachment = new CharacterEquipmentAttachment(
            "Mesh.adr",
            SlotId: 3,
            ShaderParameterGroupId: 0x99AABBCC,
            AppearanceIds: [0x11223344, 0x55667788]);

        Assert.Equal(
            Convert.FromHexString(
                "9401" +
                "05000000" +
                "0110000000000000" +
                "00000000" +
                "00000000" +
                "00000000" +
                "00000000" +
                "01000000" +
                "080000004D6573682E616472" +
                "0700000044656661756C74" +
                "0700000044656661756C74" +
                "0100000023" +
                "00000000" +
                "00000000" +
                "00000000" +
                "03000000" +
                "CCBBAA99" +
                "02000000" +
                "44332211" +
                "88776655" +
                "00" +
                "01"),
            Bytes(w => new SetCharacterEquipment(0x1001, Attachments: [attachment]).WriteTo(w)));
    }

    [Fact]
    public void AugustCharacterVisualsResolveGenderHeadHairAndStarterMeshesTogether()
    {
        CharacterVisuals female = CharacterVisuals.FromSelection(
            gender: 2,
            headId: 3,
            hairId: 2,
            skinToneId: 664,
            profileId: 270);

        Assert.Equal(2u, female.Gender);
        Assert.Equal("SurvivorFemale_Head_01.adr", female.HeadModel);
        Assert.Equal(664u, female.SkinToneId);
        Assert.Equal("SurvivorFemale_Hair_ShortMessy.adr", female.HairModel);
        Assert.Equal([2u, 3u, 4u, 5u, 7u], female.StarterOutfit.Select(a => a.SlotId));
        Assert.All(female.StarterOutfit.Take(4),
            attachment => Assert.Contains("Female", attachment.ModelName));
        Assert.Equal("Weapon_Empty.adr", female.StarterOutfit[4].ModelName);
        Assert.All(female.StarterOutfit, attachment =>
        {
            Assert.Equal("Default", attachment.TextureAlias);
            Assert.Equal("Default", attachment.TintAlias);
            Assert.Equal("#", attachment.DecalAlias);
        });
        Assert.Equal(
            new uint[]
            {
                DynamicAppearanceReference.CranberryGearGroup,
                DynamicAppearanceReference.CranberryShirtGroup,
                DynamicAppearanceReference.CranberryPantsGroup,
                DynamicAppearanceReference.CranberryGearGroup,
                0,
            },
            female.StarterOutfit.Select(attachment => attachment.ShaderParameterGroupId));

        CharacterVisuals headWins = CharacterVisuals.FromSelection(1, 6, 0, 0, 0);
        Assert.Equal(2u, headWins.Gender);
        Assert.Equal("SurvivorFemale_Head_03.adr", headWins.HeadModel);

        CharacterVisuals rowId = CharacterVisuals.FromSelection(1, 1, 0, 3, 0);
        Assert.Equal(664u, rowId.SkinToneId);
        Assert.Equal(0u, CharacterVisuals.FromSelection(1, 1, 0, 999, 0).SkinToneId);
    }

    [Fact]
    public void LogoutPacketsMatchTheDerivedVectors()
    {
        Assert.Equal(Convert.FromHexString("113000"), Bytes(w => new CompleteLogoutProcess().WriteTo(w)));
        Assert.Equal(
            Convert.FromHexString("C4" + "01" + "01000000" + "31"),
            Bytes(w => new CharacterSelectSessionResponse("1").WriteTo(w)));
    }
}
