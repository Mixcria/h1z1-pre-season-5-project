using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class ResourceEventPacketTests
{
    [Fact]
    public void DamagedPlayerHealthUsesTheCompleteNativeResourceRow()
    {
        using var writer = new PacketWriter();
        CharacterResourceUpdate.Health(4129, 9000, 10000, 10000).WriteTo(writer);
        Assert.Equal(Convert.FromHexString(
            "8D0000000003211000000000000001000000010000002823000010270000"
            + new string('0', 71 * 2)), writer.Written.ToArray());
    }

    [Theory]
    [InlineData(2500u, 5000u, 5000u, 5000u, 10000u)]
    [InlineData(0u, 4000u, 10000u, 0u, 4000u)]
    [InlineData(500u, 50u, 100u, 10000u, 5000u)]
    [InlineData(uint.MaxValue, uint.MaxValue, uint.MaxValue, 10000u, 10000u)]
    public void HealthResourceUsesTheAuthoredMaximumWithOverflowSafeNormalization(
        uint current, uint previous, uint maximum, uint expected, uint expectedPrevious)
    {
        var update = CharacterResourceUpdate.Health(1, current, previous, maximum);
        Assert.Equal(expected, update.Value);
        Assert.Equal(expectedPrevious, update.PreviousValue);
    }

    [Fact]
    public void InitialStaminaMatchesTheAugustTypeThreeVector()
    {
        CharacterResource stamina = Assert.Single(
            CharacterResource.Starter,
            resource => resource.ResourceType == 6);
        var packet = CharacterResourceUpdate.Initial(0x1001, stamina);
        using var writer = new PacketWriter();
        packet.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(CharacterResourceUpdate.WireLength, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "8D0000000003011000000000000006000000060000005802000058020000"
                + new string('0', (CharacterResourceUpdate.WireLength - 30) * 2)),
            bytes);
    }

    [Fact]
    public void InitialHealthCarriesEqualValueAndPreviousValue()
    {
        CharacterResource health = Assert.Single(
            CharacterResource.Starter,
            resource => resource.ResourceType == 1);
        var packet = CharacterResourceUpdate.Initial(0x1001, health);
        using var writer = new PacketWriter();
        packet.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(CharacterResourceUpdate.Opcode, bytes[0]);
        Assert.Equal(CharacterResourceUpdate.EventType, bytes[5]);
        Assert.Equal(Convert.FromHexString("1027000010270000"), bytes[22..30]);
        Assert.All(bytes[30..], value => Assert.Equal(0, value));
    }
}
