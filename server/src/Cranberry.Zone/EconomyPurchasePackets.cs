using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cranberry.Protocol;

namespace Cranberry.Zone;

public sealed record EconomyOrderLine(uint BundleId, uint StoreId, uint Quantity,
    string Reference, string PaymentSource)
{
    // Captured August PlaceOrder, wire-20260905-215814:765. The final string is native
    // out-of-band rental/creator metadata, not a real-money payment credential.
    public const string NativeDefaultOutOfBandData = "<OobData rentalTermId=\"0\" playerStudioId=\"0\"/>";
}

/// <summary>
/// August native order writers FUN_14102a0d0/14102a270, called by FUN_141029e70 (place)
/// and FUN_141027a10 (preview). CreateNewOrder and CancelOrder are local operations.
/// TrackId is a client-process counter, not an account-global transaction identifier.
/// Unsettled string meanings retain positional names and never authorize an account.
/// </summary>
public sealed record EconomyOrderRequest(ushort SubOpcode, string BuyerReference, string CharacterReference,
    uint TrackId, string Context, string Coupon, string CurrencyCode, string GiftMessage,
    IReadOnlyList<EconomyOrderLine> Lines, byte PlaceFlag = 0, string PreviewCode = "")
{
    public const ushort Preview = 1;
    public const ushort Place = 3;
    public const int MaximumLines = 50;
    public const int MaximumPacketLength = 16_384;

    public static EconomyOrderRequest Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPacketLength) throw new PacketFormatException("Order packet exceeds its limit.");
        var reader = new PacketReader(payload);
        if (reader.ReadByte() != ZoneOpcodes.InGamePurchaseBase) throw new PacketFormatException("Not an order packet.");
        ushort sub = reader.ReadUInt16();
        if (sub is not Preview and not Place) throw new PacketFormatException("Unsupported order request.");
        string buyer = Text(ref reader);
        string character = Text(ref reader);
        uint track = reader.ReadUInt32();
        string context = Text(ref reader);
        string coupon = Text(ref reader);
        string currency = Text(ref reader);
        string gift = Text(ref reader);
        int count = reader.ReadInt32();
        if (count < 1 || count > MaximumLines || count > reader.Remaining / 20)
            throw new PacketFormatException("Invalid order line count.");
        var lines = new List<EconomyOrderLine>(count);
        for (int i = 0; i < count; i++)
            lines.Add(new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), Text(ref reader), Text(ref reader)));
        byte flag = 0;
        string previewCode = string.Empty;
        if (sub == Place)
        {
            flag = reader.ReadByte();
            if (flag > 1) throw new PacketFormatException("Invalid order placement flag.");
        }
        else previewCode = Text(ref reader);
        if (!reader.AtEnd) throw new PacketFormatException("Unexpected order tail.");
        return new(sub, buyer, character, track, context, coupon, currency, gift, lines.AsReadOnly(), flag, previewCode);
    }

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.InGamePurchaseBase);
        writer.WriteUInt16(SubOpcode);
        writer.WriteString(BuyerReference);
        writer.WriteString(CharacterReference);
        writer.WriteUInt32(TrackId);
        writer.WriteString(Context);
        writer.WriteString(Coupon);
        writer.WriteString(CurrencyCode);
        writer.WriteString(GiftMessage);
        writer.WriteInt32(Lines.Count);
        foreach (var line in Lines)
        {
            writer.WriteUInt32(line.BundleId);
            writer.WriteUInt32(line.StoreId);
            writer.WriteUInt32(line.Quantity);
            writer.WriteString(line.Reference);
            writer.WriteString(line.PaymentSource);
        }
        if (SubOpcode == Place) writer.WriteByte(PlaceFlag);
        else writer.WriteString(PreviewCode);
    }

    /// <summary>A stable signature prevents reusing a live track ID for different order contents.</summary>
    public string Fingerprint()
    {
        using var writer = new PacketWriter();
        (this with { SubOpcode = Place, PlaceFlag = 0, PreviewCode = string.Empty }).WriteTo(writer);
        return Convert.ToHexString(SHA256.HashData(writer.Written)).ToLowerInvariant();
    }

    private static string Text(ref PacketReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 256) throw new PacketFormatException("Invalid order text length.");
        try { return new UTF8Encoding(false, true).GetString(reader.ReadBytes(count)); }
        catch (DecoderFallbackException) { throw new PacketFormatException("Invalid order text encoding."); }
    }
}

/// <summary>FUN_14102d5b0 (preview) and FUN_14102d420 (place); success is result 1.</summary>
public sealed record EconomyOrderResponse(uint TrackId, uint Result, uint Total = 0, uint Tax = 0,
    string OrderId = "", bool IsPreview = false)
{
    public const uint Success = 1;
    public const uint Failure = 2;
    public const uint InsufficientFunds = 7;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.InGamePurchaseBase);
        writer.WriteUInt16(IsPreview ? (ushort)2 : (ushort)4);
        writer.WriteUInt32(TrackId);
        writer.WriteUInt32(Result);
        if (!IsPreview) writer.WriteString(OrderId);
        writer.WriteUInt32(Total);
        writer.WriteUInt32(Tax);
    }
}

public sealed record EconomyStoreBundle(uint BundleId, string Name, uint CurrencyId, uint Price,
    uint ContentItemId, uint MaximumQuantity = 50, uint CategoryId = 0,
    uint NameLocaleId = 0, uint ImageSetId = 0, uint DescriptionLocaleId = 0)
{
    // The menu offers Unlock One and Unlock 10; larger stock is unlocked in batches.
    public const uint MaximumCrateQuantity = 10;
}

/// <summary>
/// August 27/06: 1410504c0 -> 141030450 -> 141031640. Each top-level category has
/// a keyed group containing one category ID. StoreBundles.CategoryId (14101ad70)
/// resolves bundle +254 through this map and formats the path as "0^categoryId".
/// </summary>
public sealed record EconomyStoreCategoryGroups(IReadOnlyList<uint> CategoryIds)
{
    public void WriteTo(PacketWriter writer)
    {
        if (CategoryIds.Count is < 1 or > 256 || CategoryIds.Any(id => id == 0)
            || CategoryIds.Distinct().Count() != CategoryIds.Count)
            throw new ArgumentException("Invalid store category groups.");
        writer.WriteByte(ZoneOpcodes.InGamePurchaseBase);
        writer.WriteUInt16(6);
        writer.WriteInt32(CategoryIds.Count);
        foreach (uint categoryId in CategoryIds)
        {
            writer.WriteUInt32(categoryId); // group map key
            writer.WriteUInt32(categoryId); // group +08
            writer.WriteInt32(1); // categories in this path
            writer.WriteUInt32(categoryId);
        }
    }
}

public sealed record EconomyStoreCategory(uint CategoryId, uint NameLocaleId, uint ImageSetId, uint DisplayOrder);

/// <summary>
/// August 27/07 category tree: 141050370 -> 14102b0f0 -> 141031770 -> 14102bba0.
/// 14100d660 exposes the category ID, locale name, image and tint. These are root
/// categories with an absent parent 0; 1415d5e80 recomputes product counts.
/// </summary>
public sealed record EconomyStoreCategories(IReadOnlyList<EconomyStoreCategory> Categories)
{
    public void WriteTo(PacketWriter writer)
    {
        if (Categories.Count is < 1 or > 256
            || Categories.Any(c => c.CategoryId == 0 || c.NameLocaleId == 0 || c.ImageSetId == 0)
            || Categories.Select(c => c.CategoryId).Distinct().Count() != Categories.Count)
            throw new ArgumentException("Invalid store categories.");
        writer.WriteByte(ZoneOpcodes.InGamePurchaseBase);
        writer.WriteUInt16(7);
        writer.WriteInt32(Categories.Count);
        foreach (var category in Categories)
        {
            writer.WriteUInt32(category.CategoryId); // map key
            writer.WriteUInt32(category.CategoryId); // +08
            writer.WriteUInt32(category.NameLocaleId); // +0c
            writer.WriteString(category.ImageSetId.ToString(CultureInfo.InvariantCulture)); // +18
            writer.WriteString("0"); // +30 tint
            writer.WriteBool(false); // +48, native default
            writer.WriteUInt32(0); // +1b0 parent (root sentinel is not a category row)
            writer.WriteUInt32(0); // +1b4 product count, recomputed after StoreUpdate
            writer.WriteUInt32(category.DisplayOrder); // +1b8
        }
        writer.WriteBool(false); // +46d1: not sorted; 141022e80 sorts before exposing rows
    }
}

/// <summary>
/// 27/05 store update, variant 1. August parser chain: 14102da10 -> 14102b240 -> 141031d00
/// -> 14102b680 -> 14102e320 -> 14102c050. These are native schemas, not portable 1087 bodies.
/// Unknown fields are zero, content is one client item, sale ends at Int64.MaxValue.
/// Bundle metadata is required by native CanPurchaseStoreBundle before any request is sent.
/// </summary>
public sealed record EconomyStoreUpdate(IReadOnlyList<EconomyStoreBundle> Bundles)
{
    public const uint StoreId = 1;
    public void WriteTo(PacketWriter writer)
    {
        if (Bundles.Count == 0 || Bundles.Count > 256 || Bundles.Select(b => b.BundleId).Distinct().Count() != Bundles.Count)
            throw new ArgumentException("Invalid economy store bundle list.");
        writer.WriteByte(ZoneOpcodes.InGamePurchaseBase);
        writer.WriteUInt16(5);
        writer.WriteUInt32(1); // store update variant
        writer.WriteUInt32(StoreId);
        writer.WriteUInt32(StoreId); // store +08
        writer.WriteUInt32(0); // +0c
        writer.WriteUInt32(0); // +10
        writer.WriteString(string.Empty); // +20
        writer.WriteString(string.Empty); // +38
        writer.WriteInt32(Bundles.Count);
        foreach (EconomyStoreBundle bundle in Bundles)
        {
            if (bundle.BundleId == 0 || bundle.ContentItemId == 0 || bundle.CurrencyId is not 1 and not 4 and not 5
                || bundle.Price == 0 || bundle.Price > int.MaxValue || bundle.MaximumQuantity == 0
                || (bundle.CurrencyId == 5 && (bundle.CategoryId == 0 || bundle.NameLocaleId == 0 || bundle.ImageSetId == 0))
                || bundle.MaximumQuantity > (bundle.CurrencyId switch
                    { 4 => EconomyStoreBundle.MaximumCrateQuantity, 5 => 1u, _ => 50u }))
                throw new ArgumentException("Invalid economy bundle.");
            writer.WriteUInt32(bundle.BundleId); // collection key; 141031d00
            writer.WriteUInt32(bundle.BundleId); // bundle +220; 14102c050
            Words(writer, bundle.NameLocaleId, bundle.DescriptionLocaleId, 0); // +224/+228/+22c
            // 14101ad70 resolves +224/+228 through locale data and parses +20 as ImageSetId.
            writer.WriteString(bundle.ImageSetId == 0 ? bundle.Name
                : bundle.ImageSetId.ToString(CultureInfo.InvariantCulture)); // +20
            writer.WriteString(string.Empty); // +38
            writer.WriteBool(false); // +249
            writer.WriteString(string.Empty); // +50
            Words(writer, bundle.CurrencyId, bundle.Price, 0, 0, bundle.MaximumQuantity); // +230..+240
            writer.WriteUInt64(0); // sale start +08
            writer.WriteUInt64(long.MaxValue); // sale end +10: never expired
            writer.WriteUInt32(0); // +244
            writer.WriteBool(false); // +248
            writer.WriteInt32(1); // content list 141032160/14102bd70
            Words(writer, 0, 1, bundle.ContentItemId); // content +08/+0c/+10
            writer.WriteString(string.Empty); // content +18
            writer.WriteString(string.Empty); // content +30
            writer.WriteInt32(0); // second list 141032550

            Words(writer, bundle.CategoryId == 0 ? 0u : StoreId, bundle.CategoryId); // +250 store / +254 category
            writer.WriteBool(false); // +27b
            Words(writer, 0, 0, 0, 0, 0, 0, 0, 0); // +258..+274
            writer.WriteBool(false); // +278
            writer.WriteBool(false); // +279
            writer.WriteBool(false); // +27a: not locked by a prerequisite

            Words(writer, 0, 0, 0, 0, 0, 0); // 14102b680: +280..+294
            writer.WriteString(string.Empty); // +298
            Words(writer, 0, 0, 0, 0, 0); // +2b0/+320/+334/+338/+33c
            writer.WriteUInt64(0); // +2b8
            writer.WriteString(string.Empty); // +2c0
            Words(writer, 0, 0); // +340/+344
            writer.WriteBool(false); // +348; optional trailing 12 bytes absent
        }
    }

    private static void Words(PacketWriter writer, params uint[] values)
    {
        foreach (uint value in values) writer.WriteUInt32(value);
    }
}

/// <summary>FUN_14102ce70: Marketplace enabled and its second native byte.</summary>
public sealed record EconomyMarketplaceState(bool Enabled = true)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.InGamePurchaseBase);
        writer.WriteUInt16(0x19);
        writer.WriteBool(Enabled);
        writer.WriteByte(0);
    }
}

/// <summary>FUN_141034730/141052d50; SKU2 Crowns wallet uses KH$.</summary>
public sealed record EconomyWalletBalanceUpdate(uint Balance, string CurrencyCode = "KH$")
{
    public void WriteTo(PacketWriter writer)
    {
        if (Balance > int.MaxValue || CurrencyCode is not "KH$" and not "KS$" and not "KF$")
            throw new ArgumentOutOfRangeException(nameof(Balance));
        writer.WriteByte(ZoneOpcodes.InGamePurchaseBase);
        writer.WriteUInt16(0x30);
        writer.WriteUInt32(Balance);
        writer.WriteString(CurrencyCode);
    }
}

/// <summary>
/// The existing August WalletInfoResponse schema with its local-wallet ready flag set.
/// FUN_141052eb0 writes flagB to +133020; FUN_141059ac0 (IsWalletZipcodeOnFile) reads it.
/// Local Crown purchases require no billing-address step or external payment service.
/// </summary>
public sealed record EconomyWalletInfoResponse(uint Crowns, uint Skulls = 0, uint Credits = 0)
{
    public void WriteTo(PacketWriter writer)
    {
        if (Crowns > int.MaxValue || Skulls > int.MaxValue || Credits > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Crowns));
        writer.WriteByte(ZoneOpcodes.InGamePurchaseBase);
        writer.WriteUInt16(0x0b);
        writer.WriteUInt32(1);
        writer.WriteBool(false);
        writer.WriteUInt32(Crowns);
        writer.WriteUInt32(0);
        writer.WriteString("KH$");
        writer.WriteString(string.Empty);
        writer.WriteBool(true);
        // 1410313c0 reads a keyed map of the same wallet body (14102eaa0).
        // 14104aae0 obtains Player.Currency ids5/6 exclusively from KS$/KF$ here.
        writer.WriteInt32(2);
        ExtraWallet(writer, "KS$", Skulls);
        ExtraWallet(writer, "KF$", Credits);
    }

    private static void ExtraWallet(PacketWriter writer, string code, uint balance)
    {
        writer.WriteString(code); // map key
        writer.WriteBool(false);
        writer.WriteUInt32(balance);
        writer.WriteUInt32(int.MaxValue);
        writer.WriteString(code); // wallet currency code
        writer.WriteString(string.Empty);
        writer.WriteBool(true);
    }
}

/// <summary>
/// August Rewards.AddRewardItem 1c01: 140ffd930 -> 140a45090 -> type-1 row
/// 1421dce70/1421dc9b0. The trailing true calls 14145b220 and replaces
/// BaseClient.Rewards.Entries, which GrinderWindow reads before its animation.
/// Its "Item Text Color" column is consumed as rarity 0/5/6/7/8, not an RGB tint.
/// This is a presentation packet; the durable account item was already granted.
/// </summary>
public sealed record EconomyRewardReveal(uint AccountItemId, uint NameLocaleId, uint ImageSetId,
    uint Quantity, uint RarityId)
{
    public const int Length = 102;

    public void WriteTo(PacketWriter writer)
    {
        if (AccountItemId == 0 || NameLocaleId == 0 || ImageSetId == 0 || Quantity is 0 or > int.MaxValue)
            throw new ArgumentException("Missing native reward presentation metadata.");
        writer.WriteByte(0x1c); writer.WriteByte(1);
        writer.WriteBool(false); // no extended item GUID/tint fields
        writer.WriteInt32(0); // currency-map entries: balances use the authoritative wallet packets
        for (int index = 0; index < 6; index++) writer.WriteUInt32(0);
        writer.WriteUInt64(0); writer.WriteUInt64(0);
        writer.WriteUInt32(0); writer.WriteUInt32(0);
        writer.WriteInt32(1); // reward entries
        writer.WriteUInt32(1); // native type discriminator is a dword
        writer.WriteBool(false); // hidden
        writer.WriteUInt32(ImageSetId);
        writer.WriteUInt32(0); // tint
        writer.WriteUInt32(NameLocaleId);
        writer.WriteUInt32(Quantity);
        writer.WriteUInt32(AccountItemId);
        writer.WriteUInt32(0); // parameter 2
        writer.WriteString(string.Empty);
        writer.WriteUInt32(RarityId);
        writer.WriteBool(false); // members only
        writer.WriteInt32(0); // trailing dword list
        writer.WriteBool(true); // replace BaseClient.Rewards.Entries
    }
}
