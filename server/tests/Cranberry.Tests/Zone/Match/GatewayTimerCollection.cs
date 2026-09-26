namespace Cranberry.Tests.Zone.MatchLobby;

// These gateway tests execute real readiness, queue and zoning timers. Keep their
// bounded waits independent of CPU/thread-pool contention from the full test suite.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GatewayTimerCollection
{
    public const string Name = "gateway-timers";
}
