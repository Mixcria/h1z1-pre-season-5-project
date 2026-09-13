using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

// docs/81 §4. The owner's retail Pre-Season-3 numbers, adopted under D53, on Cranberry's own
// 10,000-unit bar. These tests pin the ARITHMETIC the owner will feel when he plays - how many body
// shots a kill takes - rather than the table's spelling.
public sealed class ShootingBalanceTests
{
    [Fact]
    public void TheBarIsOneHundredUnitsPerHitPointOnBothSettings()
    {
        // ZoneRetailBalance keys its numbers to a 100 HP bar; both of Cranberry's health settings
        // are 10,000. If either ever moves, every damage number in this lane silently rescales.
        Assert.Equal(100, RetailBalance.UnitsPerHp);
        Assert.Equal(10_000, MatchSettings.Default.StartingHealth);
        Assert.Equal(10_000u, MatchSettings.Default.Gas.MaxHitpoints);
        Assert.Equal(RetailBalance.FullHealthUnits, MatchSettings.Default.StartingHealth);
    }

    [Theory]
    [InlineData(6u, 2_500, 4)]      // AR-15
    [InlineData(10u, 2_500, 4)]     // AR-15, the sheet's second row
    [InlineData(1405u, 3_000, 4)]   // AK-47
    [InlineData(1373u, 6_500, 2)]   // .308 Hunting Rifle
    [InlineData(1388u, 3_000, 4)]   // .44 Magnum
    [InlineData(2u, 2_500, 4)]      // M1911A1
    [InlineData(1401u, 1_800, 6)]   // M9
    [InlineData(1400u, 1_800, 6)]   // R380
    public void EachWeaponTakesTheStatedNumberOfBodyShots(uint definitionId, int units, int shots)
    {
        int perShot = RetailBalance.BodyDamageUnits(definitionId, distance: 0, unmappedUnits: 0);
        Assert.Equal(units, perShot);

        int taken = (int)Math.Ceiling(MatchSettings.Default.StartingHealth / (double)perShot);
        Assert.Equal(shots, taken);
    }

    [Fact]
    public void TheStarterWeaponResolvesThroughTheAugustSheetToTheArFifteenRow()
    {
        // The whole join, in one assertion: item 2425 -> WEAPON_ID 6 -> the AR-15 row, with no
        // Cranberry-authored mapping table anywhere in between.
        Assert.True(AugustWeaponFacts.TryGet(AugustHeldWeapon.ItemDefinitionId, out AugustWeaponFact fact));
        Assert.Equal(6u, fact.WeaponId);

        RetailWeapon? maybe = RetailBalance.RowForItem(AugustHeldWeapon.ItemDefinitionId);
        Assert.NotNull(maybe);
        RetailWeapon row = maybe.Value;
        Assert.Equal("AR-15", row.Name);
        Assert.Equal(WeaponClass.Rifle, row.Class);
    }

    [Fact]
    public void TheTwentySixteenSkinIdsAreNotInTheTable()
    {
        // docs/81 §4c.1: in the August ClientItemDefinitions 1449 is a CreateRecipe and 1450/1451/
        // 1452 are Generic rows. Porting the owner's skin aliases would have attached rifle damage
        // to a crafting recipe.
        foreach (uint skin in new uint[] { 1448, 1449, 1450, 1451, 1452 })
        {
            Assert.Null(RetailBalance.RowFor(skin));
        }
    }

    [Fact]
    public void AnUnmappedWeaponGetsTheCranberryDefaultAndNotAThirdPartyTable()
    {
        int units = RetailBalance.BodyDamageUnits(
            weaponDefinitionId: 999_999,
            distance: 0,
            CombatOptions.Default.UnmappedWeaponBodyUnits);

        Assert.Equal(2_000, units);
        Assert.Equal(5, (int)Math.Ceiling(MatchSettings.Default.StartingHealth / (double)units));
    }

    [Fact]
    public void TheShotgunReliesOnSpreadForRangeAndRetainsItsFullHitBudget()
    {
        const uint pump = 1374;

        foreach (double distance in new double[] { 0, 5, 17.5, 30, 100, 500 })
            Assert.Equal(706, RetailBalance.BodyDamageUnits(pump, distance, 0));
        Assert.InRange(706 * RetailBalance.RetailShotgunPellets, 12_000, 12_002);
    }

    [Fact]
    public void TheRateOfFireGateComesFromTheAugustSheetUnderACranberryFloor()
    {
        // docs/81 §5. The sheet is STRICTER than the owner's transcribed 1300 / 400 overrides on
        // both weapons where they differ, which is the right direction for an authoritative gate.
        int floor = CombatOptions.Default.RefireFloorMs;

        Assert.Equal(120, RetailBalance.RefireGateMs(2425, floor));   // AR-15
        Assert.Equal(1800, RetailBalance.RefireGateMs(1373, floor));  // .308, not his 1300
        Assert.Equal(750, RetailBalance.RefireGateMs(1374, floor));   // 12GA, not his 400
        Assert.Equal(200, RetailBalance.RefireGateMs(2, floor));      // M1911

        // The floor only ever raises a zero row; it never gates a real weapon.
        Assert.Equal(40, floor);
        Assert.Equal(floor, RetailBalance.RefireGateMs(itemDefinitionId: 999_999, floor));
    }

    [Fact]
    public void ArmourTiersComeFromTheAugustDatasheet()
    {
        // docs/81 §4f, and it corrects the owner's comment: laminated is BULK 1000 at August, not
        // the 500 he quotes - his account of the 6/29 patch is right and this is the post-patch
        // client.
        Assert.Equal(ArmourTier.Makeshift, ArmourModel.TierOf(2204));   // BULK 50
        Assert.Equal(ArmourTier.Makeshift, ArmourModel.TierOf(2205));   // BULK 50
        Assert.Equal(ArmourTier.Laminated, ArmourModel.TierOf(2271));   // BULK 1000
        Assert.Equal(ArmourTier.None, ArmourModel.TierOf(2604));        // kevlar, IS_ARMOR 0
        Assert.Equal(ArmourTier.None, ArmourModel.TierOf(0));

        // He hard-codes 3378 alone; August carries four rows of exactly that shape, so the whole
        // BULK 325 band is classed rather than one id.
        foreach (uint band in new uint[] { 3378, 4022, 4068, 4183 })
        {
            Assert.Equal(ArmourTier.Makeshift, ArmourModel.TierOf(band));
        }

        Assert.Equal(1, ArmourModel.AbsorbsOf(ArmourTier.Makeshift));
        Assert.Equal(2, ArmourModel.AbsorbsOf(ArmourTier.Laminated));
    }

    [Fact]
    public void EveryGeneratedArmourRowIsOneOfTheTwoClasses()
    {
        Assert.NotEmpty(AugustArmourFacts.All);

        foreach (AugustArmourFact fact in AugustArmourFacts.All)
        {
            Assert.True(
                fact.ItemClass is AugustArmourFacts.BodyArmourClass or AugustArmourFacts.HeadClass);
            Assert.True(ArmourModel.IsHelmet(fact.ItemId) ^ (ArmourModel.TierOf(fact.ItemId) != ArmourTier.None));
        }
    }

    [Fact]
    public void AHelmetIsAHeadRowWithTheClientsOwnArmourFlag()
    {
        // A beanie must not stop a rifle round: IS_ARMOR is the flag that keeps hats out.
        AugustArmourFact helmet = AugustArmourFacts.All.First(
            row => row.ItemClass == AugustArmourFacts.HeadClass);

        Assert.True(ArmourModel.IsHelmet(helmet.ItemId));
        Assert.Equal(250u, helmet.Bulk);
        Assert.False(ArmourModel.IsHelmet(2204));   // body armour is not a helmet
        Assert.False(ArmourModel.IsHelmet(0));
    }

    [Fact]
    public void AFreshVestRefreshesTheAbsorbBudgetAndABrokenOneDoesNot()
    {
        // The difference between armour and an infinite shield.
        var armour = default(Armour);
        const uint laminated = 2271;

        Assert.Equal(ArmourTier.Laminated, armour.Wearing(laminated));
        Assert.Equal(2, armour.AbsorbsLeft);

        armour.AbsorbsLeft = 0;
        Assert.Equal(ArmourTier.None, armour.Wearing(laminated));   // same item, still spent

        Assert.Equal(ArmourTier.Makeshift, armour.Wearing(2204));   // a different item rearms
        Assert.Equal(1, armour.AbsorbsLeft);
    }

    [Fact]
    public void TheMedicalRowsAreTheOwnersRetailNumbers()
    {
        Medical? bandageRow = MedicalModel.For(2423);
        Assert.NotNull(bandageRow);
        Medical bandage = bandageRow.Value;
        Assert.Equal(10, bandage.TotalHp);
        Assert.Equal(10, bandage.OverSeconds);
        Assert.Equal(3, bandage.ApplySeconds);
        Assert.True(bandage.StopsBleed);
        Assert.Equal(3_000, MedicalModel.ApplyMsFor(2423));
        Assert.Equal(100, MedicalModel.HealUnitsPerTick(bandage));   // 1 HP a second

        Medical? kitRow = MedicalModel.For(2424);
        Assert.NotNull(kitRow);
        Medical kit = kitRow.Value;
        Assert.Equal(60, kit.TotalHp);
        Assert.Equal(60, kit.OverSeconds);
        Assert.Equal(5, kit.ApplySeconds);
        Assert.Equal(5_000, MedicalModel.ApplyMsFor(2424));
        Assert.Equal(100, MedicalModel.HealUnitsPerTick(kit));

        Assert.Equal(MedicalModel.For(24), MedicalModel.For(2423));  // the same bandage, two ids
        Assert.Null(MedicalModel.For(2425));                          // the AR-15 is not a medical
    }

    [Fact]
    public void BleedIsFiveStatesDrainingFiftyUnitsPerStatePerSecond()
    {
        Assert.Equal(5, MedicalModel.MaxBleedSeverity);
        Assert.Equal(0, MedicalModel.BleedUnitsPerSecond(0));
        Assert.Equal(50, MedicalModel.BleedUnitsPerSecond(1));     // 0.5 HP/s - a nuisance
        Assert.Equal(250, MedicalModel.BleedUnitsPerSecond(5));    // 2.5 HP/s - an emergency
        Assert.Equal(250, MedicalModel.BleedUnitsPerSecond(99));   // clamped
    }
}
