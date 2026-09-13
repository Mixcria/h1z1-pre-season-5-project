using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// The KOTK-2017 retail ground roster, written out here rather than read back from the generated
/// file, so these tests pin the shipped data instead of agreeing with it. Every id was verified
/// against the August <c>ClientItemDefinitions.txt</c> (docs/39 §3): all 72 exist, all sit in
/// <c>CONTENT_ID</c> 1 ("Live"), and every ground actor resolves in the August <c>Models.txt</c>.
/// </summary>
internal static class LootRoster
{
    internal static readonly uint[] Shirts = [2127, 2128, 2137, 2138, 2139, 2140, 2141, 2142, 2143, 2145];
    internal static readonly uint[] Trousers = [2173, 2174, 2175, 2176, 2177, 2178];
    internal static readonly uint[] Caps = [2096, 2100, 2102, 2104, 2105, 2106];
    internal static readonly uint[] Beanies = [2162, 2166];
    internal static readonly uint[] Sneakers = [2215, 2216, 2217, 2218];
    internal static readonly uint[] Boots = [2206, 2207, 2208, 2209];
    internal static readonly uint[] Helmets = [2168, 2169, 2170, 2171, 2172];
    internal static readonly uint[] CivilianBackpacks = [2112, 2113, 2114, 2115, 2116, 2117];
    internal static readonly uint[] Throwables = [14, 65, 2235, 2236, 2237];

    /// <summary>
    /// The eleven weapons, and nothing else — melee is the Machete and the Combat Knife. The
    /// Crossbow (2246) is D273's: the owner's own retail research names it live KOTK loot in this
    /// window and his Z1 roster removed it on an explicit UNPROVEN-BY-ABSENCE note.
    /// </summary>
    internal static readonly uint[] Weapons = [2, 10, 83, 84, 1374, 1718, 1986, 1991, 1997, 2229, 2246];

    /// <summary>The seven calibres <c>Ammo01</c> may draw. <c>.308</c> is airdrop-only and absent.</summary>
    internal static readonly uint[] Ammunition = [1428, 1429, 1511, 1719, 1992, 1998, 2325];

    /// <summary>Wooden Arrow. Reaches the floor only as the Recurve Bow's cluster pair.</summary>
    internal const uint WoodenArrow = 112;

    internal const uint DuctTape = 134;
    internal const uint WaistPack = 1803;
    internal const uint TanMilitaryBackpack = 2124;
    internal const uint LaminatedTacticalBodyArmor = 2271;
    internal const uint FieldBandage = 2423;
    internal const uint TacticalFirstAidKit = 2424;

    /// <summary>All 72 natural loot ids after the September 8 bandage removal.</summary>
    internal static readonly HashSet<uint> All =
    [
        .. Shirts, .. Trousers, .. Caps, .. Beanies, .. Sneakers, .. Boots, .. Helmets,
        .. CivilianBackpacks, .. Throwables, .. Weapons, .. Ammunition,
        WoodenArrow, DuctTape, WaistPack, TanMilitaryBackpack, LaminatedTacticalBodyArmor,
        TacticalFirstAidKit,
    ];

    /// <summary>
    /// The ids docs/39 §2.2 measured on the floor and removed, by name and by number, so a future
    /// edit has to delete this list to bring any of them back. Nine are the owner's own retail
    /// exclusions (.308 rifle and ammunition, crossbow, camo tactical helmet, satchel, framed and
    /// black-military backpacks); the rest are the Just Survive melee family and the M1911 row that
    /// carries no passive equip slot. <b>D273 removed the Crossbow from this list</b> and put it
    /// on <c>Weapons01</c>; the .308 rifle and its round stay, and are now reachable through the
    /// airdrop crate instead (D274).
    /// </summary>
    internal static readonly (uint Id, string Why)[] Excluded =
    [
        (3, "Hatchet — Just Survive melee, never KotK ground loot"),
        (58, "Wood Axe — Just Survive melee"),
        (82, "Crowbar — Just Survive melee"),
        (1373, ".308 Hunting Rifle (alternate row) — airdrop-only"),
        (1469, ".308 Round — airdrop-only"),
        (1702, "M1911A1 row with PASSIVE_EQUIP_SLOT_ID 0 — cannot auto-equip (docs/39 §7)"),
        (1721, "Aluminum Baseball Bat — Just Survive melee"),
        (1724, "Baseball Bat — Just Survive melee"),
        (1727, "Lead Pipe — Just Survive melee"),
        (1745, "Fire Axe — Just Survive melee"),
        (1899, ".308 Hunting Rifle — airdrop-only in this retail window"),
        (2111, "Framed Backpack — not in the retail roster"),
        (2118, "Black Military Backpack — the roster has the Tan one only"),
        (2125, "Satchel — not in the roster; the fanny-pack prop belongs to 1803 Waist Pack"),
        (2205, "Plated Body Armor — the roster has exactly one vest"),
        (2274, "Police Body Armor — the roster has exactly one vest"),
        (2290, "Camo Tactical Helmet — not in the target roster"),
    ];

    internal static LootTables Tables => Shared.Tables.Value;

    internal static Z2LootSpawns Spawns => Shared.Spawns.Value;

    /// <summary>
    /// The whole map's layout at seed 1, built once for the suite: <see cref="Z2LootLayout.Build"/>
    /// walks all 168,322 markers, and every test here would otherwise pay for that again.
    /// </summary>
    internal static Z2LootLayout Layout => Shared.Layout.Value;

    private static class Shared
    {
        internal static readonly Lazy<LootTables> Tables = new(LootTables.LoadDefault);
        internal static readonly Lazy<Z2LootSpawns> Spawns = new(Z2LootSpawns.LoadDefault);

        internal static readonly Lazy<Z2LootLayout> Layout =
            new(() => Z2LootLayout.Build(Spawns.Value, Tables.Value, matchSeed: 1));
    }
}
