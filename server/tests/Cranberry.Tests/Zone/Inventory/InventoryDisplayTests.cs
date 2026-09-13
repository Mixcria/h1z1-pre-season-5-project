using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

/// <summary>
/// docs/46 — the inventory <em>display</em>: why the owner saw a dressed character with an empty
/// panel, and the three conditions that put an item in a panel box.
/// <para>
/// docs/46 §7a, stated once: an item appears in loadout-17 box <c>S</c> iff (1) it exists in the
/// client's item collection — one <c>ClientUpdate.ItemAdd</c> — and (2) that record carries
/// <c>ContainerGuid = 0xFFFFFFFFFFFFFFFF</c> and <c>SlotId = S</c>, and (3) a <c>86 04</c>/<c>86 05</c>
/// record exists with <c>loadoutId = 17</c>, <c>slotId = S</c> and a <b>non-zero</b> item definition
/// id at <c>+0x08</c>. Wave 3 satisfied none of the three; each one is pinned below.
/// </para>
/// <para>
/// Every claim these tests pin is DERIVED from the August binary and the capture
/// <c>captures\wire-20260829-220829.txt</c>. Nothing here is LIVE-VERIFIED (D29): no
/// client-originated packet or screenshot confirms the panel actually fills.
/// </para>
/// </summary>
public sealed class InventoryDisplayTests
{
    [Fact]
    public void SkinIdentityReachesItemAndLoadoutRecordsWhileGameplayKeepsTheBaseFacts()
    {
        ulong next = 100;
        var inventory = new PlayerInventory(Character, () => ++next,
            new InventoryOptions { StarterOutfit = [], BaseCarryBulk = 10000 })
        { SkinDefinition = id => id == 2229 ? 2600u : id };
        inventory.Bootstrap();
        inventory.TryPickUp(2229, 1, out var gun);
        Assert.NotNull(gun);
        Assert.Equal(2229u, gun.DefinitionId);
        Assert.Equal(2600u, gun.DisplayDefinitionId);
        Assert.Equal(2600u, gun.ToRecord(Character).DefinitionId);
        Assert.Equal(2600u, BitConverter.ToUInt32(Bytes(inventory.ToLoadoutSlot(gun).WriteTo), 18));
        Assert.True(InventoryItemFacts.TryGet(2229, out var baseFact));
        Assert.Equal(baseFact.Bulk, gun.Fact.Bulk);
    }

    private const ulong Character = 0x0102_0304_0506_0708;
    private const uint MilitaryBackpack = 2124;
    private const uint FieldBandage = 2423;
    private const uint MotorcycleHelmet = 2168;

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    private static PlayerInventory Fresh(InventoryOptions? options = null)
    {
        ulong next = 0x9000;
        var inventory = new PlayerInventory(Character, () => ++next, options);
        inventory.Bootstrap();
        return inventory;
    }

    // -------------------------------------------------------------------------------------
    // §7a condition 2 — the equipped sentinel on the item record
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// docs/46 §3. <c>FUN_140da7b10</c> accepts an item for loadout slot S only when
    /// <c>item.ContainerGuid == DAT_143ce43b0</c> (the static constant
    /// <c>0xFFFFFFFFFFFFFFFF</c>, four references in the image, all this one query) <b>and</b>
    /// <c>item.SlotId == S</c>. Wave 3 sent <c>ContainerGuid 0, SlotId 0</c> for a worn item, so the
    /// lookup could never match whatever the loadout record said.
    /// </summary>
    [Fact]
    public void AWornItemsRecordCarriesTheEquippedSentinelAndTheLoadoutSlot()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            StarterOutfit = [],
            QuickUseConsumables = false,
        });
        inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);

        InventoryItem record = helmet!.ToRecord(Character);

        Assert.Equal(PlayerInventory.EquippedContainerGuid, record.ContainerGuid);
        Assert.Equal(0xFFFF_FFFF_FFFF_FFFFUL, record.ContainerGuid);
        Assert.Equal(SurvivorLoadout.Head, record.SlotId);
        Assert.Equal(PlayerInventory.LoadoutContainerDefinitionId, record.ContainerDefinitionId);

        // ...and on the wire: containerGuid at +21, slotId at +33 of the 62-byte base record.
        byte[] bytes = Bytes(record.WriteTo);
        Assert.Equal(InventoryItem.BaseLength, bytes.Length);
        Assert.Equal(0xFFFF_FFFF_FFFF_FFFFUL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(21)));
        Assert.Equal(PlayerInventory.LoadoutContainerDefinitionId,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(29)));
        Assert.Equal(SurvivorLoadout.Head, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(33)));
    }

    /// <summary>
    /// The other arm is untouched: an item that really is inside a container still names that
    /// container, its definition and its per-container slot, byte for byte as before.
    /// </summary>
    [Fact]
    public void AContainerResidentsRecordIsUnchanged()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions
        {
            StarterOutfit = [],
            QuickUseConsumables = false,
        });
        inventory.TryPickUp(FieldBandage, 2, out InventoryItemInstance? bandage);

        InventoryItem record = bandage!.ToRecord(Character);

        Assert.Equal(inventory.BaseBag!.Guid, record.ContainerGuid);
        Assert.Equal(ContainerDefinitionTable.BaseInventoryDefinitionId, record.ContainerDefinitionId);
        Assert.Equal(1u, record.SlotId);
        Assert.NotEqual(PlayerInventory.EquippedContainerGuid, record.ContainerGuid);
    }

    /// <summary>
    /// The sentinel is a <b>wire</b> value only. The model keeps <c>ContainerGuid = 0</c> for a worn
    /// item, which is the predicate <see cref="InventoryItemInstance.Bulk"/> uses — so a worn
    /// backpack still costs the bag nothing.
    /// </summary>
    [Fact]
    public void TheSentinelDoesNotLeakIntoTheModel()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { StarterOutfit = [] });
        inventory.TryPickUp(MilitaryBackpack, 1, out InventoryItemInstance? pack);

        Assert.Equal(0ul, pack!.ContainerGuid);
        Assert.Equal(0, pack.Bulk);
        Assert.Equal(0, inventory.BaseBag!.BulkUsed);
    }

    // -------------------------------------------------------------------------------------
    // §7a condition 3 — the definition id at record +0x08 is a hard gate
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// docs/46 §3a. <c>FUN_140dc0150</c> (the <c>LoadoutSlotChanged</c> consumer) and
    /// <c>FUN_140d35250</c> (the weapon-wheel rebuild) both open with
    /// <c>if (0 &lt; *(int *)(record + 8))</c>. Every <c>86 05</c> in the capture carried 0 there —
    /// <c>86 05 | 0310000000000000 | 03000000 | 26000000 | 00000000 | ...</c> — so every binding
    /// wave 3 sent was skipped entirely. Byte offset 18 of the 39-byte packet: 1 opcode + 1 sub +
    /// 8 character guid + record <c>+0x08</c>. (39, not 35, since wave 12 appended the trailing
    /// <c>u32 currentSlotId</c> the reader <c>FUN_140d32ca0</c> requires - S6 §7.1.)
    /// </summary>
    [Fact]
    public void SetLoadoutSlotCarriesTheItemDefinitionIdAtOffsetEighteen()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { StarterOutfit = [] });
        inventory.TryPickUp(MotorcycleHelmet, 1, out InventoryItemInstance? helmet);

        byte[] bytes = Bytes(inventory.ToLoadoutSlot(helmet!).WriteTo);

        Assert.Equal(SetLoadoutSlot.Length, bytes.Length);
        Assert.Equal(39, bytes.Length);
        Assert.Equal(SurvivorLoadout.Id, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10)));
        Assert.Equal(SurvivorLoadout.Head, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14)));
        Assert.Equal(MotorcycleHelmet, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(18)));
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(18)));
        Assert.Equal(helmet!.Guid, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(22)));
    }

    /// <summary>
    /// The same gate on the list form. Each 29-byte element is <c>u32 key</c> then the 25-byte
    /// record, so the definition id sits at element offset <b>12</b> (4 key + record <c>+0x08</c>),
    /// after the 18-byte header (<see cref="SetLoadoutSlots.HeaderLength"/>; the trailing
    /// <c>currentSlotId</c> follows the list, so it does not move the elements). Not one element may
    /// carry 0.
    /// </summary>
    [Fact]
    public void LoadoutSlotsIncludeEmptyDestinationsAndNonZeroDefinitionsForOccupiedSlots()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { QuickUseConsumables = false });
        inventory.TryPickUp(MotorcycleHelmet, 1, out _);
        inventory.TryPickUp(1889, 1, out _);                 // AR-15 -> the first wheel box

        SetLoadoutSlots packet = inventory.ToLoadoutSlots();
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(packet.Length, bytes.Length);
        Assert.Equal(SetLoadoutSlots.EmptyLength + (29 * packet.Slots.Count), bytes.Length);
        Assert.NotEmpty(packet.Slots);

        for (int i = 0; i < packet.Slots.Count; i++)
        {
            int element = SetLoadoutSlots.HeaderLength + (LoadoutSlotEntry.Length * i);
            LoadoutSlotEntry entry = packet.Slots[i];

            Assert.Equal(inventory.LoadoutSlots.ContainsKey(entry.Key), entry.Slot.ItemDefinitionId != 0);
            Assert.Equal(entry.Key, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(element)));
            Assert.Equal(entry.Slot.SlotId, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(element + 8)));
            Assert.Equal(
                entry.Slot.ItemDefinitionId,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(element + 12)));
            Assert.Equal(
                entry.Slot.ItemGuid,
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(element + 16)));
        }
    }

    /// <summary>The record still binds the guid the item collection was given.</summary>
    [Fact]
    public void TheLoadoutRecordNamesTheItemsOwnDefinitionAndGuid()
    {
        PlayerInventory inventory = Fresh();

        foreach (LoadoutSlotEntry entry in inventory.ToLoadoutSlots().Slots)
        {
            if (!inventory.LoadoutSlots.ContainsKey(entry.Key))
            {
                Assert.Equal(0u, entry.Slot.ItemDefinitionId);
                Assert.Equal(0ul, entry.Slot.ItemGuid);
                continue;
            }
            InventoryItemInstance bound = inventory.LoadoutSlots[entry.Key];
            Assert.Equal(bound.DefinitionId, entry.Slot.ItemDefinitionId);
            Assert.Equal(bound.Guid, entry.Slot.ItemGuid);
            Assert.Equal(SurvivorLoadout.Id, entry.Slot.LoadoutId);
            Assert.Equal(entry.Key, entry.Slot.SlotId);
        }
    }

    // -------------------------------------------------------------------------------------
    // §7a condition 1 — the item collection has to contain the item at all
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// docs/46 §7c. Every instance the model holds needs one <c>ItemAdd</c>, bag first, and every
    /// worn record carries the sentinel — so no bootstrap grant names a container guid and the
    /// burst is safe on either side of the destructive <c>InitContainers</c>.
    /// </summary>
    [Fact]
    public void ItemGrantsCoverEveryInstanceWithTheBagFirst()
    {
        PlayerInventory inventory = Fresh();
        IReadOnlyList<InventoryItem> grants = inventory.ToItemGrants();

        Assert.Equal(inventory.Items.Count, grants.Count);
        Assert.Equal(inventory.BaseBag!.Guid, grants[0].ItemGuid);
        Assert.Equal(ContainerDefinitionTable.BaseInventoryItemDefinitionId, grants[0].DefinitionId);

        Assert.Equal(
            inventory.Items.Keys.OrderBy(guid => guid),
            grants.Select(grant => grant.ItemGuid).OrderBy(guid => guid));
        Assert.All(grants, grant => Assert.Equal(Character, grant.OwnerGuid));
        Assert.All(grants, grant => Assert.Equal(PlayerInventory.EquippedContainerGuid, grant.ContainerGuid));
    }

    /// <summary>A bag resident is granted too, naming its container rather than the sentinel.</summary>
    [Fact]
    public void ItemGrantsIncludeContainerResidents()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { QuickUseConsumables = false });
        inventory.TryPickUp(FieldBandage, 1, out InventoryItemInstance? bandage);

        InventoryItem grant = Assert.Single(
            inventory.ToItemGrants(),
            g => g.ItemGuid == bandage!.Guid);

        Assert.Equal(inventory.BaseBag!.Guid, grant.ContainerGuid);
        Assert.Equal(1u, grant.SlotId);
    }

    // -------------------------------------------------------------------------------------
    // The starting outfit (docs/46 §7b) — the whole of the owner's item 2
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The panel boxes the owner sees filled on landing: hands 16, chest 10, feet 13, legs 14,
    /// plus the hidden required Fists (7) and the hidden bag (43). Every loadout slot is resolved
    /// by the client's own rule (<c>FLAG_AUTO_EQUIP</c> + <c>LoadoutSlotItemClasses</c>), not by a
    /// hard-coded table.
    /// </summary>
    [Fact]
    public void BootstrapDressesTheSurvivorInRealItems()
    {
        PlayerInventory inventory = Fresh();

        Assert.Equal(
            [
                SurvivorLoadout.Binoculars,
                SurvivorLoadout.Fists,
                SurvivorLoadout.Chest,
                SurvivorLoadout.Feet,
                SurvivorLoadout.Legs,
                SurvivorLoadout.Gloves,
                SurvivorLoadout.QuickUse1,
                SurvivorLoadout.Inventory,
            ],
            inventory.LoadoutSlots.Keys);

        Assert.Equal(SurvivorStarterOutfit.Gloves, inventory.LoadoutSlots[SurvivorLoadout.Gloves].DefinitionId);
        Assert.Equal(SurvivorStarterOutfit.Shirt, inventory.LoadoutSlots[SurvivorLoadout.Chest].DefinitionId);
        Assert.Equal(SurvivorStarterOutfit.Pants, inventory.LoadoutSlots[SurvivorLoadout.Legs].DefinitionId);
        Assert.Equal(SurvivorStarterOutfit.Boots, inventory.LoadoutSlots[SurvivorLoadout.Feet].DefinitionId);
        Assert.Equal(
            PlayerInventory.SurvivorBinocularsItemDefinitionId,
            inventory.LoadoutSlots[SurvivorLoadout.Binoculars].DefinitionId);
        Assert.Equal(
            4u,
            inventory.LoadoutSlots[SurvivorLoadout.QuickUse1].Count);
    }

    /// <summary>
    /// docs/46 §7b: shirt <c>PARAM1 21</c> and pants <c>PARAM1 29</c> are both
    /// <c>ContainerDefinitions.MAX_BULK = 50</c>, so the survivor lands carrying <c>0/200</c>
    /// instead of <c>0/100</c> — the client's own join, not an invention. Cargo stays in one
    /// backing bag, while c8/02 declares shirt, trousers and that bag as protocol records.
    /// </summary>
    [Fact]
    public void TheStartingOutfitRaisesCapacityAndDeclaresItsThreeProtocolContainers()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { BaseCarryBulk = 100 });

        Assert.Single(inventory.Containers);
        Assert.Equal((0, 200), inventory.Capacity);
        Assert.Equal(
            [SurvivorLoadout.Chest, SurvivorLoadout.Legs, SurvivorLoadout.Inventory],
            inventory.ToInitContainers().Containers.Select(entry => entry.Key));
    }

    /// <summary>
    /// The owner's acceptance step 3: a military backpack fills the backpack box and raises
    /// <b>one</b> dynamic capacity number. The backpack also gets the protocol declaration the
    /// client's re-indexer expects. 200 + <c>PARAM1 28</c>'s 2000.
    /// </summary>
    [Fact]
    public void AMilitaryBackpackRaisesCapacityAndAddsItsProtocolContainer()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { BaseCarryBulk = 100 });

        InventoryPlacement plan = inventory.TryPickUp(MilitaryBackpack, 1, out InventoryItemInstance? pack);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.Equal(SurvivorLoadout.Backpack, plan.LoadoutSlotId);
        Assert.Equal(pack, inventory.LoadoutSlots[SurvivorLoadout.Backpack]);
        Assert.Single(inventory.Containers);
        Assert.Equal(inventory.BaseBag!.Guid, inventory.Containers.Keys.Single());
        Assert.Equal(
            [
                SurvivorLoadout.Chest,
                SurvivorLoadout.Backpack,
                SurvivorLoadout.Legs,
                SurvivorLoadout.Inventory,
            ],
            inventory.ToInitContainers().Containers.Select(entry => entry.Key));
        Assert.Equal((0, 2200), inventory.Capacity);
        Assert.Equal(2200u, inventory.ToRecord(inventory.BaseBag).MaxBulk);
    }

    /// <summary>
    /// The starting state owns both its inventory bindings and its worn garment body slots. Fists
    /// remains loadout-only, so the baseline empty-hand attachment stays client-owned and no RHand
    /// slot is managed until a real weapon is selected.
    /// </summary>
    [Fact]
    public void TheStartingOutfitOwnsItsBodySlotsSoItCanBeVisuallyRemoved()
    {
        PlayerInventory inventory = Fresh();

        Assert.Equal(
            [BodySlots.Hands, BodySlots.Chest, BodySlots.Legs, BodySlots.Feet],
            inventory.EquipmentSlots.Keys);
        InventoryItemInstance fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];
        Assert.Equal(PlayerInventory.SurvivorFistsItemDefinitionId, fists.DefinitionId);
        Assert.Equal(0u, fists.EquipmentSlotId);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.False(inventory.EquipmentSlots.ContainsKey(BodySlots.RightHand));
        Assert.Equal(
            [BodySlots.Hands, BodySlots.Chest, BodySlots.Legs, BodySlots.Feet],
            inventory.ManagedEquipmentSlots.Order());
    }

    /// <summary>One list to turn the whole thing off, for a bisect against a live failure.</summary>
    [Fact]
    public void TheStartingOutfitIsOneKnob()
    {
        PlayerInventory bare = Fresh(new InventoryOptions { BaseCarryBulk = 100, StarterOutfit = [] });

        Assert.Equal(
            [SurvivorLoadout.Binoculars, SurvivorLoadout.Fists,
                SurvivorLoadout.QuickUse1, SurvivorLoadout.Inventory],
            bare.LoadoutSlots.Keys);
        Assert.Equal((0, 100), bare.Capacity);
    }

    /// <summary>
    /// docs/46 §7b: each garment is a real <c>ClientItemDefinitions</c> row whose
    /// <c>PASSIVE_EQUIP_SLOT_ID</c> matches the body slot the dress already attaches, and whose
    /// auto-equip loadout slot is the one the client's own rule picks.
    /// </summary>
    [Theory]
    [InlineData(SurvivorStarterOutfit.Gloves, 25008u, BodySlots.Hands, SurvivorLoadout.Gloves)]
    [InlineData(SurvivorStarterOutfit.Shirt, 25002u, BodySlots.Chest, SurvivorLoadout.Chest)]
    [InlineData(SurvivorStarterOutfit.Pants, 25003u, BodySlots.Legs, SurvivorLoadout.Legs)]
    [InlineData(SurvivorStarterOutfit.Boots, 25005u, BodySlots.Feet, SurvivorLoadout.Feet)]
    public void EveryStarterGarmentIsARealRowInTheSlotTheClientWouldPick(
        uint itemId, uint itemClass, uint bodySlot, uint loadoutSlot)
    {
        Assert.True(InventoryItemFacts.TryGet(itemId, out InventoryItemFact fact));
        Assert.Equal(itemClass, fact.ItemClass);
        Assert.Equal(bodySlot, fact.PassiveEquipSlotId);
        Assert.True(fact.CanEquip);
        Assert.Equal(loadoutSlot, InventoryAutoAssign.AutoEquipLoadoutSlot(itemId));

        StarterOutfitPiece piece = SurvivorStarterOutfit.Pieces.Single(p => p.ItemDefinitionId == itemId);
        Assert.Equal(bodySlot, piece.BodySlotId);
    }

    /// <summary>
    /// The point of choosing 2324 / 2144 / 2065: their mesh is <b>the same string</b> the starter
    /// dress already attaches, so granting them changes nothing visually. The catalogue is the
    /// source, because only 6 of 253 class-25002 rows carry a <c>MODEL_NAME</c> at all.
    /// <para>
    /// Body slot 5 is the known exception: <c>Survivor&lt;gender&gt;_Feet_Jeds.adr</c> has no item
    /// row anywhere, so 2613 (combat boots, which does carry a <c>MODEL_NAME</c>) stands in until
    /// docs/46 I1(e) swaps the starter mesh to match. This test states that gap rather than hiding
    /// it: flip the assertion when I1(e) lands.
    /// </para>
    /// </summary>
    [Fact]
    public void TheGarmentMeshesAreTheMeshesTheDressAlreadyAttaches()
    {
        CharacterVisuals male = CharacterVisuals.FromSelection(CharacterVisuals.Male, 1, 0, 0, 0);

        foreach (StarterOutfitPiece piece in SurvivorStarterOutfit.Pieces)
        {
            CharacterEquipmentAttachment dressed =
                male.StarterOutfit.Single(a => a.SlotId == piece.BodySlotId);
            string expected = piece.MeshName.Replace("<gender>", "Male", StringComparison.Ordinal);

            if (piece.ItemDefinitionId == SurvivorStarterOutfit.Boots)
            {
                // D351: the starter item now names the Stealth pair actually displayed.
                Assert.Equal(expected, dressed.ModelName);
                Assert.Equal(3711u, piece.ItemDefinitionId);
                Assert.Equal(Cranberry.Zone.Movement.FootwearTier.Stealth,
                    Cranberry.Zone.Movement.Footwear.TierFor(piece.ItemDefinitionId));
                continue;
            }

            Assert.Equal(expected, dressed.ModelName);
            Assert.Contains(
                AugustSkinCatalog.Apparel,
                entry => entry.RewardItemId == piece.ItemDefinitionId
                    && entry.MaleModelName == expected
                    && entry.EquipmentSlotId == piece.BodySlotId);
        }
    }

    /// <summary>
    /// Picking up another shirt leaves the worn garment in place and carries the original pickup.
    /// </summary>
    [Fact]
    public void PickingUpAShirtLeavesTheStarterGarmentEquipped()
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { BaseCarryBulk = 100 });
        InventoryItemInstance starterShirt = inventory.LoadoutSlots[SurvivorLoadout.Chest];

        InventoryPlacement plan = inventory.TryPickUp(2130, 1, out InventoryItemInstance? newShirt);

        Assert.Equal(InventoryPlacementKind.Container, plan.Kind);
        Assert.Equal(0ul, plan.DisplacedItemGuid);
        Assert.Equal(starterShirt, inventory.LoadoutSlots[SurvivorLoadout.Chest]);
        Assert.Equal(inventory.BaseBag!.Guid, newShirt!.ContainerGuid);
        Assert.Equal(0u, newShirt.LoadoutSlotId);

        // The spare shirt is cargo, so its record names the bag.
        InventoryItem record = newShirt.ToRecord(Character);
        Assert.Equal(inventory.BaseBag.Guid, record.ContainerGuid);
        Assert.NotEqual(PlayerInventory.EquippedContainerGuid, record.ContainerGuid);
    }
}
