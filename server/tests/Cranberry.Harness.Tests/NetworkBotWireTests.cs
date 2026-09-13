using System.Numerics;
using Cranberry.Harness.Protocol;
using Cranberry.NetworkBots;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;

namespace Cranberry.Harness.Tests;

public sealed class NetworkBotWireTests
{
    [Theory]
    [InlineData(-1454.35f, -22.28f, -1498.17f)]
    [InlineData(1.25f, 1500.75f, -4095.5f)]
    [InlineData(0f, .01f, -.01f)]
    public void GeneratedPlayerMovementReachesThePlayerChannelAndDecodesCoordinates(float x, float y, float z)
    {
        byte[] packet = BotWire.Movement(new(x, y, z), 123456);
        Assert.Equal((6, 2), ((int)(packet[0] & 31), (int)(packet[0] >> 5)));
        var decoded = ClientMovementUpdate.Parse(packet.AsSpan(1));
        Assert.Equal(123456u, decoded.ClientTime);
        Assert.True(Vector3.Distance(new(x, y, z), decoded.Position!.Value) < .02f);
    }

    [Fact]
    public void PostureAndAimDoNotShiftTheMovementCoordinates()
    {
        byte[] packet = BotWire.Movement(new(123.45f, -8, 18), 9000, posture: 0x403, yaw: 1.2f);
        var decoded = ClientMovementUpdate.Parse(packet.AsSpan(1));
        Assert.Equal(0x403u, decoded.Posture);
        Assert.Equal(1.2f, decoded.Orientation);
        Assert.Equal(123.45f, decoded.Position!.Value.X, 2);
    }

    [Fact]
    public void CanopyMovementIncludesTheRecordedManagedOpcodeAndOwnedTransient()
    {
        byte[] packet = BotWire.Movement(new(100, 750, -300), 1700, managed: 2);
        Assert.Equal(new byte[] { 0x66, 0x90, 0x08 }, packet[..3]);
        var parsed = ClientManagedMovementUpdate.Parse(packet.AsSpan(1));
        Assert.Equal(2u, parsed.TransientId);
        Assert.Equal(new Vector3(100, 750, -300), parsed.Movement.Position);
    }

    [Fact]
    public void HotbarRequestContainsAllThreeClientFields()
    {
        var packet = BotWire.SelectSlot(3);
        Assert.True(SelectLoadoutSlotRequest.TryParse(packet.AsSpan(1), out var request));
        Assert.Equal(0u, request!.Unknown);
        Assert.Equal(3u, request.SlotId);
    }

    [Fact]
    public void GroundItemDescriptionDoesNotCountAsInventoryOwnership()
    {
        var observation = new BotObservation { SelfGuid = () => 4097 };
        byte[] Item(ulong owner, ulong item)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)5); w.Write((byte)0x11); w.Write((ushort)2); w.Write(owner);
            w.Write(63u); w.Write(1429u); w.Write(0u); w.Write(item); w.Write(30u);
            w.Write(false); w.Write(owner == 4097 ? 100ul : 0ul); w.Write(0u); w.Write(1u);
            w.Write(new byte[32]); return stream.ToArray();
        }
        observation.Observe(ObservedPacket.ParseGateway(Item(9999, 9999)), TimeSpan.Zero);
        Assert.Empty(observation.Inventory);
        Assert.Equal(0, observation.ItemAdds);
        Assert.Equal(1429u, observation.WorldItemDefinitions[9999]);
        observation.Observe(ObservedPacket.ParseGateway(Item(4097, 777)), TimeSpan.Zero);
        Assert.Single(observation.Inventory);
        Assert.Equal(1, observation.ItemAdds);
    }

    [Fact]
    public void LootStackUpdatesCountAsGrantsWithoutCountingDuplicatesOrDurability()
    {
        var observation = new BotObservation { SelfGuid = () => 4097 };
        void Receive(ushort kind, uint count) => observation.Observe(
            ObservedPacket.ParseGateway(InventoryPacket(kind, 4097, 777, 1429, count)), TimeSpan.Zero);
        Receive(2, 30);
        Receive(3, 60); // Second ground stack merges into the already held item.
        Assert.Equal(1, observation.ItemAdds);
        Assert.Equal(60L, observation.GrantedUnitsByDefinition[1429]);
        Assert.Equal(60u, observation.Inventory[777].Count);
        Receive(3, 60); // Retransmission or durability-only update grants nothing.
        Receive(2, 60); // A repeated ItemAdd for the same GUID also grants nothing.
        Assert.Equal(60L, observation.GrantedUnitsByDefinition[1429]);
        Receive(3, 20); // Consumption reduces holdings without inventing a pickup.
        Assert.Equal(60L, observation.GrantedUnitsByDefinition[1429]);
        Receive(3, 25);
        Assert.Equal(65L, observation.GrantedUnitsByDefinition[1429]);
    }

    [Fact]
    public void UnknownForeignAndMalformedUpdatesCannotClaimALootRace()
    {
        var observation = new BotObservation { SelfGuid = () => 4097 };
        void Receive(byte[] bytes) => observation.Observe(ObservedPacket.ParseGateway(bytes), TimeSpan.Zero);
        Receive(InventoryPacket(3, 4097, 777, 1429, 30)); // Unknown GUID: native client ignores it.
        Receive(InventoryPacket(2, 9999, 777, 1429, 30)); // Ground item description.
        Assert.Empty(observation.Inventory);
        Assert.Empty(observation.GrantedUnitsByDefinition);
        Receive(InventoryPacket(2, 4097, 777, 1429, 30));
        Receive(InventoryPacket(3, 9999, 777, 1429, 60)); // Foreign owner.
        Receive(InventoryPacket(3, 4097, 777, 2425, 60)); // Definition mismatch.
        Receive(InventoryPacket(3, 4097, 777, 1429, 60)[..^1]); // Truncated fixed native record.
        Assert.Equal(30u, observation.Inventory[777].Count);
        Assert.Equal(30L, observation.GrantedUnitsByDefinition[1429]);
        Assert.False(observation.GrantedUnitsByDefinition.ContainsKey(2425));
    }

    private static byte[] InventoryPacket(ushort kind, ulong owner, ulong guid, uint definition, uint count)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)5); writer.Write((byte)0x11); writer.Write(kind); writer.Write(owner);
        if (kind == 2) writer.Write(63u);
        writer.Write(definition); writer.Write(0u); writer.Write(guid); writer.Write(count);
        writer.Write(false); writer.Write(100ul); writer.Write(0u); writer.Write(1u);
        writer.Write(new byte[25]); // Remaining native fixed item fields, independent of server encoder.
        if (kind == 2) writer.Write((byte)0); // Generic item class tail only belongs to ItemAdd.
        return stream.ToArray();
    }

    [Fact]
    public void FireHintCarriesTheSameProjectileAndNormalizedAim()
    {
        var bytes = BotWire.FireHint(500, new(100, 20, 100), new(103, 20, 104), 12, 700);
        Assert.True(Cranberry.Zone.Combat.WeaponBaseDecoder.TryReadWeaponFireHint(bytes.AsSpan(1), out var hint));
        Assert.Equal(500ul, hint.WeaponGuid);
        var entry = Assert.Single(hint.Hints);
        Assert.Equal(12u, entry.ProjectileId);
        Assert.Equal(.6f, entry.DirectionX, 5);
        Assert.Equal(.8f, entry.DirectionZ, 5);
    }

    [Fact]
    public void QaCharacterCreationUsesTheAugustCreateEnvelope()
    {
        byte[] packet = LoginWire.CreateCharacter("NetProbe");
        var request = Cranberry.Login.CharacterCreateRequest.Parse(packet.AsSpan(1));
        var payload = Cranberry.Login.CharacterCreatePayload.Parse(request.Payload);
        Assert.Equal(1ul, request.ServerId);
        Assert.Equal("NetProbe", payload.Name);
        Assert.Equal(AugustClient.Version, payload.ClientVersion);
    }

    [Fact]
    public void MovementWindowsExcludeOlderQueuedSamplesAndExposeAStalledPeer()
    {
        var observation = new BotObservation { Tick = () => 2050 };
        observation.Peers[100] = 16;
        observation.Peers[101] = 17;
        observation.BeginMovementWindow(2000);
        void Pose(uint tick)
        {
            byte[] movement = BotWire.Movement(Vector3.Zero, tick);
            observation.Observe(ObservedPacket.ParseGateway([5, 0x78, 0x40, .. movement[1..]]), TimeSpan.Zero);
        }
        Pose(1900); // A queued packet from before this phase must not count toward its rate.
        Pose(2030);
        var window = observation.MovementWindow(2100, airborne: false);
        Assert.Equal(1, window.Samples);
        Assert.Equal(1, window.OlderRecords);
        Assert.Equal(20, window.P99Ms);
        Assert.Equal(1, window.MissingPeers);
        Assert.Equal(0, window.MinimumPairHz);
        observation.Peers.Remove(101);
        Assert.Equal(70, observation.MovementWindow(2100, airborne: false).StalestPeerMs);
        observation.BeginMovementWindow(2200);
        window = observation.MovementWindow(2500, airborne: false);
        Assert.Equal(1, window.MissingPeers);
        Assert.Equal(470, window.StalestPeerMs); // Report its actual age even with no new phase sample.
    }
}
