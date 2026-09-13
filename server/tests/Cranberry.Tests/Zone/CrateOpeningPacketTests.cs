using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone;

public sealed class CrateOpeningPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void StartRequestIsExactlyNativeItemAndOneFlagWithoutAnItemsPhase()
    {
        byte[] request = Convert.FromHexString("F503880C000001");
        Assert.Equal(new StartCrateOpening(3208, true), StartCrateOpening.Parse(request));
        Assert.Equal(new StartCrateOpening(3208, false), StartCrateOpening.Parse(Convert.FromHexString("F503880C000000")));
        for (int i = 0; i < request.Length; i++)
        {
            byte[] truncated = request[..i];
            Assert.Throws<PacketFormatException>(() => StartCrateOpening.Parse(truncated));
        }
        Assert.Throws<PacketFormatException>(() => StartCrateOpening.Parse([.. request, 0]));
        Assert.Throws<PacketFormatException>(() => StartCrateOpening.Parse(Convert.FromHexString("F503880C000002")));
        Assert.Throws<PacketFormatException>(() => StartCrateOpening.Parse(Convert.FromHexString("F5030000000001")));
    }

    [Fact]
    public void CompletionPacketsHaveNoTransactionBody()
    {
        Assert.True(StartCrateOpening.IsCompletion([0xf5, 4]));
        Assert.True(StartCrateOpening.IsCompletion([0xf5, 5]));
        Assert.False(StartCrateOpening.IsCompletion([0xf5, 5, 0]));
        Assert.False(StartCrateOpening.IsCompletion([0xf5, 3]));
        Assert.False(StartCrateOpening.IsCompletion([]));
    }

    [Fact]
    public void NativeRowsUseLocaleAndImageMetadataRatherThanAccountItemIds()
    {
        Assert.Equal(Convert.FromHexString("F507010000003C0000007800000007000000"),
            Bytes(w => new CrateOpeningOpened(60, 120, 7).WriteTo(w)));
        Assert.Equal(Convert.FromHexString("F506010000003C00000078000000FF0000007F00000001000000"),
            Bytes(w => new CrateOpeningUnopened([new(60, 120, 255, 127, 1)]).WriteTo(w)));
        Assert.Equal(Convert.FromHexString("F50600000000"), Bytes(w => new CrateOpeningUnopened([]).WriteTo(w)));
    }

    [Fact]
    public void SuccessResetsBeforeEachRewardAndFinishesWithoutResettingResults()
    {
        var catalog = EconomyCatalog.Default;
        var skin = catalog.Skins[1812];
        var award = new OwnedAccountItem(1, skin.AccountItemId, skin.RewardItemId, 2, "crate:3208", true);
        var packets = CrateOpeningPresentation.Result(catalog.Crates[3208], [award], true);
        Assert.Equal(new byte[] { 2, 6, 7, 7, 2 }, packets.Select(packet => packet[1]).ToArray());
        Assert.Equal(1, packets[0][^6]); // ResetTables
        Assert.Equal(0, packets[^1][^6]);
        Assert.Equal(1, packets[^1][^5]); // ShowAgain
        var count = new PacketReader(packets[^1].AsSpan(packets[^1].Length - 4));
        Assert.Equal(2u, count.ReadUInt32());
        foreach (byte[] packet in packets.Skip(2).Take(2))
        {
            var row = new PacketReader(packet.AsSpan(2));
            Assert.Equal(1, row.ReadInt32()); // Native handler only presents the first row.
            Assert.Equal(skin.NameLocaleId, row.ReadUInt32());
            Assert.Equal(skin.ImageSetId, row.ReadUInt32());
            Assert.Equal(skin.RarityId, row.ReadUInt32());
            Assert.Equal(0, row.Remaining);
        }
        Assert.NotEqual(skin.AccountItemId, skin.NameLocaleId);
    }

    [Fact]
    public void RejectionClearsPriorRewardsAndShowsFinishedWithoutClaimingAReward()
    {
        var packets = CrateOpeningPresentation.Result(null, [], false);
        Assert.Equal(new byte[] { 2, 6, 2 }, packets.Select(packet => packet[1]).ToArray());
        Assert.Equal(0, packets[^1][4]); // CrateIsOpen
        Assert.Equal(0, packets[^1][^5]); // ShowAgain
        Assert.Contains("UI.CrateOpening.FinishedNoCratesOpened", System.Text.Encoding.UTF8.GetString(packets[^1]));
    }
}
