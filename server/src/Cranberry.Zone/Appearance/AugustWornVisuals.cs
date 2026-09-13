using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Appearance;

/// <summary>
/// One item the player has picked up and put into a body slot, reduced to the three facts that
/// decide what mesh hangs on the character.
/// </summary>
/// <param name="BodySlotId">
/// <c>EquipmentSlotDefinitions.txt</c> row - 1 Head, 10 Backpack, 100 ChestArmor, 76/77/80 the
/// long-gun stow pegs. This is the key of <c>PlayerInventory.EquipmentSlots</c> and it is already
/// correct on the wire; only the attachment behind it was missing (docs/54 s3.1, docs/74 W1).
/// </param>
/// <param name="ItemDefinitionId"><c>ClientItemDefinitions</c> <c>*ID</c>.</param>
/// <param name="SheetModelName">
/// <c>ClientItemDefinitions.MODEL_NAME</c> for the item, which may contain the literal
/// <c>&lt;gender&gt;</c> token, and which is empty for every wearable in the loot roster - that
/// emptiness is the whole of symptom B.
/// </param>
public readonly record struct AugustWornItem(
    uint BodySlotId,
    uint ItemDefinitionId,
    string SheetModelName,
    uint? SkinOverrideDefinitionId = null);

/// <summary>
/// Attaches picked-up gear to the character.
/// <para>
/// <b>The defect this repairs (docs/54 s3, docs/74 W1, LIVE-VERIFIED).</b> Every
/// <c>SetCharacterEquipmentWithSlots</c> in the 2026-08-30 play-test carried a constant "5 meshes"
/// - the starter outfit - through five pickups that added equipment-slot rows for body slots 1,
/// 10, 76, 77 and 100. docs/74 probe W1 measured the same thing again on the wire after six
/// pickups: body slots <b>76, 77 and 80</b> carry a <c>94 01</c> equipment-slot row and <b>no
/// attachment element at all</b>. That is exactly <i>"some go into the slots but that's it. They
/// do not show on me."</i>
/// </para>
/// <para>
/// <b>Wave 8 (D53, docs/80).</b> The wearables half was repaired in wave 5 and is verified on the
/// wire. The stowed half was deferred - <see cref="AttachableBodySlots"/> was the literal
/// <c>{1, 10, 100}</c> - and this is the step that lifts it. The owner's own Z1 server dresses a
/// stowed weapon by hanging the item's third-person mesh on the passive slot it resolved, and its
/// reference-session census counted <c>SetCharacterEquipmentSlot 76 x26, 77 x16, 80 x8</c>. Every
/// <c>_3P.adr</c> the roster needs is already in <see cref="AugustWornMeshCatalog"/> and already
/// ships (<see cref="AugustModelCatalog"/>), so the allow-list was the only thing in the way.
/// </para>
/// <para>
/// <b>The two resolvers are complementary.</b> <see cref="AugustWornMeshCatalog"/> covers 54 of the
/// 72 roster items - every wearable - out of the dynamic-appearance table's own <c>ModelId</c>
/// column; the sheet covers the machete, the throwables and the medical items. Neither covers
/// everything and together they cover all of it, so this type tries the catalogue first and falls
/// back to the sheet - canonicalised through <see cref="AugustAssetIndex"/>, because the sheet
/// spells the machete <c>Weapons_Machete01_3p.adr</c> and the packs spell it <c>..._3P.adr</c>.
/// </para>
/// <para>
/// <b>Guard 5 (docs/45).</b> Body slot 7 is the active hand and an equipment-slot row naming it
/// hard-crashes the client. Nothing here can emit one - this type writes <i>attachments</i>, never
/// rows - but <see cref="AttachableBodySlots"/> excludes 7 anyway and <c>Dress</c> refuses it a
/// second time, so a future edit to the allow-list cannot reach the active hand by accident.
/// </para>
/// </summary>
public static class AugustWornVisuals
{
    /// <summary><c>EquipmentSlotDefinitions.txt</c> row 7, <c>RHand</c> - the active hand.</summary>
    public const uint ActiveHandSlotId = 7;

    /// <summary>
    /// The pre-wave-8 allow-list: the three slots a pickup could only ever <em>add</em> to the
    /// starter outfit. Kept as the one-variable rollback behind
    /// <c>ZoneOptions.Skins.DressStowedWeapons = false</c> (<c>CRANBERRY_STOWED_MESHES=0</c>), so
    /// that if an attachment on a stow peg upsets the client the owner can put the wire back to the
    /// shape docs/74 measured without a rebuild.
    /// </summary>
    public static IReadOnlySet<uint> AdditiveBodySlots { get; } =
        new HashSet<uint> { 1, 10, 100 };

    /// <summary>
    /// Every body slot a picked-up item may hang a mesh on: each <c>IS_EQUIPMENT</c> row of the
    /// August <c>EquipmentSlotDefinitions</c> table except the active hand and the vehicle
    /// hardpoints.
    /// <para>
    /// Derived from the client's own sheet rather than written out as a literal, because the
    /// literal is what went wrong: the deferral in docs/54 s3.3 was recorded as a list of four slot
    /// ids and then read for three waves as if it were a rule. <c>GROUP_ID = 1</c> marks a
    /// <em>vehicle</em> hardpoint (30-33, 41-73) and is excluded; <c>IS_EQUIPMENT = 0</c> excludes
    /// slots 15, 27 and 85, the last of which <c>EquipSlotItemClasses.txt</c> still lists as a stow
    /// candidate (<c>InventorySlots.g.cs</c> already refuses it for the same reason).
    /// </para>
    /// <para>
    /// The set is wider than the slots a pickup can actually reach - it contains the weapon rail
    /// and optic rows (16, 17, 34-40, 61, 62) too - and that is deliberate. It is filtered against
    /// <c>PlayerInventory.EquipmentSlots</c>, whose keys come from the item sheet's
    /// <c>PASSIVE_EQUIP_SLOT_ID</c> and from <c>EquipmentSlotTable.StowSlotsForItemClass</c>, so a
    /// rail id can never be a key. Naming the sheet's own rule is safer than curating a second
    /// list that has to be maintained against it.
    /// </para>
    /// <para>
    /// Never 7. See <see cref="ActiveHandSlotId"/>.
    /// </para>
    /// </summary>
    public static IReadOnlySet<uint> AttachableBodySlots { get; } =
        new HashSet<uint>(EquipmentSlotTable.All
            .Where(slot => slot.IsEquipment && slot.GroupId == 0 && slot.Id != ActiveHandSlotId)
            .Select(slot => slot.Id));

    /// <summary>
    /// The third-person mesh for one item on a body of <paramref name="gender"/>.
    /// <para>
    /// The dynamic-appearance catalogue wins, because it is the only resolver that has an answer
    /// for a wearable; <paramref name="sheetModelName"/> is the fallback for the items it has no
    /// row for. The sheet writes the gender as the literal <c>&lt;gender&gt;</c> token
    /// (<c>Survivor&lt;gender&gt;_Back_Backpack_Military.adr</c>), which the client substitutes -
    /// so does this - and it spells the third-person suffix <c>_3p</c> where the asset packs spell
    /// it <c>_3P</c>, so the substituted name is put through
    /// <see cref="AugustAssetIndex.Canonical"/> before it goes out. The catalogue path needs no
    /// canonicalisation: its names are generated <em>from</em> the pack index.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> when neither resolver has a mesh. An empty model name must stay a
    /// skipped attachment, never an empty string on the wire.</returns>
    public static bool TryResolveMesh(
        uint itemDefinitionId,
        uint gender,
        string? sheetModelName,
        out string modelName,
        out uint shaderParameterGroupId)
    {
        // Gallery item 3750 is named AR-15 by the client but carries no model/datasheet row.
        // Display choice: reuse the unskinned base AR-15 mesh; its separate weapon identity
        // and the player's saved ordinary AR-15 skin remain unchanged.
        if (itemDefinitionId == Weapons.CrateOpeningWeapon.ItemId)
            itemDefinitionId = Weapons.CrateOpeningWeapon.VisualItemId;
        if (AugustWornMeshCatalog.TryGet(itemDefinitionId, out AugustWornMesh mesh))
        {
            modelName = mesh.ModelNameFor(gender);
            shaderParameterGroupId = mesh.ShaderParameterGroupId;
            return modelName.Length != 0;
        }

        shaderParameterGroupId = 0;
        if (string.IsNullOrEmpty(sheetModelName))
        {
            // Starter Henley 3533 and crafted makeshift armor 3378 have no datasheet
            // mesh. The August wardrobe still names their models for both bodies.
            // ModelForReward only accepts a model present in the client asset index.
            modelName = itemDefinitionId == Inventory.SurvivorStarterOutfit.Shirt
                || Combat.ArmourModel.TierOf(itemDefinitionId) != Combat.ArmourTier.None
                    ? AugustWornSkins.ModelForReward(itemDefinitionId, gender) ?? string.Empty
                    : string.Empty;
            return modelName.Length != 0;
        }

        modelName = AugustAssetIndex.Canonical(sheetModelName.Replace(
            "<gender>",
            gender == CharacterVisuals.Female ? "Female" : "Male",
            StringComparison.Ordinal));
        return modelName.Length != 0;
    }

    /// <summary>
    /// Adds one <see cref="CharacterEquipmentAttachment"/> per attachable worn item to
    /// <paramref name="attachments"/>, replacing whatever already occupies that body slot.
    /// </summary>
    /// <param name="attachments">The dress being assembled - the wardrobe's own list, in place.</param>
    /// <param name="worn">The player's occupied body slots.</param>
    /// <param name="gender"><see cref="CharacterVisuals.Male"/> or <see cref="CharacterVisuals.Female"/>.</param>
    /// <param name="appearanceRowsFor">
    /// <c>AugustDynamicAppearanceTable.AppearanceRowsFor</c>, or <c>null</c> when the table failed
    /// to load and the starter palette is in use. Passing the rows is what the working spawned-in
    /// wardrobe path does (<c>AugustWardrobeVisuals.BuildAttachments</c>) and a pickup matches it
    /// rather than inventing a third convention.
    /// </param>
    /// <param name="attachableBodySlots">
    /// <c>null</c> means <see cref="AttachableBodySlots"/>. The zone service passes
    /// <see cref="AdditiveBodySlots"/> when <c>CRANBERRY_STOWED_MESHES=0</c>.
    /// </param>
    /// <param name="definesShaderGroup">
    /// <c>AugustDynamicAppearanceTable.DefinesShaderGroup</c>, or <c>null</c> to send
    /// <c>ShaderParameterGroupId 0</c> for everything, which is what every build before wave 8 did.
    /// <para>
    /// This is a <em>predicate</em> and not a bool on purpose (docs/80 edit 3). docs/54 s4 forbids
    /// handing the client a group id the transmitted appearance table holds no parameter rows for;
    /// the nine roster weapons are inside the filter's <c>wantedItems</c> and 26 of the 54 worn
    /// wearables are not, so the honest gate is per group, not per server. It also replaces the
    /// process-wide mutable <c>SendShaderParameterGroup</c> static, which
    /// <c>VerifyWave5FixesTests</c> had already had to build a whole xUnit collection around.
    /// </para>
    /// </param>
    /// <param name="skinRewardFor">
    /// <b>D283, docs/106 §13.</b> The wardrobe selection that colours a pickup: given the looted
    /// item's definition id and the mesh just resolved for it, the <b>reward item id</b> whose
    /// appearance rows and shader group go out instead of the base item's, or <c>0</c> for "no
    /// selection, or one this body/mesh refuses". <c>null</c> is the pre-D283 wire, where a stow
    /// peg and a worn backpack always carried the base item's own colourway however many skins the
    /// owner had selected (<c>CRANBERRY_WORN_SKINS_IN_WORLD=0</c>).
    /// <para>
    /// It takes the resolved mesh rather than resolving one itself so that the re-model guard
    /// compares against the name that is actually going on the wire.
    /// </para>
    /// </param>
    /// <param name="shaderGroupFor">
    /// The shader group for the row item (base or skin reward), resolved the way the client's own
    /// selector resolves it (<c>AugustDynamicAppearanceTable.ShaderGroupFor</c>). <c>0</c> means
    /// "no answer" and the mesh catalogue's own group is used, exactly as before. Whatever this
    /// returns still goes through <paramref name="definesShaderGroup"/>.
    /// </param>
    /// <returns>How many attachments were added.</returns>
    public static int Dress(
        List<CharacterEquipmentAttachment> attachments,
        IEnumerable<AugustWornItem> worn,
        uint gender,
        Func<uint, IReadOnlyList<uint>>? appearanceRowsFor,
        IReadOnlySet<uint>? attachableBodySlots = null,
        Func<uint, bool>? definesShaderGroup = null,
        Func<uint, string, uint>? skinRewardFor = null,
        Func<uint, uint>? shaderGroupFor = null)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(worn);

        IReadOnlySet<uint> allowed = attachableBodySlots ?? AttachableBodySlots;
        int dressed = 0;
        foreach (AugustWornItem item in worn)
        {
            // Belt and braces over the allow-list: the active hand is never dressed from a
            // pickup, whatever a later edit does to the allow-list (docs/45, guard 5).
            if (item.BodySlotId == ActiveHandSlotId || !allowed.Contains(item.BodySlotId))
            {
                continue;
            }

            if (!TryResolveMesh(
                    item.SkinOverrideDefinitionId ?? item.ItemDefinitionId,
                    gender,
                    item.SheetModelName,
                    out string modelName,
                    out uint shaderParameterGroupId))
            {
                // Looted wardrobe garments and worn hoodies can lack a loot-roster mesh.
                // This fallback is only for worn rendering; weapon wield eligibility and the
                // existing starter/menu outfit composition still use the original resolver.
                uint visualItem = item.SkinOverrideDefinitionId ?? item.ItemDefinitionId;
                if (item.SkinOverrideDefinitionId is not null
                    || (ItemUseOptionTable.Allows(visualItem, 96) && ItemUseOptionTable.Allows(visualItem, 97)))
                    modelName = AugustWornSkins.ModelForReward(visualItem, gender) ?? string.Empty;
                if (modelName.Length == 0)
                    continue;
            }

            // D283: the row item is the SKIN's reward when one applies to this looted item on this
            // body, and the looted item itself otherwise. Category-compatible helmet, backpack
            // and armour skins also replace the base mesh with their authored mesh.
            uint rowItem = item.ItemDefinitionId;
            if (item.SkinOverrideDefinitionId is uint explicitSkin)
            {
                rowItem = explicitSkin;
                modelName = AugustWornSkins.ModelForReward(explicitSkin, gender) ?? modelName;
            }
            else if (skinRewardFor is not null)
            {
                uint reward = skinRewardFor(item.ItemDefinitionId, modelName);
                if (reward != 0)
                {
                    rowItem = reward;
                    // Appearance rows and shader data must be paired with the selected skin's mesh.
                    modelName = AugustWornSkins.ModelForReward(reward, gender) ?? modelName;
                }
            }

            IReadOnlyList<uint> appearanceIds = appearanceRowsFor?.Invoke(rowItem) ?? [];

            // The client applies the winning appearance row's own group and reads this field only
            // when no row resolves (FUN_140c70d60 tail), so the group must follow the row item.
            uint group = shaderGroupFor?.Invoke(rowItem) ?? 0;
            if (group == 0)
            {
                group = shaderParameterGroupId;
            }

            var attachment = new CharacterEquipmentAttachment(
                modelName,
                item.BodySlotId,
                // docs/54 s1.1: every .adr in this roster declares exactly one texture alias,
                // "default0", and an empty <TintAliases/>. Both fields are inert here and any
                // other value is a guess.
                TextureAlias: "Default",
                TintAlias: "Default",
                // D338: a material-effect skin (Infernal, Showdown, Frostbite...) is carried by
                // this id, not by a shader group; 0 for a plain tint.
                EffectId: AugustMaterialEffects.EffectIdFor(rowItem),
                ShaderParameterGroupId:
                    definesShaderGroup is not null && definesShaderGroup(group)
                        ? group
                        : 0,
                AppearanceIds: appearanceIds.Count == 0 ? null : appearanceIds);

            // One attachment per slot: the client's attachment list is keyed by slot and a
            // duplicate is a race. FindIndex/assign rather than RemoveAll(lambda): the lambda
            // captured item.BodySlotId and allocated a closure per worn item, and the removal was
            // a second full scan (docs/80 edit 14 - tidiness, not a measured win; this is not a
            // per-tick path).
            uint slotId = item.BodySlotId;
            int existing = attachments.FindIndex(row => row.SlotId == slotId);
            if (existing >= 0)
            {
                attachments[existing] = attachment;
            }
            else
            {
                attachments.Add(attachment);
            }

            dressed++;
        }

        if (dressed > 0)
        {
            // BY SLOT ID, like both siblings (AugustWardrobe.BuildAttachments,
            // AugustWorldEquipment.BuildAttachments, which each return
            // `attachments.OrderBy(a => a.SlotId)`). Appending left the list as 2,3,4,5,7,1,10,100:
            // an ordering no capture of this client has ever carried, on the one packet the owner
            // reports not working ("some go into the slots but that's it. They don't show on me").
            //
            // Slot ids are unique here - the replace pass above guarantees one attachment per slot
            // - so the instability of List.Sort cannot reorder equals.
            attachments.Sort(static (left, right) => left.SlotId.CompareTo(right.SlotId));
        }

        return dressed;
    }
}
