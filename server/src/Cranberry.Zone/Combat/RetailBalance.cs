using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Combat;

/// <summary>How a weapon behaves under the hit rule. Not a wire value.</summary>
public enum WeaponClass : byte
{
    /// <summary>No row; the caller uses <see cref="CombatOptions.UnmappedWeaponBodyUnits"/>.</summary>
    Unknown = 0,

    /// <summary>AR-15, AK-47. Headshot lethal bare, absorbed by a helmet.</summary>
    Rifle,

    /// <summary>The .308. Headshot lethal <b>through</b> a helmet.</summary>
    HuntingRifle,

    /// <summary>M1911, M9, R380, .44 Magnum. Same head rule as a rifle.</summary>
    Pistol,

    /// <summary>The 12GA pump. <see cref="RetailWeapon.BodyHp"/> is PER PELLET and a head pellet is
    /// treated as a body pellet.</summary>
    Shotgun,
}

/// <summary>One row of the owner's retail Pre-Season-3 research.</summary>
/// <param name="Name">For the log only.</param>
/// <param name="BodyHp">Body-shot damage in HIT POINTS on a 100 HP bar - per pellet for a shotgun.</param>
/// <param name="Class">Which head rule applies.</param>
/// <param name="Grade">The owner's own confidence word, carried so the log cannot launder a guess.</param>
public readonly record struct RetailWeapon(string Name, double BodyHp, WeaponClass Class, string Grade);

/// <summary>
/// <b>Weapon damage, and the only place it comes from.</b> The owner's own research into Daybreak's
/// retail Pre-Season-3 numbers, adopted under D53 and re-keyed onto the August client's own
/// <c>WEAPON_ID</c>.
/// <para>
/// <b>Why the server has to supply these at all.</b> The August extraction's own
/// <c>FireModeDisplayStats.txt</c> has 176 rows and only <b>2</b> carry a non-zero
/// <c>MAX_DAMAGE</c>, and the extraction ships <b>no</b> <c>WeaponDefinitions.txt</c>. So at 1148,
/// exactly as at 1087, weapon damage is server-authored and this table is the only sourced one that
/// exists (docs/81 §4a).
/// </para>
/// <para>
/// <b>The clean split this file is half of:</b> <em>structure</em> - clip size, refire, reload, fire
/// group - comes from the August client's own <c>ClientItemDatasheetData.txt</c> via
/// <see cref="AugustWeaponFacts"/>; <em>damage and armour</em> come from here. No number in
/// Cranberry's combat path has third-party provenance, which is one better than the owner's server,
/// where a reference balance table coexists with this one behind a switch.
/// </para>
/// <para>
/// <b>Three things deliberately did not cross</b> (docs/81 §4c): his 2016 skin ids 1448-1452, which
/// in the August <c>ClientItemDefinitions.txt</c> are a <c>CreateRecipe</c> and three <c>Generic</c>
/// rows - porting them would have attached rifle damage to a crafting recipe; his 18-name
/// <c>hitLocation</c> vocabulary, whose provenance is excluded and which changes no damage; and his
/// "unmapped weapon keeps the reference balance table" fallback, replaced by
/// <see cref="CombatOptions.UnmappedWeaponBodyUnits"/>.
/// </para>
/// </summary>
public static class RetailBalance
{
    /// <summary>
    /// Health units per hit point. <c>MatchSettings.StartingHealth</c> and
    /// <c>GasSettings.MaxHitpoints</c> are both 10,000 on a 100 HP bar, which is exactly the owner's
    /// own scale.
    /// </summary>
    public const int UnitsPerHp = 100;

    /// <summary>The full bar, in units - what a lethal headshot pays.</summary>
    public const int FullHealthUnits = 100 * UnitsPerHp;

    /// <summary>
    /// Limb multiplier. <b>1.0, and that is a decision rather than an omission</b>: the owner's own
    /// note is that inventing one "would be a number that looks sourced and is not". No retail
    /// source publishes per-limb multipliers for this build.
    /// </summary>
    public const double LimbMultiplier = 1.0;

    /// <summary>
    /// May 17 through early August uses the systematic 17-pellet branch. The pattern
    /// and group are supplied by <see cref="AugustShotgunPattern"/> after source overlays.
    /// </summary>
    public const int RetailShotgunPellets = AugustShotgunPattern.PelletCount;

    /// <summary>
    /// The table, keyed on <b>weapon-definition id</b> - <c>AugustWeaponFact.WeaponId</c>, which
    /// equals the item row's <c>PARAM1</c> on every August row checked. Cranberry's own starter
    /// weapon 2425 resolves through the August sheet to definition 6 and picks up the AR-15 row with
    /// no extra mapping at all.
    /// </summary>
    public static IReadOnlyDictionary<uint, RetailWeapon> Rows { get; } =
        new Dictionary<uint, RetailWeapon>
        {
            // AR-15. The August patch note is "22.5 (down from 25)", i.e. 25 is the pre-patch value
            // this build window carries.
            [6] = new("AR-15", 25, WeaponClass.Rifle, "PROVEN"),
            [10] = new("AR-15 (sheet row 2)", 25, WeaponClass.Rifle, "PROVEN (same weapon)"),

            [1405] = new("AK-47", 30, WeaponClass.Rifle, "PROVEN"),
            [1373] = new(".308 Hunting Rifle", 65, WeaponClass.HuntingRifle, "PROVEN"),
            [1388] = new(".44 Magnum", 30, WeaponClass.Pistol, "PROVEN"),
            [2] = new("M1911A1", 25, WeaponClass.Pistol, "PROVEN"),
            [1401] = new("M9", 18, WeaponClass.Pistol, "PROVEN"),

            // Presence on this build is proven; the damage is not published. Placed in the M9 class,
            // which is what every community guide of the period does.
            [1400] = new("R380", 18, WeaponClass.Pistol, "SECONDARY - M9 class, damage not published"),

            // BodyHp is PER PELLET here.
            [1374] = new("12GA Pump Shotgun", AugustShotgunPattern.PelletDamageHp, WeaponClass.Shotgun,
                "17 pellets sourced / damage reconstructed to preserve 120 HP full-hit budget"),
        };

    /// <summary>The row for a weapon-definition id, or null.</summary>
    public static RetailWeapon? RowFor(uint weaponDefinitionId) =>
        Rows.TryGetValue(weaponDefinitionId, out RetailWeapon row) ? row : null;

    /// <summary>
    /// The weapon-definition id for an item, through the August client's own datasheet. 0 when the
    /// sheet does not carry the item.
    /// </summary>
    public static uint WeaponDefinitionIdFor(uint itemDefinitionId) =>
        WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact fact) ? fact.WeaponId : 0u;

    /// <summary>The row for an ITEM, resolved through the August sheet.</summary>
    public static RetailWeapon? RowForItem(uint itemDefinitionId) =>
        RowFor(WeaponDefinitionIdFor(itemDefinitionId));

    /// <summary>
    /// Body damage in health units for one projectile of this weapon at this range, or
    /// <paramref name="unmappedUnits"/> when the weapon has no row.
    /// </summary>
    public static int BodyDamageUnits(uint weaponDefinitionId, double distance, int unmappedUnits)
    {
        if (RowFor(weaponDefinitionId) is not { } row)
        {
            return unmappedUnits;
        }

        // May's shotgun revision removed distance scaling; fewer pellets connect at range.
        // Every mapped weapon therefore uses its body/pellet amount at any accepted distance.
        return Units(row.BodyHp);
    }

    /// <summary>Hit points to health units, rounded.</summary>
    public static int Units(double hitPoints) => (int)Math.Round(hitPoints * UnitsPerHp);

    /// <summary>
    /// The rate-of-fire gate for an item: <c>max(floor, REFIRE_TIME_MS)</c>. One table lookup
    /// against the August client's own sheet, not a branch chain on definition id - and the sheet is
    /// <em>stricter</em> than the owner's transcribed overrides on both weapons where they differ
    /// (.308 1800 ms vs his 1300, 12GA 750 ms vs his 400), which is the right direction for an
    /// authoritative gate.
    /// </summary>
    public static int RefireGateMs(uint itemDefinitionId, int floorMs) =>
        WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact fact)
            ? Math.Max(floorMs, fact.RefireTimeMs)
            : floorMs;

    /// <summary>
    /// D337: the gate for the mode the client is actually in. Under the captured table the client
    /// runs the capture's clocks (AK-47 145 ms, M9 ADS 125) rather than the August sheet's (160,
    /// 150), so the gate reads the SHIPPED fire mode's <c>REFIRE_TIME_MS</c> - resolved as D159
    /// does, <c>fireGroupId * 2 + fireModeIndex</c> - and falls back to the sheet when the capture
    /// has nothing for that mode or <paramref name="shipped"/> is off.
    /// </summary>
    public static int RefireGateMs(uint itemDefinitionId, int floorMs, int fireModeIndex, bool shipped, bool liveProfile = false)
    {
        if (shipped && liveProfile && Z1LiveGunplay.TryClock(itemDefinitionId, fireModeIndex,
            WeaponListLayouts.FireModeRefireTime, out int liveRefire)) return Math.Max(floorMs, liveRefire);
        if (shipped
            && WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact fact)
            && fact.FireGroupId != 0
            && CapturedWeaponTable.OverridesFor(fact.FireGroupId * 2 + (uint)Math.Max(0, fireModeIndex))
                is { } words
            && words.TryGetValue(WeaponListLayouts.FireModeRefireTime, out uint refire))
        {
            return Math.Max(floorMs, (int)refire);
        }

        return RefireGateMs(itemDefinitionId, floorMs);
    }

    /// <summary>
    /// The reload time for an item, in milliseconds, from the August sheet's own
    /// <c>RELOAD_TIME_MS</c> [P] - the same word list 2's <c>FireMode.ReloadTime</c> carries, so
    /// the server's timer and the client's animation read one number. 0 when the sheet names none.
    /// </summary>
    public static int ReloadTimeMs(uint itemDefinitionId) =>
        WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact fact)
            ? Math.Max(0, fact.ReloadTimeMs)
            : 0;

    /// <summary>Read the reload clock from the same mode table sent to the client.</summary>
    public static int ReloadTimeMs(uint itemDefinitionId, int fireModeIndex, bool shipped, bool liveProfile = false)
    {
        if (shipped && liveProfile && Z1LiveGunplay.TryClock(itemDefinitionId, fireModeIndex,
            WeaponListLayouts.FireModeReloadTime, out int liveReload)) return liveReload;
        if (shipped
            && WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact fact)
            && fact.FireGroupId != 0
            && CapturedWeaponTable.OverridesFor(fact.FireGroupId * 2 + (uint)Math.Max(0, fireModeIndex))
                is { } words
            && words.TryGetValue(WeaponListLayouts.FireModeReloadTime, out uint reload)
            && reload <= int.MaxValue)
        {
            return (int)reload;
        }

        return ReloadTimeMs(itemDefinitionId);
    }

    /// <summary>
    /// The magazine size for an item, from the August sheet. 0 means the sheet names no magazine
    /// (melee, fists), and such a weapon is never refused for an empty one.
    /// </summary>
    public static int ClipSize(uint itemDefinitionId) =>
        WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact fact) ? fact.ClipSize : 0;

    /// <summary>A display name for the log; the item id when the table has no row.</summary>
    public static string NameFor(uint itemDefinitionId) =>
        RowForItem(itemDefinitionId) is { } row ? row.Name : $"item {itemDefinitionId}";
}
