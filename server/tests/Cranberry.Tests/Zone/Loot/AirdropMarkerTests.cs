using System.Collections;
using System.Numerics;
using System.Reflection;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

public sealed partial class AirdropGatewayTests
{
    private static void Move(SoeConnection viewer, Vector3 position)
    {
        var movement = Get<SessionMovementState>(viewer.Tag!, "Movement");
        movement.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString("020018F6B21C00000000")));
        movement.PinPlayer(position);
    }

    private static (long Clock, AirdropCrateState Crate) Land(Fixture f, SoeConnection viewer)
    {
        f.Pump(viewer, 0);
        var payload = Assert.Single(Assert.Single(f.Controller(viewer).Flights).Payloads);
        Move(viewer, payload.ImpactPosition);
        f.Pump(viewer, payload.ImpactAtMs);
        return (payload.ImpactAtMs, Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(
            viewer.Tag!, "AirdropCrates")).Value);
    }

    private static List<byte[]> MarkerTags(Fixture f, SoeConnection viewer, byte sub) => f.Recorder.Sent
        .Where(p => ReferenceEquals(p.Connection, viewer) && p.Bytes.Length >= 15
            && p.Bytes[1] == 0x0f && p.Bytes[2] == sub
            && BitConverter.ToUInt32(p.Bytes, 11) == f.Options.MarkerEffectId)
        .Select(p => p.Bytes).ToList();

    private static int MarkerCount(Fixture f) => ((IDictionary)typeof(ZoneService)
        .GetField("_airdropMarkers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Service)!).Count;

    [Fact]
    public void MarkerIsCrateOwnedAndIndependentOfNearLootAndGrenadeRanges()
    {
        using var f = new Fixture();
        var first = f.Add();
        var peer = f.Add();
        var unrelated = f.Add(666);
        var (clock, crate) = Land(f, first);
        Move(peer, crate.Position + new Vector3(500, 0, 0));
        Move(unrelated, crate.Position);
        f.Pump(peer, clock);
        Call(f.Service, "SyncAirdropMarkers", unrelated.Tag);
        ulong guid = BitConverter.ToUInt64(Assert.Single(MarkerTags(f, first, 0x15)), 3);
        Assert.Equal(guid, BitConverter.ToUInt64(Assert.Single(MarkerTags(f, peer, 0x15)), 3));
        Assert.Empty(MarkerTags(f, unrelated, 0x15));
        Assert.Equal(1, MarkerCount(f));
        f.Pump(first, clock + 1);
        f.Pump(peer, clock + 1);
        Assert.Single(MarkerTags(f, first, 0x15));
        Assert.Single(MarkerTags(f, peer, 0x15));

        // Presentation must create the anchor before attaching its persistent tag.
        var sent = f.Recorder.Sent.Where(p => ReferenceEquals(p.Connection, first)).Select(p => p.Bytes).ToList();
        int tagAt = sent.FindIndex(p => p.Length >= 15 && p[1] == 0x0f && p[2] == 0x15
            && BitConverter.ToUInt64(p, 3) == guid);
        Assert.True(tagAt > 0);
        Assert.Equal(ZoneOpcodes.AddLightweightNpc, sent[tagAt - 1][1]);

        // First receiver's departure removes only that receiver; it cannot retire shared loot/FX.
        Call(f.Service, "LeaveSharedLoot", first.Tag);
        Assert.Single(MarkerTags(f, first, 0x16));
        Assert.Empty(MarkerTags(f, peer, 0x16));
        Assert.Equal(1, MarkerCount(f));
        var late = f.Add();
        Move(late, crate.Position);
        f.Pump(late, clock + 2);
        Assert.Equal(guid, BitConverter.ToUInt64(Assert.Single(MarkerTags(f, late, 0x15)), 3));
        Assert.Equal(crate.UnlockAtMs, Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(
            late.Tag!, "AirdropCrates")).Value.UnlockAtMs);
    }

    [Fact]
    public void MarkerInterestHysteresisReentryAndWorldGenerationAreIdempotent()
    {
        using var f = new Fixture();
        var viewer = f.Add();
        var (clock, crate) = Land(f, viewer);
        ulong guid = BitConverter.ToUInt64(Assert.Single(MarkerTags(f, viewer, 0x15)), 3);
        Move(viewer, crate.Position + new Vector3(f.Options.MarkerRangeMetres + 50, 0, 0));
        f.Pump(viewer, clock + 1);
        Assert.Empty(MarkerTags(f, viewer, 0x16));
        Move(viewer, crate.Position + new Vector3(f.Options.MarkerRangeMetres + f.Options.MarkerHysteresisMetres + 1, 0, 0));
        f.Pump(viewer, clock + 2);
        Assert.Single(MarkerTags(f, viewer, 0x16));
        var packets = f.Recorder.Sent.Select(p => p.Bytes).ToList();
        int removed = packets.FindLastIndex(p => p.Length >= 15 && p[1] == 0x0f && p[2] == 0x16);
        Assert.Equal(new byte[] { 0x0f, 1 }, packets[removed + 1].AsSpan(1, 2).ToArray());
        Assert.Equal(guid, BitConverter.ToUInt64(packets[removed + 1], 3));
        Move(viewer, crate.Position + new Vector3(f.Options.MarkerRangeMetres + 50, 0, 0));
        f.Pump(viewer, clock + 3);
        Assert.Single(MarkerTags(f, viewer, 0x15)); // exit band does not authorize new entry
        Move(viewer, crate.Position);
        f.Pump(viewer, clock + 4);
        Assert.Equal(2, MarkerTags(f, viewer, 0x15).Count);
        Set(viewer.Tag!, "WorldGeneration", Get<int>(viewer.Tag!, "WorldGeneration") + 1);
        f.Pump(viewer, clock + 5);
        f.Pump(viewer, clock + 6);
        Assert.Equal(3, MarkerTags(f, viewer, 0x15).Count);
        Assert.Single(MarkerTags(f, viewer, 0x16)); // old-world removals must not target the new world
        Call(f.Service, "LeaveSharedLoot", viewer.Tag);
        Assert.Equal(0, MarkerCount(f));
        Assert.Equal(2, MarkerTags(f, viewer, 0x16).Count);
    }

    [Fact]
    public void ClaimedCrateRetiresAllMarkersAndCannotResurrectForALateViewer()
    {
        using var f = new Fixture(oldMatch: true);
        var first = f.Add();
        var peer = f.Add();
        var (clock, crate) = Land(f, first);
        Move(peer, crate.Position);
        f.Pump(peer, clock);
        ulong crateGuid = Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(first.Tag!, "AirdropCrates")).Key;
        Assert.True((bool)Call(f.Service, "TryOpenAirdropCrate", first, first.Tag, crateGuid, "marker-test")!);
        Assert.Single(MarkerTags(f, first, 0x16));
        Assert.Single(MarkerTags(f, peer, 0x16));
        Assert.Equal(0, MarkerCount(f));
        int lootCount = Get<LootWorld>(first.Tag!, "Loot").Count;
        Assert.False((bool)Call(f.Service, "TryOpenAirdropCrate", first, first.Tag, crateGuid, "duplicate")!);
        Assert.Equal(lootCount, Get<LootWorld>(first.Tag!, "Loot").Count);
        var late = f.Add();
        Move(late, crate.Position);
        f.Pump(late, clock + 1);
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(late.Tag!, "AirdropCrates"));
        Assert.Empty(MarkerTags(f, late, 0x15));
    }

    [Fact]
    public void CulledCrateReturnsWithTheSameContentsUnlockAndSingleMarker()
    {
        using var f = new Fixture();
        var viewer = f.Add();
        var (clock, crate) = Land(f, viewer);
        ulong oldGuid = Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates")).Key;
        Get<MatchLoot>(viewer.Tag!, "StreamedLoot").NoteEvicted(oldGuid);
        Call(f.Service, "EvictGroundLoot", viewer, viewer.Tag, oldGuid);
        Move(viewer, crate.Position + new Vector3(f.Options.MarkerRangeMetres + f.Options.MarkerHysteresisMetres + 100, 0, 0));
        f.Pump(viewer, clock + 1);
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
        Move(viewer, crate.Position);
        f.Pump(viewer, clock + 2);
        var restored = Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
        Assert.NotEqual(oldGuid, restored.Key);
        Assert.Same(crate, restored.Value);
        Assert.Equal(2, MarkerTags(f, viewer, 0x15).Count);
        Assert.Equal(1, MarkerCount(f));
        f.Pump(viewer, clock + 3);
        Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
    }

    [Fact]
    public void ConfiguredCrateExpiryRetiresEveryViewerAndLateCatchupCannotRestartIt()
    {
        using var f = new Fixture(droppedLifetimeMs: 5000);
        var viewer = f.Add();
        var peer = f.Add();
        var (clock, crate) = Land(f, viewer);
        Move(peer, crate.Position);
        f.Pump(peer, clock + 4000);
        f.Pump(viewer, clock + 5000);
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(peer.Tag!, "AirdropCrates"));
        Assert.Single(MarkerTags(f, viewer, 0x16));
        Assert.Single(MarkerTags(f, peer, 0x16));
        Assert.Equal(0, MarkerCount(f));
        var late = f.Add();
        Move(late, crate.Position);
        f.Pump(late, clock + 6000);
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(late.Tag!, "AirdropCrates"));
        Assert.Empty(MarkerTags(f, late, 0x15));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterFlightFailureDoesNotFreezeLandedCrateInterestOrSharedExpiry(bool unsupportedTerrain)
    {
        using var f = new Fixture(droppedLifetimeMs: 5000, maxDrops: 2);
        var viewer = f.Add();
        var peer = f.Add();
        var (clock, crate) = Land(f, viewer);
        Move(peer, crate.Position);
        f.Pump(peer, clock);
        ulong oldGuid = Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(
            viewer.Tag!, "AirdropCrates")).Key;
        Get<MatchLoot>(viewer.Tag!, "StreamedLoot").NoteEvicted(oldGuid);
        Call(f.Service, "EvictGroundLoot", viewer, viewer.Tag, oldGuid);
        Move(viewer, crate.Position + new Vector3(f.Options.MarkerRangeMetres + f.Options.MarkerHysteresisMetres + 100, 0, 0));

        // Force the next flight to encounter unsupported landing geometry after one crate landed.
        // The same pump that catches this failure must still retire the out-of-range marker view.
        Set(f.Controller(viewer), "NextDropAtMs", clock + 1);
        var invalidGas = new GasController(new GasSettings
        {
            PlayAreaCentre = unsupportedTerrain ? new Vector3(100_000, 0, 100_000) : new Vector3(float.NaN),
            CentrePlan = GasCentrePlan.Drift,
        });
        invalidGas.Start(f.StartedAtMs, 42);
        Set(viewer.Tag!, "Gas", invalidGas);
        f.Pump(viewer, clock + 1);
        Assert.Equal(1, f.Controller(viewer).Announced);
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
        Assert.Single(MarkerTags(f, viewer, 0x16));
        Assert.Empty(MarkerTags(f, peer, 0x16));

        // Subsequent pumps take the already-faulted path, but keep the original crate and clocks.
        Move(viewer, crate.Position);
        f.Pump(viewer, clock + 2);
        var restored = Assert.Single(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
        Assert.NotEqual(oldGuid, restored.Key);
        Assert.Same(crate, restored.Value);
        Assert.Equal(2, MarkerTags(f, viewer, 0x15).Count);
        Assert.Equal(1, MarkerCount(f));
        f.Pump(viewer, clock + 5000);
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(viewer.Tag!, "AirdropCrates"));
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(peer.Tag!, "AirdropCrates"));
        Assert.Equal(2, MarkerTags(f, viewer, 0x16).Count);
        Assert.Single(MarkerTags(f, peer, 0x16));
        Assert.Equal(0, MarkerCount(f));
        var late = f.Add();
        Move(late, crate.Position);
        f.Pump(late, clock + 6000);
        Assert.Empty(Get<Dictionary<ulong, AirdropCrateState>>(late.Tag!, "AirdropCrates"));
        Assert.Empty(MarkerTags(f, late, 0x15));
    }

    [Fact]
    public void DisconnectClearsViewerReferencesAndLastDepartureRetiresTheMarker()
    {
        using var f = new Fixture();
        var first = f.Add();
        var peer = f.Add();
        var (clock, crate) = Land(f, first);
        Move(peer, crate.Position);
        f.Pump(peer, clock);
        first.Disconnect();
        f.Service.OnDisconnected(first, DisconnectCause.ServerRequested);
        Assert.Equal(1, MarkerCount(f));
        f.Pump(peer, clock + 1);
        Assert.Empty(MarkerTags(f, peer, 0x16));
        peer.Disconnect();
        f.Service.OnDisconnected(peer, DisconnectCause.ServerRequested);
        Assert.Equal(0, MarkerCount(f));
    }
}
