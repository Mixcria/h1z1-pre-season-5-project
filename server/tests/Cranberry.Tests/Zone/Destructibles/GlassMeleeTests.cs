using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Destructibles;

namespace Cranberry.Tests.Zone.Destructibles;

public sealed class GlassMeleeTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.07f)]
    [InlineData(-0.15f)]
    public void StandingOnARoofPaneRequiresLookingDownAndUsesTheSameReach(float slope)
    {
        var normal = Vector3.Normalize(new Vector3(slope, 1, 0));
        var up = Vector3.Normalize(new Vector3(1, -slope, 0));
        var pane = new GlassPane(25, new(10, 8, 10), normal, up, 2.5f, 1.1f);
        var glass = new GlassMeleeCatalog([pane]);
        var world = new DestructibleWorld();
        var feet = pane.Center + Vector3.UnitY * 0.02f;
        Assert.Null(glass.Find(feet, 0, world));
        Assert.Null(glass.Find(feet, 0, world, MathF.PI / 2));
        var hit = Assert.IsType<GlassMeleeHit>(glass.Find(feet, 0, world, -MathF.PI / 2));
        Assert.Equal(25u, hit.ObjectId);
        Assert.Equal(1.22f, hit.Distance, 3);
        Assert.NotNull(glass.Find(feet, 0, world, -MathF.PI / 3));
        Assert.Null(glass.Find(feet + Vector3.UnitY, 0, world, -MathF.PI / 2));
        Assert.Null(glass.Find(feet + Vector3.UnitZ * 2, 0, world, -MathF.PI / 2));
        Assert.Null(glass.Find(feet, 0, world, float.NaN));
        Assert.Null(glass.Find(feet, 0, world, float.PositiveInfinity));
        Assert.Null(glass.Find(feet, 0, world, MathF.PI));
    }

    [Fact]
    public void AuthoredWarehouseRoofPanesAreAlreadyBreakableGlass()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Data/Destructibles/z2-glass-panes.json")));
        var roofs = doc.RootElement.GetProperty("panes").EnumerateArray()
            .Where(p => MathF.Abs(p[5].GetSingle()) > 0.7f).ToArray();
        Assert.Equal(478, roofs.Length);
        foreach (var row in roofs)
        {
            uint objectId = row[0].GetUInt32();
            Assert.True(DestructibleCatalog.Default.Props[objectId].IsGlass);
            var feet = new Vector3(row[1].GetSingle(), row[2].GetSingle() + 0.02f, row[3].GetSingle());
            var hit = GlassMeleeCatalog.Default.Find(feet, 0, new DestructibleWorld(), -MathF.PI / 2);
            Assert.Equal(objectId, hit?.ObjectId);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.78f)]
    [InlineData(1.57f)]
    [InlineData(3.14f)]
    public void RotatedPanesUseTheirSurfaceAndAcceptAPunchFromEitherSide(float yaw)
    {
        var normal = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        var pane = new GlassPane(1, new(100, 5, 100), normal, Vector3.UnitY, 1.1f, 3f);
        // Wide pane: punch near its edge, well outside the radius of the placement origin.
        var edge = pane.Center + Vector3.Cross(normal, Vector3.UnitY) * 2.9f;
        Assert.Equal(1.5f, pane.Contact(edge + normal * 1.5f, -normal)!.Value, 4);
        Assert.Equal(1.5f, pane.Contact(edge - normal * 1.5f, normal)!.Value, 4);
        Assert.Null(pane.Contact(edge + normal * 2.1f, -normal));
        Assert.Null(pane.Contact(edge + normal, normal));
        Assert.Null(pane.Contact(edge + Vector3.UnitY * 2 + normal, -normal));
        Assert.Null(pane.Contact(new(float.NaN, 0, 0), normal));
    }

    [Fact]
    public void ClosestIntactPaneWinsAndBreakingItUsesTheSharedDestructionLedger()
    {
        var pane = new GlassPane(1, new(0, 1.2f, 1), Vector3.UnitZ, Vector3.UnitY, 1, 1);
        var panes = new GlassMeleeCatalog([pane, pane with { ObjectId = 2, Center = new(0, 1.2f, 1.8f) }]);
        var world = new DestructibleWorld();
        var catalog = new DestructibleCatalog([
            new(1, "Common_Props_GlassWindow01.adr", Vector3.Zero, 1, 2000),
            new(2, "Common_Props_TintedWindow01.adr", Vector3.Zero, 1, 2000),
            new(3, "Farm_Props_Fences_Fence01.adr", Vector3.Zero, 1, 5000)]);
        Assert.Equal(1u, panes.Find(Vector3.Zero, 0, world)!.Value.ObjectId);
        Assert.True(world.BreakGlass(catalog, 1)!.Value.Destroyed);
        Assert.Null(world.BreakGlass(catalog, 1));
        Assert.Null(world.BreakGlass(catalog, 3));
        Assert.Equal(2u, panes.Find(Vector3.Zero, 0, world)!.Value.ObjectId);
        Assert.Null(panes.Find(Vector3.Zero, MathF.PI, world));
        Assert.Null(panes.Find(Vector3.Zero, null, world));
        Assert.Single(world.Destroyed);
        Assert.Equal(15407, GlassMeleeCatalog.Default.Count);
    }

    [Fact]
    public void GlassUsesTheSameSwingCadenceAndDoesNotPayAgainForAbilityCopies()
    {
        var session = new SessionCombat { MeleeGlass = new(12, 1.5f), MeleeHeading = 0 };
        var options = CombatOptions.Default;
        Assert.Equal(12u, MeleeArm.ResolveTriggerSwing(session, options, 85, 1, Vector3.Zero, 1000, 1).MeleeGlassObjectId);
        Assert.Null(MeleeArm.ResolveTriggerSwing(session, options, 85, 1, Vector3.Zero, 1100, 2).MeleeGlassObjectId);
        var replies = new List<WeaponArmResult>();
        MeleeArm.Handle(session, Convert.FromHexString("A0030400000075F4100003000000"), options,
            85, Vector3.Zero, 1600, replies);
        Assert.All(replies, reply => Assert.Null(reply.MeleeGlassObjectId));
        session.MeleePlayers.Add(new(999, new(0, 0, 0.8f)));
        var nearerPlayer = MeleeArm.ResolveTriggerSwing(session, options, 85, 1, Vector3.Zero, 2000, 3);
        Assert.Null(nearerPlayer.MeleeGlassObjectId);
        Assert.Equal(999ul, nearerPlayer.MeleeHit!.Value.Guid);
    }
}
