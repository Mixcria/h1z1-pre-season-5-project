using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Theory]
    [InlineData(1542u, 0, 2)]
    [InlineData(1542u, 1, 0)]
    [InlineData(2425u, 0, 2)]
    [InlineData(0u, 0, 1)]
    public void FireModeLifecycle_AbsentDescriptorCannotCreateWeaponState(uint item, byte group, byte mode)
    {
        const ulong guid = 0x3100_0000_0000_0001;
        var combat = new SessionCombat();
        var results = new List<WeaponArmResult>();

        WeaponFireArm.Handle(combat, ShootingPacketBuilder.SwitchFireModeRequest(guid, group, mode),
            CombatOptions.Default, item, item == 0 ? 0 : guid, Vector3.Zero, 0, results);

        Assert.Contains("mode absent from the weapon descriptor", Assert.Single(results).Line,
            StringComparison.Ordinal);
        Assert.Equal(0, combat.Shooter.WeaponCount);
        Assert.False(combat.Shooter.Knows(guid));
        Assert.Equal(-1, combat.Shooter.AmmoOf(guid));
        Assert.Null(results[0].Reply);
        Assert.Null(results[0].RemoteUpdate);
    }

    [Theory]
    [InlineData(1542u)]
    [InlineData(1695u)]
    public void FireModeLifecycle_ValidOpticNotificationsPreserveMagazineAndOnlyRelayChanges(uint item)
    {
        const ulong guid = 0x3100_0000_0000_0001;
        var combat = new SessionCombat();
        combat.Shooter.DeclareWeapon(guid, item, 0);
        var results = new List<WeaponArmResult>();
        var transitions = new List<WeaponArmResult>();

        foreach (byte mode in new byte[] { 1, 1, 0 })
        {
            WeaponFireArm.Handle(combat, ShootingPacketBuilder.SwitchFireModeRequest(guid, 0, mode),
                CombatOptions.Default, item, guid, Vector3.Zero, 0, results);
            transitions.Add(Assert.Single(results));
        }

        Assert.Equal(1, combat.Shooter.WeaponCount);
        Assert.Equal(0, combat.Shooter.AmmoOf(guid));
        Assert.Equal(0, combat.Shooter.FireModeOf(guid));
        Assert.Equal(RemoteWeaponPackets.WeaponUpdateType.SwitchFireMode, transitions[0].RemoteUpdate);
        Assert.Null(transitions[1].RemoteUpdate);
        Assert.Equal(RemoteWeaponPackets.WeaponUpdateType.SwitchFireMode, transitions[2].RemoteUpdate);
        Assert.All(transitions, result => Assert.Null(result.Reply));
    }

    [Fact]
    public void FireModeLifecycle_ChangedHandRejectsOldOpticAndMenuCannotRecreateItsState()
    {
        using var f = new Fixture();
        var player = f.Add(1, Vector3.Zero);
        var gun = CombatGun(f, player, 2425, 7);
        var inventory = Get<PlayerInventory>(player.Tag!, "Inventory");
        var optic = inventory.LoadoutSlots[SurvivorLoadout.Binoculars];
        var combat = Get<SessionCombat>(player.Tag!, "Combat");

        SelectSlot(SurvivorLoadout.Binoculars);
        Assert.Equal(optic.Guid, inventory.WieldedItemGuid);
        CombatGatewayPacket(f, player, ShootingPacketBuilder.SwitchFireModeRequest(optic.Guid, 0, 1));
        Assert.Equal(1, combat.Shooter.FireModeOf(optic.Guid));
        SelectSlot(SurvivorLoadout.Wheel1);
        Assert.Equal(gun.Guid, inventory.WieldedItemGuid);
        CombatGatewayPacket(f, player, ShootingPacketBuilder.SwitchFireModeRequest(optic.Guid, 0, 0));
        Assert.Equal(1, combat.Shooter.FireModeOf(optic.Guid)); // stale packet cannot change the stowed item
        Assert.Equal(7, combat.Shooter.AmmoOf(gun.Guid));

        Call(f.Service, "AbandonMatch", player, player.Tag, "fire-mode lifecycle regression");
        Assert.Null(Get<PlayerInventory?>(player.Tag!, "Inventory"));
        Assert.Equal(0, combat.Shooter.WeaponCount);
        int mark = f.Recorder.Routed.Count;
        CombatGatewayPacket(f, player, ShootingPacketBuilder.SwitchFireModeRequest(optic.Guid, 0, 0));
        Assert.Equal(0, combat.Shooter.WeaponCount);
        Assert.Empty(CombatRemote(f, player, mark));

        void SelectSlot(uint slot)
        {
            using var packet = new PacketWriter();
            packet.WriteByte(SelectLoadoutSlotRequest.Opcode);
            packet.WriteByte(SelectLoadoutSlotRequest.SubOpcode);
            packet.WriteUInt32(0);
            packet.WriteUInt32(slot);
            packet.WriteUInt32(1234);
            CombatGatewayPacket(f, player, packet.Written.ToArray(), channel: 0);
        }
    }
}
