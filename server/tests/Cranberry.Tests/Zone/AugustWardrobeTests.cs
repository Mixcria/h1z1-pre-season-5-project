using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone;

public sealed class AugustWardrobeTests
{
    private static readonly CharacterVisuals Male = CharacterVisuals.FromSelection(
        CharacterVisuals.Male,
        headId: 1,
        hairId: 1,
        skinToneId: 665,
        profileId: 0);

    private static readonly CharacterEquipmentAttachment[] BareAppearance =
    [
        .. Male.StarterOutfit,
        new("SurvivorMale_Head_01.adr", 15, ShaderParameterGroupId: 665),
        new("SurvivorMale_Hair_MediumMessy.adr", 27),
    ];

    [Fact]
    public void EmptyWardrobeProjectsStarterOutfitAndSelectedFeatures()
    {
        var wardrobe = new AugustWardrobeState();

        IReadOnlyList<CharacterEquipmentAttachment> attachments =
            AugustWardrobeVisuals.BuildAttachments(Male, wardrobe, appearance: null);

        Assert.Equal(BareAppearance, attachments);
        Assert.Empty(wardrobe.Snapshot());
    }

    [Fact]
    public void ExplicitHatSelectionIsTheOnlyAddedMenuAttachment()
    {
        var wardrobe = new AugustWardrobeState();
        AugustSkinCatalogEntry hat = Apparel(rewardItemId: 2484);

        Apply(wardrobe, hat, requestedCategory: 2158);

        IReadOnlyList<CharacterEquipmentAttachment> attachments =
            AugustWardrobeVisuals.BuildAttachments(Male, wardrobe, appearance: null);
        CharacterEquipmentAttachment wornHat = Assert.Single(
            attachments,
            attachment => attachment.SlotId == 1);

        Assert.Equal(hat.MaleModelName, wornHat.ModelName);
        Assert.Equal(BareAppearance.Length + 1, attachments.Count);
        Assert.Equal(
            BareAppearance,
            attachments.Where(attachment => attachment.SlotId != 1).ToArray());
    }

    [Fact]
    public void NewChoiceInTheSameCategoryReplacesTheOldChoice()
    {
        var wardrobe = new AugustWardrobeState();
        AugustSkinCatalogEntry first = Apparel(rewardItemId: 2484);
        AugustSkinCatalogEntry second = AugustSkinCatalog.Apparel.First(entry =>
            entry.CategoryPrototypeId == 2158
            && entry.RewardItemId != first.RewardItemId
            && entry.MaleModelName != first.MaleModelName);

        Apply(wardrobe, first, requestedCategory: 2158);
        Apply(wardrobe, second, requestedCategory: 2158);

        AugustSkinCatalogEntry stored = Assert.Single(wardrobe.Snapshot());
        Assert.Equal(second.RewardItemId, stored.RewardItemId);

        CharacterEquipmentAttachment worn = Assert.Single(
            AugustWardrobeVisuals.BuildAttachments(Male, wardrobe, appearance: null),
            attachment => attachment.SlotId == 1);
        Assert.Equal(second.MaleModelName, worn.ModelName);
    }

    [Theory]
    [InlineData(2827u, 2827u, 1u)]   // motorcycle-helmet strip, folded onto head in the catalogue
    [InlineData(2172u, 2172u, 1u)]   // tactical-helmet strip
    [InlineData(2570u, 2570u, 3u)]   // ghillie strip, folded onto chest
    [InlineData(2215u, 2215u, 5u)]   // running-shoe strip, folded onto feet
    public void NativeAlternateStripFromClickIsPreserved(
        uint rewardItemId,
        uint requestedCategory,
        uint expectedEquipmentSlot)
    {
        var wardrobe = new AugustWardrobeState();
        AugustSkinCatalogEntry generated = Apparel(rewardItemId);

        Apply(wardrobe, generated, requestedCategory);

        AugustSkinCatalogEntry stored = Assert.Single(wardrobe.Snapshot());
        Assert.Equal(requestedCategory, stored.CategoryPrototypeId);
        Assert.Equal(expectedEquipmentSlot, stored.EquipmentSlotId);
        Assert.Equal(generated.RewardItemId, stored.RewardItemId);
        Assert.Equal(generated.AccountItemId, stored.AccountItemId);
    }

    [Fact]
    public void CategoryCannotBeSpoofedAcrossPhysicalEquipmentSlots()
    {
        var wardrobe = new AugustWardrobeState();
        AugustSkinCatalogEntry hat = Apparel(rewardItemId: 2484);
        SkinItemSelectionRequest request = Request(hat, requestedCategory: 2271);

        bool accepted = wardrobe.TryApply(request, out _, out _, out string reason);

        Assert.False(accepted);
        Assert.Contains("not requested category 2271", reason);
        Assert.Empty(wardrobe.Snapshot());
    }

    [Fact]
    public void PickupOnlyPresetsRemainSelectedButNeverDressTheLobbyBody()
    {
        var wardrobe = new AugustWardrobeState();

        Apply(wardrobe, Apparel(rewardItemId: 2827), requestedCategory: 2827); // helmet
        Apply(wardrobe, Apparel(rewardItemId: 2570), requestedCategory: 2570); // ghillie
        Apply(wardrobe, Apparel(rewardItemId: 2051), requestedCategory: 2038); // backpack
        Apply(wardrobe, Apparel(rewardItemId: 2215), requestedCategory: 2215); // running shoes
        Apply(wardrobe, Apparel(rewardItemId: 2271), requestedCategory: 2271); // body armour

        Assert.Equal(5, wardrobe.Snapshot().Count);
        Assert.All(wardrobe.Snapshot(), entry => Assert.True(AugustWardrobeCatalog.IsPickupOnly(entry)));
        Assert.Empty(wardrobe.WornSnapshot());
        Assert.Equal(
            BareAppearance,
            AugustWardrobeVisuals.BuildAttachments(Male, wardrobe, appearance: null));
    }

    [Fact]
    public void HatAndHelmetPresetsCoexistButOnlyTheHatIsWornBeforePickup()
    {
        var wardrobe = new AugustWardrobeState();
        AugustSkinCatalogEntry hat = Apparel(rewardItemId: 2484);

        Apply(wardrobe, hat, requestedCategory: 2158);
        Apply(wardrobe, Apparel(rewardItemId: 2827), requestedCategory: 2827);
        Apply(wardrobe, Apparel(rewardItemId: 2172), requestedCategory: 2172);

        Assert.Equal([2158u, 2172u, 2827u],
            wardrobe.Snapshot().Select(entry => entry.CategoryPrototypeId));
        Assert.Equal([2158u],
            wardrobe.WornSnapshot().Select(entry => entry.CategoryPrototypeId));

        CharacterEquipmentAttachment wornHead = Assert.Single(
            AugustWardrobeVisuals.BuildAttachments(Male, wardrobe, appearance: null),
            attachment => attachment.SlotId == 1);
        Assert.Equal(hat.MaleModelName, wornHead.ModelName);
    }

    [Fact]
    public void BackpackFamilyPresetsCoexistWithoutCreatingAStarterBackpack()
    {
        var wardrobe = new AugustWardrobeState();

        Apply(wardrobe, AugustSkinCatalog.Apparel.First(entry => entry.CategoryPrototypeId == 2038), 2038);
        Apply(wardrobe, AugustSkinCatalog.Apparel.First(entry => entry.CategoryPrototypeId == 2121), 2121);
        Apply(wardrobe, AugustSkinCatalog.Apparel.First(entry => entry.CategoryPrototypeId == 2125), 2125);

        Assert.Equal(3, wardrobe.Snapshot().Count);
        Assert.DoesNotContain(
            AugustWardrobeVisuals.BuildAttachments(Male, wardrobe, appearance: null),
            attachment => attachment.SlotId == 10);
    }

    [Fact]
    public void LegacyFoldedPickupModelsAreStillGatedByTheirPhysicalMeaning()
    {
        AugustSkinCatalogEntry helmet = Apparel(rewardItemId: 2827)
            with { CategoryPrototypeId = 2158 };
        AugustSkinCatalogEntry runningShoes = Apparel(rewardItemId: 2215)
            with { CategoryPrototypeId = 2209 };
        AugustSkinCatalogEntry ghillie = Apparel(rewardItemId: 2570)
            with { CategoryPrototypeId = 3250 };
        AugustSkinCatalogEntry hat = Apparel(rewardItemId: 2484);

        Assert.True(AugustWardrobeCatalog.IsPickupOnly(helmet));
        Assert.True(AugustWardrobeCatalog.IsPickupOnly(runningShoes));
        Assert.True(AugustWardrobeCatalog.IsPickupOnly(ghillie));
        Assert.False(AugustWardrobeCatalog.IsPickupOnly(hat));
    }

    [Fact]
    public void BodySnapshotLetsAnActuallyEquippedPickupPresetSupersedeLobbyClothing()
    {
        var wardrobe = new AugustWardrobeState();
        Apply(wardrobe, Apparel(rewardItemId: 2484), requestedCategory: 2158);
        Apply(wardrobe, Apparel(rewardItemId: 2827), requestedCategory: 2827);
        Apply(wardrobe, Apparel(rewardItemId: 2051), requestedCategory: 2038);

        Assert.Equal([2158u],
            wardrobe.WornSnapshot().Select(entry => entry.CategoryPrototypeId));
        Assert.Equal([2827u, 2038u],
            wardrobe.WornSnapshot(new HashSet<uint> { 2827, 2038 })
                .Select(entry => entry.CategoryPrototypeId));
    }

    [Fact]
    public void UnsetRemovesOnlyTheRequestedPresetCategory()
    {
        var wardrobe = new AugustWardrobeState();
        Apply(wardrobe, Apparel(rewardItemId: 2484), requestedCategory: 2158);
        Apply(wardrobe, Apparel(rewardItemId: 2827), requestedCategory: 2827);

        var unsetHelmet = new SkinItemSelectionRequest(
            SkinItemSelectionRequest.RequestUnsetSkinItem,
            Field1: 1,
            Field2: 0,
            SlotType: 1,
            CategoryPrototypeId: 2827,
            ClickedId: 0);

        Assert.True(wardrobe.TryApply(unsetHelmet, out _, out bool removed, out _));
        Assert.True(removed);
        Assert.Equal([2158u], wardrobe.Snapshot().Select(entry => entry.CategoryPrototypeId));
    }

    [Fact]
    public void WeaponPresetNeverBecomesACharacterAttachment()
    {
        var wardrobe = new AugustWardrobeState();
        AugustSkinCatalogEntry weapon = AugustSkinCatalog.Weapons.First();

        Apply(wardrobe, weapon, weapon.CategoryPrototypeId, slotType: 2);

        Assert.Single(wardrobe.Snapshot());
        Assert.Empty(wardrobe.WornSnapshot());
        Assert.Equal([weapon], wardrobe.WeaponSnapshot());
        Assert.Equal(
            BareAppearance,
            AugustWardrobeVisuals.BuildAttachments(Male, wardrobe, appearance: null));
    }

    [Fact]
    public void ASelectedHoodieStartsWithItsHoodDown()
    {
        var wardrobe = new AugustWardrobeState();
        AugustSkinCatalogEntry hoodie = Apparel(rewardItemId: 3405);
        Apply(wardrobe, hoodie, hoodie.CategoryPrototypeId);

        CharacterEquipmentAttachment chest = Assert.Single(
            AugustWardrobeVisuals.BuildAttachments(Male, wardrobe, appearance: null),
            attachment => attachment.SlotId == BodySlots.Chest);

        Assert.Contains("Hoodie_Down", chest.ModelName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hoodie_Up", chest.ModelName, StringComparison.OrdinalIgnoreCase);
    }

    private static AugustSkinCatalogEntry Apparel(uint rewardItemId) =>
        AugustSkinCatalog.Apparel.Single(entry => entry.RewardItemId == rewardItemId);

    private static void Apply(
        AugustWardrobeState wardrobe,
        AugustSkinCatalogEntry entry,
        uint requestedCategory,
        uint slotType = 1)
    {
        Assert.True(
            wardrobe.TryApply(
                Request(entry, requestedCategory, slotType),
                out AugustSkinCatalogEntry selected,
                out bool removed,
                out string reason),
            reason);
        Assert.False(removed);
        Assert.Equal(requestedCategory, selected.CategoryPrototypeId);
    }

    private static SkinItemSelectionRequest Request(
        AugustSkinCatalogEntry entry,
        uint requestedCategory,
        uint slotType = 1) =>
        new(
            SkinItemSelectionRequest.RequestSetSkinItemByItemId,
            Field1: 1,
            Field2: 0,
            SlotType: slotType,
            CategoryPrototypeId: requestedCategory,
            ClickedId: entry.AccountItemId);
}
