namespace Cranberry.Zone;

/// <summary>
/// The visual identity carried from character creation into the zone. Every filename and slot in
/// this type comes from the August client's own HeadTypes, Models and EquipmentSlotDefinitions
/// tables; it does not depend on an older server's packet implementation.
/// </summary>
public sealed record CharacterVisuals(
    uint Gender,
    uint HeadId,
    uint HairId,
    uint SkinToneId,
    uint ProfileId,
    string HeadModel,
    string HairModel,
    IReadOnlyList<CharacterEquipmentAttachment> StarterOutfit)
{
    public const uint Male = 1;
    public const uint Female = 2;

    /// <summary>
    /// Resolves a roster selection to meshes that are present in the August asset packs. A known
    /// head is authoritative for gender so a female head can never be attached to a male body.
    /// Unknown values fall back to Head_01 for the selected gender.
    /// </summary>
    public static CharacterVisuals FromSelection(
        uint gender,
        uint headId,
        uint hairId,
        uint skinToneId,
        uint profileId)
    {
        (uint resolvedHeadId, uint resolvedGender, string headModel) = ResolveHead(gender, headId);
        string genderName = resolvedGender == Female ? "Female" : "Male";

        // HairMappings.txt's August rows are ShaderParameterOverride items and carry no filename.
        // The base hair mesh is therefore selected by body gender; the row id is retained so the
        // later shader-table work can apply the user's chosen colour without changing geometry.
        string hairModel = resolvedGender == Female
            ? "SurvivorFemale_Hair_ShortMessy.adr"
            : "SurvivorMale_Hair_MediumMessy.adr";

        CharacterEquipmentAttachment[] outfit =
        [
            StarterMesh(
                $"Survivor{genderName}_Hands_Gloves_Fingerless.adr",
                slotId: 2,
                DynamicAppearanceReference.CranberryGearGroup),
            StarterMesh(
                $"Survivor{genderName}_Chest_Shirt_Henley.adr",
                slotId: 3,
                DynamicAppearanceReference.CranberryShirtGroup),
            StarterMesh(
                $"Survivor{genderName}_Legs_Pants_StraightLeg.adr",
                slotId: 4,
                DynamicAppearanceReference.CranberryPantsGroup),
            StarterMesh(
                $"Survivor{genderName}_Feet_Jeds.adr",
                slotId: 5,
                DynamicAppearanceReference.CranberryGearGroup),
            // The client's empty-hand item keeps the menu actor in its relaxed idle locomotion
            // set instead of leaving slot 7 in the raised unarmed-combat pose.
            StarterMesh("Weapon_Empty.adr", slotId: 7, shaderParameterGroupId: 0),
        ];

        return new CharacterVisuals(
            resolvedGender,
            resolvedHeadId,
            hairId,
            ResolveSkinTone(skinToneId),
            profileId,
            headModel,
            hairModel,
            outfit);
    }

    private static CharacterEquipmentAttachment StarterMesh(
        string modelName,
        uint slotId,
        uint shaderParameterGroupId) =>
        new(
            modelName,
            slotId,
            ShaderParameterGroupId: shaderParameterGroupId);

    private static (uint HeadId, uint Gender, string Model) ResolveHead(uint gender, uint headId) =>
        headId switch
        {
            1 => (1, Male, "SurvivorMale_Head_01.adr"),
            2 => (2, Male, "SurvivorMale_Head_02.adr"),
            3 => (3, Female, "SurvivorFemale_Head_01.adr"),
            4 => (4, Female, "SurvivorFemale_Head_02.adr"),
            5 => (5, Male, "SurvivorMale_Head_03.adr"),
            6 => (6, Female, "SurvivorFemale_Head_03.adr"),
            7 => (7, Male, "SurvivorMale_Head_04.adr"),
            8 => (8, Female, "SurvivorFemale_Head_04.adr"),
            _ when gender == Female => (3, Female, "SurvivorFemale_Head_01.adr"),
            _ => (1, Male, "SurvivorMale_Head_01.adr"),
        };

    /// <summary>
    /// Character creation normally stores the shader group (662/664/665/666). Accepting the
    /// SkinTones.txt row id as well keeps hand-authored/test admissions deterministic.
    /// </summary>
    private static uint ResolveSkinTone(uint value) => value switch
    {
        1 => 665,
        2 => 666,
        3 => 664,
        4 => 666,
        5 => 662,
        6 => 662,
        7 => 664,
        8 => 665,
        662 or 664 or 665 or 666 => value,
        _ => 0,
    };
}
