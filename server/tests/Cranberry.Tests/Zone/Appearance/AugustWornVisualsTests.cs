using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// docs/54 §3 and I3 — symptom B, <i>"I am able to pick up items and some go into the slots but
/// that's it. They don't show on me."</i>
/// <para>
/// The play-test's own wire is the specification these tests hold to: five
/// <c>SetCharacterEquipmentWithSlots</c> packets, five pickups, and a constant <b>5 meshes</b>
/// throughout. Each test below is one of the reasons that number could not move.
/// </para>
/// </summary>
[Collection(AppearanceStaticsCollection.Name)]
public sealed class AugustWornVisualsTests
{
    /// <summary>The starter outfit: slots 2, 3, 4, 5 and 7, five meshes (docs/54 §3.1).</summary>
    private static List<CharacterEquipmentAttachment> StarterDress(uint gender) =>
        [.. CharacterVisuals.FromSelection(gender, headId: 1, hairId: 1, skinToneId: 665, profileId: 0)
            .StarterOutfit];

    private static AugustWornItem Worn(uint slot, uint item, string sheetModel = "") =>
        new(slot, item, sheetModel);

    // ---------------------------------------------------------------------------------------
    // The failure the owner saw, and its repair.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The exact pickup sequence of the 09:10 session. Wave 4: 5 meshes after five pickups.
    /// Wave 5: 5 + 3, with the two long guns on slots 76 and 77 still undressed. <b>Wave 8: 5 + 5</b>
    /// — docs/80 edit 1 lifts the stowed-weapon deferral, which is the owner's own
    /// <i>"they don't show on me"</i> for every firearm (docs/74 probe W1 measured slots 76/77/80
    /// carrying an equipment-slot row and no attachment element at all).
    /// </summary>
    [Fact]
    public void ThePlayTestPickupsStopSayingFiveMeshes()
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);
        Assert.Equal(5, dress.Count);

        int dressed = AugustWornVisuals.Dress(
            dress,
            [
                Worn(slot: 1, item: 2106),    // Maroon Trucker Cap
                Worn(slot: 76, item: 10),     // AR-15   — Weapon_M16A4_3P.adr
                Worn(slot: 77, item: 1374),   // Shotgun — Weapons_PumpShotgun01_3P.adr
                Worn(slot: 10, item: 2112),   // Black Backpack
                Worn(slot: 100, item: 2271),  // Laminated Tactical Body Armor
            ],
            CharacterVisuals.Male,
            appearanceRowsFor: null);

        Assert.Equal(5, dressed);
        Assert.Equal(10, dress.Count);
        Assert.Equal(
            "Weapon_M16A4_3P.adr",
            Assert.Single(dress, attachment => attachment.SlotId == 76).ModelName);
        Assert.Equal(
            "Weapons_PumpShotgun01_3P.adr",
            Assert.Single(dress, attachment => attachment.SlotId == 77).ModelName);

        // SORTED BY SLOT ID, re-baselined by the wave-5 verify pass. Dress used to append, leaving
        // 2,3,4,5,7,1,10,100 — an ordering no capture of this client has ever carried, on the one
        // packet the owner reports not working. Both sibling builders return
        // `attachments.OrderBy(a => a.SlotId)` (AugustWardrobe.BuildAttachments:304,
        // AugustWorldEquipment.BuildAttachments:275) and the starter dress arrives sorted, so
        // appending made a helmet pickup the first thing in this server to break the convention.
        // The assertion is exactly as strict as it was; only the expected order moved, and it moved
        // TOWARDS the shape every accepted packet had.
        Assert.Equal(
            [1u, 2u, 3u, 4u, 5u, 7u, 10u, 76u, 77u, 100u],
            dress.Select(attachment => attachment.SlotId));
    }

    /// <summary>
    /// The owner's acceptance check: a helmet on the head, a backpack on the back, a vest on the
    /// chest — with the client's own gendered meshes behind them.
    /// </summary>
    [Theory]
    [InlineData(CharacterVisuals.Male, 1u, 2172u, "SurvivorMale_Head_Helmet_Tactical_ChipsScratches.adr")]
    [InlineData(CharacterVisuals.Female, 1u, 2172u, "SurvivorFemale_Head_Helmet_Tactical_ChipsScratches.adr")]
    [InlineData(CharacterVisuals.Male, 10u, 2124u, "SurvivorMale_Back_Backpack_Military.adr")]
    [InlineData(CharacterVisuals.Female, 10u, 2124u, "SurvivorFemale_Back_Backpack_Military.adr")]
    [InlineData(CharacterVisuals.Male, 100u, 2271u, "SurvivorMale_Armor_Kevlar_Basic_Velcro.adr")]
    [InlineData(CharacterVisuals.Female, 100u, 2271u, "SurvivorFemale_Armor_Kevlar_Basic_Velcro.adr")]
    public void PickedUpGearAttachesTheClientsOwnGenderedMesh(
        uint gender,
        uint bodySlotId,
        uint itemDefinitionId,
        string expectedMesh)
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(gender);

        Assert.Equal(1, AugustWornVisuals.Dress(
            dress,
            [Worn(bodySlotId, itemDefinitionId)],
            gender,
            appearanceRowsFor: null));

        CharacterEquipmentAttachment worn = Assert.Single(
            dress,
            attachment => attachment.SlotId == bodySlotId);
        Assert.Equal(expectedMesh, worn.ModelName);
        Assert.True(AugustModelCatalog.CanLoad(
            gender == CharacterVisuals.Female
                ? Mesh(itemDefinitionId).FemaleModelId
                : Mesh(itemDefinitionId).MaleModelId));
    }

    // ---------------------------------------------------------------------------------------
    // The wire shape. docs/54 §1.1 and §3.3: everything but the mesh stays at the safe form.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ADressedAttachmentCarriesTheSafeFormOfEveryOtherField()
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);
        AugustWornVisuals.Dress(dress, [Worn(1, 2172)], CharacterVisuals.Male, null);

        CharacterEquipmentAttachment helmet = Assert.Single(
            dress,
            attachment => attachment.SlotId == 1);

        // §1.1: every .adr in the roster has one alias, "default0", and an empty <TintAliases/>;
        // any other value here is a guess. §1.2: the item tint field indexes 15 rows of
        // PlanetSide 2 empire camouflage and must stay 0.
        Assert.Equal("Default", helmet.TextureAlias);
        Assert.Equal("Default", helmet.TintAlias);
        Assert.Equal("#", helmet.DecalAlias);
        Assert.Equal(0u, helmet.TintId);
        Assert.Equal(0u, helmet.CompositeEffectId);
        Assert.Equal(0u, helmet.EffectId);
        Assert.False(helmet.Flag);

        // §4 / docs/80 edit 3: the shader group is the only live colour lever, and it now travels
        // when — and only when — the caller's predicate says the transmitted appearance table
        // defines parameters for it. No predicate (this call) means 0, which is what every build
        // before wave 8 sent for everything.
        Assert.Equal(0u, helmet.ShaderParameterGroupId);
        Assert.Null(helmet.AppearanceIds);
    }

    /// <summary>
    /// The bytes themselves. A picked-up helmet's attachment, serialised exactly as
    /// <c>SetCharacterEquipmentWithSlots</c> will serialise it: the mesh, three inert alias
    /// strings, five zeroed dwords around body slot 1, an empty appearance-id list and the
    /// trailing flag. 108 bytes, and the only field that is not zero or a constant is the mesh —
    /// which is the whole of the repair.
    /// </summary>
    [Fact]
    public void APickedUpHelmetSerialisesToTheseExactBytes()
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);
        AugustWornVisuals.Dress(dress, [Worn(1, 2172)], CharacterVisuals.Male, null);

        using var writer = new PacketWriter();
        Assert.Single(dress, attachment => attachment.SlotId == 1).WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(108, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "340000005375727669766F724D616C655F486561645F48656C6D65745F5461637469"
                + "63616C5F43686970735363726174636865732E6164720700000044656661756C7407"
                + "00000044656661756C7401000000230000000000000000000000000100000000"
                + "0000000000000000"),
            bytes);
    }

    /// <summary>
    /// docs/80 edit 3. The colour lever is now a PER-GROUP predicate, not a process-wide bool, and
    /// that is what makes it safe to have on by default: the nine roster weapons are inside the
    /// appearance filter's <c>wantedItems</c> so their groups are already on the wire, while the 26
    /// worn wearables the filter discards must keep 0 or the client is handed a group it holds no
    /// parameters for (docs/54 §I4, which stays an untouched separate experiment).
    /// </summary>
    [Fact]
    public void TheShaderGroupTravelsOnlyWhenTheTableDefinesIt()
    {
        Assert.True(AugustWornMeshCatalog.TryGet(2112, out AugustWornMesh blackBackpack));
        Assert.True(AugustWornMeshCatalog.TryGet(2115, out AugustWornMesh greenBackpack));

        // Same mesh, different colourway: nothing but the group tells them apart (docs/54 §1.3).
        Assert.Equal(blackBackpack.MaleModelName, greenBackpack.MaleModelName);
        Assert.Equal(805u, blackBackpack.ShaderParameterGroupId);
        Assert.Equal(804u, greenBackpack.ShaderParameterGroupId);

        // A table that defines the three long-gun groups and nothing else.
        static bool Defines(uint group) => group is 168 or 29 or 71;

        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);
        AugustWornVisuals.Dress(
            dress,
            [
                Worn(76, 10),     // AR-15,  group 168 — defined
                Worn(77, 1374),   // Pump shotgun, group 29 — defined
                Worn(80, 2229),   // AK-47,  group 71 — defined
                Worn(10, 2112),   // Black backpack, group 805 — NOT defined
            ],
            CharacterVisuals.Male,
            appearanceRowsFor: null,
            attachableBodySlots: null,
            definesShaderGroup: Defines);

        Assert.Equal(168u, Group(dress, 76));
        Assert.Equal(29u, Group(dress, 77));
        Assert.Equal(71u, Group(dress, 80));
        Assert.Equal(0u, Group(dress, 10));

        // And with no predicate at all, nothing carries a group.
        List<CharacterEquipmentAttachment> plain = StarterDress(CharacterVisuals.Male);
        AugustWornVisuals.Dress(plain, [Worn(76, 10)], CharacterVisuals.Male, null);
        Assert.Equal(0u, Group(plain, 76));

        static uint Group(List<CharacterEquipmentAttachment> dress, uint slot) =>
            Assert.Single(dress, attachment => attachment.SlotId == slot).ShaderParameterGroupId;
    }

    [Fact]
    public void AppearanceRowsAreCarriedExactlyAsTheWardrobePathCarriesThem()
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);
        AugustWornVisuals.Dress(
            dress,
            [Worn(1, 2172)],
            CharacterVisuals.Male,
            item => item == 2172 ? [392u, 393u] : []);

        Assert.Equal(
            [392u, 393u],
            Assert.Single(dress, attachment => attachment.SlotId == 1).AppearanceIds!);
    }

    // ---------------------------------------------------------------------------------------
    // Guard 5 (docs/45): body slot 7 is the active hand.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheActiveHandIsNeverDressedFromAPickup()
    {
        Assert.DoesNotContain(
            AugustWornVisuals.ActiveHandSlotId,
            AugustWornVisuals.AttachableBodySlots);

        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);
        CharacterEquipmentAttachment fists = Assert.Single(
            dress,
            attachment => attachment.SlotId == AugustWornVisuals.ActiveHandSlotId);

        Assert.Equal(0, AugustWornVisuals.Dress(
            dress,
            [Worn(AugustWornVisuals.ActiveHandSlotId, 10)],
            CharacterVisuals.Male,
            null));

        // Untouched: still five meshes, and slot 7 still carries the client's own empty-hand item.
        Assert.Equal(5, dress.Count);
        Assert.Same(fists, Assert.Single(
            dress,
            attachment => attachment.SlotId == AugustWornVisuals.ActiveHandSlotId));
    }

    /// <summary>
    /// docs/80 edit 1, and this test used to assert the OPPOSITE. docs/54 §3.3 held 3/4/5 and
    /// 76/77/78/106 back for "a separate observed step" on the grounds that no capture had carried
    /// an attachment for them; docs/74 W1 then measured exactly those slots bound with no mesh,
    /// which is the owner's <i>"they don't show on me"</i>, and D53 makes his own server's census
    /// of <c>SetCharacterEquipmentSlot 76 ×26, 77 ×16, 80 ×8</c> citable. This is the separate
    /// observed step.
    /// </summary>
    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(76u)]
    [InlineData(77u)]
    [InlineData(78u)]
    [InlineData(106u)]
    public void SlotsHeldForALaterStepAreNowDressed(uint bodySlotId)
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);

        Assert.Equal(1, AugustWornVisuals.Dress(
            dress,
            [Worn(bodySlotId, 2172)],
            CharacterVisuals.Male,
            null));
        Assert.Equal(
            "SurvivorMale_Head_Helmet_Tactical_ChipsScratches.adr",
            Assert.Single(dress, attachment => attachment.SlotId == bodySlotId).ModelName);
    }

    /// <summary>
    /// And the rollback still holds the same seven back, so <c>CRANBERRY_STOWED_MESHES=0</c> puts
    /// the wire back to exactly what docs/74 W1 measured.
    /// </summary>
    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(76u)]
    [InlineData(77u)]
    [InlineData(78u)]
    [InlineData(106u)]
    public void TheRollbackAllowListStillHoldsThemBack(uint bodySlotId)
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);
        int before = dress.Count;

        Assert.Equal(0, AugustWornVisuals.Dress(
            dress,
            [Worn(bodySlotId, 2172)],
            CharacterVisuals.Male,
            appearanceRowsFor: null,
            attachableBodySlots: AugustWornVisuals.AdditiveBodySlots));
        Assert.Equal(before, dress.Count);
    }

    /// <summary>
    /// The allow-list is the client's own <c>EquipmentSlotDefinitions</c> rule, not a literal:
    /// every <c>IS_EQUIPMENT</c> row outside the vehicle hardpoint group, minus the active hand.
    /// </summary>
    [Fact]
    public void TheAttachableSlotsAreTheSheetsOwnEquipmentRows()
    {
        Assert.Equal([1u, 10u, 100u], AugustWornVisuals.AdditiveBodySlots.Order());

        IReadOnlySet<uint> attachable = AugustWornVisuals.AttachableBodySlots;

        // The slots that matter, named one at a time so a regression says which.
        foreach (uint slot in (uint[])[1, 2, 3, 4, 5, 10, 28, 29, 76, 77, 78, 79, 80, 81, 82, 83,
            84, 100, 101, 103, 104, 105, 106, 107, 108, 109, 110])
        {
            Assert.Contains(slot, attachable);
        }

        // Never the active hand (docs/45 guard 5).
        Assert.DoesNotContain(7u, attachable);

        // Never a vehicle hardpoint (GROUP_ID 1).
        foreach (uint hardpoint in (uint[])[30, 31, 32, 33, 41, 50, 60, 73])
        {
            Assert.DoesNotContain(hardpoint, attachable);
        }

        // Never a row the sheet marks IS_EQUIPMENT = 0 — 85 is the one EquipSlotItemClasses still
        // lists as a stow candidate, and InventorySlots.g.cs refuses it for the same reason.
        foreach (uint notEquipment in (uint[])[15, 27, 85])
        {
            Assert.DoesNotContain(notEquipment, attachable);
        }

        Assert.Equal(
            Cranberry.Zone.Inventory.EquipmentSlotTable.All
                .Count(slot => slot.IsEquipment && slot.GroupId == 0 && slot.Id != 7),
            attachable.Count);
    }

    // ---------------------------------------------------------------------------------------
    // The two resolvers, and the fact that between them nothing in the roster is left out.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheAppearanceCatalogueWinsAndTheSheetIsTheFallback()
    {
        // 2172 has an appearance row; a stale MODEL_NAME must not beat it.
        Assert.True(AugustWornVisuals.TryResolveMesh(
            2172, CharacterVisuals.Male, "Something_Else.adr", out string fromCatalogue, out _));
        Assert.Equal("SurvivorMale_Head_Helmet_Tactical_ChipsScratches.adr", fromCatalogue);

        // 83 (Machete) has no appearance row and does have a MODEL_NAME, with the client's own
        // <gender> token in it.
        Assert.False(AugustWornMeshCatalog.TryGet(83, out _));
        Assert.True(AugustWornVisuals.TryResolveMesh(
            83, CharacterVisuals.Female, "Survivor<gender>_Test.adr", out string fromSheet, out _));
        Assert.Equal("SurvivorFemale_Test.adr", fromSheet);

        // Neither resolver has an answer: an empty mesh stays a skipped attachment, never an empty
        // string on the wire (docs/54 §3.3).
        Assert.False(AugustWornVisuals.TryResolveMesh(
            1429, CharacterVisuals.Male, string.Empty, out string none, out uint group));
        Assert.Equal(string.Empty, none);
        Assert.Equal(0u, group);
    }

    [Fact]
    public void EveryMeshInTheCatalogueIsOneTheClientCanActuallyLoad()
    {
        Assert.Equal(54, AugustWornMeshCatalog.Entries.Count);
        // 72 before D273 added the Crossbow to the ground roster. Its own worn row is skipped:
        // the 1087 capture names ModelId 10935, which is not an August Models.txt row, so the
        // crossbow is UNCOVERED here exactly as an item with no captured row would be
        // (tools/appearance/gen-appearance-catalog.py NO_AUGUST_MESH).
        // September 8: bandages no longer belong to the naturally spawned roster.
        Assert.Equal(72, AugustWornMeshCatalog.RosterItemCount);

        foreach (AugustWornMesh mesh in AugustWornMeshCatalog.Entries)
        {
            Assert.True(
                AugustModelCatalog.CanLoad(mesh.MaleModelId),
                AugustModelGuard.Explain(mesh.MaleModelId));
            Assert.True(
                AugustModelCatalog.CanLoad(mesh.FemaleModelId),
                AugustModelGuard.Explain(mesh.FemaleModelId));
            Assert.Equal(
                AugustModelCatalog.FileNameFor(mesh.MaleModelId),
                mesh.ModelNameFor(CharacterVisuals.Male));
            Assert.Equal(
                AugustModelCatalog.FileNameFor(mesh.FemaleModelId),
                mesh.ModelNameFor(CharacterVisuals.Female));
        }
    }

    /// <summary>
    /// docs/54 §5.1: appearance rows 181 and 182 both carry <c>GenderId 2</c>, so item 10 resolves
    /// no male row in the source. Both name the same model, so the generator's fallback is exact —
    /// this pins that it stayed exact rather than becoming a guess.
    /// </summary>
    [Fact]
    public void TheArFifteensDuplicatedGenderRowStillResolvesOneMesh()
    {
        AugustWornMesh rifle = Mesh(10);
        Assert.Equal("Weapon_M16A4_3P.adr", rifle.MaleModelName);
        Assert.Equal("Weapon_M16A4_3P.adr", rifle.FemaleModelName);
        Assert.Equal(9591u, rifle.MaleModelId);
        Assert.Equal(168u, rifle.ShaderParameterGroupId);
    }

    [Fact]
    public void DressIgnoresAnItemWithNeitherResolver()
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);

        // 1429 (.223 Round) is ammunition: no appearance row and no MODEL_NAME. It cannot reach a
        // body slot in practice, and if it ever did it must not put an empty mesh on the wire.
        Assert.Equal(0, AugustWornVisuals.Dress(
            dress, [Worn(1, 1429)], CharacterVisuals.Male, null));
        Assert.Equal(5, dress.Count);
    }

    [Fact]
    public void DressingASlotTwiceLeavesOneAttachment()
    {
        List<CharacterEquipmentAttachment> dress = StarterDress(CharacterVisuals.Male);
        AugustWornVisuals.Dress(dress, [Worn(1, 2172)], CharacterVisuals.Male, null);
        AugustWornVisuals.Dress(dress, [Worn(1, 2168)], CharacterVisuals.Male, null);

        CharacterEquipmentAttachment head = Assert.Single(
            dress,
            attachment => attachment.SlotId == 1);
        Assert.Equal(Mesh(2168).MaleModelName, head.ModelName);
        Assert.Equal(6, dress.Count);
    }

    private static AugustWornMesh Mesh(uint itemDefinitionId)
    {
        Assert.True(AugustWornMeshCatalog.TryGet(itemDefinitionId, out AugustWornMesh mesh));
        return mesh;
    }
}
