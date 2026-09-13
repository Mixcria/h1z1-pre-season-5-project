using System.Buffers.Binary;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    private static byte[] NativeAddPacket(uint definitionId, uint count = 1, ulong target = 0)
    {
        byte[] packet = new byte[NativeItemAdd.Length];
        packet[0] = 9;
        packet[1] = 0xea;
        packet[2] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), definitionId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11), count);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(15), target);
        return packet;
    }

    private static byte[] NativeListPacket(ulong target = 0)
    {
        byte[] packet = new byte[NativeItemList.Length];
        packet[0] = 9;
        packet[1] = 0x3c;
        packet[2] = 4;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(3), target);
        return packet;
    }

    private static void SendNativeItem(ZoneService service, SoeConnection connection, byte[] payload)
    {
        byte[] packet = new byte[payload.Length + 1];
        packet[0] = new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte();
        payload.CopyTo(packet, 1);
        service.OnMessage(connection, packet);
    }

    private static byte[] NativeRemovePacket(ulong itemGuid, bool delete, uint count = 1, ulong target = 0)
    {
        byte[] packet = new byte[delete ? NativeItemDelete.Length : NativeItemDrop.Length];
        packet[0] = delete ? (byte)9 : NativeItemDrop.BaseOpcode;
        packet[1] = delete ? (byte)0xeb : (byte)0x11;
        packet[2] = delete ? (byte)3 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(3), itemGuid);
        if (delete) BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(11), target);
        else BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11), count);
        return packet;
    }

    [Fact]
    public void NativeItemAddUsesRealPickupAndSplitsCountsLargerThanOneStack()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        SendNativeItem(service, connection, NativeAddPacket(2124)); // Real backpack capacity.
        Assert.Contains(inventory.Items.Values, i => i.DefinitionId == 2124 && i.LoadoutSlotId != 0);
        long beforeCount = inventory.Items.Values.Where(i => i.DefinitionId == 1429).Sum(i => (long)i.Count);
        int before = SentCount(recorder);

        SendNativeItem(service, connection, NativeAddPacket(1429, 120, Member<ulong>(connection, "Guid")));

        Assert.Equal(beforeCount + 120, inventory.Items.Values.Where(i => i.DefinitionId == 1429).Sum(i => (long)i.Count));
        Assert.All(inventory.Items.Values.Where(i => i.DefinitionId == 1429), i => Assert.True(i.Count <= i.Fact.MaxStackSize));
        Assert.Contains(ConsoleLines(recorder, before), line => line.StartsWith("+ gave") && line.EndsWith("x120"));
        Assert.Contains(Sent(recorder, before), packet => !IsConsolePrint(packet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeItemListShowsTheSameRealInventoryWithoutMutatingIt(bool explicitSelf)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SendNativeItem(service, connection, NativeAddPacket(2424, 3));
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        var original = inventory.Items.Values.Select(i => (i.Guid, i.DefinitionId, i.Count)).ToArray();
        int before = SentCount(recorder);
        byte[] packet = NativeListPacket(explicitSelf ? Member<ulong>(connection, "Guid") : 0);
        packet[0] = ConsoleOpcodes.AdminBase;

        SendNativeItem(service, connection, packet);

        // First aid grants share one stack in the dedicated medical slot.
        Assert.Equal(3L, inventory.Items.Values.Where(i => i.DefinitionId == 2424).Sum(i => (long)i.Count));
        Assert.Single(ConsoleLines(recorder, before), line => line.Contains("Tactical First Aid Kit x3"));
        Assert.Equal(original, inventory.Items.Values.Select(i => (i.Guid, i.DefinitionId, i.Count)).ToArray());
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
    }

    [Theory]
    [InlineData("tint", "tint")]
    [InlineData("rental", "addRental")]
    [InlineData("target", "own inventory")]
    [InlineData("unknown-id", "unknown inventory item")]
    [InlineData("zero-count", "invalid count")]
    [InlineData("huge-count", "invalid count")]
    [InlineData("short", "27 bytes")]
    [InlineData("long", "27 bytes")]
    [InlineData("player", "Tester or above")]
    [InlineData("lobby", "needs InMatch")]
    public void NativeItemAddRejectsUnsupportedMalformedOrUnauthorizedRequestsBeforeMutation(string variant, string refusal)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        var original = inventory.Items.Values.Select(i => (i.Guid, i.DefinitionId, i.Count)).ToArray();
        byte[] packet = NativeAddPacket(2424);
        switch (variant)
        {
            case "tint": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7), 1); break;
            case "rental": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), 1); break;
            case "target": BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(15), 0x9999); break;
            case "unknown-id": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), uint.MaxValue); break;
            case "zero-count": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11), 0); break;
            case "huge-count": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11), 1001); break;
            case "short": packet = packet[..^1]; break;
            case "long": packet = [.. packet, 0]; break;
            case "player": Session(connection).TierOverride = ConsoleTier.Player; break;
            case "lobby": EnterStep(connection, "Lobby"); break;
        }
        int before = SentCount(recorder);

        SendNativeItem(service, connection, packet);

        Assert.Equal(original, inventory.Items.Values.Select(i => (i.Guid, i.DefinitionId, i.Count)).ToArray());
        if (variant == "player") Assert.Empty(Sent(recorder, before));
        else Assert.Contains(ConsoleLines(recorder, before), line => line.Contains(refusal, StringComparison.OrdinalIgnoreCase));
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("target")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("player")]
    public void NativeItemListRefusesUnsupportedOrUnauthorizedRequests(string variant)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        byte[] packet = NativeListPacket();
        switch (variant)
        {
            case "mode": packet[11] = 1; break;
            case "target": BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(3), 0x9999); break;
            case "short": packet = packet[..^1]; break;
            case "long": packet = [.. packet, 0]; break;
            case "player": Session(connection).TierOverride = ConsoleTier.Player; break;
        }
        int before = SentCount(recorder);
        SendNativeItem(service, connection, packet);
        if (variant == "player") Assert.Empty(Sent(recorder, before));
        else
        {
            string line = Assert.Single(ConsoleLines(recorder, before));
            Assert.True(line.StartsWith('-') || line.StartsWith('!'), line);
        }
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
    }

    [Fact]
    public void NativeItemDropMovesOnlyTheRequestedUnitsToStreamedGroundLoot()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SendNativeItem(service, connection, NativeAddPacket(1429, 20));
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        var rounds = Assert.Single(inventory.Items.Values, i => i.DefinitionId == 1429);
        var loot = Member<LootWorld>(connection, "Loot");
        var beforeGround = loot.Items.Select(i => i.WorldGuid).ToHashSet();
        int before = SentCount(recorder);

        SendNativeItem(service, connection, NativeRemovePacket(rounds.Guid, delete: false, count: 7));

        Assert.Equal(13u, inventory.Items[rounds.Guid].Count);
        var dropped = Assert.Single(loot.Items.Where(i => !beforeGround.Contains(i.WorldGuid)));
        Assert.Equal(7u, dropped.Count);
        Assert.Equal(1429u, dropped.ItemDefinitionId);
        Assert.True(Member<MatchLoot>(connection, "StreamedLoot").IsStreamed(LootStreamKey.ForDropped(dropped.WorldGuid)));
        Assert.Contains(ConsoleLines(recorder, before), line => line.StartsWith("+ dropped") && line.EndsWith("x7"));
    }

    [Theory]
    [InlineData(2425u)]
    [InlineData(2424u)]
    public void NativeItemDeleteRemovesTheInstanceAndBindingsWithoutDroppingOrConsuming(uint definition)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SendNativeItem(service, connection, NativeAddPacket(definition));
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        var item = Assert.Single(inventory.Items.Values, i => i.DefinitionId == definition);
        var shooter = Member<SessionCombat>(connection, "Combat").Shooter;
        if (definition == 2425) shooter.DeclareWeapon(item.Guid, definition, 17);
        SetSessionMember(connection, "Hitpoints", 5000u);
        var loot = Member<LootWorld>(connection, "Loot");
        var beforeGround = loot.Items.Select(i => i.WorldGuid).ToArray();
        int before = SentCount(recorder);

        SendNativeItem(service, connection, NativeRemovePacket(item.Guid, delete: true,
            target: Member<ulong>(connection, "Guid")));

        Assert.False(inventory.Items.ContainsKey(item.Guid));
        Assert.DoesNotContain(inventory.LoadoutSlots.Values, i => i.Guid == item.Guid);
        Assert.DoesNotContain(inventory.EquipmentSlots.Values, i => i.Guid == item.Guid);
        Assert.False(shooter.Knows(item.Guid));
        Assert.Equal(5000u, Member<uint>(connection, "Hitpoints"));
        Assert.Equal(beforeGround, loot.Items.Select(i => i.WorldGuid).ToArray());
        Assert.Contains(Sent(recorder, before), p => p.Length > 3 && p[1] == 0x11 && p[2] == 4);
        Assert.Contains(ConsoleLines(recorder, before), line => line.StartsWith("+ deleted"));
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "required")]
    [InlineData(true, "required")]
    [InlineData(false, "zero-count")]
    [InlineData(false, "too-many")]
    [InlineData(true, "target")]
    [InlineData(false, "short")]
    [InlineData(true, "long")]
    [InlineData(false, "player")]
    [InlineData(true, "lobby")]
    public void NativeItemRemovalRefusesInvalidTargetsAndProtectedOrUnauthorizedMutations(bool delete, string variant)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SendNativeItem(service, connection, NativeAddPacket(2424));
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        var item = Assert.Single(inventory.Items.Values, i => i.DefinitionId == 2424);
        ulong guid = variant == "required" ? inventory.LoadoutSlots[SurvivorLoadout.Fists].Guid
            : variant == "unknown" ? ulong.MaxValue : item.Guid;
        byte[] packet = NativeRemovePacket(guid, delete,
            count: variant == "zero-count" ? 0u : variant == "too-many" ? 2u : 1u,
            target: variant == "target" ? 0x9999UL : 0UL);
        if (variant == "short") packet = packet[..^1];
        if (variant == "long") packet = [.. packet, 0];
        if (variant == "player") Session(connection).TierOverride = ConsoleTier.Player;
        if (variant == "lobby") EnterStep(connection, "Lobby");
        var original = inventory.Items.Values.Select(i => (i.Guid, i.DefinitionId, i.Count)).ToArray();
        int before = SentCount(recorder);

        SendNativeItem(service, connection, packet);

        Assert.Equal(original, inventory.Items.Values.Select(i => (i.Guid, i.DefinitionId, i.Count)).ToArray());
        if (variant == "player") Assert.Empty(Sent(recorder, before));
        else Assert.NotEmpty(ConsoleLines(recorder, before));
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
    }
}
