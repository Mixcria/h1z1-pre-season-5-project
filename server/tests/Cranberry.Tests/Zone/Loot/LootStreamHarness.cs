using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// A stand-in for the parts of <c>ZoneService</c> the loot streamer talks to: the session's
/// <see cref="LootWorld"/> (which mints the guids and transient ids), its <see cref="MatchLoot"/>
/// working set, and the <c>FullNpcSent</c> guid set whose bookkeeping an eviction must keep straight
/// (docs/52 §5d).
/// <para>
/// It exists so these tests exercise the <b>whole loop</b> — plan, mint, note, evict, note — rather
/// than <see cref="MatchLoot"/> in isolation. The two places a streamer breaks are both in the loop
/// and neither is visible from inside the planner: a landing burst that is never adopted, and an
/// eviction whose server-side bookkeeping is only half done.
/// </para>
/// </summary>
internal sealed class LootStreamHarness
{
    /// <summary>The wave-4 play-test's own touchdown, <c>logs/host-20260830-090725.log</c> 09:12:49.488.</summary>
    internal static readonly Vector3 Landing = new(1624.66f, 42.35f, -2173.87f);

    internal LootStreamHarness(LootStreamOptions? options = null, Z2LootLayout? layout = null)
    {
        Options = options ?? LootStreamOptions.Default;
        // Wave 8: the layout is overridable so a test can run the SAME sweep against the
        // pre-wave-8 world (SpawnChanceOverride = 0.27) and the owner's, which is what turns a
        // coverage figure into evidence of a fix rather than an assertion of one.
        Layout = layout ?? LootRoster.Layout;
    }

    internal LootStreamOptions Options { get; }

    internal LootWorld World { get; } = new();

    internal MatchLoot Loot { get; } = new();

    /// <summary>Mirrors <c>GatewaySessionState.FullNpcSent</c>: guids whose <c>0xda</c> has gone out.</summary>
    internal HashSet<ulong> FullNpcSent { get; } = [];

    /// <summary>Bytes this harness believes went on the link, tick by tick.</summary>
    internal List<int> TickBytes { get; } = [];

    internal Z2LootLayout Layout { get; }

    /// <summary>
    /// One re-stream tick, end to end, exactly as <c>ZoneService.PumpWorld</c>'s loot arm is
    /// specified to run it in docs/52 <c>## Integration</c> step 6: evictions first, then spawns,
    /// then at most one <c>ProximateItems</c> republish.
    /// </summary>
    internal LootStreamPlan Tick(in Vector3 centre)
    {
        LootStreamPlan plan = Loot.PlanRestream(centre, Layout, Options, World.TransientIdHeadroom);
        Apply(plan);
        return plan;
    }

    /// <summary>A tick that only runs when <see cref="MatchLoot.ShouldRestream"/> says one is due.</summary>
    internal LootStreamPlan? TickIfDue(in Vector3 centre) =>
        Loot.ShouldRestream(centre, Options) ? Tick(centre) : null;

    internal void Apply(LootStreamPlan plan)
    {
        foreach (ulong guid in plan.Evictions)
        {
            // The three things an eviction must do server-side. FullNpcSent is the one docs/52 §5d
            // singles out: the pickup path already removes from it, and an eviction that did not
            // would grow it for the life of the session until it stopped meaning what its name says.
            Assert.True(World.TryEvict(guid, out _));
            FullNpcSent.Remove(guid);
            Assert.True(World.WasEvicted(guid));
        }

        foreach (LootStreamSpawn spawn in plan.Spawns)
        {
            GroundLootItem item = World.Spawn(
                spawn.ItemDefinitionId,
                spawn.GroundModelId,
                spawn.Position,
                spawn.Count,
                spawn.NameId);
            Loot.NoteSpawned(spawn.Key, item.WorldGuid, item.Position);
            FullNpcSent.Add(item.WorldGuid);
        }

        TickBytes.Add(plan.EstimatedBytes);
    }

    /// <summary>
    /// The landing burst, reproduced: <c>ZoneService.SpawnRealGroundLoot</c>'s 80 m disc and its cap
    /// of 128 <i>including</i> the ammunition boxes, then adopted into the working set.
    /// <para>
    /// The adoption is the point. The landing burst does not go through
    /// <see cref="MatchLoot.PlanRestream"/>, so if <c>ZoneService</c> does not hand each object to
    /// <see cref="MatchLoot.NoteSpawned"/>, the first re-stream tick sees an empty working set and
    /// spawns all 128 of them again — the client would hold two of every item, and only one of each
    /// pair could ever be destroyed.
    /// </para>
    /// </summary>
    internal int Land(in Vector3 centre, float radius = 80f, int cap = 128, bool adopt = true)
    {
        Span<int> found = stackalloc int[512];
        Span<LootClusterItem> cluster = stackalloc LootClusterItem[2];
        int matched = Math.Min(Layout.QueryLive(centre, radius, found), found.Length);
        int spawned = 0;
        int boxes = 0;

        for (int i = 0; i < matched && spawned + boxes < cap; i++)
        {
            if (!Layout.TryGet(found[i], out LootSpawnRoll roll))
            {
                continue;
            }

            Place(LootStreamKey.ForMarker(found[i]), roll.ItemDefinitionId, roll.GroundModelId, roll.Position, roll.Count, roll.NameId, adopt);
            spawned++;

            int pair = Layout.ClusterFor(roll, cluster);
            for (int box = 0; box < pair; box++)
            {
                LootClusterItem ammunition = cluster[box];
                Place(LootStreamKey.ForBox(ammunition), ammunition.ItemDefinitionId, ammunition.GroundModelId, ammunition.Position, ammunition.Count, ammunition.NameId, adopt);
                boxes++;
            }
        }

        return spawned + boxes;
    }

    /// <summary>A pickup: destructive in the registry and permanent in the streamer's taken sets.</summary>
    internal GroundLootItem PickUp(ulong worldGuid)
    {
        Assert.True(World.TryClaim(worldGuid, out GroundLootItem? item));
        FullNpcSent.Remove(worldGuid);
        Assert.True(Loot.NoteTaken(worldGuid));
        return item!;
    }

    private void Place(
        in LootStreamKey key,
        uint definitionId,
        uint modelId,
        in Vector3 position,
        uint count,
        uint nameId,
        bool adopt)
    {
        GroundLootItem item = World.Spawn(definitionId, modelId, position, count, nameId);
        FullNpcSent.Add(item.WorldGuid);
        if (adopt)
        {
            Loot.NoteSpawned(key, item.WorldGuid, item.Position);
        }
    }
}
