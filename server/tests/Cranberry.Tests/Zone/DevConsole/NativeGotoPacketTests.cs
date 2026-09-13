using Cranberry.Protocol;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

public sealed class NativeGotoPacketTests
{
    [Fact]
    public void GotoReadsTheNativeSerializerOrderIncludingItsUninterpretedTail()
    {
        var request = NativeGotoRequest.Parse(Convert.FromHexString(
            "09360488776655443322110000000005000000416C6963650807060504030201"));
        Assert.False(request.Waypoint);
        Assert.Equal(0x1122334455667788UL, request.TargetGuid);
        Assert.Equal(0u, request.NpcDefinitionId);
        Assert.Equal("Alice", request.TargetName);
        Assert.Equal(0x0102030405060708UL, request.TrailingValue);
    }

    [Fact]
    public void NpcDefinitionAndWaypointAreDifferentNativeLayouts()
    {
        var npc = NativeGotoRequest.Parse(Convert.FromHexString(
            "0A36040000000000000000D2040000000000000000000000000000"));
        Assert.Equal(1234u, npc.NpcDefinitionId);
        Assert.Empty(npc.TargetName);
        var waypoint = NativeGotoRequest.Parse(Convert.FromHexString(
            "093804887766554433221105000000416C696365"));
        Assert.True(waypoint.Waypoint);
        Assert.Equal(0x1122334455667788UL, waypoint.TargetGuid);
        Assert.Equal("Alice", waypoint.TargetName);
        Assert.Equal(0UL, waypoint.TrailingValue);
    }

    [Theory]
    [InlineData("093604000000000000000000000000000000000000000000000000")]
    [InlineData("093804000000000000000000000000")]
    public void EveryTruncationAndTrailingByteIsRejected(string hex)
    {
        byte[] full = Convert.FromHexString(hex);
        _ = NativeGotoRequest.Parse(full);
        for (int length = 0; length < full.Length; length++)
        {
            byte[] truncated = full[..length];
            Assert.Throws<PacketFormatException>(() => NativeGotoRequest.Parse(truncated));
        }
        Assert.Throws<PacketFormatException>(() => NativeGotoRequest.Parse([.. full, 0]));
    }

    [Fact]
    public void MatchesClaimsRecognizedHeadersEvenWhenTheBodyIsMalformed()
    {
        Assert.True(NativeGotoRequest.Matches([9, 0x36, 4]));
        Assert.True(NativeGotoRequest.Matches([10, 0x38, 4]));
        Assert.False(NativeGotoRequest.Matches([9, 0x37, 4]));
        Assert.False(NativeGotoRequest.Matches([11, 0x36, 4]));
        Assert.Throws<PacketFormatException>(() => NativeGotoRequest.Parse(
            Convert.FromHexString("0938040000000000000000FFFFFFFF")));
    }
}
