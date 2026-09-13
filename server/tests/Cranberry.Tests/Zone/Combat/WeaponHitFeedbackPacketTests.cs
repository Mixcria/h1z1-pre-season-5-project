using Cranberry.Protocol;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class WeaponHitFeedbackPacketTests
{
    // Independent vectors from August parser 1412be8f0 and dispatcher 1412caa00:
    // u8 base/sub, u32 damage, u8 flags, i32 feedback ID (constructor default -1).
    [Theory]
    [InlineData(false, false, false, false, 0x01)]
    [InlineData(true, false, false, true, 0x15)]
    [InlineData(false, true, false, false, 0x41)]
    [InlineData(false, true, true, false, 0xc1)]
    [InlineData(true, true, true, false, 0xc5)]
    [InlineData(true, true, false, true, 0x55)]
    public void HitPacketHasTheNativeColourAndFrameFlags(
        bool head, bool armour, bool broken, bool killed, byte expectedFlags)
    {
        var packet = new WeaponHitFeedback(2500, IsHeadshot: head, HitArmour: armour,
            CrackedArmour: broken, Killed: killed);
        using var writer = new PacketWriter();
        packet.WriteTo(writer);
        Assert.Equal(11, WeaponHitFeedback.Length);
        Assert.Equal(new byte[] { 0x1a, 0x10, 0xc4, 0x09, 0, 0, expectedFlags,
            0xff, 0xff, 0xff, 0xff }, writer.Written.ToArray());
    }

    [Fact]
    public void ZeroHealthDamageStillConfirmsAnArmourHit()
    {
        var hit = new HitOutcome(0, true, true, true, false, "helmet absorbed");
        var packet = WeaponHitFeedback.For(hit);
        Assert.Equal(0u, packet.DamageUnits);
        Assert.Equal(0xc5, packet.Flags);
    }

    [Fact]
    public void ArmourContactCanBeReportedWithoutChangingPenetratingDamage()
    {
        var hit = new HitOutcome(10000, true, false, false, true, "penetrating headshot");
        var packet = WeaponHitFeedback.For(hit, killed: true, armourHit: true);
        Assert.Equal(10000u, packet.DamageUnits);
        Assert.Equal(0x55, packet.Flags);
        Assert.False(hit.DamagedArmour);
        Assert.False(hit.BrokeArmour);
    }
}
