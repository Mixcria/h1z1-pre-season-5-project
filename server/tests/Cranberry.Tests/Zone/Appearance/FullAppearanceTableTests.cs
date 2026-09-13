using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// D316 / docs/124: <c>CRANBERRY_APPEARANCE_TABLE=full</c> ships the friend
/// <c>DynamicAppearance</c> capture WHOLE, the way the owner's Z1 server does, and
/// <c>filtered</c> still produces exactly the payload that shipped before it.
/// </summary>
[Collection(AppearanceStaticsCollection.Name)]
public sealed class FullAppearanceTableTests
{
    private const string Source = @"C:\Z1\Server\Data\dynamicAppearanceFriend.bin";

    // The capture, as its own three arrays count themselves (verified by an independent parse of
    // C:\Z1\Server\Data\dynamicAppearanceFriend.bin: 6,625,396 inflated bytes, consumed exactly).
    private const int SourceAppearances = 3_551;
    private const int SourceSemantics = 1_667;
    private const int SourceParameters = 119_158;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NautilusUsesOnlyShippedTexturesAndPreservesItsOtherShaderParameters(bool whole)
    {
        var table = Load(whole, out _);
        using var source = File.OpenRead(Source);
        using var inflate = new System.IO.Compression.ZLibStream(source, System.IO.Compression.CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        inflate.CopyTo(bytes);
        var captured = ShaderParameters(bytes.ToArray().AsSpan(2));
        var sent = ShaderParameters(table.CreateReferenceData().Payload);
        var oldNautilus = captured.Where(p => p.Group == 1862).ToArray();
        var nautilus = sent.Where(p => p.Group == 1862).ToArray();
        Assert.Equal(77, nautilus.Length);
        var missing = Assert.Single(oldNautilus, p => p.Texture == "Wood_01_PT.dds");
        Assert.Equal("Pattern0TintMap", missing.Name);
        Assert.Equal("black.dds", Assert.Single(nautilus, p => p.Name == missing.Name).Texture);
        Assert.Equal("Octopus_01_SS.dds", Assert.Single(nautilus, p => p.Name == "DecalTint").Texture);
        Assert.Equal(oldNautilus.Where(p => p.Name != missing.Name).Select(p => Convert.ToHexString(p.Bytes)),
            nautilus.Where(p => p.Name != missing.Name).Select(p => Convert.ToHexString(p.Bytes)));
        var assets = File.ReadLines(@"C:\Aug2017\out\data_aug\pack-index-aug.tsv")
            .Select(line => line.Split('\t')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(missing.Texture, assets);
        Assert.All(nautilus.Where(p => p.Texture.Length > 0), p => Assert.Contains(p.Texture, assets));
        Assert.Equal(1102u, AugustMaterialEffects.EffectIdFor(4032));
        foreach (uint gender in new uint[] { 1, 2 })
        {
            Assert.Equal(1862u, table.ShaderGroupFor(4032, gender));
            Assert.All(table.AppearanceRowsFor(4032, gender), id =>
            {
                Assert.True(table.TryGetRow(id, out var row));
                Assert.Equal(9519u, row.ModelId); // the shotgun, never a body model
            });
        }
        if (whole)
            Assert.Equal(captured.Where(p => !RepairedTexture(p.Group, p.Name)).Select(p => Convert.ToHexString(p.Bytes)),
                sent.Take(captured.Count).Where(p => !RepairedTexture(p.Group, p.Name)).Select(p => Convert.ToHexString(p.Bytes)));
    }

    private static bool RepairedTexture(uint group, string name) =>
        (group == 1862 && name == "Pattern0TintMap")
        || (group is 1747 or 1919 && name == "PatternTintMask");

    [Theory]
    [InlineData(true, 1718u, 1747u, 9840u)]
    [InlineData(false, 1718u, 1747u, 9840u)]
    [InlineData(true, 1997u, 1919u, 9848u)]
    [InlineData(false, 1997u, 1919u, 9848u)]
    public void EquippedPistolsResolveTheirModelsWithOnlyShippedTextures(bool whole, uint item, uint group, uint model)
    {
        var table = Load(whole, out _);
        var parameters = ShaderParameters(table.CreateReferenceData().Payload).Where(p => p.Group == group).ToArray();
        Assert.Equal("black.dds", Assert.Single(parameters, p => p.Name == "PatternTintMask").Texture);
        var assets = File.ReadLines(@"C:\Aug2017\out\data_aug\pack-index-aug.tsv")
            .Select(line => line.Split('\t')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(parameters.Where(p => p.Texture.Length > 0), p => Assert.Contains(p.Texture, assets));
        foreach (uint gender in new uint[] { 1, 2 })
        {
            Assert.Equal(group, table.ShaderGroupFor(item, gender));
            Assert.NotEmpty(table.RowsForItem(item, gender));
            Assert.All(table.RowsForItem(item, gender), row => Assert.Equal(model, row.ModelId));
        }
    }

    private static List<(uint Group, string Name, string Texture, byte[] Bytes)> ShaderParameters(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        reader.Skip(reader.ReadInt32() * 28);
        for (int n = reader.ReadInt32(); n > 0; n--)
        {
            reader.ReadUInt32();
            reader.Skip(reader.ReadInt32() * 4);
        }
        var parameters = new List<(uint, string, string, byte[])>();
        for (int n = reader.ReadInt32(); n > 0; n--)
        {
            int start = reader.Position;
            uint group = reader.ReadUInt32();
            string name = reader.ReadString();
            reader.Skip(21);
            string texture = reader.ReadString();
            reader.ReadUInt32();
            parameters.Add((group, name, texture, payload[start..reader.Position].ToArray()));
        }
        Assert.True(reader.AtEnd);
        return parameters;
    }

    [Fact]
    public void EveryCapturedHoodedMeshIsEligibleOnlyForItsOwnBody()
    {
        if (!File.Exists(Source)) return;
        var table = Load(whole: true, out _);
        using var source = File.OpenRead(Source);
        using var inflate = new System.IO.Compression.ZLibStream(source, System.IO.Compression.CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        inflate.CopyTo(bytes);
        var r = new PacketReader(bytes.ToArray().AsSpan(2));
        var hooded = new Dictionary<uint, List<AugustAppearanceRow>>();
        for (int n = r.ReadInt32(); n > 0; n--)
        {
            uint id = r.ReadUInt32(); r.ReadUInt32(); uint item = r.ReadUInt32();
            uint model = r.ReadUInt32(); uint gender = r.ReadUInt32();
            uint requirement = r.ReadUInt32(); uint shader = r.ReadUInt32();
            if (model is not (9653 or 9654 or 9697 or 9698 or 9741 or 9742)) continue;
            Assert.True(table.TryGetRow(id, out var repaired));
            Assert.Equal(model is 9653 or 9698 or 9742 ? 2u : 1u, repaired.GenderId);
            Assert.Equal(shader, repaired.ShaderParameterGroupId);
            Assert.Equal(0u, requirement);
            if (!hooded.TryGetValue(item, out var rows)) hooded[item] = rows = [];
            rows.Add(repaired);
        }
        Assert.NotEmpty(hooded);
        foreach (var rows in hooded.Values)
        foreach (uint gender in new uint[] { 1, 2 })
        {
            // A menu manager may construct its own list, in either order. No eligible
            // row can supply the other body's mesh, even after native set sorting.
            var chosen = AugustSkinCensus.PickRow(rows.AsEnumerable().Reverse().ToArray(), gender);
            Assert.NotNull(chosen);
            Assert.Equal(gender, AugustSkinCensus.BakedGenderOf(AugustModelCatalog.FileNameFor(chosen.Value.ModelId)));
        }
        var twin = new[] { 1586u, 1585u }.Select(id => { Assert.True(table.TryGetRow(id, out var row)); return row; }).ToArray();
        Assert.Equal(1586u, AugustSkinCensus.PickRow(twin, 2)!.Value.RowId);
        Assert.Equal(1585u, AugustSkinCensus.PickRow(twin, 1)!.Value.RowId);
    }

    [Fact]
    public void RepairsChangeOnlyCrownColourAndHoodieWildcardGenderWords()
    {
        if (!File.Exists(Source)) return;

        AugustDynamicAppearanceTable table = Load(whole: true, out _);
        Assert.Equal(252u, table.ShaderGroupFor(2778, CharacterVisuals.Male));
        Assert.Equal(252u, table.ShaderGroupFor(2778, CharacterVisuals.Female));
        Assert.Equal(250u, table.ShaderGroupFor(2777, CharacterVisuals.Male));
        Assert.Equal(250u, table.ShaderGroupFor(2777, CharacterVisuals.Female));
        Assert.True(table.DefinesShaderGroup(252));
        Assert.Equal(147, table.CorrectedAppearanceCount);
        Assert.Equal(1, table.CorrectedShaderGroupCount);
        Assert.Equal(146, table.CorrectedGenderCount);

        using var source = File.OpenRead(Source);
        using var inflate = new System.IO.Compression.ZLibStream(source, System.IO.Compression.CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        inflate.CopyTo(bytes);
        byte[] captured = bytes.ToArray();
        byte[] sent = table.CreateReferenceData().Payload;
        var changed = new List<uint>();
        for (int index = 0; index < SourceAppearances; index++)
        {
            int at = 4 + index * 28;
            uint id = BitConverter.ToUInt32(sent, at);
            if (sent.AsSpan(at, 28).SequenceEqual(captured.AsSpan(at + 2, 28))) continue;
            changed.Add(id);
            if (id != 336)
            {
                uint model = BitConverter.ToUInt32(captured, at + 14);
                Assert.Contains(model, new uint[] { 9653, 9654, 9697, 9698, 9741, 9742 });
                Assert.Equal(0u, BitConverter.ToUInt32(captured, at + 18));
                Assert.Equal(model is 9653 or 9698 or 9742 ? 2u : 1u, BitConverter.ToUInt32(sent, at + 16));
                Assert.Equal(captured.AsSpan(at + 2, 16).ToArray(), sent.AsSpan(at, 16).ToArray());
                Assert.Equal(captured.AsSpan(at + 22, 8).ToArray(), sent.AsSpan(at + 20, 8).ToArray());
                continue;
            }
            Assert.Equal(captured.AsSpan(at + 2, 24).ToArray(), sent.AsSpan(at, 24).ToArray());
            Assert.Equal(3264u, BitConverter.ToUInt32(captured, at + 26));
            Assert.Equal(252u, BitConverter.ToUInt32(sent, at + 24));
        }
        Assert.Equal(147, changed.Count);
        Assert.Contains(336u, changed);
    }

    /// <summary>
    /// The default is full. The whole point of D316 is that the grey table is the thing you have
    /// to ask for.
    /// </summary>
    [Fact]
    public void TheWholeTableIsTheDefault()
    {
        Assert.True(AugustDynamicAppearanceTable.WholeTableFromText(null));
        Assert.True(AugustDynamicAppearanceTable.WholeTableFromText("full"));
        Assert.True(AugustDynamicAppearanceTable.WholeTableFromText("FULL"));
        Assert.True(AugustDynamicAppearanceTable.WholeTableFromText("anything else"));
        Assert.False(AugustDynamicAppearanceTable.WholeTableFromText("filtered"));
        Assert.False(AugustDynamicAppearanceTable.WholeTableFromText(" Filtered "));
    }

    /// <summary>
    /// Deliverable 1: every appearance row, every item map and every shader parameter of the
    /// source survives the re-wrap into Cranberry's own 1148 <c>DynamicAppearanceDefinitions</c>
    /// form - the counts are the SOURCE's counts, not a subset of them.
    /// </summary>
    [Fact]
    public void TheWholeTableCarriesExactlyTheSourceCounts()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load(whole: true, out string status);

        Assert.True(table.WholeTable);
        Assert.Equal(SourceAppearances, table.AppearanceCount);
        Assert.Equal(SourceSemantics, table.SemanticCount);

        // The capture's own parameters, plus the starter palette's three Cranberry groups (the
        // four skin-tone groups the palette also defines are the capture's, so nothing doubles).
        Assert.Equal(SourceParameters + 18, table.ParameterCount);
        Assert.Equal(
            18,
            DynamicAppearanceReference.StarterValues.Count - table.SuppressedStarterValueCount);
        Assert.Equal(24, table.SuppressedStarterValueCount);

        // All thirteen authored items (D203) are dressed by the capture itself, so all of their
        // authored rows stand down and the capture is the only source of an appearance row.
        Assert.Equal(0, table.AuthoredRowCount);
        Assert.Equal(AugustAuthoredAppearance.Rows.Length, table.SuppressedAuthoredRowCount);

        Assert.Contains("WHOLE friend appearance table", status);
        Assert.Contains("3,551 appearances", status);
        Assert.Contains("1,667 item maps", status);

        // Re-read the payload and count the arrays back, so this is a statement about the BYTES.
        var reader = new PacketReader(table.CreateReferenceData().Payload);
        Assert.Equal(SourceAppearances, reader.ReadInt32());
        reader.Skip(SourceAppearances * 28);
        int semantics = reader.ReadInt32();
        Assert.Equal(SourceSemantics, semantics);
        for (int index = 0; index < semantics; index++)
        {
            _ = reader.ReadUInt32();
            reader.Skip(reader.ReadInt32() * 4);
        }

        Assert.Equal(SourceParameters + 18, reader.ReadInt32());
    }

    /// <summary>
    /// Deliverable 4's second half: <c>filtered</c> is still, exactly, what tonight's session
    /// sent. Pinned by count and payload length rather than by a hash, so a future authored row
    /// moves one number in one place.
    /// </summary>
    [Fact]
    public void FilteredRetainsItsCountsWithTheNautilusTextureRepair()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load(whole: false, out string status);

        Assert.False(table.WholeTable);
        Assert.Equal(1_474, table.AppearanceCount);
        Assert.Equal(686, table.SemanticCount);
        Assert.Equal(22_487, table.ParameterCount);
        // 1,273,927 payload bytes with the 26 authored rows on (the pre-D203 captured-only
        // payload, which AugustDynamicAppearanceTableTests still pins, is 1,268,831).
        Assert.Equal(1_273_927 - 25, table.PayloadLength); // Three missing textures -> black.dds.
        Assert.Equal(26, table.AuthoredRowCount);
        Assert.Equal(0, table.SuppressedAuthoredRowCount);
        Assert.Equal(0, table.SuppressedStarterValueCount);
        Assert.Contains("filtered Z1-compatible appearance table", status);
    }

    /// <summary>
    /// The whole table is the capture, re-wrapped: same envelope, same three arrays, same 1148
    /// type name, and a payload that is the source body plus the three appended starter groups.
    /// </summary>
    [Fact]
    public void TheWholeTableIsTheCapturePlusTheEnvelopeAndNothingElse()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable whole = Load(whole: true, out _);

        // 6,625,396 source bytes, less its own two opcode bytes, plus the three appended starter
        // groups (six values each; 37 fixed bytes a value plus the semantic's name).
        Assert.Equal(6_625_394 + 960 - 25, whole.PayloadLength);

        using var writer = new PacketWriter();
        whole.CreateReferenceData().WriteTo(writer);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(ZoneOpcodes.ReferenceData, reader.ReadByte());
        ushort taggedNameLength = reader.ReadUInt16();
        Assert.Equal(
            DynamicAppearanceReference.TypeName,
            System.Text.Encoding.ASCII.GetString(reader.ReadBytes(taggedNameLength & 0x1fff)));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal((uint)whole.PayloadLength, reader.ReadUInt32());
        Assert.Equal(whole.PayloadLength, reader.ReadInt32());
        Assert.Equal(whole.PayloadLength, reader.ReadRest().Length);

        // The wire size, gateway tunnel byte included, the way the filtered table's own test
        // counts 1,268,872.
        Assert.Equal(6_626_395 - 25, 1 + writer.Position);
    }

    /// <summary>
    /// Deliverable 3: the items the 2026-09-04 session drew as <c>grp 0 app []</c>. The whole
    /// table answers six of them.
    /// </summary>
    [Theory]
    [InlineData(10u, 168u)]     // AR-15
    [InlineData(2229u, 71u)]    // AK-47
    [InlineData(1374u, 29u)]    // pump shotgun
    [InlineData(2114u, 807u)]   // Mansport backpack
    [InlineData(2172u, 271u)]   // motorcycle helmet
    [InlineData(2209u, 481u)]   // Conveys sneakers
    public void TheWholeTableColoursTheItemsTonightDrewGrey(uint item, uint expectedGroup)
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable whole = Load(whole: true, out _);

        Assert.NotEmpty(whole.AppearanceRowsFor(item));
        Assert.Equal(expectedGroup, whole.ShaderGroupForAnyBody(item));
        Assert.True(
            whole.DefinesShaderGroup(expectedGroup),
            $"item {item} names group {expectedGroup}; the payload must carry its parameters");
    }

    /// <summary>
    /// And the honest half of the same report: the capture holds no appearance row for a
    /// throwable, the binoculars or either machete, at all - so <c>grp 0</c> stays the truthful
    /// value and the mesh keeps the client's neutral tint (docs/106 A21, §13.7).
    /// </summary>
    [Theory]
    [InlineData(2237u)]  // gas grenade
    [InlineData(65u)]    // M67 frag
    [InlineData(2236u)]  // smoke grenade
    [InlineData(2235u)]  // flashbang
    [InlineData(14u)]    // molotov
    [InlineData(1542u)]  // binoculars
    [InlineData(83u)]    // machete
    [InlineData(2228u)]  // machete (second definition)
    public void TheWholeTableStillHasNoRowForAThrowableOrTheBinoculars(uint item)
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable whole = Load(whole: true, out _);

        Assert.Empty(whole.AppearanceRowsFor(item));
        Assert.Equal(0u, whole.ShaderGroupForAnyBody(item));
    }

    /// <summary>
    /// Deliverable 1's pacing question, measured rather than argued. The whole table is one
    /// reliable message of ~13,000 datagrams; the send window meters it, and the only thing that
    /// decides how long it takes to drain is how often the peer acknowledges.
    /// <para>
    /// The channel is driven here the way <c>ReliableChannelTests</c> drives it, minus the inbound
    /// half (whose <c>ReassemblyBuffer.MaxMessageLength</c> of 1 MB is a guard on what a client may
    /// send US, not a limit on what we send). Nothing is resent, so no datagram ever waits out the
    /// 300 ms timer: the send costs one acknowledgement round trip per window of 96.
    /// </para>
    /// </summary>
    [Fact]
    public void TheWholeTableDrainsInOneRoundTripPerWindowWithNoResends()
    {
        var settings = new SessionSettings { UdpLength = 512 };
        var sent = new List<int>();
        var outbound = new OutboundChannel(settings, datagram => sent.Add(datagram.Length));

        byte[] message = new byte[6_626_354];
        outbound.Send(message, cipher: null, now: 0);

        // 508 payload bytes a datagram; the first fragment gives four of them to the length prefix.
        int datagrams = outbound.PendingCount;
        Assert.Equal(13_045, datagrams);
        Assert.True(datagrams < ushort.MaxValue, "the sequence number would wrap inside one message");

        // Only one window may be on the wire before the peer has said anything at all.
        Assert.Equal(outbound.SendWindow, sent.Count);

        int rounds = 0;
        int acknowledged = 0;
        long now = 0;
        while (outbound.PendingCount > 0 && rounds < 1_000)
        {
            rounds++;
            acknowledged = sent.Count;
            outbound.Acknowledge((ushort)(acknowledged - 1), now);
        }

        Assert.Equal(0, outbound.PendingCount);
        Assert.Equal(0, outbound.DatagramsResent);
        Assert.False(outbound.PeerLost);
        Assert.Equal(datagrams, acknowledged);

        // One acknowledgement round per configured window. September 7's transport tuning
        // reduced that window to 32, so a fixed historical 136-round assertion is no longer valid.
        Assert.Equal((datagrams + outbound.SendWindow - 1) / outbound.SendWindow, rounds);
        Assert.True(
            rounds * 10 < outbound.PeerSilenceLimitMs,
            "at 10 ms an acknowledgement the send would outlast the peer-silence limit");
    }

    private static AugustDynamicAppearanceTable Load(bool whole, out string status)
    {
        bool previousWhole = AugustDynamicAppearanceTable.ShipWholeTable;
        bool previousOverrides = AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides;
        bool previousAuthored = AugustDynamicAppearanceTable.ApplyAuthoredRows;
        try
        {
            AugustDynamicAppearanceTable.ShipWholeTable = whole;
            AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides = false;
            AugustDynamicAppearanceTable.ApplyAuthoredRows = true;
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
            AugustDynamicAppearanceTable.ShipWholeTable = previousWhole;
            AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides = previousOverrides;
            AugustDynamicAppearanceTable.ApplyAuthoredRows = previousAuthored;
        }
    }
}
