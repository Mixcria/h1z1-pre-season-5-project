using System.Collections;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Loot;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class MedicalMovementTests
{
    [Theory]
    [InlineData(2423u)]
    [InlineData(2424u)]
    [InlineData(3375u)]
    public void MedicalLifecycle_DeathCancelsApplicationBeforeItsQueuedCallback(uint item)
    {
        using var world = new Fixture(item);
        world.Start();
        Action queued = world.TakeTimer();
        world.Sent.Clear();

        world.Invoke("KillPlayer", DamageCause.Bullet, 0ul, string.Empty, 0u);

        Assert.True(world.Get<bool>("DeathSent"));
        Assert.Equal(0u, world.Get<uint>("Hitpoints"));
        Assert.Null(world.Cast);
        Assert.Equal(0, world.Get<long>("ConsumeBusyUntil"));
        Assert.Equal(2, world.Sent.Count(IsStop));
        int stop = world.Sent.FindIndex(IsStop);
        int death = world.Sent.FindIndex(p => p.Length > 1 && p[0] == 0xce && p[1] == 4);
        Assert.True(stop >= 0 && death > stop);
        Assert.DoesNotContain(world.Sent, p => p.Length > 1 && p[0] == 0x9e && p[1] == 7);

        // Death transfers the unconsumed unit to the body bag. A cancelled medical
        // must not spend it or publish a delayed stop over the endgame interface.
        var bags = world.Get<Dictionary<ulong, BodyBag>>("BodyBags");
        var retained = Assert.Single(bags.Values.SelectMany(bag => bag.Items.Values), i => i.ItemGuid == world.Item.Guid);
        Assert.Equal(1u, retained.Count);
        int packets = world.Sent.Count;
        queued();
        Assert.Equal(packets, world.Sent.Count);
        Assert.Equal(0u, world.Get<uint>("Hitpoints"));
        Assert.Equal(1u, Assert.Single(bags.Values.SelectMany(bag => bag.Items.Values),
            i => i.ItemGuid == world.Item.Guid).Count);
    }

    [Theory]
    [InlineData("death")]
    [InlineData("vitals-reset")]
    [InlineData("world-exit")]
    public void MedicalLifecycle_CleanupRemovesActiveNativeEffectsAndInvalidatesBothTickChains(string transition)
    {
        using var world = new Fixture(initialHitpoints: 1000);
        world.Invoke("BeginHeal", MedicalModel.For(2424)!.Value);
        Action healTick = world.TakeTimer();
        world.Wound();
        Action bleedTick = world.TakeTimer();
        ulong healing = BitConverter.ToUInt64(Assert.Single(world.Sent,
            p => p.Length > 1 && p[0] == 0x9e && p[1] == 7), 10);
        ulong bleeding = world.Get<ulong>("BleedHudInstance");
        Assert.NotEqual(0ul, bleeding);
        Assert.NotEqual(healing, bleeding);
        world.Sent.Clear();

        switch (transition)
        {
            case "death": world.Invoke("KillPlayer", DamageCause.Bullet, 0ul, string.Empty, 0u); break;
            case "vitals-reset": world.Invoke("ResetPlayerVitals"); break;
            case "world-exit": world.Invoke("AbandonMatch", "medical lifecycle test"); break;
        }

        Assert.Empty(world.Get<IDictionary>("HealingEffects"));
        Assert.Equal(0ul, world.Get<ulong>("BleedHudInstance"));
        Assert.Equal(0u, world.Get<uint>("BleedEffect"));
        Assert.Single(world.Sent, p => IsMedicalRemoval(p, healing));
        Assert.Single(world.Sent, p => IsMedicalRemoval(p, bleeding));
        uint afterCleanup = world.Get<uint>("Hitpoints");
        int packets = world.Sent.Count;

        healTick();
        bleedTick();

        Assert.Equal(afterCleanup, world.Get<uint>("Hitpoints"));
        Assert.Equal(packets, world.Sent.Count);
        Assert.Empty(world.Get<IDictionary>("HealingEffects"));
        Assert.Equal(0ul, world.Get<ulong>("BleedHudInstance"));
    }

    private static bool IsMedicalRemoval(byte[] packet, ulong instance) =>
        packet.Length == 18 && packet[0] == 0x9e && packet[1] == 8
        && BitConverter.ToUInt64(packet, 10) == instance;
}
