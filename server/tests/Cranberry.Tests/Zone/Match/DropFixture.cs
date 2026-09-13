using Cranberry.Zone.Loot;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchDrop;

/// <summary>
/// The shipped placement file and the place list built over it, loaded once for every test class in
/// this directory. The anchor pass is a few hundred milliseconds over 144,365 markers, so paying it
/// per class would be the slowest thing in the suite.
/// </summary>
/// <remarks>
/// <b>Namespace note.</b> These files live in <c>tests/Cranberry.Tests/Zone/Match/</c> but are
/// declared <c>Cranberry.Tests.Zone.MatchDrop</c> on purpose: a <c>Cranberry.Tests.Zone.Match</c>
/// namespace would shadow the <c>Cranberry.Zone.World.Match</c> <i>type</i> for its sibling
/// <c>Cranberry.Tests.Zone.World</c>, whose <c>WorldSeamTests</c> writes a bare <c>typeof(Match)</c>
/// — nested namespaces beat a <c>using</c>-imported type, so that file would stop compiling. The
/// production side has no such sibling and is plain <c>Cranberry.Zone.Match</c>.
/// </remarks>
internal static class DropFixture
{
    private static readonly Lazy<Z2LootSpawns> LazySpawns = new(Z2LootSpawns.LoadDefault);

    private static readonly Lazy<Z2DropPois> LazyPlaces =
        new(() => Z2DropPois.For(LazySpawns.Value, DropOptions.Default));

    internal static Z2LootSpawns Spawns => LazySpawns.Value;

    internal static Z2DropPois Places => LazyPlaces.Value;
}
