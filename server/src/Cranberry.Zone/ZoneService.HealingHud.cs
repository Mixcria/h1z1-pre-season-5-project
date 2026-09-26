using Cranberry.Transport;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Native Effects datasources drive the stock HUD. The server remains responsible
    // for actual healing; the timed modifier only describes its remaining projection.
    private ulong AddHealingHud(SoeConnection connection, GatewaySessionState state, Medical row)
    {
        uint effect = row.Name switch
        {
            "Tactical First Aid Kit" => 120581u,
            "Field Bandage" => 120583u,
            _ => 0, // No recovered stock icon assignment for gauze/procoagulant.
        };
        ulong instance = NextMedicalEffectInstance(state);
        int duration = checked(Math.Max(1, row.OverSeconds) * (int)MedicalModel.TickIntervalMs);
        // Health resource 1 has an authored maximum of 10000. Keep the modifier on
        // the same normalized scale as CharacterResourceUpdate.Health.
        float rate = MedicalModel.HealUnitsPerTick(row) * 10_000f
            / _options.Gas.MaxHitpoints / MedicalModel.TickIntervalMs;
        var resource = new MedicalResourceEffect(state.Guid, instance, 1, rate, 0,
            (ulong)Environment.TickCount64, duration);
        state.HealingEffects.Add(instance, (effect, resource));
        SendHealingEffect(connection, state.Guid, effect, resource);
        return instance;
    }

    private void RemoveHealingHud(SoeConnection connection, GatewaySessionState state, ulong instance)
    {
        if (!state.HealingEffects.Remove(instance)) return;
        SendTunnel(connection, new RemoveMedicalEffect(state.Guid, instance).WriteTo);
    }

    private void ClearHealingHud(SoeConnection connection, GatewaySessionState state)
    {
        state.HealingHudGeneration++;
        foreach (ulong instance in state.HealingEffects.Keys)
            SendTunnel(connection, new RemoveMedicalEffect(state.Guid, instance).WriteTo);
        state.HealingEffects.Clear();
    }

    private static ulong NextMedicalEffectInstance(GatewaySessionState state) =>
        0x4d45440000000000ul | ++state.MedicalEffectSequence;

    private void PublishHealingHud(SoeConnection connection, GatewaySessionState state)
    {
        foreach (var active in state.HealingEffects.Values)
        {
            // Duplicate native instances are ignored, so an explicit resync replaces
            // each owned instance and retains its original start time.
            SendTunnel(connection, new RemoveMedicalEffect(state.Guid, active.Resource.InstanceId).WriteTo);
            SendHealingEffect(connection, state.Guid, active.EffectId, active.Resource);
        }
    }

    private void SendHealingEffect(SoeConnection connection, ulong subject, uint effect, MedicalResourceEffect resource)
    {
        if (effect != 0)
        {
            uint name = effect == 120581 ? 8887u : 8886u;
            SendTunnel(connection, new MedicalEffectTag(subject, resource.InstanceId, effect, name).WriteTo);
        }
        SendTunnel(connection, resource.WriteTo);
    }
}
