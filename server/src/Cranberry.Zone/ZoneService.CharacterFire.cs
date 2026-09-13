using Cranberry.Transport;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Combat;
using Cranberry.Zone.World;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // August ActorCompositeEffectDefinitions.xml: PFX_Fire_Person_loop.
    internal const uint CharacterFireEffectId = 1212;

    private void TouchCharacterFire(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Hitpoints == 0 || state.DeathSent) return;
        state.CharacterFireUntilMs = Environment.TickCount64 + Rulings.Throwables.CloudTickMs + 100;
        if (state.CharacterFireActive) return;
        state.CharacterFireActive = true;
        PublishCharacterFire(connection, state, true);
        int generation = state.WorldGeneration;
        void Expire()
        {
            if (generation != state.WorldGeneration || !state.CharacterFireActive) return;
            long remaining = state.CharacterFireUntilMs - Environment.TickCount64;
            if (remaining <= 0 || state.DeathSent || state.Hitpoints == 0)
                StopCharacterFire(connection, state);
            else if (!Later(connection, (int)Math.Min(remaining, int.MaxValue), Expire))
                StopCharacterFire(connection, state);
        }
        if (!Later(connection, Rulings.Throwables.CloudTickMs + 100, Expire))
            StopCharacterFire(connection, state);
    }

    private void StopCharacterFire(SoeConnection connection, GatewaySessionState state)
    {
        if (!state.CharacterFireActive) return;
        state.CharacterFireActive = false;
        state.CharacterFireUntilMs = 0;
        PublishCharacterFire(connection, state, false);
    }

    private void PublishCharacterFire(SoeConnection connection, GatewaySessionState state, bool active)
    {
        KeyValuePair<GatewaySessionState, SoeConnection>[] members =
            _sharedLootMembership.TryGetValue(state, out ulong id)
                ? _sharedLootMatches[id].Members.ToArray() : [new(state, connection)];
        foreach (var (viewer, link) in members)
        {
            if (link.State != ConnectionState.Open || viewer.Match != state.Match) continue;
            if (active) SendTunnel(link, new AddEffectTagCompositeEffect(state.Guid, CharacterFireEffectId).WriteTo);
            else SendTunnel(link, new RemoveEffectTagCompositeEffect(state.Guid, CharacterFireEffectId).WriteTo);
        }
    }
}
