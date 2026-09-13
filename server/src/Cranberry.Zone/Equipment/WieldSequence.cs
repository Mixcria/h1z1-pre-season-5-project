using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Equipment;

/// <summary>
/// The eight-packet order that puts an item in the active hand, expressed against
/// ClientProtocol_1148 (docs/94, docs/95).
///
/// <para>
/// <b>PROVENANCE, corrected 2026-09-02 (wave 11, lane 0A).</b> Two different things are folded into
/// this file and they do not have the same standing.
/// </para>
/// <list type="bullet">
/// <item>
/// <b>The two packet BODIES are proven from the August binary.</b> <c>94 02</c> from
/// <c>FUN_140cd8be0</c> / <c>FUN_140cd4f90</c> and <c>94 03</c> from <c>FUN_140cd97c0</c>; the dumps
/// are under <c>out\ghidra-aug\wave10-equipslot</c> and <c>wave10-unsetslot</c>. Nothing about the
/// bytes rests on anyone else's server. See <c>EquipmentSlotPackets.cs</c>.
/// </item>
/// <item>
/// <b>The ORDER is hotbar behaviour observed in the owner's Z1 session logs</b>, adopted under D53
/// as his design and re-expressed here — his own implementation is
/// <c>C:\Z1\Server\Zone\ZoneInventoryActions.cs:873-1000</c>, whose own comment at :919-920
/// attributes the burst to a third-party session. So the order is <b>[I]</b>, a behaviour adopted
/// from the owner, not <b>[P]</b>: it is not evidence about the August client, and the first draft
/// of this file was wrong to describe it as an oracle measurement ("60 of 60 identical" over a tap
/// log). That claim, and the log it came from, are retracted — docs/00 names those files as a
/// forbidden third-party artefact.
/// </item>
/// </list>
///
/// <para>
/// <b>Why an order at all.</b> Cranberry's wield was one packet: a whole-character
/// <c>94 01 SetCharacterEquipment</c> that happened to carry a body-slot-7 row. On 2026-08-31 that
/// reached the client for the first time and the owner could not move or act until he dropped the
/// gun; the client sent <c>82 27 AimBlockedNotify</c> 19 ms later. The hotbar draw binds the slot
/// <b>last</b>, after it has torn the old ability down, freed the vacated slot, re-stated the item
/// and installed the new ability manager. Cranberry bound it first and did none of the rest. Two of
/// the steps have a reason of their own that does not depend on where the order came from: the
/// <c>11 04 + 11 02</c> re-state is the only way to change an item's <c>containerSlotId</c>, and
/// equipment goes last because the ability manager has to exist before the slot names an ability.
/// </para>
///
/// <para>
/// <b>The order, and where each id comes from.</b> Every id below is in the generated map and none
/// is among docs/84's nine deletions; the 1087 ids in brackets are the base-shift of docs/84, not a
/// measurement.
/// </para>
///
/// <list type="number">
/// <item><c>a0 03</c> <c>Abilities.UninitAbility</c> — tear the OLD weapon's ability down (1087 <c>a1 03</c>)</item>
/// <item><c>94 03</c> <c>Equipment.UnsetCharacterEquipmentSlot</c> — free the slot being vacated (1087 <c>95 03</c>)</item>
/// <item><c>11 04</c> <c>ClientUpdate.ItemDelete</c> — drop the stale instance (identical at both builds)</item>
/// <item><c>11 02</c> <c>ClientUpdate.ItemAdd</c> — re-state it with its new container/slot (identical)</item>
/// <item><b><c>82 ??</c> <c>Weapon.Weapon</c> — NOT SHIPPED HERE.</b> See <see cref="WeaponArmStepIsUnderived"/>.</item>
/// <item><c>86 04</c> <c>Loadout.SetLoadoutSlots</c> — refresh the wheel (1087 <c>87 04</c>)</item>
/// <item><c>a0 05</c> <c>Abilities.SetActivatableAbilityManager</c> — install the NEW ability (1087 <c>a1 05</c>)</item>
/// <item><c>94 02</c> <c>Equipment.SetCharacterEquipmentSlot</c> — <b>bind the item to the slot, last</b> (1087 <c>95 02</c>)</item>
/// </list>
///
/// <para>
/// <b>This is a hypothesis with an ordering behind it, not a proof (D29).</b> The two new packet
/// bodies are proven from the August binary (see <c>EquipmentSlotPackets.cs</c>); that this ORDER
/// helps at all is adopted behaviour, not evidence. It has since been shown NOT to be the fix: the
/// 19:42 session of 2026-08-31 ran exactly this sequence and froze identically (D143 — the freeze
/// is <c>WeaponDefinitions def+0x5c / +0x58 = 0</c>). It stays as the shape of a draw, gated by one
/// word.
/// </para>
/// </summary>
public static class WieldSequence
{
    /// <summary>
    /// <b>Step 5 of the eight is deliberately absent, and this is the reason.</b>
    ///
    /// <para>
    /// The draw the order was adopted from carries one <c>Weapon.Weapon</c> at wield time. Its 1087
    /// bytes are not reproduced here — they were read from a forbidden third-party artefact
    /// (docs/00), and the point below stands without them: whatever the 1087 envelope was, it is
    /// not the 1148 one.
    /// </para>
    /// <para>
    /// <b>It does not port, and that is now a finding rather than an open question.</b> docs/20 §2
    /// derived the 1148 family prefix from the client's own dispatcher <c>FUN_140b07010</c> and it
    /// is <b>six</b> bytes — <c>[0] u8 0x82; [1..4] u32 (read and never used); [5] u8 sub</c> — with
    /// <b>no wrapper byte</b>. The 1087 packet has <b>seven</b>: a <c>0x00</c> sits between the base and
    /// the game time, and only then comes <c>0x15</c>. The two builds do not share this envelope.
    /// </para>
    /// <para>
    /// Nor does the payload line up. At 1148 <c>82 15</c> is <c>WeaponPacket::cIdRemoteWeaponBase</c>,
    /// and docs/20 §3 shows it is a <b>two-stage</b> packet: <c>FUN_140b07010</c> case <c>0x15</c>
    /// parses only an outer envelope with <c>FUN_140a64a30</c>, then switches on a remote sub at
    /// <c>rec+0x20</c> (1..7; <c>0x02</c> is <c>cIdAddWeapon</c>), whose body is a <b>varint</b>
    /// owner network id followed by a nested weapon-state blob. The 1087 packet's 21 bytes are flat: no varint,
    /// no blob length, and the byte in the remote-sub position is <c>0x04</c>, which is not one of
    /// the named remote subs.
    /// </para>
    /// <para>
    /// So this is the one part of the draw that is a genuine 1087→1148 <b>redesign</b> rather than a
    /// renumbering — the exception to docs/84's headline.
    /// </para>
    /// <para>
    /// <b>CORRECTION, 2026-08-31 19:2x (the wave-10 follow-up; docs/95's correction block, D186,
    /// and S5c §2 which later settled it).</b> An earlier draft of this note called
    /// <c>82 15</c> "the next needle". It is not. The August registration table names it
    /// <c>WeaponPacket::cIdRemoteWeaponBase</c>, with a nested tree
    /// <c>82 15 01..07</c> / <c>82 15 04 01..10</c> whose members are <c>Remote::cIdAddWeapon</c>,
    /// <c>RemoteWeaponUpdate::cTypeFireState</c> and so on. <b>It is how OTHER players' weapons are
    /// replicated to this client</b> — it was never the packet that arms the local player's own
    /// hand, so it cannot be what the shipped sequence is missing. Building it would make other
    /// people's guns visible, which is worth doing and is not this problem.
    /// </para>
    /// </summary>
    public const string WeaponArmStepIsUnderived =
        "Step 5 (Weapon.Weapon) is not shipped and does not port: 1087 has a 7-byte prefix where "
        + "1148 has 6 (docs/20 s2), and 1148's 82 15 is a two-stage Remote:: envelope with a varint "
        + "and a nested blob where the 1087 one is flat. It must be built from the August side. See docs/95 s3.";

    /// <summary>One packet of the sequence: a label for the log, and the bytes.</summary>
    public readonly record struct Step(string Label, Action<PacketWriter> Write);

    /// <summary>
    /// The ordered packets for drawing <paramref name="drawnItem"/> into
    /// <paramref name="activeHandSlotId"/>.
    /// </summary>
    /// <param name="characterGuid">The player's own character guid.</param>
    /// <param name="drawnItem">The item record being drawn, already in its new loadout binding.</param>
    /// <param name="drawnMesh">The third-person attachment for the drawn item.</param>
    /// <param name="activeHandSlotId">Body slot 7.</param>
    /// <param name="vacatedBodySlotId">
    /// The body slot the drawn item is LEAVING, or 0 when it came from the ground and vacates
    /// nothing. The adopted draw sends step 2 only when a slot is actually being freed.
    /// </param>
    /// <param name="previousAbilityId">
    /// The ability of the weapon leaving the hand, or 0 when the hand was empty. The adopted draw sends step 1
    /// only when there is something to uninitialise.
    /// </param>
    /// <param name="loadout">The refreshed wheel, for steps 6 and 7.</param>
    /// <param name="sendAbilityManager">
    /// <c>CombatOptions.SendAbilityManager</c>. When it is off, step 7 is skipped — the same rule
    /// <c>SendLoadoutSlots</c> already applies, so the two paths cannot disagree.
    /// </param>
    /// <param name="weapons">Supplies the Weapon-tail <c>ItemAdd</c> and the active-hand clearance.</param>
    /// <param name="itemAlreadyGranted">
    /// The client already has the item's current container/slot and skin, either from pickup or
    /// an earlier loadout grant. Keep that instance alive. Only a changed item placement needs
    /// the delete/re-add update; selecting an existing hotbar slot changes equipment alone.
    /// </param>
    public static IReadOnlyList<Step> Build(
        ulong characterGuid,
        InventoryItem drawnItem,
        CharacterEquipmentAttachment drawnMesh,
        uint activeHandSlotId,
        uint vacatedBodySlotId,
        uint previousAbilityId,
        SetLoadoutSlots loadout,
        bool sendAbilityManager,
        WeaponSession weapons,
        bool itemAlreadyGranted = false)
    {
        ArgumentNullException.ThrowIfNull(drawnItem);
        ArgumentNullException.ThrowIfNull(drawnMesh);
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(weapons);

        var steps = new List<Step>(8);

        // 1. a0 03 — only when the hand held something. Uninitialising an ability that was never
        //    initialised is a packet the client has no state for, and the adopted draw does not send one.
        if (previousAbilityId != 0)
        {
            byte[] uninit = AbilityPackets.UninitAbility(previousAbilityId);
            steps.Add(new Step($"a0 03 UninitAbility({previousAbilityId})", w => w.WriteRaw(uninit)));
        }

        // 2. 94 03 — only when a slot is actually vacated. A ground pickup vacates nothing.
        if (vacatedBodySlotId != 0)
        {
            // The reference capture uses profile 5 for full dresses and 3 for slot deltas.
            // Match the incremental packet pair (skins-and-colours.md section on profile ids).
            var unset = new UnsetCharacterEquipmentSlot(characterGuid, vacatedBodySlotId, ProfileId: 3);
            steps.Add(new Step($"94 03 UnsetCharacterEquipmentSlot(slot {vacatedBodySlotId})", unset.WriteTo));
        }

        // 3 + 4. 11 04 then 11 02 — the delete/re-add pair that makes the client rebuild the item
        //        object rather than patch it. FUN_140c35400 fires *changed* rather than *added* for
        //        a guid it already holds, which is the docs/46 §6a rule; the adopted draw removes it first so the
        //        add is a genuine add. A fresh pickup was already sent in its final placement;
        //        rebuilding it again would repeat factory/tail parsing, OnItemReceived, and the
        //        recipe/UI refreshes in FUN_140c35400 / FUN_140dbf5b0 with identical item data.
        ulong itemGuid = drawnItem.ItemGuid;
        if (!itemAlreadyGranted)
        {
            steps.Add(new Step($"11 04 ItemDelete({itemGuid})",
                w => new ItemDelete(characterGuid, itemGuid).WriteTo(w)));
            steps.Add(new Step($"11 02 ItemAdd({itemGuid})",
                weapons.CreateItemAdd(characterGuid, drawnItem)));
        }

        // 5. 82 ?? Weapon.Weapon — NOT SHIPPED. See WeaponArmStepIsUnderived.

        // 6. 86 04 — the wheel, before the ability manager reads it.
        steps.Add(new Step("86 04 SetLoadoutSlots", loadout.WriteTo));

        // 7. a0 05 — built FROM that same loadout, so the two cannot disagree on the entry count.
        if (sendAbilityManager)
        {
            byte[] manager = AbilityPackets.SetActivatableAbilityManager(loadout);
            steps.Add(new Step("a0 05 SetActivatableAbilityManager", w => w.WriteRaw(manager)));
        }

        // 8. 94 02 — the binding, LAST. This is the whole point of the ordering.
        var bind = new SetCharacterEquipmentSlot(
            characterGuid,
            new EquipmentSlotRow(activeHandSlotId, itemGuid, TintAlias: "Default", DecalAlias: "#"),
            drawnMesh,
            Clearance: weapons.Clearance);

        // Guard 5: a refused binding means the WHOLE sequence is wrong to send. Steps 1-7 without
        // step 8 would leave the client with the old item deleted and nothing bound - strictly
        // worse than not wielding at all.
        if (!bind.IsPermitted)
        {
            return [];
        }

        steps.Add(new Step($"94 02 SetCharacterEquipmentSlot(slot {activeHandSlotId}, item {itemGuid})",
            bind.WriteTo));
        return steps;
    }
}
