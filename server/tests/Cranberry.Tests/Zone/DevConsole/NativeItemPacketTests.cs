using Cranberry.Protocol;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

public sealed class NativeItemPacketTests
{
    [Fact]
    public void AddReadsTheExactAugustSerializerFieldOrder()
    {
        // Deliberately different field values; synthesized from FUN_14126ba40's writes.
        var packet = Convert.FromHexString("09EA034433221188776655CCBBAA99080706050403020110203040");
        var request = NativeItemAdd.Parse(packet);
        Assert.Equal(0x11223344u, request.DefinitionId);
        Assert.Equal(0x55667788u, request.TintId);
        Assert.Equal(0x99aabbccu, request.Count);
        Assert.Equal(0x0102030405060708UL, request.TargetGuid);
        Assert.Equal(0x40302010u, request.RentalTerm);
        packet[0] = ConsoleOpcodes.AdminBase;
        Assert.Equal(request, NativeItemAdd.Parse(packet));
    }

    [Fact]
    public void ListReadsTheExactAugustSerializerFieldOrder()
    {
        var packet = Convert.FromHexString("093C040807060504030201AB");
        Assert.Equal(new NativeItemList(0x0102030405060708UL, 0xab), NativeItemList.Parse(packet));
        packet[0] = ConsoleOpcodes.AdminBase;
        Assert.Equal(new NativeItemList(0x0102030405060708UL, 0xab), NativeItemList.Parse(packet));
    }

    [Fact]
    public void DropAndDeleteReadTheOriginalSerializersIncludingTheTwoByteItemsSubOpcode()
    {
        Assert.Equal(new NativeItemDrop(0x0102030405060708UL, 0x11223344),
            NativeItemDrop.Parse(Convert.FromHexString("151100080706050403020144332211")));
        Assert.Equal(new NativeItemDelete(0x0102030405060708UL, 0x1112131415161718UL),
            NativeItemDelete.Parse(Convert.FromHexString("09EB0308070605040302011817161514131211")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DropAndDeleteRejectTruncatedTrailingOrWrongOpcodeData(bool drop)
    {
        byte[] packet = drop ? Convert.FromHexString("151100080706050403020144332211")
            : Convert.FromHexString("09EB0308070605040302011817161514131211");
        void Parse(byte[] value)
        {
            if (drop) _ = NativeItemDrop.Parse(value);
            else _ = NativeItemDelete.Parse(value);
        }
        for (int length = 0; length < packet.Length; length++)
        {
            byte[] truncated = packet[..length];
            Assert.Throws<PacketFormatException>(() => Parse(truncated));
        }
        Assert.Throws<PacketFormatException>(() => Parse([.. packet, 0]));
        packet[2] ^= 1;
        Assert.Throws<PacketFormatException>(() => Parse(packet));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KnownPacketsRejectEveryTruncationTrailingDataAndDifferentOpcodes(bool add)
    {
        byte[] packet = add
            ? Convert.FromHexString("09EA034433221188776655CCBBAA99080706050403020110203040")
            : Convert.FromHexString("093C040807060504030201AB");
        void Parse(byte[] value)
        {
            if (add) _ = NativeItemAdd.Parse(value);
            else _ = NativeItemList.Parse(value);
        }
        for (int length = 0; length < packet.Length; length++)
        {
            byte[] truncated = packet[..length];
            Assert.Throws<PacketFormatException>(() => Parse(truncated));
        }
        Assert.Throws<PacketFormatException>(() => Parse([.. packet, 0]));
        packet[0] = 8;
        Assert.Throws<PacketFormatException>(() => Parse(packet));
        packet[0] = 9;
        packet[1] ^= 1;
        Assert.Throws<PacketFormatException>(() => Parse(packet));
    }
}
