using System.IO.Compression;
using System.Security.Cryptography;
using Cranberry.Protocol;
using Cranberry.Zone.Appearance;

namespace Cranberry.Zone;

/// <summary>
/// One retained row of the dynamic-appearance table, as the client reads it.
/// </summary>
/// <param name="RowId">The row id carried in a <c>CharacterEquipmentAttachment</c>'s appearance list.</param>
/// <param name="ItemDefinitionId">The item this row dresses.</param>
/// <param name="ModelId">A <c>Models.txt</c> row - resolve it through <c>AugustModelCatalog</c>.</param>
/// <param name="GenderId">
/// 1 male, 2 female, and <b>0 is a wildcard that WINS over the gendered rows</b> - measured on the
/// owner's own client, not inferred (docs/80 s1.4). A wildcard row can therefore name the other
/// body's mesh, which is what <c>AugustSkinCensus</c> verdict C1 looks for.
/// </param>
/// <param name="ShaderParameterGroupId">The colourway; 0 means the row carries none.</param>
public readonly record struct AugustAppearanceRow(
    uint RowId,
    uint ItemDefinitionId,
    uint ModelId,
    uint GenderId,
    uint ShaderParameterGroupId);

/// <summary>
/// The two texture parameters of one shader group that decide whether a <c>_Tintable</c> mesh
/// shows a colour or composites bare grey.
/// </summary>
public readonly record struct AugustShaderGroupTint(string? DecalTint, string? TilingTint)
{
    /// <summary>
    /// The client's own "nothing applied" textures. An empty name is the same statement.
    /// </summary>
    public static bool IsUntinted(string? texture) =>
        string.IsNullOrEmpty(texture)
        || texture.Equals("white.dds", StringComparison.OrdinalIgnoreCase)
        || texture.Equals("grey.dds", StringComparison.OrdinalIgnoreCase)
        || texture.Equals("gray.dds", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when neither slot carries a real pattern.</summary>
    public bool RendersUntinted => IsUntinted(DecalTint) && IsUntinted(TilingTint);
}

/// <summary>
/// Converts the locally available Z1 dynamic-appearance reference packet into the named
/// <c>ReferenceData</c> payload used by ClientProtocol_1148. Only rows reachable from the August
/// wardrobe catalogue are retained, so the client receives compatible skin definitions without
/// the several megabytes of unrelated NPC and legacy-item appearances.
/// </summary>
public sealed class AugustDynamicAppearanceTable
{
    private const int SourcePacketLength = 6_625_396;
    private const string SourcePacketMd5 = "59d33ecb0580512fa13c4a78d69d084b";
    private const int MaximumSourceBytes = 16 * 1024 * 1024;

    private readonly byte[] _payload;
    private readonly Lazy<ReferenceData> _compressedReference;
    private readonly IReadOnlyDictionary<uint, IReadOnlyList<uint>> _appearanceRowsByItem;
    private readonly IReadOnlyDictionary<uint, AugustAppearanceRow> _rowsById;
    private readonly IReadOnlySet<uint> _definedShaderGroups;
    private readonly IReadOnlyDictionary<uint, AugustShaderGroupTint> _tintByGroup;
    private readonly IReadOnlySet<uint> _baseTintedGroups;

    private AugustDynamicAppearanceTable(
        byte[] payload,
        IReadOnlyDictionary<uint, IReadOnlyList<uint>> appearanceRowsByItem,
        IReadOnlyDictionary<uint, AugustAppearanceRow> rowsById,
        IReadOnlySet<uint> definedShaderGroups,
        IReadOnlyDictionary<uint, AugustShaderGroupTint> tintByGroup,
        IReadOnlySet<uint> baseTintedGroups,
        int appearanceCount,
        int semanticCount,
        int parameterCount,
        int correctedAppearanceCount,
        int correctedShaderGroupCount,
        int correctedGenderCount,
        bool wholeTable)
    {
        WholeTable = wholeTable;
        _payload = payload;
        _compressedReference = new(() => ReferenceData.CreateCompressed(DynamicAppearanceReference.TypeName, payload));
        _appearanceRowsByItem = appearanceRowsByItem;
        _rowsById = rowsById;
        _definedShaderGroups = definedShaderGroups;
        _tintByGroup = tintByGroup;
        _baseTintedGroups = baseTintedGroups;
        AppearanceCount = appearanceCount;
        SemanticCount = semanticCount;
        ParameterCount = parameterCount;
        CorrectedAppearanceCount = correctedAppearanceCount;
        CorrectedShaderGroupCount = correctedShaderGroupCount;
        CorrectedGenderCount = correctedGenderCount;
    }

    /// <summary>True when this table is the friend capture WHOLE, D316's default.</summary>
    public bool WholeTable { get; }

    public int AppearanceCount { get; }
    public int SemanticCount { get; }
    public int ParameterCount { get; }
    public int CorrectedAppearanceCount { get; }
    public int CorrectedShaderGroupCount { get; }
    public int CorrectedGenderCount { get; }
    public int PayloadLength => _payload.Length;

    /// <summary>
    /// Whether the generated <see cref="AugustSkinCatalog.AppearanceRowOverrides"/> are applied to
    /// the appearance rows.
    /// </summary>
    /// <remarks>
    /// Default <c>false</c> — regression 2026-08-29 (docs/32). The overrides rewrite
    /// ShaderParameterGroupId and GenderId on 152 rows, which pulls 484 extra shader values into
    /// the table and grows the ReferenceData payload from 1,268,831 to 1,295,407 bytes. The table
    /// is re-sent during a zone transition, where the client rebuilds the actor from it; with the
    /// overrides applied the client stops answering after ClientBeginZoning and never sends
    /// ClientIsReady (Zoning), so it sits on LoadingScreenWindow forever. The last known-good
    /// session (logs/host-20260829-150206.log — "22,409 shader values, 1,268,831 bytes", no
    /// "repaired" clause) predates them; every session from 15:07 onward carries them and none has
    /// entered Z2 since. Re-enable only once the overrides are validated against the client's own
    /// reader — the menu path tolerates them, the zoning path does not.
    /// </remarks>
    public static bool ApplyAppearanceRowOverrides { get; set; }

    /// <summary>
    /// Whether <see cref="AugustAuthoredAppearance"/>'s Cranberry-authored rows are added to the
    /// transmitted table. <b>Default on</b>; <c>CRANBERRY_APPEARANCE_AUTHORED_ROWS=0</c> restores
    /// the pre-2026-09-03 payload exactly, to the byte.
    /// <para>
    /// docs/106 addendum 2026-09-03. The boot census names thirteen ground-loot wearables — the
    /// four Mansport backpacks, the six <c>TintTshirt</c> shirts, the two motorcycle helmets and
    /// the Conveys sneakers — on both bodies with one verdict: <i>"no appearance row at all, and
    /// the mesh is tintable - it composites white.dds / grey.dds, i.e. GREY"</i>. None of the
    /// thirteen is an <see cref="AugustSkinCatalog"/> reward, so <see cref="Filter"/>'s
    /// <c>wantedItems</c> discards every row the source holds for them and the client is handed a
    /// tintable mesh with nothing to tint it. These rows are <b>Cranberry's own</b> — the
    /// <c>ModelId</c> and <c>GenderId</c> are the August <c>Models.txt</c>, the
    /// highlight/midtone/shadow ramp is measured off each mesh's own <c>_C.dds</c> colour map by
    /// <c>tools/appearance/ddstint.py</c>, and only the hue is a ruling (D203), read off the
    /// item's own name in the client's locale.
    /// </para>
    /// <para>
    /// This adds to the payload; it rewrites nothing. The D22 MD5 gate on the captured source and
    /// every retained captured row, semantic and parameter are byte-identical either way, which
    /// is what <c>AugustDynamicAppearanceTableTests</c> pins in both states.
    /// </para>
    /// </summary>
    public static bool ApplyAuthoredRows { get; set; } = AuthoredRowsDefault();

    /// <summary>
    /// Whether the WHOLE friend <c>DynamicAppearance</c> capture is re-wrapped and sent - every
    /// appearance row, every item map and every shader parameter - or only the rows the August
    /// wardrobe catalogue reaches. <b>Default whole</b> (D316);
    /// <c>CRANBERRY_APPEARANCE_TABLE=filtered</c> restores the pre-2026-09-04 payload to the byte.
    /// <para>
    /// docs/124. The owner's Z1 server, whose skins are right, ships this table WHOLE and
    /// uncompressed - 3,551 appearances, 1,667 item maps, 119,158 shader parameters, 6,625,396
    /// bytes on the wire - and its own note says an item with no row here composites
    /// <c>DecalTint=white.dds</c> / <c>TilingTint=grey.dds</c>, which is exactly the grey/white the
    /// owner reports. Cranberry's filter kept 1,474 / 686 / 22,487, so every worn item outside the
    /// reward catalogue - the backpack, the helmet, the boots, the pump, the AK - was handed a
    /// tintable mesh with no colourway at all. D312 lets the capture ship whole; this is the
    /// switch that does it.
    /// </para>
    /// <para>
    /// Two things the whole table changes besides its size, both because the source becomes the
    /// authority: the thirteen Cranberry-authored rows (D203) are dropped for any item the capture
    /// already dresses (all thirteen, <see cref="SuppressedAuthoredRowCount"/>), and the starter
    /// palette appends only the groups the capture does not define
    /// (<see cref="SuppressedStarterValueCount"/>).
    /// </para>
    /// </summary>
    public static bool ShipWholeTable { get; set; } = WholeTableDefault();

    /// <summary>
    /// <c>CRANBERRY_APPEARANCE_TABLE</c>: <c>full</c> (default) or <c>filtered</c>. Anything else
    /// is read as <c>full</c>, because a typo must not silently ship the grey table.
    /// </summary>
    public static bool WholeTableFromText(string? value) =>
        !(value ?? string.Empty).Trim().Equals("filtered", StringComparison.OrdinalIgnoreCase);

    private static bool WholeTableDefault() =>
        WholeTableFromText(Environment.GetEnvironmentVariable("CRANBERRY_APPEARANCE_TABLE"));

    /// <summary>
    /// Authored rows (D203) the whole table drops because the capture already dresses their item.
    /// Always zero in <c>filtered</c> mode, where the authored rows are the whole point.
    /// </summary>
    public int SuppressedAuthoredRowCount { get; private init; }

    /// <summary>
    /// Starter-palette shader values the whole table drops because the capture defines that group
    /// itself. Always zero in <c>filtered</c> mode.
    /// </summary>
    public int SuppressedStarterValueCount { get; private init; }

    private static bool AuthoredRowsDefault()
    {
        string? value = Environment.GetEnvironmentVariable("CRANBERRY_APPEARANCE_AUTHORED_ROWS");
        return value is null
            || !(value.Equals("0", StringComparison.Ordinal)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The number of Cranberry-authored appearance rows this table carries.</summary>
    public int AuthoredRowCount { get; private init; }

    public ReferenceData CreateReferenceData() =>
        new(DynamicAppearanceReference.TypeName, _payload);

    /// <summary>Cache the complete table in the client's existing LZ4 envelope for WAN startup.</summary>
    public ReferenceData CreateCompressedReferenceData() => _compressedReference.Value;

    public IReadOnlyList<uint> AppearanceRowsFor(uint itemDefinitionId)
    {
        if (!_appearanceRowsByItem.TryGetValue(
                itemDefinitionId, out IReadOnlyList<uint>? rows))
        {
            return [];
        }

        // Four-row apparel entries collapse to the FINAL pair on the equipment wire. The two
        // indices are unchanged from wave 4; the reason is not.
        //
        // CORRECTED wave 8 (D53, docs/80 s1.4). This used to say the four rows were a hood
        // down/up pair and that the menu default was Up. The owner's own Z1 server measured what
        // the four rows actually are, on his client, and it is not hood state: item 3405's rows
        // arrive as [1081 gender 2, 1082 gender 1, 1597 gender 0, 1598 gender 0], and what the
        // client LOADED on a male body was 1597 - the FEMALE mesh - x46 in gm-clicksession.log
        // L11951. A GENDER_ID of 0 is a WILDCARD that beats the gendered rows, so the last pair
        // is the wildcard pair, and taking it is right for a reason that is uncomfortable rather
        // than reassuring: a wildcard can name the other body's mesh. AugustSkinCensus reports
        // exactly that case (verdict C1) for the August population, at boot, byte-free.
        return rows.Count == 4 ? [rows[2], rows[3]] : rows;
    }

    /// <summary>
    /// The wire projection of an item's appearance rows, <b>ordered so the row naming this body's
    /// mesh comes first</b>.
    /// <para>
    /// <b>This is the fix for a defect the wave-8 census found and nobody had ever seen</b>
    /// (docs/80 §Built). Every four-row hooded-chest entry collapses to a pair of
    /// <c>GENDER_ID = 0</c> <em>wildcards</em>, one naming
    /// <c>SurvivorMale_Chest_Hoodie_Up_Tintable.adr</c> and one naming the female mesh - and the
    /// order varies per item: 2375 puts the male row first, 3405 puts the female row first. The
    /// owner measured on his own client that the FIRST wildcard wins, so on 49 (item, body) pairs
    /// in this catalogue the client was being handed the other body's hoodie. Reordering the pair
    /// costs no bytes, changes no id, and cannot affect an item whose rows already agree with the
    /// body.
    /// </para>
    /// <para>
    /// <paramref name="gender"/> of 0 disables the reordering entirely and returns
    /// <see cref="AppearanceRowsFor(uint)"/> verbatim, which is the pre-wave-8 wire and the
    /// rollback (<c>CRANBERRY_GENDER_ROWS=0</c>).
    /// </para>
    /// </summary>
    public IReadOnlyList<uint> AppearanceRowsFor(uint itemDefinitionId, uint gender)
    {
        IReadOnlyList<uint> rows = AppearanceRowsFor(itemDefinitionId);
        return OrderRowsForBody(rows, gender);
    }

    /// <summary>
    /// The explicit down/up projection for a hoodie. Four-row hoodie definitions are two gendered
    /// <c>Down</c> rows followed by two wildcard <c>Up</c> rows; the generic projection deliberately
    /// retains the historical final pair, while this API lets the inventory action choose posture.
    /// </summary>
    public IReadOnlyList<uint> AppearanceRowsForHood(
        uint itemDefinitionId,
        bool hoodUp,
        uint gender = 0)
    {
        if (!_appearanceRowsByItem.TryGetValue(
                itemDefinitionId, out IReadOnlyList<uint>? allRows))
        {
            return [];
        }

        IReadOnlyList<uint> selected = allRows.Count == 4
            ? hoodUp ? [allRows[2], allRows[3]] : [allRows[0], allRows[1]]
            : AppearanceRowsFor(itemDefinitionId);
        return OrderRowsForBody(selected, gender);
    }

    private IReadOnlyList<uint> OrderRowsForBody(IReadOnlyList<uint> rows, uint gender)
    {
        if (gender == 0 || rows.Count < 2)
        {
            return rows;
        }

        // Only ever a repair, never a reshuffle: act if and only if the row the client would pick
        // FIRST names the OTHER body, and some later row names this one.
        if (BakedGenderOfRow(rows[0]) != OtherBody(gender))
        {
            return rows;
        }

        for (int index = 1; index < rows.Count; index++)
        {
            if (BakedGenderOfRow(rows[index]) != gender)
            {
                continue;
            }

            var reordered = new uint[rows.Count];
            reordered[0] = rows[index];
            int next = 1;
            for (int source = 0; source < rows.Count; source++)
            {
                if (source != index)
                {
                    reordered[next++] = rows[source];
                }
            }

            return reordered;
        }

        return rows;
    }

    private static uint OtherBody(uint gender) =>
        gender == CharacterVisuals.Female ? CharacterVisuals.Male : CharacterVisuals.Female;

    private uint BakedGenderOfRow(uint rowId) =>
        _rowsById.TryGetValue(rowId, out AugustAppearanceRow row)
            ? Appearance.AugustSkinCensus.BakedGenderOf(AugustModelCatalog.FileNameFor(row.ModelId))
            : 0;

    /// <summary>
    /// The appearance rows this server actually puts on the wire for one item, in wire order, with
    /// their model, gender and shader group.
    /// <para>
    /// Deliberately the <b>same</b> projection as <see cref="AppearanceRowsFor"/> - four-row
    /// entries collapsed to the final pair - and not the full retained set. The client can only
    /// pick from the ids the attachment carries, so a census that resolved over rows the packet
    /// omits would describe a packet nobody sends.
    /// </para>
    /// </summary>
    public IReadOnlyList<AugustAppearanceRow> RowsForItem(uint itemDefinitionId) =>
        RowsForItem(itemDefinitionId, gender: 0);

    /// <summary>
    /// The same, for a character of <paramref name="gender"/>, so a census describes the packet a
    /// real body would receive rather than an unordered one. Zero disables the ordering.
    /// </summary>
    public IReadOnlyList<AugustAppearanceRow> RowsForItem(uint itemDefinitionId, uint gender)
    {
        IReadOnlyList<uint> onWire = AppearanceRowsFor(itemDefinitionId, gender);
        if (onWire.Count == 0)
        {
            return [];
        }

        var rows = new List<AugustAppearanceRow>(onWire.Count);
        foreach (uint rowId in onWire)
        {
            if (_rowsById.TryGetValue(rowId, out AugustAppearanceRow row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>One retained appearance row by its row id.</summary>
    public bool TryGetRow(uint rowId, out AugustAppearanceRow row) =>
        _rowsById.TryGetValue(rowId, out row);

    /// <summary>
    /// The shader parameter group the August client itself would end up applying to an attachment
    /// carrying this item's appearance ids on a body of <paramref name="gender"/> — 0 when no row
    /// applies.
    /// <para>
    /// <b>The selection rule is the client's, read out of its own selector</b>
    /// <c>FUN_140c476f0</c> (dump <c>out\ghidra-aug\wave10-equipslot</c>, 2026-09-03): the
    /// attachment's appearance ids live in a <c>std::set</c> and the selector walks it by
    /// <em>in-order traversal</em> — leftmost node first, successor through the parent link — so
    /// the ids are tried in <b>ascending row-id order, never packet order</b>. For each id it
    /// finds the row, and takes it when <c>CLIENT_REQUIREMENT_ID = 0</c> and either
    /// <c>GENDER_ID = 0</c> (a wildcard) or <c>GENDER_ID</c> equals the actor's gender
    /// (actor+0x18c4, named "Gender" by the client's own data-source publisher
    /// <c>FUN_1411e7fe0</c>); otherwise it moves to the next id. The winning row supplies both the
    /// mesh (its <c>ModelId</c>, resolved through the model manager) and the colourway (its
    /// <c>ShaderParameterGroupId</c>) — the packet's own model name and group are used only when
    /// no row resolves (<c>FUN_140c70d60</c> tail, attachment+0x190).
    /// </para>
    /// <para>
    /// That last sentence is why this method exists: the packet's <c>ShaderParameterGroupId</c> is
    /// the <em>fallback</em> colour, and Cranberry was sending 0 there for every wardrobe row and
    /// for the active hand, which is the client's neutral <c>Default</c> tint — highlight 1,1,1,
    /// midtone .5,.5,.5, shadow 0,0,0 (docs/69 §3.4) — i.e. white on a greyscale tint mask and
    /// grey on a <c>_Tintable</c> mesh.
    /// </para>
    /// </summary>
    public uint ShaderGroupFor(uint itemDefinitionId, uint gender)
    {
        IReadOnlyList<uint> rowIds = AppearanceRowsFor(itemDefinitionId);
        if (rowIds.Count == 0)
        {
            return 0;
        }

        // Ascending row id: the client's set order, not ours. Sorting a copy keeps the wire order
        // (which OrderAppearanceRowsByGender still decides) independent of this answer.
        uint[] ascending = [.. rowIds];
        Array.Sort(ascending);

        foreach (uint rowId in ascending)
        {
            if (!_rowsById.TryGetValue(rowId, out AugustAppearanceRow row))
            {
                continue;
            }

            if (row.GenderId == 0 || row.GenderId == gender)
            {
                return row.ShaderParameterGroupId;
            }
        }

        return 0;
    }

    /// <summary>
    /// The group of an item's lowest-numbered appearance row <b>whatever body it is for</b>; 0 when
    /// the item has no row at all.
    /// <para>
    /// <b>The answer to the census's <c>NoRowForThisBody</c> verdict</b> (docs/106 §12, D226).
    /// <see cref="ShaderGroupFor(uint, uint)"/> returns 0 when every row belongs to the other body,
    /// and 0 on the wire is not "no opinion" - the client reads the packet's own field when no row
    /// resolves and 0 there is the neutral <c>Default</c> tint. Item 10, the AR-15, is exactly that
    /// case: two rows, both <c>GENDER_ID 2</c>, and a white rifle on every male body. A weapon's
    /// colourway is not gendered - it is the same mesh and the same colour map either way - so the
    /// other body's group is the right answer and the only alternative is white.
    /// </para>
    /// <para>
    /// It is deliberately a SEPARATE method rather than a fallback inside
    /// <see cref="ShaderGroupFor(uint, uint)"/>: that one answers "what would the client's own
    /// selector land on", which is a question about the client, and this one answers "what colourway
    /// does this item have", which is a question about the table. The caller decides which it wants,
    /// and <c>CRANBERRY_CROSS_GENDER_SHADER=0</c> takes this one away.
    /// </para>
    /// </summary>
    public uint ShaderGroupForAnyBody(uint itemDefinitionId)
    {
        IReadOnlyList<uint> rowIds = AppearanceRowsFor(itemDefinitionId);

        if (rowIds.Count == 0)
        {
            return 0;
        }

        // The same ascending walk, without the gender predicate.
        uint[] ascending = [.. rowIds];
        Array.Sort(ascending);

        foreach (uint rowId in ascending)
        {
            if (_rowsById.TryGetValue(rowId, out AugustAppearanceRow row))
            {
                return row.ShaderParameterGroupId;
            }
        }

        return 0;
    }

    /// <summary>
    /// True when the payload this table puts on the wire carries parameter rows for
    /// <paramref name="shaderParameterGroupId"/>.
    /// <para>
    /// Handing the client a group id it holds no parameters for is the one thing docs/54 s4
    /// forbids without its own experiment. This is the check that keeps the colour path honest
    /// <em>per item</em> instead of by a global switch: the nine roster weapons are already inside
    /// the filter's <c>wantedItems</c>, so colouring them costs zero payload bytes, while the 26
    /// worn wearables the filter discards stay at 0 and docs/54 I4 remains an untouched,
    /// separately-observed experiment (docs/80 edit 2).
    /// </para>
    /// </summary>
    public bool DefinesShaderGroup(uint shaderParameterGroupId) =>
        shaderParameterGroupId != 0 && _definedShaderGroups.Contains(shaderParameterGroupId);

    /// <summary>
    /// The <c>DecalTint</c> / <c>TilingTint</c> textures a retained shader group composites.
    /// Both being one of the client's untinted defaults (<c>white.dds</c>, <c>grey.dds</c>) over a
    /// <c>_Tintable</c> mesh is the definition of "renders grey" - the owner's Z1 census reads
    /// exactly these two parameter rows for exactly that verdict.
    /// </summary>
    public AugustShaderGroupTint TintOfGroup(uint shaderParameterGroupId) =>
        _tintByGroup.TryGetValue(shaderParameterGroupId, out AugustShaderGroupTint tint)
            ? tint
            : default;

    /// <summary>
    /// True when the group carries a <c>BaseTint*A</c> ramp that is <em>not</em> the client's
    /// neutral <c>Default</c> — i.e. it recolours the albedo itself rather than compositing a
    /// pattern over it.
    /// <para>
    /// <b>Why this is a second, independent way to be coloured.</b> The census's C2 verdict was
    /// ported from the owner's Z1 server, whose every colourway carries a real
    /// <c>DecalTint</c>/<c>TilingTint</c> texture, so "both untinted ⇒ grey" held there. It does
    /// not hold in general: docs/69 §3.4 measured the material and the three anchors that decide
    /// what a tintable mesh looks like are <c>BaseTintHighlightA</c>, <c>BaseTintMidtoneA</c> and
    /// <c>BaseTintShadowA</c> — highlight <c>1,1,1</c> / midtone <c>.5,.5,.5</c> / shadow
    /// <c>0,0,0</c> is the identity remap that leaves a luminance mask grey, and anything else is
    /// a colour. Cranberry's own starter palette has always coloured the starter outfit exactly
    /// this way, and so do the authored rows (docs/106 addendum).
    /// </para>
    /// </summary>
    public bool CarriesBaseTint(uint shaderParameterGroupId) =>
        _baseTintedGroups.Contains(shaderParameterGroupId);

    public static bool TryLoad(
        string? compressedPacketPath,
        out AugustDynamicAppearanceTable? table,
        out string status)
    {
        table = null;
        if (string.IsNullOrWhiteSpace(compressedPacketPath))
        {
            status = "no Z1 appearance source configured; using the project starter palette";
            return false;
        }

        if (!File.Exists(compressedPacketPath))
        {
            status = $"appearance source '{compressedPacketPath}' does not exist; using the project starter palette";
            return false;
        }

        try
        {
            byte[] source = Inflate(compressedPacketPath);
            if (source.Length != SourcePacketLength)
            {
                status = $"appearance source inflated to {source.Length:N0} bytes, expected {SourcePacketLength:N0}";
                return false;
            }

            string md5 = Convert.ToHexString(MD5.HashData(source)).ToLowerInvariant();
            if (!string.Equals(md5, SourcePacketMd5, StringComparison.Ordinal))
            {
                status = $"appearance source md5 {md5} is not the validated local reference {SourcePacketMd5}";
                return false;
            }

            table = Filter(source);
            status = (table.WholeTable
                    ? "WHOLE friend appearance table: "
                    : "filtered Z1-compatible appearance table: ")
                + $"{table.AppearanceCount:N0} appearances, "
                + $"{table.SemanticCount:N0} item maps, {table.ParameterCount:N0} shader values, "
                + $"{table.PayloadLength:N0} bytes; repaired {table.CorrectedAppearanceCount:N0} "
                + $"rows ({table.CorrectedShaderGroupCount:N0} shader groups, "
                + $"{table.CorrectedGenderCount:N0} genders); "
                + (table.AuthoredRowCount > 0
                    ? $"{table.AuthoredRowCount:N0} Cranberry-authored row(s) for "
                        + $"{AugustAuthoredAppearance.Items.Length:N0} loot wearable(s) added "
                        + "(CRANBERRY_APPEARANCE_AUTHORED_ROWS=0 removes them)"
                    : "no Cranberry-authored rows (CRANBERRY_APPEARANCE_AUTHORED_ROWS is off)")
                + (table.WholeTable
                    ? $"; the capture is the authority, so {table.SuppressedAuthoredRowCount:N0} "
                        + $"authored row(s) and {table.SuppressedStarterValueCount:N0} starter "
                        + "shader value(s) were dropped as duplicates "
                        + "(CRANBERRY_APPEARANCE_TABLE=filtered restores the old payload)"
                    : string.Empty);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or InvalidDataException
            or PacketFormatException
            or ArgumentException)
        {
            status = $"appearance source rejected ({exception.Message}); using the project starter palette";
            return false;
        }
    }

    private static byte[] Inflate(string path)
    {
        using FileStream input = File.OpenRead(path);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(SourcePacketLength);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = inflate.Read(buffer);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            if (output.Length > MaximumSourceBytes)
            {
                throw new InvalidDataException(
                    $"dynamic-appearance source exceeds {MaximumSourceBytes:N0} bytes");
            }
        }

        return output.ToArray();
    }

    private static AugustDynamicAppearanceTable Filter(byte[] source)
    {
        var reader = new PacketReader(source);
        if (reader.ReadByte() != ZoneOpcodes.ReferenceData || reader.ReadByte() != 0x06)
        {
            throw new InvalidDataException("source is not the Z1 ReferenceData.DynamicAppearance packet");
        }

        int appearanceCount = ReadCount(ref reader, "item appearances", 20_000);
        var appearances = new List<ItemAppearance>(appearanceCount);
        for (int index = 0; index < appearanceCount; index++)
        {
            appearances.Add(new ItemAppearance(
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32()));
        }

        int semanticCount = ReadCount(ref reader, "shader semantic maps", 20_000);
        var semantics = new List<ShaderSemantic>(semanticCount);
        for (int index = 0; index < semanticCount; index++)
        {
            uint itemId = reader.ReadUInt32();
            int rowCount = ReadCount(ref reader, "appearance-row ids", 32);
            uint[] rowIds = new uint[rowCount];
            for (int row = 0; row < rowCount; row++)
            {
                rowIds[row] = reader.ReadUInt32();
            }

            semantics.Add(new ShaderSemantic(itemId, rowIds));
        }

        int parameterCount = ReadCount(ref reader, "shader parameters", 250_000);
        var parameters = new List<ShaderParameter>(parameterCount);
        // The two parameter rows that decide whether a tintable mesh renders as a colour or as
        // bare grey. Every group carries the same 20-odd named rows; only DecalTint and TilingTint
        // name a texture that is not a shader default (docs/80 s1.4, verdict C2).
        var tints = new Dictionary<uint, AugustShaderGroupTint>();
        // The second, independent way a group can carry a colour: a BaseTint*A ramp that is not
        // the client's neutral Default (docs/69 s3.4). See CarriesBaseTint.
        var baseTinted = new HashSet<uint>();
        for (int index = 0; index < parameterCount; index++)
        {
            int start = reader.Position;
            uint groupId = reader.ReadUInt32();
            string parameterName = reader.ReadString();
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            _ = reader.ReadSingle();
            _ = reader.ReadUInt32();
            _ = reader.ReadBool();
            int textureStart = reader.Position;
            string textureName = reader.ReadString();
            int textureEnd = reader.Position;
            _ = reader.ReadUInt32();
            byte[] parameterBytes = source.AsSpan(start, reader.Position - start).ToArray();
            bool missingPistolMask = parameterName == "PatternTintMask"
                && ((groupId == 1747 && textureName == "Weapons_Magnum_PM.dds")
                    || (groupId == 1919 && textureName == "Weapons_M9_PM.dds"));
            if (missingPistolMask
                || (groupId == 1862 && parameterName == "Pattern0TintMap" && textureName == "Wood_01_PT.dds"))
            {
                // The base Magnum and M9 groups also reference absent paint masks. A black
                // mask disables pattern painting while retaining the gun's authored material.
                // Repair the reference data so first-person, hand and stow appearances agree.
                // Nautilus (4032): the captured wood pattern is absent from all August packs
                // and appears in the client's AssetFailure log during failed outfit builds.
                // Use the shipped neutral pattern already used by Pattern1TintMap. Retain
                // the octopus decal, colours and caustics effect; one unavailable texture
                // must not hold up the character's entire attachment composite.
                textureName = "black.dds";
                using var compatible = new PacketWriter();
                compatible.WriteRaw(source.AsSpan(start, textureStart - start));
                compatible.WriteString(textureName);
                compatible.WriteRaw(source.AsSpan(textureEnd, reader.Position - textureEnd));
                parameterBytes = compatible.Written.ToArray();
            }
            parameters.Add(new ShaderParameter(groupId, parameterBytes));

            if (IsNonNeutralBaseTint(parameterName, x, y, z))
            {
                baseTinted.Add(groupId);
            }

            if (parameterName is not ("DecalTint" or "TilingTint"))
            {
                continue;
            }

            tints.TryGetValue(groupId, out AugustShaderGroupTint tint);
            tints[groupId] = parameterName == "DecalTint"
                ? tint with { DecalTint = textureName }
                : tint with { TilingTint = textureName };
        }

        if (!reader.AtEnd)
        {
            throw new InvalidDataException($"source has {reader.Remaining} trailing byte(s)");
        }

        // D316 (docs/124): whole, or only what the reward catalogue reaches. The parse above is
        // the same either way - the counts, the tints and the BaseTint census read the SOURCE, so
        // a diagnostic never depends on the switch.
        bool whole = ShipWholeTable;

        HashSet<uint> wantedItems =
        [
            .. AugustSkinCatalog.Apparel.Select(entry => entry.RewardItemId),
            .. AugustSkinCatalog.Weapons.Select(entry => entry.RewardItemId),
            .. AugustSkinCatalog.Apparel.Select(entry => entry.CategoryPrototypeId),
            .. AugustSkinCatalog.Weapons.Select(entry => entry.CategoryPrototypeId),
        ];

        int correctedAppearanceCount = 0;
        int correctedShaderGroupCount = 0;
        int correctedGenderCount = 0;
        var keptAppearanceRows = new List<ItemAppearance>();
        foreach (ItemAppearance row in appearances)
        {
            if (!whole && !wantedItems.Contains(row.ItemId))
            {
                continue;
            }

            keptAppearanceRows.Add(ApplyAppearanceRowOverrides
                ? ApplyGeneratedCorrection(
                    row,
                    ref correctedAppearanceCount,
                    ref correctedShaderGroupCount,
                    ref correctedGenderCount)
                : RepairHoodedApparelGender(
                    RepairCrownBackpack(row, ref correctedAppearanceCount, ref correctedShaderGroupCount),
                    ref correctedAppearanceCount, ref correctedGenderCount));
        }

        ItemAppearance[] keptAppearances = [.. keptAppearanceRows];

        if (ApplyAppearanceRowOverrides
            && correctedAppearanceCount != AugustSkinCatalog.AppearanceRowOverrides.Count)
        {
            throw new InvalidDataException(
                $"applied {correctedAppearanceCount:N0} of "
                + $"{AugustSkinCatalog.AppearanceRowOverrides.Count:N0} generated appearance "
                + "row corrections");
        }

        HashSet<uint> keptRowIds = [.. keptAppearances.Select(row => row.RowId)];
        HashSet<uint> keptGroups =
        [.. keptAppearances.Select(row => row.ShaderParameterGroupId).Where(id => id != 0)];
        // Keep the previous group's definitions in filtered mode too. This isolated colour
        // repair must not change the reference table's shape or remove unrelated shader data.
        if (!ApplyAppearanceRowOverrides && correctedAppearanceCount != 0)
            keptGroups.Add(3264);

        ShaderSemantic[] keptSemantics =
        [
            .. semantics
                .Where(row => whole || wantedItems.Contains(row.ItemId))
                .Select(row => new ShaderSemantic(
                    row.ItemId,
                    [.. row.AppearanceRowIds.Where(keptRowIds.Contains)]))
                .Where(row => row.AppearanceRowIds.Length > 0),
        ];
        // Whole means whole: every parameter of every group, including the 614 groups no retained
        // row names. Z1 ships them and the client takes them; dropping them here would be exactly
        // the "optimisation" that left a worn item pointing at a group with nothing behind it.
        ShaderParameter[] keptParameters = whole
            ? [.. parameters]
            : [.. parameters.Where(row => keptGroups.Contains(row.GroupId))];
        HashSet<uint> definedGroups = [.. keptParameters.Select(row => row.GroupId)];
        uint[] undefinedGroups = [.. keptGroups.Where(group => !definedGroups.Contains(group))];
        if (undefinedGroups.Length > 0)
        {
            throw new InvalidDataException(
                $"{undefinedGroups.Length:N0} retained shader group(s) have no parameter rows: "
                + string.Join(",", undefinedGroups.Take(20)));
        }

        byte[] starterPayload = DynamicAppearanceReference.CreateStarterAppearance().Payload;
        var starterReader = new PacketReader(starterPayload);
        if (starterReader.ReadInt32() != 0 || starterReader.ReadInt32() != 0)
        {
            throw new InvalidDataException("project starter appearance unexpectedly contains item rows");
        }

        int starterParameterCount = ReadCount(ref starterReader, "starter shader parameters", 128);
        byte[] starterParameters = starterReader.ReadRest().ToArray();

        // In whole mode the capture defines 662/664/665/666 itself, so the starter palette appends
        // only its own 0x435242xx groups. Filtered mode keeps writing the blob's bytes verbatim,
        // which is what makes CRANBERRY_APPEARANCE_TABLE=filtered byte-identical to what shipped.
        DynamicAppearanceShaderValue[] starterValues = [];
        int suppressedStarterValues = 0;
        if (whole)
        {
            HashSet<uint> sourceGroups = [.. keptParameters.Select(row => row.GroupId)];
            starterValues =
            [
                .. DynamicAppearanceReference.StarterValues
                    .Where(value => !sourceGroups.Contains(value.ShaderParameterGroupId)),
            ];
            suppressedStarterValues =
                DynamicAppearanceReference.StarterValues.Count - starterValues.Length;
            starterParameterCount = starterValues.Length;
        }

        // Cranberry's own rows for the thirteen wearables the wantedItems filter leaves bare
        // (docs/106 addendum 2026-09-03). Purely additive: every retained captured row, semantic
        // and parameter above is untouched, so CRANBERRY_APPEARANCE_AUTHORED_ROWS=0 reproduces
        // the previous payload to the byte.
        AuthoredAppearance authored = BuildAuthoredRows(
            keptRowIds,
            rowsByItemKeys: keptSemantics,
            dropRowsTheSourceAlreadyDresses: whole);

        using var writer = new PacketWriter(whole ? 7 * 1024 * 1024 : 3 * 1024 * 1024);
        writer.WriteInt32(keptAppearances.Length + authored.Rows.Count);
        foreach (ItemAppearance row in keptAppearances)
        {
            writer.WriteUInt32(row.RowId);
            writer.WriteUInt32(row.DataId);
            writer.WriteUInt32(row.ItemId);
            writer.WriteUInt32(row.ModelId);
            writer.WriteUInt32(row.GenderId);
            writer.WriteUInt32(row.ClientRequirementId);
            writer.WriteUInt32(row.ShaderParameterGroupId);
        }

        foreach (AugustAuthoredAppearanceRow row in authored.Rows)
        {
            writer.WriteUInt32(row.RowId);
            writer.WriteUInt32(row.RowId);           // DataId: the source's 3,551 rows all repeat
            writer.WriteUInt32(row.ItemDefinitionId); // the row id here, so an authored row does too
            writer.WriteUInt32(row.ModelId);
            writer.WriteUInt32(row.GenderId);
            writer.WriteUInt32(0);                   // ClientRequirementId: no requirement
            writer.WriteUInt32(row.ShaderParameterGroupId);
        }

        writer.WriteInt32(keptSemantics.Length + authored.Semantics.Count);
        foreach (ShaderSemantic row in keptSemantics.Concat(authored.Semantics))
        {
            writer.WriteUInt32(row.ItemId);
            writer.WriteInt32(row.AppearanceRowIds.Length);
            foreach (uint rowId in row.AppearanceRowIds)
            {
                writer.WriteUInt32(rowId);
            }
        }

        writer.WriteInt32(
            keptParameters.Length + starterParameterCount + authored.ShaderValues.Count);
        foreach (ShaderParameter parameter in keptParameters)
        {
            writer.WriteRaw(parameter.WireBytes);
        }

        if (whole)
        {
            foreach (DynamicAppearanceShaderValue value in starterValues)
            {
                value.WriteTo(writer);
            }
        }
        else
        {
            writer.WriteRaw(starterParameters);
        }

        foreach (DynamicAppearanceShaderValue value in authored.ShaderValues)
        {
            value.WriteTo(writer);
        }

        var rowsByItem = keptSemantics.Concat(authored.Semantics).ToDictionary(
            row => row.ItemId,
            row => (IReadOnlyList<uint>)row.AppearanceRowIds);

        // The census needs the rows themselves, not their ids: which model each names, which body
        // it applies to, and which shader group colours it. ~1,448 rows x 20 bytes, once at load.
        var appearanceRowsById = new Dictionary<uint, AugustAppearanceRow>(
            keptAppearances.Length + authored.Rows.Count);
        foreach (ItemAppearance row in keptAppearances)
        {
            appearanceRowsById[row.RowId] = new AugustAppearanceRow(
                row.RowId, row.ItemId, row.ModelId, row.GenderId, row.ShaderParameterGroupId);
        }

        foreach (AugustAuthoredAppearanceRow row in authored.Rows)
        {
            appearanceRowsById[row.RowId] = new AugustAppearanceRow(
                row.RowId,
                row.ItemDefinitionId,
                row.ModelId,
                row.GenderId,
                row.ShaderParameterGroupId);
        }

        foreach (DynamicAppearanceShaderValue value in starterValues.Concat(authored.ShaderValues))
        {
            definedGroups.Add(value.ShaderParameterGroupId);
            if (IsNonNeutralBaseTint(value.Semantic, value.Value.X, value.Value.Y, value.Value.Z))
            {
                baseTinted.Add(value.ShaderParameterGroupId);
            }
        }

        return new AugustDynamicAppearanceTable(
            writer.Written.ToArray(),
            rowsByItem,
            appearanceRowsById,
            definedGroups,
            tints.Where(pair => definedGroups.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value),
            baseTinted,
            keptAppearances.Length + authored.Rows.Count,
            keptSemantics.Length + authored.Semantics.Count,
            keptParameters.Length + starterParameterCount + authored.ShaderValues.Count,
            correctedAppearanceCount,
            correctedShaderGroupCount,
            correctedGenderCount,
            whole)
        {
            AuthoredRowCount = authored.Rows.Count,
            SuppressedAuthoredRowCount = authored.DroppedRowCount,
            SuppressedStarterValueCount = suppressedStarterValues,
        };
    }

    /// <summary>
    /// A <c>BaseTint*A</c> parameter that has moved off the client's neutral <c>Default</c>
    /// (<c>TintSemanticTables.txt</c> row 1: highlight 1,1,1, midtone .5,.5,.5, shadow 0,0,0).
    /// </summary>
    private static bool IsNonNeutralBaseTint(string semantic, float x, float y, float z) =>
        semantic switch
        {
            "BaseTintHighlightA" => !(Near(x, 1f) && Near(y, 1f) && Near(z, 1f)),
            "BaseTintMidtoneA" => !(Near(x, 0.5f) && Near(y, 0.5f) && Near(z, 0.5f)),
            "BaseTintShadowA" => !(Near(x, 0f) && Near(y, 0f) && Near(z, 0f)),
            _ => false,
        };

    private static bool Near(float value, float expected) => MathF.Abs(value - expected) < 1e-4f;

    /// <summary>
    /// The Cranberry-authored rows, their item maps and their shader parameters — or nothing at
    /// all when <see cref="ApplyAuthoredRows"/> is off.
    /// </summary>
    private static AuthoredAppearance BuildAuthoredRows(
        HashSet<uint> keptRowIds,
        ShaderSemantic[] rowsByItemKeys,
        bool dropRowsTheSourceAlreadyDresses)
    {
        if (!ApplyAuthoredRows || AugustAuthoredAppearance.Rows.Length == 0)
        {
            return new AuthoredAppearance([], [], [], 0);
        }

        HashSet<uint> keptItems = [.. rowsByItemKeys.Select(row => row.ItemId)];
        var byItem = new Dictionary<uint, List<uint>>();
        var rows = new List<AugustAuthoredAppearanceRow>(AugustAuthoredAppearance.Rows.Length);
        int dropped = 0;
        foreach (AugustAuthoredAppearanceRow row in AugustAuthoredAppearance.Rows)
        {
            if (keptRowIds.Contains(row.RowId))
            {
                throw new InvalidDataException(
                    $"authored appearance row {row.RowId} collides with a retained source row");
            }

            if (keptItems.Contains(row.ItemDefinitionId))
            {
                // D316: in whole mode the capture dresses all thirteen of these items itself, so
                // the authored row is the duplicate and the CAPTURE wins. In filtered mode a
                // collision is still a build error - the authored rows exist precisely because the
                // filter left those items bare, and two rows for one item would be a mistake.
                if (!dropRowsTheSourceAlreadyDresses)
                {
                    throw new InvalidDataException(
                        $"authored appearance row {row.RowId} dresses item {row.ItemDefinitionId}, "
                        + "which the retained source table already covers");
                }

                dropped++;
                continue;
            }

            rows.Add(row);
            if (!byItem.TryGetValue(row.ItemDefinitionId, out List<uint>? ids))
            {
                ids = [];
                byItem[row.ItemDefinitionId] = ids;
            }

            ids.Add(row.RowId);
        }

        // Ascending row id per item, which is the order the client's own selector walks
        // (FUN_140c476f0, a std::set in-order traversal - see ShaderGroupFor).
        ShaderSemantic[] semantics =
        [
            .. byItem
                .OrderBy(pair => pair.Key)
                .Select(pair => new ShaderSemantic(pair.Key, [.. pair.Value.Order()])),
        ];

        // A dropped row's colourway would otherwise ship with nothing pointing at it.
        HashSet<uint> survivingGroups = [.. rows.Select(row => row.ShaderParameterGroupId)];
        IReadOnlyList<DynamicAppearanceShaderValue> shaderValues = dropped == 0
            ? AugustAuthoredAppearance.ShaderValues
            : [.. AugustAuthoredAppearance.ShaderValues
                .Where(value => survivingGroups.Contains(value.ShaderParameterGroupId))];

        return new AuthoredAppearance(rows, semantics, shaderValues, dropped);
    }

    private sealed record AuthoredAppearance(
        IReadOnlyList<AugustAuthoredAppearanceRow> Rows,
        IReadOnlyList<ShaderSemantic> Semantics,
        IReadOnlyList<DynamicAppearanceShaderValue> ShaderValues,
        int DroppedRowCount);

    // The friend capture's male Crown/Showdown backpack points at group 3264 (yellow).
    // The independent 2017 table and the captured female row 337 both use group 252.
    // Repair only this verified field; group 252 is already present, so no rows or values
    // are added and the general, historically unsafe override switch remains off.
    private static ItemAppearance RepairCrownBackpack(
        ItemAppearance row, ref int correctedAppearances, ref int correctedGroups)
    {
        if (row.RowId != 336 || row.ItemId != 2778 || row.ModelId != 9622
            || row.GenderId != 1 || row.ShaderParameterGroupId != 3264)
            return row;

        correctedAppearances++;
        correctedGroups++;
        return row with { ShaderParameterGroupId = 252 };
    }

    // August Models.txt: gendered hood-up/down hoodie and M65 parka meshes.
    // The captured table marks both as gender-0 wildcards. The native selector
    // 140c476f0 sorts IDs, so sending the female ID first cannot prevent the male
    // row winning. Correct the eligibility in ReferenceData itself: this also
    // covers the menu skin manager, which builds attachments independently.
    // Only the gender word changes; shaders, requirements, IDs and size stay intact.
    private static ItemAppearance RepairHoodedApparelGender(
        ItemAppearance row, ref int correctedAppearances, ref int correctedGenders)
    {
        uint gender = row.ModelId switch
        {
            9653 or 9698 or 9742 => CharacterVisuals.Female,
            9654 or 9697 or 9741 => CharacterVisuals.Male,
            _ => 0,
        };
        if (row.GenderId != 0 || gender == 0) return row;
        correctedAppearances++;
        correctedGenders++;
        return row with { GenderId = gender };
    }

    private static ItemAppearance ApplyGeneratedCorrection(
        ItemAppearance row,
        ref int correctedAppearanceCount,
        ref int correctedShaderGroupCount,
        ref int correctedGenderCount)
    {
        if (!AugustSkinCatalog.AppearanceRowOverrides.TryGetValue(
                row.RowId, out AugustAppearanceRowOverride correction))
        {
            return row;
        }

        if (correction.ItemDefinitionId != row.ItemId)
        {
            throw new InvalidDataException(
                $"appearance correction row {row.RowId} expects item "
                + $"{correction.ItemDefinitionId}, source contains {row.ItemId}");
        }

        bool shaderChanged = correction.ShaderParameterGroupId != row.ShaderParameterGroupId;
        bool genderChanged = correction.GenderId != row.GenderId;
        if (!shaderChanged && !genderChanged)
        {
            throw new InvalidDataException(
                $"appearance correction row {row.RowId} changes no source field");
        }

        correctedAppearanceCount++;
        correctedShaderGroupCount += shaderChanged ? 1 : 0;
        correctedGenderCount += genderChanged ? 1 : 0;
        return row with
        {
            GenderId = correction.GenderId,
            ShaderParameterGroupId = correction.ShaderParameterGroupId,
        };
    }

    private static int ReadCount(ref PacketReader reader, string label, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException($"{label} count {count} is outside 0..{maximum}");
        }

        return count;
    }

    private sealed record ItemAppearance(
        uint RowId,
        uint DataId,
        uint ItemId,
        uint ModelId,
        uint GenderId,
        uint ClientRequirementId,
        uint ShaderParameterGroupId);

    private sealed record ShaderSemantic(uint ItemId, uint[] AppearanceRowIds);
    private sealed record ShaderParameter(uint GroupId, byte[] WireBytes);
}
