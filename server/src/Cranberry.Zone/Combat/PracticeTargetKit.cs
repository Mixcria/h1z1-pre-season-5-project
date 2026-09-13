using Cranberry.Protocol;
using Cranberry.Zone.World;

namespace Cranberry.Zone.Combat;

public static class PracticeTargetKit
{
    public const uint Helmet = 2168, BodyArmour = 2271, Backpack = 2121, Rifle = 2425;

    /// <summary>The same charged AR-15 group/mode used by real peer weapons.</summary>
    public static RemoteWeaponBlob Weapon { get; } = BuildWeapon();

    private static RemoteWeaponBlob BuildWeapon()
    {
        if (!Weapons.WeaponItemProfiles.TryGet(Rifle, out var weapon)
            || !Weapons.AugustWeaponTable.HasFireGroup(Rifle, out uint group))
            return new(RetailBalance.WeaponDefinitionIdFor(Rifle), RemoteWeaponBlob.RightHandSlot, []);
        return new(weapon.WeaponId, RemoteWeaponBlob.RightHandSlot,
            [new RemoteFireGroup(group, [new RemoteFireMode((uint)Math.Max(0, weapon.ClipSize)),
                new RemoteFireMode(Weapons.AugustWeaponTable.TriggerChargeSentinel)])]);
    }

    public static void Equip(PracticeTarget target)
    {
        target.FullKit = true;
        target.BodyArmourItemId = BodyArmour;
        target.HelmetItemId = Helmet;
        target.Armour.Wearing(BodyArmour);
        target.Armour.WearingHelmet(Helmet);
    }

    public static void WriteSpawn(PacketWriter w, PracticeTarget target) => PeerSpawnWriter.WriteAddLightweightPc(w,
        new PeerCharacterRecord { Guid = target.WorldGuid, TransientId = target.TransientId,
            Identity = new SelfIdentity { Name = target.Name }, ModelId = PracticeTargetPack.DefaultModelId,
            Position = target.Position, Rotation = target.Rotation });

    public static IReadOnlyList<CharacterEquipmentAttachment> Dress(PracticeTarget target)
    {
        var visuals = CharacterVisuals.FromSelection(1, 1, 1, 664, 270);
        var result = visuals.StarterOutfit.Where(x => x.SlotId != 7).ToList();
        // d5 carries the body only. These non-inventory customization slots are valid
        // 94/01 attachment targets (140c71010 -> 142299130); helmet slot 1 is separate.
        result.Add(new(visuals.HeadModel, 15, ShaderParameterGroupId: visuals.SkinToneId));
        result.Add(new(visuals.HairModel, 27));
        result.Add(new("SurvivorMale_Back_Backpack_Military.adr", 10));
        result.Add(new("Weapon_M16A4_3p.adr", 7));
        if (target.Armour.HelmetIntact) result.Add(new("SurvivorMale_Head_Helmet_Motorcycle_Tintable.adr", 1,
            ShaderParameterGroupId: DynamicAppearanceReference.CranberryGearGroup));
        if (target.Armour.BodyAbsorbsLeft > 0) result.Add(new("SurvivorMale_Armor_Kevlar_Basic_Velcro.adr", 100));
        return result;
    }

    public static byte[] FullCharacter(PracticeTarget target) => FullCharacterPackets.Promote(
        new PeerCharacterRecord { Guid = target.WorldGuid, TransientId = target.TransientId,
            Position = target.Position, Rotation = target.Rotation }, Dress(target));

    public static void WriteDress(PacketWriter w, PracticeTarget target) =>
        new SetCharacterEquipment(target.WorldGuid, Attachments: Dress(target)).WriteTo(w);
}
