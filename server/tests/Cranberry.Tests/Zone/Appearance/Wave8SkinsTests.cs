using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// Wave 8, lane SKINS (D53, docs/80): the owner's own Z1 behaviour re-expressed against
/// Cranberry's 1148 packet layer.
/// <para>
/// <b>Grade (D29).</b> Everything here is BUILT and unit-TESTED. Nothing in this file is evidence
/// that the client did anything - only a capture, a harness probe or the owner's eyes can say
/// that, and none of them has seen an attachment on body slot 76 in this build.
/// </para>
/// </summary>
[Collection(AppearanceStaticsCollection.Name)]
public sealed class Wave8SkinsTests
{
    // The only appearance source the filter accepts. Every test that needs the real table is
    // skipped when it is absent, so the suite stays green on a machine that does not have it.
    private const string AppearanceSource = @"C:\Z1\Server\Data\dynamicAppearanceFriend.bin";

    // ---------------------------------------------------------------------------------------
    // Edit 1 - the stowed weapons.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The nine roster weapons the owner can actually pick up, each on the passive slot the
    /// client's own <c>EquipSlotItemClasses</c> resolves for its <c>ITEM_CLASS</c>, and the exact
    /// <c>_3P.adr</c> that goes on the wire. This is the acceptance list for
    /// <i>"they don't show on me"</i>.
    /// </summary>
    [Theory]
    [InlineData(76u, 10u, "Weapon_M16A4_3P.adr", 168u)]
    [InlineData(77u, 1374u, "Weapons_PumpShotgun01_3P.adr", 29u)]
    [InlineData(80u, 2229u, "Weapon_AK47_3P.adr", 71u)]
    [InlineData(78u, 2u, "Weapon_Pistol_45Auto_3P.adr", 745u)]
    [InlineData(79u, 1718u, "Weapons_Pistol_44Magnum01_3P.adr", 1747u)]
    [InlineData(81u, 1991u, "Weapon_Pistol_380Auto_3P.adr", 656u)]
    [InlineData(78u, 1997u, "Weapons_M9Auto_3P.adr", 1919u)]
    [InlineData(82u, 1986u, "Weapons_Bow01_3P.adr", 639u)]
    [InlineData(106u, 84u, "Weapons_CombatKnife01_3P.adr", 73u)]
    public void EveryRosterWeaponWearsItsOwnThirdPersonMeshOnItsStowSlot(
        uint bodySlotId,
        uint itemDefinitionId,
        string expectedMesh,
        uint expectedGroup)
    {
        List<CharacterEquipmentAttachment> dress = Starter(CharacterVisuals.Male);

        Assert.Equal(1, AugustWornVisuals.Dress(
            dress,
            [new AugustWornItem(bodySlotId, itemDefinitionId, string.Empty)],
            CharacterVisuals.Male,
            appearanceRowsFor: null,
            attachableBodySlots: null,
            definesShaderGroup: static _ => true));

        CharacterEquipmentAttachment stowed = Assert.Single(
            dress,
            attachment => attachment.SlotId == bodySlotId);
        Assert.Equal(expectedMesh, stowed.ModelName);
        Assert.Equal(expectedGroup, stowed.ShaderParameterGroupId);

        // And the file is one the client can actually load - naming an absent .adr is a silent
        // failure, which is the whole reason AugustAssetIndex exists.
        Assert.True(AugustAssetIndex.Ships(expectedMesh), expectedMesh);
    }

    /// <summary>
    /// The stow slot itself still comes from the client's sheet, not from this lane. Recorded here
    /// because docs/80 §3.6 found Cranberry's resolver is BETTER than the owner's - it keys on
    /// <c>PASSIVE_EQUIP_SLOT_GROUP_ID</c> and excludes slot 85, which his flat tuple list does not
    /// - so no later wave should "port" his.
    /// </summary>
    [Fact]
    public void TheStowGroupsAreStillTheClientsOwnAndStillExcludeSlotEightyFive()
    {
        Assert.Equal([76u, 77u, 80u], EquipmentSlotTable.StowSlotsForItemClass(25036));
        Assert.Equal([76u, 77u, 80u], EquipmentSlotTable.StowSlotsForItemClass(25037));
        Assert.Equal([78u, 79u, 81u], EquipmentSlotTable.StowSlotsForItemClass(4096));
        Assert.Equal([78u, 79u, 81u], EquipmentSlotTable.StowSlotsForItemClass(4098));
        Assert.Equal([82u, 83u, 84u], EquipmentSlotTable.StowSlotsForItemClass(25038));
        Assert.Equal([103u], EquipmentSlotTable.StowSlotsForItemClass(25047));

        Assert.True(EquipmentSlotTable.TryGet(85, out EquipmentSlotDefinition backStab));
        Assert.False(backStab.IsEquipment);
        Assert.DoesNotContain(85u, AugustWornVisuals.AttachableBodySlots);
    }

    /// <summary>
    /// The machete is the one roster item whose mesh comes from the item sheet rather than the
    /// appearance catalogue, and the sheet spells it <c>_3p</c> where the packs spell it
    /// <c>_3P</c>. Its slot (106) is a slot edit 1 has just started dressing, so the
    /// canonicalisation is load-bearing rather than tidy.
    /// </summary>
    [Fact]
    public void TheSheetFallbackIsCanonicalisedAgainstThePackIndex()
    {
        Assert.False(AugustWornMeshCatalog.TryGet(83, out _));

        Assert.True(AugustWornVisuals.TryResolveMesh(
            83, CharacterVisuals.Male, "Weapons_Machete01_3p.adr", out string mesh, out _));
        Assert.Equal("Weapons_Machete01_3P.adr", mesh);
        Assert.True(AugustAssetIndex.Ships(mesh));

        // A name nothing ships comes back unchanged rather than blanked: this index covers .adr
        // files only and must not silently delete a mesh a caller resolved some other way.
        Assert.Equal(
            "Definitely_Not_An_Asset.adr",
            AugustAssetIndex.Canonical("Definitely_Not_An_Asset.adr"));
        Assert.False(AugustAssetIndex.Ships("Definitely_Not_An_Asset.adr"));

        // The gendered sheet path still substitutes, and its result is canonical too.
        Assert.True(AugustWornVisuals.TryResolveMesh(
            999_999,
            CharacterVisuals.Female,
            "Survivor<gender>_Back_Backpack_Military.adr",
            out string gendered,
            out _));
        Assert.Equal("SurvivorFemale_Back_Backpack_Military.adr", gendered);
        Assert.True(AugustAssetIndex.Ships(gendered));
    }

    [Fact]
    public void TheAssetIndexCoversEveryMeshThisServerCanAttach()
    {
        Assert.Equal(3_406, AugustAssetIndex.ActorFileCount);

        foreach (AugustWornMesh mesh in AugustWornMeshCatalog.Entries)
        {
            Assert.True(AugustAssetIndex.Ships(mesh.MaleModelName), mesh.MaleModelName);
            Assert.True(AugustAssetIndex.Ships(mesh.FemaleModelName), mesh.FemaleModelName);
        }

        foreach (CharacterEquipmentAttachment starter in Starter(CharacterVisuals.Male)
            .Concat(Starter(CharacterVisuals.Female)))
        {
            Assert.True(AugustAssetIndex.Ships(starter.ModelName), starter.ModelName);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Edits 2-3 - the colour, per group and only when the table defines it.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The claim that makes edit 3 free: all nine roster weapon groups are already inside the
    /// transmitted payload, and the 26 worn wearables the filter discards are not - so the colour
    /// costs zero new bytes and docs/54 §I4's isolated experiment is untouched. Pinned against the
    /// real table when it is present.
    /// </summary>
    [Fact]
    public void TheNineWeaponGroupsAreOnTheWireAndTheFilteredWearablesAreNot()
    {
        if (!File.Exists(AppearanceSource))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Table();

        // Guard 1: the payload has not moved. This is the acceptance test for edits 2, 3 and 6.
        Assert.Equal(1_268_806, table.PayloadLength);
        Assert.Equal(1_448, table.AppearanceCount);
        Assert.Equal(22_409, table.ParameterCount);

        foreach (uint group in (uint[])[168, 29, 71, 745, 1747, 656, 1919, 639, 73])
        {
            Assert.True(table.DefinesShaderGroup(group), $"group {group}");
        }

        // 2112 Black Backpack and 2115 Green Backpack are two of the 26 the filter discards.
        Assert.True(AugustWornMeshCatalog.TryGet(2112, out AugustWornMesh black));
        Assert.True(AugustWornMeshCatalog.TryGet(2115, out AugustWornMesh green));
        Assert.False(table.DefinesShaderGroup(black.ShaderParameterGroupId));
        Assert.False(table.DefinesShaderGroup(green.ShaderParameterGroupId));

        // Group 0 is never "defined": it is the absence of a colourway, not a colourway.
        Assert.False(table.DefinesShaderGroup(0));

        // And the end-to-end consequence on the packet: the AK is coloured, the backpack is not.
        List<CharacterEquipmentAttachment> dress = Starter(CharacterVisuals.Male);
        AugustWornVisuals.Dress(
            dress,
            [
                new AugustWornItem(80, 2229, string.Empty),
                new AugustWornItem(10, 2112, string.Empty),
            ],
            CharacterVisuals.Male,
            table.AppearanceRowsFor,
            attachableBodySlots: null,
            definesShaderGroup: table.DefinesShaderGroup);

        Assert.Equal(71u, Assert.Single(dress, a => a.SlotId == 80).ShaderParameterGroupId);
        Assert.Equal(0u, Assert.Single(dress, a => a.SlotId == 10).ShaderParameterGroupId);
    }

    // ---------------------------------------------------------------------------------------
    // Edit 5 - identical-dress suppression.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AnIdenticalDressIsDroppedAndADifferentOneIsNot()
    {
        var suppressor = new AugustDressSuppressor();
        byte[] first = [0x94, 0x01, 1, 2, 3];
        byte[] same = [0x94, 0x01, 1, 2, 3];
        byte[] different = [0x94, 0x01, 1, 2, 4];

        Assert.True(suppressor.ShouldSend(first, enabled: true));
        Assert.False(suppressor.ShouldSend(same, enabled: true));
        Assert.False(suppressor.ShouldSend(same, enabled: true));
        Assert.True(suppressor.ShouldSend(different, enabled: true));
        Assert.False(suppressor.ShouldSend(different, enabled: true));

        Assert.Equal(2, suppressor.Sent);
        Assert.Equal(3, suppressor.Suppressed);
        Assert.Equal("2 sent / 3 suppressed", suppressor.Counters());
    }

    [Fact]
    public void ForgettingRestoresTheDressAndTheSwitchDisablesTheWholeThing()
    {
        var forgotten = new AugustDressSuppressor();
        byte[] dress = [0x94, 0x01, 9];
        Assert.True(forgotten.ShouldSend(dress, enabled: true));
        Assert.False(forgotten.ShouldSend(dress, enabled: true));

        // A manager burst, or a zone transition, forgets - and the next dress always goes out.
        forgotten.Forget();
        Assert.True(forgotten.ShouldSend(dress, enabled: true));

        // CRANBERRY_DRESS_SUPPRESS=0: every dress goes out, and the baseline is still tracked so
        // flipping the switch mid-session cannot compare against a stale packet.
        var off = new AugustDressSuppressor();
        Assert.True(off.ShouldSend(dress, enabled: false));
        Assert.True(off.ShouldSend(dress, enabled: false));
        Assert.Equal(0, off.Suppressed);
        Assert.False(off.ShouldSend(dress, enabled: true));
    }

    // ---------------------------------------------------------------------------------------
    // Edits 6-7 - the appearance rows and the grey census.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CensusUsesAscendingIdsRegardlessOfPacketOrderOrWildcardPriority()
    {
        var matching = new AugustAppearanceRow(1081, 3405, 9739, 1, 340);
        var wildcard = new AugustAppearanceRow(1597, 3405, 9653, 0, 340);
        Assert.Equal(matching, AugustSkinCensus.PickRow([wildcard, matching], 1));
        Assert.Equal(matching, AugustSkinCensus.PickRow([matching, wildcard], 1));
        Assert.Equal(wildcard, AugustSkinCensus.PickRow([matching, wildcard], 2));
        Assert.Equal(wildcard, AugustSkinCensus.PickRow([wildcard], 1));
    }

    [Fact]
    public void TheCensusNamesTheThreeOtherWaysAnItemCanBeWrong()
    {
        // C3: rows exist, none applies to this body.
        AugustSkinCensusRow noRow = ResolveWith(
            [new AugustAppearanceRow(1, 1, ModelIdOf("SurvivorFemale_Head_01.adr"), 2, 0)],
            CharacterVisuals.Male);
        Assert.Equal(AugustSkinVerdict.NoRowForThisBody, noRow.Verdict);

        // C4: the mesh we would send is in no pack. This one needs no table at all.
        AugustSkinCensusRow missing = AugustSkinCensus.Resolve(
            null, 1, CharacterVisuals.Male, "Not_A_Shipped_Asset.adr");
        Assert.Equal(AugustSkinVerdict.MeshDoesNotShip, missing.Verdict);

        // C2 without a table: a tintable mesh and no appearance row composites white.dds/grey.dds.
        AugustSkinCensusRow grey = AugustSkinCensus.Resolve(
            null, 1, CharacterVisuals.Male, "SurvivorMale_Chest_Shirt_TintTshirt.adr");
        Assert.Equal(AugustSkinVerdict.RendersGrey, grey.Verdict);

        // A non-tintable mesh with no row is not a defect: its albedo carries its colour.
        AugustSkinCensusRow fine = AugustSkinCensus.Resolve(
            null, 1, CharacterVisuals.Male, "SurvivorMale_Legs_Pants_LeggingsNoTint.adr");
        Assert.Equal(AugustSkinVerdict.Fine, fine.Verdict);

        // ...and a WEAPON with no row is (2026-09-03, docs/106 §12). This is the verdict the census
        // structurally could not reach before, and it is the owner's white AK-47.
        AugustSkinCensusRow whiteGun = AugustSkinCensus.Resolve(
            null, 1, CharacterVisuals.Male, "Weapon_M16A4_3P.adr");
        Assert.Equal(AugustSkinVerdict.RendersGrey, whiteGun.Verdict);

        Assert.Equal(CharacterVisuals.Female, AugustSkinCensus.BakedGenderOf("SurvivorFemale_X.adr"));
        Assert.Equal(CharacterVisuals.Male, AugustSkinCensus.BakedGenderOf("SurvivorMale_X.adr"));
        Assert.Equal(0u, AugustSkinCensus.BakedGenderOf("Weapon_AK47_3P.adr"));
        Assert.Equal(0u, AugustSkinCensus.BakedGenderOf(null));
    }

    /// <summary>
    /// The census against the real table: it resolves both bodies for every wearable, it never
    /// throws, and it changes nothing. The number of problems is deliberately NOT pinned - it is a
    /// measurement of the client's data, and pinning it would turn a diagnostic into a guard.
    /// </summary>
    [Fact]
    public void TheCensusRunsOverTheRealTableAndCostsNoBytes()
    {
        if (!File.Exists(AppearanceSource))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Table();
        int payloadBefore = table.PayloadLength;

        (int considered, IReadOnlyList<AugustSkinCensusRow> problems) = AugustSkinCensus.Run(table);

        Assert.True(considered > 500, $"only {considered} (item, body) pairs walked");
        Assert.Equal(payloadBefore, table.PayloadLength);
        Assert.All(problems, row => Assert.NotEqual(AugustSkinVerdict.Fine, row.Verdict));
        Assert.All(problems, row => Assert.NotEmpty(row.Problem));

        IReadOnlyList<string> lines = AugustSkinCensus.Describe(table);
        Assert.NotEmpty(lines);
        Assert.Contains("skin census:", lines[0]);
        Assert.True(lines.Count <= AugustSkinCensus.MaximumDetailLines + 2);
    }

    /// <summary>
    /// The two near-white weapons docs/69 named: their shader groups exist and are transmitted, so
    /// edit 3 can send them - and their meshes are NOT tintable, so the census correctly declines
    /// to call them grey. Both halves matter: the first is why the colour is free, the second is
    /// why the census does not cry wolf about the very items that started this.
    /// </summary>
    [Fact]
    public void TheAkAndTheShotgunHaveTransmittedGroupsAndAreNotFlaggedTintable()
    {
        if (!File.Exists(AppearanceSource))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Table();

        foreach ((uint item, uint group, string mesh) in
            (ReadOnlySpan<(uint, uint, string)>)[
                (2229u, 71u, "Weapon_AK47_3P.adr"),
                (1374u, 29u, "Weapons_PumpShotgun01_3P.adr"),
                (10u, 168u, "Weapon_M16A4_3P.adr")])
        {
            Assert.True(AugustWornMeshCatalog.TryGet(item, out AugustWornMesh worn));
            Assert.Equal(group, worn.ShaderParameterGroupId);
            Assert.Equal(mesh, worn.MaleModelName);
            Assert.True(table.DefinesShaderGroup(group));

            AugustSkinCensusRow row = AugustSkinCensus.Resolve(
                table, item, CharacterVisuals.Male, mesh);
            Assert.NotEqual(AugustSkinVerdict.MeshDoesNotShip, row.Verdict);
            Assert.NotEqual(AugustSkinVerdict.RendersGrey, row.Verdict);
        }

        // The measurement behind that last assertion, and the reason the colour may still not be
        // what he wants: the AR-15's and the pump shotgun's own groups composite nothing but the
        // client's untinted defaults. Sending the group is correct and free; it is not a promise
        // that the gun turns green.
        Assert.True(table.TintOfGroup(168).RendersUntinted);
        Assert.True(table.TintOfGroup(29).RendersUntinted);
        Assert.False(table.TintOfGroup(71).RendersUntinted);
    }

    // ---------------------------------------------------------------------------------------
    // The defect the census found: gender-ordered appearance rows.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The census earned its keep on its first run.</b> Every four-row hooded-chest entry in
    /// this catalogue collapses to a pair of <c>GENDER_ID = 0</c> wildcards - one naming
    /// <c>SurvivorMale_Chest_Hoodie_Up_Tintable.adr</c> and one the female mesh - in an order that
    /// varies per item (2375 male-first, 3405 female-first). Under the client rule the owner
    /// measured, the FIRST wildcard wins, so half of them were handing a character the other
    /// body's hoodie. 49 (item, body) pairs; nobody had ever seen it.
    /// </summary>
    [Fact]
    public void TheHoodedWildcardPairIsOrderedForTheBodyWearingIt()
    {
        if (!File.Exists(AppearanceSource))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Table();

        // 2375 puts the MALE mesh first in the packet; 3405 puts the FEMALE mesh first. Both pairs
        // are gender-0 wildcards, which is what makes the order load-bearing.
        Assert.Equal([1555u, 1556u], table.AppearanceRowsFor(2375));
        Assert.Equal([1597u, 1598u], table.AppearanceRowsFor(3405));

        // Unordered (CRANBERRY_GENDER_ROWS=0) the wire is exactly what it was before wave 8.
        Assert.Equal([1555u, 1556u], table.AppearanceRowsFor(2375, gender: 0));

        // Ordered, each body gets its own mesh first - and the ids are the same two ids, so this
        // costs no bytes.
        Assert.Equal([1555u, 1556u], table.AppearanceRowsFor(2375, CharacterVisuals.Male));
        Assert.Equal([1556u, 1555u], table.AppearanceRowsFor(2375, CharacterVisuals.Female));
        Assert.Equal([1598u, 1597u], table.AppearanceRowsFor(3405, CharacterVisuals.Male));
        Assert.Equal([1597u, 1598u], table.AppearanceRowsFor(3405, CharacterVisuals.Female));

        // The reordering is a repair and never a reshuffle: a pair whose first row already names
        // this body, or names neither body, is returned untouched. Item 10's two rows are both
        // Weapon_M16A4_3P.adr, which is neither body's mesh.
        Assert.Equal(table.AppearanceRowsFor(10), table.AppearanceRowsFor(10, CharacterVisuals.Male));
        Assert.Equal(table.AppearanceRowsFor(10), table.AppearanceRowsFor(10, CharacterVisuals.Female));
    }

    /// <summary>
    /// And the whole-catalogue consequence: with the ordering on, the census reports <b>no</b>
    /// wrong-body verdict at all. The count of remaining problems is not pinned - it measures the
    /// client's data, not this server's code - but "zero of this class" is the acceptance test for
    /// the repair.
    /// </summary>
    [Fact]
    public void RepairedHoodieGendersPreventWrongBodyWithEitherPacketOrder()
    {
        if (!File.Exists(AppearanceSource))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Table();

        (_, IReadOnlyList<AugustSkinCensusRow> before) =
            AugustSkinCensus.Run(table, orderRowsByGender: false);
        (_, IReadOnlyList<AugustSkinCensusRow> after) =
            AugustSkinCensus.Run(table, orderRowsByGender: true);

        Assert.DoesNotContain(before, row => row.Verdict == AugustSkinVerdict.WrongBodyMesh);
        Assert.DoesNotContain(after, row => row.Verdict == AugustSkinVerdict.WrongBodyMesh);

        // Nothing else moved: the repair reorders ids, it does not add or remove verdicts.
        Assert.Equal(
            before.Count(row => row.Verdict == AugustSkinVerdict.RendersGrey),
            after.Count(row => row.Verdict == AugustSkinVerdict.RendersGrey));
    }

    /// <summary>
    /// The owner's own census calls a mesh tintable when its name contains "Tint". The August
    /// catalogue contains <c>SurvivorMale_Legs_Pants_LeggingsNoTint.adr</c>, whose name says the
    /// opposite in the same three letters - and it was the census's one false positive on its
    /// first run over this data. The negation is checked first.
    /// </summary>
    [Fact]
    public void ANoTintMeshIsNotTintable()
    {
        Assert.True(AugustSkinCensus.IsTintable("SurvivorMale_Chest_Shirt_TintTshirt.adr"));
        Assert.True(AugustSkinCensus.IsTintable("SurvivorFemale_Back_Backpack_Military_Tintable.adr"));
        Assert.False(AugustSkinCensus.IsTintable("SurvivorMale_Legs_Pants_LeggingsNoTint.adr"));
        Assert.False(AugustSkinCensus.IsTintable(null));

        // CORRECTED 2026-09-03 (docs/106 §12). A weapon mesh IS tintable, and reading its file name
        // for the word "Tint" was the reason the census could never report a white gun: no weapon
        // mesh is called *_Tintable.adr, and docs/69 measured the AK-47 and pump-shotgun colour
        // maps at 30.4 % and 30.1 % near-white - luminance masks, ranks 1 and 2 of thirty.
        Assert.True(AugustSkinCensus.IsTintable("Weapon_AK47_3P.adr"));
        Assert.True(AugustSkinCensus.IsTintable("Weapons_PumpShotgun01_3P.adr"));
        Assert.True(AugustSkinCensus.IsWeaponMesh("Weapon_M16A4_3P.adr"));
        Assert.False(AugustSkinCensus.IsWeaponMesh("SurvivorMale_Chest_Shirt_TintTshirt.adr"));
    }

    // ---------------------------------------------------------------------------------------
    // Edit 12 - preview-only gating from the client's own column.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void PreviewOnlyGatingMatchesTheClientsOwnColumns()
    {
        // SkinItemSlot.txt: SELECT_PREVIEW_ONLY = 1 on 10 bodyarmor, 11 guns, 12 melee only.
        Assert.Equal([10u, 11u, 12u], AugustPreviewOnlySkins.PreviewOnlySkinSlots.Order());

        // SkinItemSlotItem.txt: SELECT_PREVIEW_ONLY = 1 on exactly these three prototypes.
        Assert.Equal(
            [2172u, 2570u, 2827u],
            AugustPreviewOnlySkins.PreviewOnlyCategoryPrototypes.Order());

        // 28, not 29: SkinItemSlotItem.txt numbers its rows 1..29 with id 8 absent.
        Assert.Equal(28, AugustPreviewOnlySkins.SkinSlotByCategoryPrototype.Count);

        foreach (uint previewOnly in (uint[])[2827, 2172, 2570, 2271, 3378])
        {
            Assert.True(AugustPreviewOnlySkins.IsPreviewOnly(previewOnly), $"{previewOnly}");
        }

        // Backpacks and performance footwear are 0 in BOTH sheets - they are the divergence.
        foreach (uint lobby in (uint[])[2038, 2121, 2125, 2215, 2563, 2158, 3250, 2109, 2209])
        {
            Assert.False(AugustPreviewOnlySkins.IsPreviewOnly(lobby), $"{lobby}");
        }
    }

    /// <summary>
    /// The divergence, and the fact that flipping it is one variable. Default true = no behaviour
    /// change in this wave; the owner decides, not this lane.
    /// </summary>
    [Fact]
    public void TheBackpackAndFootwearGateIsOneVariableWide()
    {
        Assert.True(new SkinOptions().GateBackpacksAndPerformanceFootwear);
        Assert.True(AugustWorldEquipmentPolicy.GateBackpacksAndPerformanceFootwear);

        AugustSkinCatalogEntry pack = Entry(2121, 10, "SurvivorMale_Back_Backpack_Military.adr");
        AugustSkinCatalogEntry shoes = Entry(2209, 5, "SurvivorMale_Feet_Conveys_Tintable.adr");
        AugustSkinCatalogEntry armour = Entry(2271, 100, "SurvivorMale_Armor_Kevlar_Basic_Velcro.adr");

        Assert.True(AugustWorldEquipmentPolicy.IsPickupOnly(pack));
        Assert.True(AugustWorldEquipmentPolicy.IsPickupOnly(shoes));
        Assert.True(AugustWorldEquipmentPolicy.IsPickupOnly(armour));

        try
        {
            AugustWorldEquipmentPolicy.GateBackpacksAndPerformanceFootwear = false;
            Assert.False(AugustWorldEquipmentPolicy.IsPickupOnly(pack));
            Assert.False(AugustWorldEquipmentPolicy.IsPickupOnly(shoes));

            // Body armour never moves: the client's own column says slot 10 bodyarmor is
            // preview-only, so it is pickup-only on either side of the switch.
            Assert.True(AugustWorldEquipmentPolicy.IsPickupOnly(armour));
        }
        finally
        {
            AugustWorldEquipmentPolicy.GateBackpacksAndPerformanceFootwear = true;
        }
    }

    /// <summary>
    /// The thirteen folded rows docs/80 edit 12 would have lost. The generated catalogue folds
    /// strips that share a <c>PARAM1</c> onto the first prototype for a physical slot, so a
    /// tactical Santa hat carries category 2158 (hats, preview-only 0) and a pair of Conveys
    /// carries 2209. Deleting the model heuristics - which the spec asked for - would have moved
    /// them into the lobby outfit.
    /// </summary>
    [Fact]
    public void TheFoldedHelmetAndFootwearRowsStayPickupOnly()
    {
        AugustSkinCatalogEntry[] folded =
        [
            .. AugustSkinCatalog.Apparel.Where(entry =>
                (entry.CategoryPrototypeId == 2158
                    && (AugustWorldEquipmentPolicy.IsHelmetModel(entry.MaleModelName)))
                || (entry.CategoryPrototypeId == 2209
                    && AugustWorldEquipmentPolicy.IsPerformanceFootwearModel(entry.MaleModelName))),
        ];

        Assert.NotEmpty(folded);
        Assert.All(folded, entry => Assert.True(
            AugustWorldEquipmentPolicy.IsPickupOnly(entry),
            $"{entry.CategoryPrototypeId}/{entry.MaleModelName}"));

        // ...and none of them is preview-only by the client's column alone, which is exactly why
        // the heuristics had to stay.
        Assert.All(folded, entry => Assert.False(
            AugustPreviewOnlySkins.IsPreviewOnly(entry.CategoryPrototypeId)));
    }

    // ---------------------------------------------------------------------------------------
    // Edit 13 - the switches.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EverySwitchDefaultsToTheBehaviourTheOwnerAskedFor()
    {
        var options = new SkinOptions();
        Assert.True(options.DressStowedWeapons);
        Assert.True(options.SendWornShaderGroups);
        Assert.True(options.SkinRetintOnly);
        Assert.True(options.SuppressIdenticalDress);
        Assert.True(options.RunSkinCensus);
        Assert.True(options.OrderAppearanceRowsByGender);
        Assert.True(options.GateBackpacksAndPerformanceFootwear);
        Assert.Null(options.WardrobeStoreRoot);

        // The one line he reads at boot names every one of them.
        string described = options.Describe();
        foreach (string fragment in (string[])
            ["stowed meshes on", "worn shader groups on", "re-tint-not-re-model on",
             "identical-dress suppression on", "gender-ordered appearance rows on",
             "census on", "wardrobe store off"])
        {
            Assert.Contains(fragment, described, StringComparison.Ordinal);
        }

        // ...and the hub hands the same record to the zone.
        Assert.True(new ZoneOptions().Skins.DressStowedWeapons);
        Assert.True(new ZoneOptions().Skins.RunSkinCensus);
    }

    private static List<CharacterEquipmentAttachment> Starter(uint gender) =>
        [.. CharacterVisuals
            .FromSelection(gender, headId: gender == CharacterVisuals.Female ? 3u : 1u, hairId: 1,
                skinToneId: 665, profileId: 0)
            .StarterOutfit];

    private static AugustSkinCatalogEntry Entry(uint category, uint slot, string model) =>
        new(category, RewardItemId: 0, AccountItemId: 0, slot, model, model, "Default");

    private static uint ModelIdOf(string fileName) =>
        AugustModelCatalog.Rows.First(row =>
            string.Equals(row.FileName, fileName, StringComparison.OrdinalIgnoreCase)).ModelId;

    private static AugustDynamicAppearanceTable Table()
    {
        // Wave 8's guard 1 is about the CAPTURED payload not moving, so the Cranberry-authored
        // rows are held off here (docs/106 addendum 2026-09-03). They are additive and are pinned
        // separately in AuthoredAppearanceTests.
        bool previous = AugustDynamicAppearanceTable.ApplyAuthoredRows;
        // D316: guard 1 is about the FILTERED captured payload not moving.
        bool previousWhole = AugustDynamicAppearanceTable.ShipWholeTable;
        try
        {
            AugustDynamicAppearanceTable.ShipWholeTable = false;
            AugustDynamicAppearanceTable.ApplyAuthoredRows = false;
            Assert.True(AugustDynamicAppearanceTable.TryLoad(
                AppearanceSource, out AugustDynamicAppearanceTable? table, out string status), status);
            return table!;
        }
        finally
        {
            AugustDynamicAppearanceTable.ApplyAuthoredRows = previous;
            AugustDynamicAppearanceTable.ShipWholeTable = previousWhole;
        }
    }

    /// <summary>
    /// The census row-selection rule, exercised against hand-built rows through the production
    /// resolver seam. A real, shipped, non-tintable mesh keeps verdicts C2 and C4 out of the way so
    /// that only the ordering is under test.
    /// </summary>
    private static AugustSkinCensusRow ResolveWith(AugustAppearanceRow[] rows, uint gender) =>
        AugustSkinCensus.Resolve(
            rows, table: null, rows[0].ItemDefinitionId, gender, "Weapon_AK47_3P.adr");
}
