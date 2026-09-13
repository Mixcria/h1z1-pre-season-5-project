using Cranberry.Protocol;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class TeamScorePacketTests
{
    [Theory]
    [InlineData(2u, 2u, 1u)]
    [InlineData(5u, 3u, 4u)]
    public void EliminatedPlayerCanHaveLivingTeammatesWithoutFinishingTheTeam(uint size, uint mode, uint living)
    {
        using var writer = new PacketWriter();
        new MatchScoreUpdate(10, 20, mode, 2, PlayerCount: 100,
            TeamSize: size, TeamCount: 30, TeamKills: 7, LivingTeamMembers: living,
            PlayerFinished: true).WriteTo(writer);
        var reader = new PacketReader(writer.Written);
        reader.Skip(18);
        uint[] words = new uint[19];
        for (int index = 0; index < words.Length; index++) words[index] = reader.ReadUInt32();
        Assert.Equal(2u, words[3]);
        Assert.Equal(7u, words[9]);
        Assert.Equal(7000u, words[10]);
        Assert.Equal(100u, words[13]);
        Assert.Equal(mode, words[14]);
        Assert.Equal(size, words[15]);
        Assert.Equal(30u, words[16]);
        reader.Skip(9); // trial byte and two currency words
        Assert.True(reader.ReadBool()); // IsPlayerFinished
        Assert.False(reader.ReadBool()); // IsTeamFinished
        Assert.Equal(living, reader.ReadUInt32());
        Assert.True(reader.AtEnd);
        Assert.Equal(MatchScoreUpdate.Length, writer.Written.Length);
    }
}
