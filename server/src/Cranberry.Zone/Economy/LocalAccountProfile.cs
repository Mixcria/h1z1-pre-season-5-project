namespace Cranberry.Zone.Economy;

/// <summary>Local edition entitlements; durable receipts preserve purchases, spending and saved progress.</summary>
public static class LocalAccountProfile
{
    public const string OperationId = "local-skins:20260913-v1";

    public static AccountEconomyResult Apply(AccountEconomyStore store, string accountId)
    {
        var starter = StarterAccountProfile.Apply(store, accountId);
        if (!starter.Succeeded) return starter;
        return store.Execute(accountId, OperationId, "local-skin-grant", draft =>
        {
            foreach (var skin in EconomyCatalog.Default.Skins.Values.OrderBy(skin => skin.AccountItemId))
                if (!draft.Owns(skin.AccountItemId))
                    draft.Grant(skin.AccountItemId, skin.RewardItemId, 1, OperationId, skin.CanScrap);
            return null;
        });
    }
}
