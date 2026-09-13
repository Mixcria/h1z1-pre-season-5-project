using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

// The guid layout and the disjointness proof of docs/22 §7.1, asserted against the real constants
// in the tree rather than against the numbers quoted in the design.
public sealed class EntityIdTests
{
    [Fact]
    public void CreateRoundTripsKindMatchIdAndSequence()
    {
        EntityId id = EntityId.Create(EntityKind.Vehicle, matchId: 3, sequence: 12);

        Assert.Equal(EntityKind.Vehicle, id.Kind);
        Assert.Equal(3, id.MatchId);
        Assert.Equal(12UL, id.Sequence);
        Assert.False(id.IsNone);
    }

    [Fact]
    public void TheDefaultIsNone()
    {
        Assert.True(EntityId.None.IsNone);
        Assert.Equal(EntityKind.None, EntityId.None.Kind);
        Assert.Equal(default, EntityId.None);
    }

    [Fact]
    public void KindNoneIsReservedForTheLoginHostAndCannotBeMinted() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EntityId.Create(EntityKind.None, 1, 1));

    [Fact]
    public void ASequenceWiderThanFortyBitsIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EntityId.Create(EntityKind.Character, 1, EntityId.MaxSequence + 1));

        // The boundary itself is legal.
        EntityId id = EntityId.Create(EntityKind.Character, 1, EntityId.MaxSequence);
        Assert.Equal(EntityId.MaxSequence, id.Sequence);
    }

    [Fact]
    public void MatchGuidsAreDisjointFromRosterCharacterKeys()
    {
        // CharacterRosterStore._nextEntityKey starts at 0x1000 and ascends by 1 (CharacterRosterStore.cs:17,173).
        for (ulong key = 0x1001; key < 0x1001 + 8192; key++)
        {
            Assert.False(EntityId.IsMatchAllocated(key));
        }
    }

    [Fact]
    public void MatchGuidsAreDisjointFromTheLegacyParachuteOffset()
    {
        var options = new ZoneOptions();

        // The chute guid today is rosterKey + 0x1000, so it stays in the 0x00 top byte.
        for (ulong key = 0x1001; key < 0x1001 + 4096; key++)
        {
            Assert.False(EntityId.IsMatchAllocated(key + options.ParachuteGuidOffset));
        }
    }

    [Fact]
    public void MatchGuidsAreDisjointFromTheLegacyLootBases()
    {
        Assert.False(EntityId.IsMatchAllocated(LootWorld.DefaultWorldGuidBase));
        Assert.False(EntityId.IsMatchAllocated(LootWorld.DefaultItemGuidBase));

        // 0x20 and 0x31 are the top bytes; the whole legacy range stays outside 0x01-0x06.
        Assert.False(EntityId.IsMatchAllocated(LootWorld.DefaultWorldGuidBase + 1_000_000));
        Assert.False(EntityId.IsMatchAllocated(LootWorld.DefaultItemGuidBase + 1_000_000));
    }

    [Fact]
    public void EveryMintedKindIsInsideTheMatchAllocatedSpace()
    {
        foreach (EntityKind kind in Enum.GetValues<EntityKind>())
        {
            if (kind == EntityKind.None)
            {
                continue;
            }

            EntityId id = EntityId.Create(kind, matchId: ushort.MaxValue, sequence: EntityId.MaxSequence);
            Assert.True(EntityId.IsMatchAllocated(id.Value), $"{kind} escaped the match space.");
        }
    }

    [Fact]
    public void OrderingFollowsTheRawValue()
    {
        EntityId first = EntityId.Create(EntityKind.Character, 1, 1);
        EntityId second = EntityId.Create(EntityKind.Character, 1, 2);
        EntityId otherMatch = EntityId.Create(EntityKind.Character, 2, 1);

        Assert.True(first.CompareTo(second) < 0);
        Assert.True(second.CompareTo(otherMatch) < 0);
        Assert.Equal(0, first.CompareTo(first));
    }

    [Fact]
    public void ToStringIsLogReadable()
    {
        Assert.Equal("Vehicle#3:0000000012", EntityId.Create(EntityKind.Vehicle, 3, 12).ToString());
        Assert.Equal("None", EntityId.None.ToString());
    }
}
