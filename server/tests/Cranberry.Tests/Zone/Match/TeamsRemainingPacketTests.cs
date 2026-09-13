using Cranberry.Protocol;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchEndgame;

public sealed class TeamsRemainingPacketTests
{
    [Fact]
    public void AugustTeamCounterCarriesPlayersBeforeTeamsAndConsumesElevenBytes()
    {
        using var writer = new PacketWriter();
        new TeamsRemaining(147, 31).WriteTo(writer);
        Assert.Equal(new byte[] { 0xce, 0x0a, 0, 147, 0, 0, 0, 31, 0, 0, 0 }, writer.Written.ToArray());
    }
}
