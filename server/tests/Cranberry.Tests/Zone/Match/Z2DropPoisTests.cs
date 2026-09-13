using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchDrop;

/// <summary>
/// The drop dataset, pinned against the shipped <c>z2-loot-spawns.bin</c> (docs/48 §4). These are
/// data tests: they exist so that a change to the placement file that would quietly make a place
/// undroppable — or drop a player into an empty field — fails here rather than in a play-test.
/// </summary>
public sealed class Z2DropPoisTests
{
    /// <summary>The worst anchor in the game, measured over all 92 places (docs/48 §4.3).</summary>
    private const int WorstAnchorNeighbours = 122;

    /// <summary>The closest any anchor comes to the ±4,096 m terrain edge (ChangsWildCampgrounds).</summary>
    private const float ClosestAnchorToEdge = 202f;

    [Fact]
    public void TheShippedFileCollapsesToNinetyTwoNamedPlaces()
    {
        Z2DropPois places = DropFixture.Places;

        Assert.Equal(126, DropFixture.Spawns.Areas.Length);
        Assert.Equal(92, places.Count);

        int tagged = 0;
        int boxes = 0;
        foreach (DropPoi place in places.Places)
        {
            tagged += place.MarkerCount;
            boxes += place.BoxCount;
        }

        // 144,365 of the 168,322 markers carry an area (85.8 %); the rest are NoArea.
        Assert.Equal(144_365, tagged);
        Assert.Equal(126, boxes);
    }

    [Fact]
    public void EveryAreaNameIsALootPlaceAndEveryPlaceHasMarkers()
    {
        foreach (string area in DropFixture.Spawns.Areas)
        {
            Assert.StartsWith("Loot.", area, StringComparison.Ordinal);
        }

        foreach (DropPoi place in DropFixture.Places.Places)
        {
            Assert.True(place.MarkerCount > 0, $"{place.Area} has no markers");
            Assert.True(place.BoxCount > 0, $"{place.Area} has no boxes");
        }
    }

    [Fact]
    public void PlacesAreUniqueAndOrderedSoTheDrawIsAFunctionOfTheDataNotTheFileLayout()
    {
        IReadOnlyList<DropPoi> places = DropFixture.Places.Places;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < places.Count; i++)
        {
            Assert.True(seen.Add(places[i].Area), $"{places[i].Area} appears twice");
            if (i > 0)
            {
                Assert.True(
                    string.CompareOrdinal(places[i - 1].Area, places[i].Area) < 0,
                    $"{places[i - 1].Area} sorts after {places[i].Area}");
            }
        }
    }

    /// <summary>
    /// The invariant that makes docs/44 blocker 4 ("NOTHING SPAWNED") unreachable through the drop
    /// path: every anchor already clears <see cref="DropOptions.MinimumMarkers"/> by a wide margin,
    /// so the jitter fallback in <see cref="DropPlanner"/> can never fail.
    /// </summary>
    [Fact]
    public void EveryAnchorHasLootUnderIt()
    {
        Z2DropPois places = DropFixture.Places;
        DropPoi worst = places.Places[0];

        foreach (DropPoi place in places.Places)
        {
            if (place.AnchorNeighbours < worst.AnchorNeighbours)
            {
                worst = place;
            }

            // The stored count is the real one, not a stale field.
            Assert.Equal(place.AnchorNeighbours, places.CountWithin(place.Anchor, places.LootRadius));
        }

        Assert.True(
            worst.AnchorNeighbours >= WorstAnchorNeighbours,
            $"the worst anchor is {worst.Area} with {worst.AnchorNeighbours} markers within {places.LootRadius} m");
        Assert.True(worst.AnchorNeighbours > DropOptions.Default.MinimumMarkers);
    }

    /// <summary>
    /// docs/48 §4.4's answer to "a sensible distance from the map edge": it is measured, not
    /// invented. The guard excludes nothing today, and this test is what makes that stay true.
    /// </summary>
    [Fact]
    public void NoAnchorIsNearTheTerrainEdge()
    {
        Z2DropPois places = DropFixture.Places;
        DropPoi closest = places.Places[0];

        foreach (DropPoi place in places.Places)
        {
            if (place.EdgeMetres < closest.EdgeMetres)
            {
                closest = place;
            }

            Assert.True(MathF.Abs(place.Anchor.X) <= places.MapHalfExtentMetres, place.Area);
            Assert.True(MathF.Abs(place.Anchor.Z) <= places.MapHalfExtentMetres, place.Area);
        }

        Assert.Equal("ChangsWildCampgrounds", closest.Area);
        Assert.True(
            closest.EdgeMetres >= ClosestAnchorToEdge,
            $"{closest.Area} is {closest.EdgeMetres:F1} m from the edge");
        Assert.True(closest.EdgeMetres >= DropOptions.Default.MinimumEdgeMetres);
    }

    /// <summary>
    /// Why the anchor is the densest marker and not the centroid (docs/48 §4.3). Both of these
    /// places are three boxes hundreds of metres apart, so their mean lands in the empty gap: a
    /// centroid drop would hand the player a world with nothing in it, and the log would still say
    /// it worked.
    /// </summary>
    [Theory]
    [InlineData("JayWildernessCamp")]
    [InlineData("TakeetaFarms")]
    public void ThePlacesWhoseCentroidIsEmptyStillHaveAUsableAnchor(string area)
    {
        Z2DropPois places = DropFixture.Places;
        DropPoi place = Assert.IsType<DropPoi>(places.Find(area));

        Vector3 sum = Vector3.Zero;
        foreach (int marker in place.Markers)
        {
            sum += DropFixture.Spawns[marker].Position;
        }

        Vector3 centroid = sum / place.MarkerCount;

        Assert.Equal(0, places.CountWithin(centroid, places.LootRadius));
        Assert.True(place.AnchorNeighbours >= WorstAnchorNeighbours, $"{area}: {place.AnchorNeighbours}");
    }

    /// <summary>
    /// The wave-2 fixed drop is itself a centroid, and it is three times worse than its own place
    /// allows: 376 markers within 80 m against the anchor's 1,252. This is the measurement that
    /// makes the whole anchor pass worth its few hundred milliseconds.
    /// </summary>
    [Fact]
    public void TheShippedFixedDropIsFarWorseThanItsOwnPlacesAnchor()
    {
        Z2DropPois places = DropFixture.Places;
        Vector4 shipped = new ZoneOptions().MatchDropSpawn;
        int atShipped = places.CountWithin(new Vector3(shipped.X, shipped.Y, shipped.Z), places.LootRadius);

        DropPoi pv = Assert.IsType<DropPoi>(places.Find("PVResidential"));

        Assert.Equal(376, atShipped);
        Assert.True(pv.AnchorNeighbours >= 1_245, $"PVResidential anchor: {pv.AnchorNeighbours}");
        Assert.True(pv.AnchorNeighbours > atShipped * 3);
    }

    [Theory]
    [InlineData("Loot.PVResidential.01", "PVResidential")]
    [InlineData("Loot.Dam", "Dam")]
    [InlineData("Loot.CWPUtilitiesCompound192", "CWPUtilitiesCompound192")]
    [InlineData("Loot.Super22StopAndGo.03", "Super22StopAndGo")]
    [InlineData("Loot.UnnamedStorageYard.01", "UnnamedStorageYard")]
    [InlineData("GasWeightArea.ScottsField.01", "GasWeightArea.ScottsField")]
    public void PlaceKeyStripsThePrefixAndTheBoxSuffix(string area, string expected) =>
        Assert.Equal(expected, Z2DropPois.PlaceKeyOf(area));

    /// <summary>
    /// The log line the owner reads. The two client oddities <c>Unnamed</c> and
    /// <c>UnnamedStorageYard</c> are kept verbatim rather than "fixed", because renaming them would
    /// break the one-to-one mapping back to the client's own data.
    /// </summary>
    [Theory]
    [InlineData("RubyLakeCampgroundEast", "Ruby Lake Campground East")]
    [InlineData("PVResidential", "PV Residential")]
    [InlineData("CWPUtilitiesCompound192", "CWP Utilities Compound 192")]
    [InlineData("FZMAStationAlpha", "FZMA Station Alpha")]
    [InlineData("LZPVRadioStation", "LZPV Radio Station")]
    [InlineData("JTsPassAndGas", "JTs Pass And Gas")]
    [InlineData("TYeeWildernessCamp", "TYee Wilderness Camp")]
    [InlineData("Super22StopAndGo", "Super 22 Stop And Go")]
    [InlineData("DeSotosServiceStop", "De Sotos Service Stop")]
    [InlineData("Dam", "Dam")]
    [InlineData("Unnamed", "Unnamed")]
    [InlineData("Runamokcampground", "Runamokcampground")]
    public void DisplayNamesSplitCamelCaseWithoutManglingAcronyms(string area, string expected) =>
        Assert.Equal(expected, Z2DropPois.DisplayNameFor(area));

    [Fact]
    public void EveryPlaceHasADisplayName()
    {
        foreach (DropPoi place in DropFixture.Places.Places)
        {
            Assert.False(string.IsNullOrWhiteSpace(place.DisplayName));
            Assert.Equal(place.Area, place.DisplayName.Replace(" ", string.Empty, StringComparison.Ordinal));
        }
    }

    /// <summary>One anchor pass per spawn set, however many matches ask for it.</summary>
    [Fact]
    public void ForCachesAgainstTheSpawnSet() =>
        Assert.Same(DropFixture.Places, Z2DropPois.For(DropFixture.Spawns, DropOptions.Default));

    [Fact]
    public void MarkerIndicesAreAscendingAndPointAtTheirOwnPlace()
    {
        Z2LootSpawns spawns = DropFixture.Spawns;

        foreach (DropPoi place in DropFixture.Places.Places)
        {
            ReadOnlySpan<int> markers = place.Markers;
            for (int i = 0; i < markers.Length; i++)
            {
                if (i > 0)
                {
                    Assert.True(markers[i] > markers[i - 1], place.Area);
                }

                LootSpawnPoint point = spawns[markers[i]];
                Assert.True(point.HasArea, place.Area);
                Assert.Equal(place.Area, Z2DropPois.PlaceKeyOf(spawns.Areas[point.AreaIndex]));
            }
        }
    }
}
