using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class InteractionFeedbackTests
{
    [Theory]
    [InlineData(2158u, SurvivorLoadout.Head)]
    [InlineData(2046u, SurvivorLoadout.Head)]
    [InlineData(2170u, SurvivorLoadout.Head)]
    [InlineData(3711u, SurvivorLoadout.Feet)]
    [InlineData(1373u, SurvivorLoadout.Wheel1)]
    [InlineData(1542u, SurvivorLoadout.Binoculars)]
    public void DroppedEquipmentCanBeClickedBackOnUsingThePublishedProximityOwner(uint definition, uint slot)
    {
        using var world = new Fixture(proximity: true);
        world.MoveTo(Vector3.Zero);
        if (world.Inventory.LoadoutSlots.TryGetValue(slot, out var starter))
            world.Inventory.RemoveUnits(starter.Guid, 0);
        world.Inventory.TryPickUp(definition, 1, out var worn);
        Assert.Equal(slot, worn!.LoadoutSlotId);

        world.Send(UseItem(4, worn.Guid));

        Assert.False(world.Inventory.Items.ContainsKey(worn.Guid));
        Assert.False(world.Inventory.LoadoutSlots.ContainsKey(slot));
        var ground = Assert.Single(world.Loot.Items);
        var row = Assert.Single(world.Sent, p => Is(p, 0xf8, 0x01));
        Assert.Equal(80, row.Length);
        ulong itemGuid = BitConverter.ToUInt64(row, 18);
        ulong ownerGuid = BitConverter.ToUInt64(row, 60);
        Assert.Equal(ground.WorldGuid, itemGuid);
        Assert.Equal(ground.WorldGuid, ownerGuid);
        Assert.Equal(ground.WorldGuid, BitConverter.ToUInt64(row, 72));
        var remoteItem = Assert.Single(world.Sent, p => Is(p, 0x11, 0x02));
        Assert.Equal(ground.WorldGuid, BitConverter.ToUInt64(remoteItem, 3));
        Assert.Equal(ground.WorldGuid, BitConverter.ToUInt64(remoteItem, 23));
        Assert.Equal(ground.WorldGuid, BitConverter.ToUInt64(remoteItem, 65));
        Assert.True(world.Sent.IndexOf(remoteItem) < world.Sent.IndexOf(row));
        // A correct owner GUID alone does not make the August row actionable: the native
        // datasource and MoveItem validator both require its world's NPC IsWorldItem flag.
        var npc = Assert.Single(world.Sent, p => Is(p, 0xea, 0x04)
            && System.Text.Encoding.UTF8.GetString(p).Contains(CreateComponent.NpcComponentClass,
                StringComparison.Ordinal));
        var interact = Assert.Single(world.Sent, p => Is(p, 0xea, 0x04)
            && System.Text.Encoding.UTF8.GetString(p).Contains(CreateComponent.InteractComponentClass,
                StringComparison.Ordinal));
        Assert.Equal(1, npc[^1]); // actual wire81, native+0x78, not the old incorrect85B payload
        Assert.Equal(82, BitConverter.ToInt32(npc, npc.Length - 82 - 4));
        int npcIdOffset = npc.Length - 82 - ReplicationDataEntry.HeaderLength;
        int interactIdOffset = interact.Length - 11 - ReplicationDataEntry.HeaderLength;
        Assert.NotEqual(BitConverter.ToUInt32(npc, npcIdOffset),
            BitConverter.ToUInt32(interact, interactIdOffset));
        Assert.Equal(ground.NameId, BitConverter.ToUInt32(npc, npc.Length - 82 + 12));
        Assert.True(world.Sent.FindIndex(p => p[0] == 0xd6) < world.Sent.FindIndex(p => p[0] == 0xda));
        Assert.True(world.Sent.FindIndex(p => p[0] == 0xda) < world.Sent.IndexOf(npc));
        Assert.True(world.Sent.IndexOf(npc) < world.Sent.IndexOf(interact));
        Assert.True(world.Sent.IndexOf(npc) < world.Sent.IndexOf(row));

        world.Sent.Clear();
        // InventoryWindow.onRendererMouseUp sends the row owner/item into MoveItem2,
        // with destination container "0" and slot -1 for the ordinary left click.
        var click = ProximityClick(ownerGuid, itemGuid);
        world.Send(click);
        world.Send(click);

        Assert.Empty(world.Loot.Items);
        Assert.Equal(definition, world.Inventory.LoadoutSlots[slot].DefinitionId);
        Assert.Single(world.Inventory.Items.Values, i => i.DefinitionId == definition);
        Assert.Single(world.Sent, p => Is(p, 0x11, 0x02));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x01));
    }

    [Fact]
    public void StaleProximityClickCannotPickUpADroppedHatAfterWalkingAway()
    {
        using var world = new Fixture(proximity: true);
        world.MoveTo(Vector3.Zero);
        world.Inventory.TryPickUp(2158, 1, out var hat);
        world.Send(UseItem(4, hat!.Guid));
        var ground = Assert.Single(world.Loot.Items);
        world.MoveTo(new Vector3(20, 0, 0));
        world.Sent.Clear();

        world.Send(ProximityClick(ground.WorldGuid, ground.WorldGuid));

        Assert.True(world.Loot.TryGet(ground.WorldGuid, out _));
        Assert.False(world.Inventory.LoadoutSlots.ContainsKey(SurvivorLoadout.Head));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x11, 0x02) || Is(p, 0x0f, 0x01));
    }

    private static byte[] UseItem(uint option, ulong item)
    {
        using var w = new PacketWriter();
        w.WriteByte(0xac);
        w.WriteByte(0x2c);
        w.WriteUInt32(1);
        w.WriteUInt32(0);
        w.WriteUInt32(option);
        w.WriteUInt64(0x1001);
        w.WriteUInt64(0x1001);
        w.WriteUInt64(0x1001);
        w.WriteUInt64(item);
        w.WriteByte(1);
        return w.Written.ToArray();
    }

    private static byte[] ProximityClick(ulong owner, ulong item)
    {
        using var w = new PacketWriter();
        w.WriteByte(0xc8);
        w.WriteUInt16(1);
        w.WriteUInt64(0);
        w.WriteUInt64(owner);
        w.WriteUInt64(item);
        w.WriteUInt64(0x1001);
        w.WriteUInt32(1);
        w.WriteInt32(-1);
        return w.Written.ToArray();
    }
}
