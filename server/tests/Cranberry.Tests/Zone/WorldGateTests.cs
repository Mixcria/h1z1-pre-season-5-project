using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class WorldGateTests
{
    [Fact]
    public void NetworkProximityUpdatesCompleteIsFamilyByteAndSubOpcodeOnly()
    {
        // FUN_140a60d00 reads u8 family + u16 sub-opcode and requires the packet to end there;
        // registration id 0x34001100 = cClientUpdatePacketIdNetworkProximityUpdatesComplete.
        using var writer = new PacketWriter();
        new NetworkProximityUpdatesComplete().WriteTo(writer);
        Assert.Equal(new byte[] { 0x11, 0x34, 0x00 }, writer.Written.ToArray());
    }

    [Fact]
    public void ReferenceDataCarriesTypeNameLengthsAndRawBlob()
    {
        // FUN_140a12af0: u16 (13-bit length | 0x2000 hash flag) + name; FUN_140b055c0: u32
        // uncompressed length, i32 blob length, blob (raw because the lengths agree, FUN_14220e860).
        using var writer = new PacketWriter();
        ReferenceData.EmptyProfileDefinitions.WriteTo(writer);
        Assert.Equal(
            Convert.FromHexString(
                "17" +
                "1220" + "50726F66696C65446566696E6974696F6E73" + "00" +
                "04000000" +
                "04000000" + "00000000"),
            writer.Written.ToArray());
    }
}
