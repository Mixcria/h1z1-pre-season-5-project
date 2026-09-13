using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

[Collection(Cranberry.Tests.Zone.Appearance.AppearanceStaticsCollection.Name)]
public sealed class AugustDynamicAppearanceTableTests
{
    // The only source the filter accepts (length + md5 gated in AugustDynamicAppearanceTable).
    // Every assertion below is skipped when it is absent, so the suite stays green on a machine
    // that does not have it.
    private static readonly string Source = TestData.DynamicAppearanceSource;

    /// <summary>
    /// The regression itself: the overrides shipped enabled between 15:07 and 18:32 on 2026-08-29
    /// and no session entered Z2 in that window. The default must stay off until a
    /// client-originated ClientIsReady (Zoning) proves otherwise (docs/32).
    /// </summary>
    [Fact]
    public void AppearanceRowOverridesAreOffByDefault() =>
        Assert.False(AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides);

    [Fact]
    public void MissingCompatibilitySourceFallsBackCleanly()
    {
        bool loaded = AugustDynamicAppearanceTable.TryLoad(
            @"C:\Aug2017\data\does-not-exist.dynamic-appearance",
            out AugustDynamicAppearanceTable? table,
            out string status);

        Assert.False(loaded);
        Assert.Null(table);
        Assert.Contains("does not exist", status);
    }

    /// <summary>
    /// The shipping shape: <see cref="AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides"/>
    /// off. Both sessions in which the client actually entered Z2 carry exactly this table —
    ///
    /// <code>
    /// logs/host-20260829-150206.log:1  filtered Z1-compatible appearance table:
    ///     1,448 appearances, 673 item maps, 22,409 shader values, 1,268,831 bytes
    /// logs/host-20260829-184346.log:1  ... 22,409 shader values, 1,268,831 bytes;
    ///     repaired 0 rows (0 shader groups, 0 genders)
    /// </code>
    ///
    /// and both captures show it on the wire at 1,268,872 bytes (41 bytes of ReferenceData header
    /// over the payload): wire-20260829-150206.txt row 258, wire-20260829-184346.txt row 139.
    /// </summary>
    [Fact]
    public void FilteredTableKeepsTheAcceptedShapeWithTheCrownColourRepair()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load(applyOverrides: false, out string status);

        Assert.Equal(1_448, table.AppearanceCount);
        Assert.Equal(673, table.SemanticCount);
        Assert.Equal(22_409, table.ParameterCount);
        Assert.Equal(1_268_806, table.PayloadLength);
        Assert.Equal(101, table.CorrectedAppearanceCount);
        Assert.Equal(1, table.CorrectedShaderGroupCount);
        Assert.Equal(100, table.CorrectedGenderCount);
        Assert.Contains("22,409 shader values, 1,268,806 bytes", status);

        // Sanity on the retained wardrobe surface, unchanged by the toggle.
        Assert.InRange(table.AppearanceCount, 1, 3_551);
        Assert.InRange(table.SemanticCount, 1, 1_667);
        Assert.InRange(table.ParameterCount, 1, 119_200);
        Assert.NotEmpty(table.AppearanceRowsFor(2484));            // Boonie Hat reward

        // Four-row hooded entries collapse to the active (Up) pair on the equipment wire.
        Assert.Equal(new uint[] { 1585, 1586 }, table.AppearanceRowsFor(2889));
        IReadOnlyDictionary<uint, AppearanceRow> rows =
            ReadAppearanceRows(table.CreateReferenceData().Payload);
        Assert.Equal(2889u, rows[1585].ItemDefinitionId);
        Assert.Equal(2889u, rows[1586].ItemDefinitionId);

        // The ReferenceData envelope: tagged type name, then the payload length twice.
        using var writer = new PacketWriter();
        table.CreateReferenceData().WriteTo(writer);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(ZoneOpcodes.ReferenceData, reader.ReadByte());
        ushort taggedNameLength = reader.ReadUInt16();
        Assert.Equal(DynamicAppearanceReference.TypeName.Length, taggedNameLength & 0x1fff);
        Assert.Equal(
            DynamicAppearanceReference.TypeName,
            System.Text.Encoding.ASCII.GetString(reader.ReadBytes(taggedNameLength & 0x1fff)));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal((uint)table.PayloadLength, reader.ReadUInt32());
        Assert.Equal(table.PayloadLength, reader.ReadInt32());
        Assert.Equal(table.PayloadLength, reader.ReadRest().Length);

        // Wire size the client accepted: 1 opcode + 2 tagged length + name + 1 + 4 + 4, plus the
        // gateway tunnel byte the capture includes.
        Assert.Equal(1_268_847, 1 + writer.Position);
    }

    [Fact]
    public void HoodieRowsCanExplicitlySelectDownAndUpPostures()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load(applyOverrides: false, out _);

        Assert.Equal([1081u, 1082u], table.AppearanceRowsForHood(3405, hoodUp: false));
        Assert.Equal([1597u, 1598u], table.AppearanceRowsForHood(3405, hoodUp: true));
        Assert.NotEqual(
            table.AppearanceRowsForHood(3405, hoodUp: false),
            table.AppearanceRowsForHood(3405, hoodUp: true));
    }

    /// <summary>
    /// Keeps the <see cref="AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides"/> code path
    /// covered without claiming it is correct to ship. Turning it on rewrites 152 rows and grows
    /// the payload — the exact table the broken sessions sent:
    ///
    /// <code>
    /// logs/host-20260829-173352.log:1  filtered Z1-compatible appearance table:
    ///     1,448 appearances, 673 item maps, 22,893 shader values, 1,295,407 bytes;
    ///     repaired 152 rows (51 shader groups, 101 genders)
    /// </code>
    ///
    /// That session, and every session from 15:07 onward, stopped answering after
    /// ClientBeginZoning and never sent ClientIsReady (Zoning) — docs/32. This test asserts what
    /// the toggle <i>does</i> (the override table is applied faithfully and nothing else moves),
    /// never that the client accepts the result.
    /// </summary>
    [Fact]
    public void ApplyingTheGeneratedOverridesRewritesExactlyTheOverriddenRowsAndGrowsThePayload()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable plain = Load(applyOverrides: false, out _);
        AugustDynamicAppearanceTable corrected = Load(applyOverrides: true, out string status);

        Assert.Equal(152, corrected.CorrectedAppearanceCount);
        Assert.Equal(51, corrected.CorrectedShaderGroupCount);
        Assert.Equal(101, corrected.CorrectedGenderCount);
        Assert.Equal(AugustSkinCatalog.AppearanceRowOverrides.Count, corrected.CorrectedAppearanceCount);
        Assert.Equal(22_893, corrected.ParameterCount);
        Assert.Equal(1_295_382, corrected.PayloadLength);
        Assert.Contains("repaired 152 rows (51 shader groups, 101 genders)", status);

        // 484 extra shader values and 26,576 extra bytes — the growth that broke zoning.
        Assert.Equal(484, corrected.ParameterCount - plain.ParameterCount);
        Assert.Equal(26_576, corrected.PayloadLength - plain.PayloadLength);
        Assert.Equal(plain.AppearanceCount, corrected.AppearanceCount);
        Assert.Equal(plain.SemanticCount, corrected.SemanticCount);

        // Every rewritten row is an override row, carries the override's values, and no row
        // outside the override table moves.
        IReadOnlyDictionary<uint, AppearanceRow> before =
            ReadAppearanceRows(plain.CreateReferenceData().Payload);
        IReadOnlyDictionary<uint, AppearanceRow> after =
            ReadAppearanceRows(corrected.CreateReferenceData().Payload);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());

        uint[] changed = [.. before.Where(row => after[row.Key] != row.Value).Select(row => row.Key).Order()];
        Assert.Equal(
            AugustSkinCatalog.AppearanceRowOverrides.Where(pair =>
                before[pair.Key].ShaderParameterGroupId != pair.Value.ShaderParameterGroupId
                || before[pair.Key].GenderId != pair.Value.GenderId).Select(pair => pair.Key).Order(),
            changed);
        Assert.All(changed, rowId =>
        {
            AugustAppearanceRowOverride expected = AugustSkinCatalog.AppearanceRowOverrides[rowId];
            Assert.Equal(expected.ItemDefinitionId, after[rowId].ItemDefinitionId);
            Assert.Equal(expected.GenderId, after[rowId].GenderId);
            Assert.Equal(expected.ShaderParameterGroupId, after[rowId].ShaderParameterGroupId);
            Assert.Equal(before[rowId].ItemDefinitionId, after[rowId].ItemDefinitionId);
        });
    }

    /// <summary>
    /// The toggle is process-global, so it is set and restored around each load. Both tests live
    /// in this one class, which xUnit runs sequentially, and no other test loads the table.
    /// </summary>
    private static AugustDynamicAppearanceTable Load(bool applyOverrides, out string status)
    {
        bool previous = AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides;
        // Every number this class pins is the CAPTURED table the client accepted, so the
        // Cranberry-authored rows are held off here on purpose. They have their own pinned
        // numbers, and their own byte-exact comparison against these, in
        // Zone/Appearance/AuthoredAppearanceTests (docs/106 addendum 2026-09-03).
        bool previousAuthored = AugustDynamicAppearanceTable.ApplyAuthoredRows;
        // D316 (docs/124): this class pins the FILTERED table - the one two Z2 sessions accepted -
        // so it names the mode rather than inheriting the process default, which is now full.
        bool previousWhole = AugustDynamicAppearanceTable.ShipWholeTable;
        try
        {
            AugustDynamicAppearanceTable.ShipWholeTable = false;
            AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides = applyOverrides;
            AugustDynamicAppearanceTable.ApplyAuthoredRows = false;
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
            AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides = previous;
            AugustDynamicAppearanceTable.ApplyAuthoredRows = previousAuthored;
            AugustDynamicAppearanceTable.ShipWholeTable = previousWhole;
        }
    }

    private static IReadOnlyDictionary<uint, AppearanceRow> ReadAppearanceRows(byte[] payload)
    {
        var reader = new PacketReader(payload);
        int count = reader.ReadInt32();
        var rows = new Dictionary<uint, AppearanceRow>(count);
        for (int index = 0; index < count; index++)
        {
            uint rowId = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // inner/data id
            uint itemId = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // model id
            uint genderId = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // client requirement id
            uint shaderGroupId = reader.ReadUInt32();
            rows.Add(rowId, new AppearanceRow(itemId, genderId, shaderGroupId));
        }

        return rows;
    }

    private readonly record struct AppearanceRow(
        uint ItemDefinitionId,
        uint GenderId,
        uint ShaderParameterGroupId);
}
