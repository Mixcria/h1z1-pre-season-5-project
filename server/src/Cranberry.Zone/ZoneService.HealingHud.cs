using Cranberry.Transport;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // The August HUD already authors bandage/first-aid frames in EffectView. Publish only
    // their active state; the server remains responsible for every healing tick.
    private uint AddHealingHud(SoeConnection connection, GatewaySessionState state, Medical row)
    {
        uint effect = row.Name.Contains("First Aid", StringComparison.Ordinal) ? 120581u : 120583u;
        state.HealingHudCounts.TryGetValue(effect, out int count);
        state.HealingHudCounts[effect] = count + 1;
        PublishHealingHud(connection, state);
        return effect;
    }

    private void RemoveHealingHud(SoeConnection connection, GatewaySessionState state, uint effect)
    {
        if (!state.HealingHudCounts.TryGetValue(effect, out int count)) return;
        if (count > 1) state.HealingHudCounts[effect] = count - 1;
        else state.HealingHudCounts.Remove(effect);
        PublishHealingHud(connection, state);
    }

    private void ClearHealingHud(SoeConnection connection, GatewaySessionState state)
    {
        state.HealingHudGeneration++;
        if (state.HealingHudCounts.Count == 0) return;
        state.HealingHudCounts.Clear();
        PublishHealingHud(connection, state);
    }

    private void PublishHealingHud(SoeConnection connection, GatewaySessionState state)
    {
        string value = state.HealingHudCounts.ContainsKey(120581) ? "120581"
            : state.HealingHudCounts.ContainsKey(120583) ? "120583" : "0";
        SendTunnel(connection, new UpdateStringHashToValueManager("Cranberry.Healing", value).WriteTo);
    }
}
