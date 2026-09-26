using System.Numerics;

namespace Cranberry.Zone.World;

/// <summary>Pregame replication policy informed by the September 20 ROTK capture.
/// Sixteen peers and a 60-unit entry radius are observed reference values. Exit
/// and replacement margins are Cranberry policy to prevent repeated boundary churn.
/// A zero player limit restores the normal match visibility path.</summary>
public sealed record LobbyInterestSettings(int MaxPlayers = 16, float RadiusMetres = 60f)
{
    public static LobbyInterestSettings Default { get; } = new();
    public const float ExitMarginMetres = 6f;
    public const float ReplacementMarginMetres = 3f;
}

public sealed partial class SessionRegistry
{
    // Registry calls run on the listener thread. Reuse buffers between viewers.
    private readonly List<(PeerSession Subject, float Rank)> _lobbyCandidates = [];
    private readonly HashSet<EntityId> _lobbySelected = [];

    private void SweepLobby(PeerSession viewer, List<PeerEnter> enters,
        List<PeerLeave> leaves, LobbyInterestSettings settings)
    {
        _lobbyCandidates.Clear();
        _lobbySelected.Clear();
        ObserverView view = viewer.View;
        if (_matches.TryGetValue(viewer.MatchId, out List<PeerSession>? members))
        {
            foreach (PeerSession subject in members)
            {
                InterestCandidatesVisited++;
                if (ReferenceEquals(viewer, subject) || !subject.IsReplicable
                    || subject.MatchId != viewer.MatchId) continue;
                bool known = view.Knows(subject.Key);
                float distance = HorizontalDistance(viewer.ViewPosition, subject.Position);
                float radius = settings.RadiusMetres + (known ? LobbyInterestSettings.ExitMarginMetres : 0);
                if (!float.IsFinite(distance) || distance > radius) continue;
                float rank = known ? Math.Max(0, distance - LobbyInterestSettings.ReplacementMarginMetres) : distance;
                _lobbyCandidates.Add((subject, rank));
            }
        }
        _lobbyCandidates.Sort(static (a, b) =>
        {
            int distance = a.Rank.CompareTo(b.Rank);
            return distance != 0 ? distance : a.Subject.CharacterGuid.CompareTo(b.Subject.CharacterGuid);
        });
        int count = Math.Min(settings.MaxPlayers, _lobbyCandidates.Count);
        for (int i = 0; i < count; i++) _lobbySelected.Add(_lobbyCandidates[i].Subject.Key);

        // Remove before adding. The existing ordered despawn/appearance writer and
        // transient callbacks also update the reverse movement/combat fan-out index.
        int leaveBudget = view.DespawnBudgetPerTick;
        for (int i = view.KnownCount - 1; i >= 0 && leaveBudget > 0; i--)
        {
            EntityId id = view.KnownAt(i);
            if (_lobbySelected.Contains(id)) continue;
            _ = view.Transients.TryGet(id, out uint transient);
            view.MarkForgotten(id);
            view.Transients.Release(id);
            leaves.Add(new PeerLeave(id.Value, transient));
            leaveBudget--;
            Left++;
        }
        int enterBudget = Math.Min(view.SpawnBudgetPerTick, Math.Max(0, settings.MaxPlayers - view.KnownCount));
        for (int i = 0; i < count; i++)
        {
            PeerSession subject = _lobbyCandidates[i].Subject;
            if (view.Knows(subject.Key)) continue;
            if (enterBudget <= 0) { SpawnBudgetStops++; break; }
            view.MarkKnown(subject.Key);
            enters.Add(new PeerEnter(subject, view.Transients.Acquire(subject.Key)));
            enterBudget--;
            Entered++;
        }
        // Do not retain sessions after they leave the registry.
        _lobbyCandidates.Clear();
        _lobbySelected.Clear();
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) =>
        !float.IsFinite(a.Y) || !float.IsFinite(b.Y) ? float.NaN
            : MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
