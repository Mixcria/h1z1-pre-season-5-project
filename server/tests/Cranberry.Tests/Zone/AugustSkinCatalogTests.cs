using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class AugustSkinCatalogTests
{
    [Theory]
    [InlineData(3933)]
    [InlineData(3862)]
    public void HelmetDescriptionFamilyUsesHelmetPrototype(uint rewardItemId)
    {
        AugustSkinCatalogEntry entry = Assert.Single(
            AugustSkinCatalog.Apparel,
            entry => entry.RewardItemId == rewardItemId);

        Assert.Equal(2827u, entry.CategoryPrototypeId);
        Assert.Equal(1u, entry.EquipmentSlotId);
    }

    [Fact]
    public void GeneratedCatalogueHasCompleteGenderModelsAndTextureAliases()
    {
        AugustSkinCatalogEntry[] entries =
        [.. AugustSkinCatalog.Apparel, .. AugustSkinCatalog.Weapons];

        Assert.Equal(678, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.MaleModelName));
            Assert.False(string.IsNullOrWhiteSpace(entry.FemaleModelName));
            Assert.False(string.IsNullOrWhiteSpace(entry.TextureAlias));
        });
    }

    [Fact]
    public void GeneratedAppearanceCorrectionsCoverKnownColourAndGenderDefects()
    {
        Assert.Equal(152, AugustSkinCatalog.AppearanceRowOverrides.Count);

        Assert.Equal(
            new AugustAppearanceRowOverride(2778, 252, 1),
            AugustSkinCatalog.AppearanceRowOverrides[336]);
        Assert.Equal(
            new AugustAppearanceRowOverride(2889, 340, 1),
            AugustSkinCatalog.AppearanceRowOverrides[1585]);
        Assert.Equal(
            new AugustAppearanceRowOverride(2889, 340, 2),
            AugustSkinCatalog.AppearanceRowOverrides[1586]);
        Assert.Equal(
            new AugustAppearanceRowOverride(10, 168, 1),
            AugustSkinCatalog.AppearanceRowOverrides[182]);
    }
}
