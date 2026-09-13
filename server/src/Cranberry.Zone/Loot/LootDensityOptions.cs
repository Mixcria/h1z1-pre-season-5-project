using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Loot;

/// <summary>
/// A set of <see cref="LootItemKind"/>s as one 32-bit mask. Used for the room's singleton kinds
/// (one bag, one vest, one helmet), where an allocation-free set that a hot loop can test is worth
/// more than a collection.
/// </summary>
public readonly struct LootItemKindSet(uint mask) : IEquatable<LootItemKindSet>
{
    /// <summary>The raw bit mask, bit <c>n</c> being <see cref="LootItemKind"/> ordinal <c>n</c>.</summary>
    public uint Mask { get; } = mask;

    public bool IsEmpty => Mask == 0;

    public bool Contains(LootItemKind kind) => (Mask & (1u << (int)kind)) != 0;

    public LootItemKindSet With(LootItemKind kind) => new(Mask | (1u << (int)kind));

    public bool Equals(LootItemKindSet other) => Mask == other.Mask;

    public override bool Equals(object? obj) => obj is LootItemKindSet other && Equals(other);

    public override int GetHashCode() => (int)Mask;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "(none)";
        }

        var names = new List<string>();
        foreach (LootItemKind kind in Enum.GetValues<LootItemKind>())
        {
            if (Contains(kind))
            {
                names.Add(kind.ToString());
            }
        }

        return string.Join('+', names);
    }

    public static bool operator ==(LootItemKindSet left, LootItemKindSet right) => left.Equals(right);

    public static bool operator !=(LootItemKindSet left, LootItemKindSet right) => !left.Equals(right);
}

/// <summary>
/// <b>How much loot is on the floor.</b> Everything the density model needs, in one place.
///
/// <para><b>The dial is per family, and since wave 8 it is the owner's own number.</b> The shipped
/// values live in <c>z2-loot-tables.json</c> as each category's
/// <see cref="LootCategoryTable.SpawnChance"/> — written by <c>tools/data/gen-loot-tables.py</c>'s
/// <c>SPAWN_CHANCES</c>, so the world can be retuned without a rebuild — and
/// <see cref="SpawnChanceOverride"/> is how a host or a test overrides every category at once.</para>
///
/// <para><b>O5 (D53): the five gates of the world he actually runs.</b>
/// <see cref="Weapons01SpawnChance"/> 0.1267, <see cref="Gear01SpawnChance"/> 0.0850,
/// <see cref="Backpack01SpawnChance"/> 0.2500, <see cref="FirstAidKit01SpawnChance"/> 0.3000,
/// <see cref="Ammo01SpawnChance"/> 0.6000 — marker-weighted <see cref="WeightedSpawnChance"/>
/// <b>0.1223</b>. Each is his own <c>spawnChance</c> folded together with his own pool and
/// <c>empty</c> weights: <c>effective = spawnChance/100 × (1 − empty / totalPool)</c>. Cranberry
/// keeps that fold — one editable number per family instead of two that have to be kept
/// consistent — and that is the one place this model differs in <i>mechanism</i> from his.
/// docs/39 §1.1 and docs/65 §2.2 ruled those five numbers unusable; <b>D53 supersedes both</b>
/// (docs/78 §2.2).</para>
///
/// <para><b>What it does to the floor.</b> 44,830 gated → 43,095 kept at the old flat 0.27, against
/// <b>20,158 → 20,128</b> at his gates: roughly half as much on the ground, and about
/// <b>30,818</b> ground entities once the cluster boxes are counted rather than 59,816. The mix
/// moves his way too — <c>Gear01</c> from 57 % of the floor to 40 %, <c>FirstAidKit01</c> from
/// 6.5 % to 16 %.</para>
///
/// <para><b>The old derivation, which was honest and is now the fallback.</b> docs/39 §4.2 solved a
/// single <c>p</c> from Cranberry's own measurement — the client's markers sit <b>12.45 to a 4 m
/// room</b> over 4,000 samples of the 166,781 placements — against O3, the owner's retail-footage
/// census of <b>0-6 loose items per room, mean about 3</b>, and corrected it to <c>0.27</c> once the
/// symmetric caps' real 10.1 % cost was measured (0.22 → 2.37 items per room; 0.25 → 2.63;
/// 0.27 → 2.80; 0.30 → 3.01). That ladder is the <i>room-census</i> arm of the evidence and his
/// running server is the <i>live</i> arm, and they disagree by about 2×. D53 and the owner's own
/// instruction this wave both point at the running server, so the running server wins — and
/// <see cref="PreWave8SpawnChance"/> keeps the other answer one line away.</para>
///
/// <para><b>A side-effect worth naming: the room caps go inert.</b> At his gates they suppress 30
/// items out of 20,158 — <b>0.1 %</b> against 3.9 % simulated (11.4 % shipped) at 0.27. Keep them:
/// they now cost nothing and they are still the guard against the owner's own "sixteen items in one
/// small room" report. But their presence or absence stops being a difference that decides anything
/// (docs/78 §2.2).</para>
///
/// <para>The caps are a <b>post-pass over the deterministic roll</b> (<see cref="Z2LootLayout"/>),
/// never a "stop after N in this burst" — that would be the blob-then-nothing defect in a new
/// costume. They are evaluated from <i>every</i> member's own room, so after the pass every marker
/// on the map has at most <see cref="MaxItemsPerRoom"/> items around it, not just the first one
/// looked at.</para>
/// </summary>
public sealed record LootDensityOptions
{
    /// <summary>
    /// <b>D270 — <c>Weapons01</c> at the ruled density, 0.27.</b> Was his 0.1267 (18 % over a
    /// 405-weight pool carrying a 120-weight <c>empty</c> row). Every constant in this group
    /// mirrors <c>SPAWN_CHANCES</c> in <c>tools/data/gen-loot-tables.py</c>. They are documentation
    /// and test anchors — the value the runtime actually uses comes from the generated file — but
    /// the two must agree, and <c>LootDensityTests</c> fails if they drift apart.
    /// </summary>
    public const double Weapons01SpawnChance = Rulings.LootGates.Weapons01SpawnChance;

    /// <summary>
    /// <b>D271 — the clothing gate, at the ruled density 0.27.</b> Was his 0.0850: the lowest of
    /// the five gates, on the family that owns 96,722 of the 168,322 markers, which is why clothing
    /// was 6.8 % of a floor that retail covered in folded shirts and work boots
    /// (<c>AUDIT-loot.md</c> G3). A family gated at the flat ruled density produces its own share of
    /// the markers as its share of the floor, so this number <i>is</i> the audit's Gear01 marker
    /// share, 57.5 %.
    /// </summary>
    public const double Gear01SpawnChance = Rulings.LootGates.Gear01SpawnChance;

    /// <summary><b>D270</b> — <c>Backpack01</c> at the ruled density. Was his 25 %.</summary>
    public const double Backpack01SpawnChance = Rulings.LootGates.Backpack01SpawnChance;

    /// <summary>
    /// His <c>FirstAidKit01</c> gate: 30 %, no <c>empty</c> row. <b>Untouched by D270</b> — it
    /// already sat above the ruled density, and the ruling raises the floor rather than flattening
    /// it. One of the two <c>THIRD_PARTY_SHAPED</c> rows left in the whole server.
    /// </summary>
    public const double FirstAidKit01SpawnChance = Rulings.LootGates.FirstAidKit01SpawnChance;

    /// <summary>
    /// His <c>Ammo01</c> gate: 60 %, no <c>empty</c> row, on 72 markers. <b>Untouched</b>, in
    /// D272's own words: <i>"the client's own markers pair ammo only at the 72 Ammo01 spots — keep
    /// those as they are."</i>
    /// </summary>
    public const double Ammo01SpawnChance = Rulings.LootGates.Ammo01SpawnChance;

    /// <summary>
    /// <b>D275 — the item the 2017-06-29 laminated-armour rules apply to.</b> Laminated Tactical
    /// Body Armor; <c>Models.txt</c> 9583's own DESCRIPTION is <i>"Ground spawn kevlar vest"</i>.
    /// </summary>
    public const uint LaminatedArmourItemDefinitionId =
        Rulings.LootGates.LaminatedArmourItemDefinitionId;

    /// <summary>
    /// The marker-weighted mean of the five gates over the August field's 166,781 item markers.
    /// Documentation and a test anchor; nothing gates against it.
    /// </summary>
    public const double WeightedSpawnChance = Rulings.LootGates.WeightedSpawnChance;

    /// <summary>
    /// The pre-wave-8 world in one number. <c>SpawnChanceOverride = 0.27</c> restores the flat gate
    /// docs/39 solved — 43,095 items, 59,816 ground entities, a 2.80-item room — without
    /// regenerating a file. This is the owner's revert if his own density reads too thin in play,
    /// and it is deliberately a constant rather than a comment so a host can name it.
    /// </summary>
    public const double PreWave8SpawnChance = Rulings.LootGates.PreWave8SpawnChance;

    /// <summary>
    /// The gate a category falls back to when the generated file names none. Only reachable through
    /// a hand-edited table, since the generator writes a gate for every category that has entries.
    /// </summary>
    public const double DefaultSpawnChance = WeightedSpawnChance;

    /// <summary>
    /// The owner's gate for one generated category key, for the tests and for a host that wants to
    /// print the model. A key the wave-8 map does not name — <c>FireExtinguisher</c>, which has no
    /// item in the August client at all — returns 0.
    /// </summary>
    public static double OwnerSpawnChanceFor(string categoryKey) => categoryKey switch
    {
        "Weapons01" => Weapons01SpawnChance,
        "Gear01" => Gear01SpawnChance,
        "Backpack01" => Backpack01SpawnChance,
        "FirstAidKit01" => FirstAidKit01SpawnChance,
        "Ammo01" => Ammo01SpawnChance,
        _ => 0.0,
    };

    /// <summary>The shipped options: the caps the generated file carries, no gate override.</summary>
    public static LootDensityOptions Default { get; } = new();

    /// <summary>
    /// Replaces every category's own <see cref="LootCategoryTable.SpawnChance"/> when set. Null
    /// (the default) uses the file's per-family values, which is the one place the numbers should
    /// normally be changed. A host that wants a denser or emptier world for one run sets this.
    /// <para>
    /// <b>The named alternative is <see cref="PreWave8SpawnChance"/>.</b>
    /// <c>SpawnChanceOverride = 0.27</c> is the whole pre-wave-8 world back in one line — the flat
    /// gate, 43,095 items, 59,816 ground entities — because the owner's own instruction for this
    /// wave is <i>"then I test and loop"</i> and a loop needs a one-word revert. Note that it
    /// flattens the <i>mix</i> as well as the total: at 0.27 the floor is 57 % <c>Gear01</c>, at his
    /// gates it is 40 %.
    /// </para>
    /// </summary>
    public double? SpawnChanceOverride { get; init; }

    /// <summary>Horizontal radius of a "room" for the caps. Owner-authored (D20): 4 m.</summary>
    public float RoomRadiusMetres { get; init; } = Rulings.LootGates.RoomRadiusMetres;

    /// <summary>
    /// Height tolerance of a room, so a first floor's loot is not counted against a ground
    /// floor's. Owner-authored (D20): ±2 m.
    /// </summary>
    public float RoomHeightMetres { get; init; } = Rulings.LootGates.RoomHeightMetres;

    /// <summary>Most items one room may hold — the top of the owner's counted retail range.</summary>
    public int MaxItemsPerRoom { get; init; } = Rulings.LootGates.MaxItemsPerRoom;

    /// <summary>Most weapons one room may hold. Owner-authored (D20).</summary>
    public int MaxWeaponsPerRoom { get; init; } = Rulings.LootGates.MaxWeaponsPerRoom;

    /// <summary>Kinds a room may hold at most one of: backpack, body armour, helmet.</summary>
    public LootItemKindSet SingletonKinds { get; init; } =
        default(LootItemKindSet)
            .With(LootItemKind.Backpack)
            .With(LootItemKind.BodyArmor)
            .With(LootItemKind.Helmet);

    /// <summary>
    /// <b>D275 — are the 2017-06-29 laminated-armour rules in force?</b> On is the ruling; off
    /// restores the flat 18/608 share of <c>Gear01</c> and the 700 vests it lays at D270's density,
    /// which is the A/B.
    /// </summary>
    public bool LaminatedArmourRules { get; init; } = true;

    /// <summary>
    /// The chance a marker that has <i>already rolled</i> the vest keeps it — retail's world spawn
    /// chance, cut from 10 % to 5 % on 2017-06-29 (the owner's own research, High confidence,
    /// dated). It is drawn from the marker's own third sub-stream, so changing it never re-shuffles
    /// which markers are occupied or what else they hold.
    /// </summary>
    public double LaminatedArmourWorldChance { get; init; } =
        Rulings.LootGates.LaminatedArmourWorldChance;

    /// <summary>
    /// Retail's <b>250 m anti-cluster radius</b>: no two vests on the map may stand closer than
    /// this. Enforced in ascending ZONE instance-id order, so the same seed keeps the same vests on
    /// any machine.
    /// </summary>
    public float LaminatedArmourSpacingMetres { get; init; } =
        Rulings.LootGates.LaminatedArmourSpacingMetres;

    /// <summary>
    /// Retail's <b>max 3 per map square</b>. A ceiling guard rather than the binding rule: at the
    /// ruled 5 % the radius above has already spaced the vests below it.
    /// </summary>
    public int LaminatedArmourMaxPerSquare { get; init; } =
        Rulings.LootGates.LaminatedArmourMaxPerSquare;

    /// <summary>
    /// How big a "map square" is, in metres. <b>Cranberry's own</b>: the Z2 terrain's ±4,096 m
    /// extent (docs/29 §2.1) in an 8 × 8 grid. This build ships no map-grid definition of any
    /// kind, and the client's own terrain squares are the 128 m <c>.cnk</c> chunks — far below the
    /// 250 m radius, which would make the cap meaningless. The ruling row is the one line to change
    /// if a real grid size is ever read out of the client.
    /// </summary>
    public float LaminatedArmourMapSquareMetres { get; init; } =
        Rulings.LootGates.LaminatedArmourMapSquareMetres;

    /// <summary>
    /// The gate this category actually rolls against: <see cref="SpawnChanceOverride"/> when set,
    /// otherwise the generated file's own per-category value.
    /// </summary>
    public double SpawnChanceFor(LootCategoryTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return SpawnChanceOverride ?? table.SpawnChance;
    }

    /// <summary>Throws when a hand-edited file carries geometry the cap pass cannot honour.</summary>
    public void Validate(string origin)
    {
        if (!float.IsFinite(RoomRadiusMetres) || RoomRadiusMetres <= 0f)
        {
            throw new InvalidDataException($"{origin}: density.roomRadiusMetres is {RoomRadiusMetres}.");
        }

        if (!float.IsFinite(RoomHeightMetres) || RoomHeightMetres < 0f)
        {
            throw new InvalidDataException($"{origin}: density.roomHeightMetres is {RoomHeightMetres}.");
        }

        if (MaxItemsPerRoom <= 0 || MaxWeaponsPerRoom < 0)
        {
            throw new InvalidDataException(
                $"{origin}: density caps are {MaxItemsPerRoom} item(s) / {MaxWeaponsPerRoom} weapon(s); "
                + "a cap of zero items would empty the world.");
        }

        // Z2LootLayout keeps one byte of running room count per marker, which is 40× the owner's
        // counted retail maximum and still worth pinning rather than truncating silently.
        if (MaxItemsPerRoom > byte.MaxValue || MaxWeaponsPerRoom > byte.MaxValue)
        {
            throw new InvalidDataException(
                $"{origin}: density caps above {byte.MaxValue} are not supported.");
        }

        if (SpawnChanceOverride is { } chance && (!double.IsFinite(chance) || chance is < 0.0 or > 1.0))
        {
            throw new InvalidDataException($"{origin}: spawnChanceOverride {chance} is not a probability.");
        }

        if (!double.IsFinite(LaminatedArmourWorldChance)
            || LaminatedArmourWorldChance is < 0.0 or > 1.0)
        {
            throw new InvalidDataException(
                $"{origin}: laminatedArmourWorldChance {LaminatedArmourWorldChance} is not a probability.");
        }

        if (!float.IsFinite(LaminatedArmourSpacingMetres) || LaminatedArmourSpacingMetres < 0f)
        {
            throw new InvalidDataException(
                $"{origin}: laminatedArmourSpacingMetres is {LaminatedArmourSpacingMetres}.");
        }

        if (!float.IsFinite(LaminatedArmourMapSquareMetres) || LaminatedArmourMapSquareMetres <= 0f)
        {
            throw new InvalidDataException(
                $"{origin}: laminatedArmourMapSquareMetres is {LaminatedArmourMapSquareMetres}; "
                + "a square of zero metres has no cells to cap.");
        }

        if (LaminatedArmourMaxPerSquare < 0)
        {
            throw new InvalidDataException(
                $"{origin}: laminatedArmourMaxPerSquare is {LaminatedArmourMaxPerSquare}.");
        }
    }
}
