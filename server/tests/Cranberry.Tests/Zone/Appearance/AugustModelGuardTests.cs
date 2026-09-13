using Cranberry.Zone.Appearance;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// docs/54 §2 and I2. A ground prop has no colour lever on the wire — <c>AddLightweightNpc</c>
/// carries no shader group and no appearance ids, and every ground <c>.adr</c> in the roster
/// declares one texture alias and an empty <c>&lt;TintAliases/&gt;</c> — so the <c>Models.txt</c>
/// row IS the item's colour, and the only two ways to get it wrong are to pick the wrong row or to
/// pick one the client cannot load. Both happened, and both are pinned here.
/// </summary>
public sealed class AugustModelGuardTests
{
    private static readonly Lazy<LootTables> Tables = new(LootTables.LoadDefault);

    [Fact]
    public void EveryGroundModelInTheLootTablesResolvesAndShips()
    {
        var used = new List<AugustModelUse>();
        foreach (LootCategoryTable category in Tables.Value.Categories)
        {
            foreach (LootTableEntry entry in category.Entries)
            {
                used.Add(new AugustModelUse(
                    entry.GroundModelId,
                    $"loot category {category.Key} item {entry.ItemDefinitionId} ({entry.Name})"));
            }
        }

        foreach (LootClusterBox box in Tables.Value.Clusters)
        {
            used.Add(new AugustModelUse(
                box.GroundModelId,
                $"cluster box for weapon {box.WeaponItemDefinitionId}, "
                    + $"item {box.ItemDefinitionId} ({box.Name})"));
        }

        // 37 distinct ground models at wave 4, of which exactly one — 10137 — was unshippable.
        Assert.NotEmpty(used);
        Assert.Empty(AugustModelGuard.Unloadable(used));
    }

    [Fact]
    public void EveryVehicleAndDoorModelResolvesAndShips()
    {
        var used = new List<AugustModelUse>();
        foreach (VehicleDefinition vehicle in VehicleRoster.LoadDefault().Vehicles)
        {
            used.Add(new AugustModelUse(vehicle.ModelId, $"vehicle {vehicle.Name}"));
        }

        foreach (DoorKind kind in Z2Doors.LoadDefault().Kinds)
        {
            used.Add(new AugustModelUse(kind.ModelId, $"door kind {kind.Name}"));

            // THE ID ACTUALLY ON THE WIRE. DoorCollision.SpawnModelFor returns CollisionModelId
            // whenever the family has a twin and the mode is KinematicMesh — which is the shipped
            // default (ZoneOptions.DoorCollision), and covers 3,639 of Z2's 4,103 doors. Sweeping
            // kind.ModelId alone left the guard inert for the id the client is handed, which is
            // exactly the failure it was written for one wave earlier: model 10137
            // Common_Props_AmmoBoxes_Shotgun.adr is named by Models.txt, ships in none of the 256
            // packs, and produced the owner's white shotgun box.
            if (kind.HasCollisionModel)
            {
                used.Add(new AugustModelUse(
                    kind.CollisionModelId,
                    $"door kind {kind.Name} kinematic collision twin"));
            }
        }

        Assert.NotEmpty(used);
        Assert.Empty(AugustModelGuard.Unloadable(used));
    }

    /// <summary>
    /// Symptom A, half one: <i>"the AR-15 … should have green boxes but it's appearing as brown."</i>
    /// The AR-15's own actor is achromatic (docs/54 §2.1 — <c>M16A4_C_3P.dds</c> mean RGB 86,86,86,
    /// one texture alias, empty tint aliases), so the coloured objects the owner is describing are
    /// the two <c>.223 Round</c> boxes the owner's own cluster rule puts beside every gun. They were
    /// the AK-47's tan box.
    /// </summary>
    [Theory]
    [InlineData(1429u, AugustModelGuard.OliveAmmunitionBoxModelId, "Common_Props_AmmoBox02.adr")]
    [InlineData(2325u, AugustModelGuard.AkAmmunitionBoxModelId, "Common_Props_AmmoBoxes_AK47.adr")]
    [InlineData(1511u, AugustModelGuard.ShotgunAmmunitionBoxModelId, "Common_Props_AmmoBoxe01.adr")]
    public void AmmunitionBoxPropsAreTheOnesTheClientsOwnMarkersUse(
        uint itemDefinitionId,
        uint expectedModelId,
        string expectedFileName)
    {
        // Both the cluster beside the gun and the loose Ammo01 roll must agree: they are the same
        // box seen two ways, and the pair drifting apart is how 10137 survived a whole wave.
        LootClusterBox box = Assert.Single(
            Tables.Value.Clusters.ToArray(),
            candidate => candidate.ItemDefinitionId == itemDefinitionId);
        Assert.True(Tables.Value.TryGet("Ammo01", out LootCategoryTable? ammunition));
        LootTableEntry loose = Assert.Single(
            ammunition!.Entries.ToArray(),
            candidate => candidate.ItemDefinitionId == itemDefinitionId);

        Assert.Equal(expectedModelId, box.GroundModelId);
        Assert.Equal(expectedModelId, loose.GroundModelId);
        Assert.Equal(expectedFileName, AugustModelCatalog.FileNameFor(expectedModelId));
        Assert.True(AugustModelCatalog.CanLoad(expectedModelId));
    }

    /// <summary>
    /// Symptom A, half two, and the reason it is LIVE-VERIFIED rather than argued: the shotgun's
    /// box named an actor that is in <c>Models.txt</c> and in none of the 256 asset packs, and the
    /// owner's own client logged <c>Failed to load asset</c> for it ten times (docs/54 §2.3).
    /// </summary>
    [Fact]
    public void TheShotgunBoxModelThatShipsInNoPackIsNeverNamedAgain()
    {
        Assert.True(AugustModelCatalog.TryGet(
            AugustModelGuard.AbsentShotgunAmmunitionBoxModelId,
            out AugustModelRow row));
        Assert.Equal("Common_Props_AmmoBoxes_Shotgun.adr", row.FileName);
        Assert.False(row.Ships);
        Assert.False(AugustModelCatalog.CanLoad(AugustModelGuard.AbsentShotgunAmmunitionBoxModelId));

        foreach (LootCategoryTable category in Tables.Value.Categories)
        {
            foreach (LootTableEntry entry in category.Entries)
            {
                Assert.NotEqual(
                    AugustModelGuard.AbsentShotgunAmmunitionBoxModelId,
                    entry.GroundModelId);
            }
        }

        foreach (LootClusterBox box in Tables.Value.Clusters)
        {
            Assert.NotEqual(
                AugustModelGuard.AbsentShotgunAmmunitionBoxModelId,
                box.GroundModelId);
        }
    }

    [Fact]
    public void TheCatalogueIsTheWholeAugustModelsTable()
    {
        Assert.Equal(AugustModelCatalog.RowCount, AugustModelCatalog.Rows.Count);
        Assert.Equal(1173, AugustModelCatalog.RowCount);
        Assert.Equal(1099, AugustModelCatalog.ShippableRowCount);
        Assert.Equal(50_502, AugustModelCatalog.IndexedAssetCount);
        Assert.Equal(
            AugustModelCatalog.ShippableRowCount,
            AugustModelCatalog.Rows.Count(row => row.Ships));
    }

    [Fact]
    public void AnIdOutsideTheAugustModelsTableIsAFailureAndSaysSo()
    {
        const uint invented = 999_999;
        Assert.False(AugustModelCatalog.CanLoad(invented));
        Assert.Equal(string.Empty, AugustModelCatalog.FileNameFor(invented));

        string failure = Assert.Single(
            AugustModelGuard.Unloadable([new AugustModelUse(invented, "a future lane")]));
        Assert.Contains("a future lane", failure, StringComparison.Ordinal);
        Assert.Contains("not a row in the August Models.txt", failure, StringComparison.Ordinal);
    }
}
