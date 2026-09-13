using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// The <c>Weapon</c>-class <c>ClientUpdate.ItemAdd</c> tail - docs/58 §5, docs/60 stage 2.
/// <para>
/// This is the half of the docs/45 crash that <em>destroys</em> fire groups: <c>FUN_141483fa0</c>'s
/// <c>i8 fireGroupCount</c> drives <c>FUN_1414819a0(comp+0x28, currentCount - wireCount)</c>, a
/// destructor that is <b>not</b> guarded by the cursor's overrun flag. Cranberry's 1-byte Generic
/// tail exhausts the cursor, the count reads 0, and the array is emptied even if the client had just
/// built it. Every length assertion below is therefore load-bearing.
/// </para>
/// </summary>
public sealed class WeaponItemAddTailTests
{
    [Fact]
    public void VolcanicAkSkinGetsTheFullWeaponTailAndCanBeWielded()
    {
        const uint skin = 4033;
        Assert.False(AugustWeaponFacts.TryGet(skin, out _));
        var session = new WeaponSession();
        session.MarkWeaponDefinitionsSent();
        session.MagazineSource = (_, _) => 17;

        byte[] wire = Bytes(session.CreateItemAdd(SelfGuid, Record(skin)));
        Assert.True(session.Clearance!.IsClearedForActiveHand(ItemGuid));
        Assert.Equal(51u, session.Ledger.DeliveredFireGroupId(ItemGuid));
        byte[] baseWire = Bytes(session.CreateItemAdd(SelfGuid, Record(2229)));

        Assert.Equal(skin, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(15)));
        Assert.Equal(149, wire.Length);
        Assert.Equal(baseWire[19..], wire[19..]);
        Assert.Equal(17u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(82)));
        Assert.Equal(2325u, AmmoTypes.AmmoItemFor(skin));
        Assert.Equal(30, RetailBalance.ClipSize(skin));
    }

    [Fact]
    public void EveryCatalogWeaponSkinRetainsItsPrototypeWeaponTail()
    {
        foreach (var skin in AugustSkinCatalog.Weapons)
        {
            if (AugustWeaponTable.CreateTail(skin.CategoryPrototypeId) is not { } prototype) continue;
            var tail = AugustWeaponTable.CreateTail(skin.RewardItemId);
            Assert.NotNull(tail);
            Assert.Equal(Bytes(prototype.WriteTo), Bytes(tail.WriteTo));
            Assert.Equal(AmmoTypes.AmmoItemFor(skin.CategoryPrototypeId), AmmoTypes.AmmoItemFor(skin.RewardItemId));
        }
    }

    private const ulong SelfGuid = 0x3100_0000_0000_0001;
    private const ulong ItemGuid = 0x3100_0000_0000_0004;

    /// <summary>The AR-15: <c>ClientItemDefinitions</c> 2425, <c>PARAM1 6</c>, fire group 6, clip 30.</summary>
    private const uint Ar15 = 2425;

    /// <summary>The R380: <c>ClientItemDefinitions</c> 1991, <c>PARAM1 1400</c>, fire group 38, clip 7.</summary>
    private const uint R380 = 1991;

    /// <summary>The fists: <c>ClientItemDefinitions</c> 85, <c>PARAM1 12</c>, fire group 12, clip 0.</summary>
    private const uint Fists = 85;

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    private static InventoryItem Record(uint definitionId) =>
        new(definitionId, ItemGuid, Count: 1, OwnerGuid: SelfGuid, ContainerGuid: 0,
            ContainerDefinitionId: 0, SlotId: 1);

    // ------------------------------------------------------------------ docs/58 §8a, byte for byte

    /// <summary>
    /// <b>The docs/58 §8a worked AR-15 tail, byte for byte</b> - one fire group (6) with two fire
    /// modes whose charges are the client's own <c>CLIP_SIZE 30</c> and the design sentinel 1, and
    /// <c>comp+0x64 = 0</c>.
    /// <para>
    /// <b>Wave 13 (docs/107 §1): 72 bytes, not 68.</b> The leading counted array at <c>comp+0x10</c>
    /// is the weapon's AMMO-SLOT array and it now carries one element - the rounds in the magazine.
    /// The reader is <c>FUN_141484a20</c>, which takes exactly one <c>u32</c> off the cursor per
    /// element (the 0x10 in docs/58 is the in-memory stride, not a wire width), and the resulting
    /// 149-byte <c>ItemAdd</c> is byte-for-byte the length of the click-proven 1087 weapon
    /// <c>ItemAdd</c> Z1 sends. <see cref="TheAmmoSlotSwitchIsTheWave12TailExactly"/> pins the
    /// 68-byte form as the revert.
    /// </para>
    /// </summary>
    [Fact]
    public void TheWorkedAr15TailIsExactlyDocs58Section8a()
    {
        WeaponItemAddTail tail = AugustWeaponTable.CreateTail(Ar15)!;
        byte[] wire = Bytes(tail.WriteTo);

        byte[] expected =
        [
            0x00,                                           // FUN_140bcdc70   -> item+0x59
            0x01, 0x00, 0x00, 0x00,                         // FUN_141484a20   n0 = 1  (one ammo slot)
            0x00, 0x00, 0x00, 0x00,                         //   slot[0] = 0 rounds (a looted gun)
            0x01,                                           // i8 fireGroupCount = 1
            0x06, 0x00, 0x00, 0x00,                         //   fireGroupId = 6   -> entry+0x18
            0x02,                                           //   i8 fireModeCount = 2
            0x00, 0x00, 0x00, 0x00, 0x00,                   //     mode 0: flags 0, effectId 0
            0x1E, 0x00, 0x00, 0x00,                         //     mode 0: a = 30 (CLIP_SIZE)
            0x00, 0x00, 0x00, 0x00,                         //     mode 0: b = 0
            0x00, 0x00, 0x00, 0x00, 0x00,                   //     mode 1: flags 0, effectId 0
            0x01, 0x00, 0x00, 0x00,                         //     mode 1: a = 1 (design sentinel)
            0x00, 0x00, 0x00, 0x00,                         //     mode 1: b = 0
            0x00,                                           // i8  -> comp+0x40  equipment slot (see below)
            0x01,                                           // u8  -> comp+0x44  state 1 = IDLE
            0x00, 0x00, 0x00, 0x00,                         // u32 -> comp+0x48
            0x00,                                           // i8  -> comp+0x64 = 0  CURRENT GROUP
            0xFF,                                           // i8  -> comp+0x6c  = -1
            0xFF,                                           // i8  -> comp+0x70  = -1
            0x00, 0x00, 0x00, 0x00,                         // u32 -> comp+0x74
            0x00,                                           // u8  -> comp+0xa8
            0x00, 0x00, 0x00, 0x00,                         // u32 -> comp+0xac
            0x00,                                           // i8  -> comp+0x7c (as a float)
            0xFF, 0xFF, 0xFF, 0xFF,                         // u32 -> comp+0xe8  = 0xffffffff
            0x00, 0x00, 0x00, 0x00,                         // FUN_141484b70   n1 = 0
            0x00, 0x00, 0x00, 0x00,                         // FUN_141484740   n2 = 0
        ];

        Assert.Equal(expected, wire);
        Assert.Equal(WeaponItemAddTail.SingleGroupLengthWithMagazine, wire.Length);
        Assert.Equal(72, wire.Length);
        Assert.Equal(tail.Length, wire.Length);
        Assert.True(tail.AmmoSlot);
        Assert.Equal(0, tail.Magazine);
    }

    /// <summary>
    /// <b>docs/107 §1 - the magazine really is on the wire, and it is the only place it can be.</b>
    /// A tail built for a loaded gun writes the count as the single <c>u32</c> element of the
    /// <c>comp+0x10</c> ammo-slot array, and nothing else in the 72 bytes moves.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    public void TheMagazineIsTheOneElementOfTheAmmoSlotArray(int rounds)
    {
        WeaponItemAddTail empty = AugustWeaponTable.CreateTail(Ar15)!;
        WeaponItemAddTail loaded = empty with { Magazine = rounds };

        byte[] emptyWire = Bytes(empty.WriteTo);
        byte[] loadedWire = Bytes(loaded.WriteTo);

        Assert.Equal(72, loadedWire.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(loadedWire.AsSpan(1)));       // n0
        Assert.Equal((uint)rounds, BinaryPrimitives.ReadUInt32LittleEndian(loadedWire.AsSpan(5)));

        // Only the four element bytes ever differ - the magazine cannot disturb the fire groups.
        int[] differing =
            [.. Enumerable.Range(0, emptyWire.Length).Where(i => emptyWire[i] != loadedWire[i])];
        Assert.All(differing, i => Assert.InRange(i, 5, 8));
    }

    /// <summary>
    /// <c>CRANBERRY_WEAPON_TAIL_AMMO=0</c> reproduces the wave-5..12 tail byte for byte: the array
    /// is empty, the tail is 68 bytes and the <c>ItemAdd</c> is the 145 the owner's three
    /// 2026-09-02 sessions actually received.
    /// </summary>
    [Fact]
    public void TheAmmoSlotSwitchIsTheWave12TailExactly()
    {
        var off = new WeaponSession(
            WeaponStageOptions.Default with { WriteWeaponTailMagazine = false });

        WeaponItemAddTail reverted = off.CreateTail(Ar15, magazine: 30)!;
        byte[] wire = Bytes(reverted.WriteTo);

        Assert.False(reverted.AmmoSlot);
        Assert.Equal(0, reverted.Magazine);                                  // and the count is dropped too
        Assert.Equal(WeaponItemAddTail.SingleGroupLength, wire.Length);
        Assert.Equal(68, wire.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(1)));

        // Byte for byte, the rest of the tail is what the shipped one writes after its element.
        byte[] shipped = Bytes(AugustWeaponTable.CreateTail(Ar15)!.WriteTo);
        Assert.Equal(wire.AsSpan(5).ToArray(), shipped.AsSpan(9).ToArray());
    }

    /// <summary>
    /// <b>A weapon with no calibre and no magazine keeps the empty array</b> - Z1's own gate
    /// (<c>ZoneInventory.cs:3140</c>) and the client's own self-init, which sizes the same vector
    /// from the weapon definition's <c>def+0xd0</c> array (<c>FUN_142291180</c>). So the fists'
    /// tail length does not move, and only guns grow by four bytes.
    /// </summary>
    [Fact]
    public void OnlyAWeaponWithACalibreAndAMagazineDeclaresAnAmmoSlot()
    {
        Assert.True(AugustWeaponTable.CreateTail(Ar15)!.AmmoSlot);          // .223, clip 30
        Assert.True(AugustWeaponTable.CreateTail(R380)!.AmmoSlot);          // clip 7
        Assert.False(AugustWeaponTable.CreateTail(Fists)!.AmmoSlot);        // clip 0, no round

        Assert.Equal(72, Bytes(AugustWeaponTable.CreateTail(Ar15)!.WriteTo).Length);
        Assert.Equal(68, Bytes(AugustWeaponTable.CreateTail(Fists)!.WriteTo).Length);
    }

    /// <summary>
    /// <b>The session asks its magazine source for the live count</b>, so a re-announce after a
    /// reload carries the reload rather than resetting the hotbar to the pickup value. With no
    /// source bound the tail carries 0, which is what a looted gun holds.
    /// </summary>
    [Fact]
    public void TheSessionFillsTheMagazineFromItsSource()
    {
        var session = new WeaponSession(WeaponStageOptions.Default);
        InventoryItem item = Record(Ar15);

        Assert.Equal(0, session.CreateTail(Ar15, 0)!.Magazine);
        Assert.Equal(145 + 4, Bytes(session.CreateItemAdd(SelfGuid, item)).Length);

        session.MagazineSource = (guid, definitionId) =>
            guid == ItemGuid && definitionId == Ar15 ? 17 : 0;

        byte[] wire = Bytes(session.CreateItemAdd(SelfGuid, item));

        // 15-byte envelope + the 62-byte base record, then baseFlag, then n0 and the element.
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(15 + 62 + 1)));
        Assert.Equal(17u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(15 + 62 + 5)));
    }

    // ---------------------------------------------------- S6 §2.4 / §7.2, the idle-state scalars

    /// <summary>
    /// <b>The switch is byte-exact in both positions.</b> <c>CRANBERRY_WEAPON_TAIL_STATE=0</c>
    /// reproduces the wave-5..11 block of zeros exactly; ON writes Z1's four values and nothing
    /// else. The length never moves, which is what makes this safe to flip live.
    /// </summary>
    [Fact]
    public void TheIdleStateSwitchChangesExactlyFourFieldsAndNoLength()
    {
        WeaponItemAddTail on = AugustWeaponTable.CreateTail(Ar15)!;
        WeaponItemAddTail off = on with { IdleState = false };

        byte[] onWire = Bytes(on.WriteTo);
        byte[] offWire = Bytes(off.WriteTo);

        Assert.True(on.IdleState);                      // the record's default
        Assert.Equal(onWire.Length, offWire.Length);
        Assert.Equal(72, offWire.Length);       // wave 13: the ammo slot is on in both arms

        // Off = the historic all-zero scalar block, byte for byte.
        Assert.Equal(new byte[WeaponItemAddTail.ScalarBlockLength], offWire[41..64]);

        // On differs at exactly comp+0x44, +0x6c, +0x70 and the four bytes of +0xe8.
        int[] differing = [.. Enumerable.Range(0, onWire.Length).Where(i => onWire[i] != offWire[i])];
        Assert.Equal([42, 48, 49, 60, 61, 62, 63], differing);

        Assert.Equal(0x00, onWire[41]);                                 // comp+0x40 stays 0 (see IdleStateScalars)
        Assert.Equal(WeaponItemAddTail.IdleStateScalars.EquipmentSlot, onWire[41]);
        Assert.Equal(WeaponItemAddTail.IdleStateScalars.WeaponState, onWire[42]);
        Assert.Equal(1, onWire[42]);                                    // in FUN_142291930's {1,3,9,0xb,0xc,0xe}
        Assert.Equal(-1, (sbyte)onWire[48]);
        Assert.Equal(-1, (sbyte)onWire[49]);
        Assert.Equal(
            WeaponItemAddTail.IdleStateScalars.UnknownE8,
            BinaryPrimitives.ReadUInt32LittleEndian(onWire.AsSpan(60)));
    }

    /// <summary>
    /// <c>WeaponSession</c> is where <c>CRANBERRY_WEAPON_TAIL_STATE</c> is applied, so the wire is
    /// what the switch says whichever construction path the caller took.
    /// </summary>
    [Fact]
    public void TheSessionAppliesTheIdleStateSwitchToEveryTailItBuilds()
    {
        var on = new WeaponSession(WeaponStageOptions.Default);
        var off = new WeaponSession(WeaponStageOptions.Default with { TailIdleState = false });

        Assert.True(on.CreateTail(Ar15)!.IdleState);
        Assert.False(off.CreateTail(Ar15)!.IdleState);
        Assert.Equal(0x01, Bytes(on.CreateTail(Ar15)!.WriteTo)[42]);
        Assert.Equal(0x00, Bytes(off.CreateTail(Ar15)!.WriteTo)[42]);
    }

    /// <summary>
    /// docs/58 §8b - the R380 differs from the AR-15 in exactly two places: the fire-group id
    /// (<c>38 = 0x26</c>) and mode 0's charge (<c>CLIP_SIZE 7</c>). Same length, same everything else.
    /// </summary>
    [Fact]
    public void TheR380TailDiffersFromTheAr15InExactlyTwoFields()
    {
        byte[] ar15 = Bytes(AugustWeaponTable.CreateTail(Ar15)!.WriteTo);
        byte[] r380 = Bytes(AugustWeaponTable.CreateTail(R380)!.WriteTo);

        Assert.Equal(ar15.Length, r380.Length);
        Assert.Equal(38u, BinaryPrimitives.ReadUInt32LittleEndian(r380.AsSpan(10)));    // fireGroupId
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(r380.AsSpan(20)));       // mode 0 charge

        int[] differing = [.. Enumerable.Range(0, ar15.Length).Where(i => ar15[i] != r380[i])];
        // 0x26 vs 0x06 at offset 10; 0x07 vs 0x1e at offset 20 - four bytes later than before
        // wave 13, because the ammo-slot element now sits in front of the fire groups.
        Assert.Equal([10, 20], differing);
    }

    /// <summary>
    /// docs/58 §9 - the fists are not a special case. Item 85 is an ordinary
    /// <c>CODE_FACTORY_NAME = Weapon</c> row. <b>Report 3</b>: because its <c>CLIP_SIZE</c> is 0,
    /// mode 0's charge (<c>mode+0x18</c>, the hotbar's magazine CAPACITY) is <b>0</b> so the client
    /// shows NO magazine, while mode 1 keeps the design sentinel 1 - the mode
    /// <c>FUN_142291b90</c>'s hard-coded index-1 gate actually tests, so the fists still wield.
    /// </summary>
    [Fact]
    public void TheFistsTailShowsNoMagazineButStillWields()
    {
        WeaponItemAddTail tail = AugustWeaponTable.CreateTail(Fists)!;
        byte[] wire = Bytes(tail.WriteTo);

        // 68, not 72: the fists have no calibre and no magazine, so they declare no ammo slot.
        Assert.False(tail.AmmoSlot);
        Assert.Equal(68, wire.Length);
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(6)));
        // Mode 0's charge is now 0 (the magazine capacity the hotbar rendered as a 1-round mag), and
        // mode 1's is the sentinel (the wield/attack gate).
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(16)));
        Assert.Equal(AugustWeaponTable.TriggerChargeSentinel, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(29)));

        FireGroupDefinition group = Assert.Single(tail.Groups);
        Assert.Equal(AugustWeaponTable.FireModesPerGroup, group.Modes.Count);
        Assert.True(group.SupportsLocalAttack);
        Assert.True(tail.IsSafeForActiveHand);

        // The revert restores the sentinel in mode 0 (the old, magazine-showing behaviour).
        WeaponItemAddTail reverted = AugustWeaponTable.CreateTail(Fists, plainWieldNoMagazine: false)!;
        byte[] revertedWire = Bytes(reverted.WriteTo);
        Assert.Equal(
            AugustWeaponTable.TriggerChargeSentinel,
            BinaryPrimitives.ReadInt32LittleEndian(revertedWire.AsSpan(16)));

        // Optic lookup tests the definition ID, not this runtime charge; no fake round is needed.
        WeaponItemAddTail binoculars = AugustWeaponTable.CreateTail(1542u)!;
        byte[] binocularsWire = Bytes(binoculars.WriteTo);
        Assert.False(binoculars.AmmoSlot);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(binocularsWire.AsSpan(1)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(binocularsWire.AsSpan(16)));
    }

    // ------------------------------------------------------------------ the safety predicate

    /// <summary>
    /// <b>A tail with no fire groups is exactly the wave-5 failure</b> and must never be reported as
    /// safe for the active hand - nor may a tail whose current index addresses nothing, nor one
    /// whose current group cannot satisfy the trigger gate's hard-coded fire-mode index 1.
    /// </summary>
    [Fact]
    public void OnlyATailThatSurvivesTheCrashSiteIsSafeForTheActiveHand()
    {
        var good = new FireGroupDefinition(6, [new FireModeDefinition(0, 0, 30), new FireModeDefinition(0, 0, 1)]);
        var oneMode = new FireGroupDefinition(6, [new FireModeDefinition(0, 0, 30)]);
        var zeroCharge = new FireGroupDefinition(6, [new FireModeDefinition(0, 0, 0), new FireModeDefinition(0, 0, 0)]);

        Assert.True(new WeaponItemAddTail([good]).IsSafeForActiveHand);
        Assert.False(new WeaponItemAddTail([]).IsSafeForActiveHand);                        // empty array
        Assert.False(new WeaponItemAddTail([good], CurrentFireGroupIndex: 1).IsSafeForActiveHand);
        Assert.False(new WeaponItemAddTail([oneMode]).IsSafeForActiveHand);                 // docs/56 §1.7
        Assert.False(new WeaponItemAddTail([zeroCharge]).IsSafeForActiveHand);              // mode+0x18 = 0
    }

    /// <summary>The empty tail is still a well-formed 45-byte record; only its meaning is fatal.</summary>
    [Fact]
    public void AZeroGroupTailIsStillWellFormed()
    {
        byte[] wire = Bytes(new WeaponItemAddTail([]).WriteTo);

        Assert.Equal(WeaponItemAddTail.FixedLength, wire.Length);
        Assert.Equal(0x00, wire[5]);        // i8 fireGroupCount = 0 - the destructive value
    }

    // ------------------------------------------------------------------ the ItemAdd envelope

    /// <summary>
    /// The whole <c>11 00 02</c> packet: the 15-byte envelope, a blob length of <c>62 + 72 = 134</c>
    /// and 149 bytes on the wire - which is exactly the length of the click-proven 1087 weapon
    /// <c>ItemAdd</c> Z1 sends (docs/107 §1). The 62-byte base record must be byte-identical to the one
    /// <see cref="ItemAdd"/> writes - only the tail changes.
    /// </summary>
    [Fact]
    public void TheWeaponItemAddIsTheOrdinaryItemAddWithTheWeaponTail()
    {
        InventoryItem item = Record(Ar15);
        WeaponItemAddTail tail = AugustWeaponTable.CreateTail(Ar15)!;

        byte[] weapon = Bytes(new WeaponItemAdd(SelfGuid, item, tail).WriteTo);
        byte[] generic = Bytes(new ItemAdd(SelfGuid, item).WriteTo);

        Assert.Equal(149, weapon.Length);
        Assert.Equal(15 + InventoryItem.BaseLength + 72, weapon.Length);
        Assert.Equal(134u, BinaryPrimitives.ReadUInt32LittleEndian(weapon.AsSpan(11)));  // the blob length
        Assert.Equal(63u, BinaryPrimitives.ReadUInt32LittleEndian(generic.AsSpan(11)));  // 62 + the 1-byte tail

        // Opcode + sub + target guid (11 bytes) and the 62-byte base record are identical; only the
        // i32 blob length at offset 11 and the tail after offset 15 differ.
        Assert.Equal(generic.Take(11), weapon.Take(11));
        Assert.Equal(
            generic.Skip(15).Take(InventoryItem.BaseLength),
            weapon.Skip(15).Take(InventoryItem.BaseLength));
        Assert.Equal(ItemAdd.SubOpcode, WeaponItemAdd.SubOpcode);
    }

    // ------------------------------------------------------------------ which items get a tail

    /// <summary>
    /// A real tail is written only for a <c>CODE_FACTORY_NAME = Weapon</c> row whose datasheet names
    /// a non-zero fire group. Everything else keeps Cranberry's live-proven 1-byte Generic tail -
    /// including grenades (65, 66) and the other rows whose <c>FIRE_GROUP_ID</c> is 0, which would
    /// otherwise put an unresolvable id in the array.
    /// </summary>
    [Fact]
    public void OnlyWeaponsWithARealFireGroupGetATail()
    {
        Assert.NotNull(AugustWeaponTable.CreateTail(Ar15));
        Assert.NotNull(AugustWeaponTable.CreateTail(R380));
        Assert.NotNull(AugustWeaponTable.CreateTail(Fists));

        Assert.Null(AugustWeaponTable.CreateTail(2423));    // Field Bandage - CODE_FACTORY_NAME Generic
        // HE grenade - a Weapon row with FIRE_GROUP_ID 0. docs/120 D300 gives it the ruled group
        // 1404 by default; the overlay off is the datasheet's own answer, no tail.
        Assert.Null(AugustWeaponTable.CreateTail(65, plainWieldNoMagazine: true, throwables: false));
        Assert.NotNull(AugustWeaponTable.CreateTail(65));
        Assert.Null(AugustWeaponTable.CreateTail(0));       // not a row at all

        Assert.False(AugustWeaponTable.HasFireGroup(65, out uint none, throwables: false));
        Assert.Equal(0u, none);
        Assert.True(AugustWeaponTable.HasFireGroup(65, out uint ruled));
        Assert.Equal(Cranberry.Zone.Generated.Rulings.Throwables.FragFireGroupId, ruled);
        Assert.True(AugustWeaponTable.HasFireGroup(Ar15, out uint ar15Group));
        Assert.Equal(6u, ar15Group);
    }

    /// <summary>
    /// The client's own two sheets agree: <c>ClientItemDefinitions.PARAM1</c> is
    /// <c>ClientItemDatasheetData.WEAPON_ID</c> for every weapon row that has both. That equality is
    /// what makes list 0 keyable at all (docs/58 §2 step 10), so it is pinned rather than assumed.
    /// </summary>
    [Fact]
    public void Param1IsTheWeaponIdOnEveryWeaponRow()
    {
        int checkedRows = 0;
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (!InventoryItemFacts.TryGet(weapon.ItemId, out InventoryItemFact item)
                || item.CodeFactory != ItemCodeFactory.Weapon)
            {
                continue;
            }

            Assert.Equal(item.Param1, weapon.WeaponId);
            checkedRows++;
        }

        Assert.True(checkedRows > 100, $"only {checkedRows} weapon rows cross-checked");
    }
}
