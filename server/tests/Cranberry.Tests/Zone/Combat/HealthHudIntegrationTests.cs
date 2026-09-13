using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Theory]
    [InlineData(DamageCause.Bullet)]
    [InlineData(DamageCause.Melee)]
    [InlineData(DamageCause.Falling)]
    [InlineData(DamageCause.Vehicle)]
    [InlineData(DamageCause.ToxicGas)]
    [InlineData(DamageCause.Fire)]
    [InlineData(DamageCause.BombingRun)]
    public void AcceptedDamageUpdatesTheVictimsHealthResource(DamageCause cause)
    {
        using var f = new Fixture();
        var victim = f.Add(1, Vector3.Zero);
        f.Add(2, Vector3.UnitX);
        int mark = f.Recorder.Routed.Count;

        f.Service.ForTest(victim).Damage(1000, cause);

        var update = Assert.Single(f.Recorder.Routed.Skip(mark), p => IsHudHealth(p.Packet));
        Assert.Same(victim, update.Connection);
        AssertHudHealth(update.Packet, 1, 9000, 10000);
        Assert.Equal(9000u, f.Health(victim));
    }

    [Fact]
    public void ReportedSixFireTicksThenFatalCollisionPublishEveryHealthValueBeforeDeath()
    {
        using var f = new Fixture();
        var victim = f.Add(4129, Vector3.Zero);
        int mark = f.Recorder.Sent.Count;

        for (int tick = 0; tick < 6; tick++) f.Service.ForTest(victim).Damage(1000, DamageCause.Fire);
        f.Service.ForTest(victim).Damage(5826, DamageCause.Vehicle);

        var packets = f.Recorder.Sent.Skip(mark).ToArray();
        var updates = packets.Where(IsHudHealth).ToArray();
        Assert.Equal(7, updates.Length);
        uint previous = 10000;
        uint[] expected = [9000, 8000, 7000, 6000, 5000, 4000, 0];
        for (int index = 0; index < expected.Length; index++)
        {
            AssertHudHealth(updates[index], 4129, expected[index], previous);
            previous = expected[index];
        }
        int zeroIndex = Array.IndexOf(packets, updates[^1]);
        int deathIndex = Array.FindIndex(packets, p => p.Length > 2 && p[1] == 0x0f && p[2] == 0x4f);
        Assert.True(deathIndex > zeroIndex);
        Assert.Equal(0u, f.Health(victim));
    }

    [Fact]
    public void ResourceResendKeepsWoundedHealthAndPhaseResetRestoresBothValues()
    {
        using var f = new Fixture();
        var victim = f.Add(1, Vector3.Zero);
        f.Service.ForTest(victim).Damage(6000, DamageCause.Fire);
        int mark = f.Recorder.Sent.Count;

        Call(f.Service, "SendInitialCharacterResources", victim, victim.Tag, "health regression");
        AssertHudHealth(Assert.Single(f.Recorder.Sent.Skip(mark), IsHudHealth), 1, 4000, 4000);

        mark = f.Recorder.Sent.Count;
        Call(f.Service, "ResetPlayerVitals", victim, victim.Tag);
        AssertHudHealth(Assert.Single(f.Recorder.Sent.Skip(mark), IsHudHealth), 1, 10000, 4000);
        Assert.Equal(10000u, f.Health(victim));
    }

    private static bool IsHudHealth(byte[] packet) => packet.Length == 102 && packet[1] == 0x8d
        && packet[6] == 3 && BitConverter.ToUInt32(packet, 15) == 1 && BitConverter.ToUInt32(packet, 19) == 1;

    private static void AssertHudHealth(byte[] packet, ulong guid, uint current, uint previous)
    {
        Assert.True(IsHudHealth(packet));
        Assert.Equal(guid, BitConverter.ToUInt64(packet, 7));
        Assert.Equal(current, BitConverter.ToUInt32(packet, 23));
        Assert.Equal(previous, BitConverter.ToUInt32(packet, 27));
    }
}
