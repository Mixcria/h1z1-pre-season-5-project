using Cranberry.Zone.Appearance;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Weapons;

/// <summary>Which projectile table <c>CRANBERRY_PROJECTILE_TABLE</c> selects.</summary>
public enum ProjectileTableSource
{
    /// <summary>
    /// Cranberry's own 14 records at the client's constructor defaults, with no model and no
    /// effect - every wave before docs/123, byte for byte.
    /// </summary>
    August = 0,

    /// <summary>All 131 rows of the owner's own Z1 <c>projectileDefinitions.json</c> (D314).</summary>
    Z1 = 1,
}

/// <summary>
/// <b>D314 - Z1's 131 projectile definitions, on Cranberry's wire (docs/123 §6).</b>
///
/// <para>
/// Unlike the weapon table, this one needed no re-expression: the 1087
/// <c>projectileDefinitionSchema.json</c> column order is <b>field for field</b> the [P] 1148
/// record order <see cref="ProjectileDefinitionRecord"/> already writes, from <c>ID</c> at
/// <c>rec+0x18</c> through the <c>PLAYER_BULLET_RADIUS_LIST</c> at <c>rec+0x120</c> to
/// <c>ANGULAR_VELOCITY_Z_MAX</c> at <c>rec+0x118</c>. The only difference in the whole record is
/// that 1148 reads one more word after that - <c>FADE_OUT_MS</c> at <c>rec+0x11c</c> - which the
/// raw branch writes as the client's own zero.
/// </para>
///
/// <para>
/// <b>Three column identifications fall out of the match</b>, and they correct Cranberry's own
/// names rather than the wire (no length moves, no byte moves):
/// </para>
/// <list type="bullet">
/// <item><c>rec+0xa8</c>, carried as "an UNNAMED scalar with a <c>10.0f</c> preset" since docs/120
/// §3.2, is <b><c>DAMAGE</c></b>.</item>
/// <item>the record's SECOND string (<c>rec+0x40</c>), which
/// <see cref="ProjectileDefinitionRecord.AttachmentOverride"/> names, is
/// <b><c>FP_MODEL_FILE_NAME</c></b>.</item>
/// <item>the record's THIRD string (<c>rec+0x88</c>), which
/// <see cref="ProjectileDefinitionRecord.FirstPersonModelFileName"/> names, is
/// <b><c>BONE_ATTACHMENT_OVERRIDE</c></b>.</item>
/// </list>
///
/// <para>
/// <b>The effect audit.</b> A composite-effect id the August build has no definition for makes the
/// client queue an effect it cannot resolve. Every effect column of every row
/// (<c>PROJECTILE_EFFECT_ID</c>, <c>LAND_EFFECT_ID</c>, <c>INDIRECT_DAMAGE_EFFECT_ID</c>,
/// <c>TRACER_EFFECT_ID</c>, <c>FP_TRACER_EFFECT_ID</c>) is therefore checked against
/// <see cref="AugustEffectCatalog"/> - the build's own 1,047 definitions - and zeroed when it is
/// not there. Z1 does the same thing by hand for three ids its own 1087 client lacked
/// (<c>ClientKnownEffectsOnly</c>, 5836 / 5840 / 5904); this is the same idea against the August
/// table instead of a fixed list. <see cref="ZeroedEffectIds"/> is what it found.
/// </para>
/// </summary>
public static class Z1ProjectileTable
{
    /// <summary>
    /// Every effect id in the Z1 rows that <see cref="AugustEffectCatalog"/> does not define, and
    /// which <see cref="Records"/> therefore ships as 0. Ascending, distinct.
    /// </summary>
    public static IReadOnlyList<uint> ZeroedEffectIds => _zeroedEffectIds;

    /// <summary>How many effect cells (row x column) were zeroed to build <see cref="Records"/>.</summary>
    public static int ZeroedEffectCellCount => _zeroedCells;

    /// <summary>
    /// Every model file name in the Z1 rows that no shipping row of <see cref="AugustModelCatalog"/>
    /// carries (Planetside leftovers in the 2016 table: RPG rockets, snowballs, tank shells,
    /// vehicle debris), and which <see cref="Records"/> therefore ships blank - the client then
    /// draws <c>InvisibleTriangle.adr</c> instead of trying to load a file its packs lack. The
    /// 20:12 session on 2026-09-04 died at the table with 16 of these on the wire (docs/123 §7).
    /// Sorted, distinct.
    /// </summary>
    public static IReadOnlyList<string> BlankedModelNames => _blankedModelNames;

    /// <summary>How many string cells (row x column) were blanked to build <see cref="Records"/>.</summary>
    public static int BlankedModelCellCount => _blankedModelCells;

    /// <summary>
    /// The 131 Z1 rows as 1148 records, with unknown effect ids zeroed. Ascending by id, which is
    /// the order the source is emitted in.
    /// </summary>
    public static IReadOnlyList<ProjectileDefinitionRecord> Records => _records;

    /// <summary>
    /// The shipped table: <see cref="Records"/> plus, when <paramref name="throwables"/> is on, the
    /// five grenade records of <see cref="AugustThrowables"/>.
    /// <para>
    /// The two sets are disjoint by construction - the throwable ids are
    /// <c>Rulings.Throwables.ProjectileIdBase</c> (71,000) and up, and the highest Z1 id is 70,229 -
    /// so no Z1 row can silently replace a grenade, and the docs/122 lane A step 4 rule ("keep the
    /// five throwable records if the Z1 rows lack them") resolves to "keep all five".
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProjectileDefinitionRecord> RecordsFor(
        bool throwables, float throwSpeed) =>
        throwables
            ? [.. Records, .. AugustThrowables.ProjectileRecords(throwSpeed)]
            : Records;

    /// <summary>The blob for a session, built through the same writer the August table uses.</summary>
    public static ProjectileDefinitionsBlob CreateBlob(
        bool populate, bool throwables, float throwSpeed) =>
        new(populate ? RecordsFor(throwables, throwSpeed) : []);

    /// <summary>Every projectile id the Z1 table defines - what a list-4 row can resolve against.</summary>
    public static IReadOnlySet<uint> ProjectileIds { get; } =
        Z1ProjectileFacts.Rows.Select(row => row.ProjectileId).ToHashSet();

    private static readonly List<uint> _zeroedEffectIds = [];
    private static int _zeroedCells;
    private static readonly List<string> _blankedModelNames = [];
    private static int _blankedModelCells;
    private static readonly ProjectileDefinitionRecord[] _records = Build();

    private static ProjectileDefinitionRecord[] Build()
    {
        SortedSet<uint> zeroed = [];
        SortedSet<string> blanked = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> shipping = new(
            AugustModelCatalog.Rows.Where(model => model.Ships).Select(model => model.FileName),
            StringComparer.OrdinalIgnoreCase);
        var records = new ProjectileDefinitionRecord[Z1ProjectileFacts.Rows.Length];

        string ShippingOrBlank(string fileName)
        {
            if (fileName.Length == 0 || shipping.Contains(fileName))
            {
                return fileName;
            }

            blanked.Add(fileName);
            _blankedModelCells++;
            return string.Empty;
        }

        for (int i = 0; i < records.Length; i++)
        {
            Z1ProjectileRow row = Z1ProjectileFacts.Rows[i];
            uint[] words = [.. row.Words];

            foreach (int column in Z1ProjectileFacts.EffectColumnIndices)
            {
                uint effectId = words[column];
                if (effectId == 0 || AugustEffectCatalog.Contains(effectId))
                {
                    continue;
                }

                zeroed.Add(effectId);
                _zeroedCells++;
                words[column] = 0;
            }

            records[i] = new ProjectileDefinitionRecord(
                row.ProjectileId,
                ShippingOrBlank(row.ModelFileName),
                ShippingOrBlank(row.FirstPersonModelFileName),
                row.BoneAttachmentOverride)
            {
                RawWords = words,
                BulletRadii = row.BulletRadii,
            };
        }

        _zeroedEffectIds.AddRange(zeroed);
        _blankedModelNames.AddRange(blanked);
        return records;
    }
}
