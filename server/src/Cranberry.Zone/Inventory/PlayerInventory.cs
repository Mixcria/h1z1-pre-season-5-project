namespace Cranberry.Zone.Inventory;

/// <summary>
/// Server-side settings for the inventory model. Everything here that is <b>not</b> read from a
/// client datasheet is an owner decision and is flagged as such, in the sense of D23's gas
/// schedule: design, not derivation.
/// </summary>
public sealed record InventoryOptions
{
    /// <summary>
    /// The carry capacity a survivor has before any container is worn.
    /// <para>
    /// <b>OWNER DECISION (D26, docs/41 §7 L7), not a derived value.</b> Container definition 117 -
    /// the character's own bag - is <c>MAX_BULK 0, IS_DYNAMIC_BULK 1</c>, so the client supplies no
    /// base at all; its capacity is entirely subsumed from the containers equipped on the
    /// character. The only sheet row carrying 100 (<c>ContainerDefinitions</c> row 49) is
    /// referenced by no item. D26's "base 100" is therefore recorded here as a knob, exactly like
    /// D23's gas schedule, and never presented as a fact about the August build.
    /// </para>
    /// </summary>
    public int BaseCarryBulk { get; init; } = 100;

    /// <summary>
    /// Test a loadout slot's class set against <c>ITEM_CLASS</c> <b>union</b>
    /// <c>ItemClassMappings.txt</c>, which is the August client's own rule, rather than against
    /// <c>ITEM_CLASS</c> alone (docs/78 §4.5).
    /// <para>
    /// <b>ON, because it is a correctness fix rather than a design choice.</b> The proof is by
    /// construction from the client's own sheets: loadout 17's slots 40 (Q, class 25080), 41 (E,
    /// 25009) and 5 (25081) name classes with <b>zero</b> rows in <c>ClientItemDefinitions</c>
    /// carrying them as <c>ITEM_CLASS</c>, so a client that did not fold the mapping in could never
    /// fill those tiles in any build. <see cref="ItemClassMappings"/> carries the table and the
    /// argument. The owner's own server hit the identical trap at 1087.
    /// </para>
    /// <para>
    /// <b>By itself it changes no placement.</b> Every item it newly matches — 2423 Field Bandage,
    /// 2424 First Aid Kit, 3375 — is <c>FLAG_CAN_EQUIP = 0</c>, and RULE 1 of
    /// <c>InventoryAutoAssign.Resolve</c> refuses those before the class test is reached. What it
    /// fixes is <c>SupportingLoadoutSlots</c>, the direct translation of the client's
    /// <c>FindAllSupportingLoadoutSlotsByItemId</c>, which was answering the wrong thing for 311
    /// items. Moving a medkit onto a tile is the separate, riskier
    /// <see cref="QuickUseConsumables"/>.
    /// </para>
    /// </summary>
    public bool FoldItemClassMappings { get; init; } = true;

    /// <summary>
    /// Let an item with <c>FLAG_CAN_EQUIP = 0</c> take a free quick-use loadout slot whose
    /// class set accepts it, instead of falling straight into the bag — i.e. let a first aid kit
    /// reach a quick-use tile.
    /// <para>
    /// <b>ON.</b> Loadout 17 defines slots 40 and 41 as <c>ConsumeItem1</c>/<c>ConsumeItem2</c>
    /// (Q and E), with mapped classes 25080/25009. This puts starter bandages on Q and the next
    /// compatible medical pickup on E.
    /// </para>
    /// <para>
    /// The carve-out is deliberately narrow: it can only name Q or E, so it cannot
    /// put a bandage in an apparel slot or in the required Fists slot. Note also that
    /// <c>FLAG_CAN_EQUIP</c> is <i>not</i> one of the four conditions the client's own
    /// <c>FUN_140d35510</c> tests — it is a Cranberry rule from docs/41 §5b, which was safe while it
    /// was redundant and becomes load-bearing the moment <see cref="FoldItemClassMappings"/> is on.
    /// </para>
    /// </summary>
    public bool QuickUseConsumables { get; init; } = true;

    /// <summary>
    /// Bind loadout slot 7 to item 85 ("Fists") at bootstrap. The sheet asks for it:
    /// loadout 17 slot 7 is <c>FLAG_REQUIRED = 1</c>, <c>FLAG_AUTO_EQUIP = 1</c>,
    /// <c>ITEM_ID = 85</c>, and the client's own config names the same id
    /// (<c>Inventory.SpecialEmptyHandsItemId = 85</c>).
    /// </summary>
    public bool EquipFists { get; init; } = true;

    /// <summary>
    /// Wield a picked-up weapon when the hand is empty, instead of stowing it. The owner's stated
    /// expectation ("so I wasn't able to test") is that the first gun picked up should be the one
    /// in your hands.
    /// <para>
    /// <b>OFF, and it must stay off until docs/45 §5b is built.</b> Wielding is the only thing that
    /// puts an item in body slot 7, and a body-slot-7 <c>EquipmentSlotRow</c> hard-crashes the
    /// August client: the per-frame active-hand update reads the bound weapon's fire-group
    /// descriptor and dereferences it unguarded, and Cranberry has never sent fire-group data, so
    /// the descriptor is <c>NULL</c> (<c>EXCEPTION_ACCESS_VIOLATION</c> reading <c>0x38</c> at
    /// <c>FUN_1411cee73+0xa1</c>). 4 of 4 packets carrying such a row killed the client in
    /// <c>host-20260829-220829.log</c>; 5 of 5 without one were accepted. See
    /// <c>ActiveHandRowGuard</c>, which is the belt to this switch's braces.
    /// </para>
    /// <para>
    /// With this off, <c>InventoryAutoAssign.Resolve</c> RULE 3 takes <c>PassiveBodySlot(...)</c>
    /// instead of <c>ACTIVE_EQUIP_SLOT_ID</c>, so a picked-up AR-15 stows on body slot 76
    /// (<c>R_LongWeapon_1</c>), an R380 on 78 (<c>R_ShortWeapon_1</c>) and a machete on its own
    /// <c>PASSIVE_EQUIP_SLOT_ID</c>. The gun still enters the inventory and still fills a
    /// weapon-wheel box - only the hand binding is withheld.
    /// </para>
    /// </summary>
    public bool WieldFirstWeapon { get; init; }

    /// <summary>
    /// Draw a weapon with the <b>eight-packet order</b> of docs/95 instead of one whole-character
    /// <c>94 01 SetCharacterEquipment</c> that happens to carry a body-slot-7 row. The order is
    /// hotbar behaviour observed in the owner's Z1 session logs, adopted under D53; the two packet
    /// bodies it adds are proven from the August binary (docs/95 §1, §2).
    /// <para>
    /// <b>OFF by default — this is the untested half of the 2026-08-31 finding.</b> The one-packet
    /// wield DOES reach the client and does NOT crash it (docs/93 closed docs/45), but the owner
    /// could not move or act until he dropped the gun. The hotbar draw binds the slot LAST, after
    /// tearing the old ability down, freeing the vacated slot, re-stating the item and installing
    /// the new ability manager; Cranberry bound it first and did none of the rest. (The "sixty
    /// identical occurrences" this note used to cite came from a forbidden third-party artefact and
    /// is retracted — see docs/95's 2026-09-02 correction.)
    /// </para>
    /// <para>
    /// <c>CRANBERRY_WIELD_SEQUENCE=1</c> turns it on. It does nothing at all unless something
    /// actually wields — pair it with <c>CRANBERRY_WIELD_FIRST_PICKUP=1</c>, or with a hotbar
    /// selection.
    /// </para>
    /// </summary>
    public bool UseWieldSequence { get; init; } = true;

    /// <summary>
    /// Include <c>CODE_FACTORY_NAME = Weapon</c> rows — fists and binoculars — in
    /// the bootstrap <c>ItemAdd</c> burst that <c>ZoneService.EnsureInventory</c> sends inside the
    /// zoning sequence.
    /// <para>
    /// <b>ON.</b> <c>ZoneService</c> sends these records through <c>WeaponSession.CreateItemAdd</c>,
    /// which supplies the August weapon-specific tail. Turning it off is retained as a rollback.
    /// </para>
    /// </summary>
    public bool GrantWeaponItemsAtBootstrap { get; init; } = true;

    /// <summary>
    /// The item rows granted at bootstrap so the inventory panel's clothing boxes are not empty,
    /// in the order they are granted. Empty turns the starting outfit off entirely.
    /// <para>
    /// docs/46 §7b. The panel is fed by the loadout manager resolved against the item collection,
    /// not by the <c>94 01</c> attachment list, so a character can be fully dressed and show an
    /// entirely empty panel - which is what the owner reported after wave 3. Each id here is a real
    /// <c>ClientItemDefinitions</c> row whose mesh is the one
    /// <c>CharacterVisuals.FromSelection</c> already attaches (except the boots - see
    /// <see cref="SurvivorStarterOutfit"/>), so granting them changes nothing visually.
    /// </para>
    /// <para>
    /// Each piece is bound to both its <c>FLAG_AUTO_EQUIP</c> loadout slot and its body slot. That
    /// makes Drop/Remove authoritative for the visible mesh instead of letting the immutable
    /// wardrobe baseline silently dress the item again.
    /// </para>
    /// </summary>
    public IReadOnlyList<uint> StarterOutfit { get; init; } = SurvivorStarterOutfit.DefaultItemDefinitionIds;

    /// <summary>
    /// Answer <c>Items.RequestUseItem</c> (<c>0xac/0x2c</c>) with <c>ITEM_USE_OPTION_ID 4</c>
    /// <c>DropItem</c>: take the item out of the inventory and put it back on the ground.
    /// <para>
    /// <b>ON.</b> The client has been asking for this since wave 2 and Cranberry has never answered
    /// - 21 <c>zone Items sub=0x2c ... (unanswered)</c> lines across four captures, the great
    /// majority of them option 4 on an item the player had just picked up. The packet is
    /// client-originated, so its layout is LIVE-VERIFIED rather than derived
    /// (<see cref="RequestUseItem"/>), and the reply is made of writers that are already proven:
    /// <c>ItemDelete (11 04)</c>, <c>SetLoadoutSlots (86/04)</c> and <c>UpdateContainer (c8/06)</c>.
    /// </para>
    /// </summary>
    public bool AnswerDropItem { get; init; } = true;

    /// <summary>
    /// Answer <c>ConsumeItem</c> (<c>ITEM_USE_OPTION_ID</c> 1, 2, 3, 9, 11, 17, 47, 52, 53, 56, 95,
    /// 99, 103) by taking one unit out of the inventory.
    /// <para>
    /// <b>OFF, deliberately.</b> Consuming is the easy half; the effect - the heal on a Field
    /// Bandage, the cure on a Procoagulant - belongs to the resource/effect lane, and eating a
    /// bandage that heals nothing is worse for the owner than a bandage that does nothing at all,
    /// because the item is gone either way and only one of the two can be diagnosed. Turn this on in
    /// the same change that applies the effect.
    /// </para>
    /// </summary>
    public bool AnswerConsumeItem { get; init; } = true;   // D341: on - the cast-and-heal exists now

    /// <summary>
    /// Answer <c>RemoveItem</c> (12), the most-offered option in the whole August build - 1,382
    /// items carry it, more than <c>DropItem</c>'s 1,365 (docs/86 SS2.3). It takes a worn item off
    /// and puts it in the bag; on an item that is already in the bag it repaints the tile.
    /// <para>
    /// <b>ON.</b> Until wave 9 it fell through to "no server behaviour is derived for this action
    /// yet" and sent nothing at all, which is a dead menu entry on more items than any other verb.
    /// Semantics ported from <c>C:\Z1\Server\Zone\ZoneItemUse.cs</c> <c>Remove</c> (D53).
    /// </para>
    /// </summary>
    public bool AnswerRemoveItem { get; init; } = true;

    /// <summary>
    /// Answer <c>MoveItem</c> (61), offered on 1,257 items.
    /// <para>
    /// <b>ON.</b> docs/63 SS5.3 concluded that <c>MoveItem</c> needs a destination that no observed
    /// packet form has room for, and therefore that drag-and-drop must ride a different sub. That is
    /// <b>falsified</b>: in the owner's server <c>MoveItem</c> carries no destination <em>because it
    /// is a reciprocal toggle</em> - worn goes to the bag, bagged-and-equippable goes to the loadout
    /// (<c>ZoneItemUse.Move</c>). Both packet forms are now fully accounted for (docs/86 SS2.5).
    /// </para>
    /// </summary>
    public bool AnswerMoveItem { get; init; } = true;

    /// <summary>
    /// Answer <c>EquipItem</c> (60), offered on 910 items: put a bagged item into the loadout slot
    /// its own <c>FLAG_AUTO_EQUIP</c> class row names. Ported from
    /// <c>ZoneInventoryActions.TryEquip</c> (D53).
    /// <para><b>ON.</b> This is the only way to arm yourself from the panel.</para>
    /// </summary>
    public bool AnswerEquipItem { get; init; } = true;

    /// <summary>
    /// Answer <c>SalvageItem</c> (6 and 63), offered on 634 items, by running
    /// <c>Crafting.ShredTable.Shred</c>.
    /// <para>
    /// <b>ON, and it costs nothing to switch on because the work was already written and tested.</b>
    /// <c>ShredTable</c>'s own header records its blocker as "the packet the client sends when a
    /// player right-clicks Shred... two candidates (<c>09 0b</c>, <c>ac 2c</c>
    /// <c>Items::RequestUseItem</c>)". docs/63 SS1, written the same wave by a different lane, proves
    /// from 21 client-originated packets that it is <c>ac 2c</c> with option 6/63. The two halves
    /// were never joined, so the owner shredded four times on 30 Aug and got silence from green
    /// code.
    /// </para>
    /// </summary>
    public bool AnswerSalvageItem { get; init; } = true;

    /// <summary>
    /// Answer <c>UnloadWeapon</c> (7), offered on 106 items.
    /// <para>
    /// <b>ON - but "answer" here means a named refusal, not an unload, and that is deliberate.</b>
    /// Unloading has to move rounds from a magazine into the bag, and this build's magazine is not
    /// fed from the bag at all: <c>ShooterCombatState.Reload</c> sets
    /// <c>Ammo = RetailBalance.ClipSize(...)</c> out of nothing. Unload-then-reload-then-unload
    /// would therefore mint ammunition without limit. Z1's <c>ZoneItemUse.Unload</c> is safe only
    /// because his reload consumes carried rounds, which is the shooting lane's model to build, not
    /// this lane's. The verb answers with a reason instead of silence; flipping the refusal into a
    /// real unload is a two-line change the moment reload draws from the bag.
    /// </para>
    /// </summary>
    public bool AnswerUnloadWeapon { get; init; } = true;

    /// <summary>
    /// An item with no loot-table ground actor falls back to its <c>ITEM_CLASS</c>'s folded-clothes
    /// prop and then to the burlap bag, instead of having its drop refused
    /// (<see cref="DroppedItemCatalogue"/>).
    /// <para>
    /// <b>ON.</b> This is the owner's own <c>DropItem refused for item 2613 ... this build has no
    /// ground actor for it</c>, three times in a five-minute session. Off restores wave 8's refusal
    /// exactly.
    /// </para>
    /// </summary>
    public bool UniversalGroundActor { get; init; } = true;

    /// <summary>
    /// An equip is never refused because the item it displaces will not fit in the bag. The
    /// displaced item goes on the ground instead.
    /// <para>
    /// <b>ON.</b> The owner's log: <c>Command.PlayerSelect REFUSED item 2168 - loadout slot 11 holds
    /// item 2172 and the bag has no room for it (displaced, but bulk 180 + 250 exceeds 200)</c>.
    /// Both helmets are <c>BULK</c> 250 against a base carrier of 200 (100 + shirt 50 + pants 50),
    /// so <b>no helmet can ever be displaced into a Cranberry bag before a backpack is found</b> and
    /// the first helmet you pick up is the last one you can have. Z1's <c>TryEquip</c> displacement
    /// path does no bulk check at all and under D53 his behaviour is the truth; this improves on it
    /// in the one way <see cref="UniversalGroundActor"/> makes possible, by putting the overflow on
    /// the floor rather than over-filling the bag. Off restores the refusal.
    /// </para>
    /// </summary>
    public bool SwapNeverRefusedForBulk { get; init; } = true;

    /// <summary>
    /// The owner's server draws a
    /// centre-screen cast bar for the second a shred occupies (his r37 fix for "shredding worked but
    /// it needs to show a timer"), on <c>d0 02</c>. That literal is a <b>trap</b> in August, where
    /// 0xd0 is <c>TimedGrantBase</c>; the right base is <c>0xcf CharacterStateBase</c>, which IS in
    /// the 1148 registration table - but carries <b>no registered subs at all</b>, like 0xc8 and
    /// 0xce. Sub <c>0x02</c> and its 65-byte body are ported from the reference server.
    /// <para>
    /// Cranberry sends <c>cf 02</c> at shred start with the option's one-second duration and
    /// animation id, then applies the shred when that duration completes.
    /// </para>
    /// </summary>
    public const string ShredInteractionProtocol =
        "cf 02 CharacterState.InteractionStart - 67 bytes, duration and animation from August data";

    /// <summary>
    /// Named for the effects lane and <b>not</b> flipped here: when this and
    /// <see cref="AnswerConsumeItem"/> are both on, a consume sends the <c>ResourceEvent</c> type 3
    /// that applies the item's heal. docs/63 SS5.1's reasoning holds - a bandage that heals nothing
    /// is worse than one that does nothing - and the health model is not this lane's to design.
    /// This exists so there is no design left to do, only wiring.
    /// </summary>
    public bool ConsumeHealsThroughResourceEvent { get; init; }

    /// <summary>
    /// <b>ON (D260).</b> Every item whose own <c>ItemUseOptionGroup</c> offers <c>SalvageItem</c>
    /// actually shreds.
    /// <para>
    /// Joining <c>ItemUseOptions.txt</c> × <c>ItemUseOptionGroups.txt</c> ×
    /// <c>ItemIdUseOptionGroupId.txt</c> gives the exact 645-item shreddable set, and <b>thirteen</b>
    /// of them fell outside <see cref="Crafting.ShredTable.Yields"/> and were answered with
    /// <c>Container.Error WrongItemType</c>: <b>Boots (94)</b>, Improvised Compass (1444), Blackberry
    /// Pie (1706), Broken Wooden Item (1694), dev item 40 and the eight ammunition rounds on option
    /// 87. Boots is the one a player meets in a real match. Telling a player the client offered them
    /// an option it should not have is the exact "right-click did nothing" class of defect docs/86
    /// was written to end.
    /// </para>
    /// <para>
    /// Off restores the wave-9 seven-class table and those thirteen refusals.
    /// </para>
    /// </summary>
    public bool ShredEveryOfferedItem { get; init; } = true;

    /// <summary>
    /// <b>ON (D259).</b> Finish a drop with <c>0f 4a DroppedItemNotification</c>, the toast the
    /// friend's server sends last in its own drop chain.
    /// <para>
    /// Without it a drop looks like an item vanishing. Off restores the wave-9 chain exactly.
    /// </para>
    /// </summary>
    public bool SendDroppedItemNotification { get; init; } = true;

    /// <summary>
    /// <b>ON (D259).</b> Spawn a dropped object facing the way the player is facing, instead of with
    /// an identity rotation.
    /// <para>
    /// The captured drop's <c>d7</c> carries the four floats <c>(0, -3.0808, 0, 1)</c> — a yaw in
    /// <b>component Y</b> with <c>w</c> left at 1, which is not a normalised quaternion and settles
    /// the Euler-vs-quaternion question docs/33 §6c left open <em>for this field</em>. Off restores
    /// <c>(0, 0, 0, 1)</c>, which is the identity in either reading.
    /// </para>
    /// </summary>
    public bool DropCarriesFacing { get; init; } = true;
}

/// <summary>
/// One item instance the client knows about - the thing a <c>ClientUpdate.ItemAdd</c> creates.
/// An instance lives in <em>at most one</em> place at a time: either a container slot
/// (<see cref="ContainerGuid"/> non-zero) or a loadout slot (<see cref="LoadoutSlotId"/> non-zero).
/// </summary>
public sealed class InventoryItemInstance
{
    internal InventoryItemInstance(ulong guid, uint definitionId, uint count, InventoryItemFact fact)
    {
        Guid = guid;
        DefinitionId = definitionId;
        Count = count;
        Fact = fact;
    }

    /// <summary>The instance guid the client keys everything on.</summary>
    public ulong Guid { get; }

    /// <summary>The <c>ClientItemDefinitions</c> row.</summary>
    public uint DefinitionId { get; }

    internal Func<uint, uint>? SkinDefinition { get; init; }
    /// <summary>A looted or explicitly chosen appearance, including an unskinned base item.</summary>
    public uint? SkinOverrideDefinitionId { get; internal set; }
    /// <summary>UI identity; the base definition still owns capacity, armour, ammo and crafting.</summary>
    public uint DisplayDefinitionId => SkinOverrideDefinitionId
        ?? (LoadoutSlotId == 0 && Fact.ItemClass == 25002 ? DefinitionId : SkinDefinition?.Invoke(DefinitionId) ?? DefinitionId);

    /// <summary>Stack size. Capped by <see cref="InventoryItemFact.MaxStackSize"/>.</summary>
    public uint Count { get; internal set; }

    /// <summary>The nine datasheet columns the model reads (docs/41 §5a).</summary>
    public InventoryItemFact Fact { get; }

    /// <summary>Container this instance sits in, or 0 when it is worn/wielded instead.</summary>
    public ulong ContainerGuid { get; internal set; }

    /// <summary><c>ContainerDefinitions</c> row of <see cref="ContainerGuid"/>, or 0.</summary>
    public uint ContainerDefinitionId { get; internal set; }

    /// <summary>Slot index <em>within</em> <see cref="ContainerGuid"/>, or 0. 1-based.</summary>
    public uint ContainerSlotId { get; internal set; }

    /// <summary>Loadout slot this instance is bound to, or 0.</summary>
    public uint LoadoutSlotId { get; internal set; }

    /// <summary>Body slot the mesh attaches to, or 0 when the item shows no attachment.</summary>
    public uint EquipmentSlotId { get; internal set; }

    /// <summary>
    /// Bulk this instance costs its container. Zero when it is not <em>in</em> a container: bulk is
    /// a per-container number (the client's own <c>Bulk(Used/Max)</c> display), and worn or wielded
    /// items are bound to loadout slots instead. That is also why item 85 "Fists"
    /// (<c>BULK = 1000000</c>, a never-in-a-bag sentinel) and item 3156 "Inventory"
    /// (<c>BULK = 9999</c>, the bag itself) cost nothing - neither is ever a container resident.
    /// </summary>
    public int Bulk => ContainerGuid == 0 ? 0 : Fact.Bulk * (int)Count;

    /// <summary>
    /// The 62-byte wire record (<c>FUN_140a3aa60</c>) for this instance.
    /// <para>
    /// The two fields that were wrong before this lane are <see cref="ContainerGuid"/> - which was
    /// <c>ZoneOptions.GroundLootContainerGuid</c>, defaulted to <b>0</b>, a guid that names no
    /// container - and <see cref="ContainerSlotId"/>, which was a global monotonic counter rather
    /// than an index within a container (docs/41 §0).
    /// </para>
    /// </summary>
    public InventoryItem ToRecord(ulong ownerGuid) => LoadoutSlotId != 0
        ? new(
            DefinitionId: DisplayDefinitionId,
            ItemGuid: Guid,
            Count: Count,
            OwnerGuid: ownerGuid,
            ContainerGuid: PlayerInventory.EquippedContainerGuid,
            ContainerDefinitionId: PlayerInventory.LoadoutContainerDefinitionId,
            SlotId: LoadoutSlotId)
        : new(
            DefinitionId: DisplayDefinitionId,
            ItemGuid: Guid,
            Count: Count,
            OwnerGuid: ownerGuid,
            ContainerGuid: ContainerGuid,
            ContainerDefinitionId: ContainerDefinitionId,
            SlotId: ContainerSlotId);
}

/// <summary>
/// One server-owned storage container on the character. The base bag (definition 117) is created by
/// <see cref="PlayerInventory.Bootstrap"/>. Other worn container items are advertised as empty
/// protocol records by <see cref="PlayerInventory.ToInitContainers"/> while cargo remains in this
/// dynamic bag.
/// </summary>
public sealed class InventoryContainer
{
    private readonly SortedDictionary<uint, InventoryItemInstance> _slots = [];

    internal InventoryContainer(ulong guid, uint definitionId, ulong ownerGuid, ContainerDefinition definition)
    {
        Guid = guid;
        DefinitionId = definitionId;
        OwnerGuid = ownerGuid;
        Definition = definition;
    }

    /// <summary>
    /// The container's guid.
    /// <para>
    /// <b>LEAD (docs/41 §7 L4):</b> Cranberry uses the <em>providing item's own instance guid</em>
    /// as the container guid. Nothing decompiled states that; it is consistent with everything
    /// observed (a container is keyed only by guid, <c>REMOVE_WHEN_EMPTY</c> and
    /// <c>ITEM_SPAWNER_ID</c> are per-definition, and the client has a <c>SourceItem</c> container
    /// kind), and the first live run falsifies it by answering
    /// <see cref="ContainerErrorCode.UnknownContainer"/>.
    /// </para>
    /// </summary>
    public ulong Guid { get; }

    /// <summary>The <c>ContainerDefinitions</c> row id.</summary>
    public uint DefinitionId { get; }

    /// <summary>The character this container hangs off - the record's associated character guid.</summary>
    public ulong OwnerGuid { get; }

    /// <summary>The sheet row.</summary>
    public ContainerDefinition Definition { get; }

    /// <summary>Slot index -> item, ascending. Slot indices are 1-based and per container.</summary>
    public IReadOnlyDictionary<uint, InventoryItemInstance> Slots => _slots;

    /// <summary>Sum of the bulk of everything in this container.</summary>
    public int BulkUsed
    {
        get
        {
            int used = 0;
            foreach (InventoryItemInstance item in _slots.Values)
            {
                used += item.Fact.Bulk * (int)item.Count;
            }

            return used;
        }
    }

    /// <summary>
    /// The lowest free 1-based slot index. Indices are reused once an item leaves, because the
    /// client keys its per-container item collection on this number (docs/41 §5d).
    /// <c>ContainerDefinitions[117].MAXIMUM_SLOTS</c> is 9999, so bulk is the real cap.
    /// </summary>
    public uint NextFreeSlot()
    {
        uint slot = 1;
        while (_slots.ContainsKey(slot))
        {
            slot++;
        }

        return slot;
    }

    internal void Place(InventoryItemInstance item, uint slot)
    {
        _slots[slot] = item;
        item.ContainerGuid = Guid;
        item.ContainerDefinitionId = DefinitionId;
        item.ContainerSlotId = slot;
        item.LoadoutSlotId = 0;
        item.EquipmentSlotId = 0;
    }

    internal void Remove(InventoryItemInstance item)
    {
        if (item.ContainerGuid == Guid)
        {
            _slots.Remove(item.ContainerSlotId);
            item.ContainerGuid = 0;
            item.ContainerDefinitionId = 0;
            item.ContainerSlotId = 0;
        }
    }
}

/// <summary>
/// The authoritative result of selecting a hotbar tile. Loadout slots name UI tiles while body
/// slot 7 names the thing actually held; carrying both facts together keeps the switch atomic.
/// </summary>
public sealed record LoadoutSelection(
    uint PreviousLoadoutSlotId,
    uint SelectedLoadoutSlotId,
    InventoryItemInstance? PreviousHandItem,
    InventoryItemInstance SelectedItem,
    uint PreviousHandStowedBodySlot);

/// <summary>
/// The whole of one player's inventory: item instances, containers, loadout bindings and body-slot
/// attachments, plus the bulk arithmetic. Derivation: docs/41-inventory-slots.md §§2-5.
/// <para>
/// <b>The server is authoritative for bulk</b> (docs/41 §4c). <c>maxBulk</c> and <c>bulkUsed</c> are
/// fields of the container record the server sends; the client only renders them and gates local
/// drags. A ground pickup arrives as <c>Command.InteractRequest</c> and never passes through client
/// validation at all, so if this class does not refuse an over-capacity pickup, nothing will.
/// </para>
/// </summary>
public sealed class PlayerInventory
{
    private readonly Dictionary<ulong, InventoryItemInstance> _items = [];
    private readonly Dictionary<ulong, InventoryContainer> _containers = [];
    private readonly SortedDictionary<uint, InventoryItemInstance> _loadout = [];
    private readonly SortedDictionary<uint, InventoryItemInstance> _equipment = [];
    private readonly HashSet<uint> _managedEquipmentSlots = [];
    private readonly Func<ulong> _nextGuid;

    public PlayerInventory(ulong characterGuid, Func<ulong> guidAllocator, InventoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(guidAllocator);
        CharacterGuid = characterGuid;
        _nextGuid = guidAllocator;
        Options = options ?? new InventoryOptions();
    }

    /// <summary>The character these belong to.</summary>
    public ulong CharacterGuid { get; }

    /// <summary>The active KOTK survivor loadout (17).</summary>
    public uint LoadoutId { get; } = SurvivorLoadout.Id;

    public InventoryOptions Options { get; }

    /// <summary>Every item instance the client has been told about, by guid.</summary>
    public IReadOnlyDictionary<ulong, InventoryItemInstance> Items => _items;

    /// <summary>Every container on the character, by guid.</summary>
    public IReadOnlyDictionary<ulong, InventoryContainer> Containers => _containers;

    /// <summary>Loadout slot -> the item bound to it, ascending by slot.</summary>
    public IReadOnlyDictionary<uint, InventoryItemInstance> LoadoutSlots => _loadout;

    /// <summary>Body slot -> the item attached there, ascending by slot.</summary>
    public IReadOnlyDictionary<uint, InventoryItemInstance> EquipmentSlots => _equipment;

    /// <summary>
    /// Body slots whose visual state has become inventory-authoritative. A slot remains managed
    /// after its item is dropped or stowed so the wardrobe/starter baseline cannot silently put the
    /// removed garment back on the character during the next full dress.
    /// </summary>
    public IReadOnlySet<uint> ManagedEquipmentSlots => _managedEquipmentSlots;

    /// <summary>Whether the currently worn hoodie is raised. Hoodies always start lowered.</summary>
    public bool HoodUp { get; private set; }

    public Func<uint, uint>? SkinDefinition { get; init; }

    /// <summary>The base bag (container definition 117), or null before <see cref="Bootstrap"/>.</summary>
    public InventoryContainer? BaseBag { get; private set; }

    /// <summary>
    /// The item currently attached to body slot 7 (RHand) - what the player is holding, and what
    /// <c>EquipmentSlotRow(7, guid)</c> must name for it to be wieldable (docs/36 §W3).
    /// </summary>
    public ulong WieldedItemGuid =>
        _equipment.TryGetValue(BodySlots.RightHand, out InventoryItemInstance? held) ? held.Guid : 0;

    /// <summary>The currently selected hotbar tile, or zero before bootstrap.</summary>
    public uint CurrentLoadoutSlotId { get; private set; }

    /// <summary>
    /// Whether the RHand currently contains the required Fists item. Fists are the client's
    /// representation of an empty weapon hand, so the first real weapon may replace them even
    /// though <see cref="WieldedItemGuid"/> is non-zero.
    /// </summary>
    public bool HandIsFists =>
        _equipment.TryGetValue(BodySlots.RightHand, out InventoryItemInstance? held)
        && held.DefinitionId == SurvivorFistsItemDefinitionId;

    /// <summary>
    /// Create the character's own bag and, unless turned off, the Fists.
    /// <para>
    /// The bag is item <b>3156 "Inventory"</b> (class 25068, <c>PARAM1 = 117</c>), bound to loadout
    /// slot <b>43</b>, which is <c>FLAG_AUTO_EQUIP = 1</c> and <c>FLAG_IS_VISIBLE = 0</c>: a hidden,
    /// auto-equipped container item. The container it provides is definition 117, the only
    /// <c>IS_DYNAMIC_BULK</c> row in the sheet (docs/41 §4b).
    /// </para>
    /// </summary>
    public void Bootstrap()
    {
        if (BaseBag is not null)
        {
            return;
        }

        InventoryItemInstance bag = CreateInstance(ContainerDefinitionTable.BaseInventoryItemDefinitionId, 1);
        BindLoadout(bag, SurvivorLoadout.Inventory);
        BaseBag = CreateContainerFor(bag);

        if (Options.EquipFists)
        {
            // LoadoutSlots (17, 7): FLAG_REQUIRED = 1, FLAG_AUTO_EQUIP = 1, ITEM_ID = 85.
            InventoryItemInstance fists = CreateInstance(SurvivorFistsItemDefinitionId, 1);
            // August only proves the required Fists *loadout* binding.  C:\Z1's March-era
            // hand-reset helper also puts it in its equipment slot, but copying that wire meaning
            // produced a 94/01 body-slot-7 row here and locked the August client.  Keep the empty
            // hand as the client's baseline Weapon_Empty attachment until a real hotbar weapon is
            // selected.
            BindLoadout(fists, SurvivorLoadout.Fists);
            CurrentLoadoutSlotId = SurvivorLoadout.Fists;
        }

        // The August KOTK loadout has a dedicated key-5 tile for binoculars. Like fists it is a
        // Weapon-factory item, so WeaponSession supplies its class-specific ItemAdd tail.
        InventoryItemInstance binoculars = CreateInstance(SurvivorBinocularsItemDefinitionId, 1);
        BindLoadout(binoculars, SurvivorLoadout.Binoculars);

        // The reference spawn carries one four-count Field Bandage instance on Q even though the
        // ordinary pickup catalogue treats bandages as singletons. This is intentional starter
        // state, not a general stacking rule.
        InventoryItemInstance bandages = CreateInstance(StarterBandageItemDefinitionId, 4);
        BindLoadout(bandages, SurvivorLoadout.QuickUse1);

        // Give the clothes the character is already wearing real item instances, binding both the
        // inventory tile and body slot so dropping one removes its visible attachment too.
        foreach (uint definitionId in Options.StarterOutfit)
        {
            uint loadoutSlotId = InventoryAutoAssign.AutoEquipLoadoutSlot(definitionId, LoadoutId);
            if (loadoutSlotId == 0)
            {
                // No FLAG_AUTO_EQUIP slot of this loadout accepts the row's ITEM_CLASS. Granting it
                // anyway would put an item in the collection that the panel can never resolve, so
                // skip it rather than invent a slot.
                continue;
            }

            uint bodySlotId = LoadoutSlotTable.TryGet(
                    LoadoutId, loadoutSlotId, out LoadoutSlotDefinition slot)
                ? slot.EquipSlotId
                : 0;
            BindLoadout(CreateInstance(definitionId, 1), loadoutSlotId, bodySlotId);
        }
    }

    /// <summary>
    /// The container guid an item carries on the wire while it is <b>worn or wielded</b> rather than
    /// stored - the client's "equipped" sentinel.
    /// <para>
    /// docs/46 §3. <c>FUN_140da7b10</c> answers <em>"what is equipped in loadout slot S?"</em> by
    /// walking the item collection's definition-id index (<c>node+0x70</c>, chained at <c>+0x68</c>)
    /// and accepting a node only when <c>*(u64*)(node+0x20) == DAT_143ce43b0</c> <b>and</b>
    /// <c>*(int*)(node+0x2c) == S</c> - that is, the item's <c>ContainerGuid</c> is this constant and
    /// its <c>SlotId</c> is the <em>loadout</em> slot id. <c>DAT_143ce43b0</c> is the static constant
    /// <c>0xFFFFFFFFFFFFFFFF</c> with exactly four references in the whole image, all of them this
    /// one query (<c>0x140da7b43</c>, <c>0x140dba91a</c>, <c>0x140dbffb8</c>, <c>0x140dc0076</c>).
    /// <c>FUN_140dba880</c> is the same rule returning <c>item+0x10</c> - the guid - as its
    /// <em>output</em>, which is why the loadout record's own <c>itemGuid</c> is not the key.
    /// </para>
    /// <para>
    /// It is a <b>wire</b> value only: <see cref="InventoryItemInstance.ContainerGuid"/> stays 0 for
    /// a worn item, because the model uses it to mean "really inside a container" - which is also
    /// what makes <see cref="InventoryItemInstance.Bulk"/> zero for worn items.
    /// </para>
    /// </summary>
    public const ulong EquippedContainerGuid = 0xFFFF_FFFF_FFFF_FFFFUL;

    /// <summary>
    /// <c>ContainerDefinitions</c> row 101, the loadout/equipped pseudo-container. Capture-backed
    /// equipped item records pair this with <see cref="EquippedContainerGuid"/>.
    /// </summary>
    public const uint LoadoutContainerDefinitionId = 101;

    /// <summary>
    /// The capture-backed value in the otherwise unidentified bulk field of dynamic container 117.
    /// Static equipped-container records use zero.
    /// </summary>
    public const uint DynamicInventoryUnknownBulkField = 28;

    /// <summary>Item 85, the survivor loadout's default hand item.</summary>
    public const uint SurvivorFistsItemDefinitionId = 85;

    /// <summary>Item 1542, bound to loadout-17 slot 5 (the binocular hotbar tile).</summary>
    public const uint SurvivorBinocularsItemDefinitionId = 1542;

    /// <summary>Item 2423, seeded as four Field Bandages on quick-use Q.</summary>
    public const uint StarterBandageItemDefinitionId = 2423;

    /// <summary>
    /// The container's capacity as the client should display it.
    /// <para>
    /// A static container reports its sheet <c>MAX_BULK</c>. The dynamic bag (definition 117)
    /// reports <see cref="InventoryOptions.BaseCarryBulk"/> plus the <c>MAX_BULK</c> of every
    /// container <em>item</em> equipped on the character - which is what the client's own
    /// <c>Container::SetBulkSubsummed: maxBulk=%d</c> (<c>0x14360c358</c>) and
    /// <c>Container::ModifyMaxBulk</c> (<c>0x14360c388</c>) describe. Shirt +50, pants +50,
    /// satchel +300, backpack +1000, framed +1200, military +2000 - the figures of D26,
    /// reproduced from the client's own join of <c>ClientItemDefinitions.PARAM1</c> onto
    /// <c>ContainerDefinitions.MAX_BULK</c> (docs/41 §4a).
    /// </para>
    /// </summary>
    public int MaxBulkOf(InventoryContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (!container.Definition.IsDynamicBulk)
        {
            return container.Definition.MaxBulk;
        }

        int subsumed = Options.BaseCarryBulk;
        foreach (InventoryItemInstance worn in _loadout.Values)
        {
            if (worn.Guid == container.Guid)
            {
                // The bag never subsumes itself: its own PARAM1 is the dynamic row.
                continue;
            }

            subsumed += MaxBulkProvidedBy(worn.Fact);
        }

        return subsumed;
    }

    /// <summary>Bulk in use in the base bag, and its capacity. Convenience for logging.</summary>
    public (int Used, int Max) Capacity =>
        BaseBag is null ? (0, 0) : (BaseBag.BulkUsed, MaxBulkOf(BaseBag));

    /// <summary>
    /// Decide where <paramref name="itemDefinitionId"/> should go, without changing anything.
    /// </summary>
    public InventoryPlacement Plan(uint itemDefinitionId, uint count = 1) =>
        InventoryAutoAssign.Resolve(this, itemDefinitionId, count);

    /// <summary>
    /// Resolve and apply. On success <paramref name="instance"/> is the new (or merged) item, and
    /// the returned plan names every packet the caller must send (docs/41 §6b). On refusal the
    /// plan carries a <see cref="ContainerErrorCode"/> and nothing has changed.
    /// </summary>
    public InventoryPlacement TryPickUp(
        uint itemDefinitionId,
        uint count,
        out InventoryItemInstance? instance,
        bool preserveEquippedArmour = false)
    {
        InventoryPlacement plan = InventoryAutoAssign.Resolve(
            this, itemDefinitionId, count, preserveEquippedArmour);
        return ApplyPickup(itemDefinitionId, count, plan, out instance);
    }

    // The listener applies a validated destination plan without another auto-placement pass.
    internal InventoryPlacement ApplyPickup(uint itemDefinitionId, uint count,
        InventoryPlacement plan, out InventoryItemInstance? instance)
    {
        instance = null;
        switch (plan.Kind)
        {
            case InventoryPlacementKind.Refused:
                return plan;

            case InventoryPlacementKind.Stack:
                instance = _items[plan.StackTargetItemGuid];
                instance.Count += count;
                return plan;

            case InventoryPlacementKind.Container:
            {
                InventoryContainer container = _containers[plan.ContainerGuid];
                instance = CreateInstance(itemDefinitionId, count);
                container.Place(instance, plan.ContainerSlotId);
                return plan;
            }

            case InventoryPlacementKind.LoadoutSlot:
            {
                instance = CreateInstance(itemDefinitionId, count);
                if (plan.DisplacedItemGuid != 0 && BaseBag is not null)
                {
                    // The resolver either checked cargo room for the displaced item or scheduled
                    // a footwear drop. The caller publishes that ground item after this mutation.
                    InventoryItemInstance displaced = _items[plan.DisplacedItemGuid];
                    if (plan.GroundDrop is not null) RemoveUnits(displaced.Guid, 0);
                    else
                    {
                        Unbind(displaced);
                        BaseBag.Place(displaced, BaseBag.NextFreeSlot());
                    }
                }

                // Cargo continues to live in the one dynamic base bag. ToInitContainers separately
                // advertises an empty protocol container for every equipped EquippableContainer,
                // because the August client re-indexes those records by their loadout slots. Keeping
                // the declarations virtual here avoids splitting server-owned cargo across the
                // shirt/trousers/backpack records while still satisfying that client association.
                BindLoadout(instance, plan.LoadoutSlotId, plan.EquipmentSlotId);
                if (plan.Wielded)
                {
                    CurrentLoadoutSlotId = plan.LoadoutSlotId;
                }
                return plan;
            }

            default:
                return plan;
        }
    }

    /// <summary>Allocate an instance guid and register the item. No placement.</summary>
    public InventoryItemInstance CreateInstance(uint definitionId, uint count)
    {
        InventoryItemFacts.TryGet(definitionId, out InventoryItemFact fact);
        var instance = new InventoryItemInstance(_nextGuid(), definitionId, count, fact)
        {
            SkinDefinition = id => SkinDefinition?.Invoke(id) ?? id,
        };
        _items[instance.Guid] = instance;
        return instance;
    }

    /// <summary>
    /// Bind an item to a loadout slot and, when <paramref name="equipmentSlotId"/> is non-zero, to
    /// a body slot. Any previous occupant of either slot is unbound first.
    /// </summary>
    public void BindLoadout(InventoryItemInstance item, uint loadoutSlotId, uint equipmentSlotId = 0)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ContainerGuid != 0 && _containers.TryGetValue(item.ContainerGuid, out InventoryContainer? from))
        {
            from.Remove(item);
        }

        if (_loadout.TryGetValue(loadoutSlotId, out InventoryItemInstance? previous) && previous.Guid != item.Guid)
        {
            Unbind(previous);
        }

        _loadout[loadoutSlotId] = item;
        item.LoadoutSlotId = loadoutSlotId;

        if (equipmentSlotId != 0)
        {
            AttachEquipment(item, equipmentSlotId);
            if (equipmentSlotId == BodySlots.RightHand)
            {
                CurrentLoadoutSlotId = loadoutSlotId;
            }
        }
    }

    /// <summary>
    /// Select a real, wieldable occupant of a loadout-17 hotbar slot. A switch moves the old hand
    /// item to its normal passive body slot, then moves the selected item to RHand; the item's
    /// loadout binding never changes, so its UI tile remains stable throughout the switch.
    /// </summary>
    public bool TrySelectLoadoutSlot(uint loadoutSlotId, out LoadoutSelection? selection)
    {
        selection = null;
        if (!LoadoutSlotTable.TryGet(LoadoutId, loadoutSlotId, out LoadoutSlotDefinition slot)
            || !slot.Wheelable
            || !_loadout.TryGetValue(loadoutSlotId, out InventoryItemInstance? selected)
            || selected.Fact.ActiveEquipSlotId != BodySlots.RightHand)
        {
            return false;
        }

        uint previousSlotId = CurrentLoadoutSlotId;
        _equipment.TryGetValue(BodySlots.RightHand, out InventoryItemInstance? previous);
        CurrentLoadoutSlotId = loadoutSlotId;

        if (previous?.Guid == selected.Guid)
        {
            // Defensive cleanup for inventories made by the short-lived bootstrap that attached
            // Fists to RHand.  A Fists selection is loadout state only on this August wire.
            if (selected.DefinitionId == SurvivorFistsItemDefinitionId)
            {
                DetachEquipment(selected);
            }

            selection = new LoadoutSelection(
                previousSlotId,
                loadoutSlotId,
                previous,
                selected,
                PreviousHandStowedBodySlot: 0);
            return true;
        }

        // The selected item may currently be slung. Free that slot before resolving the former
        // hand's passive placement so an AK at 76 can swap cleanly with another AK.
        DetachEquipment(selected);

        uint previousStowedBodySlot = 0;
        if (previous is not null)
        {
            DetachEquipment(previous);

            // Fists are the empty-hand sentinel and are never rendered on the back. Binoculars
            // likewise have no passive slot. A real weapon follows the sheet's free-slot rule.
            if (previous.DefinitionId != SurvivorFistsItemDefinitionId)
            {
                var occupied = new HashSet<uint>(_equipment.Keys);
                previousStowedBodySlot = InventoryAutoAssign.PassiveBodySlot(previous.Fact, occupied);
                if (previousStowedBodySlot != 0)
                {
                    AttachEquipment(previous, previousStowedBodySlot);
                }
            }
        }

        // Fists is the empty-hand sentinel, not an August 94/01 RHand binding.  The client already
        // owns its Weapon_Empty attachment; emitting a body-slot-7 row for 85 is the regression
        // that locked movement/look after the inventory bootstrap.  A real selected weapon still
        // occupies the server's active hand and gets the narrow weapon visual path.
        if (selected.DefinitionId != SurvivorFistsItemDefinitionId)
        {
            AttachEquipment(selected, BodySlots.RightHand);
        }

        selection = new LoadoutSelection(
            previousSlotId,
            loadoutSlotId,
            previous,
            selected,
            previousStowedBodySlot);
        return true;
    }

    /// <summary>Pin an item's appearance without changing its gameplay definition or ownership.</summary>
    public bool TrySetItemSkin(ulong itemGuid, uint definitionId)
    {
        if (!_items.TryGetValue(itemGuid, out var item)
            || (definitionId != item.DefinitionId
                && (!Appearance.AugustWornSkins.TryGetCategory(item.DefinitionId, out uint category)
                    || !Appearance.AugustWornSkins.TryGetCategory(definitionId, out uint skinCategory)
                    || category != skinCategory)))
            return false;

        item.SkinOverrideDefinitionId = definitionId;
        if (item.EquipmentSlotId == BodySlots.Chest)
            HoodUp = false;
        return true;
    }

    /// <summary>
    /// Change the currently worn hoodie's posture. A non-hoodie chest item is refused, and raising
    /// is also refused while a hat or helmet occupies the head slot.
    /// </summary>
    public bool TrySetHood(bool up)
    {
        if (!_equipment.TryGetValue(BodySlots.Chest, out InventoryItemInstance? chest)
            || !ItemUseOptionTable.Allows(chest.DisplayDefinitionId, 96)
            || !ItemUseOptionTable.Allows(chest.DisplayDefinitionId, 97)
            || (up && _equipment.ContainsKey(BodySlots.Head)))
        {
            return false;
        }

        HoodUp = up;
        return true;
    }

    /// <summary>
    /// Take <paramref name="count"/> units of an instance out of the inventory - the model half of
    /// a drop or a consume. Removing the whole stack unbinds it from its loadout and body slots,
    /// frees its container slot and forgets the instance, so the guid can never be named again.
    /// Returns how many units actually left.
    /// <para>
    /// Freeing the container slot matters: <c>InventoryContainer.NextFreeSlot</c> reuses indices,
    /// because the client keys its per-container item collection on the slot number (docs/41 §5d),
    /// and a slot that is never released leaks a cell out of the grid for the rest of the match.
    /// </para>
    /// </summary>
    public uint RemoveUnits(ulong itemGuid, uint count)
    {
        if (!_items.TryGetValue(itemGuid, out InventoryItemInstance? item))
        {
            return 0;
        }

        uint taken = count == 0 || count > item.Count ? item.Count : count;
        if (taken < item.Count)
        {
            item.Count -= taken;
            return taken;
        }

        if (item.ContainerGuid != 0 && _containers.TryGetValue(item.ContainerGuid, out InventoryContainer? from))
        {
            from.Remove(item);
        }

        Unbind(item);
        _items.Remove(itemGuid);

        // A container item that is thrown away takes its container with it. Bootstrap's bag is the
        // one instance that can reach this line only through a caller that bypassed
        // InventoryActions, which refuses it (loadout slot 43).
        _containers.Remove(itemGuid);
        if (BaseBag is not null && BaseBag.Guid == itemGuid)
        {
            BaseBag = null;
        }

        return taken;
    }

    /// <summary>
    /// What <see cref="MaxBulkOf"/> would return for the base bag if the item bound to
    /// <paramref name="loadoutSlotId"/> were replaced by <paramref name="incomingDefinitionId"/>.
    /// <para>
    /// This is the capacity a <b>swap</b> has to be tested against, not the current one. Swapping a
    /// Military Backpack (<c>PARAM1 28</c>, <c>MAX_BULK 2000</c>) for a plain one
    /// (<c>PARAM1 22</c>, 1000) drops the bag's capacity by 1000 at the same instant the displaced
    /// military pack becomes 500 bulk of cargo inside it. Testing the displaced item against the
    /// capacity that is about to disappear let a near-full bag go over its own maximum with no
    /// <c>Container.Error</c> at all - the one thing docs/41 §4c says the server is the only
    /// authority for.
    /// </para>
    /// </summary>
    public int MaxBulkAfterSwap(uint loadoutSlotId, uint incomingDefinitionId)
    {
        if (BaseBag is null)
        {
            return 0;
        }

        if (!BaseBag.Definition.IsDynamicBulk)
        {
            return BaseBag.Definition.MaxBulk;
        }

        int subsumed = Options.BaseCarryBulk;
        foreach (KeyValuePair<uint, InventoryItemInstance> bound in _loadout)
        {
            if (bound.Value.Guid == BaseBag.Guid || bound.Key == loadoutSlotId)
            {
                continue;
            }

            subsumed += MaxBulkProvidedBy(bound.Value.Fact);
        }

        InventoryItemFacts.TryGet(incomingDefinitionId, out InventoryItemFact incoming);
        return subsumed + MaxBulkProvidedBy(incoming);
    }

    /// <summary>
    /// The <c>MAX_BULK</c> an item contributes to the dynamic bag: <c>ContainerDefinitions</c> row
    /// <c>PARAM1</c> of an <c>EquippableContainer</c> row, zero for anything else (docs/41 §4a).
    /// </summary>
    public static int MaxBulkProvidedBy(InventoryItemFact fact) =>
        fact.CodeFactory == ItemCodeFactory.EquippableContainer
        && ContainerDefinitionTable.TryGet(fact.Param1, out ContainerDefinition provided)
            ? provided.MaxBulk
            : 0;

    /// <summary>
    /// Put an item the character is holding into the base bag, unbinding it from any loadout or body
    /// slot first. The half of an unequip that touches the model.
    /// <para>
    /// <b>It performs no bulk test</b>, deliberately: the caller has already decided the item is
    /// going into the bag, and every caller that needs the test does it first
    /// (<c>ItemVerbs.Unequip</c> measures against <see cref="MaxBulkAfterSwap"/>, which is the
    /// capacity the move LEAVES BEHIND rather than the current one). A method that silently refused
    /// here would leave the item belonging nowhere at all, which is worse than an over-full bag.
    /// </para>
    /// </summary>
    /// <returns>False only when there is no bag to put it in.</returns>
    public bool TryStow(InventoryItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (BaseBag is not InventoryContainer bag)
        {
            return false;
        }

        Unbind(item);
        bag.Place(item, bag.NextFreeSlot());
        return true;
    }

    /// <summary>Reorder two equipped weapons without changing their hand, meshes or magazines.</summary>
    internal void ExchangeWeaponSlots(InventoryItemInstance first, InventoryItemInstance second)
    {
        uint a = first.LoadoutSlotId, b = second.LoadoutSlotId;
        if (a is not (1 or 2 or 4) || b is not (1 or 2 or 4) || a == b
            || !_loadout.TryGetValue(a, out var left) || !ReferenceEquals(left, first)
            || !_loadout.TryGetValue(b, out var right) || !ReferenceEquals(right, second))
            throw new InvalidOperationException("Weapon exchange no longer names two occupied slots.");
        _loadout[a] = second;
        _loadout[b] = first;
        first.LoadoutSlotId = b;
        second.LoadoutSlotId = a;
        if (CurrentLoadoutSlotId == a) CurrentLoadoutSlotId = b;
        else if (CurrentLoadoutSlotId == b) CurrentLoadoutSlotId = a;
    }

    /// <summary>Detach an item from its loadout and body slots. It then belongs nowhere.</summary>
    public void Unbind(InventoryItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool vacatedActiveHand = item.DefinitionId != SurvivorFistsItemDefinitionId
            && item.EquipmentSlotId == BodySlots.RightHand
            && _equipment.TryGetValue(BodySlots.RightHand, out InventoryItemInstance? held)
            && held.Guid == item.Guid;

        if (item.LoadoutSlotId != 0 && _loadout.TryGetValue(item.LoadoutSlotId, out InventoryItemInstance? bound)
            && bound.Guid == item.Guid)
        {
            _loadout.Remove(item.LoadoutSlotId);
        }

        if (item.EquipmentSlotId != 0
            && _equipment.TryGetValue(item.EquipmentSlotId, out InventoryItemInstance? attached)
            && attached.Guid == item.Guid)
        {
            _equipment.Remove(item.EquipmentSlotId);
        }

        if (item.EquipmentSlotId == BodySlots.Chest)
            HoodUp = false;
        item.LoadoutSlotId = 0;
        item.EquipmentSlotId = 0;

        // Dropping, unequipping or otherwise removing the drawn gun selects the required Fists
        // tile. Unlike the March reference, that must NOT manufacture an August body-slot-7 row:
        // the baseline empty-hand attachment remains client-owned.
        if (vacatedActiveHand)
        {
            RestoreFistsToHand();
        }
    }

    /// <summary>Detach only the body binding while preserving the item's loadout tile.</summary>
    private void DetachEquipment(InventoryItemInstance item)
    {
        if (item.EquipmentSlotId != 0
            && _equipment.TryGetValue(item.EquipmentSlotId, out InventoryItemInstance? attached)
            && attached.Guid == item.Guid)
        {
            _equipment.Remove(item.EquipmentSlotId);
        }

        item.EquipmentSlotId = 0;
    }

    /// <summary>Attach an already-owned loadout item to exactly one body slot.</summary>
    private void AttachEquipment(InventoryItemInstance item, uint equipmentSlotId)
    {
        DetachEquipment(item);

        if (_equipment.TryGetValue(equipmentSlotId, out InventoryItemInstance? wasThere)
            && wasThere.Guid != item.Guid)
        {
            wasThere.EquipmentSlotId = 0;
        }

        _equipment[equipmentSlotId] = item;
        item.EquipmentSlotId = equipmentSlotId;
        _managedEquipmentSlots.Add(equipmentSlotId);

        // A newly equipped chest garment always begins in its neutral posture, and any real head
        // item wins over a raised hood.
        if (equipmentSlotId is BodySlots.Chest or BodySlots.Head)
        {
            HoodUp = false;
        }
    }

    /// <summary>Restore the required Fists selection after a non-fist item vacates RHand.</summary>
    private void RestoreFistsToHand()
    {
        if (_loadout.TryGetValue(SurvivorLoadout.Fists, out InventoryItemInstance? fists)
            && fists.DefinitionId == SurvivorFistsItemDefinitionId)
        {
            // Clean up a pre-recovery Fists body binding if one reached this object.  Fists stays
            // in loadout slot 7 only; the August client owns the empty-hand mesh.
            DetachEquipment(fists);
            CurrentLoadoutSlotId = SurvivorLoadout.Fists;
            return;
        }

        CurrentLoadoutSlotId = 0;
    }

    /// <summary>
    /// Create the container a worn container item provides: <c>containerDefinitionId</c> from the
    /// item's <c>PARAM1</c>, keyed by the item's own instance guid (docs/41 §5e, lead L4).
    /// </summary>
    public InventoryContainer CreateContainerFor(InventoryItemInstance containerItem)
    {
        ArgumentNullException.ThrowIfNull(containerItem);
        if (!ContainerDefinitionTable.TryGet(containerItem.Fact.Param1, out ContainerDefinition definition))
        {
            throw new ArgumentException(
                $"Item {containerItem.DefinitionId} has PARAM1 {containerItem.Fact.Param1}, "
                + "which is not a ContainerDefinitions row.",
                nameof(containerItem));
        }

        var container = new InventoryContainer(
            containerItem.Guid, definition.Id, CharacterGuid, definition);
        _containers[container.Guid] = container;
        return container;
    }

    /// <summary>
    /// True when an item is one the August client's container re-indexer selects: an
    /// <c>EquippableContainer</c> whose <c>PARAM1</c> names a real container definition.
    /// </summary>
    public static bool ProvidesContainer(uint itemDefinitionId) =>
        InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact fact)
        && fact.CodeFactory == ItemCodeFactory.EquippableContainer
        && ContainerDefinitionTable.TryGet(fact.Param1, out _);

    /// <summary>Instance overload of <see cref="ProvidesContainer(uint)"/>.</summary>
    public static bool ProvidesContainer(InventoryItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return TryGetProvidedContainer(item, out _);
    }

    private static bool TryGetProvidedContainer(
        InventoryItemInstance item,
        out ContainerDefinition definition)
    {
        definition = default;
        return item.Fact.CodeFactory == ItemCodeFactory.EquippableContainer
            && ContainerDefinitionTable.TryGet(item.Fact.Param1, out definition);
    }

    // ---------------------------------------------------------------------------------------
    // Wire projection
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Project one container to its wire record (<c>FUN_140a37290</c>). The capture-backed item
    /// element key is the item definition id; the per-container slot remains in the item record.
    /// </summary>
    public ContainerRecord ToRecord(InventoryContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var items = new List<ContainerItemEntry>(container.Slots.Count);
        foreach (KeyValuePair<uint, InventoryItemInstance> slot in container.Slots)
        {
            items.Add(new ContainerItemEntry(
                slot.Value.DisplayDefinitionId,
                slot.Value.ToRecord(CharacterGuid)));
        }

        return new ContainerRecord(
            ContainerGuid: container.Guid,
            ContainerDefinitionId: container.DefinitionId,
            AssociatedCharacterGuid: CharacterGuid,
            SlotCount: (uint)container.Definition.MaximumSlots,
            Items: items,
            MaxBulk: (uint)Math.Max(0, MaxBulkOf(container)),
            BulkUsed: (uint)Math.Max(0, container.BulkUsed),
            FlagA: true,
            UnknownBulkField: container.DefinitionId == ContainerDefinitionTable.BaseInventoryDefinitionId
                ? DynamicInventoryUnknownBulkField
                : 0,
            FlagB: true);
    }

    /// <summary>
    /// Every item instance the client must be told about, in the order the <c>ClientUpdate.ItemAdd</c>
    /// burst has to send them: the bag first, then everything bound to a loadout slot ascending by
    /// slot, then every container resident.
    /// <para>
    /// docs/46 §7a condition 1: <b>an item that is not in the item collection cannot appear
    /// anywhere</b> - not in a panel box, not on the weapon wheel, not in the grid. A loadout
    /// binding (<c>86 04</c>/<c>86 05</c>) names a definition id and a slot, and the client resolves
    /// that pair against the collection; if the collection is empty the box stays empty however
    /// correct the binding is. That is why <see cref="Bootstrap"/>'s clothes need one
    /// <c>ItemAdd</c> each before <c>SetLoadoutSlots</c> goes out.
    /// </para>
    /// <para>
    /// Ordering rule (docs/36 §W3): every packet that names a guid comes after the <c>ItemAdd</c>
    /// that created it. A <b>worn</b> record carries <see cref="EquippedContainerGuid"/> rather than
    /// a container guid, so the bag and the loadout-bound items are safe on either side of the
    /// destructive <c>InitContainers</c>; docs/46 §7c puts them first so the <c>c8/02</c> re-index
    /// (<c>FUN_140d7bcb0</c>) runs with the items already present.
    /// </para>
    /// <para>
    /// <b>The third loop is the exception, and the earlier claim that "none of the bootstrap records
    /// names a container guid" was wrong</b> (verify wave 4): a container resident's record
    /// <em>does</em> name the bag guid (<c>InventoryItemInstance.ToRecord</c>, the
    /// <c>LoadoutSlotId == 0</c> branch), which is exactly what
    /// <c>InventoryDisplayTests.ItemGrantsIncludeContainerResidents</c> asserts. It is harmless only
    /// because <see cref="Bootstrap"/> leaves the bag empty; anything seeded into the bag would be
    /// granted against a container the client has not been told about yet. A caller that sends this
    /// list before <c>InitContainers</c> must keep that in mind.
    /// </para>
    /// </summary>
    public IReadOnlyList<InventoryItem> ToItemGrants()
    {
        var grants = new List<InventoryItem>(_items.Count);
        ulong bagGuid = BaseBag?.Guid ?? 0;
        if (bagGuid != 0 && _items.TryGetValue(bagGuid, out InventoryItemInstance? bagItem))
        {
            grants.Add(bagItem.ToRecord(CharacterGuid));
        }

        foreach (InventoryItemInstance worn in _loadout.Values)
        {
            if (worn.Guid != bagGuid)
            {
                grants.Add(worn.ToRecord(CharacterGuid));
            }
        }

        foreach (InventoryContainer container in _containers.Values.OrderBy(c => c.Guid))
        {
            foreach (KeyValuePair<uint, InventoryItemInstance> slot in container.Slots)
            {
                grants.Add(slot.Value.ToRecord(CharacterGuid));
            }
        }

        return grants;
    }

    /// <summary>
    /// The bootstrap grant set. By default it includes weapon-factory starter items because the
    /// caller serialises them through <c>WeaponSession</c>; the option can still filter them for a
    /// rollback capture.
    /// <para>
    /// Fists and binoculars require their class-specific ItemAdd tail. This method returns records;
    /// <c>ZoneService.EnsureInventory</c> is responsible for passing them to <c>WeaponSession</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<InventoryItem> ToBootstrapGrants()
    {
        IReadOnlyList<InventoryItem> all = ToItemGrants();
        if (Options.GrantWeaponItemsAtBootstrap)
        {
            return all;
        }

        var kept = new List<InventoryItem>(all.Count);
        foreach (InventoryItem grant in all)
        {
            if (_items.TryGetValue(grant.ItemGuid, out InventoryItemInstance? instance)
                && instance.Fact.CodeFactory == ItemCodeFactory.Weapon)
            {
                continue;
            }

            kept.Add(grant);
        }

        return kept;
    }

    /// <summary>
    /// The full container declaration for <c>Container.InitContainers</c> (0xc8/02). It contains
    /// one entry per equipped <c>EquippableContainer</c>, keyed by the provider's loadout slot. The
    /// base bag carries the cargo; shirt, trousers and backpack records are initially empty but are
    /// still required for the August client's loadout/container association pass.
    /// </summary>
    public InitContainers ToInitContainers()
    {
        var entries = new List<ContainerEntry>(_loadout.Count);
        foreach (KeyValuePair<uint, InventoryItemInstance> bound in _loadout)
        {
            InventoryItemInstance provider = bound.Value;
            if (!TryGetProvidedContainer(provider, out ContainerDefinition definition))
            {
                continue;
            }

            ContainerRecord record = _containers.TryGetValue(provider.Guid, out InventoryContainer? storage)
                ? ToRecord(storage)
                : new ContainerRecord(
                    ContainerGuid: provider.Guid,
                    ContainerDefinitionId: definition.Id,
                    AssociatedCharacterGuid: CharacterGuid,
                    SlotCount: (uint)Math.Max(0, definition.MaximumSlots),
                    Items: [],
                    MaxBulk: (uint)Math.Max(0, definition.MaxBulk),
                    BulkUsed: 0,
                    FlagA: true,
                    UnknownBulkField: definition.Id == ContainerDefinitionTable.BaseInventoryDefinitionId
                        ? DynamicInventoryUnknownBulkField
                        : 0,
                    FlagB: true);

            entries.Add(new ContainerEntry(bound.Key, record));
        }

        return new InitContainers(CharacterGuid, entries);
    }

    /// <summary>One container's delta, for <c>Container.UpdateContainer</c> (0xc8/06).</summary>
    public UpdateContainer ToUpdate(InventoryContainer container) =>
        new(CharacterGuid, ToRecord(container));

    /// <summary>
    /// The whole loadout, for <c>Loadouts.SetLoadoutSlots</c> (0x86/04) - and the same 29-byte
    /// element the self record's loadout list carries (<c>SelfRecord.cs:509</c>).
    /// </summary>
    public SetLoadoutSlots ToLoadoutSlots()
    {
        var entries = new List<LoadoutSlotEntry>();
        // Native CanMoveItem (140d7d650) requires the destination to exist in the
        // current slot map even when its item is empty. Omitting vacant slots makes
        // the client refuse an equip before sending Container.MoveItem.
        foreach (var slot in LoadoutSlotTable.Slots(LoadoutId))
        {
            entries.Add(new LoadoutSlotEntry(slot.SlotId,
                _loadout.TryGetValue(slot.SlotId, out var bound)
                    ? ToLoadoutRecord(bound)
                    : new LoadoutSlotRecord(LoadoutId, slot.SlotId, 0)));
        }

        return new SetLoadoutSlots(
            CharacterGuid,
            LoadoutId,
            entries,
            LoadoutSelectionRule.CurrentSlotFor(this));
    }

    /// <summary>One binding, for <c>Loadouts.SetLoadoutSlot</c> (0x86/05).</summary>
    public SetLoadoutSlot ToLoadoutSlot(InventoryItemInstance item) =>
        new(CharacterGuid, ToLoadoutRecord(item), LoadoutSelectionRule.CurrentSlotFor(this));

    /// <summary>
    /// docs/46 §3a: <c>ItemDefinitionId</c> is a hard gate, not an optional field. Both
    /// <c>FUN_140dc0150</c> (the <c>LoadoutSlotChanged</c> consumer) and <c>FUN_140d35250</c> (the
    /// weapon-wheel rebuild) early-out on <c>0 &lt; *(int*)(record+8)</c>, and the value is the
    /// definition-id key passed to <c>FUN_140da7b10</c>. Wave 3 wrote 0 here, which made every
    /// <c>86 04</c>/<c>86 05</c> it sent inert.
    /// </summary>
    private LoadoutSlotRecord ToLoadoutRecord(InventoryItemInstance item) =>
        new(LoadoutId, item.LoadoutSlotId, item.Guid, ItemDefinitionId: item.DisplayDefinitionId);
}
