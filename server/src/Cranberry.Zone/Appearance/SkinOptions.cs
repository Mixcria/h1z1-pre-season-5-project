namespace Cranberry.Zone.Appearance;

/// <summary>
/// Wave 8 (D53, docs/80): every switch that changes how the character looks. One record so the
/// hub only gains one property, and so the boot line can print the whole of it in one place.
/// <para>
/// Every default here is the behaviour the owner asked for; every one of them is a one-variable
/// rollback, because each changes what he sees on his own back and the client has not been asked
/// about any of them yet (D29 - this lane is BUILT and unit-TESTED, never LIVE-VERIFIED).
/// </para>
/// </summary>
public sealed record SkinOptions
{
    /// <summary>
    /// Hang a mesh on every <c>IS_EQUIPMENT</c> body slot a pickup can reach, including the
    /// stowed-weapon pegs 76/77/80 (long guns), 78/79/81 (pistols), 82-84 (bows) and 106 (melee),
    /// and the apparel slots 3/4/5 where a looted shirt replaces a starter mesh.
    /// <para>
    /// <b>This is the owner longest-standing complaint</b>: <i>"I am able to pick up items and some
    /// go into the slots but that is it. They do not show on me."</i> docs/74 probe W1 measured it
    /// on the wire - body slots 76, 77 and 80 carried a <c>94 01</c> equipment-slot row and no
    /// attachment element at all. Every <c>_3P.adr</c> the roster needs ships.
    /// </para>
    /// <para>
    /// <c>false</c> (<c>CRANBERRY_STOWED_MESHES=0</c>) restores
    /// <see cref="AugustWornVisuals.AdditiveBodySlots"/> - the pre-wave-8 <c>{1, 10, 100}</c> - and
    /// with it exactly the wire docs/74 measured. No client has yet seen an attachment on a stow
    /// peg in this build, so this is the variable to move first if a pickup misbehaves.
    /// </para>
    /// </summary>
    public bool DressStowedWeapons { get; init; } = true;

    /// <summary>
    /// Carry the item own <c>ShaderParameterGroupId</c> on a worn attachment, when - and only
    /// when - the transmitted appearance table defines parameter rows for that group
    /// (<c>AugustDynamicAppearanceTable.DefinesShaderGroup</c>).
    /// <para>
    /// docs/69 proved the AK-47 and pump shotgun colour maps are 30.4 % and 30.1 % near-white,
    /// ranks 1 and 2 of the thirty ground-loot maps, and Cranberry sent group <b>0</b> for
    /// everything - so the moment a gun goes on the owner back it is a white gun. All nine roster
    /// weapon ids are already inside the appearance filter <c>wantedItems</c>, so their groups are
    /// already in the 1,268,831-byte payload and sending them costs <b>zero</b> new bytes. The 26
    /// worn wearables the filter discards keep 0, which is what leaves docs/54 I4 an untouched,
    /// separately-observed experiment.
    /// </para>
    /// <para><c>CRANBERRY_WORN_SHADER=0</c> puts every worn attachment back to group 0.</para>
    /// </summary>
    public bool SendWornShaderGroups { get; init; } = true;

    /// <summary>
    /// A wardrobe pick may re-tint a looted item, never re-model it
    /// (<c>AugustWorldEquipmentVisuals.RetintOnly</c>). <c>CRANBERRY_SKIN_REMODEL=1</c> restores
    /// the pre-wave-8 behaviour, which is the one that gave the owner the small civilian rucksack
    /// when he looted a military backpack.
    /// </summary>
    public bool SkinRetintOnly { get; init; } = true;

    /// <summary>
    /// Never re-send a <c>SetCharacterEquipment*</c> byte-identical to the last one this session
    /// received.
    /// <para>
    /// The owner measured what a full dress costs this client: his own
    /// <c>AttachmentProcessor.log</c> pairs each <c>RemoveAttachmentInternal</c> with the next
    /// <c>ProcessNewAttachment</c> at <b>76 to 534 ms</b> - the visible bare-body frame, his "goes
    /// naked for a split second" - and in one session <b>11 of 25</b> full dresses were
    /// byte-identical repeats that still paid it. Cranberry sends a full dress on bootstrap, on
    /// Gear-editor open and on Gear-editor close, which is the same pattern.
    /// </para>
    /// <para><c>CRANBERRY_DRESS_SUPPRESS=0</c> sends every dress unconditionally.</para>
    /// </summary>
    public bool SuppressIdenticalDress { get; init; } = true;

    /// <summary>
    /// Run the boot-time grey census (<see cref="AugustSkinCensus"/>) and log what it finds. It
    /// changes no byte and costs microseconds; its whole point is to answer "which ids render
    /// grey" before the owner launches rather than after. <c>CRANBERRY_SKIN_CENSUS=0</c> silences
    /// it.
    /// </summary>
    public bool RunSkinCensus { get; init; } = true;

    /// <summary>
    /// Order an attachment appearance-id pair so the row naming THIS body mesh comes first.
    /// <para>
    /// The wave-8 census found the defect this repairs, and nobody had ever seen it: every
    /// four-row hooded-chest entry collapses to a pair of <c>GENDER_ID = 0</c> wildcards, one male
    /// mesh and one female mesh, in an order that varies per item - and the owner measured on his
    /// own client that the FIRST wildcard wins. <b>49 (item, body) pairs in this catalogue were
    /// handing a female character a male hoodie.</b> Reordering the pair costs no bytes and
    /// changes no id.
    /// </para>
    /// <para><c>CRANBERRY_GENDER_ROWS=0</c> restores the pre-wave-8 order exactly.</para>
    /// </summary>
    public bool OrderAppearanceRowsByGender { get; init; } = true;

    /// <summary>
    /// Give the <b>active hand</b> (body slot 7) the same appearance rows and shader parameter
    /// group the same item gets on its stow peg.
    /// <para>
    /// <b>The world-only defect this repairs (docs/106, D190), proven inside a single packet.</b>
    /// <c>captures\wire-20260902-212215.txt:8142</c>, 21:24:32.814, in Z2:
    /// <c>slot=7 grp=0 app=[] Weapon_M16A4_3P.adr</c> beside
    /// <c>slot=76 grp=168 app=[181,182] Weapon_M16A4_3P.adr</c> — the same mesh, tinted on the
    /// player's back and untinted in his hands, because <c>ApplyActiveHandVisual</c> built its
    /// attachment with the two-argument constructor and threw the resolved group away. The August
    /// client only reads the packet's own group when no appearance row resolves
    /// (<c>FUN_140c70d60</c> tail), and 0 there is the neutral <c>Default</c> tint, so the mesh
    /// composites at highlight 1,1,1 / midtone .5 / shadow 0. docs/69 measured the AK-47 and pump
    /// shotgun colour maps at <b>30.4 %</b> and <b>30.1 %</b> near-white — ranks 1 and 2 of thirty
    /// — so for those two guns "no group" means literally white. Invisible in the menu, where slot
    /// 7 holds <c>Weapon_Empty.adr</c>; unmissable after the Z2 drop.
    /// </para>
    /// <para><c>CRANBERRY_HAND_APPEARANCE=0</c> puts the bare two-field attachment back.</para>
    /// </summary>
    public bool SendActiveHandAppearance { get; init; } = true;

    /// <summary>
    /// Carry a <em>fallback</em> shader parameter group on a wardrobe attachment instead of the
    /// hard-coded <c>0</c>.
    /// <para>
    /// The client applies the winning appearance row's own group and reads the packet's field only
    /// when no row resolves; sending 0 is therefore not "no opinion" but the neutral tint. Every
    /// other dress site already carries a group (<see cref="SendWornShaderGroups"/> for pickups,
    /// <c>CharacterVisuals.StarterMesh</c> for the starter outfit,
    /// <c>AugustWorldEquipmentVisuals.BaseAttachment</c> for loot); the wardrobe path was the one
    /// that did not, which is why a wardrobe pick that fails to resolve goes grey and a looted item
    /// in the same slot does not. Gated by <c>DefinesShaderGroup</c>, so it can never name a group
    /// the transmitted table holds no parameters for (docs/54 §4) and costs zero payload bytes.
    /// </para>
    /// <para><c>CRANBERRY_WARDROBE_SHADER=0</c> restores the hard-coded 0.</para>
    /// </summary>
    public bool SendWardrobeShaderGroups { get; init; } = true;

    /// <summary>
    /// End every skin-item burst outside the <c>ClientBeginZoning</c> burst with an equipment
    /// writer, so the client's own skin-manager recomposite is never the last thing to touch the
    /// outfit.
    /// <para>
    /// The menu bootstrap already obeys the owner's law — <c>ac 11 → ac 19 → ac 23 → ac 28 →
    /// ac 24×N → 94 01</c> (<c>captures\wire-20260902-212215.txt:23-33</c>) — and every burst the
    /// world adds inverts it: <c>94 01 → ac 24×N</c> at both <c>ClientIsReady</c>s and at the
    /// parachute landing. His Z1 round-29 measurement of that inversion is the purple suit
    /// (<c>ZoneWardrobe.DressAfterManagers</c>, D86). This APPENDS one dress; it reorders nothing,
    /// so the docs/32 order regression guard 4 pins is untouched and the zoning burst is not
    /// changed at all.
    /// </para>
    /// <para>
    /// <b>DEFAULT OFF since 2026-09-03 (D211, docs/108): this is the packet that cost the owner
    /// two "G10" match entries.</b> The paragraph above is wrong where it says the appended dress
    /// "reorders nothing". It reorders the burst the client actually reads: a lobby
    /// <c>ClientIsReady</c> that ends <c>94 01 → ac 24×N → 94 01</c> leaves the client unable to
    /// finish the next <c>ClientBeginZoning</c> world load at all — it logs
    /// <c>WFWR: Waiting for load : Zoning = true, Loading = true</c>, stops acknowledging, and
    /// never sends the zoning <c>ClientIsReady</c>. Seven owner-free live runs on 2026-09-03
    /// isolate this one field: runs 1, 3, 4 and 5 (appended dress ON) all wedge there and runs 2,
    /// 6 and 7 (appended dress OFF) all reach the parachute landing, with run 7 differing from run
    /// 4 by nothing else. It is the same law docs/32's third regression states — the dress goes
    /// BEFORE the skin rows and nothing re-states it afterwards.
    /// </para>
    /// <para><c>CRANBERRY_DRESS_LAST=1</c> restores the appended dress, and the client refuses it.</para>
    /// </summary>
    public bool DressLastAfterSkinRows { get; init; }

    /// <summary>
    /// Answer EVERY accepted skin click with the same look the Gear-editor close path sends: the
    /// one-row <c>ac 24</c> echo the retail server sends, and then the <c>94 01</c> dress that
    /// actually carries the garment. This is the owner's defect 2 of 2026-09-03 — <i>"switching between skins
    /// in the appearance/gear screens does NOT change the character in real time"</i>.
    /// <para>
    /// The friend server answers a click with the single <c>ad 23</c> row and nothing else
    /// (<c>C:\Project\out\ingest-admin-20260822-part1\packets_1118_62892.log</c>, 16:49:00.061
    /// request → 16:49:00.100 reply, twelve clicks between 16:49:00 and 16:49:13 with no
    /// <c>95 01</c> at all), and that is enough THERE because its <c>95 01</c> never carries a
    /// wardrobe garment: all six of its menu dresses carry the same five starter meshes and differ
    /// only in the escrow account-item guids bound into the equipment-slot rows (lines 153, 341,
    /// 379, 456, 468, 540). Cranberry bakes the selected garment into the dress instead, so for
    /// Cranberry the dress IS the look and a click that does not re-send it cannot be seen.
    /// </para>
    /// <para><c>CRANBERRY_SKIN_LIVE_PREVIEW=0</c> restores the single-row echo and nothing else.</para>
    /// </summary>
    public bool LiveSkinPreview { get; init; } = Enabled("CRANBERRY_SKIN_LIVE_PREVIEW");

    /// <summary>
    /// Let the <c>94 01</c> dress be the ONLY writer of the menu look, by dropping the
    /// <c>ac 24</c> re-announce from the <b>lobby</b> <c>ClientIsReady</c> (the match one is
    /// untouched).
    /// <para>
    /// The owner's defects 1 and 3 of 2026-09-03 are one bug with one cause. Every menu burst in
    /// <c>captures\wire-20260903-174857.txt</c> ends with the dress — bootstrap :23-34
    /// (<c>ac 11 → ac 19 → ac 23 → ac 28 → ac 24×7 → 94 01</c>), Gear-editor open :331-342,
    /// Gear-editor close :447-458 — and exactly one does not: the lobby <c>ClientIsReady</c> at
    /// :53-60 sends <c>94 01 → ac 24×7</c>, leaving the client's own skin-manager recomposite
    /// (D86, the round-29 purple suit) as the last thing to touch the outfit. The four dresses are
    /// byte-identical, so the look the owner sees in the main menu and the look he sees the instant
    /// he opens the Gear editor come from two different sources and differ for that reason alone.
    /// </para>
    /// <para>
    /// D211 forbids the other repair: appending a second dress after the rows is the packet that
    /// cost the owner two match entries, and <c>CRANBERRY_DRESS_LAST</c> is OFF for that reason.
    /// Dropping the rows instead never builds D211's forbidden <c>94 01 → ac 24×N → 94 01</c>; it
    /// leaves the lobby ready reply as a bare <c>94 01</c>, which is <b>exactly</b> the shape of
    /// the 2026-08-29 18:43 session that proved zoning works — that character had no wardrobe
    /// selections at all, so <c>SendWornSkinItems</c> emitted nothing there either
    /// (<c>ZoneService.cs</c>, the match-zoning comment). It keeps docs/32's third regression
    /// ("the dress goes before the skin rows") trivially true, and it matches the friend server,
    /// which sends nothing whatsoever on <c>ClientIsReady</c>
    /// (<c>packets_1118_62892.log</c>, 16:48:45.321 and 16:48:49.757). The rows are not lost: the
    /// bootstrap sent them two seconds earlier and the Gear editor re-sends them.
    /// </para>
    /// <para><c>CRANBERRY_MENU_LOOK_DRESS=0</c> restores the lobby re-announce.</para>
    /// </summary>
    public bool MenuLookFromDressOnly { get; init; } = Enabled("CRANBERRY_MENU_LOOK_DRESS");

    /// <summary>
    /// Directory of the durable per-character wardrobe store, or <c>null</c>/empty to keep
    /// selections in memory only (which is what every build before wave 8 did - a
    /// <c>ConcurrentDictionary</c> that died with the host and never evicted an entry).
    /// <c>CRANBERRY_WARDROBE_STORE</c> overrides it.
    /// </summary>
    public string? WardrobeStoreRoot { get; init; }

    /// <summary>
    /// Keep Cranberry own extra pickup-only gate on the three backpack prototypes and the two
    /// performance-footwear prototypes, which neither the August <c>SELECT_PREVIEW_ONLY</c> column
    /// nor the owner Z1 server applies. Default <c>true</c> = no behaviour change;
    /// <c>CRANBERRY_LOBBY_PACKS=1</c> makes them ordinary lobby clothing, which is what both
    /// sources say they are.
    /// </summary>
    public bool GateBackpacksAndPerformanceFootwear { get; init; } = true;

    /// <summary>
    /// Send a selected weapon skin's appearance rows for the weapon in the player's hands
    /// (docs/106 §12, D225).
    /// <para>
    /// A weapon selection reaches this server only as <c>ac 24 SetSkinItem</c> in collection 2.
    /// <c>AugustWorldEquipmentPolicy.Rules</c> lists apparel categories and nothing else, so
    /// <c>TryEquip</c> refuses every weapon and <c>ApplySkin</c> is never reached for a gun: the
    /// owner's three weapon selections changed nothing in the world at all. This substitutes the
    /// reward item's rows and shader group for the base item's on body slot 7, and only when the
    /// transmitted table actually carries rows for that reward - never a blind swap.
    /// </para>
    /// <para><c>CRANBERRY_WEAPON_SKINS_IN_WORLD=0</c> restores the base rows.</para>
    /// </summary>
    public bool SendWeaponSkinsInWorld { get; init; } = Enabled("CRANBERRY_WEAPON_SKINS_IN_WORLD");

    /// <summary>
    /// When an item's appearance rows are all for the OTHER body, still carry that row's shader
    /// group on the packet rather than the neutral 0 (docs/106 §12, D226).
    /// <para>
    /// The boot census's <c>NoRowForThisBody</c> verdict is exactly this case: item 10, the AR-15,
    /// has two rows and both are <c>GENDER_ID 2</c>, so a male body resolves no row, the client
    /// falls back to the packet's own group, and 0 there is the neutral tint. A weapon's colourway
    /// is not gendered - the mesh is the same file - so the other body's group is the right answer
    /// and the only alternative is white. It is still gated by <c>DefinesShaderGroup</c>.
    /// </para>
    /// <para><c>CRANBERRY_CROSS_GENDER_SHADER=0</c> restores the 0.</para>
    /// </summary>
    public bool CrossGenderShaderGroup { get; init; } = Enabled("CRANBERRY_CROSS_GENDER_SHADER");

    /// <summary>
    /// Send a selected skin's appearance rows and shader group for a picked-up item on <b>every</b>
    /// worn slot and stow peg, not only the hand (docs/106 §13, D283).
    /// <para>
    /// D225 reached body slot 7 and nothing else, and the only other site that could dress a
    /// looted item with a pick - <c>AugustWorldEquipmentVisuals.ApplySkin</c> - reads a state
    /// object (<c>AugustWorldEquipmentState</c>) that nothing in this server ever fills. So the
    /// backpack, the helmets and the three guns on the owner's back all carried their base
    /// colourway in the 2026-09-03 19:48 session while his eighteen selections sat unused.
    /// <see cref="AugustWornSkins"/> resolves the pick from the catalogue's own row for the looted
    /// item, and D53's re-tint-not-re-model guard still declines a pick that would change the
    /// silhouette.
    /// </para>
    /// <para><c>CRANBERRY_WORN_SKINS_IN_WORLD=0</c> restores the base rows on every slot but 7.</para>
    /// </summary>
    public bool SendWornSkinsInWorld { get; init; } = true;

    /// <summary>
    /// Replay the durable wardrobe store's saved selections at login (D89). Default <c>true</c>.
    /// <para>
    /// <b>This is where the owner's menu outfit comes from</b> (docs/106 §14, D284). Nothing in the
    /// menu look is a "default": <c>CharacterVisuals.StarterOutfit</c> is the retail starter five,
    /// and every garment over the top of it is a row of <c>state\wardrobe\w-&lt;guid&gt;.json</c>
    /// that some earlier session committed and no session ever clears - which is why a hoodie the
    /// owner does not remember choosing keeps coming back
    /// (<c>w-0000000000001004.json</c>: <c>3250 -&gt; reward 4266</c>,
    /// <c>Survivor*_Chest_Hoodie_Down_Tintable.adr</c>).
    /// <c>CRANBERRY_WARDROBE_RESTORE=0</c> logs in with no selections at all, i.e. the retail
    /// starter outfit, without touching the file; renaming the file clears it for good.
    /// </para>
    /// </summary>
    public bool RestoreWardrobeSelections { get; init; } = true;

    /// <summary>The one line the owner reads at boot to know which of these are live.</summary>
    public string Describe() =>
        $"skins: stowed meshes {OnOff(DressStowedWeapons)}, worn shader groups "
        + $"{OnOff(SendWornShaderGroups)}, hand appearance "
        + $"{OnOff(SendActiveHandAppearance)}, wardrobe shader groups "
        + $"{OnOff(SendWardrobeShaderGroups)}, dress last after skin rows "
        + $"{OnOff(DressLastAfterSkinRows)}, re-tint-not-re-model {OnOff(SkinRetintOnly)}, "
        + $"identical-dress suppression {OnOff(SuppressIdenticalDress)}, gender-ordered "
        + $"appearance rows {OnOff(OrderAppearanceRowsByGender)}, census "
        + $"{OnOff(RunSkinCensus)}, live skin preview {OnOff(LiveSkinPreview)}, "
        + $"menu look from the dress only {OnOff(MenuLookFromDressOnly)}, "
        + $"lobby backpack/footwear gate "
        + $"{OnOff(GateBackpacksAndPerformanceFootwear)}, weapon skins in world "
        + $"{OnOff(SendWeaponSkinsInWorld)}, cross-gender shader group "
        + $"{OnOff(CrossGenderShaderGroup)}, worn skins in world "
        + $"{OnOff(SendWornSkinsInWorld)}, wardrobe restore "
        + $"{OnOff(RestoreWardrobeSelections)}, wardrobe store "
        + (string.IsNullOrWhiteSpace(WardrobeStoreRoot) ? "off (in-memory only)" : WardrobeStoreRoot);

    private static string OnOff(bool value) => value ? "on" : "OFF";

    /// <summary>
    /// A default-ON rollback switch read straight from the environment. The two 2026-09-03
    /// menu-skin switches live here rather than in the config lane's key table because that lane
    /// is uncommitted in this tree; only <c>=0</c> turns one off.
    /// </summary>
    private static bool Enabled(string variable) =>
        Environment.GetEnvironmentVariable(variable) is not "0";
}
