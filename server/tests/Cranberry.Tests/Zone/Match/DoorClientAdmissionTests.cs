using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class WorldModeRoutingTests
{
    [Fact]
    public void MatchAdmissionWaitsForThatPlayersVerifiedDoorClient()
    {
        var f = new Fixture(); var a = f.Player(); var b = f.Player();
        Set(a.Tag!, "AccountId", "a"); Set(b.Tag!, "AccountId", "b");
        var ready = new HashSet<string>(); f.Service.DoorSwingClientReady = ready.Contains;
        var request = new PlayerWorldTransferRequest(1, "", 0, 1, 1);
        Assert.False((bool)Call(f.Service, "PrepareMatchAdmission", a.Tag!, request)!);
        ready.Add("a");
        Assert.True((bool)Call(f.Service, "PrepareMatchAdmission", a.Tag!, request)!);
        Assert.False((bool)Call(f.Service, "PrepareMatchAdmission", b.Tag!, request)!);
        ready.Add("b");
        Assert.True((bool)Call(f.Service, "PrepareMatchAdmission", b.Tag!, request)!);
    }
}
