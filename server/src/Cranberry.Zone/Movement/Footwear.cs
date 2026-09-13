using Cranberry.Protocol;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Movement;

public enum FootwearTier { Barefoot, Stealth, Sturdy, Fast }

/// <summary>August item passive abilities identify gameplay tiers independently of cosmetic meshes.</summary>
public static class Footwear
{
    public static FootwearTier TierFor(uint itemId) => FootwearItems.Tiers.GetValueOrDefault(itemId);
    public static bool AllowsShred(uint itemId, uint optionId) =>
        optionId is 6 or 63 && TierFor(itemId) != FootwearTier.Barefoot;
    public static FootwearTier Equipped(PlayerInventory? inventory) =>
        inventory is not null && inventory.LoadoutSlots.TryGetValue(SurvivorLoadout.Feet, out var shoes)
            ? TierFor(shoes.DefinitionId) : FootwearTier.Barefoot;

    // Official August 29, 2017 Combat Update: sprint bonuses 5%, 12%, 16%; barefoot 0%.
    public static float SpeedMultiplier(FootwearTier tier) => tier switch
    {
        FootwearTier.Stealth => 1.05f,
        FootwearTier.Sturdy => 1.12f,
        FootwearTier.Fast => 1.16f,
        _ => 1f,
    };

    public static string AudioSwitch(FootwearTier tier) => tier switch
    {
        FootwearTier.Stealth => "Silent",
        FootwearTier.Sturdy => "Boot",
        FootwearTier.Fast => "Sneaker",
        _ => "Barefoot",
    };

    public static MovementProfile Apply(MovementProfile baseline, FootwearTier tier,
        bool affectWalking = true) => affectWalking
        ? baseline with { MaxMovementSpeed = baseline.MaxMovementSpeed * SpeedMultiplier(tier) }
        : baseline with { SprintSpeedModifier = baseline.SprintSpeedModifier * SpeedMultiplier(tier) };

    /// <summary>1148 registration 0xdc/02; GUID then two strings, reference Audio.SetSwitch schema.</summary>
    public static byte[] AudioPacket(ulong characterId, FootwearTier tier)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xdc);
        writer.WriteByte(2);
        writer.WriteUInt64(characterId);
        writer.WriteString("ShoeType");
        writer.WriteString(AudioSwitch(tier));
        return writer.Written.ToArray();
    }
}
