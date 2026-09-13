using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.World;

/// <summary>
/// The bytes one viewer is sent about one other player: the enter burst, the pose relay and the
/// despawn. Lane 3C's whole wire surface, as a pure function of a <see cref="PeerSession"/> and the
/// transient id the viewer knows it by.
///
/// <para>
/// <b>Why it is separate from the sending.</b> The order of the enter burst is a client fact
/// (docs/100 §1) and the three <c>82 15</c> invariants are client facts (docs/100 §4a); a rule of
/// that kind belongs somewhere a test can read it without a socket, a session or a match. What
/// <c>ZoneService</c> keeps is the decision of <em>when</em> and <em>to whom</em>.
/// </para>
/// </summary>
public static class PeerBurst
{
    private static RemoteWeaponBlob HeldWeapon(PeerSession subject)
    {
        // The native component looks up WEAPON_ID, not the inventory item ID.
        // Its group loader sizes the runtime array to the wire count: an empty
        // array leaves a visible gun with no firing modes or firing audio.
        uint item = subject.HeldWeaponDefinitionId;
        if (Weapons.WeaponItemProfiles.TryGet(item, out var weapon)
            && Weapons.AugustWeaponTable.HasFireGroup(item, out uint group))
            return new(weapon.WeaponId, RemoteWeaponBlob.RightHandSlot,
                [new RemoteFireGroup(group,
                    [new RemoteFireMode((uint)Math.Max(0, weapon.ClipSize)),
                     new RemoteFireMode(Weapons.AugustWeaponTable.TriggerChargeSentinel)])]);
        return new(Combat.RetailBalance.WeaponDefinitionIdFor(item), RemoteWeaponBlob.RightHandSlot, []);
    }

    private static void AddHeldWeapon(PeerSession subject, uint transientId, List<byte[]> into)
    {
        var weapon = HeldWeapon(subject);
        into.Add(RemoteWeaponPackets.AddWeapon(transientId, subject.HeldWeaponItemGuid, weapon));
        // August's constructor starts current group/mode at -1. AddWeapon loads the
        // descriptor, and Reset(stateCount: 0) does not load selection state. Explicitly
        // select the retained, charged mode through its existing native handler.
        // An unknown descriptor must not reference a group/mode that was never created.
        byte group = subject.HeldFireGroupIndex, mode = subject.HeldFireModeIndex;
        if (group < weapon.FireGroups.Count && mode < weapon.FireGroups[group].Modes.Count
            && weapon.FireGroups[group].Modes[mode].Charge is > 0 and <= int.MaxValue)
            into.Add(RemoteWeaponPackets.SwitchFireMode(transientId, subject.HeldWeaponItemGuid, (sbyte)group, (sbyte)mode));
    }
    /// <summary>
    /// The enter burst, in the order docs/100 §1 fixes and <see cref="InterestSystem.EnterBurst"/>
    /// names. Appended to <paramref name="into"/>, which is cleared first.
    ///
    /// <list type="number">
    /// <item><c>d5 AddLightweightPc</c> — <b>first, and not by preference</b>. It creates the
    /// entity every later packet needs: <c>82 15</c>'s lookup <c>FUN_140aeb750</c> only returns
    /// entities carrying the <c>"ProxiedCharacter"</c> type descriptor, and <c>d9</c> tests the
    /// <c>entity+0x37ec &amp; 0x40</c> bit this packet's handler sets.</item>
    /// <item><c>94 01 SetCharacterEquipment</c> for the peer's guid — the remote dress. The same
    /// packet the local player's own dress uses, written for another character id, so no new writer
    /// exists for it. It deliberately carries <b>no equipment-slot rows</b>: those are inventory
    /// identity for the client they name, a body-slot-7 row is what killed the client in docs/45,
    /// and a peer's inventory is not this viewer's business.</item>
    /// <item><c>d9 LightweightToFullPc</c> creates the remote weapon manager and clears the
    /// pending-full-data bit. Every <c>82 15</c> message is rejected until this succeeds.</item>
    /// <item><c>82 15 01 RemoteWeapon.Reset</c> — the arsenal. Its handler destroys every weapon
    /// already registered against the entity before it reads a byte, which is what makes the state
    /// well defined however many times a peer enters and leaves.</item>
    /// <item><c>82 15 02 RemoteWeapon.AddWeapon</c>, only for a character with a gun in its hand.
    /// Slot 7 is what fires the client's "weapon entered this character's hand" listeners.</item>
    /// </list>
    ///
    /// <para>
    /// <b>The three invariants, enforced by the writers themselves.</b> The owner id is the
    /// <em>viewer's</em> id for the OTHER character — <c>RemoteWeaponPackets.GuardOwner</c> refuses
    /// both <see cref="TransientIdTable.LocalPlayer"/> and 0; <c>weaponItemInstanceId</c> is the
    /// item instance guid, which <c>GuardEquipmentRowGuid</c> refuses to let be 0; and
    /// <c>gameTime</c> is not a parameter of any writer in the family, because a server timestamp
    /// there queues the packet behind a clock this server does not own.
    /// </para>
    /// </summary>
    public static void Enter(PeerSession subject, uint transientId, List<byte[]> into)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        into.Add(PeerSpawnWriter.AddLightweightPc(new PeerCharacterRecord
        {
            Guid = subject.CharacterGuid,
            TransientId = transientId,
            Identity = new SelfIdentity { Name = subject.CharacterName },
            ModelId = subject.ModelId,
            Position = subject.Position,
            Rotation = subject.Rotation,
            Field178 = subject.MovementVersion,
        }));

        into.Add(Dress(subject));
        into.Add(FullCharacter(subject, transientId));
        into.Add(Movement.Footwear.AudioPacket(subject.CharacterGuid, subject.Footwear));
        into.Add(RemoteWeaponPackets.Reset(transientId, []));

        if (subject.HeldWeaponItemGuid != 0 && subject.HeldWeaponDefinitionId != 0)
        {
            AddHeldWeapon(subject, transientId, into);
        }
        if (subject.WeaponStance is uint stance)
            into.Add(new WeaponStance(subject.CharacterGuid, stance).ToArray());
    }

    /// <summary>
    /// <b>The re-dress</b> (D322, docs/106 §13): what a viewer that ALREADY has
    /// <paramref name="subject"/> spawned is sent when the subject's own <c>94 01</c> changed. The
    /// dress is the same packet the enter burst carries, under the same guid, so a skinned rifle
    /// drawn after the viewer entered reaches the viewer with the same rows and group the
    /// subject's own client got. When <paramref name="rearm"/> is set - the gun in the hand changed
    /// - the <c>82 15 01 Reset</c> / <c>82 15 02 AddWeapon</c> pair follows, in the enter burst's
    /// order and under its three invariants: Reset first, because its handler destroys every weapon
    /// already registered against the entity before it reads a byte; AddWeapon only for a gun.
    /// Appended to <paramref name="into"/>, which is cleared first.
    /// </summary>
    public static void Redress(PeerSession subject, uint transientId, bool rearm, List<byte[]> into)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(into);
        Redress(subject, transientId, rearm, Dress(subject),
            Movement.Footwear.AudioPacket(subject.CharacterGuid, subject.Footwear), into);
    }

    // Dress and footstep packets name the character GUID and are identical for every viewer.
    // Prepare these once per broadcast; weapon packets still use each viewer's transient ID.
    internal static void Redress(PeerSession subject, uint transientId, bool rearm,
        byte[] dress, byte[] footsteps, List<byte[]> into, IReadOnlyList<byte[]>? deltas = null)
    {
        into.Clear();
        if (deltas is null) into.Add(dress);
        else into.AddRange(deltas);
        into.Add(footsteps);
        if (!rearm)
        {
            return;
        }

        into.Add(RemoteWeaponPackets.Reset(transientId, []));
        if (subject.HeldWeaponItemGuid != 0 && subject.HeldWeaponDefinitionId != 0)
        {
            AddHeldWeapon(subject, transientId, into);
        }
        if (subject.WeaponStance is uint stance)
            into.Add(new WeaponStance(subject.CharacterGuid, stance).ToArray());
    }

    /// <summary>Promotion precedes every remote arsenal on interest entry; never repeat on redress.</summary>
    public static byte[] FullCharacter(PeerSession subject, uint transientId) => FullCharacterPackets.Promote(
        new PeerCharacterRecord { Guid = subject.CharacterGuid, TransientId = transientId,
            Position = subject.Position, Rotation = subject.Rotation, Field178 = subject.MovementVersion },
        subject.Dress, subject.RelayPose);

    /// <summary>The peer's <c>94 01 SetCharacterEquipment</c> — the dress, under the peer's guid.</summary>
    public static byte[] Dress(PeerSession subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        using var writer = new Protocol.PacketWriter();
        new SetCharacterEquipment(subject.CharacterGuid, Attachments: subject.Dress).WriteTo(writer);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// One relayed pose: <c>78 | varint transientId | the mover's own record</c>.
    ///
    /// <para>
    /// <b>The opcode-framed form of the two proven ones</b> (docs/100 §5). <c>RelaySystem</c> writes
    /// the opcode-free one because it writes on channel 2, where <c>FUN_140dd1400</c> synthesises
    /// <c>0x78</c> from the channel number; everything <c>ZoneService.SendTunnel</c> sends goes out
    /// on channel 0, so the opcode has to be in the bytes. Both reach the same reader through the
    /// same vtable slot <c>+0x250</c>.
    /// </para>
    ///
    /// <para><b>The record is never re-encoded</b> — it is the mover's own bytes, and the only
    /// thing that changes between two viewers is the varint in front of it.</para>
    /// </summary>
    public static byte[] Pose(uint transientId, ReadOnlySpan<byte> record) =>
        PeerSpawnWriter.PlayerUpdatePosition(transientId, record);

    /// <summary>
    /// The despawn: <c>0f 01 RemovePlayer</c>, twelve bytes, with the effect flag 0 that removes
    /// the actor without the death presentation.
    /// </summary>
    public static byte[] Leave(ulong characterGuid) => PeerSpawnWriter.Despawn(characterGuid);
}
