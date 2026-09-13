using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Equipment;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

public sealed class PeerEquipmentDeltaTests
{
    private sealed class Sink : IPeerSink
    {
        public bool IsOpen => true;
        public void Send(byte[] bytes) { }
    }
    [Fact]
    public void RemoteHandCarriesItsMeshAndItemBindingBeforeRemoteWeaponState()
    {
        const ulong subjectId = 10, observerId = 20, itemId = 30;
        var hand = new SetCharacterEquipmentSlot(subjectId, new(7, itemId),
            new CharacterEquipmentAttachment("Weapon_M16A4_3P.adr", 7, AppearanceIds: [97u, 98u]));
        using var writer = new PacketWriter();
        hand.WriteForObserverTo(writer, observerId);
        byte[] delta = writer.Written.ToArray();
        Assert.Equal(new byte[] { 0x94, 2 }, delta[..2]);
        Assert.Equal(subjectId, BitConverter.ToUInt64(delta, 6));
        Assert.Equal(7u, BitConverter.ToUInt32(delta, 14));
        Assert.Equal(7u, BitConverter.ToUInt32(delta, 18));
        Assert.Equal(itemId, BitConverter.ToUInt64(delta, 22));
        Assert.Contains("Weapon_M16A4_3P.adr", System.Text.Encoding.UTF8.GetString(delta));
        var subject = new PeerSession(subjectId, new Sink()) { HeldWeaponItemGuid = itemId, HeldWeaponDefinitionId = 2425 };
        List<byte[]> burst = [];
        PeerBurst.Redress(subject, 16, true, [], [0xdc, 2], burst, [delta]);
        Assert.Same(delta, burst[0]); Assert.Equal(5, burst.Count);
        Assert.Equal(1, burst[2][6]); Assert.Equal(2, burst[3][6]);
        Assert.DoesNotContain(burst, p => p[0] == 0x94 && p[1] == 1);
    }
    [Fact]
    public void RemoteBranchCannotBeUsedForTheLocalPlayerAndDoesNotWeakenItsGuard()
    {
        var hand = new SetCharacterEquipmentSlot(10, new(7, 30), new("Weapon_M16A4_3P.adr", 7));
        using var writer = new PacketWriter();
        Assert.False(hand.IsPermitted);
        Assert.Throws<InvalidOperationException>(() => hand.WriteTo(writer));
        Assert.Throws<ArgumentException>(() => hand.WriteForObserverTo(writer, 10));
        Assert.Throws<ArgumentException>(() => hand.WriteForObserverTo(writer, 0));
        Assert.Throws<ArgumentException>(() => (hand with { CharacterId = 0 }).WriteForObserverTo(writer, 20));
        Assert.Throws<ArgumentException>(() => (hand with { Row = new(7, 0) }).WriteForObserverTo(writer, 20));
        Assert.Empty(writer.Written.ToArray());
    }
}
