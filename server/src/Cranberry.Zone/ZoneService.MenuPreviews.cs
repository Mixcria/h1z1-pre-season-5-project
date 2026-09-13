using Cranberry.Transport;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private const string WeaponCategoryPreviewPrefix = "kotkweaponpreview:";

    private void PreviewWeaponCategory(SoeConnection connection, GatewaySessionState state, string request)
    {
        // Category navigation is a preview only. It must never select a skin or grant an item,
        // and the same public UI binding cannot mutate an in-match character.
        if (state.Match != MatchStep.Menu || state.Inventory is not null
            || !state.MenuView.StartsWith("kotkappearanceweapons", StringComparison.Ordinal)
            || !uint.TryParse(request.AsSpan(WeaponCategoryPreviewPrefix.Length), out uint prototype)
            || !InventoryItemFacts.TryGet(prototype, out var item)
            || item.CodeFactory != ItemCodeFactory.Weapon
            || !TryResolveMenuWeaponMesh(prototype, state.Gender, out _, out _)) return;

        state.MenuWeaponPreviewCategoryId = prototype;
        state.MenuWeaponPreview = state.Wardrobe.Snapshot()
            .Where(skin => skin.CategoryPrototypeId == prototype)
            .Select(skin => (AugustSkinCatalogEntry?)skin).FirstOrDefault();
        state.MenuApparelPreview = null;
        SendCharacterAppearance(connection, state, "weapon category preview");
        SendTunnel(connection, writer => new WeaponStance(state.Guid, 1).WriteTo(writer));
    }

    private static bool TryResolveMenuWeaponMesh(uint prototype, uint gender, out string model, out uint shader)
    {
        model = "";
        shader = 0;
        if (!InventoryItemFacts.TryGet(prototype, out var item) || item.CodeFactory != ItemCodeFactory.Weapon)
            return false;
        if (AugustWornVisuals.TryResolveMesh(prototype, gender, item.ModelName, out model, out shader))
            return true;
        // Some base item rows omit their mesh. A category whose skins all use the same
        // authored model supplies an unambiguous base mesh; no cosmetic tint is selected.
        var models = AugustSkinCatalog.Weapons.Where(skin => skin.CategoryPrototypeId == prototype)
            .Select(skin => skin.ModelNameFor(gender)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (models.Length != 1 || !AugustAssetIndex.Ships(models[0])) return false;
        model = AugustAssetIndex.Canonical(models[0]);
        return true;
    }
}
