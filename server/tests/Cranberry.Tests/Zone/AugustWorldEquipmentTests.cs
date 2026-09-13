using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

/// <summary>
/// In the appearance-statics collection because <c>AugustWorldEquipmentVisuals.RetintOnly</c> and
/// <c>AugustWorldEquipmentPolicy.GateBackpacksAndPerformanceFootwear</c> are process-wide, for the
/// same reason <c>AppearanceStaticsCollection</c> was created in wave 5.
/// </summary>
[Collection(Cranberry.Tests.Zone.Appearance.AppearanceStaticsCollection.Name)]
public sealed class AugustWorldEquipmentTests
{
    [Theory]
    [InlineData(2827, 1, 11, AugustWorldEquipmentKind.Helmet)]
    [InlineData(2172, 1, 11, AugustWorldEquipmentKind.Helmet)]
    [InlineData(2570, 3, 10, AugustWorldEquipmentKind.GhillieSuit)]
    [InlineData(2038, 10, 12, AugustWorldEquipmentKind.Backpack)]
    [InlineData(2121, 10, 12, AugustWorldEquipmentKind.Backpack)]
    [InlineData(2125, 10, 12, AugustWorldEquipmentKind.Backpack)]
    [InlineData(2215, 5, 13, AugustWorldEquipmentKind.FastFootwear)]
    [InlineData(2563, 5, 13, AugustWorldEquipmentKind.StealthFootwear)]
    [InlineData(2271, 100, 38, AugustWorldEquipmentKind.BodyArmor)]
    [InlineData(3378, 100, 38, AugustWorldEquipmentKind.BodyArmor)]
    public void PickupOnlyCategoriesResolveToTheirAugustEquipmentSlots(
        uint category,
        uint equipmentSlot,
        uint loadoutSlot,
        AugustWorldEquipmentKind kind)
    {
        Assert.True(AugustWorldEquipmentPolicy.TryGetRule(category, out var rule));
        Assert.Equal(equipmentSlot, rule.EquipmentSlotId);
        Assert.Equal(loadoutSlot, rule.LoadoutSlotId);
        Assert.Equal(kind, rule.Kind);
    }

    [Theory]
    [InlineData(2158, 1)]   // hats
    [InlineData(3250, 3)]   // shirts
    [InlineData(2109, 4)]   // trousers
    [InlineData(2209, 5)]   // ordinary/sturdy shoes
    [InlineData(2668, 2)]   // gloves
    [InlineData(2870, 28)]  // face
    [InlineData(2254, 29)]  // eyes
    public void OrdinaryWardrobeCategoriesAreNotPickupOnly(uint category, uint equipmentSlot)
    {
        var row = Entry(category, equipmentSlot, "SurvivorMale_Ordinary.adr");
        Assert.False(AugustWorldEquipmentPolicy.IsPickupOnly(row));
    }

    [Theory]
    [InlineData(1, "SurvivorMale_Head_Helmet_Motorcycle_Tintable.adr")]
    [InlineData(1, "SurvivorMale_Head_Mask_Unicorn.adr")]
    [InlineData(3, "SurvivorMale_Chest_GhillieSuit.adr")]
    [InlineData(5, "SurvivorMale_Feet_Conveys_Tintable.adr")]
    [InlineData(5, "SurvivorMale_Feet_SilentBoots.adr")]
    [InlineData(10, "SurvivorMale_Back_Backpack_Military.adr")]
    [InlineData(100, "SurvivorMale_Armor_Kevlar_Basic_Velcro.adr")]
    public void FoldedCatalogueRowsStillCannotLeakPickupGearIntoTheLobby(
        uint equipmentSlot,
        string model)
    {
        var folded = Entry(2158, equipmentSlot, model);
        Assert.True(AugustWorldEquipmentPolicy.IsPickupOnly(folded));
    }

    [Fact]
    public void EquipmentStateRejectsClothingAndSlotMismatches()
    {
        var state = new AugustWorldEquipmentState();
        var hat = Item(2158, 1, itemId: 2158, guid: 1, "Hat.adr");
        var backpackInHead = Item(2121, 1, itemId: 2121, guid: 2, "Backpack.adr");

        Assert.False(state.TryEquip(hat, out _, out string clothingReason));
        Assert.Contains("ordinary clothing", clothingReason);
        Assert.False(state.TryEquip(backpackInHead, out _, out string slotReason));
        Assert.Contains("slot 10", slotReason);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void OnePhysicalSlotContainsOnlyTheLatestRealItem()
    {
        var state = new AugustWorldEquipmentState();
        var first = Item(2121, 10, itemId: 2121, guid: 1, "MilitaryA.adr");
        var second = Item(2038, 10, itemId: 2038, guid: 2, "MansportB.adr");

        Assert.True(state.TryEquip(first, out AugustEquippedWorldItem? none, out _));
        Assert.Null(none);
        Assert.True(state.TryEquip(second, out AugustEquippedWorldItem? replaced, out _));
        Assert.Equal(first, replaced);
        Assert.Equal([second], state.Snapshot());

        Assert.True(state.TryUnequip(10, out AugustEquippedWorldItem? removed));
        Assert.Equal(second, removed);
        Assert.Empty(state.Snapshot());
    }

    /// <summary>
    /// A pickup-only preset renders only once its real base item exists - and, since wave 8
    /// (D53, docs/80 edit 4), only when it is a <b>re-tint</b> of that base rather than a
    /// <b>re-model</b> of it. This test used to take the first 2121 catalogue row, which is
    /// <c>..._Backpack_Military_Rasta.adr</c>, and assert that its mesh replaced the looted
    /// military pack's - precisely the owner's <i>"I equipped a military backpack and it gave me
    /// the smaller incorrect one"</i>. It now takes a pick of the same mesh, which is the case the
    /// transfer exists for; the declined case has its own test below.
    /// </summary>
    [Fact]
    public void PickupSkinAppearsOnlyOnAMatchingEquippedBaseItem()
    {
        const string militaryMesh = "SurvivorMale_Back_Backpack_Military.adr";
        AugustSkinCatalogEntry skin = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2121
            && string.Equals(entry.MaleModelName, militaryMesh, StringComparison.Ordinal));
        var wardrobe = new AugustWardrobeState();
        Assert.True(wardrobe.TryApply(
            Selection(skin),
            out AugustSkinCatalogEntry selected,
            out _,
            out _));

        CharacterVisuals visuals = CharacterVisuals.FromSelection(1, 1, 1, 1, 5);
        var equipment = new AugustWorldEquipmentState();

        IReadOnlyList<CharacterEquipmentAttachment> before =
            AugustWorldEquipmentVisuals.BuildAttachments(visuals, wardrobe, null, equipment);
        Assert.DoesNotContain(before, attachment => attachment.SlotId == 10);

        var baseItem = Item(
            category: 2121,
            equipmentSlot: 10,
            itemId: 2121,
            guid: 0x3100_0000_0000_0001,
            model: militaryMesh);
        Assert.True(equipment.TryEquip(baseItem, out _, out _));

        IReadOnlyList<CharacterEquipmentAttachment> after =
            AugustWorldEquipmentVisuals.BuildAttachments(visuals, wardrobe, null, equipment);
        CharacterEquipmentAttachment rendered = Assert.Single(
            after,
            attachment => attachment.SlotId == 10);
        Assert.Equal(selected.MaleModelName, rendered.ModelName);
        Assert.Equal(selected.TextureAlias, rendered.TextureAlias);

        Assert.True(equipment.TryUnequip(10, out _));
        IReadOnlyList<CharacterEquipmentAttachment> unequipped =
            AugustWorldEquipmentVisuals.BuildAttachments(visuals, wardrobe, null, equipment);
        Assert.DoesNotContain(unequipped, attachment => attachment.SlotId == 10);
    }

    /// <summary>
    /// <b>The military-backpack defect itself</b> (D53, docs/80 edit 4). Loot a plain military
    /// pack with the Rasta military pack selected in the same category: the silhouette on the wire
    /// must stay the looted item's, because the silhouette is the item's tier and the tier is what
    /// another player reads off you at 80 m. The owner's own server declines such a transfer
    /// WHOLE - the looted item keeps its model, its texture alias and its appearance rows - and so
    /// does this. Applying only the tint would have been a state he has never played.
    /// </summary>
    [Fact]
    public void ADifferentMeshedPickReModelsNothingAndReTintsNothing()
    {
        AugustSkinCatalogEntry rasta = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2121
            && entry.MaleModelName.Contains("Rasta", StringComparison.Ordinal));
        var wardrobe = new AugustWardrobeState();
        Assert.True(wardrobe.TryApply(Selection(rasta), out _, out _, out _));

        var equipment = new AugustWorldEquipmentState();
        var looted = Item(
            category: 2121,
            equipmentSlot: 10,
            itemId: 2121,
            guid: 0x3100_0000_0000_0002,
            model: "SurvivorMale_Back_Backpack_Military.adr");
        Assert.True(equipment.TryEquip(looted, out _, out _));

        CharacterEquipmentAttachment rendered = Assert.Single(
            AugustWorldEquipmentVisuals.BuildAttachments(
                CharacterVisuals.FromSelection(1, 1, 1, 1, 5), wardrobe, null, equipment),
            attachment => attachment.SlotId == 10);

        Assert.Equal(looted.ModelName, rendered.ModelName);
        Assert.Equal(looted.TextureAlias, rendered.TextureAlias);

        // ...and with the rule switched off (CRANBERRY_SKIN_REMODEL=1) the pre-wave-8 behaviour is
        // exactly recoverable, which is what makes this a decision the owner can test rather than
        // one this lane imposed on him.
        try
        {
            AugustWorldEquipmentVisuals.RetintOnly = false;
            CharacterEquipmentAttachment reModelled = Assert.Single(
                AugustWorldEquipmentVisuals.BuildAttachments(
                    CharacterVisuals.FromSelection(1, 1, 1, 1, 5), wardrobe, null, equipment),
                attachment => attachment.SlotId == 10);
            Assert.Equal(rasta.MaleModelName, reModelled.ModelName);
        }
        finally
        {
            AugustWorldEquipmentVisuals.RetintOnly = true;
        }
    }

    [Fact]
    public void ASelectedMilitarySkinDoesNotDressAnotherBackpackFamily()
    {
        AugustSkinCatalogEntry militarySkin = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2121 && !string.IsNullOrWhiteSpace(entry.MaleModelName));
        var wardrobe = new AugustWardrobeState();
        Assert.True(wardrobe.TryApply(Selection(militarySkin), out _, out _, out _));

        var equipment = new AugustWorldEquipmentState();
        var mansport = Item(
            category: 2038,
            equipmentSlot: 10,
            itemId: 2038,
            guid: 3,
            model: "SurvivorMale_Back_Backpack_Mansport_Tintable.adr");
        Assert.True(equipment.TryEquip(mansport, out _, out _));

        IReadOnlyList<CharacterEquipmentAttachment> attachments =
            AugustWorldEquipmentVisuals.BuildAttachments(
                CharacterVisuals.FromSelection(1, 1, 1, 1, 5),
                wardrobe,
                null,
                equipment);

        CharacterEquipmentAttachment rendered = Assert.Single(
            attachments,
            attachment => attachment.SlotId == 10);
        Assert.Equal(mansport.ModelName, rendered.ModelName);
        Assert.Equal(mansport.TextureAlias, rendered.TextureAlias);
    }

    private static AugustSkinCatalogEntry Entry(
        uint category,
        uint equipmentSlot,
        string model) =>
        new(category, RewardItemId: 0, AccountItemId: 0, equipmentSlot, model, model, "Default");

    private static AugustEquippedWorldItem Item(
        uint category,
        uint equipmentSlot,
        uint itemId,
        ulong guid,
        string model) =>
        new(itemId, guid, category, equipmentSlot, model);

    private static SkinItemSelectionRequest Selection(AugustSkinCatalogEntry entry) =>
        new(
            SkinItemSelectionRequest.RequestSetSkinItemByItemId,
            Field1: 1,
            Field2: 0,
            SlotType: 1,
            entry.CategoryPrototypeId,
            entry.AccountItemId);
}
