using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Equipment;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Equipment;

/// <summary>
/// docs/94 / docs/95.
/// <para>
/// <b>Provenance, corrected 2026-09-02 (wave 11, lane 0A).</b> Two tests here used to assert the
/// bytes of <c>94 02</c> and <c>94 03</c> against hex copied from <c>C:\Project\out\refsessions</c>,
/// described at the time as "the owner's own Z1 server". Those files are tap logs of a third-party
/// emulator (docs/00's forbidden list), so the vectors are gone and with them the two byte-equality
/// assertions. What replaces them is the layout docs/95 §1 derives from the August client's own
/// parsers — <c>FUN_140cd8be0</c> / <c>FUN_140cd4f90</c> for the binding, <c>FUN_140cd97c0</c> for the
/// clear — asserted as sizes and composition: head 14, slot row 32, attachment 82 = 128 bytes for
/// <c>94 02</c>, and a flat 22 for <c>94 03</c>. A "simplified" head or a reintroduced <c>94 01</c>
/// alias pair still moves those numbers, so the tests still bite; they simply no longer stand on
/// anyone else's output.
/// </para>
/// </summary>
public class WieldSequenceTests
{
    // Arbitrary test identities. Nothing here is copied from any recorded session: the guids only
    // have to be distinguishable, and the strings are the client's own asset and alias names.
    private const ulong CharacterGuid = 0x0000_5EED_0000_0001;
    private const ulong AkItemGuid = 0x3000_0000_0023_F85F;
    private const uint ProfileId = 3;

    /// <summary>docs/95 §1: <c>u8 base; u8 sub; u32 profileId; u64 characterGuid</c>.</summary>
    private const int HeadLength = 14;

    /// <summary>
    /// docs/95 §1, the row <c>FUN_140cd4f90</c> reads at <c>+0x20 / +0x28 / +0x30 / +0x70</c>:
    /// <c>u32 key; u32 slotId; u64 itemGuid; str tint; str decal</c> — 32 bytes with
    /// <c>"Default"</c> and <c>"#"</c> in the two aliases.
    /// </summary>
    private const int SlotRowLength = 32;

    /// <summary>
    /// docs/95 §1, the single attachment element read by <c>thunk_FUN_140a2ed70</c> — 82 bytes for
    /// <c>Weapon_AK47_3P.adr</c> with two appearance ids.
    /// </summary>
    private const int AttachmentLength = 82;

    /// <summary>
    /// Clears every guid, so a test about BYTES is not also a test about guard 5. The guard has its
    /// own two tests below, and they use the real <see cref="WeaponFireGroupLedger"/>.
    /// </summary>
    private sealed class ClearsEverything : IActiveHandClearance
    {
        public bool IsClearedForActiveHand(ulong itemGuid) => true;
    }

    private static string Hex(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter(512);
        write(writer);
        return Convert.ToHexString(writer.Written.ToArray()).ToLowerInvariant();
    }

    /// <summary>
    /// <c>94 02</c> is the head <c>FUN_140cd4f90</c> reads, one slot row and one attachment, and
    /// nothing else: 14 + 32 + 82 = 128 bytes. The three parts are asserted separately so a
    /// "simplified" head or a reintroduced <c>94 01</c> alias pair names which part moved.
    /// </summary>
    [Fact]
    public void SetCharacterEquipmentSlotIsAHeadOneSlotRowAndOneAttachment()
    {
        var packet = new SetCharacterEquipmentSlot(
            CharacterGuid,
            new EquipmentSlotRow(SlotId: 7, ItemGuid: AkItemGuid, TintAlias: "Default", DecalAlias: "#"),
            new CharacterEquipmentAttachment(
                "Weapon_AK47_3P.adr",
                SlotId: 7,
                AppearanceIds: [97, 98]),
            ProfileId: ProfileId,
            Clearance: new ClearsEverything());

        string actual = Hex(packet.WriteTo);
        byte[] bytes = Convert.FromHexString(actual);

        Assert.Equal(HeadLength + SlotRowLength + AttachmentLength, bytes.Length);
        Assert.Equal(128, bytes.Length);

        // The head: 94 02, then u32 profileId, then u64 characterGuid - and no u32 0 and no alias
        // pair, which is exactly how 94 02 differs from 94 01.
        Assert.Equal(0x94, bytes[0]);
        Assert.Equal(0x02, bytes[1]);
        Assert.Equal(ProfileId, BitConverter.ToUInt32(bytes, 2));
        Assert.Equal(CharacterGuid, BitConverter.ToUInt64(bytes, 6));

        // The row starts at the head and carries the slot and the item the binding is about.
        Assert.Equal(7u, BitConverter.ToUInt32(bytes, HeadLength + 4));
        Assert.Equal(AkItemGuid, BitConverter.ToUInt64(bytes, HeadLength + 8));

        // The attachment is the model string, so the row cannot have eaten into it.
        Assert.Contains("Weapon_AK47_3P.adr", System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>94 03</c> is a flat 22 bytes, exactly as <c>FUN_140cd97c0</c> reads them:
    /// <c>u8 base; u8 sub; u32 profileId; u64 characterGuid; u32 unknown; u32 slotId</c>. The
    /// unknown u32 is zero because <c>FUN_140cd97c0</c> reads it into a local it never uses.
    /// </summary>
    [Fact]
    public void UnsetCharacterEquipmentSlotIsTheTwentyTwoBytesTheClientParserReads()
    {
        var packet = new Cranberry.Zone.UnsetCharacterEquipmentSlot(
            CharacterGuid, SlotId: 76, ProfileId: ProfileId);
        byte[] bytes = Convert.FromHexString(Hex(packet.WriteTo));

        Assert.Equal(22, bytes.Length);
        Assert.Equal(Cranberry.Zone.UnsetCharacterEquipmentSlot.Length, bytes.Length);
        Assert.Equal(0x94, bytes[0]);
        Assert.Equal(0x03, bytes[1]);
        Assert.Equal(ProfileId, BitConverter.ToUInt32(bytes, 2));
        Assert.Equal(CharacterGuid, BitConverter.ToUInt64(bytes, 6));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 14));
        Assert.Equal(76u, BitConverter.ToUInt32(bytes, 18));
    }

    /// <summary>
    /// The binding is a slot-7 row, so it answers to guard 5 like every other one. With no
    /// clearance the packet refuses to write rather than writing a slot with no item in it.
    /// </summary>
    [Fact]
    public void TheBindingRefusesToWriteWhenTheGuardHasNoClearance()
    {
        var packet = new SetCharacterEquipmentSlot(
            CharacterGuid,
            new EquipmentSlotRow(SlotId: 7, ItemGuid: AkItemGuid),
            new CharacterEquipmentAttachment("Weapon_AK47_3P.adr", SlotId: 7),
            Clearance: new WeaponFireGroupLedger());

        Assert.False(packet.IsPermitted);
        Assert.Throws<InvalidOperationException>(() => Hex(packet.WriteTo));
    }

    /// <summary>A row for a passive slot is not an active-hand row, so it is never guarded.</summary>
    [Fact]
    public void APassiveSlotBindingIsNeverGuarded()
    {
        var packet = new SetCharacterEquipmentSlot(
            CharacterGuid,
            new EquipmentSlotRow(SlotId: 76, ItemGuid: AkItemGuid),
            new CharacterEquipmentAttachment("Weapon_AK47_3P.adr", SlotId: 76),
            Clearance: new WeaponFireGroupLedger());

        Assert.True(packet.IsPermitted);
    }

    private static (WeaponSession Weapons, InventoryItem Item) ClearedWeapon()
    {
        var weapons = new WeaponSession(WeaponStageOptions.Default);
        Assert.True(weapons.TryCreateWeaponDefinitions(out _));
        weapons.MarkWeaponDefinitionsSent();

        var item = new InventoryItem(
            DefinitionId: AugustHeldWeapon.ItemDefinitionId,
            ItemGuid: AkItemGuid,
            Count: 1,
            OwnerGuid: 1234,
            ContainerGuid: 0,
            ContainerDefinitionId: 0,
            SlotId: 0);

        // Writing the ItemAdd is what puts the item in the ledger — the ledger records SENDS.
        using var scratch = new PacketWriter(512);
        weapons.CreateItemAdd(1234, item)(scratch);
        Assert.True(weapons.Ledger.IsClearedForActiveHand(AkItemGuid));
        return (weapons, item);
    }

    private static SetLoadoutSlots EmptyLoadout() =>
        new(1234, SurvivorLoadout.Id, [], SurvivorLoadout.Fists);

    /// <summary>
    /// <b>The ordering test.</b> The hotbar draw binds the slot LAST, after the ability manager
    /// (docs/95 §4 — hotbar behaviour observed in the owner's Z1 session logs, adopted under D53;
    /// the two packet BODIES above are proven from the August binary). Cranberry bound it first and
    /// alone, and the owner could not move. If this test's last element stops being <c>94 02</c>,
    /// the change has undone the shape docs/95 describes.
    /// </summary>
    [Fact]
    public void TheBindingIsTheLastPacketAndTheAbilityManagerPrecedesIt()
    {
        (WeaponSession weapons, InventoryItem item) = ClearedWeapon();

        IReadOnlyList<WieldSequence.Step> steps = WieldSequence.Build(
            characterGuid: 1234,
            item,
            new CharacterEquipmentAttachment("Weapon_M16A4_3p.adr", SlotId: 7),
            activeHandSlotId: 7,
            vacatedBodySlotId: 76,
            previousAbilityId: 999,
            EmptyLoadout(),
            sendAbilityManager: true,
            weapons);

        string[] labels = [.. steps.Select(s => s.Label.Split(' ')[0] + " " + s.Label.Split(' ')[1])];

        Assert.Equal(
            ["a0 03", "94 03", "11 04", "11 02", "86 04", "a0 05", "94 02"],
            labels);
    }

    /// <summary>
    /// A ground pickup into an empty hand vacates no slot and uninitialises no ability, so steps 1
    /// and 2 of the draw are absent — and the binding is still last.
    /// </summary>
    [Fact]
    public void AGroundPickupIntoAnEmptyHandSkipsTheUninitAndTheUnset()
    {
        (WeaponSession weapons, InventoryItem item) = ClearedWeapon();

        IReadOnlyList<WieldSequence.Step> steps = WieldSequence.Build(
            characterGuid: 1234,
            item,
            new CharacterEquipmentAttachment("Weapon_M16A4_3p.adr", SlotId: 7),
            activeHandSlotId: 7,
            vacatedBodySlotId: 0,
            previousAbilityId: 0,
            EmptyLoadout(),
            sendAbilityManager: true,
            weapons);

        string[] labels = [.. steps.Select(s => s.Label.Split(' ')[0] + " " + s.Label.Split(' ')[1])];
        Assert.Equal(["11 04", "11 02", "86 04", "a0 05", "94 02"], labels);
    }

    [Fact]
    public void AFreshlyGrantedWeaponRetainsItsInstanceAndBindsAfterTheAbilityManager()
    {
        (WeaponSession weapons, InventoryItem item) = ClearedWeapon();

        IReadOnlyList<WieldSequence.Step> steps = WieldSequence.Build(
            1234, item,
            new CharacterEquipmentAttachment("Weapon_M16A4_3p.adr", SlotId: 7),
            activeHandSlotId: 7, vacatedBodySlotId: 0, previousAbilityId: 0,
            EmptyLoadout(), sendAbilityManager: true, weapons,
            itemAlreadyGranted: true);

        byte[][] packets = [.. steps.Select(step => Convert.FromHexString(Hex(step.Write)))];
        Assert.Equal(new byte[] { 0x86, 0xa0, 0x94 }, packets.Select(p => p[0]));
        Assert.Equal(new byte[] { 0x04, 0x05, 0x02 }, packets.Select(p => p[1]));
        Assert.Equal(item.ItemGuid, BitConverter.ToUInt64(packets[^1], HeadLength + 8));
    }

    /// <summary>
    /// <b>All or nothing.</b> When the guard refuses the binding the sequence is EMPTY, not
    /// truncated: steps 1–7 without step 8 would delete the client's item and bind nothing back,
    /// which is strictly worse than never wielding.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnclearedItemProducesNoPacketsAtAllRatherThanASequenceWithoutItsBinding(bool itemAlreadyGranted)
    {
        var weapons = new WeaponSession(WeaponStageOptions.Default);   // nothing sent, nothing cleared
        var item = new InventoryItem(
            DefinitionId: AugustHeldWeapon.ItemDefinitionId,
            ItemGuid: AkItemGuid,
            Count: 1,
            OwnerGuid: 1234,
            ContainerGuid: 0,
            ContainerDefinitionId: 0,
            SlotId: 0);

        IReadOnlyList<WieldSequence.Step> steps = WieldSequence.Build(
            characterGuid: 1234,
            item,
            new CharacterEquipmentAttachment("Weapon_M16A4_3p.adr", SlotId: 7),
            activeHandSlotId: 7,
            vacatedBodySlotId: 0,
            previousAbilityId: 0,
            EmptyLoadout(),
            sendAbilityManager: true,
            weapons, itemAlreadyGranted);

        Assert.Empty(steps);
    }

    /// <summary>
    /// Step 5 is deliberately missing and says so in one place. A future wave that ships
    /// <c>Weapon.Weapon</c> should delete this test along with the constant — it exists so the hole
    /// is a decision on the record rather than an oversight.
    /// </summary>
    [Fact]
    public void TheWeaponArmStepIsRecordedAsUnderivedRatherThanGuessed()
    {
        Assert.Contains("not shipped", WieldSequence.WeaponArmStepIsUnderived);
        Assert.Contains("docs/95", WieldSequence.WeaponArmStepIsUnderived);
    }
}
