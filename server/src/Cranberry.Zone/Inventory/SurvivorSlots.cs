namespace Cranberry.Zone.Inventory;

/// <summary>
/// Named ids for the body (equipment) slots the KOTK survivor uses. Every value is the
/// <c>ID</c> column of <c>EquipmentSlotDefinitions.txt</c> and is the number that travels as
/// <c>CharacterEquipmentAttachment.SlotId</c> / <c>EquipmentSlotRow.SlotId</c>
/// (docs/41 §1a, docs/36 §W3). The full 86-row table is
/// <see cref="EquipmentSlotTable"/>; this class exists only so call sites read as English.
/// </summary>
public static class BodySlots
{
    /// <summary>
    /// Hats and protective helmets. <b>The dangerous slot.</b> It is the only
    /// <c>IS_REQUIRED = 1</c> row in the whole sheet with an empty <c>DEFAULT_ADR</c>: clearing 3
    /// (Chest), 4 (Legs) or 105 (eyes) falls back to <c>Survivor&lt;gender&gt;_Chest_Bra.adr</c> /
    /// <c>..._Legs_Pants_Underwear.adr</c> / <c>..._Eyes_01.adr</c>, but clearing 1 leaves a
    /// required slot with no mesh and no default. That is exactly the crash-vs-hang split docs/32
    /// measured, and it closes docs/32's open question G10 (docs/41 §1d).
    /// <para>
    /// <b>Standing rule:</b> never send <c>UnsetCharacterEquipmentSlot</c> for this slot - or for
    /// any slot; the burst is 0x in every capture the client accepted (docs/32).
    /// </para>
    /// </summary>
    public const uint Head = 1;

    /// <summary>Gloves (item class 25008).</summary>
    public const uint Hands = 2;

    /// <summary>Shirts, jackets, hoodies (25002). Required; default <c>..._Chest_Bra.adr</c>.</summary>
    public const uint Chest = 3;

    /// <summary>Pants, shorts, leggings (25003). Required; default <c>..._Underwear.adr</c>.</summary>
    public const uint Legs = 4;

    /// <summary>Footwear (25005).</summary>
    public const uint Feet = 5;

    /// <summary>
    /// The wielded slot. <c>ANIM_NAME = WeaponSwitch</c>, and the <b>only</b> value any item's
    /// <c>ACTIVE_EQUIP_SLOT_ID</c> ever takes across all 2,643 rows (262 of them wieldable).
    /// </summary>
    public const uint RightHand = 7;

    /// <summary>Backpacks and satchels (25004) - the slot that carries most of the bulk.</summary>
    public const uint Backpack = 10;

    /// <summary>Fanny pack / belt pouch (25013).</summary>
    public const uint Belt = 11;

    /// <summary>Masks, bandanas, respirators (25040).</summary>
    public const uint Face = 28;

    /// <summary>Goggles and glasses (25045).</summary>
    public const uint Eyes = 29;

    /// <summary>Body armour (25041).</summary>
    public const uint ChestArmor = 100;

    /// <summary>Jacket layer (25046).</summary>
    public const uint Jacket = 101;

    /// <summary>Hair (25039) - <c>IS_EQUIPMENT = 0</c>, not an attachment target.</summary>
    public const uint Hair = 27;

    /// <summary>The eyeball mesh. Required; default <c>Survivor&lt;gender&gt;_Eyes_01.adr</c>.</summary>
    public const uint Eyeballs = 105;

    /// <summary>
    /// True when clearing this slot would leave the client with a required slot that has no mesh
    /// and no default - the G10 condition (docs/41 §1d). Only slot 1 satisfies it in this build,
    /// but the predicate is computed from the sheet so it stays correct if the table ever changes.
    /// </summary>
    public static bool ClearingCrashesClient(uint slotId) =>
        EquipmentSlotTable.TryGet(slotId, out EquipmentSlotDefinition slot)
        && slot.IsRequired
        && slot.DefaultAdr.Length == 0;
}

/// <summary>
/// Named ids for the slots of loadout 17, the KOTK survivor loadout (<c>LoadoutSlots.txt</c>, docs/41
/// §2a). This is a <em>different id space</em> from <see cref="BodySlots"/>: a loadout slot is what
/// the inventory panel draws and what <c>Loadouts.SetLoadoutSlot</c> binds an item guid to, while a
/// body slot is where the mesh attaches.
/// </summary>
public static class SurvivorLoadout
{
    /// <summary>
    /// The August pack ships survivor-like loadouts 3 and 17. Cranberry uses the KOTK loadout 17:
    /// fists on Slot4, binoculars on Slot5, and Q/E consumables in slots 40/41.
    /// Loadout 3 is retained as <see cref="LegacyId"/> for old capture diagnostics only.
    /// </summary>
    public const uint FriendLoadoutId = 17;

    /// <summary>
    /// Loadout 17: keys 1-3 in slots 1/2/4, fists on key 4 (slot 7), binoculars on key 5
    /// (slot 5), and Q/E consumables in slots 40/41.
    /// </summary>
    public const uint Id = FriendLoadoutId;

    /// <summary>The earlier Cranberry choice, retained only for diagnostics and old captures.</summary>
    public const uint LegacyId = LoadoutSlotTable.SurvivorLoadoutId;

    // --- weapon and utility hotbar slots -------------------------------------------------------

    /// <summary>Weapon hotbar <c>Slot1</c>.</summary>
    public const uint Wheel1 = 1;

    /// <summary>Weapon hotbar <c>Slot2</c>.</summary>
    public const uint Wheel2 = 2;

    /// <summary>Weapon hotbar <c>Slot3</c>.</summary>
    public const uint Wheel3 = 4;

    /// <summary>Key 5, the auto-equipped binoculars/utility slot (mapped class 25081).</summary>
    public const uint Binoculars = 5;

    /// <summary>
    /// Hotbar <c>Slot4</c> - <c>FLAG_REQUIRED = 1</c>, <c>FLAG_AUTO_EQUIP = 1</c>,
    /// <c>ITEM_ID = 85</c>. This is why "Fists" exists: the survivor loadout requires a filled hand
    /// slot and the sheet names item 85 as its default. The client's own config agrees -
    /// <c>Inventory.SpecialEmptyHandsItemId = 85</c> in <c>StringHashValues.g.cs</c>. Auto-assign
    /// must never take this slot.
    /// </summary>
    public const uint Fists = 7;

    /// <summary>Q - loadout 17 <c>ConsumeItem1</c>, slot 40 (class 25080, the Field Bandage). D341.</summary>
    public const uint QuickUseOne = 40;

    /// <summary>E - loadout 17 <c>ConsumeItem2</c>, slot 41 (class 25009, the First Aid Kit). D341.</summary>
    public const uint QuickUseTwo = 41;

    /// <summary>Quick-use Q / <c>ConsumeItem1</c> (mapped class 25080).</summary>
    public const uint QuickUse1 = 40;

    /// <summary>Quick-use E / <c>ConsumeItem2</c> (mapped class 25009).</summary>
    public const uint QuickUse2 = 41;

    // Compatibility names for callers written while loadout 3 called these utility slots.
    public const uint Utility1 = QuickUse1;
    public const uint Utility2 = QuickUse2;

    // --- the auto-equip apparel slots (FLAG_AUTO_EQUIP = 1) -----------------------------------

    /// <summary>Chest -> body slot 3 (25002).</summary>
    public const uint Chest = 10;

    /// <summary>Head -> body slot 1 (25000).</summary>
    public const uint Head = 11;

    /// <summary>Backpack -> body slot 10 (25004).</summary>
    public const uint Backpack = 12;

    /// <summary>Feet -> body slot 5 (25005).</summary>
    public const uint Feet = 13;

    /// <summary>Legs -> body slot 4 (25003).</summary>
    public const uint Legs = 14;

    /// <summary>Gloves -> body slot 2 (25008).</summary>
    public const uint Gloves = 16;

    /// <summary>Belt -> body slot 11 (25013).</summary>
    public const uint Belt = 25;

    /// <summary>Face -> body slot 28 (25040).</summary>
    public const uint Face = 28;

    /// <summary>Eyes -> body slot 29 (25045).</summary>
    public const uint EyeWear = 29;

    /// <summary>Jacket -> body slot 101 (25046).</summary>
    public const uint Jacket = 30;

    /// <summary>Chest armour -> body slot 100 (25041).</summary>
    public const uint ChestArmor = 38;

    /// <summary>
    /// The hidden bag. <c>FLAG_AUTO_EQUIP = 1</c>, <c>FLAG_IS_VISIBLE = 0</c>, accepts class 25068,
    /// which is item <b>3156 "Inventory"</b> - a container item whose <c>PARAM1 = 117</c> is the
    /// only <c>IS_DYNAMIC_BULK</c> row in <c>ContainerDefinitions.txt</c>. The character's carry
    /// capacity is this container's, subsumed from whatever containers are worn (docs/41 §4b).
    /// </summary>
    public const uint Inventory = 43;

    /// <summary>Vehicle key, loadout-17's auto-equipped slot for mapped class 25095.</summary>
    public const uint VehicleKey = 48;

    /// <summary>
    /// The three weapon slots auto-assign considers, in hotbar order.
    /// <see cref="Fists"/> is excluded - it is the required Fists slot.
    /// </summary>
    public static ReadOnlySpan<uint> WeaponWheelOrder => [Wheel1, Wheel2, Wheel3];

    /// <summary>
    /// Every assignable hotbar destination in the order the server considers it: the three weapon
    /// slots, binoculars on key 5, then Q and E. Required fists are deliberately excluded.
    /// </summary>
    public static ReadOnlySpan<uint> WheelOrder =>
        [Wheel1, Wheel2, Wheel3, Binoculars, QuickUse1, QuickUse2];
}
