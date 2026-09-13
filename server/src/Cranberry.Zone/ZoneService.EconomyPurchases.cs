using System.Text.Json;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Economy;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    /// <summary>Call before generic Items/InGamePurchase dispatch. Only recognized economy requests are consumed.</summary>
    private bool HandleEconomyPurchase(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        if (_economy is null) return false;
        if (payload.Length >= 2 && payload[0] == ZoneOpcodes.ItemsBase
            && payload[1] == RequestPreviewAccountCrateRewards.SubOpcode)
        {
            var request = RequestPreviewAccountCrateRewards.Parse(payload);
            if (request.CrateItemId != EconomyPurchaseService.ScrapyardContentItemId) return false;
            bool allowed = state.Match == MatchStep.Menu;
            SendTunnel(connection, writer => request.WriteResponse(writer, allowed ? 0u : 1u));
            if (allowed) SendScrapyardPreview(connection, state, new EconomyPurchaseService(_economy));
            return true;
        }
        if (payload.Length < 3 || payload[0] != ZoneOpcodes.InGamePurchaseBase) return false;
        ushort sub = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        if (sub == 0x0a)
        {
            if (payload.Length != 3) throw new PacketFormatException("WalletInfoRequest has no body.");
            var account = ReadAccountEconomy(state);
            SendTunnel(connection, writer => new EconomyWalletInfoResponse(account.Balance(4), account.Balance(5), account.Balance(6)).WriteTo(writer));
            return true;
        }
        if (sub is not EconomyOrderRequest.Preview and not EconomyOrderRequest.Place) return false;
        EconomyOrderRequest order = EconomyOrderRequest.Parse(payload);
        if (state.Match != MatchStep.Menu)
        {
            SendTunnel(connection, writer => new EconomyOrderResponse(order.TrackId, EconomyOrderResponse.Failure,
                IsPreview: sub == EconomyOrderRequest.Preview).WriteTo(writer));
            return true;
        }
        var service = new EconomyPurchaseService(_economy);
        if (sub == EconomyOrderRequest.Preview)
        {
            EconomyOrderQuote quote = service.Quote(state.AccountId, state.Guid, order);
            SendTunnel(connection, writer => new EconomyOrderResponse(order.TrackId, quote.Result, quote.Total,
                IsPreview: true).WriteTo(writer));
            return true;
        }
        AccountEconomyResult result = service.Place(state.AccountId, state.EconomyLinkId, state.Guid, order);
        if (!result.Succeeded)
        {
            SendTunnel(connection, writer => new EconomyOrderResponse(order.TrackId,
                result.Error == "Insufficient currency." ? EconomyOrderResponse.InsufficientFunds : EconomyOrderResponse.Failure).WriteTo(writer));
            _log.Warn($"{connection} economy order refused: {result.Error}");
            return true;
        }
        var receipt = JsonSerializer.Deserialize<EconomyPurchaseReceipt>(result.Receipt!.ResultJson!)!;
        PublishAccountEconomy(state.AccountId, inventoryChanged: true);
        // Populate the reward datasource before OnOrderResponse starts the Grinder animation.
        if (receipt.Lines.Any(line => line.BundleId == EconomyPurchaseService.ScrapyardBundleId))
        {
            var winning = receipt.Award.Granted.Select(item => new AccountRewardRow(item.AccountItemId, item.Count)).ToArray();
            SendTunnel(connection, writer => new ReportRewardCrateContents(winning, ScrapyardPossibleRewards()).WriteTo(writer));
            SendScrapyardReveal(connection, receipt);
        }
        SendTunnel(connection, writer => new EconomyOrderResponse(order.TrackId, EconomyOrderResponse.Success,
            receipt.Total, OrderId: receipt.OrderId).WriteTo(writer));
        _log.Info($"{connection} economy order {receipt.OrderId}: total {receipt.Total}, replay {result.Replayed}");
        return true;
    }

    /// <summary>Call once the menu client is ready, after ownership and balances are available.</summary>
    private void SendEconomyPurchaseCatalog(SoeConnection connection, GatewaySessionState state)
    {
        if (_economy is null || state.Match != MatchStep.Menu) return;
        var service = new EconomyPurchaseService(_economy);
        SendTunnel(connection, writer => new EconomyStoreCategoryGroups(service.Offers.Values
            .Select(offer => offer.Bundle.CategoryId).Where(id => id != 0).Distinct().Order().ToArray()).WriteTo(writer));
        SendTunnel(connection, writer => new EconomyStoreCategories([SkullStoreCatalog.Category]).WriteTo(writer));
        SendTunnel(connection, writer => new EconomyStoreUpdate(service.Offers.Values
            .OrderBy(offer => offer.Bundle.BundleId).Select(offer => offer.Bundle).ToArray()).WriteTo(writer));
        var account = ReadAccountEconomy(state);
        SendTunnel(connection, writer => new EconomyWalletInfoResponse(account.Balance(4), account.Balance(5), account.Balance(6)).WriteTo(writer));
        SendEconomyWalletBalance(connection, account.Balance(4), account.Balance(5), account.Balance(6));
        SendTunnel(connection, writer => new EconomyMarketplaceState().WriteTo(writer));
    }

    private void SendEconomyWalletBalance(SoeConnection connection, uint crowns, uint skulls, uint credits)
    {
        SendTunnel(connection, writer => new EconomyWalletBalanceUpdate(crowns).WriteTo(writer));
        SendTunnel(connection, writer => new EconomyWalletBalanceUpdate(skulls, "KS$").WriteTo(writer));
        SendTunnel(connection, writer => new EconomyWalletBalanceUpdate(credits, "KF$").WriteTo(writer));
    }

    private void SendScrapyardPreview(SoeConnection connection, GatewaySessionState state, EconomyPurchaseService service)
    {
        EconomyPurchaseReceipt? prior = service.LastResult(state.AccountId, state.Guid, EconomyPurchaseService.ScrapyardBundleId);
        AccountRewardRow[] winners = prior?.Award.Granted
            .Select(item => new AccountRewardRow(item.AccountItemId, item.Count)).ToArray() ?? [];
        SendTunnel(connection, writer => new ReportRewardCrateContents(winners, ScrapyardPossibleRewards()).WriteTo(writer));
        if (prior is not null) SendScrapyardReveal(connection, prior);
    }

    private void SendScrapyardReveal(SoeConnection connection, EconomyPurchaseReceipt receipt)
    {
        OwnedAccountItem reward = receipt.Award.Granted.Single();
        EconomySkin skin = EconomyCatalog.Default.Skins[reward.AccountItemId];
        SendTunnel(connection, writer => new EconomyRewardReveal(skin.AccountItemId, skin.NameLocaleId,
            skin.ImageSetId, reward.Count, skin.RarityId).WriteTo(writer));
    }

    private static AccountRewardRow[] ScrapyardPossibleRewards() => EconomyCatalog.Default.ScrapyardRewards
        .Select(reward => new AccountRewardRow(reward.AccountItemId, reward.Count)).ToArray();
}
