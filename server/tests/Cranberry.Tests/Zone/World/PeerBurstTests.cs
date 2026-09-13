using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

/// <summary>
/// docs/109 lane 3C: the bytes one viewer is sent about one other player. The ORDER of the enter
/// burst and the three <c>82 15</c> invariants are client facts (docs/100 §1, §4a) and this is
/// where they are pinned.
/// </summary>
public sealed class PeerBurstTests
{
    [Fact]
    public void LateObserverReceivesKnownWeaponStanceAfterActorAndWeaponCreation()
    {
        var subject = Subject(armed: true);
        subject.WeaponStance = 3;
        List<byte[]> burst = [];
        PeerBurst.Enter(subject, TransientIdTable.FirstAllocated, burst);
        Assert.Equal(new Cranberry.Zone.Weapons.WeaponStance(subject.CharacterGuid, 3).ToArray(), burst[^1]);
        subject.ResetWorldPose();
        PeerBurst.Enter(subject, TransientIdTable.FirstAllocated, burst);
        Assert.DoesNotContain(burst, p => p.Length >= 2 && p[0] == 0x0f && p[1] == 0x20);
    }
    private sealed class NullSink : IPeerSink
    {
        public bool IsOpen => true;

        public void Send(byte[] zonePacket)
        {
        }
    }

    private static PeerSession Subject(bool armed = false)
    {
        var session = new PeerSession(0x1002, new NullSink())
        {
            CharacterName = "Bob",
            ModelId = 9469,
            InMatch = true,
            Position = new Vector3(-233.83f, 506.36f, -4892.03f),
            Dress = [new CharacterEquipmentAttachment("SurvivorMale_Head_01.adr", SlotId: 1)],
        };

        if (armed)
        {
            session.HeldWeaponItemGuid = 0x2000_0000_0000_0007;
            session.HeldWeaponDefinitionId = 2425;              // the August AR-15 (docs/02, 2026-08-29)
        }

        return session;
    }

    [Fact]
    public void AnUnarmedPeerIsSpawnedDressedAndResetInThatOrder()
    {
        List<byte[]> burst = [];
        PeerBurst.Enter(Subject(), TransientIdTable.FirstAllocated, burst);
        Assert.Single(burst, p => p[0] == 0xdc && p[1] == 2);
        burst.RemoveAll(p => p[0] == 0xdc);

        Assert.Equal(4, burst.Count);

        // 1. d5 AddLightweightPc — first because 82 15's lookup only returns entities it created.
        Assert.Equal(ZoneOpcodes.AddLightweightPc, burst[0][0]);
        Assert.Equal(0x1002ul, BinaryPrimitives.ReadUInt64LittleEndian(burst[0].AsSpan(1)));

        // 2. 94 01 SetCharacterEquipment FOR THE PEER'S GUID — the writer takes the character id as
        // a parameter, which is exactly why no new writer was needed for the remote dress.
        Assert.Equal(ZoneOpcodes.EquipmentBase, burst[1][0]);
        Assert.Equal(0x01, burst[1][1]);
        Assert.Equal(0x1002ul, BinaryPrimitives.ReadUInt64LittleEndian(burst[1].AsSpan(6)));

        // 3. d9 clears pending full data and creates the weapon manager.
        Assert.Equal(0xd9, burst[2][0]);

        // 4. 82 15 01 RemoteWeapon.Reset.
        Assert.Equal(ZoneOpcodes.WeaponBase, burst[3][0]);
        Assert.Equal(0x15, burst[3][5]);
        Assert.Equal(0x01, burst[3][6]);
    }

    [Fact]
    public void AnArmedPeerAddsTheHeldWeaponAfterTheReset()
    {
        List<byte[]> burst = [];
        PeerBurst.Enter(Subject(armed: true), TransientIdTable.FirstAllocated, burst);
        Assert.Single(burst, p => p[0] == 0xdc && p[1] == 2);
        burst.RemoveAll(p => p[0] == 0xdc);

        Assert.Equal(6, burst.Count);
        Assert.Equal(0x01, burst[3][6]);                        // Reset first: it destroys the lot
        Assert.Equal(0x02, burst[4][6]);                        // then AddWeapon
        Assert.Equal(Cranberry.Zone.Combat.RemoteWeaponPackets.SwitchFireMode(
            TransientIdTable.FirstAllocated, 0x2000_0000_0000_0007, 0, 0), burst[5]);

        // gameTime is 0 in every writer of the family — never a server timestamp, which would queue
        // the packet behind the client's own interpolated clock (docs/100 §4a).
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(burst[4].AsSpan(1)));

        // The owner id is the VIEWER's id for the OTHER character, one client varint after the sub
        // byte: 16 packs as (16 << 2) | 0 extra bytes = 0x40, one byte. weaponItemInstanceId — the
        // guid GuardEquipmentRowGuid refuses to let be 0 — follows it.
        Assert.Equal(1, ClientVarInt.Length(TransientIdTable.FirstAllocated));
        Assert.Equal(TransientIdTable.FirstAllocated << 2, burst[4][7]);
        Assert.Equal(0x2000_0000_0000_0007ul, BinaryPrimitives.ReadUInt64LittleEndian(burst[4].AsSpan(8)));
    }

    [Theory]
    [InlineData(2425u, 6u, 6u, 30u)] // AR-15: item ID differs from WEAPON_ID.
    [InlineData(1374u, 1374u, 16u, 6u)] // Shotgun: group ID differs from both.
    public void EnterAndWeaponSwitchGiveTheNativeWeaponItsDefinitionAndFireGroup(uint item, uint weapon, uint group, uint clip)
    {
        var subject = Subject(armed: true);
        subject.HeldWeaponDefinitionId = item;
        List<byte[]> burst = [];
        for (int change = 0; change < 2; change++)
        {
            if (change == 0) PeerBurst.Enter(subject, 16, burst);
            else PeerBurst.Redress(subject, 16, true, burst);
            byte[] add = Assert.Single(burst, p => p[0] == 0x82 && p[5] == 0x15 && p[6] == 2);
            Assert.Equal(add.Length - 20, BinaryPrimitives.ReadInt32LittleEndian(add.AsSpan(16)));
            Assert.Equal(weapon, BinaryPrimitives.ReadUInt32LittleEndian(add.AsSpan(20)));
            Assert.Equal(7, add[24]); // Active hand.
            Assert.Equal(1, add[25]); // Native loader must keep one firing group.
            Assert.Equal(group, BinaryPrimitives.ReadUInt32LittleEndian(add.AsSpan(26)));
            Assert.Equal(2, add[30]); // Both runtime modes need explicit charge.
            Assert.Equal(clip, BinaryPrimitives.ReadUInt32LittleEndian(add.AsSpan(31)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(add.AsSpan(39))); // Trigger mode.
            byte[] select = Assert.Single(burst, p => p[0] == 0x82 && p[5] == 0x15
                && p[6] == 4 && p[8] == 6);
            Assert.Equal(Cranberry.Zone.Combat.RemoteWeaponPackets.SwitchFireMode(
                16, subject.HeldWeaponItemGuid, 0, 0), select);
            Assert.True(burst.IndexOf(select) > burst.IndexOf(add));
        }
    }

    /// <summary>
    /// D322 (docs/106 §13): the re-dress a viewer that already has the peer spawned is sent when
    /// the peer's own <c>94 01</c> changed - the dress alone when only the outfit moved, the dress
    /// then the enter burst's <c>82 15</c> pair when the gun in the hand changed.
    /// </summary>
    [Fact]
    public void AReDressIsTheDressAloneUnlessTheHandChanged()
    {
        List<byte[]> burst = [];

        PeerBurst.Redress(Subject(armed: true), TransientIdTable.FirstAllocated, rearm: false, burst);
        Assert.Single(burst, p => p[0] == 0xdc && p[1] == 2);
        burst.RemoveAll(p => p[0] == 0xdc);
        byte[] dress = Assert.Single(burst);
        Assert.Equal(ZoneOpcodes.EquipmentBase, dress[0]);
        Assert.Equal(0x01, dress[1]);
        Assert.Equal(0x1002ul, BinaryPrimitives.ReadUInt64LittleEndian(dress.AsSpan(6)));

        PeerBurst.Redress(Subject(armed: true), TransientIdTable.FirstAllocated, rearm: true, burst);
        Assert.Single(burst, p => p[0] == 0xdc && p[1] == 2);
        burst.RemoveAll(p => p[0] == 0xdc);
        Assert.Equal(4, burst.Count);
        Assert.Equal(ZoneOpcodes.EquipmentBase, burst[0][0]);
        Assert.Equal(0x01, burst[1][6]);                        // Reset first, as on enter
        Assert.Equal(0x02, burst[2][6]);                        // then the held gun
        Assert.Equal(Cranberry.Zone.Combat.RemoteWeaponPackets.SwitchFireMode(
            TransientIdTable.FirstAllocated, 0x2000_0000_0000_0007, 0, 0), burst[3]);
        Assert.Equal(0x2000_0000_0000_0007ul, BinaryPrimitives.ReadUInt64LittleEndian(burst[2].AsSpan(8)));

        // An emptied hand: the Reset alone clears the viewer's arsenal, and nothing is added.
        PeerBurst.Redress(Subject(), TransientIdTable.FirstAllocated, rearm: true, burst);
        Assert.Single(burst, p => p[0] == 0xdc && p[1] == 2);
        burst.RemoveAll(p => p[0] == 0xdc);
        Assert.Equal(2, burst.Count);
        Assert.Equal(0x01, burst[1][6]);
    }

    [Fact]
    public void TheOwnerIdMayNeverBeTheViewersOwnActorOrTheNoNetworkIdSentinel()
    {
        List<byte[]> burst = [];

        // FUN_140b07010 case 0x15 drops the packet when the owner id equals world+0x324f8, which is
        // what the self record's +0xe0 varint set — so 1 is a self-echo and 0 is the sentinel.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PeerBurst.Enter(Subject(), TransientIdTable.LocalPlayer, burst));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PeerBurst.Enter(Subject(), 0, burst));
    }

    [Fact]
    public void ThePoseRelayIsTheOpcodeTheIdAndTheClientsOwnRecordUnchanged()
    {
        byte[] record = MovementRecord.Position(new Vector3(1.005f, 2f, 3f));

        byte[] relayed = PeerBurst.Pose(TransientIdTable.FirstAllocated, record);

        Assert.Equal(ZoneOpcodes.PlayerUpdatePosition, relayed[0]);
        int varIntLength = ClientVarInt.Length(TransientIdTable.FirstAllocated);
        Assert.Equal(1 + varIntLength + record.Length, relayed.Length);

        // Byte for byte the client's own record. Never a re-encode: the pose is quantised at two
        // decimal places, so a decode/re-encode round trip would move a peer for no reason and drop
        // any field this server does not yet parse (docs/100 §5).
        Assert.Equal(record, relayed[(1 + varIntLength)..]);
    }

    [Fact]
    public void TheDespawnIsTheTwelveByteRemovePlayerForTheCharactersGuid()
    {
        byte[] leave = PeerBurst.Leave(0x1002);

        Assert.Equal(RemovePlayer.Length, leave.Length);
        Assert.Equal(ZoneOpcodes.CharacterBase, leave[0]);
        Assert.Equal(0x01, leave[1]);
        Assert.Equal(0x1002ul, BinaryPrimitives.ReadUInt64LittleEndian(leave.AsSpan(2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(leave.AsSpan(10)));  // no ragdoll
    }

    [Fact]
    public void TheSpawnRecordIsExactLengthGatedAndTheWriterAgreesWithItsOwnLengthFunction()
    {
        PeerSession subject = Subject();
        var record = new PeerCharacterRecord
        {
            Guid = subject.CharacterGuid,
            TransientId = TransientIdTable.FirstAllocated,
            Identity = new SelfIdentity { Name = subject.CharacterName },
            ModelId = subject.ModelId,
            Position = subject.Position,
            Rotation = subject.Rotation,
        };

        // FUN_140af3950 case 0xd5 applies the record only when the reader set no error byte AND the
        // buffer is exhausted; one trailing byte and the spawn is dropped in silence.
        Assert.Equal(
            PeerSpawnWriter.AddLightweightPcLength(record),
            PeerSpawnWriter.AddLightweightPc(record).Length);
    }

    [Fact]
    public void PromotionIsNotRepeatedWhenAnExistingPeerChangesWeapons()
    {
        var subject = Subject(armed: true);
        List<byte[]> burst = [];
        PeerBurst.Enter(subject, 16, burst);
        Assert.Single(burst, p => p[0] == 0xd9);
        subject.HeldWeaponItemGuid++;
        PeerBurst.Redress(subject, 16, true, burst);
        Assert.DoesNotContain(burst, p => p[0] == 0xd9);
    }
}
