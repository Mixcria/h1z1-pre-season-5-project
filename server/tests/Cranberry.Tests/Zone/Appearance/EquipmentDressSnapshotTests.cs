using Cranberry.Zone;
using Cranberry.Zone.Appearance;

namespace Cranberry.Tests.Zone.Appearance;

public sealed class EquipmentDressSnapshotTests
{
    private static readonly CharacterEquipmentAttachment Shirt = new("shirt.adr", 3);
    private static readonly CharacterEquipmentAttachment Pump = new("Weapons_PumpShotgun01_3P.adr", 76);

    [Fact]
    public void SwappingTwoGunsUpdatesThePegWithoutRebuildingClothesOrClearingTheHand()
    {
        var before = new EquipmentDressSnapshot([Shirt, new("ak.adr", 7), Pump],
            [new(3, 10), new(76, 20)]);
        var after = new EquipmentDressSnapshot([Shirt, new("pump.adr", 7), new("ak.adr", 76)],
            [new(3, 10), new(76, 30)]);
        var changes = after.DeltaFrom(before, 100, null, handBoundSeparately: true);
        byte[] packet = Assert.Single(changes!);
        Assert.Equal(new byte[] { 0x94, 2 }, packet[..2]);
        Assert.Equal(76u, BitConverter.ToUInt32(packet, 14));
        Assert.Equal(30ul, BitConverter.ToUInt64(packet, 22));
        Assert.DoesNotContain("shirt.adr", System.Text.Encoding.UTF8.GetString(packet));
    }

    [Fact]
    public void PickingUpAShotgunUpdatesOnlyItsSlot()
    {
        var before = new EquipmentDressSnapshot([Shirt], [new(3, 10)]);
        var after = new EquipmentDressSnapshot([Shirt, Pump], [new(3, 10), new(76, 20)]);
        byte[] packet = Assert.Single(after.DeltaFrom(before, 100, null)!);
        Assert.Equal(new byte[] { 0x94, 2 }, packet[..2]);
        Assert.Equal(76u, BitConverter.ToUInt32(packet, 14));
        Assert.Equal(20ul, BitConverter.ToUInt64(packet, 22));
        Assert.DoesNotContain("shirt.adr", System.Text.Encoding.UTF8.GetString(packet));
    }

    [Fact]
    public void RemovingTheWeaponClearsOnlyItsSlot()
    {
        var before = new EquipmentDressSnapshot([Shirt, Pump], [new(3, 10), new(76, 20)]);
        var after = new EquipmentDressSnapshot([Shirt], [new(3, 10)]);
        byte[] packet = Assert.Single(after.DeltaFrom(before, 100, null)!);
        Assert.Equal(new byte[] { 0x94, 3 }, packet[..2]);
        Assert.Equal(76u, BitConverter.ToUInt32(packet, 18));
        Assert.Empty(after.DeltaFrom(after, 100, null)!);
    }

    [Fact]
    public void AnUnboundChangeRequiresTheFullDress()
    {
        var before = new EquipmentDressSnapshot([Shirt], []);
        var after = new EquipmentDressSnapshot([Shirt with { ModelName = "other.adr" }], []);
        Assert.Null(after.DeltaFrom(before, 100, null));
    }

    [Fact]
    public void EquipmentWithoutAMeshUpdatesItsBindingWithoutRefreshingClothes()
    {
        var before = new EquipmentDressSnapshot([Shirt], [new(3, 10)]);
        var after = new EquipmentDressSnapshot([Shirt], [new(3, 10), new(11, 100)]);
        byte[] packet = Assert.Single(after.DeltaFrom(before, 100, null)!);
        Assert.Equal(new byte[] { 0x94, 2 }, packet[..2]);
        Assert.Equal(11u, BitConverter.ToUInt32(packet, 14));
        Assert.Equal(100ul, BitConverter.ToUInt64(packet, 22));
        Assert.Equal(0, BitConverter.ToInt32(packet, 38)); // empty attachment model
        Assert.Empty(after.DeltaFrom(after, 100, null)!);
    }

    [Fact]
    public void RequiredHeadRemovalAndAnExistingMeshBecomingUnresolvedRequireFullDress()
    {
        var before = new EquipmentDressSnapshot([Shirt, new("helmet.adr", 1)], [new(3, 10), new(1, 20)]);
        var bare = new EquipmentDressSnapshot([Shirt], [new(3, 10)]);
        Assert.Null(bare.DeltaFrom(before, 100, null));
        var missingMesh = new EquipmentDressSnapshot([Shirt], [new(3, 10), new(1, 20)]);
        Assert.Null(missingMesh.DeltaFrom(before, 100, null));
    }

    [Fact]
    public void ReplacingAHelmetUpdatesItWithoutClearingTheRequiredHeadSlot()
    {
        var before = new EquipmentDressSnapshot([Shirt, new("helmet.adr", 1)], [new(3, 10), new(1, 20)]);
        var after = new EquipmentDressSnapshot([Shirt, new("other-helmet.adr", 1)], [new(3, 10), new(1, 30)]);
        byte[] packet = Assert.Single(after.DeltaFrom(before, 100, null)!);
        Assert.Equal(new byte[] { 0x94, 2 }, packet[..2]);
        Assert.Equal(1u, BitConverter.ToUInt32(packet, 14));
        Assert.Equal(30ul, BitConverter.ToUInt64(packet, 22));
    }

    [Fact]
    public void SeparatelyBoundHandChangesAreDetectedWithoutSendingAGuardedRow()
    {
        var before = new EquipmentDressSnapshot([Shirt, new("ak.adr", 7)], [new(3, 10)]);
        var shirtChange = new EquipmentDressSnapshot([Shirt with { TintId = 123 }, new("ak.adr", 7)], [new(3, 10)]);
        Assert.False(shirtChange.HandDiffersFrom(before));
        var handChange = new EquipmentDressSnapshot([Shirt, new("pump.adr", 7)], [new(3, 10)]);
        Assert.True(handChange.HandDiffersFrom(before));
        Assert.Empty(handChange.DeltaFrom(before, 100, null, handBoundSeparately: true)!);
        Assert.Null(handChange.DeltaFrom(before, 100, null));
    }
}
