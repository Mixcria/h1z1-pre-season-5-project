using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// docs/22 §7.2: per-viewer transient ids, reserved 0-15, recycled LIFO on despawn.
public sealed class TransientIdTableTests
{
    private static EntityId Item(ulong sequence) =>
        EntityId.Create(EntityKind.GroundItem, matchId: 1, sequence: sequence);

    [Fact]
    public void AllocationStartsAboveTheReservedIds()
    {
        var table = new TransientIdTable();

        Assert.Equal(TransientIdTable.FirstAllocated, table.Acquire(Item(1)));
        Assert.True(TransientIdTable.LocalPlayer < TransientIdTable.FirstAllocated);
    }

    [Fact]
    public void AcquireIsStableForARepeatedId()
    {
        var table = new TransientIdTable();
        uint first = table.Acquire(Item(1));

        Assert.Equal(first, table.Acquire(Item(1)));
        Assert.Equal(1, table.LiveCount);
    }

    [Fact]
    public void ReleaseThenAcquireIsLifo()
    {
        var table = new TransientIdTable();
        uint a = table.Acquire(Item(1));
        uint b = table.Acquire(Item(2));

        table.Release(Item(1));
        table.Release(Item(2));

        Assert.Equal(b, table.Acquire(Item(3)));
        Assert.Equal(a, table.Acquire(Item(4)));
    }

    [Fact]
    public void AnIdIsNeverHeldByTwoEntitiesAtOnce()
    {
        var table = new TransientIdTable();
        var live = new Dictionary<uint, EntityId>();

        for (ulong sequence = 1; sequence <= 500; sequence++)
        {
            EntityId id = Item(sequence);
            uint transientId = table.Acquire(id);

            Assert.False(live.ContainsKey(transientId), $"{transientId} was double-issued.");
            live[transientId] = id;

            // Churn: drop every third entity, exactly as interest management would.
            if (sequence % 3 == 0)
            {
                table.Release(id);
                live.Remove(transientId);
            }
        }

        Assert.Equal(live.Count, table.LiveCount);
    }

    [Fact]
    public void OneHundredFiftyLiveIdsStayInsideTwoVarintBytes()
    {
        var table = new TransientIdTable();
        for (ulong sequence = 1; sequence <= 150; sequence++)
        {
            table.Acquire(Item(sequence));
        }

        Assert.Equal(150, table.LiveCount);
        Assert.Equal(2, ClientVarInt.Length(table.Peak));
    }

    [Fact]
    public void RecyclingKeepsThePeakFlatAcrossALongMatch()
    {
        var table = new TransientIdTable();
        for (ulong sequence = 1; sequence <= 20_000; sequence++)
        {
            EntityId id = Item(sequence);
            table.Acquire(id);
            table.Release(id);
        }

        Assert.Equal(TransientIdTable.FirstAllocated, table.Peak);
        Assert.Equal(0, table.LiveCount);
    }

    [Fact]
    public void ResolveMapsAnEchoedIdBackToItsEntity()
    {
        var table = new TransientIdTable();
        EntityId id = Item(9);
        uint transientId = table.Acquire(id);

        Assert.True(table.TryResolve(transientId, out EntityId resolved));
        Assert.Equal(id, resolved);
        Assert.True(table.TryGet(id, out uint again));
        Assert.Equal(transientId, again);

        table.Release(id);
        Assert.False(table.TryResolve(transientId, out _));
        Assert.False(table.TryGet(id, out _));
    }

    [Fact]
    public void ClearResetsTheWholeTable()
    {
        var table = new TransientIdTable();
        table.Acquire(Item(1));
        table.Acquire(Item(2));
        table.Clear();

        Assert.Equal(0, table.LiveCount);
        Assert.Equal(0u, table.Peak);
        Assert.Equal(TransientIdTable.FirstAllocated, table.Acquire(Item(3)));
    }

    [Fact]
    public void ReleasingAnUnknownEntityIsANoOp()
    {
        var table = new TransientIdTable();
        table.Release(Item(42));

        Assert.Equal(0, table.LiveCount);
    }

    /// <summary>
    /// <b>Id 1 is the viewer's own actor and is never handed to a peer (D158).</b> The client drops
    /// any <c>82 15</c> whose owner id equals its own transient id (S5c §0 finding 2) and would
    /// relay a peer's pose onto itself, so allocation starting at 16 is a correctness rule, not
    /// tidiness.
    /// </summary>
    [Fact]
    public void TheLocalPlayerIdIsNeverAllocatedToAPeer()
    {
        var table = new TransientIdTable();

        for (ulong sequence = 1; sequence <= 200; sequence++)
        {
            uint id = table.Acquire(EntityId.Create(EntityKind.Character, 1, sequence));
            Assert.NotEqual(TransientIdTable.LocalPlayer, id);
            Assert.NotEqual(0u, id);
            Assert.True(id >= TransientIdTable.FirstAllocated);
        }

        Assert.Equal(1u, TransientIdTable.LocalPlayer);
    }

    /// <summary>
    /// A released id goes back on the free list, so it can be re-issued - but only after the despawn
    /// has been queued. Re-issuing it must produce a working binding, not a stale one.
    /// </summary>
    [Fact]
    public void ARecycledIdBindsCleanlyToItsNewEntity()
    {
        var table = new TransientIdTable();
        EntityId first = EntityId.Create(EntityKind.Character, 1, 1);
        EntityId second = EntityId.Create(EntityKind.Character, 1, 2);

        uint id = table.Acquire(first);
        table.Release(first);
        Assert.Equal(id, table.Acquire(second));

        Assert.True(table.TryResolve(id, out EntityId resolved));
        Assert.Equal(second, resolved);
        Assert.False(table.TryGet(first, out _));
    }

}
