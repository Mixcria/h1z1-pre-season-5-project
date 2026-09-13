using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    private static byte[][] ViewerPackets(Fixture f, SoeConnection viewer, int mark = 0) =>
        [.. f.Recorder.Routed.Skip(mark).Where(r => ReferenceEquals(r.Connection, viewer)).Select(r => r.Packet[1..])];

    private static void AssertPromotedBeforeWeapons(byte[][] packets, ulong guid, uint transient)
    {
        int spawn = Array.FindIndex(packets, p => p.Length > 9 && p[0] == 0xd5
            && BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(1)) == guid);
        Assert.True(spawn >= 0);
        byte[] encoded = BitConverter.GetBytes((transient << 2) | (uint)(Cranberry.Zone.ClientVarInt.Length(transient) - 1));
        int full = Array.FindIndex(packets, spawn + 1, p => p[0] == 0xd9
            && p.AsSpan(6, Cranberry.Zone.ClientVarInt.Length(transient))
                .SequenceEqual(encoded.AsSpan(0, Cranberry.Zone.ClientVarInt.Length(transient))));
        Assert.True(full > spawn);
        int firstWeapon = Array.FindIndex(packets, spawn + 1, p => p[0] == 0x82 && p[5] == 0x15);
        Assert.True(firstWeapon > full, "August drops every remote weapon message while full data is pending.");
    }

    [Fact]
    public void NativePlayersAndLateViewersReceivePromotionBeforeTheArsenal()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, Vector3.UnitX);
        CombatInterest(f);
        AssertPromotedBeforeWeapons(ViewerPackets(f, viewer), 1, CombatOwner(shooter, viewer));
        var late = f.Add(3, Vector3.UnitX * 2);
        CombatInterest(f);
        AssertPromotedBeforeWeapons(ViewerPackets(f, late), 1, CombatOwner(shooter, late));
    }

    [Fact]
    public void SharedBotsAndLateViewersUseTheSamePromotionGateAsPlayers()
    {
        using var f = new Fixture();
        f.Service.Post = _ => { };
        var owner = f.Add(1, BotTestOrigin);
        var viewer = f.Add(2, BotTestOrigin + Vector3.UnitX);
        var outside = f.Add(3, BotTestOrigin, matchId: 2);
        int mark = f.Recorder.Routed.Count;
        Assert.True(Bots(f, owner, "spawn").Ok);
        var target = Assert.Single(Get<SessionCombat>(viewer.Tag!, "Combat").Targets.All);
        AssertPromotedBeforeWeapons(ViewerPackets(f, viewer, mark), target.WorldGuid, target.TransientId);
        Assert.DoesNotContain(ViewerPackets(f, outside, mark), p => p[0] == 0xd9);
        var late = f.Add(4, BotTestOrigin);
        int lateMark = f.Recorder.Routed.Count;
        Call(f.Service, "PumpBots", BotWorldFor(f, target));
        AssertPromotedBeforeWeapons(ViewerPackets(f, late, lateMark), target.WorldGuid, target.TransientId);
    }

    [Fact]
    public void FullDataRequestsRespectInterestAndDoNotReplayWeaponActions()
    {
        using var f = new Fixture();
        var subject = f.Add(1, Vector3.Zero);
        var viewer = f.Add(2, Vector3.UnitX);
        var outside = f.Add(3, Vector3.Zero, matchId: 2);
        CombatInterest(f);
        int mark = f.Recorder.Routed.Count;
        Call(f.Service, "NotePeerFullCharacterDataRequest", viewer, viewer.Tag, 1UL);
        Assert.Single(ViewerPackets(f, viewer, mark), p => p[0] == 0xd9);
        Assert.Empty(CombatRemote(f, viewer, mark));
        mark = f.Recorder.Routed.Count;
        Call(f.Service, "NotePeerFullCharacterDataRequest", outside, outside.Tag, 1UL);
        Call(f.Service, "NotePeerFullCharacterDataRequest", subject, subject.Tag, 1UL);
        Assert.Empty(ViewerPackets(f, outside, mark));
        Assert.Empty(ViewerPackets(f, subject, mark));
        Get<PeerSession>(viewer.Tag!, "Peer").View.MarkForgotten(new EntityId(1));
        Call(f.Service, "NotePeerFullCharacterDataRequest", viewer, viewer.Tag, 1UL);
        Assert.Empty(ViewerPackets(f, viewer, mark));
    }
}
