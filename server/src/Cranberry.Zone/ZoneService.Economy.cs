using System.Collections.Concurrent;
using System.Text.Json;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Economy;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private readonly AccountEconomyStore? _economy;
    private readonly ConcurrentDictionary<SoeConnection, GatewaySessionState> _accountSessions = new();

    private bool InitializeAccountEconomy(SoeConnection connection, GatewaySessionState state)
    {
        if (_economy is null)
        {
            _accountSessions[connection] = state;
            return true;
        }
        if (string.IsNullOrWhiteSpace(state.AccountId))
        {
            _log.Warn($"{connection} account economy requires server-resolved account admission");
            return false;
        }
        try
        {
            var seed = new AccountEconomySeed(_options.MenuTopBar.CurrencyRecords()
                .ToDictionary(row => row.CurrencyId, row => row.Amount));
            _economy.GetOrCreate(state.AccountId, seed);
            if (_options.ProvisionLocalAccounts)
            {
                var local = LocalAccountProfile.Apply(_economy, state.AccountId);
                if (!local.Succeeded)
                    throw new AccountEconomyStoreException(local.Error ?? "Local account provisioning failed.");
                if (!local.Replayed)
                    _log.Info($"{connection} provisioned local skins, Crowns and locked crate families");
            }
            else if (_options.ProvisionStarterAccounts && state.AccountId != LocalOwnerAccountId)
            {
                var starter = StarterAccountProfile.Apply(_economy, state.AccountId);
                if (!starter.Succeeded)
                    throw new AccountEconomyStoreException(starter.Error ?? "Starter package failed.");
                if (!starter.Replayed)
                    _log.Info($"{connection} provisioned starter Crowns, basic apparel and locked crate families");
                var skins = ClientSkinGrant.Apply(_economy, state.AccountId);
                if (!skins.Succeeded)
                    throw new AccountEconomyStoreException(skins.Error ?? "Client skin grant failed.");
                if (!skins.Replayed)
                    _log.Info($"{connection} provisioned missing Client skins with event exclusions");
            }
            Experience.Read(state.AccountId);
            RetryPendingBountyCompletions(state.AccountId);
            _bountyLedger!.RecoverAbandonedRun(state.AccountId);
            // Once per existing character, preserve the user's actual saved choices. A receipt
            // also records an empty migration, so later reconnects never regrant a scrapped skin.
            var migrated = _economy.Execute(state.AccountId, $"wardrobe-migration:{state.Guid:x16}",
                "wardrobe-migration", draft =>
                {
                    foreach (uint itemId in state.Wardrobe.Snapshot().Select(s => s.AccountItemId)
                        .Concat(state.VehicleSkins.Snapshot().Select(s => s.ItemId)).Distinct())
                    {
                        if (!draft.Owns(itemId) && EconomyCatalog.Default.TryGetSkin(itemId, out var skin))
                            draft.Grant(skin.AccountItemId, skin.RewardItemId, 1,
                                $"wardrobe-migration:{state.Guid:x16}", skin.CanScrap);
                    }
                    return null;
                });
            if (!migrated.Succeeded) throw new AccountEconomyStoreException(migrated.Error ?? "Migration failed.");
            ReconcileAccountSelections(state, migrated.Snapshot!);
            _accountSessions[connection] = state;
            _log.Info($"{connection} loaded saved account economy revision {migrated.Snapshot!.Revision}");
            return true;
        }
        catch (AccountEconomyStoreException ex)
        {
            _log.Warn($"{connection} account economy unavailable; admission refused: {ex.Message}");
            return false;
        }
    }

    private AccountEconomySnapshot ReadAccountEconomy(GatewaySessionState state) =>
        _economy!.GetOrCreate(state.AccountId);

    private IEnumerable<SetAccountCurrencyRecord> AccountCurrencyRecords(GatewaySessionState state)
    {
        if (_economy is null)
        {
            if (state.Currency.Count == 0)
                foreach (var row in _options.MenuTopBar.CurrencyRecords()) state.Currency[row.CurrencyId] = row.Amount;
            return new uint[] { 1, 4, 5, 6 }.Select(id => new SetAccountCurrencyRecord(id, state.Currency.GetValueOrDefault(id)));
        }
        var account = ReadAccountEconomy(state);
        return new uint[] { 1, 4, 5, 6 }.Select(id => new SetAccountCurrencyRecord(id, account.Balance(id)));
    }

    private void ReconcileAccountSelections(GatewaySessionState state, AccountEconomySnapshot account)
    {
        bool wardrobeChanged = false;
        foreach (var entry in state.Wardrobe.Snapshot())
        {
            if (account.Owns(entry.AccountItemId)) continue;
            state.Wardrobe.TryApply(new SkinItemSelectionRequest(
                SkinItemSelectionRequest.RequestUnsetSkinItem, 0, 0, 0, entry.CategoryPrototypeId, 0),
                out _, out _, out _);
            wardrobeChanged = true;
        }
        if (wardrobeChanged)
        {
            state.MenuApparelPreview = null;
            state.MenuWeaponPreview = null;
            state.MenuWeaponPreviewCategoryId = 0;
            _wardrobeStore.Save(state.Guid, state.Wardrobe);
        }
        bool vehiclesChanged = false;
        foreach (var choice in state.VehicleSkins.Snapshot())
        {
            if (account.Owns(choice.ItemId)) continue;
            state.VehicleSkins.Unset(choice.VehicleId, choice.ModPoint);
            vehiclesChanged = true;
        }
        if (vehiclesChanged) state.VehicleSkins.Save(_options.Skins.WardrobeStoreRoot, state.Guid, _log.Warn);
    }

    private void PublishAccountEconomy(string accountId, bool inventoryChanged)
    {
        if (_economy is null) return;
        var account = _economy.GetOrCreate(accountId);
        foreach (var pair in _accountSessions)
        {
            if (pair.Value.AccountId != accountId || pair.Key.State != ConnectionState.Open) continue;
            var state = pair.Value;
            SendEconomyWalletBalance(pair.Key, account.Balance(4), account.Balance(5), account.Balance(6));
            foreach (uint id in new uint[] { 1, 4, 5, 6 })
            {
                uint amount = account.Balance(id);
                state.Currency[id] = amount;
                SendTunnel(pair.Key, writer => new SetAccountCurrencyRecord(id, amount).WriteTo(writer));
            }
            if (!inventoryChanged) continue;
            if (state.CrateOpening is { } gallery)
            {
                // A catalogue/appearance burst can replace the temporary gallery rifle's native
                // hand binding. Keep it until End restores the normal hand, even on the results
                // screen. Other sessions for this account still receive their usual refresh.
                gallery.AccountInventoryRefreshPending = true;
                continue;
            }
            SendAccountInventory(pair.Key, state, account);
            if (state.Match == MatchStep.Menu)
                SendCharacterAppearance(pair.Key, state, "account ownership changed");
        }
    }

    private void SendAccountInventory(SoeConnection connection, GatewaySessionState state,
        AccountEconomySnapshot account)
    {
        ReconcileAccountSelections(state, account);
        SendSkinCatalogue(connection, state);
        SendVehicleSkinManager(connection, state);
    }

    private void HandleGrinderExchange(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        GrinderExchangeRequest request = GrinderExchangeRequest.Parse(payload);
        AccountEconomyResult? result = null;
        if (_economy is not null && state.Match == MatchStep.Menu)
            result = new AccountEconomyOperations(_economy).ScrapBatch(state.AccountId,
                $"items:{state.EconomyLinkId}:{++state.EconomyRequestSequence}", request.Items);
        uint scrap = 0;
        if (result?.Succeeded == true)
        {
            var award = JsonSerializer.Deserialize<EconomyAwardReceipt>(result.Receipt!.ResultJson!)!;
            scrap = checked((uint)award.CurrencyChanges[1]);
            PublishAccountEconomy(state.AccountId, inventoryChanged: true);
            SendTunnel(connection, writer => new ReportRewardScrap(scrap).WriteTo(writer));
        }
        else
            _log.Warn($"{connection} refused Grinder exchange: {result?.Error ?? "Unavailable action or phase."}");
        SendTunnel(connection, writer => new GrinderExchangeResponse(scrap).WriteTo(writer));
    }

    private void HandleAccountEconomyItem(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        if (payload[1] == RequestPreviewAccountCrateRewards.SubOpcode)
        {
            var preview = RequestPreviewAccountCrateRewards.Parse(payload);
            bool available = _economy is not null && state.Match == MatchStep.Menu
                && EconomyCatalog.Default.TryGetCrate(preview.CrateItemId, out var offered) && offered.Rewards.Count > 0;
            SendTunnel(connection, writer => preview.WriteResponse(writer, available ? 0u : 1u));
            if (available)
            {
                var crate = EconomyCatalog.Default.Crates[preview.CrateItemId];
                SendTunnel(connection, writer => new ReportRewardCrateContents([], EconomyCatalog.PreviewRewards(crate)).WriteTo(writer));
            }
            return;
        }

        // The SOE reliable stream delivers each accepted message once. The request codecs have no
        // arbitrary transaction token. Use a server-issued link/sequence receipt; StackAtClick,
        // where supplied by the native UI, adds optimistic stale-stack protection for scrapping.
        string operationId = $"items:{state.EconomyLinkId}:{++state.EconomyRequestSequence}";
        bool canAct = _economy is not null && state.Match == MatchStep.Menu;
        AccountEconomyResult? result = null;
        if (payload[1] == RequestUseAccountItem.SubOpcode)
        {
            var request = RequestUseAccountItem.Parse(payload);
            if (canAct && request.ItemUseOptionId == 98)
                result = new AccountEconomyOperations(_economy!).Scrap(state.AccountId, operationId,
                    request.AccountItemId, request.UInt32Parameters.ContainsKey(1) ? request.StackAtClick : null);
            SendTunnel(connection, writer => request.WriteResponse(writer, result?.Succeeded == true ? 0u : 1u));
        }
        else if (payload[1] == RequestOpenAccountCrate.SubOpcode)
        {
            var request = RequestOpenAccountCrate.Parse(payload);
            if (canAct)
                result = new AccountEconomyOperations(_economy!).OpenCrates(state.AccountId, operationId, request.Crates);
            SendTunnel(connection, writer => request.WriteResponse(writer, result?.Succeeded == true ? 0u : 1u));
        }
        if (result?.Succeeded != true)
        {
            _log.Warn($"{connection} refused account economy action: {result?.Error ?? "Unavailable action or phase."}");
            return;
        }
        var award = JsonSerializer.Deserialize<EconomyAwardReceipt>(result.Receipt!.ResultJson!)!;
        PublishAccountEconomy(state.AccountId, inventoryChanged: true);
        if (award.CurrencyChanges.TryGetValue(1, out long scrap) && scrap > 0)
            SendTunnel(connection, writer => new ReportRewardScrap((uint)scrap).WriteTo(writer));
        if (award.Granted.Count > 0)
        {
            var winners = award.Granted.GroupBy(item => item.AccountItemId)
                .Select(group => new AccountRewardRow(group.Key, checked((uint)group.Sum(item => (long)item.Count)))).ToArray();
            SendTunnel(connection, writer => new ReportRewardCrateContents(winners, []).WriteTo(writer));
        }
    }
}
