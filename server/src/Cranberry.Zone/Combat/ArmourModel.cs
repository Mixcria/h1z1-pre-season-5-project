namespace Cranberry.Zone.Combat;

/// <summary>What a body-armour item is worth. Not a wire value.</summary>
public enum ArmourTier : byte
{
    /// <summary>Nothing worn, or worn but not <c>IS_ARMOR</c> - the cosmetic kevlar vest 2604.</summary>
    None = 0,

    /// <summary>Makeshift (wood, wood+metal, and the crafted PS3 band). Absorbs one body shot.</summary>
    Makeshift = 1,

    /// <summary>Laminated. Absorbs two body shots.</summary>
    Laminated = 2,
}

/// <summary>
/// Body armour and helmets, classified from the <b>August client's own datasheet</b> rather than
/// from a hand-typed id list. The mechanism is the owner's and it is right; the numbers are
/// August's, and they correct one line of his comment.
/// <para>
/// <b>What the August rows actually say</b> (docs/81 §4f, from
/// <c>out\data_aug\ClientItemDefinitions.txt</c>):
/// </para>
/// <code>
///   ITEM_CLASS 25041, IS_ARMOR 1:
///     2204 Survivor&lt;gender&gt;_Armor_Makeshift_Wood.adr       BULK   50
///     2205 Survivor&lt;gender&gt;_Armor_Makeshift_WoodMetal.adr  BULK   50
///     3378, 4022, 4068, 4183  (Generic, no MODEL_NAME)        BULK  325
///     32 InfantryEquipment rows                               BULK 1000
///   ITEM_CLASS 25041, IS_ARMOR 0:
///     2604 Survivor&lt;gender&gt;_Armor_Kevlar_Basic.adr         BULK  250   cosmetic, excluded
///   ITEM_CLASS 25000, IS_ARMOR 1:  68 helmet rows, all BULK 250
/// </code>
/// <para>
/// Two differences from his file, both deliberate. <b>Laminated is <c>BULK 1000</c>, not the 500 his
/// comment quotes</b> - the August build is the post-6/29 client, which confirms his account of the
/// patch history rather than contradicting it. And where he hard-codes <c>if (id == 3378) return
/// Makeshift;</c>, August carries <b>four</b> rows of exactly that shape, so the whole 325 band is
/// classed as makeshift instead of one id.
/// </para>
/// <para>
/// <b>The rearming rule is the difference between armour and an infinite shield</b>: the absorb
/// budget is keyed to the item id in the slot, so a fresh vest refreshes it and re-wearing a broken
/// one does not. <see cref="Armour.Wearing"/> and <see cref="Armour.WearingHelmet"/> enforce it.
/// </para>
/// </summary>
public static class ArmourModel
{
    /// <summary>
    /// <c>BULK</c> at or below this is makeshift; above it, laminated. The owner cuts at 100; 400
    /// is the same ruling generalised to include August's 325 band, and it is still far clear of
    /// both clusters (50 and 1000).
    /// </summary>
    public const uint MakeshiftBulkCeiling = 400;

    /// <summary>Body shots a tier absorbs before it is destroyed.</summary>
    public static int AbsorbsOf(ArmourTier tier) => tier switch
    {
        ArmourTier.Makeshift => 1,
        ArmourTier.Laminated => 2,
        _ => 0,
    };

    /// <summary>The armour tier of an item, from the client's own datasheet.</summary>
    public static ArmourTier TierOf(uint itemDefinitionId)
    {
        if (itemDefinitionId == 0
            || !AugustArmourFacts.TryGet(itemDefinitionId, out AugustArmourFact fact)
            || fact.ItemClass != AugustArmourFacts.BodyArmourClass)
        {
            return ArmourTier.None;
        }

        return fact.Bulk <= MakeshiftBulkCeiling ? ArmourTier.Makeshift : ArmourTier.Laminated;
    }

    /// <summary>
    /// True when this item is a HELMET - <c>ITEM_CLASS 25000</c> with the client's own
    /// <c>IS_ARMOR</c> flag set. The flag matters: the head slot also holds hats and masks, and a
    /// beanie must not stop a rifle round.
    /// </summary>
    public static bool IsHelmet(uint itemDefinitionId) =>
        itemDefinitionId != 0
        && AugustArmourFacts.TryGet(itemDefinitionId, out AugustArmourFact fact)
        && fact.ItemClass == AugustArmourFacts.HeadClass;
}

/// <summary>
/// One combatant's armour state, as a plain mutable struct so it can live inline on a player object
/// with no allocation and no hashing on the hot path. The owner hangs the same four fields off a
/// <c>ConditionalWeakTable</c>; Cranberry's players are slot-indexed objects and do not need one.
/// </summary>
public struct Armour
{
    /// <summary>The item currently in the armour slot; 0 for none.</summary>
    public uint BodyItemId;

    /// <summary>Absorbs left on <see cref="BodyItemId"/>. 0 means broken.</summary>
    public int AbsorbsLeft;

    /// <summary>The item currently in the head slot; 0 for none.</summary>
    public uint HeadItemId;

    /// <summary>False once a helmet has eaten a headshot.</summary>
    public bool HelmetIntact;

    /// <summary>
    /// Counts qualifying hits so that armour halves bleeding by <b>parity</b> rather than by rolling
    /// a die - the owner's own choice, and the reason his self-tests are deterministic.
    /// </summary>
    public byte BleedParity;

    /// <summary>
    /// Declares what is worn in the armour slot and returns the live tier. Changing the item resets
    /// the absorb budget; naming the same item again leaves a spent vest spent.
    /// </summary>
    public ArmourTier Wearing(uint itemDefinitionId)
    {
        if (BodyItemId != itemDefinitionId)
        {
            BodyItemId = itemDefinitionId;
            AbsorbsLeft = ArmourModel.AbsorbsOf(ArmourModel.TierOf(itemDefinitionId));
        }

        return AbsorbsLeft > 0 ? ArmourModel.TierOf(itemDefinitionId) : ArmourTier.None;
    }

    /// <summary>
    /// Declares what is worn in the head slot and returns whether it is an unbroken helmet. Same
    /// rearming rule: a new helmet is intact, the same broken one stays broken.
    /// </summary>
    public bool WearingHelmet(uint itemDefinitionId)
    {
        if (HeadItemId != itemDefinitionId)
        {
            HeadItemId = itemDefinitionId;
            HelmetIntact = true;
        }

        return ArmourModel.IsHelmet(itemDefinitionId) && HelmetIntact;
    }

    /// <summary>Absorbs left on whatever is worn now, without changing anything.</summary>
    public readonly int BodyAbsorbsLeft => AbsorbsLeft;

    /// <summary>Clears everything - a respawn, or a match reset.</summary>
    public void Reset()
    {
        BodyItemId = 0;
        AbsorbsLeft = 0;
        HeadItemId = 0;
        HelmetIntact = false;
        BleedParity = 0;
    }
}
