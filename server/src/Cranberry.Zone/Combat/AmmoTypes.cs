using System.Collections.Frozen;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Combat;

/// <summary>
/// <b>Which round a gun eats</b>, and the one place the answer comes from.
///
/// <para>
/// <b>Provenance: the client's own files.</b> The August extraction ships no
/// <c>WeaponDefinitions</c> sheet and no <c>FireModes</c> table - the column
/// <c>FIRE_MODE.AMMO_ITEM_ID</c> that the owner's server reads simply is not in the build's data
/// (<c>out\data_aug\derived\weapons.json</c> <c>serverSideGaps["fire-mode-table"]</c>: <i>"No
/// FireModes asset exists in the 50,502-entry pack index under any extension"</i>). What the client
/// <em>does</em> ship is the pairing in plain words: every firearm's own locale description ends
/// <c>"[Ammo Type: X]"</c>, and <c>X</c> is the display name of a <c>ClientItemDefinitions</c> row.
/// The pipeline's own reader resolves that pairing into
/// <c>weapons[].ammo.ammoItemId</c> (<c>fieldProvenance</c>: <i>"that text matched to a
/// ClientItemDefinitions item name"</i>), and the seventeen rows below are that mapping, verbatim,
/// keyed on <c>WEAPON_ID</c>.
/// </para>
///
/// <para>
/// <b>It agrees with the owner's table row for row</b> where the two overlap (S6 §4.4: AR-15 1429,
/// AK-47 2325, 12GA 1511, .308 1469, .44 1719, R380 1992, M9 1998, M1911 1428) - which is the
/// strongest evidence either table is right, because neither was derived from the other.
/// </para>
///
/// <para>
/// <b>The three rows that are NOT from that text</b> are the bows: their descriptions name no ammo
/// type, so the mapping comes from Cranberry's own loot design
/// (<c>Data/Loot/z2-loot-tables.json</c> <c>clusters</c>, weapon item 1986 "Recurve Bow" → item 112
/// "Wooden Arrow") and from item 112's own description, <i>"These arrows can be used with a
/// bow."</i> They are marked in the table and are a design choice, not an extraction.
/// </para>
/// </summary>
public static class AmmoTypes
{
    /// <summary>
    /// <c>ClientItemDefinitions</c> row 1511, "12 Gauge Buckshot Shell" - the only round in the
    /// build that is loaded one at a time, and therefore the test for the shell loop.
    /// </summary>
    public const uint ShotgunShellItemDefinitionId = 1511;

    /// <summary>
    /// <c>ClientItemDefinitions</c> row 112, "Wooden Arrow" - <i>"These arrows can be used with a
    /// bow."</i>
    /// </summary>
    public const uint WoodenArrowItemDefinitionId = 112;

    /// <summary>
    /// <c>ClientItemDefinitions</c> row 2325, "7.62x39 Round" - the AK-47 family's round, and
    /// therefore the key of the retail automatic set (D331, docs/121 §3): the AK-47 is the one gun
    /// whose own description (locale 11959) calls it an <i>"automatic weapon"</i>.
    /// </summary>
    public const uint Ak47RoundItemDefinitionId = 2325;

    /// <summary>
    /// <c>WEAPON_ID</c> → the <c>ClientItemDefinitions</c> row of the round it fires. A weapon with
    /// no row here has no ammunition item in this build and can never be reloaded from the bag.
    /// </summary>
    public static FrozenDictionary<uint, uint> ByWeaponDefinitionId { get; } =
        new Dictionary<uint, uint>
        {
            // EXTRACTED - "[Ammo Type: X]" on the item's own locale description.
            [2] = 1428,       // M1911A1          -> .45 Round
            [6] = 1429,       // AR-15            -> .223 Round
            [1337] = 1428,    // M1911A1 (row 2)  -> .45 Round
            [1373] = 1469,    // .308 Hunting Rifle -> .308 Round
            [1374] = 1511,    // 12GA Pump Shotgun  -> 12 Gauge Buckshot Shell
            [1388] = 1719,    // .44 Magnum       -> .44 Round
            [1400] = 1992,    // R380             -> .380 Round
            [1401] = 1998,    // M9               -> 9mm Round
            [1405] = 2325,    // AK-47            -> 7.62x39 Round
            [1424] = 2325,    // Modified AK-47   -> 7.62x39 Round
            [1432] = 2325,    // test gfern AK47  -> 7.62x39 Round
            [1433] = 1511,    // 12GA Pump Shotgun (row 2) -> 12 Gauge Buckshot Shell
            [1434] = 1428,    // test gfern       -> .45 Round
            [1435] = 1429,    // M16              -> .223 Round
            [1455] = 2325,    // AK-47 (row 3)    -> 7.62x39 Round
            [1456] = 1429,    // AR-15 (row 2)    -> .223 Round
            [1457] = 1429,    // AR-15 (row 3)    -> .223 Round

            // DESIGN - the bows name no ammo type; Cranberry's own cluster table pairs them with
            // the Wooden Arrow, whose description says it is what a bow takes.
            [113] = WoodenArrowItemDefinitionId,    // Makeshift Bow
            [1387] = WoodenArrowItemDefinitionId,   // Wood Bow
            [1399] = WoodenArrowItemDefinitionId,   // Recurve Bow
            [1414] = WoodenArrowItemDefinitionId,   // Crossbow
        }.ToFrozenDictionary();

    /// <summary>
    /// The ammunition item an <b>item definition</b> fires, resolved through the August client's own
    /// <c>ClientItemDatasheetData.WEAPON_ID</c>. 0 when the item is not a firearm this build carries
    /// a round for - the fists, a melee weapon, a throwable, or a gun with no pairing.
    /// </summary>
    public static uint AmmoItemFor(uint itemDefinitionId) =>
        WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact fact)
        && ByWeaponDefinitionId.TryGetValue(fact.WeaponId, out uint ammo)
            ? ammo
            : 0u;

    /// <summary>
    /// True when this weapon is loaded <b>one round at a time</b> rather than a magazine at a time -
    /// the pump shotgun, and the owner's <c>ShellByShellReload</c> (S6 §4.2 "shell loop"). Tested on
    /// the round rather than on the gun, so both of the build's 12GA rows are covered without a
    /// second table.
    /// </summary>
    public static bool IsShellByShell(uint itemDefinitionId) =>
        AmmoItemFor(itemDefinitionId) == ShotgunShellItemDefinitionId;
}
