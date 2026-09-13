using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// docs/106 §13 (D283) — <i>"when I pick up items in the world they don't get skinned (backpack,
/// guns etc.); the AK-47 was the only one skinned"</i>, owner, 2026-09-03 evening, against
/// <c>logs\host-20260903-194804.log</c> / <c>captures\wire-20260903-194804.txt</c>.
/// <para>
/// What the capture holds, decoded attachment by attachment: the hand (D225) carried the skin —
/// <c>:22867</c> 19:58:47.188 <c>slot 7 grp 207 app [249,250] Weapon_AK47_3P.adr</c>, which is
/// reward 2662's colourway, not item 2229's — and nothing else did. The same AK on its peg at
/// <c>:23968</c> is <c>slot 80 grp 71 app [97,98]</c>, the BASE rows; the military backpack is
/// <c>slot 10 grp 237 app [332,333]</c>, item 2124's own rows and not selection 2121 → 2778; the
/// stowed shotgun is <c>slot 76 grp 29 app [27,28]</c> and the stowed AR-15
/// <c>slot 77 grp 168 app [181,182]</c>.
/// </para>
/// <para>
/// Send-side only (docs/32's evidence standard): none of this is LIVE-VERIFIED.
/// </para>
/// </summary>
[Collection(AppearanceStaticsCollection.Name)]
public sealed class WornSkinsInWorldTests
{
    [Theory]
    [InlineData(10u, 0u)]
    [InlineData(10u, 4076u)]
    [InlineData(2425u, 0u)]
    [InlineData(2425u, 4076u)]
    public void DrawingAPickedUpAr15RetainsItsClientItemAndClothes(uint itemId, uint skinAccount)
    {
        using var session = new Session(itemId, skins: null);
        if (skinAccount != 0) session.ClickSkin(10, skinAccount);
        session.EnterAndDrop();
        session.PickUp();
        object state = session.Connection.Tag!;
        var inventory = (PlayerInventory)state.GetType().GetProperty("Inventory")!.GetValue(state)!;
        var match = state.GetType().GetProperty("Match")!;
        match.SetValue(state, Enum.Parse(match.PropertyType, "InMatch"));
        var weapon = Assert.Single(inventory.Items.Values, i => i.DefinitionId == itemId);
        var before = weapon.ToRecord(Self);
        var combat = (SessionCombat)state.GetType().GetProperty("Combat")!.GetValue(state)!;
        combat.Shooter.DeclareWeapon(weapon.Guid, itemId, 17);

        // Stow and draw again as well: an existing wheel item needs no replacement grant.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            session.SelectSlot(weapon.LoadoutSlotId);
            var sent = session.SentSinceMark();
            Assert.DoesNotContain(sent, p => p.Length > 3 && p[1] == 0x11 && p[2] is 2 or 4);
            Assert.DoesNotContain(sent, p => p.Length > 2 && p[1] == 0xac && p[2] == 0x24);
            Assert.DoesNotContain(sent, p => p.Length > 2 && p[1] == 0x94 && p[2] == 1);
            int manager = Array.FindIndex(sent, p => p.Length > 2 && p[1] == 0xa0 && p[2] == 5);
            int bind = Array.FindLastIndex(sent, p => p.Length > 30 && p[1] == 0x94 && p[2] == 2
                && BitConverter.ToUInt32(p, 15) == 7);
            Assert.True(manager >= 0 && bind > manager);
            Assert.Equal(weapon.Guid, BitConverter.ToUInt64(sent[bind], 23));
            Assert.Equal(before, weapon.ToRecord(Self));
            Assert.Equal(17, combat.Shooter.AmmoOf(weapon.Guid));
            Assert.Same(weapon, inventory.Items[weapon.Guid]);
            session.SelectSlot(SurvivorLoadout.Fists);
        }
    }

    [Theory]
    [InlineData(10u, 4076u, 4073u, true)]
    [InlineData(2425u, 4076u, 4073u, true)]
    [InlineData(2229u, 2599u, 2600u, true)]
    [InlineData(1374u, 3752u, 3720u, true)]
    [InlineData(2229u, 2599u, 2600u, false)]
    public void InventoryWeaponSkinClickRefreshesCarriedItemAndPreservesRuntime(
        uint weaponId, uint accountId, uint rewardId, bool held)
    {
        Assert.True(File.Exists(Source));
        using var session = new Session(weaponId, skins: null);
        session.EnterAndDrop();
        session.PickUp();
        session.SelectSlot(held ? SurvivorLoadout.Wheel1 : SurvivorLoadout.Fists);
        object state = session.Connection.Tag!;
        var stateType = state.GetType();
        var inventory = (PlayerInventory)stateType.GetProperty("Inventory")!.GetValue(state)!;
        var combat = (SessionCombat)stateType.GetProperty("Combat")!.GetValue(state)!;
        // The existing development-pickup fixture initializes all live inventory/weapon managers;
        // select the gameplay branch before delivering the same AC32 request as the new menu.
        var matchProperty = stateType.GetProperty("Match")!;
        matchProperty.SetValue(state, Enum.Parse(matchProperty.PropertyType, "InMatch"));
        var weapon = Assert.Single(inventory.Items.Values, item => item.DefinitionId == weaponId);
        combat.Shooter.DeclareWeapon(weapon.Guid, weaponId, 4);
        Assert.Equal(FireVerdict.Accepted, combat.Shooter.Fire(
            new WeaponFire(weapon.Guid, 1, 2, 3, [44]), weaponId, 1000, CombatOptions.Default).Verdict);
        combat.Shooter.SpendDurability(weapon.Guid, 5, AmmoOptions.Default.MaxDurability);
        combat.Shooter.SelectFireMode(weapon.Guid, 0, 1);
        uint ammoId = AmmoTypes.AmmoItemFor(weaponId);
        Assert.NotEqual(InventoryPlacementKind.Refused, inventory.TryPickUp(ammoId, 12, out var reserve).Kind);
        Assert.NotNull(reserve);
        var ammo = new PlayerAmmoContext(inventory, Self, AmmoOptions.Default);
        ulong reloadingGuid = weapon.Guid;
        uint reloadingDefinition = weaponId;
        uint reloadingAmmo = ammoId;
        if (!held)
        {
            // A holstered gun cannot be reloading. Hold a different, owned rifle and retain
            // its legitimate reload while changing the spare AK's skin.
            Assert.NotEqual(InventoryPlacementKind.Refused, inventory.TryPickUp(10, 1, out var other).Kind);
            Assert.NotNull(other);
            combat.Shooter.DeclareWeapon(other.Guid, 10, 2);
            Assert.True(inventory.TrySelectLoadoutSlot(other.LoadoutSlotId, out _));
            reloadingGuid = other.Guid;
            reloadingDefinition = 10;
            reloadingAmmo = AmmoTypes.AmmoItemFor(10);
            Assert.NotEqual(InventoryPlacementKind.Refused, inventory.TryPickUp(reloadingAmmo, 12, out _).Kind);
        }
        var pending = new PendingWeaponReload(reloadingGuid, reloadingDefinition, reloadingAmmo, ammo, 800, true, 1000);
        combat.Reload = pending;
        uint currentSlot = inventory.CurrentLoadoutSlotId;
        uint loadoutSlot = weapon.LoadoutSlotId;
        uint equipmentSlot = weapon.EquipmentSlotId;
        int count = inventory.Items.Count;
        int runtimeCount = combat.Shooter.WeaponCount;

        session.Mark();
        uint skinCategory = weaponId == 2425 ? 10u : weaponId;
        session.SelectWeaponSkin(weapon.Guid, skinCategory, accountId);

        Assert.Same(weapon, inventory.Items[weapon.Guid]);
        Assert.Equal(weaponId, weapon.DefinitionId);
        Assert.Equal(rewardId, weapon.DisplayDefinitionId);
        Assert.Equal(count, inventory.Items.Count);
        Assert.Equal(currentSlot, inventory.CurrentLoadoutSlotId);
        Assert.Equal(loadoutSlot, weapon.LoadoutSlotId);
        Assert.Equal(equipmentSlot, weapon.EquipmentSlotId);
        Assert.Equal(3, combat.Shooter.AmmoOf(weapon.Guid));
        Assert.Equal(12, ammo.Count(ammoId));
        Assert.Equal(runtimeCount, combat.Shooter.WeaponCount);
        Assert.Equal(AmmoOptions.Default.MaxDurability - 5, combat.Shooter.DurabilityOf(weapon.Guid));
        Assert.Equal(1, combat.Shooter.FireModeOf(weapon.Guid));
        Assert.Same(pending, combat.Reload);
        Assert.Equal(1800, pending.DueAtMs);
        Assert.Equal(12, ammo.Count(reloadingAmmo));
        Assert.Equal(FireVerdict.RateOfFire, combat.Shooter.Fire(
            new WeaponFire(weapon.Guid, 1, 2, 3, [45]), weaponId, 1001, CombatOptions.Default).Verdict);

        byte[][] sent = session.SentSinceMark();
        byte[] added = Assert.Single(sent, p => p.Length > 80 && p[1] == 0x11 && p[2] == 2);
        Assert.Equal(rewardId, BitConverter.ToUInt32(added, 16));
        Assert.Equal(weapon.Guid, BitConverter.ToUInt64(added, 24));
        Assert.Equal(3u, BitConverter.ToUInt32(added, 83));
        Assert.Equal((uint)(AmmoOptions.Default.MaxDurability - 5), BitConverter.ToUInt32(added, 57));
        var skin = Reward(rewardId);
        Assert.Equal(Load().AppearanceRowsFor(rewardId, CharacterVisuals.Male),
            session.LastAttachment(skin.MaleModelName, held).Rows);

        session.Mark();
        session.SelectWeaponSkin(weapon.Guid, skinCategory, accountId);
        Assert.DoesNotContain(session.SentSinceMark(), p => p.Length > 3 && p[1] == 0x11 && p[2] is 2 or 4);
    }

    [Theory]
    [InlineData(CharacterVisuals.Male)]
    [InlineData(CharacterVisuals.Female)]
    public void ToxicLaminatedArmourSelectsItsOwnShippedMeshAndMaterial(uint gender)
    {
        var toxic = Reward(2477);
        Assert.True(AugustWornVisuals.TryResolveMesh(2271, gender, null, out var baseMesh, out _));
        Assert.NotEqual(baseMesh, toxic.ModelNameFor(gender));
        Assert.True(AugustWornSkins.TryResolve(2271, gender, baseMesh, [toxic],
            retintOnly: true, out var selected, out var reason), reason);
        Assert.Equal(2477u, selected.RewardItemId);
        Assert.True(AugustAssetIndex.Ships(selected.ModelNameFor(gender)));

        var table = Load();
        var attachments = new List<CharacterEquipmentAttachment>();
        AugustWornVisuals.Dress(attachments, [new AugustWornItem(100, 2271, "")], gender,
            id => table.AppearanceRowsFor(id, gender),
            definesShaderGroup: table.DefinesShaderGroup,
            skinRewardFor: (_, _) => selected.RewardItemId,
            shaderGroupFor: id => table.ShaderGroupFor(id, gender));
        var armour = Assert.Single(attachments);
        Assert.Equal(selected.ModelNameFor(gender), armour.ModelName);
        Assert.Equal(table.AppearanceRowsFor(2477, gender), armour.AppearanceIds);
        Assert.Equal(table.ShaderGroupFor(2477, gender), armour.ShaderParameterGroupId);
    }

    [Fact]
    public void ToxicLaminatedArmourPickupUsesSelectedMeshAndAppearanceOnTheWire()
    {
        Assert.True(File.Exists(Source));
        using var session = new Session(2271, skins: null);
        session.ClickSkin(2271, Reward(2477).AccountItemId);
        session.EnterAndDrop();
        session.PickUp();
        var armour = session.LastAttachment(Reward(2477).MaleModelName, hand: false);
        Assert.Equal(100u, armour.Slot);
        Assert.Equal(Load().ShaderGroupFor(2477, CharacterVisuals.Male), armour.Group);
        Assert.Equal(Load().AppearanceRowsFor(2477, CharacterVisuals.Male), armour.Rows);
    }

    [Fact]
    public void AsusMilitaryBackpackUsesItsSelectedModelAndAppearanceRows()
    {
        Assert.True(File.Exists(Source));
        using var session = new Session(2124, skins: null);
        var skin = Reward(4577);
        session.ClickSkin(2121, skin.AccountItemId);
        session.EnterAndDrop();
        session.PickUp();
        var back = session.LastAttachment(skin.MaleModelName, hand: false);
        Assert.Equal(10u, back.Slot);
        Assert.Equal(Load().ShaderGroupFor(4577, CharacterVisuals.Male), back.Group);
        Assert.Equal(Load().AppearanceRowsFor(4577, CharacterVisuals.Male), back.Rows);
    }

    [Theory]
    [InlineData(1u, "SurvivorMale_Armor_Homemade.adr")]
    [InlineData(2u, "SurvivorFemale_Armor_Homemade.adr")]
    public void CraftedMakeshiftArmorResolvesItsAuthoredMesh(uint gender, string expected)
    {
        Assert.True(AugustWornVisuals.TryResolveMesh(3378, gender, "", out var model, out _));
        Assert.Equal(expected, model);
        Assert.True(AugustAssetIndex.Ships(model));
    }

    [Fact]
    public void VolcanicAkPickupCarriesFireGroupsBeforeSelectingTheSkinnedHand()
    {
        Assert.True(File.Exists(Source), "The authorized appearance reference is required for this regression.");
        using var session = new Session(2229, skins: null);
        session.ClickSkin(2229, Reward(4033).AccountItemId);
        session.EnterAndDrop();
        session.PickUp();
        byte[] grant = Assert.Single(session.SentSinceMark(), p => p.Length > 28
            && p[1] == 0x11 && p[2] == 2 && p[3] == 0 && BitConverter.ToUInt32(p, 16) == 4033);
        Assert.Equal(150, grant.Length); // gateway byte + 149-byte weapon ItemAdd
        Assert.Equal(1u, BitConverter.ToUInt32(grant, 79)); // ammo-slot count
        Assert.Equal(1, grant[87]); // fire-group count
        Assert.Equal(51u, BitConverter.ToUInt32(grant, 88));

        session.SelectSlot(SurvivorLoadout.Wheel1);
        var hand = session.LastAttachment("Weapon_AK47_3P.adr", hand: true);
        Assert.Equal(7u, hand.Slot);
        Assert.Equal(Load().ShaderGroupFor(4033, CharacterVisuals.Male), hand.Group);
        Assert.Contains(session.SentSinceMark(), p => p.Length > 2 && p[1] == 0x94 && p[2] == 2);
    }

    [Theory]
    [InlineData(2124u, 2121u, 2782u, 2777u)]
    [InlineData(2124u, 2121u, 2783u, 2778u)]
    [InlineData(2112u, 2038u, 1806u, 2051u)]
    [InlineData(2113u, 2038u, 1806u, 2051u)]
    [InlineData(2114u, 2038u, 1806u, 2051u)]
    [InlineData(2115u, 2038u, 1806u, 2051u)]
    [InlineData(2116u, 2038u, 1806u, 2051u)]
    [InlineData(2117u, 2038u, 1806u, 2051u)]
    public void BackpackPickupAnnouncesItsSelectionAndDropUsesThatSkinsGroup(
        uint itemId, uint category, uint account, uint reward)
    {
        if (!File.Exists(Source)) return;
        using var session = new Session(itemId, skins: null);
        session.ClickSkin(category, account);
        session.EnterAndDrop();
        session.PickUp();
        byte[][] pickup = session.SentSinceMark();
        int skinAt = Array.FindIndex(pickup, p => p.Length > 2 && p[1] == 0xac && p[2] == 0x24);
        int itemAt = Array.FindIndex(pickup, p => p.Length > 28 && p[1] == 0x11 && p[2] == 2 && p[3] == 0
            && BitConverter.ToUInt32(p, 16) == reward);
        Assert.True(skinAt >= 0 && itemAt > skinAt);
        ulong itemGuid = BitConverter.ToUInt64(pickup[itemAt], 24);
        session.DropItem(itemGuid);
        byte[] ground = Assert.Single(session.SentSinceMark(), p => p.Length > 16 && p[1] == AddLightweightItem.Opcode);
        uint expected = Load().ShaderGroupForAnyBody(reward);
        Assert.NotEqual(0u, expected);
        Assert.Equal(expected, BitConverter.ToUInt32(ground, ground.Length - 16));
    }

    /// <summary>The MD5-gated compatibility source (D22); every session case skips without it.</summary>
    private static readonly string Source = TestData.DynamicAppearanceSource;

    private const ulong Self = 0x1101;

    // ------------------------------------------------------------------ the resolver, on its own

    [Fact]
    public void ALootedItemNamesItsOwnWardrobeCategoryOutOfTheCatalogue()
    {
        // The joins the fix rests on, and the four the owner actually looted on 2026-09-03.
        Assert.True(AugustWornSkins.TryGetCategory(2124, out uint backpack));
        Assert.Equal(2121u, backpack);
        Assert.True(AugustWornSkins.TryGetCategory(2170, out uint helmet));
        Assert.Equal(2827u, helmet);
        Assert.True(AugustWornSkins.TryGetCategory(2229, out uint ak));
        Assert.Equal(2229u, ak);
        Assert.True(AugustWornSkins.TryGetCategory(1374, out uint shotgun));
        Assert.Equal(1374u, shotgun);

        // Loot-only colours still belong to the same wardrobe family as their shared mesh.
        Assert.True(AugustWornSkins.TryGetCategory(2171, out uint whiteHelmet));
        Assert.Equal(2827u, whiteHelmet);
        Assert.True(AugustWornSkins.TryGetCategory(2112, out uint smallPack));
        Assert.Equal(2038u, smallPack);
        Assert.False(AugustWornSkins.TryGetCategory(1429, out _)); // .223 ammunition
    }

    [Fact]
    public void TheSelectionForTheItemsOwnCategoryWinsAndNothingElseDoes()
    {
        AugustSkinCatalogEntry pack = Reward(2778);
        AugustSkinCatalogEntry gloves = Reward(2445);

        Assert.True(AugustWornSkins.TryResolve(
            2124,
            CharacterVisuals.Male,
            "SurvivorMale_Back_Backpack_Military.adr",
            [gloves, pack],
            retintOnly: true,
            out AugustSkinCatalogEntry picked,
            out string why));
        Assert.Equal(2778u, picked.RewardItemId);
        Assert.Contains("2121", why, StringComparison.Ordinal);

        // The glove selection alone reaches nothing on the back.
        Assert.False(AugustWornSkins.TryResolve(
            2124,
            CharacterVisuals.Male,
            "SurvivorMale_Back_Backpack_Military.adr",
            [gloves],
            retintOnly: true,
            out _,
            out string none));
        Assert.Contains("no skin selected", none, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectedHelmetCanReplaceTheBaseHelmetMesh()
    {
        // The owner's own case on 2026-09-03: helmet 2170 wears the motorcycle mesh and his 2827
        // selection was reward 3551, a skull mask. D53 (docs/80 edit 4) declines that.
        AugustSkinCatalogEntry mask = Reward(3551);
        Assert.Equal("SurvivorMale_Head_Mask_Skull.adr", mask.MaleModelName);

        Assert.True(AugustWornSkins.TryResolve(
            2170,
            CharacterVisuals.Male,
            "SurvivorMale_Head_Helmet_Motorcycle_Tintable.adr",
            [mask],
            retintOnly: true,
            out _,
            out string why));
        Assert.Contains("3551", why, StringComparison.Ordinal);

        Assert.True(AugustWornSkins.TryResolve(
            2170,
            CharacterVisuals.Male,
            "SurvivorMale_Head_Helmet_Motorcycle_Tintable.adr",
            [mask],
            retintOnly: false,
            out AugustSkinCatalogEntry allowed,
            out _));
        Assert.Equal(3551u, allowed.RewardItemId);

        // A pick that IS the looted item changes nothing and is refused before any lookup.
        Assert.False(AugustWornSkins.TryResolve(
            2124,
            CharacterVisuals.Male,
            "SurvivorMale_Back_Backpack_Military.adr",
            [Reward(2124)],
            retintOnly: true,
            out _,
            out string same));
        Assert.Contains("the looted item itself", same, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the attachment, byte-exact

    /// <summary>
    /// The bytes <c>AugustWornVisuals.Dress</c> now writes for a stowed AK-47 with reward 2662
    /// selected: the same mesh, the SKIN's two appearance rows and the skin's shader group, in the
    /// field order <c>CharacterEquipmentAttachment.WriteTo</c> spells. Before D283 this attachment
    /// carried <c>grp 71 app [97,98]</c> — item 2229's own colourway — which is
    /// <c>captures\wire-20260903-194804.txt:23968 slot 80</c>.
    /// </summary>
    [Fact]
    public void AStowedGunsAttachmentCarriesTheSkinsRowsByteForByte()
    {
        var attachments = new List<CharacterEquipmentAttachment>();
        int dressed = AugustWornVisuals.Dress(
            attachments,
            [new AugustWornItem(76, 2229, string.Empty)],
            CharacterVisuals.Male,
            item => item == 2662 ? [249u, 250u] : [97u, 98u],
            definesShaderGroup: _ => true,
            skinRewardFor: (item, mesh) => item == 2229
                && mesh == "Weapon_AK47_3P.adr" ? 2662u : 0u,
            shaderGroupFor: item => item == 2662 ? 207u : 71u);

        Assert.Equal(1, dressed);
        using var writer = new PacketWriter();
        Assert.Single(attachments).WriteTo(writer);

        byte[] expected =
        [
            .. Str("Weapon_AK47_3P.adr"),
            .. Str("Default"),                   // texture alias
            .. Str("Default"),                   // tint alias
            .. Str("#"),                         // decal alias
            0, 0, 0, 0,                          // tint id
            0, 0, 0, 0,                          // composite effect
            0, 0, 0, 0,                          // effect
            76, 0, 0, 0,                         // body slot 76, the first long-gun peg
            207, 0, 0, 0,                        // reward 2662's shader group, not item 2229's 71
            2, 0, 0, 0,                          // two appearance rows
            249, 0, 0, 0,                        // reward 2662 gender 1
            250, 0, 0, 0,                        // reward 2662 gender 2
            0,                                   // trailing flag
        ];
        Assert.Equal(expected, writer.Written.ToArray());
    }

    /// <summary>With the resolver absent the attachment is byte-identical to the pre-D283 wire.</summary>
    [Fact]
    public void WithNoResolverTheAttachmentIsTheBaseColourwayItAlwaysWas()
    {
        var attachments = new List<CharacterEquipmentAttachment>();
        AugustWornVisuals.Dress(
            attachments,
            [new AugustWornItem(76, 2229, string.Empty)],
            CharacterVisuals.Male,
            _ => [97u, 98u],
            definesShaderGroup: _ => true);

        using var writer = new PacketWriter();
        Assert.Single(attachments).WriteTo(writer);
        byte[] actual = writer.Written.ToArray();

        // The two fields D283 moves, read back out of the bytes: group then the row list. 71 is
        // item 2229's OWN group, straight out of AugustWornMeshCatalog - the colourway the
        // 2026-09-03 capture carries on peg 80 - and 97/98 are its own rows.
        int groupAt = actual.Length - 1 - (3 * 4) - 4;
        Assert.Equal(71u, BitConverter.ToUInt32(actual, groupAt));
        Assert.Equal(2u, BitConverter.ToUInt32(actual, groupAt + 4));
        Assert.Equal(97u, BitConverter.ToUInt32(actual, groupAt + 8));
        Assert.Equal(98u, BitConverter.ToUInt32(actual, groupAt + 12));
    }

    // ------------------------------------------------------------------------- the fake session

    /// <summary>
    /// The owner's four pickups, one session each: select the skin in the menu, drop the item on
    /// the ground, pick it up, and read the attachment out of the <c>94 01</c> the pickup sends.
    /// Every row and group must be the SKIN's.
    /// </summary>
    [Theory]
    // item, category, clicked account item, reward, mesh
    [InlineData(2124u, 2121u, 2783u, 2778u, "SurvivorMale_Back_Backpack_Military.adr")]
    [InlineData(2170u, 2827u, 1827u, 2063u, "SurvivorMale_Head_Helmet_Motorcycle_Tintable.adr")]
    [InlineData(2229u, 2229u, 2753u, 2662u, "Weapon_AK47_3P.adr")]
    [InlineData(1374u, 1374u, 3752u, 3720u, "Weapons_PumpShotgun01_3P.adr")]
    [InlineData(1374u, 1374u, 4053u, 4032u, "Weapons_PumpShotgun01_3P.adr")]
    public void APickedUpItemWearsTheSkinSelectedForItsCategory(
        uint itemDefinitionId,
        uint categoryPrototypeId,
        uint clickedAccountItemId,
        uint rewardItemId,
        string mesh)
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();
        IReadOnlyList<uint> skinRows = table.AppearanceRowsFor(rewardItemId, CharacterVisuals.Male);
        Assert.NotEmpty(skinRows);

        (uint slot, uint group, uint[] rows) = PickUpWearing(
            itemDefinitionId, categoryPrototypeId, clickedAccountItemId, mesh, skins: null);

        Assert.NotEqual(AugustWornVisuals.ActiveHandSlotId, slot);
        Assert.Equal(skinRows, rows);
        Assert.Equal(table.ShaderGroupFor(rewardItemId, CharacterVisuals.Male), group);

        // And it is genuinely different from what the base item would have carried.
        Assert.NotEqual(
            table.AppearanceRowsFor(itemDefinitionId, CharacterVisuals.Male),
            (IReadOnlyList<uint>)rows);
    }

    /// <summary>
    /// <c>CRANBERRY_WORN_SKINS_IN_WORLD=0</c> is the whole rollback: the same click, the same
    /// pickup, and the base item's own rows on the wire again — the shape the 2026-09-03 capture
    /// carries.
    /// </summary>
    [Fact]
    public void TheSwitchPutsTheBaseColourwayBackOnEveryWornSlot()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();
        (uint _, uint group, uint[] rows) = PickUpWearing(
            2124,
            2121,
            2783,
            "SurvivorMale_Back_Backpack_Military.adr",
            skins => skins with { SendWornSkinsInWorld = false });

        Assert.Equal(table.AppearanceRowsFor(2124, CharacterVisuals.Male), rows);
        Assert.NotEqual(table.ShaderGroupFor(2778, CharacterVisuals.Male), group);
    }

    /// <summary>
    /// The re-model decline, end to end: the owner's real 2827 selection is a skull mask and the
    /// looted helmet keeps its own mesh, its own group and its own rows — WHOLE, per D53.
    /// </summary>
    [Fact]
    public void ASelectedHelmetUsesItsOwnMeshAndAppearanceRows()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();
        (uint _, uint group, uint[] rows) = PickUpWearing(
            2170,
            2827,
            3568,                                    // account item of reward 3551, the skull mask
            "SurvivorMale_Head_Mask_Skull.adr",
            skins: null);

        Assert.Equal(table.AppearanceRowsFor(3551, CharacterVisuals.Male), rows);
        Assert.Equal(table.ShaderGroupFor(3551, CharacterVisuals.Male), group);
    }

    // --------------------------------------------------- 2026-09-04: the hand, every peg, bystanders

    /// <summary>
    /// docs/106 §13 (D320): the owner's three weapon selections as they stand in
    /// <c>state\wardrobe\w-0000000000001005.json</c> on the morning of 2026-09-04, on every site
    /// a looted gun can be: the peg it lands on at pickup, the HAND when it is drawn, the peg again
    /// when it is stowed behind the fists, and the hand again on a second draw. Every attachment
    /// must be the reward's rows and the reward's group - which is what
    /// <c>captures\wire-20260903-213229.txt</c> holds at <c>:9902</c> (peg 76), <c>:9952</c>
    /// (hand), <c>:11527</c> (peg 77), <c>:11616</c> (hand), <c>:18875</c> (peg 80) and
    /// <c>:18922</c> (hand), decoded by <c>tools/appearance/dec-equipment.py</c>.
    /// </summary>
    [Theory]
    // item, its category, the clicked account item, the reward, mesh
    [InlineData(10u, 10u, 4076u, 4073u, "Weapon_M16A4_3P.adr")]
    [InlineData(2229u, 2229u, 2599u, 2600u, "Weapon_AK47_3P.adr")]
    [InlineData(1374u, 1374u, 3752u, 3720u, "Weapons_PumpShotgun01_3P.adr")]
    [InlineData(1374u, 1374u, 4053u, 4032u, "Weapons_PumpShotgun01_3P.adr")]
    public void ASkinnedGunIsTheSkinOnThePegInTheHandAndOnThePegAgain(
        uint itemDefinitionId,
        uint categoryPrototypeId,
        uint clickedAccountItemId,
        uint rewardItemId,
        string mesh)
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();
        IReadOnlyList<uint> skinRows = table.AppearanceRowsFor(rewardItemId, CharacterVisuals.Male);
        uint skinGroup = table.ShaderGroupFor(rewardItemId, CharacterVisuals.Male);
        Assert.NotEmpty(skinRows);
        Assert.NotEqual(0u, skinGroup);
        Assert.NotEqual(table.AppearanceRowsFor(itemDefinitionId, CharacterVisuals.Male), skinRows);

        using var session = new Session(itemDefinitionId, skins: null);
        session.ClickSkin(categoryPrototypeId, clickedAccountItemId);
        session.EnterAndDrop();

        // 1. the pickup dresses the peg
        session.PickUp();
        (uint pegSlot, uint pegGroup, uint[] pegRows) = session.LastAttachment(mesh, hand: false);
        Assert.Contains(pegSlot, (uint[])[76u, 77u, 78u, 80u]);
        Assert.Equal(skinRows, pegRows);
        Assert.Equal(skinGroup, pegGroup);

        // 2. the draw puts the same rows in the hand
        session.SelectSlot(SurvivorLoadout.Wheel1);
        (_, uint handGroup, uint[] handRows) = session.LastAttachment(mesh, hand: true);
        Assert.Equal(skinRows, handRows);
        Assert.Equal(skinGroup, handGroup);

        // 3. the stow (fists) puts it back on a peg, still the skin - never the base
        session.SelectSlot(SurvivorLoadout.Fists);
        (uint stowedSlot, uint stowedGroup, uint[] stowedRows) = session.LastAttachment(mesh, hand: false);
        Assert.Contains(stowedSlot, (uint[])[76u, 77u, 78u, 80u]);
        Assert.Equal(skinRows, stowedRows);
        Assert.Equal(skinGroup, stowedGroup);

        // 4. and the second draw is the skin again
        session.SelectSlot(SurvivorLoadout.Wheel1);
        (_, uint againGroup, uint[] againRows) = session.LastAttachment(mesh, hand: true);
        Assert.Equal(skinRows, againRows);
        Assert.Equal(skinGroup, againGroup);
    }

    /// <summary>
    /// docs/106 §3.1's standing "defect", measured (D323): item 10's two rows (181, 182) are both
    /// <c>GENDER_ID 2</c>, so on a male body the client's selector (<c>FUN_140c476f0</c>) returns
    /// no row and the draw (<c>FUN_140c70d60</c>) takes the packet's OWN model name and group.
    /// D226 puts group 168 - the rows' own group - in that field, on the peg and in the hand, so
    /// the unskinned rifle is its own colour. <c>captures\wire-20260903-194804.txt:23968</c>
    /// carries exactly this shape on slot 77.
    /// </summary>
    [Fact]
    public void AnUnskinnedAr15OnAMaleBodyCarriesItsRowsOwnGroupOnThePegAndInTheHand()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();
        Assert.Equal(0u, table.ShaderGroupFor(10, CharacterVisuals.Male));
        Assert.Equal(168u, table.ShaderGroupForAnyBody(10));

        using var session = new Session(10, skins: null);
        session.EnterAndDrop();
        session.PickUp();
        (_, uint pegGroup, uint[] pegRows) = session.LastAttachment("Weapon_M16A4_3P.adr", hand: false);
        Assert.Equal(table.AppearanceRowsFor(10, CharacterVisuals.Male), pegRows);
        Assert.Equal(168u, pegGroup);

        session.SelectSlot(SurvivorLoadout.Wheel1);
        (_, uint handGroup, uint[] handRows) = session.LastAttachment("Weapon_M16A4_3P.adr", hand: true);
        Assert.Equal(table.AppearanceRowsFor(10, CharacterVisuals.Male), handRows);
        Assert.Equal(168u, handGroup);
    }

    /// <summary>
    /// The census agrees with the wire (D323): with D226's cross-body group on, item 10 on a male
    /// body is Fine - coloured by the packet's own group - and the note says so; with it off, the
    /// verdict is the old <c>NoRowForThisBody</c>, which is what the wire carried before D226. A
    /// female body never needed either.
    /// </summary>
    [Fact]
    public void TheCensusReadsTheAr15OnAMaleBodyTheWayTheClientDraws()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();

        AugustSkinCensusRow male = AugustSkinCensus.Resolve(
            table, 10, CharacterVisuals.Male, "Weapon_M16A4_3P.adr");
        Assert.Equal(AugustSkinVerdict.Fine, male.Verdict);
        Assert.Equal(0u, male.RowId);
        Assert.Equal(168u, male.ShaderParameterGroupId);
        Assert.Contains("168", male.Problem, StringComparison.Ordinal);

        AugustSkinCensusRow withoutD226 = AugustSkinCensus.Resolve(
            table, 10, CharacterVisuals.Male, "Weapon_M16A4_3P.adr", crossGenderShaderGroup: false);
        Assert.Equal(AugustSkinVerdict.NoRowForThisBody, withoutD226.Verdict);

        AugustSkinCensusRow female = AugustSkinCensus.Resolve(
            table, 10, CharacterVisuals.Female, "Weapon_M16A4_3P.adr");
        Assert.Equal(AugustSkinVerdict.Fine, female.Verdict);
        Assert.Equal(181u, female.RowId);

        (int _, IReadOnlyList<AugustSkinCensusRow> problems) = AugustSkinCensus.Run(table);
        Assert.DoesNotContain(problems, row => row.ItemDefinitionId == 10);
    }

    /// <summary>
    /// Why the hand needs no re-model guard (D321): every weapon reward in the August catalogue
    /// names the SAME third-person mesh as the gun it skins, on both bodies. A weapon skin is a
    /// colourway and nothing else, so D225's substitution in <c>BuildActiveHandAttachment</c> can
    /// only ever re-tint, and the pegs' D53 guard in <c>AugustWornSkins</c> never fires for a gun.
    /// </summary>
    [Fact]
    public void EveryWeaponSkinNamesTheMeshOfTheGunItSkins()
    {
        int compared = 0;
        foreach (AugustSkinCatalogEntry skin in AugustSkinCatalog.Weapons)
        {
            if (!AugustWornMeshCatalog.TryGet(skin.CategoryPrototypeId, out AugustWornMesh baseMesh))
            {
                continue;                       // the machetes: no worn-mesh row, no peg to dress
            }

            foreach (uint gender in (uint[])[CharacterVisuals.Male, CharacterVisuals.Female])
            {
                Assert.Equal(
                    AugustAssetIndex.Canonical(baseMesh.ModelNameFor(gender)),
                    AugustAssetIndex.Canonical(skin.ModelNameFor(gender)),
                    StringComparer.OrdinalIgnoreCase);
                compared++;
            }
        }

        Assert.True(compared > 100, $"only {compared} weapon-skin meshes compared");
    }

    /// <summary>
    /// The bystander (D322). A viewer that already has this character spawned gets the changed
    /// dress under this character's guid - the SKIN's rows in the hand - and, because the gun in
    /// the hand changed, the enter burst's <c>82 15 01</c> / <c>82 15 02</c> pair after it. Before
    /// this the enter burst was the only carrier of a peer's dress.
    /// </summary>
    [Fact]
    public void ABystanderReceivesTheSkinnedHandWhenTheGunIsDrawn()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();
        IReadOnlyList<uint> skinRows = table.AppearanceRowsFor(4073, CharacterVisuals.Male);
        uint skinGroup = table.ShaderGroupFor(4073, CharacterVisuals.Male);

        using var session = new Session(10, skins: null);
        session.ClickSkin(10, 4076);
        session.EnterAndDrop();
        session.PickUp();

        // Stand a viewer next to the session: registered, in the match, and already knowing this
        // character by a transient id - the state the interest sweep leaves after an ENTER burst.
        var sink = new RecordingSink();
        PeerSession viewer = session.Service.PeerRegistry.Register(0x2002, sink);
        viewer.InMatch = true;
        uint transient = viewer.View.Transients.Acquire(new EntityId(Self));
        Assert.True(transient >= TransientIdTable.FirstAllocated);

        session.SelectSlot(SurvivorLoadout.Wheel1);

        // 94 01 for OUR guid, with the skin in the hand.
        byte[] dress = Assert.Single(sink.Packets, p => p[0] == ZoneOpcodes.EquipmentBase && p[1] == 0x01);
        Assert.Equal(Self, BitConverter.ToUInt64(dress, 6));
        (uint slot, uint group, uint[] rows, string model) = Attachments(dress, tunnelHeader: false)
            .Single(a => a.Slot == AugustWornVisuals.ActiveHandSlotId);
        Assert.Equal("Weapon_M16A4_3P.adr", model);
        Assert.Equal(skinRows, rows);
        Assert.Equal(skinGroup, group);

        // then 82 15 01 Reset, then 82 15 02 AddWeapon, addressed to the viewer's id for us.
        int dressAt = sink.Packets.IndexOf(dress);
        Assert.True(sink.Packets.Count >= dressAt + 3, "no 82 15 pair after the re-dress");
        Assert.Equal(0xdc, sink.Packets[dressAt + 1][0]); // footwear audio accompanies peer dress
        byte[] reset = sink.Packets[dressAt + 2];
        byte[] add = sink.Packets[dressAt + 3];
        Assert.Equal(ZoneOpcodes.WeaponBase, reset[0]);
        Assert.Equal(0x15, reset[5]);
        Assert.Equal(0x01, reset[6]);
        Assert.Equal(ZoneOpcodes.WeaponBase, add[0]);
        Assert.Equal(0x15, add[5]);
        Assert.Equal(0x02, add[6]);
        Assert.Equal(transient << 2, (uint)add[7]);

        // A stow re-dresses again (the gun moves to a peg, the hand empties) - a second 94 01 and
        // a Reset with NO AddWeapon behind it, because an empty hand registers no weapon.
        sink.Packets.Clear();
        session.SelectSlot(SurvivorLoadout.Fists);
        byte[] stowDress = Assert.Single(sink.Packets, p => p[0] == ZoneOpcodes.EquipmentBase && p[1] == 0x01);
        (_, uint stowedGroup, uint[] stowedRows, _) = Attachments(stowDress, tunnelHeader: false)
            .Single(a => a.Model == "Weapon_M16A4_3P.adr");
        Assert.Equal(skinRows, stowedRows);
        Assert.Equal(skinGroup, stowedGroup);
        Assert.Single(sink.Packets, p => p[0] == ZoneOpcodes.WeaponBase && p[5] == 0x15 && p[6] == 0x01);
        Assert.DoesNotContain(sink.Packets, p => p[0] == ZoneOpcodes.WeaponBase && p[5] == 0x15 && p[6] == 0x02);
    }

    /// <summary>
    /// <c>CRANBERRY_PEER_REDRESS=0</c> is the rollback: the viewer is told nothing after the enter
    /// burst, which is exactly the pre-D322 relay.
    /// </summary>
    [Fact]
    public void TheRedressSwitchOffTellsABystanderNothing()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        using var session = new Session(10, skins: null, peers => peers with { Redress = false });
        session.EnterAndDrop();
        session.PickUp();

        var sink = new RecordingSink();
        PeerSession viewer = session.Service.PeerRegistry.Register(0x2002, sink);
        viewer.InMatch = true;
        viewer.View.Transients.Acquire(new EntityId(Self));

        session.SelectSlot(SurvivorLoadout.Wheel1);
        Assert.Empty(sink.Packets);
    }

    // ================================================================================= plumbing

    private static AugustSkinCatalogEntry Reward(uint rewardItemId) =>
        AugustSkinCatalog.Apparel
            .Concat(AugustSkinCatalog.Weapons)
            .Single(entry => entry.RewardItemId == rewardItemId);

    private static byte[] Str(string value)
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(value);
        return [.. BitConverter.GetBytes(text.Length), .. text];
    }

    private static AugustDynamicAppearanceTable Load()
    {
        Assert.True(
            AugustDynamicAppearanceTable.TryLoad(
                Source, out AugustDynamicAppearanceTable? table, out string status),
            status);
        return table!;
    }

    /// <summary>
    /// One session: log in as a male character, click one skin tile, drop one
    /// <paramref name="itemDefinitionId"/> on the development drop, pick it up, and return the
    /// (body slot, shader group, appearance rows) of the attachment wearing
    /// <paramref name="mesh"/> on a slot that is not the active hand.
    /// </summary>
    private static (uint Slot, uint Group, uint[] Rows) PickUpWearing(
        uint itemDefinitionId,
        uint categoryPrototypeId,
        uint clickedAccountItemId,
        string mesh,
        Func<SkinOptions, SkinOptions>? skins)
    {
        using var session = new Session(itemDefinitionId, skins);
        session.ClickSkin(categoryPrototypeId, clickedAccountItemId);
        session.EnterAndDrop();
        session.PickUp();
        return session.LastAttachment(mesh, hand: false);
    }

    /// <summary>
    /// A male character in a one-item world: log in, optionally click a skin, enter, take the
    /// development drop, pick the item up, press hotbar keys, and read attachments out of the
    /// last <c>94 01</c> each step sent. The same session the four 2026-09-03 cases used, with the
    /// hotbar added so the hand and the stow can be read too.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly ConcurrentQueue<Action> _pending = new();
        private readonly RecordingRecorder _recorder = new();
        private int _mark;

        public Session(
            uint itemDefinitionId,
            Func<SkinOptions, SkinOptions>? skins,
            Func<PeerOptions, PeerOptions>? peers = null)
        {
            var options = new ZoneOptions
            {
                DynamicAppearanceSourcePath = Source,
                DevGroundLootMs = 1,
                DevGroundLootCount = 1,
                GroundLootItemDefinitionId = itemDefinitionId,
                GroundLootRadius = 0f,
                SendDoors = false,
                SendVehicles = false,
                Skins = skins?.Invoke(new SkinOptions()) ?? new SkinOptions(),
                Peers = peers?.Invoke(PeerOptions.Default) ?? PeerOptions.Default,
            };

            var tickets = new GatewayTicketRegistry();
            GatewayAdmission admission = tickets.Issue(
                (uint)Self, "DDa", gender: 1, headId: 1, hairId: 1, skinToneId: 665, profileId: 270);
            Service = new ZoneService(new SilentLog(), _recorder, tickets, options)
            {
                Post = _pending.Enqueue,
            };
            var request = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
            Connection = new SoeConnection(
                new IPEndPoint(IPAddress.Loopback, 5555),
                in request,
                SessionSettings.WithSeed(1),
                Service.OnSessionRequest(new IPEndPoint(IPAddress.Loopback, 5555), in request),
                Service,
                new SilentLog(),
                (_, _) => { },
                now: 0);
            Service.OnConnected(Connection);

            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode);
            login.WriteUInt64(admission.Guid);
            login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol);
            login.WriteString(GatewayLoginRequest.AugustVersion);
            Service.OnMessage(Connection, login.Written.ToArray());
        }

        public ZoneService Service { get; }

        public SoeConnection Connection { get; }

        public void SelectWeaponSkin(ulong itemGuid, uint category, uint account)
        {
            using var use = new PacketWriter();
            use.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
            use.WriteByte(0xac); use.WriteByte(0x2c); use.WriteUInt64(1); use.WriteUInt32(88);
            use.WriteUInt64(Self); use.WriteUInt64(Self); use.WriteUInt64(Self);
            use.WriteUInt64(itemGuid); use.WriteByte(1);
            Service.OnMessage(Connection, use.Written.ToArray());
            SendSkinClick(Service, Connection, category, account, SetSkinItemManager.WeaponCollectionId);
        }

        public void ClickSkin(uint categoryPrototypeId, uint clickedAccountItemId) =>
            SendSkinClick(Service, Connection, categoryPrototypeId, clickedAccountItemId);

        public void EnterAndDrop()
        {
            SendClientIsReady(Service, Connection);
            Assert.True(SpinWait.SpinUntil(() => !_pending.IsEmpty, 30_000), "the development drop");
            Assert.True(_pending.TryDequeue(out Action? drop));
            drop!();
        }

        public void PickUp()
        {
            Mark();
            SendInteractRequest(Service, Connection, LootWorld.DefaultWorldGuidBase);
        }

        public byte[][] SentSinceMark() => [.. _recorder.Messages.Where(m => m.Direction == "s2c")
            .Skip(_mark).Select(m => m.Bytes)];

        public void DropItem(ulong itemGuid)
        {
            Mark();
            using var use = new PacketWriter();
            use.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            use.WriteByte(ItemUseOpcodes.ItemsBase);
            use.WriteByte(ItemUseOpcodes.RequestUseItemSub);
            use.WriteUInt64(1);
            use.WriteUInt32(4);
            use.WriteUInt64(Self);
            use.WriteUInt64(Self);
            use.WriteUInt64(Self);
            use.WriteUInt64(itemGuid);
            use.WriteByte(1);
            Service.OnMessage(Connection, use.Written.ToArray());
        }

        public void SelectSlot(uint slotId)
        {
            Mark();
            using var select = new PacketWriter();
            select.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            select.WriteByte(SelectLoadoutSlotRequest.Opcode);
            select.WriteByte(SelectLoadoutSlotRequest.SubOpcode);
            select.WriteUInt32(0);
            select.WriteUInt32(slotId);
            select.WriteUInt32(1234);
            Service.OnMessage(Connection, select.Written.ToArray());
        }

        /// <summary>
        /// The (slot, group, rows) of the attachment wearing <paramref name="mesh"/> in the LAST
        /// <c>94 01</c> sent since the last step - in the hand when <paramref name="hand"/> is
        /// set, on any other slot otherwise.
        /// </summary>
        public (uint Slot, uint Group, uint[] Rows) LastAttachment(string mesh, bool hand)
        {
            var packets = _recorder.Messages.Where(m => m.Direction == "s2c")
                .Skip(_mark).Select(m => m.Bytes).Where(p => p.Length > 2
                    && p[1] == ZoneOpcodes.EquipmentBase && p[2] is 1 or 2).Reverse();
            foreach (byte[] dress in packets)
            foreach ((uint slot, uint group, uint[] rows, string model) in Attachments(dress))
            {
                if ((slot == AugustWornVisuals.ActiveHandSlotId) == hand
                    && string.Equals(model, mesh, StringComparison.Ordinal))
                    return (slot, group, rows);
            }

            Assert.Fail($"no attachment for \"{mesh}\" {(hand ? "in the hand" : "outside the hand")} "
                + "in the last dress; sent " + string.Join("; ", packets.SelectMany(p => Attachments(p))
                    .Select(a => $"{a.Slot}:{a.Model}")));
            return default;
        }

        public void Dispose()
        {
        }

        public void Mark() => _mark = _recorder.Messages.Count(m => m.Direction == "s2c");
    }

    /// <summary>A viewer's sink: every zone packet the relay sends it, in order.</summary>
    private sealed class RecordingSink : IPeerSink
    {
        public List<byte[]> Packets { get; } = [];

        public bool IsOpen => true;

        public void Send(byte[] zonePacket) => Packets.Add(zonePacket);
    }

    /// <summary>
    /// The attachment list of a <c>94 01</c> / <c>SetCharacterEquipmentWithSlots</c>. The slot rows
    /// are <c>{u32 slotId; u32 slotId; u64 itemGuid; str; str}</c> — five fields, not two.
    /// </summary>
    private static List<(uint Slot, uint Group, uint[] Rows, string Model)> Attachments(
        byte[] packet,
        bool tunnelHeader = true)
    {
        var reader = new PacketReader(packet.AsSpan(tunnelHeader ? 1 : 0));
        reader.ReadByte();
        byte sub = reader.ReadByte();
        reader.ReadUInt32();                 // profile id
        reader.ReadUInt64();                 // character guid
        if (sub == 1)
        {
        reader.ReadUInt32();                 // unknown
        reader.ReadString();                 // tint alias
        reader.ReadString();                 // decal alias
        int slots = reader.ReadInt32();
        for (int i = 0; i < slots; i++)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt64();
            reader.ReadString();
            reader.ReadString();
        }

        }
        else
        {
            reader.ReadUInt32(); // key
            reader.ReadUInt32(); // body slot
            reader.ReadUInt64(); // item
            reader.ReadString();
            reader.ReadString();
        }
        int count = sub == 1 ? reader.ReadInt32() : 1;
        var found = new List<(uint, uint, uint[], string)>(count);
        for (int i = 0; i < count; i++)
        {
            string model = reader.ReadString();
            reader.ReadString();             // texture alias
            reader.ReadString();             // tint alias
            reader.ReadString();             // decal alias
            reader.ReadUInt32();             // tint id
            reader.ReadUInt32();             // composite effect
            reader.ReadUInt32();             // effect
            uint slot = reader.ReadUInt32();
            uint group = reader.ReadUInt32();
            int appearances = reader.ReadInt32();
            var rows = new uint[appearances];
            for (int row = 0; row < appearances; row++)
            {
                rows[row] = reader.ReadUInt32();
            }

            reader.ReadByte();               // trailing flag
            found.Add((slot, group, rows, model));
        }

        return found;
    }

    private static void SendClientIsReady(ZoneService service, SoeConnection connection)
    {
        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        service.OnMessage(connection, ready.Written.ToArray());
    }

    private static void SendInteractRequest(
        ZoneService service,
        SoeConnection connection,
        ulong targetGuid)
    {
        using var interact = new PacketWriter();
        interact.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        interact.WriteByte(ZoneOpcodes.CommandBase);
        interact.WriteUInt16(InteractRequest.SubOpcode);
        interact.WriteUInt64(targetGuid);
        service.OnMessage(connection, interact.Written.ToArray());
    }

    private static void SendSkinClick(
        ZoneService service,
        SoeConnection connection,
        uint categoryPrototypeId,
        uint clickedAccountItemId,
        uint collectionId = SetSkinItemManager.ApparelCollectionId)
    {
        // captures/wire-20260903-194804.txt — ac 32 {1, 0, collection, category, clicked}, 22 B.
        using var click = new PacketWriter();
        click.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        click.WriteByte(ZoneOpcodes.ItemsBase);
        click.WriteByte(SkinItemSelectionRequest.RequestSetSkinItemByItemId);
        click.WriteUInt32(1);
        click.WriteUInt32(0);
        click.WriteUInt32(collectionId);
        click.WriteUInt32(categoryPrototypeId);
        click.WriteUInt32(clickedAccountItemId);
        service.OnMessage(connection, click.Written.ToArray());
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
}
