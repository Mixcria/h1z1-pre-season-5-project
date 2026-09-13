using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Inventory;

// REGRESSION GUARD 5 - docs/45-gun-pickup-crash.md.
//
// An Equipment.SetCharacterEquipment (0x94/01) equipment-slot row whose slot id is 7 (RHand, the
// active hand) hard-crashes the August client, whatever item it names. In
// logs/host-20260829-220829.log across four sessions, 5 of 5 packets whose row list named only body
// slots 1/5/10/11/100 were accepted and 4 of 4 that also carried a slot-7 row killed the client -
// over item 83 (Machete, class 4098), 1991 (R380, class 4096) and 10 (AR-15, class 25036), and with
// the slot-7 ATTACHMENT both changed and unchanged. The 22:34:02.648 crash packet is the
// 22:33:54.678 accepted packet byte for byte with the row count bumped 1 -> 2 and 24 bytes inserted.
// H1Z1.exe_08292026_223402.dmp: EXCEPTION_ACCESS_VIOLATION reading 0x38 at FUN_1411cee73+0xa1, i.e.
// FUN_14228d840(item + 0xa8) returning NULL for a weapon with an empty fire-group array.
//
// These tests pin the two halves of the fix:
//   * InventoryOptions.WieldFirstWeapon = false, so a picked-up weapon cannot create an active
//     body-slot-7 binding; Fists remains a required loadout-only empty-hand selection;
//   * ActiveHandRowGuard inside SetCharacterEquipmentWithSlots.WriteTo, so no call site - including
//     the dormant GiveStarterWeapon path - can put such a row on the wire even if it tries.
// A selected or explicitly auto-wielded weapon may occupy RHand in the model, but its row remains
// guarded on the wire.
public sealed class GunPickupCrashGuardTests
{
    // ClientItemDefinitions rows named in docs/45 2a and 4.
    private const uint Machete = 83;             // class 4098,  MODEL_NAME Weapons_Machete01_3p.adr
    private const uint R380 = 1991;              // class 4096,  sheet MODEL_NAME empty; catalogue mesh
    private const uint Ar15ModelLess = 10;       // class 25036, sheet MODEL_NAME empty; catalogue mesh
    private const uint Ak47 = 2229;              // class 25036, sheet MODEL_NAME empty; catalogue mesh
    private const uint Ar15WithModel = 2425;     // class 25036, MODEL_NAME Weapon_M16A4_3p.adr
    private const uint UnresolvedWeapon = 1373;  // class 25036, neither resolver has a mesh
    private const uint Helmet = 2172;            // the 22:33:54 pickup: body slot 1, accepted
    private const uint Sneakers = 2217;          // the 22:25:08 pickup: body slot 5, accepted

    /// <summary>The active hand - <c>EquipmentSlotDefinitions.txt</c> row 7, <c>RHand</c>.</summary>
    private const uint RHand = 7;

    private static PlayerInventory Fresh(InventoryOptions? options = null)
    {
        ulong next = 0x4000;
        var inventory = new PlayerInventory(
            0xdead_beef, () => ++next, (options ?? new InventoryOptions()) with { StarterOutfit = [] });
        inventory.Bootstrap();
        return inventory;
    }

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    private static void AssertFistsRemainSelectedOffBody(PlayerInventory inventory)
    {
        InventoryItemInstance fists = inventory.LoadoutSlots[SurvivorLoadout.Fists];
        Assert.Equal(PlayerInventory.SurvivorFistsItemDefinitionId, fists.DefinitionId);
        Assert.Equal(SurvivorLoadout.Fists, inventory.CurrentLoadoutSlotId);
        Assert.Equal(0u, fists.EquipmentSlotId);
        Assert.Equal(0ul, inventory.WieldedItemGuid);
        Assert.False(inventory.EquipmentSlots.ContainsKey(RHand));
    }

    /// <summary>
    /// The equipment-slot list of a <c>SetCharacterEquipmentWithSlots</c> as it lands on the wire:
    /// the <c>i32</c> count sits immediately after the fixed head
    /// (<c>u8 opcode; u8 sub; u32 profileId; u64 characterId; u32; str ""; str ""</c> = 26 bytes),
    /// and each row with two empty strings is exactly <see cref="EquipmentSlotRow.MinimalLength"/>.
    /// </summary>
    private static List<uint> SerialisedSlotIds(SetCharacterEquipmentWithSlots packet)
    {
        byte[] bytes = Bytes(packet.WriteTo);
        const int countOffset = 1 + 1 + 4 + 8 + 4 + 4 + 4;
        Assert.Equal(26, countOffset);

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(countOffset));
        var slotIds = new List<uint>(count);
        int cursor = countOffset + 4;
        for (int i = 0; i < count; i++)
        {
            // u32 hash key; u32 slotId; u64 itemGuid; str tint; str decal.
            Assert.Equal(
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4)));
            slotIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4)));
            cursor += EquipmentSlotRow.MinimalLength;
        }

        return slotIds;
    }

    // -------------------------------------------------------------------------------------
    // The wire guard - ActiveHandRowGuard
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// docs/45 2: the crash packet is the accepted packet plus a 24-byte slot-7 row. Adding that
    /// row to a packet the client accepted must now change <b>nothing at all</b> - not the count
    /// field, not the length.
    /// </summary>
    [Fact]
    public void AddingTheSlotSevenRowChangesNotOneByte()
    {
        // The 22:33:54.678 shape: one accepted row (body slot 1, the helmet).
        var accepted = new SetCharacterEquipmentWithSlots(
            4099,
            Slots: [new EquipmentSlotRow(1, 0x3100000000000003)],
            Attachments: [new CharacterEquipmentAttachment(AugustHeldWeapon.EmptyHandModelName, RHand)]);

        // The 22:34:02.648 shape: the same packet with the slot-7 row the client died on.
        var withActiveHand = accepted with
        {
            Slots =
            [
                new EquipmentSlotRow(1, 0x3100000000000003),
                new EquipmentSlotRow(RHand, 0x3100000000000004),
            ],
        };

        byte[] before = Bytes(accepted.WriteTo);
        byte[] after = Bytes(withActiveHand.WriteTo);

        Assert.Equal(before, after);
        Assert.Equal(24, EquipmentSlotRow.MinimalLength);
        Assert.Equal([1u], SerialisedSlotIds(withActiveHand));
    }

    /// <summary>
    /// The 22:25:21 crash: rows for body slots 5, 7 and 11. Only 5 and 11 may reach the wire, in
    /// order, and the packet must be exactly 24 bytes shorter than the caller's row list implies.
    /// </summary>
    [Fact]
    public void OnlyTheActiveHandRowIsDroppedAndTheRestKeepTheirOrder()
    {
        var packet = new SetCharacterEquipmentWithSlots(
            4099,
            Slots:
            [
                new EquipmentSlotRow(5, 0x3100000000000001),
                new EquipmentSlotRow(RHand, 0x3100000000000002),
                new EquipmentSlotRow(11, 0x3100000000000003),
            ]);

        Assert.Equal([5u, 11u], SerialisedSlotIds(packet));

        int kept = Bytes(packet.WriteTo).Length;
        int allThree = Bytes(w =>
        {
            // What the wave-3 serialiser would have written: the same packet with no guard.
            w.WriteByte(SetCharacterEquipmentWithSlots.Opcode);
            w.WriteByte(SetCharacterEquipmentWithSlots.SubOpcode);
            w.WriteUInt32(5);
            w.WriteUInt64(4099);
            w.WriteUInt32(0);
            w.WriteString(string.Empty);
            w.WriteString(string.Empty);
            w.WriteInt32(3);
            new EquipmentSlotRow(5, 0x3100000000000001).WriteTo(w);
            new EquipmentSlotRow(RHand, 0x3100000000000002).WriteTo(w);
            new EquipmentSlotRow(11, 0x3100000000000003).WriteTo(w);
            w.WriteInt32(0);
            w.WriteBool(true);
        }).Length;

        Assert.Equal(24, allThree - kept);
    }

    /// <summary>
    /// docs/45 I3: the dormant <c>GiveStarterWeapon</c> branch sends
    /// <c>Slots: [AugustHeldWeapon.SlotRow(guid)]</c> - a bare slot-7 row. Guarded, that packet is
    /// byte-identical to the same dress with no rows at all, which is the shape docs/32 proved.
    /// </summary>
    [Fact]
    public void TheStarterWeaponRowIsInertOnTheWire()
    {
        IReadOnlyList<CharacterEquipmentAttachment> dress =
            [new CharacterEquipmentAttachment(AugustHeldWeapon.AttachmentModelName, RHand)];

        byte[] withRow = Bytes(new SetCharacterEquipmentWithSlots(
            4099, Slots: [AugustHeldWeapon.SlotRow(0x3100000000000004)], Attachments: dress).WriteTo);
        byte[] withoutRow = Bytes(new SetCharacterEquipmentWithSlots(
            4099, Slots: [], Attachments: dress).WriteTo);

        Assert.Equal(withoutRow, withRow);
        // The mesh in the hand is untouched: it is a separate list and was never implicated.
        Assert.Contains(
            AugustHeldWeapon.AttachmentModelName,
            System.Text.Encoding.UTF8.GetString(withRow),
            StringComparison.Ordinal);
    }

    /// <summary>The guard's own predicate, so the reason survives a refactor of the writer.</summary>
    [Fact]
    public void TheGuardNamesTheActiveHandSlot()
    {
        Assert.Equal(RHand, ActiveHandRowGuard.ActiveHandSlotId);
        Assert.Equal(AugustHeldWeapon.RightHandSlotId, ActiveHandRowGuard.ActiveHandSlotId);
        Assert.True(ActiveHandRowGuard.IsActiveHandRow(new EquipmentSlotRow(RHand, 1)));
        Assert.False(ActiveHandRowGuard.IsActiveHandRow(new EquipmentSlotRow(76, 1)));

        // Nothing to remove: the same list instance comes back, so the guard costs nothing on the
        // path every real packet takes.
        IReadOnlyList<EquipmentSlotRow> clean = [new EquipmentSlotRow(1, 1), new EquipmentSlotRow(5, 2)];
        Assert.Same(clean, ActiveHandRowGuard.Filter(clean));
        Assert.Empty(ActiveHandRowGuard.Filter(null));
    }

    // -------------------------------------------------------------------------------------
    // The placement guard - InventoryOptions.WieldFirstWeapon
    // -------------------------------------------------------------------------------------

    /// <summary>docs/45 I2: wielding is off by default, and that default is the P0 fix.</summary>
    [Fact]
    public void WieldingIsOffByDefault() =>
        Assert.False(new InventoryOptions().WieldFirstWeapon);

    /// <summary>
    /// With the default rollback option, definition 2425 takes wheel loadout slot 1 and stows on
    /// body slot 76 (<c>R_LongWeapon_1</c>) instead of creating a body-slot-7 binding.
    /// </summary>
    [Fact]
    public void APickedUpRifleStowsInsteadOfWielding()
    {
        PlayerInventory inventory = Fresh();
        InventoryPlacement plan = inventory.TryPickUp(Ar15WithModel, 1, out InventoryItemInstance? rifle);

        Assert.Equal(InventoryPlacementKind.LoadoutSlot, plan.Kind);
        Assert.False(plan.Wielded);
        Assert.Equal(1u, plan.LoadoutSlotId);
        Assert.Equal(AugustHeldWeapon.StowedSlotId, plan.EquipmentSlotId);
        Assert.Equal(76u, plan.EquipmentSlotId);
        AssertFistsRemainSelectedOffBody(inventory);
        Assert.Equal(rifle!.Guid, inventory.EquipmentSlots[76].Guid);
        // The log line the owner reads must say why, not just where.
        Assert.Contains("docs/45", plan.Rule, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three definitions that actually crashed the client in host-20260829-220829.log. With the
    /// rollback option, none may create a body-slot-7 binding; the projection of the inventory's
    /// body slots - which is what <c>ZoneService.SendCharacterAppearance</c> turns into rows - must
    /// still carry no 7 after the wire guard.
    /// </summary>
    [Theory]
    [InlineData(Machete)]
    [InlineData(R380)]
    [InlineData(Ar15ModelLess)]
    [InlineData(Ar15WithModel)]
    public void TheItemsThatCrashedTheClientNeverReachTheActiveHand(uint definitionId)
    {
        PlayerInventory inventory = Fresh();
        inventory.TryPickUp(Sneakers, 1, out _);      // body slot 5, accepted at 22:25:08
        inventory.TryPickUp(Helmet, 1, out _);        // body slot 1, accepted at 22:33:54
        InventoryPlacement plan = inventory.TryPickUp(definitionId, 1, out _);

        Assert.NotEqual(RHand, plan.EquipmentSlotId);
        Assert.False(plan.Wielded);
        AssertFistsRemainSelectedOffBody(inventory);

        var packet = new SetCharacterEquipmentWithSlots(
            4099,
            Slots: [.. inventory.EquipmentSlots.Select(w => new EquipmentSlotRow(w.Key, w.Value.Guid))]);
        Assert.DoesNotContain(RHand, SerialisedSlotIds(packet));
    }

    /// <summary>
    /// The strong default-option form: no item definition in the sheet can create a body-slot-7
    /// binding by the resolver. All 2,643 rows, so a future datasheet regeneration cannot reopen the
    /// rollback path.
    /// </summary>
    [Fact]
    public void DefaultPlacementNeverCreatesAnActiveHandBinding()
    {
        int wieldable = 0;
        foreach (InventoryItemFact fact in InventoryItemFacts.All)
        {
            if (fact.ActiveEquipSlotId == 0)
            {
                continue;
            }

            wieldable++;
            PlayerInventory inventory = Fresh();
            InventoryPlacement plan = inventory.Plan(fact.DefinitionId);
            Assert.NotEqual(RHand, plan.EquipmentSlotId);
            Assert.False(plan.Wielded);
            AssertFistsRemainSelectedOffBody(inventory);
        }

        // docs/41 5a: ACTIVE_EQUIP_SLOT_ID is only ever 0 or 7, on 262 of the 2,643 rows. If this
        // number moves, the sheet was regenerated and the sweep above is measuring something else.
        Assert.Equal(262, wieldable);
    }

    // -------------------------------------------------------------------------------------
    // The cosmetic rule - a wielded item must have a mesh to hold (docs/45 4)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// With auto-wield enabled, the mesh gate is the full August worn-mesh resolver, not the raw
    /// <c>MODEL_NAME</c> column. Definitions 10, 1991 and 2229 deliberately have an empty sheet
    /// model but resolve through the appearance catalogue; 1373 resolves nowhere and must leave the
    /// empty hand unbound.
    /// </summary>
    [Theory]
    [InlineData(R380, true)]
    [InlineData(Ar15ModelLess, true)]
    [InlineData(Ak47, true)]
    [InlineData(Ar15WithModel, true)]
    [InlineData(Machete, true)]
    [InlineData(UnresolvedWeapon, false)]
    public void OnlyAnItemWithAResolvedHeldMeshMayEverBeWielded(uint definitionId, bool expectWielded)
    {
        PlayerInventory inventory = Fresh(new InventoryOptions { WieldFirstWeapon = true });
        InventoryPlacement plan = inventory.TryPickUp(definitionId, 1, out _);

        Assert.True(InventoryItemFacts.TryGet(definitionId, out InventoryItemFact fact));
        Assert.Equal(RHand, fact.ActiveEquipSlotId);
        bool hasResolvedMesh = AugustWornVisuals.TryResolveMesh(
            definitionId,
            CharacterVisuals.Male,
            fact.ModelName,
            out string modelName,
            out _);
        Assert.Equal(expectWielded, hasResolvedMesh);
        Assert.Equal(expectWielded, modelName.Length != 0);
        Assert.Equal(expectWielded, plan.Wielded);
        Assert.Equal(expectWielded, plan.EquipmentSlotId == RHand);

        if (!expectWielded)
        {
            Assert.Contains("no third-person mesh resolves", plan.Rule, StringComparison.Ordinal);
            AssertFistsRemainSelectedOffBody(inventory);
        }
        else
        {
            Assert.Equal(definitionId, inventory.EquipmentSlots[RHand].DefinitionId);
            // ...and even then the wire guard still refuses to serialise the row.
            var packet = new SetCharacterEquipmentWithSlots(
                4099,
                Slots: [.. inventory.EquipmentSlots.Select(w => new EquipmentSlotRow(w.Key, w.Value.Guid))]);
            Assert.DoesNotContain(RHand, SerialisedSlotIds(packet));
        }
    }
}
