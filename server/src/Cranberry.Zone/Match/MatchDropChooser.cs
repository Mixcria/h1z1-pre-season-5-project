using Cranberry.Zone.Gas;
using Cranberry.Zone.Loot;

namespace Cranberry.Zone.Match;

/// <summary>
/// <b>The one call the match-start path makes.</b> Holds the process-wide place list, builds it on
/// first use, and turns (gas schedule, match seed) into a <see cref="DropPlan"/>.
///
/// <para><b>Why a chooser and not a bare static.</b> The dataset can fail to load — the placement
/// file is shipped content — and a missing file must degrade to the old fixed spawn rather than
/// take the match down on the listener thread, which is the rule <c>SpawnRealGroundLoot</c> already
/// follows. Wrapping the lazy build here means the caller writes one <c>if</c> and gets the reason
/// string for its warning line for free, and the failure is logged once rather than once per
/// match.</para>
///
/// <para>Thread-safe: the build is guarded, and everything it produces is immutable.</para>
/// </summary>
public sealed class MatchDropChooser
{
    private readonly Func<Z2LootSpawns> _spawns;
    private readonly Lock _gate = new();
    private Z2DropPois? _places;
    private string? _failure;
    private bool _built;

    /// <param name="options">The knobs; <see cref="DropOptions.Default"/> is the shipped design.</param>
    /// <param name="spawns">
    /// How to get the markers. Pass the host's existing shared set —
    /// <c>() =&gt; _lootLayout.Value.Spawns</c> — so the drop and the loot read one truth and the
    /// 4 MB file is not loaded twice.
    /// </param>
    public MatchDropChooser(DropOptions options, Func<Z2LootSpawns> spawns)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spawns);
        options.Validate();
        Options = options;
        _spawns = spawns;
    }

    /// <summary>Builds a chooser over the placement file shipped beside the server.</summary>
    public static MatchDropChooser Default(DropOptions? options = null) =>
        new(options ?? DropOptions.Default, Z2LootSpawns.LoadDefault);

    public DropOptions Options { get; }

    /// <summary>
    /// The place list, or null when it could not be built. Building is attempted at most once; the
    /// reason for a failure is in <paramref name="reason"/>.
    /// </summary>
    public Z2DropPois? Places(out string? reason)
    {
        lock (_gate)
        {
            if (!_built)
            {
                _built = true;
                try
                {
                    _places = Z2DropPois.For(_spawns(), Options);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Deliberately broad, and for the same reason SpawnRealGroundLoot is: the loot
                    // data is loose shipped content, so a missing or hand-edited file is the
                    // likeliest failure of all and it must cost the match a warning, not a crash.
                    _places = null;
                    _failure = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            reason = _failure;
            return _places;
        }
    }

    /// <summary>
    /// Plans this match's drop. False means "use the fixed spawn": either
    /// <see cref="DropOptions.Enabled"/> is off, or the dataset is unavailable — in which case
    /// <paramref name="reason"/> says why and is worth one warning line.
    /// </summary>
    public bool TryPlan(GasSchedule? schedule, ulong matchSeed, out DropPlan plan, out string? reason)
    {
        reason = null;
        plan = null!;

        if (!Options.Enabled)
        {
            return false;
        }

        Z2DropPois? places = Places(out reason);
        if (places is null)
        {
            return false;
        }

        // Phase 1's target is the first circle the client is ever shown, so it is the "initial
        // safe-zone location" retail chose the spawn area relative to.
        GasCircle? first = schedule is not null && schedule.Phases.Count > 0
            ? schedule.Phase(1).Target
            : null;

        DropPlan? planned = DropPlanner.Plan(places, first, Options, matchSeed);
        if (planned is null)
        {
            reason ??= "the place list is empty";
            return false;
        }

        plan = planned;
        return true;
    }
}
