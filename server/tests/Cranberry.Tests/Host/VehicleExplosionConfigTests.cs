using Cranberry.Host.Config;

namespace Cranberry.Tests.Host;

public sealed class VehicleExplosionConfigTests
{
    [Fact]
    public void VehicleBlastSettingsReachTheRunningOptions()
    {
        var config = CranberryConfig.Load([], _ => null,
            """{"vehicles":{"explosions":false,"explosionDamage":5000,"explosionFullRadius":2,"explosionRadius":10}}""");
        Assert.False(config.Vehicles.Damage.Explosions);
        Assert.Equal(5000u, config.Vehicles.Damage.ExplosionDamage);
        Assert.Equal(2f, config.Vehicles.Damage.ExplosionFullDamageRadius);
        Assert.Equal(10f, config.Vehicles.Damage.ExplosionRadius);
    }
}
