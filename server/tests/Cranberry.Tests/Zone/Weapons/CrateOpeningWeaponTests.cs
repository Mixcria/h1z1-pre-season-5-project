using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class CrateOpeningWeaponTests
{
    [Fact]
    public void GalleryProfileUsesItsOwnCapturedWeaponAndTheAugustHiddenSlot()
    {
        Assert.False(AugustWeaponFacts.TryGet(3750, out _));
        Assert.True(InventoryItemFacts.TryGet(3750, out var item));
        Assert.Equal(25079u, item.ItemClass);
        Assert.Equal(1457u, item.Param1);
        Assert.True(WeaponItemProfiles.TryGet(3750, out var profile));
        Assert.Equal(new AugustWeaponFact(3750, 1457, 110, 120, 0, 9999999), profile);
        Assert.Equal(1429u, AmmoTypes.AmmoItemFor(3750));
        Assert.Equal(47u, InventoryAutoAssign.AutoEquipLoadoutSlot(3750));
        Assert.True(WeaponItemProfiles.TryGet(10, out var standard));
        Assert.Equal(6u, standard.WeaponId);
        Assert.Equal(30, standard.ClipSize);
    }

    [Theory]
    [InlineData(WeaponTableSource.Generated)]
    [InlineData(WeaponTableSource.Captured)]
    public void GalleryAddsACompleteResolvableWeaponWithoutChangingOrdinaryRecords(WeaponTableSource source)
    {
        var session = new WeaponSession(WeaponStageOptions.Default with { WeaponTable = source }, crateOpeningWeapon: true);
        var ordinary = new WeaponSession(WeaponStageOptions.Default with { WeaponTable = source }).Blob;
        var blob = session.Blob;
        var weapon = Assert.Single(blob.WeaponDefinitions!, row => row.WeaponDefinitionId == 1457);
        Assert.Equal(new uint[] { 110 }, weapon.FireGroupIds);
        Assert.Equal(new WeaponAmmoSlotRow(1429, 9999999), Assert.Single(weapon.AmmoSlots!));
        var group = Assert.Single(blob.FireGroups!, row => row.FireGroupId == 110);
        Assert.Equal(new uint[] { 220, 221 }, group.FireModeIds);
        foreach (uint id in group.FireModeIds!)
        {
            var mode = Assert.Single(blob.FireModes!, row => row.FireModeId == id);
            Assert.Equal(id, mode.DefinitionId);
            Assert.Equal(120u, mode.Overrides![WeaponListLayouts.FireModeRefireTime]);
            Assert.Equal(1429u, mode.Overrides[WeaponListLayouts.FireModeAmmoItemId]);
            Assert.Contains(blob.ConeOfFire!, row => row.ConeOfFireId == mode.Overrides[WeaponListLayouts.FireModePlayerStateGroupId]);
            Assert.Contains(blob.AimAssist!, row => row.AimAssistId == mode.Overrides[WeaponListLayouts.FireModeAimAssistConfig]);
            Assert.Contains(blob.FireModeProjectiles!, row => row.FireModeDefinitionId == id
                && row.AmmoItemId == 1429 && row.ProjectileDefinitionId == AugustProjectileTable.ProjectileForAmmoItem(1429));
        }
        // Separate Blob builds recreate array/dictionary members, whose record equality compares
        // references. Compare the entire ordinary wire after removing only the gallery additions.
        var originalConeIds = (ordinary.ConeOfFire ?? []).Select(row => row.ConeOfFireId).ToHashSet();
        var originalAimIds = (ordinary.AimAssist ?? []).Select(row => row.AimAssistId).ToHashSet();
        var withoutGallery = blob with
        {
            WeaponDefinitions = [.. blob.WeaponDefinitions!.Where(row => row.WeaponDefinitionId != 1457)],
            FireGroups = [.. blob.FireGroups!.Where(row => row.FireGroupId != 110)],
            FireModes = [.. blob.FireModes!.Where(row => row.FireModeId is not 220 and not 221)],
            FireModeProjectiles = [.. blob.FireModeProjectiles!.Where(row => row.FireModeDefinitionId is not 220 and not 221)],
            ConeOfFire = [.. blob.ConeOfFire!.Where(row => originalConeIds.Contains(row.ConeOfFireId))],
            AimAssist = [.. blob.AimAssist!.Where(row => originalAimIds.Contains(row.AimAssistId))],
        };
        Assert.Equal(ordinary.ToArray(), withoutGallery.ToArray());
        Assert.Equal(blob.Length, blob.ToArray().Length);
    }

    [Fact]
    public void GalleryGrantCarriesARealMagazineAndBecomesSafeOnlyAfterItsDefinitions()
    {
        var session = new WeaponSession(crateOpeningWeapon: true) { MagazineSource = (_, _) => 100 };
        var item = new InventoryItem(3750, 900, 1, 4097, PlayerInventory.EquippedContainerGuid,
            PlayerInventory.LoadoutContainerDefinitionId, 47);
        using var writer = new PacketWriter();
        Assert.Throws<InvalidOperationException>(() => session.CreateItemAdd(4097, item));
        Assert.False(session.Ledger.IsClearedForActiveHand(900));
        var tail = Assert.IsType<WeaponItemAddTail>(session.CreateTail(3750, 100));
        Assert.True(tail.IsSafeForActiveHand);
        Assert.True(tail.AmmoSlot);
        Assert.Equal(100, tail.Magazine);
        Assert.Equal(110u, tail.CurrentGroup!.FireGroupId);
        Assert.True(session.TryCreateWeaponDefinitions(out _));
        session.MarkWeaponDefinitionsSent();
        session.CreateItemAdd(4097, item)(writer);
        Assert.True(session.Ledger.IsClearedForActiveHand(900));
    }

    [Fact]
    public void IncompleteStagesRefuseGalleryGrantInsteadOfSendingAGenericWeaponTail()
    {
        var session = new WeaponSession(WeaponStageOptions.Default with { PopulateFireModes = false }, crateOpeningWeapon: true);
        Assert.False(session.SupportsCrateOpeningWeapon);
        Assert.DoesNotContain(session.Blob.WeaponDefinitions!, row => row.WeaponDefinitionId == 1457);
        var item = new InventoryItem(3750, 900, 1, 4097, PlayerInventory.EquippedContainerGuid,
            PlayerInventory.LoadoutContainerDefinitionId, 47);
        Assert.Throws<InvalidOperationException>(() => session.CreateItemAdd(4097, item));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void GalleryUsesTheBaseAr15MeshForEitherBody(uint gender)
    {
        Assert.True(AugustWornVisuals.TryResolveMesh(10, gender, "", out var ordinary, out uint ordinaryShader));
        Assert.True(AugustWornVisuals.TryResolveMesh(3750, gender, "", out var gallery, out uint galleryShader));
        Assert.Equal(ordinary, gallery);
        Assert.Equal(ordinaryShader, galleryShader);
    }
}
