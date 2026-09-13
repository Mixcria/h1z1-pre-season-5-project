using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Equipment;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Equipment;

public sealed class FistsEquipmentBindingTests
{
    [Fact]
    public void FistsRequireTheDeliveredComponentAndStopBindingWhenAnotherSlotIsSelected()
    {
        ulong next = 100;
        var inventory = new PlayerInventory(10, () => ++next);
        inventory.Bootstrap();
        var weapons = new WeaponSession(WeaponStageOptions.Default);
        Assert.Null(FistsEquipmentBinding.Create(10, inventory, weapons.Clearance));
        weapons.MarkWeaponDefinitionsSent();
        Assert.Null(FistsEquipmentBinding.Create(10, inventory, weapons.Clearance));
        var fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];
        using var writer = new PacketWriter();
        weapons.CreateItemAdd(10, fists.ToRecord(10))(writer);
        var binding = Assert.IsType<SetCharacterEquipmentSlot>(
            FistsEquipmentBinding.Create(10, inventory, weapons.Clearance));
        Assert.Equal(fists.Guid, binding.Row.ItemGuid);
        Assert.Equal(BodySlots.RightHand, binding.Row.SlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Binoculars, out _));
        Assert.Null(FistsEquipmentBinding.Create(10, inventory, weapons.Clearance));
        Assert.True(inventory.TrySelectLoadoutSlot(SurvivorLoadout.Fists, out _));
        Assert.NotNull(FistsEquipmentBinding.Create(10, inventory, weapons.Clearance));
        weapons.Ledger.Forget(fists.Guid);
        Assert.Null(FistsEquipmentBinding.Create(10, inventory, weapons.Clearance));
    }
}
