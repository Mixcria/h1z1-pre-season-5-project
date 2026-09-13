using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class EconomyPurchasePacketTests
{
    [Fact]
    public void CapturedAugustScrapyardOrderIncludesDefaultOutOfBandMetadata()
    {
        // QA wire-20260905-215814.txt:765, gateway byte06 removed.
        byte[] bytes = Convert.FromHexString("270300040000003430393704000000343039370100000005000000656E5F5553"
            + "00000000030000005343500000000001000000F4000000010000000100000001000000302E000000"
            + "3C4F6F62446174612072656E74616C5465726D49643D22302220706C6179657253747564696F49643D2230222F3E00");
        var request = EconomyOrderRequest.Parse(bytes);
        Assert.Equal(244u, Assert.Single(request.Lines).BundleId);
        Assert.Equal(EconomyOrderLine.NativeDefaultOutOfBandData, request.Lines[0].PaymentSource);
        Assert.Equal(bytes, Bytes(request.WriteTo));
    }
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // Native FUN_141029e70/14102a0d0/14102a270, not a server-authored fake transaction token.
    private static byte[] PlaceFixture() => Convert.FromHexString(
        "2703000400000034303937040000003430393701000000"
        + "05000000656E5F5553000000000300000053435000000000"
        + "01000000F400000001000000010000000100000030010000003000");

    [Fact]
    public void PlaceOrderParsesTheNativeTrackCurrencyAndBundleOrder()
    {
        byte[] fixture = PlaceFixture();
        EconomyOrderRequest order = EconomyOrderRequest.Parse(fixture);
        Assert.Equal(1u, order.TrackId);
        Assert.Equal("4097", order.CharacterReference);
        Assert.Equal("en_US", order.Context);
        Assert.Equal("SCP", order.CurrencyCode);
        Assert.Equal(new EconomyOrderLine(244, 1, 1, "0", "0"), Assert.Single(order.Lines));
        Assert.Equal(fixture, Bytes(order.WriteTo));
    }

    [Fact]
    public void PreviewUsesATrailingStringAndPlaceUsesOneFlagByte()
    {
        EconomyOrderRequest place = EconomyOrderRequest.Parse(PlaceFixture());
        var preview = place with { SubOpcode = EconomyOrderRequest.Preview, PreviewCode = "AB12" };
        byte[] bytes = Bytes(preview.WriteTo);
        Assert.Equal(PlaceFixture().Length + 7, bytes.Length);
        Assert.Equal("AB12", EconomyOrderRequest.Parse(bytes).PreviewCode);
        Assert.Equal(place.Fingerprint(), preview.Fingerprint());
    }

    [Fact]
    public void EveryTruncationOversizeStringAndTrailingByteIsRefused()
    {
        byte[] bytes = PlaceFixture();
        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] shorter = bytes[..length];
            Assert.Throws<PacketFormatException>(() => EconomyOrderRequest.Parse(shorter));
        }
        Assert.Throws<PacketFormatException>(() => EconomyOrderRequest.Parse([.. bytes, 0]));
        byte[] invalidFlag = [.. bytes];
        invalidFlag[^1] = 2;
        Assert.Throws<PacketFormatException>(() => EconomyOrderRequest.Parse(invalidFlag));
        byte[] hugeString = [.. bytes];
        BitConverter.GetBytes(257).CopyTo(hugeString, 3);
        Assert.Throws<PacketFormatException>(() => EconomyOrderRequest.Parse(hugeString));
    }

    [Fact]
    public void ResponseBodiesMatchSeparateNativePreviewAndPlaceParsers()
    {
        Assert.Equal(Convert.FromHexString("27020001000000010000006400000000000000"),
            Bytes(writer => new EconomyOrderResponse(1, 1, 100, IsPreview: true).WriteTo(writer)));
        Assert.Equal(Convert.FromHexString("2704000100000001000000030000006162636400000000000000"),
            Bytes(writer => new EconomyOrderResponse(1, 1, 100, OrderId: "abc").WriteTo(writer)));
    }

    [Fact]
    public void StoreBundleMatchesNativeSchemaAndPutsScrapPriceAtThePurchaseGate()
    {
        byte[] bytes = Bytes(writer => new EconomyStoreUpdate([new(244, "Scrapyard", 1, 100, 3806, 1)]).WriteTo(writer));
        Assert.Equal(259, bytes.Length);
        var reader = new PacketReader(bytes);
        Assert.Equal(0x27, reader.ReadByte());
        Assert.Equal(5, reader.ReadUInt16());
        Assert.Equal(1u, reader.ReadUInt32()); // variant
        Assert.Equal(1u, reader.ReadUInt32()); // store id
        Assert.Equal(1u, reader.ReadUInt32()); // native store +08
        reader.Skip(8);
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(244u, reader.ReadUInt32()); // collection key
        Assert.Equal(244u, reader.ReadUInt32()); // native bundle +220
        reader.Skip(12);
        Assert.Equal("Scrapyard", reader.ReadString());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.False(reader.ReadBool());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(1u, reader.ReadUInt32()); // +230 currency
        Assert.Equal(100u, reader.ReadUInt32()); // +234 checked by 141043770
        reader.Skip(8);
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(0UL, reader.ReadUInt64());
        Assert.Equal((ulong)long.MaxValue, reader.ReadUInt64());
        reader.Skip(5);
        Assert.Equal(1, reader.ReadInt32());
        reader.Skip(8);
        Assert.Equal(3806u, reader.ReadUInt32()); // preview RequestByBundle walks content +10
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(0, reader.ReadInt32());
        // Remaining native 14102e320 (44 bytes) and 14102b680 (69 bytes), no sale or optional tail.
        Assert.Equal(113, reader.Remaining);
        Assert.All(reader.ReadRest().ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void SkullCategoryGroupResolvesTheNativeRewardsPagePath()
    {
        // Native list path [13] is formatted as index 0 + '^' + category 13: "0^13".
        Assert.Equal(Convert.FromHexString("270600010000000D0000000D000000010000000D000000"),
            Bytes(writer => new EconomyStoreCategoryGroups([13]).WriteTo(writer)));
        Assert.Throws<ArgumentException>(() => Bytes(writer => new EconomyStoreCategoryGroups([0]).WriteTo(writer)));
        Assert.Throws<ArgumentException>(() => Bytes(writer => new EconomyStoreCategoryGroups([13, 13]).WriteTo(writer)));
    }

    [Fact]
    public void SkullCategoryTreeHasItsNativeIdentityIconAndTerminatingParent()
    {
        Assert.Equal(Convert.FromHexString(
            "270700010000000D0000000D0000003D3B0000040000003137363001000000300000000000000000000D00000000"),
            Bytes(writer => new EconomyStoreCategories([new(13, 15165, 1760, 13)]).WriteTo(writer)));
        Assert.Throws<ArgumentException>(() => Bytes(writer =>
            new EconomyStoreCategories([new(0, 15165, 1760, 13)]).WriteTo(writer)));
    }

    [Fact]
    public void SkullBundlePublishesTheNativeCategoryNameIconAndSingleItemPrice()
    {
        var bundle = new EconomyStoreBundle(53791, "White Suit Jacket", 5, 250, 3791, 1,
            CategoryId: 13, NameLocaleId: 15528, ImageSetId: 1764);
        var reader = new PacketReader(Bytes(writer => new EconomyStoreUpdate([bundle]).WriteTo(writer)));
        reader.Skip(35); // store header, including one bundle
        Assert.Equal(53791u, reader.ReadUInt32());
        Assert.Equal(53791u, reader.ReadUInt32());
        Assert.Equal(15528u, reader.ReadUInt32()); // localized Name, native +224
        Assert.Equal(0u, reader.ReadUInt32()); // localized Description, native +228
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal("1764", reader.ReadString()); // ImageSetId parsed from +20 string
        Assert.Equal("", reader.ReadString());
        Assert.False(reader.ReadBool());
        Assert.Equal("", reader.ReadString());
        Assert.Equal(5u, reader.ReadUInt32());
        Assert.Equal(250u, reader.ReadUInt32());
        reader.Skip(8);
        Assert.Equal(1u, reader.ReadUInt32());
        reader.Skip(21); // sale times, extra dword and flag
        Assert.Equal(1, reader.ReadInt32());
        reader.Skip(8);
        Assert.Equal(3791u, reader.ReadUInt32()); // owns the account recipe, not appearance 3785
        Assert.Equal("", reader.ReadString());
        Assert.Equal("", reader.ReadString());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(EconomyStoreUpdate.StoreId, reader.ReadUInt32()); // native +250
        Assert.Equal(13u, reader.ReadUInt32()); // native +254, category-path lookup
        Assert.Equal(105, reader.Remaining);
        Assert.All(reader.ReadRest().ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ArgumentException>(() => Bytes(writer =>
            new EconomyStoreUpdate([bundle with { MaximumQuantity = 2 }]).WriteTo(writer)));
        Assert.Throws<ArgumentException>(() => Bytes(writer =>
            new EconomyStoreUpdate([bundle with { CategoryId = 0 }]).WriteTo(writer)));
        Assert.Throws<ArgumentException>(() => Bytes(writer =>
            new EconomyStoreUpdate([bundle with { NameLocaleId = 0 }]).WriteTo(writer)));
        Assert.Throws<ArgumentException>(() => Bytes(writer =>
            new EconomyStoreUpdate([bundle with { ImageSetId = 0 }]).WriteTo(writer)));
    }

    [Fact]
    public void CrownStoreBundlePublishesTenLimitAndRejectsEleven()
    {
        var bundle = new EconomyStoreBundle(177, "Locked", 4, 250, 401, 10);
        var reader = new PacketReader(Bytes(writer => new EconomyStoreUpdate([bundle]).WriteTo(writer)));
        reader.Skip(55); // store header, collection key, bundle id and three native dwords
        Assert.Equal("Locked", reader.ReadString());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.False(reader.ReadBool());
        Assert.Equal(string.Empty, reader.ReadString());
        reader.Skip(16); // currency, price, two reserved dwords
        Assert.Equal(10u, reader.ReadUInt32());
        Assert.Throws<ArgumentException>(() => Bytes(writer =>
            new EconomyStoreUpdate([bundle with { MaximumQuantity = 11 }]).WriteTo(writer)));
        Assert.Throws<ArgumentException>(() => Bytes(writer =>
            new EconomyStoreUpdate([bundle with { CurrencyId = 1, MaximumQuantity = 51 }]).WriteTo(writer)));
    }

    [Fact]
    public void WalletProjectionCarriesRealCrownsAndSkipsExternalBillingForm()
    {
        byte[] bytes = Bytes(writer => new EconomyWalletInfoResponse(2000, 500, 200).WriteTo(writer));
        var reader = new PacketReader(bytes.AsSpan(3));
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.False(reader.ReadBool());
        Assert.Equal(2000u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal("KH$", reader.ReadString());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.True(reader.ReadBool());
        Assert.Equal(2, reader.ReadInt32());
        foreach (var wallet in new[] { (Code: "KS$", Amount: 500u), (Code: "KF$", Amount: 200u) })
        {
            Assert.Equal(wallet.Code, reader.ReadString());
            Assert.False(reader.ReadBool());
            Assert.Equal(wallet.Amount, reader.ReadUInt32());
            Assert.Equal((uint)int.MaxValue, reader.ReadUInt32());
            Assert.Equal(wallet.Code, reader.ReadString());
            Assert.Equal(string.Empty, reader.ReadString());
            Assert.True(reader.ReadBool());
        }
        Assert.True(reader.AtEnd);
        Assert.Equal(Convert.FromHexString("273000D0070000030000004B4824"),
            Bytes(writer => new EconomyWalletBalanceUpdate(2000).WriteTo(writer)));
    }

    [Fact]
    public void GrinderRevealUsesTheAugustDwordTypeAndDedicatedRewardTableFlag()
    {
        byte[] bytes = Bytes(writer => new EconomyRewardReveal(1812, 10001, 101, 1, 7).WriteTo(writer));
        Assert.Equal(EconomyRewardReveal.Length, bytes.Length);
        var reader = new PacketReader(bytes);
        Assert.Equal(0x1c, reader.ReadByte()); Assert.Equal(1, reader.ReadByte());
        Assert.False(reader.ReadBool());
        Assert.Equal(0, reader.ReadInt32()); // currencies
        Assert.All(reader.ReadBytes(48).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(1, reader.ReadInt32()); // entries
        Assert.Equal(1u, reader.ReadUInt32()); // full-width discriminator
        Assert.False(reader.ReadBool());
        Assert.Equal(101u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(10001u, reader.ReadUInt32()); Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(1812u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadString()); Assert.Equal(7u, reader.ReadUInt32());
        Assert.False(reader.ReadBool()); Assert.Equal(0, reader.ReadInt32());
        Assert.True(reader.ReadBool()); Assert.True(reader.AtEnd);
        var catalog = Cranberry.Zone.Economy.EconomyCatalog.Default;
        Assert.All(catalog.ScrapyardRewards, reward =>
        {
            var skin = catalog.Skins[reward.AccountItemId];
            Assert.Equal(EconomyRewardReveal.Length, Bytes(writer => new EconomyRewardReveal(
                skin.AccountItemId, skin.NameLocaleId, skin.ImageSetId, reward.Count, skin.RarityId).WriteTo(writer)).Length);
        });
    }
}
