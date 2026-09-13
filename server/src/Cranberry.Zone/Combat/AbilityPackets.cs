using Cranberry.Protocol;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Combat;

/// <summary>
/// Sub-opcodes of the Abilities family (<c>ZoneOpcodes.AbilitiesBase</c> = <c>0xa0</c>).
/// <para>
/// <b>Every one of these is registered by the August client itself</b>, all four at
/// <c>FUN_1413bef90</c> in <c>out\registrations-1148.json</c>:
/// <c>cPacketIdAbilitiesBase 0xa000</c>, <c>InitAbility 0x100a000</c>,
/// <c>UpdateAbility 0x200a000</c>, <c>UninitAbility 0x300a000</c>,
/// <c>SetActivatableAbilityManager 0x500a000</c>. The owner's <c>ClientProtocol_1087</c> server
/// numbers the same family <c>0xa1</c>, so this is the wave-9 bridge's clean <b>base -1</b> with
/// every sub unchanged - the same shift the whole <c>WeaponPacket::</c> family takes.
/// </para>
/// </summary>
public static class AbilityOpcodes
{
    /// <summary><c>Abilities::cAbilityPacketIdInitAbility</c> - c2s, the click that starts a swing.</summary>
    public const byte InitAbilitySub = 0x01;

    /// <summary><c>Abilities::cAbilityPacketIdUpdateAbility</c> - c2s, the swing landing.</summary>
    public const byte UpdateAbilitySub = 0x02;

    /// <summary><c>Abilities::cAbilityPacketIdUninitAbility</c> - s2c on draw and holster.</summary>
    public const byte UninitAbilitySub = 0x03;

    /// <summary><c>Abilities::cAbilityPacketIdSetActivatableAbilityManager</c> - s2c.</summary>
    public const byte SetActivatableAbilityManagerSub = 0x05;
}

/// <summary>
/// <b>The abilities wire, ported from the owner's <c>ZoneAbilities.cs</c> under D53</b> and
/// re-expressed against <c>ClientProtocol_1148</c>.
///
/// <para>
/// <b>Why melee lives here and not in the weapon family.</b> The bridge's first reading was that
/// melee rides <c>0x82</c>, on the strength of <c>82 22 MeleeHitMaterial</c>. It does not: the
/// owner's own handler for that sub logs the material and <em>explicitly refuses to apply damage</em>
/// (<c>C:\Z1\Server\Zone\ZoneCombat.cs:799-809</c>), because a melee hit resolves through the
/// ability system. A swing is <c>a0 01 InitAbility</c> on the click and <c>a0 02 UpdateAbility</c>
/// on the landing, and neither is accepted by the client unless
/// <c>a0 05 SetActivatableAbilityManager</c> has told it which abilities the loadout can activate.
/// </para>
///
/// <para>
/// <b>Where the numbers come from.</b> The entry shape, the two repeated ability dwords, the
/// trailing <c>2</c> and <c>0x40</c>, the fists' extra element and the hardcoded head entry are all
/// the owner's, from <c>ZoneAbilities.cs:230-300</c>. The <em>ability ids</em> are not: they come
/// from this build's own <see cref="AugustAbilityFacts"/>, i.e. column 6 of the August client's
/// <c>ClientItemDefinitions.txt</c>. The two agree where it matters most - his hardcoded head entry
/// is item 83 / ability 1111164, and August's row 83 is <b>1111164</b> - which is the strongest
/// corroboration available that his writer is correct data rather than inherited guesswork.
/// </para>
///
/// <para>
/// <b>Grade (D29).</b> BUILT and unit-TESTED, not LIVE-VERIFIED. There are <b>zero</b> <c>0xa0</c>
/// packets in either direction across all 111 captures
/// (<c>out\wave9-bridge\capture-census-20260830.json</c>), so nothing on this path has ever been
/// seen on an August wire. The experiment that settles it is a single left-click with a melee
/// weapon in the active hand: a <c>[MELEE]</c> line in the host log is the first <c>0xa0</c> byte
/// this project has ever received.
/// </para>
/// </summary>
public static class AbilityPackets
{
    /// <summary>
    /// The owner's hardcoded head entry: loadout slot 1, item 83, ability 1111164
    /// (<c>ZoneAbilities.cs:126-131</c>). His own comment for it is "hardcoded one weapon ability to
    /// fix fists after respawning". It is emitted unconditionally, even when slot 1 is empty.
    /// </summary>
    public const uint HeadEntryLoadoutSlotId = 1;

    /// <summary>The head entry's item. August's own row 83 carries ability
    /// <see cref="HeadEntryAbilityId"/>, so this pair is confirmed on both sides.</summary>
    public const uint HeadEntryItemDefinitionId = 83;

    /// <summary>The head entry's ability. Verified against
    /// <c>out\data_aug\ClientItemDefinitions.txt</c> row 83 column <c>ACTIVATABLE_ABILITY_ID</c>.</summary>
    public const uint HeadEntryAbilityId = 1_111_164;

    /// <summary>
    /// The extra element the FISTS entry carries in front of its own ability id. Not a typo for the
    /// 2016 fists ability (1111157) - both ids exist in August's own <c>AbilityEx.txt</c>.
    /// </summary>
    public const uint FistsExtraAbilityId = 1_111_278;

    /// <summary>Item 85, "Fists". <c>PlayerInventory.SurvivorFistsItemDefinitionId</c>.</summary>
    public const uint FistsItemDefinitionId = PlayerInventory.SurvivorFistsItemDefinitionId;

    /// <summary>August AbilityEx/ClientEffects punch combo; also used by the local Z1 reference.</summary>
    public const uint Z1FistsAbilityId = 1_111_157;

    /// <summary>
    /// Item 85 has ABILITY_ID=0, but August AbilityEx 1111157 and its ClientEffects
    /// explicitly define LeftJab, RightStraight, LeftHook, RightHook and RightUppercut.
    /// Advertise that ability here as well as in the melee fire mode.
    /// </summary>
    public static readonly uint FistsAbilityOverride = Z1FistsAbilityId;

    /// <summary><c>abilityLineId</c>. The owner initialises it to 1 and never increments it.</summary>
    private const uint AbilityLineId = 1;

    /// <summary><c>unknownDword3</c>, 2 in every entry of every packet he captured.</summary>
    private const uint EntryTrailerDword = 2;

    /// <summary><c>unknownByte</c>, always <c>0x40</c>.</summary>
    private const byte EntryTrailerByte = 0x40;

    /// <summary><c>deactivateAbility</c>'s two fixed dwords.</summary>
    private const uint UninitDword1 = 4;

    private const uint UninitDword2 = 3;

    /// <summary>
    /// <c>Abilities.UninitAbility</c> (<c>a0 03</c>), 14 bytes:
    /// <c>u8 0xa0; u8 0x03; u32 4; u32 abilityId; u32 3</c>. The owner's captured bytes for the
    /// fists, from his own session, are <c>a1 03 04000000 75f41000 03000000</c> - identical but for
    /// the base byte this build shifts down by one.
    /// </summary>
    public static byte[] UninitAbility(uint abilityId)
    {
        using var writer = new PacketWriter(16);
        writer.WriteByte(ZoneOpcodes.AbilitiesBase);
        writer.WriteByte(AbilityOpcodes.UninitAbilitySub);
        writer.WriteUInt32(UninitDword1);
        writer.WriteUInt32(abilityId);
        writer.WriteUInt32(UninitDword2);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// <c>Abilities.SetActivatableAbilityManager</c> (<c>a0 05</c>) for the loadout a
    /// <see cref="SetLoadoutSlots"/> is about to publish.
    /// <para>
    /// The client's own <c>pGetActivatableAbilities</c> emits the hardcoded head entry, then one
    /// entry per loadout occupant whose item definition resolves, in ascending slot order. An
    /// occupant with no <c>ClientItemDefinitions</c> row is <b>skipped</b>, exactly as the reference
    /// skips it - the two sides must agree on the entry count or they disagree on the length.
    /// </para>
    /// </summary>
    public static byte[] SetActivatableAbilityManager(SetLoadoutSlots loadout)
    {
        ArgumentNullException.ThrowIfNull(loadout);

        var entries = new List<(uint Slot, uint ItemDefinitionId)>(loadout.Slots.Count + 1)
        {
            (HeadEntryLoadoutSlotId, HeadEntryItemDefinitionId),
        };

        foreach (LoadoutSlotEntry entry in loadout.Slots.OrderBy(e => e.Slot.SlotId))
        {
            uint itemDefinitionId = entry.Slot.ItemDefinitionId;

            if (itemDefinitionId == 0 || !AugustAbilityFacts.HasRow(itemDefinitionId))
            {
                continue;
            }

            entries.Add((entry.Slot.SlotId, itemDefinitionId));
        }

        return SetActivatableAbilityManager(entries);
    }

    /// <summary>
    /// The writer proper, with no inventory behind it, so a test can feed it entries directly and
    /// demand exact bytes back.
    /// </summary>
    public static byte[] SetActivatableAbilityManager(
        IReadOnlyList<(uint Slot, uint ItemDefinitionId)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var writer = new PacketWriter(32 + (entries.Count * 40));
        writer.WriteByte(ZoneOpcodes.AbilitiesBase);
        writer.WriteByte(AbilityOpcodes.SetActivatableAbilityManagerSub);
        writer.WriteUInt32((uint)entries.Count);

        foreach ((uint slot, uint itemDefinitionId) in entries)
        {
            uint abilityId = AbilityIdOf(itemDefinitionId);
            bool fists = itemDefinitionId == FistsItemDefinitionId;

            writer.WriteUInt32(slot);              // loadoutSlotId
            writer.WriteUInt32(AbilityLineId);     // abilityLineId

            writer.WriteUInt32(fists ? 2u : 1u);   // unknownArray1 count

            if (fists)
            {
                // The fists carry an extra element IN FRONT of their own ability, and only the
                // fists do (the owner's ZoneAbilities.cs:275-285, from character.ts:1467-1477).
                writer.WriteUInt32(FistsExtraAbilityId);
                writer.WriteUInt32(FistsExtraAbilityId);
                writer.WriteUInt32(0);
            }

            writer.WriteUInt32(abilityId);         // unknownDword1 == abilityId
            writer.WriteUInt32(abilityId);         // unknownDword2 == abilityId, again
            writer.WriteUInt32(0);                 // unknownDword3

            writer.WriteUInt32(EntryTrailerDword); // outer unknownDword3, always 2
            writer.WriteUInt32(itemDefinitionId);  // itemDefinitionId
            writer.WriteByte(EntryTrailerByte);    // unknownByte, always 0x40
        }

        return writer.Written.ToArray();
    }

    /// <summary>
    /// The ability id this server publishes for an item: August's own column, except for the
    /// explicit fists punch-ability mapping (<see cref="FistsAbilityOverride"/>).
    /// </summary>
    public static uint AbilityIdOf(uint itemDefinitionId) =>
        itemDefinitionId == FistsItemDefinitionId && FistsAbilityOverride != 0
            ? FistsAbilityOverride
            : AugustAbilityFacts.AbilityIdOf(itemDefinitionId);

    /// <summary>
    /// The inbound half. Both <c>a0 01</c> and <c>a0 02</c> carry the ability id as a
    /// <c>u32</c> at <b>wire offset 10</b> and both end with a counted, printable hit-location
    /// string ("None" in every one of the owner's reference packets).
    /// </summary>
    /// <remarks>
    /// The trailing string is read <b>from the end</b>, not by offset. That is the owner's own
    /// reasoning (<c>ZoneAbilities.cs:722-728</c>) and it transfers unchanged: the two packets have
    /// different fixed prefixes and the middle of neither is settled, but a <c>u32</c> length
    /// followed by exactly that many printable bytes, ending the packet, is unambiguous.
    /// </remarks>
    public static bool TryReadAbilityRequest(
        ReadOnlySpan<byte> packet, out byte sub, out uint abilityId, out string hitLocation)
    {
        sub = 0;
        abilityId = 0;
        hitLocation = string.Empty;

        if (packet.Length < 14 || packet[0] != ZoneOpcodes.AbilitiesBase)
        {
            return false;
        }

        sub = packet[1];
        abilityId = BitConverter.ToUInt32(packet[10..14]);
        hitLocation = TrailingString(packet);
        return true;
    }

    /// <summary>The counted, printable string both ability packets end with, or empty.</summary>
    internal static string TrailingString(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 7)
        {
            return string.Empty;
        }

        for (int lengthAt = packet.Length - 5; lengthAt >= 2; lengthAt--)
        {
            uint length = BitConverter.ToUInt32(packet[lengthAt..(lengthAt + 4)]);

            if (length == 0 || length > 32 || lengthAt + 4 + (int)length != packet.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> text = packet.Slice(lengthAt + 4, (int)length);
            bool printable = true;

            foreach (byte b in text)
            {
                if (b is < 0x20 or > 0x7e)
                {
                    printable = false;
                    break;
                }
            }

            if (printable)
            {
                return System.Text.Encoding.ASCII.GetString(text);
            }
        }

        return string.Empty;
    }
}
