using System.Numerics;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExplosionWithoutPlayerDeathsSendsNoPopulationAlert(bool worldObject)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, new(100, 0, 0));
        int mark = f.Recorder.Routed.Count;
        if (worldObject)
            Call(f.Service, "ExplodeWorldObject", shooter, shooter.Tag, Vector3.Zero, true);
        else
        {
            var car = AddFeedbackVehicle(f, shooter);
            Call(f.Service, "ApplyVehicleDamage", shooter, shooter.Tag, car, 100000u, "test", false, null);
        }
        Assert.Equal(10000u, Get<uint>(shooter.Tag!, "Hitpoints"));
        Assert.DoesNotContain(f.Recorder.Routed.Skip(mark), r => r.Packet.Length > 2
            && ((r.Packet[1] == 0x11 && r.Packet[2] == 0x31)
                || (r.Packet[1] == 0xce && r.Packet[2] == 9)));
        Assert.False(Get<bool>(shooter.Tag!, "VictorySent"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARealBlastEliminationStillResolvesTheSurvivingPlayer(bool worldObject)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, new(100, 0, 0));
        var victim = f.Add(2, Vector3.Zero);
        if (worldObject)
            Call(f.Service, "ExplodeWorldObject", shooter, shooter.Tag, Vector3.Zero, true);
        else
        {
            var car = AddFeedbackVehicle(f, shooter);
            Call(f.Service, "ApplyVehicleDamage", shooter, shooter.Tag, car, 100000u, "test", false, shooter.Tag);
        }
        Assert.True(Get<bool>(victim.Tag!, "DeathSent"));
        Assert.True(Get<bool>(shooter.Tag!, "VictorySent"));
        Assert.False(Get<bool>(victim.Tag!, "VictorySent"));
    }
}
