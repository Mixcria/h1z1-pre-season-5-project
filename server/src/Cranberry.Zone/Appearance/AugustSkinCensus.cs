namespace Cranberry.Zone.Appearance;

/// <summary>
/// What this server will do with one item on one body.
/// </summary>
/// <param name="ItemDefinitionId">The item.</param>
/// <param name="Gender"><see cref="CharacterVisuals.Male"/> or <see cref="CharacterVisuals.Female"/>.</param>
/// <param name="MeshWeSend">The <c>.adr</c> this server puts in the attachment.</param>
/// <param name="RowId">The appearance row the client will pick, or 0 when it will pick none.</param>
/// <param name="MeshTheRowNames">The <c>.adr</c> that row's <c>MODEL_ID</c> resolves to.</param>
/// <param name="ShaderParameterGroupId">That row's colourway.</param>
/// <param name="Verdict">One of <see cref="AugustSkinCensus"/>'s C1-C4, or <see cref="AugustSkinVerdict.Fine"/>.</param>
/// <param name="Problem">A sentence naming the defect, empty when there is none.</param>
public readonly record struct AugustSkinCensusRow(
    uint ItemDefinitionId,
    uint Gender,
    string MeshWeSend,
    uint RowId,
    string MeshTheRowNames,
    uint ShaderParameterGroupId,
    AugustSkinVerdict Verdict,
    string Problem);

/// <summary>The four ways an item can be wrong before anyone has clicked anything.</summary>
public enum AugustSkinVerdict
{
    /// <summary>Nothing predicted wrong.</summary>
    Fine = 0,

    /// <summary>C1: the row the client picks names the other body's mesh.</summary>
    WrongBodyMesh,

    /// <summary>C2: a tintable mesh whose group composites only untinted defaults - it renders grey.</summary>
    RendersGrey,

    /// <summary>C3: the item has appearance rows and not one of them applies to this body.</summary>
    NoRowForThisBody,

    /// <summary>C4: the mesh this server would send is not in the client's asset packs.</summary>
    MeshDoesNotShip,
}

/// <summary>
/// <b>The grey census</b> (D53, docs/80 edit 7; ported in shape from the owner's own
/// <c>ZoneSkinCensus.cs</c>, which he wrote after three rounds of answering "why is it grey" one
/// half at a time).
/// <para>
/// For every id a player can end up wearing, on both bodies, resolve the appearance row the client
/// will actually pick and say what will be wrong with it. It changes <b>no byte</b> - it is a log
/// writer and nothing else - and everything it reports has previously cost a click session to
/// find. Cranberry hard-coded <c>ShaderParameterGroupId: 0</c> for three waves and only found out
/// what that looked like when the owner said the shotgun was white.
/// </para>
/// <para>
/// Native August selector 140c476f0 walks appearance IDs in ascending order and accepts
/// the first wildcard or matching body. The older wildcard-first diagnostic was incorrect;
/// see docs/106 section 3.2 and docs/hoodie-body-20260906.md.
/// </para>
/// </summary>
public static class AugustSkinCensus
{
    /// <summary>Detail lines printed before the summary tail, as in the owner's own census.</summary>
    public const int MaximumDetailLines = 40;

    /// <summary>
    /// Resolve one item against one body exactly the way the client resolves it.
    /// </summary>
    /// <param name="table">The table this server is about to transmit, or <c>null</c> for the starter palette.</param>
    /// <param name="itemDefinitionId">The item.</param>
    /// <param name="gender">The body.</param>
    /// <param name="meshWeSend">The mesh this server would attach, already gender-resolved.</param>
    /// <param name="orderRowsByGender">D88's wire ordering; inert for the verdict (docs/106 §3.2).</param>
    /// <param name="crossGenderShaderGroup">
    /// <b>D323 (docs/106 §13).</b> Whether the attachment this server sends carries D226's
    /// cross-body group when no row applies to this body - <c>SkinOptions.CrossGenderShaderGroup</c>.
    /// The client's draw (<c>FUN_140c70d60</c>) takes the packet's own model name and group through
    /// the SAME virtual, in the same argument positions, when its selector returns no row, so an
    /// item with rows only for the other body is coloured by that group exactly as a resolving row
    /// would colour it. With this on, item 10 on a male body is <see cref="AugustSkinVerdict.Fine"/>
    /// with a note; with it off, it is <see cref="AugustSkinVerdict.NoRowForThisBody"/>, which is
    /// what the wire carried before D226.
    /// </param>
    public static AugustSkinCensusRow Resolve(
        AugustDynamicAppearanceTable? table,
        uint itemDefinitionId,
        uint gender,
        string meshWeSend,
        bool orderRowsByGender = true,
        bool crossGenderShaderGroup = true) =>
        Resolve(
            table?.RowsForItem(itemDefinitionId, orderRowsByGender ? gender : 0) ?? [],
            table,
            itemDefinitionId,
            gender,
            meshWeSend,
            crossGenderShaderGroup && table is not null
                ? table.ShaderGroupForAnyBody(itemDefinitionId)
                : 0);

    /// <summary>
    /// The same resolution against rows supplied directly, which is the seam the tests use: there
    /// is no way to build an <see cref="AugustDynamicAppearanceTable"/> from hand-written rows
    /// (it exists only as the filtered form of a 6.6 MB source), and the row-selection rule is the
    /// one thing in this file that is a measurement rather than a mechanism.
    /// </summary>
    /// <param name="rows">The rows the attachment will carry, in wire order.</param>
    /// <param name="table">The transmitted table, for the group's parameters; null for the starter palette.</param>
    /// <param name="itemDefinitionId">The item.</param>
    /// <param name="gender">The body.</param>
    /// <param name="meshWeSend">The mesh this server would attach, already gender-resolved.</param>
    /// <param name="packetShaderGroup">
    /// The <c>ShaderParameterGroupId</c> the attachment itself will carry when no row resolves -
    /// D226's cross-body group - or 0 when the packet carries none. Only read on the no-row path.
    /// </param>
    public static AugustSkinCensusRow Resolve(
        IReadOnlyList<AugustAppearanceRow> rows,
        AugustDynamicAppearanceTable? table,
        uint itemDefinitionId,
        uint gender,
        string meshWeSend,
        uint packetShaderGroup = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // C4 first: a mesh the client cannot load makes every other verdict moot, and it is the
        // one failure that produces no error the player can see - just an absent attachment.
        if (!AugustAssetIndex.Ships(meshWeSend))
        {
            return new AugustSkinCensusRow(
                itemDefinitionId, gender, meshWeSend, 0, string.Empty, 0,
                AugustSkinVerdict.MeshDoesNotShip,
                $"\"{meshWeSend}\" is in no Assets_*.pack - the client answers "
                    + "\"Failed to load asset\" in its own AssetFailure.log and draws nothing");
        }

        bool tintable = IsTintable(meshWeSend);

        if (rows.Count == 0)
        {
            return new AugustSkinCensusRow(
                itemDefinitionId, gender, meshWeSend, 0, string.Empty, 0,
                tintable ? AugustSkinVerdict.RendersGrey : AugustSkinVerdict.Fine,
                tintable
                    ? "no appearance row at all, and the mesh is tintable - it composites "
                        + "white.dds / grey.dds, i.e. GREY"
                    : string.Empty);
        }

        if (PickRow(rows, gender) is not AugustAppearanceRow pick)
        {
            // D323: no row for this body is not the end of the story. FUN_140c70d60's tail draws
            // the packet's OWN model name with the packet's OWN group through the same virtual
            // (+0x4b8) a resolving row goes through, so when this server puts D226's cross-body
            // group in the attachment (ZoneService.WornSkinShaderGroupResolver /
            // BuildActiveHandAttachment) the item is coloured exactly as its other-body row would
            // have coloured it. Item 10, the AR-15, is that case on every male body: rows 181/182
            // are both GENDER_ID 2, and the attachment carries group 168 - which is what
            // captures\wire-20260903-194804.txt:23968 (slot 77 grp 168 app [181,182]) holds.
            if (packetShaderGroup != 0
                && table is not null
                && table.DefinesShaderGroup(packetShaderGroup))
            {
                AugustShaderGroupTint fallbackTint = table.TintOfGroup(packetShaderGroup);
                bool fallbackColoured = !fallbackTint.RendersUntinted
                    || table.CarriesBaseTint(packetShaderGroup);
                if (tintable && !fallbackColoured)
                {
                    return new AugustSkinCensusRow(
                        itemDefinitionId, gender, meshWeSend, 0, string.Empty, packetShaderGroup,
                        AugustSkinVerdict.RendersGrey,
                        $"no row applies to a {NameOfGender(gender)} body and the packet's own group "
                            + $"{packetShaderGroup} carries no tint - a tintable mesh renders GREY");
                }

                return new AugustSkinCensusRow(
                    itemDefinitionId, gender, meshWeSend, 0, string.Empty, packetShaderGroup,
                    AugustSkinVerdict.Fine,
                    $"no row applies to a {NameOfGender(gender)} body; the client draws the packet's "
                        + $"own mesh with the packet's own group {packetShaderGroup} (D226, D323)");
            }

            return new AugustSkinCensusRow(
                itemDefinitionId, gender, meshWeSend, 0, string.Empty, 0,
                AugustSkinVerdict.NoRowForThisBody,
                $"the item has {rows.Count} appearance row(s) and NOT ONE of them applies to a "
                    + $"{NameOfGender(gender)} body - the client applies no shader group at all "
                    + "and the mesh composites untinted");
        }

        string rowMesh = AugustModelCatalog.TryGet(pick.ModelId, out AugustModelRow model)
            ? model.FileName
            : string.Empty;

        uint bakedGender = BakedGenderOf(rowMesh);
        if (bakedGender != 0 && bakedGender != gender)
        {
            return new AugustSkinCensusRow(
                itemDefinitionId, gender, meshWeSend, pick.RowId, rowMesh,
                pick.ShaderParameterGroupId, AugustSkinVerdict.WrongBodyMesh,
                $"the row the client picks names \"{rowMesh}\", which is the "
                    + $"{NameOfGender(bakedGender)} body's mesh - it will NOT cover a "
                    + $"{NameOfGender(gender)} character");
        }

        AugustShaderGroupTint tint = table?.TintOfGroup(pick.ShaderParameterGroupId) ?? default;
        bool defined = table?.DefinesShaderGroup(pick.ShaderParameterGroupId) ?? false;

        // Two independent ways to be coloured, and the second was missing until 2026-09-03: a
        // pattern composited over the albedo (DecalTint / TilingTint, which is how every colourway
        // in the owner's Z1 table works and is what C2 was ported to look for), or the albedo
        // itself remapped (BaseTint*A off the client's neutral Default - docs/69 s3.4, and how
        // both Cranberry's starter palette and the authored loot rows colour a mesh).
        bool baseTinted = defined
            && table is not null
            && table.CarriesBaseTint(pick.ShaderParameterGroupId);
        if (tintable && !(defined && (!tint.RendersUntinted || baseTinted)))
        {
            return new AugustSkinCensusRow(
                itemDefinitionId, gender, meshWeSend, pick.RowId, rowMesh,
                pick.ShaderParameterGroupId, AugustSkinVerdict.RendersGrey,
                defined
                    ? $"shader group {pick.ShaderParameterGroupId} carries "
                        + $"DecalTint=\"{tint.DecalTint}\" TilingTint=\"{tint.TilingTint}\" and no "
                        + "BaseTint ramp off the client's neutral Default - all three untinted, so "
                        + "a tintable mesh renders GREY"
                    : $"shader group {pick.ShaderParameterGroupId} has no parameter rows in the "
                        + "transmitted table, so a tintable mesh renders GREY");
        }

        return new AugustSkinCensusRow(
            itemDefinitionId, gender, meshWeSend, pick.RowId, rowMesh,
            pick.ShaderParameterGroupId, AugustSkinVerdict.Fine, string.Empty);
    }

    /// <summary>
    /// The row the client will pick for this body, or <c>null</c> when none applies.
    /// <para>
    /// Native 140c476f0 walks a set in ascending ID order and accepts the first wildcard
    /// or matching gender. Packet order does not affect the winner (docs/106 section 3.2).
    /// </para>
    /// </summary>
    public static AugustAppearanceRow? PickRow(IReadOnlyList<AugustAppearanceRow> rows, uint gender)
    {
        ArgumentNullException.ThrowIfNull(rows);

        AugustAppearanceRow? picked = null;
        foreach (AugustAppearanceRow row in rows)
        {
            if ((row.GenderId == 0 || row.GenderId == gender)
                && (picked is null || row.RowId < picked.Value.RowId))
            {
                picked = row;
            }
        }
        return picked;
    }

    /// <summary>
    /// Walk every worn roster mesh and every wardrobe/weapon catalogue row, on both bodies, and
    /// return what will be wrong. Pure: no logging, no wire, no disk.
    /// </summary>
    public static (int Checked, IReadOnlyList<AugustSkinCensusRow> Problems) Run(
        AugustDynamicAppearanceTable? table,
        bool orderRowsByGender = true,
        bool crossGenderShaderGroup = true)
    {
        var problems = new List<AugustSkinCensusRow>();
        int considered = 0;

        foreach ((uint item, string male, string female) in Population())
        {
            foreach ((uint gender, string mesh) in
                (ReadOnlySpan<(uint, string)>)[(CharacterVisuals.Male, male), (CharacterVisuals.Female, female)])
            {
                if (mesh.Length == 0)
                {
                    continue;
                }

                considered++;
                AugustSkinCensusRow row = Resolve(
                    table, item, gender, mesh, orderRowsByGender, crossGenderShaderGroup);
                if (row.Verdict != AugustSkinVerdict.Fine)
                {
                    problems.Add(row);
                }
            }
        }

        return (considered, problems);
    }

    /// <summary>
    /// The census as the owner reads it at boot: one summary line, at most
    /// <see cref="MaximumDetailLines"/> detail lines, and a tail of the remaining ids.
    /// </summary>
    public static IReadOnlyList<string> Describe(
        AugustDynamicAppearanceTable? table,
        bool orderRowsByGender = true,
        bool crossGenderShaderGroup = true)
    {
        (int considered, IReadOnlyList<AugustSkinCensusRow> problems) =
            Run(table, orderRowsByGender, crossGenderShaderGroup);
        var lines = new List<string>(Math.Min(problems.Count, MaximumDetailLines) + 2)
        {
            $"skin census: {considered:N0} (item, body) pair(s) resolved through the transmitted "
                + $"appearance table exactly as the client resolves them - {problems.Count:N0} "
                + "will not render correctly"
                + (table is null ? " (starter palette: no appearance table loaded)" : string.Empty),
        };

        if (problems.Count == 0)
        {
            lines.Add(
                "skin census: every wearable picks a row of its own body's gender and a shader "
                    + "group with a real DecalTint/TilingTint. Nothing is predicted grey.");
            return lines;
        }

        foreach (AugustSkinCensusRow row in problems.Take(MaximumDetailLines))
        {
            lines.Add(
                $"skin census {row.Verdict} item {row.ItemDefinitionId} on a "
                    + $"{NameOfGender(row.Gender)} body: we send \"{row.MeshWeSend}\", row "
                    + $"{row.RowId} group {row.ShaderParameterGroupId} - {row.Problem}");
        }

        if (problems.Count > MaximumDetailLines)
        {
            lines.Add(
                $"skin census: {problems.Count - MaximumDetailLines:N0} more, same shapes - "
                    + string.Join(
                        ',',
                        problems.Skip(MaximumDetailLines)
                            .Select(row => row.ItemDefinitionId)
                            .Distinct()
                            .Take(60)));
        }

        return lines;
    }

    /// <summary>
    /// Every (item, male mesh, female mesh) the census walks: the worn roster meshes this server
    /// attaches on a pickup, plus every apparel and weapon row a wardrobe click can select.
    /// </summary>
    private static IEnumerable<(uint Item, string Male, string Female)> Population()
    {
        var seen = new HashSet<(uint, string)>();
        foreach (AugustWornMesh mesh in AugustWornMeshCatalog.Entries)
        {
            if (seen.Add((mesh.ItemDefinitionId, mesh.MaleModelName)))
            {
                yield return (mesh.ItemDefinitionId, mesh.MaleModelName, mesh.FemaleModelName);
            }
        }

        foreach (AugustSkinCatalogEntry entry in
            AugustSkinCatalog.Apparel.Concat(AugustSkinCatalog.Weapons))
        {
            if (entry.MaleModelName.Length != 0
                && seen.Add((entry.RewardItemId, entry.MaleModelName)))
            {
                yield return (entry.RewardItemId, entry.MaleModelName, entry.FemaleModelName);
            }
        }
    }

    /// <summary>
    /// The body a mesh file name is baked for, or 0 when the name says nothing. This is the
    /// August spelling convention and it is exhaustive across the packs: an apparel mesh is
    /// <c>SurvivorMale_...</c> or <c>SurvivorFemale_...</c>, and a weapon mesh is neither.
    /// </summary>
    public static uint BakedGenderOf(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            return 0;
        }

        // Female first: "SurvivorFemale" contains neither "SurvivorMale" nor a trap, but ordering
        // it first makes the intent obvious to the next reader.
        if (modelName.StartsWith("SurvivorFemale", StringComparison.OrdinalIgnoreCase))
        {
            return CharacterVisuals.Female;
        }

        return modelName.StartsWith("SurvivorMale", StringComparison.OrdinalIgnoreCase)
            ? CharacterVisuals.Male
            : 0;
    }

    /// <summary>
    /// Whether a mesh composites a tint at all.
    /// <para>
    /// The owner census asks only whether the name contains <c>Tint</c>. That is right for
    /// <c>..._Tintable.adr</c> and wrong for <c>SurvivorMale_Legs_Pants_LeggingsNoTint.adr</c>,
    /// whose name says the opposite in the same three letters - and the August catalogue has that
    /// exact file, which was the census one false positive on its first run. So the negation is
    /// checked first.
    /// </para>
    /// </summary>
    public static bool IsTintable(string? modelName) =>
        !string.IsNullOrEmpty(modelName)
        && (IsWeaponMesh(modelName)
            || (modelName.Contains("Tint", StringComparison.OrdinalIgnoreCase)
                && !modelName.Contains("NoTint", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A third-person weapon mesh, by the August spelling convention: <c>Weapon_*.adr</c> or
    /// <c>Weapons_*.adr</c>, which is every gun, bow and blade in
    /// <c>AugustWornMeshCatalog</c> and nothing else.
    /// <para>
    /// <b>Why this makes a weapon "tintable" for the census</b> (docs/106 §12, 2026-09-03).
    /// The census asked only whether the file name contains <c>Tint</c>, and NO weapon mesh is
    /// named <c>*_Tintable.adr</c> - so verdict <c>RendersGrey</c> could never fire for a gun and
    /// the census structurally could not report a white weapon. It is a colour map that decides
    /// this, not a file name: docs/69 measured <c>Weapons_AK47_3P_C.dds</c> at <b>30.4 %</b> and
    /// <c>Weapons_PumpShotgun01_C_3P.dds</c> at <b>30.1 %</b> near-white, ranks 1 and 2 of the
    /// thirty ground-loot maps, which is what a luminance mask meant to be tinted looks like. A
    /// weapon whose shader group carries no colour is exactly as grey as a <c>_Tintable</c> shirt,
    /// and the owner's white AK-47 and white pump shotgun are the proof.
    /// </para>
    /// </summary>
    public static bool IsWeaponMesh(string? modelName) =>
        !string.IsNullOrEmpty(modelName)
        && modelName.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase);

    private static string NameOfGender(uint gender) =>
        gender == CharacterVisuals.Female ? "female" : "male";
}
