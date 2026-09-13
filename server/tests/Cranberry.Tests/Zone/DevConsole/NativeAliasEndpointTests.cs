using System.Numerics;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;

namespace Cranberry.Tests.Zone.DevConsole;

public sealed class NativeAliasEndpointTests
{
    [Fact]
    public void OriginalGodExpansionTogglesAndSupportsExplicitState()
    {
        var fixture = ConsoleFixture.Over(CommandCatalog.Build());
        fixture.Engine.ExecuteLine(fixture.Context, fixture.Session, "gm", "invuln");
        Assert.True(fixture.Session.Invulnerable);
        fixture.Engine.ExecuteLine(fixture.Context, fixture.Session, "gm", "invuln off");
        Assert.False(fixture.Session.Invulnerable);
        fixture.Engine.ExecuteLine(fixture.Context, fixture.Session, "gm", "invuln on extra");
        Assert.False(fixture.Session.Invulnerable);
    }

    [Fact]
    public void OriginalAliasTargetsAreRegisteredWithoutReplacingTheClientAliases()
    {
        var registry = CommandCatalog.Build();
        Assert.Contains("gm", registry.Names);
        Assert.Contains("location", registry.Names);
        Assert.DoesNotContain("god", registry.Names);
        Assert.DoesNotContain("loc", registry.Names);
        Assert.Same(registry.ByName("where"), registry.ByName("location"));
    }

    [Fact]
    public void PlayerCannotGainGodModeThroughTheOriginalAliasEndpoint()
    {
        var fixture = ConsoleFixture.Over(CommandCatalog.Build());
        fixture.Session.Tier = ConsoleTier.Player;
        fixture.Engine.ExecuteLine(fixture.Context, fixture.Session, "gm", "invuln");
        Assert.False(fixture.Session.Invulnerable);
    }
}
