using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// docs/106 addendum (2026-09-03) — the thirteen ground-loot wearables the boot census reports as
/// GREY get Cranberry's <b>own</b> appearance rows.
/// <para>
/// The defect the census printed at every boot, twice per item, once per body
/// (<c>logs/host-20260902-212957.log:18-42</c>):
/// <i>"no appearance row at all, and the mesh is tintable - it composites white.dds / grey.dds,
/// i.e. GREY"</i>. None of the four Mansport backpacks, six <c>TintTshirt</c> shirts, two
/// motorcycle helmets or the Conveys sneakers is an <see cref="AugustSkinCatalog"/> reward, so the
/// filter's <c>wantedItems</c> discards every row the D22-gated source holds for them and the
/// client is handed a tintable mesh with nothing to tint it. They are loot, so they can only ever
/// be seen in Z2 — which is the half of the owner's "grey in the real world" report docs/106's
/// three send-site fixes did not touch.
/// </para>
/// <para>
/// What this file pins: the rows are added, they are added <b>byte for byte</b> as
/// <see cref="AugustAuthoredAppearance"/> declares them, every captured byte of the payload is
/// untouched, the switch reproduces the old payload exactly, and every item the census named now
/// resolves a row of its own body's gender with a colourway. Send-side only (docs/32's evidence
/// standard): nothing here is LIVE-VERIFIED.
/// </para>
/// </summary>
[Collection(AppearanceStaticsCollection.Name)]
public sealed class AuthoredAppearanceTests
{
    /// <summary>The MD5-gated source (D22). Assertions that need it are skipped when it is absent.</summary>
    private const string Source = @"C:\Z1\Server\Data\dynamicAppearanceFriend.bin";

    /// <summary>
    /// The thirteen ids, transcribed from the boot census of the last session before this lane
    /// (<c>C:\Aug2017\logs\host-20260902-212957.log:18-42</c>, verdict <c>RendersGrey</c>, reason
    /// "no appearance row at all"). Item 10 (AR-15) is also in that census but with verdict
    /// <c>NoRowForThisBody</c> — a different defect, and not this lane's.
    /// </summary>
    private static readonly uint[] CensusGreyItems =
        [2112, 2113, 2115, 2117, 2127, 2137, 2139, 2141, 2142, 2145, 2168, 2171, 2218];

    /// <summary>The payload before this lane: the table both sessions that entered Z2 carried.</summary>
    private const int CapturedOnlyPayloadBytes = 1_268_806;

    /// <summary>
    /// 26 rows x 28 B + 13 item maps x 16 B + 78 shader values. A value is 4 (group) + 4 + n
    /// (semantic) + 16 (float4) + 4 (reserved) + 1 (bool) + 4 + 0 (empty texture) + 4 (type id);
    /// the six semantic names are 98 characters per group.
    /// </summary>
    private const int AuthoredPayloadBytes = (26 * 28) + (13 * 16) + (78 * 37) + (13 * 98);

    // -------------------------------------------------------------------------------------
    // the catalogue itself - no source file needed
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Default ON, and the switch is the environment variable. The whole point of the lane is that
    /// the owner sees a coloured backpack without setting anything;
    /// <c>CRANBERRY_APPEARANCE_AUTHORED_ROWS=0</c> is the rollback — so this asserts the mapping
    /// rather than the literal <c>true</c>, which would make the suite fail on a machine that has
    /// the rollback set.
    /// </summary>
    [Fact]
    public void AuthoredRowsAreOnUnlessTheEnvironmentTurnsThemOff()
    {
        string? value = Environment.GetEnvironmentVariable("CRANBERRY_APPEARANCE_AUTHORED_ROWS");
        bool expected = value is null
            || !(value.Equals("0", StringComparison.Ordinal)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, AugustDynamicAppearanceTable.ApplyAuthoredRows);
        if (value is null)
        {
            Assert.True(AugustDynamicAppearanceTable.ApplyAuthoredRows);
        }
    }

    [Fact]
    public void TheCatalogueCoversExactlyTheThirteenItemsTheCensusNames()
    {
        Assert.Equal<IEnumerable<uint>>(CensusGreyItems, AugustAuthoredAppearance.Items);
        Assert.Equal(2 * CensusGreyItems.Length, AugustAuthoredAppearance.Rows.Length);
        Assert.Equal(6 * CensusGreyItems.Length, AugustAuthoredAppearance.ShaderValues.Length);
    }

    /// <summary>
    /// Every authored row names a mesh the August client actually ships, for the body the row
    /// claims, and it is <b>the same mesh this server already attaches</b> for that item — so the
    /// row can only ever recolour the pickup, never re-model it (D84's rule, docs/80 edit 3).
    /// </summary>
    [Fact]
    public void EveryAuthoredRowNamesTheMeshThisServerAlreadySends()
    {
        foreach (AugustAuthoredAppearanceRow row in AugustAuthoredAppearance.Rows)
        {
            Assert.Contains(row.ItemDefinitionId, AugustAuthoredAppearance.Items);
            Assert.True(
                row.GenderId is CharacterVisuals.Male or CharacterVisuals.Female,
                $"row {row.RowId} gender {row.GenderId}");

            Assert.True(
                AugustModelCatalog.TryGet(row.ModelId, out AugustModelRow model),
                $"row {row.RowId} model {row.ModelId} is in no August Models.txt row");
            Assert.True(model.Ships, $"{model.FileName} is in no Assets_*.pack");
            Assert.Equal(row.GenderId, AugustSkinCensus.BakedGenderOf(model.FileName));

            Assert.True(
                AugustWornMeshCatalog.TryGet(row.ItemDefinitionId, out AugustWornMesh worn),
                $"item {row.ItemDefinitionId} has no worn mesh");
            Assert.Equal(
                row.GenderId == CharacterVisuals.Male ? worn.MaleModelId : worn.FemaleModelId,
                row.ModelId);
        }
    }

    /// <summary>
    /// Row ids and group ids are Cranberry's own 0x4352xxxx space. The captured source's largest
    /// row id is 5,857 and its largest shader group is 3,716, so a collision is arithmetically
    /// impossible — and <see cref="AugustDynamicAppearanceTable"/> throws rather than shipping one
    /// if that ever stops being true.
    /// </summary>
    [Fact]
    public void AuthoredIdsAreUniqueAndOutOfTheCapturedRange()
    {
        Assert.Equal(
            AugustAuthoredAppearance.Rows.Length,
            AugustAuthoredAppearance.Rows.Select(row => row.RowId).Distinct().Count());
        Assert.All(AugustAuthoredAppearance.Rows, row =>
        {
            Assert.True(row.RowId >= 0x4352_0000, $"row id {row.RowId:X8}");
            Assert.True(row.ShaderParameterGroupId >= 0x4352_0000,
                $"group {row.ShaderParameterGroupId:X8}");
        });

        // One colourway per item, shared by both bodies, and never shared between items.
        Dictionary<uint, uint> groupByItem = [];
        foreach (AugustAuthoredAppearanceRow row in AugustAuthoredAppearance.Rows)
        {
            if (groupByItem.TryGetValue(row.ItemDefinitionId, out uint group))
            {
                Assert.Equal(group, row.ShaderParameterGroupId);
            }
            else
            {
                groupByItem[row.ItemDefinitionId] = row.ShaderParameterGroupId;
            }
        }

        Assert.Equal(
            CensusGreyItems.Length,
            groupByItem.Values.Distinct().Count());
    }

    /// <summary>
    /// Every authored group carries the six <c>BaseTint</c> parameters, and its A branch has
    /// actually moved off the client's neutral <c>Default</c> — highlight 1,1,1, midtone
    /// .5,.5,.5, shadow 0,0,0 (docs/69 s3.4). A group that had not moved would be a group that
    /// still renders the mesh grey, which is the defect, not the fix.
    /// </summary>
    [Fact]
    public void EveryAuthoredGroupCarriesARampThatIsNotTheClientsNeutralDefault()
    {
        IEnumerable<IGrouping<uint, DynamicAppearanceShaderValue>> groups =
            AugustAuthoredAppearance.ShaderValues.GroupBy(v => v.ShaderParameterGroupId);

        foreach (IGrouping<uint, DynamicAppearanceShaderValue> group in groups)
        {
            Assert.Equal(
                [
                    "BaseTintHighlightA", "BaseTintMidtoneA", "BaseTintShadowA",
                    "BaseTintHighlightB", "BaseTintMidtoneB", "BaseTintShadowB",
                ],
                group.Select(value => value.Semantic));

            DynamicAppearanceShaderValue highlight = group.First();
            DynamicAppearanceShaderValue midtone = group.ElementAt(1);
            DynamicAppearanceShaderValue shadow = group.ElementAt(2);
            Assert.True(
                highlight.Value.X < 1f || highlight.Value.Y < 1f || highlight.Value.Z < 1f
                    || midtone.Value != new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 0.5f)
                    || shadow.Value.X > 0f || shadow.Value.Y > 0f || shadow.Value.Z > 0f,
                $"group {group.Key:X8} is the neutral Default");

            // The ramp is monotone: shadow <= midtone <= highlight in every channel. It is derived
            // from ordered luminance percentiles, so an inversion means the generator is wrong.
            Assert.True(shadow.Value.X <= midtone.Value.X + 1e-4f, $"group {group.Key:X8} R");
            Assert.True(midtone.Value.X <= highlight.Value.X + 1e-4f, $"group {group.Key:X8} R");
            Assert.True(shadow.Value.Y <= midtone.Value.Y + 1e-4f, $"group {group.Key:X8} G");
            Assert.True(midtone.Value.Y <= highlight.Value.Y + 1e-4f, $"group {group.Key:X8} G");
            Assert.True(shadow.Value.Z <= midtone.Value.Z + 1e-4f, $"group {group.Key:X8} B");
            Assert.True(midtone.Value.Z <= highlight.Value.Z + 1e-4f, $"group {group.Key:X8} B");
        }
    }

    // -------------------------------------------------------------------------------------
    // against the real table
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The byte-exact test.</b> The payload with the authored rows is the payload without them,
    /// with three blocks inserted at the tail of each of the three sections and three counts
    /// bumped — and nothing else. Each inserted block is compared with the bytes
    /// <see cref="AugustAuthoredAppearance"/> declares, written the way the wire writes them.
    /// </summary>
    [Fact]
    public void TheAuthoredBlocksAreAddedByteForByteAndNothingElseMoves()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        byte[] plain = Load(authored: false, out _).CreateReferenceData().Payload;
        AugustDynamicAppearanceTable table = Load(authored: true, out string status);
        byte[] full = table.CreateReferenceData().Payload;

        Assert.Equal(CapturedOnlyPayloadBytes, plain.Length);
        Assert.Equal(CapturedOnlyPayloadBytes + AuthoredPayloadBytes, full.Length);
        Assert.Equal(26, table.AuthoredRowCount);
        Assert.Contains("26 Cranberry-authored row(s) for 13 loot wearable(s) added", status);

        Sections before = Split(plain);
        Sections after = Split(full);

        Assert.Equal(before.AppearanceCount + 26, after.AppearanceCount);
        Assert.Equal(before.SemanticCount + 13, after.SemanticCount);
        Assert.Equal(before.ParameterCount + 78, after.ParameterCount);

        // Every captured byte, unchanged and still in the same order.
        Assert.Equal(before.Appearances, after.Appearances[..before.Appearances.Length]);
        Assert.Equal(before.Semantics, after.Semantics[..before.Semantics.Length]);
        Assert.Equal(before.Parameters, after.Parameters[..before.Parameters.Length]);

        // ... and exactly the declared authored bytes after them.
        Assert.Equal(ExpectedAuthoredRows(), after.Appearances[before.Appearances.Length..]);
        Assert.Equal(ExpectedAuthoredSemantics(), after.Semantics[before.Semantics.Length..]);
        Assert.Equal(ExpectedAuthoredParameters(), after.Parameters[before.Parameters.Length..]);
    }

    /// <summary>
    /// <b>The acceptance test the lane exists for.</b> Every item the boot census named as GREY
    /// now resolves an appearance row of its own body's gender, naming its own body's mesh, with a
    /// colourway the transmitted table actually defines — so the census reports none of them.
    /// </summary>
    [Fact]
    public void EveryItemTheBootCensusNamedNowResolvesAColouredRowOnBothBodies()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load(authored: true, out _);

        foreach (uint item in CensusGreyItems)
        {
            Assert.True(
                AugustWornMeshCatalog.TryGet(item, out AugustWornMesh worn),
                $"item {item}");

            foreach ((uint gender, string mesh) in (ReadOnlySpan<(uint, string)>)
                [(CharacterVisuals.Male, worn.MaleModelName),
                 (CharacterVisuals.Female, worn.FemaleModelName)])
            {
                IReadOnlyList<AugustAppearanceRow> rows = table.RowsForItem(item, gender);
                Assert.NotEmpty(rows);

                AugustAppearanceRow pick = Assert.IsType<AugustAppearanceRow>(
                    AugustSkinCensus.PickRow(rows, gender));
                Assert.Equal(gender, pick.GenderId);
                Assert.Equal(mesh, AugustModelCatalog.FileNameFor(pick.ModelId));

                // A group the table holds no parameters for is exactly what docs/54 s4 forbids.
                Assert.True(table.DefinesShaderGroup(pick.ShaderParameterGroupId));
                Assert.True(table.CarriesBaseTint(pick.ShaderParameterGroupId));
                Assert.Equal(
                    pick.ShaderParameterGroupId,
                    table.ShaderGroupFor(item, gender));

                AugustSkinCensusRow verdict = AugustSkinCensus.Resolve(table, item, gender, mesh);
                Assert.Equal(AugustSkinVerdict.Fine, verdict.Verdict);
            }
        }
    }

    /// <summary>
    /// The census as a whole: none of the thirteen appears in it any more. Item 10 on a male body
    /// — the AR-15's two rows are both <c>GENDER_ID 2</c> in the source (docs/106 s3.1) — was the
    /// one pair left until D323 read it the way the client draws it (docs/106 §13).
    /// </summary>
    [Fact]
    public void TheBootCensusNoLongerNamesAnyOfTheThirteen()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load(authored: true, out _);
        (int considered, IReadOnlyList<AugustSkinCensusRow> problems) = AugustSkinCensus.Run(table);

        Assert.True(considered > 1_000, $"the census walked only {considered} pairs");
        Assert.DoesNotContain(problems, row => CensusGreyItems.Contains(row.ItemDefinitionId));

        // 83 and 2228 are the two machetes, and they joined the census on 2026-09-03 when a weapon
        // mesh became tintable (docs/106 §12): neither has an appearance row at all, so both really
        // do composite untinted. Item 10 (the AR-15, both rows GENDER_ID 2) left the census on
        // 2026-09-04 (D323): the packet's own group 168 colours it on a male body exactly as a row
        // would, and the census now reads it the way the client draws it. Nothing else may appear.
        Assert.All(problems, row => Assert.Contains(row.ItemDefinitionId, (uint[])[83u, 2228u]));
        Assert.DoesNotContain(problems, row => row.ItemDefinitionId == 10);
    }

    /// <summary>
    /// The rollback is exact: with the switch off the payload is the one both sessions that
    /// entered Z2 carried, to the byte, and the boot census says what it said before.
    /// </summary>
    [Fact]
    public void TheSwitchOffReproducesTheCapturedOnlyTableExactly()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load(authored: false, out string status);

        Assert.Equal(0, table.AuthoredRowCount);
        Assert.Equal(CapturedOnlyPayloadBytes, table.PayloadLength);
        Assert.Equal(1_448, table.AppearanceCount);
        Assert.Equal(673, table.SemanticCount);
        Assert.Equal(22_409, table.ParameterCount);
        Assert.Contains("no Cranberry-authored rows", status);
        Assert.All(CensusGreyItems, item => Assert.Empty(table.RowsForItem(item)));
    }

    // -------------------------------------------------------------------------------------
    // helpers
    // -------------------------------------------------------------------------------------

    private static AugustDynamicAppearanceTable Load(bool authored, out string status)
    {
        bool previous = AugustDynamicAppearanceTable.ApplyAuthoredRows;
        // D316: every number this class pins is the FILTERED payload the authored rows were built
        // for. The whole table dresses all thirteen of those items itself and drops them, which is
        // FullAppearanceTableTests' subject, not this one.
        bool previousWhole = AugustDynamicAppearanceTable.ShipWholeTable;
        try
        {
            AugustDynamicAppearanceTable.ShipWholeTable = false;
            AugustDynamicAppearanceTable.ApplyAuthoredRows = authored;
            Assert.True(
                AugustDynamicAppearanceTable.TryLoad(
                    Source,
                    out AugustDynamicAppearanceTable? table,
                    out status),
                status);
            return Assert.IsType<AugustDynamicAppearanceTable>(table);
        }
        finally
        {
            AugustDynamicAppearanceTable.ApplyAuthoredRows = previous;
            AugustDynamicAppearanceTable.ShipWholeTable = previousWhole;
        }
    }

    private static byte[] ExpectedAuthoredRows()
    {
        using var writer = new PacketWriter();
        foreach (AugustAuthoredAppearanceRow row in AugustAuthoredAppearance.Rows)
        {
            writer.WriteUInt32(row.RowId);
            writer.WriteUInt32(row.RowId);
            writer.WriteUInt32(row.ItemDefinitionId);
            writer.WriteUInt32(row.ModelId);
            writer.WriteUInt32(row.GenderId);
            writer.WriteUInt32(0);
            writer.WriteUInt32(row.ShaderParameterGroupId);
        }

        return writer.Written.ToArray();
    }

    private static byte[] ExpectedAuthoredSemantics()
    {
        using var writer = new PacketWriter();
        foreach (uint item in AugustAuthoredAppearance.Items.Order())
        {
            uint[] rowIds =
            [
                .. AugustAuthoredAppearance.Rows
                    .Where(row => row.ItemDefinitionId == item)
                    .Select(row => row.RowId)
                    .Order(),
            ];
            writer.WriteUInt32(item);
            writer.WriteInt32(rowIds.Length);
            foreach (uint rowId in rowIds)
            {
                writer.WriteUInt32(rowId);
            }
        }

        return writer.Written.ToArray();
    }

    private static byte[] ExpectedAuthoredParameters()
    {
        using var writer = new PacketWriter();
        foreach (DynamicAppearanceShaderValue value in AugustAuthoredAppearance.ShaderValues)
        {
            value.WriteTo(writer);
        }

        return writer.Written.ToArray();
    }

    private readonly record struct Sections(
        int AppearanceCount,
        byte[] Appearances,
        int SemanticCount,
        byte[] Semantics,
        int ParameterCount,
        byte[] Parameters);

    /// <summary>The payload's three sections, cut exactly where the client's loader cuts them.</summary>
    private static Sections Split(byte[] payload)
    {
        var reader = new PacketReader(payload);
        int appearanceCount = reader.ReadInt32();
        int appearanceStart = reader.Position;
        for (int index = 0; index < appearanceCount; index++)
        {
            _ = reader.ReadBytes(28);
        }

        byte[] appearances = payload[appearanceStart..reader.Position];

        int semanticCount = reader.ReadInt32();
        int semanticStart = reader.Position;
        for (int index = 0; index < semanticCount; index++)
        {
            _ = reader.ReadUInt32();
            int rowCount = reader.ReadInt32();
            for (int row = 0; row < rowCount; row++)
            {
                _ = reader.ReadUInt32();
            }
        }

        byte[] semantics = payload[semanticStart..reader.Position];

        int parameterCount = reader.ReadInt32();
        int parameterStart = reader.Position;
        for (int index = 0; index < parameterCount; index++)
        {
            _ = reader.ReadUInt32();
            _ = reader.ReadString();
            _ = reader.ReadBytes(16);
            _ = reader.ReadUInt32();
            _ = reader.ReadBool();
            _ = reader.ReadString();
            _ = reader.ReadUInt32();
        }

        byte[] parameters = payload[parameterStart..reader.Position];
        Assert.True(reader.AtEnd, $"{reader.Remaining} trailing byte(s)");

        return new Sections(
            appearanceCount, appearances,
            semanticCount, semantics,
            parameterCount, parameters);
    }
}
