using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Equipment;

/// <summary>Binds the real fists item only after its weapon component has been delivered.</summary>
public static class FistsEquipmentBinding
{
    public static SetCharacterEquipmentSlot? Create(ulong characterId, PlayerInventory inventory,
        IActiveHandClearance? clearance)
    {
        if (inventory.CurrentLoadoutSlotId != SurvivorLoadout.Fists
            || inventory.WieldedItemGuid != 0
            || !inventory.LoadoutSlots.TryGetValue(SurvivorLoadout.Fists, out var fists)
            || fists.DefinitionId != PlayerInventory.SurvivorFistsItemDefinitionId)
            return null;
        var packet = new SetCharacterEquipmentSlot(characterId, new(BodySlots.RightHand, fists.Guid),
            new("Weapon_Empty.adr", BodySlots.RightHand), Clearance: clearance);
        return packet.IsPermitted ? packet : null;
    }
}
