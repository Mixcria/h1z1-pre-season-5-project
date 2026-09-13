using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class PartyTests
{
    [Fact]
    public void InvitedCharacterAloneCanAcceptAndTokenIsConsumed()
    {
        var parties = new PartyRegistry();
        PartyInvitation invite = Assert.IsType<PartyInvitation>(parties.Invite(10, 20, 100));
        Assert.Null(parties.Respond(invite.Token, 30, true, 101));
        PartySnapshot group = Assert.IsType<PartySnapshot>(parties.Respond(invite.Token, 20, true, 102));
        Assert.Equal(10ul, group.Leader);
        Assert.Equal(new ulong[] { 10, 20 }, group.Members);
        Assert.Equal(group.Id, parties.Find(20)!.Id);
        Assert.Null(parties.Respond(invite.Token, 20, true, 103));
    }

    [Fact]
    public void ExpiredDeclinedAndReplacedInvitesCannotJoin()
    {
        var parties = new PartyRegistry();
        PartyInvitation expired = Assert.IsType<PartyInvitation>(parties.Invite(10, 20, 100));
        Assert.Null(parties.Respond(expired.Token, 20, true, expired.ExpiresAtMs));
        PartyInvitation declined = Assert.IsType<PartyInvitation>(parties.Invite(10, 20, 100_000));
        Assert.Null(parties.Respond(declined.Token, 20, false, 100_001));
        Assert.Null(parties.Respond(declined.Token, 20, true, 100_002));
        PartyInvitation old = Assert.IsType<PartyInvitation>(parties.Invite(10, 20, 100_010));
        PartyInvitation latest = Assert.IsType<PartyInvitation>(parties.Invite(30, 20, 100_011));
        Assert.Null(parties.Respond(old.Token, 20, true, 100_012));
        Assert.Equal(30ul, parties.Respond(latest.Token, 20, true, 100_013)!.Leader);
    }

    [Fact]
    public void OnlyLeaderCanInviteAndAcceptRechecksFiveMemberCapacity()
    {
        var parties = new PartyRegistry();
        var invites = Enumerable.Range(2, 5).Select(i => parties.Invite(1, (ulong)i, 100)!).ToArray();
        PartySnapshot group = parties.Respond(invites[0].Token, 2, true, 101)!;
        Assert.Null(parties.Invite(2, 7, 102));
        Assert.Null(parties.Invite(8, 2, 102));
        foreach (PartyInvitation invite in invites.Skip(1).Take(3))
            group = parties.Respond(invite.Token, invite.Invitee, true, 103)!;
        Assert.Equal(5, group.Members.Count);
        Assert.Null(parties.Respond(invites[^1].Token, 6, true, 104));
        Assert.Null(parties.Invite(1, 7, 105));
        Assert.Null(parties.Find(6));
    }

    [Fact]
    public void DepartureTransfersLeadershipDissolvesLastPairAndCancelsInvitations()
    {
        var parties = new PartyRegistry();
        foreach (ulong target in new ulong[] { 2, 3 })
        {
            PartyInvitation invite = parties.Invite(1, target, 10)!;
            parties.Respond(invite.Token, target, true, 11);
        }
        PartyInvitation pending = parties.Invite(1, 4, 12)!;
        PartySnapshot remaining = parties.Leave(1)!;
        Assert.Equal(2ul, remaining.Leader);
        Assert.Equal(new ulong[] { 2, 3 }, remaining.Members);
        Assert.Null(parties.Respond(pending.Token, 4, true, 13));
        Assert.Null(parties.Find(1));
        Assert.Null(parties.Leave(2));
        Assert.Null(parties.Find(3));
    }

    [Theory]
    [InlineData(PartyPacket.InviteSub, 0u)]
    [InlineData(PartyPacket.JoinSub, 1u)]
    [InlineData(PartyPacket.JoinSub, 2u)]
    public void NativeInviteAndJoinRoundTripAndRejectEveryTruncatedPrefix(byte sub, uint state)
    {
        var invite = new PartyInviteData(42, 0,
            new(10, new SelfIdentity { Name = "Leader" }),
            new(20, new SelfIdentity { Name = "Guest" }), 0x10000001);
        var expected = new PartyPacket(sub, 1, 0, 7, invite, state);
        using var writer = new PacketWriter();
        expected.WriteTo(writer);
        byte[] payload = writer.Written.ToArray();
        Assert.Equal(new byte[] { 0x13, sub, 1, 0, 0, 0, 0 }, payload[..7]);
        Assert.True(PartyPacket.TryParse(payload, out PartyPacket? actual));
        Assert.Equal(expected, actual);
        for (int length = 0; length < payload.Length; length++)
            Assert.False(PartyPacket.TryParse(payload.AsSpan(0, length), out _));
        Assert.False(PartyPacket.TryParse([.. payload, 0], out _));
    }

    [Fact]
    public void AugustLeaveIsSevenBytesNotLegacyDwordHeader()
    {
        using var writer = new PacketWriter();
        new PartyPacket(4, 1, 0, 0).WriteTo(writer);
        Assert.Equal(Convert.FromHexString("13040100000000"), writer.Written.ToArray());
        Assert.True(PartyPacket.TryParse(writer.Written, out PartyPacket? actual));
        Assert.Equal(PartyPacket.LeaveSub, actual.SubOpcode);
    }
}
