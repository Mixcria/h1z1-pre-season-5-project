namespace Cranberry.Zone.DevConsole.Commands;

/// <summary>
/// The short roster names the console accepts for an item, and the definition id each resolves to.
/// <para>
/// <b>Every id here is one this server already puts on a wire.</b> The weapons, ammunition, medical
/// items, packs and armour are the rows of Cranberry's own ground-loot table
/// (<c>src/Cranberry.Zone/Data/Loot/z2-loot-tables.json</c>, whose every row carries its own
/// <c>why</c> naming the <c>ITEM_CLASS</c>, the passive slot and the <c>Models.txt</c> ground form);
/// the three rows that table does not carry - the AR-15's held row, the .308 and the hatchet - come
/// from the client's own locale sheet <c>out\data_aug\item-names-en_us.json</c> (docs/28) and are
/// the rows that carry a third-person <c>MODEL_NAME</c>, so a granted gun has a mesh to appear in.
/// </para>
/// <para>
/// <b>Why 2425 and not 10 for the AR-15.</b> Both rows are called "AR-15" and both carry
/// <c>ITEM_CLASS 25036</c>, but row 10 is one of the 105 model-less duplicate weapon rows
/// (docs/45 §4): wielding it attaches nothing and the player holds an invisible gun. 2425 carries
/// <c>Weapon_M16A4_3p.adr</c> and is the row <c>WeaponInHandPackets.cs:280-298</c> already pins as
/// "the AR-15" for the hand. Same rule chose 1899 over 1373 for the .308 and 3 over the model-less
/// hatchet rows.
/// </para>
/// <para>
/// This table is a convenience, never a gate: <c>/give 2604</c> and <c>/loot spawn 2423 3</c> take
/// a bare definition id, so an item with no short name here is still reachable
/// (<see cref="CommandLine.Item"/>).
/// </para>
/// </summary>
public static class ItemNames
{
    private static readonly (string Name, uint Id, string Label)[] Table =
    [
        // --- weapons (the /give roster of design §2.5, in menu order) ---------------------------
        ("ar15", 2425u, "AR-15"),
        ("ak47", 2229u, "AK-47"),
        ("308", 1899u, ".308 Hunting Rifle"),
        ("12ga", 1374u, "12GA Pump Shotgun"),
        ("44", 1718u, ".44 Magnum"),
        ("1911", 2u, "M1911A1"),
        ("m9", 1997u, "M9"),
        ("r380", 1991u, "R380"),
        ("machete", 83u, "Machete"),
        ("hatchet", 3u, "Hatchet"),
        ("knife", 84u, "Combat Knife"),
        ("bow", 1986u, "Recurve Bow"),

        // --- ammunition --------------------------------------------------------------------------
        ("223", 1429u, ".223 Round"),
        ("762", 2325u, "7.62x39 Round"),
        ("buckshot", 1511u, "12 Gauge Buckshot Shell"),
        ("45", 1428u, ".45 Round"),
        ("9mm", 1998u, "9mm Round"),
        ("380", 1992u, ".380 Round"),
        ("44ammo", 1719u, ".44 Round"),
        ("308ammo", 1469u, ".308 Round"),
        ("arrow", 112u, "Wooden Arrow"),

        // --- medical, packs, armour -------------------------------------------------------------
        ("bandage", 2423u, "Field Bandage"),
        ("medkit", 2424u, "Tactical First Aid Kit"),
        ("backpack", 2112u, "Black Backpack"),
        ("waistpack", 1803u, "Waist Pack"),
        ("helmet", 2168u, "Black Motorcycle Helmet"),
        ("armour", 2271u, "Laminated Tactical Body Armor"),
        ("armor", 2271u, "Laminated Tactical Body Armor"),

        // --- throwables and craft inputs ---------------------------------------------------------
        ("frag", 65u, "M67 Frag Grenade"),
        ("molotov", 14u, "Molotov Cocktail"),
        ("smoke", 2236u, "M83 Smoke Grenade"),
        ("gasnade", 2237u, "M47 Gas Grenade"),
        ("stun", 2235u, "M-84 Stun Grenade"),
        ("ducttape", 134u, "Duct Tape"),
    ];

    private static readonly Dictionary<string, uint> ByName =
        Table.ToDictionary(row => row.Name, row => row.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<uint, string> LabelById = BuildLabels();

    /// <summary>Every short name, in table order - what a usage line and the menu roster read.</summary>
    public static IReadOnlyList<string> Names => [.. Table.Select(row => row.Name)];

    /// <summary>The definition id a short name means, or null.</summary>
    public static int? Resolve(string? name) =>
        name is not null && ByName.TryGetValue(name.Trim(), out uint id) ? (int)id : null;

    /// <summary>
    /// The human label for a definition id: the roster label when there is one, otherwise
    /// <c>item &lt;id&gt;</c>. Never throws and never returns an empty string, because it goes
    /// straight into a console line.
    /// </summary>
    public static string Label(uint definitionId) =>
        LabelById.TryGetValue(definitionId, out string? label) ? label : $"item {definitionId}";

    /// <summary>
    /// The closest short name to something the caller typed, for the
    /// <c>- unknown item 'ar16' -- did you mean ar15?</c> line of design §2.3. Null when nothing is
    /// close enough to be worth suggesting.
    /// </summary>
    public static string? Nearest(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        string want = typed.Trim().ToLowerInvariant();
        string? best = null;
        int bestScore = int.MaxValue;
        foreach ((string name, _, _) in Table)
        {
            int score = Distance(want, name);
            if (score < bestScore)
            {
                bestScore = score;
                best = name;
            }
        }

        // Two edits is the point where a suggestion stops helping and starts misleading.
        return bestScore <= 2 ? best : null;
    }

    private static Dictionary<uint, string> BuildLabels()
    {
        Dictionary<uint, string> labels = [];
        foreach ((_, uint id, string label) in Table)
        {
            labels[id] = label;
        }

        return labels;
    }

    /// <summary>Plain Levenshtein distance; the table is 30 rows, so nothing cleverer is warranted.</summary>
    private static int Distance(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
