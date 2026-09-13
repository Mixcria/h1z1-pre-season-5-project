using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Lighting;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// <b>D274 — retail's second loot channel.</b> A fake session: the match clock is turned by hand,
/// the events it produces are checked, the crate's own spawn bytes are checked against the writer
/// <c>ZoneService</c> actually uses, the two banners are checked on the capture-proven
/// <c>TextAlert</c> channel, and the crate is opened into items.
///
/// <para><b>Why the airdrop can be tested without a client at all.</b>
/// <see cref="MatchAirdrops"/> owns the schedule and nothing else — it sends no packets, holds no
/// session, and learns about the gas only through the circle it is handed. Everything below is
/// therefore the same code the live pump runs, driven by a <c>long</c>.</para>
/// </summary>
public sealed class AirdropTests
{
    private static readonly Lazy<AirdropTables> Tables = new(AirdropTables.LoadDefault);

    private const uint LaminatedArmour = 2271;
    private const uint HuntingRifle = 1373;
    private const uint HuntingRifleRound = 1469;
    private const uint FirstAidKit = 2424;

    /// <summary>A schedule that fires at once, so a test does not have to sit through 5:00.</summary>
    private static AirdropOptions Immediate => AirdropOptions.Default with
    {
        FirstDropAtMs = 0,
        DropIntervalMs = 1_000,
        DescentMs = 0,
    };

    private static readonly GasCircle Circle = new(new Vector3(-250f, 0f, 100f), 1_000f);

    /// <summary>
    /// The crate's ids are the client's own, and every one of them is quoted from a file this
    /// project extracted rather than from anybody's server.
    /// </summary>
    [Fact]
    public void TheCrateIsTheClientsOwnMilitaryCrate()
    {
        AirdropCrate crate = Tables.Value.Crate;

        // ClientItemDefinitions 1501 "Military Crate", ITEM_CLASS 25016 (World Container),
        // PARAM1 51 -> ContainerDefinitions 51: 100 slots, WITHDRAWAL_ONLY, REMOVE_WHEN_EMPTY.
        Assert.Equal(1501u, crate.ItemDefinitionId);
        Assert.Equal(1344u, crate.NameId);
        Assert.Equal(25016u, crate.ItemClass);
        Assert.Equal(51u, crate.ContainerDefinitionId);

        // Models.txt 9218 "Crate.Military for air drops." / 9219 "Crate.Military.Parachute for air
        // drops." / 9215 "AirDropC130". Both airborne models are now sent in native delivery routes.
        Assert.Equal(9218u, crate.GroundModelId);
        Assert.Equal(9219u, crate.DescendingModelId);
        Assert.Equal(9215u, crate.PlaneModelId);
        Assert.Equal(Rulings.LootGates.PlaneModelId, crate.PlaneModelId);
    }

    /// <summary>The native landing route uses an effect defined by this exact August client.</summary>
    [Fact]
    public void TheLandingEffectIsARealDefinitionThisBuildWouldAccept()
    {
        Assert.Equal(5038u, AirdropOptions.Default.LandingEffectId);
        Assert.True(AugustEffectCatalog.Contains(5038u));
        Assert.Equal("PFX_Impact_AirDrop_Large", AugustEffectCatalog.ById(5038u)!.Value.Name);
        Assert.True(CompositeEffectGate.IsDefined(5038u));
        Assert.False(CompositeEffectGate.IsDenied(5038u));
        Assert.True(CompositeEffectGate.Default.Allowed(5038u, "airdrop landing"));
    }

    [Fact]
    public void ADropAnnouncesThenReleasesAndLandsAtTheAnnouncedPoint()
    {
        var airdrops = new MatchAirdrops(AirdropOptions.Default with
            { BombsEnabled = false, DescentMs = 20_000 }, matchSeed: 1);
        var events = new List<AirdropEvent>();
        Assert.Equal(0, airdrops.Tick(299_000, 50, Circle, Vector3.Zero, events));
        airdrops.Tick(300_000, 50, Circle, Vector3.Zero, events);
        AirdropEvent inbound = Assert.Single(events, e => e.Kind == AirdropEventKind.Inbound);
        AirdropFlight flight = Assert.Single(airdrops.Flights);
        AirdropPayload payload = Assert.Single(flight.Payloads);
        Assert.Contains(events, e => e.Kind == AirdropEventKind.PlaneSpawned);
        Assert.Equal(1, airdrops.InFlight);
        Assert.InRange(Circle.HorizontalDistanceTo(inbound.Position), 0,
            (float)(Circle.Radius * AirdropOptions.Default.SafeZoneFraction));
        events.Clear();
        airdrops.Tick(payload.ReleaseAtMs, 50, Circle, Vector3.Zero, events);
        Assert.Contains(events, e => e.Kind == AirdropEventKind.CrateReleased);
        Assert.Equal(payload.ReleaseAtMs + 20_000, payload.ImpactAtMs);
        events.Clear();
        airdrops.Tick(payload.ImpactAtMs, 50, Circle, Vector3.Zero, events);
        AirdropEvent landed = Assert.Single(events, e => e.Kind == AirdropEventKind.Landed);
        Assert.Equal(inbound.Position, landed.Position);
        Assert.Equal(0, airdrops.InFlight);
        Assert.Equal(1, airdrops.Landed);
    }

    [Fact]
    public void SupplyCratesContinueBelowTwentyAfterALargeLobbyShrinks()
    {
        var airdrops = new MatchAirdrops(Immediate with { BombsEnabled = false }, matchSeed: 7);
        var events = new List<AirdropEvent>();
        airdrops.Tick(0, 150, Circle, Vector3.Zero, events);
        Assert.False(airdrops.Exhausted(20));
        airdrops.Tick(1_000, 20, Circle, Vector3.Zero, events);
        Assert.Equal(2, airdrops.Announced);
        airdrops.Tick(100_000, 1, Circle, Vector3.Zero, events);
        Assert.Equal(4, airdrops.Announced);
        Assert.Equal(4, airdrops.Landed);
    }

    [Fact]
    public void ASoloMatchGetsSupplyCratesAndNoBombs()
    {
        Assert.Equal(1, AirdropOptions.Default.MinPlayersAlive);
        var airdrops = new MatchAirdrops(Immediate with
            { BombRunChance = 1, BombRunDelayMs = 0 }, matchSeed: 3);
        var events = new List<AirdropEvent>();
        airdrops.Tick(0, 1, Circle, Vector3.Zero, events);
        Assert.Single(events, e => e.Kind == AirdropEventKind.Inbound);
        Assert.False(airdrops.Exhausted(1));
        Assert.Equal(0, airdrops.BombRuns);
        Assert.False(airdrops.BombsAllowed(1));
        Assert.True(new MatchAirdrops(Immediate, matchSeed: 3).Exhausted(0));
    }

    /// <summary>The per-match cap holds however long the match runs.</summary>
    [Fact]
    public void AMatchDeliversAtMostTheRuledNumberOfCrates()
    {
        var airdrops = new MatchAirdrops(Immediate, matchSeed: 11);
        var events = new List<AirdropEvent>();
        airdrops.Tick(10_000_000, 1, Circle, Vector3.Zero, events);

        Assert.Equal(AirdropOptions.Default.MaxDropsPerMatch, airdrops.Announced);
        Assert.Equal(AirdropOptions.Default.MaxDropsPerMatch, airdrops.Landed);
        Assert.Equal(4, airdrops.Announced);
    }

    /// <summary>
    /// <b>The contents.</b> Retail: <i>"always laminated armor, 50 % chance of a hunting rifle, plus
    /// medkits/ammo/guns"</i>. The vest and the medical are in every crate; the rifle comes with its
    /// own ammunition or not at all; and the .308 reaches a player <b>only</b> here, which is the
    /// whole reason the feature exists (<c>AUDIT-loot.md</c> F10).
    /// </summary>
    [Fact]
    public void EveryCrateCarriesTheVestAndTheRifleAlwaysBringsItsAmmunition()
    {
        AirdropTables tables = Tables.Value;
        var airdrops = new MatchAirdrops(AirdropOptions.Default, matchSeed: 1);
        int withRifle = 0;

        for (int drop = 0; drop < 200; drop++)
        {
            List<AirdropItem> contents = airdrops.RollContents(tables, drop);

            Assert.Contains(contents, i => i.ItemDefinitionId == LaminatedArmour);
            Assert.Equal(2, contents.Count(i => i.ItemDefinitionId == FirstAidKit));

            bool rifle = contents.Any(i => i.ItemDefinitionId == HuntingRifle);
            Assert.Equal(rifle, contents.Any(i => i.ItemDefinitionId == HuntingRifleRound));
            if (rifle)
            {
                withRifle++;
                Assert.Equal(12u, contents.Single(i => i.ItemDefinitionId == HuntingRifleRound).Count);
            }

            // Guaranteed + at most one rifle bundle + PoolDraws bundles: never an empty crate.
            Assert.True(contents.Count >= 3, $"crate {drop} held only {contents.Count} item(s)");
        }

        // 50 %, over 200 crates, at four standard errors.
        Assert.InRange(withRifle / 200.0, 0.36, 0.64);

        // Every gun in the pool arrives with its own rounds - the same rule the floor's clusters
        // enforce, because a gun with nothing for it is an inert prop.
        foreach (AirdropBundle bundle in tables.Pool)
        {
            if (bundle.Items.Any(i => i.Kind == LootItemKind.Weapon))
            {
                Assert.Contains(bundle.Items, i => i.Kind == LootItemKind.Ammunition);
            }
        }
    }

    /// <summary>
    /// The .308 and its round are the two ids the ground roster explicitly EXCLUDES, and the crate
    /// is the one place they are supposed to appear. This is the assertion that keeps the exclusion
    /// honest: it is airdrop-only, not absent.
    /// </summary>
    [Fact]
    public void TheAirdropIsTheOnlyPlaceTheHuntingRifleExists()
    {
        Assert.Contains(LootRoster.Excluded, row => row.Id == HuntingRifle);
        Assert.Contains(LootRoster.Excluded, row => row.Id == HuntingRifleRound);

        foreach (LootCategoryTable table in LootRoster.Tables.Categories)
        {
            Assert.DoesNotContain(
                table.Entries.ToArray(),
                e => e.ItemDefinitionId is HuntingRifle or HuntingRifleRound);
        }

        Assert.Contains(
            Tables.Value.Rifle.SelectMany(b => b.Items),
            i => i.ItemDefinitionId == HuntingRifle);
    }

    /// <summary>
    /// <b>Crate → HUD alert, byte for byte.</b> The two banners go out on
    /// <c>ClientUpdate.TextAlert 11 31</c>, which carries a STRING rather than a locale id — the
    /// August client ships no airdrop string of any kind — so this pins the exact bytes
    /// <c>ZoneService</c> puts on the wire for both.
    /// </summary>
    [Fact]
    public void TheTwoBannersAreTextAlertsCarryingTheRuledSentences()
    {
        Assert.Equal("A military crate is inbound.", AirdropOptions.Default.InboundText);
        Assert.Equal("The military crate has landed.", AirdropOptions.Default.LandedText);

        foreach (string text in new[]
                 {
                     AirdropOptions.Default.InboundText, AirdropOptions.Default.LandedText,
                 })
        {
            using var writer = new PacketWriter();
            GasAlerts.Write(writer, text);
            byte[] bytes = writer.Written.ToArray();

            Assert.Equal(GasAlerts.HeaderLength + text.Length, bytes.Length);
            Assert.Equal(GasAlerts.Family, bytes[0]);
            Assert.Equal(ZoneOpcodes.ClientUpdateBase, bytes[0]);
            Assert.Equal(GasAlerts.SubOpcode, BitConverter.ToUInt16(bytes, 1));
            Assert.Equal((uint)text.Length, BitConverter.ToUInt32(bytes, 3));
            Assert.Equal(text, System.Text.Encoding.UTF8.GetString(bytes.AsSpan(7)));
        }
    }

    /// <summary>
    /// <b>Crate spawned, byte for byte.</b> The crate is an ordinary ground object, so it goes out
    /// through the same <c>AddLightweightNpc 0xd6</c> writer every item on the floor uses — which is
    /// exactly why the feature needs no new packet. This pins the client's own model and name id in
    /// the bytes.
    /// </summary>
    [Fact]
    public void TheCrateGoesOutThroughTheOrdinaryGroundObjectWriter()
    {
        AirdropCrate crate = Tables.Value.Crate;
        var spawn = new AddLightweightItem(
            Guid: 0x2000_0000_0000_0009,
            TransientId: 4242,
            GroundModelId: crate.GroundModelId,
            Position: new Vector3(-250f, 42f, 100f),
            NameId: crate.NameId);

        using var writer = new PacketWriter();
        spawn.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        Assert.Equal(ZoneOpcodes.AddLightweightNpc, bytes[0]);
        Assert.Equal(spawn.Length, bytes.Length);

        // Byte for byte against the SAME writer an ordinary ground item goes out through, with only
        // the model and the name id changed: that identity is the whole reason the crate needs no
        // new packet and no new pickup path.
        var ordinary = new AddLightweightItem(
            Guid: 0x2000_0000_0000_0009,
            TransientId: 4242,
            GroundModelId: 9066,
            Position: new Vector3(-250f, 42f, 100f),
            NameId: 12302);
        using var reference = new PacketWriter();
        ordinary.WriteTo(reference);
        byte[] referenceBytes = reference.Written.ToArray();

        Assert.Equal(referenceBytes.Length, bytes.Length);
        int differing = bytes.Where((b, i) => b != referenceBytes[i]).Count();
        Assert.True(differing > 0, "the crate's spawn is byte-identical to a bandage's");

        // The client's own model and name id really are in the bytes.
        Assert.Contains(true, SearchWindows(bytes, BitConverter.GetBytes(crate.GroundModelId)));
        Assert.Contains(true, SearchWindows(bytes, BitConverter.GetBytes(crate.NameId)));
    }

    /// <summary>
    /// <b>Opened → items.</b> The spill is a deterministic ring inside the client's own 2 m
    /// proximity ball, so the <c>[F]</c> panel lists a crate's contents as one set and every item is
    /// then an ordinary ground pickup.
    /// </summary>
    [Fact]
    public void OpeningACrateLaysEveryItemInsideTheProximityBall()
    {
        var airdrops = new MatchAirdrops(AirdropOptions.Default, matchSeed: 5);
        List<AirdropItem> contents = airdrops.RollContents(Tables.Value, dropIndex: 0);
        var crate = new Vector3(-250f, 42f, 100f);

        var placed = new List<Vector3>();
        for (int i = 0; i < contents.Count; i++)
        {
            Vector3 at = airdrops.SpillPositionFor(crate, i, contents.Count);
            Assert.Equal(crate.Y, at.Y);

            float distance = MathF.Sqrt(
                ((at.X - crate.X) * (at.X - crate.X)) + ((at.Z - crate.Z) * (at.Z - crate.Z)));
            Assert.Equal(AirdropOptions.Default.SpillRadiusMetres, distance, 3);

            // D70: the whole spill has to fall inside the client's own 2 m proximity ball, or the
            // panel stops listing a crate's contents together.
            Assert.True(distance <= Rulings.LootGates.PanelRadiusMetres);
            placed.Add(at);
        }

        // Deterministic and distinct: no two items share a point.
        Assert.Equal(placed.Count, placed.Distinct().Count());
        for (int i = 0; i < contents.Count; i++)
        {
            Assert.Equal(placed[i], airdrops.SpillPositionFor(crate, i, contents.Count));
        }
    }

    /// <summary>Same seed, same crates, in the same places — as with every other loot decision.</summary>
    [Fact]
    public void TheSameSeedDeliversTheSameCrates()
    {
        static (List<AirdropEvent> Events, List<AirdropItem> First) Run(ulong seed)
        {
            var airdrops = new MatchAirdrops(Immediate, seed);
            var events = new List<AirdropEvent>();
            airdrops.Tick(10_000_000, 1, Circle, Vector3.Zero, events);
            return (events, airdrops.RollContents(Tables.Value, 0));
        }

        (List<AirdropEvent> a, List<AirdropItem> firstA) = Run(1);
        (List<AirdropEvent> b, List<AirdropItem> firstB) = Run(1);
        (List<AirdropEvent> c, _) = Run(2);

        Assert.Equal(a, b);
        Assert.Equal(firstA, firstB);
        Assert.Equal(Circle.Centre, a[0].Position);
        Assert.Equal(Circle.Centre, c[0].Position); // Different seeds change flight/loot, not centre targeting.
    }

    /// <summary>The switch really removes the whole channel.</summary>
    [Fact]
    public void TurningAirdropsOffProducesNothingAtAll()
    {
        var airdrops = new MatchAirdrops(Immediate with { Enabled = false }, matchSeed: 1);
        var events = new List<AirdropEvent>();

        Assert.Equal(0, airdrops.Tick(10_000_000, 1, Circle, Vector3.Zero, events));
        Assert.Empty(events);
        Assert.Equal(0, airdrops.Announced);
    }

    /// <summary>A hand-edited airdrop file is refused rather than read into a silently empty crate.</summary>
    [Fact]
    public void ABrokenAirdropFileIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => AirdropTables.Parse(
            System.Text.Encoding.UTF8.GetBytes("""{"format":"cranberry.airdrop","formatVersion":9}"""),
            "v9"));

        Assert.Throws<InvalidDataException>(() => AirdropTables.Parse(
            System.Text.Encoding.UTF8.GetBytes(
                """{"format":"cranberry.airdrop","formatVersion":1,"crate":{}}"""),
            "no crate ids"));
    }

    private static IEnumerable<bool> SearchWindows(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            yield return haystack.AsSpan(i, needle.Length).SequenceEqual(needle);
        }
    }
}
