using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MolotovContactStartsFireAndDamageBeforeAnyFuseTimer(bool hitReport, bool hasInventory)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = f.Add(2, new(10, 0, 0));
        var outside = f.Add(3, new(20, 0, 0));
        var otherMatch = f.Add(4, new(10, 0, 0), matchId: 2);
        f.SeeEveryone();
        var inventory = new PlayerInventory(1, Get<LootWorld>(shooter.Tag!, "Loot").NextItemGuid,
            new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        if (hasInventory) Set(shooter.Tag!, "Inventory", inventory);
        var combat = Get<SessionCombat>(shooter.Tag!, "Combat");
        var results = Get<List<WeaponArmResult>>(shooter.Tag!, "WeaponArmResults");
        void Weapon(byte[] packet)
        {
            WeaponFireArm.Handle(combat, packet, CombatOptions.Default, 14,
                Vector3.Zero, 1000, results);
            Call(f.Service, "DrainCombatArm", shooter, shooter.Tag);
        }

        const uint projectile = 63;
        Weapon(ShootingPacketBuilder.Fire(0x3100000000000042, 0, 0, 0, [projectile]));
        Assert.Equal(10000u, f.Health(victim));
        int mark = f.Recorder.Routed.Count;
        byte[] contact = Convert.FromHexString(hitReport
            ? "828C45601C063F0000000000000000000000B10C00C5140E93C0C08AA74400A000000000000000003F80"
            : "828C45601C213F0000000000000000000000DC9E063F333398BC4049103E4DAF563FB10C00C5140E93C0C08AA744EC4677BF00000000008484BEC3FD5D3E9380D0BE172163BFFFFFFFFF00A00000000001");
        int position = WeaponBaseDecoder.HeaderLength + (hitReport ? 12 : 28);
        BitConverter.GetBytes(10f).CopyTo(contact, position);
        BitConverter.GetBytes(0f).CopyTo(contact, position + 4);
        BitConverter.GetBytes(0f).CopyTo(contact, position + 8);
        f.Move(shooter, new(10, 0, 0));
        Weapon(contact);

        // DrainCombatArm must dispatch the contact result to the real cloud/damage path now.
        Assert.Equal(9000u, f.Health(victim));
        Assert.Equal(9000u, f.Health(shooter));
        Assert.Equal(10000u, f.Health(outside));
        Assert.Equal(10000u, f.Health(otherMatch));
        Assert.Empty(combat.Grenades.Live);
        var sent = f.Recorder.Routed.Skip(mark).ToArray();
        var retired = Assert.Single(sent, r => r.Packet.Length == 26
            && r.Packet[1] == 0x82 && r.Packet[6] == 0x1e);
        Assert.Same(shooter, retired.Connection);
        Assert.Equal(WeaponReplyPackets.RetireProjectile(projectile), retired.Packet[1..]);
        Assert.True(Array.FindIndex(sent, r => r.Packet.SequenceEqual(retired.Packet))
            < Array.FindIndex(sent, r => r.Packet.Length > 3
                && r.Packet[1] == 0x0f && r.Packet[2] == 0x43));
        Assert.Contains(sent, r => ReferenceEquals(r.Connection, shooter)
            && r.Packet.Length > 3 && r.Packet[1] == 0x0f && r.Packet[2] == 0x43);
        Assert.Contains(sent, r => ReferenceEquals(r.Connection, shooter)
            && r.Packet.Length > 3 && r.Packet[1] == 0x0f && r.Packet[2] == 0x15);

        // The shared fire anchor must not render another intact bottle. Its model
        // field follows the variable-width transient id and empty name string.
        var anchor = Assert.Single(sent, r => ReferenceEquals(r.Connection, shooter)
            && r.Packet.Length >= 201 && r.Packet[1] == 0xd6);
        int transientLength = anchor.Packet.Length - 200;
        Assert.Equal(33u, BitConverter.ToUInt32(anchor.Packet, 19 + transientLength));
        ulong anchorGuid = BitConverter.ToUInt64(anchor.Packet, 2);
        Assert.Contains(sent, r => ReferenceEquals(r.Connection, victim)
            && r.Packet.SequenceEqual(anchor.Packet));
        Assert.DoesNotContain(sent, r => ReferenceEquals(r.Connection, otherMatch)
            && r.Packet.SequenceEqual(anchor.Packet));
        Assert.Contains(sent, r => ReferenceEquals(r.Connection, shooter)
            && r.Packet.Length >= 15 && r.Packet[1] == 0x0f && r.Packet[2] == 0x15
            && BitConverter.ToUInt64(r.Packet, 3) == anchorGuid);

        int afterContact = f.Recorder.Routed.Count;
        Weapon(contact);
        Assert.Equal(9000u, f.Health(victim));
        Assert.DoesNotContain(f.Recorder.Routed.Skip(afterContact), r => r.Packet.Length > 3
            && r.Packet[1] == 0x0f && r.Packet[2] is 0x43 or 0x15);
    }

    [Fact]
    public void MolotovTimersFromAnAbandonedWorldCannotDamageOrSendIntoTheNextWorld()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = f.Add(2, new(10, 0, 0));
        var combat = Get<SessionCombat>(shooter.Tag!, "Combat");
        Assert.True(AugustThrowables.TryGet(14, out var fact));
        var grenade = new LiveGrenade
        {
            ProjectileId = 63, Fact = fact, ItemGuid = 0x42,
            ThrowPoint = new(10, 0, 0), ThrownAtMs = 0, FuseDueAtMs = 0,
        };
        combat.Grenades.Add(grenade);
        var detonation = new Detonation(fact, new(10, 0, 0), true, false, 63);
        int previousWorld = Get<int>(shooter.Tag!, "WorldGeneration");
        Set(shooter.Tag!, "WorldGeneration", previousWorld + 1);
        int mark = f.Recorder.Routed.Count;

        Call(f.Service, "PumpOverdueGrenades", shooter, shooter.Tag, previousWorld);
        Call(f.Service, "CloudTick", shooter, shooter.Tag, detonation, 0x4900000000000001ul,
            1, Rulings.Throwables.CloudTickMs, previousWorld);

        Assert.Equal(10000u, f.Health(victim));
        Assert.Equal(mark, f.Recorder.Routed.Count);
        Assert.False(grenade.Detonated);
    }

    [Fact]
    public void RetiringABottleSelectsNoWeaponOrRefundableModeAndOnlyItsProjectile()
    {
        // Native FUN_140a47580 reads two signed bytes; FUN_14228f030 refuses -1 before
        // mode lookup. The separate cleanup loop consumes the counted projectile ids.
        byte[] packet = WeaponReplyPackets.RetireProjectile(0x12345678);
        Assert.Equal(25, packet.Length);
        Assert.Equal(new byte[] { 0x82, 0, 0, 0, 0, 0x1e }, packet[..6]);
        Assert.Equal(0ul, BitConverter.ToUInt64(packet, 6));
        Assert.Equal(0, packet[14]);
        Assert.Equal(0xff, packet[15]);
        Assert.Equal(0xff, packet[16]);
        Assert.Equal(1u, BitConverter.ToUInt32(packet, 17));
        Assert.Equal(0x12345678u, BitConverter.ToUInt32(packet, 21));
    }
}
