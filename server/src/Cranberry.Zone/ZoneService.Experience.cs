using Cranberry.Transport;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Progression;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private AccountExperience? _experience;
    private sealed record PendingExperienceKill(string AccountId, SoeConnection Connection,
        GatewaySessionState State, int Generation, ulong Victim, string VictimName);
    private readonly Dictionary<string, PendingExperienceKill> _pendingExperience = [];
    private bool _experienceRetryArmed;
    private AccountExperience Experience => _experience ??= new(_economy, _options.ExperienceCurve,
        _options.ExperienceCurve.Seed(_options.MenuTopBar.Experience, _options.MenuTopBar.Rank));

    private SetExperience ExperiencePacket(GatewaySessionState state, ExperienceProgress current,
        ExperienceGrant? grant = null, ulong victim = 0, string victimName = "")
    {
        var previous = state.Match == MatchStep.Menu ? current : state.ExperienceAtMatchStart ?? current;
        return new()
        {
            RecordId = 0, Experience = current.Total, Rank = current.Level,
            Flags = grant is not null && current.Level > grant.Before.Level ? 1u : 0u,
            Word2 = 0, Word4 = current.Percent, Word6 = previous.Total, Word5 = previous.Level,
            Word30 = 0, Word38 = 0, Word3c = 0, Word40 = false,
            Awards = grant is { Amount: > 0 }
                ? [new(grant.Amount, 1, 1, 0, victim, victimName)] : [],
        };
    }

    private SetExperience SendAccountExperience(SoeConnection connection, GatewaySessionState state)
    {
        var current = Experience.Read(ScoreAccount(state));
        if (state.Match != MatchStep.Menu) state.ExperienceAtMatchStart ??= current;
        SendTunnel(connection, new SetExperienceRanks(_options.ExperienceCurve.Thresholds).WriteTo);
        var packet = ExperiencePacket(state, current);
        SendTunnel(connection, packet.WriteTo);
        return packet;
    }

    private void AwardKillExperience(SoeConnection connection, GatewaySessionState state, ulong victim, string victimName)
    {
        string operation = $"kill-xp:{state.EconomyLinkId}:{state.MatchAdmissionGeneration}:{victim:x16}";
        _pendingExperience.TryAdd(operation, new(ScoreAccount(state), connection, state,
            state.MatchAdmissionGeneration, victim, victimName));
        RetryPendingExperience();
    }

    private void RetryPendingExperience(bool refreshScore = false)
    {
        var blockedAccounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in _pendingExperience.ToArray())
        {
            var pending = pair.Value;
            if (blockedAccounts.Contains(pending.AccountId)) continue;
            var connection = pending.Connection;
            var state = pending.State;
            bool sameMatch = state.MatchAdmissionGeneration == pending.Generation && state.Match != MatchStep.Menu;
            try
            {
                // The queue survives link closure and new matches. Commit the original account's
                // award even then, but never attribute it to a later match or replay its kill HUD.
                if (sameMatch && connection.State == ConnectionState.Open && state.ExperienceAtMatchStart is null)
                    SendAccountExperience(connection, state);
                var grant = Experience.AwardKill(pending.AccountId, pair.Key);
                _pendingExperience.Remove(pair.Key);
                if (grant.Replayed) continue;
                if (sameMatch)
                {
                    state.ExperienceEarned = checked(state.ExperienceEarned + grant.Amount);
                    if (connection.State == ConnectionState.Open)
                    {
                        SendTunnel(connection, ExperiencePacket(state, grant.After, grant, pending.Victim, pending.VictimName).WriteTo);
                        if (refreshScore) PublishScore(connection, state, state.Score.FinalPlacement ?? 0, state.Score.Settled);
                    }
                }
                foreach (var other in _accountSessions)
                    if (ScoreAccount(other.Value) == pending.AccountId && other.Key.State == ConnectionState.Open
                        && (!ReferenceEquals(other.Key, connection) || !sameMatch))
                        SendAccountExperience(other.Key, other.Value);
                _log.Info($"{connection} experience: +{grant.Amount} kill {pending.Victim}, total {grant.After.Total}, "
                    + $"level {grant.After.Level} ({grant.After.Percent}%)");
            }
            catch (AccountEconomyStoreException error)
            {
                blockedAccounts.Add(pending.AccountId);
                _log.Warn($"{connection} kill experience pending for {pending.Victim}: {error.Message}");
            }
        }
        if (_pendingExperience.Count == 0 || _experienceRetryArmed || Post is not { } post) return;
        _experienceRetryArmed = true;
        _ = Task.Delay(1000).ContinueWith(_ => post(() =>
        {
            _experienceRetryArmed = false;
            RetryPendingExperience(refreshScore: true);
        }), TaskScheduler.Default);
    }
}
