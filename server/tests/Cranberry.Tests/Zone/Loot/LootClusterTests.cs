using System.Numerics;
using Cranberry.Zone.Loot;

namespace Cranberry.Tests.Zone.Loot;

/// <summary>
/// The gun cluster (docs/39 §5): *"one AR-15 with two boxes next to it with 30 ammo in each."*
/// This is the reason ammunition can leave the two tables that used to carry it and still leave
/// every firearm on the map usable — and the reason a floor reads as a set rather than as loose
/// rounds scattered through empty rooms.
/// <para>
/// <b>D286 (2026-09-03 evening) REVERSES D272 back to TWO boxes.</b> D272 had cut the pair to one
/// because at two, 34.7 % of every ground object on the map was an ammunition box
/// (<c>AUDIT-loot.md</c> G2); the owner played that world and ruled it back to two. The shipped
/// world is therefore TWO boxes again, tested here as the primary
/// (<see cref="TheShippedWorldPlacesTwoBoxesPerGun"/>), and the single box is kept under test as the
/// A/B: <see cref="OneBoxIsStillPlacedCorrectlyWhenTheFileAsksForIt"/> runs the geometry against a
/// <c>boxesPerGun: 1</c> table.
/// </para>
/// </summary>
public sealed class LootClusterTests
{
    /// <summary>The shipped tables with <c>boxesPerGun</c> forced to 1 — the D272 world.</summary>
    private static readonly Lazy<LootTables> SingleTables = new(() =>
    {
        string path = LootDataPaths.Require(LootTables.DefaultFileName);
        string json = File.ReadAllText(path)
            .Replace("\"boxesPerGun\": 2", "\"boxesPerGun\": 1", StringComparison.Ordinal);
        return LootTables.Parse(System.Text.Encoding.UTF8.GetBytes(json), "boxesPerGun=1");
    });

    private static readonly Lazy<Z2LootLayout> SingleLayout = new(() =>
        Z2LootLayout.Build(LootRoster.Spawns, SingleTables.Value, matchSeed: 1));

    [Fact]
    public void TheShippedWorldPlacesTwoBoxesPerGun()
    {
        Z2LootLayout layout = LootRoster.Layout;
        LootTables tables = LootRoster.Tables;
        Assert.Equal(2, tables.BoxesPerGun);

        Span<LootClusterItem> boxes = stackalloc LootClusterItem[2];

        int guns = 0;
        int melee = 0;

        for (int i = 0; i < layout.MarkerCount; i += 13)
        {
            if (!layout.TryGet(i, out LootSpawnRoll roll) || layout.KindAt(i) != LootItemKind.Weapon)
            {
                continue;
            }

            int written = layout.ClusterFor(roll, boxes);
            if (!tables.TryGetCluster(roll.ItemDefinitionId, out LootClusterBox expected))
            {
                // Melee: the Machete and the Combat Knife get no ammunition, and must not
                // silently get an empty-count box either.
                Assert.Equal(0, written);
                Assert.Contains(roll.ItemDefinitionId, (uint[])[83, 84]);
                melee++;
                continue;
            }

            Assert.Equal(2, written);
            guns++;

            foreach (LootClusterItem box in boxes)
            {
                Assert.Equal(expected.ItemDefinitionId, box.ItemDefinitionId);
                Assert.Equal(expected.GroundModelId, box.GroundModelId);
                Assert.Equal(expected.NameId, box.NameId);

                // One magazine, never a dribble.
                Assert.Equal(expected.Count, box.Count);

                // Beside the gun, at its own height, within the declared offset.
                Assert.Equal(roll.Position.Y, box.Position.Y);
                float distance = Vector3.Distance(box.Position, roll.Position);
                Assert.Equal(tables.ClusterOffsetMetres, distance, 3);
            }

            // The pair straddles the gun rather than stacking on one side…
            Assert.True(
                Vector3.Distance(boxes[0].Position, boxes[1].Position)
                    > tables.ClusterOffsetMetres * 1.9f,
                "the two boxes are on the same side of the gun");

            // …perpendicular to the marker's own yaw, so a rifle lying along its yaw never has a
            // box under it.
            var forward = new Vector3(MathF.Sin(roll.Yaw), 0f, MathF.Cos(roll.Yaw));
            Vector3 toBox = Vector3.Normalize(boxes[0].Position - roll.Position);
            Assert.Equal(0f, Vector3.Dot(forward, toBox), 3);

            // The second box is turned, so a pair does not read as one object mirrored.
            Assert.Equal(roll.Yaw, boxes[0].Yaw);
            Assert.Equal(roll.Yaw + tables.ClusterSecondBoxYawOffset, boxes[1].Yaw, 4);

            // Addressable and traceable back to its gun. Z2's marker instance ids are unique but
            // span the whole 32-bit range (0x0001_5863 … 0xFFF9_B06D, 84,786 of them with the high
            // bit already set), so a box's identity is the pair, never a synthesised id.
            Assert.Equal(roll.InstanceId, boxes[0].GunInstanceId);
            Assert.Equal(roll.InstanceId, boxes[1].GunInstanceId);
            Assert.Equal(0, boxes[0].BoxIndex);
            Assert.Equal(1, boxes[1].BoxIndex);
            Assert.NotEqual(boxes[0].Key, boxes[1].Key);
        }

        Assert.True(guns > 200, $"only {guns} clustered guns were sampled");
        Assert.True(melee > 20, $"only {melee} melee weapons were sampled");
    }

    /// <summary>The D272 A/B: a <c>boxesPerGun: 1</c> table places exactly one box per gun,
    /// correctly, at the same offset and on the marker's own perpendicular.</summary>
    [Fact]
    public void OneBoxIsStillPlacedCorrectlyWhenTheFileAsksForIt()
    {
        Z2LootLayout layout = SingleLayout.Value;
        LootTables tables = SingleTables.Value;
        Assert.Equal(1, tables.BoxesPerGun);

        Span<LootClusterItem> boxes = stackalloc LootClusterItem[2];

        int guns = 0;
        for (int i = 0; i < layout.MarkerCount; i += 13)
        {
            if (!layout.TryGet(i, out LootSpawnRoll roll) || layout.KindAt(i) != LootItemKind.Weapon)
            {
                continue;
            }

            int written = layout.ClusterFor(roll, boxes);
            if (!tables.TryGetCluster(roll.ItemDefinitionId, out LootClusterBox expected))
            {
                Assert.Equal(0, written);
                continue;
            }

            Assert.Equal(1, written);
            guns++;

            LootClusterItem box = boxes[0];
            Assert.Equal(expected.ItemDefinitionId, box.ItemDefinitionId);
            Assert.Equal(expected.GroundModelId, box.GroundModelId);
            Assert.Equal(expected.NameId, box.NameId);
            Assert.Equal(expected.Count, box.Count);
            Assert.Equal(roll.Position.Y, box.Position.Y);
            Assert.Equal(tables.ClusterOffsetMetres, Vector3.Distance(box.Position, roll.Position), 3);

            var forward = new Vector3(MathF.Sin(roll.Yaw), 0f, MathF.Cos(roll.Yaw));
            Vector3 toBox = Vector3.Normalize(box.Position - roll.Position);
            Assert.Equal(0f, Vector3.Dot(forward, toBox), 3);

            Assert.Equal(roll.InstanceId, box.GunInstanceId);
            Assert.Equal(0, box.BoxIndex);
        }

        Assert.True(guns > 200, $"only {guns} clustered guns were sampled");
    }

    [Fact]
    public void NothingButAGunEverProducesACluster()
    {
        Z2LootLayout layout = LootRoster.Layout;
        Span<LootClusterItem> boxes = stackalloc LootClusterItem[2];

        for (int i = 0; i < layout.MarkerCount; i += 11)
        {
            if (!layout.TryGet(i, out LootSpawnRoll roll) || layout.KindAt(i) == LootItemKind.Weapon)
            {
                continue;
            }

            Assert.Equal(0, layout.ClusterFor(roll, boxes));
        }
    }

    /// <summary>
    /// A caller with no room for the whole cluster gets nothing rather than half of one: half a
    /// cluster is a wrong world, not a smaller one. At D286's two boxes that means a one-wide span
    /// refuses and a two-wide span succeeds; on a <c>boxesPerGun: 1</c> table a one-wide span
    /// succeeds and an empty one refuses.
    /// </summary>
    [Fact]
    public void ASpanTooSmallForTheWholeClusterProducesNoBoxAtAll()
    {
        Z2LootLayout pair = LootRoster.Layout;
        Z2LootLayout single = SingleLayout.Value;

        for (int i = 0; i < pair.MarkerCount; i++)
        {
            if (!pair.TryGet(i, out LootSpawnRoll roll)
                || !LootRoster.Tables.TryGetCluster(roll.ItemDefinitionId, out _))
            {
                continue;
            }

            Span<LootClusterItem> two = stackalloc LootClusterItem[2];
            Span<LootClusterItem> one = stackalloc LootClusterItem[1];

            // The shipped two-box world: the whole pair or nothing.
            Assert.Equal(2, pair.ClusterFor(roll, two));
            Assert.Equal(0, pair.ClusterFor(roll, one));
            Assert.Equal(0, pair.ClusterFor(roll, []));

            // The one-box A/B: a single box fits a one-wide span, nothing fits an empty one.
            Assert.Equal(1, single.ClusterFor(roll, one));
            Assert.Equal(0, single.ClusterFor(roll, []));
            return;
        }

        Assert.Fail("no clustered gun anywhere on the map");
    }
}
