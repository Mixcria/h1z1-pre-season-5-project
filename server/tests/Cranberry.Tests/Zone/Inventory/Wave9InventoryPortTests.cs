using Cranberry.Protocol;
using Cranberry.Zone.Crafting;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Inventory;

/// <summary>
/// Wave 9, the INVENTORY lane: the owner asked for his inventory to be ported straight over, and
/// these pin what crossed.
/// <para>
/// The lane's finding is that the inventory was not broken, it was <b>2/7ths built</b>. The August
/// client offers seven context-menu verbs across 2,345 items; Cranberry answered two, and one of
/// those ships off. The most-offered option in the whole build - <c>RemoveItem</c> (12), on 1,382
/// items, more than <c>DropItem</c>'s 1,365 - reached
/// <c>"no server behaviour is derived for this action yet"</c> and sent nothing. See docs/86.
/// </para>
/// <para>
/// <b>D29.</b> Every request these exercise is client-originated and therefore live; every reply is
/// BUILT and TESTED and has never been seen to land.
/// </para>
/// </summary>
public sealed class Wave9InventoryPortTests
{
    // The two items the owner's own session refused, host log 20260830-203615 L206/L608/L726.
    private const uint HisRefusedShirt = 2144;      // SurvivorStarterOutfit, ITEM_CLASS 25002
    private const uint HisRefusedBoots = 2613;      // SurvivorStarterOutfit, ITEM_CLASS 25005
    private const uint FoldedShirtModel = 9249;
    private const uint JedsModel = 10040;

    // Both helmets of his refused swap. BULK 250 each against a base carrier of 200.
    private const uint MotorcycleHelmet = 2168;
    private const uint TacticalHelmet = 2172;

    private const uint ScrapOfCloth = 23;           // ITEM_CLASS 16052 - no apparel row, no loot row
    private const uint Ammo762 = 2325;

    private const uint DropItemOption = 4;
    private const uint SalvageItemOption = 6;
    private const uint SalvageArmourOption = 63;
    private const uint UnloadWeaponOption = 7;
    private const uint RemoveItemOption = 12;
    private const uint EquipItemOption = 60;
    private const uint MoveItemOption = 61;

    private static PlayerInventory Fresh(InventoryOptions? options = null)
    {
        ulong next = 0x3100_0000_0000_0001;
        var inventory = new PlayerInventory(
            characterGuid: 4099,
            guidAllocator: () => next++,
            options: options ?? new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        return inventory;
    }

    private static RequestUseItem Request(
        PlayerInventory inventory, ulong itemGuid, uint option, uint count = 0) =>
        new(
            UnknownA: 1,
            ItemUseOptionId: option,
            CharacterGuid: inventory.CharacterGuid,
            SourceCharacterGuid: inventory.CharacterGuid,
            TargetCharacterGuid: inventory.CharacterGuid,
            ItemGuid: itemGuid,
            Simple: count == 0,
            Count: count,
            TrailingBytes: 0,
            ItemCount: 1);

    private static DroppedItemCatalogue Catalogue() => new(LootTables.LoadDefault());

    // =================================================================================
    // 1. The ground actor - his three-tier rule (docs/86 §2.1, §2.2)
    // =================================================================================

    /// <summary>
    /// docs/63 §5.4 claimed the only undroppable things were "the four starter garments and nothing
    /// else - everything a player can pick up in a match is by construction in these tables". It was
    /// wrong by a factor of two and a half: SIX of the ten uncatalogued items are things the server
    /// itself mints through crafting and shred, and the owner never saw them because he never got
    /// that far. Every one of them is now groundable.
    /// </summary>
    [Fact]
    public void EveryItemAReachableInventoryCanHoldNowHasAGroundActor()
    {
        DroppedItemCatalogue catalogue = Catalogue();

        var reachable = new List<uint>(catalogue.ItemDefinitionIds);
        reachable.AddRange(SurvivorStarterOutfit.DefaultItemDefinitionIds);
        reachable.AddRange(ShredTable.Yields.Values.Select(y => y.ItemDefinitionId));
        reachable.AddRange(
        [
            CraftingCatalog.ScrapOfCloth,
            CraftingCatalog.ArmorScrap,
            CraftingCatalog.CompositeFabric,
        ]);

        var refused = new List<uint>();
        foreach (uint id in reachable.Distinct())
        {
            if (!catalogue.TryGet(id, universalFallback: true, out uint model, out uint nameId, out _)
                || model == 0
                || nameId == 0)
            {
                refused.Add(id);
            }
        }

        Assert.Empty(refused);
    }

    /// <summary>
    /// The two drops his session actually refused. 2144 is <c>ITEM_CLASS</c> 25002 and 2613 is
    /// 25005, so under his rule they land on the client's own folded-shirt and boots props.
    /// </summary>
    [Fact]
    public void HisTwoRefusedGarmentsLandOnTheClientsOwnFoldedClothesProps()
    {
        DroppedItemCatalogue catalogue = Catalogue();

        Assert.True(catalogue.TryGet(
            HisRefusedShirt, true, out uint shirtModel, out uint shirtName, out GroundActorSource a));
        Assert.Equal(FoldedShirtModel, shirtModel);
        Assert.Equal(GroundActorSource.ApparelClass, a);
        Assert.Equal(8947u, shirtName);     // the item sheet's own NAME_ID, so the prompt names it

        Assert.True(catalogue.TryGet(
            HisRefusedBoots, true, out uint bootModel, out uint bootName, out GroundActorSource b));
        Assert.Equal(JedsModel, bootModel);
        Assert.Equal(GroundActorSource.ApparelClass, b);
        Assert.Equal(12610u, bootName);
    }

    /// <summary>
    /// The eight rows, pinned. If one of them ever renders as the wrong prop this is the single line
    /// to change (docs/86 §5.3), and this test is what stops it changing by accident. The backpack
    /// row is the one that cannot pass on a typo: 9706 is independently the ground model the loot
    /// tables already resolve for the sport backpacks.
    /// </summary>
    [Fact]
    public void EveryApparelGroundModelIsTheAugustModelsRowItClaims()
    {
        Assert.Equal(
            new Dictionary<uint, uint>
            {
                [25000] = 66,
                [25002] = 9249,
                [25003] = 9736,
                [25004] = 9706,
                [25005] = 10040,
                [25008] = 9491,
                [25040] = 10050,
                [25045] = 9504,
            },
            DroppedItemCatalogue.ApparelGroundModels);

        // Cross-check against the loot tables, which resolved their own ground models from the same
        // Models.txt independently of this table.
        DroppedItemCatalogue catalogue = Catalogue();
        uint backpack = catalogue.ItemDefinitionIds.First(id =>
            InventoryItemFacts.TryGet(id, out InventoryItemFact f) && f.ItemClass == 25004);
        Assert.True(catalogue.TryGet(backpack, out uint model, out _));
        Assert.Equal(DroppedItemCatalogue.ApparelGroundModels[25004], model);
    }

    /// <summary>
    /// Everything with no loot row and no apparel class lands on <c>Models.txt</c> row 9,
    /// <c>Common_Props_BurlapBag.adr</c> - his universal fallback. Scrap of Cloth is
    /// <c>ITEM_CLASS</c> 16052, a shred yield the server mints and the floor never carries.
    /// </summary>
    [Fact]
    public void AnItemWithNoLootRowAndNoApparelClassLandsOnTheBurlapBag()
    {
        Assert.True(Catalogue().TryGet(
            ScrapOfCloth, true, out uint model, out uint nameId, out GroundActorSource source));
        Assert.Equal(DroppedItemCatalogue.BurlapBagModel, model);
        Assert.Equal(GroundActorSource.BurlapBag, source);
        Assert.NotEqual(0u, nameId);
    }

    /// <summary>The revert switch really reverts: off, this is wave 8 exactly, refusal included.</summary>
    [Fact]
    public void UniversalGroundActorOffRestoresTheWave8Refusal()
    {
        Assert.False(Catalogue().TryGet(HisRefusedShirt, false, out _, out _, out _));
    }

    // =================================================================================
    // 2. The five dead verbs (docs/86 §2.3, §3.3)
    // =================================================================================

    /// <summary>
    /// The headline. Every <c>ItemUseOptions</c> row an item can legally carry now resolves either
    /// to a real action or to a <b>named</b> refusal - never to
    /// <see cref="ItemActionKind.NotImplemented"/>, which sent nothing at all and is what the owner
    /// experienced as "right-clicking a tile does nothing".
    /// </summary>
    [Fact]
    public void NoOptionTheClientCanOfferStillReachesNotImplemented()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(HisRefusedShirt, 1, out InventoryItemInstance? shirt);
        Assert.NotNull(shirt);

        var silent = new List<uint>();
        foreach (uint optionId in ItemUseOptionTable.OptionsForItem(HisRefusedShirt))
        {
            ItemActionResult plan = InventoryActions.Resolve(
                inventory, Request(inventory, shirt!.Guid, optionId));
            if (plan.Kind == ItemActionKind.NotImplemented)
            {
                silent.Add(optionId);
            }
        }

        // ConsumeItem is the one verb that may still be silent, and only because its switch ships
        // off: the effect lane owns the heal (docs/63 §5.1).
        Assert.All(silent, id => Assert.Equal(ItemUseOptionKind.ConsumeItem, ItemUseOptionTable.KindOf(id)));
    }

    /// <summary>Each of the five verbs wave 8 answered with silence, named individually.</summary>
    [Theory]
    [InlineData(RemoveItemOption)]
    [InlineData(MoveItemOption)]
    [InlineData(EquipItemOption)]
    [InlineData(SalvageItemOption)]
    [InlineData(SalvageArmourOption)]
    [InlineData(UnloadWeaponOption)]
    public void TheFiveDeadVerbsNoLongerGoSilent(uint optionId)
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(HisRefusedShirt, 1, out InventoryItemInstance? shirt);
        Assert.NotNull(shirt);

        // Straight to the verb layer, so the test names the verb rather than the item's own menu
        // group (a shirt carries no UnloadWeapon row, and the point here is the dispatcher arm).
        ItemUseOptionKind kind = ItemUseOptionTable.KindOf(optionId);
        ItemActionResult plan = kind switch
        {
            ItemUseOptionKind.RemoveItem => ItemVerbs.Remove(inventory, shirt!, kind),
            ItemUseOptionKind.MoveItem => ItemVerbs.Move(inventory, shirt!, kind),
            ItemUseOptionKind.EquipItem => ItemVerbs.Equip(inventory, shirt!, kind),
            ItemUseOptionKind.SalvageItem => ItemVerbs.Salvage(inventory, shirt!, kind, optionId),
            _ => InventoryActions.Resolve(inventory, Request(inventory, shirt!.Guid, optionId)),
        };

        Assert.NotEqual(ItemActionKind.NotImplemented, plan.Kind);
        Assert.NotEqual(string.Empty, plan.Rule);
        if (plan.Kind == ItemActionKind.Refused)
        {
            Assert.NotEqual(ContainerErrorCode.None, plan.Error);
        }
    }

    /// <summary>
    /// His rule, and the one that stops the tile snapping back: an item already where the client
    /// asked for it is answered with a repaint, not with silence.
    /// </summary>
    [Fact]
    public void RemoveItemOnABaggedItemRepaintsRatherThanGoingSilent()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? ammo);
        Assert.NotNull(ammo);
        Assert.NotEqual(0ul, ammo!.ContainerGuid);      // it is in the bag, not worn

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, ammo.Guid, RemoveItemOption));

        Assert.Equal(ItemActionKind.Repaint, plan.Kind);
        Assert.Equal(30u, inventory.Items[ammo.Guid].Count);     // and nothing was destroyed
    }

    /// <summary>
    /// docs/63 §5.3 concluded <c>MoveItem</c> must ride a different sub because no packet form has
    /// room for a destination. Falsified: it carries no destination because it is a reciprocal
    /// toggle. Worn goes to the bag; bagged-and-equippable goes back to the loadout.
    /// </summary>
    [Fact]
    public void MoveItemIsReciprocal()
    {
        // A helmet is BULK 250 and the base carrier is 100, so a player with no backpack cannot put
        // one in his bag at all - his TryUnequip refuses it for exactly the same reason. Give this
        // one the room, because the point under test is the TOGGLE, not the bulk rule;
        // TakingAHelmetOffWithNoBackpackIsRefusedForBulk pins the other half.
        PlayerInventory inventory = Fresh(
            new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1000 });
        inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);
        Assert.NotNull(helmet);
        uint headSlot = helmet!.LoadoutSlotId;
        Assert.NotEqual(0u, headSlot);

        ItemActionResult off = InventoryActions.Perform(
            inventory, Request(inventory, helmet.Guid, MoveItemOption));
        Assert.Equal(ItemActionKind.Unequip, off.Kind);
        Assert.Equal(headSlot, off.ClearedLoadoutSlotId);
        Assert.False(inventory.LoadoutSlots.ContainsKey(headSlot));
        Assert.NotEqual(0ul, inventory.Items[helmet.Guid].ContainerGuid);

        ItemActionResult on = InventoryActions.Perform(
            inventory, Request(inventory, helmet.Guid, MoveItemOption));
        Assert.Equal(ItemActionKind.Equip, on.Kind);
        Assert.Equal(headSlot, on.BoundLoadoutSlotId);
        Assert.Equal(helmet.Guid, inventory.LoadoutSlots[headSlot].Guid);
        Assert.Equal(0ul, inventory.Items[helmet.Guid].ContainerGuid);
    }

    /// <summary>
    /// <b>A consequence the owner should hear before he tests it.</b> Every helmet in the August
    /// sheet is <c>BULK</c> 250 and the base carrier is 100 (+50 shirt +50 pants), so until a
    /// backpack is found a helmet <em>cannot be taken off into the bag</em> - and that is his own
    /// server's behaviour too (<c>ZoneInventoryActions.TryUnequip</c> refuses on
    /// <c>HasBulkSpace</c>). The difference wave 9 makes is that the refusal now says so instead of
    /// closing the menu; the way to get rid of a helmet without a backpack is Drop, which now works
    /// for every item.
    /// </summary>
    [Fact]
    public void TakingAHelmetOffWithNoBackpackIsRefusedForBulkAndSaysSo()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);
        Assert.NotNull(helmet);
        Assert.NotEqual(0u, helmet!.LoadoutSlotId);

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, helmet.Guid, RemoveItemOption));

        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        Assert.Contains("no room", plan.Rule);
        Assert.Equal(helmet.Guid, inventory.LoadoutSlots[helmet.LoadoutSlotId].Guid);

        // But it can always be DROPPED, which is the wave-9 fix: a helmet has a real loot-table
        // ground actor, so this was never the refused case - and even if it were, the fallback is.
        ItemActionResult drop = InventoryActions.Resolve(
            inventory, Request(inventory, helmet.Guid, DropItemOption));
        Assert.Equal(ItemActionKind.Drop, drop.Kind);
    }

    /// <summary>
    /// An <c>EquipItem</c> on something already worn, and a <c>RemoveItem</c> on something already
    /// bagged, are both repaints. Neither may destroy or move anything.
    /// </summary>
    [Fact]
    public void AVerbThatAsksForWhereTheItemAlreadyIsChangesNothing()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);
        Assert.NotNull(helmet);
        uint slot = helmet!.LoadoutSlotId;

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, helmet.Guid, EquipItemOption));

        Assert.Equal(ItemActionKind.Repaint, plan.Kind);
        Assert.Equal(helmet.Guid, inventory.LoadoutSlots[slot].Guid);
    }

    // =================================================================================
    // 3. The helmet swap he was refused (docs/86 §3.4)
    // =================================================================================

    /// <summary>
    /// His log, verbatim: <c>loadout slot 11 holds item 2172 and the bag has no room for it
    /// (displaced, but bulk 180 + 250 exceeds 200)</c>. Both helmets are BULK 250 against a base
    /// carrier of 200, so <b>no helmet could ever be displaced into a Cranberry bag before a
    /// backpack was found</b> - the first helmet you picked up was the last one you could have. Z1's
    /// displacement path applies no bulk test at all; this takes his rule and puts the overflow on
    /// the ground rather than over-filling the bag, which is only possible because the ground actor
    /// is now total.
    /// </summary>
    [Fact]
    public void SwappingAHelmetWithAFullBagPutsTheOldOneOnTheGroundInsteadOfRefusing()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { StarterOutfit = [] });
        inventory.TryPickUp(TacticalHelmet, 1, out InventoryItemInstance? worn);
        Assert.NotNull(worn);
        uint headSlot = worn!.LoadoutSlotId;

        // The second helmet has to be IN THE BAG for an EquipItem to name it, and a helmet is 250
        // bulk against a 100 base carrier - so the bag can hold neither of them. That is precisely
        // the state his session was in.
        InventoryItemInstance spare = inventory.CreateInstance(MotorcycleHelmet, 1);
        Assert.True(inventory.TryStow(spare));

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, spare.Guid, EquipItemOption));

        Assert.Equal(ItemActionKind.Equip, plan.Kind);
        Assert.Equal(headSlot, plan.BoundLoadoutSlotId);
        Assert.Equal(worn.Guid, plan.DisplacedItemGuid);
        Assert.True(plan.DisplacedToGround);
        Assert.Equal(TacticalHelmet, plan.DisplacedDefinitionId);

        // The new helmet is worn, the old one has left the inventory entirely, and it is groundable.
        Assert.Equal(spare.Guid, inventory.LoadoutSlots[headSlot].Guid);
        Assert.DoesNotContain(worn.Guid, inventory.Items.Keys);
        Assert.True(Catalogue().TryGet(TacticalHelmet, true, out uint model, out _, out _));
        Assert.NotEqual(0u, model);
    }

    /// <summary>The revert switch: off, his L463 refusal comes back exactly.</summary>
    [Fact]
    public void SwapNeverRefusedForBulkOffRestoresTheRefusal()
    {
        PlayerInventory inventory = Fresh(
            new InventoryOptions { StarterOutfit = [], SwapNeverRefusedForBulk = false });
        inventory.TryPickUp(TacticalHelmet, 1, out InventoryItemInstance? worn);
        Assert.NotNull(worn);

        InventoryItemInstance spare = inventory.CreateInstance(MotorcycleHelmet, 1);
        Assert.True(inventory.TryStow(spare));

        ItemActionResult plan = InventoryActions.Perform(
            inventory, Request(inventory, spare.Guid, EquipItemOption));

        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.InteractionValidationFailed, plan.Error);
        Assert.Equal(worn!.Guid, inventory.LoadoutSlots[worn.LoadoutSlotId].Guid);
    }

    // =================================================================================
    // 4. Shred (docs/86 §3.2)
    // =================================================================================

    /// <summary>
    /// The cheapest high-value fix in the lane: no new packet and no new derivation, only two wave-6
    /// documents joined. <c>ShredTable.Shred</c> has been green since wave 6; the owner shredded his
    /// worn shirt four times on 30 Aug and got a "no server behaviour is derived" line each time.
    /// </summary>
    [Fact]
    public void ShreddingHisWornShirtProducesFourScrapsOfCloth()
    {
        PlayerInventory inventory = Fresh(
            new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1000 });
        inventory.TryPickUp(HisRefusedShirt, 1, out InventoryItemInstance? shirt);
        Assert.NotNull(shirt);

        ItemActionResult plan = InventoryActions.Resolve(
            inventory, Request(inventory, shirt!.Guid, SalvageItemOption));

        Assert.Equal(ItemActionKind.Shred, plan.Kind);

        CraftOutcome outcome = ShredTable.Shred(inventory, plan.ItemGuid);

        Assert.True(outcome.Succeeded);
        Assert.Equal(4u, CraftingService.CountAvailable(inventory, CraftingCatalog.ScrapOfCloth));
        Assert.DoesNotContain(shirt.Guid, inventory.Items.Keys);
    }

    /// <summary>
    /// The busy window is the client's own <c>BUSY_MSEC</c> for the option that was clicked, not a
    /// constant - and where his 1087 table and the August sheet disagree, <b>August wins</b>. Option
    /// 63 is 1000 in <c>ItemUseOptions.txt</c> and 3000 in his <c>ClientUseOptions</c>.
    /// </summary>
    [Theory]
    [InlineData(SalvageItemOption, 1000)]
    [InlineData(SalvageArmourOption, 1000)]
    public void TheShredBusyWindowIsTheAugustSheetsOwnBusyMsec(uint optionId, int expected)
    {
        PlayerInventory inventory = Fresh(
            new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1000 });
        inventory.TryPickUp(HisRefusedShirt, 1, out InventoryItemInstance? shirt);
        Assert.NotNull(shirt);

        ItemActionResult plan = ItemVerbs.Salvage(
            inventory, shirt!, ItemUseOptionKind.SalvageItem, optionId);

        Assert.Equal(expected, plan.BusyMilliseconds);
        Assert.True(ItemUseOptionTable.TryGet(optionId, out ItemUseOptionDefinition row));
        Assert.Equal(row.BusyMsec, plan.BusyMilliseconds);
    }

    /// <summary>
    /// A worn shred that cannot fit its own yield must put the garment back <b>on the body</b>, not
    /// leave it in the bag that just proved it had no room. The stow is how the worn item reaches
    /// the single bagged code path; the rollback has to undo it too.
    /// </summary>
    [Fact]
    public void AWornShredWithNoRoomForTheYieldPutsTheGarmentBackOnTheBody()
    {
        // A carrier of exactly the shirt's own bulk: the shirt fits while worn (a worn item is 0
        // cargo), and the two Scrap of Cloth the shred would produce do not.
        PlayerInventory inventory = Fresh(new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 1 });
        inventory.TryPickUp(HisRefusedShirt, 1, out InventoryItemInstance? shirt);
        Assert.NotNull(shirt);

        uint wasInSlot = shirt!.LoadoutSlotId;
        uint wasOnBody = shirt.EquipmentSlotId;
        if (wasInSlot == 0)
        {
            return;             // auto-assign bagged it; there is no worn rollback to test
        }

        CraftOutcome outcome = ShredTable.Shred(inventory, shirt.Guid);

        Assert.False(outcome.Succeeded);
        Assert.Equal(shirt.Guid, inventory.LoadoutSlots[wasInSlot].Guid);
        Assert.Equal(wasInSlot, inventory.Items[shirt.Guid].LoadoutSlotId);
        Assert.Equal(wasOnBody, inventory.Items[shirt.Guid].EquipmentSlotId);
        Assert.Equal(0ul, inventory.Items[shirt.Guid].ContainerGuid);
    }

    /// <summary>
    /// Something with no shred yield answers with a reason, not with a destroyed item.
    /// <para>
    /// <b>D260 moved the example.</b> This used to shred 7.62×39 ammunition and assert a refusal;
    /// the client's own <c>ItemUseOptions</c> group for that item <em>offers</em> Shred (option 87),
    /// so the refusal was the defect, not the rule. Body armour is the case that is genuinely
    /// unshreddable: no <c>SalvageItem</c> row anywhere touches class 25041, which is exactly why
    /// the client calls Armor Scrap the remnant of a <em>destroyed</em> helmet.
    /// </para>
    /// </summary>
    [Fact]
    public void ShreddingSomethingWithNoYieldIsRefusedByName()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(CraftingCatalog.MakeshiftArmor, 1, out InventoryItemInstance? armour);
        Assert.NotNull(armour);

        ItemActionResult plan = ItemVerbs.Salvage(
            inventory, armour!, ItemUseOptionKind.SalvageItem, SalvageItemOption);

        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        Assert.Equal(ContainerErrorCode.WrongItemType, plan.Error);
        Assert.Equal(1u, inventory.Items[armour!.Guid].Count);
    }

    /// <summary>
    /// <b>D260, the other half.</b> Ammunition IS offered Shred by the client (option 87), so it
    /// shreds - into Gunpowder, this build's own ammunition material. With
    /// <see cref="InventoryOptions.ShredEveryOfferedItem"/> off, the wave-9 refusal comes back.
    /// </summary>
    [Fact]
    public void ShreddingAmmunitionIsAnsweredRatherThanRefused()
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Ammo762, 30, out InventoryItemInstance? ammo);
        Assert.NotNull(ammo);

        ItemActionResult plan = ItemVerbs.Salvage(
            inventory, ammo!, ItemUseOptionKind.SalvageItem, SalvageItemOption);

        Assert.Equal(ItemActionKind.Shred, plan.Kind);
        Assert.True(ShredTable.IsShreddable(Ammo762, out RecipeIngredient yield));
        Assert.Equal(CraftingCatalog.Gunpowder, yield.ItemDefinitionId);
        Assert.False(
            ShredTable.IsShreddable(Ammo762, out _, everyOfferedItem: false));
    }

    // =================================================================================
    // 5. The 75-byte typed parameter block (docs/86 §2.5)
    // =================================================================================

    /// <summary>
    /// docs/63 §1 carried four <c>[LEAD]</c> words in this packet's tail. They are not leads: his
    /// <c>TryReadStackSize</c> reads the same bytes as <c>u32 entries</c> then
    /// <c>entries x {u32 paramId, u32 value}</c>, and his own boot self-check labels the sixteen
    /// zero bytes that follow as four further empty typed lists. Cranberry's fixed
    /// <c>CountOffset = 55</c> is the <c>entries = 1, paramId = 1</c> special case, so
    /// <see cref="RequestUseItem.Count"/> reads exactly what it read in wave 8.
    /// </summary>
    [Fact]
    public void TheStackAtClickBlockIsATypedParamListNotThreeUnknownWords()
    {
        // The 120-round sample of docs/63 §1: item 1429, a 4 x 30 stack the owner tried to drop.
        byte[] packet = QuantityForm(DropItemOption, characterGuid: 4099, itemGuid: 0x3100000000000009, stack: 120);

        Assert.Equal(RequestUseItem.QuantityFormLength, packet.Length);
        RequestUseItem parsed = RequestUseItem.Parse(packet);

        Assert.False(parsed.Simple);
        Assert.Equal(1u, parsed.ItemCount);
        Assert.Equal(0u, parsed.ReservedA);
        Assert.True(parsed.HasStackAtClick);
        Assert.Equal(120u, parsed.StackAtClick);
        Assert.Equal(120u, parsed.Count);                       // unchanged from wave 8
        Assert.Equal(0, parsed.TrailingBytes);

        ItemUseParameter only = Assert.Single(parsed.Parameters!);
        Assert.Equal(RequestUseItem.StackSizeParameterId, only.ParameterId);
        Assert.Equal(120u, only.Value);
    }

    /// <summary>
    /// The general form his parser handles and Cranberry's fixed offset could not: more than one
    /// entry, with the stack size not first. Nothing in the captures looks like this - the point is
    /// that it no longer reads a stack size out of the wrong word if one ever does.
    /// </summary>
    [Fact]
    public void ATypedBlockWithTwoEntriesIsReadByParamIdNotByOffset()
    {
        using var w = new PacketWriter();
        w.WriteByte(ItemUseOpcodes.ItemsBase);
        w.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        w.WriteUInt32(1);                       // itemCount
        w.WriteUInt32(0);                       // reservedA
        w.WriteUInt32(DropItemOption);
        w.WriteUInt64(4099);
        w.WriteUInt64(4099);
        w.WriteUInt64(4099);
        w.WriteUInt64(0x3100000000000009);
        w.WriteByte(0);                         // noParams = 0
        w.WriteUInt32(2);                       // TWO entries
        w.WriteUInt32(7);
        w.WriteUInt32(999);                     // a parameter this server has never seen
        w.WriteUInt32(RequestUseItem.StackSizeParameterId);
        w.WriteUInt32(42);

        RequestUseItem parsed = RequestUseItem.Parse(w.Written.ToArray());

        Assert.Equal(2, parsed.Parameters!.Count);
        Assert.Equal(42u, parsed.StackAtClick);
        Assert.Equal(42u, parsed.Count);        // and NOT 999, which is where offset 55 pointed
    }

    /// <summary>The 47-byte form still parses, still means "all of it", and carries no block.</summary>
    [Fact]
    public void TheShortFormIsUnchanged()
    {
        using var w = new PacketWriter();
        w.WriteByte(ItemUseOpcodes.ItemsBase);
        w.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        w.WriteUInt32(1);
        w.WriteUInt32(0);
        w.WriteUInt32(RemoveItemOption);
        w.WriteUInt64(4099);
        w.WriteUInt64(4099);
        w.WriteUInt64(4099);
        w.WriteUInt64(0x3100000000000009);
        w.WriteByte(1);

        byte[] bytes = w.Written.ToArray();
        Assert.Equal(RequestUseItem.MinimumLength, bytes.Length);

        RequestUseItem parsed = RequestUseItem.Parse(bytes);
        Assert.True(parsed.Simple);
        Assert.Equal(0u, parsed.Count);
        Assert.False(parsed.HasStackAtClick);
        Assert.Empty(parsed.Parameters!);
        Assert.Equal(ItemUseOptionKind.RemoveItem, parsed.Kind);
    }

    private static byte[] QuantityForm(uint option, ulong characterGuid, ulong itemGuid, uint stack)
    {
        using var w = new PacketWriter();
        w.WriteByte(ItemUseOpcodes.ItemsBase);
        w.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        w.WriteUInt32(1);                       // itemCount
        w.WriteUInt32(0);                       // reservedA
        w.WriteUInt32(option);
        w.WriteUInt64(characterGuid);
        w.WriteUInt64(characterGuid);
        w.WriteUInt64(characterGuid);
        w.WriteUInt64(itemGuid);
        w.WriteByte(0);                         // noParams = 0
        w.WriteUInt32(1);                       // one entry
        w.WriteUInt32(RequestUseItem.StackSizeParameterId);
        w.WriteUInt32(stack);
        w.WriteUInt32(0);                       // intParamsB
        w.WriteUInt32(0);                       // qwordParams
        w.WriteUInt32(0);                       // vectorParams
        w.WriteUInt32(0);                       // stringParams
        return w.Written.ToArray();
    }

    // =================================================================================
    // 6. The guards, re-pinned across the new verbs
    // =================================================================================

    /// <summary>
    /// <b>Regression guard 5, over every verb this lane added.</b> Body slot 7 is the active hand,
    /// and an <c>EquipmentSlotRow</c> naming it hard-crashes the August client while no fire-group
    /// data is on the wire. No context menu may create an RHand body binding while Fists is the
    /// required selected tile; a weapon equipped from the panel is STOWED, exactly as a picked-up
    /// one is with <c>WieldFirstWeapon</c> off.
    /// </summary>
    [Fact]
    public void NoNewVerbCreatesAnActiveHandBinding()
    {
        PlayerInventory inventory = Fresh();
        InventoryItemInstance fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];

        foreach (InventoryItemFact fact in InventoryItemFacts.All.Where(f => f.CanEquip).Take(400))
        {
            InventoryItemInstance held = inventory.CreateInstance(fact.DefinitionId, 1);
            Assert.True(inventory.TryStow(held));

            ItemActionResult plan = ItemVerbs.Equip(inventory, held, ItemUseOptionKind.EquipItem);

            Assert.NotEqual(7u, plan.BoundEquipmentSlotId);
            Assert.NotEqual(7u, plan.BoundLoadoutSlotId);

            ItemVerbs.Apply(inventory, plan);
            Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
            Assert.Equal(0u, fists.EquipmentSlotId);
            Assert.Equal(0ul, inventory.WieldedItemGuid);
            Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));

            // Put the model back where it started so the sweep tests one item at a time.
            inventory.RemoveUnits(held.Guid, 0);
            if (plan.DisplacedItemGuid != 0)
            {
                inventory.RemoveUnits(plan.DisplacedItemGuid, 0);
            }
        }
    }

    /// <summary>
    /// The bag and the fists are not the player's to take off, whichever verb asks. Loadout slot 43
    /// is the hidden carrier and slot 7 is <c>FLAG_REQUIRED</c> with <c>ITEM_ID</c> 85.
    /// </summary>
    [Theory]
    [InlineData(RemoveItemOption)]
    [InlineData(MoveItemOption)]
    [InlineData(DropItemOption)]
    public void TheBagAndTheFistsAreRefusedByEveryVerb(uint optionId)
    {
        PlayerInventory inventory = Fresh();

        foreach (uint slot in new[] { SurvivorLoadout.Inventory, SurvivorLoadout.Fists })
        {
            if (!inventory.LoadoutSlots.TryGetValue(slot, out InventoryItemInstance? held))
            {
                continue;
            }

            ItemUseOptionKind kind = ItemUseOptionTable.KindOf(optionId);
            ItemActionResult plan = kind switch
            {
                ItemUseOptionKind.RemoveItem => ItemVerbs.Remove(inventory, held, kind),
                ItemUseOptionKind.MoveItem => ItemVerbs.Move(inventory, held, kind),
                _ => InventoryActions.Resolve(inventory, Request(inventory, held.Guid, optionId)),
            };

            Assert.Equal(ItemActionKind.Refused, plan.Kind);
            Assert.Equal(held.Guid, inventory.LoadoutSlots[slot].Guid);
        }
    }

    /// <summary>
    /// A weapon cannot go in a KOTK bag - every gun in the sheet is BULK 1500 against a carrier of
    /// 200 - and his refusal says so in words the player can act on rather than closing the menu.
    /// That is not a defect and never will be; it is why loadout slots 1/2/4 exist.
    /// </summary>
    [Fact]
    public void UnequippingAWeaponIntoAFullBagIsRefusedWithHisReason()
    {
        PlayerInventory inventory = Fresh();
        uint rifle = InventoryItemFacts.All
            .First(f => f.CodeFactory == ItemCodeFactory.Weapon && f.Bulk > 200 && f.CanEquip)
            .DefinitionId;
        inventory.TryPickUp(rifle, 1, out InventoryItemInstance? gun);
        Assert.NotNull(gun);

        if (gun!.LoadoutSlotId == 0)
        {
            return;         // auto-assign bagged it; nothing to unequip
        }

        ItemActionResult plan = ItemVerbs.Unequip(inventory, gun, ItemUseOptionKind.RemoveItem);

        Assert.Equal(ItemActionKind.Refused, plan.Kind);
        Assert.Contains("weapon slots are for", plan.Rule);
        Assert.Equal(gun.Guid, inventory.LoadoutSlots[gun.LoadoutSlotId].Guid);
    }

    /// <summary>
    /// A verb switched off must go back to sending nothing rather than to sending something wrong.
    /// This is the revert path the owner would use if any of the five new verbs misbehaves live.
    /// </summary>
    [Fact]
    public void EveryNewVerbHasAWorkingRevertSwitch()
    {
        var off = new InventoryOptions
        {
            StarterOutfit = [],
            AnswerRemoveItem = false,
            AnswerMoveItem = false,
            AnswerEquipItem = false,
            AnswerSalvageItem = false,
            AnswerUnloadWeapon = false,
        };
        PlayerInventory inventory = Fresh(off);
        inventory.TryPickUp(HisRefusedShirt, 1, out InventoryItemInstance? shirt);
        Assert.NotNull(shirt);

        foreach (uint optionId in new[] { RemoveItemOption, MoveItemOption, EquipItemOption, SalvageItemOption })
        {
            ItemActionResult plan = InventoryActions.Perform(
                inventory, Request(inventory, shirt!.Guid, optionId));
            Assert.Equal(ItemActionKind.NotImplemented, plan.Kind);
        }

        // And nothing moved.
        Assert.True(inventory.Items.ContainsKey(shirt!.Guid));
    }

    /// <summary>
    /// The defaults are ON. The owner should not have to set an environment variable to right-click
    /// a tile; a switch here is for reverting, not for opting in.
    /// </summary>
    [Fact]
    public void EveryVerbTheOwnerAskedForIsOnByDefault()
    {
        var defaults = new InventoryOptions();

        Assert.True(defaults.AnswerDropItem);
        Assert.True(defaults.AnswerRemoveItem);
        Assert.True(defaults.AnswerMoveItem);
        Assert.True(defaults.AnswerEquipItem);
        Assert.True(defaults.AnswerSalvageItem);
        Assert.True(defaults.AnswerUnloadWeapon);
        Assert.True(defaults.UniversalGroundActor);
        Assert.True(defaults.SwapNeverRefusedForBulk);

        Assert.Contains("cf 02", InventoryOptions.ShredInteractionProtocol);
        Assert.Contains("67 bytes", InventoryOptions.ShredInteractionProtocol);
        Assert.True(defaults.AnswerConsumeItem);    // D341: the cast-and-heal exists now
        Assert.False(defaults.ConsumeHealsThroughResourceEvent);
    }
}
