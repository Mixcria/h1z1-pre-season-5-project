using System.Xml.Linq;
using Cranberry.Login;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Login;

public sealed class GameWorldCatalogTests
{
    [Theory]
    [InlineData(1u, 13u, MatchMode.Solo, "Solo EU World 1")]
    [InlineData(6u, 5u, MatchMode.Duos, "Duos EU World 1")]
    [InlineData(7u, 7u, MatchMode.Fives, "Fives EU World 1")]
    [InlineData(8u, 13u, MatchMode.Solo, "Hosted Games")]
    [InlineData(9u, 13u, MatchMode.Solo, "Solo EU World 2")]
    [InlineData(10u, 5u, MatchMode.Duos, "Duos EU World 2")]
    [InlineData(11u, 7u, MatchMode.Fives, "Fives EU World 2")]
    public void AdvertisedWorldsAndAdmissionAgree(uint id, uint gameMode, MatchMode mode, string label)
    {
        Assert.True(GameWorldCatalog.TryGet(id, out var world));
        Assert.Equal(gameMode, world.GameModeId);
        Assert.Equal(label, world.DisplayName);
        GameServerEntry entry = world.ServerEntry();
        Assert.Equal((ulong)id, entry.Id);
        Assert.Equal(label, entry.Name);
        Assert.True(entry.IsAllowed);
        Assert.Equal(0, entry.State);
        var population = XElement.Parse(entry.Info.ToDocument());
        Assert.Equal(gameMode, (uint)population.Attribute("Mode")!);
        Assert.Equal(id == GameWorldCatalog.PrimaryWorldId ? "1" : "0", (string?)population.Attribute("IsLogin"));
        Assert.Equal(world.IsHosted ? "1" : "0", (string?)population.Attribute("IsEvt"));
        Assert.True(MatchAdmissionRegistry.Default.TryGetDefinition(id, out var definition));
        Assert.Equal(gameMode, definition.GameModeId);
        Assert.Equal(label, definition.DisplayName);
        var admission = MatchAdmissionRegistry.Default.Resolve(new(id, "", 0, 1, 1), 42);
        Assert.Equal(mode, admission.Mode);
        Assert.Equal(world.IsHosted ? MatchQueueKind.Hosted : MatchQueueKind.Public, admission.QueueKind);
        Assert.Equal(!world.IsHosted && mode == MatchMode.Solo, BountyEligibility.IsEligible(admission));
    }

    [Fact]
    public void OtherRegionsRemainReservedAndOnlyPrimaryWorldCanBeTheLoginHost()
    {
        Assert.Equal(GameWorldCatalog.Default.Count, GameWorldCatalog.Default.Select(world => world.WorldId).Distinct().Count());
        Assert.DoesNotContain(GameWorldCatalog.Default, world => world.WorldId is >= 2 and <= 5);
        Assert.Single(GameWorldCatalog.Default, world => world.ServerEntry().Info.IsLogin);
        Assert.False(GameWorldCatalog.TryGet(999, out _));
    }
}
