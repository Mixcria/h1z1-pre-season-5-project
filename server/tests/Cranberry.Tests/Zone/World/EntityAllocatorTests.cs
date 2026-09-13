using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// The allocator is the answer to the per-session identity collision of docs/22 §7.1: LootWorld is
// rebuilt with the same fixed bases for every connection, so two sessions mint identical loot guids
// and identical transient ids immediately.
public sealed class EntityAllocatorTests
{
    [Fact]
    public void TheFirstGuidIsSequenceOne()
    {
        var allocator = new EntityAllocator(7);
        EntityId first = allocator.Next(EntityKind.Character);

        Assert.Equal(1UL, first.Sequence);
        Assert.Equal(7, first.MatchId);
        Assert.Equal(EntityKind.Character, first.Kind);
        Assert.Equal(1UL, allocator.Issued);
    }

    [Fact]
    public void OneHundredThousandGuidsAcrossEveryKindNeverRepeat()
    {
        var allocator = new EntityAllocator(1);
        EntityKind[] kinds =
        [
            EntityKind.Character,
            EntityKind.Vehicle,
            EntityKind.GroundItem,
            EntityKind.ItemInstance,
            EntityKind.Container,
            EntityKind.Projectile,
        ];

        var seen = new HashSet<ulong>(100_000);
        for (int i = 0; i < 100_000; i++)
        {
            EntityId id = allocator.Next(kinds[i % kinds.Length]);
            Assert.True(seen.Add(id.Value), $"Allocator repeated {id} after {i} allocations.");
        }

        Assert.Equal(100_000, seen.Count);
        Assert.Equal(100_000UL, allocator.Issued);
    }

    [Fact]
    public void TheSequenceIsSharedAcrossKindsSoAKindChangeCannotAlias()
    {
        var allocator = new EntityAllocator(1);
        EntityId character = allocator.Next(EntityKind.Character);
        EntityId vehicle = allocator.Next(EntityKind.Vehicle);

        Assert.NotEqual(character.Sequence, vehicle.Sequence);
        Assert.NotEqual(character.Value, vehicle.Value);
    }

    [Fact]
    public void TwoMatchesNeverCollide()
    {
        var first = new EntityAllocator(1);
        var second = new EntityAllocator(2);

        var seen = new HashSet<ulong>();
        for (int i = 0; i < 10_000; i++)
        {
            Assert.True(seen.Add(first.Next(EntityKind.GroundItem).Value));
            Assert.True(seen.Add(second.Next(EntityKind.GroundItem).Value));
        }

        Assert.Equal(20_000, seen.Count);
    }

    [Fact]
    public void TheKindByteIsNeverTheRosterSpace()
    {
        var allocator = new EntityAllocator(4);
        for (int i = 0; i < 1_000; i++)
        {
            EntityId id = allocator.Next(EntityKind.Vehicle);
            Assert.NotEqual(EntityKind.None, id.Kind);
            Assert.True(EntityId.IsMatchAllocated(id.Value));
        }
    }

    [Fact]
    public void AllocatingIsFreeOfManagedAllocation()
    {
        var allocator = new EntityAllocator(1);
        allocator.Next(EntityKind.Character);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            _ = allocator.Next(EntityKind.GroundItem);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
