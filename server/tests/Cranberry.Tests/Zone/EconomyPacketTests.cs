using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class EconomyPacketTests
{
    [Fact]
    public void CapturedNativeGrinderRequestAndAugustScalarResponse()
    {
        // wire-20260905-221208.txt, 22:12:30.300; gateway prefix 06 omitted.
        var request = GrinderExchangeRequest.Parse(Convert.FromHexString("DF0100010000002909000001000000"));
        Assert.Equal(new AccountRewardRow(2345, 1), Assert.Single(request.Items));
        Assert.Equal(Convert.FromHexString("DF020014000000"), Bytes(w => new GrinderExchangeResponse(20).WriteTo(w)));
        foreach (string invalid in new[] { "DF010000000000", "DF0100010000002909000000000000",
            "DF0100010000002909000065000000", "DF010001000000290900000100000000",
            "DF01000200000029090000010000002909000001000000", "DF0200010000002909000001000000" })
            Assert.Throws<PacketFormatException>(() => GrinderExchangeRequest.Parse(Convert.FromHexString(invalid)));
    }

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void NativeScrapAndRemoveRowsUseDifferentIdsAndCounts()
    {
        Assert.Equal(Convert.FromHexString("AC2014000000"), Bytes(w => new ReportRewardScrap(20).WriteTo(w)));
        Assert.Equal(Convert.FromHexString("AC13300700000042524301000000"),
            Bytes(w => new RemoveAccountItem(0x4352420000000730).WriteTo(w)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bytes(w => new ReportRewardScrap(uint.MaxValue).WriteTo(w)));
    }

    [Fact]
    public void PreviewRewardListHasNoWinnerAndOpeningCarriesBothNativeArrays()
    {
        Assert.Equal(Convert.FromHexString("AC1F00000000010000001407000001000000"),
            Bytes(w => new ReportRewardCrateContents([], [new(1812)]).WriteTo(w)));
        byte[] result = Bytes(w => new ReportRewardCrateContents([new(1812)], [new(1812), new(1810)]).WriteTo(w));
        var reader = new PacketReader(result.AsSpan(2));
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(1812u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(2, reader.ReadInt32());
        Assert.Equal(16, reader.Remaining);
    }

    [Fact]
    public void ScrapRequestHasPhaseAndUseOptionRatherThanAnInventedQuantity()
    {
        byte[] bytes = Convert.FromHexString("AC2D0100000000000000620000001407000001");
        RequestUseAccountItem request = RequestUseAccountItem.Parse(bytes);
        Assert.Equal(98u, request.ItemUseOptionId);
        Assert.Equal(1812u, request.AccountItemId);
        Assert.Equal(0u, request.StackAtClick);
        Assert.Equal(Convert.FromHexString("AC2D020000000B000000620000001407000001"),
            Bytes(w => request.WriteResponse(w, 11)));
        AssertEveryTruncationRejected(bytes, p => RequestUseAccountItem.Parse(p));
        Assert.Throws<PacketFormatException>(() => RequestUseAccountItem.Parse([.. bytes, 0]));
    }

    [Fact]
    public void AccountTypedMapsAreFullyConsumedAndStackAtClickIsSeparate()
    {
        byte[] bytes = Convert.FromHexString("AC2D0100000000000000620000001407000000"
            + "010000000100000003000000" // integer map: key1 stack-at-click3
            + "00000000" // float map
            + "01000000020000003007000000425243" // guid map
            + "00000000" // vector map
            + "00000000"); // string map
        RequestUseAccountItem request = RequestUseAccountItem.Parse(bytes);
        Assert.Equal(3u, request.StackAtClick);
        Assert.Equal(0x4352420000000730ul, request.UInt64Parameters[2]);
        AssertEveryTruncationRejected(bytes, p => RequestUseAccountItem.Parse(p));
    }

    [Fact]
    public void OpenCratesRequiresMatchingKeysAndBoundedPositiveCounts()
    {
        byte[] bytes = Convert.FromHexString("AC39010000000000000001000000880C0000880C000002000000");
        RequestOpenAccountCrate request = RequestOpenAccountCrate.Parse(bytes);
        Assert.Equal(new AccountRewardRow(3208, 2), Assert.Single(request.Crates));
        AssertEveryTruncationRejected(bytes, p => RequestOpenAccountCrate.Parse(p));
        byte[] mismatch = (byte[])bytes.Clone();
        mismatch[14]++;
        Assert.Throws<PacketFormatException>(() => RequestOpenAccountCrate.Parse(mismatch));
        byte[] excessive = (byte[])bytes.Clone();
        excessive[22] = 100;
        Assert.Equal(100u, Assert.Single(RequestOpenAccountCrate.Parse(excessive).Crates).Count);
        excessive[22] = 101;
        Assert.Throws<PacketFormatException>(() => RequestOpenAccountCrate.Parse(excessive));
    }

    [Fact]
    public void PreviewRequestsAreExactlyFourteenBytesAndNeverAcceptedAsReplies()
    {
        byte[] bytes = Convert.FromHexString("AC3A0100000000000000880C0000");
        RequestPreviewAccountCrateRewards request = RequestPreviewAccountCrateRewards.Parse(bytes);
        Assert.Equal(3208u, request.CrateItemId);
        AssertEveryTruncationRejected(bytes, p => RequestPreviewAccountCrateRewards.Parse(p));
        Assert.Throws<PacketFormatException>(() => RequestPreviewAccountCrateRewards.Parse(Bytes(w => request.WriteResponse(w))));
    }

    private static void AssertEveryTruncationRejected(byte[] bytes, Action<byte[]> parse)
    {
        for (int length = 0; length < bytes.Length; length++)
            Assert.Throws<PacketFormatException>(() => parse(bytes[..length]));
    }
}
